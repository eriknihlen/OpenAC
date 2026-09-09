using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementStructWideningTests
{
    [Fact]
    public void ObjectId_TopLevelId_RoundTrip()
    {
        var mvs = new MovementStruct
        {
            Type = MovementType.MoveToObject,
            ObjectId = 0x50001234u,
            TopLevelId = 0x50005678u,
        };

        Assert.Equal(0x50001234u, mvs.ObjectId);
        Assert.Equal(0x50005678u, mvs.TopLevelId);
    }

    [Fact]
    public void Pos_RoundTrips_WorldPositionAndCell()
    {
        var pos = new Position(0x12340001u, new Vector3(10f, 20f, 3f), Quaternion.Identity);
        var mvs = new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = pos,
        };

        Assert.Equal(pos, mvs.Pos);
        Assert.Equal(0x12340001u, mvs.Pos.ObjCellId);
        Assert.Equal(new Vector3(10f, 20f, 3f), mvs.Pos.Frame.Origin);
    }

    [Fact]
    public void Radius_Height_RoundTrip()
    {
        var mvs = new MovementStruct
        {
            Type = MovementType.MoveToObject,
            Radius = 0.75f,
            Height = 1.8f,
        };

        Assert.Equal(0.75f, mvs.Radius);
        Assert.Equal(1.8f, mvs.Height);
    }

    [Fact]
    public void Params_HoldsMovementParametersReference()
    {
        var p = new MovementParameters { CanCharge = true };
        var mvs = new MovementStruct
        {
            Type = MovementType.TurnToHeading,
            Params = p,
        };

        Assert.Same(p, mvs.Params);
    }

    [Fact]
    public void ExistingFields_Type_Motion_StillPresent_NoRegression()
    {
        var mvs = new MovementStruct
        {
            Type = MovementType.RawCommand,
            Motion = 0x45000005u,
            Speed = 1.5f,
            Autonomous = true,
            ModifyInterpretedState = true,
            ModifyRawState = false,
        };

        Assert.Equal(MovementType.RawCommand, mvs.Type);
        Assert.Equal(0x45000005u, mvs.Motion);
        Assert.Equal(1.5f, mvs.Speed);
        Assert.True(mvs.Autonomous);
        Assert.True(mvs.ModifyInterpretedState);
        Assert.False(mvs.ModifyRawState);
    }
}
