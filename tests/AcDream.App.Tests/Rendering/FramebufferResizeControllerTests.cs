using System.Globalization;
using AcDream.App.Input;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class FramebufferResizeControllerTests
{
    [Fact]
    public void ResizePreservesViewportAspectCameraDevToolsOrder()
    {
        var calls = new List<string>();
        var aspect = new ViewportAspectState();
        var owner = new FramebufferResizeController(aspect);
        owner.BindViewport(new Viewport(calls));
        owner.BindCamera(new Camera(calls));
        owner.BindDevTools(new DevTools(calls));

        owner.Resize(1600, 900);

        Assert.Equal(["viewport:1600x900", "camera:1.778", "devtools:1600x900"], calls);
        Assert.Equal(1600f / 900f, aspect.Aspect, 5);
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-1, 720)]
    [InlineData(1280, -1)]
    public void NonPositiveFramebufferIsRejectedWithoutMutatingTargets(int width, int height)
    {
        var calls = new List<string>();
        var aspect = new ViewportAspectState();
        var owner = new FramebufferResizeController(aspect);
        owner.BindViewport(new Viewport(calls));
        owner.BindCamera(new Camera(calls));
        owner.BindDevTools(new DevTools(calls));

        owner.Resize(width, height);

        Assert.Empty(calls);
        Assert.Equal(16f / 9f, aspect.Aspect);
    }

    [Fact]
    public void LateBindingDoesNotReplayEarlierResize()
    {
        var calls = new List<string>();
        var owner = new FramebufferResizeController(new ViewportAspectState());
        owner.Resize(1024, 768);

        owner.BindViewport(new Viewport(calls));
        owner.BindCamera(new Camera(calls));
        owner.BindDevTools(new DevTools(calls));

        Assert.Empty(calls);
        owner.Resize(800, 600);
        Assert.Equal(["viewport:800x600", "camera:1.333", "devtools:800x600"], calls);
    }

    [Fact]
    public void DevToolsBindingReleasesExpectedTargetWithoutReplayingResize()
    {
        var calls = new List<string>();
        var owner = new FramebufferResizeController(new ViewportAspectState());
        var target = new DevTools(calls);
        using var binding = new FramebufferDevToolsBinding(owner, target);

        owner.Resize(900, 700);
        binding.Dispose();
        binding.Dispose();
        owner.Resize(1000, 800);

        Assert.Equal(["devtools:900x700"], calls);
    }

    private sealed class Viewport(List<string> calls) : IFramebufferViewportTarget
    {
        public void ResizeViewport(int width, int height) =>
            calls.Add($"viewport:{width}x{height}");
    }

    private sealed class Camera(List<string> calls) : IFramebufferCameraTarget
    {
        public void SetAspect(float aspect) => calls.Add(
            string.Create(CultureInfo.InvariantCulture, $"camera:{aspect:F3}"));
    }

    private sealed class DevTools(List<string> calls) : IFramebufferDevToolsTarget
    {
        public void ResetLayout(int width, int height) =>
            calls.Add($"devtools:{width}x{height}");
    }
}
