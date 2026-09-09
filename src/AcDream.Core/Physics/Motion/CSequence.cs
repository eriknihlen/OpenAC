using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Core.Physics.Motion;

public interface IAnimHookQueue
{
    /// <summary>Queue a matched AnimFrame hook (already direction-filtered
    /// by <c>execute_hooks</c>).</summary>
    void AddAnimHook(DatReaderWriter.Types.AnimationHook hook);

    void AddAnimDoneHook();
}

public sealed class CSequence
{
    private readonly LinkedList<AnimSequenceNode> _animList = new(); // anim_list (DLList)
    private LinkedListNode<AnimSequenceNode>? _firstCyclic;          // first_cyclic
    private LinkedListNode<AnimSequenceNode>? _currAnim;
    private readonly IAnimationLoader _loader;

    public double FrameNumber;

    /// <summary>Sequence root-motion velocity accumulator (body-local).</summary>
    public Vector3 Velocity;

    /// <summary>Sequence angular-velocity accumulator.</summary>
    public Vector3 Omega;

    /// <summary>Static pose used when no animation node is active
    /// (<c>placement_frame</c>, §16).</summary>
    public AnimationFrame? PlacementFrame { get; private set; }

    public uint PlacementFrameId { get; private set; }

    public CSequence(IAnimationLoader loader) => _loader = loader;

    // ── inspection surface (adapter + tests) ────────────────────────────

    public AnimSequenceNode? CurrAnim => _currAnim?.Value;
    public AnimSequenceNode? FirstCyclic => _firstCyclic?.Value;
    public int Count => _animList.Count;

    public bool HasAnims() => _animList.Count > 0;

    public void SetCurrAnimForTest(int index)
    {
        var n = _animList.First;
        for (int i = 0; i < index && n != null; i++) n = n.Next;
        _currAnim = n;
    }


    public void AppendAnimation(AnimData animData)
    {
        var node = new AnimSequenceNode(animData, _loader);
        if (!node.HasAnim)
            return;

        _animList.AddLast(node);
        _firstCyclic = _animList.Last;

        if (_currAnim is null)
        {
            _currAnim = _animList.First;
            FrameNumber = _currAnim!.Value.GetStartingFrame();
        }
    }


    public void Clear()
    {
        ClearAnimations();
        ClearPhysics();
        PlacementFrame = null;
        PlacementFrameId = 0;
    }

    public void ClearAnimations()
    {
        _animList.Clear();
        _firstCyclic = null;
        _currAnim = null;
        FrameNumber = 0.0;
    }

    public void ClearPhysics()
    {
        Velocity = Vector3.Zero;
        Omega = Vector3.Zero;
    }

    // ── remove family (§6-§8) ───────────────────────────────────────────

    public void RemoveCyclicAnims()
    {
        var node = _firstCyclic;
        while (node is not null)
        {
            var next = node.Next;
            if (ReferenceEquals(_currAnim, node))
            {
                var prev = node.Previous;
                _currAnim = prev;
                FrameNumber = prev is null ? 0.0 : prev.Value.GetEndingFrame();
            }
            _animList.Remove(node);
            node = next;
        }
        _firstCyclic = _animList.Last;
    }

