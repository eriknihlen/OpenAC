using AcDream.App.UI;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;

namespace AcDream.App.World;

internal sealed class InventoryWorldDropProjectionController : IDisposable
{
    private readonly ItemInteractionController _interaction;
    private readonly ClientObjectTable _objects;
    private readonly LiveEntityRuntime _runtime;
    private readonly LiveEntityHydrationController _hydration;
    private readonly SelectionState _selection;
    private readonly PendingSplitToWorldProjection _pending;
    private readonly Func<double> _now;
    private bool _disposed;

    public InventoryWorldDropProjectionController(
        ItemInteractionController interaction,
        ClientObjectTable objects,
        LiveEntityRuntime runtime,
        LiveEntityHydrationController hydration,
        SelectionState selection,
        Func<double> now)
    {
        _interaction = interaction
            ?? throw new ArgumentNullException(nameof(interaction));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _hydration = hydration
            ?? throw new ArgumentNullException(nameof(hydration));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _pending = new PendingSplitToWorldProjection();

        _interaction.WorldDropDispatched += OnWorldDropDispatched;
        _objects.MoveRequestFailed += OnMoveRequestFailed;
        _objects.Cleared += OnObjectsCleared;
    }

    public bool TryRecoverUnknownPosition(
        WorldSession.EntityPositionUpdate update)
    {
        bool known = _runtime.TryGetSnapshot(update.Guid, out _);
        if (known
            || !_pending.TryResolve(update, _now(), out WorldSession.EntitySpawn spawn))
        {
            return false;
        }

        _hydration.OnCreate(spawn);
        bool recovered = _runtime.TryGetSnapshot(update.Guid, out _);
        if (recovered)
        {
            _selection.Select(update.Guid, SelectionChangeSource.System);
        }
        return recovered;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _objects.Cleared -= OnObjectsCleared;
        _objects.MoveRequestFailed -= OnMoveRequestFailed;
        _interaction.WorldDropDispatched -= OnWorldDropDispatched;
        _pending.Clear();
    }

    private void OnWorldDropDispatched(WorldDropDispatch dispatch)
    {
        bool hasSource = _runtime.TryGetSnapshot(
            dispatch.Request.ItemId,
            out WorldSession.EntitySpawn source);
        if (dispatch.Request.Kind is not InventoryRequestKind.SplitToWorld
            || dispatch.Amount == 0
            || !hasSource)
        {
            return;
        }

        _pending.Record(
            dispatch.Request.Token,
            source,
            dispatch.Amount,
            _now());
    }

    private void OnMoveRequestFailed(MoveRequestFailure failure) =>
        _pending.ClearSource(failure.ItemId);

    private void OnObjectsCleared() => _pending.Clear();
}

internal sealed class PendingSplitToWorldProjection
{
    internal const double RetailRecognitionSeconds = 10.0;

    private PendingSplit? _value;

    internal bool HasPending => _value is not null;

    internal void Record(
        ulong requestToken,
        WorldSession.EntitySpawn source,
        uint amount,
        double now)
    {
        if (requestToken == 0
            || source.Guid == 0
            || amount == 0
            || !double.IsFinite(now))
        {
            _value = null;
            return;
        }

        _value = new PendingSplit(requestToken, source, amount, now);
    }

    internal bool TryResolve(
        WorldSession.EntityPositionUpdate update,
        double now,
        out WorldSession.EntitySpawn spawn)
    {
        spawn = default;
        if (_value is not { } pending)
            return false;

        double age = now - pending.RequestTime;
        if (!double.IsFinite(age)
            || age < 0
            || age >= RetailRecognitionSeconds)
        {
            _value = null;
            return false;
        }

        if (update.Guid == 0 || update.Guid == pending.Source.Guid)
            return false;

        _value = null;
        spawn = BuildSpawn(pending.Source, pending.Amount, update);
        return true;
    }

    internal void ClearSource(uint sourceGuid)
    {
        if (_value is { } pending && pending.Source.Guid == sourceGuid)
            _value = null;
    }

    internal void Clear() => _value = null;

    private static WorldSession.EntitySpawn BuildSpawn(
        WorldSession.EntitySpawn source,
        uint amount,
        WorldSession.EntityPositionUpdate update)
    {
        PhysicsSpawnData? physics = source.Physics is { } sourcePhysics
            ? sourcePhysics with
            {
                Position = update.Position,
                Parent = null,
                Velocity = update.Velocity,
                Timestamps = sourcePhysics.Timestamps with
                {
                    Position = update.PositionSequence,
                    Teleport = update.TeleportSequence,
                    ForcePosition = update.ForcePositionSequence,
                    Instance = update.InstanceSequence,
                    Movement = 0,
                    ServerControlledMove = 0,
                },
            }
            : null;

        return source with
        {
            Guid = update.Guid,
            Position = update.Position,
            StackSize = checked((int)amount),
            ContainerId = 0u,
            WielderId = 0u,
            CurrentWieldedLocation = 0u,
            InstanceSequence = update.InstanceSequence,
            MovementSequence = 0,
            ServerControlSequence = 0,
            PositionSequence = update.PositionSequence,
            ParentGuid = null,
            ParentLocation = null,
            PlacementId = update.PlacementId,
            Physics = physics,
        };
    }

    private readonly record struct PendingSplit(
        ulong RequestToken,
        WorldSession.EntitySpawn Source,
        uint Amount,
        double RequestTime);
}
