using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class ModernBatchDataLayoutTests
{
    [Fact]
    public void Size_Is16Bytes_MatchingGpuBatchDataStride()
    {
        Assert.Equal(16, Unsafe.SizeOf<ModernBatchData>());
    }

    [Fact]
    public void FieldOffsets_MatchStd430Layout()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<ModernBatchData>(nameof(ModernBatchData.TextureTableIndex)));
        Assert.Equal(4, (int)Marshal.OffsetOf<ModernBatchData>(nameof(ModernBatchData.SurfaceOpacity)));
        Assert.Equal(8, (int)Marshal.OffsetOf<ModernBatchData>(nameof(ModernBatchData.TextureIndex)));
        Assert.Equal(12, (int)Marshal.OffsetOf<ModernBatchData>(nameof(ModernBatchData.Flags)));
    }

    [Fact]
    public void FieldValues_RoundTripThroughTheStruct()
    {
        var data = new ModernBatchData
        {
            TextureTableIndex = 7u,
            SurfaceOpacity = 0.25f,
            TextureIndex = 3u,
        };

        Assert.Equal(7u, data.TextureTableIndex);
        Assert.Equal(0.25f, data.SurfaceOpacity);
        Assert.Equal(3u, data.TextureIndex);
        Assert.Equal(0u, data.Flags);
    }
}
