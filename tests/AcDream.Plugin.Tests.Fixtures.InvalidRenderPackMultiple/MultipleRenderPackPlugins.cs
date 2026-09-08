using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Plugin.Tests.Fixtures.InvalidRenderPackMultiple;

public sealed class FirstRenderPackPlugin : IRenderPackPlugin
{
    public void Register(IRenderPackRegistry registry) { }
}

public sealed class SecondRenderPackPlugin : IRenderPackPlugin
{
    public void Register(IRenderPackRegistry registry) { }
}
