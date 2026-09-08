using AcDream.Launcher.Core.Launching;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

/// <summary>
/// LU6. A sessions row answers "who is playing, and are they in yet?" — not
/// "which launch mode started this process".
/// </summary>
public sealed class LauncherSessionRowViewModelTests
{
    [Theory]
    [InlineData(LauncherActivityState.Starting, "Starting")]
    [InlineData(LauncherActivityState.Running, "Starting")]
    [InlineData(LauncherActivityState.Connected, "Character select")]
    [InlineData(LauncherActivityState.InWorld, "In game")]
    [InlineData(LauncherActivityState.Disconnected, "Stopping")]
    [InlineData(LauncherActivityState.Stopping, "Stopping")]
    [InlineData(LauncherActivityState.Exited, "Stopped")]
    [InlineData(LauncherActivityState.Cancelled, "Stopped")]
    [InlineData(LauncherActivityState.Failed, "Failed")]
    public void StateReadsAsPlainEnglish(LauncherActivityState state, string expected)
    {
        var row = new LauncherSessionRowViewModel(
            Snapshot(state: state),
            _ => Task.CompletedTask);

        Assert.Equal(expected, row.State);
    }

    [Theory]
    [InlineData(LaunchMode.Gui)]
    [InlineData(LaunchMode.GuiSelect)]
    [InlineData(LaunchMode.Headless)]
    public void InGameDoesNotDependOnHowTheSessionWasLaunched(LaunchMode mode)
    {
        var row = new LauncherSessionRowViewModel(
            Snapshot(state: LauncherActivityState.InWorld, mode: mode),
            _ => Task.CompletedTask);

        Assert.Equal("In game", row.State);
    }

    [Fact]
    public void ACharacterSelectLaunchSaysSoUntilACharacterIsKnown()
    {
        var row = new LauncherSessionRowViewModel(
            Snapshot(character: null, mode: LaunchMode.GuiSelect),
            _ => Task.CompletedTask);

        Assert.Equal("Character select", row.Character);
        Assert.Equal("testaccount", row.Account);
    }

    [Fact]
    public void AnEnteredCharacterReplacesTheCharacterSelectPlaceholder()
    {
        var row = new LauncherSessionRowViewModel(
            Snapshot(
                character: "+alex",
                mode: LaunchMode.GuiSelect,
                state: LauncherActivityState.InWorld),
            _ => Task.CompletedTask);

        Assert.Equal("+alex", row.Character);
        Assert.Equal("In game", row.State);
    }

    [Fact]
    public void AProbeIsLabelledAsACharacterRefresh()
    {
        var row = new LauncherSessionRowViewModel(
            Snapshot(kind: LauncherActivityKind.Probe, character: null, mode: null),
            _ => Task.CompletedTask);

        Assert.Equal("Character refresh", row.Character);
        Assert.Equal("Reading characters", row.State);
    }

    [Fact]
    public void StopIsOfferedOnlyWhileTheSessionIsActive()
    {
        var running = new LauncherSessionRowViewModel(
            Snapshot(state: LauncherActivityState.InWorld),
            _ => Task.CompletedTask);
        var finished = new LauncherSessionRowViewModel(
            Snapshot(state: LauncherActivityState.Exited),
            _ => Task.CompletedTask);

        Assert.True(running.StopCommand.CanExecute(null));
        Assert.False(finished.StopCommand.CanExecute(null));
    }

    private static LauncherSessionSnapshot Snapshot(
        LauncherActivityState state = LauncherActivityState.Connected,
        LauncherActivityKind kind = LauncherActivityKind.Play,
        string? character = "+Acdream",
        LaunchMode? mode = LaunchMode.Gui,
        string status = "Fixture status.") => new(
            "session-1",
            kind,
            "Local ACE",
            "testaccount",
            character,
            mode,
            state,
            status,
            ExitCode: state == LauncherActivityState.Exited ? 0 : null,
            Error: null,
            CreatedAt: DateTimeOffset.UnixEpoch);
}
