using System.Numerics;
using AcDream.Core.Content;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;

namespace AcDream.Runtime.Maps;

/// <summary>
/// The plugin dungeon-map API over one game runtime, handed out by every
/// host: which landblock the character stands in, whether a cell is a sealed
/// dungeon's, and a landblock's floorplan. A host binds the runtime and the
/// game files as they come to exist; until each is bound the members that
/// need it answer the inert defaults. The plans are data that never changes,
/// so each is projected to plugin records once and handed out unchanged.
/// </summary>
internal sealed class RuntimeDungeonMapAutomation : IDungeonMapAutomation
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, PluginDungeonFloorplan> _projected = new();
    /// <summary>
    /// How many landblocks' indoor cells are kept; the landblock read
    /// longest ago goes first once it is full.
    /// </summary>
    internal const int MaximumCachedIndoorLandblocks = 64;

    private readonly Dictionary<uint, IReadOnlyList<PluginIndoorCell>> _indoorCells = new();
    private readonly Queue<uint> _indoorCellOrder = new();
    private GameRuntime? _runtime;
    private IDatObjectSource? _content;
    private object? _contentLock;
    private DungeonFloorplanBuilder? _builder;

    public void Bind(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
            _runtime = runtime;
    }

    public void BindContent(IDatObjectSource content, object contentLock)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentLock);
        lock (_gate)
        {
            if (ReferenceEquals(_content, content) && ReferenceEquals(_contentLock, contentLock))
                return;
            _content = content;
            _contentLock = contentLock;
            _builder = new DungeonFloorplanBuilder(content, contentLock);
            _projected.Clear();
            _indoorCells.Clear();
            _indoorCellOrder.Clear();
        }
    }

    public bool IsSealedDungeon(uint cellId)
    {
        IDatObjectSource? content;
        object? contentLock;
        lock (_gate)
        {
            content = _content;
            contentLock = _contentLock;
        }
        return content is not null
            && contentLock is not null
            && SealedDungeonCells.IsSealedDungeon(content, contentLock, cellId);
    }

    /// <summary>
    /// The landblock of the character's body, which every client carries
    /// between the server's updates; zero until there is a body.
    /// </summary>
    public uint CurrentLandblockId
    {
        get
        {
            GameRuntime? runtime;
            lock (_gate)
                runtime = _runtime;
            if (runtime is null)
                return 0u;
            RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
            return movement.HasController
                ? movement.Position.ObjCellId & 0xFFFF0000u
                : 0u;
        }
    }

    public IReadOnlyList<PluginIndoorCell> CaptureIndoorCells(uint landblockId)
    {
        uint landblock = landblockId & 0xFFFF0000u;
        IDatObjectSource? content;
        object? contentLock;
        lock (_gate)
        {
            if (_indoorCells.TryGetValue(landblock, out IReadOnlyList<PluginIndoorCell>? kept))
                return kept;
            content = _content;
            contentLock = _contentLock;
        }
        if (content is null || contentLock is null)
            return Array.Empty<PluginIndoorCell>();

        // Read-only, because every plugin is handed the same list.
        IReadOnlyList<PluginIndoorCell> cells =
            Array.AsReadOnly(ReadIndoorCells(content, contentLock, landblock));
        lock (_gate)
        {
            // Content replaced while this was read: hand back what was read,
            // and keep nothing from the files that are gone.
            if (!ReferenceEquals(_content, content))
                return cells;
            if (_indoorCells.TryGetValue(landblock, out IReadOnlyList<PluginIndoorCell>? kept))
                return kept;
            if (_indoorCells.Count >= MaximumCachedIndoorLandblocks)
                _indoorCells.Remove(_indoorCellOrder.Dequeue());
            _indoorCells[landblock] = cells;
            _indoorCellOrder.Enqueue(landblock);
            return cells;
        }
    }

    /// <summary>
    /// The landblock's cell count from its information file, then each cell
    /// the count covers, as stored. A cell the files do not have is left out.
    /// </summary>
    private static PluginIndoorCell[] ReadIndoorCells(
        IDatObjectSource content,
        object contentLock,
        uint landblock)
    {
        var cells = new List<PluginIndoorCell>();
        LandBlockInfo? info;
        lock (contentLock)
            info = content.Get<LandBlockInfo>(landblock | 0xFFFEu);
        if (info is null || info.NumCells == 0)
            return [];
        uint firstCell = landblock | 0x0100u;
        for (uint offset = 0; offset < info.NumCells; offset++)
        {
            uint cellId = firstCell + offset;
            // One cell at a time, so a large dungeon does not hold the shared
            // files away from everything else while it is read.
            EnvCell? envCell;
            lock (contentLock)
                envCell = content.Get<EnvCell>(cellId);
            if (envCell is null)
                continue;
            cells.Add(new PluginIndoorCell(
                cellId,
                envCell.EnvironmentId,
                envCell.CellStructure,
                envCell.Position?.Origin ?? Vector3.Zero,
                envCell.Position?.Orientation ?? Quaternion.Identity,
                envCell.Flags.HasFlag(EnvCellFlags.SeenOutside)));
        }
        return cells.ToArray();
    }

    public PluginDungeonFloorplan CaptureFloorplan(uint landblockId)
    {
        uint landblock = landblockId & 0xFFFF0000u;
        DungeonFloorplanBuilder? builder;
        lock (_gate)
        {
            if (_projected.TryGetValue(landblock, out PluginDungeonFloorplan? kept))
                return kept;
            builder = _builder;
        }
        if (builder is null)
            return PluginDungeonFloorplan.Empty;

        // The builder keeps its own plan per landblock and reads the files
        // under the lock it was given; the projection to plugin records is
        // made off every lock and kept here so a plugin that asks each frame
        // gets the same object back.
        DungeonFloorplan? plan = builder.TryBuild(landblock);
        PluginDungeonFloorplan projected = plan is null
            ? PluginDungeonFloorplan.Empty
            : Project(plan);
        lock (_gate)
        {
            if (!ReferenceEquals(_builder, builder))
                return projected;
            if (_projected.TryGetValue(landblock, out PluginDungeonFloorplan? kept))
                return kept;
            _projected[landblock] = projected;
            return projected;
        }
    }

    private static PluginDungeonFloorplan Project(DungeonFloorplan plan)
    {
        var layers = new PluginDungeonLayer[plan.Layers.Length];
        for (int index = 0; index < layers.Length; index++)
        {
            DungeonFloorplanLayer layer = plan.Layers[index];
            var floors = new IReadOnlyList<Vector2>[layer.Floors.Length];
            for (int floor = 0; floor < floors.Length; floor++)
                floors[floor] = layer.Floors[floor];
            var walls = new PluginDungeonWall[layer.Walls.Length];
            for (int wall = 0; wall < walls.Length; wall++)
                walls[wall] = new PluginDungeonWall(layer.Walls[wall].Start, layer.Walls[wall].End);
            layers[index] = new PluginDungeonLayer(layer.Z, floors, walls);
        }

        var cells = new PluginDungeonCell[plan.Cells.Length];
        for (int index = 0; index < cells.Length; index++)
        {
            DungeonFloorplanCell cell = plan.Cells[index];
            cells[index] = new PluginDungeonCell(cell.CellId, cell.Center, cell.LayerZ);
        }

        return new PluginDungeonFloorplan(
            plan.LandblockId,
            layers,
            cells,
            plan.Bounds.IsEmpty ? Vector3.Zero : plan.Bounds.Min,
            plan.Bounds.IsEmpty ? Vector3.Zero : plan.Bounds.Max);
    }
}
