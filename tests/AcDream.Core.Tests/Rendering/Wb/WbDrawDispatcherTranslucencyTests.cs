using AcDream.App.Rendering.Wb;
using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherTranslucencyTests
{
    [Theory]
    [InlineData(TranslucencyKind.Opaque,     true)]
    [InlineData(TranslucencyKind.ClipMap,    true)]
    [InlineData(TranslucencyKind.AlphaBlend, false)]
    [InlineData(TranslucencyKind.Additive,   false)]
    [InlineData(TranslucencyKind.InvAlpha,   false)]
    public void IsOpaque_PartitionsByKind(TranslucencyKind kind, bool expected)
    {
        Assert.Equal(expected, WbDrawDispatcher.IsOpaquePublic(kind));
    }
}
