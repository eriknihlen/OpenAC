using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.Core.Combat;
using AcDream.Runtime;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Input;

public sealed class GameplayInputActionRouterTests
{
    [Theory]
    [InlineData("pointer", "pointer")]
    [InlineData("combat", "pointer,combat")]
    [InlineData("retained", "pointer,combat,retained")]
    [InlineData("character-option", "pointer,combat,retained,character-option")]
    [InlineData("selection", "pointer,combat,retained,character-option,selection")]
    [InlineData("movement", "pointer,combat,retained,character-option,selection,movement")]
    [InlineData("command", "pointer,combat,retained,character-option,selection,movement,command")]
    public void Press_PreservesFrozenPriorityAndStopsAtConsumer(
        string consumeAt,
        string expectedCsv)
    {
        var harness = new Harness(consumeAt);
        harness.Router.Attach();

        harness.Actions.Raise(
            InputAction.AcdreamToggleDebugPanel,
            ActivationType.Press);

        Assert.Equal(expectedCsv.Split(','), harness.Targets.Calls);
    }

    [Fact]
    public void Scroll_IsBetweenPointerAndCombat_AndOnlyPressMovesIt()
    {
        var harness = new Harness();
        harness.Router.Attach();

        harness.Actions.Raise(InputAction.ScrollUp, ActivationType.Release);
        Assert.Equal(["pointer"], harness.Targets.Calls);

        harness.Targets.Calls.Clear();
        harness.Actions.Raise(InputAction.ScrollDown, ActivationType.Press);
        Assert.Equal(["pointer", "scroll:ScrollDown"], harness.Targets.Calls);
    }

    [Fact]
    public void Release_ReachesCombatThenStopsAtPressGate()
    {
        var harness = new Harness();
        harness.Router.Attach();

        harness.Actions.Raise(
            InputAction.CombatHighAttack,
            ActivationType.Release);

        Assert.Equal(["pointer", "combat"], harness.Targets.Calls);
    }

    [Fact]
    public void DoubleClick_PassesGateToRetainedSelectionMovementAndCommands()
    {
        var harness = new Harness();
        harness.Router.Attach();

        harness.Actions.Raise(
            InputAction.SelectDblLeft,
            ActivationType.DoubleClick);

        Assert.Equal(
            ["pointer", "combat", "retained", "character-option", "selection", "movement", "command"],
            harness.Targets.Calls);
    }

    [Fact]
    public void Click_PassesGateToSelection()
    {
        var harness = new Harness("selection");
        harness.Router.Attach();

        harness.Actions.Raise(
            InputAction.SelectRight,
            ActivationType.Click);

        Assert.Equal(
            ["pointer", "combat", "retained", "character-option", "selection"],
            harness.Targets.Calls);
    }

    [Fact]
    public void Attach_SeedsAndTracksCombatScopes()
    {
        var harness = new Harness();
        harness.Combat.CurrentModeValue = CombatMode.Missile;
        harness.Router.Attach();

        harness.Combat.Raise(CombatMode.Magic);
        harness.Combat.Raise(CombatMode.NonCombat);

        Assert.Equal(
            [InputScope.MissileCombat, InputScope.MagicCombat, null],
            harness.Actions.Scopes);
    }

    [Theory]
    [InlineData(InputAction.Ready, RuntimeMovementCommand.Ready)]
    [InlineData(InputAction.Sitting, RuntimeMovementCommand.Sit)]
    [InlineData(InputAction.Crouch, RuntimeMovementCommand.Crouch)]
    [InlineData(InputAction.Sleeping, RuntimeMovementCommand.Sleep)]
    public void RetailPostureKeys_MapToCanonicalRuntimeCommands(
        InputAction action,
        RuntimeMovementCommand expected)
    {
        Assert.Equal(
            expected,
            RuntimeGameplayInputPriorityTargets.ResolvePressedMovementCommand(action));
    }

