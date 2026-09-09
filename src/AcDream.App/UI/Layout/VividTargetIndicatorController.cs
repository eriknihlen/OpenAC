using System.Numerics;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Content;

namespace AcDream.App.UI.Layout;

public readonly record struct VividTargetInfo(
    Vector3 SelectionSphereCenter,
    float SelectionSphereRadius,
    uint ItemType,
    uint ObjectDescriptionFlags);

public sealed record VividTargetRuntimeBindings(
    SelectionState Selection,
    Func<uint> PlayerGuid,
    Func<bool> Enabled,
    Func<uint, VividTargetInfo?> ResolveTarget,
    Func<(Matrix4x4 View, Matrix4x4 Projection, Vector2 Viewport)> Camera);

public sealed class VividTargetIndicatorController
{
    private const uint ClientEnumCategory = 0x10000009u;
    private const uint FirstSourceImageEnum = 1u;
    private const int SourceImageCount = 12;
    private const float ViewportMargin = 8f;

    private readonly UiPanel _onScreen;
    private readonly UiTextureElement[] _corners;
    private readonly UiTextureElement _offScreen;
    private readonly VividTargetRuntimeBindings _bindings;
    private readonly VividTargetSource[] _sources;
    private readonly (float Width, float Height)[] _cornerSizes;

    private VividTargetIndicatorController(
        UiPanel onScreen,
        UiTextureElement[] corners,
        UiTextureElement offScreen,
        VividTargetSource[] sources,
        VividTargetRuntimeBindings bindings)
    {
        _onScreen = onScreen;
        _corners = corners;
        _offScreen = offScreen;
        _sources = sources;
        _cornerSizes = sources.Take(4)
            .Select(source => (source.Width, source.Height))
            .ToArray();
        _bindings = bindings;
    }

    public static VividTargetIndicatorController? Mount(
        UiRoot host,
        RetailUiAssets assets,
        VividTargetRuntimeBindings bindings)
    {
        var onScreen = new UiPanel
        {
            Name = "VividTargetIndicatorOnScreen",
            BackgroundColor = Vector4.Zero,
            BorderColor = Vector4.Zero,
            ClickThrough = true,
            Visible = false,
            ZOrder = -10_000,
            Anchors = AnchorEdges.None,
        };

        var sources = new VividTargetSource[SourceImageCount];
        for (uint i = 0; i < SourceImageCount; i++)
        {
            uint did;
            lock (assets.DatLock)
                did = RetailDataIdResolver.Resolve(
                    assets.Dats,
                    enumValue: FirstSourceImageEnum + i,
                    enumCategory: ClientEnumCategory);
            if (did == 0u)
                return null;

            var resolved = assets.ResolveSprite(did);
            if (resolved.Texture == 0u || resolved.Width <= 0 || resolved.Height <= 0)
                return null;
            sources[i] = new VividTargetSource(
                resolved.Texture, resolved.Width, resolved.Height);
        }

        var corners = new UiTextureElement[4];
        for (int i = 0; i < corners.Length; i++)
        {
            VividTargetSource source = sources[i];
            corners[i] = new UiTextureElement
            {
                Name = $"VividTargetCorner{i + 1}",
                Texture = source.Texture,
                Width = source.Width,
                Height = source.Height,
                ClickThrough = true,
                Anchors = AnchorEdges.None,
            };
            onScreen.AddChild(corners[i]);
        }

        var offScreen = new UiTextureElement
        {
            Name = "VividTargetIndicatorOffScreen",
            Visible = false,
            ClickThrough = true,
            ZOrder = -10_000,
            Anchors = AnchorEdges.None,
        };

        host.AddChild(onScreen);
        host.AddChild(offScreen);
        return new VividTargetIndicatorController(
            onScreen, corners, offScreen, sources, bindings);
    }

