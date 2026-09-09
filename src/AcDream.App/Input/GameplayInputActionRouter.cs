using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.Core.Combat;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal interface IGameplayInputActionSurface
{
    void AddFired(Action<InputAction, ActivationType> callback);

    void RemoveFired(Action<InputAction, ActivationType> callback);

    void SetCombatScope(InputScope? scope);

    void SetCameraAlternateScope(bool active);
}

internal sealed class DispatcherGameplayInputActionSurface(InputDispatcher dispatcher)
    : IGameplayInputActionSurface
{
    private readonly InputDispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public void AddFired(Action<InputAction, ActivationType> callback) =>
        _dispatcher.Fired += callback;

    public void RemoveFired(Action<InputAction, ActivationType> callback) =>
        _dispatcher.Fired -= callback;

    public void SetCombatScope(InputScope? scope) =>
        _dispatcher.SetCombatScope(scope);

    public void SetCameraAlternateScope(bool active) =>
        _dispatcher.SetCameraAlternateScope(active);
}

internal interface ICombatModeEventSurface
{
    CombatMode CurrentMode { get; }

    void AddChanged(Action<CombatMode> callback);

    void RemoveChanged(Action<CombatMode> callback);
}

internal sealed class CombatStateModeEventSurface(CombatState combat)
    : ICombatModeEventSurface
{
    private readonly CombatState _combat = combat
        ?? throw new ArgumentNullException(nameof(combat));

    public CombatMode CurrentMode => _combat.CurrentMode;

    public void AddChanged(Action<CombatMode> callback) =>
        _combat.CombatModeChanged += callback;

    public void RemoveChanged(Action<CombatMode> callback) =>
        _combat.CombatModeChanged -= callback;
}

internal interface IGameplayInputPriorityTargets
{
    bool HandlePointerAction(InputAction action, ActivationType activation);

    void HandleScroll(InputAction action);

    bool HandleCombatAction(InputAction action, ActivationType activation);

    bool HandleRetainedUiAction(InputAction action);

    bool HandleCharacterOptionAction(InputAction action);

    bool HandleSelectionAction(InputAction action);

    bool HandlePressedMovementAction(InputAction action);

    void HandleCommand(InputAction action);
}

