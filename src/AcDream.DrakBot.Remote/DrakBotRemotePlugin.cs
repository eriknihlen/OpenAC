using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// DrakBot Remote as the host sees it: a built-in plugin beside the bot
/// that, when configured, listens for the phone and answers it from the
/// plugin tick. It takes the bot through <see cref="DrakBotPlugin"/>'s
/// controller (so it must be registered after the bot) and the world
/// through the plugin contract. Without a port it does nothing at all.
/// </summary>
public sealed class DrakBotRemotePlugin(DrakBotPlugin bot, RemoteHostServices? services = null) : IAcDreamPlugin
{
    public const string Id = "acdream.drakbot.remote";
    public const string DisplayName = "DrakBot Remote";
    public const string Version = "0.1.0";

    /// <summary>How often a new status goes out to the phone at most.</summary>
    public const double StatusIntervalSeconds = 0.15;

    /// <summary>How long an unchanged status is held back before it goes out anyway, so the feed reads as alive.</summary>
    public const double StatusHeartbeatSeconds = 3d;

    /// <summary>How often the pack and the settings are re-read.</summary>
    public const double DocumentIntervalSeconds = 1d;
    /// <summary>How often the dungeon document is rebuilt: the graph does not change, the patrol step and the stalls do.</summary>
    public const double DungeonIntervalSeconds = 10d;
    private double _dungeonBuiltAt = double.NegativeInfinity;

    /// <summary>Icons rendered in one tick at most.</summary>
    public const int IconsPerTick = 8;

    private readonly DrakBotPlugin _bot = bot ?? throw new ArgumentNullException(nameof(bot));
    private readonly RemoteHostServices _services = services ?? RemoteHostServices.None;
    private readonly ConcurrentQueue<(uint Did, TaskCompletionSource<byte[]?> Result)> _iconRequests = new();
    private readonly ConcurrentDictionary<uint, byte[]?> _icons = new();
    private IPluginHost? _host;
    private RemoteOptions _options = new();
    private RemoteTelemetry? _telemetry;
    private RemoteStatusBuilder? _status;
    private RemoteInventoryBuilder? _inventory;
    private RemoteMapKeeper? _maps;
    private IAutomationSurface? _surface;
    private RemoteCommandApplier? _commands;
    private RemoteHttpServer? _server;
    private IDisposable? _commandLease;
    private bool _enabled;
    private double _statusPublishedAt = double.NegativeInfinity;
    private double _statusBuiltAt = double.NegativeInfinity;
    private double _documentsPublishedAt = double.NegativeInfinity;
    private string? _lastSettingsJson;

    public RemoteOptions Options => _options;

    /// <summary>The port the remote listens on, or zero when it does not.</summary>
    public int Port => _server?.Port ?? 0;

    public bool IsListening => _server?.IsListening == true;

    internal RemoteCommandApplier? Commands => _commands;

