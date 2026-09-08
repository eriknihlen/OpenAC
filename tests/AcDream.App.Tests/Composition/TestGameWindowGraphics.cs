using AcDream.App.Composition;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Composition;

internal sealed class TestGameWindowGraphics : GameWindowGraphics
{
    public static TestGameWindowGraphics Instance { get; } = new();

    private TestGameWindowGraphics()
    {
    }

    public override IWorldPassScope? WorldPassScope { get; } = new VulkanWorldPassScope(sampleCount: 1);

    public override void Dispose()
    {
    }
}
