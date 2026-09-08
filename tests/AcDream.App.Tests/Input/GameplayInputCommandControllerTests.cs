using AcDream.App.Combat;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Input;

public sealed class GameplayInputCommandControllerTests
{
    [Theory]
    [InlineData(InputAction.ToggleInventoryPanel, "inventory")]
    [InlineData(InputAction.AcdreamToggleFlyMode, "fly-or-chase")]
    [InlineData(InputAction.AcdreamTogglePlayerMode, "player-mode")]
    [InlineData(InputAction.ToggleOptionsPanel, "options")]
    [InlineData(InputAction.CombatToggleCombat, "combat")]
    [InlineData(InputAction.ToggleFloatingChatWindow1, "chat-window-1")]
    [InlineData(InputAction.ToggleFloatingChatWindow2, "chat-window-2")]
    [InlineData(InputAction.ToggleFloatingChatWindow3, "chat-window-3")]
    [InlineData(InputAction.ToggleFloatingChatWindow4, "chat-window-4")]
    [InlineData(InputAction.ToggleChatEntry, "focus-chat")]
    [InlineData(InputAction.EnterChatMode, "focus-chat")]
    [InlineData(InputAction.LOGOUT, "logout")]
    public void RecognizedCommand_RoutesToTypedOwner(
        InputAction action,
        string expected)
    {
        var harness = new Harness();

        bool handled = harness.Controller.Handle(action);

        Assert.True(handled);
        Assert.Equal([expected], harness.Calls);
    }

    [Fact]
    public void RetiredDebugPanelCommand_IsConsumedWithoutClaimingATypedOwner()
    {
        var harness = new Harness();

        bool handled = harness.Controller.Handle(InputAction.AcdreamToggleDebugPanel);

        Assert.True(handled);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public void DiagnosticCommand_PrecedesRemainingCommandSwitch()
    {
        var harness = new Harness();
        harness.Diagnostics.HandledAction = InputAction.AcdreamToggleDebugPanel;

        bool handled = harness.Controller.Handle(
            InputAction.AcdreamToggleDebugPanel);

        Assert.True(handled);
        Assert.Equal(["diagnostic"], harness.Calls);
    }

    [Fact]
    public void UnknownCommand_IsNotClaimed()
    {
        var harness = new Harness();

        bool handled = harness.Controller.Handle(InputAction.MovementForward);

        Assert.False(handled);
        Assert.Empty(harness.Calls);
    }

    [Theory]
    [InlineData(true, "cancel-target")]
    [InlineData(false, "gameplay-options")]
    public void Escape_PreservesRetailTargetThenGameplayOptionsPriority(
        bool targetMode,
        string expected)
    {
        var harness = new Harness
        {
            TargetMode = { IsActive = targetMode },
        };

        bool handled = harness.Controller.Handle(InputAction.EscapeKey);

        Assert.True(handled);
        Assert.Equal([expected], harness.Calls);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Retained = new FakeRetained(Calls);
            Diagnostics = new FakeDiagnostics(Calls);
            Player = new FakePlayerMode(Calls);
            TargetMode = new FakeTargetMode(Calls);
            Combat = new FakeCombat(Calls);
            Runtime = new FakeRuntimeView();
            Controller = new GameplayInputCommandController(
                Retained,
                Diagnostics,
                Player,
                TargetMode,
                Runtime,
                Combat);
        }

        public List<string> Calls { get; } = [];
        public FakeRetained Retained { get; }
        public FakeDiagnostics Diagnostics { get; }
        public FakePlayerMode Player { get; }
        public FakeTargetMode TargetMode { get; }
        public FakeCombat Combat { get; }
        public FakeRuntimeView Runtime { get; }
        public GameplayInputCommandController Controller { get; }
    }

    private sealed class FakeRetained(List<string> calls)
        : IRetainedGameplayWindowCommands
    {
        public void ToggleInventory() => calls.Add("inventory");

        public void ToggleFloatingChatWindow(int windowId) =>
            calls.Add($"chat-window-{windowId}");

        public void ToggleOptionsPanel() => calls.Add("options");

        public void ToggleGameplayOptionsPage() => calls.Add("gameplay-options");

        public void FocusChatEntry() => calls.Add("focus-chat");

        public void LogOutCharacter() => calls.Add("logout");
    }

    private sealed class FakeDiagnostics(List<string> calls)
        : IRuntimeDiagnosticCommands
    {
        public InputAction? HandledAction { get; set; }

        public bool Handle(InputAction action)
        {
            if (action != HandledAction)
                return false;
            calls.Add("diagnostic");
            return true;
        }

        public void CycleTimeOfDay() => calls.Add("time");
        public void CycleWeather() => calls.Add("weather");
        public void ToggleCollisionWireframes() => calls.Add("collision");
    }

    private sealed class FakePlayerMode(List<string> calls)
        : IPlayerModeGameplayCommands
    {
        public void ToggleFlyOrChase() => calls.Add("fly-or-chase");
        public void TogglePlayerMode() => calls.Add("player-mode");
    }

    private sealed class FakeTargetMode(List<string> calls)
        : IItemTargetModeCommands
    {
        public bool IsActive { get; set; }
        public bool IsAnyTargetModeActive => IsActive;
        public void CancelTargetMode() => calls.Add("cancel-target");
    }

    private sealed class FakeCombat(List<string> calls) : IRuntimeCombatCommands
    {
        public RuntimeCommandResult Execute(
            RuntimeGenerationToken expectedGeneration,
            RuntimeCombatCommand command)
        {
            Assert.Equal(RuntimeCombatCommand.ToggleMode, command);
            calls.Add("combat");
            return new RuntimeCommandResult(
                RuntimeCommandStatus.Accepted,
                expectedGeneration);
        }

        public RuntimeCommandResult ExecuteAttack(
            RuntimeGenerationToken expectedGeneration,
            in RuntimeCombatAttackInput command) =>
            new(
                RuntimeCommandStatus.Unsupported,
                expectedGeneration);
    }

    private sealed class FakeRuntimeView : IGameRuntimeView
    {
        public RuntimeGenerationToken Generation => new(7);
        public RuntimeLifecycleSnapshot Lifecycle => throw new NotSupportedException();
        public IGameRuntimeClock Clock => throw new NotSupportedException();
        public IRuntimeEntityView Entities => throw new NotSupportedException();
        public IRuntimeInventoryView Inventory => throw new NotSupportedException();
        public IRuntimeInventoryStateView InventoryState =>
            throw new NotSupportedException();
        public IRuntimeCharacterView Character =>
            throw new NotSupportedException();
        public IRuntimeSocialView Social => throw new NotSupportedException();
        public IRuntimeChatView Chat => throw new NotSupportedException();
        public IRuntimeFellowshipView Fellowship => throw new NotSupportedException();
        public IRuntimeAllegianceView Allegiance => throw new NotSupportedException();
        public IRuntimeActionView Actions => throw new NotSupportedException();
        public IRuntimeMovementView Movement => throw new NotSupportedException();
        public AcDream.Runtime.World.IRuntimeWorldEnvironmentView Environment =>
            throw new NotSupportedException();
        public IRuntimePortalView Portal => throw new NotSupportedException();
        public RuntimeStateCheckpoint CaptureCheckpoint() =>
            throw new NotSupportedException();
    }

}
