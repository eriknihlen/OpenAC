using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Physics;

internal sealed class RemoteInboundMotionDispatcher
{
    private readonly Func<
        MovementManager,
        uint,
        WorldSession.EntityMotionUpdate,
        bool> _routeServerMoveTo;
    private readonly Action<IPhysicsObjHost?, uint> _stickToObject;

    public RemoteInboundMotionDispatcher(
        Func<MovementManager, uint, WorldSession.EntityMotionUpdate, bool>
            routeServerMoveTo,
        Action<IPhysicsObjHost?, uint> stickToObject)
    {
        _routeServerMoveTo = routeServerMoveTo
            ?? throw new ArgumentNullException(nameof(routeServerMoveTo));
        _stickToObject = stickToObject
            ?? throw new ArgumentNullException(nameof(stickToObject));
    }

    public RemoteInboundMotionDispatchResult Apply(
        WorldSession.EntityMotionUpdate update,
        MovementManager movement,
        IInterpretedMotionSink? animationSink,
        IPhysicsObjHost? host,
        uint cellId,
        uint fallbackForwardClass,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(movement);
        bool Current() => isCurrent?.Invoke() ?? true;

        MotionInterpreter motion = movement.Minterp;
        uint previousForward = motion.InterpretedState.ForwardCommand;
        RemoteInboundMotionDispatchResult Superseded() => new(
            RoutedMoveTo: false,
            AppliedInterpretedState: false,
            PreviousForwardCommand: previousForward,
            CurrentForwardCommand: motion.InterpretedState.ForwardCommand,
            Superseded: true);
        if (!Current())
            return Superseded();

        motion.InterruptCurrentMovement?.Invoke();
        if (!Current())
            return Superseded();
        motion.UnstickFromObject?.Invoke();
        if (!Current())
            return Superseded();

        uint wireStyle = update.MotionState.Stance != 0
            ? 0x80000000u | update.MotionState.Stance
            : 0x8000003Du;
        if (motion.InterpretedState.CurrentStyle != wireStyle)
        {
            motion.DoMotion(
                wireStyle,
                new MovementParameters());
            if (!Current())
                return Superseded();
        }

        bool routedMoveTo = _routeServerMoveTo(movement, cellId, update);
        if (!Current())
            return Superseded();
        if (routedMoveTo)
        {
            return new RemoteInboundMotionDispatchResult(
                RoutedMoveTo: true,
                AppliedInterpretedState: false,
                PreviousForwardCommand: previousForward,
                CurrentForwardCommand: motion.InterpretedState.ForwardCommand);
        }

        if (update.MotionState.MovementType != 0)
        {
            return new RemoteInboundMotionDispatchResult(
                RoutedMoveTo: false,
                AppliedInterpretedState: false,
                PreviousForwardCommand: previousForward,
                CurrentForwardCommand: motion.InterpretedState.ForwardCommand);
        }

        InboundInterpretedState interpreted =
            InboundInterpretedMotionFactory.Create(
                update.MotionState,
                fallbackForwardClass);
        motion.MoveToInterpretedState(interpreted, animationSink);
        if (!Current())
            return Superseded();

        if (update.MotionState.StickyObjectGuid is { } stickyGuid
            && stickyGuid != 0)
        {
            _stickToObject(host, stickyGuid);
            if (!Current())
                return Superseded();
        }
        motion.StandingLongJump = update.MotionState.StandingLongJump;

        return new RemoteInboundMotionDispatchResult(
            RoutedMoveTo: false,
            AppliedInterpretedState: true,
            PreviousForwardCommand: previousForward,
            CurrentForwardCommand: interpreted.ForwardCommand);
    }
}

internal readonly record struct RemoteInboundMotionDispatchResult(
    bool RoutedMoveTo,
    bool AppliedInterpretedState,
    uint PreviousForwardCommand,
    uint CurrentForwardCommand,
    bool Superseded = false)
{
    public bool ForwardCommandChanged =>
        AppliedInterpretedState
        && PreviousForwardCommand != CurrentForwardCommand;
}
