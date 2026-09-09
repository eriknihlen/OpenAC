using AcDream.Core.Combat;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime;

public readonly record struct RuntimeEventStamp(
    RuntimeGenerationToken Generation,
    ulong Sequence,
    ulong FrameNumber);

public enum RuntimeCommandDomain
{
    Session = 0,
    Selection = 1,
    Combat = 2,
    Movement = 3,
    Chat = 4,
    Portal = 5,
    InventoryState = 6,
    Spellbook = 7,
    Character = 8,
    Social = 9,
    Magic = 10,
    Fellowship = 11,
    Allegiance = 12,
}

public readonly record struct RuntimeLifecycleDelta(
    RuntimeEventStamp Stamp,
    RuntimeLifecycleState Previous,
    RuntimeLifecycleState Current);

public readonly record struct RuntimeCommandDelta(
    RuntimeEventStamp Stamp,
    RuntimeCommandDomain Domain,
    int Operation,
    RuntimeCommandStatus Status,
    uint PrimaryObjectId = 0u,
    string? Text = null);

public enum RuntimeEntityChange
{
    Registered,
    Updated,
    Rebucketed,
    Hidden,
    Withdrawn,
    Deleted,
}

public readonly record struct RuntimeEntityDelta(
    RuntimeEventStamp Stamp,
    RuntimeEntityChange Change,
    RuntimeEntitySnapshot Entity);

public readonly record struct RuntimePlacementDelta(
    RuntimeEventStamp Stamp,
    RuntimePlacementProjectionSnapshot Placement);

public enum RuntimeInventoryChange
{
    Added,
    Updated,
    Moved,
    Removed,
    Cleared,
}

public readonly record struct RuntimeInventoryDelta(
    RuntimeEventStamp Stamp,
    RuntimeInventoryChange Change,
    RuntimeInventoryItemSnapshot Item);

public readonly record struct RuntimeChatEntry(
    long Revision,
    uint SenderGuid,
    int Kind,
    string Sender,
    string Text,
    string ChannelName);

public readonly record struct RuntimeChatDelta(
    RuntimeEventStamp Stamp,
    RuntimeChatEntry Entry);

public readonly record struct RuntimeMovementDelta(
    RuntimeEventStamp Stamp,
    RuntimeMovementSnapshot Movement);

public readonly record struct RuntimePortalDelta(
    RuntimeEventStamp Stamp,
    RuntimePortalSnapshot Portal);

public readonly record struct RuntimeCombatDelta(
    RuntimeEventStamp Stamp,
    CombatMode Mode,
    int TrackedTargetCount,
    RuntimeCombatAttackSnapshot Attack);

public interface IRuntimeEventObserver
{
    void OnLifecycle(in RuntimeLifecycleDelta delta);

    void OnCommand(in RuntimeCommandDelta delta);

    void OnEntity(in RuntimeEntityDelta delta);

    void OnInventory(in RuntimeInventoryDelta delta);

    void OnChat(in RuntimeChatDelta delta);

    void OnMovement(in RuntimeMovementDelta delta);

    void OnPortal(in RuntimePortalDelta delta);

    void OnCombat(in RuntimeCombatDelta delta);
}

public interface IRuntimeEventSource
{
    IDisposable Subscribe(IRuntimeEventObserver observer);
}

public enum RuntimeTraceKind
{
    Lifecycle,
    Command,
    Entity,
    Inventory,
    Chat,
    Movement,
    Portal,
    Combat,
    Checkpoint,
}

public readonly record struct RuntimeTraceEntry(
    RuntimeEventStamp Stamp,
    RuntimeTraceKind Kind,
    int Code,
    long Value,
    uint PrimaryObjectId,
    uint SecondaryObjectId,
    string Text);

public sealed class RuntimeTraceRecorder : IRuntimeEventObserver
{
    private readonly List<RuntimeTraceEntry> _entries = [];

    public IReadOnlyList<RuntimeTraceEntry> Entries => _entries;

