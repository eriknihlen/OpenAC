using AcDream.Core.Lighting;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Streaming;

public sealed class LandblockStaticPresentationPublication
{
    private readonly Dictionary<uint, WorldEntity> _entities;
    private readonly Dictionary<uint, WorldEntitySnapshot> _snapshots;
    private readonly uint[] _orderedPreviouslyActiveIds;
    private readonly Dictionary<uint, WorldEntitySnapshot> _replacementActive;

    internal LandblockStaticPresentationPublication(
        object owner,
        LandblockPhysicsPublication physicsPublication,
        Dictionary<uint, WorldEntity> entities,
        Dictionary<uint, WorldEntitySnapshot> snapshots,
        IReadOnlyDictionary<uint, WorldEntitySnapshot> priorActive,
        HashSet<uint> previouslyActiveIds,
        uint[] orderedPreviouslyActiveIds)
    {
        Owner = owner;
        PhysicsPublication = physicsPublication;
        _entities = entities;
        _snapshots = snapshots;
        PriorActive = priorActive;
        PreviouslyActiveIds = previouslyActiveIds;
        _orderedPreviouslyActiveIds = orderedPreviouslyActiveIds;
        _replacementActive = new Dictionary<uint, WorldEntitySnapshot>(
            physicsPublication.Build.Landblock.Entities.Count);
    }

    internal object Owner { get; }
    internal LandblockPhysicsPublication PhysicsPublication { get; }
    internal IReadOnlyDictionary<uint, WorldEntitySnapshot> PriorActive { get; }
    internal Dictionary<uint, WorldEntity> MutableEntities => _entities;
    internal Dictionary<uint, WorldEntitySnapshot> MutableSnapshots => _snapshots;
    internal HashSet<uint> PreviouslyActiveIds { get; }
    internal IReadOnlyList<uint> OrderedPreviouslyActiveIds =>
        _orderedPreviouslyActiveIds;
    internal IReadOnlyList<KeyValuePair<uint, WorldEntitySnapshot>>
        OrderedSnapshots { get; set; } =
            Array.Empty<KeyValuePair<uint, WorldEntitySnapshot>>();
    internal Dictionary<uint, WorldEntitySnapshot> ReplacementActive =>
        _replacementActive;
    internal int PreparationCursor { get; set; }
    internal bool PreparationCommitted { get; set; }
    internal int PriorCleanupCursor { get; set; }
    internal int PluginCursor { get; set; }
    internal bool BeginCommitted { get; set; }
    internal bool CompletionCommitted { get; set; }

    public uint LandblockId => PhysicsPublication.LandblockId;
    public IReadOnlyDictionary<uint, WorldEntity> Entities => _entities;
    public IReadOnlyDictionary<uint, WorldEntitySnapshot> Snapshots => _snapshots;
}

public readonly record struct LandblockStaticPresentationDiagnostics(
    long BeginCount,
    long CompleteCount,
    long LightReplacementCount,
    long PluginSpawnCount,
    long PluginRefreshCount,
    long PluginRemovalCount,
    int ActiveLandblockCount,
    int ActiveEntityCount);

public sealed class LandblockStaticPresentationPublisher
{
    private readonly object _receiptOwner = new();
    private readonly LightingHookSink _lighting;
    private readonly TranslucencyFadeManager _translucency;
    private readonly WorldGameState _worldState;
    private readonly WorldEvents _worldEvents;
    private readonly Dictionary<uint, Dictionary<uint, WorldEntitySnapshot>>
        _activeByLandblock = new();
    private readonly Dictionary<uint, uint> _landblockByEntityId = new();

    private long _beginCount;
    private long _completeCount;
    private long _lightReplacementCount;
    private long _pluginSpawnCount;
    private long _pluginRefreshCount;
    private long _pluginRemovalCount;

    public LandblockStaticPresentationPublisher(
        LightingHookSink lighting,
        TranslucencyFadeManager translucency,
        WorldGameState worldState,
        WorldEvents worldEvents)
    {
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        _translucency = translucency
            ?? throw new ArgumentNullException(nameof(translucency));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _worldEvents = worldEvents ?? throw new ArgumentNullException(nameof(worldEvents));
    }

    public LandblockStaticPresentationDiagnostics Diagnostics => new(
        _beginCount,
        _completeCount,
        _lightReplacementCount,
        _pluginSpawnCount,
        _pluginRefreshCount,
        _pluginRemovalCount,
        _activeByLandblock.Count,
        _landblockByEntityId.Count);

    internal bool MatchesResources(
        LightingHookSink lighting,
        TranslucencyFadeManager translucency) =>
        ReferenceEquals(_lighting, lighting)
        && ReferenceEquals(_translucency, translucency);

