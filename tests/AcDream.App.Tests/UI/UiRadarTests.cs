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
