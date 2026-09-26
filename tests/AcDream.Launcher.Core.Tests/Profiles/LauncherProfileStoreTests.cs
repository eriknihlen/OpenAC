using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class LauncherProfileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;

    public LauncherProfileStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-launcher-profile-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, "launcher-profiles.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void LoadOnMissingFileYieldsEmptyDocumentWithoutTouchingDisk()
    {
        var store = new LauncherProfileStore(_filePath);

        bool loaded = store.Load();

        Assert.False(loaded);
        Assert.False(File.Exists(_filePath));
        Assert.Equal(2, store.Document.Version);
        Assert.Empty(store.Document.Servers);
    }

    [Fact]
    public void AddServerThenSaveThenReloadRoundTrips()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();

        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.Save();

        Assert.True(File.Exists(_filePath));

        var reloaded = new LauncherProfileStore(_filePath);
        reloaded.Load();

        ServerProfile server = Assert.Single(reloaded.Document.Servers);
        Assert.Equal("Local ACE", server.Name);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Equal(9000, server.Port);
        Assert.Empty(server.Accounts);
    }

    [Fact]
    public void SaveNeverWritesShowBetaPluginsWhenFalse()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);

        store.Save();

        Assert.DoesNotContain("showBetaPlugins", File.ReadAllText(_filePath));
    }

    [Fact]
    public void SaveNeverWritesAnUnsetAccountRowSelection()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "pw");

        store.Save();

        string saved = File.ReadAllText(_filePath);
        Assert.DoesNotContain("selectedCharacter", saved);
        Assert.DoesNotContain("selectedLaunchMode", saved);
    }

    [Fact]
    public void ShowBetaPluginsRoundTrips()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.SetShowBetaPlugins(true);
        store.Save();
        Assert.Contains("\"showBetaPlugins\": true", File.ReadAllText(_filePath));

        var reloaded = new LauncherProfileStore(_filePath);
        reloaded.Load();

        Assert.True(reloaded.Document.ShowBetaPlugins);
    }

    [Fact]
    public void AddServerRejectsDuplicateName()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);

        var ex = Assert.Throws<LauncherProfileException>(
            () => store.AddServer("Local ACE", "127.0.0.1", 9001));
        Assert.Contains("Local ACE", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void AddServerRejectsOutOfRangePort(int port)
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();

        Assert.Throws<LauncherProfileException>(
            () => store.AddServer("Local ACE", "127.0.0.1", port));
    }

    [Fact]
    public void EditServerRenamesAndUpdatesHostAndPort()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);

        store.EditServer("Local ACE", newName: "Home ACE", newHost: "10.0.0.5", newPort: 9001);

        ServerProfile server = Assert.Single(store.Document.Servers);
        Assert.Equal("Home ACE", server.Name);
        Assert.Equal("10.0.0.5", server.Host);
        Assert.Equal(9001, server.Port);
    }

    [Fact]
    public void EditServerOnUnknownNameThrows()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();

        Assert.Throws<LauncherProfileException>(
            () => store.EditServer("Nope", newHost: "1.2.3.4"));
    }

    [Fact]
    public void RemoveServerRemovesIt()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);

        store.RemoveServer("Local ACE");

        Assert.Empty(store.Document.Servers);
    }

    [Fact]
    public void AddEditRemoveAccountRoundTrip()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);

        store.AddAccount("Local ACE", "testaccount", "testpassword");
        AccountProfile account = Assert.Single(
            store.Document.Servers.Single().Accounts);
        Assert.Equal("testaccount", account.Account);
        Assert.Equal("testpassword", account.Password);

        store.EditAccount(
            "Local ACE",
            "testaccount",
            newAccount: "renamed",
            newPassword: "newpass");
        account = Assert.Single(store.Document.Servers.Single().Accounts);
        Assert.Equal("renamed", account.Account);
        Assert.Equal("newpass", account.Password);

        store.RemoveAccount("Local ACE", "renamed");
        Assert.Empty(store.Document.Servers.Single().Accounts);
    }

    [Fact]
    public void AddAccountRejectsDuplicateAccountOnSameServer()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "pw");

        Assert.Throws<LauncherProfileException>(
            () => store.AddAccount("Local ACE", "testaccount", "pw2"));
    }

    [Fact]
    public void CharacterAndAccountSettersChangeOnlyWhatTheyName()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "pw");
        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);

        store.EditCharacter(
            "Local ACE",
            "testaccount",
            "+Acdream",
            launchMode: LaunchMode.Headless);
        store.SetCharacterPlugins("Local ACE", "testaccount", "+Acdream", ["ExamplePlugin"]);
        store.SetAccountLoginCommands("Local ACE", "testaccount", ["/tell someone, hi"]);

        AccountProfile account = store.Document.Servers.Single().Accounts.Single();
        CharacterProfile character = Assert.Single(account.Characters);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Equal(["ExamplePlugin"], character.Plugins);
        Assert.Equal(["/tell someone, hi"], account.LoginCommands);
        Assert.Equal("0x5000000A", character.Id);
    }

    [Fact]
    public void AddEditAndRemoveCachedCharacterRoundTripsThroughTheCrudSurface()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "pw");

        store.AddCharacter(
            "Local ACE",
            "testaccount",
            "+Manual",
            "0x5000000a");
        CharacterProfile character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal("0x5000000A", character.Id);
        Assert.Equal(LaunchMode.GuiSelect, character.LaunchMode);

        store.EditCharacter(
            "Local ACE",
            "testaccount",
            "+Manual",
            newName: "+Renamed",
            newId: "",
            launchMode: LaunchMode.Headless);
        character = Assert.Single(
            store.Document.Servers.Single().Accounts.Single().Characters);
        Assert.Equal("+Renamed", character.Name);
        Assert.Null(character.Id);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);

        store.RemoveCharacter("Local ACE", "testaccount", "+Renamed");
        Assert.Empty(store.Document.Servers.Single().Accounts.Single().Characters);
    }

    [Theory]
    [InlineData("5000000A")]
    [InlineData("0x00000000")]
    [InlineData("not-an-id")]
    public void AddCharacterRejectsAnAmbiguousOrInvalidId(string id)
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "pw");

        Assert.Throws<LauncherProfileException>(() =>
            store.AddCharacter(
                "Local ACE",
                "testaccount",
                "+Manual",
                id));
    }

    [Fact]
    public void FullProfileWithServersAccountsAndCharactersRoundTripsThroughDisk()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "testpassword");
        store.MergeRoster(
            "Local ACE",
            "testaccount",
            [new CharacterRosterEntry(0x5000000A, "+Acdream", 0)]);
        store.EditCharacter(
            "Local ACE",
            "testaccount",
            "+Acdream",
            launchMode: LaunchMode.Gui);
        store.SetAccountPlugins("Local ACE", "testaccount", ["ExamplePlugin"]);
        store.SetAccountProfiles("Local ACE", "testaccount", ["Main", "Bots"]);
        store.SetAccountLoginCommands("Local ACE", "testaccount", ["/vt start"]);
        store.Save();

        string text = File.ReadAllText(_filePath);
        Assert.Contains("\"launchMode\":\"gui\"", text.Replace(" ", string.Empty));

        var reloaded = new LauncherProfileStore(_filePath);
        reloaded.Load();

        ServerProfile server = Assert.Single(reloaded.Document.Servers);
        AccountProfile account = Assert.Single(server.Accounts);
        CharacterProfile character = Assert.Single(account.Characters);
        Assert.Equal("+Acdream", character.Name);
        Assert.Equal("0x5000000A", character.Id);
        Assert.Equal(LaunchMode.Gui, character.LaunchMode);
        Assert.Null(character.Plugins);
        Assert.Equal(["ExamplePlugin"], account.Plugins);
        Assert.Equal(["Main", "Bots"], account.Profiles);
        Assert.Equal(["/vt start"], account.LoginCommands);
    }

    [Fact]
    public void LoadRejectsUnsupportedVersion()
    {
        File.WriteAllText(_filePath, """{"version":3,"servers":[]}""");
        var store = new LauncherProfileStore(_filePath);

        Assert.Throws<LauncherProfileException>(() => store.Load());
    }

    [Fact]
    public void LoadRejectsUnmappedMembersStrictly()
    {
        File.WriteAllText(
            _filePath,
            """{"version":1,"servers":[],"unexpectedField":true}""");
        var store = new LauncherProfileStore(_filePath);

        Assert.Throws<LauncherProfileException>(() => store.Load());
    }

    [Fact]
    public void SaveWritesCamelCaseJson()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.Save();

        string text = File.ReadAllText(_filePath);
        Assert.Contains("\"version\"", text);
        Assert.Contains("\"servers\"", text);
        Assert.Contains("\"host\"", text);
        Assert.DoesNotContain("\"Version\"", text);
        Assert.DoesNotContain("\"Servers\"", text);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void SaveSetsOwnerOnlyPermissionsOnUnix()
    {
        if (!LauncherOperatingSystem.IsUnix)
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");

        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "testpassword");
        store.Save();

        UnixFileMode mode = File.GetUnixFileMode(_filePath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            mode);
    }

    [Fact]
    public void TempCredentialCreationOptionsRequestAtomicPlatformCorrectCreation()
    {
        FileStreamOptions options =
            LauncherProfileStore.CreateCredentialTempFileOptions();
        Assert.Equal(FileMode.CreateNew, options.Mode);
        Assert.Equal(FileAccess.Write, options.Access);
        Assert.Equal(FileShare.None, options.Share);

        if (LauncherOperatingSystem.IsUnix)
        {
            Assert.Equal(
                LauncherProfileStore.OwnerOnlyFileMode,
                options.UnixCreateMode);
        }
        else
        {
            Assert.Null(options.UnixCreateMode);
        }
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void TempCredentialFileIsOwnerOnlyFromItsFirstObservableUnixState()
    {
        if (!LauncherOperatingSystem.IsUnix)
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");

        string tempPath = _filePath + ".tmp";
        using FileStream stream = LauncherProfileStore.CreateCredentialTempFile(tempPath);

        Assert.Equal(
            LauncherProfileStore.OwnerOnlyFileMode,
            File.GetUnixFileMode(tempPath));
    }

    [Fact]
    public void SaveDeletesTheStaleTempFileWhenTheFinalRenameFails()
    {
        Directory.CreateDirectory(_filePath);
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "testpassword");

        Assert.ThrowsAny<Exception>(() => store.Save());

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void LoadDeletesAStaleTempFileLeftBehindByACrashedSave()
    {
        File.WriteAllText(_filePath + ".tmp", """{"version":1,"servers":[]}""");

        var store = new LauncherProfileStore(_filePath);
        store.Load();

        Assert.False(File.Exists(_filePath + ".tmp"));
    }
}
