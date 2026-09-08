using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class WbDispatcherDepthMaskTests
{
    [Theory]
    [InlineData(TranslucencyKind.Opaque,     true)]   // opaque pass — depth write
    [InlineData(TranslucencyKind.ClipMap,    true)]   // foliage — depth write (binary alpha / A2C)
    [InlineData(TranslucencyKind.AlphaBlend, false)]  // transparent — no depth write
    [InlineData(TranslucencyKind.Additive,   false)]
    [InlineData(TranslucencyKind.InvAlpha,   false)]
    public void IsOpaquePartition_ImpliesDepthWriteAttribution(
        TranslucencyKind kind, bool expectsDepthWrite)
    {
        bool isOpaque = WbDrawDispatcher.IsOpaquePublic(kind);
        Assert.Equal(expectsDepthWrite, isOpaque);
    }
}
