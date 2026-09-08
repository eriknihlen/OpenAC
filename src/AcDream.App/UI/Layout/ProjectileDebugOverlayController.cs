using System.Numerics;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI.Layout;

internal sealed class ProjectileDebugOverlayController
{
    private static readonly Vector4 ClearColor = new(0f, 1f, 0f, 0.95f);
    private static readonly Vector4 BlockedColor = new(1f, 0f, 0f, 0.95f);

    private readonly UiPanel _root;
    private readonly Func<IReadOnlyList<PluginProjectileDebugSample>> _samples;
    private readonly Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)>
        _camera;
    private readonly List<UiPanel> _markers = [];

    private ProjectileDebugOverlayController(
        UiPanel root,
        Func<IReadOnlyList<PluginProjectileDebugSample>> samples,
        Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> camera)
    {
        _root = root;
        _samples = samples;
        _camera = camera;
    }

    internal static ProjectileDebugOverlayController Mount(
        UiRoot host,
        Func<IReadOnlyList<PluginProjectileDebugSample>> samples,
        Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> camera)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(camera);
        var root = new UiPanel
        {
            Name = "PluginProjectileDebugOverlay",
            BackgroundColor = Vector4.Zero,
            BorderColor = Vector4.Zero,
            ClickThrough = true,
            Visible = false,
            ZOrder = -9_999,
            Anchors = AnchorEdges.None,
        };
        host.AddChild(root);
        return new ProjectileDebugOverlayController(root, samples, camera);
    }

    internal void Tick()
    {
        IReadOnlyList<PluginProjectileDebugSample> samples = _samples();
        var camera = _camera();
        if (samples.Count == 0
            || camera.Viewport.X <= 0f
            || camera.Viewport.Y <= 0f)
        {
            HideAll();
            return;
        }

        EnsureMarkerCount(samples.Count);
        _root.Left = 0f;
        _root.Top = 0f;
        _root.Width = camera.Viewport.X;
        _root.Height = camera.Viewport.Y;
        int visible = 0;
        for (int index = 0; index < samples.Count; index++)
        {
            PluginProjectileDebugSample sample = samples[index];
            if (!ScreenProjection.TryProjectSphereToScreenRect(
                    sample.WorldPosition,
                    sample.Radius,
                    camera.View,
                    camera.Projection,
                    camera.Viewport,
                    out Vector2 minimum,
                    out Vector2 maximum,
                    out _,
                    minSidePixels: 4f)
                || maximum.X < 0f
                || maximum.Y < 0f
                || minimum.X > camera.Viewport.X
                || minimum.Y > camera.Viewport.Y)
            {
                continue;
            }

            UiPanel marker = _markers[visible++];
            marker.Left = MathF.Max(0f, minimum.X);
            marker.Top = MathF.Max(0f, minimum.Y);
            marker.Width = MathF.Max(
                1f,
                MathF.Min(camera.Viewport.X, maximum.X) - marker.Left);
            marker.Height = MathF.Max(
                1f,
                MathF.Min(camera.Viewport.Y, maximum.Y) - marker.Top);
            marker.BorderColor = sample.IsClear ? ClearColor : BlockedColor;
            marker.Visible = true;
        }
        for (int index = visible; index < _markers.Count; index++)
            _markers[index].Visible = false;
        _root.Visible = visible > 0;
    }

    private void EnsureMarkerCount(int count)
    {
        while (_markers.Count < count)
        {
            var marker = new UiPanel
            {
                Name = $"PluginProjectileDebugMarker{_markers.Count}",
                BackgroundColor = Vector4.Zero,
                BorderColor = ClearColor,
                BorderThickness = 1.5f,
                ClickThrough = true,
                Visible = false,
                Anchors = AnchorEdges.None,
            };
            _markers.Add(marker);
            _root.AddChild(marker);
        }
    }

    private void HideAll()
    {
        _root.Visible = false;
        for (int index = 0; index < _markers.Count; index++)
            _markers[index].Visible = false;
    }
}
