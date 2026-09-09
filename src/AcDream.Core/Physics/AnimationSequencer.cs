using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics;

/// <summary>
/// Minimal interface for resolving Animation objects by id.
/// Abstracted so the sequencer can be unit-tested without a real DatCollection.
/// </summary>
public interface IAnimationLoader
{
    /// <summary>Load an Animation by its dat id, or return null.</summary>
    Animation? LoadAnimation(uint id);
}


public readonly struct PartTransform
{
    public readonly Vector3 Origin;
    public readonly Quaternion Orientation;

    public PartTransform(Vector3 origin, Quaternion orientation)
    {
        Origin = origin;
        Orientation = orientation;
    }
}

public sealed class AnimationSequencer
{
    // ── Public state ─────────────────────────────────────────────────────────

    public uint CurrentStyle => _state.Style;

    public uint CurrentMotion => _state.Substate;

    public float CurrentSpeedMod => _state.SubstateMod;

    public Vector3 CurrentVelocity => _core.Velocity;

    public Vector3 CurrentOmega => _core.Omega;

    // Diagnostics
    public int QueueCount => _core.Count;
    public bool HasCurrentNode => _core.CurrAnim != null;

    internal CSequence Core => _core;

    public (int AnimRefHash, bool IsLooping, double Framerate, int StartFrame, int EndFrame, double FramePosition, int QueueCount) CurrentNodeDiag
    {
        get
        {
            var n = _core.CurrAnim;
            if (n is null)
                return (0, false, 0.0, 0, 0, 0.0, _core.Count);
            int hash = n.Anim is null
                ? 0
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(n.Anim);
            bool isLooping = ReferenceEquals(_core.CurrAnim, _core.FirstCyclic);
            return (hash, isLooping, n.Framerate, n.LowFrame, n.HighFrame, _core.FrameNumber, _core.Count);
        }
    }

