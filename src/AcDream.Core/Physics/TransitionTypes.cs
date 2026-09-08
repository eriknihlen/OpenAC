using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Items;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

public enum TransitionState
{
    Invalid = 0,
    OK = 1,
    Collided = 2,
    Adjusted = 3,
    Slid = 4,
}

internal enum TransitionCellCollisionPhase
{
    Environment,
    Building,
    Objects,
}

public enum InsertType
{
    Transition = 0,
    Placement = 1,
    InitialPlacement = 2,
}

[Flags]
public enum ObjectInfoState : uint
{
    None = 0x000,
    Contact = 0x001,
    OnWalkable = 0x002,
    IsViewer = 0x004,
    PathClipped = 0x008,
    FreeRotate = 0x010,
    PerfectClip = 0x040,
    IsImpenetrable = 0x080,
    IsPlayer = 0x100,
    EdgeSlide = 0x200,
    IgnoreCreatures = 0x400,
    IsPK = 0x800,
    IsPKLite = 0x1000,
    CanBypassMoveRestrictions = 0x2000,
}

public sealed class ObjectInfo
{
    public ObjectInfoState State;
    public float StepUpHeight = 0.01f;  // PhysicsGlobals.DefaultStepHeight
    public float StepDownHeight = 0.04f;
    public bool Ethereal;
    public bool StepDown = true;
    public float Scale = 1.0f;

    public PhysicsStateFlags MoverPhysicsState;

    /// <summary>
    /// Authoritative designated projectile target. Zero means unknown/no
    /// target; it is never inferred from the player's selected UI target.
    /// </summary>
    public uint TargetId;

    public uint SelfEntityId;

    public bool MoverHasGravity;

    public bool Contact => (State & ObjectInfoState.Contact) != 0;
    public bool OnWalkable => (State & ObjectInfoState.OnWalkable) != 0;
    public bool IsViewer => (State & ObjectInfoState.IsViewer) != 0;
    public bool IsPlayer => (State & ObjectInfoState.IsPlayer) != 0;
    public bool EdgeSlide => (State & ObjectInfoState.EdgeSlide) != 0;
    public bool PathClipped => (State & ObjectInfoState.PathClipped) != 0;
    public bool FreeRotate => (State & ObjectInfoState.FreeRotate) != 0;
    public bool CanBypassMoveRestrictions => (State & ObjectInfoState.CanBypassMoveRestrictions) != 0;

    public bool MissileIgnore(
        uint targetEntityId,
        uint targetPhysicsState,
        EntityCollisionFlags targetFlags)
    {
        var targetState = (PhysicsStateFlags)targetPhysicsState;
        if ((targetState & PhysicsStateFlags.Missile) != 0)
            return true;
        if ((MoverPhysicsState & PhysicsStateFlags.Missile) == 0)
            return false;
        if (targetEntityId == TargetId)
            return false;

        bool hasWeenie = (targetFlags & EntityCollisionFlags.HasWeenie) != 0;
        if ((targetState & PhysicsStateFlags.Ethereal) != 0 && hasWeenie)
            return true;

        return TargetId != 0
            && hasWeenie
            && (targetFlags & EntityCollisionFlags.IsCreature) != 0;
    }

    public TransitionState CheckEntryRestrictions(
        uint cellRestrictionObj,
        ClientObjectTable? objects)
    {
        if (!IsPlayer) return TransitionState.OK;               // NPCs/props bypass entirely
        if (CanBypassMoveRestrictions) return TransitionState.OK;
        if (cellRestrictionObj == 0) return TransitionState.OK;

        ClientObject? restrictionObject = objects?.Get(cellRestrictionObj);
        if (restrictionObject is null) return TransitionState.Collided;

        uint moverId = SelfEntityId;
        uint houseOwnerId = restrictionObject.HouseOwnerId ?? 0;
        if (houseOwnerId == 0 || houseOwnerId == moverId) return TransitionState.OK;

        HouseRestrictionRecord? restrictions = restrictionObject.Restrictions;
        if (restrictions is null) return TransitionState.OK;

        uint moverMonarchId = objects?.Get(moverId)?.MonarchId ?? 0;
        return restrictions.IsAllowedIn(moverId, moverMonarchId)
            ? TransitionState.OK
            : TransitionState.Collided;
    }

    public float GetWalkableZ()
        => OnWalkable ? PhysicsGlobals.FloorZ : PhysicsGlobals.LandingZ;

    public bool VelocityKilled;

    public void StopVelocity() { VelocityKilled = true; }

    public void ResetForReuse()
    {
        State = ObjectInfoState.None;
        StepUpHeight = PhysicsGlobals.DefaultStepHeight;
        StepDownHeight = 0.04f;
        Ethereal = false;
        StepDown = true;
        Scale = 1.0f;
        MoverPhysicsState = PhysicsStateFlags.None;
        TargetId = 0;
        SelfEntityId = 0;
        MoverHasGravity = false;
        VelocityKilled = false;
    }

    internal void CopyFrom(ObjectInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        State = source.State;
        StepUpHeight = source.StepUpHeight;
        StepDownHeight = source.StepDownHeight;
        Ethereal = source.Ethereal;
        StepDown = source.StepDown;
        Scale = source.Scale;
        MoverPhysicsState = source.MoverPhysicsState;
        TargetId = source.TargetId;
        SelfEntityId = source.SelfEntityId;
        MoverHasGravity = source.MoverHasGravity;
        VelocityKilled = source.VelocityKilled;
    }
}

public sealed class CollisionInfo
{
    private bool _contactPlaneValid;
    private Plane _contactPlane;
    private uint _contactPlaneCellId;
    private bool _contactPlaneIsWater;

    private bool _lastKnownContactPlaneValid;
    private Plane _lastKnownContactPlane;
    private uint _lastKnownContactPlaneCellId;
    private bool _lastKnownContactPlaneIsWater;

    public bool ContactPlaneValid
    {
        get => _contactPlaneValid;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _contactPlaneValid != value)
                PhysicsDiagnostics.LogCpBoolWrite("ContactPlaneValid", _contactPlaneValid, value);
            _contactPlaneValid = value;
        }
    }

    public Plane ContactPlane
    {
        get => _contactPlane;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && !PlaneEquals(_contactPlane, value))
                PhysicsDiagnostics.LogCpPlaneWrite("ContactPlane", _contactPlane, value);
            _contactPlane = value;
        }
    }

    public uint ContactPlaneCellId
    {
        get => _contactPlaneCellId;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _contactPlaneCellId != value)
                PhysicsDiagnostics.LogCpCellIdWrite("ContactPlaneCellId", _contactPlaneCellId, value);
            _contactPlaneCellId = value;
        }
    }

    public bool ContactPlaneIsWater
    {
        get => _contactPlaneIsWater;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _contactPlaneIsWater != value)
                PhysicsDiagnostics.LogCpBoolWrite("ContactPlaneIsWater", _contactPlaneIsWater, value);
            _contactPlaneIsWater = value;
        }
    }

    public bool LastKnownContactPlaneValid
    {
        get => _lastKnownContactPlaneValid;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _lastKnownContactPlaneValid != value)
                PhysicsDiagnostics.LogCpBoolWrite("LastKnownContactPlaneValid", _lastKnownContactPlaneValid, value);
            _lastKnownContactPlaneValid = value;
        }
    }

    public Plane LastKnownContactPlane
    {
        get => _lastKnownContactPlane;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && !PlaneEquals(_lastKnownContactPlane, value))
                PhysicsDiagnostics.LogCpPlaneWrite("LastKnownContactPlane", _lastKnownContactPlane, value);
            _lastKnownContactPlane = value;
        }
    }

    public uint LastKnownContactPlaneCellId
    {
        get => _lastKnownContactPlaneCellId;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _lastKnownContactPlaneCellId != value)
                PhysicsDiagnostics.LogCpCellIdWrite("LastKnownContactPlaneCellId", _lastKnownContactPlaneCellId, value);
            _lastKnownContactPlaneCellId = value;
        }
    }

    public bool LastKnownContactPlaneIsWater
    {
        get => _lastKnownContactPlaneIsWater;
        set
        {
            if (PhysicsDiagnostics.ProbeContactPlaneEnabled && _lastKnownContactPlaneIsWater != value)
                PhysicsDiagnostics.LogCpBoolWrite("LastKnownContactPlaneIsWater", _lastKnownContactPlaneIsWater, value);
            _lastKnownContactPlaneIsWater = value;
        }
    }

    private static bool PlaneEquals(Plane a, Plane b) =>
        a.Normal.X == b.Normal.X &&
        a.Normal.Y == b.Normal.Y &&
        a.Normal.Z == b.Normal.Z &&
        a.D == b.D;

    public bool SlidingNormalValid;
    public Vector3 SlidingNormal;       // XY only (Z zeroed)

    public bool CollisionNormalValid;
    public Vector3 CollisionNormal;

    public bool CollidedWithEnvironment;
    public int FramesStationaryFall;

    public Vector3 AdjustOffset;
    public readonly List<uint> CollideObjectGuids = new();
    public uint? LastCollidedObjectGuid;

    internal int ContactPlaneWriteCount { get; private set; }

    public void SetContactPlane(
        Plane plane,
        uint cellId,
        bool isWater = false)
    {
        if (ContactPlaneValid
            && ContactPlaneCellId == cellId
            && ContactPlaneIsWater == isWater
            && PlaneEquals(ContactPlane, plane))
        {
            return;
        }

        ContactPlaneWriteCount++;
        ContactPlaneValid = true;
        ContactPlane = plane;
        ContactPlaneCellId = cellId;
        ContactPlaneIsWater = isWater;

    }

    public void InitContactPlane(
        Plane plane,
        uint cellId,
        bool isWater = false)
    {
        SetContactPlane(plane, cellId, isWater);

        LastKnownContactPlaneValid = true;
        LastKnownContactPlane = plane;
        LastKnownContactPlaneCellId = cellId;
        LastKnownContactPlaneIsWater = isWater;
    }

    public void SetSlidingNormal(Vector3 normal)
    {
        SlidingNormalValid = true;
        SlidingNormal = new Vector3(normal.X, normal.Y, 0f);
        if (SlidingNormal.LengthSquared() > PhysicsGlobals.EpsilonSq)
            SlidingNormal = Vector3.Normalize(SlidingNormal);
    }

    public void SetCollisionNormal(Vector3 normal)
    {
        CollisionNormalValid = true;
        CollisionNormal = normal;
    }

    public void ResetForReuse()
    {
        _contactPlaneValid = false;
        _contactPlane = default;
        _contactPlaneCellId = 0;
        _contactPlaneIsWater = false;
        _lastKnownContactPlaneValid = false;
        _lastKnownContactPlane = default;
        _lastKnownContactPlaneCellId = 0;
        _lastKnownContactPlaneIsWater = false;

        SlidingNormalValid = false;
        SlidingNormal = Vector3.Zero;
        CollisionNormalValid = false;
        CollisionNormal = Vector3.Zero;
        CollidedWithEnvironment = false;
        FramesStationaryFall = 0;
        AdjustOffset = Vector3.Zero;
        CollideObjectGuids.Clear();
        LastCollidedObjectGuid = null;
        ContactPlaneWriteCount = 0;
    }

    internal void CopyFrom(CollisionInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _contactPlaneValid = source._contactPlaneValid;
        _contactPlane = source._contactPlane;
        _contactPlaneCellId = source._contactPlaneCellId;
        _contactPlaneIsWater = source._contactPlaneIsWater;
        _lastKnownContactPlaneValid = source._lastKnownContactPlaneValid;
        _lastKnownContactPlane = source._lastKnownContactPlane;
        _lastKnownContactPlaneCellId = source._lastKnownContactPlaneCellId;
        _lastKnownContactPlaneIsWater = source._lastKnownContactPlaneIsWater;
        SlidingNormalValid = source.SlidingNormalValid;
        SlidingNormal = source.SlidingNormal;
        CollisionNormalValid = source.CollisionNormalValid;
        CollisionNormal = source.CollisionNormal;
        CollidedWithEnvironment = source.CollidedWithEnvironment;
        FramesStationaryFall = source.FramesStationaryFall;
        AdjustOffset = source.AdjustOffset;
        CollideObjectGuids.Clear();
        CollideObjectGuids.AddRange(source.CollideObjectGuids);
        LastCollidedObjectGuid = source.LastCollidedObjectGuid;
        ContactPlaneWriteCount = source.ContactPlaneWriteCount;
    }
}

internal sealed class CellOrderScratchArena
{
    private readonly List<List<uint>> _records = new();
    private int _depth;

    internal int ActiveDepth => _depth;
    internal int RetainedRecordCount => _records.Count;
    internal IReadOnlyList<List<uint>> RetainedRecords => _records;

    internal List<uint> Rent()
    {
        if (_depth == _records.Count)
            _records.Add(new List<uint>());

        List<uint> record = _records[_depth++];
        record.Clear();
        return record;
    }

    internal void Return(List<uint> record)
    {
        int index = _depth - 1;
        if (index < 0 || !ReferenceEquals(_records[index], record))
        {
            throw new InvalidOperationException(
                "Cell-order scratch must be returned in LIFO order.");
        }

        record.Clear();
        _depth = index;
    }

    internal void ResetForReuse()
    {
        foreach (List<uint> record in _records)
            record.Clear();
        _depth = 0;
    }
}

public sealed class SpherePath
{
    public int NumSphere = 1;

    // Sphere arrays — index 0 = foot/body, index 1 = head (when NumSphere==2)
    public readonly Sphere[] LocalSphere = new Sphere[2] { new(), new() };
    public readonly Sphere[] GlobalSphere = new Sphere[2] { new(), new() };
    public readonly Sphere[] GlobalCurrCenter = new Sphere[2] { new(), new() };

    // Positions
    public Vector3 BeginPos;
    public Vector3 EndPos;
    public Vector3 CurPos;
    public Vector3 CheckPos;
    public Quaternion BeginOrientation = Quaternion.Identity;
    public Quaternion EndOrientation = Quaternion.Identity;
    public Quaternion CurOrientation = Quaternion.Identity;
    public Quaternion CheckOrientation = Quaternion.Identity;

    public uint CurCellId;
    public uint CheckCellId;

    public Vector3? CarriedBlockOrigin;

    // Per-step offset
    public Vector3 GlobalOffset;

    // Step-up state
    public bool StepUp;
    public Vector3 StepUpNormal;
    public bool Collide;

    // Step-down state
    public bool StepDown;
    public float StepDownAmt;
    public float WalkInterp = 1.0f;

    // Walkable tracking
    public bool WalkableValid;
    public Plane WalkablePlane;
    public Vector3[]? WalkableVertices;
    public Vector3 WalkableUp = Vector3.UnitZ;
    public float WalkableAllowance = PhysicsGlobals.FloorZ;
    public bool HasWalkablePolygon => WalkableValid && WalkableVertices is { Length: >= 3 };

    public bool LastWalkableValid;
    public Plane LastWalkablePlane;
    public Vector3[]? LastWalkableVertices;
    public Vector3 LastWalkableUp = Vector3.UnitZ;
    public bool HasLastWalkablePolygon => LastWalkableValid && LastWalkableVertices is { Length: >= 3 };

    // Backup for restore
    public Vector3 BackupCheckPos;
    public uint BackupCheckCellId;

    // Misc flags
    public bool NegPolyHit;
    public bool NegStepUp;
    public Vector3 NegCollisionNormal;
    public bool CheckWalkable;
    public InsertType InsertType = InsertType.Transition;
    public bool PlacementAllowsSliding = true;

    public bool ObstructionEthereal;

    public bool BldgCheck;

    public bool HitsInteriorCell;

    private Vector3[]? _walkableVertexStorage;
    private Vector3[]? _lastWalkableVertexStorage;
    internal readonly CellArray CellCandidates = new();
    internal readonly CellArray SetPositionQueryFootprint = new();
    internal readonly CellOrderScratchArena OrderedCellScratch = new();

    internal Vector3[]? RetainedWalkableVertexStorage => _walkableVertexStorage;
    internal Vector3[]? RetainedLastWalkableVertexStorage => _lastWalkableVertexStorage;

    public void SetCheckPos(Vector3 pos, uint cellId)
    {
        CheckPos = pos;
        CheckCellId = cellId;
        for (int i = 0; i < NumSphere; i++)
        {
            GlobalSphere[i].Origin =
                Vector3.Transform(LocalSphere[i].Origin, CheckOrientation) + pos;
            GlobalSphere[i].Radius = LocalSphere[i].Radius;
        }
    }

    public void AddOffsetToCheckPos(Vector3 offset)
    {
        CheckPos += offset;
        for (int i = 0; i < NumSphere; i++)
            GlobalSphere[i].Origin += offset;
    }

    public void SaveCheckPos()
    {
        BackupCheckPos = CheckPos;
        BackupCheckCellId = CheckCellId;
    }

    public void RestoreCheckPos()
    {
        SetCheckPos(BackupCheckPos, BackupCheckCellId);
    }

    public void SetCollide(Vector3 collisionNormal)
    {
        Collide = true;
        BackupCheckPos = CheckPos;
        BackupCheckCellId = CheckCellId;
        StepUpNormal = collisionNormal;
        WalkInterp = 1.0f;
    }

    public void SetWalkable(Plane plane, Vector3[] vertices, Vector3 up)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        WalkableValid = true;
        WalkablePlane = plane;
        WalkableVertices = CopyExact(vertices, ref _walkableVertexStorage);
        WalkableUp = up;
        WalkableAllowance = PhysicsGlobals.FloorZ;