    [Fact]
    public void EscapeMovementRung_PreservesRetailPriority()
    {
        var charging = new AcDream.Runtime.Gameplay.JumpChargeSnapshot(
            IsCharging: true,
            Power: 0.5f);
        var repeat = new RuntimeCombatAttackSnapshot(
            0,
            AttackHeight.Medium,
            0f,
            0f,
            false,
            false,
            0f,
            RepeatAttackInProgress: true);

        Assert.Equal(
            RuntimeMovementCommand.FinishJump,
            RuntimeGameplayInputPriorityTargets.ResolveEscapeMovementCommand(
                InputAction.EscapeKey,
                isStandingStill: false,
                charging,
                repeat));

        Assert.Equal(
            RuntimeMovementCommand.StopCompletely,
            RuntimeGameplayInputPriorityTargets.ResolveEscapeMovementCommand(
                InputAction.EscapeKey,
                isStandingStill: false,
                jumpCharge: default,
                repeat));

        Assert.Null(
            RuntimeGameplayInputPriorityTargets.ResolveEscapeMovementCommand(
                InputAction.EscapeKey,
                isStandingStill: true,
                jumpCharge: default,
                repeat with { RepeatAttackInProgress = false }));
    }

    [Theory]
    [InlineData(0, "remove-actions")]
    [InlineData(1, "remove-combat,remove-actions")]
    public void AttachFailureAfterSideEffect_RollsBackExactPrefixInReverse(
        int failingEdge,
        string expectedRemovalCsv)
    {
        var calls = new List<string>();
        var actions = new FakeActionSurface(calls)
        {
            ThrowAfterAdd = failingEdge == 0,
        };
        var combat = new FakeCombatSurface(calls)
        {
            ThrowAfterAdd = failingEdge == 1,
        };
        var router = Create(actions, combat, new FakeTargets());

        Assert.Throws<InvalidOperationException>(router.Attach);

        Assert.Equal(expectedRemovalCsv.Split(','), calls.Where(
            call => call.StartsWith("remove", StringComparison.Ordinal)));
        Assert.True(router.IsDisposalComplete);
    }

    [Fact]
    public void ReentrantDeliveryDuringAdd_CannotEnterPartialRouter()
    {
        var calls = new List<string>();
        var actions = new FakeActionSurface(calls)
        {
            RaiseDuringAdd = true,
        };
        var targets = new FakeTargets();
        var router = Create(actions, new FakeCombatSurface(calls), targets);

        router.Attach();

        Assert.Empty(targets.Calls);
        actions.Raise(InputAction.AcdreamToggleDebugPanel, ActivationType.Press);
        Assert.NotEmpty(targets.Calls);
    }

    [Fact]
    public void AttachAndRollbackFailure_RetainsCleanupForLaterDispose()
    {
        var calls = new List<string>();
        var actions = new FakeActionSurface(calls)
        {
            ThrowAfterAdd = true,
            RemoveFailures = int.MaxValue,
        };
        var router = Create(
            actions,
            new FakeCombatSurface(calls),
            new FakeTargets());

        Assert.Throws<AggregateException>(router.Attach);
        Assert.False(router.IsDisposalComplete);

        actions.RemoveFailures = 0;
        router.Dispose();

        Assert.True(router.IsDisposalComplete);
        Assert.Null(actions.Callback);
    }

    [Fact]
    public void Dispose_RetriesOnlyFailedRemovalAndNeverReplaysSuccess()
    {
        var calls = new List<string>();
        var actions = new FakeActionSurface(calls);
        var combat = new FakeCombatSurface(calls)
        {
            RemoveFailures = int.MaxValue,
        };
        var router = Create(actions, combat, new FakeTargets());
        router.Attach();

        Assert.Throws<AggregateException>(router.Dispose);
        Assert.False(router.IsDisposalComplete);

        combat.RemoveFailures = 0;
        router.Dispose();

        Assert.True(router.IsDisposalComplete);
        Assert.Equal(4, calls.Count(call => call == "remove-combat"));
        Assert.Equal(1, calls.Count(call => call == "remove-actions"));
    }

    [Fact]
    public void Deactivate_MakesCopiedCallbacksSilentBeforePhysicalDetach()
    {
        var harness = new Harness();
        harness.Router.Attach();
        Action<InputAction, ActivationType> copiedAction =
            harness.Actions.Callback!;
        Action<CombatMode> copiedCombat = harness.Combat.Callback!;

        harness.Router.Deactivate();
        copiedAction(InputAction.AcdreamToggleDebugPanel, ActivationType.Press);
        copiedCombat(CombatMode.Melee);

        Assert.Empty(harness.Targets.Calls);
        Assert.Equal([null], harness.Actions.Scopes);
    }

