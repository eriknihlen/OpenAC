using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class LauncherProfileTextTests
{
    [Fact]
    public void UsersSurviveNoServersAndPopulateEveryServerAddedLater()
    {
        var document = new LauncherProfileDocument();
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, "Alice | one\nBob | two");
        Assert.Equal(2, document.Users!.Count);
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, "One | localhost | 9000\nTwo | localhost | 9001");
        Assert.All(document.Servers, server => Assert.Equal(new[] { "Alice", "Bob" }, server.Accounts.Select(account => account.Account)));
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, "");
        Assert.Contains("Alice | one", LauncherProfileText.Read(document, LauncherTextEditorKind.Users));
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, "Three | localhost | 9002");
        Assert.Equal(2, document.Servers[0].Accounts.Count);
        Assert.DoesNotContain("Three", LauncherProfileText.Read(document, LauncherTextEditorKind.Users));
    }

    private static LauncherProfileDocument Sample() => new()
    {
        Servers = [new() { Name = "One", Host = "localhost", Port = 9000,
            Accounts = [new() { Account = "User", Password = "first", Characters = [new() { Name = "Hero", Id = "0x50000001", LaunchMode = LaunchMode.Headless, Plugins = ["plugin"], LoginCommands = ["/help"] }] }] },
            new() { Name = "Two", Host = "localhost", Port = 9001, Accounts = [new() { Account = "User", Password = "first" }] }],
    };

    [Fact]
    public void UserRoundTripPreservesSharedCredentialsAndCharacterSettings()
    {
        var document = Sample();
        string text = LauncherProfileText.Read(document, LauncherTextEditorKind.Users);
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, text);
        Assert.Equal("first", document.Servers[0].Accounts[0].Password);
        Assert.Equal("first", document.Servers[1].Accounts[0].Password);
        var character = document.Servers[0].Accounts[0].Characters[0];
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Equal(["plugin"], character.Plugins);
        Assert.Equal(["/help"], character.LoginCommands);
    }

    [Theory]
    [InlineData("New | secret | One, Unknown")]
    [InlineData("New | secret | One, One")]
    [InlineData("New | \"secret | One")]
    [InlineData("New")]
    public void InvalidUsersDoNotPartiallyReplaceProfiles(string text)
    {
        var document = Sample();
        string before = LauncherProfileText.Read(document, LauncherTextEditorKind.Users);
        var error = Assert.Throws<LauncherProfileException>(() => LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, text));
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(before, LauncherProfileText.Read(document, LauncherTextEditorKind.Users));
    }

    [Fact]
    public void ServerEndpointEditRetainsAccounts()
    {
        var document = Sample();
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, "One | new.example | 9010");
        Assert.Single(document.Servers);
        Assert.Equal("new.example", document.Servers[0].Host);
        Assert.Equal("Hero", document.Servers[0].Accounts[0].Characters[0].Name);
    }

    [Fact]
    public void FailedDiskWriteRollsBackTheWholeUserEdit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "launcher-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LauncherProfileStore(directory);
            store.AddServer("One", "localhost", 9000);
            store.AddAccount("One", "Original", "original-password");
            var error = Record.Exception(() => store.ExecuteTransaction(() => LauncherProfileText.Apply(store.Document,
                LauncherTextEditorKind.Users, "New | new-password")));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("Original", store.Document.Servers[0].Accounts[0].Account);
            Assert.Equal("original-password", store.Document.Servers[0].Accounts[0].Password);
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public void BareUsernameAndEmptyPasswordUsesAllServers()
    {
        var document = Sample();
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, "\r\nNew | \r\n");
        Assert.All(document.Servers, server =>
        {
            Assert.Equal("New", Assert.Single(server.Accounts).Account);
            Assert.Equal("", server.Accounts[0].Password);
        });
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, " \r\n ");
        Assert.All(document.Servers, server => Assert.Empty(server.Accounts));
    }

    [Fact]
    public void QuotedSeparatorsWhitespaceAndEscapesRoundTrip()
    {
        var document = Sample();
        document.Servers[0].Name = "Server, One | Test";
        document.Servers[0].Accounts[0].Password = " secret|,\\\"\r\n ";
        document.Servers[0].Accounts[0].Account = " User | One ";
        string users = LauncherProfileText.Read(document, LauncherTextEditorKind.Users);
        string servers = LauncherProfileText.Read(document, LauncherTextEditorKind.Servers);
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, servers);
        LauncherProfileText.Apply(document, LauncherTextEditorKind.Users, users);
        Assert.Equal("Server, One | Test", document.Servers[0].Name);
        Assert.Equal(" User | One ", document.Servers[0].Accounts[0].Account);
        Assert.Equal(" secret|,\\\"\r\n ", document.Servers[0].Accounts[0].Password);
    }

    [Fact]
    public void CommandsChangeOnlyCommandsAndValidateAllBeforeApplying()
    {
        var document = Sample();
        LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands,
            "[{\"server\":\"One\",\"account\":\"User\",\"character\":\"Hero\",\"commands\":[\"/one\",\"/two\"]}]");
        var character = document.Servers[0].Accounts[0].Characters[0];
        Assert.Equal(["/one", "/two"], character.LoginCommands);
        Assert.Equal(LaunchMode.Headless, character.LaunchMode);
        Assert.Throws<LauncherProfileException>(() => LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands,
            "[{\"server\":\"One\",\"account\":\"User\",\"character\":\"Unknown\",\"commands\":[]}]"));
        Assert.Equal(["/one", "/two"], character.LoginCommands);
    }
}
