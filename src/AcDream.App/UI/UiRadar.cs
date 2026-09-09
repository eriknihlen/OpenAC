using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Ui;

namespace AcDream.App.UI;

public readonly record struct UiRadarBlip(
    uint ObjectId,
    string Name,
    float PixelX,
    float PixelY,
    Vector4 Color,
    RadarBlipShape Shape,
    bool Selected = false);

public sealed record UiRadarSnapshot(
    float PlayerHeadingDegrees,
    IReadOnlyList<UiRadarBlip> Blips,
    string? CoordinatesText,
    bool BlankBlips = false,
    bool UiLocked = false)
{
    public static UiRadarSnapshot Empty { get; } = new(
        0f,
        Array.Empty<UiRadarBlip>(),
        null);
}

public sealed class UiRadar : UiElement
{
    public const uint RetailClassId = 0x10000010u;
    public const float RetailRefreshSeconds = RetailRadar.UpdateIntervalSeconds;
    public const float HoverRadiusPixels = 6f;

    private static readonly Vector4 PlayerMarkerColor = new(0f, 1f, 0f, 1f);

    private UiRadarSnapshot _snapshot = UiRadarSnapshot.Empty;
    private double _refreshAccumulator;
    private uint? _hoveredObjectId;
    private string? _hoveredObjectName;

    public Vector2 Center { get; set; } = new(60f, 60f);

    public Func<UiRadarSnapshot>? SnapshotProvider { get; set; }

    public Action<uint>? SelectObject { get; set; }

    /// <summary>Optional host hook for target highlights/status text.</summary>
    public Action<uint?>? HoveredObjectChanged { get; set; }

    public UiRadarSnapshot Snapshot => _snapshot;
    public uint? HoveredObjectId => _hoveredObjectId;

    public override bool HandlesClick => true;

    /// <summary>Apply one projected frame immediately. Used once by the binder, then by the 25 ms tick.</summary>
    public void ApplySnapshot(UiRadarSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;

        if (_hoveredObjectId is uint hovered)
        {
            bool stillPresent = false;
            for (int i = 0; i < snapshot.Blips.Count; i++)
            {
                if (snapshot.Blips[i].ObjectId == hovered)
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
                SetHovered(null, null);
        }
    }

    /// <summary>Poll the provider now, without waiting for the next retained-tree tick.</summary>
    public void Refresh()
    {
        if (SnapshotProvider is { } provider)
            ApplySnapshot(provider() ?? UiRadarSnapshot.Empty);
    }

    protected override void OnTick(double deltaSeconds)
    {
        if (SnapshotProvider is null)
            return;

        _refreshAccumulator += deltaSeconds;
        if (_refreshAccumulator < RetailRefreshSeconds)
            return;

        _refreshAccumulator %= RetailRefreshSeconds;
        Refresh();
    }

    protected override bool OnHitTest(float localX, float localY)
    {
        bool inside = base.OnHitTest(localX, localY);
        if (!inside)
        {
            SetHovered(null, null);
            return false;
        }

        UpdateHoveredBlip(localX, localY);
        return true;
    }

    public override bool OnEvent(in UiEvent e)
    {
        if (e.Type == UiEventType.HoverLeave)
        {
            SetHovered(null, null);
            return false;
        }

        if (e.Type == UiEventType.Click && _hoveredObjectId is uint guid)
        {
            SelectObject?.Invoke(guid);
            return SelectObject is not null;
        }

        return false;
    }

    public override string? GetTooltipText() => _hoveredObjectName;

    protected override void OnDrawAfterChildren(UiRenderContext ctx)
    {
        if (!_snapshot.BlankBlips)
        {
            for (int i = 0; i < _snapshot.Blips.Count; i++)
                DrawBlip(ctx, _snapshot.Blips[i]);
        }

        int cx = RoundPixel(Center.X);
        int cy = RoundPixel(Center.Y);
        DrawPixels(ctx, cx, cy, PlayerMarkerColor, RetailRadar.PlayerMarkerPixels);
    }

    private void UpdateHoveredBlip(float localX, float localY)
    {
        uint? nearestId = null;
        string? nearestName = null;
        float nearestDistanceSquared = HoverRadiusPixels * HoverRadiusPixels;

        if (!_snapshot.BlankBlips)
        {
            for (int i = 0; i < _snapshot.Blips.Count; i++)
            {
                var blip = _snapshot.Blips[i];
                float dx = localX - blip.PixelX;
                float dy = localY - blip.PixelY;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared <= nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestId = blip.ObjectId;
                    nearestName = blip.Name;
                }
            }
        }

        SetHovered(nearestId, nearestName);
    }

    private void SetHovered(uint? id, string? name)
    {
        if (_hoveredObjectId == id && _hoveredObjectName == name)
            return;

        _hoveredObjectId = id;
        _hoveredObjectName = name;
        HoveredObjectChanged?.Invoke(id);
    }

    private static void DrawBlip(UiRenderContext ctx, in UiRadarBlip blip)
    {
        int x = RoundPixel(blip.PixelX);
        int y = RoundPixel(blip.PixelY);
        DrawPixels(ctx, x, y, blip.Color, RetailRadar.GetBlipPixels(blip.Shape));

        if (blip.Selected)
            DrawPixels(ctx, x, y, blip.Color, RetailRadar.SelectionPixels);
    }

    private static void DrawPixels(
        UiRenderContext ctx,
        int centerX,
        int centerY,
        Vector4 color,
        ReadOnlySpan<RadarPixelOffset> offsets)
    {
        for (int i = 0; i < offsets.Length; i++)
            ctx.DrawFill(centerX + offsets[i].X, centerY + offsets[i].Y, 1, 1, color);
    }

    private static int RoundPixel(float value)
        => checked((int)MathF.Round(value, MidpointRounding.ToEven));
}
