using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementManagerTests
{
    private sealed class NonCreatureWeenie : IWeenieObject
    {
        public bool InqJumpVelocity(float extent, out float vz) { vz = 0f; return false; }
        public bool InqRunRate(out float rate) { rate = 1f; return false; }
        public bool CanJump(float extent) => false;
        bool IWeenieObject.IsCreature() => false;
    }

    private static (MovementManager Mm, MoveToManagerHarness H, int[] FactoryCalls) MakeFacade()
    {
        var h = new MoveToManagerHarness();
        var factoryCalls = new int[1];
        var mm = new MovementManager(h.Interp)
        {
            MoveToFactory = () => { factoryCalls[0]++; return h.Manager; },
        };
        return (mm, h, factoryCalls);
    }


    [Fact]
    public void MakeMoveToManager_CreatesViaFactory_ExactlyOnce()
    {
        var (mm, h, calls) = MakeFacade();
        Assert.Null(mm.MoveTo);

        mm.MakeMoveToManager();
        Assert.Same(h.Manager, mm.MoveTo);
        Assert.Equal(1, calls[0]);

        mm.MakeMoveToManager();
        Assert.Same(h.Manager, mm.MoveTo);
        Assert.Equal(1, calls[0]);
    }

    [Fact]
    public void MakeMoveToManager_WithoutFactory_IsANoOp()
    {
        var mm = new MovementManager(new MotionInterpreter());
        mm.MakeMoveToManager();
        Assert.Null(mm.MoveTo);
    }


    [Fact]
    public void PerformMovement_InterpTypes_RouteToMinterp_NotMoveTo()
    {
        var (mm, h, calls) = MakeFacade();

        var result = mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.InterpretedCommand,
            Motion = MotionCommand.WalkForward,
            Speed = 1f,
            ModifyInterpretedState = true,
        });

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.WalkForward, h.Interp.InterpretedState.ForwardCommand);
        Assert.Equal(0, calls[0]);
        Assert.Null(mm.MoveTo);
    }

    [Fact]
    public void PerformMovement_InterpTypes_ForwardOriginalParametersObject()
    {
        var (mm, h, calls) = MakeFacade();
        int interrupts = 0;
        h.Interp.InterruptCurrentMovement = () => interrupts++;
        var parameters = new MovementParameters
        {
            Speed = 0.37f,
            SetHoldKey = true,
            HoldKeyToApply = HoldKey.Run,
            CancelMoveTo = false,
            ModifyRawState = true,
            ModifyInterpretedState = true,
        };

        var result = mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.RawCommand,
            Motion = MotionCommand.TurnRight,
            Speed = 9f,
            ModifyRawState = false,
            ModifyInterpretedState = false,
            Params = parameters,
        });

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(0.37f, h.Interp.RawState.TurnSpeed);
        Assert.Equal(HoldKey.Run, h.Interp.RawState.CurrentHoldKey);
        Assert.Equal(0, interrupts);
        Assert.Equal(0, calls[0]);
    }

    [Fact]
    public void PerformMovement_MoveToTypes_LazyCreate_AndRouteToMoveTo()
    {
        var (mm, h, calls) = MakeFacade();

        var result = mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(1, calls[0]);
        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
    }

    [Fact]
    public void PerformMovement_InvalidAndOutOfRangeTypes_Fail0x47()
    {
        var (mm, _, calls) = MakeFacade();

        Assert.Equal(WeenieError.GeneralMovementFailure,
            mm.PerformMovement(new MovementStruct { Type = MovementType.Invalid }));
        Assert.Equal(WeenieError.GeneralMovementFailure,
            mm.PerformMovement(new MovementStruct { Type = (MovementType)10 }));
        Assert.Equal(0, calls[0]);
    }

    [Fact]
    public void PerformMovement_ActivatesBeforeDispatch_EvenWhenTypeIsInvalid()
    {
        var (mm, _, _) = MakeFacade();
        int activations = 0;
        mm.ActivatePhysicsObject = () => activations++;

        Assert.Equal(WeenieError.None,
            mm.PerformMovement(new MovementStruct
            {
                Type = MovementType.StopCompletely,
            }));
        Assert.Equal(WeenieError.GeneralMovementFailure,
            mm.PerformMovement(new MovementStruct
            {
                Type = (MovementType)99,
            }));

        Assert.Equal(2, activations);
    }

    [Fact]
    public void PerformMovement_MoveToType_WithoutFactory_Fails0x47()
    {
        var mm = new MovementManager(new MotionInterpreter());
        Assert.Equal(WeenieError.GeneralMovementFailure,
            mm.PerformMovement(new MovementStruct { Type = MovementType.TurnToHeading }));
    }


    [Fact]
    public void UseTime_BeforeMoveToExists_IsANoOp_AndDoesNotCreate()
    {
        var (mm, _, calls) = MakeFacade();
        mm.UseTime();
        Assert.Equal(0, calls[0]);
        Assert.Null(mm.MoveTo);
    }

    [Fact]
    public void UseTime_RelaysToMoveTo()
    {
        var (mm, h, _) = MakeFacade();
        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;

        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters { DistanceToObject = 0.6f, UseSpheres = false },
        });
        h.DrainPendingMotions();

        h.WorldPosition = new Position(1u, new Vector3(19.7f, 0f, 0f), Quaternion.Identity);
        h.Advance(2.0);
        mm.UseTime();

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
    }


    [Fact]
    public void HitGround_RelaysToMinterp_AndToleratesNullMoveTo()
    {
        var (mm, h, _) = MakeFacade();
        h.Body.State |= PhysicsStateFlags.Gravity;
        bool minterpHit = false;
        h.Interp.RemoveLinkAnimations = () => minterpHit = true;

        mm.HitGround();
        Assert.True(minterpHit);
    }

    [Fact]
    public void HitGround_RelaysMinterpFirst_ThenMoveTo()
    {
        var (mm, h, _) = MakeFacade();
        h.Body.State |= PhysicsStateFlags.Gravity;
        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });
        h.DrainPendingMotions();

        bool? queueEmptyAtMinterpLeg = null;
        h.Interp.RemoveLinkAnimations =
            () => queueEmptyAtMinterpLeg ??= !h.Interp.MotionsPending();

        mm.HitGround();

        Assert.True(queueEmptyAtMinterpLeg);          // minterp leg ran first
        Assert.True(h.Interp.MotionsPending());       // a re-dispatch landed after it
    }

    [Fact]
    public void HitGround_ReachesMoveTo_WhenMinterpLegIsGated()
    {
        var (mm, h, _) = MakeFacade();
        h.Interp.WeenieObj = new NonCreatureWeenie();
        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });
        h.DrainPendingMotions();

        mm.HitGround();

        Assert.True(h.Interp.MotionsPending());
    }


    [Fact]
    public void HandleExitWorld_DrainsMinterp_AndDoesNotTouchMoveTo()
    {
        var (mm, h, _) = MakeFacade();
        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });
        Assert.True(h.Interp.MotionsPending()); // the arm's dispatch is queued

        mm.HandleExitWorld();

        Assert.False(h.Interp.MotionsPending());
        Assert.Equal(MovementType.MoveToPosition, h.Manager.MovementTypeState);
    }


    [Fact]
    public void CancelMoveTo_NullTolerant_AndRelaysToMoveTo()
    {
        var (mm, h, _) = MakeFacade();
        mm.CancelMoveTo(WeenieError.ActionCancelled); // no moveto yet — no throw

        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });
        Assert.True(mm.IsMovingTo());

        mm.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(MovementType.Invalid, h.Manager.MovementTypeState);
        Assert.False(mm.IsMovingTo());
        Assert.True(h.StopCompletelyCalls > 0);
    }

    [Fact]
    public void IsMovingTo_FalseBeforeMoveToExists()
    {
        var (mm, _, _) = MakeFacade();
        Assert.False(mm.IsMovingTo());
    }


    [Fact]
    public void HandleUpdateTarget_NullTolerant_AndFeedsMoveToDeferredStart()
    {
        var (mm, h, _) = MakeFacade();
        var info = new TargetInfo
        {
            ObjectId = 0x5000AAAAu,
            Status = TargetStatus.Ok,
            TargetPosition = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity),
            InterpolatedPosition = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity),
        };
        mm.HandleUpdateTarget(info); // no moveto yet — no throw

        // The V2 "uninitialized type-6 stall": MoveToObject defers its node
        // build to the FIRST HandleUpdateTarget delivery.
        h.ContactValue = true;
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        mm.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToObject,
            ObjectId = 0x5000AAAAu,
            TopLevelId = 0x5000AAAAu,
            Pos = new Position(1u, new Vector3(10f, 0f, 0f), Quaternion.Identity),
            Params = new MovementParameters(),
        });
        Assert.False(h.Manager.Initialized);

        mm.HandleUpdateTarget(info);

        Assert.True(h.Manager.Initialized);
    }
}