    public LandblockStaticPresentationPublication PreparePublication(
        LandblockPhysicsPublication physicsPublication)
    {
        LandblockStaticPresentationPublication publication =
            CreatePublication(physicsPublication);
        while (!AdvancePreparationOne(publication))
        {
        }
        return publication;
    }

    internal LandblockStaticPresentationPublication CreatePublication(
        LandblockPhysicsPublication physicsPublication)
    {
        ArgumentNullException.ThrowIfNull(physicsPublication);

        uint canonical = Canonicalize(physicsPublication.LandblockId);
        _activeByLandblock.TryGetValue(
            canonical,
            out Dictionary<uint, WorldEntitySnapshot>? active);
        var entities = new Dictionary<uint, WorldEntity>();
        var snapshots = new Dictionary<uint, WorldEntitySnapshot>();
        HashSet<uint> previous = active is not null
            ? active.Keys.ToHashSet()
            : new HashSet<uint>();
        uint[] orderedPrevious = previous.Order().ToArray();
        return new LandblockStaticPresentationPublication(
            _receiptOwner,
            physicsPublication,
            entities,
            snapshots,
            active ?? new Dictionary<uint, WorldEntitySnapshot>(),
            previous,
            orderedPrevious);
    }

    internal bool AdvancePreparationOne(
        LandblockStaticPresentationPublication publication)
    {
        ValidateReceipt(publication);
        if (publication.PreparationCommitted)
            return true;

        IReadOnlyList<WorldEntity> source =
            publication.PhysicsPublication.Build.Landblock.Entities;
        uint canonical = Canonicalize(publication.LandblockId);
        if (publication.PreparationCursor < source.Count)
        {
            WorldEntity entity = source[publication.PreparationCursor];
            if (entity.ServerGuid != 0)
            {
                publication.PreparationCursor++;
                return false;
            }
            if (publication.Entities.ContainsKey(entity.Id))
            {
                throw new InvalidOperationException(
                    $"Landblock 0x{canonical:X8} contains duplicate DAT-static ID " +
                    $"0x{entity.Id:X8}.");
            }
            if (_landblockByEntityId.TryGetValue(entity.Id, out uint owner)
                && owner != canonical)
            {
                throw new InvalidOperationException(
                    $"DAT-static ID 0x{entity.Id:X8} is already owned by " +
                    $"landblock 0x{owner:X8}.");
            }
            if (publication.PriorActive.TryGetValue(
                    entity.Id,
                    out WorldEntitySnapshot retained)
                && retained.SourceId != entity.SourceGfxObjOrSetupId)
            {
                throw new InvalidOperationException(
                    $"Retained DAT-static ID 0x{entity.Id:X8} changed source " +
                    $"from 0x{retained.SourceId:X8} to " +
                    $"0x{entity.SourceGfxObjOrSetupId:X8}.");
            }

            publication.MutableEntities.Add(entity.Id, entity);
            publication.MutableSnapshots.Add(entity.Id, Snapshot(entity));
            publication.PreparationCursor++;
            return false;
        }

        publication.OrderedSnapshots = publication.Snapshots
            .OrderBy(static pair => pair.Key)
            .ToArray();
        publication.PreparationCommitted = true;
        return true;
    }