    public int FirstCyclicAnimRefHash
    {
        get
        {
            var fc = _core.FirstCyclic;
            return fc?.Anim is null
                ? 0
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fc.Anim);
        }
    }

    // ── Private state ────────────────────────────────────────────────────────

    private readonly Setup _setup;
    private readonly MotionTable _mtable;
    private readonly IAnimationLoader _loader;

    private readonly CSequence _core;

    private readonly CMotionTable _table;
    private readonly MotionState _state;
    private readonly MotionTableManager _manager;

    private bool _initialized;

    private readonly List<AnimationHook> _pendingHooks = new();

    private readonly PartTransform[] _partTransformScratch;
    private readonly PartTransformBuffer _partTransformView;



    public AnimationSequencer(Setup setup, MotionTable motionTable, IAnimationLoader loader)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(motionTable);
        ArgumentNullException.ThrowIfNull(loader);

        _setup = setup;
        _mtable = motionTable;
        _loader = loader;
        _partTransformScratch = new PartTransform[_setup.Parts.Count];
        _partTransformView = new PartTransformBuffer(_partTransformScratch);
        _core = new CSequence(loader);
        _core.HookObj = new AdapterHookQueue(this);
        _table = new CMotionTable(motionTable);
        _state = new MotionState();
        _manager = new MotionTableManager(_table, _state, _core, new ForwardingMotionDoneSink(this));

        InitializeSetupDefaultAnimation((uint)setup.DefaultAnimation);
    }

    public MotionTableManager Manager => _manager;

    public Action<uint, bool>? MotionDoneTarget { get; set; }

    private sealed class ForwardingMotionDoneSink : IMotionDoneSink
    {
        private readonly AnimationSequencer _owner;
        public ForwardingMotionDoneSink(AnimationSequencer owner) => _owner = owner;
        public void MotionDone(uint motion, bool success)
            => _owner.MotionDoneTarget?.Invoke(motion, success);
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;
        _initialized = true;
        _manager.InitializeState();
    }

    // ── Public API ───────────────────────────────────────────────────────────


    public void SetCycle(uint style, uint motion, float speedMod = 1f)
    {
        EnsureInitialized();

        uint adjustedMotion = motion;
        float adjustedSpeed = speedMod;
        switch (motion & 0xFFFFu)
        {
            case 0x000E: // TurnLeft → TurnRight (negate speed)
                adjustedMotion = (motion & 0xFFFF0000u) | 0x000Du;
                adjustedSpeed = -speedMod;
                break;
            case 0x0010: // SideStepLeft → SideStepRight (negate speed)
                adjustedMotion = (motion & 0xFFFF0000u) | 0x000Fu;
                adjustedSpeed = -speedMod;
                break;
            case 0x0006: // WalkBackward → WalkForward (negate + BackwardsFactor)
                adjustedMotion = (motion & 0xFFFF0000u) | 0x0005u;
                adjustedSpeed = -speedMod * 0.65f;
                break;
        }

        if (style != 0 && style != _state.Style)
            _manager.PerformMovement(MotionTableMovement.Interpreted(style, 1f));

        uint dispatchResult = PerformMovement(
            MotionTableMovement.Interpreted(adjustedMotion, adjustedSpeed));


        if (dispatchResult != MotionTableManagerError.Success)
            return;

    }

    public void RemoveAllLinkAnimations() => _core.RemoveAllLinkAnimations();

    public void InitializeState() => EnsureInitialized();

    public bool InitializeSetupDefaultAnimation(uint animationId)
    {
        if (animationId == 0)
            return false;

        _core.ClearAnimations();
        _pendingHooks.Clear();
        _core.AppendAnimation(new AnimData
        {
            AnimId = (QualifiedDataId<Animation>)animationId,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 30f,
        });
        return _core.CurrAnim is not null;
    }

    public uint PerformMovement(MotionTableMovement movement)
    {
        EnsureInitialized();
        return _manager.PerformMovement(movement);
    }


    public IReadOnlyList<PartTransform> Advance(float dt)
        => Advance(dt, rootMotionFrame: null);

    public IReadOnlyList<PartTransform> Advance(float dt, Frame? rootMotionFrame)
    {
        int partCount = _setup.Parts.Count;

        if (_core.CurrAnim == null && rootMotionFrame is null)
            return BuildIdentityFrame(partCount);
        if (dt <= 0f)
            return SampleCurrentPose();

        _core.Update(dt, rootMotionFrame);

        return _core.CurrAnim == null
            ? BuildIdentityFrame(partCount)
            : BuildBlendedFrame();
    }

    public IReadOnlyList<PartTransform> SampleCurrentPose()
        => _core.CurrAnim == null
            ? BuildIdentityFrame(_setup.Parts.Count)
            : BuildBlendedFrame();

    public IReadOnlyList<AnimationHook> PendingHooks => _pendingHooks;

    public IReadOnlyList<AnimationHook> ConsumePendingHooks()
    {
        if (_pendingHooks.Count == 0)
            return Array.Empty<AnimationHook>();

        var result = _pendingHooks.ToArray();
        _pendingHooks.Clear();
        return result;
    }


    public void PlayAction(uint motionCommand, float speedMod = 1f)
    {
        EnsureInitialized();
        _manager.PerformMovement(MotionTableMovement.Interpreted(motionCommand, speedMod));
    }

    public void Reset()
    {
        _manager.HandleExitWorld();
        _core.Clear();
        _pendingHooks.Clear();
        _state.Style = 0;
        _state.Substate = 0;
        _state.SubstateMod = 1f;
        _state.ClearModifiers();
        _state.ClearActions();
        _initialized = false;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private sealed class AdapterHookQueue : IAnimHookQueue
    {
        private readonly AnimationSequencer _owner;
        public AdapterHookQueue(AnimationSequencer owner) => _owner = owner;

        public void AddAnimHook(AnimationHook hook) => _owner._pendingHooks.Add(hook);

        public void AddAnimDoneHook() => _owner._pendingHooks.Add(AnimationDoneSentinel);
    }

    private static readonly AnimationDoneHook AnimationDoneSentinel =
        new() { Direction = AnimationHookDir.Both };



    private IReadOnlyList<PartTransform> BuildBlendedFrame()
    {
        int partCount = _setup.Parts.Count;

        var curr = _core.CurrAnim;
        if (curr is null || curr.Anim is null)
            return BuildIdentityFrame(partCount);

        int numPartFrames = curr.Anim.PartFrames.Count;

        int rangeLo = Math.Min(curr.LowFrame, curr.HighFrame);
        int rangeHi = Math.Max(curr.LowFrame, curr.HighFrame);
        rangeHi = Math.Min(rangeHi, numPartFrames - 1);
        rangeLo = Math.Max(rangeLo, 0);

        int frameIdx = (int)Math.Floor(_core.FrameNumber);
        frameIdx = Math.Clamp(frameIdx, rangeLo, rangeHi);

        int nextIdx;
        if (curr.Framerate >= 0f)
        {
            nextIdx = frameIdx + 1;
            if (nextIdx > rangeHi || nextIdx >= numPartFrames)
                nextIdx = frameIdx;
        }
        else
        {
            nextIdx = frameIdx - 1;
            if (nextIdx < rangeLo)
                nextIdx = frameIdx;
        }

        // Fractional blend weight (always in [0, 1]).
        double rawT = _core.FrameNumber - Math.Floor(_core.FrameNumber);
        float t = (float)Math.Clamp(rawT, 0.0, 1.0);

        var f0Parts = curr.Anim.PartFrames[frameIdx].Frames;
        var f1Parts = curr.Anim.PartFrames[nextIdx].Frames;

        int authoredPartCount = Math.Min(partCount, f0Parts.Count);
        var result = _partTransformScratch;
        for (int i = 0; i < authoredPartCount; i++)
        {
            var p0 = f0Parts[i];
            var p1 = i < f1Parts.Count ? f1Parts[i] : p0;

            result[i] = new PartTransform(
                Vector3.Lerp(p0.Origin, p1.Origin, t),
                SlerpRetailClient(p0.Orientation, p1.Orientation, t));
        }

        _partTransformView.SetCount(authoredPartCount);
        return _partTransformView;
    }

    private IReadOnlyList<PartTransform> BuildIdentityFrame(int partCount)
    {
        // MP-Alloc: same reusable buffer as BuildBlendedFrame (see
        // _partTransformScratch) — overwritten in place, never reallocated.
        var result = _partTransformScratch;
        for (int i = 0; i < partCount; i++)
            result[i] = new PartTransform(Vector3.Zero, Quaternion.Identity);
        _partTransformView.SetCount(partCount);
        return _partTransformView;
    }

    private sealed class PartTransformBuffer : IReadOnlyList<PartTransform>
    {
        private readonly PartTransform[] _items;

        public PartTransformBuffer(PartTransform[] items) => _items = items;

        public int Count { get; private set; }
        public PartTransform this[int index] => index >= 0 && index < Count
            ? _items[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

        public void SetCount(int count) => Count = count;

        public IEnumerator<PartTransform> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return _items[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }


    public static Quaternion SlerpRetailClient(Quaternion q1, Quaternion q2, float t)
    {
        float dot = q1.W * q2.W + q1.X * q2.X + q1.Y * q2.Y + q1.Z * q2.Z;

        Quaternion q2s;
        if (dot < 0f)
        {
            dot = -dot;
            q2s = new Quaternion(-q2.X, -q2.Y, -q2.Z, -q2.W);
        }
        else
        {
            q2s = q2;
        }

        const float SlerpEpsilon = 1e-4f;
        float w1, w2;

        if (1f - dot <= SlerpEpsilon)
        {
            w1 = 1f - t;
            w2 = t;
        }
        else
        {
            float omega    = MathF.Acos(dot);
            float sinOmega = MathF.Sin(omega);
            float invSin   = 1f / sinOmega;

            float candidate1 = MathF.Sin((1f - t) * omega) * invSin;
            float candidate2 = MathF.Sin(t * omega) * invSin;

            if (candidate1 >= 0f && candidate1 <= 1f
                && candidate2 >= 0f && candidate2 <= 1f)
            {
                w1 = candidate1;
                w2 = candidate2;
            }
            else
            {
                w1 = 1f - t;
                w2 = t;
            }
        }

        return new Quaternion(
            w1 * q1.X + w2 * q2s.X,
            w1 * q1.Y + w2 * q2s.Y,
            w1 * q1.Z + w2 * q2s.Z,
            w1 * q1.W + w2 * q2s.W);
    }
}
