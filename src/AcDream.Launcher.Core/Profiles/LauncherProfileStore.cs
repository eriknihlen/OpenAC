using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileStore
{
    internal const int CurrentVersion = 1;
    internal const UnixFileMode OwnerOnlyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    public LauncherProfileStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        Document = new LauncherProfileDocument();
    }

    public static LauncherProfileStore ForApplicationPaths(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LauncherProfileStore(
            Path.Combine(paths.ConfigDirectory, "launcher-profiles.json"));
    }

    public string FilePath { get; }

    public LauncherProfileDocument Document { get; private set; }

    public bool Load()
    {
        DeleteStaleTempFile(FilePath + ".tmp");

        if (!File.Exists(FilePath))
        {
            Document = new LauncherProfileDocument();
            return false;
        }

        EnsureExistingCredentialFilePermissions();

        LauncherProfileDocument? document;
        using (FileStream stream = File.OpenRead(FilePath))
        {
            try
            {
                document = JsonSerializer.Deserialize<LauncherProfileDocument>(
                    stream,
                    SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new LauncherProfileException(
                    $"'{FilePath}' is not a valid launcher profile document.",
                    ex);
            }
        }

        if (document is null)
        {
            throw new LauncherProfileException($"'{FilePath}' is empty.");
        }

        if (document.Version != CurrentVersion)
        {
            throw new LauncherProfileException(
                $"Unsupported launcher-profiles version {document.Version}; "
                + $"expected {CurrentVersion}.");
        }

        ValidateAndNormalizeDocument(document);
        Document = document;
        return true;
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = FilePath + ".tmp";
        DeleteStaleTempFile(tempPath);
        try
        {
            using (FileStream stream = CreateCredentialTempFile(tempPath))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);
                }

                JsonSerializer.Serialize(stream, Document, SerializerOptions);
            }

            if (OperatingSystem.IsLinux()
                && File.GetUnixFileMode(tempPath) != OwnerOnlyFileMode)
            {
                throw new IOException(
                    "The launcher credential temp file could not be secured to mode 0600.");
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            DeleteStaleTempFile(tempPath);
            throw;
        }

    }

    internal static FileStreamOptions CreateCredentialTempFileOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = OwnerOnlyFileMode;
        }

        return options;
    }

    internal static FileStream CreateCredentialTempFile(string tempPath) =>
        new(tempPath, CreateCredentialTempFileOptions());

    private static void DeleteStaleTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
    }


    public ServerProfile AddServer(string name, string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        RequireValidPort(port);

        if (FindServer(name) is not null)
        {
            throw new LauncherProfileException(
                $"A server named '{name}' already exists.");
        }

        var server = new ServerProfile { Name = name, Host = host, Port = port };
        Document.Servers.Add(server);
        return server;
    }

    public void EditServer(
        string name,
        string? newName = null,
        string? newHost = null,
        int? newPort = null)
    {
        ServerProfile server = FindServerOrThrow(name);

        if (newName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(newName);
            if (!string.Equals(newName, server.Name, StringComparison.Ordinal)
                && FindServer(newName) is not null)
            {
                throw new LauncherProfileException(
                    $"A server named '{newName}' already exists.");
            }
        }

        if (newHost is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(newHost);
        }

        if (newPort is not null)
        {
            RequireValidPort(newPort.Value);
        }

        server.Name = newName ?? server.Name;
        server.Host = newHost ?? server.Host;
        server.Port = newPort ?? server.Port;
    }

    public void RemoveServer(string name)
    {
        ServerProfile server = FindServerOrThrow(name);
        Document.Servers.Remove(server);
    }


    public AccountProfile AddAccount(string serverName, string account, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(password);
        ServerProfile server = FindServerOrThrow(serverName);

        if (FindAccount(server, account) is not null)
        {
            throw new LauncherProfileException(
                $"Account '{account}' already exists on server '{serverName}'.");
        }

        var profile = new AccountProfile { Account = account, Password = password };
        server.Accounts.Add(profile);
        return profile;
    }

    public void EditAccount(
        string serverName,
        string account,
        string? newAccount = null,
        string? newPassword = null)
    {
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);

        if (newAccount is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(newAccount);
            if (!string.Equals(newAccount, profile.Account, StringComparison.Ordinal)
                && FindAccount(server, newAccount) is not null)
            {
                throw new LauncherProfileException(
                    $"Account '{newAccount}' already exists on server '{serverName}'.");
            }
        }

        profile.Account = newAccount ?? profile.Account;

        if (newPassword is not null)
        {
            profile.Password = newPassword;
        }
    }

    public void RemoveAccount(string serverName, string account)
    {
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);
        server.Accounts.Remove(profile);
    }


    public CharacterProfile AddCharacter(
        string serverName,
        string account,
        string characterName,
        string? id = null,
        LaunchMode launchMode = LaunchMode.GuiSelect,
        IReadOnlyList<string>? plugins = null,
        IReadOnlyList<string>? loginCommands = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        RequireValidLaunchMode(launchMode);
        ValidateStringList(plugins, "plugin", requireUnique: true);
        ValidateStringList(loginCommands, "login command", requireUnique: false);
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);

        if (FindCharacter(profile, characterName) is not null)
        {
            throw new LauncherProfileException(
                $"Character '{characterName}' already exists on account '{account}'.");
        }

        string? normalizedId = NormalizeCharacterId(id);
        if (normalizedId is not null
            && profile.Characters.Any(character => CharacterIdsEqual(character.Id, normalizedId)))
        {
            throw new LauncherProfileException(
                $"Character id '{normalizedId}' already exists on account '{account}'.");
        }

        var character = new CharacterProfile
        {
            Name = characterName,
            Id = normalizedId,
            LaunchMode = launchMode,
            Plugins = plugins is null ? [] : [.. plugins],
            LoginCommands = loginCommands is null ? [] : [.. loginCommands],
        };
        profile.Characters.Add(character);
        return character;
    }

    public void EditCharacter(
        string serverName,
        string account,
        string characterName,
        LaunchMode? launchMode = null,
        IReadOnlyList<string>? plugins = null,
        IReadOnlyList<string>? loginCommands = null,
        string? newName = null,
        string? newId = null)
    {
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);
        CharacterProfile character = FindCharacterOrThrow(profile, characterName);

        string? normalizedId = null;

        if (newName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(newName);
            if (!string.Equals(newName, character.Name, StringComparison.Ordinal)
                && FindCharacter(profile, newName) is not null)
            {
                throw new LauncherProfileException(
                    $"Character '{newName}' already exists on account '{account}'.");
            }
        }

        if (newId is not null)
        {
            normalizedId = NormalizeCharacterId(newId);
            if (normalizedId is not null
                && profile.Characters.Any(candidate =>
                    !ReferenceEquals(candidate, character)
                    && CharacterIdsEqual(candidate.Id, normalizedId)))
            {
                throw new LauncherProfileException(
                    $"Character id '{normalizedId}' already exists on account '{account}'.");
            }
        }

        if (launchMode is not null)
        {
            RequireValidLaunchMode(launchMode.Value);
        }

        ValidateStringList(plugins, "plugin", requireUnique: true);
        ValidateStringList(loginCommands, "login command", requireUnique: false);

        character.Name = newName ?? character.Name;
        if (newId is not null)
        {
            character.Id = normalizedId;
        }

        if (launchMode is not null)
        {
            character.LaunchMode = launchMode.Value;
        }

        if (plugins is not null)
        {
            character.Plugins = [.. plugins];
        }

        if (loginCommands is not null)
        {
            character.LoginCommands = [.. loginCommands];
        }
    }

    private void EnsureExistingCredentialFilePermissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(FilePath);
            if (mode != OwnerOnlyFileMode)
            {
                File.SetUnixFileMode(FilePath, OwnerOnlyFileMode);
                mode = File.GetUnixFileMode(FilePath);
            }

            if (mode != OwnerOnlyFileMode)
            {
                throw new IOException($"Mode remained {mode} after normalization.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherProfileException(
                $"'{FilePath}' could not be secured to owner-only mode 0600.",
                ex);
        }
    }

    public void ExecuteTransaction(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        LauncherProfileDocument before = CloneDocument(Document);
        try
        {
            mutation();
            ValidateAndNormalizeDocument(Document);
            Save();
        }
        catch
        {
            Document = before;
            throw;
        }
    }

    public void RemoveCharacter(
        string serverName,
        string account,
        string characterName)
    {
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);
        CharacterProfile character = FindCharacterOrThrow(profile, characterName);
        profile.Characters.Remove(character);
    }

    public void MergeRoster(
        string serverName,
        string account,
        IReadOnlyList<CharacterRosterEntry> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);

        var rosterIds = new HashSet<uint>();
        var rosterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (CharacterRosterEntry entry in roster)
        {
            if (entry.Id == 0)
            {
                throw new LauncherProfileException("A roster character id cannot be zero.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
            if (!rosterIds.Add(entry.Id) || !rosterNames.Add(entry.Name))
            {
                throw new LauncherProfileException(
                    "The reported character roster contains a duplicate id or name.");
            }
        }

        foreach (CharacterRosterEntry entry in roster)
        {
            string idText = CharacterIdFormat.ToHexString(entry.Id);

            CharacterProfile[] matches = profile.Characters
                .Where(character =>
                    (CharacterIdFormat.TryParse(character.Id, out uint existingId)
                        && existingId == entry.Id)
                    || string.Equals(character.Name, entry.Name, StringComparison.Ordinal))
                .ToArray();
            CharacterProfile? existing = matches.FirstOrDefault(character =>
                    CharacterIdFormat.TryParse(character.Id, out uint existingId)
                    && existingId == entry.Id)
                ?? matches.FirstOrDefault();

            if (existing is not null)
            {
                existing.Id = idText;
                existing.Name = entry.Name;
                foreach (CharacterProfile duplicate in matches)
                {
                    if (!ReferenceEquals(duplicate, existing))
                    {
                        profile.Characters.Remove(duplicate);
                    }
                }
                continue;
            }

            profile.Characters.Add(new CharacterProfile
            {
                Id = idText,
                Name = entry.Name,
                LaunchMode = LaunchMode.GuiSelect,
                Plugins = [],
                LoginCommands = [],
            });
        }
    }

    // --- Lookups -------------------------------------------------------

    private ServerProfile? FindServer(string name) =>
        Document.Servers.Find(
            server => string.Equals(server.Name, name, StringComparison.Ordinal));

    private ServerProfile FindServerOrThrow(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return FindServer(name)
            ?? throw new LauncherProfileException($"No server named '{name}'.");
    }

    private static AccountProfile? FindAccount(ServerProfile server, string account) =>
        server.Accounts.Find(
            candidate => string.Equals(candidate.Account, account, StringComparison.Ordinal));

    private static AccountProfile FindAccountOrThrow(ServerProfile server, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return FindAccount(server, account)
            ?? throw new LauncherProfileException(
                $"No account '{account}' on server '{server.Name}'.");
    }

    private static CharacterProfile? FindCharacter(
        AccountProfile profile,
        string characterName) =>
        profile.Characters.Find(
            character => string.Equals(
                character.Name,
                characterName,
                StringComparison.Ordinal));

    private static CharacterProfile FindCharacterOrThrow(
        AccountProfile profile,
        string characterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        return FindCharacter(profile, characterName)
            ?? throw new LauncherProfileException(
                $"No character '{characterName}' on account '{profile.Account}'.");
    }

    private static string? NormalizeCharacterId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (!CharacterIdFormat.TryParse(id, out uint parsed) || parsed == 0)
        {
            throw new LauncherProfileException(
                "Character id must be a non-zero hexadecimal value with a 0x prefix.");
        }

        return CharacterIdFormat.ToHexString(parsed);
    }

    private static bool CharacterIdsEqual(string? left, string? right) =>
        CharacterIdFormat.TryParse(left, out uint leftId)
        && CharacterIdFormat.TryParse(right, out uint rightId)
        && leftId == rightId;

    private static LauncherProfileDocument CloneDocument(
        LauncherProfileDocument source) =>
        new()
        {
            Version = source.Version,
            Servers = source.Servers.Select(server => new ServerProfile
            {
                Name = server.Name,
                Host = server.Host,
                Port = server.Port,
                Accounts = server.Accounts.Select(account => new AccountProfile
                {
                    Account = account.Account,
                    Password = account.Password,
                    Characters = account.Characters.Select(character => new CharacterProfile
                    {
                        Name = character.Name,
                        Id = character.Id,
                        LaunchMode = character.LaunchMode,
                        Plugins = [.. character.Plugins],
                        LoginCommands = [.. character.LoginCommands],
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        };

    private static void ValidateAndNormalizeDocument(LauncherProfileDocument document)
    {
        if (document.Servers is null)
        {
            throw new LauncherProfileException("The servers collection cannot be null.");
        }

        var serverNames = new HashSet<string>(StringComparer.Ordinal);
        var normalizedIds = new List<(CharacterProfile Character, uint Id)>();
        foreach (ServerProfile? server in document.Servers)
        {
            if (server is null)
            {
                throw new LauncherProfileException("A server entry cannot be null.");
            }

            RequireLoadedText(server.Name, "server name");
            RequireLoadedText(server.Host, $"host for server '{server.Name}'");
            RequireValidPort(server.Port);
            if (!serverNames.Add(server.Name))
            {
                throw new LauncherProfileException(
                    $"A server named '{server.Name}' appears more than once.");
            }

            if (server.Accounts is null)
            {
                throw new LauncherProfileException(
                    $"The accounts collection for server '{server.Name}' cannot be null.");
            }

            var accountNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (AccountProfile? account in server.Accounts)
            {
                if (account is null)
                {
                    throw new LauncherProfileException(
                        $"A null account appears under server '{server.Name}'.");
                }

                RequireLoadedText(account.Account, "account name");
                if (account.Password is null)
                {
                    throw new LauncherProfileException(
                        $"Password for account '{account.Account}' cannot be null.");
                }

                if (!accountNames.Add(account.Account))
                {
                    throw new LauncherProfileException(
                        $"Account '{account.Account}' appears more than once on server '{server.Name}'.");
                }

                if (account.Characters is null)
                {
                    throw new LauncherProfileException(
                        $"The characters collection for account '{account.Account}' cannot be null.");
                }

                var characterNames = new HashSet<string>(StringComparer.Ordinal);
                var characterIds = new HashSet<uint>();
                foreach (CharacterProfile? character in account.Characters)
                {
                    if (character is null)
                    {
                        throw new LauncherProfileException(
                            $"A null character appears under account '{account.Account}'.");
                    }

                    RequireLoadedText(character.Name, "character name");
                    if (!characterNames.Add(character.Name))
                    {
                        throw new LauncherProfileException(
                            $"Character '{character.Name}' appears more than once on account '{account.Account}'.");
                    }

                    RequireValidLaunchMode(character.LaunchMode);
                    if (character.Id is not null)
                    {
                        if (!CharacterIdFormat.TryParse(character.Id, out uint id) || id == 0)
                        {
                            throw new LauncherProfileException(
                                $"Character '{character.Name}' has an invalid id '{character.Id}'.");
                        }

                        if (!characterIds.Add(id))
                        {
                            throw new LauncherProfileException(
                                $"Character id '{character.Id}' appears more than once on account '{account.Account}'.");
                        }

                        normalizedIds.Add((character, id));
                    }

                    if (character.Plugins is null || character.LoginCommands is null)
                    {
                        throw new LauncherProfileException(
                            $"Character '{character.Name}' has a null settings collection.");
                    }

                    ValidateStringList(character.Plugins, "plugin", requireUnique: true);
                    ValidateStringList(
                        character.LoginCommands,
                        "login command",
                        requireUnique: false);
                }
            }
        }

        foreach ((CharacterProfile character, uint id) in normalizedIds)
        {
            character.Id = CharacterIdFormat.ToHexString(id);
        }
    }

    private static void ValidateStringList(
        IReadOnlyList<string>? values,
        string valueName,
        bool requireUnique)
    {
        if (values is null)
        {
            return;
        }

        HashSet<string>? seen = requireUnique
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new LauncherProfileException(
                    $"A {valueName} cannot be null or whitespace.");
            }

            if (seen is not null && !seen.Add(value))
            {
                throw new LauncherProfileException(
                    $"The {valueName} '{value}' appears more than once.");
            }
        }
    }

    private static void RequireLoadedText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LauncherProfileException($"The {field} cannot be null or whitespace.");
        }
    }

    private static void RequireValidLaunchMode(LaunchMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new LauncherProfileException($"Launch mode '{mode}' is not supported.");
        }
    }

    private static void RequireValidPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new LauncherProfileException(
                $"Port {port} is outside the valid 1-65535 range.");
        }
    }
}