    public void Tick()
    {
        if (!_bindings.Enabled()
            || _bindings.Selection.SelectedObjectId is not uint guid
            || guid == 0u
            || guid == _bindings.PlayerGuid()
            || _bindings.ResolveTarget(guid) is not VividTargetInfo target)
        {
            Hide();
            return;
        }

        var camera = _bindings.Camera();
        VividTargetProjection projection = ComputeProjection(
            target.SelectionSphereCenter,
            target.SelectionSphereRadius,
            camera.View,
            camera.Projection,
            camera.Viewport);
        if (projection.Status == VividTargetProjectionStatus.Invalid)
        {
            Hide();
            return;
        }

        RadarBlipColors.Rgba color = RadarBlipColors.For(
            target.ItemType, target.ObjectDescriptionFlags);
        var tint = new Vector4(color.Red, color.Green, color.Blue, color.Alpha);
        if (projection.Status == VividTargetProjectionStatus.OnScreen)
        {
            VividTargetLayout layout = ComputeLayout(
                projection.RectMin, projection.RectMax, camera.Viewport, _cornerSizes);
            _onScreen.Left = layout.RootPosition.X;
            _onScreen.Top = layout.RootPosition.Y;
            _onScreen.Width = layout.RootSize.X;
            _onScreen.Height = layout.RootSize.Y;
            for (int i = 0; i < _corners.Length; i++)
            {
                SetCorner(i, layout.CornerPositions[i].X, layout.CornerPositions[i].Y);
                _corners[i].Tint = tint;
            }

            _offScreen.Visible = false;
            _onScreen.Visible = true;
            return;
        }

        uint imageEnum = SelectOffScreenImageEnum(projection.AngleDegrees);
        VividTargetSource offScreenSource = _sources[imageEnum - FirstSourceImageEnum];
        Vector2 position = ComputeOffScreenPosition(
            projection.AngleDegrees,
            camera.Viewport,
            new Vector2(offScreenSource.Width, offScreenSource.Height));
        _offScreen.Texture = offScreenSource.Texture;
        _offScreen.Left = position.X;
        _offScreen.Top = position.Y;
        _offScreen.Width = offScreenSource.Width;
        _offScreen.Height = offScreenSource.Height;
        _offScreen.Tint = tint;
        _onScreen.Visible = false;
        _offScreen.Visible = true;
    }

    private void Hide()
    {
        _onScreen.Visible = false;
        _offScreen.Visible = false;
    }

    private void SetCorner(int index, float left, float top)
    {
        _corners[index].Left = left;
        _corners[index].Top = top;
    }

    internal static VividTargetProjection ComputeProjection(
        Vector3 worldCenter,
        float worldRadius,
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector2 viewport)
    {
        if (viewport.X <= 0f || viewport.Y <= 0f
            || !float.IsFinite(worldRadius) || worldRadius < 0f)
            return default;

        bool projected = ScreenProjection.TryProjectSphereToScreenRect(
            worldCenter,
            worldRadius,
            view,
            projection,
            viewport,
            out Vector2 rectMin,
            out Vector2 rectMax,
            out _,
            minSidePixels: 0f);

        if (projected
            && rectMax.X >= 0f && rectMin.X <= viewport.X
            && rectMax.Y >= 0f && rectMin.Y <= viewport.Y)
        {
            return new VividTargetProjection(
                VividTargetProjectionStatus.OnScreen,
                rectMin,
                rectMax,
                0f);
        }

        Vector3 local = Vector3.Transform(worldCenter, view);
        float length = local.Length();
        float angle = 0f;
        if (float.IsFinite(length) && length > 1e-6f)
        {
            float right = local.X / length;
            float forward = -local.Z / length;
            float up = local.Y / length;
            float radians = forward > 0f
                ? MathF.Atan2(right, up)
                : MathF.Atan2(right, 0f);
            angle = PositiveModulo(
                450f - radians * (180f / MathF.PI),
                360f);
        }

        return new VividTargetProjection(
            VividTargetProjectionStatus.OffScreen,
            default,
            default,
            angle);
    }

    internal static uint SelectOffScreenImageEnum(float angleDegrees)
    {
        float angle = PositiveModulo(angleDegrees, 360f);
        if (angle >= 338f || angle < 23f) return 6u;
        if (angle < 68f) return 7u;
        if (angle < 113f) return 9u;
        if (angle < 158f) return 12u;
        if (angle < 203f) return 11u;
        if (angle < 248f) return 10u;
        if (angle < 293f) return 8u;
        return 5u;
    }

