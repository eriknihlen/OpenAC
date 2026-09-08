using System;
using System.Linq;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Physics.Motion;


public sealed class CMotionTable
{
    private readonly MotionTable _table;

    public CMotionTable(MotionTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }


    internal static bool SameSign(float a, float b) => (a >= 0f) == (b >= 0f);

    internal static void ChangeCycleSpeed(CSequence sequence, MotionData? cyclic, float oldSpeed, float newSpeed)
    {
        const float Epsilon = 0.000199999995f;

        if (MathF.Abs(oldSpeed) > Epsilon)
        {
            sequence.MultiplyCyclicAnimationFramerate(newSpeed / oldSpeed);
            return;
        }

        if (MathF.Abs(newSpeed) <= Epsilon)
        {
            sequence.MultiplyCyclicAnimationFramerate(0f);
        }
    }

    internal static void AddMotion(CSequence sequence, MotionData? motion, float speedMod)
    {
        if (motion is null)
            return;

        sequence.SetVelocity(motion.Velocity * speedMod);
        sequence.SetOmega(motion.Omega * speedMod);

        foreach (var ad in motion.Anims)
        {
            sequence.AppendAnimation(new AnimData
            {
                AnimId = ad.AnimId,
                LowFrame = ad.LowFrame,
                HighFrame = ad.HighFrame,
                Framerate = ad.Framerate * speedMod,
            });
        }
    }

    internal static void CombineMotion(CSequence sequence, MotionData? motion, float speedMod)
    {
        if (motion is null)
            return;

        sequence.CombinePhysics(motion.Velocity * speedMod, motion.Omega * speedMod);
    }

    internal static void SubtractMotion(CSequence sequence, MotionData? motion, float speedMod)
    {
        if (motion is null)
            return;

        sequence.SubtractPhysics(motion.Velocity * speedMod, motion.Omega * speedMod);
    }

    // ── members ──────────────────────────────────────────────────────────

    public bool IsAllowed(uint candidateSubstate, MotionData? candidate, MotionState state)
    {
        if (candidate is null)
            return false;

        if ((candidate.Bitfield & 2) != 0)
        {
            uint substate = state.Substate;
            if (candidateSubstate != substate)
            {
                uint defaultSubstate = LookupStyleDefault(state.Style);
                return defaultSubstate == substate;
            }
        }

        return true;
    }

    public MotionData? GetLink(uint fromStyle, uint fromSubstate, float fromSubstateMod, uint toSubstate, float toSubstateMod)
    {
        if (toSubstateMod < 0f || fromSubstateMod < 0f)
        {
            // Reversed-direction path: link FROM toSubstate TO fromSubstate.
            int reversedKey = (int)((fromStyle << 16) | (toSubstate & 0xFFFFFFu));
            if (_table.Links.TryGetValue(reversedKey, out var revLink)
                && revLink.MotionData.TryGetValue((int)fromSubstate, out var revResult))
            {
                return revResult;
            }

            uint defaultSubstate = LookupStyleDefault(fromStyle);
            int subKey = (int)((fromStyle << 16) | (fromSubstate & 0xFFFFFFu));
            if (_table.Links.TryGetValue(subKey, out var subLink)
                && subLink.MotionData.TryGetValue((int)defaultSubstate, out var subResult))
            {
                return subResult;
            }
            return null;
        }

        // Forward-direction path: link FROM fromSubstate TO toSubstate.
        int outerKey1 = (int)((fromStyle << 16) | (fromSubstate & 0xFFFFFFu));
        if (_table.Links.TryGetValue(outerKey1, out var cmd1)
            && cmd1.MotionData.TryGetValue((int)toSubstate, out var result1))
        {
            return result1;
        }

        int outerKey2 = (int)(fromStyle << 16);
        if (_table.Links.TryGetValue(outerKey2, out var cmd2)
            && cmd2.MotionData.TryGetValue((int)toSubstate, out var result2))
        {
            return result2;
        }

        return null;
    }

