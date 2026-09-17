using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class ShadowObjectRegistryCaptureTests
{
    private const uint LandblockId = 0xA9B40000u;

    [Fact]
    public void CapturingEveryObjectGivesWhatTheDiagnosticEnumerationGivesInTheSameOrder()
    {
        ShadowObjectRegistry registry = Populated();
        var captured = new List<ShadowEntry>();

        registry.CaptureEntries(captured);

        Assert.Equal(registry.AllEntriesForDebug().ToList(), captured);
        Assert.Equal(4, captured.Count(entry => entry.EntityId == Door));
    }

    [Fact]
    public void AnObjectThatSpansCellsIsCapturedOnce()
    {
        ShadowObjectRegistry registry = Populated();
        Assert.True(registry.GetOwnerCells(Wide).Count > 1);
        var captured = new List<ShadowEntry>();

        registry.CaptureEntries(captured);

        Assert.Single(captured, entry => entry.EntityId == Wide);
    }

    [Fact]
    public void CapturingOneObjectGivesOnlyItsPartsAndNothingForAnUnknownId()
    {
        ShadowObjectRegistry registry = Populated();
        var captured = new List<ShadowEntry>();

        registry.CaptureEntries(Door, captured);
        Assert.Equal(
            registry.AllEntriesForDebug().Where(entry => entry.EntityId == Door).ToList(),
            captured);

        captured.Clear();
        registry.CaptureEntries(0x7FFFFFFFu, captured);
        Assert.Empty(captured);
    }

    [Theory]
    [InlineData(20f, 20f, 3f)]
    [InlineData(60f, 20f, 10f)]
    [InlineData(20f, 20f, 100f)]
    [InlineData(150f, 150f, 1f)]
    public void CapturingNearAPointKeepsThePartsWhoseReachComesWithinTheRadius(float x, float y, float radius)
    {
        ShadowObjectRegistry registry = Populated();
        var centre = new Vector2(x, y);
        var captured = new List<ShadowEntry>();

        registry.CaptureEntriesNear(centre, radius, captured);

        Assert.Equal(
            registry.AllEntriesForDebug()
                .Where(entry => Vector2.Distance(new Vector2(entry.Position.X, entry.Position.Y), centre) <= radius + entry.Radius)
                .ToList(),
            captured);
    }

    [Fact]
    public void CapturesAddToWhatTheListAlreadyHolds()
    {
        ShadowObjectRegistry registry = Populated();
        var captured = new List<ShadowEntry> { default };

        registry.CaptureEntries(Post, captured);

        Assert.Equal(2, captured.Count);
        Assert.Equal(default, captured[0]);
    }

    private const uint Door = 0x000F4244u;
    private const uint Post = 0x000F4245u;
    private const uint Wide = 0x000F4246u;

    private static ShadowObjectRegistry Populated()
    {
        var registry = new ShadowObjectRegistry();
        registry.RegisterMultiPart(
            entityId: Door,
            entityWorldPos: new Vector3(20f, 20f, 0f),
            entityWorldRot: Quaternion.Identity,
            shapes:
            [
                ShadowShape.Cylinder(0u, new Vector3(0f, 0f, 0.1f), Quaternion.Identity, 1f, 0.1f, 0.2f),
                Sphere(new Vector3(0.5f, 0f, 0f)),
                Sphere(new Vector3(-0.5f, 0f, 0f)),
                Sphere(new Vector3(0f, 0.5f, 0f)),
            ],
            state: 0u,
            flags: EntityCollisionFlags.None,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId);
        registry.Register(Post, 0u, new Vector3(60f, 22f, 0f), Quaternion.Identity, 0.3f, 0f, 0f, LandblockId, ShadowCollisionType.Cylinder, cylHeight: 2f);
        registry.Register(Wide, 0u, new Vector3(96f, 96f, 0f), Quaternion.Identity, 30f, 0f, 0f, LandblockId, ShadowCollisionType.Cylinder, cylHeight: 2f);
        return registry;
    }

    private static ShadowShape Sphere(Vector3 local) =>
        ShadowShape.Bsp(
            0x010044B6u,
            local,
            Quaternion.Identity,
            1f,
            ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 0.4f), null));
}