    public void AddCheckpoint(
        RuntimeEventStamp stamp,
        in RuntimeStateCheckpoint checkpoint)
    {
        _entries.Add(new RuntimeTraceEntry(
            stamp,
            RuntimeTraceKind.Checkpoint,
            (int)checkpoint.Lifecycle,
            checkpoint.ChatRevision,
            (uint)checkpoint.EntityCount,
            (uint)checkpoint.InventoryObjectCount,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"frame={checkpoint.FrameNumber};materialized={checkpoint.MaterializedEntityCount};" +
                $"containers={checkpoint.InventoryContainerCount};chat={checkpoint.ChatCount};" +
                $"inventory-state={checkpoint.InventoryState.ShortcutCount}:" +
                $"{checkpoint.InventoryState.ShortcutRevision}:" +
                $"{checkpoint.InventoryState.ItemManaCount}:" +
                $"{checkpoint.InventoryState.ItemManaRevision};" +
                $"character={checkpoint.Character.CharacterRevision}:" +
                $"{checkpoint.Character.SpellbookRevision}:" +
                $"{checkpoint.Character.LearnedSpellCount}:" +
                $"{checkpoint.Character.SkillCount}:" +
                $"{checkpoint.Character.Options.Revision}:" +
                $"{checkpoint.Character.MovementSkills.Revision};" +
                $"social={checkpoint.Social.FriendsRevision}:" +
                $"{checkpoint.Social.FriendCount}:" +
                $"{checkpoint.Social.SquelchRevision};" +
                $"fellowship={checkpoint.Fellowship.Revision}:" +
                $"{checkpoint.Fellowship.IsInFellowship}:" +
                $"{checkpoint.Fellowship.MemberCount};" +
                $"allegiance={checkpoint.Allegiance.Revision}:" +
                $"{checkpoint.Allegiance.HasProfile}:" +
                $"{checkpoint.Allegiance.RecordCount};" +
                $"actions={checkpoint.Actions.SelectionRevision}:" +
                $"{checkpoint.Actions.SelectedObjectId:X8}:" +
                $"{checkpoint.Actions.CombatRevision}:" +
                $"{(int)checkpoint.Actions.CombatMode}:" +
                $"{checkpoint.Actions.TrackedTargetHealthCount}:" +
                $"{checkpoint.Actions.InteractionRevision}:" +
                $"{(int)checkpoint.Actions.InteractionMode}:" +
                $"{checkpoint.Actions.InteractionSourceObjectId:X8}:" +
                $"{checkpoint.Actions.InteractionTransactions.Revision}:" +
                $"{checkpoint.Actions.InteractionTransactions.LastUseSourceId:X8}:" +
                $"{checkpoint.Actions.InteractionTransactions.LastUseTargetId:X8}:" +
                $"{checkpoint.Actions.InteractionTransactions.AwaitingAppraisalId:X8}:" +
                $"{checkpoint.Actions.InteractionTransactions.CurrentAppraisalId:X8}:" +
                $"{checkpoint.Actions.InteractionTransactions.OutboundCount}:" +
                $"{checkpoint.Actions.InteractionTransactions.PendingPickupToken}:" +
                $"{checkpoint.Actions.CombatAttack.Revision}:" +
                $"{(int)checkpoint.Actions.CombatAttack.RequestedHeight}:" +
                $"{BitConverter.SingleToInt32Bits(checkpoint.Actions.CombatAttack.DesiredPower):X8}:" +
                $"{BitConverter.SingleToInt32Bits(checkpoint.Actions.CombatAttack.PowerBarLevel):X8}:" +
                $"{checkpoint.Actions.CombatAttack.BuildInProgress}:" +
                $"{checkpoint.Actions.CombatAttack.RequestInProgress}:" +
                $"{BitConverter.SingleToInt32Bits(checkpoint.Actions.CombatAttack.RequestedPower):X8}:" +
                $"{checkpoint.Actions.Magic.Revision}:" +
                $"{checkpoint.Actions.Magic.LastRequestedSpellId:X8}:" +
                $"{checkpoint.Actions.Magic.LastRequestedTargetId:X8};" +
                $"environment={checkpoint.Environment.Revision}:" +
                $"{checkpoint.Environment.ActiveDayGroupIndex}:" +
                $"{(int)checkpoint.Environment.Weather}:" +
                $"{(int)checkpoint.Environment.EnvironOverride};" +
                $"environment-owner={checkpoint.EnvironmentOwnership.IsInitialized}:" +
                $"{checkpoint.EnvironmentOwnership.DayGroupDefinitionCount}:" +
                $"{checkpoint.EnvironmentOwnership.ActiveDayGroupCount};" +
                $"portal={checkpoint.Portal.Generation}:{(int)checkpoint.Portal.Kind}:" +
                $"{checkpoint.Portal.DestinationCell:X8}:{checkpoint.Portal.IsReady};" +
                $"transit-owner={checkpoint.TransitOwnership.BufferedTeleportDestinationCount}:" +
                $"{checkpoint.TransitOwnership.PendingTeleportStartCount}:" +
                $"{checkpoint.TransitOwnership.ActiveTeleportCount}:" +
                $"{checkpoint.TransitOwnership.AcceptedTeleportDestinationCount}:" +
                $"{checkpoint.TransitOwnership.ActiveRevealCount}:" +
                $"{checkpoint.TransitOwnership.PendingDestinationReadinessCount}:" +
                $"{checkpoint.TransitOwnership.HostProjectionCount}:" +
                $"{checkpoint.TransitOwnership.PendingHostAcknowledgementCount}")));
    }

    public void OnLifecycle(in RuntimeLifecycleDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Lifecycle,
            (int)delta.Current,
            (int)delta.Previous,
            0u,
            0u,
            string.Empty));

    public void OnCommand(in RuntimeCommandDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Command,
            ((int)delta.Domain << 16) | (delta.Operation & 0xFFFF),
            (int)delta.Status,
            delta.PrimaryObjectId,
            0u,
            delta.Text ?? string.Empty));

    public void OnEntity(in RuntimeEntityDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Entity,
            (int)delta.Change,
            delta.Entity.Identity.Incarnation,
            delta.Entity.Identity.ServerGuid,
            delta.Entity.CellId,
            string.Empty));

    public void OnInventory(in RuntimeInventoryDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Inventory,
            (int)delta.Change,
            delta.Item.StackSize,
            delta.Item.ObjectId,
            delta.Item.ContainerId,
            delta.Item.Name ?? string.Empty));

    public void OnChat(in RuntimeChatDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Chat,
            delta.Entry.Kind,
            delta.Entry.Revision,
            delta.Entry.SenderGuid,
            0u,
            $"{delta.Entry.ChannelName}|{delta.Entry.Sender}|{delta.Entry.Text}"));

    public void OnMovement(in RuntimeMovementDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Movement,
            delta.Movement.IsAirborne ? 1 : 0,
            BitConverter.DoubleToInt64Bits(delta.Movement.SimulationTimeSeconds),
            delta.Movement.LocalEntityId,
            delta.Movement.Position.ObjCellId,
            string.Empty));

    public void OnPortal(in RuntimePortalDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Portal,
            (int)delta.Portal.Kind,
            delta.Portal.Generation,
            delta.Portal.DestinationCell,
            0u,
            $"{delta.Portal.IsReady}:{delta.Portal.IsMaterialized}:" +
            $"{delta.Portal.IsCompleted}:{delta.Portal.IsCancelled}:" +
            $"{delta.Portal.IsWorldVisible}"));

    public void OnCombat(in RuntimeCombatDelta delta) =>
        _entries.Add(new RuntimeTraceEntry(
            delta.Stamp,
            RuntimeTraceKind.Combat,
            (int)delta.Mode,
            delta.Attack.Revision,
            (uint)delta.TrackedTargetCount,
            0u,
            $"{(int)delta.Attack.RequestedHeight}:" +
            $"{BitConverter.SingleToInt32Bits(delta.Attack.DesiredPower):X8}:" +
            $"{BitConverter.SingleToInt32Bits(delta.Attack.PowerBarLevel):X8}:" +
            $"{delta.Attack.BuildInProgress}:" +
            $"{delta.Attack.RequestInProgress}"));
}

/// <summary>Monotonic sequence owner for one runtime instance.</summary>
public sealed class RuntimeEventSequencer
{
    private RuntimeGenerationToken _generation;
    private bool _hasGeneration;
    private ulong _sequence;

    public RuntimeEventStamp Next(
        RuntimeGenerationToken generation,
        ulong frameNumber)
    {
        if (!_hasGeneration || generation != _generation)
        {
            _generation = generation;
            _sequence = 0;
            _hasGeneration = true;
        }

        return new RuntimeEventStamp(
            generation,
            checked(++_sequence),
            frameNumber);
    }

    public ulong LastSequence => _sequence;
}
