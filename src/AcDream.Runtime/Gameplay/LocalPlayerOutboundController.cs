using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

public interface IMovementTruthDiagnosticSink
{
    void OnOutbound(
        string kind,
        uint sequence,
        MovementResult result,
        Vector3 wirePosition,
        uint wireCellId,
        byte contactByte);

    void OnServerEcho(
        WorldSession.EntityPositionUpdate update,
        Vector3 serverWorldPosition);

    void ResetSession();
}

public sealed class LocalPlayerOutboundController
{
    private readonly IMovementTruthDiagnosticSink _diagnostic;

    internal LocalPlayerOutboundController(IMovementTruthDiagnosticSink diagnostic)
    {
        _diagnostic = diagnostic
            ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public LocalPlayerOutboundController(
        Action<string, uint, MovementResult, Vector3, uint, byte> diagnostic)
    {
        _diagnostic = new DelegateMovementTruthDiagnosticSink(diagnostic);
    }

    public void SendPreNetworkActions(
        WorldSession? session,
        PlayerMovementController controller,
        MovementResult movement,
        bool hidden)
    {
        if (session is null || hidden)
            return;

        if (!controller.TryGetOutboundPosition(out Position outboundPosition))
        {
            return;
        }
        uint wireCellId = outboundPosition.ObjCellId;
        Vector3 wirePosition = outboundPosition.Frame.Origin;
        Quaternion wireRotation = outboundPosition.Frame.Orientation;

        if (movement.ShouldSendMovementEvent)
            TrySendMovement(session, controller, movement);

        if (movement.JumpExtent.HasValue && movement.JumpVelocity.HasValue)
        {
            uint sequence = session.NextGameActionSequence();
            byte[] body = JumpAction.Build(
                gameActionSequence: sequence,
                extent: movement.JumpExtent.Value,
                velocity: movement.JumpVelocity.Value,
                cellId: wireCellId,
                position: wirePosition,
                rotation: wireRotation,
                instanceSequence: session.InstanceSequence,
                serverControlSequence: session.ServerControlSequence,
                teleportSequence: session.TeleportSequence,
                forcePositionSequence: session.ForcePositionSequence);
            session.SendGameAction(body);
        }
    }

    public void SendPostNetworkPosition(
        WorldSession? session,
        PlayerMovementController controller,
        bool hidden)
    {
        if (session is null || hidden)
            return;

        if (!controller.TryGetOutboundPosition(out Position position))
        {
            return;
        }
        uint wireCellId = position.ObjCellId;
        Vector3 wirePosition = position.Frame.Origin;
        Quaternion wireRotation = position.Frame.Orientation;

        if (!controller.ShouldSendPositionEvent(
                position,
                controller.ContactPlane,
                controller.SimTimeSeconds)
            || !controller.CanSendPositionEvent)
        {
            return;
        }

        MovementResult movement = controller.CapturePresentationResult();
        byte contactByte = movement.IsOnGround ? (byte)1 : (byte)0;
        uint sequence = session.NextGameActionSequence();
        byte[] body = AutonomousPosition.Build(
            gameActionSequence: sequence,
            cellId: wireCellId,
            position: wirePosition,
            rotation: wireRotation,
            instanceSequence: session.InstanceSequence,
            serverControlSequence: session.ServerControlSequence,
            teleportSequence: session.TeleportSequence,
            forcePositionSequence: session.ForcePositionSequence,
            lastContact: contactByte);
        _diagnostic.OnOutbound(
            "AP",
            sequence,
            movement,
            wirePosition,
            wireCellId,
            contactByte);
        session.SendGameAction(body);
        controller.NotePositionSent(
            position,
            controller.ContactPlane,
            controller.SimTimeSeconds);
    }

    internal void SendImmediatePosition(
        WorldSession? session,
        PlayerMovementController? controller)
    {
        if (session is null
            || controller is null
            || !controller.CanSendPositionEvent)
        {
            return;
        }

        if (!controller.TryGetOutboundPosition(out Position outboundPosition))
            return;
        uint cellId = outboundPosition.ObjCellId;
        Vector3 position = outboundPosition.Frame.Origin;
        Quaternion rotation = outboundPosition.Frame.Orientation;

        uint sequence = session.NextGameActionSequence();
        byte[] body = AutonomousPosition.Build(
            gameActionSequence: sequence,
            cellId: cellId,
            position: position,
            rotation: rotation,
            instanceSequence: session.InstanceSequence,
            serverControlSequence: session.ServerControlSequence,
            teleportSequence: session.TeleportSequence,
            forcePositionSequence: session.ForcePositionSequence,
            lastContact: 1);
        session.SendGameAction(body);
        controller.NotePositionSent(
            outboundPosition,
            controller.ContactPlane,
            controller.SimTimeSeconds);
    }

    public bool TrySendMovement(
        WorldSession? session,
        PlayerMovementController? controller,
        MovementResult movement)
    {
        if (session is null || controller is null)
            return false;

        if (!controller.TryGetOutboundPosition(out Position outboundPosition))
        {
            return false;
        }
        uint wireCellId = outboundPosition.ObjCellId;
        Vector3 wirePosition = outboundPosition.Frame.Origin;
        Quaternion wireRotation = outboundPosition.Frame.Orientation;

        byte contactByte = movement.IsOnGround ? (byte)1 : (byte)0;
        RawMotionState rawMotionState = BuildRawMotionState(movement);
        uint sequence = session.NextGameActionSequence();
        byte[] body = MoveToState.Build(
            gameActionSequence: sequence,
            rawMotionState: rawMotionState,
            cellId: wireCellId,
            position: wirePosition,
            rotation: wireRotation,
            instanceSequence: session.InstanceSequence,
            serverControlSequence: session.ServerControlSequence,
            teleportSequence: session.TeleportSequence,
            forcePositionSequence: session.ForcePositionSequence,
            contact: contactByte != 0,
            standingLongjump: false);
        _diagnostic.OnOutbound(
            "MTS",
            sequence,
            movement,
            wirePosition,
            wireCellId,
            contactByte);
        session.SendGameAction(body);
        controller.NoteMovementSent(
            controller.SimTimeSeconds,
            movement.IsMouseLookMovementEvent);
        return true;
    }

    public static RawMotionState BuildRawMotionState(MovementResult movement)
    {
        if (movement.RawMotionStateOverride is { } rawMotionState)
            return new RawMotionState(rawMotionState);

        HoldKey axisHoldKey = movement.IsRunning ? HoldKey.Run : HoldKey.None;
        return new RawMotionState
        {
            CurrentHoldKey = axisHoldKey,
            CurrentStyle = movement.CurrentStyle,
            ForwardCommand = movement.ForwardCommand
                ?? RawMotionState.Default.ForwardCommand,
            ForwardHoldKey = movement.ForwardCommand.HasValue
                ? axisHoldKey : HoldKey.Invalid,
            ForwardSpeed = movement.ForwardSpeed
                ?? RawMotionState.Default.ForwardSpeed,
            SidestepCommand = movement.SidestepCommand
                ?? RawMotionState.Default.SidestepCommand,
            SidestepHoldKey = movement.SidestepCommand.HasValue
                ? movement.SidestepUsesRunHold
                    ? HoldKey.Run
                    : axisHoldKey
                : HoldKey.Invalid,
            SidestepSpeed = movement.SidestepSpeed
                ?? RawMotionState.Default.SidestepSpeed,
            TurnCommand = movement.TurnCommand
                ?? RawMotionState.Default.TurnCommand,
            TurnHoldKey = movement.TurnCommand.HasValue
                ? movement.TurnUsesRunHold
                    ? HoldKey.Run
                    : axisHoldKey
                : HoldKey.Invalid,
            TurnSpeed = movement.TurnSpeed
                ?? RawMotionState.Default.TurnSpeed,
        };
    }
}

internal sealed class DelegateMovementTruthDiagnosticSink
    : IMovementTruthDiagnosticSink
{
    private readonly Action<string, uint, MovementResult, Vector3, uint, byte>
        _outbound;

    public DelegateMovementTruthDiagnosticSink(
        Action<string, uint, MovementResult, Vector3, uint, byte> outbound) =>
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));

    public void OnOutbound(
        string kind,
        uint sequence,
        MovementResult result,
        Vector3 wirePosition,
        uint wireCellId,
        byte contactByte) =>
        _outbound(kind, sequence, result, wirePosition, wireCellId, contactByte);

    public void OnServerEcho(
        WorldSession.EntityPositionUpdate update,
        Vector3 serverWorldPosition)
    {
    }

    public void ResetSession()
    {
    }
}
