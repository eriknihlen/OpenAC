using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Meshing;

public class GfxObjDegradeResolverTests
{
    [Fact]
    public void NoDegradeTable_ReturnsBaseMesh()
    {
        const uint baseId = 0x01001212u;
        var baseGfx = new GfxObj { Flags = 0, DIDDegrade = 0 };
        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = baseGfx };

        bool ok = GfxObjDegradeResolver.TryResolveCloseGfxObj(
            id => gfxObjs.GetValueOrDefault(id),
            _ => null,
            baseId,
            out uint resolvedId,
            out var resolvedGfx);

        Assert.True(ok);
        Assert.Equal(baseId, resolvedId);
        Assert.Same(baseGfx, resolvedGfx);
    }

    [Fact]
    public void ValidDegradeTable_ReturnsSlotZero()
    {
        const uint baseId = 0x01000055u;       // low-detail Aluvian Male upper arm
        const uint degradeInfoId = 0x110006D0u;
        const uint closeId = 0x01001795u;

        var baseGfx = new GfxObj
        {
            Flags = GfxObjFlags.HasDIDDegrade,
            DIDDegrade = degradeInfoId,
        };
        var closeGfx = new GfxObj { Flags = 0 };
        var degradeInfo = new GfxObjDegradeInfo
        {
            Degrades = { new GfxObjInfo { Id = closeId } },
        };

        var gfxObjs = new Dictionary<uint, GfxObj>
        {
            [baseId] = baseGfx,
            [closeId] = closeGfx,
        };
        var degradeInfos = new Dictionary<uint, GfxObjDegradeInfo>
        {
            [degradeInfoId] = degradeInfo,
        };

        bool ok = GfxObjDegradeResolver.TryResolveCloseGfxObj(
            id => gfxObjs.GetValueOrDefault(id),
            id => degradeInfos.GetValueOrDefault(id),
            baseId,
            out uint resolvedId,
            out var resolvedGfx);

        Assert.True(ok);
        Assert.Equal(closeId, resolvedId);
        Assert.Same(closeGfx, resolvedGfx);
    }

    [Fact]
    public void MissingSlotZeroMesh_FallsBackToBase()
    {
        const uint baseId = 0x01000055u;
        const uint degradeInfoId = 0x110006D0u;
        const uint missingCloseId = 0xDEADBEEFu;

        var baseGfx = new GfxObj
        {
            Flags = GfxObjFlags.HasDIDDegrade,
            DIDDegrade = degradeInfoId,
        };
        var degradeInfo = new GfxObjDegradeInfo
        {
            Degrades = { new GfxObjInfo { Id = missingCloseId } },
        };
        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = baseGfx };
        var degradeInfos = new Dictionary<uint, GfxObjDegradeInfo>
        {
            [degradeInfoId] = degradeInfo,
        };

        bool ok = GfxObjDegradeResolver.TryResolveCloseGfxObj(
            id => gfxObjs.GetValueOrDefault(id),
            id => degradeInfos.GetValueOrDefault(id),
            baseId,
            out uint resolvedId,
            out var resolvedGfx);

        Assert.True(ok);
        Assert.Equal(baseId, resolvedId);
        Assert.Same(baseGfx, resolvedGfx);
    }

    [Fact]
    public void EmptyDegradesList_FallsBackToBase()
    {
        const uint baseId = 0x01000055u;
        const uint degradeInfoId = 0x110006D0u;

        var baseGfx = new GfxObj
        {
            Flags = GfxObjFlags.HasDIDDegrade,
            DIDDegrade = degradeInfoId,
        };
        var degradeInfo = new GfxObjDegradeInfo();   // empty Degrades

        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = baseGfx };
        var degradeInfos = new Dictionary<uint, GfxObjDegradeInfo>
        {
            [degradeInfoId] = degradeInfo,
        };

        bool ok = GfxObjDegradeResolver.TryResolveCloseGfxObj(
            id => gfxObjs.GetValueOrDefault(id),
            id => degradeInfos.GetValueOrDefault(id),
            baseId,
            out uint resolvedId,
            out var resolvedGfx);

        Assert.True(ok);
        Assert.Equal(baseId, resolvedId);
        Assert.Same(baseGfx, resolvedGfx);
    }

    [Fact]
    public void MissingBaseGfxObj_ReturnsFalse()
    {
        const uint baseId = 0xDEADBEEFu;

        bool ok = GfxObjDegradeResolver.TryResolveCloseGfxObj(
            _ => null,
            _ => null,
            baseId,
            out uint resolvedId,
            out var resolvedGfx);

        Assert.False(ok);
        Assert.Equal(baseId, resolvedId);
        Assert.Null(resolvedGfx);
    }


    [Fact]
    public void IsRuntimeHiddenMarker_EditorMarkerDegradingToNothing_True()
    {
        const uint markerGfx = 0x010028CAu;
        const uint degradeId = 0x11000118u;
        var gfx = new GfxObj { Flags = GfxObjFlags.HasDIDDegrade, DIDDegrade = degradeId };
        var info = new GfxObjDegradeInfo
        {
            Degrades =
            {
                new GfxObjInfo { Id = markerGfx, MaxDist = 0f },
                new GfxObjInfo { Id = 0u, MaxDist = float.MaxValue },
            },
        };
        var gfxObjs = new Dictionary<uint, GfxObj> { [markerGfx] = gfx };
        var infos = new Dictionary<uint, GfxObjDegradeInfo> { [degradeId] = info };

        Assert.True(GfxObjDegradeResolver.IsRuntimeHiddenMarker(
            id => gfxObjs.GetValueOrDefault(id), id => infos.GetValueOrDefault(id), markerGfx));
    }

    /// <summary>A real LOD object — slot 0 visible out to a real distance (MaxDist&gt;0) —
    /// is NOT a marker, even though it degrades further.</summary>
    [Fact]
    public void IsRuntimeHiddenMarker_NormalLodObject_False()
    {
        const uint baseId = 0x01000055u;
        const uint degradeId = 0x110006D0u;
        var gfx = new GfxObj { Flags = GfxObjFlags.HasDIDDegrade, DIDDegrade = degradeId };
        var info = new GfxObjDegradeInfo
        {
            Degrades =
            {
                new GfxObjInfo { Id = 0x01001795u, MaxDist = 25f },
                new GfxObjInfo { Id = 0u, MaxDist = float.MaxValue },
            },
        };
        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = gfx };
        var infos = new Dictionary<uint, GfxObjDegradeInfo> { [degradeId] = info };

        Assert.False(GfxObjDegradeResolver.IsRuntimeHiddenMarker(
            id => gfxObjs.GetValueOrDefault(id), id => infos.GetValueOrDefault(id), baseId));
    }

    /// <summary>No degrade table at all → not a marker.</summary>
    [Fact]
    public void IsRuntimeHiddenMarker_NoDegradeTable_False()
    {
        const uint baseId = 0x01001212u;
        var gfx = new GfxObj { Flags = 0, DIDDegrade = 0 };
        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = gfx };
        Assert.False(GfxObjDegradeResolver.IsRuntimeHiddenMarker(
            id => gfxObjs.GetValueOrDefault(id), _ => null, baseId));
    }

    [Fact]
    public void IsRuntimeHiddenMarker_EditorSlotButDegradesToRealMesh_False()
    {
        const uint baseId = 0x01002000u;
        const uint degradeId = 0x11002000u;
        var gfx = new GfxObj { Flags = GfxObjFlags.HasDIDDegrade, DIDDegrade = degradeId };
        var info = new GfxObjDegradeInfo
        {
            Degrades =
            {
                new GfxObjInfo { Id = baseId, MaxDist = 0f },
                new GfxObjInfo { Id = 0x01002001u, MaxDist = float.MaxValue },
            },
        };
        var gfxObjs = new Dictionary<uint, GfxObj> { [baseId] = gfx };
        var infos = new Dictionary<uint, GfxObjDegradeInfo> { [degradeId] = info };

        Assert.False(GfxObjDegradeResolver.IsRuntimeHiddenMarker(
            id => gfxObjs.GetValueOrDefault(id), id => infos.GetValueOrDefault(id), baseId));
    }
}
