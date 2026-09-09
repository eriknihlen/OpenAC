using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailPViewRendererTests
{
    [Fact]
    public void DrawInside_NeverCallsFlushLandscapeAlphaDirectly()
    {
        MethodInfo drawInside = typeof(RetailPViewRenderer).GetMethod(
            "DrawInside",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(drawInside);

        Assert.DoesNotContain(
            calls,
            call => call.Target.DeclaringType == typeof(RetailPViewPassExecutor)
                && call.Target.Name == nameof(RetailPViewPassExecutor.FlushLandscapeAlpha));

        Assert.Contains(
            calls,
            call => call.Target.DeclaringType == typeof(RetailPViewPassExecutor)
                && call.Target.Name == nameof(RetailPViewPassExecutor.DrawUnattachedSceneParticles));
    }
}