    public void BeginPublication(LandblockStaticPresentationPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.PreparationCommitted)
            throw new InvalidOperationException(
                "Static presentation cannot begin before preparation commits.");
        while (!AdvanceBeginOne(publication))
        {
        }
    }

    internal bool AdvanceBeginOne(
        LandblockStaticPresentationPublication publication)
    {
        ValidateReceipt(publication);
        if (publication.BeginCommitted)
            return true;

        if (publication.PriorCleanupCursor
            < publication.OrderedPreviouslyActiveIds.Count)
        {
            uint id = publication.OrderedPreviouslyActiveIds[
                publication.PriorCleanupCursor];
            bool retained = publication.Entities.ContainsKey(id);
            _lighting.UnregisterOwner(id, forgetState: !retained);
            if (!retained)
            {
                _translucency.ClearEntity(id);
                _worldState.RemoveById(id);
                _worldEvents.ForgetEntity(id);
                _landblockByEntityId.Remove(id);
                _pluginRemovalCount++;
            }
            publication.PriorCleanupCursor++;
            return false;
        }

        publication.BeginCommitted = true;
        _beginCount++;
        return true;
    }

    public void PrepareEntityBeforeCollision(
        LandblockStaticPresentationPublication publication,
        WorldEntity entity)
    {
        ValidateReceipt(publication);
        ArgumentNullException.ThrowIfNull(entity);
        if (!publication.BeginCommitted)
            throw new InvalidOperationException(
                "Static presentation cannot prepare an entity before begin commits.");
        if (entity.ServerGuid != 0)
            return;
        if (!publication.Entities.TryGetValue(entity.Id, out WorldEntity? expected)
            || !ReferenceEquals(expected, entity))
        {
            throw new InvalidOperationException(
                $"DAT-static entity 0x{entity.Id:X8} does not belong to this publication.");
        }

        bool retained = publication.PreviouslyActiveIds.Contains(entity.Id);
        _lighting.UnregisterOwner(entity.Id, forgetState: !retained);
        if (!retained)
            _translucency.ClearEntity(entity.Id);

        PhysicsDatBundle datBundle = publication.PhysicsPublication.Build.Landblock.PhysicsDats
            ?? PhysicsDatBundle.Empty;
        uint sourceId = entity.SourceGfxObjOrSetupId;
        if ((sourceId & 0xFF000000u) == 0x02000000u
            && datBundle.Setups.TryGetValue(sourceId, out var setup))
        {
            IReadOnlyList<LightSource> lights = LightInfoLoader.Load(
                setup,
                ownerId: entity.Id,
                entityPosition: entity.Position,
                entityRotation: entity.Rotation,
                cellId: entity.ParentCellId ?? 0u);
            for (int i = 0; i < lights.Count; i++)
                _lighting.RegisterOwnedLight(lights[i]);
        }
        _lightReplacementCount++;
    }

    public void CompletePublication(
        LandblockStaticPresentationPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.BeginCommitted
            || !publication.PhysicsPublication.CompletionCommitted)
        {
            throw new InvalidOperationException(
                "Static presentation requires completed physics publication.");
        }
        while (!AdvanceCompleteOne(publication))
        {
        }
    }

    internal bool AdvanceCompleteOne(
        LandblockStaticPresentationPublication publication)
    {
        ValidateReceipt(publication);
        if (!publication.BeginCommitted
            || !publication.PhysicsPublication.CompletionCommitted)
        {
            throw new InvalidOperationException(
                "Static presentation requires completed physics publication.");
        }
        if (publication.CompletionCommitted)
            return true;

        if (publication.PluginCursor < publication.OrderedSnapshots.Count)
        {
            (uint id, WorldEntitySnapshot snapshot) =
                publication.OrderedSnapshots[publication.PluginCursor];
            _worldState.Add(snapshot);
            if (publication.PreviouslyActiveIds.Contains(id))
            {
                _worldEvents.UpsertCurrent(snapshot);
                _pluginRefreshCount++;
            }
            else
            {
                _worldEvents.FireEntitySpawned(snapshot);
                _pluginSpawnCount++;
            }
            _landblockByEntityId[id] = Canonicalize(publication.LandblockId);
            publication.ReplacementActive[id] = snapshot;
            publication.PluginCursor++;
            return false;
        }

        uint canonical = Canonicalize(publication.LandblockId);
        if (publication.ReplacementActive.Count == 0)
            _activeByLandblock.Remove(canonical);
        else
            _activeByLandblock[canonical] = publication.ReplacementActive;
        publication.CompletionCommitted = true;
        _completeCount++;
        return true;
    }

    public void RemoveLighting(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.ServerGuid == 0)
            _lighting.UnregisterOwner(entity.Id);
    }

    public void RemoveTranslucency(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.ServerGuid == 0)
            _translucency.ClearEntity(entity.Id);
    }

    public void RemovePluginProjection(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.ServerGuid != 0)
            return;

        _worldState.RemoveById(entity.Id);
        _worldEvents.ForgetEntity(entity.Id);
        if (_landblockByEntityId.Remove(entity.Id, out uint landblockId)
            && _activeByLandblock.TryGetValue(
                landblockId,
                out Dictionary<uint, WorldEntitySnapshot>? active))
        {
            active.Remove(entity.Id);
            if (active.Count == 0)
                _activeByLandblock.Remove(landblockId);
        }
        _pluginRemovalCount++;
    }

    private void ValidateReceipt(LandblockStaticPresentationPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication.Owner, _receiptOwner))
        {
            throw new ArgumentException(
                "The static presentation receipt belongs to another publisher.",
                nameof(publication));
        }
    }

    private static WorldEntitySnapshot Snapshot(WorldEntity entity) => new(
        Id: entity.Id,
        SourceId: entity.SourceGfxObjOrSetupId,
        Position: entity.Position,
        Rotation: entity.Rotation);

    private static uint Canonicalize(uint landblockId) =>
        (landblockId & 0xFFFF0000u) | 0xFFFFu;
}
