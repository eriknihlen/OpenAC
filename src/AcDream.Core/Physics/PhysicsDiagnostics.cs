using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Physics;

public static class PhysicsDiagnostics
{
    public static int CollisionShadowSampleEvery { get; set; } =
        ParsePositiveInt(
            Environment.GetEnvironmentVariable(
                "ACDREAM_COLLISION_SHADOW_EVERY"));

    public static string CollisionShadowArtifactDirectory { get; set; } =
        Environment.GetEnvironmentVariable(
            "ACDREAM_COLLISION_SHADOW_DIR")
        ?? Path.Combine(
            Environment.CurrentDirectory,
            ".test-out",
            "collision-shadow");

    public static bool ProbeResolveEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_RESOLVE") == "1";

    public static bool ProbeCellEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_CELL") == "1";

    public static bool ProbeChildCellEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_CHILD_CELL") == "1";

    public static bool ProbeParkEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_PARK") == "1";

    public static bool ProbeWorldFrameEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_WORLD_FRAME") == "1";

    public static bool DumpMotionEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_MOTION") == "1";

    public static bool ProbeBuildingEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_BUILDING") == "1";

    public static bool ProbeCellSetEnabled { get; set; }
        = Environment.GetEnvironmentVariable("ACDREAM_PROBE_CELLSET") == "1";

    public static bool ProbeRemoteTeleportEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_REMOTE_TELEPORT") == "1";

    public static void LogRemoteTeleport(
        uint guid,
        string cause,
        bool hookRan,
        string placementStatus)
    {
        if (!ProbeRemoteTeleportEnabled) return;
        Console.WriteLine(System.FormattableString.Invariant(
            $"[remote-teleport] guid=0x{guid:X8} cause={cause} hookRan={hookRan} placement={placementStatus}"));
    }


    private static readonly string? RemoteSlideProbeRaw =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_REMOTE_SLIDE");

    public static bool ProbeRemoteSlideEnabled { get; set; } =
        !string.IsNullOrWhiteSpace(RemoteSlideProbeRaw);

    public static IReadOnlySet<uint> ProbeRemoteSlideGuids { get; set; } =
        RemoteSlideProbeRaw is null || RemoteSlideProbeRaw.Trim() == "1"
            ? new HashSet<uint>()
            : ParseHexIdList(RemoteSlideProbeRaw);

    public static bool ShouldLogRemoteSlide(uint guid) =>
        ProbeRemoteSlideEnabled
        && (ProbeRemoteSlideGuids.Count == 0
            || ProbeRemoteSlideGuids.Contains(guid));

    [ThreadStatic] private static uint _remoteSlideAttributionGuid;

    public static void BeginRemoteSlideAttribution(uint guid)
    {
        if (!ProbeRemoteSlideEnabled) return;
        _remoteSlideAttributionGuid = guid;
    }

    public static uint RemoteSlideAttributionGuid => _remoteSlideAttributionGuid;

    private const long RemoteSlideTickThrottleMs = 200;

    [ThreadStatic]
    private static Dictionary<uint, (long Ms, int Signature)>? _remoteSlideTickGate;

    public static bool ShouldEmitRemoteSlideTick(uint guid, int signature)
    {
        if (!ShouldLogRemoteSlide(guid)) return false;
        _remoteSlideTickGate ??= new Dictionary<uint, (long, int)>();
        long now = Environment.TickCount64;
        if (_remoteSlideTickGate.TryGetValue(guid, out var previous)
            && previous.Signature == signature
            && now - previous.Ms < RemoteSlideTickThrottleMs)
        {
            return false;
        }
        _remoteSlideTickGate[guid] = (now, signature);
        return true;
    }

