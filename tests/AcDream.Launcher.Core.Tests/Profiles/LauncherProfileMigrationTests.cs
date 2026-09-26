using System.Text.Json;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

/// <summary>A file an older launcher wrote (plugins and logon commands on every character, one user
/// list for every server) loads into the account shape without losing anything.</summary>
public sealed class LauncherProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "acdream-launcher-migration-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_root, LauncherProfileStore.FileName);

    private string OlderFilePath => Path.Combine(_root, LauncherProfileStore.OlderFileName);

    private LauncherProfileStore NewStore() => new(FilePath, OlderFilePath);

    public LauncherProfileMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string Version1File = """
        {
          "version": 1,
          "servers": [
            {
              "name": "sawato", "host": "localhost", "port": 9000,
              "accounts": [
                {
                  "account": "testaccount", "password": "pw1",
                  "characters": [
                    { "name": "+Mossy", "id": "0x50000027", "launchMode": "guiSelect", "plugins": ["acdream.mosstank", "openac.mosswartmassacre"], "loginCommands": [] },
                    { "name": "+Acdream", "id": "0x5000000A", "launchMode": "guiSelect", "plugins": ["openac.mosswartmassacre", "acdream.mosstank"], "loginCommands": [] }
                  ]
                },
                { "account": "notan3", "password": "pw3", "characters": [] }
              ]
            },
            {
              "name": "coldeve", "host": "play.coldeve.ac", "port": 9000,
              "accounts": [
                { "account": "testaccount", "password": "pw1", "characters": [] },
                {
                  "account": "notan3", "password": "pw3",
                  "characters": [
                    { "name": "Festivus", "id": "0x5005FBB5", "launchMode": "gui", "plugins": ["acdream.mosstank", "none"], "loginCommands": ["/vt meta load bore", "/vt start"] },
                    { "name": "Mule", "launchMode": "guiSelect", "plugins": ["example.arrow"], "loginCommands": ["/mm ws enable"] },
                    { "name": "Quiet", "launchMode": "guiSelect", "plugins": ["acdream.mosstank", "example.arrow"], "loginCommands": [] }
                  ],
                  "selectedCharacter": "Festivus",
                  "selectedLaunchMode": "headless"
                }
              ]
            }
          ],
          "users": [
            { "account": "testaccount", "password": "pw1" },
            { "account": "notan3", "password": "pw3" }
          ]
        }
        """;

    private LauncherProfileStore LoadVersion1(string json = Version1File)
    {
        File.WriteAllText(OlderFilePath, json);
        LauncherProfileStore store = NewStore();
        Assert.True(store.Load());
        return store;
    }

    private static AccountProfile Account(LauncherProfileStore store, string server, string account) =>
        store.Document.Servers.Single(item => item.Name == server).Accounts.Single(item => item.Account == account);

    [Fact]
    public void AnAccountsPluginsAreEveryPluginItsCharactersHadInFirstSeenOrder()
    {
        LauncherProfileStore store = LoadVersion1();

        Assert.Equal(["acdream.mosstank", "openac.mosswartmassacre"], Account(store, "sawato", "testaccount").Plugins);
        Assert.Equal(["acdream.mosstank", "example.arrow"], Account(store, "coldeve", "notan3").Plugins);
    }

    [Fact]
    public void OnlyACharacterWhosePluginsDifferFromItsAccountKeepsItsOwnList()
    {
        LauncherProfileStore store = LoadVersion1();

        Assert.Null(Account(store, "sawato", "testaccount").Characters.Single(c => c.Name == "+Mossy").Plugins);
        AccountProfile notan3 = Account(store, "coldeve", "notan3");
        Assert.Equal(["acdream.mosstank"], notan3.Characters.Single(c => c.Name == "Festivus").Plugins);
        Assert.Equal(["example.arrow"], notan3.Characters.Single(c => c.Name == "Mule").Plugins);
        Assert.Null(notan3.Characters.Single(c => c.Name == "Quiet").Plugins);
    }

    [Fact]
    public void ACharacterWithTheSamePluginsInAnotherOrderKeepsItsOrder()
    {
        LauncherProfileStore store = LoadVersion1();

        // +Acdream listed the same two plugins as +Mossy, the other way round; plugins load in
        // list order, so that order is kept as its own list.
        AccountProfile account = Account(store, "sawato", "testaccount");
        Assert.Null(account.Characters.Single(c => c.Name == "+Mossy").Plugins);
        Assert.Equal(["openac.mosswartmassacre", "acdream.mosstank"], account.Characters.Single(c => c.Name == "+Acdream").Plugins);
    }

    [Fact]
    public void AnAccountKeepsTheFirstCharactersCommandsAndTheNoticeListsTheOthers()
    {
        LauncherProfileStore store = LoadVersion1();

        Assert.Equal(["/vt meta load bore", "/vt start"], Account(store, "coldeve", "notan3").LoginCommands);
        Assert.Empty(Account(store, "sawato", "testaccount").LoginCommands);
        string notice = Assert.IsType<string>(store.MigrationNotice);
        Assert.Contains("Mule's logon commands were not kept", notice);
        Assert.Contains("/mm ws enable", notice);
        Assert.Contains("Quiet had no logon commands; it now runs Festivus's.", notice);
        Assert.Contains(store.Version1BackupPath, notice);
        Assert.DoesNotContain("pw3", notice);
    }

    [Fact]
    public void AccountsWhoseCharactersAgreeRaiseNoNotice()
    {
        string json = Version1File
            .Replace("\"loginCommands\": [\"/mm ws enable\"]", "\"loginCommands\": [\"/vt meta load bore\", \"/vt start\"]")
            .Replace("\"plugins\": [\"acdream.mosstank\", \"example.arrow\"], \"loginCommands\": []",
                "\"plugins\": [\"acdream.mosstank\", \"example.arrow\"], \"loginCommands\": [\"/vt meta load bore\", \"/vt start\"]");

        LauncherProfileStore store = LoadVersion1(json);

        Assert.Null(store.MigrationNotice);
    }

    [Fact]
    public void EverythingElseCarriesOver()
    {
        LauncherProfileStore store = LoadVersion1();

        AccountProfile notan3 = Account(store, "coldeve", "notan3");
        Assert.Equal("pw3", notan3.Password);
        Assert.Equal("Festivus", notan3.SelectedCharacter);
        Assert.Equal(LaunchMode.Headless, notan3.SelectedLaunchMode);
        CharacterProfile festivus = notan3.Characters[0];
        Assert.Equal(("Festivus", "0x5005FBB5", LaunchMode.Gui), (festivus.Name, festivus.Id, festivus.LaunchMode));
        Assert.Equal(["sawato", "coldeve"], store.Document.Servers.Select(server => server.Name));
        Assert.Equal(("play.coldeve.ac", 9000), (store.Document.Servers[1].Host, store.Document.Servers[1].Port));
        Assert.All(store.Document.Servers, server => Assert.Empty(server.Accounts.SelectMany(account => account.Profiles)));
    }

    [Fact]
    public void TheSharedUserListGivesEveryServerItsAccountsAsBefore()
    {
        const string json = """
            {"version":1,
             "servers":[
               {"name":"one","host":"h","port":9000,"accounts":[{"account":"b","password":"stale","characters":[{"name":"Hero","plugins":["p"],"loginCommands":[]}]}]},
               {"name":"two","host":"h","port":9001,"accounts":[]}],
             "users":[{"account":"a","password":"pa"},{"account":"b","password":"pb"}]}
            """;

        LauncherProfileStore store = LoadVersion1(json);

        Assert.All(store.Document.Servers, server =>
            Assert.Equal(["a", "b"], server.Accounts.Select(account => account.Account)));
        Assert.Equal("pb", Account(store, "one", "b").Password);
        Assert.Equal("Hero", Account(store, "one", "b").Characters.Single().Name);
        Assert.Equal(["p"], Account(store, "one", "b").Plugins);
        Assert.Empty(Account(store, "two", "b").Characters);
    }

    [Fact]
    public void TheOldFileIsKeptByteForByteAndTheNewOneWrittenOnce()
    {
        LauncherProfileStore store = LoadVersion1();

        Assert.Equal(Version1File, File.ReadAllText(store.Version1BackupPath));
        using (JsonDocument written = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            Assert.Equal(2, written.RootElement.GetProperty("version").GetInt32());
            Assert.False(written.RootElement.TryGetProperty("users", out _));
        }

        LauncherProfileStore reloaded = NewStore();
        reloaded.Load();
        Assert.Null(reloaded.MigrationNotice);
        Assert.Equal(JsonSerializer.Serialize(store.Document), JsonSerializer.Serialize(reloaded.Document));
        Assert.Equal(Version1File, File.ReadAllText(store.Version1BackupPath));
        Assert.Equal(Path.Combine(_root, "launcher-profiles.v1-backup.json"), store.Version1BackupPath);
    }

    [Fact]
    public void TheOlderLaunchersFileIsNeverWrittenSoItKeepsWorking()
    {
        LauncherProfileStore store = LoadVersion1();
        store.ExecuteTransaction(() => store.AddServer("Added later", "h", 9000));

        Assert.Equal(Version1File, File.ReadAllText(OlderFilePath));
        Assert.Contains("Added later", File.ReadAllText(FilePath));
    }

    [Fact]
    public void OnceWrittenTheNewFileIsReadAndTheOlderFileIgnored()
    {
        LoadVersion1();
        // An older launcher run afterwards goes on using its own file.
        File.WriteAllText(OlderFilePath, """{"version":1,"servers":[]}""");

        LauncherProfileStore store = NewStore();
        store.Load();

        Assert.Equal(["sawato", "coldeve"], store.Document.Servers.Select(server => server.Name));
        Assert.Null(store.MigrationNotice);
    }

    [Fact]
    public void WithNeitherFileTheProfilesStartEmptyAndNothingIsWritten()
    {
        LauncherProfileStore store = NewStore();

        Assert.False(store.Load());
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public void AnExistingBackupIsNeverOverwritten()
    {
        LauncherProfileStore store = NewStore();
        File.WriteAllText(store.Version1BackupPath, "earlier backup");

        LoadVersion1();

        Assert.Equal("earlier backup", File.ReadAllText(store.Version1BackupPath));
    }

    [Fact]
    public void AnInvalidOldFileIsRefusedAndLeftAlone()
    {
        string json = """{"version":1,"servers":[{"name":"a","host":"h","port":0,"accounts":[]}]}""";
        File.WriteAllText(OlderFilePath, json);
        LauncherProfileStore store = NewStore();

        Assert.Throws<LauncherProfileException>(() => store.Load());
        Assert.Equal(json, File.ReadAllText(OlderFilePath));
        Assert.False(File.Exists(store.Version1BackupPath));
        Assert.False(File.Exists(FilePath));
    }
}
