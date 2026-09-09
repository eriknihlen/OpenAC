using System.Collections.Generic;

namespace AcDream.Core.Physics.Motion;

public sealed class MotionEntry
{
    public uint Motion;
    public float SpeedMod = 1f;

    public MotionEntry(uint motion, float speedMod)
    {
        Motion = motion;
        SpeedMod = speedMod;
    }
}

public sealed class MotionState
{
    public uint Style;

    public uint Substate;

    /// <summary>Speed modifier of the base substate (default 1.0).</summary>
    public float SubstateMod = 1f;

    private readonly LinkedList<MotionEntry> _modifiers = new(); // modifier_head — push-front stack
    private readonly LinkedList<MotionEntry> _actions = new();   // action_head/tail — FIFO

    public MotionState()
    {
    }

    public MotionState(MotionState other)
    {
        Style = other.Style;
        Substate = other.Substate;
        SubstateMod = other.SubstateMod;
        foreach (var m in other._modifiers)
            _modifiers.AddLast(new MotionEntry(m.Motion, m.SpeedMod));
        foreach (var a in other._actions)
            _actions.AddLast(new MotionEntry(a.Motion, a.SpeedMod));
    }

    public IEnumerable<MotionEntry> Modifiers => _modifiers;

    public IEnumerable<MotionEntry> Actions => _actions;

    public void AddModifierNoCheck(uint motion, float speedMod)
        => _modifiers.AddFirst(new MotionEntry(motion, speedMod));

    public bool AddModifier(uint motion, float speedMod)
    {
        for (var n = _modifiers.First; n is not null; n = n.Next)
            if (n.Value.Motion == motion)
                return false;

        if (Substate == motion)
            return false;

        AddModifierNoCheck(motion, speedMod);
        return true;
    }

    public void RemoveModifier(MotionEntry entry)
    {
        _modifiers.Remove(entry);
    }

    public void ClearModifiers() => _modifiers.Clear();

    public void AddAction(uint motion, float speedMod)
        => _actions.AddLast(new MotionEntry(motion, speedMod));

    public uint RemoveActionHead()
    {
        var head = _actions.First;
        if (head is null)
            return 0;
        _actions.RemoveFirst();
        return head.Value.Motion;
    }

    public void ClearActions() => _actions.Clear();
}