    public static void LogRemoteSlideUp(
        uint     guid,
        bool     wireGrounded,
        Vector3? wireVelocity,
        string   disposition,
        float?   playerDistance,
        float    bodyToTarget,
        float    bodySnapThreshold,
        bool     willBeDrTicked,
        bool     firstUp,
        bool     airborne,
        bool     contact,
        bool     onWalkable,
        bool     gravity,
        Vector3  bodyVelocity,
        bool     contactPlaneValid,
        float    contactPlaneNormalZ,
        Vector3  wirePosition,
        Vector3  bodyPosition,
        int      interpQueueDepth,
        int      interpFailCount)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string wireVel = wireVelocity is { } wv
            ? string.Format(ci, "({0:F3},{1:F3},{2:F3})", wv.X, wv.Y, wv.Z)
            : "null";
        string playerDist = playerDistance is { } pd
            ? pd.ToString("F2", ci)
            : "n/a";
        Console.WriteLine(string.Format(ci,
            "[remote-slide-up] guid=0x{0:X8} t={1} wireGrounded={2} wireVel={3} " +
            "disp={4} playerDist={5} bodyToTarget={6:F3} snapThreshold={7:F3} " +
            "willBeDrTicked={8} firstUpAtEntry={9} airborne={10} contact={11} " +
            "onWalkable={12} gravity={13} bodyVel=({14:F3},{15:F3},{16:F3}) " +
            "cpValid={17} cpNz={18:F4} floorZ={19:F4} steep={20} " +
            "wirePos=({21:F3},{22:F3},{23:F3}) bodyPos=({24:F3},{25:F3},{26:F3}) " +
            "queueDepth={27} failCount={28}",
            guid, Environment.TickCount64, wireGrounded, wireVel,
            disposition, playerDist, bodyToTarget, bodySnapThreshold,
            willBeDrTicked, firstUp, airborne, contact,
            onWalkable, gravity,
            bodyVelocity.X, bodyVelocity.Y, bodyVelocity.Z,
            contactPlaneValid, contactPlaneNormalZ, PhysicsGlobals.FloorZ,
            contactPlaneValid && contactPlaneNormalZ < PhysicsGlobals.FloorZ,
            wirePosition.X, wirePosition.Y, wirePosition.Z,
            bodyPosition.X, bodyPosition.Y, bodyPosition.Z,
            interpQueueDepth, interpFailCount));
    }

    public static void LogRemoteSlideVector(
        uint    guid,
        Vector3 wireVelocity,
        Vector3 wireOmega,
        bool    willMarkAirborne,
        bool    airborneBefore,
        bool    contact,
        bool    onWalkable,
        bool    gravity,
        Vector3 bodyVelocity,
        bool    contactPlaneValid,
        float   contactPlaneNormalZ)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[remote-slide-vec] guid=0x{0:X8} t={1} " +
            "wireVel=({2:F3},{3:F3},{4:F3}) wireOmega=({5:F3},{6:F3},{7:F3}) " +
            "willMarkAirborne={8} airborneBefore={9} contact={10} " +
            "onWalkable={11} gravity={12} bodyVel=({13:F3},{14:F3},{15:F3}) " +
            "cpValid={16} cpNz={17:F4} floorZ={18:F4} steep={19}",
            guid, Environment.TickCount64,
            wireVelocity.X, wireVelocity.Y, wireVelocity.Z,
            wireOmega.X, wireOmega.Y, wireOmega.Z,
            willMarkAirborne, airborneBefore, contact,
            onWalkable, gravity,
            bodyVelocity.X, bodyVelocity.Y, bodyVelocity.Z,
            contactPlaneValid, contactPlaneNormalZ, PhysicsGlobals.FloorZ,
            contactPlaneValid && contactPlaneNormalZ < PhysicsGlobals.FloorZ));
    }

    public static void LogRemoteSlideBodySnap(
        uint    guid,
        bool    firstUp,
        bool    willBeDrTicked,
        float   bodyToTarget,
        float   threshold,
        Vector3 bodyPosition,
        Vector3 targetPosition,
        int     interpQueueDepth,
        int     interpFailCount)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[remote-slide-snap] producer=ap87-4m guid=0x{0:X8} t={1} " +
            "firstUp={2} willBeDrTicked={3} bodyToTarget={4:F3} threshold={5:F3} " +
            "body=({6:F3},{7:F3},{8:F3}) target=({9:F3},{10:F3},{11:F3}) " +
            "queueDepth={12} failCount={13}",
            guid, Environment.TickCount64,
            firstUp, willBeDrTicked, bodyToTarget, threshold,
            bodyPosition.X, bodyPosition.Y, bodyPosition.Z,
            targetPosition.X, targetPosition.Y, targetPosition.Z,
            interpQueueDepth, interpFailCount));
    }

    public static void LogRemoteSlideEnqueue(
        uint    guid,
        float   bodyToTarget,
        Vector3 targetPosition,
        int     interpQueueDepth,
        int     interpFailCount)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[remote-slide-enq] guid=0x{0:X8} t={1} bodyToTarget={2:F3} " +
            "target=({3:F3},{4:F3},{5:F3}) queueDepth={6} failCount={7}",
            guid, Environment.TickCount64, bodyToTarget,
            targetPosition.X, targetPosition.Y, targetPosition.Z,
            interpQueueDepth, interpFailCount));
    }

    public static void LogRemoteSlideStallSnap(
        int     failCount,
        int     threshold,
        int     queueDepth,
        Vector3 bodyPosition,
        Vector3 tailPosition,
        float   distanceToHead)
    {
        uint guid = _remoteSlideAttributionGuid;
        if (!ShouldLogRemoteSlide(guid)) return;
        Vector3 tailDelta = tailPosition - bodyPosition;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[remote-slide-snap] producer=interp-stall guid=0x{0:X8} t={1} " +
            "failCount={2} threshold={3} queueDepth={4} " +
            "body=({5:F3},{6:F3},{7:F3}) tail=({8:F3},{9:F3},{10:F3}) " +
            "tailDelta=({11:F3},{12:F3},{13:F3}) tailDeltaLen={14:F3} " +
            "distToHead={15:F3}",
            guid, Environment.TickCount64,
            failCount, threshold, queueDepth,
            bodyPosition.X, bodyPosition.Y, bodyPosition.Z,
            tailPosition.X, tailPosition.Y, tailPosition.Z,
            tailDelta.X, tailDelta.Y, tailDelta.Z, tailDelta.Length(),
            distanceToHead));
    }

    public static void LogRemoteSlideTick(
        uint    guid,
        bool    airborne,
        bool    forcedContact,
        bool    forcedWalkable,
        Vector3 velocityBeforeZero,
        bool    resolved,
        bool    resolveInContact,
        bool    resolveOnWalkable,
        bool    resolveIsOnGround,
        bool    resolveContactPlaneValid,
        float   resolveContactPlaneNormalZ,
        bool    bodyContactPlaneValid,
        float   bodyContactPlaneNormalZ,
        bool    contact,
        bool    onWalkable,
        bool    gravity,
        Vector3 velocity,
        Vector3 acceleration,
        Vector3 preIntegratePosition,
        Vector3 postIntegratePosition,
        Vector3 resolvedPosition)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[remote-slide-tick] guid=0x{0:X8} t={1} airborne={2} " +
            "entryNoContact={3} entryNoWalkable={4} " +
            "velBeforeZero=({5:F3},{6:F3},{7:F3}) resolved={8} " +
            "rsInContact={9} rsOnWalkable={10} rsIsOnGround={11} " +
            "rsCpValid={12} rsCpNz={13:F4} " +
            "bodyCpValid={14} bodyCpNz={15:F4} floorZ={16:F4} steep={17} " +
            "contact={18} onWalkable={19} gravity={20} " +
            "vel=({21:F3},{22:F3},{23:F3}) accel=({24:F3},{25:F3},{26:F3}) " +
            "pre=({27:F3},{28:F3},{29:F3}) post=({30:F3},{31:F3},{32:F3}) " +
            "out=({33:F3},{34:F3},{35:F3}) moved={36:F4}",
            guid, Environment.TickCount64, airborne,
            forcedContact, forcedWalkable,
            velocityBeforeZero.X, velocityBeforeZero.Y, velocityBeforeZero.Z,
            resolved,
            resolveInContact, resolveOnWalkable, resolveIsOnGround,
            resolveContactPlaneValid, resolveContactPlaneNormalZ,
            bodyContactPlaneValid, bodyContactPlaneNormalZ, PhysicsGlobals.FloorZ,
            bodyContactPlaneValid && bodyContactPlaneNormalZ < PhysicsGlobals.FloorZ,
            contact, onWalkable, gravity,
            velocity.X, velocity.Y, velocity.Z,
            acceleration.X, acceleration.Y, acceleration.Z,
            preIntegratePosition.X, preIntegratePosition.Y, preIntegratePosition.Z,
            postIntegratePosition.X, postIntegratePosition.Y, postIntegratePosition.Z,
            resolvedPosition.X, resolvedPosition.Y, resolvedPosition.Z,
            Vector3.Distance(preIntegratePosition, resolvedPosition)));
    }

    public static void LogCellSetBuild(
        uint seedCellId,
        System.Numerics.Vector3 sphereCenter,
        System.Collections.Generic.IReadOnlyCollection<uint> cellSet)
    {
        if (!ProbeCellSetEnabled) return;
        var ids = new System.Text.StringBuilder();
        bool first = true;
        foreach (uint id in cellSet)
        {
            if (!first) ids.Append(',');
            ids.Append(System.FormattableString.Invariant($"0x{id:X8}"));
            first = false;
        }
        Console.WriteLine(System.FormattableString.Invariant(
            $"[cellset-build] seed=0x{seedCellId:X8} sphere=({sphereCenter.X:F3},{sphereCenter.Y:F3},{sphereCenter.Z:F3}) count={cellSet.Count} ids={ids}"));
    }

    public static ResolvedPolygon? LastBspHitPoly { get; set; }

    public static bool ProbeUseabilityFallbackEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_USEABILITY_FALLBACK") == "1";

    public static bool DumpSteepRoofEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_STEEP_ROOF") == "1";

    public static bool ProbeIndoorBspEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_INDOOR_BSP") == "1";

    public static bool ProbeCellCacheEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_CELL_CACHE") == "1";

    public static bool ProbeContactPlaneEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_CONTACT_PLANE") == "1";

    public static bool ProbePushBackEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_PUSH_BACK") == "1";

    public static bool ProbePolyDumpEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_POLY_DUMP") == "1";

    public static void LogPolyDump(uint cellId, ResolvedPolygon poly)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(256);
        sb.AppendFormat(ci,
            "[poly-dump] cell=0x{0:X8} polyId=0x{1:X4} numPts={2} sides={3} " +
            "n=({4:F6},{5:F6},{6:F6}) d={7:F6} verts=[",
            cellId, poly.Id, poly.NumPoints, poly.SidesType,
            poly.Plane.Normal.X, poly.Plane.Normal.Y, poly.Plane.Normal.Z, poly.Plane.D);
        for (int i = 0; i < poly.Vertices.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.AppendFormat(ci, "({0:F6},{1:F6},{2:F6})",
                poly.Vertices[i].X, poly.Vertices[i].Y, poly.Vertices[i].Z);
        }
        sb.Append(']');
        Console.WriteLine(sb.ToString());
    }

    public static bool ProbePlacementFailEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_PLACEMENT_FAIL") == "1";

    public static bool ProbeSweptEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_SWEPT") == "1";

    public static bool ProbeJumpEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_JUMP") == "1";

    public static bool ProbeTeleportEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_TELEPORT") == "1";

    public static void LogTeleport(string point, uint id, string extra = "")
    {
        if (!ProbeTeleportEnabled) return;
        Console.WriteLine(System.FormattableString.Invariant(
            $"[tp-probe] {point,-6} id=0x{id:X8} t={Environment.TickCount64} {extra}"));
    }

    public static bool ProbeLocalTeleportEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_LOCAL_TELEPORT") == "1";

    public static string LocalTeleportHostKind { get; set; } = "graphical";

    public static void LogLocalTeleportArrival(
        string cause,
        string placementStatus,
        long portalGeneration,
        ushort teleportSequence,
        uint destinationCell,
        uint resolvedCell,
        bool hookTailRan,
        bool leashArmed,
        bool autorunCancelled)
    {
        if (!ProbeLocalTeleportEnabled) return;
        string hookTailText = hookTailRan ? "ran" : "skipped";
        string leashText = leashArmed ? "armed" : "unarmed";
        string autorunText = autorunCancelled ? "cancelled" : "unchanged";
        Console.WriteLine(System.FormattableString.Invariant(
            $"[local-tp] cause={cause} host={LocalTeleportHostKind} status={placementStatus} gen={portalGeneration} seq={teleportSequence} dest=0x{destinationCell:X8} resolved=0x{resolvedCell:X8} hookTail={hookTailText} leash={leashText} autorun={autorunText}"));
    }

    public static bool ProbeStepWalkEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_PROBE_STEP_WALK") == "1";

    public static IReadOnlySet<uint> ProbeDumpCellIds { get; set; } =
        ParseHexIdList(Environment.GetEnvironmentVariable("ACDREAM_DUMP_CELLS"));

    public static string ProbeDumpCellsPath { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_CELLS_DIR")
        ?? "tests/AcDream.Core.Tests/Fixtures/cellar-ascent";

    public static bool ProbeDumpCellsEnabled => ProbeDumpCellIds.Count > 0;

    public static IReadOnlySet<uint> ProbeDumpGfxObjIds { get; set; } =
        ParseHexIdList(Environment.GetEnvironmentVariable("ACDREAM_DUMP_GFXOBJS"));

    public static string ProbeDumpGfxObjsPath { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_GFXOBJS_DIR")
        ?? "tests/AcDream.Core.Tests/Fixtures/cellar-ascent";

    public static bool ProbeDumpGfxObjsEnabled => ProbeDumpGfxObjIds.Count > 0;

    public static void ResetForTest()
    {
        ProbeResolveEnabled           = false;
        ProbeCellEnabled              = false;
        ProbeParkEnabled              = false;
        ProbeBuildingEnabled          = false;
        ProbeCellSetEnabled           = false;
        ProbeUseabilityFallbackEnabled= false;
        DumpSteepRoofEnabled          = false;
        ProbeIndoorBspEnabled         = false;
        ProbeCellCacheEnabled         = false;
        ProbeContactPlaneEnabled      = false;
        ProbePushBackEnabled          = false;
        ProbePolyDumpEnabled          = false;
        ProbePlacementFailEnabled     = false;
        ProbeSweptEnabled             = false;
        ProbeStepWalkEnabled          = false;
        ProbeTeleportEnabled          = false;
        ProbeRemoteTeleportEnabled    = false;
        ProbeRemoteSlideEnabled       = false;
        ProbeRemoteSlideGuids         = new System.Collections.Generic.HashSet<uint>();
        _remoteSlideAttributionGuid   = 0;
        _remoteSlideTickGate          = null;

        LastBspHitPoly                = null;
        LastPlacementFailPolyId       = 0;
        LastPlacementFailPolyNormal   = default;
        LastPlacementFailPolyD        = 0f;
        LastPlacementFailSolidLeaf    = false;

        // Dump-trigger sets
        ProbeDumpCellIds              = new System.Collections.Generic.HashSet<uint>();
        ProbeDumpGfxObjIds            = new System.Collections.Generic.HashSet<uint>();

        ResetPerfectClipTailGuardForTest();
    }

    private static IReadOnlySet<uint> ParseHexIdList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new System.Collections.Generic.HashSet<uint>();

        var ids = new System.Collections.Generic.HashSet<uint>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[2..];

            if (uint.TryParse(
                trimmed,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    public static ushort LastPlacementFailPolyId { get; set; }
    public static Vector3 LastPlacementFailPolyNormal { get; set; }
    public static float LastPlacementFailPolyD { get; set; }
    public static bool LastPlacementFailSolidLeaf { get; set; }

    public static void LogPlacementFail(
        string  source,
        Vector3 sphereCenter,
        float   radius,
        int     sphereIdx,
        uint    cellId,
        Vector3 worldOrigin,
        bool    ethereal)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string polyDesc = LastPlacementFailSolidLeaf
            ? "solid_leaf=1"
            : LastPlacementFailPolyId != 0
                ? string.Format(ci, "polyId=0x{0:X4} n=({1:F4},{2:F4},{3:F4}) d={4:F4}",
                    LastPlacementFailPolyId,
                    LastPlacementFailPolyNormal.X, LastPlacementFailPolyNormal.Y, LastPlacementFailPolyNormal.Z,
                    LastPlacementFailPolyD)
                : "no_poly_info";

        Console.WriteLine(string.Format(ci,
            "[place-fail] source={0} cell=0x{1:X8} sphere=({2:F4},{3:F4},{4:F4}) r={5:F4} " +
            "sphereIdx={6} worldOrigin=({7:F4},{8:F4},{9:F4}) ethereal={10} {11}",
            source, cellId, sphereCenter.X, sphereCenter.Y, sphereCenter.Z, radius,
            sphereIdx, worldOrigin.X, worldOrigin.Y, worldOrigin.Z, ethereal, polyDesc));
    }

    public static void LogPushBackAdjust(
        Vector3 inputCenter,
        Vector3 outputCenter,
        Plane   plane,
        float   radius,
        float   walkInterpBefore,
        float   walkInterpAfter,
        float   dpPos,
        float   dpMove,
        float   iDist,
        bool    applied)
    {
        var delta = outputCenter - inputCenter;
        float deltaMag = delta.Length();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[push-back] site=adjust_sphere " +
            "in=({0:F4},{1:F4},{2:F4}) " +
            "out=({3:F4},{4:F4},{5:F4}) " +
            "delta=({6:F4},{7:F4},{8:F4}) deltaMag={9:F4} " +
            "n=({10:F4},{11:F4},{12:F4}) d={13:F4} " +
            "r={14:F4} winterp={15:F4}->{16:F4} " +
            "dpPos={17:F4} dpMove={18:F4} iDist={19:F4} applied={20}",
            inputCenter.X,  inputCenter.Y,  inputCenter.Z,
            outputCenter.X, outputCenter.Y, outputCenter.Z,
            delta.X,        delta.Y,        delta.Z,        deltaMag,
            plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D,
            radius, walkInterpBefore, walkInterpAfter,
            dpPos, dpMove, iDist, applied));
    }

    public static void LogPushBackDispatch(
        Vector3 sphereCenter,
        Vector3 movement,
        bool    collide,
        int     insertType,
        int     objState,
        float   walkInterpEntry,
        int     returnState)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[push-back-disp] site=dispatch " +
            "center=({0:F4},{1:F4},{2:F4}) " +
            "mvmt=({3:F4},{4:F4},{5:F4}) " +
            "collide={6} insertType={7} objState=0x{8:X} " +
            "winterp={9:F4} return={10}",
            sphereCenter.X, sphereCenter.Y, sphereCenter.Z,
            movement.X, movement.Y, movement.Z,
            collide, insertType, objState,
            walkInterpEntry, returnState));
    }

    public static void LogPushBackCellTransit(
        uint   primaryCellId,
        uint   otherCellId,
        int    bspResult,
        bool   halted)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine(string.Format(ci,
            "[push-back-cell] site=other_cell " +
            "primary=0x{0:X8} other=0x{1:X8} " +
            "bspResult={2} halted={3}",
            primaryCellId, otherCellId, bspResult, halted));
    }

    public static void LogStepWalk(
        string          site,
        int             stepIndex,
        int             stepCount,
        SpherePath      sp,
        CollisionInfo   ci,
        ObjectInfo      oi,
        Vector3         requestedOffset,
        Vector3         adjustedOffset,
        TransitionState? state = null,
        string?         detail = null)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var checkDelta = sp.CheckPos - sp.CurPos;
        string stateText = state.HasValue ? state.Value.ToString() : "n/a";
        string stepText = stepIndex >= 0 && stepCount > 0
            ? string.Format(culture, "{0}/{1}", stepIndex + 1, stepCount)
            : "-";

        Console.WriteLine(string.Format(culture,
            "[step-walk] site={0} step={1} state={2} " +
            "cur=({3:F4},{4:F4},{5:F4}) check=({6:F4},{7:F4},{8:F4}) " +
            "delta=({9:F4},{10:F4},{11:F4}) cell=0x{12:X8}->0x{13:X8} " +
            "req=({14:F4},{15:F4},{16:F4}) adj=({17:F4},{18:F4},{19:F4}) " +
            "winterp={20:F4} stepUp={21} stepDown={22} insert={23} " +
            "oi=0x{24:X} contact={25} onWalkable={26} " +
            "cp={27} lkcp={28} hit={29} slide={30} walkPoly={31} lastWalkPoly={32}{33}",
            site, stepText, stateText,
            sp.CurPos.X, sp.CurPos.Y, sp.CurPos.Z,
            sp.CheckPos.X, sp.CheckPos.Y, sp.CheckPos.Z,
            checkDelta.X, checkDelta.Y, checkDelta.Z,
            sp.CurCellId, sp.CheckCellId,
            requestedOffset.X, requestedOffset.Y, requestedOffset.Z,
            adjustedOffset.X, adjustedOffset.Y, adjustedOffset.Z,
            sp.WalkInterp,
            sp.StepUp, sp.StepDown, sp.InsertType,
            (uint)oi.State, oi.Contact, oi.OnWalkable,
            FormatPlane(ci.ContactPlaneValid, ci.ContactPlane, ci.ContactPlaneCellId, ci.ContactPlaneIsWater),
            FormatPlane(ci.LastKnownContactPlaneValid, ci.LastKnownContactPlane, ci.LastKnownContactPlaneCellId, ci.LastKnownContactPlaneIsWater),
            FormatVector(ci.CollisionNormalValid, ci.CollisionNormal),
            FormatVector(ci.SlidingNormalValid, ci.SlidingNormal),
            sp.HasWalkablePolygon, sp.HasLastWalkablePolygon,
            string.IsNullOrEmpty(detail) ? string.Empty : " " + detail));
    }

    public static void LogStepWalkAdjust(
        string  branch,
        Vector3 input,
        Vector3 output,
        Plane?  contactPlane,
        bool    slidingValid,
        Vector3 slidingNormal,
        float   collisionAngle,
        float   walkInterp)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        string cpDesc = contactPlane is { } cp
            ? string.Format(culture,
                "n=({0:F4},{1:F4},{2:F4}) d={3:F4}",
                cp.Normal.X, cp.Normal.Y, cp.Normal.Z, cp.D)
            : "n/a";

        string slideDesc = slidingValid
            ? string.Format(culture,
                "({0:F4},{1:F4},{2:F4})",
                slidingNormal.X, slidingNormal.Y, slidingNormal.Z)
            : "n/a";

        Console.WriteLine(string.Format(culture,
            "[step-walk-adjust] branch={0} input=({1:F4},{2:F4},{3:F4}) " +
            "output=({4:F4},{5:F4},{6:F4}) zGain={7:F4} " +
            "cp={8} slide={9} colAngle={10:F4} winterp={11:F4}",
            branch,
            input.X, input.Y, input.Z,
            output.X, output.Y, output.Z,
            output.Z - input.Z,
            cpDesc, slideDesc, collisionAngle, walkInterp));
    }

    private static string FormatVector(bool valid, Vector3 value)
    {
        if (!valid)
            return "n/a";

        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "({0:F4},{1:F4},{2:F4})",
            value.X, value.Y, value.Z);
    }

    private static string FormatPlane(bool valid, Plane plane, uint cellId, bool isWater)
    {
        if (!valid)
            return "n/a";

        float zAtOrigin = MathF.Abs(plane.Normal.Z) > PhysicsGlobals.EPSILON
            ? -plane.D / plane.Normal.Z
            : float.NaN;

        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "cell=0x{0:X8},water={1},n=({2:F4},{3:F4},{4:F4}),d={5:F4},z0={6:F4}",
            cellId, isWater,
            plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D,
            zAtOrigin);
    }

    public static void LogCpBoolWrite(string field, bool oldValue, bool newValue)
    {
        var caller = GetCpCallerName();
        Console.WriteLine(System.FormattableString.Invariant(
            $"[cp-write] {field}: {oldValue} -> {newValue} caller={caller}"));
    }

    public static void LogCpPlaneWrite(string field, Plane oldPlane, Plane newPlane)
    {
        var caller = GetCpCallerName();
        Console.WriteLine(System.FormattableString.Invariant(
            $"[cp-write] {field}: n=({oldPlane.Normal.X:F3},{oldPlane.Normal.Y:F3},{oldPlane.Normal.Z:F3}) D={oldPlane.D:F3} -> n=({newPlane.Normal.X:F3},{newPlane.Normal.Y:F3},{newPlane.Normal.Z:F3}) D={newPlane.D:F3} caller={caller}"));
    }

    public static void LogCpCellIdWrite(string field, uint oldValue, uint newValue)
    {
        var caller = GetCpCallerName();
        Console.WriteLine(System.FormattableString.Invariant(
            $"[cp-write] {field}: 0x{oldValue:X8} -> 0x{newValue:X8} caller={caller}"));
    }

    private static string GetCpCallerName()
    {
        var st = new System.Diagnostics.StackTrace(2, fNeedFileInfo: true);
        for (int i = 0; i < st.FrameCount; i++)
        {
            var f = st.GetFrame(i);
            var m = f?.GetMethod();
            if (m is null) continue;
            var typeName = m.DeclaringType?.Name ?? "?";
            if (typeName == "CollisionInfo" || typeName == "PhysicsDiagnostics") continue;
            int line = f?.GetFileLineNumber() ?? 0;
            return line > 0 ? $"{typeName}.{m.Name}:{line}" : $"{typeName}.{m.Name}";
        }
        return "?";
    }


    public static void RecordSpherePerfectClipTailReach(bool moverIsViewer) =>
        RecordPerfectClipTailReachCore(
            "Sphere", moverIsViewer,
            ref _sphereToiCameraLiveCount, ref _sphereToiUnverifiedCount,
            ref _sphereToiUnverifiedAnnounced);

    public static void RecordCylPerfectClipTailReach(bool moverIsViewer) =>
        RecordPerfectClipTailReachCore(
            "Cyl", moverIsViewer,
            ref _cylToiCameraLiveCount, ref _cylToiUnverifiedCount,
            ref _cylToiUnverifiedAnnounced);

    private static void RecordPerfectClipTailReachCore(
        string tail, bool moverIsViewer,
        ref int cameraLiveCount, ref int unverifiedCount, ref int unverifiedAnnounced)
    {
        if (moverIsViewer)
        {
            System.Threading.Interlocked.Increment(ref cameraLiveCount);
            return;
        }

        System.Threading.Interlocked.Increment(ref unverifiedCount);
        if (System.Threading.Interlocked.Exchange(ref unverifiedAnnounced, 1) != 0)
            return;

        Console.WriteLine(
            $"[perfectclip-tail] UNVERIFIED mover reached the {tail} PerfectClip "
            + "time-of-impact tail; the verified-reachable population is the camera / "
            + "IsViewer only. A non-viewer mover just executed this path and its "
            + "reachability was never re-verified, so do not trust the result without "
            + "checking it.");
    }

    private static int _sphereToiCameraLiveCount;
    private static int _sphereToiUnverifiedCount;
    private static int _sphereToiUnverifiedAnnounced;
    private static int _cylToiCameraLiveCount;
    private static int _cylToiUnverifiedCount;
    private static int _cylToiUnverifiedAnnounced;

    public static int SphereToiCameraLiveCount => _sphereToiCameraLiveCount;
    public static int SphereToiUnverifiedCount => _sphereToiUnverifiedCount;
    public static int CylToiCameraLiveCount => _cylToiCameraLiveCount;
    public static int CylToiUnverifiedCount => _cylToiUnverifiedCount;

    public static void ResetPerfectClipTailGuardForTest()
    {
        _sphereToiCameraLiveCount = 0;
        _sphereToiUnverifiedCount = 0;
        _sphereToiUnverifiedAnnounced = 0;
        _cylToiCameraLiveCount = 0;
        _cylToiUnverifiedCount = 0;
        _cylToiUnverifiedAnnounced = 0;
    }


    /// <summary>
    /// Initial state from <c>ACDREAM_DUMP_TRANSIT_FAIL=1</c>.
    /// </summary>
    public static bool DumpTransitFailEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_DUMP_TRANSIT_FAIL") == "1";

    private const float TransitFailNonzeroRequestXYSq = 0.001f * 0.001f;

    private const float TransitFailZeroXYSq = 0.0001f * 0.0001f;

    [ThreadStatic] private static List<string>? _transitFailBuffer;
    [ThreadStatic] private static string? _transitFailAdjustLine;

    public static void BeginTransitFailTrace()
    {
        if (!DumpTransitFailEnabled) return;
        (_transitFailBuffer ??= new List<string>()).Clear();
        _transitFailAdjustLine = null;
    }

    public static void TraceTransitInsertAttempt(
        uint moverId,
        int attempt,
        string phase,
        TransitionState envState,
        TransitionState? buildingState,
        TransitionState? objectsState,
        TransitionState outcome,
        Vector3 collisionNormal,
        uint? collidedObjectGuid)
    {
        if (!DumpTransitFailEnabled) return;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string buildingText = buildingState is { } b ? b.ToString() : "n/a";
        string objectsText = objectsState is { } o ? o.ToString() : "n/a";
        string collidedText = outcome == TransitionState.Collided
            ? string.Format(ci,
                " collN=({0:F3},{1:F3},{2:F3}) src={3}",
                collisionNormal.X, collisionNormal.Y, collisionNormal.Z,
                phase == "objects"
                    ? (collidedObjectGuid is { } guid
                        ? string.Format(ci, "object:0x{0:X8}", guid)
                        : "object:none")
                    : phase)
            : "";

        (_transitFailBuffer ??= new List<string>()).Add(string.Format(ci,
            "[transit-fail-insert] mover=0x{0:X8} attempt={1} phase={2} " +
            "env={3} building={4} objects={5} outcome={6}{7}",
            moverId, attempt, phase, envState, buildingText, objectsText,
            outcome, collidedText));
    }

    public static void TraceTransitStepUp(
        uint moverId,
        string edge,
        Vector3 inputNormal,
        bool onWalkable,
        float stepUpHeight,
        Vector3 pos,
        bool? succeeded,
        Vector3? landedNormal)
    {
        if (!DumpTransitFailEnabled) return;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        float floor = PhysicsGlobals.FloorZ;
        string verdict = inputNormal.Z >= floor ? "WALKABLE" : "STEEP";
        string outcomeText;
        if (succeeded is null)
        {
            outcomeText = "";
        }
        else if (succeeded.Value && landedNormal is { } landed)
        {
            string landedVerdict = landed.Z >= floor ? "WALKABLE" : "STEEP";
            outcomeText = string.Format(ci,
                " outcome=SUCCESS landedN=({0:F3},{1:F3},{2:F3})->{3}",
                landed.X, landed.Y, landed.Z, landedVerdict);
        }
        else
        {
            outcomeText = " outcome=FAILED";
        }

        (_transitFailBuffer ??= new List<string>()).Add(string.Format(ci,
            "[transit-fail-stepup] mover=0x{0:X8} edge={1} " +
            "n=({2:F3},{3:F3},{4:F3})->{5} onWalkable={6} stepUpHeight={7:F3} " +
            "pos=({8:F2},{9:F2},{10:F2}){11}",
            moverId, edge,
            inputNormal.X, inputNormal.Y, inputNormal.Z, verdict,
            onWalkable, stepUpHeight,
            pos.X, pos.Y, pos.Z, outcomeText));
    }

    public static void TraceTransitValidateWalkable(
        uint moverId,
        string branch,
        float dist,
        float waterDepth,
        bool oiContact,
        bool spStepDown,
        bool? guardPassed,
        Vector3 normal,
        TransitionState outcome)
    {
        if (!DumpTransitFailEnabled) return;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string guardText = guardPassed is { } g ? g.ToString() : "n/a";

        (_transitFailBuffer ??= new List<string>()).Add(string.Format(ci,
            "[transit-fail-walk] mover=0x{0:X8} branch={1} dist={2:F5} " +
            "waterDepth={3:F4} oiContact={4} spStepDown={5} guardPassed={6} " +
            "normal=({7:F3},{8:F3},{9:F3}) outcome={10}",
            moverId, branch, dist, waterDepth, oiContact, spStepDown,
            guardText, normal.X, normal.Y, normal.Z, outcome));
    }

    public static void TraceTransitAdjustOffset(
        uint moverId, string branch, Vector3 offsetIn, Vector3 offsetOut)
    {
        if (!DumpTransitFailEnabled) return;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _transitFailAdjustLine = string.Format(ci,
            "[transit-fail-adjust] mover=0x{0:X8} branch={1} " +
            "in=({2:F4},{3:F4},{4:F4}) out=({5:F4},{6:F4},{7:F4})",
            moverId, branch,
            offsetIn.X, offsetIn.Y, offsetIn.Z,
            offsetOut.X, offsetOut.Y, offsetOut.Z);
    }

    public static void EmitTransitFailIfStuck(
        uint moverId, Vector3 currentPos, Vector3 targetPos, Vector3 resultPos)
    {
        if (!DumpTransitFailEnabled) return;

        float reqX = targetPos.X - currentPos.X;
        float reqY = targetPos.Y - currentPos.Y;
        float reqXYSq = reqX * reqX + reqY * reqY;
        float actX = resultPos.X - currentPos.X;
        float actY = resultPos.Y - currentPos.Y;
        float actXYSq = actX * actX + actY * actY;

        bool stuck = reqXYSq >= TransitFailNonzeroRequestXYSq
                     && actXYSq <= TransitFailZeroXYSq;

        if (stuck)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int lineCount = (_transitFailBuffer?.Count ?? 0)
                + (_transitFailAdjustLine is null ? 0 : 1);
            Console.WriteLine(string.Format(ci,
                "[transit-fail] mover=0x{0:X8} STUCK-TICK " +
                "reqXY=({1:F4},{2:F4}) reqLen={3:F4} " +
                "actXY=({4:F4},{5:F4}) actLen={6:F4} " +
                "in=({7:F3},{8:F3},{9:F3}) tgt=({10:F3},{11:F3},{12:F3}) " +
                "out=({13:F3},{14:F3},{15:F3}) lines={16}",
                moverId, reqX, reqY, MathF.Sqrt(reqXYSq),
                actX, actY, MathF.Sqrt(actXYSq),
                currentPos.X, currentPos.Y, currentPos.Z,
                targetPos.X, targetPos.Y, targetPos.Z,
                resultPos.X, resultPos.Y, resultPos.Z,
                lineCount));

            if (_transitFailBuffer is { Count: > 0 } buffer)
            {
                foreach (string line in buffer)
                    Console.WriteLine(line);
            }
            if (_transitFailAdjustLine is not null)
                Console.WriteLine(_transitFailAdjustLine);
        }

        _transitFailBuffer?.Clear();
        _transitFailAdjustLine = null;
    }

    private static int ParsePositiveInt(string? value) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
        && parsed > 0
            ? parsed
            : 0;
}
