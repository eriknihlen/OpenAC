using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>One command the phone sent: an action and its value.</summary>
public readonly record struct RemoteCommand(string Action, string Value);

/// <summary>
/// Applies the phone's commands to the bot and the character. Requests
/// arrive on a listener thread and are only queued; <see cref="Drain"/>
/// runs them on the plugin tick, the thread everything else the bot does
/// runs on. Every action is idempotent set-semantics, so a command the
/// phone repeats after a lost reply changes nothing the second time.
/// </summary>
internal sealed class RemoteCommandApplier
{
    /// <summary>Commands applied in one tick at most; the rest wait for the next.</summary>
    public const int DrainCap = 64;

    /// <summary>
    /// The hold-to-move dead-man's switch. A held direction is a latched
    /// run that only a release ends, and every hop between the finger and
    /// here can lose the release; the phone re-sends each held start every
    /// third of a second, and a hold that has gone quiet this long is let
    /// go of.
    /// </summary>
    public const double MoveHoldSeconds = 2d;

    /// <summary>A second assess of the same item within this window is dropped.</summary>
    public const double AssessRepeatSeconds = 5d;

    private readonly BotController _controller;
    private readonly IAutomationSurface _surface;
    private readonly RemoteStatusBuilder _status;
    private readonly RemoteHostServices _services;
    private readonly IPluginLogger _log;
    private readonly ConcurrentQueue<RemoteCommand> _queue = new();
    private readonly Dictionary<uint, double> _recentAssess = [];
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private double _holdDeadline = double.NegativeInfinity;
    private bool _holding;

