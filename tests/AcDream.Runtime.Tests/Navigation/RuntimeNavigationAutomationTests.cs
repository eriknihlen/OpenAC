using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Navigation;

namespace AcDream.Runtime.Tests.Navigation;

public sealed class RuntimeNavigationAutomationTests
{
    [Fact]
    public void MoveEnumsMatchTheRuntimeByName()
    {
        Assert.Equal(Enum.GetNames<RuntimeMoveDirection>(), Enum.GetNames<PluginMoveDirection>());
        Assert.Equal(Enum.GetNames<RuntimeMovePace>(), Enum.GetNames<PluginMovePace>());
        Assert.Equal(Enum.GetNames<RuntimeMoveUnit>(), Enum.GetNames<PluginMoveUnit>());
        Assert.Equal(Enum.GetNames<RuntimeMoveChannel>(), Enum.GetNames<PluginMoveChannel>());
        Assert.Equal(Enum.GetNames<RuntimeScriptedMoveState>(), Enum.GetNames<PluginMoveState>());
    }

    [Fact]
    public void EachDirectionOccupiesTheSameChannelForPluginsAsInTheRuntime()
    {
        foreach (PluginMoveDirection direction in Enum.GetValues<PluginMoveDirection>())
        {
            RuntimeMoveRequest request = RuntimeNavigationProjection.MoveRequest(
                direction, PluginMovePace.Run, 1f, PluginMoveUnit.MetersOrDegrees)!.Value;
            Assert.Equal(
                RuntimeNavigationProjection.Channel(PluginMoveReport.ChannelOf(direction)),
                request.Channel);
        }
    }

    [Theory]
    [InlineData(PluginMoveDirection.Forward, 10f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.TurnLeft, 720f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.StrafeRight, 0f, PluginMoveUnit.MetersOrDegrees, true)]
    [InlineData(PluginMoveDirection.Forward, 300f, PluginMoveUnit.Seconds, true)]
    [InlineData(PluginMoveDirection.StrafeRight, -1f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Backward, 501f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Forward, 301f, PluginMoveUnit.Seconds, false)]
    [InlineData((PluginMoveDirection)99, 1f, PluginMoveUnit.MetersOrDegrees, false)]
    [InlineData(PluginMoveDirection.Forward, 1f, (PluginMoveUnit)9, false)]
    public void AMoveIsProjectedOnlyWhenTheRuntimeCanCarryItOut(
        PluginMoveDirection direction,
        float amount,
        PluginMoveUnit unit,
        bool valid)
    {
        RuntimeMoveRequest? request =
            RuntimeNavigationProjection.MoveRequest(direction, PluginMovePace.Walk, amount, unit);

        Assert.Equal(valid, request is not null);
        if (request is { } projected)
        {
            Assert.Equal(RuntimeMovePace.Walk, projected.Pace);
            Assert.Equal(unit == PluginMoveUnit.Seconds ? RuntimeMoveUnit.Seconds : RuntimeMoveUnit.MetersOrDegrees, projected.Unit);
            Assert.Equal(amount, projected.Amount);
        }
    }

    [Fact]
    public void TheMoveReportCarriesEachChannelAndTheJump()
    {
        var snapshot = new RuntimeScriptedMoveSnapshot(
            new RuntimeMoveChannelSnapshot(
                7,
                RuntimeScriptedMoveState.Moving,
                new RuntimeMoveRequest(RuntimeMoveDirection.Backward, RuntimeMovePace.Walk, 20f, RuntimeMoveUnit.Seconds),
                12.5f,
                4f),
            new RuntimeMoveChannelSnapshot(
                8,
                RuntimeScriptedMoveState.Blocked,
                new RuntimeMoveRequest(RuntimeMoveDirection.StrafeLeft, RuntimeMovePace.Run, 4f),
                1.5f,
                2f),
            new RuntimeMoveChannelSnapshot(
                9,
                RuntimeScriptedMoveState.Completed,
                new RuntimeMoveRequest(RuntimeMoveDirection.TurnRight, RuntimeMovePace.Run, 45f),
                45f,
                0.5f),
            3,
            true);

        Assert.Equal(
            new PluginMoveReport(
                new PluginMoveProgress(7, PluginMoveState.Moving, PluginMoveDirection.Backward, PluginMovePace.Walk, 20f, PluginMoveUnit.Seconds, 12.5f, 4f),
                new PluginMoveProgress(8, PluginMoveState.Blocked, PluginMoveDirection.StrafeLeft, PluginMovePace.Run, 4f, PluginMoveUnit.MetersOrDegrees, 1.5f, 2f),
                new PluginMoveProgress(9, PluginMoveState.Completed, PluginMoveDirection.TurnRight, PluginMovePace.Run, 45f, PluginMoveUnit.MetersOrDegrees, 45f, 0.5f),
                3,
                true),
            RuntimeNavigationProjection.MoveReport(snapshot));
    }

    [Theory]
    [InlineData(0xA9B40032u, 144.86f, 40.19f, 94f)]
    [InlineData(0x00190162u, 90f, -1010f, 6.005f)]
    [InlineData(0x7E0307A3u, 310.353577f, -312.687317f, 6.005f)]
    public void APlacePluginsSeeInMapCoordinatesIsPlacedBackInItsLandblocksFrame(uint cell, float x, float y, float z)
    {
        var local = new Vector3(x, y, z);
        PluginNavigationPosition seen = RuntimeNavigationProjection.Position(
            new Position(cell, new CellFrame(local, Quaternion.Identity)));

        Vector3 placed = RuntimeNavigationProjection.LandblockLocal(seen);

        Assert.Equal(local.X, placed.X, 2);
        Assert.Equal(local.Y, placed.Y, 2);
        Assert.Equal(local.Z, placed.Z, 2);
    }

