using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeRemoteSteepContactSlideTests
{
    private const float SteepGradient = 1.30f;

    private const float WalkableGradient = 0.10f;

    [Fact]
    public void SteepTerrainProducesANonWalkableContactPlane()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(SteepGradient);

        Assert.True(harness.Remote.Body.ContactPlaneValid);
        Assert.InRange(
            harness.Remote.Body.ContactPlane.Normal.Z,
            0.55f,
            PhysicsGlobals.FloorZ - 0.001f);
    }

    [Fact]
    public void SteepContactDoesNotLatchALanding()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(SteepGradient);
        harness.Remote.Airborne = true;
        int groundEdges = 0;
        harness.Remote.Motion.RemoveLinkAnimations = () => groundEdges++;

        harness.Tick(40);

        Assert.True(harness.Remote.Body.InContact);
        Assert.False(harness.Remote.Body.OnWalkable);
        Assert.Equal(0, groundEdges);
    }

    [Fact]
    public void GravityPersistsAcrossTicksOnASteepContact()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(SteepGradient);
        harness.Remote.Airborne = true;

        harness.Tick(40);

        Assert.True(harness.Remote.Body.HasGravity);
        Assert.True(harness.Remote.Body.Acceleration.Z < -1f);
    }

    [Fact]
    public void SteepContactKeepsTheBodySlidingDownhill()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(SteepGradient);
        Vector3 start = harness.Remote.Body.Position;

        harness.Tick(40);

        Vector3 travelled = harness.Remote.Body.Position - start;
        Assert.True(
            travelled.Length() > 0.25f,
            $"expected a slide, body moved {travelled.Length():F4} m");
        Assert.True(
            travelled.Z < -0.1f,
            $"expected downhill travel, dz = {travelled.Z:F4} m");
    }

    [Fact]
    public void TheTickNeverAssertsContactOrWalkableWithoutASweep()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(WalkableGradient);
        harness.Remote.CellId = 0u;
        harness.Remote.Body.TransientState &= ~(TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable);
        harness.Remote.Airborne = false;

        harness.Tick(1);

        Assert.False(harness.Remote.Body.InContact);
        Assert.False(harness.Remote.Body.OnWalkable);
    }

    [Fact]
    public void AGroundedTickOnASteepFaceReleasesTheBodyInsteadOfPinningIt()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(SteepGradient);
        harness.Remote.Body.TransientState |=
            TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
        harness.Remote.Airborne = false;
        harness.Remote.Body.Velocity =
            Vector3.Normalize(new Vector3(0f, 1f, -SteepGradient)) * 3f;
        Vector3 start = harness.Remote.Body.Position;

        harness.Tick(40);

        Assert.False(harness.Remote.Body.OnWalkable);
        float travelled = (harness.Remote.Body.Position - start).Length();
        Assert.True(
            travelled > 2f,
            $"expected the steep face to release the body, travelled {travelled:F3} m");
    }

    [Fact]
    public void AuthoritativeVelocityIsNotDiscardedOnAGroundedTick()
    {
        using RemoteRampHarness harness = RemoteRampHarness.OnRamp(WalkableGradient);
        Assert.False(harness.Remote.Airborne);
        harness.Remote.Body.Velocity = new Vector3(2.146f, 2.264f, -3.549f);

        harness.Tick(1);

        Assert.NotEqual(Vector3.Zero, harness.Remote.Body.Velocity);
        Assert.True(
            harness.Remote.Body.Velocity.X > 0.5f,
            $"velocity X was {harness.Remote.Body.Velocity.X:F4}");
    }

    [Fact]
    public void CommittedTransientsAgreeWithTheCommittedContactPlane()
    {
        using RemoteRampHarness steep = RemoteRampHarness.OnRamp(SteepGradient);
        steep.Tick(20);
        AssertTransientsAreContactPlaneDerived(
            steep.Remote.Body, expectWalkable: false);

        using RemoteRampHarness gentle = RemoteRampHarness.OnRamp(WalkableGradient);
        gentle.Tick(20);
        AssertTransientsAreContactPlaneDerived(
            gentle.Remote.Body, expectWalkable: true);
    }

    private static void AssertTransientsAreContactPlaneDerived(
        PhysicsBody body,
        bool expectWalkable)
    {
        Assert.True(
            !body.OnWalkable || body.InContact,
            "OnWalkable without Contact — the two transients were asserted "
            + "independently of the contact plane");

        Assert.True(
            !body.InContact || body.ContactPlaneValid,
            "Contact without a valid contact plane — Contact was not "
            + "plane-derived");

        Assert.Equal(
            expectWalkable,
            body.ContactPlane.Normal.Z >= PhysicsGlobals.FloorZ);
        Assert.Equal(expectWalkable, body.OnWalkable);
    }

    [Fact]
    public void WalkableLandingStillLandsAndFiresTheGroundEdgeOnce()
    {
        using RemoteRampHarness harness = RemoteRampHarness.Airborne(WalkableGradient, height: 3f);
        int groundEdges = 0;
        harness.Remote.Motion.RemoveLinkAnimations = () => groundEdges++;

        harness.Tick(60);

        Assert.True(harness.Remote.Body.OnWalkable);
        Assert.False(harness.Remote.Airborne);
        Assert.Equal(1, groundEdges);
    }

    [Fact]
    public void WalkableLandingDoesNotClearTheGravityStateBit()
    {
        using RemoteRampHarness harness = RemoteRampHarness.Airborne(WalkableGradient, height: 3f);

        harness.Tick(60);

        Assert.True(harness.Remote.Body.OnWalkable);
        Assert.True(harness.Remote.Body.HasGravity);
    }
}
