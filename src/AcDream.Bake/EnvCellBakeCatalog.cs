using AcDream.Core.Rendering.Wb;

namespace AcDream.Bake;

/// <summary>
/// The identity-bearing portion of one EnvCell DAT record needed by the bake.
/// Position, portals, and static objects are instance data and intentionally do
/// not participate in shared shell geometry.
/// </summary>
public sealed record EnvCellBakeSource(
    uint FileId,
    uint EnvironmentId,
    ushort CellStructure,
    IReadOnlyList<ushort> Surfaces);

public sealed record EnvCellGeometryGroup(
    ulong GeometryId,
    uint EnvironmentId,
    ushort CellStructure,
    ushort[] Surfaces,
    uint[] FileIds);

public sealed class EnvCellBakeCatalog
{
    public IReadOnlyList<EnvCellGeometryGroup> Groups { get; }
    public int CellCount { get; }
    public int UniqueGeometryCount => Groups.Count;
    public int AliasCount => CellCount - UniqueGeometryCount;

    private EnvCellBakeCatalog(IReadOnlyList<EnvCellGeometryGroup> groups, int cellCount)
    {
        Groups = groups;
        CellCount = cellCount;
    }

    public static EnvCellBakeCatalog Build(IEnumerable<EnvCellBakeSource> sources) =>
        Build(sources, EnvCellGeometryIdentity.Compute);

    public static EnvCellBakeCatalog Build(
        IEnumerable<EnvCellBakeSource> sources,
        Func<uint, ushort, IReadOnlyList<ushort>, ulong> computeIdentity)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(computeIdentity);

        var builder = new EnvCellBakeCatalogBuilder(computeIdentity);
        foreach (var source in sources)
            builder.Add(source);
        return builder.Build();
    }

    internal static EnvCellBakeCatalog Create(
        IReadOnlyList<EnvCellGeometryGroup> groups,
        int cellCount) =>
        new(groups, cellCount);
}

public sealed class EnvCellBakeCatalogBuilder
{
    private readonly Func<uint, ushort, IReadOnlyList<ushort>, ulong> _computeIdentity;
    private readonly Dictionary<ulong, MutableGroup> _byGeometry = new();
    private readonly HashSet<uint> _fileIds = new();
    private int _cellCount;
    private bool _built;

    public EnvCellBakeCatalogBuilder()
        : this(EnvCellGeometryIdentity.Compute)
    {
    }

    public EnvCellBakeCatalogBuilder(
        Func<uint, ushort, IReadOnlyList<ushort>, ulong> computeIdentity)
    {
        ArgumentNullException.ThrowIfNull(computeIdentity);
        _computeIdentity = computeIdentity;
    }

    public void Add(EnvCellBakeSource source)
    {
        if (_built)
            throw new InvalidOperationException("the EnvCell bake catalog is already complete");

        if (!_fileIds.Add(source.FileId))
            throw new InvalidDataException($"duplicate EnvCell file id 0x{source.FileId:X8}");

        ulong geometryId = _computeIdentity(
            source.EnvironmentId,
            source.CellStructure,
            source.Surfaces);

        if (_byGeometry.TryGetValue(geometryId, out var existing))
        {
            if (existing.EnvironmentId != source.EnvironmentId ||
                existing.CellStructure != source.CellStructure ||
                !SurfacesEqual(existing.Surfaces, source.Surfaces))
            {
                throw new InvalidDataException(
                    $"EnvCell geometry identity collision 0x{geometryId:X16}: " +
                    $"cell 0x{existing.FileIds[0]:X8} and cell 0x{source.FileId:X8} " +
                    "have different environment/cell-structure/surface tuples");
            }

            existing.FileIds.Add(source.FileId);
        }
        else
        {
            _byGeometry.Add(
                geometryId,
                new MutableGroup(
                    geometryId,
                    source.EnvironmentId,
                    source.CellStructure,
                    source.Surfaces.ToArray(),
                    new List<uint> { source.FileId }));
        }

        _cellCount++;
    }

    public EnvCellBakeCatalog Build()
    {
        if (_built)
            throw new InvalidOperationException("the EnvCell bake catalog is already complete");
        _built = true;

        foreach (var group in _byGeometry.Values)
            group.FileIds.Sort();

        var groups = _byGeometry.Values
            .Select(group => new EnvCellGeometryGroup(
                group.GeometryId,
                group.EnvironmentId,
                group.CellStructure,
                group.Surfaces,
                group.FileIds.ToArray()))
            .OrderBy(group => group.FileIds[0])
            .ToArray();

        return EnvCellBakeCatalog.Create(groups, _cellCount);
    }

    private static bool SurfacesEqual(
        ReadOnlySpan<ushort> expected,
        IReadOnlyList<ushort> actual)
    {
        if (expected.Length != actual.Count)
            return false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                return false;
        }
        return true;
    }

    private sealed record MutableGroup(
        ulong GeometryId,
        uint EnvironmentId,
        ushort CellStructure,
        ushort[] Surfaces,
        List<uint> FileIds);
}
