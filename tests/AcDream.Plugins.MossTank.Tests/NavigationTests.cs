using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class NavigationTests
{
    [Theory]
    [InlineData(0d, 1d, 0f)]
    [InlineData(1d, 0d, 90f)]
    [InlineData(0d, -1d, 180f)]
    [InlineData(-1d, 0d, 270f)]
    public void DesiredHeadingUsesVtankCompassConvention(
        double eastWest,
        double northSouth,
        float expected)
    {
        PluginNavigationPosition origin = Position(0d, 0d);
        PluginNavigationPosition target = Position(eastWest, northSouth);

        Assert.Equal(expected, NavigationController.DesiredHeading(origin, target));
    }

    [Theory]
    [InlineData(350f, 10f, 20f)]
    [InlineData(10f, 350f, -20f)]
    [InlineData(90f, 270f, 180f)]
    public void SignedHeadingDeltaChoosesShortestRetailTurn(
        float current,
        float desired,
        float expected) =>
        Assert.Equal(expected, NavigationController.SignedHeadingDelta(current, desired));

    [Fact]
    public void PointSteeringStopsAndFacesTheHeadingOutsideTheFourDegreeBand()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Empty(automation.Intents);
        Assert.Equal(1, automation.ClearCount);
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));
    }

    [Fact]
    public void PointSteeringDoesNotReissueFaceHeadingInsideSevenTenthsOfASecond()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.293d, canAct: true));
        Assert.True(controller.Tick(0.293d, canAct: true));

        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));

        Assert.True(controller.Tick(0.293d, canAct: true));
        Assert.Single(automation.FacedHeadings);
        Assert.True(controller.Tick(0.293d, canAct: true));
        Assert.Equal(2, automation.FacedHeadings.Count);
    }

    [Fact]
    public void PointSteeringRunsForwardInsideTheFourDegreeBand()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 88f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.True(intent.Run);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.TurnRight);
        Assert.Empty(automation.FacedHeadings);
    }

    [Fact]
    public void PointSteeringIssuesNoTurnKeyIntentsAtAnyOffset()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 60f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.DoesNotContain(
            automation.Intents,
            static intent => intent.TurnLeft || intent.TurnRight);
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));
    }

    [Fact]
    public void ANavigateTierBelowTheWinnerStillClearsTheMovementIntent()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Circular,
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var rule = new ControllerMacroRule(
            "NavigateRouteIdle",
            context => controller.Tick(context.ElapsedSeconds, context.CanAct),
            gate: () => true,
            onLostTurn: controller.StopForLostTurn,
            bookkeepWhenBlocked: false);

        Assert.True(rule.ValidNow(new MacroPassContext(0.05d, CanAct: true)));
        Assert.NotEmpty(automation.FacedHeadings);
        Assert.Equal(1, automation.ClearCount);

        Assert.False(rule.ValidNow(new MacroPassContext(0.05d, CanAct: false)));
        Assert.Equal(2, automation.ClearCount);
    }

    [Fact]
    public void CircularRouteWrapsAndOnceRouteStops()
    {
        RouteWaypoint first = Waypoint(RouteWaypointType.Point, Position(0d, 0d));
        RouteWaypoint second = Waypoint(RouteWaypointType.Point, Position(1d, 0d));
        var circularAutomation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(first.Position),
        };
        NavigationController circular = Controller(
            circularAutomation,
            RouteMode.Circular,
            first,
            second);

        Assert.True(circular.Tick(0.05d, canAct: true));
        Assert.Equal(1, circular.CurrentWaypointIndex);
        circularAutomation.NavigationSnapshot = Snapshot(second.Position);
        Assert.True(circular.Tick(0.05d, canAct: true));
        Assert.Equal(0, circular.CurrentWaypointIndex);

        var onceAutomation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(first.Position),
        };
        NavigationController once = Controller(
            onceAutomation,
            RouteMode.Once,
            first);

        Assert.True(once.Tick(0.05d, canAct: true));
        Assert.False(once.Tick(0.05d, canAct: true));
        Assert.Equal("Once route complete.", once.Status);
    }

    [Fact]
    public void FollowReadsMovingTargetAndHoldsAtMinimumDistance()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(1d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 2d,
            FollowTargetObjectId = 7u,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);

        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.005d, 0d));
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, automation.ClearCount);
        Assert.Contains("holding", controller.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FollowAroundCornersUsesOldestUnreachedBreadcrumb()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.1d, 0d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 2d,
            FollowTargetObjectId = 7u,
            FollowAroundCorners = true,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.Objects[7u] = new PluginNavigationObject(
            7u,
            "Leader",
            Position(0.1d, 0.1d));
        automation.Intents.Clear();

        Assert.True(controller.Tick(0.05d, canAct: true));

        PluginMovementIntent intent = Assert.Single(automation.Intents);
        Assert.True(intent.Forward);
        Assert.False(intent.TurnLeft);
        Assert.False(intent.TurnRight);
    }

    [Fact]
    public void CheckpointWaitsForServerAcceptedPosition()
    {
        PluginNavigationPosition point = Position(0d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(point) with
            {
                ConfirmedPosition = Position(0.1d, 0d),
                ConfirmedPositionRevision = 4UL,
            },
        };
        NavigationController controller = Controller(
            automation,
            RouteMode.Once,
            Waypoint(RouteWaypointType.Checkpoint, point));

        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Contains("waiting for server", controller.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(controller.Tick(14d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);

        automation.NavigationSnapshot = Snapshot(point) with
        {
            ConfirmedPosition = point,
            ConfirmedPositionRevision = 5UL,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    [Fact]
    public void ClosedDoorPausesRouteAndUsesCanonicalItemAction()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        automation.WorldObjects.Add(new PluginNavigationObject(
            55u,
            "Dungeon Door",
            Position(0.01d, 0d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        var settings = new NavigationSettings
        {
            Enabled = true,
            OpenDoors = true,
            Mode = RouteMode.Circular,
        };
        settings.Waypoints.Add(Waypoint(
            RouteWaypointType.Point,
            Position(1d, 0d)));
        var controller = new NavigationController(new FakeHost(automation), settings);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([55u], automation.UsedObjects);
        Assert.Empty(automation.Intents);

        automation.WorldObjects[0] = automation.WorldObjects[0] with
        {
            IsOpen = true,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(Assert.Single(automation.Intents).Forward);
    }

    [Fact]
    public void PauseAndChatActionsObserveOfficialInitialDelay()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
        };
        RouteWaypoint pause = Waypoint(RouteWaypointType.Pause, Position(0d, 0d));
        pause.DurationMilliseconds = 100;
        RouteWaypoint chat = Waypoint(RouteWaypointType.ChatCommand, Position(0d, 0d));
        chat.Text = "/say route";
        NavigationController controller = Controller(
            automation,
            RouteMode.Once,
            pause,
            chat);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.True(controller.Tick(0.19d, canAct: true));
        Assert.Empty(automation.SubmittedChat);
        Assert.True(controller.Tick(0.01d, canAct: true));
        Assert.Equal(["/say route"], automation.SubmittedChat);
        Assert.False(controller.Tick(0.01d, canAct: true));
    }

    [Fact]
    public void PortalWaypointWaitsForPortalExitRatherThanUseDispatch()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            ItemCompletion = new PluginItemUseCompletion(4, 10u, 0u, 0u),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.Portal, Position(0d, 0d));
        use.ObjectId = 77u;
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([77u], automation.UsedObjects);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.ItemCompletion = new PluginItemUseCompletion(5, 77u, 0u, 0u);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = automation.NavigationSnapshot with
        {
            IsPortalSpace = true,
        };
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = Snapshot(Position(0.1d, 0d));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    [Fact]
    public void UseNpcRepeatsUntilTheNpcRespondsInChat()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            FoundObject = new PluginNavigationObject(
                91u,
                "Town Crier",
                Position(0.01d, 0d)),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.UseNpc, Position(0d, 0d));
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.ChatMessages.Add(new PluginChatMessage(
            1UL,
            91u,
            3,
            "Town Crier",
            "Town Crier tells you, Welcome.",
            string.Empty));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
    }

    [Fact]
    public void NamedNpcWaypointReacquiresChangedObjectId()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            FoundObject = new PluginNavigationObject(
                91u,
                "Town Crier",
                Position(0.01d, 0d)),
        };
        RouteWaypoint use = Waypoint(RouteWaypointType.UseNpc, Position(0d, 0d));
        use.ObjectId = 77u;
        use.ObjectName = "Town Crier";
        NavigationController controller = Controller(automation, RouteMode.Once, use);

        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(91u, use.ObjectId);
        Assert.Equal([91u], automation.UsedObjects);
        Assert.Equal("Town Crier", automation.FindName);
    }

    [Fact]
    public void JumpAlignsBeforeChargingAndWaitsForLanding()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 0f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 100;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(automation.Intents);
        Assert.Equal(90f, Assert.Single(automation.FacedHeadings));

        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(automation.Intents[^1].Jump);
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.False(automation.Intents[^1].Jump);

        automation.NavigationSnapshot = Snapshot(
            Position(0d, 0d, heading: 90f),
            airborne: true);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f));
        Assert.True(controller.Tick(0.25d, canAct: true));
        Assert.False(controller.Tick(0.01d, canAct: true));
    }

    [Fact]
    public void JumpChargeExecutionClampsAtRetailTwoThousandMillisecondCeiling()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d, heading: 90f)),
        };
        RouteWaypoint jump = Waypoint(RouteWaypointType.Jump, Position(0d, 0d));
        jump.JumpHeadingDegrees = 90f;
        jump.JumpChargeMilliseconds = 5000;
        NavigationController controller = Controller(automation, RouteMode.Once, jump);

        Assert.True(controller.Tick(1.9d, canAct: true));
        Assert.True(automation.Intents[^1].Jump);

        Assert.True(controller.Tick(0.2d, canAct: true));
        Assert.False(automation.Intents[^1].Jump);
    }

    [Fact]
    public void RouteProfilesRoundTripWaypointFieldsButLeaveSettingsOwnedFieldsAlone()
    {
        var storage = new MemoryStorage();
        var host = new FakeHost(new FakeAutomation(), storage);
        var source = new NavigationSettings
        {
            Mode = RouteMode.Linear,
        };
        source.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Jump,
            Position = new PluginNavigationPosition(0x7F7F0001u, 1.2d, -3.4d, 5.6d, 78f, true),
            ObjectId = 88u,
            ObjectName = "Portal",
            Text = "/say hello",
            DurationMilliseconds = 1234,
            Recall = RouteRecallKind.SecondaryPortalRecall,
            JumpHeadingDegrees = 271.5f,
            JumpRun = true,
            JumpChargeMilliseconds = 875,
            JumpDirection = RouteJumpDirection.StrafeRight,
        });
        var first = new MossTankRouteProfileStore(host);
        Assert.True(first.BindCharacter("Test Character"));
        first.SaveCurrent(source);

        // Pre-seed values a Settings-profile load would already have set —
        // loading the route must leave every one of them untouched.
        var target = new NavigationSettings
        {
            Enabled = false,
            Priority = false,
            MinimumDistanceMeters = 9d,
            FollowAroundCorners = true,
            OpenDoors = false,
            DoorIdentifyRangeMeters = 11d,
            DoorOpenRangeMeters = 1d,
            DoorLockpickExcessThreshold = -3,
        };
        var second = new MossTankRouteProfileStore(host);
        Assert.True(second.BindCharacter("Test Character"));
        Assert.True(second.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.Equal(RouteMode.Linear, target.Mode);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(RouteWaypointType.Jump, waypoint.Type);
        Assert.Equal(271.5f, waypoint.JumpHeadingDegrees);
        Assert.True(waypoint.JumpRun);
        Assert.Equal(875, waypoint.JumpChargeMilliseconds);
        Assert.Equal(RouteJumpDirection.Forward, waypoint.JumpDirection);

        Assert.False(target.Enabled);
        Assert.False(target.Priority);
        Assert.Equal(9d, target.MinimumDistanceMeters);
        Assert.True(target.FollowAroundCorners);
        Assert.False(target.OpenDoors);
        Assert.Equal(11d, target.DoorIdentifyRangeMeters);
        Assert.Equal(1d, target.DoorOpenRangeMeters);
        Assert.Equal(-3, target.DoorLockpickExcessThreshold);
    }

    private static string LegacyRouteByCharacterKey(string characterName)
    {
        string identity = "char:" + characterName.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/route/{hash}.json";
    }

    [Fact]
    public void RouteStoreMigratesLegacyJsonProfileToAfAndDeletesTheJsonKey()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = """
            {
              "Mode": 1,
              "Waypoints": [
                { "Type": 0, "EastWest": 5.0, "NorthSouth": 6.0 }
              ]
            }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.True(store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.False(storage.Text.ContainsKey(legacyKey));
        Assert.Equal(RouteMode.Linear, target.Mode);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(5.0d, waypoint.Position.EastWest, precision: 3);
        Assert.Equal(6.0d, waypoint.Position.NorthSouth, precision: 3);

        // Idempotent second run: nothing left to migrate.
        var reopened = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(reopened.BindCharacter("Barris"));
        var reloaded = new NavigationSettings();
        Assert.True(reopened.LoadCurrent(reloaded, MetafSerializer.NoOpSpells.Instance));
        Assert.Single(reloaded.Waypoints);
    }

    [Fact]
    public void RouteStoreLeavesLegacyJsonUntouchedWhenAfCounterpartExists()
    {
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = """{ "Mode": 1, "Waypoints": [] }""";
        string realKey = "navs/" + VtankProfileDirectory.AutoCharacterFileName(
            "Barris", string.Empty, "af");
        var real = new NavigationSettings { Mode = RouteMode.Circular };
        real.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = new PluginNavigationPosition(0x00010001u, 1d, 2d, 0d, 0f, true),
        });
        storage.Text[realKey] = MetafSerializer.SaveNav(real);

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.True(store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.True(storage.Text.ContainsKey(legacyKey));
        Assert.Equal(RouteMode.Circular, target.Mode);
        Assert.Single(target.Waypoints);
    }

    [Theory]
    [InlineData(0, (int)RouteRecallKind.LifestoneRecall)]        // old Lifestone
    [InlineData(1, (int)RouteRecallKind.Marketplace)]              // old Marketplace
    [InlineData(2, (int)RouteRecallKind.PrimaryPortalRecall)]      // old PrimaryPortal
    [InlineData(3, (int)RouteRecallKind.SecondaryPortalRecall)]    // old SecondaryPortal
    public void LegacyJsonRouteMigratesOldRecallOrdinalToTheRightNewKind(
        int legacyOrdinal,
        int expectedKindOrdinal)
    {
        var expectedKind = (RouteRecallKind)expectedKindOrdinal;
        var storage = new MemoryStorage();
        string legacyKey = LegacyRouteByCharacterKey("Barris");
        storage.Text[legacyKey] = $$"""
            {
              "Mode": 1,
              "Waypoints": [
                { "Type": 2, "EastWest": 1.0, "NorthSouth": 2.0, "Recall": {{legacyOrdinal}} }
              ]
            }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        Assert.True(store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(RouteWaypointType.Recall, waypoint.Type);

        string fileName = "navs/" + VtankProfileDirectory.AutoCharacterFileName(
            "Barris", string.Empty, "af");
        Assert.True(storage.Text.TryGetValue(fileName, out string? af));
        Assert.Contains($"{{{RouteWaypoint.RecallDisplayName(expectedKind)}}}", af);
    }

    private static string LegacyRouteNamedKey(string name)
    {
        string identity = "named:" + name.Trim().ToUpperInvariant();
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"profiles/route/{hash}.json";
    }

    [Fact]
    public void RouteRosterSweepConvertsEveryNamedLegacyProfileOnce()
    {
        var storage = new MemoryStorage();
        storage.Text["profiles/route/index.json"] = """{ "Names": ["Farming", "Buffing"] }""";
        storage.Text[LegacyRouteNamedKey("Farming")] = """
            { "Mode": 1, "Waypoints": [ { "Type": 0, "EastWest": 1.0, "NorthSouth": 2.0 } ] }
            """;
        storage.Text[LegacyRouteNamedKey("Buffing")] = """
            { "Mode": 1, "Waypoints": [ { "Type": 0, "EastWest": 3.0, "NorthSouth": 4.0 } ] }
            """;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.False(storage.Text.ContainsKey(LegacyRouteNamedKey("Farming")));
        Assert.False(storage.Text.ContainsKey(LegacyRouteNamedKey("Buffing")));
        Assert.False(storage.Text.ContainsKey("profiles/route/index.json"));
        var farming = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            storage.Text["navs/Farming.af"], farming, MetafSerializer.NoOpSpells.Instance, out _));
        Assert.Equal(1.0d, Assert.Single(farming.Waypoints).Position.EastWest, precision: 3);
        var buffing = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            storage.Text["navs/Buffing.af"], buffing, MetafSerializer.NoOpSpells.Instance, out _));
        Assert.Equal(3.0d, Assert.Single(buffing.Waypoints).Position.EastWest, precision: 3);

        // Idempotent: a fresh store against the same storage sweeps nothing
        // more (there is no roster key left to read).
        var reopened = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(reopened.BindCharacter("Barris"));
        var reloadTarget = new NavigationSettings();
        reopened.LoadCurrent(reloadTarget, MetafSerializer.NoOpSpells.Instance);
        Assert.True(storage.Text.ContainsKey("navs/Farming.af"));
        Assert.True(storage.Text.ContainsKey("navs/Buffing.af"));
    }


    [Fact]
    public void RouteStoreMigratesFlatNavMarkedFileIntoNavsFolderWithMarkerStripped()
    {
        var storage = new MemoryStorage();
        var route = new NavigationSettings();
        route.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = new PluginNavigationPosition(0x00010001u, 7d, 8d, 0d, 0f, true),
        });
        storage.Text["nav_Hunt.af"] = MetafSerializer.SaveNav(route);

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.True(storage.Text.ContainsKey("navs/Hunt.af"));
        Assert.False(storage.Text.ContainsKey("nav_Hunt.af"));

        // Idempotent second run: nothing left at the root to migrate.
        var reopened = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(reopened.BindCharacter("Barris"));
        reopened.LoadCurrent(new NavigationSettings(), MetafSerializer.NoOpSpells.Instance);
        Assert.True(storage.Text.ContainsKey("navs/Hunt.af"));
        Assert.False(storage.Text.ContainsKey("nav_Hunt.af"));
    }

    [Fact]
    public void RouteStoreMigratesFlatHiddenAutoRouteFileWithMarkerStripped()
    {
        var storage = new MemoryStorage();
        storage.Text["--Barris_Coldeve.af"] = MetafSerializer.SaveNav(new NavigationSettings());
        // meta's own (unmarked) flat auto file — must be left for the Meta
        // store's own sweep, not touched here.
        var route = new NavigationSettings();
        route.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = new PluginNavigationPosition(0x00010001u, 1d, 2d, 0d, 0f, true),
        });
        storage.Text["--nav_Barris_Coldeve.af"] = MetafSerializer.SaveNav(route);

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        var target = new NavigationSettings();
        store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.True(storage.Text.ContainsKey("navs/--Barris_Coldeve.af"));
        Assert.False(storage.Text.ContainsKey("--nav_Barris_Coldeve.af"));
        Assert.True(storage.Text.ContainsKey("--Barris_Coldeve.af"));
    }

    [Fact]
    public void RouteStoreLeavesFlatFileInPlaceWhenNavsDestinationAlreadyExists()
    {
        var storage = new MemoryStorage();
        var canonical = new NavigationSettings();
        canonical.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Point,
            Position = new PluginNavigationPosition(0x00010001u, 9d, 9d, 0d, 0f, true),
        });
        string canonicalContent = MetafSerializer.SaveNav(canonical);
        storage.Text["navs/Hunt.af"] = canonicalContent;
        storage.Text["nav_Hunt.af"] = MetafSerializer.SaveNav(new NavigationSettings());

        var host = new FakeHost(new FakeAutomation(), storage);
        var store = new MossTankRouteProfileStore(host);
        Assert.True(store.BindCharacter("Barris"));
        store.LoadCurrent(new NavigationSettings(), MetafSerializer.NoOpSpells.Instance);

        Assert.Equal(canonicalContent, storage.Text["navs/Hunt.af"]);
        Assert.True(storage.Text.ContainsKey("nav_Hunt.af"));
        Assert.Contains(
            host.Logger.Warnings,
            message => message.Contains("nav_Hunt.af", StringComparison.Ordinal)
                && message.Contains("navs/Hunt.af", StringComparison.Ordinal));
    }

    [Fact]
    public void RouteStoreLeavesNonMarkedFlatAfFilesForTheMetaStore()
    {
        var storage = new MemoryStorage();
        storage.Text["SharedMeta.af"] = "1\r\n";

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        store.LoadCurrent(new NavigationSettings(), MetafSerializer.NoOpSpells.Instance);

        Assert.True(storage.Text.ContainsKey("SharedMeta.af"));
        Assert.False(storage.Text.ContainsKey("navs/SharedMeta.af"));
    }


    [Fact]
    public void RouteStoreRefusesToLoadAMetaOnlyFileWithNoticeNamingMetasFolder()
    {
        string metaOnlyContent = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "vtank", "af", "bella.af"));
        var storage = new MemoryStorage();
        storage.Text["navs/Misplaced.af"] = metaOnlyContent;

        var store = new MossTankRouteProfileStore(new FakeHost(new FakeAutomation(), storage));
        Assert.True(store.BindCharacter("Barris"));
        Assert.True(store.Select("Misplaced"));

        var target = new NavigationSettings();
        bool loaded = store.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance);

        Assert.False(loaded);
        Assert.NotNull(store.RecoveryNotice);
        Assert.Contains("metas/", store.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowModeRouteRoundTripsTheFollowTargetThroughAf()
    {
        var storage = new MemoryStorage();
        var host = new FakeHost(new FakeAutomation(), storage);
        var source = new NavigationSettings
        {
            Mode = RouteMode.Target,
            FollowTargetObjectId = 99u,
            FollowTargetName = "Leader",
        };
        var first = new MossTankRouteProfileStore(host);
        Assert.True(first.BindCharacter("Test Character"));
        first.SaveCurrent(source);

        var target = new NavigationSettings();
        var second = new MossTankRouteProfileStore(host);
        Assert.True(second.BindCharacter("Test Character"));
        Assert.True(second.LoadCurrent(target, MetafSerializer.NoOpSpells.Instance));

        Assert.Equal(RouteMode.Target, target.Mode);
        Assert.Equal(99u, target.FollowTargetObjectId);
        Assert.Equal("Leader", target.FollowTargetName);
    }


    [Fact]
    public void RouteRecallKindListsVTanksTwentySixRecallsInOrderPlusMarketplaceLast()
    {
        Assert.Equal(
            [
                "PrimaryPortalRecall", "SecondaryPortalRecall", "LifestoneRecall",
                "LifestoneSending", "PortalRecall", "RecallAphusLassel",
                "RecallTheSanctuary", "RecallToTheSingularityCaul", "GlendenWoodRecall",
                "AerlintheRecall", "MountLetheRecall", "UlgrimsRecall", "BurRecall",
                "ParadoxTouchedOlthoiInfestedAreaRecall", "CallOfTheMhoireForge",
                "ColosseumRecall", "FacilityHubRecall", "GearKnightInvasionAreaCampRecall",
                "LostCityOfNeftetRecall", "ReturnToTheKeep", "RynthidRecall",
                "ViridianRiseRecall", "ViridianRiseGreatTreeRecall",
                "CelestialHandStrongholdRecall", "RadiantBloodStrongholdRecall",
                "EldrytchWebStrongholdRecall", "Marketplace",
            ],
            Enum.GetNames<RouteRecallKind>());
    }

    /// <summary>
    /// The name<->id table both ways: every non-Marketplace kind's display
    /// name (RecallDisplayName) round-trips back to the SAME kind via its
    /// spell id (RecallSpellId is exposed nowhere to parse by name, so
    /// this proves the two lookups agree with each other rather than one
    /// silently drifting).
    /// </summary>
    [Theory]
    [InlineData((int)RouteRecallKind.PrimaryPortalRecall, "Primary Portal Recall", 48u)]
    [InlineData((int)RouteRecallKind.SecondaryPortalRecall, "Secondary Portal Recall", 2647u)]
    [InlineData((int)RouteRecallKind.LifestoneRecall, "Lifestone Recall", 1635u)]
    [InlineData((int)RouteRecallKind.LifestoneSending, "Lifestone Sending", 1636u)]
    [InlineData((int)RouteRecallKind.PortalRecall, "Portal Recall", 2645u)]
    [InlineData((int)RouteRecallKind.RecallAphusLassel, "Recall Aphus Lassel", 2931u)]
    [InlineData((int)RouteRecallKind.RecallTheSanctuary, "Recall the Sanctuary", 2023u)]
    [InlineData((int)RouteRecallKind.RecallToTheSingularityCaul, "Recall to the Singularity Caul", 2943u)]
    [InlineData((int)RouteRecallKind.GlendenWoodRecall, "Glenden Wood Recall", 3865u)]
    [InlineData((int)RouteRecallKind.AerlintheRecall, "Aerlinthe Recall", 2041u)]
    [InlineData((int)RouteRecallKind.MountLetheRecall, "Mount Lethe Recall", 2813u)]
    [InlineData((int)RouteRecallKind.UlgrimsRecall, "Ulgrim's Recall", 2941u)]
    [InlineData((int)RouteRecallKind.BurRecall, "Bur Recall", 4084u)]
    [InlineData((int)RouteRecallKind.ParadoxTouchedOlthoiInfestedAreaRecall,
        "Paradox-touched Olthoi Infested Area Recall", 4198u)]
    [InlineData((int)RouteRecallKind.CallOfTheMhoireForge, "Call of the Mhoire Forge", 4128u)]
    [InlineData((int)RouteRecallKind.ColosseumRecall, "Colosseum Recall", 4213u)]
    [InlineData((int)RouteRecallKind.FacilityHubRecall, "Facility Hub Recall", 5175u)]
    [InlineData((int)RouteRecallKind.GearKnightInvasionAreaCampRecall,
        "Gear Knight Invasion Area Camp Recall", 5330u)]
    [InlineData((int)RouteRecallKind.LostCityOfNeftetRecall, "Lost City of Neftet Recall", 5541u)]
    [InlineData((int)RouteRecallKind.ReturnToTheKeep, "Return to the Keep", 4214u)]
    [InlineData((int)RouteRecallKind.RynthidRecall, "Rynthid Recall", 6150u)]
    [InlineData((int)RouteRecallKind.ViridianRiseRecall, "Viridian Rise Recall", 6321u)]
    [InlineData((int)RouteRecallKind.ViridianRiseGreatTreeRecall, "Viridian Rise Great Tree Recall", 6322u)]
    [InlineData((int)RouteRecallKind.CelestialHandStrongholdRecall, "Celestial Hand Stronghold Recall", 6325u)]
    [InlineData((int)RouteRecallKind.RadiantBloodStrongholdRecall, "Radiant Blood Stronghold Recall", 6327u)]
    [InlineData((int)RouteRecallKind.EldrytchWebStrongholdRecall, "Eldrytch Web Stronghold Recall", 6326u)]
    [InlineData((int)RouteRecallKind.Marketplace, "Marketplace Recall", 0u)]
    public void RecallNameAndSpellIdTablesAgree(int kindOrdinal, string name, uint spellId)
    {
        var kind = (RouteRecallKind)kindOrdinal;
        Assert.Equal(name, RouteWaypoint.RecallDisplayName(kind));
        Assert.Equal(spellId, RouteWaypoint.SpellIdForRecall(kind));
    }

    [Fact]
    public void RecallWaypointWithNonZeroSpellIdCastsThatSpell()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            Recall = RouteRecallKind.AerlintheRecall,
            RecallSpellId = RouteWaypoint.SpellIdForRecall(RouteRecallKind.AerlintheRecall),
            RecallSpellName = RouteWaypoint.RecallDisplayName(RouteRecallKind.AerlintheRecall),
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Contains(2041u, magic.CastSpellIds);
    }

    [Fact]
    public void RecallWaypointForMarketplaceSubmitsTheSlashCommandNotACast()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            Recall = RouteRecallKind.Marketplace,
            RecallSpellId = RouteWaypoint.SpellIdForRecall(RouteRecallKind.Marketplace),
            RecallSpellName = RouteWaypoint.RecallDisplayName(RouteRecallKind.Marketplace),
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Empty(magic.CastSpellIds);
        Assert.Contains("/marketplace", automation.SubmittedChat);
    }

    [Fact]
    public void RecallWaypointWithUnresolvedSpellNameRefusesAndSkipsWithoutCasting()
    {
        var magic = new FakeMagic();
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Position(0d, 0d)),
            Magic = magic,
        };
        var waypoint = new RouteWaypoint
        {
            Type = RouteWaypointType.Recall,
            RecallSpellId = 0u,
            RecallSpellName = "NotARealSpell",
            Position = Position(0d, 0d),
        };
        NavigationController controller = Controller(automation, RouteMode.Once, waypoint);

        Assert.True(controller.Tick(0.1d, canAct: true));

        Assert.Empty(magic.CastSpellIds);
        Assert.Empty(automation.SubmittedChat);
        Assert.Contains("NotARealSpell", controller.Status);

        Assert.False(controller.Tick(0.1d, canAct: true));
        Assert.Equal("Once route complete.", controller.Status);
    }

    private sealed class FakeMagic : IMagicCommands
    {
        public List<uint> CastSpellIds { get; } = [];
        public bool IsCasting => false;
        public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Refused;
        public bool Cast(uint spellId)
        {
            CastSpellIds.Add(spellId);
            return true;
        }
    }

    // ── fd.cs:129-138, the nav-minimum-distance idle-peace override ──────

    private static (NavigationController Controller, FakeAutomation Automation)
        ArrivalOverride(double minimumDistanceMeters, bool idlePeaceMode)
    {
        PluginNavigationPosition point = Position(0d, 0d);
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(point),
            CombatSnapshot = new PluginCombatSnapshot
            {
                Mode = PluginCombatMode.Peace,
            },
            EquipmentItems =
            [
                new PluginEquipmentItem(
                    ObjectId: 800u,
                    Name: "Recovery Wand",
                    ItemType: 0x00008000u,
                    ValidLocations: 0x00100000u,
                    EquippedLocation: 0x00100000u,
                    ContainerObjectId: 0u,
                    WielderObjectId: 1u,
                    CombatUse: 0,
                    DamageType: 0,
                    WeaponSkill: 0,
                    Damage: 0,
                    DamageVariance: 0d),
            ],
        };
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Circular,
            MinimumDistanceMeters = minimumDistanceMeters,
        };
        settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, point));
        settings.Waypoints.Add(
            Waypoint(RouteWaypointType.Point, Position(1d, 0d)));
        var combat = new CombatSettings { IdlePeaceMode = idlePeaceMode };
        var host = new FakeHost(automation);
        var controller = new NavigationController(host, settings);
        controller.BindCombatModeGate(
            new CombatModeGate(host, combat, new VitalSettings(), _ => { }),
            combat);
        return (controller, automation);
    }

    [Fact]
    public void LowWaypointDistanceInPeaceWarnsOnceAndForcesMagicMode()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 0.5d, idlePeaceMode: true);

        Assert.True(controller.Tick(1d, canAct: true));

        // Not advanced: the arrival was refused this tick.
        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.Equal(
            NavigationController.LowWaypointDistanceWarning,
            Assert.Single(automation.PostedSystemMessages)
                .Replace("[MossTank] ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(["EnterMode:Magic"], automation.ModeRequests);

        // The warning is posted once per approach, not once per pass.
        automation.ModeRequests.Clear();
        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Single(automation.PostedSystemMessages);

        controller.ResetOncePerRunWarnings();
        Assert.True(controller.Tick(1d, canAct: true));
        Assert.Equal(2, automation.PostedSystemMessages.Count);
    }

    /// <summary>
    /// fd.cs:131 — the SETTING gates only the warning. With Idle Peace off,
    /// the forced push into Magic mode still happens.
    /// </summary>
    [Fact]
    public void LowWaypointDistanceForcesMagicEvenWithIdlePeaceOff()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 0.5d, idlePeaceMode: false);

        Assert.True(controller.Tick(1d, canAct: true));

        Assert.Empty(automation.PostedSystemMessages);
        Assert.Equal(["EnterMode:Magic"], automation.ModeRequests);
        Assert.Equal(0, controller.CurrentWaypointIndex);
    }

    [Fact]
    public void OrdinaryWaypointDistanceArrivesWithoutTouchingCombatMode()
    {
        (NavigationController controller, FakeAutomation automation) =
            ArrivalOverride(minimumDistanceMeters: 2d, idlePeaceMode: true);

        Assert.True(controller.Tick(1d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Empty(automation.PostedSystemMessages);
        Assert.Empty(automation.ModeRequests);
    }

    private static NavigationController Controller(
        FakeAutomation automation,
        RouteMode mode,
        params RouteWaypoint[] waypoints)
    {
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = mode,
            MinimumDistanceMeters = 2d,
        };
        settings.Waypoints.AddRange(waypoints);
        return new NavigationController(new FakeHost(automation), settings);
    }

    [Fact]
    public void AClientWalkedLegIsAskedForOnceAndHoldsThePassWhileTheWalkGoesOn()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));

        Assert.True(controller.Tick(0.05d, canAct: true));
        (PluginNavigationPosition asked, float arrival) = Assert.Single(automation.GoTos);
        Assert.Equal(Meters(0d, 20d), asked);
        Assert.Equal(2f, arrival);

        automation.WalkIs(PluginGoToState.Walking);
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.Waiting, "waiting: MossTank is running Attack");
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Single(automation.GoTos);
        Assert.Empty(automation.Intents);
        Assert.Equal(0, controller.CurrentWaypointIndex);
        Assert.Contains("walked by the client", controller.Status, StringComparison.Ordinal);
    }

    /// <summary>A walk that ends as near its waypoint as the client can reach, without sight of it, still moves the route on.</summary>
    [Fact]
    public void AClientWalkThatEndsWithoutSightOfItsWaypointStillMovesTheRouteOn()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));
        Assert.True(controller.Tick(0.05d, canAct: true));

        automation.NavigationSnapshot = Snapshot(Meters(0d, 14d));
        automation.WalkIs(PluginGoToState.ArrivedWithoutSight, "no reachable spot can see the goal");
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Empty(automation.PostedSystemMessages);
    }

    [Fact]
    public void AClientWalkThatArrivesMovesTheRouteOnToTheNextLeg()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));
        Assert.True(controller.Tick(0.05d, canAct: true));

        automation.NavigationSnapshot = Snapshot(Meters(0d, 17d));
        automation.WalkIs(PluginGoToState.Arrived, "arrived");
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, controller.CurrentWaypointIndex);

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal([Meters(0d, 20d), Meters(20d, 20d)], automation.GoTos.Select(static walk => walk.Position));
    }

    /// <summary>
    /// A follow route with client pathing on asks the client to follow the target once, within
    /// the follow distance, and holds its turn while the client follows, steering nothing itself.
    /// </summary>
    [Fact]
    public void AFollowRouteWithClientPathingFollowsTheTargetThroughTheClient()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        automation.Objects[7u] = new PluginNavigationObject(7u, "Leader", Meters(0d, 30d));
        NavigationController controller = ClientFollow(automation);

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.Walking, "walking");
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Equal([(7u, 3f)], automation.Follows);
        Assert.Empty(automation.Intents);
        Assert.Contains("Leader", controller.Status);
    }

    /// <summary>A follow the player's keys or portal space ended is asked for again after a moment.</summary>
    [Fact]
    public void AClientFollowThatEndsIsAskedForAgainAfterAMoment()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        automation.Objects[7u] = new PluginNavigationObject(7u, "Leader", Meters(0d, 30d));
        NavigationController controller = ClientFollow(automation);
        controller.Tick(0.05d, canAct: true);

        automation.WalkIs(PluginGoToState.Interrupted, "the player moved the character");
        controller.Tick(0.05d, canAct: true);
        Assert.Single(automation.Follows);

        controller.Tick(1.1d, canAct: true);
        controller.Tick(0.05d, canAct: true);
        Assert.Equal(2, automation.Follows.Count);
    }

    /// <summary>A target the client will not follow, as one that is not a player, is said once and not asked for again.</summary>
    [Fact]
    public void AFollowTargetTheClientWillNotFollowIsSaidOnceAndNotAskedForAgain()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        automation.Objects[7u] = new PluginNavigationObject(7u, "Drudge", Meters(0d, 30d));
        NavigationController controller = ClientFollow(automation);
        controller.Tick(0.05d, canAct: true);

        automation.WalkIs(PluginGoToState.NoRoute, "only players can be followed, and 0x00000007 is not one");
        for (int tick = 0; tick < 5; tick++)
            controller.Tick(1d, canAct: true);

        Assert.Single(automation.Follows);
        Assert.Equal(
            "[MossTank] Drudge cannot be followed (only players can be followed, and 0x00000007 is not one).",
            Assert.Single(automation.PostedSystemMessages));
    }

    [Fact]
    public void TurningNavigationOffStopsTheClientFollow()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        automation.Objects[7u] = new PluginNavigationObject(7u, "Leader", Meters(0d, 30d));
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = RouteMode.Target,
            MinimumDistanceMeters = 3d,
            FollowTargetObjectId = 7u,
            FollowTargetName = "Leader",
            WalkLegsWithClient = true,
        };
        var controller = new NavigationController(new FakeHost(automation), settings);
        controller.Tick(0.05d, canAct: true);
        automation.WalkIs(PluginGoToState.Walking, "walking");

        settings.Enabled = false;
        controller.Tick(0.05d, canAct: true);

        Assert.Equal(1, automation.StopGoToCount);
    }

    private static NavigationController ClientFollow(FakeAutomation automation) =>
        new(
            new FakeHost(automation),
            new NavigationSettings
            {
                Enabled = true,
                Mode = RouteMode.Target,
                MinimumDistanceMeters = 3d,
                FollowTargetObjectId = 7u,
                FollowTargetName = "Leader",
                WalkLegsWithClient = true,
            });

    [Fact]
    public void ALegTheClientCannotWalkIsSkippedAndARouteWithNoWalkableLegStopsAsking()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.NoRoute, "no route joins the character to it");
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(1, controller.CurrentWaypointIndex);
        Assert.Equal(
            "[MossTank] Waypoint 1 could not be walked (no route joins the character to it); moving on to the next.",
            Assert.Single(automation.PostedSystemMessages));

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.Blocked, "stuck");
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(0, controller.CurrentWaypointIndex);

        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Equal(2, automation.GoTos.Count);
        Assert.Contains("No leg of the route could be walked", controller.Status, StringComparison.Ordinal);
        Assert.Equal(
            "[MossTank] No leg of the route could be walked; reset the route to try again.",
            automation.PostedSystemMessages[^1]);
        Assert.Equal(3, automation.PostedSystemMessages.Count);

        controller.Reset();
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(3, automation.GoTos.Count);
    }

    [Fact]
    public void AnUnwalkablePointBesideOneTheCharacterStandsAtIsNotAskedForForever()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 1d), Meters(0d, 45d));

        for (int pass = 0; pass < 12; pass++)
        {
            controller.Tick(0.05d, canAct: true);
            if (automation.GoToReport.State == PluginGoToState.Planning)
                automation.WalkIs(PluginGoToState.NoRoute, "no spot within 10 m of the goal that the start can reach can see it");
        }

        Assert.Equal(2, automation.GoTos.Count);
        Assert.Equal(
            "[MossTank] No leg of the route could be walked; reset the route to try again.",
            automation.PostedSystemMessages[^1]);
        Assert.Contains("No leg of the route could be walked", controller.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void AWalkTheRouteDidNotAskForIsLeftToFinishFirst()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Meters(0d, 0d)),
            GoToReport = new PluginGoToReport(7, PluginGoToState.Walking, 0x50000001u, 12f, 0, "walking"),
        };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));

        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Empty(automation.GoTos);
        Assert.Equal("Waiting for a walk the route did not ask for to end.", controller.Status);

        automation.WalkIs(PluginGoToState.Arrived, "arrived");
        Assert.True(controller.Tick(0.05d, canAct: true));
        Assert.Equal(Meters(0d, 20d), Assert.Single(automation.GoTos).Position);
    }

    [Fact]
    public void TurningNavigationOffStopsTheWalkTheRouteAskedFor()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, NavigationSettings settings) =
            ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));
        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.Walking);

        settings.Enabled = false;
        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.False(controller.Tick(0.05d, canAct: true));

        Assert.Equal(1, automation.StopGoToCount);
    }

    [Fact]
    public void DoorsAreLeftToTheWalksWhenTheClientWalksTheLegs()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        automation.WorldObjects.Add(new PluginNavigationObject(55u, "Door", Meters(0d, 0.1d))
        {
            IsDoor = true,
            IsOpen = false,
            HasLockState = true,
        });
        (NavigationController controller, NavigationSettings settings) =
            ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d), Meters(20d, 20d));
        settings.OpenDoors = true;

        Assert.True(controller.Tick(0.05d, canAct: true));
        automation.WalkIs(PluginGoToState.Walking);
        Assert.True(controller.Tick(0.05d, canAct: true));

        Assert.Empty(automation.UsedObjects);
        Assert.Single(automation.GoTos);
        Assert.DoesNotContain("door", controller.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClientThatCannotWalkLegsLeavesThePassToTheRulesBelow()
    {
        var automation = new FakeAutomation
        {
            NavigationSnapshot = Snapshot(Meters(0d, 0d)),
            GoToAnswer = PluginNavigationCommandStatus.Unavailable,
        };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 20d));

        Assert.False(controller.Tick(0.05d, canAct: true));
        Assert.Contains("cannot walk route legs", controller.Status, StringComparison.Ordinal);
        Assert.Empty(automation.Intents);
    }

    /// <summary>
    /// A route whose walk goes on for 90 seconds of the route's own time without reaching its
    /// waypoint says so in chat once; time the macro spends on other rules does not count.
    /// </summary>
    [Fact]
    public void AClientWalkedRouteThatReachesNoWaypointForNinetySecondsSaysSoOnce()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        (NavigationController controller, _) = ClientLegs(automation, RouteMode.Circular, Meters(0d, 50d), Meters(20d, 50d));
        Assert.True(controller.Tick(1d, canAct: true));
        automation.WalkIs(PluginGoToState.Walking);

        for (int second = 0; second < 89; second++)
            controller.Tick(1d, canAct: true);
        for (int second = 0; second < 30; second++)
            controller.Tick(1d, canAct: false);
        Assert.Empty(automation.PostedSystemMessages);

        for (int second = 0; second < 5; second++)
            controller.Tick(1d, canAct: true);

        Assert.Equal(
            "[MossTank] The route has not reached waypoint 1 in 90 seconds.",
            Assert.Single(automation.PostedSystemMessages));
    }

    /// <summary>
    /// MossTank asks the client's walks to wait while it needs the character, so a walk and
    /// a fight never steer the body at once, and lets go when it is disabled.
    /// </summary>
    [Fact]
    public void MossTankHoldsTheClientsWalksWhileItNeedsTheCharacterUntilItIsDisabled()
    {
        var automation = new FakeAutomation { NavigationSnapshot = Snapshot(Meters(0d, 0d)) };
        var plugin = new MossTankPlugin();
        plugin.Initialize(new FakeHost(automation));

        plugin.Enable();

        Func<string?> pause = Assert.Single(automation.GoToPauses);
        Assert.Null(pause());

        plugin.Disable();

        Assert.Empty(automation.GoToPauses);
    }

    private static (NavigationController Controller, NavigationSettings Settings) ClientLegs(
        FakeAutomation automation,
        RouteMode mode,
        params PluginNavigationPosition[] points)
    {
        var settings = new NavigationSettings
        {
            Enabled = true,
            Mode = mode,
            MinimumDistanceMeters = 2d,
            WalkLegsWithClient = true,
        };
        foreach (PluginNavigationPosition point in points)
            settings.Waypoints.Add(Waypoint(RouteWaypointType.Point, point));
        return (new NavigationController(new FakeHost(automation), settings), settings);
    }

    private static PluginNavigationPosition Meters(double east, double north) =>
        Position(east / 240d, north / 240d);

    private static RouteWaypoint Waypoint(
        RouteWaypointType type,
        PluginNavigationPosition position) => new()
    {
        Type = type,
        Position = position,
    };

    private static PluginNavigationSnapshot Snapshot(
        PluginNavigationPosition position,
        bool airborne = false) => new(
        IsAvailable: true,
        IsPortalSpace: false,
        LocalObjectId: 1u,
        position,
        IsMoving: false,
        IsAirborne: airborne);

    private static PluginNavigationPosition Position(
        double eastWest,
        double northSouth,
        float heading = 0f) => new(
        0x7F7F0001u,
        eastWest,
        northSouth,
        0d,
        heading,
        IsOutdoor: true);

    private sealed class FakeHost(
        FakeAutomation automation,
        IPluginStorage? storage = null) : IPluginHost
    {
        public bool HasUi => false;
        public FakeLogger Logger { get; } = new();
        public IPluginLogger Log => Logger;
        public IGameState State { get; } = new FakeState();
        public IEvents Events { get; } = new FakeEvents();
        public ISelectionService Selection { get; } = new FakeSelection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage { get; } = storage ?? NoOpPluginStorage.Instance;
        public IAutomationSurface Automation { get; } = automation;
        public IPluginStorage VtankProfiles { get; } = storage ?? NoOpPluginStorage.Instance;
        public IPluginLootClassifierRegistry LootClassifiers { get; } = new InertLootClassifierRegistry();
    }

    private sealed class InertLootClassifierRegistry : IPluginLootClassifierRegistry
    {
        public IDisposable Register(string classifierId, string displayName, IPluginLootClassifier classifier) =>
            new Revocation();

        private sealed class Revocation : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeAutomation
        : IAutomationSurface, INavigationAutomation, IPluginChat, IItemAutomation,
          ICombatAutomation, IEquipmentAutomation
    {
        public bool IsAvailable => true;
        public ICombatAutomation Combat => this;
        public IEquipmentAutomation Equipment => this;
        public PluginCombatSnapshot CombatSnapshot { get; set; }
        PluginCombatSnapshot ICombatAutomation.Snapshot => CombatSnapshot;
        public List<string> ModeRequests { get; } = [];
        public List<string> PostedSystemMessages { get; } = [];
        public List<PluginEquipmentItem> EquipmentItems { get; set; } = [];
        bool IEquipmentAutomation.IsAvailable => EquipmentItems.Count > 0;
        bool IEquipmentAutomation.IsBusy => false;
        IReadOnlyList<PluginEquipmentItem> IEquipmentAutomation.CaptureOwnedEquipment() =>
            EquipmentItems;
        PluginEquipmentCommandResult IEquipmentAutomation.Equip(
            uint objectId,
            uint requestedLocation)
        {
            ModeRequests.Add($"Equip:{objectId}");
            return new(PluginEquipmentCommandStatus.Started);
        }

        IReadOnlyList<PluginCombatTarget> ICombatAutomation.CaptureHostileTargets(
            float maximumDistance) => [];
        PluginCombatCommandResult ICombatAutomation.EnterDefaultMode() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.EnterMode(PluginCombatMode mode)
        {
            ModeRequests.Add($"EnterMode:{mode}");
            return new(PluginCombatCommandStatus.Started);
        }

        PluginCombatCommandResult ICombatAutomation.BeginPhysicalAttack(
            uint targetObjectId,
            PluginAttackHeight height,
            float power) => new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.ReleasePhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        PluginCombatCommandResult ICombatAutomation.AbortPhysicalAttack() =>
            new(PluginCombatCommandStatus.Unavailable);
        public ICharacterInfo Character => NoOpAutomationSurface.Instance;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic { get; set; } = NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        public PluginNavigationSnapshot NavigationSnapshot { get; set; }
        PluginNavigationSnapshot INavigationAutomation.Snapshot => NavigationSnapshot;
        public Dictionary<uint, PluginNavigationObject> Objects { get; } = [];
        public List<PluginNavigationObject> WorldObjects { get; } = [];
        public List<PluginMovementIntent> Intents { get; } = [];
        public List<string> SubmittedChat { get; } = [];
        public List<PluginChatMessage> ChatMessages { get; } = [];
        public List<uint> UsedObjects { get; } = [];
        public int ClearCount { get; private set; }
        public List<(PluginNavigationPosition Position, float ArrivalMeters)> GoTos { get; } = [];
        public PluginNavigationCommandStatus GoToAnswer { get; set; } = PluginNavigationCommandStatus.Accepted;
        public PluginGoToReport GoToReport { get; set; }
        public int StopGoToCount { get; private set; }

        public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters)
        {
            GoTos.Add((position, arrivalMeters));
            if (GoToAnswer == PluginNavigationCommandStatus.Accepted)
                GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, 0u, float.NaN, 0, "planning");
            return GoToAnswer;
        }

        public List<(uint PlayerId, float Buffer)> Follows { get; } = [];

        public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters)
        {
            Follows.Add((playerId, bufferMeters));
            GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, playerId, float.NaN, 0, "planning");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopGoTo()
        {
            StopGoToCount++;
            GoToReport = GoToReport with { State = PluginGoToState.Stopped, Reason = "stopped" };
            return PluginNavigationCommandStatus.Accepted;
        }

        public void WalkIs(PluginGoToState state, string reason = "") =>
            GoToReport = GoToReport with { State = state, Reason = reason };

        public List<Func<string?>> GoToPauses { get; } = [];

        public IDisposable PauseGoToWhile(Func<string?> need)
        {
            GoToPauses.Add(need);
            return new Unregister(() => GoToPauses.Remove(need));
        }

        private sealed class Unregister(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
        public PluginItemUseCompletion ItemCompletion { get; set; }
        public PluginItemUseCompletion LastCompletion => ItemCompletion;
        public PluginNavigationObject? FoundObject { get; set; }
        public string? FindName { get; private set; }

        public bool TryGetObject(uint objectId, out PluginNavigationObject value) =>
            Objects.TryGetValue(objectId, out value);

        public bool TryFindObject(
            string name,
            in PluginNavigationPosition near,
            double maximumDistanceMeters,
            out PluginNavigationObject value)
        {
            FindName = name;
            value = FoundObject ?? default;
            return FoundObject.HasValue;
        }

        public IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
            WorldObjects;

        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            Intents.Add(intent);
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearCount++;
            return PluginNavigationCommandStatus.Accepted;
        }

        public List<float> FacedHeadings { get; } = [];

        public PluginNavigationCommandStatus FaceHeading(float headingDegrees)
        {
            FacedHeadings.Add(headingDegrees);
            return PluginNavigationCommandStatus.Accepted;
        }

        public bool Submit(string text)
        {
            SubmittedChat.Add(text);
            return true;
        }

        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
            ChatMessages.Where(message => message.Sequence > afterSequence).ToArray();

        public void PostSystemMessage(string text) =>
            PostedSystemMessages.Add(text);

        public PluginItemCommandResult Use(uint objectId)
        {
            UsedObjects.Add(objectId);
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
        public Dictionary<string, string> Text => _text;
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            _text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => _text.Keys
            .Where(key => prefix.Length == 0
                || key.StartsWith(prefix + "/", StringComparison.Ordinal))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        public void WriteText(string key, string content) => _text[key] = content;
        public bool Delete(string key) => _text.Remove(key);
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeState : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class FakeEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }

        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeSelection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }

        public bool Select(uint objectId) => false;
        public bool Clear() => false;
    }
}
