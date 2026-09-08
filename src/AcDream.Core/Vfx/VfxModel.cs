using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Vfx;

public enum ParticleType
{
    Unknown = 0,
    Still = 1,
    LocalVelocity = 2,
    ParabolicLVGA = 3,
    ParabolicLVGAGR = 4,
    Swarm = 5,
    Explode = 6,
    Implode = 7,
    ParabolicLVLA = 8,
    ParabolicLVLALR = 9,
    ParabolicGVGA = 10,
    ParabolicGVGAGR = 11,
    GlobalVelocity = 12,
    NumParticleType = 13,
}

public enum ParticleEmitterKind
{
    Unknown = 0,
    BirthratePerSec = 1,
    BirthratePerMeter = 2,
}

/// <summary>
/// Render stage for an active particle emitter.
/// </summary>
public enum ParticleRenderPass
{
    Scene = 0,
    SkyPreScene = 1,
    SkyPostScene = 2,
}

public enum UnattachedEmitterCellScope
{
    Any = 0,
    OutdoorCells = 1,
    InteriorCells = 2,
}

public enum ParticleVisibilityPolicy
{
    World = 0,
    Examination = 1,
    PassOwned = 2,
}

[Flags]
public enum EmitterFlags : uint
{
    None = 0,
    Additive = 0x01,
    Billboard = 0x02,
    FaceCamera = 0x04,
    AttachLocal = 0x08,
}

public sealed class EmitterDesc
{
    public float MaxDegradeDistance { get; init; } = 100f;
    public uint DatId { get; init; }
    public ParticleType Type { get; init; }
    public ParticleEmitterKind EmitterKind { get; init; } = ParticleEmitterKind.BirthratePerSec;
    public EmitterFlags Flags { get; init; }
    public uint TextureSurfaceId { get; init; }
    public uint GfxObjId { get; init; }
    public uint HwGfxObjId { get; init; }
    public uint SoundOnSpawn { get; init; }

    // Emission behavior.
    public float Birthrate { get; init; }
    public float EmitRate { get; init; }
    public int MaxParticles { get; init; }
    public int InitialParticles { get; init; }
    public int TotalParticles { get; init; }
    public float LifetimeMin { get; init; }
    public float LifetimeMax { get; init; }
    public float Lifespan { get; init; }
    public float LifespanRand { get; init; }
    public float StartDelay { get; init; }
    public float TotalDuration { get; init; }

    // Spawn geometry.
    public Vector3 OffsetDir { get; init; } = new(0, 0, 1);
    public float MinOffset { get; init; }
    public float MaxOffset { get; init; }
    public float SpawnDiskRadius { get; init; }

    public Vector3 InitialVelocity { get; init; }
    public float VelocityJitter { get; init; }
    public Vector3 Gravity { get; init; } = new(0, 0, -9.8f);
    public Vector3 A { get; init; }
    public float MinA { get; init; } = 1f;
    public float MaxA { get; init; } = 1f;
    public Vector3 B { get; init; }
    public float MinB { get; init; } = 1f;
    public float MaxB { get; init; } = 1f;
    public Vector3 C { get; init; }
    public float MinC { get; init; } = 1f;
    public float MaxC { get; init; } = 1f;

    // Appearance over lifetime.
    public uint StartColorArgb { get; init; } = 0xFFFFFFFF;
    public uint EndColorArgb { get; init; } = 0xFFFFFFFF;
    public float StartAlpha { get; init; } = 1f;
    public float EndAlpha { get; init; } = 0f;
    public float StartSize { get; init; } = 0.5f;
    public float EndSize { get; init; } = 0.5f;
    public float ScaleRand { get; init; }
    public float TransRand { get; init; }
    public float StartRotation { get; init; }
    public float EndRotation { get; init; }
}

public sealed class PhysicsScript
{
    public uint ScriptId { get; init; }
    public IReadOnlyList<PhysicsScriptHook> Hooks { get; init; } = Array.Empty<PhysicsScriptHook>();
}

public sealed record PhysicsScriptHook(
    float StartTime,
    PhysicsScriptHookType Type,
    uint RefDataId,
    int PartIndex,
    Vector3 Offset,
    bool IsParentLocal);

public enum PhysicsScriptHookType
{
    CreateParticle = 18,
    DestroyParticle = 19,
    PlaySound = 1,
    AnimationDone = 2,
}

/// <summary>
/// Individual runtime particle. Owned by the <c>ParticleSystem</c>.
/// </summary>
public struct Particle
{
    public Vector3 EmissionOrigin;
    public Quaternion SpawnRotation;
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Offset;
    public Vector3 A;
    public Vector3 B;
    public Vector3 C;
    public float SpawnedAt;
    public float FrozenTimeAtSpawn;
    public float Lifetime;
    public float Age;
    public float StartSize;
    public float EndSize;
    public float StartAlpha;
    public float EndAlpha;
    public uint ColorArgb;
    public float Size;
    public float Rotation;
    public bool Alive;
}

/// <summary>
/// One active emitter instance. The <c>ParticleSystem</c> holds a pool
/// of these; each one maintains its own particle array.
/// </summary>
public sealed class ParticleEmitter
{
    public int Handle { get; init; }
    public EmitterDesc Desc { get; init; } = null!;
    public Vector3 AnchorPos { get; set; }
    public Vector3 OwnerPosition { get; set; }
    public uint OwnerCellId { get; set; }
    public Quaternion AnchorRot { get; set; } = Quaternion.Identity;
    public uint AttachedObjectId { get; set; }
    public ParticleVisibilityPolicy VisibilityPolicy { get; set; }
    public bool PresentationVisible { get; set; } = true;
    public bool SimulationEnabled { get; set; } = true;
    public bool ViewEligible { get; set; } = true;
    public bool DegradedOut { get; set; }
    public int AttachedPartIndex { get; set; } = -1;
    public Particle[] Particles { get; init; } = null!;
    public ParticleRenderPass RenderPass { get; init; }
    public int ActiveCount;
    public float EmittedAccumulator;
    public float StartedAt;
    public float LastEmitTime;
    /// <summary>
    /// Total time this infinite emitter spent degraded. Particles subtract
    /// the delta since their own birth rather than being rewritten one by one.
    /// </summary>
    public float FrozenTime;
    public Vector3 LastEmitOffset;
    public int TotalEmitted;
    public bool Finished;
}

/// <summary>
/// Top-level particle orchestrator. App-layer renderer batches these.
/// </summary>
public interface IParticleSystem
{
    /// <summary>Spawn an emitter attached to a world position or entity.</summary>
    int SpawnEmitter(
        EmitterDesc desc,
        Vector3 anchor,
        Quaternion? rot = null,
        uint attachedObjectId = 0,
        int attachedPartIndex = -1,
        ParticleRenderPass renderPass = ParticleRenderPass.Scene,
        ParticleVisibilityPolicy visibilityPolicy = ParticleVisibilityPolicy.World);

    /// <summary>Fire a full PhysicsScript at a target.</summary>
    void PlayScript(uint scriptId, uint targetObjectId, float modifier = 1f);

    /// <summary>Advance all active emitters by dt seconds.</summary>
    void Tick(float dt);

    /// <summary>Stop an emitter early.</summary>
    void StopEmitter(int handle, bool fadeOut);

    int ActiveParticleCount { get; }
    int ActiveEmitterCount { get; }
}
