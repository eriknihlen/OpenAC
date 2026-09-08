using AcDream.Core.Physics;
using AcDream.Runtime.World;
using AcDream.Runtime.Session;

namespace AcDream.Runtime;

public readonly record struct RuntimeEntityIdentity(
    uint ServerGuid,
    uint LocalEntityId,
    ushort Incarnation);

public readonly record struct RuntimeEntitySnapshot(
    RuntimeEntityIdentity Identity,
    uint CellId,
    uint PhysicsState,
    Position? Position);

public interface IRuntimeEntityVisitor
{
    void Visit(in RuntimeEntitySnapshot entity);
}

public interface IRuntimeEntityView
{
    int Count { get; }

    int MaterializedCount { get; }

    bool TryGet(uint serverGuid, out RuntimeEntitySnapshot entity);

    void Visit(IRuntimeEntityVisitor visitor);
}

public readonly record struct RuntimeInventoryItemSnapshot(
    uint ObjectId,
    ushort Incarnation,
    string Name,
    uint ContainerId,
    int ContainerSlot,
    uint WielderId,
    uint EquipLocation,
    int StackSize,
    int Value);

public interface IRuntimeInventoryVisitor
{
    void Visit(in RuntimeInventoryItemSnapshot item);
}

public interface IRuntimeInventoryView
{
    int ObjectCount { get; }

    int ContainerCount { get; }

    bool TryGet(uint objectId, out RuntimeInventoryItemSnapshot item);

    void Visit(IRuntimeInventoryVisitor visitor);
}

public interface IRuntimeChatView
{
    long Revision { get; }

    int Count { get; }
}

public readonly record struct RuntimeMovementSnapshot(
    bool HasController,
    uint LocalEntityId,
    Position Position,
    System.Numerics.Vector3 Velocity,
    bool IsAirborne,
    double SimulationTimeSeconds,
    long Revision = 0,
    bool AutoRunActive = false,
    bool HasCommandInput = false,
    Gameplay.MovementInput CommandInput = default);

public interface IRuntimeMovementView
{
    RuntimeMovementSnapshot Snapshot { get; }

    bool IsStandingStill { get; }

    Gameplay.JumpChargeSnapshot JumpCharge { get; }
}

public enum RuntimePortalKind
{
    None,
    Login,
    Portal,
}

public readonly record struct RuntimeTeleportDestination(
    uint EntityGuid,
    ushort InstanceSequence,
    ushort PositionSequence,
    ushort TeleportSequence,
    ushort ForcePositionSequence,
    Position Position)
{
    public uint CellId => Position.ObjCellId;
}

public readonly record struct RuntimeDestinationReadiness(
    long Generation,
    uint DestinationCell,
    bool IsIndoor,
    bool IsUnhydratable,
    int RequiredRenderRadius,
    bool IsRenderNeighborhoodReady,
    bool AreCompositeTexturesReady,
    bool IsCollisionReady)
{
    public bool HasDestination => DestinationCell != 0u;

    public bool IsReady => HasDestination
        && (IsUnhydratable
            || (IsRenderNeighborhoodReady
                && AreCompositeTexturesReady
                && IsCollisionReady));
}

[Flags]
public enum RuntimeWorldHostAcknowledgementStage
{
    None = 0,
    ProjectionRegistered = 1 << 0,
    SimulationReleaseProjected = 1 << 1,
    DestinationReservationReleased = 1 << 2,
    TerminalProjected = 1 << 3,
}

public readonly record struct RuntimeWorldHostProjectionToken(
    long Generation,
    uint DestinationCell)
{
    public bool IsValid => Generation != 0 && DestinationCell != 0u;
}

public readonly record struct RuntimeWorldHostAcknowledgement(
    RuntimeWorldHostProjectionToken Projection,
    RuntimeWorldHostAcknowledgementStage Stage);