    public bool GetObjectSequence(uint motion, MotionState state, CSequence sequence, float speed, out uint outTicks, bool stopCall)
    {
        outTicks = 0;

        uint style = state.Style;
        if (style == 0)
            return false;

        uint substate = state.Substate;
        if (substate == 0)
            return false;

        uint styleDefault = LookupStyleDefault(style); // var_c

        uint target = motion;

        if (target == styleDefault && !stopCall && (substate & 0x20000000) != 0)
            return true;

        float requestedSpeed = speed; // ebp_1

        if ((int)target < 0)
        {
            if (style == target)
                return true; // already in that style

            uint currentStyleDefault = LookupStyleDefault(style); // eax_1

            MotionData? exitLink = null; // var_4_1
            if (substate != currentStyleDefault)
            {
                exitLink = GetLink(style, substate, state.SubstateMod, currentStyleDefault, requestedSpeed);
            }

            uint targetStyleDefault = LookupStyleDefault(target); // arg7_style_default
            if (_table.StyleDefaults.ContainsKey((DRWMotionCommand)target))
            {
                MotionData? newCycle = LookupCycle(target, targetStyleDefault); // eax_5

                if (newCycle is not null)
                {
                    if ((newCycle.Bitfield & 1) != 0)
                        state.ClearModifiers();

                    MotionData? directOrHop1 = GetLink(style, styleDefault, state.SubstateMod, target, requestedSpeed); // arg2
                    MotionData? hop2 = null; // var_10_1

                    if (directOrHop1 is null && target != style)
                    {
                        // DOUBLE-HOP VIA default_style.
                        directOrHop1 = GetLink(style, styleDefault, 1f, (uint)_table.DefaultStyle, 1f);
                        uint defaultStyleDefaultSubstate = LookupStyleDefault((uint)_table.DefaultStyle);
                        hop2 = GetLink((uint)_table.DefaultStyle, defaultStyleDefaultSubstate, 1f, target, 1f);
                    }

                    sequence.ClearPhysics();
                    sequence.RemoveCyclicAnims();
                    AddMotion(sequence, exitLink, requestedSpeed);
                    AddMotion(sequence, directOrHop1, requestedSpeed);
                    AddMotion(sequence, hop2, requestedSpeed);
                    AddMotion(sequence, newCycle, requestedSpeed);

                    state.Substate = targetStyleDefault;
                    state.Style = target;
                    state.SubstateMod = speed;
                    ReModify(sequence, state);

                    uint numAnims2 = (uint)(exitLink?.Anims.Count ?? 0);
                    uint ecx20 = (uint)(directOrHop1?.Anims.Count ?? 0);
                    uint numAnims1 = (uint)(hop2?.Anims.Count ?? 0);
                    outTicks = (uint)newCycle.Anims.Count + numAnims1 + ecx20 + numAnims2 - 1;
                    return true;
                }
            }
        }

        if ((target & 0x40000000) != 0)
        {
            uint substateId = target & 0xFFFFFFu;
            MotionData? cyclic = LookupCycle(style, substateId); // eax_24

            if (cyclic is null)
            {
                cyclic = LookupCycle((uint)_table.DefaultStyle, substateId);
            }

            if (cyclic is not null && IsAllowed(target, cyclic, state))
            {

                // ---- FAST RE-SPEED PATH ----
                if (target == substate
                    && SameSign(requestedSpeed, state.SubstateMod)
                    && sequence.HasAnims())
                {
                    ChangeCycleSpeed(sequence, cyclic, state.SubstateMod, requestedSpeed);
                    SubtractMotion(sequence, cyclic, state.SubstateMod);
                    CombineMotion(sequence, cyclic, requestedSpeed);
                    state.SubstateMod = speed;
                    return true;
                }

                if ((cyclic.Bitfield & 1) != 0)
                    state.ClearModifiers();

                MotionData? directLink = GetLink(style, substate, state.SubstateMod, target, requestedSpeed); // eax_34
                bool sameSignDirect = directLink is not null && SameSign(requestedSpeed, state.SubstateMod);

                MotionData? hop2 = null; // var_10_1
                MotionData? linkOrHop1 = directLink; // arg2

                if (directLink is null || !sameSignDirect)
                {
                    uint styleDefaultSubstate = LookupStyleDefault(style);
                    linkOrHop1 = GetLink(style, substate, state.SubstateMod, styleDefaultSubstate, 1f);
                    hop2 = GetLink(style, styleDefaultSubstate, 1f, target, requestedSpeed);
                }

                sequence.ClearPhysics();
                sequence.RemoveCyclicAnims();

                if (hop2 is null)
                {
                    float signedSpeed = (state.SubstateMod == 0f || SameSign(state.SubstateMod, speed))
                        ? speed : -speed;
                    AddMotion(sequence, linkOrHop1, signedSpeed);
                }
                else
                {
                    AddMotion(sequence, linkOrHop1, state.SubstateMod);
                    AddMotion(sequence, hop2, requestedSpeed);
                }

                AddMotion(sequence, cyclic, requestedSpeed);

                uint oldSubstate = state.Substate;
                if (oldSubstate != target && (oldSubstate & 0x20000000) != 0)
                {
                    uint styleDefaultSubstate2 = LookupStyleDefault(style);
                    if (styleDefaultSubstate2 != target)
                        state.AddModifierNoCheck(oldSubstate, state.SubstateMod);
                }

                state.SubstateMod = speed;
                state.Substate = target;
                ReModify(sequence, state);

                uint ecx45 = (uint)(linkOrHop1?.Anims.Count ?? 0);
                uint numAnims1b = (uint)(hop2?.Anims.Count ?? 0);
                outTicks = (uint)cyclic.Anims.Count + numAnims1b + ecx45 - 1;
                return true;
            }
        }

        if ((target & 0x10000000) != 0)
        {
            MotionData? baseCycle = LookupCycle(style, substate & 0xFFFFFFu); // eax_57
            if (baseCycle is not null)
            {
                MotionData? directLink = GetLink(style, substate, state.SubstateMod, target, requestedSpeed); // eax_60
                if (directLink is not null)
                {
                    state.AddAction(target, requestedSpeed);
                    sequence.ClearPhysics();
                    sequence.RemoveCyclicAnims();
                    AddMotion(sequence, directLink, requestedSpeed);
                    AddMotion(sequence, baseCycle, state.SubstateMod);
                    ReModify(sequence, state);
                    outTicks = (uint)directLink.Anims.Count;
                    return true;
                }

                // No direct link -> route through the style default (double-hop out-and-back).
                uint styleDefaultSubstate = LookupStyleDefault(style);
                MotionData? outHop = GetLink(style, substate, state.SubstateMod, styleDefaultSubstate, 1f); // eax_66
                if (outHop is not null)
                {
                    MotionData? actionLink = GetLink(style, styleDefaultSubstate, 1f, target, requestedSpeed); // eax_68
                    if (actionLink is not null)
                    {
                        MotionData? baseCycleRefetch = LookupCycle(style, substate & 0xFFFFFFu); // eax_69 (same key, re-fetched)
                        if (baseCycleRefetch is not null)
                        {
                            MotionData? returnHop = GetLink(style, styleDefaultSubstate, 1f, substate, state.SubstateMod);

                            state.AddAction(target, requestedSpeed);
                            sequence.ClearPhysics();
                            sequence.RemoveCyclicAnims();
                            AddMotion(sequence, outHop, 1f);
                            AddMotion(sequence, actionLink, requestedSpeed);
                            AddMotion(sequence, returnHop, 1f);
                            AddMotion(sequence, baseCycleRefetch, state.SubstateMod);
                            ReModify(sequence, state);

                            uint ticks = (uint)outHop.Anims.Count + (uint)actionLink.Anims.Count;
                            if (returnHop is not null)
                                ticks += (uint)returnHop.Anims.Count;
                            outTicks = ticks;
                            return true;
                        }
                    }
                }
            }
        }

        if ((target & 0x20000000) != 0)
        {
            MotionData? baseCycle = LookupCycle(style, substate & 0xFFFFFFu); // eax_81

            if (baseCycle is not null && (baseCycle.Bitfield & 1) == 0)
            {
                uint modKey = target & 0xFFFFFFu;
                MotionData? modifierStyled = LookupModifier(style, modKey, styleSpecific: true); // eax_85
                MotionData? modifierGlobal = null; // eax_87
                if (modifierStyled is null)
                    modifierGlobal = LookupModifier(style, modKey, styleSpecific: false);
                MotionData? modifierData = modifierStyled ?? modifierGlobal;

                if (modifierStyled is not null || modifierGlobal is not null)
                {
                    bool added = state.AddModifier(target, requestedSpeed);
                    if (!added)
                    {
                        StopSequenceMotion(target, 1f, state, sequence, out _);
                        added = state.AddModifier(target, requestedSpeed);
                    }
                    if (added)
                    {
                        CombineMotion(sequence, modifierData, requestedSpeed);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void ReModify(CSequence sequence, MotionState state)
    {
        if (!state.Modifiers.Any())
            return;

        var snapshot = new MotionState(state);

        do
        {
            var head = state.Modifiers.First();
            uint motion = head.Motion;
            float speedMod = head.SpeedMod;
            state.RemoveModifier(head);

            var snapshotHead = snapshot.Modifiers.First();
            snapshot.RemoveModifier(snapshotHead);

            GetObjectSequence(motion, state, sequence, speedMod, out _, stopCall: false);
        } while (snapshot.Modifiers.Any());
    }

    public bool StopSequenceMotion(uint motion, float speed, MotionState state, CSequence sequence, out uint outTicks)
    {
        outTicks = 0;

        if ((motion & 0x40000000) != 0 && motion == state.Substate)
        {
            uint styleDefaultSubstate = LookupStyleDefault(state.Style);
            GetObjectSequence(styleDefaultSubstate, state, sequence, 1f, out outTicks, stopCall: true);
            return true;
        }

        if ((motion & 0x20000000) != 0)
        {
            foreach (var node in state.Modifiers)
            {
                if (node.Motion != motion)
                    continue;

                uint modKey = motion & 0xFFFFFFu;
                MotionData? modData = LookupModifier(state.Style, modKey, styleSpecific: true);
                modData ??= LookupModifier(state.Style, modKey, styleSpecific: false);

                if (modData is not null)
                {
                    SubtractMotion(sequence, modData, node.SpeedMod);
                    state.RemoveModifier(node);
                    return true;
                }

                break; // matching motion id found but no MotionData anywhere -> give up
            }
        }

        return false;
    }

    public bool SetDefaultState(MotionState state, CSequence sequence, out uint outTicks)
    {
        outTicks = 0;

        if (!_table.StyleDefaults.TryGetValue(_table.DefaultStyle, out var defaultSubstateCmd))
            return false;

        uint defaultSubstate = (uint)defaultSubstateCmd;

        state.ClearModifiers();
        state.ClearActions();

        MotionData? cyclic = LookupCycle((uint)_table.DefaultStyle, defaultSubstate);
        if (cyclic is null)
            return false;

        state.Style = (uint)_table.DefaultStyle;
        state.Substate = defaultSubstate;
        state.SubstateMod = 1f;
        outTicks = (uint)cyclic.Anims.Count - 1;

        sequence.ClearPhysics();
        sequence.ClearAnimations();
        AddMotion(sequence, cyclic, state.SubstateMod);

        return true;
    }

    public bool DoObjectMotion(uint motion, MotionState state, CSequence sequence, float speed, out uint outTicks)
        => GetObjectSequence(motion, state, sequence, speed, out outTicks, stopCall: false);

    public bool StopObjectMotion(uint motion, float speed, MotionState state, CSequence sequence, out uint outTicks)
        => StopSequenceMotion(motion, speed, state, sequence, out outTicks);

    public bool StopObjectCompletely(MotionState state, CSequence sequence, out uint outTicks)
    {
        outTicks = 0;
        bool anyModifierStopOk = false;

        while (state.Modifiers.Any())
        {
            var node = state.Modifiers.First();
            float speedMod = node.SpeedMod;
            if (StopSequenceMotion(node.Motion, speedMod, state, sequence, out outTicks))
                anyModifierStopOk = true;
            else
                break; // defensive: avoid infinite loop if a stop can't unlink (shouldn't happen)
        }

        if (StopSequenceMotion(state.Substate, state.SubstateMod, state, sequence, out outTicks))
            return true;

        return anyModifierStopOk;
    }

    // ── private lookup helpers ──────────────────────────────────────────

    private uint LookupStyleDefault(uint style)
        => _table.StyleDefaults.TryGetValue((DRWMotionCommand)style, out var def) ? (uint)def : 0u;

    private MotionData? LookupCycle(uint style, uint substate)
    {
        int key = (int)((style << 16) | (substate & 0xFFFFFFu));
        return _table.Cycles.TryGetValue(key, out var data) ? data : null;
    }

    private MotionData? LookupModifier(uint style, uint modKey, bool styleSpecific)
    {
        int key = styleSpecific ? (int)((style << 16) | modKey) : (int)modKey;
        return _table.Modifiers.TryGetValue(key, out var data) ? data : null;
    }
}