        LastWalkableValid = true;
        LastWalkablePlane = plane;
        LastWalkableVertices = CopyExact(vertices, ref _lastWalkableVertexStorage);
        LastWalkableUp = up;
    }

    internal void SetWalkable(
        Plane plane,
        in TerrainTriangleVertices vertices,
        Vector3 up)
    {
        Vector3[] walkable = RentExact(3, ref _walkableVertexStorage);
        Vector3[] last = RentExact(3, ref _lastWalkableVertexStorage);
        walkable[0] = last[0] = vertices.V0;
        walkable[1] = last[1] = vertices.V1;
        walkable[2] = last[2] = vertices.V2;

        WalkableValid = true;
        WalkablePlane = plane;
        WalkableVertices = walkable;
        WalkableUp = up;
        WalkableAllowance = PhysicsGlobals.FloorZ;
        LastWalkableValid = true;
        LastWalkablePlane = plane;
        LastWalkableVertices = last;
        LastWalkableUp = up;
    }

    internal void SetWalkableTransformed(
        Plane plane,
        ReadOnlySpan<Vector3> localVertices,
        Quaternion localToWorld,
        float scale,
        Vector3 worldOrigin,
        Vector3 up)
    {
        Vector3[] walkable = RentExact(localVertices.Length, ref _walkableVertexStorage);
        Vector3[] last = RentExact(localVertices.Length, ref _lastWalkableVertexStorage);

        for (int i = 0; i < localVertices.Length; i++)
        {
            Vector3 transformed =
                Vector3.Transform(localVertices[i] * scale, localToWorld) + worldOrigin;
            walkable[i] = transformed;
            last[i] = transformed;
        }

        WalkableValid = true;
        WalkablePlane = plane;
        WalkableVertices = walkable;
        WalkableUp = up;
        WalkableAllowance = PhysicsGlobals.FloorZ;

        LastWalkableValid = true;
        LastWalkablePlane = plane;
        LastWalkableVertices = last;
        LastWalkableUp = up;
    }

    public void ClearWalkable()
    {
        WalkableValid = false;
        WalkableVertices = null;
    }

    public bool RestoreLastWalkable()
    {
        if (!HasLastWalkablePolygon || LastWalkableVertices is null)
            return false;

        WalkableValid = true;
        WalkablePlane = LastWalkablePlane;
        WalkableVertices = CopyExact(LastWalkableVertices, ref _walkableVertexStorage);
        WalkableUp = LastWalkableUp;
        return true;
    }

    internal bool CheckWalkables()
    {
        if (!HasWalkablePolygon || WalkableVertices is null)
            return true;

        var footSphere = GlobalSphere[0];
        return BSPQuery.CheckWalkableSupport(
            WalkablePlane,
            WalkableVertices,
            footSphere.Origin,
            footSphere.Radius * 0.5f,
            WalkableUp);
    }

    public void ResetForReuse()
    {
        NumSphere = 1;
        ResetSphereArray(LocalSphere);
        ResetSphereArray(GlobalSphere);
        ResetSphereArray(GlobalCurrCenter);

        BeginPos = Vector3.Zero;
        EndPos = Vector3.Zero;
        CurPos = Vector3.Zero;
        CheckPos = Vector3.Zero;
        BeginOrientation = Quaternion.Identity;
        EndOrientation = Quaternion.Identity;
        CurOrientation = Quaternion.Identity;
        CheckOrientation = Quaternion.Identity;
        CurCellId = 0;
        CheckCellId = 0;
        CarriedBlockOrigin = null;
        GlobalOffset = Vector3.Zero;
        StepUp = false;
        StepUpNormal = Vector3.Zero;
        Collide = false;
        StepDown = false;
        StepDownAmt = 0f;
        WalkInterp = 1.0f;
        WalkableValid = false;
        WalkablePlane = default;
        WalkableVertices = null;
        WalkableUp = Vector3.UnitZ;
        WalkableAllowance = PhysicsGlobals.FloorZ;
        LastWalkableValid = false;
        LastWalkablePlane = default;
        LastWalkableVertices = null;
        LastWalkableUp = Vector3.UnitZ;
        BackupCheckPos = Vector3.Zero;
        BackupCheckCellId = 0;
        NegPolyHit = false;
        NegStepUp = false;
        NegCollisionNormal = Vector3.Zero;
        CheckWalkable = false;
        InsertType = InsertType.Transition;
        PlacementAllowsSliding = true;
        ObstructionEthereal = false;
        BldgCheck = false;
        HitsInteriorCell = false;
        if (_walkableVertexStorage is not null)
            System.Array.Clear(_walkableVertexStorage);
        if (_lastWalkableVertexStorage is not null)
            System.Array.Clear(_lastWalkableVertexStorage);
        CellCandidates.Clear();
        CellCandidates.UnionTarget = null;
        SetPositionQueryFootprint.Clear();
        OrderedCellScratch.ResetForReuse();
    }

    internal void CopyFrom(SpherePath source)
    {
        ArgumentNullException.ThrowIfNull(source);
        NumSphere = source.NumSphere;
        CopySphereArray(source.LocalSphere, LocalSphere);
        CopySphereArray(source.GlobalSphere, GlobalSphere);
        CopySphereArray(source.GlobalCurrCenter, GlobalCurrCenter);
        BeginPos = source.BeginPos;
        EndPos = source.EndPos;
        CurPos = source.CurPos;
        CheckPos = source.CheckPos;
        BeginOrientation = source.BeginOrientation;
        EndOrientation = source.EndOrientation;
        CurOrientation = source.CurOrientation;
        CheckOrientation = source.CheckOrientation;
        CurCellId = source.CurCellId;
        CheckCellId = source.CheckCellId;
        CarriedBlockOrigin = source.CarriedBlockOrigin;
        GlobalOffset = source.GlobalOffset;
        StepUp = source.StepUp;
        StepUpNormal = source.StepUpNormal;
        Collide = source.Collide;
        StepDown = source.StepDown;
        StepDownAmt = source.StepDownAmt;
        WalkInterp = source.WalkInterp;
        WalkableValid = source.WalkableValid;
        WalkablePlane = source.WalkablePlane;
        WalkableVertices = source.WalkableVertices is null
            ? null
            : CopyExact(
                source.WalkableVertices,
                ref _walkableVertexStorage);
        WalkableUp = source.WalkableUp;
        WalkableAllowance = source.WalkableAllowance;
        LastWalkableValid = source.LastWalkableValid;
        LastWalkablePlane = source.LastWalkablePlane;
        LastWalkableVertices = source.LastWalkableVertices is null
            ? null
            : CopyExact(
                source.LastWalkableVertices,
                ref _lastWalkableVertexStorage);
        LastWalkableUp = source.LastWalkableUp;
        BackupCheckPos = source.BackupCheckPos;
        BackupCheckCellId = source.BackupCheckCellId;
        NegPolyHit = source.NegPolyHit;
        NegStepUp = source.NegStepUp;
        NegCollisionNormal = source.NegCollisionNormal;
        CheckWalkable = source.CheckWalkable;
        InsertType = source.InsertType;
        PlacementAllowsSliding = source.PlacementAllowsSliding;
        ObstructionEthereal = source.ObstructionEthereal;
        BldgCheck = source.BldgCheck;
        HitsInteriorCell = source.HitsInteriorCell;
        CellCandidates.Clear();
        foreach (uint id in source.CellCandidates)
            CellCandidates.Add(id);
        OrderedCellScratch.ResetForReuse();
    }

    private static Vector3[] CopyExact(
        ReadOnlySpan<Vector3> source,
        ref Vector3[]? storage)
    {
        Vector3[] destination = RentExact(source.Length, ref storage);
        source.CopyTo(destination);
        return destination;
    }

    private static Vector3[] RentExact(int length, ref Vector3[]? storage)
    {
        if (storage is null || storage.Length != length)
            storage = new Vector3[length];

        return storage;
    }

    private static void ResetSphereArray(Sphere[] spheres)
    {
        for (int i = 0; i < spheres.Length; i++)
        {
            spheres[i].Origin = Vector3.Zero;
            spheres[i].Radius = 0f;
        }
    }

    private static void CopySphereArray(Sphere[] source, Sphere[] destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i].Origin = source[i].Origin;
            destination[i].Radius = source[i].Radius;
        }
    }

    public TransitionState StepUpSlide(Transition transition)
    {
        var ci = transition.CollisionInfo;
        ci.ContactPlaneValid = false;
        ci.ContactPlaneIsWater = false;
        return transition.SlideSphereInternal(StepUpNormal, GlobalCurrCenter[0].Origin);
    }

    public TransitionState PrecipiceSlide(Transition transition)
    {
        if (!HasWalkablePolygon || WalkableVertices is null)
        {
            ClearWalkable();
            return TransitionState.Collided;
        }

        if (!BSPQuery.FindCrossedEdge(
                WalkablePlane,
                WalkableVertices,
                GlobalSphere[0].Origin,
                WalkableUp,
                out var collisionNormal))
        {
            ClearWalkable();
            return TransitionState.Collided;
        }

        ClearWalkable();
        StepUp = false;

        var offset = GlobalSphere[0].Origin - GlobalCurrCenter[0].Origin;
        if (Vector3.Dot(collisionNormal, offset) > 0f)
            collisionNormal = -collisionNormal;

        return transition.SlideSphereInternal(collisionNormal, GlobalCurrCenter[0].Origin);
    }

    public void InitPath(
        Vector3 begin,
        Vector3 end,
        uint cellId,
        float sphereRadius,
        float sphereHeight = 0f,
        Vector3? localSphereOrigin = null,
        Quaternion? beginOrientation = null,
        Quaternion? endOrientation = null)
    {
        Vector3 origin0 = localSphereOrigin ?? new Vector3(0, 0, sphereRadius);
        if (sphereHeight > 0)
        {
            InitPathCore(
                begin, end, cellId, 2,
                origin0, sphereRadius,
                new Vector3(0, 0, sphereHeight - sphereRadius), sphereRadius,
                beginOrientation, endOrientation);
        }
        else
        {
            InitPathCore(
                begin, end, cellId, 1,
                origin0, sphereRadius,
                Vector3.Zero, 0f,
                beginOrientation, endOrientation);
        }
    }

    public void InitPath(
        Vector3 begin,
        Vector3 end,
        uint cellId,
        ImmutableArray<FlatCollisionSphere> spheres,
        float scale = 1f,
        Quaternion? beginOrientation = null,
        Quaternion? endOrientation = null)
    {
        if (spheres.IsDefaultOrEmpty)
        {
            InitPathCore(
                begin, end, cellId, 1,
                new Vector3(0, 0, PhysicsGlobals.DummySphereRadius), PhysicsGlobals.DummySphereRadius,
                Vector3.Zero, 0f,
                beginOrientation, endOrientation);
            return;
        }

        int count = spheres.Length <= 2 ? spheres.Length : 2;
        FlatCollisionSphere s0 = spheres[0];
        if (count > 1)
        {
            FlatCollisionSphere s1 = spheres[1];
            InitPathCore(
                begin, end, cellId, 2,
                s0.Origin * scale, s0.Radius * scale,
                s1.Origin * scale, s1.Radius * scale,
                beginOrientation, endOrientation);
        }
        else
        {
            InitPathCore(
                begin, end, cellId, 1,
                s0.Origin * scale, s0.Radius * scale,
                Vector3.Zero, 0f,
                beginOrientation, endOrientation);
        }
    }

    private void InitPathCore(
        Vector3 begin,
        Vector3 end,
        uint cellId,
        int numSphere,
        Vector3 origin0,
        float radius0,
        Vector3 origin1,
        float radius1,
        Quaternion? beginOrientation,
        Quaternion? endOrientation)
    {
        BeginPos = begin;
        EndPos = end;
        CurPos = begin;
        CurCellId = cellId;

        BeginOrientation = beginOrientation ?? Quaternion.Identity;
        EndOrientation = endOrientation ?? BeginOrientation;
        CurOrientation = BeginOrientation;
        CheckOrientation = BeginOrientation;

        NumSphere = numSphere;
        LocalSphere[0].Origin = origin0;
        LocalSphere[0].Radius = radius0;
        if (numSphere > 1)
        {
            LocalSphere[1].Origin = origin1;
            LocalSphere[1].Radius = radius1;
        }

        SetCheckPos(begin, cellId);

        for (int i = 0; i < NumSphere; i++)
        {
            GlobalCurrCenter[i].Origin =
                Vector3.Transform(LocalSphere[i].Origin, CurOrientation) + begin;
            GlobalCurrCenter[i].Radius = LocalSphere[i].Radius;
        }
    }
}

public static class PhysicsGlobals
{
    public const float EPSILON = 0.0002f;
    public const float EpsilonSq = EPSILON * EPSILON;
    public const float LandingZ = 0.0871557f;
    public const float FloorZ = 0.6642f;
    public const float DefaultStepHeight = 0.01f;
    public const float Gravity = -9.8f;
    public const float MaxVelocity = 50.0f;
    public const float DummySphereRadius = 0.1f;
}

public sealed class Transition
{
    public readonly ObjectInfo ObjectInfo = new();
    public readonly SpherePath SpherePath = new();
    public readonly CollisionInfo CollisionInfo = new();

    public void ResetForReuse()
    {
        ObjectInfo.ResetForReuse();
        SpherePath.ResetForReuse();
        CollisionInfo.ResetForReuse();
    }

    internal void CopyFrom(Transition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectInfo.CopyFrom(source.ObjectInfo);
        SpherePath.CopyFrom(source.SpherePath);
        CollisionInfo.CopyFrom(source.CollisionInfo);
    }


    public static bool BspOnlyDispatch(uint entityState)
        => (entityState & (uint)PhysicsStateFlags.HasPhysicsBsp) != 0;

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    public bool FindTransitionalPosition(PhysicsEngine engine)
    {
        var sp = SpherePath;

        ObjectInfo.VelocityKilled = false;

        if (sp.CurCellId == 0)
            return false;

        Vector3 offset = sp.EndPos - sp.BeginPos;
        float dist = offset.Length();
        float radius = sp.LocalSphere[0].Radius;

        if (radius <= PhysicsGlobals.EPSILON)
            return false;

        int numSteps;
        Vector3 offsetPerStep;

        if (ObjectInfo.IsViewer)
        {
            if (dist > PhysicsGlobals.EPSILON)
            {
                offsetPerStep = offset * (radius / dist);      // radius-length steps
                numSteps = (int)MathF.Floor(dist / radius) + 1;
            }
            else
            {
                numSteps = 0;
                offsetPerStep = Vector3.Zero;
            }
        }
        else
        {
            float step = dist / radius;

            if (step > 1.0f)
            {
                numSteps = (int)MathF.Ceiling(step);
                offsetPerStep = offset * (1f / numSteps);
            }
            else if (offset != Vector3.Zero)
            {
                numSteps = 1;
                offsetPerStep = offset;
            }
            else
            {
                numSteps = 0;
                offsetPerStep = Vector3.Zero;
            }
        }


        // Apply free rotation if requested.
        if (ObjectInfo.FreeRotate)
            sp.CurOrientation = sp.EndOrientation;

        sp.SetCheckPos(sp.CurPos, sp.CurCellId);

        if (numSteps <= 0)
        {
            if (!ObjectInfo.FreeRotate)
                sp.CurOrientation = sp.EndOrientation;
            return true;
        }

        // ------------------------------------------------------------------
        // Main stepping loop
        // ------------------------------------------------------------------
        var transitionState = TransitionState.OK;
        bool stepWalkProbe = PhysicsDiagnostics.ProbeStepWalkEnabled;

        if (stepWalkProbe)
        {
            PhysicsDiagnostics.LogStepWalk(
                "find-start", -1, numSteps, sp, CollisionInfo, ObjectInfo,
                Vector3.Zero, Vector3.Zero,
                transitionState,
                $"dist={dist:F4} radius={radius:F4}");
        }

        for (int i = 0; i < numSteps; i++)
        {
            if (ObjectInfo.IsViewer && i == numSteps - 1 && dist > PhysicsGlobals.EPSILON)
                offsetPerStep = offset * ((dist - (numSteps - 1) * radius) / dist);

            Vector3 requestedOffset = offsetPerStep;

            sp.GlobalOffset = AdjustOffset(requestedOffset);

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "after-adjust", i, numSteps, sp, CollisionInfo, ObjectInfo,
                    requestedOffset, sp.GlobalOffset,
                    transitionState);
            }

            if (!ObjectInfo.IsViewer
                && sp.GlobalOffset.LengthSquared() < PhysicsGlobals.EpsilonSq)
            {
                if (stepWalkProbe)
                {
                    PhysicsDiagnostics.LogStepWalk(
                        "abort-small-offset", i, numSteps, sp, CollisionInfo, ObjectInfo,
                        requestedOffset, sp.GlobalOffset,
                        transitionState,
                        $"offsetLenSq={sp.GlobalOffset.LengthSquared():F8}");
                }
                return i != 0 && transitionState == TransitionState.OK;
            }

            // Interpolate orientation (non-free-rotate path).
            if (!ObjectInfo.FreeRotate)
            {
                float delta = (i + 1f) / numSteps;
                sp.CheckOrientation = Quaternion.Slerp(sp.BeginOrientation, sp.EndOrientation, delta);
            }

            CollisionInfo.SlidingNormalValid = false;
            CollisionInfo.ContactPlaneValid = false;
            CollisionInfo.ContactPlaneIsWater = false;

