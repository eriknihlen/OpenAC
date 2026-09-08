using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Plugin.Tests.Fixtures.InvalidRenderPackInternal;

internal sealed class InternalRenderPackPlugin : IRenderPackPlugin
{
    public void Register(IRenderPackRegistry registry) { }
}
