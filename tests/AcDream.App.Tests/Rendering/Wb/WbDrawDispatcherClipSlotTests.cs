using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherClipSlotTests
{
    [Fact]
    public void ResolveSlotForFrame_AlwaysReturnsSlotZeroNeverCulled()
    {
        var r = WbDrawDispatcher.ResolveSlotForFrame();

        Assert.Equal(0u, r.Slot);
        Assert.False(r.Culled);
    }
}