            sp.AddOffsetToCheckPos(sp.GlobalOffset);

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "before-insert", i, numSteps, sp, CollisionInfo, ObjectInfo,
                    requestedOffset, sp.GlobalOffset,
                    transitionState);
            }

            var result = TransitionalInsert(3, engine);

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "after-insert", i, numSteps, sp, CollisionInfo, ObjectInfo,
                    requestedOffset, sp.GlobalOffset,
                    result);
            }

            transitionState = ValidateTransition(result);

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "after-validate", i, numSteps, sp, CollisionInfo, ObjectInfo,
                    requestedOffset, sp.GlobalOffset,
                    transitionState);
            }

            if (CollisionInfo.FramesStationaryFall != 0
                || (CollisionInfo.CollisionNormalValid && ObjectInfo.PathClipped))
                break;
        }

        if (stepWalkProbe)
        {
            PhysicsDiagnostics.LogStepWalk(
                "find-end", -1, numSteps, sp, CollisionInfo, ObjectInfo,
                Vector3.Zero, Vector3.Zero,
                transitionState);
        }

        return transitionState == TransitionState.OK;
    }

    internal bool FindValidPosition(PhysicsEngine engine) =>
        SpherePath.InsertType == InsertType.Transition
            ? FindTransitionalPosition(engine)
            : FindPlacementPosition(engine);

    internal bool FindPlacementPosition(PhysicsEngine engine)
    {
        SpherePath sp = SpherePath;
        sp.SetCheckPos(sp.CurPos, sp.CurCellId);
        sp.InsertType = InsertType.InitialPlacement;

        TransitionState initial = ValidatePlacement(
            engine,
            InitialPlacementInsert(engine),
            retryPlacement: true);
        if (initial != TransitionState.OK)
            return false;

        sp.InsertType = InsertType.Placement;
        if (!FindPlacementPos(engine))
            return false;

        if (ObjectInfo.StepDown)
        {
            const float placementWalkableAllowance = 0.0871556997f;
            float stepDownHeight = ObjectInfo.StepDownHeight;
            sp.WalkableAllowance = placementWalkableAllowance;
            sp.SaveCheckPos();
            InsertType savedInsert = sp.InsertType;
            sp.InsertType = InsertType.Transition;

            (float probeHeight, int probeCount) = GetStepDownProbePlan(
                sp.NumSphere,
                sp.GlobalSphere[0].Radius,
                stepDownHeight);
            bool stepped = DoStepDown(
                probeHeight,
                placementWalkableAllowance,
                engine);
            if (!stepped && probeCount > 1)
            {
                stepped = DoStepDown(
                    probeHeight,
                    placementWalkableAllowance,
                    engine);
            }
            if (!stepped)
            {
                sp.RestoreCheckPos();
                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;
            }
            sp.InsertType = savedInsert;
            sp.ClearWalkable();
        }

        return ValidatePlacement(
                engine,
                TransitionState.OK,
                retryPlacement: true)
            == TransitionState.OK;
    }

    public bool FindPlacementPos(PhysicsEngine engine)
    {
        var sp = SpherePath;

        sp.SetCheckPos(sp.CurPos, sp.CurCellId);
        CollisionInfo.SlidingNormalValid = false;
        CollisionInfo.ContactPlaneValid = false;
        CollisionInfo.ContactPlaneIsWater = false;

        var transitionState = ValidatePlacementTransition(
            TransitionalInsert(3, engine));
        if (transitionState == TransitionState.OK)
            return true;

        if (!sp.PlacementAllowsSliding)
            return false;

        float adjustDistance = 4f;
        float adjustRadius = adjustDistance;
        float sphereRadius = sp.LocalSphere[0].Radius;
        bool fakeSphere = false;

        if (sphereRadius < 0.125f)
        {
            fakeSphere = true;
            adjustRadius = 2f;
        }
        else if (sphereRadius < 0.48f)
        {
            sphereRadius = 0.48f;
        }

        double stepCountExact = 4d / (double)sphereRadius;
        if (fakeSphere)
            stepCountExact *= 0.5d;
        if (stepCountExact <= 1d)
            return false;

        int stepCount = (int)Math.Ceiling(stepCountExact);
        float distancePerStep = (float)((double)adjustRadius / stepCount);
        float radiansPerStep = (float)(
            ((double)distancePerStep / sphereRadius)
            * 3.14159989f);
        float totalDistance = 0f;
        float totalRadians = 0f;

        for (int ring = 0; ring < stepCount; ring++)
        {
            totalDistance += distancePerStep;
            totalRadians += radiansPerStep;

            int sampleCount = (int)Math.Ceiling((double)totalRadians) * 2;
            float headingStep = (float)(360d / sampleCount);

            for (int sample = 0; sample < sampleCount; sample++)
            {
                sp.SetCheckPos(sp.CurPos, sp.CurCellId);

                float heading = headingStep * sample;
                Vector3 offset = GetPlacementCompassOffset(
                    heading,
                    totalDistance);
                sp.GlobalOffset = AdjustOffset(offset);

                if (sp.GlobalOffset.LengthSquared()
                    < PhysicsGlobals.EpsilonSq)
                    continue;

                sp.AddOffsetToCheckPos(sp.GlobalOffset);
                CollisionInfo.SlidingNormalValid = false;
                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;

                transitionState = ValidatePlacementTransition(
                    TransitionalInsert(3, engine));
                if (transitionState == TransitionState.OK)
                    return true;
            }
        }

        return false;
    }

    internal static Vector3 GetPlacementCompassOffset(
        float headingDegrees,
        float distance)
    {
        const double DegreesToRadians = 0.017453292519943295d;
        double radians = (double)headingDegrees * DegreesToRadians;
        return new Vector3(
            (float)Math.Sin(radians) * distance,
            (float)Math.Cos(radians) * distance,
            0f);
    }

    private TransitionState ValidatePlacementTransition(
        TransitionState transitionState)
    {
        var sp = SpherePath;
        if (sp.CheckCellId == 0)
            return TransitionState.Collided;

        if (transitionState == TransitionState.OK)
        {
            sp.CurPos = sp.CheckPos;
            sp.CurCellId = sp.CheckCellId;
            sp.CurOrientation = sp.CheckOrientation;
            for (int i = 0; i < sp.NumSphere; i++)
            {
                sp.GlobalCurrCenter[i].Origin =
                    Vector3.Transform(sp.LocalSphere[i].Origin, sp.CurOrientation) + sp.CurPos;
                sp.GlobalCurrCenter[i].Radius = sp.LocalSphere[i].Radius;
            }
        }
        else if (transitionState > TransitionState.OK
                 && transitionState <= TransitionState.Slid
                 && sp.PlacementAllowsSliding)
        {
            CollisionInfo.ResetForReuse();
        }

        return transitionState;
    }

    internal TransitionState ValidatePlacementTransitionForTest(
        TransitionState transitionState) =>
        ValidatePlacementTransition(transitionState);

    private TransitionState ValidatePlacement(
        PhysicsEngine engine,
        TransitionState transitionState,
        bool retryPlacement)
    {
        SpherePath sp = SpherePath;
        if (sp.CheckCellId == 0u)
            return TransitionState.Collided;

        if (transitionState == TransitionState.OK)
        {
            sp.CurPos = sp.CheckPos;
            sp.CurCellId = sp.CheckCellId;
            sp.CurOrientation = sp.CheckOrientation;
            for (int i = 0; i < sp.NumSphere; i++)
            {
                sp.GlobalCurrCenter[i].Origin =
                    Vector3.Transform(
                        sp.LocalSphere[i].Origin,
                        sp.CurOrientation)
                    + sp.CurPos;
                sp.GlobalCurrCenter[i].Radius = sp.LocalSphere[i].Radius;
            }
        }
        else if ((transitionState is TransitionState.Adjusted
                  or TransitionState.Slid)
                 && retryPlacement)
        {
            return ValidatePlacement(
                engine,
                PlacementInsert(engine),
                retryPlacement: false);
        }
        return transitionState;
    }

    internal TransitionState ValidatePlacementForTest(
        PhysicsEngine engine,
        TransitionState transitionState,
        bool retryPlacement) =>
        ValidatePlacement(engine, transitionState, retryPlacement);


    internal TransitionState TransitionalInsertForTest(
        int numAttempts,
        PhysicsEngine engine)
        => TransitionalInsert(numAttempts, engine);

    internal static (float ProbeHeight, int ProbeCount) GetStepDownProbePlan(
        int numSpheres,
        float sphereRadius,
        float requestedHeight)
    {
        float diameter = sphereRadius * 2f;
        float probeHeight = requestedHeight;

        if (numSpheres < 2 && diameter <= probeHeight)
            probeHeight = sphereRadius * 0.5f;

        if (diameter > probeHeight)
            return (probeHeight, 1);

        return (probeHeight * 0.5f, 2);
    }

    internal bool EdgeSlideAfterStepDownFailedForTest(
        PhysicsEngine engine,
        float stepDownHeight,
        float zVal,
        out TransitionState result)
        => EdgeSlideAfterStepDownFailed(engine, stepDownHeight, zVal, out result);

    internal TransitionState CliffSlideForTest(Plane contactPlane)
        => CliffSlide(contactPlane);

    private TransitionState TransitionalInsert(int numAttempts, PhysicsEngine engine)
    {
        if (SpherePath.CheckCellId == 0) return TransitionState.OK;
        if (numAttempts <= 0) return TransitionState.Invalid;

        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        TransitionState transitState = TransitionState.OK;

        for (int attempt = 0; attempt < numAttempts; attempt++)
        {
            transitState = InsertIntoCell(
                engine,
                sp.CheckCellId,
                numAttempts);

            if (transitState == TransitionState.Collided)
            {
                sp.NegPolyHit = false;
                return TransitionState.Collided;
            }

            if (transitState == TransitionState.Slid)
            {
                ci.ContactPlaneValid = false;
                ci.ContactPlaneIsWater = false;
                sp.NegPolyHit = false;
                continue;
            }

            if (transitState == TransitionState.Adjusted)
            {
                // insert_into_cell exhausted its own retry budget. Preserve
                // every sphere field except neg_poly_hit at this outer
                // boundary and start the next outer attempt.
                sp.NegPolyHit = false;
                continue;
            }

            if (transitState != TransitionState.OK)
                continue;

            var otherState = RunCheckOtherCellsAndAdvance(
                engine, sp.GlobalSphere[0].Origin, sp.GlobalSphere[0].Radius);

            if (otherState != TransitionState.OK)
                sp.NegPolyHit = false;

            if (otherState == TransitionState.Collided)
                return TransitionState.Collided;

            if (otherState != TransitionState.OK)
            {
                transitState = otherState;
                continue;   // ADJUSTED / SLID → retry the attempt
            }

            if (sp.Collide)
            {
                sp.Collide = false;

                bool reset = false;
                if (ci.ContactPlaneValid && DoCheckWalkable(PhysicsGlobals.LandingZ, engine))
                {
                    var savedInsert = sp.InsertType;
                    sp.InsertType = InsertType.Placement;

                    var placeState = TransitionalInsert(numAttempts, engine);

                    sp.InsertType = savedInsert;

                    if (placeState != TransitionState.OK)
                    {
                        // Placement rejected — fall through to restore.
                        placeState = TransitionState.OK;
                        reset = true;
                    }
                    else if (!reset)
                    {
                        sp.ClearWalkable();
                        return placeState;
                    }
                }
                else
                    reset = true;

                sp.ClearWalkable();

                if (reset)
                {
                    sp.RestoreCheckPos();
                    ci.ContactPlaneValid = false;
                    ci.ContactPlaneIsWater = false;

                    bool diagSteep = PhysicsDiagnostics.DumpSteepRoofEnabled;
                    if (diagSteep)
                    {
                        Console.WriteLine(
                            $"[steep-roof] PHASE3-RESET lastKnownValid={ci.LastKnownContactPlaneValid} " +
                            $"checkPos=({sp.CheckPos.X:F2},{sp.CheckPos.Y:F2},{sp.CheckPos.Z:F2}) " +
                            $"curPos=({sp.CurPos.X:F2},{sp.CurPos.Y:F2},{sp.CurPos.Z:F2}) " +
                            $"stepUpNormal=({sp.StepUpNormal.X:F2},{sp.StepUpNormal.Y:F2},{sp.StepUpNormal.Z:F2})");
                    }

                    if (ci.LastKnownContactPlaneValid)
                    {
                        ci.LastKnownContactPlaneValid = false;
                        oi.StopVelocity();
                        if (diagSteep)
                            Console.WriteLine($"[steep-roof] PHASE3-RESET-KILLV ← StopVelocity called");
                    }
                    else
                    {
                        ci.SetCollisionNormal(sp.StepUpNormal);
                    }

                    return TransitionState.Collided;
                }
            }

            if (sp.NegPolyHit && !sp.StepDown && !sp.StepUp)
            {
                sp.NegPolyHit = false;

                if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[neg-poly-dispatch] stepUp={sp.NegStepUp} n=({sp.NegCollisionNormal.X:F3},{sp.NegCollisionNormal.Y:F3},{sp.NegCollisionNormal.Z:F3})"));
                }

                if (sp.NegStepUp)
                {
                    if (DoStepUp(sp.NegCollisionNormal, engine))
                    {
                    }
                    else
                    {
                        var stepUpSlideRes = sp.StepUpSlide(this);
                        if (stepUpSlideRes == TransitionState.Slid)
                        {
                            transitState = stepUpSlideRes;
                            ci.ContactPlaneValid = false;
                            ci.ContactPlaneIsWater = false;
                            continue;
                        }
                        if (stepUpSlideRes != TransitionState.OK)
                            return stepUpSlideRes;
                    }
                }
                else
                {
                    var slideRes = SlideSphereInternal(
                        sp.NegCollisionNormal, sp.GlobalCurrCenter[0].Origin);
                    if (slideRes == TransitionState.Collided)
                        return TransitionState.Collided;   // degenerate slide → hard stop
                    transitState = slideRes;
                    continue;
                }
            }

            if (ci.ContactPlaneValid)
                return TransitionState.OK;

            if (oi.Contact && !sp.StepDown && sp.CheckCellId != 0 && oi.StepDown)
            {
                float zVal = oi.GetWalkableZ();
                float stepDownHeight = oi.StepDownHeight;
                sp.WalkableAllowance = zVal;
                sp.SaveCheckPos();

                (float probeHeight, int probeCount) = GetStepDownProbePlan(
                    sp.NumSphere,
                    sp.GlobalSphere[0].Radius,
                    stepDownHeight);
                stepDownHeight = probeHeight;

                bool steppedDown = false;
                for (int probe = 0; probe < probeCount; probe++)
                {
                    if (DoStepDown(stepDownHeight, zVal, engine))
                    {
                        steppedDown = true;
                        break;
                    }
                }

                if (steppedDown)
                {
                    sp.ClearWalkable();
                    return TransitionState.OK;
                }


                bool stop = EdgeSlideAfterStepDownFailed(
                    engine,
                    stepDownHeight,
                    zVal,
                    out TransitionState edgeState);
                if (stop)
                    return edgeState;

                if (edgeState == TransitionState.Slid)
                {
                    transitState = edgeState;
                    ci.ContactPlaneValid = false;
                    ci.ContactPlaneIsWater = false;
                    sp.NegPolyHit = false;
                    continue;
                }

                if (edgeState == TransitionState.Adjusted)
                {
                    transitState = edgeState;
                    sp.NegPolyHit = false;
                    continue;
                }

                transitState = edgeState;
                continue;
            }

            return TransitionState.OK;
        }

        return transitState;
    }

    private TransitionState InsertIntoCell(
        PhysicsEngine engine,
        uint cellId,
        int numAttempts)
    {
        if (cellId == 0)
            return TransitionState.Collided;

        TransitionState state = TransitionState.OK;
        for (int attempt = 0; attempt < numAttempts; attempt++)
        {
            state = FindPrimaryCellCollisions(engine, cellId, attempt);
            if (state is TransitionState.OK or TransitionState.Collided)
                return state;

            if (state == TransitionState.Slid)
            {
                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;
            }
        }

        return state;
    }

    private TransitionState InitialPlacementInsert(PhysicsEngine engine)
    {
        SpherePath sp = SpherePath;
        if (sp.CheckCellId == 0u)
            return TransitionState.Collided;

        TransitionState result = InsertIntoCell(
            engine,
            sp.CheckCellId,
            numAttempts: 3);
        if (result == TransitionState.OK)
        {
            result = RunCheckOtherCellsAndAdvance(
                engine,
                sp.GlobalSphere[0].Origin,
                sp.GlobalSphere[0].Radius);
        }
        return result;
    }

    private TransitionState PlacementInsert(PhysicsEngine engine)
    {
        SpherePath sp = SpherePath;
        if (sp.CheckCellId == 0u)
            return TransitionState.Collided;

        TransitionState result = InsertIntoCell(
            engine,
            sp.CheckCellId,
            numAttempts: 3);
        return result == TransitionState.OK
            ? RunCheckOtherCellsAndAdvance(
                engine,
                sp.GlobalSphere[0].Origin,
                sp.GlobalSphere[0].Radius)
            : result;
    }

    private TransitionState FindPrimaryCellCollisions(
        PhysicsEngine engine,
        uint cellId,
        int innerAttempt)
    {
        TransitionState environment = ObservePrimaryCellPhase(
            engine,
            TransitionCellCollisionPhase.Environment,
            cellId,
            FindEnvCollisions(engine, cellId));
        if (environment != TransitionState.OK)
        {
            PhysicsDiagnostics.TraceTransitInsertAttempt(
                ObjectInfo.SelfEntityId, innerAttempt, "environment",
                environment, null, null, environment,
                CollisionInfo.CollisionNormal, CollisionInfo.LastCollidedObjectGuid);
            return environment;
        }

        TransitionState building = ObservePrimaryCellPhase(
            engine,
            TransitionCellCollisionPhase.Building,
            cellId,
            FindBuildingCollisions(engine, cellId));
        if (building != TransitionState.OK)
        {
            PhysicsDiagnostics.TraceTransitInsertAttempt(
                ObjectInfo.SelfEntityId, innerAttempt, "building",
                environment, building, null, building,
                CollisionInfo.CollisionNormal, CollisionInfo.LastCollidedObjectGuid);
            return building;
        }

        TransitionState objects = ObservePrimaryCellPhase(
            engine,
            TransitionCellCollisionPhase.Objects,
            cellId,
            FindObjCollisionsInCell(engine, cellId));
        PhysicsDiagnostics.TraceTransitInsertAttempt(
            ObjectInfo.SelfEntityId, innerAttempt, "objects",
            environment, building, objects, objects,
            CollisionInfo.CollisionNormal, CollisionInfo.LastCollidedObjectGuid);
        return objects;
    }

    private TransitionState ObservePrimaryCellPhase(
        PhysicsEngine engine,
        TransitionCellCollisionPhase phase,
        uint cellId,
        TransitionState actual)
        => engine.TransitionCellCollisionTestHook is { } hook
            ? hook(this, phase, cellId, actual)
            : actual;

    private bool EdgeSlideAfterStepDownFailed(
        PhysicsEngine engine,
        float stepDownHeight,
        float zVal,
        out TransitionState result)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        if (!oi.OnWalkable || !oi.EdgeSlide)
        {
            sp.ClearWalkable();
            sp.RestoreCheckPos();
            ci.ContactPlaneValid = false;
            ci.ContactPlaneIsWater = false;
            result = TransitionState.OK;
            return true;
        }

        if (ci.ContactPlaneValid && ci.ContactPlane.Normal.Z < zVal)
        {
            var cliffPlane = ci.ContactPlane;
            sp.ClearWalkable();
            sp.RestoreCheckPos();
            ci.ContactPlaneValid = false;
            ci.ContactPlaneIsWater = false;
            result = CliffSlide(cliffPlane);
            return false;
        }

        if (sp.HasWalkablePolygon)
        {
            sp.RestoreCheckPos();
            ci.ContactPlaneValid = false;
            ci.ContactPlaneIsWater = false;
            result = sp.PrecipiceSlide(this);
            return result == TransitionState.Collided;
        }

        if (ci.ContactPlaneValid)
        {
            sp.ClearWalkable();
            sp.RestoreCheckPos();
            ci.ContactPlaneValid = false;
            ci.ContactPlaneIsWater = false;
            result = TransitionState.OK;
            return true;
        }

        Vector3 backToCurrent = sp.GlobalCurrCenter[0].Origin - sp.GlobalSphere[0].Origin;
        sp.AddOffsetToCheckPos(backToCurrent);

        _ = DoStepDown(stepDownHeight, zVal, engine);

        ci.ContactPlaneValid = false;
        ci.ContactPlaneIsWater = false;
        sp.RestoreCheckPos();

        if (sp.HasWalkablePolygon)
        {
            result = sp.PrecipiceSlide(this);
            return result == TransitionState.Collided;
        }

        sp.ClearWalkable();
        result = TransitionState.Collided;
        return true;
    }

    private TransitionState CliffSlide(Plane contactPlane)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;

        Vector3 referenceNormal = ci.LastKnownContactPlane.Normal;

        Vector3 contactNormal = Vector3.Cross(contactPlane.Normal, referenceNormal);
        contactNormal.Z = 0f;

        Vector3 collideNormal = new(-contactNormal.Y, contactNormal.X, 0f);
        if (collideNormal.LengthSquared() < PhysicsGlobals.EpsilonSq)
        {
            return TransitionState.OK;
        }

        collideNormal = Vector3.Normalize(collideNormal);

        Vector3 offset = sp.GlobalSphere[0].Origin - sp.GlobalCurrCenter[0].Origin;
        float angle = Vector3.Dot(collideNormal, offset);

        if (angle <= 0f)
        {
            sp.AddOffsetToCheckPos(collideNormal * angle);
            ci.SetCollisionNormal(collideNormal);
        }
        else
        {
            sp.AddOffsetToCheckPos(collideNormal * -angle);
            ci.SetCollisionNormal(-collideNormal);
        }

        return TransitionState.Adjusted;
    }


    internal bool TryFindIndoorWalkablePlane(
        CellPhysics cellPhysics,
        Vector3 localFootCenter,
        float sphereRadius,
        out System.Numerics.Plane worldPlane,
        out Vector3[] worldVertices,
        out uint hitPolyId)
    {
        worldPlane = default;
        worldVertices = System.Array.Empty<Vector3>();
        hitPolyId = 0;

        bool useFlat = cellPhysics.FlatPhysicsBsp is { RootIndex: >= 0 };
        if (!useFlat && cellPhysics.BSP?.Root is null)
            return false;

        float savedWalkableAllowance = this.SpherePath.WalkableAllowance;
        this.SpherePath.WalkableAllowance = PhysicsGlobals.FloorZ;

        ResolvedPolygon? hitPoly = null;
        int flatHitIndex = -1;
        ushort hitId = 0;
        Vector3 adjustedCenter;
        bool found;

        try
        {
            if (useFlat)
            {
                found = FlatBspQuery.FindWalkableSphere(
                    cellPhysics.FlatPhysicsBsp!,
                    this,
                    localFootCenter,
                    sphereRadius,
                    INDOOR_WALKABLE_PROBE_DISTANCE,
                    Vector3.UnitZ,
                    out flatHitIndex,
                    out hitId,
                    out adjustedCenter);
            }
            else
            {
                found = BSPQuery.FindWalkableSphere(
                    cellPhysics.BSP!.Root!,
                    cellPhysics.Resolved,
                    this,
                    localFootCenter,
                    sphereRadius,
                    INDOOR_WALKABLE_PROBE_DISTANCE,
                    Vector3.UnitZ,
                    out hitPoly,
                    out hitId,
                    out adjustedCenter);
            }
        }
        finally
        {
            this.SpherePath.WalkableAllowance = savedWalkableAllowance;
        }


        if (!found)
            return false;

        Plane localPlane;
        if (useFlat)
        {
            FlatPolygonTable table =
                cellPhysics.FlatPhysicsBsp!.PolygonTable;
            if ((uint)flatHitIndex >= (uint)table.Polygons.Length)
                return false;
            FlatCollisionPolygon polygon = table.Polygons[flatHitIndex];
            localPlane = polygon.Plane;

            var worldNormal = Vector3.TransformNormal(
                localPlane.Normal,
                cellPhysics.WorldTransform);
            worldNormal = Vector3.Normalize(worldNormal);
            var worldV0 = Vector3.Transform(
                table.Vertices[polygon.VertexRange.Start],
                cellPhysics.WorldTransform);
            float worldD = -Vector3.Dot(worldNormal, worldV0);
            worldPlane = new System.Numerics.Plane(worldNormal, worldD);

            worldVertices = new Vector3[polygon.VertexRange.Count];
            for (int i = 0; i < worldVertices.Length; i++)
            {
                worldVertices[i] = Vector3.Transform(
                    table.Vertices[polygon.VertexRange.Start + i],
                    cellPhysics.WorldTransform);
            }
        }
        else
        {
            if (hitPoly is null)
                return false;
            localPlane = hitPoly.Plane;
            Vector3[] localVertices = hitPoly.Vertices;

            // Transform hit polygon's plane + vertices to world space. Math is
            // unchanged from the previous graph implementation.
            var worldNormal = Vector3.TransformNormal(
                localPlane.Normal,
                cellPhysics.WorldTransform);
            worldNormal = Vector3.Normalize(worldNormal);
            var worldV0 = Vector3.Transform(
                localVertices[0],
                cellPhysics.WorldTransform);
            float worldD = -Vector3.Dot(worldNormal, worldV0);
            worldPlane = new System.Numerics.Plane(worldNormal, worldD);

            worldVertices = new Vector3[localVertices.Length];
            for (int i = 0; i < localVertices.Length; i++)
            {
                worldVertices[i] = Vector3.Transform(
                    localVertices[i],
                    cellPhysics.WorldTransform);
            }
        }

        hitPolyId = hitId;
        return true;
    }

    private const float INDOOR_WALKABLE_PROBE_DISTANCE = 0.5f;


    internal TransitionState CheckOtherCells(
        PhysicsEngine engine,
        Vector3 footCenter,
        float sphereRadius,
        System.Collections.Generic.IReadOnlyCollection<uint> cellSet)
    {
        if (engine.DataCache is null) return TransitionState.OK;
        var sp = SpherePath;

        List<uint> ordered = sp.OrderedCellScratch.Rent();
        try
        {
            if (cellSet is CellArray cellArray)
            {
                for (int i = 0; i < cellArray.Count; i++)
                    ordered.Add(cellArray.OrderedIds[i]);
            }
            else
            {
                foreach (uint id in cellSet)
                    ordered.Add(id);
            }
            ordered.Sort();

            foreach (uint cellId in ordered)
            {
                if (cellId == sp.CheckCellId) continue;

                footCenter = sp.GlobalSphere[0].Origin;

                if ((cellId & 0xFFFFu) < 0x0100u)
                {
                    if (engine.DataCache.CellGraph.GetVisible(cellId) is null)
                        continue;

                    var terrainWalkable = engine.SampleTerrainWalkableInCell(
                        cellId, footCenter.X, footCenter.Y);
                    if (terrainWalkable is { } terrain)
                    {
                        var terrainState = ValidateWalkable(
                            footCenter,
                            sphereRadius,
                            terrain.Plane,
                            terrain.IsWater,
                            terrain.WaterDepth,
                            cellId: terrain.CellId,
                            walkableVertices: terrain.Vertices);

                        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                        {
                            var plane = terrain.Plane;
                            Console.WriteLine(System.FormattableString.Invariant(
                                $"[other-cells] primary=0x{sp.CheckCellId:X8} iter=0x{cellId:X8} terrain wpos=({footCenter.X:F3},{footCenter.Y:F3},{footCenter.Z:F3}) r={sphereRadius:F3} result={terrainState} n=({plane.Normal.X:F3},{plane.Normal.Y:F3},{plane.Normal.Z:F3}) d={plane.D:F3}"));
                        }

                        if (PhysicsDiagnostics.ProbePushBackEnabled)
                        {
                            PhysicsDiagnostics.LogPushBackCellTransit(
                                primaryCellId: sp.CheckCellId,
                                otherCellId: cellId,
                                bspResult: (int)terrainState,
                                halted: false);
                        }

                        if (ApplyOtherCellResult(terrainState, out var terrainHalted))
                            return terrainHalted;
                    }

                    var bldgOtherState = FindBuildingCollisions(engine, cellId);
                    if (ApplyOtherCellResult(bldgOtherState, out var bldgHalted))
                        return bldgHalted;

                    var objOtherState = FindObjCollisionsInCell(engine, cellId);
                    if (ApplyOtherCellResult(objOtherState, out var objHalted))
                        return objHalted;

                    continue;
                }

                var cell = engine.DataCache.GetCellStruct(cellId);
                if (cell is null ||
                    !CollisionTraversal.HasPhysics(engine.DataCache, cell))
                {
                    continue;
                }

                var localCenter = Vector3.Transform(footCenter, cell.InverseWorldTransform);
                var localCurrCenter = Vector3.Transform(sp.GlobalCurrCenter[0].Origin, cell.InverseWorldTransform);

                bool hasLocalSphere1 = sp.NumSphere > 1;
                Vector3 localSphere1Center = hasLocalSphere1
                    ? Vector3.Transform(sp.GlobalSphere[1].Origin, cell.InverseWorldTransform)
                    : Vector3.Zero;
                float localSphere1Radius = hasLocalSphere1
                    ? sp.GlobalSphere[1].Radius
                    : 0f;

                System.Numerics.Quaternion cellRotation;
                Vector3 cellOrigin;
                if (!System.Numerics.Matrix4x4.Decompose(cell.WorldTransform, out _,
                        out cellRotation, out cellOrigin))
                {
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[other-cells] WARN cell 0x{cellId:X8} WorldTransform did not decompose — falling back to identity rotation"));
                    cellRotation = System.Numerics.Quaternion.Identity;
                    cellOrigin = cell.WorldTransform.Translation;
                }

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                    PhysicsDiagnostics.LastBspHitPoly = null;

                var result = CollisionTraversal.FindCollisions(
                    engine.DataCache!, cell, this,
                    localCenter,
                    sphereRadius,
                    hasLocalSphere1,
                    localSphere1Center,
                    localSphere1Radius,
                    localCurrCenter,
                    Vector3.UnitZ, 1.0f, cellRotation, engine,
                    worldOrigin: cellOrigin);

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    var hit = PhysicsDiagnostics.LastBspHitPoly;
                    string polyDesc = hit is null
                        ? "poly=n/a"
                        : System.FormattableString.Invariant(
                            $"poly=0x{hit.Id:X4} n=({hit.Plane.Normal.X:F3},{hit.Plane.Normal.Y:F3},{hit.Plane.Normal.Z:F3}) d={hit.Plane.D:F3} sides={hit.SidesType}");
                    FlatCollisionSphere bs =
                        CollisionTraversal.RootBoundingSphere(
                            engine.DataCache,
                            cell);
                    string bsDesc = System.FormattableString.Invariant(
                        $"bs=({bs.Origin.X:F3},{bs.Origin.Y:F3},{bs.Origin.Z:F3}) br={bs.Radius:F3}");
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[other-cells] primary=0x{sp.CheckCellId:X8} iter=0x{cellId:X8} wpos=({footCenter.X:F3},{footCenter.Y:F3},{footCenter.Z:F3}) lpos=({localCenter.X:F3},{localCenter.Y:F3},{localCenter.Z:F3}) lprev=({localCurrCenter.X:F3},{localCurrCenter.Y:F3},{localCurrCenter.Z:F3}) r={sphereRadius:F3} {bsDesc} result={result} cn=({CollisionInfo.CollisionNormal.X:F2},{CollisionInfo.CollisionNormal.Y:F2},{CollisionInfo.CollisionNormal.Z:F2}) sn=({CollisionInfo.SlidingNormal.X:F2},{CollisionInfo.SlidingNormal.Y:F2},{CollisionInfo.SlidingNormal.Z:F2}) negHit={sp.NegPolyHit} negN=({sp.NegCollisionNormal.X:F2},{sp.NegCollisionNormal.Y:F2},{sp.NegCollisionNormal.Z:F2}) {polyDesc}"));
                }

                if (PhysicsDiagnostics.ProbeStepWalkEnabled
                    && sp.StepDown
                    && result == TransitionState.OK)
                {
                    LogNearestWalkableCandidate("other-cell", cellId, localCenter, sphereRadius, cell.Resolved);
                }

                if (PhysicsDiagnostics.ProbePushBackEnabled)
                {
                    PhysicsDiagnostics.LogPushBackCellTransit(
                        primaryCellId: sp.CheckCellId,
                        otherCellId: cellId,
                        bspResult: (int)result,
                        halted: false);
                }

                if (ApplyOtherCellResult(result, out var halted))
                    return halted;

                var objIndoorState = FindObjCollisionsInCell(engine, cellId);
                if (ApplyOtherCellResult(objIndoorState, out var objIndoorHalted))
                    return objIndoorHalted;
            }

            return TransitionState.OK;
        }
        finally
        {
            sp.OrderedCellScratch.Return(ordered);
        }
    }

    private void LogIssue98CellSetSummary(
        PhysicsEngine engine,
        uint containingCellId,
        System.Collections.Generic.IReadOnlyCollection<uint> cellSet,
        Vector3 footCenter,
        float sphereRadius)
    {
        if (!PhysicsDiagnostics.ProbeStepWalkEnabled || engine.DataCache is null)
            return;

        uint primary = SpherePath.CheckCellId;
        if ((primary & 0xFFFF0000u) != 0xA9B40000u)
            return;

        uint low = primary & 0xFFFFu;
        if (low < 0x0140u || low > 0x0148u)
            return;

        var ordered = new System.Collections.Generic.List<uint>(cellSet);
        ordered.Sort();
        string cells = string.Join(",", ordered.ConvertAll(id => $"0x{id:X8}"));

        Console.WriteLine(System.FormattableString.Invariant(
            $"[cell-set-summary] primary=0x{primary:X8} containing=0x{containingCellId:X8} has146={cellSet.Contains(0xA9B40146u)} has147={cellSet.Contains(0xA9B40147u)} foot=({footCenter.X:F4},{footCenter.Y:F4},{footCenter.Z:F4}) r={sphereRadius:F4} cells={cells}"));

        LogIssue98CellBspProbe(engine, 0xA9B40146u, footCenter, sphereRadius);
    }

    private void LogIssue98CellBspProbe(
        PhysicsEngine engine,
        uint cellId,
        Vector3 footCenter,
        float sphereRadius)
    {
        var cell = engine.DataCache?.GetCellStruct(cellId);
        if (cell?.CellBSP?.Root is null)
        {
            Console.WriteLine(System.FormattableString.Invariant(
                $"[cell-set-summary] target=0x{cellId:X8} unavailable"));
            return;
        }

        var local = Vector3.Transform(footCenter, cell.InverseWorldTransform);
        bool pointInside = BSPQuery.PointInsideCellBsp(cell.CellBSP.Root, local);
        bool sphereHit = BSPQuery.SphereIntersectsCellBsp(cell.CellBSP.Root, local, sphereRadius);
        Console.WriteLine(System.FormattableString.Invariant(
            $"[cell-set-summary] target=0x{cellId:X8} local=({local.X:F4},{local.Y:F4},{local.Z:F4}) pointInside={pointInside} sphereHit={sphereHit}"));

        if (cell.Resolved.Count > 0)
            LogNearestWalkableCandidate("issue98-target", cellId, local, sphereRadius, cell.Resolved);
    }

    private void LogNearestWalkableCandidate(
        string site,
        uint cellId,
        Vector3 localCenter,
        float sphereRadius,
        Dictionary<ushort, ResolvedPolygon> resolved)
    {
        var sp = SpherePath;

        ResolvedPolygon? nearest = null;
        ushort nearestId = 0;
        float nearestAbsDistance = float.MaxValue;
        float nearestSignedDistance = 0f;
        bool nearestInsideEdges = false;
        bool nearestOverlapsSphere = false;

        foreach (var kv in resolved)
        {
            var poly = kv.Value;
            float normalDotUp = Vector3.Dot(Vector3.UnitZ, poly.Plane.Normal);
            if (normalDotUp <= sp.WalkableAllowance)
                continue;

            float signedDistance = Vector3.Dot(poly.Plane.Normal, localCenter) + poly.Plane.D;
            float absDistance = MathF.Abs(signedDistance);
            if (absDistance >= nearestAbsDistance)
                continue;

            nearest = poly;
            nearestId = kv.Key;
            nearestAbsDistance = absDistance;
            nearestSignedDistance = signedDistance;
            nearestOverlapsSphere = absDistance <= sphereRadius - PhysicsGlobals.EPSILON;
            nearestInsideEdges = !BSPQuery.FindCrossedEdge(
                poly.Plane, poly.Vertices, localCenter, Vector3.UnitZ, out _);
        }

        if (nearest is null)
        {
            Console.WriteLine(System.FormattableString.Invariant(
                $"[walkable-nearest] site={site} cell=0x{cellId:X8} center=({localCenter.X:F4},{localCenter.Y:F4},{localCenter.Z:F4}) r={sphereRadius:F4} allowance={sp.WalkableAllowance:F4} none"));
            return;
        }

        float stepSearch = sp.StepDown ? sp.StepDownAmt * sp.WalkInterp : 0f;
        Console.WriteLine(System.FormattableString.Invariant(
            $"[walkable-nearest] site={site} cell=0x{cellId:X8} poly=0x{nearestId:X4} center=({localCenter.X:F4},{localCenter.Y:F4},{localCenter.Z:F4}) r={sphereRadius:F4} dist={nearestSignedDistance:F4} abs={nearestAbsDistance:F4} gap={nearestAbsDistance - sphereRadius:F4} insideEdges={nearestInsideEdges} overlapsSphere={nearestOverlapsSphere} n=({nearest.Plane.Normal.X:F4},{nearest.Plane.Normal.Y:F4},{nearest.Plane.Normal.Z:F4}) d={nearest.Plane.D:F4} allowance={sp.WalkableAllowance:F4} stepSearch={stepSearch:F4}"));

        LogIssue98WalkableCandidateDetail(
            site, cellId, nearestId, nearest, localCenter, sphereRadius, stepSearch);
    }

    private void LogIssue98WalkableCandidateDetail(
        string site,
        uint cellId,
        ushort polyId,
        ResolvedPolygon poly,
        Vector3 localCenter,
        float sphereRadius,
        float stepSearch)
    {
        if (!PhysicsDiagnostics.ProbeStepWalkEnabled)
            return;
        if (site != "other-cell" || cellId != 0xA9B40143u || polyId != 0x0004)
            return;
        if (localCenter.Z < -1.1f || localCenter.Z > 0.2f)
            return;

        var movement = -Vector3.UnitZ * stepSearch;
        float dpPos = Vector3.Dot(localCenter, poly.Plane.Normal) + poly.Plane.D;
        float dpMove = Vector3.Dot(movement, poly.Plane.Normal);
        bool parallel = dpMove <= PhysicsGlobals.EPSILON
            && dpMove >= -PhysicsGlobals.EPSILON;

        float dist = 0f;
        float iDist = 0f;
        float interp = SpherePath.WalkInterp;
        bool adjustWouldApply = false;

        if (!parallel)
        {
            dist = dpMove <= PhysicsGlobals.EPSILON
                ? dpPos - sphereRadius
                : -sphereRadius - dpPos;
            iDist = dist / dpMove;
            interp = (1f - iDist) * SpherePath.WalkInterp;
            adjustWouldApply = interp < SpherePath.WalkInterp && interp >= -0.5f;
        }

        var projected = localCenter - poly.Plane.Normal * dpPos;
        var adjusted = localCenter - movement * iDist;

        var verts = new System.Text.StringBuilder();
        for (int i = 0; i < poly.Vertices.Length; i++)
        {
            if (i > 0) verts.Append(',');
            var v = poly.Vertices[i];
            verts.Append(System.FormattableString.Invariant(
                $"({v.X:F4},{v.Y:F4},{v.Z:F4})"));
        }

        Console.WriteLine(System.FormattableString.Invariant(
            $"[issue98-walkable-detail] cell=0x{cellId:X8} poly=0x{polyId:X4} center=({localCenter.X:F4},{localCenter.Y:F4},{localCenter.Z:F4}) projected=({projected.X:F4},{projected.Y:F4},{projected.Z:F4}) adjusted=({adjusted.X:F4},{adjusted.Y:F4},{adjusted.Z:F4}) r={sphereRadius:F4} stepSearch={stepSearch:F4} walkInterp={SpherePath.WalkInterp:F4} dpPos={dpPos:F4} dpMove={dpMove:F4} dist={dist:F4} iDist={iDist:F4} interp={interp:F4} parallel={parallel} adjustWouldApply={adjustWouldApply} verts=[{verts}]"));
    }

    internal bool ApplyOtherCellResult(TransitionState result, out TransitionState finalState)
    {
        finalState = result;
        switch (result)
        {
            case TransitionState.Collided:
            case TransitionState.Adjusted:
                if ((ObjectInfo.State & ObjectInfoState.Contact) == 0)
                    CollisionInfo.CollidedWithEnvironment = true;
                return true;
            case TransitionState.Slid:
                CollisionInfo.ContactPlaneValid = false;
                CollisionInfo.ContactPlaneIsWater = false;
                return true;
            default:
                return false;
        }
    }

    internal TransitionState FindEnvCollisions(
        PhysicsEngine engine,
        uint primaryCellId)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;

        sp.ObstructionEthereal = false;

        Vector3 footCenter = sp.GlobalSphere[0].Origin;
        float sphereRadius = sp.GlobalSphere[0].Radius;

        uint cellLow = primaryCellId & 0xFFFFu;
        if (cellLow >= 0x0100 && engine.DataCache is not null)
        {
            var cellPhysics = engine.DataCache.GetCellStruct(primaryCellId);

            var restrictionState = ObjectInfo.CheckEntryRestrictions(
                cellPhysics?.RestrictionObj ?? 0,
                engine.Objects);
            if (restrictionState != TransitionState.OK)
            {
                if ((ObjectInfo.State & ObjectInfoState.Contact) == 0)
                    ci.CollidedWithEnvironment = true;
                return restrictionState;
            }

            if (cellPhysics is not null &&
                CollisionTraversal.HasPhysics(engine.DataCache!, cellPhysics))
            {
                var localCenter = Vector3.Transform(footCenter, cellPhysics.InverseWorldTransform);
                var localCurrCenter = Vector3.Transform(sp.GlobalCurrCenter[0].Origin, cellPhysics.InverseWorldTransform);

                // Second sphere (head) in local space, if present.
                bool hasLocalSphere1 = sp.NumSphere > 1;
                Vector3 localSphere1Center = hasLocalSphere1
                    ? Vector3.Transform(
                        sp.GlobalSphere[1].Origin,
                        cellPhysics.InverseWorldTransform)
                    : Vector3.Zero;
                float localSphere1Radius = hasLocalSphere1
                    ? sp.GlobalSphere[1].Radius
                    : 0f;

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                    PhysicsDiagnostics.LastBspHitPoly = null;

                Quaternion cellRotation;
                Vector3 cellOrigin;
                if (!Matrix4x4.Decompose(cellPhysics.WorldTransform, out _, out cellRotation, out cellOrigin))
                {
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[indoor-bsp] WARN cellPhysics.WorldTransform did not decompose cleanly for cell 0x{primaryCellId:X8} — falling back to identity rotation"));
                    cellRotation = Quaternion.Identity;
                    cellOrigin = cellPhysics.WorldTransform.Translation;
                }

                var cellState = CollisionTraversal.FindCollisions(
                    engine.DataCache!,
                    cellPhysics,
                    this,
                    localCenter,
                    sphereRadius,
                    hasLocalSphere1,
                    localSphere1Center,
                    localSphere1Radius,
                    localCurrCenter,
                    Vector3.UnitZ,  // local space Z is up
                    1.0f,
                    cellRotation,
                    engine,         // engine needed for Path 5 step-up
                    worldOrigin: cellOrigin);

                if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
                {
                    var hit = PhysicsDiagnostics.LastBspHitPoly;
                    string polyDesc = hit is null
                        ? "poly=n/a"
                        : System.FormattableString.Invariant(
                            $"n=({hit.Plane.Normal.X:F3},{hit.Plane.Normal.Y:F3},{hit.Plane.Normal.Z:F3}) sides={hit.SidesType}");
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[indoor-bsp] cell=0x{primaryCellId:X8} wpos=({footCenter.X:F3},{footCenter.Y:F3},{footCenter.Z:F3}) lpos=({localCenter.X:F3},{localCenter.Y:F3},{localCenter.Z:F3}) lprev=({localCurrCenter.X:F3},{localCurrCenter.Y:F3},{localCurrCenter.Z:F3}) r={sphereRadius:F3} result={cellState} ")
                        + polyDesc);
                }

                if (cellState != TransitionState.OK)
                {
                    if ((ObjectInfo.State & ObjectInfoState.Contact) == 0)
                        ci.CollidedWithEnvironment = true;
                    return cellState;
                }

                return TransitionState.OK;
            }
        }

        var terrainWalkable = engine.SampleTerrainWalkableInCell(
            primaryCellId,
            footCenter.X,
            footCenter.Y);
        if (terrainWalkable is not null)
        {
            var terrainState = ValidateWalkable(footCenter, sphereRadius, terrainWalkable.Value.Plane,
                                    terrainWalkable.Value.IsWater,
                                    terrainWalkable.Value.WaterDepth,
                                    cellId: terrainWalkable.Value.CellId,
                                    walkableVertices: terrainWalkable.Value.Vertices);
            if (terrainState != TransitionState.OK)
                return terrainState;
        }
        // else: no terrain loaded here — allow pass-through.

        return TransitionState.OK;
    }

    private TransitionState RunCheckOtherCellsAndAdvance(
        PhysicsEngine engine, Vector3 footCenter, float sphereRadius)
    {
        var sp = SpherePath;

        if (engine.DataCache is null)
        {
            uint resolvedOutdoorCellId = engine.ResolveCellId(
                sp.GlobalSphere[0].Origin,
                sphereRadius,
                sp.CheckCellId);
            if (resolvedOutdoorCellId != sp.CheckCellId)
                sp.SetCheckPos(sp.CheckPos, resolvedOutdoorCellId);
            return TransitionState.OK;
        }

        footCenter = sp.GlobalSphere[0].Origin;

        sp.HitsInteriorCell = false;

        uint containingCellId = CellTransit.FindCellSet(
            engine.DataCache,
            sp.GlobalSphere,
            sp.NumSphere,
            sp.CheckCellId,
            sp.CellCandidates,
            sp.CarriedBlockOrigin);
        CellArray cellSet = sp.CellCandidates;
        LogIssue98CellSetSummary(engine, containingCellId, cellSet, footCenter, sphereRadius);

        if ((sp.CheckCellId & 0xFFFFu) >= 0x0100u
            || (containingCellId & 0xFFFFu) >= 0x0100u)
        {
            sp.HitsInteriorCell = true;
        }
        else
        {
            for (int i = 0; i < cellSet.Count; i++)
            {
                uint id = cellSet.OrderedIds[i];
                if ((id & 0xFFFFu) >= 0x0100u) { sp.HitsInteriorCell = true; break; }
            }
        }

        var otherCellsState = CheckOtherCells(engine, footCenter, sphereRadius, cellSet);
        if (otherCellsState != TransitionState.OK)
            return otherCellsState;

        if (containingCellId != sp.CheckCellId)
        {
            if (sp.CarriedBlockOrigin is { } carriedOrigin
                && (sp.CheckCellId & 0xFFFFu) is >= 1u and <= 0x40u
                && (containingCellId & 0xFFFFu) is >= 1u and <= 0x40u)
            {
                int oldBlockX = (int)((sp.CheckCellId >> 24) & 0xFFu);
                int oldBlockY = (int)((sp.CheckCellId >> 16) & 0xFFu);
                int newBlockX = (int)((containingCellId >> 24) & 0xFFu);
                int newBlockY = (int)((containingCellId >> 16) & 0xFFu);
                sp.CarriedBlockOrigin = carriedOrigin + new Vector3(
                    (newBlockX - oldBlockX) * LandDefs.BlockLength,
                    (newBlockY - oldBlockY) * LandDefs.BlockLength,
                    0f);
            }

            sp.SetCheckPos(sp.CheckPos, containingCellId);
        }
        return TransitionState.OK;
    }

    private TransitionState ValidateWalkable(Vector3 sphereCenter, float sphereRadius,
                                              System.Numerics.Plane contactPlane,
                                              bool isWater, float waterDepth, uint cellId,
                                              TerrainTriangleVertices? walkableVertices = null)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        // Low point of the sphere.
        var lowPoint = sphereCenter - new Vector3(0f, 0f, sphereRadius);

        float dist = Vector3.Dot(lowPoint, contactPlane.Normal) + contactPlane.D + waterDepth;

        // ── Above or touching the surface ────────────────────────────────
        if (dist >= -PhysicsGlobals.EPSILON)
        {
            if (dist <= PhysicsGlobals.EPSILON)
            {
                bool walkableNormal = contactPlane.Normal.Z >= sp.WalkableAllowance;
                if (sp.StepDown || !oi.OnWalkable || walkableNormal)
                {
                    ci.SetContactPlane(contactPlane, cellId, isWater);
                    CacheWalkableContext(sp, contactPlane, walkableVertices);
                }

                bool restingGuardPassed = !oi.Contact && !sp.StepDown;
                if (restingGuardPassed)
                {
                    ci.SetCollisionNormal(contactPlane.Normal);
                    ci.CollidedWithEnvironment = true;
                }

                PhysicsDiagnostics.TraceTransitValidateWalkable(
                    oi.SelfEntityId, "resting", dist, waterDepth,
                    oi.Contact, sp.StepDown, restingGuardPassed,
                    contactPlane.Normal, TransitionState.OK);
            }
            else
            {
                PhysicsDiagnostics.TraceTransitValidateWalkable(
                    oi.SelfEntityId, "above", dist, waterDepth,
                    oi.Contact, sp.StepDown, guardPassed: null,
                    contactPlane.Normal, TransitionState.OK);
            }
            return TransitionState.OK;
        }

        // ── Below the surface ─────────────────────────────────────────────
        if (sp.CheckWalkable)
        {
            PhysicsDiagnostics.TraceTransitValidateWalkable(
                oi.SelfEntityId, "checkwalkable-fail", dist, waterDepth,
                oi.Contact, sp.StepDown, guardPassed: null,
                contactPlane.Normal, TransitionState.Collided);
            return TransitionState.Collided;  // walkable probe fails
        }

        float zDist = dist / contactPlane.Normal.Z;

        var result = TransitionState.OK;
        bool walkable = contactPlane.Normal.Z >= sp.WalkableAllowance;
        if (sp.StepDown || !oi.OnWalkable || walkable)
        {
            ci.SetContactPlane(contactPlane, cellId, isWater);
            CacheWalkableContext(sp, contactPlane, walkableVertices);

            if (sp.StepDown)
            {
                float interp = (1f - (-1f / (sp.StepDownAmt * sp.WalkInterp)) * zDist) * sp.WalkInterp;
                if (interp >= sp.WalkInterp || interp < -0.1f)
                {
                    PhysicsDiagnostics.TraceTransitValidateWalkable(
                        oi.SelfEntityId, "below-push", dist, waterDepth,
                        oi.Contact, sp.StepDown, guardPassed: null,
                        contactPlane.Normal, TransitionState.Collided);
                    return TransitionState.Collided;
                }
                sp.WalkInterp = interp;
            }

            // Push the sphere up out of the terrain.
            sp.AddOffsetToCheckPos(new Vector3(0f, 0f, -zDist));
            result = TransitionState.Adjusted;
        }

        bool belowPushGuardPassed = !oi.Contact && !sp.StepDown;
        if (belowPushGuardPassed)
        {
            ci.SetCollisionNormal(contactPlane.Normal);
            ci.CollidedWithEnvironment = true;
        }

        PhysicsDiagnostics.TraceTransitValidateWalkable(
            oi.SelfEntityId, "below-push", dist, waterDepth,
            oi.Contact, sp.StepDown, belowPushGuardPassed,
            contactPlane.Normal, result);

        return result;
    }

    private static void CacheWalkableContext(
        SpherePath spherePath,
        Plane contactPlane,
        TerrainTriangleVertices? walkableVertices)
    {
        if (walkableVertices is { } vertices
            && contactPlane.Normal.Z >= PhysicsGlobals.FloorZ)
        {
            spherePath.SetWalkable(contactPlane, in vertices, Vector3.UnitZ);
        }
    }


    private TransitionState FindObjCollisionsInCell(PhysicsEngine engine, uint cellId)
    {
        if (engine.DataCache is null) return TransitionState.OK;

        var objsInCell = engine.ShadowObjects.GetObjectsInCell(cellId);

        var sp = SpherePath;
        var oi = ObjectInfo;
        var ci = CollisionInfo;

        if (objsInCell.Count == 0)
        {
            return TransitionState.OK;
        }

        Vector3 checkPos = sp.GlobalSphere[0].Origin;

        // Landblock offsets feed the [resolve-bldg] probe only.
        engine.TryGetLandblockContext(checkPos.X, checkPos.Y,
            out _, out float worldOffsetX, out float worldOffsetY);

        using var nearbyObjs = ShadowEntrySnapshot.Capture(objsInCell);

        foreach (ShadowEntry obj in nearbyObjs.Entries)
        {
            if (oi.SelfEntityId != 0 && obj.EntityId == oi.SelfEntityId)
            {
                continue;
            }

            if (oi.MissileIgnore(obj.EntityId, obj.State, obj.Flags))
            {
                continue;
            }


            if (CollisionExemption.ShouldSkip(obj.State, obj.Flags, ObjectInfo.State))
            {
                continue;
            }

            bool etherealForTest = (obj.State & 0x4u) != 0
                                || (ObjectInfo.Ethereal && (obj.State & 0x1u) == 0);
            if (etherealForTest && sp.StepDown)
            {
                continue;
            }
            sp.ObstructionEthereal = etherealForTest;

            bool collisionWasValidPre = ci.CollisionNormalValid;

            if (PhysicsDiagnostics.ProbeBuildingEnabled || PhysicsDiagnostics.ProbeIndoorBspEnabled)
                PhysicsDiagnostics.LastBspHitPoly = null;

            TransitionState result;

            if (obj.CollisionType == ShadowCollisionType.BSP)
            {
                var physics = engine.DataCache.GetGfxObj(obj.GfxObjId);

                if (PhysicsDiagnostics.ProbeBuildingEnabled)
                {
                    Vector3 dxy = obj.Position - sp.GlobalCurrCenter[0].Origin;
                    float distXY = MathF.Sqrt(dxy.X * dxy.X + dxy.Y * dxy.Y);
                    bool cacheHit = physics is not null &&
                        CollisionTraversal.HasPhysics(engine.DataCache!, physics);
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[bsp-test] obj=0x{obj.EntityId:X8} gfx=0x{obj.GfxObjId:X8} state=0x{obj.State:X8} radius={obj.Radius:F3} pos=({obj.Position.X:F2},{obj.Position.Y:F2},{obj.Position.Z:F2}) distXY={distXY:F3} cacheHit={cacheHit}"));
                }

                if (physics is null ||
                    !CollisionTraversal.HasPhysics(engine.DataCache!, physics))
                {
                    sp.ObstructionEthereal = false;
                    continue;
                }

                var invRot = Quaternion.Inverse(obj.Rotation);
                float invScale = obj.Scale > 0 ? 1.0f / obj.Scale : 1.0f;

                Vector3 localSphere0Center =
                    Vector3.Transform(sp.GlobalSphere[0].Origin - obj.Position, invRot)
                    * invScale;
                float localSphere0Radius = sp.GlobalSphere[0].Radius * invScale;
                var localCurrCenter = Vector3.Transform(
                    sp.GlobalCurrCenter[0].Origin - obj.Position, invRot) * invScale;

                bool hasLocalSphere1 = sp.NumSphere > 1;
                Vector3 localSphere1Center = hasLocalSphere1
                    ? Vector3.Transform(
                        sp.GlobalSphere[1].Origin - obj.Position,
                        invRot) * invScale
                    : Vector3.Zero;
                float localSphere1Radius = hasLocalSphere1
                    ? sp.GlobalSphere[1].Radius * invScale
                    : 0f;

                // Local-space Z (up direction rotated into object space).
                var localSpaceZ = Vector3.Transform(Vector3.UnitZ, invRot);

                result = CollisionTraversal.FindCollisions(
                    engine.DataCache!,
                    physics,
                    this,
                    localSphere0Center,
                    localSphere0Radius,
                    hasLocalSphere1,
                    localSphere1Center,
                    localSphere1Radius,
                    localCurrCenter,
                    localSpaceZ,
                    obj.Scale,        // scale for local→world offsets
                    obj.Rotation,     // local→world rotation
                    engine,
                    worldOrigin: obj.Position);
            }
            else if (obj.CollisionType == ShadowCollisionType.Sphere)
            {
                if (BspOnlyDispatch(obj.State))
                {
                    if (PhysicsDiagnostics.ProbeBuildingEnabled)
                    {
                        Console.WriteLine(System.FormattableString.Invariant(
                            $"[sph-skip-bsp] obj=0x{obj.EntityId:X8} state=0x{obj.State:X8} — HAS_PHYSICS_BSP_PS dispatches BSP-only"));
                    }
                    continue;
                }

                bool isCreature = (obj.State & 0x40u) != 0
                    || (obj.Flags & EntityCollisionFlags.IsCreature) != 0;
                result = SphereCollision(obj, sp, engine, isCreature);
            }
            else
            {

                if (BspOnlyDispatch(obj.State))
                {
                    if (PhysicsDiagnostics.ProbeBuildingEnabled)
                    {
                        Console.WriteLine(System.FormattableString.Invariant(
                            $"[cyl-skip-bsp] obj=0x{obj.EntityId:X8} state=0x{obj.State:X8} — HAS_PHYSICS_BSP_PS dispatches BSP-only"));
                    }
                    continue;
                }

                result = CylinderCollision(obj, sp, engine);

                if (PhysicsDiagnostics.ProbeBuildingEnabled)
                {
                    Vector3 dxy = obj.Position - sp.GlobalCurrCenter[0].Origin;
                    float distXY = MathF.Sqrt(dxy.X * dxy.X + dxy.Y * dxy.Y);
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"[cyl-test] obj=0x{obj.EntityId:X8} state=0x{obj.State:X8} radius={obj.Radius:F3} height={obj.CylHeight:F3} pos=({obj.Position.X:F2},{obj.Position.Y:F2},{obj.Position.Z:F2}) distXY={distXY:F3} result={result}"));
                }
            }

            if (result != TransitionState.OK
                && sp.ObstructionEthereal
                && !sp.StepDown
                && (obj.State & 0x1u) == 0)
            {
                result = TransitionState.OK;
                ci.CollisionNormalValid = false;
            }

            bool attributed = result != TransitionState.OK
                || (!collisionWasValidPre && ci.CollisionNormalValid);
            if (attributed)
            {
                ci.CollideObjectGuids.Add(obj.EntityId);
                ci.LastCollidedObjectGuid = obj.EntityId;
            }

            if (sp.InsertType == InsertType.Placement
                && result == TransitionState.Collided
                && PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                var ciFmt = System.Globalization.CultureInfo.InvariantCulture;
                Console.WriteLine(string.Format(ciFmt,
                    "[place-fail-obj] entityId=0x{0:X8} gfxObjId=0x{1:X8} " +
                    "collisionType={2} position=({3:F4},{4:F4},{5:F4}) " +
                    "scale={6:F4} radius={7:F4}",
                    obj.EntityId, obj.GfxObjId,
                    obj.CollisionType,
                    obj.Position.X, obj.Position.Y, obj.Position.Z,
                    obj.Scale, obj.Radius));
            }

            if (attributed && PhysicsDiagnostics.ProbeBuildingEnabled)
            {
                uint partIdx = obj.EntityId & 0xFFu;
                uint entityIdProbe = obj.EntityId >> 8;
                var cachedPhys = engine.DataCache.GetGfxObj(obj.GfxObjId);
                var visBounds = engine.DataCache.GetVisualBounds(obj.GfxObjId);
                float bspR = cachedPhys?.BoundingSphere?.Radius ?? 0f;
                float vAabbR = visBounds?.Radius ?? 0f;
                bool hasPhys = cachedPhys is not null;
                var entOriginLb = obj.Position - new Vector3(worldOffsetX, worldOffsetY, 0f);

                var sb = new System.Text.StringBuilder(256);
                sb.Append(System.FormattableString.Invariant(
                    $"[resolve-bldg] obj=0x{obj.EntityId:X8} entityId=0x{entityIdProbe:X8} partIdx={partIdx}\n"));
                sb.Append(System.FormattableString.Invariant(
                    $"               gfxObj=0x{obj.GfxObjId:X8} hasPhys={hasPhys} bspR={bspR:F2} vAabbR={vAabbR:F2}\n"));
                sb.Append(System.FormattableString.Invariant(
                    $"               entOrigin_lb=({entOriginLb.X:F1},{entOriginLb.Y:F1},{entOriginLb.Z:F1})"));

                var poly = PhysicsDiagnostics.LastBspHitPoly;
                if (obj.CollisionType == ShadowCollisionType.Cylinder ||
                    obj.CollisionType == ShadowCollisionType.Sphere)
                {
                    sb.Append(System.FormattableString.Invariant(
                        $"\n               hitPoly: n/a ({obj.CollisionType.ToString().ToLowerInvariant()})"));
                }
                else if (poly is null)
                {
                    sb.Append("\n               hitPoly: n/a (BSP path — side-channel not written, missing BSPQuery wire site)");
                }
                else
                {
                    sb.Append(System.FormattableString.Invariant(
                        $"\n               hitPoly: numVerts={poly.NumPoints} plane=({poly.Plane.Normal.X:F3},{poly.Plane.Normal.Y:F3},{poly.Plane.Normal.Z:F3},{poly.Plane.D:F3})"));
                    int vMax = Math.Min(poly.Vertices.Length, 4);
                    for (int vi = 0; vi < vMax; vi++)
                    {
                        var vLocal = poly.Vertices[vi];
                        var vWorld = obj.Position + Vector3.Transform(vLocal * obj.Scale, obj.Rotation);
                        sb.Append(System.FormattableString.Invariant(
                            $"\n                        v{vi}_local=({vLocal.X,5:F2},{vLocal.Y,5:F2},{vLocal.Z,5:F2})  v{vi}_world=({vWorld.X,6:F2},{vWorld.Y,6:F2},{vWorld.Z,6:F2})"));
                    }
                    if (poly.Vertices.Length > 4)
                        sb.Append(System.FormattableString.Invariant(
                            $"\n                        ... ({poly.Vertices.Length - 4} more verts elided)"));
                }
                Console.WriteLine(sb.ToString());
            }

            sp.ObstructionEthereal = false;

            if (result != TransitionState.OK)
            {
                return result;
            }
        }

        return TransitionState.OK;
    }

    private TransitionState FindBuildingCollisions(PhysicsEngine engine, uint cellId)
    {
        if ((cellId & 0xFFFFu) >= 0x0100u) return TransitionState.OK;
        if (engine.DataCache is null) return TransitionState.OK;

        var building = engine.DataCache.GetBuilding(cellId);
        if (building is null || building.ModelId == 0u) return TransitionState.OK;

        var physics = engine.DataCache.GetGfxObj(building.ModelId);
        if (physics is null ||
            !CollisionTraversal.HasPhysics(engine.DataCache, physics))
        {
            return TransitionState.OK;
        }

        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        if (!Matrix4x4.Decompose(building.WorldTransform, out _,
                out Quaternion bldRotation, out Vector3 bldOrigin))
        {
            bldRotation = Quaternion.Identity;
            bldOrigin = building.WorldTransform.Translation;
        }

        var invRot = Quaternion.Inverse(bldRotation);
        Vector3 localSphere0Center =
            Vector3.Transform(sp.GlobalSphere[0].Origin - bldOrigin, invRot);
        float localSphere0Radius = sp.GlobalSphere[0].Radius;
        bool hasLocalSphere1 = sp.NumSphere > 1;
        Vector3 localSphere1Center = hasLocalSphere1
            ? Vector3.Transform(sp.GlobalSphere[1].Origin - bldOrigin, invRot)
            : Vector3.Zero;
        float localSphere1Radius = hasLocalSphere1
            ? sp.GlobalSphere[1].Radius
            : 0f;
        var localCurrCenter = Vector3.Transform(
            sp.GlobalCurrCenter[0].Origin - bldOrigin, invRot);
        var localSpaceZ = Vector3.Transform(Vector3.UnitZ, invRot);

        sp.BldgCheck = true;
        TransitionState result;
        try
        {
            result = CollisionTraversal.FindCollisions(
                engine.DataCache!,
                physics,
                this,
                localSphere0Center,
                localSphere0Radius,
                hasLocalSphere1,
                localSphere1Center,
                localSphere1Radius,
                localCurrCenter,
                localSpaceZ,
                1.0f,            // buildings are unscaled
                bldRotation,
                engine,
                worldOrigin: bldOrigin);
        }
        finally
        {
            sp.BldgCheck = false;
        }

        if (PhysicsDiagnostics.ProbeBuildingEnabled)
        {
            Console.WriteLine(System.FormattableString.Invariant(
                $"[bldg-channel] cell=0x{cellId:X8} model=0x{building.ModelId:X8} wpos=({sp.GlobalSphere[0].Origin.X:F3},{sp.GlobalSphere[0].Origin.Y:F3},{sp.GlobalSphere[0].Origin.Z:F3}) bldOrigin=({bldOrigin.X:F3},{bldOrigin.Y:F3},{bldOrigin.Z:F3}) hitsInterior={sp.HitsInteriorCell} result={result}"));
        }

        if (result != TransitionState.OK && !oi.Contact)
            ci.CollidedWithEnvironment = true;

        return result;
    }

    private TransitionState SphereCollision(ShadowEntry obj, SpherePath sp, PhysicsEngine engine, bool isCreature)
    {
        if (sp.ObstructionEthereal)
            return TransitionState.OK;

        var ci = CollisionInfo;
        var oi = ObjectInfo;
        var s0 = sp.GlobalSphere[0];
        Vector3 disp0 = s0.Origin - obj.Position;
        float radsum = s0.Radius + obj.Radius - PhysicsGlobals.EPSILON;

        bool hasHead = sp.NumSphere > 1;
        Vector3 disp1 = default;
        if (hasHead)
            disp1 = sp.GlobalSphere[1].Origin - obj.Position;

        if (sp.InsertType == InsertType.Placement)
        {
            if (SphereCollidesWithSphere(disp0, radsum))
                return TransitionState.Collided;
            if (hasHead && SphereCollidesWithSphere(disp1, radsum))
                return TransitionState.Collided;
            return TransitionState.OK;
        }

        if (sp.StepDown)
        {
            if (isCreature)
                return TransitionState.OK;
            return SphereStepSphereDown(obj, sp, disp0, radsum);
        }

        if (sp.CheckWalkable)
        {
            if (SphereCollidesWithSphere(disp0, radsum))
                return TransitionState.Collided;
            if (hasHead && SphereCollidesWithSphere(disp1, radsum))
                return TransitionState.Collided;
            return TransitionState.OK;
        }

        if (!sp.Collide)
        {
            if ((oi.State & (ObjectInfoState.Contact | ObjectInfoState.OnWalkable)) != 0)
            {
                // Grounded: foot hit → step over / slide; head hit → slide.
                if (SphereCollidesWithSphere(disp0, radsum))
                    return SphereStepSphereUp(obj, sp, engine, disp0, radsum);
                if (hasHead && SphereCollidesWithSphere(disp1, radsum))
                    return SphereSlideSphere(obj, sp, 1);
            }
            else if ((oi.State & ObjectInfoState.PathClipped) != 0)
            {
                if (SphereCollidesWithSphere(disp0, radsum))
                    return SphereCollideWithPoint(obj, sp, s0, radsum, 0);
            }
            else
            {
                // Airborne: foot hit → land on the sphere top; head hit → point hit.
                if (SphereCollidesWithSphere(disp0, radsum))
                    return SphereLandOnSphere(obj, sp);
                if (hasHead && SphereCollidesWithSphere(disp1, radsum))
                    return SphereCollideWithPoint(obj, sp, sp.GlobalSphere[1], radsum, 1);
            }
            return TransitionState.OK;
        }

        if (isCreature)
            return TransitionState.OK;   // §8.1

        bool hit0 = SphereCollidesWithSphere(disp0, radsum);
        if (!hit0 && !(hasHead && SphereCollidesWithSphere(disp1, radsum)))
            return TransitionState.OK;

        Vector3 movement = sp.GlobalCurrCenter[0].Origin - s0.Origin;
        float radsumEps = radsum + PhysicsGlobals.EPSILON;
        float lenSq = movement.LengthSquared();
        if (MathF.Abs(lenSq) < PhysicsGlobals.EPSILON)
            return TransitionState.Collided;
        float diff = -Vector3.Dot(movement, disp0);
        float disc = diff * diff - (disp0.LengthSquared() - radsumEps * radsumEps) * lenSq;
        if (disc < 0f)
            return TransitionState.Collided;
        float tt = MathF.Sqrt(disc) + diff;
        if (tt > 1f) tt = diff * 2f - tt;
        float time = tt / lenSq;
        float timecheck = (1f - time) * sp.WalkInterp;
        if (timecheck >= sp.WalkInterp || timecheck < -0.1f)
            return TransitionState.Collided;
        movement *= time;
        Vector3 dispN = (disp0 + movement) / radsumEps;
        if (dispN.Z <= sp.WalkableAllowance)   // !is_walkable_allowable — sphere top too steep to rest on
            return TransitionState.OK;
        Vector3 restPt = s0.Origin - dispN * s0.Radius;
        var contactPlane = new Plane(dispN, -Vector3.Dot(dispN, restPt));
        ci.SetContactPlane(contactPlane, sp.CheckCellId, isWater: true);
        sp.WalkInterp = timecheck;
        sp.AddOffsetToCheckPos(movement);
        return TransitionState.Adjusted;
    }

    private static bool SphereCollidesWithSphere(Vector3 disp, float radsum)
        => disp.LengthSquared() <= radsum * radsum;

    private TransitionState SphereStepSphereUp(ShadowEntry obj, SpherePath sp,
        PhysicsEngine engine, Vector3 disp0, float radsum)
    {
        float radsumEps = radsum + PhysicsGlobals.EPSILON;
        if (ObjectInfo.StepUpHeight < radsumEps - disp0.Z)
            return SphereSlideSphere(obj, sp, 0);

        Vector3 n = sp.GlobalCurrCenter[0].Origin - obj.Position;

        if (engine is not null && DoStepUp(n, engine))
            return TransitionState.OK;

        return sp.StepUpSlide(this);
    }

    private TransitionState SphereSlideSphere(ShadowEntry obj, SpherePath sp, int sphereNum)
    {
        Vector3 n = sp.GlobalCurrCenter[sphereNum].Origin - obj.Position;
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;
        return SlideSphere(n, sp.GlobalCurrCenter[sphereNum].Origin, sphereNum);
    }

    private TransitionState SphereLandOnSphere(ShadowEntry obj, SpherePath sp)
    {
        Vector3 n = sp.GlobalCurrCenter[0].Origin - obj.Position;
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;
        sp.SetCollide(n);
        sp.WalkableAllowance = PhysicsGlobals.LandingZ;
        return TransitionState.Adjusted;
    }

    private TransitionState SphereCollideWithPoint(ShadowEntry obj, SpherePath sp,
        Sphere checkSphere, float radsum, int sphereNum)
    {
        Vector3 gCenter = sp.GlobalCurrCenter[sphereNum].Origin;
        Vector3 globalOffset = gCenter - obj.Position;

        if ((ObjectInfo.State & ObjectInfoState.PerfectClip) == 0)
        {
            if (!NormalizeCheckSmall(ref globalOffset))
                CollisionInfo.SetCollisionNormal(globalOffset);
            return TransitionState.Collided;
        }

        PhysicsDiagnostics.RecordSpherePerfectClipTailReach(
            (ObjectInfo.State & ObjectInfoState.IsViewer) != 0);

        // PerfectClip exact time-of-impact reposition. Block offset = 0.
        Vector3 checkOffset = checkSphere.Origin - gCenter;
        double toi = FindSphereTimeOfCollision(checkOffset, globalOffset, radsum + PhysicsGlobals.EPSILON);
        if (toi < PhysicsGlobals.EPSILON || toi > 1.0)
            return TransitionState.Collided;
        Vector3 collisionOffset = checkOffset * (float)toi - checkOffset;
        Vector3 oldDisp = collisionOffset + checkSphere.Origin - obj.Position;
        CollisionInfo.SetCollisionNormal(oldDisp / radsum);
        sp.AddOffsetToCheckPos(oldDisp);
        return TransitionState.Adjusted;
    }

    private TransitionState SphereStepSphereDown(ShadowEntry obj, SpherePath sp,
        Vector3 disp0, float radsum)
    {
        bool hit = SphereCollidesWithSphere(disp0, radsum);
        if (!hit && sp.NumSphere > 1)
        {
            Vector3 disp1 = sp.GlobalSphere[1].Origin - obj.Position;
            hit = SphereCollidesWithSphere(disp1, radsum);
        }
        if (!hit)
            return TransitionState.OK;

        float stepDown = sp.StepDownAmt * sp.WalkInterp;
        if (MathF.Abs(stepDown) < PhysicsGlobals.EPSILON)
            return TransitionState.Collided;

        float radsumEps = radsum + PhysicsGlobals.EPSILON;
        float underRoot = radsumEps * radsumEps - (disp0.X * disp0.X + disp0.Y * disp0.Y);
        if (underRoot < 0f)
            return TransitionState.Collided;   // defensive: XY already outside radsum
        float val = MathF.Sqrt(underRoot);
        float scaledStep = (val - disp0.Z) / stepDown;
        float timecheck = (1f - scaledStep) * sp.WalkInterp;
        if (timecheck >= sp.WalkInterp || timecheck < -0.1f)
            return TransitionState.Collided;

        float interp = stepDown * scaledStep;
        Vector3 dispN = new Vector3(disp0.X, disp0.Y, disp0.Z + interp) / radsumEps;
        if (dispN.Z <= sp.WalkableAllowance)
            return TransitionState.OK;

        Vector3 restPt = obj.Position + dispN * obj.Radius;
        var restPlane = new Plane(dispN, -Vector3.Dot(dispN, restPt));
        CollisionInfo.SetContactPlane(restPlane, sp.CheckCellId, isWater: true);
        sp.WalkInterp = timecheck;
        sp.AddOffsetToCheckPos(new Vector3(0f, 0f, interp));
        return TransitionState.Adjusted;
    }

    private static double FindSphereTimeOfCollision(Vector3 movement, Vector3 spherePos, float radSum)
    {
        float distSq = movement.LengthSquared();
        if (distSq < PhysicsGlobals.EPSILON) return -1;
        float nonCollide = spherePos.LengthSquared() - radSum * radSum;
        if (nonCollide < PhysicsGlobals.EPSILON) return -1;
        float similar = -Vector3.Dot(spherePos, movement);
        double nonCollideB = (double)similar * similar - (double)nonCollide * distSq;
        if (nonCollideB < 0) return -1;
        double cDist = Math.Sqrt(nonCollideB);
        if (similar - cDist < 0)
            return -1 * (cDist + similar) / distSq;
        return -1 * (similar - cDist) / distSq;
    }

    private TransitionState CylinderCollision(ShadowEntry obj, SpherePath sp, PhysicsEngine engine)
    {
        // Degenerate dat heights: registration sites apply the same fallback;
        // kept for entries registered before it (pre-dates this port).
        float cylHeight = obj.CylHeight > 0f ? obj.CylHeight : obj.Radius * 4f;

        var s0 = sp.GlobalSphere[0];
        Vector3 disp0 = s0.Origin - obj.Position;
        float radsum = obj.Radius - PhysicsGlobals.EPSILON + s0.Radius;

        bool hasHead = sp.NumSphere > 1;
        Vector3 disp1 = default;
        float headRadius = 0f;
        if (hasHead)
        {
            disp1 = sp.GlobalSphere[1].Origin - obj.Position;
            headRadius = sp.GlobalSphere[1].Radius;
        }

        if (sp.InsertType == InsertType.Placement || sp.ObstructionEthereal)
        {
            if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius))
                return TransitionState.Collided;
            if (hasHead && CylCollidesWithSphere(disp1, radsum, cylHeight, headRadius))
                return TransitionState.Collided;
            return TransitionState.OK;
        }

        if (sp.StepDown)
            return CylStepSphereDown(obj, sp, cylHeight, disp0, radsum);

        if (sp.CheckWalkable)
        {
            if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius))
                return TransitionState.Collided;
            if (hasHead && CylCollidesWithSphere(disp1, radsum, cylHeight, headRadius))
                return TransitionState.Collided;
            return TransitionState.OK;
        }

        var oi = ObjectInfo;

        if (!sp.Collide)
        {
            if ((oi.State & (ObjectInfoState.Contact | ObjectInfoState.OnWalkable)) != 0)
            {
                // Grounded mover: foot hit → step over / onto; head hit → slide.
                if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius))
                    return CylStepSphereUp(obj, sp, engine, cylHeight, disp0, radsum);
                if (hasHead && CylCollidesWithSphere(disp1, radsum, cylHeight, headRadius))
                    return CylSlideSphere(obj, sp, cylHeight, disp1, radsum, 1);
            }
            else if ((oi.State & ObjectInfoState.PathClipped) != 0)
            {
                if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius))
                    return CylCollideWithPoint(obj, sp, cylHeight, s0, disp0, radsum, 0);
            }
            else
            {
                // Airborne: foot hit → land on the top; head hit → point hit.
                if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius))
                    return CylLandOnCylinder(obj, sp, cylHeight, disp0, radsum);
                if (hasHead && CylCollidesWithSphere(disp1, radsum, cylHeight, headRadius))
                    return CylCollideWithPoint(obj, sp, cylHeight, sp.GlobalSphere[1], disp1, radsum, 1);
            }
            return TransitionState.OK;
        }

        if (CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius)
            || (hasHead && CylCollidesWithSphere(disp1, radsum, cylHeight, headRadius)))
        {
            Vector3 movement = sp.GlobalCurrCenter[0].Origin - s0.Origin;
            if (MathF.Abs(movement.Z) < PhysicsGlobals.EPSILON)
                return TransitionState.Collided;

            float timecheck = (cylHeight + s0.Radius - disp0.Z) / movement.Z;
            Vector3 offset = movement * timecheck;

            Vector3 sum = offset + disp0;
            if (radsum * radsum < sum.X * sum.X + sum.Y * sum.Y)
                return TransitionState.OK;   // rewound point is off the cap — not a top landing

            float t = (1f - timecheck) * sp.WalkInterp;
            if (t >= sp.WalkInterp || t < -0.1f)
                return TransitionState.Collided;

            Vector3 pt = s0.Origin + offset;
            pt.Z -= s0.Radius;
            var contactPlane = new Plane(Vector3.UnitZ, -pt.Z);
            CollisionInfo.SetContactPlane(contactPlane, sp.CheckCellId, isWater: true);
            sp.WalkInterp = t;
            sp.AddOffsetToCheckPos(offset);
            return TransitionState.Adjusted;
        }

        return TransitionState.OK;
    }

    private static bool CylCollidesWithSphere(Vector3 disp, float radsum, float cylHeight, float sphereRadius)
    {
        if (disp.X * disp.X + disp.Y * disp.Y <= radsum * radsum)
        {
            float halfH = cylHeight * 0.5f;
            if (sphereRadius - PhysicsGlobals.EPSILON + halfH >= MathF.Abs(halfH - disp.Z))
                return true;
        }
        return false;
    }

    private bool CylNormalOfCollision(ShadowEntry obj, SpherePath sp, float cylHeight,
        Vector3 dispCheck, float radsum, float sphereRadius, int sphereNum, out Vector3 normal)
    {
        Vector3 dispCurr = sp.GlobalCurrCenter[sphereNum].Origin - obj.Position;
        if (radsum * radsum < dispCurr.X * dispCurr.X + dispCurr.Y * dispCurr.Y)
        {
            normal = new Vector3(dispCurr.X, dispCurr.Y, 0f);
            float halfH = cylHeight * 0.5f;
            bool zBandOverlapAtCurr =
                sphereRadius - PhysicsGlobals.EPSILON + halfH >= MathF.Abs(halfH - dispCurr.Z);
            bool noZMovement = MathF.Abs(dispCurr.Z - dispCheck.Z) <= PhysicsGlobals.EPSILON;
            return zBandOverlapAtCurr || noZMovement;
        }
        normal = new Vector3(0f, 0f, dispCheck.Z - dispCurr.Z <= 0f ? 1f : -1f);
        return true;
    }

    private static bool NormalizeCheckSmall(ref Vector3 v)
    {
        float mag = v.Length();
        if (mag < PhysicsGlobals.EPSILON)
            return true;
        v /= mag;
        return false;
    }

    private TransitionState CylStepSphereUp(ShadowEntry obj, SpherePath sp, PhysicsEngine engine,
        float cylHeight, Vector3 disp0, float radsum)
    {
        var s0 = sp.GlobalSphere[0];

        if (ObjectInfo.StepUpHeight < s0.Radius + cylHeight - disp0.Z)
            return CylSlideSphere(obj, sp, cylHeight, disp0, radsum, 0);

        CylNormalOfCollision(obj, sp, cylHeight, disp0, radsum, s0.Radius, 0, out var n);
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;

        var nWorld = Vector3.Transform(n, obj.Rotation);

        if (engine is not null && DoStepUp(nWorld, engine))
            return TransitionState.OK;

        return sp.StepUpSlide(this);
    }

    private TransitionState CylStepSphereDown(ShadowEntry obj, SpherePath sp,
        float cylHeight, Vector3 disp0, float radsum)
    {
        var s0 = sp.GlobalSphere[0];

        bool hit = CylCollidesWithSphere(disp0, radsum, cylHeight, s0.Radius);
        if (!hit && sp.NumSphere > 1)
        {
            Vector3 disp1 = sp.GlobalSphere[1].Origin - obj.Position;
            hit = CylCollidesWithSphere(disp1, radsum, cylHeight, sp.GlobalSphere[1].Radius);
        }
        if (!hit)
            return TransitionState.OK;

        float stepScale = sp.StepDownAmt * sp.WalkInterp;
        if (MathF.Abs(stepScale) < PhysicsGlobals.EPSILON)
            return TransitionState.Collided;

        float deltaZ = cylHeight + s0.Radius - disp0.Z;
        float interp = (1f - deltaZ / stepScale) * sp.WalkInterp;
        if (interp >= sp.WalkInterp || interp < -0.1f)
            return TransitionState.Collided;

        float topZ = s0.Origin.Z + deltaZ - s0.Radius;
        var contactPlane = new Plane(Vector3.UnitZ, -topZ);
        CollisionInfo.SetContactPlane(contactPlane, sp.CheckCellId, isWater: true);
        sp.WalkInterp = interp;
        sp.AddOffsetToCheckPos(new Vector3(0f, 0f, deltaZ));
        return TransitionState.Adjusted;
    }

    private TransitionState CylSlideSphere(ShadowEntry obj, SpherePath sp,
        float cylHeight, Vector3 disp, float radsum, int sphereNum)
    {
        CylNormalOfCollision(obj, sp, cylHeight, disp, radsum,
            sp.GlobalSphere[sphereNum].Radius, sphereNum, out var n);
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;

        return SlideSphere(n, sp.GlobalCurrCenter[sphereNum].Origin, sphereNum);
    }

    private TransitionState CylLandOnCylinder(ShadowEntry obj, SpherePath sp,
        float cylHeight, Vector3 disp0, float radsum)
    {
        CylNormalOfCollision(obj, sp, cylHeight, disp0, radsum,
            sp.GlobalSphere[0].Radius, 0, out var n);
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;

        sp.SetCollide(n);
        sp.WalkableAllowance = PhysicsGlobals.LandingZ;
        return TransitionState.Adjusted;
    }

    private TransitionState CylCollideWithPoint(ShadowEntry obj, SpherePath sp,
        float cylHeight, Sphere checkSphere, Vector3 disp, float radsum, int sphereNum)
    {
        bool definite = CylNormalOfCollision(obj, sp, cylHeight, disp, radsum,
            checkSphere.Radius, sphereNum, out var n);
        if (NormalizeCheckSmall(ref n))
            return TransitionState.Collided;

        if ((ObjectInfo.State & ObjectInfoState.PerfectClip) == 0)
        {
            CollisionInfo.SetCollisionNormal(n);
            return TransitionState.Collided;
        }

        PhysicsDiagnostics.RecordCylPerfectClipTailReach(
            (ObjectInfo.State & ObjectInfoState.IsViewer) != 0);

        Vector3 globCenter = sp.GlobalCurrCenter[0].Origin;
        Vector3 movement = checkSphere.Origin - globCenter;
        Vector3 oldDisp = globCenter - obj.Position;
        float radsumEps = radsum + PhysicsGlobals.EPSILON;

        float xyMoveLenSq = movement.X * movement.X + movement.Y * movement.Y;
        float dot2d = movement.X * oldDisp.X + movement.Y * oldDisp.Y;
        float xyDiff = -dot2d;
        float oldDispXYSq = oldDisp.X * oldDisp.X + oldDisp.Y * oldDisp.Y;
        float diffSq = xyDiff * xyDiff - (oldDispXYSq - radsumEps * radsumEps) * xyMoveLenSq;

        float time;
        Vector3 scaledMovement;

        if (!definite)
        {
            if (MathF.Abs(movement.Z) < PhysicsGlobals.EPSILON)
                return TransitionState.Collided;
            if (movement.Z > 0f)
            {
                n = new Vector3(0f, 0f, -1f);
                time = (movement.Z + checkSphere.Radius) / movement.Z * -1f;
            }
            else
            {
                n = new Vector3(0f, 0f, 1f);
                time = (checkSphere.Radius + cylHeight - movement.Z) / movement.Z;
            }
            scaledMovement = movement * time;

            Vector3 landed = scaledMovement + oldDisp;
            if (landed.X * landed.X + landed.Y * landed.Y >= radsumEps * radsumEps)
            {
                if (MathF.Abs(xyMoveLenSq) < PhysicsGlobals.EPSILON)
                    return TransitionState.Collided;
                if (diffSq >= 0f && xyMoveLenSq > PhysicsGlobals.EPSILON)
                {
                    float diff = MathF.Sqrt(diffSq);
                    time = xyDiff - diff < 0f
                        ? (diff - dot2d) / xyMoveLenSq
                        : (xyDiff - diff) / xyMoveLenSq;
                    scaledMovement = movement * time;
                }
                n = (scaledMovement + globCenter - obj.Position) / radsumEps;
                n.Z = 0f;
            }

            if (time < 0f || time > 1f)
                return TransitionState.Collided;

            Vector3 offsetOut = globCenter - scaledMovement - checkSphere.Origin;
            sp.AddOffsetToCheckPos(offsetOut);
            CollisionInfo.SetCollisionNormal(n);
            return TransitionState.Adjusted;
        }

        if (n.Z != 0f)
        {
            if (MathF.Abs(movement.Z) < PhysicsGlobals.EPSILON)
                return TransitionState.Collided;

            time = movement.Z > 0f
                ? -((oldDisp.Z + checkSphere.Radius) / movement.Z)
                : (checkSphere.Radius + cylHeight - oldDisp.Z) / movement.Z;
            scaledMovement = movement * time;

            if (time < 0f || time > 1f)
                return TransitionState.Collided;

            Vector3 offsetOut = globCenter + scaledMovement - checkSphere.Origin;
            sp.AddOffsetToCheckPos(offsetOut);
            CollisionInfo.SetCollisionNormal(n);
            return TransitionState.Adjusted;
        }

        if (diffSq < 0f || xyMoveLenSq < PhysicsGlobals.EPSILON)
            return TransitionState.Collided;

        {
            float diff = MathF.Sqrt(diffSq);
            time = xyDiff - diff < 0f
                ? (diff - dot2d) / xyMoveLenSq
                : (xyDiff - diff) / xyMoveLenSq;
            scaledMovement = movement * time;

            if (time < 0f || time > 1f)
                return TransitionState.Collided;

            n = (scaledMovement + globCenter - obj.Position) / radsumEps;
            n.Z = 0f;

            Vector3 offsetOut = globCenter + scaledMovement - checkSphere.Origin;
            sp.AddOffsetToCheckPos(offsetOut);
            CollisionInfo.SetCollisionNormal(n);
            return TransitionState.Adjusted;
        }
    }

    // -----------------------------------------------------------------------
    // SlideSphere — wall slide projection
    // -----------------------------------------------------------------------

    internal TransitionState SlideSphereInternal(Vector3 collisionNormal, Vector3 currPos)
        => SlideSphere(collisionNormal, currPos);

    private TransitionState SlideSphere(Vector3 collisionNormal, Vector3 currPos, int sphereNum = 0)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;

        if (collisionNormal.LengthSquared() < PhysicsGlobals.EpsilonSq)
        {
            Vector3 halfOffset = (currPos - sp.GlobalSphere[sphereNum].Origin) * 0.5f;
            sp.AddOffsetToCheckPos(halfOffset);
            return TransitionState.Adjusted;
        }

        ci.SetCollisionNormal(collisionNormal);

        Vector3 gDelta = sp.GlobalSphere[sphereNum].Origin - currPos;

        System.Numerics.Plane contactPlane;
        if (ci.ContactPlaneValid)
            contactPlane = ci.ContactPlane;
        else if (ci.LastKnownContactPlaneValid)
            contactPlane = ci.LastKnownContactPlane;
        else
        {
            float diff = Vector3.Dot(collisionNormal, gDelta);
            Vector3 offset = -collisionNormal * diff;
            sp.AddOffsetToCheckPos(offset);
            return TransitionState.Slid;
        }

        Vector3 direction = Vector3.Cross(collisionNormal, contactPlane.Normal);
        float dirLenSq = direction.LengthSquared();

        if (dirLenSq >= PhysicsGlobals.EPSILON)
        {
            float diff = Vector3.Dot(direction, gDelta);
            float invDirLenSq = 1f / dirLenSq;
            Vector3 offset = direction * diff * invDirLenSq;

            if (offset.LengthSquared() < PhysicsGlobals.EPSILON)
                return TransitionState.Collided;

            offset -= gDelta;
            sp.AddOffsetToCheckPos(offset);
            return TransitionState.Slid;
        }

        if (Vector3.Dot(collisionNormal, contactPlane.Normal) >= 0f)
        {
            float diff = Vector3.Dot(collisionNormal, gDelta);
            Vector3 offset = -collisionNormal * diff;
            sp.AddOffsetToCheckPos(offset);
            return TransitionState.Slid;
        }

        Vector3 reversed = -gDelta;
        if (reversed.LengthSquared() > PhysicsGlobals.EpsilonSq)
        {
            reversed = Vector3.Normalize(reversed);
            ci.SetCollisionNormal(reversed);
        }
        return TransitionState.Collided;
    }


    internal Vector3 AdjustOffset(Vector3 offset)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;

        Vector3 result = offset;
        bool checkSlide = false;
        string branch = "init";

        float slidingAngle = Vector3.Dot(result, ci.SlidingNormal);
        if (ci.SlidingNormalValid)
        {
            if (slidingAngle < 0f)
                checkSlide = true;
            else
                ci.SlidingNormalValid = false;
        }

        if (!ci.ContactPlaneValid)
        {
            if (checkSlide)
            {
                result -= ci.SlidingNormal * slidingAngle;
                branch = "no-cp-slide";
            }
            else
            {
                branch = "no-cp";
            }

            if (PhysicsDiagnostics.ProbeStepWalkEnabled)
                PhysicsDiagnostics.LogStepWalkAdjust(
                    branch, offset, result,
                    contactPlane: null,
                    slidingValid: ci.SlidingNormalValid,
                    slidingNormal: ci.SlidingNormal,
                    collisionAngle: 0f,
                    walkInterp: sp.WalkInterp);

            PhysicsDiagnostics.TraceTransitAdjustOffset(
                ObjectInfo.SelfEntityId, branch, offset, result);

            return result;
        }

        float collisionAngle = Vector3.Dot(result, ci.ContactPlane.Normal);
        Vector3 slideOffset = Vector3.Cross(ci.ContactPlane.Normal, ci.SlidingNormal);

        if (checkSlide)
        {
            float slideLen = slideOffset.Length();
            if (slideLen < PhysicsGlobals.EPSILON)
            {
                result = Vector3.Zero;
                branch = "slide-degenerate";
            }
            else
            {
                slideOffset /= slideLen;
                result = Vector3.Dot(slideOffset, result) * slideOffset;
                branch = "slide-crease";
            }
        }
        else if (collisionAngle <= 0f)
        {
            result -= ci.ContactPlane.Normal * collisionAngle;
            branch = "into-plane";
        }
        else
        {
            Vector3 n = ci.ContactPlane.Normal;
            if (MathF.Abs(n.Z) > PhysicsGlobals.EPSILON)
                result.Z = -(result.X * n.X + result.Y * n.Y) / n.Z;
            branch = "away-plane";
        }

        if (ci.ContactPlaneCellId != 0 && !ci.ContactPlaneIsWater)
        {
            Vector3 globCenter = sp.GlobalSphere[0].Origin;
            float radius = sp.GlobalSphere[0].Radius;

            float dist = Vector3.Dot(globCenter, ci.ContactPlane.Normal)
                         + ci.ContactPlane.D;

            if (dist < radius - PhysicsGlobals.EPSILON)
            {
                // Sphere is penetrating below the bare-radius (tangent)
                // threshold — push up along +Z to restore it to tangent
                // equilibrium.
                float zDist = (radius - dist) / ci.ContactPlane.Normal.Z;
                if (radius > MathF.Abs(zDist))
                {
                    sp.AddOffsetToCheckPos(new Vector3(0f, 0f, zDist));
                    branch += "+safety-push";
                }
            }
        }

        if (PhysicsDiagnostics.ProbeStepWalkEnabled)
            PhysicsDiagnostics.LogStepWalkAdjust(
                branch, offset, result,
                contactPlane: ci.ContactPlane,
                slidingValid: ci.SlidingNormalValid,
                slidingNormal: ci.SlidingNormal,
                collisionAngle: collisionAngle,
                walkInterp: sp.WalkInterp);

        PhysicsDiagnostics.TraceTransitAdjustOffset(
            ObjectInfo.SelfEntityId, branch, offset, result);

        return result;
    }

    // -----------------------------------------------------------------------
    // Step-down
    // -----------------------------------------------------------------------

    private bool DoStepDown(float stepDownHeight, float walkableZ, PhysicsEngine engine)
    {
        var sp = SpherePath;

        sp.NegPolyHit = false;
        sp.StepDown = true;
        sp.StepDownAmt = stepDownHeight;
        sp.WalkInterp = 1.0f;

        bool stepWalkProbe = PhysicsDiagnostics.ProbeStepWalkEnabled;
        if (stepWalkProbe)
        {
            PhysicsDiagnostics.LogStepWalk(
                "stepdown-enter", -1, 0, sp, CollisionInfo, ObjectInfo,
                Vector3.Zero, Vector3.Zero,
                detail: $"height={stepDownHeight:F4} walkableZ={walkableZ:F4}");
        }

        // If NOT in step-up mode, apply the downward offset.
        if (!sp.StepUp)
        {
            var downOffset = new Vector3(0f, 0f, -stepDownHeight);
            sp.AddOffsetToCheckPos(downOffset);

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "stepdown-after-offset", -1, 0, sp, CollisionInfo, ObjectInfo,
                    downOffset, downOffset,
                    detail: $"height={stepDownHeight:F4} walkableZ={walkableZ:F4}");
            }
        }

        var transitState = TransitionalInsert(5, engine);

        if (stepWalkProbe)
        {
            PhysicsDiagnostics.LogStepWalk(
                "stepdown-after-insert", -1, 0, sp, CollisionInfo, ObjectInfo,
                Vector3.Zero, Vector3.Zero,
                transitState,
                $"height={stepDownHeight:F4} walkableZ={walkableZ:F4}");
        }

        sp.StepDown = false;

        if (PhysicsDiagnostics.ProbeIndoorBspEnabled)
        {
            var cpn = CollisionInfo.ContactPlane.Normal;
            bool accept = transitState == TransitionState.OK
                          && CollisionInfo.ContactPlaneValid
                          && cpn.Z >= walkableZ;
            Console.WriteLine(System.FormattableString.Invariant(
                $"[stepdown-decide] cell=0x{sp.CheckCellId:X8} insert={transitState} cpValid={CollisionInfo.ContactPlaneValid} cpNz={cpn.Z:F3} walkableZ={walkableZ:F3} walkInterp={sp.WalkInterp:F3} accept={accept} pos=({sp.CheckPos.X:F3},{sp.CheckPos.Y:F3},{sp.CheckPos.Z:F3})"));
        }

        if (transitState == TransitionState.OK
            && CollisionInfo.ContactPlaneValid
            && CollisionInfo.ContactPlane.Normal.Z >= walkableZ)
        {
            if (ObjectInfo.EdgeSlide
                && !sp.StepUp
                && !DoCheckWalkable(walkableZ, engine))
            {
                return false;
            }

            var savedInsert = sp.InsertType;
            float winterpBeforePlacement = sp.WalkInterp;
            sp.InsertType = InsertType.Placement;

            var placeState = TransitionalInsert(1, engine);

            sp.InsertType = savedInsert;

            if (placeState != TransitionState.OK
                && PhysicsDiagnostics.ProbePlacementFailEnabled)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                Console.WriteLine(string.Format(ci,
                    "[place-fail] source=DoStepDown returned={0} " +
                    "sphere=({1:F4},{2:F4},{3:F4}) cell=0x{4:X8} " +
                    "stepDownHeight={5:F4} walkableZ={6:F4} " +
                    "winterpBefore={7:F4} " +
                    "contactPlane.Nz={8:F4} contactPlaneValid={9} spStepUp={10}",
                    placeState,
                    sp.CheckPos.X, sp.CheckPos.Y, sp.CheckPos.Z,
                    sp.CheckCellId,
                    stepDownHeight, walkableZ,
                    winterpBeforePlacement,
                    CollisionInfo.ContactPlane.Normal.Z,
                    CollisionInfo.ContactPlaneValid,
                    sp.StepUp));
            }

            if (stepWalkProbe)
            {
                PhysicsDiagnostics.LogStepWalk(
                    "stepdown-after-placement", -1, 0, sp, CollisionInfo, ObjectInfo,
                    Vector3.Zero, Vector3.Zero,
                    placeState,
                    $"height={stepDownHeight:F4} walkableZ={walkableZ:F4} winterpBeforePlacement={winterpBeforePlacement:F4}");
            }

            return placeState == TransitionState.OK;
        }

        if (stepWalkProbe)
        {
            PhysicsDiagnostics.LogStepWalk(
                "stepdown-reject", -1, 0, sp, CollisionInfo, ObjectInfo,
                Vector3.Zero, Vector3.Zero,
                transitState,
                $"height={stepDownHeight:F4} walkableZ={walkableZ:F4}");
        }

        return false;
    }

    internal bool DoStepDownForTest(
        float stepDownHeight,
        float walkableZ,
        PhysicsEngine engine)
        => DoStepDown(stepDownHeight, walkableZ, engine);

    // -----------------------------------------------------------------------
    // Step-up
    // -----------------------------------------------------------------------

    internal bool DoStepUp(Vector3 collisionNormal, PhysicsEngine engine)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        PhysicsDiagnostics.TraceTransitStepUp(
            oi.SelfEntityId, "enter", collisionNormal,
            onWalkable: (oi.State & ObjectInfoState.OnWalkable) != 0,
            stepUpHeight: oi.StepUpHeight,
            pos: sp.CurPos,
            succeeded: null,
            landedNormal: null);

        bool savedCpValid = ci.ContactPlaneValid;
        Plane savedCp = ci.ContactPlane;
        uint savedCpCellId = ci.ContactPlaneCellId;
        bool savedCpIsWater = ci.ContactPlaneIsWater;

        ci.ContactPlaneValid = false;
        ci.ContactPlaneIsWater = false;

        sp.StepUp = true;
        sp.StepUpNormal = collisionNormal;

        // Default values (not on walkable): small step, LandingZ threshold.
        float stepDownHeight = 0.04f;
        float zLandingValue = PhysicsGlobals.LandingZ;

        if ((oi.State & ObjectInfoState.OnWalkable) != 0)
        {
            zLandingValue = oi.GetWalkableZ();
            stepDownHeight = oi.StepUpHeight;
        }

        sp.WalkableAllowance = zLandingValue;
        sp.SaveCheckPos();

        bool stepDown = DoStepDown(stepDownHeight, zLandingValue, engine);

        sp.StepUp = false;
        sp.ClearWalkable();

        PhysicsDiagnostics.TraceTransitStepUp(
            oi.SelfEntityId, "exit", collisionNormal,
            onWalkable: (oi.State & ObjectInfoState.OnWalkable) != 0,
            stepUpHeight: oi.StepUpHeight,
            pos: sp.CheckPos,
            succeeded: stepDown,
            landedNormal: stepDown && ci.ContactPlaneValid
                ? ci.ContactPlane.Normal
                : null);

        if (!stepDown)
        {
            sp.RestoreCheckPos();

            if (savedCpValid)
            {
                ci.ContactPlane = savedCp;
                ci.ContactPlaneValid = true;
                ci.ContactPlaneCellId = savedCpCellId;
                ci.ContactPlaneIsWater = savedCpIsWater;
            }
        }

        return stepDown;
    }


    internal bool DoCheckWalkable(float zCheck, PhysicsEngine engine)
    {
        var sp = SpherePath;
        var oi = ObjectInfo;

        if ((oi.State & ObjectInfoState.OnWalkable) == 0)
            return true;

        if (sp.CheckWalkables())
            return true;

        Vector3 savedCheckPos = sp.CheckPos;
        uint savedCheckCellId = sp.CheckCellId;
        Vector3 savedBackupCheckPos = sp.BackupCheckPos;
        uint savedBackupCheckCellId = sp.BackupCheckCellId;

        float stepHeight = oi.StepDownHeight;
        var globSphere = sp.GlobalSphere[0];

        if (sp.NumSphere < 2 && stepHeight > globSphere.Radius * 2f)
            stepHeight = globSphere.Radius * 0.5f;

        if (stepHeight > globSphere.Radius * 2f)
            stepHeight *= 0.5f;

        sp.WalkableAllowance = zCheck;
        sp.CheckWalkable = true;
        sp.AddOffsetToCheckPos(new Vector3(0f, 0f, -stepHeight));

        var transitState = TransitionalInsert(1, engine);

        sp.CheckWalkable = false;
        sp.SetCheckPos(savedCheckPos, savedCheckCellId);
        sp.BackupCheckPos = savedBackupCheckPos;
        sp.BackupCheckCellId = savedBackupCheckCellId;

        return transitState != TransitionState.OK;
    }

    // -----------------------------------------------------------------------
    // Post-step validation
    // -----------------------------------------------------------------------

    private TransitionState ValidateTransition(TransitionState transitionState)
    {
        var sp = SpherePath;
        var ci = CollisionInfo;
        var oi = ObjectInfo;

        bool cleanAdvance = transitionState == TransitionState.OK && sp.CheckPos != sp.CurPos;

        if (transitionState == TransitionState.OK && sp.CheckPos != sp.CurPos)
        {
            // Movement succeeded: accept the new position.
            sp.CurPos = sp.CheckPos;
            sp.CurCellId = sp.CheckCellId;
            sp.CurOrientation = sp.CheckOrientation;

            for (int i = 0; i < sp.NumSphere; i++)
            {
                sp.GlobalCurrCenter[i].Origin =
                    Vector3.Transform(sp.LocalSphere[i].Origin, sp.CurOrientation) + sp.CurPos;
                sp.GlobalCurrCenter[i].Radius = sp.LocalSphere[i].Radius;
            }

            sp.SetCheckPos(sp.CurPos, sp.CurCellId);
            // moved = true  (FramesStationaryFall deferred to full physics port)
        }
        else if (transitionState == TransitionState.OK)
        {
            // No movement (same position): accept as-is.
            sp.SetCheckPos(sp.CurPos, sp.CurCellId);
        }
        else if (transitionState != TransitionState.Invalid)
        {
            if (ci.LastKnownContactPlaneValid)
            {
                oi.StopVelocity();

                var sphereCenter = sp.GlobalCurrCenter[0].Origin;
                var radius = sp.GlobalSphere[0].Radius;
                float angle = Vector3.Dot(ci.LastKnownContactPlane.Normal, sphereCenter)
                              + ci.LastKnownContactPlane.D;

                if (radius + PhysicsGlobals.EPSILON > MathF.Abs(angle))
                {
                    ci.SetContactPlane(
                        ci.LastKnownContactPlane,
                        ci.LastKnownContactPlaneCellId,
                        ci.LastKnownContactPlaneIsWater);
                }
            }

            if (!ci.CollisionNormalValid)
                ci.SetCollisionNormal(Vector3.UnitZ);   // default: push up

            sp.SetCheckPos(sp.CurPos, sp.CurCellId);
            transitionState = TransitionState.OK;
        }

        if (ci.CollisionNormalValid)
            ci.SetSlidingNormal(ci.CollisionNormal);

        if (ci.ContactPlaneValid)
        {
            ci.LastKnownContactPlaneValid = true;
            ci.LastKnownContactPlane = ci.ContactPlane;
            ci.LastKnownContactPlaneCellId = ci.ContactPlaneCellId;
            ci.LastKnownContactPlaneIsWater = ci.ContactPlaneIsWater;

            oi.State |= ObjectInfoState.Contact;
            if (ci.ContactPlane.Normal.Z >= PhysicsGlobals.FloorZ)
                oi.State |= ObjectInfoState.OnWalkable;
            else
                oi.State &= ~ObjectInfoState.OnWalkable;
        }
        else
        {
            ci.LastKnownContactPlaneValid = false;
            oi.State &= ~(ObjectInfoState.Contact | ObjectInfoState.OnWalkable);
        }

        if (!oi.IsViewer && oi.MoverHasGravity)
        {
            bool redo = cleanAdvance || oi.OnWalkable;
            if (!redo)
            {
                int fsf = ci.FramesStationaryFall;
                if (fsf > 0)
                {
                    if (fsf > 1)
                    {
                        ci.FramesStationaryFall = 3;

                        var up = Vector3.UnitZ;
                        float d = sp.GlobalSphere[0].Radius - sp.GlobalSphere[0].Origin.Z;
                        ci.SetContactPlane(new Plane(up, d), sp.CheckCellId, isWater: false);

                        if (!oi.Contact)
                        {
                            ci.SetCollisionNormal(up);
                            ci.CollidedWithEnvironment = true;
                        }

                        oi.State |= ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
                        ci.LastKnownContactPlaneValid = true;
                        ci.LastKnownContactPlane = ci.ContactPlane;
                        ci.LastKnownContactPlaneCellId = ci.ContactPlaneCellId;
                        ci.LastKnownContactPlaneIsWater = ci.ContactPlaneIsWater;
                    }
                    else
                        ci.FramesStationaryFall = 2;
                }
                else
                    ci.FramesStationaryFall = 1;
            }
            else
                ci.FramesStationaryFall = 0;
        }

        return transitionState;
    }
}