public readonly record struct RuntimeWorldHostProjectionSnapshot(
    RuntimeWorldHostProjectionToken Token,
    RuntimeWorldHostAcknowledgementStage PendingAcknowledgements,
    bool ProjectionRegistered,
    bool SimulationReleaseProjected,
    bool DestinationReservationReleased,
    bool IsSuperseding)
{
    public int PendingAcknowledgementCount =>
        Count(PendingAcknowledgements);

    private static int Count(RuntimeWorldHostAcknowledgementStage stages)
    {
        int value = (int)stages;
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }
}

public readonly record struct RuntimePortalSnapshot(
    long Generation,
    RuntimePortalKind Kind,
    RuntimeDestinationReadiness Readiness,
    bool Materialized,
    bool Completed,
    bool Cancelled,
    bool WorldViewportObserved,
    bool WorldSimulationAvailable,
    int InvariantFailureCount,
    bool WaitCueShown,
    int PortalMaterializationCount)
{
    public static RuntimePortalSnapshot Idle { get; } = new(
        Generation: 0,
        RuntimePortalKind.None,
        Readiness: default,
        Materialized: false,
        Completed: false,
        Cancelled: false,
        WorldViewportObserved: false,
        WorldSimulationAvailable: true,
        InvariantFailureCount: 0,
        WaitCueShown: false,
        PortalMaterializationCount: 0);

    public uint DestinationCell => Readiness.DestinationCell;
    public bool IsReady => Readiness.IsReady;
    public bool IsMaterialized => Materialized;
    public bool IsCompleted => Completed;
    public bool IsCancelled => Cancelled;
    public bool IsWorldVisible => WorldViewportObserved;
    public bool IsActive => Generation != 0 && !Cancelled;
}

public interface IRuntimePortalView
{
    RuntimePortalSnapshot Snapshot { get; }
    RuntimeWorldTransitOwnershipSnapshot Ownership { get; }
}

public readonly record struct RuntimeStateCheckpoint(
    RuntimeGenerationToken Generation,
    RuntimeLifecycleState Lifecycle,
    ulong FrameNumber,
    int EntityCount,
    int MaterializedEntityCount,
    int InventoryObjectCount,
    int InventoryContainerCount,
    RuntimeInventoryStateSnapshot InventoryState,
    RuntimeCharacterSnapshot Character,
    RuntimeSocialSnapshot Social,
    long ChatRevision,
    int ChatCount,
    RuntimeActionSnapshot Actions,
    RuntimeMovementSnapshot Movement,
    RuntimeWorldEnvironmentSnapshot Environment,
    RuntimeWorldEnvironmentOwnershipSnapshot EnvironmentOwnership,
    RuntimePortalSnapshot Portal,
    RuntimeWorldTransitOwnershipSnapshot TransitOwnership,
    RuntimeFellowshipSnapshot Fellowship,
    RuntimeAllegianceSnapshot Allegiance);

public interface IGameRuntimeView
{
    RuntimeGenerationToken Generation { get; }

    RuntimeLifecycleSnapshot Lifecycle { get; }

    IGameRuntimeClock Clock { get; }

    IRuntimeEntityView Entities { get; }

    IRuntimeInventoryView Inventory { get; }

    IRuntimeInventoryStateView InventoryState { get; }

    IRuntimeCharacterView Character { get; }

    IRuntimeSocialView Social { get; }

    IRuntimeChatView Chat { get; }

    IRuntimeCharacterSelectionView CharacterSelection =>
        throw new NotSupportedException(
            "This runtime view does not project character selection.");

    IRuntimeCharacterCreationView CharacterCreation =>
        throw new NotSupportedException(
            "This runtime view does not project character creation.");

    IRuntimeFellowshipView Fellowship { get; }

    IRuntimeAllegianceView Allegiance { get; }

    IRuntimeActionView Actions { get; }

    IRuntimeMovementView Movement { get; }

    IRuntimeWorldEnvironmentView Environment { get; }

    IRuntimePortalView Portal { get; }

    RuntimeStateCheckpoint CaptureCheckpoint();
}
