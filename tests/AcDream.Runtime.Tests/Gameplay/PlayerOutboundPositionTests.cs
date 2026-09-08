using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class PlayerOutboundPositionTests
{
    private static readonly Plane GroundPlane =
        new(Vector3.UnitZ, 0f);

    [Fact]
    public void CrossLandblockWorldFrame_SendsCarriedCellLocalPosition()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());

        controller.SeedPlacementForTest(
            new Vector3(116.07f, 214.56f, 83.76f),
            0xA9B20021u,
            new Vector3(116.07f, 22.56f, 83.76f));

        Assert.True(controller.TryGetOutboundPosition(out Position outbound));
        Assert.Equal(0xA9B20021u, outbound.ObjCellId);
        Assert.Equal(
            new Vector3(116.07f, 22.56f, 83.76f),
            outbound.Frame.Origin);
        Assert.NotEqual(controller.Position, outbound.Frame.Origin);
    }

    [Fact]
    public void UnplacedBody_DoesNotProduceWirePosition()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());

        Assert.False(controller.TryGetOutboundPosition(out _));
    }

    [Fact]
    public void OutboundPosition_PreservesCompleteAuthoritativeQuaternion()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.SeedPlacementForTest(
            new Vector3(116.07f, 214.56f, 83.76f),
            0xA9B20021u,
            new Vector3(116.07f, 22.56f, 83.76f));
        Quaternion complete = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.4f));
        controller.SetBodyOrientation(complete);

        Assert.True(controller.TryGetOutboundPosition(out Position outbound));

        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                complete,
                Quaternion.Normalize(outbound.Frame.Orientation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void PositionEvent_FirstFrameIsDue()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var current = PositionAt(Quaternion.Identity);

        Assert.True(controller.ShouldSendPositionEvent(current, GroundPlane, 0f));
    }

    [Fact]
    public void PositionEvent_StationaryHeadingChangeIsDueAfterRetailInterval()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        var turned = PositionAt(Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f));
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        Assert.False(controller.ShouldSendPositionEvent(
            turned,
            GroundPlane,
            PlayerMovementController.HeartbeatInterval));
        Assert.True(controller.ShouldSendPositionEvent(
            turned,
            GroundPlane,
            PlayerMovementController.HeartbeatInterval + 0.01f));
    }

    [Fact]
    public void PositionEvent_UnchangedCompleteFrameIsNotIdleHeartbeat()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        Assert.False(controller.ShouldSendPositionEvent(
            sent,
            GroundPlane,
            PlayerMovementController.HeartbeatInterval + 0.01f));
    }

    [Fact]
    public void MovementEvent_StampsOnlyTime_NotTheRememberedFrame()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        var turned = PositionAt(Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f));
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);
        controller.NoteMovementSent(nowSeconds: 0.9f);

        Assert.False(controller.ShouldSendPositionEvent(
            turned,
            GroundPlane,
            nowSeconds: 1.1f));
        Assert.True(controller.ShouldSendPositionEvent(
            turned,
            GroundPlane,
            nowSeconds: 1.91f));
    }

    [Fact]
    public void PositionEvent_CellOrContactPlaneChangeIsDueInsideInterval()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        var otherCell = sent with { ObjCellId = 0xA9B20022u };
        var otherPlane = new Plane(Vector3.Normalize(new Vector3(0.01f, 0f, 1f)), 0f);

        Assert.True(controller.ShouldSendPositionEvent(otherCell, GroundPlane, 0.1f));
        Assert.True(controller.ShouldSendPositionEvent(sent, otherPlane, 0.1f));
    }

    [Fact]
    public void PositionEvent_QuaternionSignIsStoredFrameDataLikeRetail()
    {
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        var oppositeStoredQuaternion = PositionAt(new Quaternion(0f, 0f, 0f, -1f));
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        Assert.True(controller.ShouldSendPositionEvent(
            oppositeStoredQuaternion,
            GroundPlane,
            PlayerMovementController.HeartbeatInterval + 0.01f));
    }

    [Fact]
    public void PositionEvent_RetailEpsilonUsesDifferentOriginAndQuaternionBoundaries()
    {
        const float epsilon = 0.000199999995f;
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = new Position(0xA9B20021u, Vector3.Zero, Quaternion.Identity);
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        var exactOriginBoundary = sent with
        {
            Frame = sent.Frame with { Origin = new Vector3(epsilon, 0f, 0f) },
        };
        var exactQuaternionBoundary = sent with
        {
            Frame = sent.Frame with
            {
                Orientation = new Quaternion(epsilon, 0f, 0f, 1f),
            },
        };
        float elapsed = PlayerMovementController.HeartbeatInterval + 0.01f;

        Assert.False(controller.ShouldSendPositionEvent(
            exactOriginBoundary,
            GroundPlane,
            elapsed));
        Assert.True(controller.ShouldSendPositionEvent(
            exactQuaternionBoundary,
            GroundPlane,
            elapsed));
    }

    [Fact]
    public void PositionEvent_RetailPlaneDistanceBoundaryIsStrict()
    {
        const float epsilon = 0.000199999995f;
        var controller = new PlayerMovementController(new PhysicsEngine());
        var sent = PositionAt(Quaternion.Identity);
        controller.NotePositionSent(sent, GroundPlane, nowSeconds: 0f);

        var exactNormalBoundary = new Plane(
            new Vector3(epsilon, 0f, 1f),
            0f);
        var exactDistanceBoundary = new Plane(Vector3.UnitZ, epsilon);

        Assert.False(controller.ShouldSendPositionEvent(
            sent,
            exactNormalBoundary,
            nowSeconds: 0.1f));
        Assert.True(controller.ShouldSendPositionEvent(
            sent,
            exactDistanceBoundary,
            nowSeconds: 0.1f));
    }

    private static Position PositionAt(Quaternion orientation)
        => new(
            0xA9B20021u,
            new Vector3(116.07f, 22.56f, 83.76f),
            orientation);
}
