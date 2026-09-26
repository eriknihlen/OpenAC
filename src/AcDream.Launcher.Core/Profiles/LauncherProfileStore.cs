using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Launcher.Core;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileStore
{
    internal const int CurrentVersion = 2;

    /// <summary>Plugins and logon commands per character, and optionally one user list shared by
    /// every server.</summary>
    internal const int Version1 = 1;

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

    /// <summary>The file this launcher reads and writes.</summary>
    public const string FileName = "launcher-profiles.v2.json";

    /// <summary>The file launchers before version 2 read and write; never written by this one.</summary>
    public const string OlderFileName = "launcher-profiles.json";

    /// <param name="filePath">The profile file this store reads and writes.</param>
    /// <param name="olderFilePath">An older launcher's profile file, read once to migrate from when
    /// <paramref name="filePath"/> does not exist yet, and never written.</param>
    public LauncherProfileStore(string filePath, string? olderFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
        OlderFilePath = olderFilePath is null ? null : Path.GetFullPath(olderFilePath);
        Document = new LauncherProfileDocument();
    }

    public static LauncherProfileStore ForApplicationPaths(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new LauncherProfileStore(
            Path.Combine(paths.ConfigDirectory, FileName),
            Path.Combine(paths.ConfigDirectory, OlderFileName));
    }

    public string FilePath { get; }

    /// <summary>An older launcher's profile file this store migrates from; null for none.</summary>
    public string? OlderFilePath { get; }

    public LauncherProfileDocument Document { get; private set; }

    /// <summary>A copy of the older launcher's file as it was when it was migrated, kept whatever
    /// happens to that file later.</summary>
    public string Version1BackupPath
    {
        get
        {
            string source = OlderFilePath ?? FilePath;
            return Path.Combine(
                Path.GetDirectoryName(source) ?? string.Empty,
                Path.GetFileNameWithoutExtension(source) + ".v1-backup.json");
        }
    }

    /// <summary>What the last <see cref="Load"/> could not carry over from an older file, in words
    /// for the player; null when it had nothing to say. Shown once, after the file is rewritten.</summary>
    public string? MigrationNotice { get; private set; }

    public bool Load()
    {
        DeleteStaleTempFile(FilePath + ".tmp");
        MigrationNotice = null;

        // This launcher's own file wins; without one, the older launcher's file is read once and
        // migrated into it. The older file is never written, so an older launcher keeps working.
        string? source = File.Exists(FilePath) ? FilePath
            : OlderFilePath is not null && File.Exists(OlderFilePath) ? OlderFilePath
            : null;
        if (source is null)
        {
            Document = new LauncherProfileDocument();
            return false;
        }

        EnsureExistingCredentialFilePermissions(source);

        byte[] bytes = File.ReadAllBytes(source);
        int version = ReadVersion(bytes, source);
        LauncherProfileDocument? document;
        IReadOnlyList<string> notices = [];
        try
        {
            document = version switch
            {
                CurrentVersion => JsonSerializer.Deserialize<LauncherProfileDocument>(
                    bytes,
                    SerializerOptions),
                Version1 => LauncherProfileMigration.FromVersion1(bytes, SerializerOptions, out notices),
                _ => throw new LauncherProfileException(
                    $"Unsupported launcher-profiles version {version}; expected {CurrentVersion}."),
            };
        }
        catch (JsonException ex)
        {
            throw new LauncherProfileException(
                $"'{source}' is not a valid launcher profile document.",
                ex);
        }

        if (document is null)
        {
            throw new LauncherProfileException($"'{source}' is empty.");
        }

        ValidateAndNormalizeDocument(document);
        Document = document;
        if (version == Version1 || source != FilePath)
        {
            // A copy of the old file as migrated, whatever an older launcher does to it later.
            if (version == Version1 && !File.Exists(Version1BackupPath))
            {
                WriteCredentialFile(Version1BackupPath, stream => stream.Write(bytes));
            }

            Save();
            MigrationNotice = notices.Count == 0
                ? null
                : "Plugins and logon commands now belong to the account, so every character on it "
                  + "shares them. Where characters on one account had different logon commands, "
                  + "the first character's were kept:"
                  + Environment.NewLine + Environment.NewLine
                  + string.Join(Environment.NewLine, notices)
                  + Environment.NewLine + Environment.NewLine
                  + $"The old file is kept at {Version1BackupPath}.";
        }

        return true;
    }

    private static int ReadVersion(byte[] bytes, string source)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("version", out JsonElement version)
                && version.TryGetInt32(out int value))
            {
                return value;
            }
        }
        catch (JsonException ex)
        {
            throw new LauncherProfileException(
                $"'{source}' is not a valid launcher profile document.",
                ex);
        }

        throw new LauncherProfileException(
            $"'{source}' is not a valid launcher profile document: it has no version.");
    }

    public void Save() =>
        WriteCredentialFile(
            FilePath,
            stream => JsonSerializer.Serialize(stream, Document, SerializerOptions));

    /// <summary>Writes a file that holds passwords: owner-only from its first byte, and whole or not
    /// at all.</summary>
    private static void WriteCredentialFile(string path, Action<Stream> write)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + ".tmp";
        DeleteStaleTempFile(tempPath);
        try
        {
            using (FileStream stream = CreateCredentialTempFile(tempPath))
            {
                if (LauncherOperatingSystem.IsUnix)
                {
                    File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);
                }

                write(stream);
            }

            if (LauncherOperatingSystem.IsUnix
                && File.GetUnixFileMode(tempPath) != OwnerOnlyFileMode)
            {
                throw new IOException(
                    "The launcher credential temp file could not be secured to mode 0600.");
            }

            File.Move(tempPath, path, overwrite: true);
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

        if (LauncherOperatingSystem.IsUnix)
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

    /// <summary>The launcher-wide beta discovery setting.</summary>
    public void SetShowBetaPlugins(bool value) => Document.ShowBetaPlugins = value;

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

    /// <summary>The plugins every character on the account launches with, unless it has its own list.</summary>
    public void SetAccountPlugins(string serverName, string account, IReadOnlyList<string> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ValidateStringList(plugins, "plugin", requireUnique: true);
        FindAccountOrThrow(FindServerOrThrow(serverName), account).Plugins = [.. plugins];
    }

    /// <summary>The account's profile tags, which the main window filters by.</summary>
    public void SetAccountProfiles(string serverName, string account, IReadOnlyList<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ValidateProfileTags(profiles);
        FindAccountOrThrow(FindServerOrThrow(serverName), account).Profiles = [.. profiles];
    }

    /// <summary>Commands run in order after any character on the account logs in.</summary>
    public void SetAccountLoginCommands(string serverName, string account, IReadOnlyList<string> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ValidateStringList(commands, "login command", requireUnique: false);
        FindAccountOrThrow(FindServerOrThrow(serverName), account).LoginCommands = [.. commands];
    }

    /// <summary>Gives a character its own plugin list, or with null puts it back on its account's.</summary>
    public void SetCharacterPlugins(
        string serverName,
        string account,
        string characterName,
        IReadOnlyList<string>? plugins)
    {
        ValidateStringList(plugins, "plugin", requireUnique: true);
        CharacterProfile character = FindCharacterOrThrow(
            FindAccountOrThrow(FindServerOrThrow(serverName), account),
            characterName);
        character.Plugins = plugins is null ? null : [.. plugins];
    }

    /// <summary>Remembers what the account's row is set to launch, so it survives a restart.</summary>
    public void EditAccountSelection(
        string serverName,
        string account,
        string? selectedCharacter,
        LaunchMode selectedLaunchMode)
    {
        ServerProfile server = FindServerOrThrow(serverName);
        AccountProfile profile = FindAccountOrThrow(server, account);
        if (selectedCharacter is not null && FindCharacter(profile, selectedCharacter) is null)
        {
            throw new LauncherProfileException(
                $"Character '{selectedCharacter}' is not on account '{account}'.");
        }
        profile.SelectedCharacter = selectedCharacter;
        profile.SelectedLaunchMode = selectedLaunchMode;
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
        IReadOnlyList<string>? plugins = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        RequireValidLaunchMode(launchMode);
        ValidateStringList(plugins, "plugin", requireUnique: true);
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
            Plugins = plugins is null ? null : [.. plugins],
        };
        profile.Characters.Add(character);
        return character;
    }

    public void EditCharacter(
        string serverName,
        string account,
        string characterName,
        LaunchMode? launchMode = null,
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

        character.Name = newName ?? character.Name;
        if (newId is not null)
        {
            character.Id = normalizedId;
        }

        if (launchMode is not null)
        {
            character.LaunchMode = launchMode.Value;
        }
    }

    private static void EnsureExistingCredentialFilePermissions(string path)
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if (mode != OwnerOnlyFileMode)
            {
                File.SetUnixFileMode(path, OwnerOnlyFileMode);
                mode = File.GetUnixFileMode(path);
            }

            if (mode != OwnerOnlyFileMode)
            {
                throw new IOException($"Mode remained {mode} after normalization.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherProfileException(
                $"'{path}' could not be secured to owner-only mode 0600.",
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

    /// <summary>A deep copy through the file format itself, so a field added later is never left out
    /// of a rollback.</summary>
    private static LauncherProfileDocument CloneDocument(LauncherProfileDocument source) =>
        JsonSerializer.Deserialize<LauncherProfileDocument>(
            JsonSerializer.SerializeToUtf8Bytes(source, SerializerOptions),
            SerializerOptions)!;

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

                if (account.Characters is null
                    || account.Plugins is null
                    || account.LoginCommands is null
                    || account.Profiles is null)
                {
                    throw new LauncherProfileException(
                        $"Account '{account.Account}' has a null collection.");
                }

                ValidateStringList(account.Plugins, "plugin", requireUnique: true);
                ValidateStringList(account.LoginCommands, "login command", requireUnique: false);
                ValidateProfileTags(account.Profiles);

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

                    ValidateStringList(character.Plugins, "plugin", requireUnique: true);
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

    /// <summary>A profile tag is a short name with none of the characters the accounts text uses
    /// as separators, and appears once per account whatever its case.</summary>
    private static void ValidateProfileTags(IReadOnlyList<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag != tag.Trim()
                || tag.IndexOfAny([',', ';', '=', '"', '#', '\r', '\n', '\t']) >= 0)
            {
                throw new LauncherProfileException(
                    $"'{tag}' is not a profile name. Use letters, digits and spaces, without , ; = \" or #.");
            }

            if (!seen.Add(tag))
            {
                throw new LauncherProfileException($"The profile '{tag}' appears more than once.");
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
