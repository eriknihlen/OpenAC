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

    /// <summary>A blip counts as under the cursor within six pixels, compared squared so the
    /// test stays in whole pixels - the same whole pixels the blip was drawn at.</summary>
    public const int HoverRadiusPixelsSquared = 36;

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

    /// <summary>The cursor is re-tested against the blips on every move over the radar, not
    /// only when it arrives, so sweeping from one blip to the next names each one in turn.</summary>
    public override bool ReceivesHoverMouseMove => true;

    protected override void OnTick(double deltaSeconds)
    {
        if (SnapshotProvider is not null)
        {
            _refreshAccumulator += deltaSeconds;
            if (_refreshAccumulator >= RetailRefreshSeconds)
            {
                _refreshAccumulator %= RetailRefreshSeconds;
                Refresh();
            }
        }

        // The blips move too, so the object under a parked cursor is resolved every frame
        // against the live cursor position and not only when the cursor itself moves.
        if (FindRoot() is { } root)
        {
            var screen = ScreenPosition;
            ResolveObjectUnderCursor(
                (int)(root.MouseX - screen.X),
                (int)(root.MouseY - screen.Y));
        }
    }

    public override bool OnEvent(in UiEvent e)
    {
        // Only the event's own target may read its coordinates; a bubbled child move carries
        // the child's local position, which would place the cursor somewhere it never was.
        if ((e.Type == UiEventType.HoverEnter || e.Type == UiEventType.MouseMove)
            && ReferenceEquals(e.Target, this))
        {
            ResolveObjectUnderCursor(e.Data1, e.Data2);
            return false;
        }

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

    /// <summary>Pick the blip under <paramref name="localX"/>/<paramref name="localY"/>: the
    /// closest one within the hover radius, measured from the whole pixel the blip was drawn at.
    /// A blip only displaces a closer candidate, so an exact tie keeps the one drawn first.
    /// Nothing within the radius clears the name.</summary>
    private void ResolveObjectUnderCursor(int localX, int localY)
    {
        uint candidateId = 0u;
        string? candidateName = null;
        int nearestDistanceSquared = int.MaxValue;

        if (!_snapshot.BlankBlips)
        {
            for (int i = 0; i < _snapshot.Blips.Count; i++)
            {
                var blip = _snapshot.Blips[i];
                int dx = localX - RoundPixel(blip.PixelX);
                int dy = localY - RoundPixel(blip.PixelY);
                int distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > HoverRadiusPixelsSquared
                    || distanceSquared >= nearestDistanceSquared)
                {
                    continue;
                }

                nearestDistanceSquared = distanceSquared;
                candidateId = blip.ObjectId;
                candidateName = blip.Name;
            }
        }

        SetHovered(candidateId == 0u ? null : candidateId, candidateId == 0u ? null : candidateName);
    }

    private void SetHovered(uint? id, string? name)
    {
        bool nameChanged = !string.Equals(_hoveredObjectName, name, StringComparison.Ordinal);
        if (_hoveredObjectId == id && !nameChanged)
            return;

        _hoveredObjectId = id;
        _hoveredObjectName = name;

        // A displayed name belongs to the blip that was under the cursor when it appeared, so a
        // different name has to replace it instead of waiting for the cursor to leave the radar.
        if (nameChanged)
            NotifyTooltipTextChanged();

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
