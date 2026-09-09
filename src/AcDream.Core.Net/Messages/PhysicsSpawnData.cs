using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Net.Messages;

public readonly record struct PhysicsTimestamps(
    ushort Position,
    ushort Movement,
    ushort State,
    ushort Vector,
    ushort Teleport,
    ushort ServerControlledMove,
    ushort ForcePosition,
    ushort ObjDesc,
    ushort Instance);

public readonly record struct PhysicsAttachment(uint Guid, uint LocationId);

public readonly record struct PhysicsMovementData(
    ReadOnlyMemory<byte> RawData,
    CreateObject.ServerMotionState? MotionState,
    bool? IsAutonomous);

public readonly record struct PhysicsSpawnData(
    uint RawState,
    CreateObject.ServerPosition? Position,
    PhysicsMovementData? Movement,
    uint? AnimationFrame,
    uint? SetupTableId,
    uint? MotionTableId,
    uint? SoundTableId,
    uint? PhysicsScriptTableId,
    PhysicsAttachment? Parent,
    ReadOnlyMemory<PhysicsAttachment>? Children,
    float? Scale,
    float? Friction,
    float? Elasticity,
    float? Translucency,
    Vector3? Velocity,
    Vector3? Acceleration,
    Vector3? AngularVelocity,
    uint? DefaultScriptType,
    float? DefaultScriptIntensity,
    PhysicsTimestamps Timestamps)
{
    public PhysicsStateFlags State => (PhysicsStateFlags)RawState;
}
