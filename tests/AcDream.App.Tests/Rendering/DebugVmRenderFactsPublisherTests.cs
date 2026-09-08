using System.Numerics;
using System.Reflection;
using AcDream.App.Rendering;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Rendering;

public sealed class DebugVmRenderFactsPublisherTests
{
    [Fact]
    public void Defaults_MatchThePriorGameWindowCache()
    {
        var publisher = new DebugVmRenderFactsPublisher();

        Assert.Equal(DebugVmRenderFacts.Initial, publisher.DebugVmFacts);
        Assert.Equal(float.PositiveInfinity, publisher.DebugVmFacts.NearestObjectDistance);
        Assert.Equal("-", publisher.DebugVmFacts.NearestObjectLabel);
        Assert.False(publisher.DebugVmFacts.Colliding);
    }

    [Fact]
    public void InactiveConsumer_DoesNotEnumerateOrReplacePriorFacts()
    {
        var publisher = new DebugVmRenderFactsPublisher();
        publisher.PublishDebugVmFacts(
            true,
            3,
            8,
            Vector3.Zero,
            [Entry(1, new Vector3(5, 0, 100), radius: 1)]);
        DebugVmRenderFacts prior = publisher.DebugVmFacts;
        var shadows = new CountingEnumerable([Entry(2, Vector3.One, 1)]);

        publisher.PublishDebugVmFacts(
            false,
            99,
            100,
            new Vector3(90, 90, 90),
            shadows);

        Assert.Equal(0, shadows.EnumerationCount);
        Assert.Equal(prior, publisher.DebugVmFacts);
    }

    [Fact]
    public void Publish_UsesHorizontalDistanceAndSubtractsBothRadii()
    {
        var publisher = new DebugVmRenderFactsPublisher();
        var origin = new Vector3(10, 20, -500);

        publisher.PublishDebugVmFacts(
            true,
            7,
            11,
            origin,
            [
                Entry(0xA, new Vector3(16, 28, 500), radius: 1.52f),
                Entry(0xB, new Vector3(30, 20, -500), radius: 2f),
            ]);

        DebugVmRenderFacts facts = publisher.DebugVmFacts;
        Assert.Equal(7, facts.VisibleLandblocks);
        Assert.Equal(11, facts.TotalLandblocks);
        Assert.Equal(8.0f, facts.NearestObjectDistance, precision: 5);
        Assert.Equal("0x0000000A Cylinder", facts.NearestObjectLabel);
        Assert.False(facts.Colliding);
    }

    [Fact]
    public void Publish_ClampsOverlapToZeroAndUsesUnclampedDistanceForCollision()
    {
        var publisher = new DebugVmRenderFactsPublisher();

        publisher.PublishDebugVmFacts(
            true,
            1,
            2,
            Vector3.Zero,
            [Entry(0x1234, new Vector3(1, 0, 200), radius: 0.6f)]);

        Assert.Equal(0f, publisher.DebugVmFacts.NearestObjectDistance);
        Assert.True(publisher.DebugVmFacts.Colliding);
        Assert.Equal("0x00001234 Cylinder", publisher.DebugVmFacts.NearestObjectLabel);
    }

    [Fact]
    public void Publish_DistanceAboveContactThresholdIsClearAndTieKeepsFirstEntry()
    {
        var publisher = new DebugVmRenderFactsPublisher();
        float centerDistance = 1f + DebugVmRenderFactsPublisher.PlayerCollisionRadius
            + DebugVmRenderFactsPublisher.ContactThreshold + 0.001f;

        publisher.PublishDebugVmFacts(
            true,
            0,
            0,
            Vector3.Zero,
            [
                Entry(0x11, new Vector3(centerDistance, 0, 0), 1f),
                Entry(0x22, new Vector3(0, centerDistance, 0), 1f),
            ]);

        Assert.True(publisher.DebugVmFacts.NearestObjectDistance
            > DebugVmRenderFactsPublisher.ContactThreshold);
        Assert.False(publisher.DebugVmFacts.Colliding);
        Assert.Equal("0x00000011 Cylinder", publisher.DebugVmFacts.NearestObjectLabel);
    }

    [Fact]
    public void Publish_EmptySequenceRestoresTheEmptyNearestFacts()
    {
        var publisher = new DebugVmRenderFactsPublisher();
        publisher.PublishDebugVmFacts(
            true,
            1,
            1,
            Vector3.Zero,
            [Entry(1, Vector3.Zero, 1)]);

        publisher.PublishDebugVmFacts(true, 4, 9, Vector3.Zero, []);

        Assert.Equal(4, publisher.DebugVmFacts.VisibleLandblocks);
        Assert.Equal(9, publisher.DebugVmFacts.TotalLandblocks);
        Assert.Equal(float.PositiveInfinity, publisher.DebugVmFacts.NearestObjectDistance);
        Assert.Equal("-", publisher.DebugVmFacts.NearestObjectLabel);
        Assert.False(publisher.DebugVmFacts.Colliding);
    }

    [Fact]
    public void Publisher_RetainsNoRuntimeOwnerDelegateOrBorrowedSequence()
    {
        FieldInfo[] fields = typeof(DebugVmRenderFactsPublisher).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Single(fields);
        Assert.Equal(typeof(DebugVmRenderFacts), fields[0].FieldType);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(GameWindow));
        Assert.DoesNotContain(fields, field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(fields, field =>
            typeof(IEnumerable<ShadowEntry>).IsAssignableFrom(field.FieldType));
        Assert.True(typeof(IDebugVmRenderFactsSource).IsAssignableFrom(
            typeof(DebugVmRenderFactsPublisher)));
    }

    private static ShadowEntry Entry(uint id, Vector3 position, float radius) => new(
        EntityId: id,
        GfxObjId: 0,
        Position: position,
        Rotation: Quaternion.Identity,
        Radius: radius,
        CollisionType: ShadowCollisionType.Cylinder);

    private sealed class CountingEnumerable(IReadOnlyList<ShadowEntry> entries)
        : IEnumerable<ShadowEntry>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<ShadowEntry> GetEnumerator()
        {
            EnumerationCount++;
            return entries.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
