using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

internal enum RuntimePositionEntityKind : byte
{
    Unknown,
    LocalPlayer,
    Remote,
    Projectile,
}

internal enum RuntimeCreateResidenceKind : byte
{
    Unknown,
    TopLevel,
    Parented,
    PickedUp,
}

internal enum RuntimeAcceptedPositionSource : byte
{
    Unknown,
    PositionEvent,
    SameIncarnationCreate,
}

internal enum RuntimeAuthoritativePositionDisposition : byte
{
    RejectedAuthority,
    RejectedData,
    AwaitFreshPosition,
    NoPositionOperation,
    Interpolate,
    SetPosition,
    SetPositionSimple,
}

internal enum RuntimeTeleportHookPhase : byte
{
    None,
    BeforePositionOperation,
    AfterPositionOperation,
    AfterEnterWorld,
}

internal enum RuntimePositionConstrainPhase : byte
{
    None,
    BeforePositionOperation,
    AfterPositionOperation,
}

internal readonly record struct RuntimeAuthoritativePositionAuthority(
    RuntimeGenerationToken Generation,
    RuntimeEntityKey Entity,
    ulong PositionAuthorityVersion,
    ushort AcceptedPositionSequence,
    ushort PreviousTeleportSequence,
    ushort AcceptedTeleportSequence,
    PositionTimestampDisposition TimestampDisposition)
{
    internal bool IsStructurallyValid => Generation.Value != 0UL
        && Entity.LocalEntityId != 0u
        && PositionAuthorityVersion != 0UL
        && TimestampDisposition is PositionTimestampDisposition.Apply
            or PositionTimestampDisposition.ForcePosition;

    internal bool TeleportAdvanced =>
        TimestampDisposition is PositionTimestampDisposition.Apply
        && PhysicsTimestampGate.IsNewer(
            PreviousTeleportSequence,
            AcceptedTeleportSequence);

    internal bool TeleportRegressed =>
        PhysicsTimestampGate.IsNewer(
            AcceptedTeleportSequence,
            PreviousTeleportSequence);
}

internal readonly record struct RuntimePositionPlacementFacts(
    PhysicsStateFlags PhysicsState,
    bool HasAuthoredMoverShape)
{
    internal bool CollisionBatchEligible =>
        (PhysicsState & PhysicsStateFlags.Hidden) == 0;
}

internal readonly record struct RuntimeCreatePositionRouteRequest(
    RuntimeAuthoritativePositionAuthority Authority,
    RuntimePositionEntityKind EntityKind,
    RuntimeCreateResidenceKind Residence,
    CreateObject.ServerPosition? AcceptedWirePosition,
    RuntimePositionPlacementFacts PlacementFacts);

internal readonly record struct RuntimeAcceptedPositionRouteRequest(
    RuntimeAuthoritativePositionAuthority Authority,
    RuntimePositionEntityKind EntityKind,
    RuntimeAcceptedPositionSource Source,
    CreateObject.ServerPosition AcceptedWirePosition,
    uint? PlacementFrame,
    Vector3? PositionPackVelocity,
    uint? CommittedCellId,
    bool HasContact,
    float PlayerDistance,
    bool UsePositionFromServer,
    bool HasAnimations,
    RuntimePositionPlacementFacts PlacementFacts);

internal readonly record struct RuntimeAcceptedPositionPrePlacementFlags(
    bool UnparentBeforeRouting,
    bool ApplyPlacementFrameBeforeRouting);

internal readonly record struct RuntimeAuthoritativePositionRoute(
    RuntimeAuthoritativePositionAuthority Authority,
    RuntimeAuthoritativePositionDisposition Disposition,
    RuntimeSetPositionOperationKind OperationKind,
    PhysicsSetPositionFlags SetPositionFlags,
    uint PlacementFrame,
    bool UnparentBeforeRouting,
    bool ApplyPlacementFrameBeforeRouting,
    bool LeaveWorld,
    RuntimeTeleportHookPhase TeleportHookPhase,
    bool StopInterpolating,
    RuntimePositionConstrainPhase ConstrainPhase,
    bool PreserveHeading,
    bool ZeroVelocity,
    bool SendPositionImmediately,
    bool CollisionBatchEligible)
{
    internal bool Accepted => Disposition is not
        RuntimeAuthoritativePositionDisposition.RejectedAuthority
        and not RuntimeAuthoritativePositionDisposition.RejectedData;

    internal bool PerformsSetPosition => Disposition is
        RuntimeAuthoritativePositionDisposition.SetPosition
        or RuntimeAuthoritativePositionDisposition.SetPositionSimple;

    internal bool RunsTeleportHook =>
        TeleportHookPhase is not RuntimeTeleportHookPhase.None;

    internal bool ConstrainBeforeRouting =>
        ConstrainPhase is RuntimePositionConstrainPhase.BeforePositionOperation;

    internal bool ConstrainAfterRouting =>
        ConstrainPhase is RuntimePositionConstrainPhase.AfterPositionOperation;
}

