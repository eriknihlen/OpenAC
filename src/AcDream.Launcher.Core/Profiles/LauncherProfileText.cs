using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public enum LauncherTextEditorKind { Users, Servers, LogonCommands }

/// <summary>Editable projections over the canonical profile document.</summary>
public static class LauncherProfileText
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Read(LauncherProfileDocument document, LauncherTextEditorKind kind) => kind switch
    {
        LauncherTextEditorKind.Users => string.Join(Environment.NewLine, document.Servers
            .SelectMany(s => s.Accounts.Select(a => (Server: s.Name, Account: a)))
            .GroupBy(x => (x.Account.Account, x.Account.Password))
            .Select(g => $"{Encode(g.Key.Account)} | {Encode(g.Key.Password)} | {string.Join(", ", g.Select(x => Encode(x.Server)))}")),
        LauncherTextEditorKind.Servers => string.Join(Environment.NewLine, document.Servers
            .Select(s => $"{Encode(s.Name)} | {Encode(s.Host)} | {s.Port}")),
        LauncherTextEditorKind.LogonCommands => JsonSerializer.Serialize(document.Servers
            .SelectMany(s => s.Accounts.SelectMany(a => a.Characters.Select(c =>
                new CommandEntry(s.Name, a.Account, c.Name, c.LoginCommands.ToArray())))), Options),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static void Apply(LauncherProfileDocument document, LauncherTextEditorKind kind, string text)
    {
        switch (kind)
        {
            case LauncherTextEditorKind.Users:
                var users = Lines(text).Select(line =>
                {
                    var fields = SplitFields(line, '|');
                    Require(fields.Length is 2 or 3, "Each user line must be username | password | server1, server2. The server list may be omitted.");
                    return new UserEntry(Decode(fields[0]), Decode(fields[1]),
                        fields.Length == 2 || string.IsNullOrWhiteSpace(fields[2])
                            ? document.Servers.Select(s => s.Name).ToArray()
                            : SplitFields(fields[2], ',').Select(Decode).ToArray());
                }).ToArray();
                var replacements = document.Servers.ToDictionary(s => s.Name, _ => new List<AccountProfile>(), StringComparer.Ordinal);
                foreach (var user in users)
                {
                    Require(!string.IsNullOrWhiteSpace(user.Username) && user.Password is not null && user.Servers is { Length: > 0 }, "Each user needs a username, password and at least one server.");
                    foreach (string name in user.Servers)
                    {
                        Require(name is not null && replacements.ContainsKey(name), "A user refers to an unknown server. Add it in Edit Servers first.");
                        var accounts = replacements[name];
                        Require(!accounts.Any(a => a.Account == user.Username), "An account may appear only once on each server.");
                        var existing = document.Servers.Single(s => s.Name == name).Accounts.Find(a => a.Account == user.Username);
                        accounts.Add(new AccountProfile { Account = user.Username, Password = user.Password, Characters = existing?.Characters ?? [] });
                    }
                }
                foreach (var server in document.Servers) server.Accounts = replacements[server.Name];
                break;
            case LauncherTextEditorKind.Servers:
                var servers = Lines(text).Select(line =>
                {
                    var fields = SplitFields(line, '|');
                    Require(fields.Length == 3, "Each server line must be name | host | port.");
                    Require(int.TryParse(Decode(fields[2]), out int port), "Server ports must be numbers from 1 to 65535.");
                    return new ServerEntry(Decode(fields[0]), Decode(fields[1]), port);
                }).ToArray();
                Require(servers.All(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Host) && s.Port is >= 1 and <= 65535), "Each server needs a name, host and port from 1 to 65535.");
                Require(servers.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() == servers.Length, "Server names must be unique.");
                document.Servers = servers.Select(s => new ServerProfile { Name = s.Name, Host = s.Host, Port = s.Port,
                    Accounts = document.Servers.Find(old => old.Name == s.Name)?.Accounts ?? [] }).ToList();
                break;
            case LauncherTextEditorKind.LogonCommands:
                var entries = Parse<CommandEntry>(text);
                var changes = new Dictionary<CharacterProfile, string[]>();
                foreach (var entry in entries)
                {
                    var character = document.Servers.Find(s => s.Name == entry.Server)?.Accounts.Find(a => a.Account == entry.Account)?.Characters.Find(c => c.Name == entry.Character);
                    Require(character is not null, "A command entry refers to an unknown server, account or character.");
                    Require(entry.Commands is not null && entry.Commands.All(c => !string.IsNullOrWhiteSpace(c)), "Commands must be nonempty strings. Use [] for no commands.");
                    Require(changes.TryAdd(character!, entry.Commands), "Each character may appear only once.");
                }
                foreach (var server in document.Servers)
                    foreach (var account in server.Accounts)
                        foreach (var character in account.Characters)
                            character.LoginCommands = changes.TryGetValue(character, out var commands) ? commands.ToList() : [];
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n')
        .Select(line => line.Trim()).Where(line => line.Length > 0);

    private static string Encode(string value) => value != value.Trim() || value.IndexOfAny(['|', ',', '"', '\r', '\n', '\t']) >= 0
        ? JsonSerializer.Serialize(value) : value;

    private static string Decode(string field)
    {
        field = field.Trim();
        if (!field.Contains('"')) return field;
        try
        {
            Require(field.StartsWith('"'), "A quoted field must use double quotes around the entire value.");
            return JsonSerializer.Deserialize<string>(field) ?? "";
        }
        catch (JsonException) { throw new LauncherProfileException("Invalid quoted field. Use double quotes around values containing separators; escape quotes as \\\" and backslashes as \\\\. "); }
    }

    private static string[] SplitFields(string line, char separator)
    {
        var fields = new List<string>();
        bool quoted = false;
        bool escaped = false;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted && escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c == separator) { fields.Add(line[start..i]); start = i + 1; }
        }
        Require(!quoted, "A quoted field is missing its closing double quote.");
        fields.Add(line[start..]);
        return fields.ToArray();
    }

    private static T[] Parse<T>(string text) where T : class
    {
        try
        {
            var values = JsonSerializer.Deserialize<T[]>(text, Options);
            Require(values is not null && values.All(v => v is not null), "Enter a JSON array of entries; use [] for an empty list.");
            return values!;
        }
        catch (JsonException) { throw new LauncherProfileException("Invalid JSON. Check field names, quotes, commas and brackets."); }
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new LauncherProfileException(message);
    }

    private sealed record UserEntry(string Username, string Password, string[] Servers);
    private sealed record ServerEntry(string Name, string Host, int Port);
    private sealed record CommandEntry(string Server, string Account, string Character, string[] Commands);
}
