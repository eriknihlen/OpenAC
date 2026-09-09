namespace AcDream.Bake.Tests;

public sealed class EnvCellBakeCatalogTests
{
    [Fact]
    public void Build_GroupsEqualGeometryAndPreservesEverySortedFileId()
    {
        var catalog = EnvCellBakeCatalog.Build(
        [
            new(0x2222_0102, 7, 3, new ushort[] { 10, 20 }),
            new(0x1111_0100, 7, 3, new ushort[] { 10, 20 }),
            new(0x1111_0101, 7, 4, new ushort[] { 10, 20 }),
            new(0x2222_0100, 7, 3, new ushort[] { 10, 20 }),
        ]);

        Assert.Equal(4, catalog.CellCount);
        Assert.Equal(2, catalog.UniqueGeometryCount);
        Assert.Equal(2, catalog.AliasCount);
        Assert.Equal(
            new uint[] { 0x1111_0100, 0x2222_0100, 0x2222_0102 },
            catalog.Groups[0].FileIds);
        Assert.Equal(new uint[] { 0x1111_0101 }, catalog.Groups[1].FileIds);
    }

    [Fact]
    public void Build_IsDeterministicAcrossSourceOrder()
    {
        EnvCellBakeSource[] sources =
        [
            new(4, 9, 2, new ushort[] { 5, 6 }),
            new(2, 9, 2, new ushort[] { 5, 6 }),
            new(3, 1, 8, new ushort[] { 7 }),
        ];

        var forward = EnvCellBakeCatalog.Build(sources);
        var reverse = EnvCellBakeCatalog.Build(sources.Reverse());

        Assert.Equal(forward.Groups, reverse.Groups, GroupComparer.Instance);
    }

    [Fact]
    public void Build_RejectsIdentityCollisionWithDifferentTuple()
    {
        EnvCellBakeSource[] sources =
        [
            new(1, 10, 20, new ushort[] { 30 }),
            new(2, 11, 20, new ushort[] { 30 }),
        ];

        var exception = Assert.Throws<InvalidDataException>(
            () => EnvCellBakeCatalog.Build(sources, (_, _, _) => 0x2_0000_0000));

        Assert.Contains("collision", exception.Message);
        Assert.Contains("0x00000001", exception.Message);
        Assert.Contains("0x00000002", exception.Message);
    }

    [Fact]
    public void Build_RejectsDuplicateFileId()
    {
        EnvCellBakeSource[] sources =
        [
            new(1, 10, 20, Array.Empty<ushort>()),
            new(1, 10, 20, Array.Empty<ushort>()),
        ];

        Assert.Throws<InvalidDataException>(() => EnvCellBakeCatalog.Build(sources));
    }

    private sealed class GroupComparer : IEqualityComparer<EnvCellGeometryGroup>
    {
        public static GroupComparer Instance { get; } = new();

        public bool Equals(EnvCellGeometryGroup? x, EnvCellGeometryGroup? y) =>
            x is not null &&
            y is not null &&
            x.GeometryId == y.GeometryId &&
            x.EnvironmentId == y.EnvironmentId &&
            x.CellStructure == y.CellStructure &&
            x.Surfaces.AsSpan().SequenceEqual(y.Surfaces) &&
            x.FileIds.AsSpan().SequenceEqual(y.FileIds);

        public int GetHashCode(EnvCellGeometryGroup obj) => obj.GeometryId.GetHashCode();
    }
}