    internal static Vector2 ComputeOffScreenPosition(
        float angleDegrees,
        Vector2 viewport,
        Vector2 imageSize)
    {
        float angleRadians = PositiveModulo(angleDegrees, 360f) * (MathF.PI / 180f);
        var direction = new Vector2(MathF.Cos(angleRadians), -MathF.Sin(angleRadians));
        var halfImage = imageSize * 0.5f;
        var center = viewport * 0.5f;
        var minimumCenter = new Vector2(ViewportMargin) + halfImage;
        var maximumCenter = viewport - new Vector2(ViewportMargin) - halfImage;

        float horizontalDistance = direction.X switch
        {
            > 1e-6f => (maximumCenter.X - center.X) / direction.X,
            < -1e-6f => (minimumCenter.X - center.X) / direction.X,
            _ => float.PositiveInfinity,
        };
        float verticalDistance = direction.Y switch
        {
            > 1e-6f => (maximumCenter.Y - center.Y) / direction.Y,
            < -1e-6f => (minimumCenter.Y - center.Y) / direction.Y,
            _ => float.PositiveInfinity,
        };
        float distance = MathF.Min(horizontalDistance, verticalDistance);
        if (!float.IsFinite(distance) || distance < 0f)
            distance = 0f;

        Vector2 position = center + direction * distance - halfImage;
        return new Vector2(
            Math.Clamp(position.X, ViewportMargin,
                MathF.Max(ViewportMargin, viewport.X - imageSize.X - ViewportMargin)),
            Math.Clamp(position.Y, ViewportMargin,
                MathF.Max(ViewportMargin, viewport.Y - imageSize.Y - ViewportMargin)));
    }

    private static float PositiveModulo(float value, float modulus)
    {
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    internal static VividTargetLayout ComputeLayout(
        Vector2 rectMin,
        Vector2 rectMax,
        Vector2 viewport,
        IReadOnlyList<(float Width, float Height)> sizes)
    {
        if (sizes.Count != 4)
            throw new ArgumentException("Retail VividTargetIndicator has exactly four corners.", nameof(sizes));

        float cornerWidth = sizes[0].Width;
        float cornerHeight = sizes[0].Height;
        float outerLeft = rectMin.X - cornerWidth;
        float outerTop = rectMin.Y - cornerHeight;
        float outerRight = rectMax.X;
        float outerBottom = rectMax.Y;

        if (outerLeft > outerRight) outerLeft = outerRight - 1f;
        if (outerTop > outerBottom) outerTop = outerBottom - 1f;
        outerLeft = MathF.Max(outerLeft, ViewportMargin);
        outerTop = MathF.Max(outerTop, ViewportMargin);
        outerRight = MathF.Max(outerRight, cornerWidth + ViewportMargin);
        outerBottom = MathF.Max(outerBottom, cornerHeight + ViewportMargin);

        float maximumRight = viewport.X - cornerWidth - ViewportMargin;
        float maximumBottom = viewport.Y - cornerHeight - ViewportMargin;
        outerLeft = MathF.Min(outerLeft, maximumRight - cornerWidth);
        outerTop = MathF.Min(outerTop, maximumBottom - cornerHeight);
        outerRight = MathF.Min(outerRight, maximumRight);
        outerBottom = MathF.Min(outerBottom, maximumBottom);
        Vector2 rootSize = new(
            MathF.Max(1f, outerRight - outerLeft + cornerWidth),
            MathF.Max(1f, outerBottom - outerTop + cornerHeight));
        return new VividTargetLayout(
            new Vector2(outerLeft, outerTop),
            rootSize,
            [
                Vector2.Zero,
                new Vector2(rootSize.X - sizes[1].Width, 0f),
                new Vector2(rootSize.X - sizes[2].Width, rootSize.Y - sizes[2].Height),
                new Vector2(0f, rootSize.Y - sizes[3].Height),
            ]);
    }
}

internal readonly record struct VividTargetLayout(
    Vector2 RootPosition,
    Vector2 RootSize,
    IReadOnlyList<Vector2> CornerPositions);

internal enum VividTargetProjectionStatus
{
    Invalid,
    OnScreen,
    OffScreen,
}

internal readonly record struct VividTargetProjection(
    VividTargetProjectionStatus Status,
    Vector2 RectMin,
    Vector2 RectMax,
    float AngleDegrees);

internal readonly record struct VividTargetSource(
    uint Texture,
    float Width,
    float Height);
