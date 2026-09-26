using System.Text;
using System.Text.Json;

namespace AcDream.Launcher.Core.Profiles;

public enum LauncherTextEditorKind { Accounts, Servers, LogonCommands }

/// <summary>
/// The plain-text views of the profile document the launcher's editors show, and the parsers that
/// write them back. A parse checks the whole text first and changes nothing when any line is
/// wrong; every error names its line.
/// </summary>
/// <remarks>
/// Accounts, grouped by server:
/// <code>
/// #Coldeve
/// Name=notan3,Password=secret,Profiles=Main;Bots
/// </code>
/// Logon commands, grouped by server and account:
/// <code>
/// #Coldeve
/// ##notan3
/// /vt start
/// </code>
/// In both, a server's section is the whole truth for that server, and a server left out of the
/// text is left as it is.
/// </remarks>
public static class LauncherProfileText
{
    private const string ServerPrefix = "#";
    private const string AccountPrefix = "##";

    public static string Read(LauncherProfileDocument document, LauncherTextEditorKind kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        return kind switch
        {
            LauncherTextEditorKind.Accounts => ReadAccounts(document),
            LauncherTextEditorKind.Servers => string.Join(Environment.NewLine, document.Servers
                .Select(s => $"{Encode(s.Name)} | {Encode(s.Host)} | {s.Port}")),
            LauncherTextEditorKind.LogonCommands => ReadLogonCommands(document),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public static void Apply(LauncherProfileDocument document, LauncherTextEditorKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(text);
        switch (kind)
        {
            case LauncherTextEditorKind.Accounts:
                ApplyAccounts(document, text);
                break;
            case LauncherTextEditorKind.Servers:
                ApplyServers(document, text);
                break;
            case LauncherTextEditorKind.LogonCommands:
                ApplyLogonCommands(document, text);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // --- Accounts -----------------------------------------------------------

    private static string ReadAccounts(LauncherProfileDocument document)
    {
        var text = new StringBuilder();
        foreach (ServerProfile server in document.Servers)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append(ServerPrefix).AppendLine(EncodeHeader(server.Name));
            foreach (AccountProfile account in server.Accounts)
            {
                text.Append("Name=").Append(Encode(account.Account))
                    .Append(",Password=").Append(Encode(account.Password));
                if (account.Profiles.Count > 0)
                {
                    text.Append(",Profiles=").Append(Encode(string.Join(';', account.Profiles)));
                }

                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static void ApplyAccounts(LauncherProfileDocument document, string text)
    {
        var errors = new List<string>();
        var sections = new List<(ServerProfile Server, List<(int Line, string Name, string Password, List<string> Profiles)> Accounts)>();
        List<(int Line, string Name, string Password, List<string> Profiles)>? current = null;
        bool serverSeen = false;
        foreach ((int number, string line) in Lines(text))
        {
            if (line.StartsWith(ServerPrefix, StringComparison.Ordinal))
            {
                serverSeen = true;
                ServerProfile? server = ResolveServer(document, line[ServerPrefix.Length..], number, errors);
                if (server is not null && sections.Any(section => ReferenceEquals(section.Server, server)))
                {
                    errors.Add($"Line {number}: server '{server.Name}' appears more than once.");
                    server = null;
                }

                current = server is null ? null : [];
                if (server is not null)
                {
                    sections.Add((server, current!));
                }

                continue;
            }

            if (current is null)
            {
                // Under a server line that was wrong, the error is already reported.
                if (!serverSeen)
                {
                    errors.Add($"Line {number}: put a #Server line above the accounts on that server.");
                }

                continue;
            }

            if (TryParseAccountLine(line, number, errors) is { } account)
            {
                if (current.Any(existing => existing.Name == account.Name))
                {
                    errors.Add($"Line {number}: account '{account.Name}' appears more than once on this server.");
                    continue;
                }

                current.Add((number, account.Name, account.Password, account.Profiles));
            }
        }

        ThrowIfAny(errors);
        foreach ((ServerProfile server, var accounts) in sections)
        {
            server.Accounts = [.. accounts.Select(parsed =>
            {
                AccountProfile account = server.Accounts.Find(existing => existing.Account == parsed.Name)
                    ?? new AccountProfile { Account = parsed.Name };
                account.Password = parsed.Password;
                account.Profiles = parsed.Profiles;
                return account;
            })];
        }
    }

    private static (string Name, string Password, List<string> Profiles)? TryParseAccountLine(
        string line,
        int number,
        List<string> errors)
    {
        string[] fields;
        try
        {
            fields = SplitFields(line, ',');
        }
        catch (LauncherProfileException ex)
        {
            errors.Add($"Line {number}: {ex.Message}");
            return null;
        }

        string? name = null;
        string? password = null;
        List<string> profiles = [];
        foreach (string field in fields)
        {
            int equals = field.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                errors.Add($"Line {number}: expected Name=…,Password=… (a server line starts with #).");
                return null;
            }

            string key = field[..equals].Trim();
            string value;
            try
            {
                value = Decode(field[(equals + 1)..]);
            }
            catch (LauncherProfileException ex)
            {
                errors.Add($"Line {number}: {ex.Message}");
                return null;
            }

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase) && name is null)
            {
                name = value;
            }
            else if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) && password is null)
            {
                password = value;
            }
            else if (key.Equals("Profiles", StringComparison.OrdinalIgnoreCase) && profiles.Count == 0)
            {
                foreach (string tag in value.Split(';').Select(tag => tag.Trim()).Where(tag => tag.Length > 0))
                {
                    if (tag.IndexOfAny([',', '=', '"', '#', '\r', '\n', '\t']) >= 0)
                    {
                        errors.Add($"Line {number}: '{tag}' is not a profile name. Use letters, digits and spaces.");
                        return null;
                    }

                    if (profiles.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add($"Line {number}: profile '{tag}' is listed twice.");
                        return null;
                    }

                    profiles.Add(tag);
                }
            }
            else
            {
                errors.Add($"Line {number}: '{key}' is not Name, Password or Profiles, or appears twice.");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"Line {number}: each account needs Name=.");
            return null;
        }

        return (name, password ?? string.Empty, profiles);
    }

    // --- Logon commands -----------------------------------------------------

    private static string ReadLogonCommands(LauncherProfileDocument document)
    {
        var text = new StringBuilder();
        foreach (ServerProfile server in document.Servers)
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append(ServerPrefix).AppendLine(EncodeHeader(server.Name));
            foreach (AccountProfile account in server.Accounts)
            {
                text.Append(AccountPrefix).AppendLine(EncodeHeader(account.Account));
                foreach (string command in account.LoginCommands)
                {
                    text.AppendLine(command);
                }
            }
        }

        return text.ToString();
    }

