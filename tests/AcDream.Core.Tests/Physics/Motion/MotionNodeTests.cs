using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MotionNodeTests
{
    [Fact]
    public void Ctor_StoresAllThreeFields()
    {
        var node = new MotionNode(ContextId: 7u, Motion: 0x41000003u, JumpErrorCode: 0x48u);

        Assert.Equal(7u, node.ContextId);
        Assert.Equal(0x41000003u, node.Motion);
        Assert.Equal(0x48u, node.JumpErrorCode);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new MotionNode(1u, 2u, 3u);
        var b = new MotionNode(1u, 2u, 3u);
        var c = new MotionNode(1u, 2u, 4u);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