internal sealed class RuntimeGameplayInputPriorityTargets
    : IGameplayInputPriorityTargets
{
    private readonly GameplayInputFrameController _frame;
    private readonly CameraPointerInputController _pointer;
    private readonly RetailUiRuntime? _retainedUi;
    private readonly SelectionInteractionController? _selection;
    private readonly IGameRuntimeView _runtimeView;
    private readonly IRuntimeSelectionCommands _runtimeSelection;
    private readonly IRuntimeMovementCommands _runtimeMovement;
    private readonly IRuntimeCharacterCommands _runtimeCharacter;
    private readonly IGameplayInputCommandTarget _commands;

    public RuntimeGameplayInputPriorityTargets(
        GameplayInputFrameController frame,
        CameraPointerInputController pointer,
        RetailUiRuntime? retainedUi,
        SelectionInteractionController? selection,
        IGameRuntimeView runtimeView,
        IRuntimeSelectionCommands runtimeSelection,
        IRuntimeMovementCommands runtimeMovement,
        IRuntimeCharacterCommands runtimeCharacter,
        IGameplayInputCommandTarget commands)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _retainedUi = retainedUi;
        _selection = selection;
        _runtimeView = runtimeView
            ?? throw new ArgumentNullException(nameof(runtimeView));
        _runtimeSelection = runtimeSelection
            ?? throw new ArgumentNullException(nameof(runtimeSelection));
        _runtimeMovement = runtimeMovement
            ?? throw new ArgumentNullException(nameof(runtimeMovement));
        _runtimeCharacter = runtimeCharacter
            ?? throw new ArgumentNullException(nameof(runtimeCharacter));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public bool HandlePointerAction(InputAction action, ActivationType activation) =>
        _frame.HandlePointerAction(action, activation)
        || _pointer.HandleCameraAction(action, activation);

    public void HandleScroll(InputAction action) =>
        _pointer.HandleScroll(action);

    public bool HandleCombatAction(InputAction action, ActivationType activation) =>
        _frame.HandleCombatAction(action, activation);

    public bool HandleRetainedUiAction(InputAction action) =>
        FinishJumpBeforeUi(action)
        || _retainedUi?.HandleInputAction(action) == true;

    public bool HandleCharacterOptionAction(InputAction action)
    {
        if (!RetailActionIdentityTable.TryGetCharacterOptionId(
                action,
                out uint optionId)
            || !CharacterOptionTable.TryGet(
                optionId,
                out CharacterOptionTableEntry entry))
        {
            return false;
        }

        RuntimeCharacterOptionsSnapshot options =
            _runtimeView.Character.Snapshot.Options;
        uint word = entry.IsOptions1 ? options.Options1 : options.Options2;
        bool current = (word & entry.Mask) != 0u;
        _runtimeCharacter.SetSingleOption(
            _runtimeView.Generation,
            optionId,
            !current);
        return true;
    }

    private bool FinishJumpBeforeUi(InputAction action)
    {
        RuntimeMovementCommand? command = ResolveEscapeMovementCommand(
            action,
            _runtimeView.Movement.IsStandingStill,
            _runtimeView.Movement.JumpCharge,
            _runtimeView.Actions.Snapshot.CombatAttack);
        if (command != RuntimeMovementCommand.FinishJump)
        {
            return false;
        }

        _runtimeMovement.Execute(
            _runtimeView.Generation,
            command.Value);
        return true;
    }

    public bool HandleSelectionAction(InputAction action)
    {
        if (action == InputAction.EscapeKey)
        {
            IRuntimeMovementView movement = _runtimeView.Movement;
            RuntimeCombatAttackSnapshot attack = _runtimeView.Actions.Snapshot
                .CombatAttack;
            RuntimeMovementCommand? escapeCommand =
                ResolveEscapeMovementCommand(
                    action,
                    movement.IsStandingStill,
                    movement.JumpCharge,
                    attack);
            if (escapeCommand == RuntimeMovementCommand.StopCompletely)
            {
                _runtimeMovement.Execute(
                    _runtimeView.Generation,
                    escapeCommand.Value);
                if (attack.RepeatAttackInProgress)
                    _frame.AbortAutomaticAttack();
                return true;
            }
        }

        RuntimeSelectionCommand? command = action switch
        {
            InputAction.SelectionClosestMonster =>
                RuntimeSelectionCommand.SelectClosestHostile,
            InputAction.SelectionPreviousSelection =>
                RuntimeSelectionCommand.SelectPrevious,
            InputAction.SelectionExamine =>
                RuntimeSelectionCommand.ExamineSelected,
            InputAction.UseSelected =>
                RuntimeSelectionCommand.UseSelected,
            InputAction.SelectionPickUp =>
                RuntimeSelectionCommand.PickUpSelected,
            _ => null,
        };
        if (command is { } typed)
        {
            _runtimeSelection.Execute(_runtimeView.Generation, typed);
            return true;
        }

        return _selection?.HandleInputAction(action) == true;
    }

    internal static RuntimeMovementCommand? ResolveEscapeMovementCommand(
        InputAction action,
        bool isStandingStill,
        in AcDream.Runtime.Gameplay.JumpChargeSnapshot jumpCharge,
        in RuntimeCombatAttackSnapshot attack)
    {
        if (action != InputAction.EscapeKey)
            return null;
        if (jumpCharge.IsCharging)
            return RuntimeMovementCommand.FinishJump;
        if (!isStandingStill || attack.RepeatAttackInProgress)
            return RuntimeMovementCommand.StopCompletely;
        return null;
    }

    public bool HandlePressedMovementAction(InputAction action)
    {
        if (RetailEmoteMotionTable.TryGetMotion(action, out uint motion))
        {
            _runtimeMovement.ExecuteMotion(
                _runtimeView.Generation,
                motion);
            return true;
        }

        RuntimeMovementCommand? command = ResolvePressedMovementCommand(action);
        if (command is { } typed)
        {
            _runtimeMovement.Execute(_runtimeView.Generation, typed);
            return true;
        }

        return _frame.HandlePressedMovementAction(action);
    }

    internal static RuntimeMovementCommand? ResolvePressedMovementCommand(
        InputAction action) => action switch
    {
        InputAction.MovementRunLock => RuntimeMovementCommand.ToggleRunLock,
        InputAction.MovementStop => RuntimeMovementCommand.Stop,
        InputAction.Ready => RuntimeMovementCommand.Ready,
        InputAction.Sitting => RuntimeMovementCommand.Sit,
        InputAction.Crouch => RuntimeMovementCommand.Crouch,
        InputAction.Sleeping => RuntimeMovementCommand.Sleep,
        _ => null,
    };

    public void HandleCommand(InputAction action) =>
        _commands.Handle(action);
}