    private static void ApplyLogonCommands(LauncherProfileDocument document, string text)
    {
        var errors = new List<string>();
        var changes = new Dictionary<ServerProfile, Dictionary<AccountProfile, List<string>>>();
        ServerProfile? server = null;
        bool serverSeen = false;
        // True under a # or ## line that was wrong: its lines are skipped, the error already reported.
        bool skipping = false;
        List<string>? commands = null;
        foreach ((int number, string line) in Lines(text))
        {
            if (line.StartsWith(AccountPrefix, StringComparison.Ordinal))
            {
                commands = null;
                skipping = true;
                if (!serverSeen)
                {
                    errors.Add($"Line {number}: put a #Server line above ##Account.");
                    continue;
                }

                if (server is null)
                {
                    continue;
                }

                if (DecodeHeader(line[AccountPrefix.Length..], number, errors) is not { } name)
                {
                    continue;
                }

                AccountProfile? account = server.Accounts.Find(candidate => candidate.Account == name);
                if (account is null)
                {
                    errors.Add($"Line {number}: server '{server.Name}' has no account '{name}'. Add it in Edit accounts first.");
                    continue;
                }

                if (!changes[server].TryAdd(account, commands = []))
                {
                    errors.Add($"Line {number}: account '{name}' appears more than once under '{server.Name}'.");
                    commands = null;
                    continue;
                }

                skipping = false;
                continue;
            }

            if (line.StartsWith(ServerPrefix, StringComparison.Ordinal))
            {
                serverSeen = true;
                commands = null;
                server = ResolveServer(document, line[ServerPrefix.Length..], number, errors);
                if (server is not null && !changes.TryAdd(server, []))
                {
                    errors.Add($"Line {number}: server '{server.Name}' appears more than once.");
                    server = null;
                }

                skipping = server is null;
                continue;
            }

            if (commands is not null)
            {
                commands.Add(line);
            }
            else if (skipping)
            {
                // Belongs to a header line that was wrong and is already reported.
            }
            else
            {
                errors.Add($"Line {number}: a command needs an ##Account line above it.");
            }
        }

        ThrowIfAny(errors);
        foreach ((ServerProfile changedServer, Dictionary<AccountProfile, List<string>> accounts) in changes)
        {
            foreach (AccountProfile account in changedServer.Accounts)
            {
                account.LoginCommands = accounts.TryGetValue(account, out List<string>? list) ? list : [];
            }
        }
    }

