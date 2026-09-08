using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Physics;

namespace AcDream.Core.Physics.Motion;


public interface IMotionDoneSink
{
    void MotionDone(uint motion, bool success);
}

public sealed class PendingMotion
{
    public uint Motion;
    public uint NumAnims;

    public PendingMotion(uint motion, uint numAnims)
    {
        Motion = motion;
        NumAnims = numAnims;
    }
}

public sealed class MotionTableManager
{
    private const uint CycleClassBit = 0x40000000u;
    private const uint ModifierClassBit = 0x20000000u;
    private const uint ActionClassBit = 0x10000000u;

    private const uint CycleTailBlockMask = 0xb0000000u;

    private const uint StyleTailBlockMask = 0x70000000u;

    public const uint ReadySentinel = 0x41000003u;

    private readonly CMotionTable? _table;
    private readonly MotionState _state;
    private readonly CSequence _sequence;
    private readonly IMotionDoneSink _sink;
    private readonly LinkedList<PendingMotion> _pendingAnimations = new(); // pending_animations
    private int _animationCounter; // animation_counter (@0x20)

    public MotionState State => _state;

    public MotionTableManager(CMotionTable? table, MotionState state, CSequence sequence, IMotionDoneSink sink)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(sink);

        _table = table;
        _state = state;
        _sequence = sequence;
        _sink = sink;
    }

    /// <summary>Read-only inspection surface for tests: the pending queue in
    /// head-to-tail order.</summary>
    public IEnumerable<PendingMotion> PendingAnimations => _pendingAnimations;

    public int AnimationCounter => _animationCounter;

    // ── add_to_queue / remove_redundant_links / truncate_animation_list ────

    public void AddToQueue(uint motion, uint ticks)
    {
        _pendingAnimations.AddLast(new PendingMotion(motion, ticks));
        RemoveRedundantLinks();
    }

    public void RemoveRedundantLinks()
    {
        var tail = _pendingAnimations.Last;
        if (tail is null)
            return;

        // Step 1: skip trailing zero-tick nodes.
        while (tail is not null && tail.Value.NumAnims == 0)
        {
            tail = tail.Previous;
        }
        if (tail is null)
            return;

        uint motion = tail.Value.Motion;

        if ((motion & CycleClassBit) != 0 && (motion & ModifierClassBit) == 0)
        {
            var scan = tail.Previous;
            LinkedListNode<PendingMotion>? matched = null;
            while (scan is not null)
            {
                if (scan.Value.Motion == motion && scan.Value.NumAnims != 0)
                {
                    matched = scan;
                    break;
                }
                if (scan.Value.NumAnims != 0 && (scan.Value.Motion & CycleTailBlockMask) != 0)
                    return; // blocked by an intervening "important" non-zero node
                scan = scan.Previous;
            }
            if (matched is not null)
                TruncateAnimationList(matched);
        }
        else if ((int)motion < 0)
        {
            var scan = tail.Previous;
            LinkedListNode<PendingMotion>? matched = null;
            while (scan is not null)
            {
                if (scan.Value.Motion == motion)
                {
                    matched = scan;
                    break;
                }
                if (scan.Value.NumAnims != 0 && (scan.Value.Motion & StyleTailBlockMask) != 0)
                    return;
                scan = scan.Previous;
            }
            if (matched is not null)
                TruncateAnimationList(matched);
        }
    }

    private void TruncateAnimationList(LinkedListNode<PendingMotion> stopAtExclusive)
    {
        uint removedTicks = 0;
        var node = _pendingAnimations.Last;
        while (!ReferenceEquals(node, stopAtExclusive))
        {
            if (node is null)
                return; // stopAtExclusive wasn't actually in the list -> abort quietly

            removedTicks += node.Value.NumAnims;
            node.Value.NumAnims = 0;
            node = node.Previous;
        }

        _sequence.RemoveLinkAnimations((int)removedTicks);
    }


    public void AnimationDone(bool success)
    {
        var head = _pendingAnimations.First;
        if (head is null)
            return;

        _animationCounter += 1;

        while (head is not null && head.Value.NumAnims <= _animationCounter)
        {
            if ((head.Value.Motion & ActionClassBit) != 0)
                _state.RemoveActionHead();

            _sink.MotionDone(head.Value.Motion, success);
            _animationCounter -= (int)head.Value.NumAnims;

            _pendingAnimations.RemoveFirst();
            head = _pendingAnimations.First;
        }

        if (_animationCounter != 0 && head is null)
            _animationCounter = 0;
    }

    public void CheckForCompletedMotions()
    {
        var head = _pendingAnimations.First;
        if (head is null)
            return;

        while (head is not null && head.Value.NumAnims == 0)
        {
            if ((head.Value.Motion & ActionClassBit) != 0)
                _state.RemoveActionHead();

            _sink.MotionDone(head.Value.Motion, true);

            _pendingAnimations.RemoveFirst();
            head = _pendingAnimations.First;
        }
    }

    public void UseTime() => CheckForCompletedMotions();

    // ── initialize_state / HandleEnterWorld / HandleExitWorld ──────────────

    public void InitializeState()
    {
        uint outTicks = 0;
        if (_table is not null)
        {
            _table.SetDefaultState(_state, _sequence, out outTicks);
        }

        AddToQueue(ReadySentinel, outTicks);
    }

    public void HandleEnterWorld()
    {
        _sequence.RemoveAllLinkAnimations();
        DrainQueue();
    }

    public void HandleExitWorld() => DrainQueue();

    private void DrainQueue()
    {
        while (_pendingAnimations.First is not null)
            AnimationDone(false);
    }

    // ── PerformMovement ──────────────────────────────────────────────────

    public uint PerformMovement(MotionTableMovement movement)
    {
        if (_table is null)
            return MotionTableManagerError.NoTable; // 7

        uint outTicks;

        switch (movement.Type)
        {
            case MovementType.InterpretedCommand:
                if (_table.DoObjectMotion(movement.Motion, _state, _sequence, movement.Speed, out outTicks))
                {
                    AddToQueue(movement.Motion, outTicks);
                    return MotionTableManagerError.Success; // 0
                }
                return MotionTableManagerError.MotionFailed; // 0x43

            case MovementType.StopInterpretedCommand:
                if (_table.StopObjectMotion(movement.Motion, movement.Speed, _state, _sequence, out outTicks))
                {
                    AddToQueue(ReadySentinel, outTicks);
                    return MotionTableManagerError.Success;
                }
                return MotionTableManagerError.MotionFailed;

            case MovementType.StopCompletely:
                _table.StopObjectCompletely(_state, _sequence, out outTicks);
                AddToQueue(ReadySentinel, outTicks); // UNCONDITIONAL — queued regardless of return value.
                return MotionTableManagerError.Success;

            default:
                return MotionTableManagerError.NotHandled;
        }
    }
}

public static class MotionTableManagerError
{
    /// <summary>0 — success.</summary>
    public const uint Success = 0u;
    /// <summary>7 — no motion table loaded.</summary>
    public const uint NoTable = 7u;
    /// <summary>0x43 — DoObjectMotion/StopObjectMotion returned failure.</summary>
    public const uint MotionFailed = 0x43u;
    public const uint NotHandled = 0xFFFFFFFFu;
}

public readonly struct MotionTableMovement
{
    public readonly MovementType Type;
    public readonly uint Motion;
    public readonly float Speed;

    public MotionTableMovement(MovementType type, uint motion, float speed)
    {
        Type = type;
        Motion = motion;
        Speed = speed;
    }

    public static MotionTableMovement Interpreted(uint motion, float speed) =>
        new(MovementType.InterpretedCommand, motion, speed);

    public static MotionTableMovement StopInterpreted(uint motion, float speed) =>
        new(MovementType.StopInterpretedCommand, motion, speed);

    public static MotionTableMovement StopCompletely() =>
        new(MovementType.StopCompletely, 0u, 1f);
}
