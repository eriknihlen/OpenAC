namespace AcDream.Runtime.Physics;

internal sealed class RemoteMotion :
    IRuntimeRemotePlacement,
    IRuntimeCanonicalPhysicsConsumer
{
    public AcDream.Core.Physics.PhysicsBody Body { get; }
    AcDream.Core.Physics.PhysicsBody IRuntimeRemoteMotion.Body => Body;

    public AcDream.Core.Physics.Motion.MovementManager Movement;

    public AcDream.Core.Physics.MotionInterpreter Motion => Movement.Minterp;
    public AcDream.Core.Physics.Motion.MotionTableDispatchSink? Sink;
    /// <summary>Last UpdatePosition timestamp — drives body.update_object sub-stepping.</summary>
    public double LastServerPosTime;
    /// <summary>Last known server position — kept for diagnostics / HUD.</summary>
    public System.Numerics.Vector3 LastServerPos;
    public System.Numerics.Vector3 LastShadowSyncPos;
    public System.Numerics.Quaternion LastShadowSyncOrientation;
    public System.Numerics.Vector3 ServerVelocity;
    public bool HasServerVelocity;

    public AcDream.Core.Physics.Motion.MoveToManager? MoveTo => Movement.MoveTo;

    private Func<AcDream.Core.Physics.Motion.IPhysicsObjHost?>? _physicsHostReader;
    private bool _fullPhysicsHostBound;
    public EntityPhysicsHost? Host => _fullPhysicsHostBound
        ? _physicsHostReader?.Invoke() as EntityPhysicsHost
        : null;
    public bool ServerMoveToActive;

    public bool HasMoveToDestination;

    public System.Numerics.Vector3 MoveToDestinationWorld;

    public float MoveToMinDistance;

    public float MoveToDistanceToObject;

    public bool MoveToMoveTowards;

    public double LastMoveToPacketTime;
    private uint _cellId;
    private Func<uint>? _canonicalCellReader;
    private Action<uint>? _canonicalCellWriter;

    public uint CellId
    {
        get => _canonicalCellReader?.Invoke() ?? _cellId;
        set
        {
            _cellId = value;
            _canonicalCellWriter?.Invoke(value);
        }
    }

    System.Numerics.Vector3 IRuntimeRemotePlacement.LastServerPosition
    {
        get => LastServerPos;
        set => LastServerPos = value;
    }

    double IRuntimeRemotePlacement.LastServerPositionTime
    {
        get => LastServerPosTime;
        set => LastServerPosTime = value;
    }

    System.Numerics.Vector3 IRuntimeRemotePlacement.LastShadowSyncPosition
    {
        get => LastShadowSyncPos;
        set => LastShadowSyncPos = value;
    }

    System.Numerics.Quaternion IRuntimeRemotePlacement.LastShadowSyncOrientation
    {
        get => LastShadowSyncOrientation;
        set => LastShadowSyncOrientation = value;
    }

    bool IRuntimeRemotePlacement.Airborne
    {
        get => Airborne;
        set => Airborne = value;
    }

    void IRuntimeRemotePlacement.HitGround() =>
        Movement.HitGround();

    void IRuntimeRemotePlacement.LeaveGround() =>
        Motion.LeaveGround();

    public bool Airborne;

    public AcDream.Core.Physics.InterpolationManager Interp { get; } =
        new AcDream.Core.Physics.InterpolationManager();

    public AcDream.Core.Physics.RemoteMotionCombiner Position { get; } =
        new AcDream.Core.Physics.RemoteMotionCombiner();

    public AcDream.Core.Physics.Motion.MotionDeltaFrame PositionManagerDeltaScratch { get; } =
        new AcDream.Core.Physics.Motion.MotionDeltaFrame();

    public System.Numerics.Vector3 PrevServerPos;
    public double PrevServerPosTime;
    public double LastOmegaDiagLogTime;

    public float MaxRootMotionSpeedSinceLastUP;

    public RemoteMotion(AcDream.Core.Physics.PhysicsBody? sharedBody = null)
    {
        Body = sharedBody ?? new AcDream.Core.Physics.PhysicsBody
        {
            State = AcDream.Core.Physics.PhysicsStateFlags.ReportCollisions,
            TransientState = AcDream.Core.Physics.TransientStateFlags.Contact
                           | AcDream.Core.Physics.TransientStateFlags.OnWalkable
                           | AcDream.Core.Physics.TransientStateFlags.Active,
            InWorld = true,
        };
        Movement = new AcDream.Core.Physics.Motion.MovementManager(
            new AcDream.Core.Physics.MotionInterpreter(Body)
            {
                WeenieObj = new AcDream.Core.Physics.RemoteWeenie(),
            });
    }

    public void BindCanonicalRuntime(
        Func<AcDream.Core.Physics.Motion.IPhysicsObjHost?> readPhysicsHost,
        Func<uint> readCell,
        Action<uint> writeCell)
    {
        ArgumentNullException.ThrowIfNull(readPhysicsHost);
        ArgumentNullException.ThrowIfNull(readCell);
        ArgumentNullException.ThrowIfNull(writeCell);
        if (_physicsHostReader is not null
            || _canonicalCellReader is not null
            || _canonicalCellWriter is not null)
        {
            throw new InvalidOperationException("The remote canonical runtime context is already bound.");
        }

        _physicsHostReader = readPhysicsHost;
        _canonicalCellReader = readCell;
        _canonicalCellWriter = writeCell;
    }

    public void BindCanonicalCell(Func<uint> read, Action<uint> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        if (_canonicalCellReader is not null || _canonicalCellWriter is not null)
            throw new InvalidOperationException("The remote canonical cell source is already bound.");
        _canonicalCellReader = read;
        _canonicalCellWriter = write;
    }

    public void MarkFullPhysicsHostBound()
    {
        if (_physicsHostReader is null)
            throw new InvalidOperationException("The canonical physics-host source is not bound.");
        _fullPhysicsHostBound = true;
    }
}
