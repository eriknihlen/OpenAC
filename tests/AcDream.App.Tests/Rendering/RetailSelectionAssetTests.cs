using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Selection;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Core.Textures;
using AcDream.Core.Selection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailSelectionAssetTests
{
    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_ResolvesAllTwelveVividTargetImages()
    {
        string datDir = ResolveDatDir();
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var master = Assert.IsType<EnumIDMap>(
            dats.Get<EnumIDMap>((uint)dats.Portal.Header.MasterMapId));
        uint subMapDid = master.ClientEnumToID[0x10000009u];
        var subMap = Assert.IsType<EnumIDMap>(dats.Get<EnumIDMap>(subMapDid));

        for (uint index = 0; index < 12; index++)
        {
            uint enumValue = 1u + index;
            uint did = RetailDataIdResolver.Resolve(dats, enumValue, 0x10000009u);
            Assert.True(did != 0u,
                $"enum {enumValue}; submap 0x{subMapDid:X8} keys: {string.Join(',', subMap.ClientEnumToID.Keys.Select(k => $"0x{k:X8}"))}");
            Assert.True(dats.Portal.TryGet<RenderSurface>(did, out var surface)
                || dats.HighRes.TryGet<RenderSurface>(did, out surface),
                $"enum 0x{enumValue:X8} resolved 0x{did:X8}");
            Assert.NotNull(surface);
            Assert.Equal(0x06000000u, did & 0xFF000000u);
            Assert.Equal(index < 4 ? (12, 12) : (24, 24),
                (surface.Width, surface.Height));

            Palette? palette = surface.DefaultPaletteId != 0
                ? dats.Get<Palette>(surface.DefaultPaletteId)
                : null;
            DecodedTexture decoded = SurfaceDecoder.DecodeRenderSurface(surface, palette);
            int coloredPixels = 0;
            for (int pixel = 0; pixel < decoded.Rgba8.Length; pixel += 4)
            {
                if (decoded.Rgba8[pixel + 3] == 0)
                    continue;
                if (decoded.Rgba8[pixel] != decoded.Rgba8[pixel + 1]
                    || decoded.Rgba8[pixel] != decoded.Rgba8[pixel + 2])
                    coloredPixels++;
            }
            Assert.True(coloredPixels == 0,
                $"image {index + 1} has {coloredPixels} non-grayscale source pixels");
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void DoorSelectionGeometry_UsesDrawingBspRootSphereAndDatPolygonOrder()
    {
        const uint doorGfxObjId = 0x010044B5u;
        string datDir = ResolveDatDir();
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var gfx = Assert.IsType<GfxObj>(dats.Get<GfxObj>(doorGfxObjId));
        var cache = new RetailSelectionGeometryCache(dats, new object());

        var selection = Assert.IsType<AcDream.Core.Selection.RetailSelectionMesh>(
            cache.Resolve(doorGfxObjId));

        var root = Assert.IsType<DatReaderWriter.Types.DrawingBSPNode>(gfx.DrawingBSP.Root);
        Assert.Equal(root.BoundingSphere.Origin, selection.SphereCenter);
        Assert.Equal(root.BoundingSphere.Radius, selection.SphereRadius);
        Assert.Equal(gfx.Polygons.Count, selection.Polygons.Count);

        var firstSource = gfx.Polygons.First().Value;
        var firstSelection = selection.Polygons[0];
        Assert.Equal((int)firstSource.SidesType == 0, firstSelection.SingleSided);
        Assert.Equal(firstSource.VertexIds.Count, firstSelection.Vertices.Count);
    }

    [Fact]
    public void DrawingSphereViewconeRejectsPartBehindCamera()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 4f / 3f, 0.1f, 100f);
        FrustumPlanes frustum = FrustumPlanes.FromViewProjection(view * projection);
        var mesh = new RetailSelectionMesh(Vector3.Zero, 1f, []);

        Assert.True(RetailSelectionScene.DrawingSphereIntersectsFrustum(
            mesh, Matrix4x4.CreateTranslation(0f, 0f, -5f), frustum));
        Assert.False(RetailSelectionScene.DrawingSphereIntersectsFrustum(
            mesh, Matrix4x4.CreateTranslation(0f, 0f, 5f), frustum));
    }

    private static string ResolveDatDir()
    {
        string? configured = SysEnv.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "client_portal.dat")))
            return configured;

        const string installed = @"C:\Turbine\Asheron's Call";
        if (File.Exists(Path.Combine(installed, "client_portal.dat")))
            return installed;

        string conventional = Path.Combine(
            SysEnv.GetFolderPath(SysEnv.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        if (File.Exists(Path.Combine(conventional, "client_portal.dat")))
            return conventional;

        throw new InvalidOperationException(
            "Lane=InstalledDat requires client_portal.dat; see docs/release-gate.md.");
    }
}
