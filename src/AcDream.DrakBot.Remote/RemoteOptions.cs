using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// How, and whether, the remote listens. Nothing listens unless something
/// enables it: the plugin's <c>remote.json</c>, the session's settings for
/// this plugin, or the <c>ACDREAM_REMOTE*</c> environment, in that order of
/// precedence (later wins). A token is optional on the loopback interface
/// and required on any other, so a LAN-facing remote can never be open.
/// </summary>
public sealed record RemoteOptions
{
    public const int DefaultPort = 8740;

    /// <summary>How many ports up from <see cref="Port"/> a busy port is retried; each client of a multi-box takes the next free one.</summary>
    public const int PortSpan = 10;

    public bool Enabled { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>"localhost" (the default) or "any" for every interface.</summary>
    public string Bind { get; init; } = "localhost";

    public string? Token { get; init; }

    public bool BindsEveryInterface =>
        string.Equals(Bind, "any", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Bind, "*", StringComparison.Ordinal)
        || string.Equals(Bind, "+", StringComparison.Ordinal)
        || string.Equals(Bind, "0.0.0.0", StringComparison.Ordinal);

    /// <summary>The listener prefix for one port.</summary>
    public string PrefixFor(int port) =>
        BindsEveryInterface
            ? $"http://+:{port.ToString(CultureInfo.InvariantCulture)}/"
            : $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>Why the options cannot be listened on, or null when they can.</summary>
    public string? Validate()
    {
        if (Port is < 1 or > 65535)
            return $"port {Port} is out of range";
        if (BindsEveryInterface && string.IsNullOrWhiteSpace(Token))
            return "a token is required to listen on every interface";
        return null;
    }

    public const string FileName = "remote.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RemoteOptions FromJson(string json) =>
        JsonSerializer.Deserialize<RemoteOptions>(json, JsonOptions)
            ?? throw new JsonException("remote options are empty");

    /// <summary>
    /// Resolves the options for one host: the storage file, then the
    /// session settings, then the environment, each layer overriding what
    /// it names. A missing file, an unreadable one, or an empty everything
    /// leaves the remote off.
    /// </summary>
    public static RemoteOptions Resolve(
        IPluginStorage storage,
        IReadOnlyDictionary<string, string> sessionSettings,
        Func<string, string?> environment,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(sessionSettings);
        ArgumentNullException.ThrowIfNull(environment);

        RemoteOptions options = new();
        string? json = null;
        try
        {
            json = storage.ReadText(FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"remote: could not read {FileName}: {error.Message}");
        }
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                options = FromJson(json);
            }
            catch (JsonException error)
            {
                warn?.Invoke($"remote: {FileName} is not valid: {error.Message}");
            }
        }

        options = Overlay(options, key => sessionSettings.TryGetValue(key, out string? value) ? value : null, warn, "session settings");
        options = Overlay(options, key => environment("ACDREAM_REMOTE" + (key.Length == 0 ? "" : "_" + key.ToUpperInvariant())), warn, "environment");
        return options;
    }

    private static RemoteOptions Overlay(
        RemoteOptions options,
        Func<string, string?> read,
        Action<string>? warn,
        string source)
    {
        // The bare key ("ACDREAM_REMOTE=1", or "enabled" in the session)
        // switches the remote on; naming a port or a token also switches
        // it on, since a session that sets a port wants to be reached.
        string? enabled = read("enabled") ?? read(string.Empty);
        if (enabled is not null)
            options = options with { Enabled = IsOn(enabled) };
        if (read("port") is { } port)
        {
            if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                options = options with { Port = value, Enabled = enabled is null || options.Enabled };
            else
                warn?.Invoke($"remote: {source} port '{port}' is not a number");
        }
        if (read("bind") is { Length: > 0 } bind)
            options = options with { Bind = bind };
        if (read("token") is { } token)
            options = options with { Token = token.Length == 0 ? null : token, Enabled = enabled is null || options.Enabled };
        return options;
    }

    private static bool IsOn(string value) =>
        value.Trim() is "1" or "true" or "on" or "yes" or "True" or "TRUE" or "On" or "Yes";
}