    public void RemoveLinkAnimations(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var prev = _firstCyclic?.Previous;
            if (prev is null)
                break;

            if (ReferenceEquals(_currAnim, prev))
            {
                _currAnim = _firstCyclic;
                if (_firstCyclic is not null)
                    FrameNumber = _firstCyclic.Value.GetStartingFrame();
            }
            _animList.Remove(prev);
        }
    }

    public void RemoveAllLinkAnimations()
    {
        while (_firstCyclic?.Previous is not null)
            RemoveLinkAnimations(1);
    }

    public void Apricot()
    {
        var head = _animList.First;
        if (head is null || ReferenceEquals(head, _currAnim))
            return;

        while (!ReferenceEquals(head, _firstCyclic))
        {
            _animList.Remove(head!);
            head = _animList.First;
            if (head is null || ReferenceEquals(head, _currAnim))
                break;
        }
    }

    // ── physics accumulators (§10-§13) ──────────────────────────────────

    public void SetVelocity(Vector3 v) => Velocity = v;
    public void SetOmega(Vector3 w) => Omega = w;
    public void CombinePhysics(Vector3 v, Vector3 w) { Velocity += v; Omega += w; }
    public void SubtractPhysics(Vector3 v, Vector3 w) { Velocity -= v; Omega -= w; }


    public void MultiplyCyclicAnimationFramerate(float factor)
    {
        for (var n = _firstCyclic; n is not null; n = n.Next)
            n.Value.MultiplyFramerate(factor);
    }

    // ── placement + accessors (§15-§17) ─────────────────────────────────

    public void SetPlacementFrame(AnimationFrame? frame, uint id)
    {
        PlacementFrame = frame;
        PlacementFrameId = id;
    }

    public AnimationFrame? GetCurrAnimframe()
    {
        if (_currAnim is null)
            return PlacementFrame;
        return _currAnim.Value.GetPartFrame((int)Math.Floor(FrameNumber));
    }

    public int GetCurrFrameNumber() => (int)Math.Floor(FrameNumber);


    public void ApplyPhysics(Frame frame, double quantum, double signSource)
    {
        double signed = Math.Abs(quantum);
        if (signSource < 0.0)
            signed = -signed;

        float sq = (float)signed;
        frame.Origin += Velocity * sq;
        FrameOps.Rotate(frame, Omega * sq);
    }


    /// <summary>Host hook queue (<c>hook_obj</c>); null = hooks dropped
    /// (objects without a physics host).</summary>
    public IAnimHookQueue? HookObj;

    public void Update(double timeElapsed, Frame? frame)
    {
        if (_animList.Count > 0 && _currAnim is not null)
        {
            UpdateInternal(timeElapsed, frame);
            Apricot();
        }
        else if (frame is not null)
        {
            ApplyPhysics(frame, timeElapsed, timeElapsed);
        }
    }

    public void UpdateInternal(double timeElapsed, Frame? frame)
    {
        while (true)
        {
            if (_currAnim is null)
                return;

            var currAnim = _currAnim.Value;
            double framerate = currAnim.Framerate;
            double frametime = framerate * timeElapsed;
            int lastFrame = (int)Math.Floor(FrameNumber);

            FrameNumber += frametime;
            double frameTimeElapsed = 0.0;
            bool animDone = false;

            if (frametime > 0.0)
            {
                if (currAnim.HighFrame < Math.Floor(FrameNumber))
                {
                    double frameOffset = FrameNumber - currAnim.HighFrame - 1.0;
                    if (frameOffset < 0.0)
                        frameOffset = 0.0;
                    if (Math.Abs(framerate) > FrameOps.FEpsilon)
                        frameTimeElapsed = frameOffset / framerate;
                    FrameNumber = currAnim.HighFrame;
                    animDone = true;
                }
                while (Math.Floor(FrameNumber) > lastFrame)
                {
                    if (frame is not null)
                    {
                        var pos = currAnim.GetPosFrame(lastFrame);
                        if (pos is not null)
                            FrameOps.Combine(frame, pos);
                        if (Math.Abs(framerate) > FrameOps.FEpsilon)
                            ApplyPhysics(frame, 1.0 / framerate, timeElapsed);
                    }
                    ExecuteHooks(currAnim.GetPartFrame(lastFrame), +1);
                    lastFrame++;
                }
            }
            else if (frametime < 0.0)
            {
                if (currAnim.LowFrame > Math.Floor(FrameNumber))
                {
                    double frameOffset = FrameNumber - currAnim.LowFrame;
                    if (frameOffset > 0.0)
                        frameOffset = 0.0;
                    if (Math.Abs(framerate) > FrameOps.FEpsilon)
                        frameTimeElapsed = frameOffset / framerate;
                    FrameNumber = currAnim.LowFrame;
                    animDone = true;
                }
                while (Math.Floor(FrameNumber) < lastFrame)
                {
                    if (frame is not null)
                    {
                        var pos = currAnim.GetPosFrame(lastFrame);
                        if (pos is not null)
                            FrameOps.Subtract1(frame, pos);
                        if (Math.Abs(framerate) > FrameOps.FEpsilon)
                            ApplyPhysics(frame, 1.0 / framerate, timeElapsed);
                    }
                    ExecuteHooks(currAnim.GetPartFrame(lastFrame), -1);
                    lastFrame--;
                }
            }
            else
            {
                if (frame is not null && Math.Abs(timeElapsed) > FrameOps.FEpsilon)
                    ApplyPhysics(frame, timeElapsed, timeElapsed);
                return;
            }

            if (!animDone)
                return;

            if (HookObj is not null
                && _animList.First is not null
                && !ReferenceEquals(_animList.First, _firstCyclic))
            {
                HookObj.AddAnimDoneHook();
            }

            AdvanceToNextAnimation(timeElapsed, frame);
            timeElapsed = frameTimeElapsed;
        }
    }

    public void AdvanceToNextAnimation(double timeElapsed, Frame? frame)
    {
        if (_currAnim is null)
            return;

        var outgoing = _currAnim.Value;

        if (timeElapsed >= 0.0)
        {
            if (frame is not null && outgoing.Framerate < 0f)
            {
                var pos = outgoing.GetPosFrame((int)FrameNumber);
                if (pos is not null)
                    FrameOps.Subtract1(frame, pos);
                if (Math.Abs(outgoing.Framerate) > FrameOps.FEpsilon)
                    ApplyPhysics(frame, 1.0 / outgoing.Framerate, timeElapsed);
            }

            _currAnim = _currAnim.Next ?? _firstCyclic;
            if (_currAnim is null)
                return;
            FrameNumber = _currAnim.Value.GetStartingFrame();

            var incoming = _currAnim.Value;
            if (frame is not null && incoming.Framerate > 0f)
            {
                var pos = incoming.GetPosFrame((int)FrameNumber);
                if (pos is not null)
                    FrameOps.Combine(frame, pos);
                if (Math.Abs(incoming.Framerate) > FrameOps.FEpsilon)
                    ApplyPhysics(frame, 1.0 / incoming.Framerate, timeElapsed);
            }
        }
        else
        {
            if (frame is not null && outgoing.Framerate >= 0f)
            {
                var pos = outgoing.GetPosFrame((int)FrameNumber);
                if (pos is not null)
                    FrameOps.Subtract1(frame, pos);
                if (Math.Abs(outgoing.Framerate) > FrameOps.FEpsilon)
                    ApplyPhysics(frame, 1.0 / outgoing.Framerate, timeElapsed);
            }

            _currAnim = _currAnim.Previous ?? _animList.Last;
            if (_currAnim is null)
                return;
            FrameNumber = _currAnim.Value.GetEndingFrame();

            var incoming = _currAnim.Value;
            if (frame is not null && incoming.Framerate < 0f)
            {
                var pos = incoming.GetPosFrame((int)FrameNumber);
                if (pos is not null)
                    FrameOps.Combine(frame, pos);
                if (Math.Abs(incoming.Framerate) > FrameOps.FEpsilon)
                    ApplyPhysics(frame, 1.0 / incoming.Framerate, timeElapsed);
            }
        }
    }

    private void ExecuteHooks(AnimationFrame? partFrame, int direction)
    {
        if (partFrame is null || HookObj is null)
            return;

        foreach (var hook in partFrame.Hooks)
        {
            if (hook is null)
                continue;
            int dir = (int)hook.Direction;
            if (dir == 0 || dir == direction)
                HookObj.AddAnimHook(hook);
        }
    }


    internal LinkedListNode<AnimSequenceNode>? CurrAnimNode
    {
        get => _currAnim;
        set => _currAnim = value;
    }

    internal LinkedListNode<AnimSequenceNode>? FirstCyclicNode => _firstCyclic;
    internal LinkedList<AnimSequenceNode> AnimList => _animList;
}