    internal RemoteStatusBuilder? Status => _status;

    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        BotController controller = _bot.Controller
            ?? throw new InvalidOperationException("DrakBot must be initialized before its remote; register the bot first.");
        _options = RemoteOptions.Resolve(
            host.Storage,
            host.SessionSettings,
            Environment.GetEnvironmentVariable,
            host.Log.Warn);
        IAutomationSurface surface = host.Automation;
        _surface = surface;
        _telemetry = new RemoteTelemetry();
        var capabilities = RemoteCapabilities.For(_services);
        _status = new RemoteStatusBuilder(controller, surface, _telemetry, _services, capabilities);
        _inventory = new RemoteInventoryBuilder(surface);
        _maps = _services.DungeonGeometry is { } geometry
            ? new RemoteMapKeeper(
                geometry,
                dungeon => RemoteDungeonMapRasterizer.Render(dungeon, DateTime.UtcNow),
                () => DateTime.UtcNow,
                host.Log)
            : null;
        _commands = new RemoteCommandApplier(controller, surface, _status, _services, host.Log);
    }

    public void Enable()
    {
        if (_host is null || _commands is null)
            throw new InvalidOperationException("Initialize must run before Enable.");
        if (_enabled)
            return;
        _enabled = true;
        _host.Events.Tick += OnTick;
        _commandLease = _host.Commands.Register("remote", HandleCommand);
        if (_options.Enabled)
            StartListening();
        else
            _host.Log.Info($"DrakBot Remote is off; /remote setup <port> [token], or {RemoteOptions.FileName} in {_host.Storage.Directory ?? "the plugin's storage"}, turns it on");
    }

    public void Disable()
    {
        if (!_enabled)
            return;
        _enabled = false;
        StopListening();
        if (_host is not null)
            _host.Events.Tick -= OnTick;
        _commandLease?.Dispose();
        _commandLease = null;
        while (_iconRequests.TryDequeue(out (uint Did, TaskCompletionSource<byte[]?> Result) request))
            request.Result.TrySetResult(null);
    }

    private void StartListening()
    {
        if (_host is null || _commands is null || _server is not null)
            return;
        if (_options.Validate() is { } problem)
        {
            _host.Log.Warn($"DrakBot Remote not started: {problem}");
            return;
        }
        var server = new RemoteHttpServer(
            _options,
            _host.Log,
            command =>
            {
                RemoteCommandApplier? commands = _commands;
                if (commands is null || !_enabled)
                    return false;
                commands.Enqueue(command);
                return true;
            },
            _services.RenderIconPng is null ? null : RequestIcon,
            _services.Frames);
        if (server.TryStart() is { } failure)
        {
            _host.Log.Warn($"DrakBot Remote could not listen ({failure})");
            server.Dispose();
            return;
        }
        _server = server;
        _host.Log.Info($"DrakBot Remote listening on {string.Join(", ", Urls(server.Port))} ({(_options.Token is null ? "no token, loopback only" : "token required")})");
    }

    private void StopListening()
    {
        _server?.Dispose();
        _server = null;
    }

    private IEnumerable<string> Urls(int port)
    {
        string suffix = ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        if (!_options.BindsEveryInterface)
        {
            yield return "http://localhost" + suffix;
            yield break;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && seen.Add(address.Address.ToString()))
                    yield return "http://" + address.Address + suffix;
            }
        }
        if (seen.Count == 0)
            yield return "http://<this pc>" + suffix;
    }

    private Task<byte[]?> RequestIcon(uint did)
    {
        if (_icons.TryGetValue(did, out byte[]? cached))
            return Task.FromResult(cached);
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _iconRequests.Enqueue((did, result));
        return result.Task;
    }

    private void OnTick(double elapsedSeconds)
    {
        if (_telemetry is null || _status is null || _inventory is null || _commands is null || _bot.Controller is null)
            return;
        _telemetry.Tick();
        double now = _bot.Controller.Engine.Clock.Now;
        _commands.Drain(now);
        RenderIcons();
        RemoteHttpServer? server = _server;
        if (server is null)
            return;
        try
        {
            if (now - _statusBuiltAt >= StatusIntervalSeconds)
            {
                _statusBuiltAt = now;
                byte[] status = _status.Build(now);
                // Vitals and chat change the document many times a second in a fight; an idle
                // client's would only differ by its timestamp, and that is not worth a push.
                if (_status.Changed || now - _statusPublishedAt >= StatusHeartbeatSeconds)
                {
                    _statusPublishedAt = now;
                    server.PublishStatus(status);
                }
            }
            if (now - _documentsPublishedAt >= DocumentIntervalSeconds)
            {
                _documentsPublishedAt = now;
                if (_inventory.BuildIfChanged() is { } inventory)
                    server.PublishInventory(inventory);
                byte[] settings = RemoteSettingsBridge.Build(_bot.Controller.Profile);
                string settingsText = System.Text.Encoding.UTF8.GetString(settings);
                if (!string.Equals(settingsText, _lastSettingsJson, StringComparison.Ordinal))
                {
                    _lastSettingsJson = settingsText;
                    server.PublishSettings(settings);
                }
            }
            if (_maps is not null && _surface is not null)
            {
                _maps.Tick(now, _surface.Navigation.Snapshot);
                if (_maps.Changed)
                    server.PublishMaps(_maps.Snapshot());
            }
            if (now - _dungeonBuiltAt >= DungeonIntervalSeconds)
            {
                _dungeonBuiltAt = now;
                if (_bot.Controller.TryBuildDungeonJson(out string dungeon, out uint landblock, out _, out _, out _, out _))
                    server.PublishDungeon($"{landblock >> 16:X4}", System.Text.Encoding.UTF8.GetBytes(dungeon));
                else
                    server.PublishDungeon(string.Empty, null);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _host?.Log.Warn($"DrakBot Remote: status build failed: {error.Message}");
        }
    }

    private void RenderIcons()
    {
        Func<uint, byte[]?>? render = _services.RenderIconPng;
        for (int count = 0; count < IconsPerTick && _iconRequests.TryDequeue(out (uint Did, TaskCompletionSource<byte[]?> Result) request); count++)
        {
            byte[]? png = null;
            if (render is not null && !_icons.TryGetValue(request.Did, out png))
            {
                try
                {
                    png = render(request.Did);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    _host?.Log.Warn($"DrakBot Remote: icon 0x{request.Did:X8} failed: {error.Message}");
                }
                _icons[request.Did] = png;
            }
            request.Result.TrySetResult(png);
        }
    }

    private void HandleCommand(PluginCommand command)
    {
        if (_host is null)
            return;
        string[] words = command.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length == 0 ? "status" : words[0].ToLowerInvariant();
        IPluginChat chat = _host.Automation.Chat;
        switch (verb)
        {
            case "status":
                chat.PostSystemMessage(_server is { } server
                    ? $"DrakBot Remote: listening on {string.Join(", ", Urls(server.Port))}; {server.FeedClients} phone(s) on the feed, {server.Requests} requests"
                    : $"DrakBot Remote: off (/remote on, or /remote setup <port> [token])");
                break;
            case "on":
                if (!_options.Enabled)
                    _options = _options with { Enabled = true };
                StartListening();
                if (_server is null)
                    chat.PostSystemMessage("DrakBot Remote: could not start; see the log");
                else
                    chat.PostSystemMessage($"DrakBot Remote: listening on port {_server.Port}");
                break;
            case "off":
                StopListening();
                chat.PostSystemMessage("DrakBot Remote: stopped");
                break;
            case "setup":
                Setup(words, chat);
                break;
            default:
                chat.PostSystemMessage("/remote status | on | off | setup <port> [token] [lan|local]");
                break;
        }
    }

    /// <summary>
    /// Writes <c>remote.json</c> so the remote comes up on every start:
    /// <c>/remote setup 8740 mytoken lan</c> listens on every interface
    /// with that token; without <c>lan</c> only this PC can connect.
    /// </summary>
    private void Setup(string[] words, IPluginChat chat)
    {
        if (_host is null)
            return;
        // Only what the line says: a setup without a token has no token.
        int port = RemoteOptions.DefaultPort;
        string? token = null;
        string bind = "localhost";
        for (int index = 1; index < words.Length; index++)
        {
            string word = words[index];
            if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                port = number;
            else if (word.Equals("lan", StringComparison.OrdinalIgnoreCase) || word.Equals("any", StringComparison.OrdinalIgnoreCase))
                bind = "any";
            else if (word.Equals("local", StringComparison.OrdinalIgnoreCase) || word.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                bind = "localhost";
            else
                token = word;
        }
        var options = new RemoteOptions { Enabled = true, Port = port, Token = token, Bind = bind };
        if (options.Validate() is { } problem)
        {
            chat.PostSystemMessage($"DrakBot Remote: {problem}");
            return;
        }
        if (!_host.Storage.IsAvailable)
        {
            chat.PostSystemMessage("DrakBot Remote: this host has no plugin storage to keep the setup in");
            return;
        }
        _host.Storage.WriteText(RemoteOptions.FileName, options.ToJson());
        _options = options;
        StopListening();
        StartListening();
        chat.PostSystemMessage(_server is { } server
            ? $"DrakBot Remote: saved and listening on {string.Join(", ", Urls(server.Port))}"
            : "DrakBot Remote: saved, but could not start; see the log");
    }
}
