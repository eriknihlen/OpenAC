using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Ui;

namespace AcDream.App.Tests.UI;

public sealed class UiRadarTests
{
    [Fact]
    public void HoverAndClick_SelectNearestProjectedBlip()
    {
        uint? selected = null;
        uint? hovered = null;
        var radar = new UiRadar
        {
            Width = 120,
            Height = 140,
            SelectObject = guid => selected = guid,
            HoveredObjectChanged = guid => hovered = guid,
        };
        radar.ApplySnapshot(new UiRadarSnapshot(
            0f,
            [
                new UiRadarBlip(0x10u, "Far", 31f, 30f, Vector4.One, RadarBlipShape.Plus),
                new UiRadarBlip(0x20u, "Near", 28f, 30f, Vector4.One, RadarBlipShape.Circle),
            ],
            "1.0N,1.0E"));

        var root = new UiRoot { Width = 200, Height = 200 };
        root.AddChild(radar);

        root.OnMouseMove(27, 30);
        Assert.Equal(0x20u, radar.HoveredObjectId);
        Assert.Equal(0x20u, hovered);
        Assert.Equal("Near", radar.GetTooltipText());

        root.OnMouseDown(UiMouseButton.Left, 27, 30);
        root.OnMouseUp(UiMouseButton.Left, 27, 30);
        Assert.Equal(0x20u, selected);

        root.OnMouseMove(100, 100);
        Assert.Null(radar.HoveredObjectId);
        Assert.Null(hovered);
        Assert.Null(radar.GetTooltipText());
    }

    [Fact]
    public void BlankBlips_DisablesHoverButKeepsSnapshot()
    {
        var radar = new UiRadar { Width = 120, Height = 140 };
        radar.ApplySnapshot(new UiRadarSnapshot(
            0f,
            [new UiRadarBlip(0x10u, "Hidden", 30f, 30f, Vector4.One, RadarBlipShape.Plus)],
            null,
            BlankBlips: true));

        var root = new UiRoot { Width = 200, Height = 200 };
        root.AddChild(radar);
        root.OnMouseMove(30, 30);

        Assert.Single(radar.Snapshot.Blips);
        Assert.Null(radar.HoveredObjectId);
    }

    private static (UiRoot Root, UiRadar Radar, List<string?> Shown, List<string?> Hidden)
        BuildHoverHarness(params UiRadarBlip[] blips)
    {
        var radar = new UiRadar { Width = 120, Height = 140 };
        radar.ApplySnapshot(new UiRadarSnapshot(0f, blips, null));

        var root = new UiRoot { Width = 200, Height = 200 };
        root.AddChild(radar);

        var shown = new List<string?>();
        var hidden = new List<string?>();
        root.TooltipShow += w => shown.Add(w.GetTooltipText());
        root.TooltipHide += _ => hidden.Add(radar.GetTooltipText());
        return (root, radar, shown, hidden);
    }

    [Fact]
    public void MovingBetweenBlips_WithoutLeavingTheRadar_ShowsTheSecondName()
    {
        var (root, radar, shown, _) = BuildHoverHarness(
            new UiRadarBlip(0x10u, "Alpha", 20f, 20f, Vector4.One, RadarBlipShape.Plus),
            new UiRadarBlip(0x20u, "Beta", 60f, 60f, Vector4.One, RadarBlipShape.Circle));

        root.Tick(0d, 0);
        root.OnMouseMove(20, 20);
        root.Tick(0.3d, 300);
        Assert.Equal(["Alpha"], shown);

        // Straight from one blip to the next; the cursor never leaves the radar.
        root.OnMouseMove(60, 60);
        Assert.Equal(0x20u, radar.HoveredObjectId);
        root.Tick(0.3d, 600);

        Assert.Equal(["Alpha", "Beta"], shown);
    }

    [Fact]
    public void MovingOntoEmptyRadar_ClearsTheDisplayedName()
    {
        var (root, radar, shown, hidden) = BuildHoverHarness(
            new UiRadarBlip(0x10u, "Alpha", 20f, 20f, Vector4.One, RadarBlipShape.Plus));

        root.Tick(0d, 0);
        root.OnMouseMove(20, 20);
        root.Tick(0.3d, 300);
        Assert.Equal(["Alpha"], shown);

        root.OnMouseMove(90, 90);
        Assert.Null(radar.HoveredObjectId);
        Assert.Null(radar.GetTooltipText());
        Assert.Single(hidden);

        root.Tick(0.3d, 600);
        Assert.Equal(["Alpha", null], shown);
    }

    [Fact]
    public void BlipsMovingUnderAParkedCursor_ChangeTheHoveredObject()
    {
        var (root, radar, _, _) = BuildHoverHarness(
            new UiRadarBlip(0x10u, "Alpha", 20f, 20f, Vector4.One, RadarBlipShape.Plus),
            new UiRadarBlip(0x20u, "Beta", 60f, 60f, Vector4.One, RadarBlipShape.Circle));

        root.Tick(0d, 0);
        root.OnMouseMove(20, 20);
        Assert.Equal(0x10u, radar.HoveredObjectId);

        // Same cursor, the world moved: Beta now sits where the cursor already is.
        radar.ApplySnapshot(new UiRadarSnapshot(
            0f,
            [
                new UiRadarBlip(0x10u, "Alpha", 60f, 60f, Vector4.One, RadarBlipShape.Plus),
                new UiRadarBlip(0x20u, "Beta", 20f, 20f, Vector4.One, RadarBlipShape.Circle),
            ],
            null));
        root.Tick(0.016d, 16);

        Assert.Equal(0x20u, radar.HoveredObjectId);
        Assert.Equal("Beta", radar.GetTooltipText());
    }

    [Fact]
    public void EquidistantBlips_KeepTheOneDrawnFirst()
    {
        var (root, radar, _, _) = BuildHoverHarness(
            new UiRadarBlip(0x10u, "First", 20f, 20f, Vector4.One, RadarBlipShape.Plus),
            new UiRadarBlip(0x20u, "Second", 24f, 20f, Vector4.One, RadarBlipShape.Circle));

        root.OnMouseMove(22, 20);

        Assert.Equal(0x10u, radar.HoveredObjectId);
        Assert.Equal("First", radar.GetTooltipText());
    }

    [Fact]
    public void Tick_PollsProviderAtRetailTwentyFiveMillisecondCadence()
    {
        int calls = 0;
        var radar = new UiRadar
        {
            Width = 120,
            Height = 140,
            SnapshotProvider = () =>
            {
                calls++;
                return UiRadarSnapshot.Empty;
            },
        };
        var root = new UiRoot { Width = 200, Height = 200 };
        root.AddChild(radar);

        root.Tick(0.024, 24);
        Assert.Equal(0, calls);

        root.Tick(0.0011, 25);
        Assert.Equal(1, calls);

        root.Tick(0.100, 125);
        Assert.Equal(2, calls);
    }
}
