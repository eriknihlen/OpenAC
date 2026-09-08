using AcDream.App.Combat;
using AcDream.App.Update;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Input;

internal interface ICombatInputFrameController
{
    void Tick();
    void HandleMovementInput(InputAction action, ActivationType activation);
    void AbortAutomaticAttack();
    bool HandleInputAction(InputAction action, ActivationType activation);
}

internal sealed class CombatAttackInputFrameAdapter : ICombatInputFrameController
{
    private readonly RuntimeCombatAttackState _owner;

    public CombatAttackInputFrameAdapter(RuntimeCombatAttackState owner) =>
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public void Tick() => _owner.Tick();

    public void HandleMovementInput(InputAction action, ActivationType activation)
    {
        if (activation != ActivationType.Press
            || action is not (
                InputAction.MovementForward
                or InputAction.MovementBackup
                or InputAction.MovementRunLock
                or InputAction.MovementJump))
        {
            return;
        }

        _owner.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.AbortForMovement,
            RuntimeInputActivation.Press));
    }

    public void AbortAutomaticAttack() =>
        _owner.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.AbortForMovement,
            RuntimeInputActivation.Press));

    public bool HandleInputAction(InputAction action, ActivationType activation)
    {
        RuntimeCombatAttackCommand? command = action switch
        {
            InputAction.CombatLowAttack =>
                RuntimeCombatAttackCommand.LowAttack,
            InputAction.CombatMediumAttack =>
                RuntimeCombatAttackCommand.MediumAttack,
            InputAction.CombatHighAttack =>
                RuntimeCombatAttackCommand.HighAttack,
            InputAction.CombatDecreaseAttackPower =>
                RuntimeCombatAttackCommand.DecreasePower,
            InputAction.CombatIncreaseAttackPower =>
                RuntimeCombatAttackCommand.IncreasePower,
            InputAction.CombatDecreaseMissileAccuracy =>
                RuntimeCombatAttackCommand.DecreasePower,
            InputAction.CombatIncreaseMissileAccuracy =>
                RuntimeCombatAttackCommand.IncreasePower,
            InputAction.CombatAimLow =>
                RuntimeCombatAttackCommand.LowAttack,
            InputAction.CombatAimMedium =>
                RuntimeCombatAttackCommand.MediumAttack,
            InputAction.CombatAimHigh =>
                RuntimeCombatAttackCommand.HighAttack,
            _ => null,
        };
        if (command is null)
            return false;

        if (activation == ActivationType.Hold)
            return true;

        return _owner.HandleCommand(new RuntimeCombatAttackInput(
            command.Value,
            activation switch
            {
                ActivationType.Press => RuntimeInputActivation.Press,
                ActivationType.Release => RuntimeInputActivation.Release,
                _ => RuntimeInputActivation.Press,
            }));
    }
}

internal sealed class GameplayInputFrameController
    : IGameplayInputFramePhase,
      AcDream.App.Streaming.ILocalPlayerTeleportInputLifetime
{
    private readonly InputDispatcher? _dispatcher;
    private readonly DispatcherMovementInputSource _movement;
    private readonly IMouseLookInputFrameController? _mouseLook;
    private readonly ICombatInputFrameController _combat;

    public GameplayInputFrameController(
        InputDispatcher? dispatcher,
        DispatcherMovementInputSource movement,
        IMouseLookInputFrameController? mouseLook,
        ICombatInputFrameController combat)
    {
        _dispatcher = dispatcher;
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _mouseLook = mouseLook;
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
    }

    public bool MouseLookActive => _mouseLook?.Active == true;

    public void Tick(UpdateFrameTiming timing)
    {
        _ = timing;
        _dispatcher?.Tick();
        _mouseLook?.Tick();
        _combat.Tick();
    }

    public bool HandlePointerAction(InputAction action, ActivationType activation) =>
        _mouseLook?.HandlePointerAction(action, activation) == true;

    public bool HandleCombatAction(InputAction action, ActivationType activation)
    {
        _combat.HandleMovementInput(action, activation);
        return _combat.HandleInputAction(action, activation);
    }

    public bool HandlePressedMovementAction(InputAction action) =>
        _movement.HandlePressedAction(action);

    public void AbortAutomaticAttack() => _combat.AbortAutomaticAttack();

    public void QueueRawMouseDelta(float dx, float dy) =>
        _mouseLook?.QueueRawDelta(dx, dy);

    public void EndMouseLook() => _mouseLook?.EndForLifecycle();

    public void ResetSession()
    {
        _mouseLook?.ResetSession();
        _movement.ResetSession();
    }
}