internal sealed class GameplayInputActionRouter : IDisposable
{
    private readonly IGameplayInputActionSurface _actions;
    private readonly ICombatModeEventSurface _combat;
    private readonly IGameplayInputPriorityTargets _targets;
    private readonly HostQuiescenceGate _quiescence;
    private readonly Action<string> _log;
    private readonly Action<InputAction, ActivationType> _fired;
    private readonly Action<CombatMode> _combatModeChanged;
    private readonly bool[] _attached = new bool[2];
    private ResourceShutdownTransaction? _detach;
    private bool _attachStarted;
    private int _disposeRequested;
    private int _active;

    public GameplayInputActionRouter(
        IGameplayInputActionSurface actions,
        ICombatModeEventSurface combat,
        IGameplayInputPriorityTargets targets,
        HostQuiescenceGate quiescence,
        Action<string>? log = null)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
        _log = log ?? (_ => { });
        _fired = OnFired;
        _combatModeChanged = OnCombatModeChanged;
    }

    public static GameplayInputActionRouter Create(
        InputDispatcher dispatcher,
        CombatState combat,
        IGameplayInputPriorityTargets targets,
        HostQuiescenceGate quiescence,
        Action<string>? log = null) =>
        new(
            new DispatcherGameplayInputActionSurface(dispatcher),
            new CombatStateModeEventSurface(combat),
            targets,
            quiescence,
            log);

    public bool IsDisposalComplete =>
        _attached.All(static attached => !attached);

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        if (_attachStarted)
        {
            throw new InvalidOperationException(
                "Gameplay action attachment has already started.");
        }

        _attachStarted = true;
        try
        {
            _attached[0] = true;
            _actions.AddFired(_fired);
            _attached[1] = true;
            _combat.AddChanged(_combatModeChanged);

            Volatile.Write(ref _active, 1);
            SetCombatScope(_combat.CurrentMode);
        }
        catch (Exception attachError)
        {
            Deactivate();
            try
            {
                EnsureDetachTransaction().CompleteOrThrow();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Gameplay action registration and rollback both failed.",
                    new InvalidOperationException(
                        "Gameplay action registration failed.", attachError),
                    rollbackError);
            }

            throw new InvalidOperationException(
                "Gameplay action registration failed and was rolled back.",
                attachError);
        }
    }

    public void Deactivate() => Interlocked.Exchange(ref _active, 0);

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        Deactivate();
        EnsureDetachTransaction().CompleteOrThrow();
    }

    private void OnFired(InputAction action, ActivationType activation) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                Route(action, activation);
        });

    private void OnCombatModeChanged(CombatMode mode) =>
        _quiescence.Invoke(() =>
        {
            if (Volatile.Read(ref _active) != 0)
                SetCombatScope(mode);
        });

    private void Route(InputAction action, ActivationType activation)
    {
        _log($"[input] {action} {activation}");

        if (action == InputAction.CameraActivateAlternateMode)
        {
            if (activation == ActivationType.Press)
                _actions.SetCameraAlternateScope(true);
            else if (activation == ActivationType.Release)
                _actions.SetCameraAlternateScope(false);
        }

        if (_targets.HandlePointerAction(action, activation))
            return;

        if (action is InputAction.ScrollUp or InputAction.ScrollDown)
        {
            if (activation == ActivationType.Press)
                _targets.HandleScroll(action);
            return;
        }

        if (_targets.HandleCombatAction(action, activation))
            return;

        if (activation is not ActivationType.Press
            and not ActivationType.DoubleClick
            and not ActivationType.Click)
        {
            return;
        }

        if (_targets.HandleRetainedUiAction(action))
            return;
        if (_targets.HandleCharacterOptionAction(action))
            return;
        if (_targets.HandleSelectionAction(action))
            return;
        if (_targets.HandlePressedMovementAction(action))
            return;

        _targets.HandleCommand(action);
    }

    private void SetCombatScope(CombatMode mode) =>
        _actions.SetCombatScope(mode switch
        {
            CombatMode.Melee => InputScope.MeleeCombat,
            CombatMode.Missile => InputScope.MissileCombat,
            CombatMode.Magic => InputScope.MagicCombat,
            _ => null,
        });

    private ResourceShutdownTransaction EnsureDetachTransaction() =>
        _detach ??= new ResourceShutdownTransaction(
            new ResourceShutdownStage("gameplay action callbacks",
            [
                new("combat mode", () => Remove(1)),
                new("dispatcher fired", () => Remove(0)),
            ]));

    private void Remove(int index)
    {
        if (!_attached[index])
            return;

        switch (index)
        {
            case 0:
                _actions.RemoveFired(_fired);
                break;
            case 1:
                _combat.RemoveChanged(_combatModeChanged);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }

        _attached[index] = false;
    }
}
