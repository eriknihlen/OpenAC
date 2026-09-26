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
        var paths = new ApplicationPathSet(Path.Combine(_root, "config"), Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _store = LauncherProfileStore.ForApplicationPaths(paths);
        _core = new LauncherOrchestrator(_store, paths, new LauncherExecutableSet("unused-client", "unused-headless"));
        _core.LoadProfiles();
        _editor = new ProfileTextEditorViewModel(_core);
    }

    private sealed class FakeClipboard : IProfileEditorClipboard
    {
        public string? Text { get; set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    [Fact]
    public void TheAccountsEditorHidesPasswordsAndIsReadOnlyUntilShown()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "notan3", "p,a\"ss");
        _editor.Open(LauncherTextEditorKind.Accounts);

        Assert.True(_editor.IsPasswordHidden);
        Assert.Equal($"#Coldeve{Environment.NewLine}Name=notan3,Password={ProfileTextEditorViewModel.HiddenPassword}{Environment.NewLine}",
            _editor.DisplayedText);
        _editor.DisplayedText = "#Coldeve\nName=someone,Password=typed while hidden";
        Assert.Contains("notan3", _editor.Text);

        _editor.ShowPasswords = true;
        Assert.Equal(_editor.Text, _editor.DisplayedText);
        _editor.DisplayedText = "#Coldeve\nName=notan3,Password=changed,Profiles=Bots\n";
        _editor.SaveCommand.Execute(null);

        Assert.False(_editor.IsOpen);
        _store.Load();
        AccountProfile account = Assert.Single(_store.Document.Servers[0].Accounts);
        Assert.Equal("changed", account.Password);
        Assert.Equal(["Bots"], account.Profiles);
    }

    [Fact]
    public void RemovingOrRenamingAnAccountWithSettingsAsksFirstAndSavesOnTheSecondSave()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "notan3", "secret");
        _core.AddAccount("Coldeve", "empty", "secret");
        _core.AddCharacter("Coldeve", "notan3", "Festivus", "0x5005FBB5");
        _core.UpdateAccountPlugins("Coldeve", "notan3", ["a.one", "b.two"]);
        string before = File.ReadAllText(_store.FilePath);
        _editor.Open(LauncherTextEditorKind.Accounts);
        _editor.ShowPasswords = true;

        _editor.DisplayedText = "#Coldeve\nName=notan5,Password=secret\n";
        _editor.SaveCommand.Execute(null);

        Assert.True(_editor.IsOpen);
        Assert.Equal(before, File.ReadAllText(_store.FilePath));
        Assert.Contains("notan3 on Coldeve (1 character, 2 plugins)", _editor.RemovalWarning);
        Assert.DoesNotContain("empty", _editor.RemovalWarning);
        Assert.Equal("Remove and save", _editor.SaveText);

        _editor.SaveCommand.Execute(null);

        Assert.False(_editor.IsOpen);
        Assert.Equal("notan5", Assert.Single(_store.Document.Servers[0].Accounts).Account);
    }

    [Fact]
    public void EditingTheTextAgainAsksAgain()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "notan3", "secret");
        _core.AddCharacter("Coldeve", "notan3", "Festivus", "0x5005FBB5");
        _editor.Open(LauncherTextEditorKind.Accounts);
        _editor.ShowPasswords = true;
        _editor.DisplayedText = "#Coldeve\n";
        _editor.SaveCommand.Execute(null);

        _editor.DisplayedText = "#Coldeve\nName=other\n";
        Assert.Null(_editor.RemovalWarning);
        _editor.SaveCommand.Execute(null);

        Assert.True(_editor.IsOpen);
        Assert.Equal("notan3", Assert.Single(_store.Document.Servers[0].Accounts).Account);
    }

    [Fact]
    public void RemovingAnAccountWithNothingSavedOnItSavesAtOnce()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "empty", "secret");
        _editor.Open(LauncherTextEditorKind.Accounts);
        _editor.ShowPasswords = true;

        _editor.DisplayedText = "#Coldeve\n";
        _editor.SaveCommand.Execute(null);

        Assert.False(_editor.IsOpen);
        Assert.Empty(_store.Document.Servers[0].Accounts);
    }

    [Fact]
    public void AWrongLineKeepsTheEditorOpenWithItsNumberAndSavesNothing()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        string before = File.ReadAllText(_store.FilePath);
        _editor.Open(LauncherTextEditorKind.LogonCommands);

        _editor.Text = "#Coldeve\n##nobody\n/vt start";
        _editor.SaveCommand.Execute(null);

        Assert.True(_editor.IsOpen);
        Assert.Equal("Line 2: server 'Coldeve' has no account 'nobody'. Add it in Edit accounts first.", _editor.Error);
        Assert.Equal(before, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public async Task CopyAllCopiesTheWholeTextAndPasteAllReplacesIt()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "notan3", "secret");
        var clipboard = new FakeClipboard();
        _editor.UseClipboard(clipboard);
        _editor.Open(LauncherTextEditorKind.Accounts);

        await _editor.CopyAllCommand.ExecuteAsync();
        Assert.Equal(_editor.Text, clipboard.Text);
        Assert.Contains("Password=secret", clipboard.Text);

        clipboard.Text = "#Coldeve\nName=pasted,Password=x";
        await _editor.PasteAllCommand.ExecuteAsync();
        Assert.Equal("#Coldeve\nName=pasted,Password=x", _editor.Text);
        _editor.SaveCommand.Execute(null);

        Assert.Equal("pasted", Assert.Single(_store.Document.Servers[0].Accounts).Account);
    }

    [Fact]
    public void LogonCommandsSaveToTheAccountAndTheSummaryCountsWhatIsTyped()
    {
        _core.AddServer("Coldeve", "play.coldeve.ac", 9000);
        _core.AddAccount("Coldeve", "notan3", "secret");
        _core.AddAccount("Coldeve", "notan", "secret");
        _editor.Open(LauncherTextEditorKind.LogonCommands);
        Assert.Equal("1 server, 2 accounts · an account with no lines runs no commands", _editor.Summary);

        _editor.Text = "#Coldeve\n##notan3\n/vt meta load bore\n/vt start\n##notan\n";
        _editor.SaveCommand.Execute(null);

        Assert.False(_editor.IsOpen);
        _store.Load();
        Assert.Equal(["/vt meta load bore", "/vt start"], _store.Document.Servers[0].Accounts[0].LoginCommands);
        Assert.Empty(_store.Document.Servers[0].Accounts[1].LoginCommands);
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
    public void SavingUnchangedEditorsPreservesWhitespaceAndSavedCharacters()
    {
        _core.AddServer(" server ", "localhost", 9000);
        _core.AddAccount(" server ", " account ", "secret");
        _core.AddCharacter(" server ", " account ", "Character", "0x50000001");
        foreach (var kind in new[] { LauncherTextEditorKind.Accounts, LauncherTextEditorKind.LogonCommands, LauncherTextEditorKind.Servers })
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
