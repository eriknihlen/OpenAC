using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Sky;
using AcDream.Core.Terrain;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class SkyVertexLayoutTests
{
    [Fact]
    public void SkyVertexLayout_StrideMatchesTheUploadedRecord()
    {
        Assert.Equal(
            (uint)Unsafe.SizeOf<Vertex>(),
            SkyRenderer.SkyVertexLayout.StrideBytes);
    }

    [Fact]
    public void SkyVertexLayout_DeclaresTheThreeAttributesTheShaderReads()
    {
        Assert.Collection(
            SkyRenderer.SkyVertexLayout.Attributes,
            position =>
            {
                Assert.Equal(0u, position.Location);
                Assert.Equal(GpuVertexFormat.Float3, position.Format);
                Assert.Equal(0u, position.OffsetBytes);
            },
            normal =>
            {
                Assert.Equal(1u, normal.Location);
                Assert.Equal(GpuVertexFormat.Float3, normal.Format);
                Assert.Equal(12u, normal.OffsetBytes);
            },
            texCoord =>
            {
                Assert.Equal(2u, texCoord.Location);
                Assert.Equal(GpuVertexFormat.Float2, texCoord.Format);
                Assert.Equal(24u, texCoord.OffsetBytes);
            });
    }

    /// <summary>
    /// Every attribute has to fit inside the stride it is read with. A layout that
    /// reaches past its own stride is the same defect wearing the other face.
    /// </summary>
    [Fact]
    public void SkyVertexLayout_EveryAttributeFitsInsideTheStride()
    {
        foreach (GpuVertexAttribute attribute in SkyRenderer.SkyVertexLayout.Attributes)
        {
            uint size = attribute.Format switch
            {
                GpuVertexFormat.Float1 => 4u,
                GpuVertexFormat.Float2 => 8u,
                GpuVertexFormat.Float3 => 12u,
                GpuVertexFormat.Float4 => 16u,
                _ => 4u,
            };
            Assert.True(
                attribute.OffsetBytes + size <= SkyRenderer.SkyVertexLayout.StrideBytes,
                $"Attribute at location {attribute.Location} reaches past the vertex stride.");
        }
    }
}