    // --- Servers ------------------------------------------------------------

    private static void ApplyServers(LauncherProfileDocument document, string text)
    {
        var servers = Lines(text).Select(entry =>
        {
            var fields = SplitFields(entry.Text, '|');
            Require(fields.Length == 3, "Each server line must be name | host | port.");
            Require(int.TryParse(Decode(fields[2]), out int port), "Server ports must be numbers from 1 to 65535.");
            return (Name: Decode(fields[0]), Host: Decode(fields[1]), Port: port);
        }).ToArray();
        Require(servers.All(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Host) && s.Port is >= 1 and <= 65535),
            "Each server needs a name, host and port from 1 to 65535.");
        Require(servers.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() == servers.Length, "Server names must be unique.");
        document.Servers = [.. servers.Select(s => new ServerProfile
        {
            Name = s.Name,
            Host = s.Host,
            Port = s.Port,
            Accounts = document.Servers.Find(old => old.Name == s.Name)?.Accounts ?? [],
        })];
    }

    // --- Shared -------------------------------------------------------------

    /// <summary>A server named on a # line: its exact name, or failing that the one server whose name
    /// matches ignoring case.</summary>
    private static ServerProfile? ResolveServer(
        LauncherProfileDocument document,
        string header,
        int number,
        List<string> errors)
    {
        if (DecodeHeader(header, number, errors) is not { } name)
        {
            return null;
        }

        ServerProfile? exact = document.Servers.Find(server => server.Name == name);
        if (exact is not null)
        {
            return exact;
        }

        ServerProfile[] matches = [.. document.Servers.Where(server =>
            string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 1)
        {
            return matches[0];
        }

        errors.Add(name.Length == 0
            ? $"Line {number}: a # line needs a server name."
            : $"Line {number}: there is no server named '{name}'. Add it with Add server first.");
        return null;
    }

    /// <summary>A name after # or ##, quoted like a value when it has edge spaces, a quote, or
    /// starts with # itself.</summary>
    private static string EncodeHeader(string name) =>
        name != name.Trim() || name.StartsWith('#') || name.IndexOfAny(['"', '\r', '\n', '\t']) >= 0
            ? JsonSerializer.Serialize(name)
            : name;

    private static string? DecodeHeader(string header, int number, List<string> errors)
    {
        try
        {
            return Decode(header);
        }
        catch (LauncherProfileException ex)
        {
            errors.Add($"Line {number}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The text's non-empty lines, trimmed, with their 1-based line numbers.</summary>
    private static IEnumerable<(int Number, string Text)> Lines(string text) => text.Split('\n')
        .Select((line, index) => (Number: index + 1, Text: line.Trim()))
        .Where(line => line.Text.Length > 0);

    private static void ThrowIfAny(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new LauncherProfileException(string.Join(Environment.NewLine, errors));
        }
    }

    private static string Encode(string value) =>
        value != value.Trim() || value.IndexOfAny(['|', ',', '"', '\r', '\n', '\t']) >= 0
            ? JsonSerializer.Serialize(value)
            : value;

    private static string Decode(string field)
    {
        field = field.Trim();
        if (!field.Contains('"'))
        {
            return field;
        }

        try
        {
            Require(field.StartsWith('"'), "A quoted value must have double quotes around all of it.");
            return JsonSerializer.Deserialize<string>(field) ?? "";
        }
        catch (JsonException)
        {
            throw new LauncherProfileException(
                "A quoted value is not valid. Put double quotes around a value that contains a comma "
                + "or a quote; write a quote as \\\" and a backslash as \\\\.");
        }
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

        Require(!quoted, "A quoted value is missing its closing double quote.");
        fields.Add(line[start..]);
        return [.. fields];
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new LauncherProfileException(message);
        }
    }
}
