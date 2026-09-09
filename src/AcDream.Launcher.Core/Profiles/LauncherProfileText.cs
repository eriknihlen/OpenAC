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
        LauncherTextEditorKind.Users => string.Join(Environment.NewLine, GetUsers(document)
            .Select(user => $"{Encode(user.Account)} | {Encode(user.Password)}")),
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
                var users = ParseUsers(text).ToList();
                Require(users.All(user => !string.IsNullOrWhiteSpace(user.Account)), "Each user needs a username.");
                Require(users.Select(user => user.Account).Distinct(StringComparer.Ordinal).Count() == users.Count,
                    "Each username must appear once. All servers use the same password for that user.");
                document.Users = users;
                SynchronizeUsers(document);
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
                document.Users ??= GetUsers(document).ToList();
                document.Servers = servers.Select(s => new ServerProfile { Name = s.Name, Host = s.Host, Port = s.Port,
                    Accounts = document.Servers.Find(old => old.Name == s.Name)?.Accounts ?? [] }).ToList();
                SynchronizeUsers(document);
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

    public static IReadOnlyList<LauncherUser> ParseUsers(string text) => Lines(text).Select(line =>
    {
        var fields = SplitFields(line, '|');
        Require(fields.Length == 2, "Each user line must be username | password.");
        return new LauncherUser(Decode(fields[0]), Decode(fields[1]));
    }).ToArray();

    private static IEnumerable<LauncherUser> GetUsers(LauncherProfileDocument document) =>
        document.Users ?? document.Servers.SelectMany(server => server.Accounts)
            .Select(account => new LauncherUser(account.Account, account.Password)).Distinct().ToList();

    internal static void SynchronizeUsers(LauncherProfileDocument document)
    {
        if (document.Users is not { } users) return;
        Require(users.All(user => user is not null && !string.IsNullOrWhiteSpace(user.Account) && user.Password is not null),
            "Each user needs a username and password field.");
        Require(users.Select(user => user.Account).Distinct(StringComparer.Ordinal).Count() == users.Count,
            "Each username must appear once. Resolve its passwords in Edit Users first.");
        foreach (var server in document.Servers)
            server.Accounts = users.Select(user => new AccountProfile
            {
                Account = user.Account, Password = user.Password,
                Characters = server.Accounts.Find(account => account.Account == user.Account)?.Characters ?? [],
            }).ToList();
    }

    internal static void EnableSharedUsers(LauncherProfileDocument document)
    {
        var users = GetUsers(document).ToList();
        // Keep conflicting older credentials available in the editor for explicit resolution.
        if (users.Select(user => user.Account).Distinct(StringComparer.Ordinal).Count() != users.Count) return;
        document.Users = users;
        SynchronizeUsers(document);
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


    private sealed record ServerEntry(string Name, string Host, int Port);
    private sealed record CommandEntry(string Server, string Account, string Character, string[] Commands);
}
