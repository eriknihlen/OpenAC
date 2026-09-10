using System.Text.Json;
using AcDream.Launcher.Core;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class LauncherProfileHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "acdream-launcher-profile-hardening-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _filePath;

    public LauncherProfileHardeningTests()
    {
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
    public void MultiFieldEditValidatesEverythingBeforeChangingAnything()
    {
        LauncherProfileStore store = CreatePopulatedStore();

        Assert.Throws<LauncherProfileException>(() =>
            store.EditServer(
                "Local ACE",
                newName: "Partially renamed",
                newHost: "changed.example.test",
                newPort: 0));
        ServerProfile server = Assert.Single(store.Document.Servers);
        Assert.Equal("Local ACE", server.Name);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Equal(9000, server.Port);

        Assert.Throws<LauncherProfileException>(() =>
            store.EditCharacter(
                "Local ACE",
                "testaccount",
                "+Acdream",
                newName: "+PartiallyRenamed",
                newId: "not-an-id"));
        CharacterProfile character = Assert.Single(server.Accounts.Single().Characters);
        Assert.Equal("+Acdream", character.Name);
        Assert.Equal("0x5000000A", character.Id);
    }

    [Fact]
    public void TransactionRestoresTheExactDocumentWhenPersistenceFails()
    {
        Directory.CreateDirectory(_filePath);
        var store = new LauncherProfileStore(_filePath);
        store.Load();

        Assert.ThrowsAny<Exception>(() =>
            store.ExecuteTransaction(() =>
                store.AddServer("Should roll back", "host", 9000)));

        Assert.Empty(store.Document.Servers);
        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void TransactionRestoresNestedCredentialAndSettingsOnMutationFailure()
    {
        LauncherProfileStore store = CreatePopulatedStore();
        string before = JsonSerializer.Serialize(store.Document);

        Assert.Throws<InvalidOperationException>(() =>
            store.ExecuteTransaction(() =>
            {
                AccountProfile account = store.Document.Servers.Single().Accounts.Single();
                account.Password = "transient-secret";
                account.Characters.Single().Plugins.Add("Transient.Plugin");
                throw new InvalidOperationException("simulated mutation failure");
            }));

        Assert.Equal(before, JsonSerializer.Serialize(store.Document));
    }

    public static TheoryData<string> InvalidDocuments => new()
    {
        { """{"version":1,"servers":null}""" },
        { """{"version":1,"servers":[null]}""" },
        { """{"version":1,"servers":[{"name":" ","host":"h","port":9000,"accounts":[]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":" ","port":9000,"accounts":[]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":0,"accounts":[]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[]},{"name":"s","host":"h2","port":9001,"accounts":[]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[]},{"account":"a","password":"p2","characters":[]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":null,"characters":[]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":null}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x00000000","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"invalid","plugins":[],"loginCommands":[]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":null,"loginCommands":[]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":["P","P"],"loginCommands":[]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[" "]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]},{"name":"c","id":"0x50000002","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}""" },
        { """{"version":1,"servers":[{"name":"s","host":"h","port":9000,"accounts":[{"account":"a","password":"p","characters":[{"name":"c1","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]},{"name":"c2","id":"0x50000001","launchMode":"gui","plugins":[],"loginCommands":[]}]}]}]}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void LoadRejectsSemanticallyInvalidDocuments(string json)
    {
        File.WriteAllText(_filePath, json);
        var store = new LauncherProfileStore(_filePath);

        Assert.Throws<LauncherProfileException>(() => store.Load());
    }

    [Fact]
    public void FailedLoadDoesNotReplaceAnAlreadyLoadedDocument()
    {
        LauncherProfileStore store = CreatePopulatedStore();
        File.WriteAllText(_filePath, """{"version":1,"servers":null}""");

        Assert.Throws<LauncherProfileException>(() => store.Load());

        Assert.Equal("Local ACE", Assert.Single(store.Document.Servers).Name);
    }

    [Fact]
    [Trait("Lane", "Unix")]
    public void UnixLoadNormalizesAnExistingCredentialFileTo0600BeforeReading()
    {
        if (!LauncherOperatingSystem.IsUnix)
        {
            throw new PlatformNotSupportedException("Lane=Unix requires a native Unix host.");
        }

        File.WriteAllText(_filePath, """{"version":1,"servers":[]}""");
        File.SetUnixFileMode(
            _filePath,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead);

        var store = new LauncherProfileStore(_filePath);
        Assert.True(store.Load());

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_filePath));
    }

    private LauncherProfileStore CreatePopulatedStore()
    {
        var store = new LauncherProfileStore(_filePath);
        store.Load();
        store.AddServer("Local ACE", "127.0.0.1", 9000);
        store.AddAccount("Local ACE", "testaccount", "password");
        store.AddCharacter(
            "Local ACE",
            "testaccount",
            "+Acdream",
            "0x5000000A",
            LaunchMode.Headless,
            ["ExamplePlugin"],
            ["/vt start"]);
        store.Save();
        return store;
    }
}
