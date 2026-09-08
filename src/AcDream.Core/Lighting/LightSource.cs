using System;
using System.Numerics;

namespace AcDream.Core.Lighting;

public enum LightKind
{
    Directional = 0,  // sun, moon — no position, infinite range
    Point       = 1,  // torch, fireplace, spell aura
    Spot        = 2,
}

public sealed class LightSource
{
    public LightKind Kind;
    public Vector3   WorldPosition;
    private Vector3 _rankingOrigin;

    public Vector3 RankingOrigin
    {
        get => _rankingOrigin;
        set
        {
            _rankingOrigin = value;
            HasRankingOrigin = true;
        }
    }

    /// <summary>True only after the nondirectional ranking root was supplied.</summary>
    public bool HasRankingOrigin { get; private set; }
    public Vector3   WorldForward;    // for Spot/Directional
    public Vector3   ColorLinear = Vector3.One;
    public float     Intensity = 1f;
    public float     Range = 10f;
    public float     ConeAngle = 0f;  // radians, Spot only
    public uint      OwnerId;         // attached entity id; 0 = world-global
    public uint      CellId;
    public bool      IsLit = true;    // SetLightHook latch
    public bool      IsDynamic;
                                      // false = static dat-baked bake (1/d³, range×1.3)
    /// <summary>
    /// True when this light belongs to a live physics object whose root can
    /// move. DAT-static lights keep their hydration-time world frame and are
    /// deliberately excluded from the per-frame pose refresh.
    /// </summary>
    public bool      TracksOwnerPose;

    public Matrix4x4 LocalPose = Matrix4x4.Identity;

    public float DistSq;
}

public readonly record struct CellAmbientState(
    Vector3 AmbientColor,
    Vector3 SunColor,
    Vector3 SunDirection);
