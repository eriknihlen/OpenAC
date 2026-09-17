using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Navigation;

namespace AcDream.Runtime.Tests.Navigation;

public sealed class NavigationChatCommandsTests
{
    [Fact]
    public void GoToACellAndItsLocalPointWalksToThatPlace()
    {
        var harness = new Harness();

        Assert.True(harness.Registry.TryHandle("/nav go to 7E0307A3 310.353577 -312.687317 6.005"));

        (PluginNavigationPosition place, float arrival) = Assert.Single(harness.Navigation.PlaceWalks);
        Assert.Equal(0x7E0307A3u, place.CellId);
        Vector3 local = RuntimeNavigationProjection.LandblockLocal(place);
        Assert.Equal(310.353577f, local.X, 2);
        Assert.Equal(-312.687317f, local.Y, 2);
        Assert.Equal(6.005f, local.Z, 2);
        Assert.Equal(2.5f, arrival);
    }

    [Fact]
    public void GoToTakesAnArrivalDistanceAndAPrefixedCell()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to 0x7E0307A3 310 -312 6 within 4");

        (PluginNavigationPosition place, float arrival) = Assert.Single(harness.Navigation.PlaceWalks);
        Assert.Equal(0x7E0307A3u, place.CellId);
        Assert.Equal(4f, arrival);
    }

    [Theory]
    [InlineData("/nav go to target")]
    [InlineData("/nav go to")]
    [InlineData("/nav go to selected")]
    public void GoToTheTargetWalksToTheSelectedObject(string line)
    {
        var harness = new Harness { Selected = 0x70000123u };

        harness.Registry.TryHandle(line);

        Assert.Equal([(0x70000123u, 2.5f)], harness.Navigation.ObjectWalks);
    }

    [Fact]
    public void GoToTheTargetWithNothingSelectedSaysSoAndWalksNowhere()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to target");

        Assert.Empty(harness.Navigation.ObjectWalks);
        Assert.Contains(harness.Said, line => line.Contains("select something first"));
    }

    [Theory]
    [InlineData("/nav stand on target", 0x70000123u, 2.5f)]
    [InlineData("/nav stand on", 0x70000123u, 2.5f)]
    [InlineData("/nav stand on 0x70000456 within 1", 0x70000456u, 1f)]
    [InlineData("/nav stand on Rock", 0x70000789u, 2.5f)]
    public void StandOnWalksOntoTheObjectAndNeverBesideIt(string line, uint objectId, float arrival)
    {
        var harness = new Harness { Selected = 0x70000123u };
        harness.Navigation.Named["Rock"] = 0x70000789u;
        harness.Registry.TryHandle("/nav debug on");

        harness.Registry.TryHandle(line);

        Assert.Equal([(objectId, arrival)], harness.Navigation.StandOns);
        Assert.Empty(harness.Navigation.ObjectWalks);
        Assert.Contains(harness.Said, said => said.StartsWith("[nav] Stand on ", StringComparison.Ordinal) && said.EndsWith(": planning", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/nav follow Bob 5", 0x70000789u, 5f)]
    [InlineData("/nav follow Bob", 0x70000789u, 3f)]
    [InlineData("/nav follow target", 0x70000123u, 3f)]
    [InlineData("/nav follow", 0x70000123u, 3f)]
    [InlineData("/nav follow 8", 0x70000123u, 8f)]
    [InlineData("/nav follow 0x70000456 within 4", 0x70000456u, 4f)]
    public void FollowTakesAPlayerAndABufferDistance(string line, uint objectId, float buffer)
    {
        var harness = new Harness { Selected = 0x70000123u };
        harness.Navigation.Named["Bob"] = 0x70000789u;
        harness.Registry.TryHandle("/nav debug on");

        harness.Registry.TryHandle(line);

        Assert.Equal([(objectId, buffer)], harness.Navigation.Follows);
        Assert.Contains(harness.Said, said => said.StartsWith("[nav] Follow ", StringComparison.Ordinal) && said.EndsWith(": planning", StringComparison.Ordinal));
    }

    /// <summary>
    /// Walks, routes and moves say nothing in chat about how they start and end unless debug
    /// narration is on; answers to what was typed, such as a problem or a status, always show.
    /// </summary>
    [Fact]
    public void WalksAndMovesAreQuietInChatUnlessDebugIsOn()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to target");
        harness.Registry.TryHandle("/nav status");
        harness.Registry.TryHandle("/motor run forward 5");
        Assert.Equal(2, harness.Said.Count);
        Assert.Contains("select something first", harness.Said[0]);
        Assert.StartsWith("Navigation: ", harness.Said[1]);

        harness.Selected = 0x70000123u;
        harness.Said.Clear();
        harness.Registry.TryHandle("/nav follow target");
        long ours = harness.Navigation.GoToReport.Sequence;
        harness.Navigation.GoToReport = new PluginGoToReport(ours, PluginGoToState.Stopped, 0x70000123u, 1f, 0, "stopped");
        harness.Commands.Tick();
        Assert.Empty(harness.Said);
    }

    [Fact]
    public void GoToAnObjectIdWalksToThatObject()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to 0x70000456 within 3");

        Assert.Equal([(0x70000456u, 3f)], harness.Navigation.ObjectWalks);
    }

    [Fact]
    public void GoToANameWalksToTheNearestObjectCalledThat()
    {
        var harness = new Harness();
        harness.Navigation.Named["Town Network Portal"] = 0x70000789u;

        harness.Registry.TryHandle("/nav go to Town Network Portal");

        Assert.Equal([(0x70000789u, 2.5f)], harness.Navigation.ObjectWalks);
    }

    [Fact]
    public void GoToMapCoordinatesStandsOnTheGroundThere()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to 24.3N 101.1W");

        (PluginNavigationPosition place, _) = Assert.Single(harness.Navigation.PlaceWalks);
        Assert.Equal(0u, place.CellId);
        Assert.Equal(24.3d, place.NorthSouth, 5);
        Assert.Equal(-101.1d, place.EastWest, 5);
        Assert.True(double.IsNaN(place.Elevation));
    }

    /// <summary>The line /loc prints is taken as it is: a cell, a bracketed point, and the heading that follows.</summary>
    [Theory]
    [InlineData("/nav go to 0x7E0307A3 [310.353577 -312.687317 6.005000] 1.000000 0.000000 0.000000 0.000000")]
    [InlineData("/nav go to Your location is: 0x7E0307A3 [310.353577 -312.687317 6.005000] 0.707107 0.000000 0.000000 -0.707107")]
    [InlineData("/nav go to 7E0307A3 [310.353577 -312.687317 6.005000]")]
    public void GoToTakesTheLineLocPrints(string line)
    {
        var harness = new Harness();

        harness.Registry.TryHandle(line);

        (PluginNavigationPosition place, float arrival) = Assert.Single(harness.Navigation.PlaceWalks);
        Assert.Equal(0x7E0307A3u, place.CellId);
        Vector3 local = RuntimeNavigationProjection.LandblockLocal(place);
        Assert.Equal(310.353577f, local.X, 2);
        Assert.Equal(-312.687317f, local.Y, 2);
        Assert.Equal(6.005f, local.Z, 2);
        Assert.Equal(2.5f, arrival);
    }

    /// <summary>A bracketed point alone lies in the cell the character stands in.</summary>
    [Fact]
    public void GoToABracketedPointAloneIsInTheCharactersOwnCell()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/nav go to [68.145149 -60.000000 0.005000] within 3");

        (PluginNavigationPosition place, float arrival) = Assert.Single(harness.Navigation.PlaceWalks);
        Assert.Equal(harness.Navigation.Snapshot.Position.CellId, place.CellId);
        Vector3 local = RuntimeNavigationProjection.LandblockLocal(place);
        Assert.Equal(68.145149f, local.X, 2);
        Assert.Equal(-60f, local.Y, 2);
        Assert.Equal(3f, arrival);
    }

    /// <summary>A route drawn without walking says in chat whether one was found.</summary>
    [Theory]
    [InlineData((int)PluginGoToState.None, "a route was found", "Route to 0x70000456: a route was found")]
    [InlineData((int)PluginGoToState.NoRoute, "no spot can see it", "Route to 0x70000456: no route (no spot can see it)")]
    public void ARoutePreviewSaysHowItEnded(int state, string reason, string expected)
    {
        var harness = new Harness();
        harness.Registry.TryHandle("/nav debug on");
        harness.Registry.TryHandle("/nav route 0x70000456");
        long sequence = harness.Navigation.GoToReport.Sequence;
        harness.Commands.Tick();
        Assert.DoesNotContain(harness.Said, line => line.StartsWith("[nav] Route to", StringComparison.Ordinal));

        harness.Navigation.GoToReport = new PluginGoToReport(sequence, (PluginGoToState)state, 0x70000456u, float.NaN, 0, reason);
        harness.Commands.Tick();
        harness.Commands.Tick();

        Assert.Equal([0x70000456u], harness.Previewed);
        Assert.Single(harness.Said, line => line.StartsWith("[nav] " + expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AWalkSaysOnceHowItEndedAndNotAboutAWalkThatReplacedIt()
    {
        var harness = new Harness { Selected = 0x70000123u };
        harness.Registry.TryHandle("/nav debug on");
        harness.Registry.TryHandle("/nav go to target");
        long ours = harness.Navigation.GoToReport.Sequence;
        harness.Said.Clear();

        harness.Commands.Tick();
        Assert.Empty(harness.Said);

        harness.Navigation.GoToReport = new PluginGoToReport(ours, PluginGoToState.Arrived, 0x70000123u, 1.2f, 0, "arrived");
        harness.Commands.Tick();
        harness.Commands.Tick();

        string said = Assert.Single(harness.Said);
        Assert.Contains("arrived", said);

        harness.Registry.TryHandle("/nav go to target");
        harness.Navigation.GoToReport = new PluginGoToReport(ours + 5, PluginGoToState.Stopped, 0u, float.NaN, 0, "a newer request replaced it");
        harness.Said.Clear();
        harness.Commands.Tick();
        Assert.Empty(harness.Said);
    }

    [Fact]
    public void MovesJoinedWithPlusStartTogetherInTheOrderWritten()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/motor run forward 20s + turn left 90");

        Assert.Equal(
            [
                (PluginMoveDirection.Forward, PluginMovePace.Run, 20f, PluginMoveUnit.Seconds),
                (PluginMoveDirection.TurnLeft, PluginMovePace.Run, 90f, PluginMoveUnit.MetersOrDegrees),
            ],
            harness.Navigation.Moves);
    }

    [Fact]
    public void TwoMovesOnOneChannelAreRefusedAndNothingStarts()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/motor run forward + walk backward 3");

        Assert.Empty(harness.Navigation.Moves);
        Assert.Contains(harness.Said, line => line.Contains("travel channel"));
    }

    [Fact]
    public void AMoveSaysHowItEndedOnceItHas()
    {
        var harness = new Harness();
        harness.Registry.TryHandle("/nav debug on");
        harness.Registry.TryHandle("/motor walk backward 3");
        harness.Said.Clear();
        long sequence = harness.Navigation.MoveReport.Travel.Sequence;

        harness.Navigation.MoveReport = harness.Navigation.MoveReport with
        {
            Travel = new PluginMoveProgress(sequence, PluginMoveState.Blocked, PluginMoveDirection.Backward, PluginMovePace.Walk, 3f, PluginMoveUnit.MetersOrDegrees, 1.5f, 1.6f),
        };
        harness.Commands.Tick();
        harness.Commands.Tick();

        string said = Assert.Single(harness.Said);
        Assert.Contains("blocked", said);
    }

    [Fact]
    public void StopEndsOneChannelOrEveryMove()
    {
        var harness = new Harness();

        harness.Registry.TryHandle("/motor stop turn");
        harness.Registry.TryHandle("/motor stop");

        Assert.Equal([(PluginMoveChannel?)PluginMoveChannel.Turn, null], harness.Navigation.Stops);
    }

    [Fact]
    public void TheGridAndRouteSayThereIsNothingToDrawOnWhereNothingIsDrawn()
    {
        var harness = new Harness(drawn: false) { Selected = 0x70000123u };

        harness.Registry.TryHandle("/nav grid");
        harness.Registry.TryHandle("/nav route target");

        Assert.Equal(2, harness.Said.Count(line => line.Contains("nothing to draw")));
    }

    [Fact]
    public void DebugSendsTheWalksNarrationToChatUntilItIsTurnedOff()
    {
        var harness = new Harness();
        Assert.Null(harness.Listener);

        harness.Registry.TryHandle("/nav debug");
        Assert.NotNull(harness.Listener);
        harness.Listener!("Route: 3 legs, 40.0 m");
        Assert.Contains("[nav] Route: 3 legs, 40.0 m", harness.Said);

        harness.Registry.TryHandle("/nav debug off");
        Assert.Null(harness.Listener);
    }

    [Fact]
    public void DebugWritesTheWalksNarrationToAFileAsWellWhenAskedTo()
    {
        var harness = new Harness();
        string path = Path.Combine(Path.GetTempPath(), $"nav-debug-{Guid.NewGuid():N}", "walks.log");
        try
        {
            harness.Registry.TryHandle($"/nav debug file {path}");
            Assert.NotNull(harness.Listener);
            harness.Listener!("Walk to 0x70000001: arrived");

            string[] lines = File.ReadAllLines(path);
            Assert.EndsWith(" Walk to 0x70000001: arrived", Assert.Single(lines));
            Assert.DoesNotContain(harness.Said, line => line.StartsWith(NavigationChatCommands.DebugPrefix, StringComparison.Ordinal));

            harness.Registry.TryHandle("/nav debug file off");
            Assert.Null(harness.Listener);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void DebugSaysSoWhereWalksAreNotNarrated()
    {
        var harness = new Harness(narrated: false);

        harness.Registry.TryHandle("/nav debug");

        Assert.Contains(harness.Said, line => line.Contains("does not narrate"));
    }

    private sealed class Harness
    {
        public Harness(bool narrated = true, bool drawn = true)
        {
            Commands = new NavigationChatCommands(
                    Navigation,
                    () => Selected,
                    Said.Add,
                    previewRoute: drawn
                        ? objectId =>
                        {
                            Previewed.Add(objectId);
                            Navigation.GoToReport = new PluginGoToReport(
                                Navigation.GoToReport.Sequence + 1, PluginGoToState.Planning, objectId, float.NaN, 0, "planning");
                            return true;
                        }
                        : null,
                    narrate: narrated ? listener => Listener = listener : null)
                .Register(Registry, events: null);
        }

        public List<uint> Previewed { get; } = [];

        public Action<string>? Listener { get; private set; }

        public uint? Selected { get; set; }
        public List<string> Said { get; } = [];
        public PluginCommandRegistry Registry { get; } = new();
        public RecordingNavigation Navigation { get; } = new();
        public NavigationChatCommands Commands { get; }
    }

    private sealed class RecordingNavigation : INavigationAutomation
    {
        public List<(uint ObjectId, float Arrival)> ObjectWalks { get; } = [];
        public List<(PluginNavigationPosition Place, float Arrival)> PlaceWalks { get; } = [];
        public List<(uint ObjectId, float Arrival)> StandOns { get; } = [];
        public List<(uint ObjectId, float Buffer)> Follows { get; } = [];
        public List<(PluginMoveDirection, PluginMovePace, float, PluginMoveUnit)> Moves { get; } = [];
        public List<PluginMoveChannel?> Stops { get; } = [];
        public Dictionary<string, uint> Named { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PluginNavigationSnapshot Snapshot { get; } = new(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: 0x50000001u,
            Position: new PluginNavigationPosition(0xA9B40032u, -101.2d, 24.1d, 0.39d, 0f, true),
            IsMoving: false,
            IsAirborne: false);

        public PluginGoToReport GoToReport { get; set; }
        public PluginMoveReport MoveReport { get; set; }

        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }

        public bool TryFindObject(string name, in PluginNavigationPosition near, double maximumDistanceMeters, out PluginNavigationObject value)
        {
            if (Named.TryGetValue(name, out uint id))
            {
                value = new PluginNavigationObject(id, name, near);
                return true;
            }
            value = default;
            return false;
        }

        public PluginNavigationCommandStatus SetMovementIntent(in PluginMovementIntent intent) => PluginNavigationCommandStatus.Accepted;
        public PluginNavigationCommandStatus ClearMovementIntent() => PluginNavigationCommandStatus.Accepted;

        public PluginNavigationCommandStatus GoTo(uint objectId, float arrivalMeters)
        {
            ObjectWalks.Add((objectId, arrivalMeters));
            GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, objectId, float.NaN, 0, "planning");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus Follow(uint playerId, float bufferMeters)
        {
            Follows.Add((playerId, bufferMeters));
            GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, playerId, float.NaN, 0, "planning");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StandOn(uint objectId, float arrivalMeters)
        {
            StandOns.Add((objectId, arrivalMeters));
            GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, objectId, float.NaN, 0, "planning");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus GoTo(PluginNavigationPosition position, float arrivalMeters)
        {
            PlaceWalks.Add((position, arrivalMeters));
            GoToReport = new PluginGoToReport(GoToReport.Sequence + 1, PluginGoToState.Planning, 0u, float.NaN, 0, "planning");
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus Move(PluginMoveDirection direction, PluginMovePace pace, float amount, PluginMoveUnit unit = PluginMoveUnit.MetersOrDegrees)
        {
            Moves.Add((direction, pace, amount, unit));
            long sequence = Moves.Count;
            var progress = new PluginMoveProgress(sequence, PluginMoveState.Moving, direction, pace, amount, unit, 0f, 0f);
            MoveReport = PluginMoveReport.ChannelOf(direction) switch
            {
                PluginMoveChannel.Travel => MoveReport with { Travel = progress },
                PluginMoveChannel.Strafe => MoveReport with { Strafe = progress },
                _ => MoveReport with { Turn = progress },
            };
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopMoving()
        {
            Stops.Add(null);
            return PluginNavigationCommandStatus.Accepted;
        }

        public PluginNavigationCommandStatus StopMoving(PluginMoveChannel channel)
        {
            Stops.Add(channel);
            return PluginNavigationCommandStatus.Accepted;
        }
    }
}
