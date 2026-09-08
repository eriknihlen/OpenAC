using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ProjectileDebugOverlayControllerTests
{
    [Fact]
    public void ProjectsTransientClearAndBlockedSamplesWithoutConsumingInput()
    {
        IReadOnlyList<PluginProjectileDebugSample> samples =
        [
            new(new Vector3(0f, 0f, -10f), true, 0.4f),
            new(new Vector3(1f, 0f, -10f), false, 0.4f),
        ];
        var root = new UiRoot { Width = 800f, Height = 600f };
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2f,
            4f / 3f,
            0.1f,
            100f);
        ProjectileDebugOverlayController controller =
            ProjectileDebugOverlayController.Mount(
                root,
                () => samples,
                () => (Matrix4x4.Identity, projection, new Vector2(800f, 600f)));

        controller.Tick();

        UiPanel overlay = Assert.IsType<UiPanel>(Assert.Single(root.Children));
        Assert.True(overlay.Visible);
        Assert.True(overlay.ClickThrough);
        Assert.Equal(2, overlay.Children.Count);
        UiPanel clear = Assert.IsType<UiPanel>(overlay.Children[0]);
        UiPanel blocked = Assert.IsType<UiPanel>(overlay.Children[1]);
        Assert.True(clear.Visible);
        Assert.True(blocked.Visible);
        Assert.Equal(new Vector4(0f, 1f, 0f, 0.95f), clear.BorderColor);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.95f), blocked.BorderColor);
        Assert.InRange(clear.Left, 380f, 400f);
        Assert.InRange(clear.Top, 280f, 300f);

        samples = [];
        controller.Tick();

        Assert.False(overlay.Visible);
        Assert.All(overlay.Children, static child => Assert.False(child.Visible));
    }
}