    [Fact]
    public void DisposeBeforeAttach_IsTerminalAndCannotResurrectCallbacks()
    {
        var harness = new Harness();

        harness.Router.Dispose();

        Assert.True(harness.Router.IsDisposalComplete);
        Assert.Throws<ObjectDisposedException>(harness.Router.Attach);
        Assert.Null(harness.Actions.Callback);
        Assert.Null(harness.Combat.Callback);
    }

    private static GameplayInputActionRouter Create(
        FakeActionSurface actions,
        FakeCombatSurface combat,
        FakeTargets targets) =>
        new(actions, combat, targets, new HostQuiescenceGate());

    private sealed class Harness
    {
        public Harness(string? consumeAt = null)
        {
            var calls = new List<string>();
            Actions = new FakeActionSurface(calls);
            Combat = new FakeCombatSurface(calls);
            Targets = new FakeTargets(consumeAt);
            Router = Create(Actions, Combat, Targets);
        }

        public FakeActionSurface Actions { get; }
        public FakeCombatSurface Combat { get; }
        public FakeTargets Targets { get; }
        public GameplayInputActionRouter Router { get; }
    }

    private sealed class FakeActionSurface(List<string> calls)
        : IGameplayInputActionSurface
    {
        public Action<InputAction, ActivationType>? Callback { get; private set; }
        public bool ThrowAfterAdd { get; set; }
        public bool RaiseDuringAdd { get; set; }
        public int RemoveFailures { get; set; }
        public List<InputScope?> Scopes { get; } = [];

        public void AddFired(Action<InputAction, ActivationType> callback)
        {
            calls.Add("add-actions");
            Callback = callback;
            if (RaiseDuringAdd)
            {
                callback(
                    InputAction.AcdreamToggleDebugPanel,
                    ActivationType.Press);
            }
            if (ThrowAfterAdd)
                throw new InvalidOperationException("actions add");
        }

        public void RemoveFired(Action<InputAction, ActivationType> callback)
        {
            calls.Add("remove-actions");
            if (RemoveFailures-- > 0)
                throw new InvalidOperationException("actions remove");
            if (ReferenceEquals(Callback, callback))
                Callback = null;
        }

        public void SetCombatScope(InputScope? scope) => Scopes.Add(scope);

        public void SetCameraAlternateScope(bool active) =>
            calls.Add($"camera-scope:{active}");

        public void Raise(InputAction action, ActivationType activation) =>
            Callback?.Invoke(action, activation);
    }

    private sealed class FakeCombatSurface(List<string> calls)
        : ICombatModeEventSurface
    {
        public CombatMode CurrentModeValue { get; set; } = CombatMode.NonCombat;
        public CombatMode CurrentMode => CurrentModeValue;
        public Action<CombatMode>? Callback { get; private set; }
        public bool ThrowAfterAdd { get; set; }
        public int RemoveFailures { get; set; }

        public void AddChanged(Action<CombatMode> callback)
        {
            calls.Add("add-combat");
            Callback = callback;
            if (ThrowAfterAdd)
                throw new InvalidOperationException("combat add");
        }

        public void RemoveChanged(Action<CombatMode> callback)
        {
            calls.Add("remove-combat");
            if (RemoveFailures-- > 0)
                throw new InvalidOperationException("combat remove");
            if (ReferenceEquals(Callback, callback))
                Callback = null;
        }

        public void Raise(CombatMode mode) => Callback?.Invoke(mode);
    }

    private sealed class FakeTargets(string? consumeAt = null)
        : IGameplayInputPriorityTargets
    {
        public List<string> Calls { get; } = [];

        public bool HandlePointerAction(InputAction action, ActivationType activation) =>
            Record("pointer");

        public void HandleScroll(InputAction action) => Calls.Add($"scroll:{action}");

        public bool HandleCombatAction(InputAction action, ActivationType activation) =>
            Record("combat");

        public bool HandleRetainedUiAction(InputAction action) =>
            Record("retained");

        public bool HandleCharacterOptionAction(InputAction action) =>
            Record("character-option");

        public bool HandleSelectionAction(InputAction action) =>
            Record("selection");

        public bool HandlePressedMovementAction(InputAction action) =>
            Record("movement");

        public void HandleCommand(InputAction action) => Calls.Add("command");

        private bool Record(string name)
        {
            Calls.Add(name);
            return consumeAt == name;
        }
    }
}
