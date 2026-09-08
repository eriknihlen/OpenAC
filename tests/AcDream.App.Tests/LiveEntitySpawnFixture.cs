using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests;

internal static class LiveEntitySpawnFixture
{
    internal static WorldSession.EntitySpawn WithConsistentPhysics(
        this WorldSession.EntitySpawn spawn) => spawn with
    {
        Physics = new PhysicsSpawnData(
            RawState: spawn.PhysicsState ?? 0u,
            Position: spawn.Position,
            Movement: null,
            AnimationFrame: spawn.PlacementId,
            SetupTableId: spawn.SetupTableId,
            MotionTableId: spawn.MotionTableId,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: spawn.ParentGuid is { } parentGuid
                && spawn.ParentLocation is { } parentLocation
                ? new PhysicsAttachment(parentGuid, parentLocation)
                : null,
            Children: null,
            Scale: spawn.ObjScale,
            Friction: spawn.Friction,
            Elasticity: spawn.Elasticity,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: new PhysicsTimestamps(
                Position: spawn.PositionSequence,
                Movement: spawn.MovementSequence,
                State: 0,
                Vector: 0,
                Teleport: 0,
                ServerControlledMove: spawn.ServerControlSequence,
                ForcePosition: 0,
                ObjDesc: 0,
                Instance: spawn.InstanceSequence)),
    };
}
