using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankNavRouteSerializerTests
{
    [Fact]
    public void LoadsEveryOfficialNav12WaypointPayload()
    {
        const string nav = """
            uTank2 NAV 1.2
            1
            10
            0
            12.5
            -3.25
            0.5
            0
            1
            13
            -4
            0
            0
            1234
            2
            14
            -5
            0
            0
            987
            3
            15
            -6
            0
            0
            2500
            4
            16
            -7
            0
            0
            /say hello
            5
            17
            -8
            0
            0
            4321
            Vendor Bob
            6
            18
            -9
            0
            0
            Portal to Holtburg
            14
            True
            18.5
            -9.5
            1
            7
            19
            -10
            0
            0
            Town Crier
            37
            True
            19.5
            -10.5
            2
            8
            20
            -11
            0
            0
            9
            21
            -12
            0
            0
            180.5
            True
            1000.00005
            """;
        var settings = new NavigationSettings { Enabled = true };

        bool loaded = VtankNavRouteSerializer.TryLoad(
            nav,
            settings,
            NoOpSpellCatalog.Instance,
            out string error);

        Assert.True(loaded, error);
        Assert.True(settings.Enabled);
        Assert.Equal(RouteMode.Circular, settings.Mode);
        Assert.Equal(10, settings.Waypoints.Count);
        Assert.Equal(RouteWaypointType.Point, settings.Waypoints[0].Type);
        Assert.Equal(12.5, settings.Waypoints[0].Position.EastWest);
        Assert.Equal(1234u, settings.Waypoints[1].ObjectId);
        Assert.Equal(987u, settings.Waypoints[2].RecallSpellId);
        Assert.Equal(2500, settings.Waypoints[3].DurationMilliseconds);
        Assert.Equal("/say hello", settings.Waypoints[4].Text);
        Assert.Equal("Vendor Bob", settings.Waypoints[5].ObjectName);
        Assert.Equal(18, settings.Waypoints[6].Position.EastWest);
        Assert.Equal(18.5, settings.Waypoints[6].ReferencePosition.EastWest);
        Assert.Equal(-9.5, settings.Waypoints[6].ReferencePosition.NorthSouth);
        Assert.Equal(1, settings.Waypoints[6].ReferencePosition.Elevation);
        Assert.Equal("Town Crier", settings.Waypoints[7].ObjectName);
        Assert.Equal(19, settings.Waypoints[7].Position.EastWest);
        Assert.Equal(19.5, settings.Waypoints[7].ReferencePosition.EastWest);
        Assert.Equal(14, settings.Waypoints[6].LegacyObjectClass);
        Assert.Equal(37, settings.Waypoints[7].LegacyObjectClass);
        Assert.True(settings.Waypoints[6].LegacyReferenceValid);
        Assert.Equal(RouteWaypointType.Checkpoint, settings.Waypoints[8].Type);
        Assert.Equal(180.5f, settings.Waypoints[9].JumpHeadingDegrees);
        Assert.True(settings.Waypoints[9].JumpRun);
        Assert.Equal(1000, settings.Waypoints[9].JumpChargeMilliseconds);
        Assert.Equal(RouteJumpDirection.StrafeRight, settings.Waypoints[9].JumpDirection);
    }

    [Fact]
    public void LoadsEmbeddedWrapperAndDoesNotMutateOnFailure()
    {
        const string wrapped = """
            Route Name
            1
            uTank2 NAV 1.2
            4
            1
            0
            1
            2
            3
            0
            """;
        var settings = new NavigationSettings();
        Assert.True(VtankNavRouteSerializer.TryLoad(
            wrapped,
            settings,
            NoOpSpellCatalog.Instance,
            out _));
        Assert.Equal(RouteMode.Once, settings.Mode);
        Assert.Single(settings.Waypoints);

        Assert.False(VtankNavRouteSerializer.TryLoad(
            "broken",
            settings,
            NoOpSpellCatalog.Instance,
            out string error));
        Assert.NotEmpty(error);
        Assert.Equal(RouteMode.Once, settings.Mode);
        Assert.Single(settings.Waypoints);
    }

    private sealed class NoOpSpellCatalog : ISpellCatalog
    {
        public static NoOpSpellCatalog Instance { get; } = new();
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }
    }
}
