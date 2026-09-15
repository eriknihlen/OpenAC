using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed class PluginInstallDialogViewModelTests
{
    private static readonly PluginCharacterOption[] Characters =
    [
        new("Local ACE", "acct-a", "+First", "+First (acct-a@Local ACE)"),
        new("Local ACE", "acct-b", "+Second", "+Second (acct-b@Local ACE)"),
    ];

    [Fact]
    public async Task DefaultChoiceIsNoneAndConfirmWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        Assert.True(dialog.EnableNone);
        Assert.False(dialog.EnableAll);
        Assert.False(dialog.EnableChoose);

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(enableCalls);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task ChoosingAllEnablesEveryOfferedCharacter()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableAll = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        (string Id, IReadOnlyList<PluginCharacterOption> Chosen) call = Assert.Single(enableCalls);
        Assert.Equal("edwards.hello", call.Id);
        Assert.Equal(Characters, call.Chosen);
    }

    [Fact]
    public async Task ChoosingSpecificCharactersEnablesOnlyThoseChecked()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableChoose = true;
        dialog.Characters[1].IsChecked = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        (string Id, IReadOnlyList<PluginCharacterOption> Chosen) call = Assert.Single(enableCalls);
        Assert.Equal(Characters[1], Assert.Single(call.Chosen));
    }

    [Fact]
    public async Task ChoosingWithNothingCheckedWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableChoose = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(enableCalls);
    }

    [Fact]
    public async Task EnableFailureAfterInstallStillClosesTheDialogAndNeverReinstalls()
    {
        var dialog = new PluginInstallDialogViewModel();
        int installCount = 0;
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ =>
            {
                installCount++;
                return Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false));
            },
            (_, _) => throw new InvalidOperationException("could not save the character profile"));

        dialog.EnableAll = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.False(dialog.IsOpen);
        Assert.Equal(1, installCount);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, installCount);
    }

    [Fact]
    public void ConfirmIsEnabledAssoonAsAListedInstallOpensWithNoTick()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { });

        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.",
            dialog.WarningText);
    }

    [Fact]
    public void ConfirmIsEnabledAssoonAsAnUnlistedInstallOpensWithNoTick()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "someone-else/some-plugin",
            "someone.plugin",
            "Some Plugin",
            isListed: false,
            isUpdate: false,
            [],
            _ => Task.FromResult(new PluginInstallResult("someone.plugin", "0.1.0", WasUpdate: false)),
            (_, _) => { });

        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.\n"
            + "This plugin is not on the OpenAC plugin list.",
            dialog.WarningText);
    }

    [Fact]
    public void UpdateOpensWithNoEnableChoiceOffered()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { });

        Assert.True(dialog.IsUpdate);
        Assert.False(dialog.ShowEnableChoice);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConfirmingAnUpdateWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            Characters,
            _ => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.False(dialog.IsOpen);
        Assert.Empty(enableCalls);
    }

    [Fact]
    public async Task CancelClosesWithoutInstallingOrEnabling()
    {
        var dialog = new PluginInstallDialogViewModel();
        bool installCalled = false;
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            Characters,
            _ =>
            {
                installCalled = true;
                return Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false));
            },
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.CancelCommand.Execute(null);

        Assert.False(dialog.IsOpen);
        Assert.False(installCalled);
        Assert.Empty(enableCalls);
        await Task.CompletedTask;
    }
}
