using AcDream.Core.Vfx;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Vfx;

public sealed class PhysicsScriptTableResolverTests
{
    private const uint TableDid = 0x34000005u;
    private const uint RawType = 0x00000016u;

    [Fact]
    public void Resolve_UsesFirstStoredUpperThresholdWithoutSorting()
    {
        PhysicsScriptTable table = BuildTable(
            (1.0f, 0x33000011u),
            (0.0f, 0x33000022u));
        var resolver = new PhysicsScriptTableResolver(
            id => id == TableDid ? table : null);

        Assert.Equal(0x33000011u, resolver.Resolve(TableDid, RawType, -0.25f));
        Assert.Equal(0x33000011u, resolver.Resolve(TableDid, RawType, 0.0f));
    }

    [Fact]
    public void Resolve_EqualityAndDuplicateThresholdChooseFirst()
    {
        PhysicsScriptTable table = BuildTable(
            (0.5f, 0x33000031u),
            (0.5f, 0x33000032u),
            (1.0f, 0x33000033u));
        var resolver = new PhysicsScriptTableResolver(_ => table);

        Assert.Equal(0x33000031u, resolver.Resolve(TableDid, RawType, 0.5f));
        Assert.Equal(0x33000033u, resolver.Resolve(TableDid, RawType, 0.75f));
    }

    [Fact]
    public void Resolve_PreservesRetailIeeeComparisonBoundaries()
    {
        PhysicsScriptTable table = BuildTable(
            (0.0f, 0x33000041u),
            (1.0f, 0x33000042u));
        var resolver = new PhysicsScriptTableResolver(_ => table);

        Assert.Equal(0x33000041u,
            resolver.Resolve(TableDid, RawType, float.NegativeInfinity));
        Assert.Null(resolver.Resolve(TableDid, RawType, float.PositiveInfinity));
        Assert.Null(resolver.Resolve(TableDid, RawType, float.NaN));
        Assert.Null(resolver.Resolve(TableDid, RawType, 1.0001f));
    }

    [Fact]
    public void Resolve_MissingOrInvalidInputsReturnNoScript()
    {
        PhysicsScriptTable invalidScript = BuildTable((1.0f, 0x32000001u));
        var resolver = new PhysicsScriptTableResolver(
            id => id == TableDid ? invalidScript : null);

        Assert.Null(resolver.Resolve(0u, RawType, 0f));
        Assert.Null(resolver.Resolve(0x35000001u, RawType, 0f));
        Assert.Null(resolver.Resolve(TableDid, 0xA5A5A5A5u, 0f));
        Assert.Null(resolver.Resolve(TableDid, RawType, 0f));
    }

    [Fact]
    public void DidValidation_UsesRetailHighByteAndAcceptsLargeIndexes()
    {
        Assert.True(PhysicsScriptTableResolver.IsPhysicsScriptDid(0x33010000u));
        Assert.True(PhysicsScriptTableResolver.IsPhysicsScriptDid(0x33FFFFFFu));
        Assert.True(PhysicsScriptTableResolver.IsPhysicsScriptTableDid(0x34010000u));
        Assert.True(PhysicsScriptTableResolver.IsPhysicsScriptTableDid(0x34FFFFFFu));
        Assert.False(PhysicsScriptTableResolver.IsPhysicsScriptDid(0x34010000u));
        Assert.False(PhysicsScriptTableResolver.IsPhysicsScriptTableDid(0x33010000u));
    }

    [Fact]
    public void Resolve_AcceptsHighIndexTableAndScriptIds()
    {
        const uint highTableDid = 0x34010000u;
        PhysicsScriptTable table = BuildTable((1.0f, 0x33010000u));
        table.Id = highTableDid;
        var resolver = new PhysicsScriptTableResolver(
            id => id == highTableDid ? table : null);

        Assert.Equal(0x33010000u, resolver.Resolve(highTableDid, RawType, 0f));
    }

    [Fact]
    public void Resolve_RejectsMismatchedEmbeddedTableId()
    {
        PhysicsScriptTable mismatched = BuildTable((1.0f, 0x33000051u));
        mismatched.Id = 0x34000006u;
        var resolver = new PhysicsScriptTableResolver(_ => mismatched);

        Assert.Null(resolver.Resolve(TableDid, RawType, 0f));
    }

    [Fact]
    public void Resolve_LoaderFailureReturnsNoPlayAndPreservesDiagnosticException()
    {
        var resolver = new PhysicsScriptTableResolver(
            _ => throw new InvalidDataException("fixture DAT failure"));

        Assert.Null(resolver.Resolve(TableDid, RawType, 0f, out Exception? failure));
        var invalid = Assert.IsType<InvalidDataException>(failure);
        Assert.Equal("fixture DAT failure", invalid.Message);
    }

    private static PhysicsScriptTable BuildTable(
        params (float Mod, uint ScriptDid)[] entries)
    {
        var data = new PhysicsScriptTableData();
        foreach ((float mod, uint scriptDid) in entries)
        {
            data.Scripts.Add(new ScriptAndModData
            {
                Mod = mod,
                ScriptId = scriptDid,
            });
        }

        var table = new PhysicsScriptTable { Id = TableDid };
        table.ScriptTable.Add(unchecked((PlayScript)RawType), data);
        return table;
    }
}
