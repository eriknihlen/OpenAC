using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class ClipFrameAssemblerTests
{
    [Fact]
    public void BeginWalkFrame_OutdoorRoot_SeedsOneFullScreenDefaultView()
    {
        using ClipFrame frame = ClipFrame.NoClip();

        ClipFrameAssembly assembly = ClipFrameAssembler.BeginWalkFrame(frame, outdoorRoot: true);

        ClipViewSlice slice = Assert.Single(assembly.OutsideViewSlices);
        Assert.Equal(0, slice.Slot);
        Assert.Empty(slice.Planes);
        Assert.Equal(new Vector4(-1f, -1f, 1f, 1f), slice.NdcAabb);
        Assert.True(assembly.OutdoorVisible);
        Assert.True(assembly.HasOutsideView);
        Assert.Equal(1, frame.SlotCount);
    }

    [Fact]
    public void BeginWalkFrame_InteriorRoot_ResetsReusedOutdoorAssemblyToEmpty()
    {
        using ClipFrame frame = ClipFrame.NoClip();
        ClipFrameAssembly reuse = ClipFrameAssembler.BeginWalkFrame(frame, outdoorRoot: true);

        ClipFrameAssembly assembly = ClipFrameAssembler.BeginWalkFrame(
            frame,
            outdoorRoot: false,
            reuse);

        Assert.Same(reuse, assembly);
        Assert.Empty(assembly.OutsideViewSlices);
        Assert.False(assembly.OutdoorVisible);
        Assert.False(assembly.HasOutsideView);
        Assert.Equal(Vector4.Zero, assembly.OutsideViewNdcAabb);
        Assert.Equal(0, assembly.ScissorFallbacks);
        Assert.Equal(1, frame.SlotCount);
    }
}
