using System.Runtime.CompilerServices;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Sky;
using AcDream.Content;
using AcDream.Core.Terrain;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class RhiVertexLayoutStrideTests
{
    [Fact]
    public void WorldMeshStrideMatchesTheVertexRecordTheMeshArenaPacks()
    {
        // ObjectMeshManager writes VertexPositionNormalTexture into
        // GlobalMeshBuffer; WbDrawDispatcher, EnvCellRenderer and the
        // mesh-particle pipeline all read it back through this layout.
        Assert.Equal(
            (uint)Unsafe.SizeOf<VertexPositionNormalTexture>(),
            GpuVertexLayout.WorldMesh.StrideBytes);
        Assert.Equal(
            (uint)VertexPositionNormalTexture.Size,
            GpuVertexLayout.WorldMesh.StrideBytes);
    }

    [Fact]
    public void TerrainStrideMatchesTheUploadedTerrainVertex()
    {
        Assert.Equal(
            (uint)Unsafe.SizeOf<TerrainVertex>(),
            TerrainModernRenderer.TerrainVertexLayout.StrideBytes);
    }

    [Fact]
    public void SkyStrideMatchesTheUploadedRecord()
    {
        // The defect that produced this whole family of assertions.
        Assert.Equal(
            (uint)Unsafe.SizeOf<Vertex>(),
            SkyRenderer.SkyVertexLayout.StrideBytes);
    }

    [Fact]
    public void RetainedUiSpriteStrideMatchesTheFloatsThePrducerAppends()
    {
        Assert.Equal(
            (uint)(TextRenderer.FloatsPerVertex * sizeof(float)),
            TextRenderer.SpriteVertexLayout.StrideBytes);
    }

    [Fact]
    public void DebugLineStrideMatchesTheFloatsTheProducerAppends()
    {
        Assert.Equal(
            (uint)(DebugLineRenderer.FloatsPerVertex * sizeof(float)),
            DebugLineRenderer.VertexLayout.StrideBytes);
    }

    [Fact]
    public void ParticleBillboardStridesMatchTheirUploadedRecords()
    {
        GpuVertexLayout layout = ParticleRenderer.BillboardVertexLayout;

        // Binding 0 is the shared unit quad: four floats (XY position, UV).
        Assert.Equal(4u * sizeof(float), layout.StrideOf(0));
        Assert.Equal(GpuVertexInputRate.Vertex, layout.InputRateOf(0));

        // Binding 1 is one BillboardGpuInstance per particle. Getting this
        // stride wrong is the sky defect wearing a per-instance face.
        Assert.Equal(
            (uint)Unsafe.SizeOf<ParticleRenderer.BillboardGpuInstance>(),
            layout.StrideOf(1));
        Assert.Equal(GpuVertexInputRate.Instance, layout.InputRateOf(1));
    }

    [Fact]
    public void ParticleMeshStridesMatchTheirUploadedRecords()
    {
        GpuVertexLayout layout = ParticleRenderer.MeshVertexLayout;

        Assert.Equal(
            (uint)Unsafe.SizeOf<VertexPositionNormalTexture>(),
            layout.StrideOf(0));
        Assert.Equal(GpuVertexInputRate.Vertex, layout.InputRateOf(0));

        Assert.Equal(
            (uint)Unsafe.SizeOf<ParticleRenderer.MeshParticleGpuInstance>(),
            layout.StrideOf(1));
        Assert.Equal(GpuVertexInputRate.Instance, layout.InputRateOf(1));
    }

    [Fact]
    public void EveryAttributeFitsInsideItsBindingStride()
    {
        foreach ((string name, GpuVertexLayout layout) in EveryRhiVertexLayout())
        {
            foreach (GpuVertexAttribute attribute in layout.Attributes)
            {
                uint stride = layout.StrideOf(attribute.Binding);
                uint size = SizeOf(attribute.Format);
                Assert.True(
                    attribute.OffsetBytes + size <= stride,
                    $"{name}: attribute at location {attribute.Location} reaches past "
                    + $"binding {attribute.Binding}'s {stride}-byte stride.");
            }
        }
    }

    [Fact]
    public void EveryAttributeNamesADeclaredBinding()
    {
        foreach ((string name, GpuVertexLayout layout) in EveryRhiVertexLayout())
        {
            foreach (GpuVertexAttribute attribute in layout.Attributes)
            {
                Assert.True(
                    layout.Bindings.Any(binding => binding.Binding == attribute.Binding),
                    $"{name}: attribute at location {attribute.Location} names undeclared "
                    + $"binding {attribute.Binding}.");
            }
        }
    }

    internal static IEnumerable<(string Name, GpuVertexLayout Layout)> EveryRhiVertexLayout()
    {
        yield return ("world mesh", GpuVertexLayout.WorldMesh);
        yield return ("terrain", TerrainModernRenderer.TerrainVertexLayout);
        yield return ("sky", SkyRenderer.SkyVertexLayout);
        yield return ("retained UI sprite", TextRenderer.SpriteVertexLayout);
        yield return ("debug line", DebugLineRenderer.VertexLayout);
        yield return ("particle billboard", ParticleRenderer.BillboardVertexLayout);
        yield return ("particle mesh", ParticleRenderer.MeshVertexLayout);
    }

    private static uint SizeOf(GpuVertexFormat format) => format switch
    {
        GpuVertexFormat.Float1 => 4u,
        GpuVertexFormat.Float2 => 8u,
        GpuVertexFormat.Float3 => 12u,
        GpuVertexFormat.Float4 => 16u,
        GpuVertexFormat.UByte4Normalized => 4u,
        GpuVertexFormat.UByte4UInt => 4u,
        GpuVertexFormat.UInt1 => 4u,
        _ => throw new NotSupportedException($"No size known for {format}."),
    };
}
