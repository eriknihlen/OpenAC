using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Entities;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.World;

public sealed class LiveEntitySameGenerationUpdateRouterTests
{
    [Theory]
    [InlineData(0, "parent")]
    [InlineData(1, "position")]
    [InlineData(2, "pickup")]
    [InlineData(3, null)]
    public void Apply_PreservesRetailTailOrderAndExclusiveSpatialBranch(
        int spatialKind,
        string? expectedSpatial)
    {
        var sink = new RecordingSink();
        SameGenerationCreateObjectEvents refresh = new(
            Description: default,
            Appearance: default,
            Parent: spatialKind == 0 ? default(CreateParentUpdate?) : null,
            Position: spatialKind == 1
                ? default(WorldSession.EntityPositionUpdate?)
                : null,
            Pickup: spatialKind == 2 ? default(PickupEvent.Parsed?) : null,
            Movement: default(WorldSession.EntityMotionUpdate?),
            State: default,
            Vector: default);

        // Nullable default(T?) has no value; explicitly wrap the selected
        // zero-valued fixture so the branch itself remains observable.
        refresh = refresh with
        {
            Parent = spatialKind == 0
                ? (CreateParentUpdate?)new CreateParentUpdate()
                : null,
            Position = spatialKind == 1
                ? (WorldSession.EntityPositionUpdate?)new WorldSession.EntityPositionUpdate()
                : null,
            Pickup = spatialKind == 2
                ? (PickupEvent.Parsed?)new PickupEvent.Parsed()
                : null,
            Movement = new WorldSession.EntityMotionUpdate(),
        };

        LiveEntitySameGenerationUpdateRouter.Apply(refresh, sink);

        var expected = new List<string> { "description", "appearance" };
        if (expectedSpatial is not null)
            expected.Add(expectedSpatial);
        expected.AddRange(["movement", "state", "vector"]);
        Assert.Equal(expected, sink.Events);
    }

    [Fact]
    public void ParentTakesPrecedenceWhenMalformedFixtureOffersAllSpatialForms()
    {
        var sink = new RecordingSink();
        var refresh = new SameGenerationCreateObjectEvents(
            default,
            default,
            new CreateParentUpdate(),
            new WorldSession.EntityPositionUpdate(),
            new PickupEvent.Parsed(),
            null,
            default,
            default);

        LiveEntitySameGenerationUpdateRouter.Apply(refresh, sink);

        Assert.Equal(
            ["description", "appearance", "parent", "state", "vector"],
            sink.Events);
    }

    private sealed class RecordingSink : ILiveEntitySameGenerationUpdateSink
    {
        public List<string> Events { get; } = [];
        public void OnDescription(uint ownerGuid, PhysicsSpawnData description) =>
            Events.Add("description");
        public void OnAppearance(ObjDescEvent.Parsed appearance) =>
            Events.Add("appearance");
        public void OnParent(CreateParentUpdate parent) => Events.Add("parent");
        public void OnPosition(WorldSession.EntityPositionUpdate position) =>
            Events.Add("position");
        public void OnPickup(PickupEvent.Parsed pickup) => Events.Add("pickup");
        public void OnMovement(WorldSession.EntityMotionUpdate movement) =>
            Events.Add("movement");
        public void OnState(SetState.Parsed state) => Events.Add("state");
        public void OnVector(VectorUpdate.Parsed vector) => Events.Add("vector");
    }
}
