using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed class ProfileFieldsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acdream-tests", Guid.NewGuid().ToString("N"));
    private readonly LauncherProfileStore _store;
    private readonly LauncherOrchestrator _core;
    private readonly ProfileTextEditorViewModel _editor;

    public ProfileFieldsEditorTests()
    {
        var paths = new ApplicationPathSet(Path.Combine(_root, "config"), Path.Combine(_root, "data"), Path.Combine(_root, "cache"), null);
        _store = LauncherProfileStore.ForApplicationPaths(paths);
        _core = new LauncherOrchestrator(_store, paths, new LauncherExecutableSet("unused-client", "unused-headless"));
        _core.LoadProfiles();
        _editor = new ProfileTextEditorViewModel(_core);
    }

    [Fact]
    public void AccountFieldsRoundTripLiteralSeparatorsAndPasswordWhitespaceAcrossServers()
    {
        const string password = "  p|a,ss\"\\word  ";
        _editor.Open(LauncherTextEditorKind.Users);
        _editor.Rows[0].Name = "Account | one";
        _editor.Rows[0].Value = password;
        Assert.Equal('●', _editor.Rows[0].PasswordChar);
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        _editor.Open(LauncherTextEditorKind.Servers);
        _editor.Rows[0].Name = "Server | one";
        _editor.Rows[0].Value = "localhost:9001";
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        _store.Load();
        var server = Assert.Single(_store.Document.Servers);
        Assert.Equal(9001, server.Port);
        Assert.Equal(password, Assert.Single(server.Accounts).Password);
        _editor.Open(LauncherTextEditorKind.Users);
        Assert.Equal("Account | one", _editor.Rows[0].Name);
        Assert.Equal(password, _editor.Rows[0].Value);
        _editor.Rows[0].Value = "discarded change";
        _editor.CancelCommand.Execute(null);
        _editor.Open(LauncherTextEditorKind.Users);
        Assert.Equal(password, _editor.Rows[0].Value);
    }

    [Theory]
    [InlineData("example.com:9000", "example.com", 9000)]
    [InlineData(" 127.0.0.1:1 ", "127.0.0.1", 1)]
    [InlineData("[::1]:65535", "::1", 65535)]
    public void ServerAddressPersistsAndReopensWithPort(string address, string host, int port)
    {
        _editor.Open(LauncherTextEditorKind.Servers);
        _editor.Rows[0].Name = "Test server";
        _editor.Rows[0].Value = address;
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        _store.Load();
        var server = Assert.Single(_store.Document.Servers);
        Assert.Equal(host, server.Host);
        Assert.Equal(port, server.Port);
        _editor.Open(LauncherTextEditorKind.Servers);
        Assert.Equal(address.Trim(), _editor.Rows[0].Value);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("localhost:abc")]
    [InlineData("https://example.com:9000")]
    [InlineData("example.com/path:9000")]
    [InlineData("::1:9000")]
    [InlineData("[example.com]:9000")]
    public void InvalidEndpointKeepsEditorOpenAndLeavesSavedProfileUnchanged(string address)
    {
        _core.AddServer("Existing", "localhost", 9000);
        string original = File.ReadAllText(_store.FilePath);
        _editor.Open(LauncherTextEditorKind.Servers);
        _editor.Rows[0].Value = address;
        _editor.SaveCommand.Execute(null);
        Assert.True(_editor.IsOpen);
        Assert.NotNull(_editor.Error);
        Assert.Equal(original, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void ExistingCharactersSurviveEndpointEditsAndConcurrentChangesAreRejected()
    {
        _core.AddServer("Existing", "localhost", 9000);
        _core.AddAccount("Existing", "Player", "secret");
        _core.AddCharacter("Existing", "Player", "Character", "0x50000001");
        _editor.Open(LauncherTextEditorKind.Servers);
        _editor.Rows[0].Value = "game.example.com:9001";
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        Assert.Equal("Character", Assert.Single(_store.Document.Servers[0].Accounts[0].Characters).Name);
        _editor.Open(LauncherTextEditorKind.Servers);
        _core.AddServer("Added elsewhere", "localhost", 9002);
        _editor.Rows[0].Value = "other.example.com:9003";
        _editor.SaveCommand.Execute(null);
        Assert.True(_editor.IsOpen);
        Assert.Contains("changed", _editor.Error);
        Assert.Equal(2, _store.Document.Servers.Count);
        Assert.Equal("game.example.com", _store.Document.Servers[0].Host);
    }

    [Fact]
    public void RemovingAccountRowsPersistsAndClearsDraftCredentials()
    {
        _editor.Open(LauncherTextEditorKind.Users);
        _editor.Rows[0].Name = "Player";
        _editor.Rows[0].Value = "secret";
        _editor.SaveCommand.Execute(null);
        _editor.Open(LauncherTextEditorKind.Users);
        var row = _editor.Rows[0];
        row.RemoveCommand.Execute(null);
        Assert.Empty(row.Value);
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        _store.Load();
        Assert.Empty(_store.Document.Users!);
    }

    [Fact]
    public void ConflictingLegacyAccountsCanBeOpenedAndResolved()
    {
        _store.Document.Users = null;
        _core.AddServer("One", "localhost", 9000);
        _core.AddServer("Two", "localhost", 9001);
        _core.AddAccount("One", "Player", "first");
        _core.AddAccount("Two", "Player", "second");
        _editor.Open(LauncherTextEditorKind.Users);
        Assert.Equal(2, _editor.Rows.Count);
        _editor.Rows[1].RemoveCommand.Execute(null);
        _editor.SaveCommand.Execute(null);
        Assert.False(_editor.IsOpen);
        Assert.All(_store.Document.Servers, server => Assert.Equal("first", Assert.Single(server.Accounts).Password));
    }

    [Fact]
    public void SavingExistingNamesPreservesWhitespaceAndSavedCharacters()
    {
        _core.AddServer(" server ", "localhost", 9000);
        _core.AddAccount(" server ", " account ", "secret");
        _core.AddCharacter(" server ", " account ", "Character", "0x50000001");
        foreach (var kind in new[] { LauncherTextEditorKind.Users, LauncherTextEditorKind.Servers })
        {
            _editor.Open(kind);
            _editor.SaveCommand.Execute(null);
            Assert.False(_editor.IsOpen);
        }
        _store.Load();
        var server = Assert.Single(_store.Document.Servers);
        Assert.Equal(" server ", server.Name);
        var account = Assert.Single(server.Accounts);
        Assert.Equal(" account ", account.Account);
        Assert.Equal("Character", Assert.Single(account.Characters).Name);
    }

    public void Dispose()
    {
        _editor.Close();
        _core.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
