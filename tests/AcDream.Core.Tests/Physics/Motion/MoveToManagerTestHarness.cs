using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.Core.Tests.Physics.Motion;

internal sealed class MoveToManagerHarness
{
    public readonly MotionInterpreter Interp = new();
    public readonly PhysicsBody Body = new();

    public Position WorldPosition = new(1u, Vector3.Zero, Quaternion.Identity);

    public float Heading;

    public readonly List<(float Heading, bool Send)> SetHeadingCalls = new();

    public float OwnRadius = 0.5f;
    public float OwnHeight = 2.0f;

    public bool ContactValue = true;
    public bool IsInterpolatingValue;
    public Vector3 Velocity = Vector3.Zero;

    public uint SelfId = 0x50000001u;

    public int StopCompletelyCalls;

    public readonly List<(uint ContextId, uint ObjectId, float Radius, double Quantum)> SetTargetCalls = new();
    public int ClearTargetCalls;
    public double TargetQuantum;
    public readonly List<double> SetTargetQuantumCalls = new();

    public int UnstickCalls;
    public readonly List<(uint Tlid, float Radius, float Height)> StickToCalls = new();
    public readonly List<WeenieError> MoveToCompleteCalls = new();

    public double CurTime;
    public const double TickSeconds = 1.0 / 30.0;

    public readonly MoveToManager Manager;

    public MoveToManagerHarness()
    {
        Interp.PhysicsObj = Body;
        Body.TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable | TransientStateFlags.Active;

        Manager = new MoveToManager(
            Interp,
            stopCompletely: () => StopCompletelyCalls++,
            getPosition: () => WorldPosition,
            getHeading: () => Heading,
            setHeading: (h, send) => { SetHeadingCalls.Add((h, send)); Heading = h; },
            getOwnRadius: () => OwnRadius,
            getOwnHeight: () => OwnHeight,
            contact: () => ContactValue,
            isInterpolating: () => IsInterpolatingValue,
            getVelocity: () => Velocity,
            getSelfId: () => SelfId,
            setTarget: (ctx, obj, radius, quantum) => SetTargetCalls.Add((ctx, obj, radius, quantum)),
            clearTarget: () => ClearTargetCalls++,
            getTargetQuantum: () => TargetQuantum,
            setTargetQuantum: q => { TargetQuantum = q; SetTargetQuantumCalls.Add(q); },
            curTime: () => CurTime);

        Manager.StickTo = (tlid, radius, height) => StickToCalls.Add((tlid, radius, height));
        Manager.MoveToComplete = err => MoveToCompleteCalls.Add(err);
        Manager.Unstick = () => UnstickCalls++;
    }

    public void Tick() => CurTime += TickSeconds;

    public void Advance(double seconds) => CurTime += seconds;

    public void DrainPendingMotions()
    {
        while (Interp.MotionsPending())
            Interp.MotionDone(0, true);
    }

    public uint ForwardCommand => Interp.InterpretedState.ForwardCommand;

    public uint TurnCommand => Interp.InterpretedState.TurnCommand;

    public float ForwardSpeed => Interp.InterpretedState.ForwardSpeed;
}