internal static class RuntimeAuthoritativePositionRouteClassifier
{
    private const float MaxPhysicsDistance = 96f;
    private const PhysicsSetPositionFlags InitialCreateFlags =
        PhysicsSetPositionFlags.Placement | PhysicsSetPositionFlags.Slide;
    private const PhysicsSetPositionFlags AuthoritativeTeleportFlags =
        PhysicsSetPositionFlags.Teleport
        | PhysicsSetPositionFlags.Slide
        | PhysicsSetPositionFlags.SendPositionEvent;

    internal static bool IsValidCreateWirePosition(
        in CreateObject.ServerPosition position) => ValidPosition(position);

    internal static RuntimeAcceptedPositionPrePlacementFlags DerivePrePlacementFlags(
        PositionTimestampDisposition disposition,
        bool hasAnimations) => disposition switch
    {
        PositionTimestampDisposition.Apply => new(
            UnparentBeforeRouting: true,
            ApplyPlacementFrameBeforeRouting: !hasAnimations),
        PositionTimestampDisposition.ForcePosition => default,
        // Rejected packets use the timestamp-only merge and never inspect
        // either flag. Returning default keeps that branch explicit.
        PositionTimestampDisposition.Rejected => default,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null),
    };

    internal static RuntimeAuthoritativePositionRoute ClassifyCreate(
        in RuntimeCreatePositionRouteRequest request)
    {
        if (!ValidCreateAuthority(request.Authority)
            || !ValidEntityKind(request.EntityKind)
            || request.Residence is RuntimeCreateResidenceKind.Unknown)
        {
            return RejectedAuthority(request.Authority);
        }

        RuntimeSetPositionOperationKind operation = OperationKind(
            request.EntityKind,
            initialCreate: true);
        bool reporting = request.PlacementFacts.CollisionBatchEligible;
        if (request.Residence is RuntimeCreateResidenceKind.Parented
            or RuntimeCreateResidenceKind.PickedUp)
        {
            return new RuntimeAuthoritativePositionRoute(
                request.Authority,
                RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
                operation,
                PhysicsSetPositionFlags.None,
                0u,
                UnparentBeforeRouting: false,
                ApplyPlacementFrameBeforeRouting: false,
                LeaveWorld: false,
                TeleportHookPhase: RuntimeTeleportHookPhase.None,
                StopInterpolating: false,
                ConstrainPhase: RuntimePositionConstrainPhase.None,
                PreserveHeading: false,
                ZeroVelocity: false,
                SendPositionImmediately: false,
                reporting);
        }

        if (request.AcceptedWirePosition is not { } position
            || !ValidPosition(position))
        {
            return RejectedData(request.Authority, operation, reporting);
        }

        return new RuntimeAuthoritativePositionRoute(
            request.Authority,
            RuntimeAuthoritativePositionDisposition.SetPosition,
            operation,
            InitialCreateFlags,
            0u,
            UnparentBeforeRouting: false,
            ApplyPlacementFrameBeforeRouting: false,
            LeaveWorld: false,
            TeleportHookPhase: request.EntityKind
                    is RuntimePositionEntityKind.LocalPlayer
                ? RuntimeTeleportHookPhase.AfterEnterWorld
                : RuntimeTeleportHookPhase.None,
            StopInterpolating: false,
            ConstrainPhase: RuntimePositionConstrainPhase.None,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            reporting);
    }

    internal static RuntimeAuthoritativePositionRoute ToCellessCreateRoute(
        in RuntimeAuthoritativePositionRoute route) =>
        new(
            route.Authority,
            RuntimeAuthoritativePositionDisposition.AwaitFreshPosition,
            route.OperationKind,
            PhysicsSetPositionFlags.None,
            0u,
            UnparentBeforeRouting: false,
            ApplyPlacementFrameBeforeRouting: false,
            LeaveWorld: false,
            TeleportHookPhase: RuntimeTeleportHookPhase.None,
            StopInterpolating: false,
            ConstrainPhase: RuntimePositionConstrainPhase.None,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            route.CollisionBatchEligible);

    internal static RuntimeAuthoritativePositionRoute ClassifyAcceptedPosition(
        in RuntimeAcceptedPositionRouteRequest request)
    {
        RuntimeSetPositionOperationKind operation = OperationKind(
            request.EntityKind,
            initialCreate: false);
        bool reporting = request.PlacementFacts.CollisionBatchEligible;
        if (!ValidAcceptedAuthority(request.Authority, request.EntityKind)
            || request.Source is RuntimeAcceptedPositionSource.Unknown
            || !ValidEntityKind(request.EntityKind))
        {
            return RejectedAuthority(request.Authority, operation, reporting);
        }
        if (!ValidPosition(request.AcceptedWirePosition))
            return RejectedData(request.Authority, operation, reporting);

        RuntimeAcceptedPositionPrePlacementFlags prePlacement =
            DerivePrePlacementFlags(
                request.Authority.TimestampDisposition,
                request.HasAnimations);
        bool force = request.Authority.TimestampDisposition
            is PositionTimestampDisposition.ForcePosition;
        if (force)
        {
            // The FORCE_POSITION branch precedes unset_parent and
            // SetPlacementFrame in HandleReceivedPosition.
            return new RuntimeAuthoritativePositionRoute(
                request.Authority,
                RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                operation,
                AuthoritativeTeleportFlags,
                request.PlacementFrame ?? 0u,
                prePlacement.UnparentBeforeRouting,
                prePlacement.ApplyPlacementFrameBeforeRouting,
                LeaveWorld: false,
                TeleportHookPhase: RuntimeTeleportHookPhase.None,
                StopInterpolating: false,
                ConstrainPhase: RuntimePositionConstrainPhase.None,
                PreserveHeading: true,
                ZeroVelocity: false,
                SendPositionImmediately: true,
                reporting);
        }

        uint placement = request.PlacementFrame ?? 0u;
        if (request.EntityKind is RuntimePositionEntityKind.LocalPlayer)
        {
            if (request.Authority.TeleportAdvanced)
            {
                return new RuntimeAuthoritativePositionRoute(
                    request.Authority,
                    RuntimeAuthoritativePositionDisposition.SetPositionSimple,
                    operation,
                    AuthoritativeTeleportFlags,
                    placement,
                    prePlacement.UnparentBeforeRouting,
                    prePlacement.ApplyPlacementFrameBeforeRouting,
                    LeaveWorld: false,
                    TeleportHookPhase: RuntimeTeleportHookPhase.AfterPositionOperation,
                    StopInterpolating: false,
                    ConstrainPhase: RuntimePositionConstrainPhase.AfterPositionOperation,
                    PreserveHeading: false,
                    ZeroVelocity: true,
                    SendPositionImmediately: false,
                    reporting);
            }

            bool interpolate = request.UsePositionFromServer
                && request.HasContact;
            return new RuntimeAuthoritativePositionRoute(
                request.Authority,
                interpolate
                    ? RuntimeAuthoritativePositionDisposition.Interpolate
                    : RuntimeAuthoritativePositionDisposition.NoPositionOperation,
                operation,
                PhysicsSetPositionFlags.None,
                placement,
                prePlacement.UnparentBeforeRouting,
                prePlacement.ApplyPlacementFrameBeforeRouting,
                LeaveWorld: false,
                TeleportHookPhase: RuntimeTeleportHookPhase.None,
                StopInterpolating: false,
                ConstrainPhase: RuntimePositionConstrainPhase.BeforePositionOperation,
                PreserveHeading: false,
                ZeroVelocity: false,
                SendPositionImmediately: false,
                reporting);
        }

        bool cellless = !request.CommittedCellId.HasValue
            || request.CommittedCellId.Value == 0u;
        if (request.Authority.TeleportAdvanced || cellless)
        {
            return new RuntimeAuthoritativePositionRoute(
                request.Authority,
                RuntimeAuthoritativePositionDisposition.SetPosition,
                operation,
                AuthoritativeTeleportFlags,
                placement,
                prePlacement.UnparentBeforeRouting,
                prePlacement.ApplyPlacementFrameBeforeRouting,
                LeaveWorld: false,
                TeleportHookPhase: RuntimeTeleportHookPhase.BeforePositionOperation,
                StopInterpolating: false,
                ConstrainPhase: RuntimePositionConstrainPhase.AfterPositionOperation,
                PreserveHeading: false,
                ZeroVelocity: false,
                SendPositionImmediately: false,
                reporting);
        }

        bool effectiveContact = request.Source
                is RuntimeAcceptedPositionSource.SameIncarnationCreate
            || request.HasContact;
        if (!effectiveContact)
        {
            return new RuntimeAuthoritativePositionRoute(
                request.Authority,
                RuntimeAuthoritativePositionDisposition.NoPositionOperation,
                operation,
                PhysicsSetPositionFlags.None,
                placement,
                prePlacement.UnparentBeforeRouting,
                prePlacement.ApplyPlacementFrameBeforeRouting,
                LeaveWorld: false,
                TeleportHookPhase: RuntimeTeleportHookPhase.None,
                StopInterpolating: false,
                ConstrainPhase: RuntimePositionConstrainPhase.None,
                PreserveHeading: false,
                ZeroVelocity: false,
                SendPositionImmediately: false,
                reporting);
        }

        if (!float.IsFinite(request.PlayerDistance)
            || request.PlayerDistance < 0f)
        {
            return RejectedData(request.Authority, operation, reporting);
        }

        bool nearby = request.PlayerDistance < MaxPhysicsDistance;
        return new RuntimeAuthoritativePositionRoute(
            request.Authority,
            nearby
                ? RuntimeAuthoritativePositionDisposition.Interpolate
                : RuntimeAuthoritativePositionDisposition.SetPositionSimple,
            operation,
            nearby ? PhysicsSetPositionFlags.None : AuthoritativeTeleportFlags,
            placement,
            prePlacement.UnparentBeforeRouting,
            prePlacement.ApplyPlacementFrameBeforeRouting,
            LeaveWorld: false,
            TeleportHookPhase: RuntimeTeleportHookPhase.None,
            StopInterpolating: !nearby,
            ConstrainPhase: RuntimePositionConstrainPhase.AfterPositionOperation,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            reporting);
    }

    private static bool ValidCreateAuthority(
        in RuntimeAuthoritativePositionAuthority authority) =>
        authority.IsStructurallyValid
        && authority.TimestampDisposition is PositionTimestampDisposition.Apply
        && authority.PreviousTeleportSequence == authority.AcceptedTeleportSequence;

    private static bool ValidAcceptedAuthority(
        in RuntimeAuthoritativePositionAuthority authority,
        RuntimePositionEntityKind kind)
    {
        if (!authority.IsStructurallyValid || authority.TeleportRegressed)
            return false;
        return authority.TimestampDisposition switch
        {
            PositionTimestampDisposition.Apply => true,
            PositionTimestampDisposition.ForcePosition =>
                kind is RuntimePositionEntityKind.LocalPlayer,
            _ => false,
        };
    }

    private static bool ValidEntityKind(RuntimePositionEntityKind kind) =>
        kind is RuntimePositionEntityKind.LocalPlayer
            or RuntimePositionEntityKind.Remote
            or RuntimePositionEntityKind.Projectile;

    private static bool ValidPosition(in CreateObject.ServerPosition position)
    {
        var origin = new Vector3(
            position.PositionX,
            position.PositionY,
            position.PositionZ);
        var orientation = new Quaternion(
            position.RotationX,
            position.RotationY,
            position.RotationZ,
            position.RotationW);
        return float.IsFinite(origin.X)
            && float.IsFinite(origin.Y)
            && float.IsFinite(origin.Z)
            && float.IsFinite(orientation.X)
            && float.IsFinite(orientation.Y)
            && float.IsFinite(orientation.Z)
            && float.IsFinite(orientation.W)
            && PositionFrameValidation.IsValid(
                position.LandblockId,
                origin,
                orientation);
    }

    private static RuntimeSetPositionOperationKind OperationKind(
        RuntimePositionEntityKind kind,
        bool initialCreate) => kind switch
        {
            RuntimePositionEntityKind.LocalPlayer when initialCreate =>
                RuntimeSetPositionOperationKind.InitialLogin,
            RuntimePositionEntityKind.LocalPlayer =>
                RuntimeSetPositionOperationKind.LocalAuthoritative,
            RuntimePositionEntityKind.Projectile =>
                RuntimeSetPositionOperationKind.ProjectileAuthoritative,
            _ => RuntimeSetPositionOperationKind.RemoteAuthoritative,
        };

    private static RuntimeAuthoritativePositionRoute RejectedAuthority(
        in RuntimeAuthoritativePositionAuthority authority,
        RuntimeSetPositionOperationKind operation =
            RuntimeSetPositionOperationKind.RemoteAuthoritative,
        bool reporting = false) => new(
            authority,
            RuntimeAuthoritativePositionDisposition.RejectedAuthority,
            operation,
            PhysicsSetPositionFlags.None,
            0u,
            UnparentBeforeRouting: false,
            ApplyPlacementFrameBeforeRouting: false,
            LeaveWorld: false,
            TeleportHookPhase: RuntimeTeleportHookPhase.None,
            StopInterpolating: false,
            ConstrainPhase: RuntimePositionConstrainPhase.None,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            reporting);

    private static RuntimeAuthoritativePositionRoute RejectedData(
        in RuntimeAuthoritativePositionAuthority authority,
        RuntimeSetPositionOperationKind operation,
        bool reporting) => new(
            authority,
            RuntimeAuthoritativePositionDisposition.RejectedData,
            operation,
            PhysicsSetPositionFlags.None,
            0u,
            UnparentBeforeRouting: false,
            ApplyPlacementFrameBeforeRouting: false,
            LeaveWorld: false,
            TeleportHookPhase: RuntimeTeleportHookPhase.None,
            StopInterpolating: false,
            ConstrainPhase: RuntimePositionConstrainPhase.None,
            PreserveHeading: false,
            ZeroVelocity: false,
            SendPositionImmediately: false,
            reporting);
}
