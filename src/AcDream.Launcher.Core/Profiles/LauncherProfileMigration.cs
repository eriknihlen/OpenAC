using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

/// <summary>
/// Turns a version 1 profile file into the current shape. Version 1 kept a plugin list and logon
/// commands on every character and, optionally, one user list shared by every server; the current
/// shape keeps plugins and commands on the account (a character may still have its own plugin
/// list) and accounts per server.
/// </summary>
public static class LauncherProfileMigration
{
    /// <summary>Reads a version 1 document and returns it in the current shape, with a notice for
    /// every logon command list that could not be kept as it was.</summary>
    public static LauncherProfileDocument FromVersion1(
        ReadOnlySpan<byte> utf8Json,
        JsonSerializerOptions options,
        out IReadOnlyList<string> notices)
    {
        Version1Document document = JsonSerializer.Deserialize<Version1Document>(utf8Json, options)
            ?? throw new LauncherProfileException("The version 1 profile file is empty.");
        if (document.Servers is null)
        {
            throw new LauncherProfileException("The servers collection cannot be null.");
        }

        var messages = new List<string>();
        var result = new LauncherProfileDocument
        {
            Version = LauncherProfileStore.CurrentVersion,
            ShowBetaPlugins = document.ShowBetaPlugins,
        };

        foreach (Version1Server? server in document.Servers)
        {
            if (server is null)
            {
                throw new LauncherProfileException("A server entry cannot be null.");
            }

            List<Version1Account?> accounts = server.Accounts
                ?? throw new LauncherProfileException(
                    $"The accounts collection for server '{server.Name}' cannot be null.");
            if (document.Users is { } users)
            {
                // Version 1 gave every server exactly the shared user list; do the same here so
                // the server shows the accounts it showed before.
                accounts = [.. users.Select(user =>
                {
                    Version1Account? existing = accounts.Find(account =>
                        account is not null && account.Account == user.Account);
                    return new Version1Account
                    {
                        Account = user.Account,
                        Password = user.Password,
                        Characters = existing?.Characters ?? [],
                        SelectedCharacter = existing?.SelectedCharacter,
                        SelectedLaunchMode = existing?.SelectedLaunchMode,
                    };
                })];
            }

            result.Servers.Add(new ServerProfile
            {
                Name = server.Name,
                Host = server.Host,
                Port = server.Port,
                Accounts = [.. accounts.Select(account => MigrateAccount(server.Name, account, messages))],
            });
        }

        notices = messages;
        return result;
    }

    private static AccountProfile MigrateAccount(
        string serverName,
        Version1Account? account,
        List<string> notices)
    {
        if (account is null)
        {
            throw new LauncherProfileException($"A null account appears under server '{serverName}'.");
        }

        List<Version1Character?> characters = account.Characters
            ?? throw new LauncherProfileException(
                $"The characters collection for account '{account.Account}' cannot be null.");
        if (characters.Any(character => character is null))
        {
            throw new LauncherProfileException(
                $"A null character appears under account '{account.Account}'.");
        }

        if (characters.Any(character => character!.Plugins is null || character.LoginCommands is null))
        {
            throw new LauncherProfileException(
                $"A character under account '{account.Account}' has a null settings collection.");
        }

        Version1Character[] present = [.. characters.Select(character => character!)];
        List<string> union = UnionOfPlugins(present);
        List<string> commands = present.Length == 0 ? [] : [.. present[0].LoginCommands ?? []];
        foreach (Version1Character character in present.Skip(1))
        {
            List<string> own = character.LoginCommands ?? [];
            if (!own.SequenceEqual(commands, StringComparer.Ordinal))
            {
                notices.Add(DescribeDroppedCommands(serverName, account.Account, present[0].Name, character.Name, own));
            }
        }

        return new AccountProfile
        {
            Account = account.Account,
            Password = account.Password,
            Plugins = union,
            LoginCommands = commands,
            SelectedCharacter = account.SelectedCharacter,
            SelectedLaunchMode = account.SelectedLaunchMode,
            Characters = [.. present.Select(character => new CharacterProfile
            {
                Name = character.Name,
                Id = character.Id,
                LaunchMode = character.LaunchMode,
                Plugins = SamePlugins(WithoutNone(character.Plugins), union)
                    ? null
                    : WithoutNone(character.Plugins),
            })],
        };
    }

    /// <summary>Every plugin any character had, in the order first seen. The literal "none" an
    /// early launcher wrote for an empty list is not a plugin.</summary>
    private static List<string> UnionOfPlugins(IEnumerable<Version1Character> characters)
    {
        var union = new List<string>();
        foreach (Version1Character character in characters)
        {
            foreach (string id in WithoutNone(character.Plugins))
            {
                if (!union.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    union.Add(id);
                }
            }
        }

        return union;
    }

    private static List<string> WithoutNone(List<string>? plugins) =>
        [.. (plugins ?? []).Where(id => !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))];

    private static bool SamePlugins(List<string> own, List<string> union) =>
        own.Count == union.Count
        && own.All(id => union.Contains(id, StringComparer.OrdinalIgnoreCase));

    private static string DescribeDroppedCommands(
        string serverName,
        string accountName,
        string keptCharacter,
        string character,
        List<string> commands) =>
        commands.Count == 0
            ? $"{accountName} on {serverName}: {character} had no logon commands; it now runs {keptCharacter}'s."
            : $"{accountName} on {serverName}: {character}'s logon commands were not kept (the account now runs {keptCharacter}'s):"
              + string.Concat(commands.Select(command => Environment.NewLine + "    " + command));

    private sealed class Version1Document
    {
        [JsonRequired]
        public int Version { get; set; }

        public List<Version1Server?>? Servers { get; set; } = [];

        public List<Version1User>? Users { get; set; }

        public bool ShowBetaPlugins { get; set; }
    }

    private sealed class Version1User
    {
        [JsonRequired]
        public string Account { get; set; } = string.Empty;

        [JsonRequired]
        public string Password { get; set; } = string.Empty;
    }

    private sealed class Version1Server
    {
        [JsonRequired]
        public string Name { get; set; } = string.Empty;

        [JsonRequired]
        public string Host { get; set; } = string.Empty;

        [JsonRequired]
        public int Port { get; set; }

        public List<Version1Account?>? Accounts { get; set; } = [];
    }

    private sealed class Version1Account
    {
        [JsonRequired]
        public string Account { get; set; } = string.Empty;

        [JsonRequired]
        public string Password { get; set; } = string.Empty;

        public List<Version1Character?>? Characters { get; set; } = [];

        public string? SelectedCharacter { get; set; }

        public LaunchMode? SelectedLaunchMode { get; set; }
    }

    private sealed class Version1Character
    {
        [JsonRequired]
        public string Name { get; set; } = string.Empty;

        public string? Id { get; set; }

        public LaunchMode LaunchMode { get; set; } = LaunchMode.GuiSelect;

        public List<string>? Plugins { get; set; } = [];

        public List<string>? LoginCommands { get; set; } = [];
    }
}