    /// <summary>
    /// A door reads open or closed as the world shows it: a door's Open property comes with an
    /// appraisal and does not follow the door opening and closing afterwards, while an open door
    /// stops colliding.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ADoorReadsOpenWhenTheWorldShowsItOpenWhateverItsAppraisalSaid(bool passable, bool appraisedOpen, bool open)
    {
        var door = new ClientObject { ObjectId = 0x7A000001u, PublicWeenieBitfield = (uint)PublicWeenieFlags.Door };
        door.Properties.Bools[(uint)PropertyBool.Open] = appraisedOpen;
        PhysicsStateFlags state = passable ? PhysicsStateFlags.Ethereal : PhysicsStateFlags.None;

        PluginNavigationObject seen = RuntimeNavigationProjection.Enrich(
            new PluginNavigationObject(door.ObjectId, "Door", default),
            door,
            state);

        Assert.True(seen.IsDoor);
        Assert.Equal(open, seen.IsOpen);
    }

    /// <summary>A door the client has appraised knows its lock, even when the appraisal named no lock at all.</summary>
    [Fact]
    public void AnAppraisedDoorKnowsItsLockStateEvenWithNoLockProperty()
    {
        var door = new ClientObject { ObjectId = 0x7A000001u, PublicWeenieBitfield = (uint)PublicWeenieFlags.Door };
        var nothingYet = RuntimeNavigationProjection.Enrich(new PluginNavigationObject(door.ObjectId, "Door", default), door, PhysicsStateFlags.None);

        door.LastAppraisalTimeMs = 1;
        var appraised = RuntimeNavigationProjection.Enrich(new PluginNavigationObject(door.ObjectId, "Door", default), door, PhysicsStateFlags.None);

        Assert.False(nothingYet.HasLockState);
        Assert.True(appraised.HasLockState);
        Assert.False(appraised.IsLocked);
    }

    [Fact]
    public void WalksWaitOnWhatAPluginSaysItNeedsUntilThePluginLetsGo()
    {
        var navigation = new RuntimeNavigationAutomation();
        var walk = new NavigationWalkController(new PhysicsEngine(), new NoWalkBody(), new NoWalkGoals());
        navigation.BindWalk(walk);
        string? need = "MossTank is running Attack";
        IDisposable broken = navigation.PauseGoToWhile(() => throw new InvalidOperationException("a broken plugin"));
        IDisposable pause = navigation.PauseGoToWhile(() => need);

        Assert.Equal("MossTank is running Attack", walk.PausedBy?.Invoke());
        need = null;
        Assert.Null(walk.PausedBy?.Invoke());
        need = "MossTank is buffing";
        pause.Dispose();
        pause.Dispose();
        Assert.Null(navigation.PauseReason());
        broken.Dispose();
    }

    [Theory]
    [InlineData((int)NavigationWalkState.Arrived, PluginGoToState.Arrived)]
    [InlineData((int)NavigationWalkState.ArrivedWithoutSight, PluginGoToState.ArrivedWithoutSight)]
    [InlineData((int)NavigationWalkState.Planned, PluginGoToState.None)]
    public void HowAWalkEndedReachesPluginsAsTheMatchingState(int state, PluginGoToState expected)
    {
        PluginGoToReport report = RuntimeNavigationProjection.GoToReport(
            new NavigationWalkReport(9, (NavigationWalkState)state, 0x50000001u, 3f, 0, "ended"));

        Assert.Equal(expected, report.State);
    }

    [Fact]
    public void AWalkWaitingOnSomethingElseIsReportedToPluginsAsWaiting()
    {
        PluginGoToReport report = RuntimeNavigationProjection.GoToReport(new NavigationWalkReport(
            4,
            NavigationWalkState.Waiting,
            0x50000001u,
            float.NaN,
            0,
            "waiting: MossTank is running Attack",
            BlockedByObjectId: 0x70000002u));

        Assert.Equal(PluginGoToState.Waiting, report.State);
        Assert.Equal("waiting: MossTank is running Attack", report.Reason);
        Assert.Equal(0x70000002u, report.BlockedByObjectId);
    }

    [Fact]
    public void EveryMemberIsUnavailableUntilARuntimeIsBound()
    {
        var navigation = new RuntimeNavigationAutomation();
        navigation.BindWalk(new NavigationWalkController(new PhysicsEngine(), new NoWalkBody(), new NoWalkGoals()));

        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.FaceHeading(90f));
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.Move(PluginMoveDirection.Forward, PluginMovePace.Run, 5f));
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.GoTo(0x50000001u, 2.5f));
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.StandOn(0x50000001u, 2.5f));
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.Follow(0x50000001u, 3f));
        Assert.Equal(PluginNavigationCommandStatus.Unavailable, navigation.StopGoTo());
        Assert.False(navigation.Snapshot.IsAvailable);
        Assert.Equal(default, navigation.GoToReport);
    }

    private sealed class NoWalkBody : INavigationWalkBody
    {
        public bool TrySample(out NavigationWalkBodySample sample)
        {
            sample = default;
            return false;
        }

        public bool BeginMove(in RuntimeMoveRequest request) => false;

        public bool StopMove(RuntimeMoveChannel channel) => false;
    }

    private sealed class NoWalkGoals : INavigationGoalSource
    {
        public bool TryLocate(uint objectId, out Vector3 position)
        {
            position = default;
            return false;
        }
    }
}
