using System;

namespace AcDream.Core.Physics;

public enum CreateObjectTimestampDisposition
{
    StaleGeneration,
    InitialGeneration,
    ExistingGeneration,
    NewGeneration,
}

public enum PositionTimestampDisposition
{
    Rejected,
    Apply,
    ForcePosition,
}

public sealed class PhysicsTimestampGate
{
    private readonly ushort[] _timestamps = new ushort[9];
    private bool _seeded;

    private const int Position = 0;
    private const int Movement = 1;
    private const int State = 2;
    private const int Vector = 3;
    private const int Teleport = 4;
    private const int ServerControlledMove = 5;
    private const int ForcePosition = 6;
    private const int ObjDesc = 7;
    private const int Instance = 8;

    public ushort PositionTimestamp => _timestamps[Position];
    public ushort MovementTimestamp => _timestamps[Movement];
    public ushort StateTimestamp => _timestamps[State];
    public ushort VectorTimestamp => _timestamps[Vector];
    public ushort TeleportTimestamp => _timestamps[Teleport];
    public ushort ServerControlledMoveTimestamp => _timestamps[ServerControlledMove];
    public ushort ForcePositionTimestamp => _timestamps[ForcePosition];
    public ushort ObjDescTimestamp => _timestamps[ObjDesc];
    public ushort InstanceTimestamp => _timestamps[Instance];

    public static bool IsNewer(ushort oldStamp, ushort newStamp)
    {
        int distance = Math.Abs((int)newStamp - oldStamp);
        return distance > 0x7FFF ? newStamp < oldStamp : oldStamp < newStamp;
    }

    public CreateObjectTimestampDisposition SeedForCreateObject(
        ushort position,
        ushort movement,
        ushort state,
        ushort vector,
        ushort teleport,
        ushort serverControlledMove,
        ushort forcePosition,
        ushort objDesc,
        ushort instance)
    {
        Span<ushort> incoming =
        [position, movement, state, vector, teleport, serverControlledMove, forcePosition, objDesc, instance];

        if (!_seeded)
        {
            incoming.CopyTo(_timestamps);
            _seeded = true;
            return CreateObjectTimestampDisposition.InitialGeneration;
        }

        ushort currentInstance = _timestamps[Instance];
        if (IsNewer(currentInstance, instance))
        {
            incoming.CopyTo(_timestamps);
            return CreateObjectTimestampDisposition.NewGeneration;
        }

        if (IsNewer(instance, currentInstance))
            return CreateObjectTimestampDisposition.StaleGeneration;

        return CreateObjectTimestampDisposition.ExistingGeneration;
    }

    public CreateObjectTimestampDisposition PreviewCreateObject(
        ushort instance)
    {
        if (!_seeded)
            return CreateObjectTimestampDisposition.InitialGeneration;
        ushort currentInstance = _timestamps[Instance];
        if (IsNewer(currentInstance, instance))
            return CreateObjectTimestampDisposition.NewGeneration;
        if (IsNewer(instance, currentInstance))
            return CreateObjectTimestampDisposition.StaleGeneration;
        return CreateObjectTimestampDisposition.ExistingGeneration;
    }

    public bool TryAcceptMovementEvent(
        ushort instance,
        ushort movement,
        ushort serverControlledMove)
    {
        if (!TryAcceptInstance(instance)) return false;
        if (!AdvanceStrict(Movement, movement)) return false;

        if (IsNewer(serverControlledMove, _timestamps[ServerControlledMove]))
            return false;
        _timestamps[ServerControlledMove] = serverControlledMove;
        return true;
    }

    public bool TryAcceptStateEvent(ushort instance, ushort state) =>
        TryAcceptInstance(instance) && AdvanceStrict(State, state);

    public bool TryAcceptVectorEvent(ushort instance, ushort vector) =>
        TryAcceptInstance(instance) && AdvanceStrict(Vector, vector);

    public bool TryAcceptObjDescEvent(ushort instance, ushort objDesc) =>
        TryAcceptInstance(instance) && AdvanceStrict(ObjDesc, objDesc);

    public bool TryAcceptPositionChannelEvent(ushort instance, ushort position) =>
        TryAcceptInstance(instance) && AdvanceStrict(Position, position);

    public bool IsCurrentInstance(ushort instance) => TryAcceptInstance(instance);

    public bool IsFreshTeleportStart(ushort teleport) =>
        _seeded && !IsNewer(teleport, _timestamps[Teleport]);

    public bool TryAcceptDeleteEvent(ushort instance, bool isLocalPlayer = false) =>
        !isLocalPlayer && _seeded && instance == _timestamps[Instance];

    public PositionTimestampDisposition TryAcceptPositionEvent(
        ushort instance,
        ushort position,
        ushort teleport,
        ushort forcePosition,
        bool isLocalPlayer)
    {
        if (!TryAcceptInstance(instance)) return PositionTimestampDisposition.Rejected;

        if (isLocalPlayer && IsNewer(_timestamps[ForcePosition], forcePosition))
        {
            _timestamps[ForcePosition] = forcePosition;

            if (!IsNewer(teleport, _timestamps[Teleport]))
            {
                _timestamps[Position] = position;
                return PositionTimestampDisposition.ForcePosition;
            }
        }

        ushort previousPosition = _timestamps[Position];
        if (!AdvanceStrict(Position, position)) return PositionTimestampDisposition.Rejected;

        if (IsNewer(teleport, _timestamps[Teleport]))
        {
            _timestamps[Position] = previousPosition;
            return PositionTimestampDisposition.Rejected;
        }

        if (IsNewer(_timestamps[Teleport], teleport))
            _timestamps[Teleport] = teleport;
        return PositionTimestampDisposition.Apply;
    }

    private bool AdvanceStrict(int channel, ushort incoming)
    {
        if (!IsNewer(_timestamps[channel], incoming)) return false;
        _timestamps[channel] = incoming;
        return true;
    }

    private bool TryAcceptInstance(ushort incoming)
    {
        return _seeded && incoming == _timestamps[Instance];
    }
}
