using System.Collections.Generic;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering.Wb;

public sealed class AnimatedEntityState
{
    private readonly Dictionary<int, ulong> _partGfxObjOverrides = new();
    private ulong _hiddenMask = 0;

    public AnimationSequencer Sequencer { get; }

    public AnimatedEntityState(AnimationSequencer sequencer)
    {
        System.ArgumentNullException.ThrowIfNull(sequencer);
        Sequencer = sequencer;
    }

    /// <summary>Set the <c>HiddenParts</c> bitmask for this entity. Bit
    /// <c>i</c> set hides part <c>i</c> at draw time.</summary>
    public void HideParts(ulong hiddenMask) => _hiddenMask = hiddenMask;

    /// <summary>True if part <c>partIdx</c> should be skipped at draw
    /// time. Returns false for part indices outside [0, 63].</summary>
    public bool IsPartHidden(int partIdx)
    {
        if (partIdx < 0 || partIdx >= 64) return false;
        return (_hiddenMask & (1ul << partIdx)) != 0;
    }

    /// <summary>Override the GfxObj id for a Setup part. Used for
    /// AnimPartChange — e.g. wielding a weapon swaps the hand-part's
    /// GfxObj.</summary>
    public void SetPartOverride(int partIdx, ulong gfxObjId)
        => _partGfxObjOverrides[partIdx] = gfxObjId;

    public bool TryGetPartOverride(int partIdx, out ulong gfxObjId)
        => _partGfxObjOverrides.TryGetValue(partIdx, out gfxObjId);

    public ulong ResolvePartGfxObj(int partIdx, ulong setupDefault)
        => TryGetPartOverride(partIdx, out var ov) ? ov : setupDefault;
}