    public RemoteCommandApplier(
        BotController controller,
        IAutomationSurface surface,
        RemoteStatusBuilder status,
        RemoteHostServices services,
        IPluginLogger log)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The actions the listener accepts; anything else is a bad request.</summary>
    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "macro", "combat", "buffing", "navigation", "looting", "meta",
        "navProfile", "lootProfile", "metaProfile", "settingsProfile",
        "forceRebuff", "cancelRebuff", "clearBusy", "hideUi", "sendChat",
        "moveStart", "moveStop", "assess", "setSetting", "closeClient",
        "botCommand",
    };

    public int Pending => _queue.Count;

    /// <summary>Whether a phone-held direction is driving the character.</summary>
    public bool IsHolding => _holding;

    /// <summary>Queues a command from any thread.</summary>
    public void Enqueue(RemoteCommand command) => _queue.Enqueue(command);

    /// <summary>Applies queued commands and services the movement hold. Plugin tick only.</summary>
    public void Drain(double now)
    {
        for (int applied = 0; applied < DrainCap && _queue.TryDequeue(out RemoteCommand command); applied++)
        {
            try
            {
                Apply(command, now);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _log.Warn($"remote: {command.Action} failed: {error.Message}");
            }
        }
        if (_holding && now > _holdDeadline)
        {
            _holding = false;
            _held.Clear();
            _surface.Navigation.ClearMovementIntent();
            _log.Info("remote: movement hold went quiet; stopped");
        }
    }

    internal void Apply(RemoteCommand command, double now)
    {
        string value = command.Value ?? string.Empty;
        bool on = IsOn(value);
        switch (command.Action.ToLowerInvariant())
        {
            case "macro":
                if (on)
                    _controller.Start();
                else
                    _controller.Stop();
                break;
            case "combat":
                _controller.Update(p => p with { Combat = p.Combat with { Enabled = on } });
                break;
            case "buffing":
                _controller.Update(p => p with { Buffs = p.Buffs with { Enabled = on } });
                break;
            case "navigation":
                _controller.Update(p => p with { Navigation = p.Navigation with { Enabled = on } });
                break;
            case "looting":
                _controller.Update(p => p with { Loot = p.Loot with { Enabled = on } });
                break;
            case "meta":
                if (_controller.Meta is { } metaSwitch)
                    metaSwitch.Enabled = on;
                else
                    _controller.Update(p => p with { Meta = p.Meta with { Enabled = on } });
                break;
            case "navprofile":
                SelectRoute(Resolve(value, _status.NavProfiles));
                break;
            case "lootprofile":
            {
                string name = Resolve(value, _status.LootProfiles) ?? string.Empty;
                _controller.Update(p => p with { Loot = p.Loot with { UtlProfile = name } });
                break;
            }
            case "metaprofile":
                SelectMeta(Resolve(value, _status.MetaProfiles));
                break;
            case "settingsprofile":
                if (Resolve(value, _status.Profiles) is { } profile && !_controller.LoadProfile(profile))
                    _log.Warn($"remote: no profile named '{profile}'");
                break;
            case "forcerebuff":
                _controller.Buffs.ForceRebuff();
                break;
            case "cancelrebuff":
                _controller.Buffs.CancelForceRebuff();
                break;
            case "clearbusy":
            {
                PluginRecoveryResult result = _surface.Recovery.ClearOneBusyReference();
                _log.Info($"remote: clear busy: {(result.Accepted ? $"{result.PreviousCount} -> {result.CurrentCount}" : result.Message)}");
                break;
            }
            case "hideui":
                _status.UiHidden = on;
                break;
            case "sendchat":
                if (value.Length > 0 && !_surface.Chat.Submit(value))
                    _log.Warn($"remote: chat refused: {value}");
                break;
            case "botcommand":
            {
                string verb = BotVerb(value);
                if (verb.Length > 0 && _controller.CommandHandler?.Invoke(verb) != true)
                    _log.Warn($"remote: bot command not handled: {verb}");
                break;
            }
            case "movestart":
                Move(value, true, now);
                break;
            case "movestop":
                Move(value, false, now);
                break;
            case "assess":
                Assess(value, now);
                break;
            case "setsetting":
                SetSetting(value);
                break;
            case "closeclient":
                if (_services.CloseClient is { } close)
                {
                    _log.Info("remote: closing the client");
                    close();
                }
                else
                {
                    _log.Warn("remote: this host does not close on request");
                }
                break;
            default:
                _log.Warn($"remote: unknown command '{command.Action}'");
                return;
        }
        if (!command.Action.StartsWith("move", StringComparison.OrdinalIgnoreCase))
            _log.Info($"remote: {command.Action}={Elide(value)}");
    }

    private void SelectRoute(string? name)
    {
        if (name is null)
        {
            _controller.Navigation.SetRoute(null);
            return;
        }
        if (!_controller.LoadRouteByName(name))
            _log.Warn($"remote: no route named '{name}'");
    }

    private void SelectMeta(string? name)
    {
        MetaEngine? meta = _controller.Meta;
        if (name is null)
        {
            meta?.Clear();
            _controller.Update(p => p with { Meta = p.Meta with { Name = string.Empty } });
            return;
        }
        if (meta is null || !meta.LoadByName(name))
            _log.Warn($"remote: no meta named '{name}'");
    }

    /// <summary>
    /// A picker value is the index into the list the status document
    /// carried, or a name; -1, "none" and an empty value mean none.
    /// </summary>
    private static string? Resolve(string value, IReadOnlyList<string> names)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            return index >= 0 && index < names.Count ? names[index] : null;
        foreach (string name in names)
        {
            if (name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return trimmed;
    }

    private void Move(string direction, bool start, double now)
    {
        string key = direction.Trim().ToLowerInvariant();
        if (key == "stop" || (key.Length == 0 && !start))
        {
            _held.Clear();
            _holding = false;
            _surface.Navigation.ClearMovementIntent();
            return;
        }
        if (key is not ("forward" or "back" or "left" or "right" or "strafeleft" or "straferight"))
            return;
        if (start)
        {
            _held.Add(key);
            _holding = true;
            _holdDeadline = now + MoveHoldSeconds;
        }
        else
        {
            _held.Remove(key);
        }
        if (_held.Count == 0)
        {
            _holding = false;
            _surface.Navigation.ClearMovementIntent();
            return;
        }
        var intent = new PluginMovementIntent(
            Forward: _held.Contains("forward"),
            Backward: _held.Contains("back"),
            StrafeLeft: _held.Contains("strafeleft"),
            StrafeRight: _held.Contains("straferight"),
            TurnLeft: _held.Contains("left"),
            TurnRight: _held.Contains("right"));
        _surface.Navigation.SetMovementIntent(intent);
    }

    private void Assess(string value, double now)
    {
        if (!uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id) || id == 0u)
            return;
        if (_recentAssess.TryGetValue(id, out double last) && now - last < AssessRepeatSeconds)
            return;
        if (_recentAssess.Count > 256)
        {
            foreach (uint stale in _recentAssess.Where(pair => now - pair.Value >= AssessRepeatSeconds).Select(static pair => pair.Key).ToArray())
                _recentAssess.Remove(stale);
        }
        _recentAssess[id] = now;
        PluginItemCommandResult result = _surface.Objects.Identify(id);
        if (!result.Accepted)
            _log.Warn($"remote: assess 0x{id:X8}: {result.Status}{(result.Notice is null ? string.Empty : ": " + result.Notice)}");
    }

    private void SetSetting(string value)
    {
        string key;
        JsonElement setting;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("key", out JsonElement keyElement)
                || keyElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("value", out JsonElement valueElement))
            {
                _log.Warn("remote: setSetting needs {\"key\":..,\"value\":..}");
                return;
            }
            key = keyElement.GetString() ?? string.Empty;
            setting = valueElement.Clone();
        }
        catch (JsonException error)
        {
            _log.Warn($"remote: setSetting is not JSON: {error.Message}");
            return;
        }
        BotProfile? patched = RemoteSettingsBridge.TryApply(_controller.Profile, key, setting, out string reason);
        if (patched is null)
        {
            _log.Warn($"remote: setSetting {reason}");
            return;
        }
        _controller.Update(_ => patched);
        _log.Info($"remote: set {key} = {RemoteSettingsBridge.Describe(setting)}");
    }

    /// <summary>"/drakbot patrol", "/bot patrol" and "patrol" all name the verb "patrol".</summary>
    internal static string BotVerb(string value)
    {
        string text = value.Trim();
        foreach (string prefix in new[] { "/drakbot ", "/bot ", "drakbot ", "bot " })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }
        return text.TrimStart('/');
    }

    private static bool IsOn(string value) =>
        value.Trim() is "1" or "true" or "on" or "True" or "TRUE" or "On" or "ON" or "yes";

    private static string Elide(string value) =>
        value.Length <= 80 ? value : value[..77] + "...";
}
