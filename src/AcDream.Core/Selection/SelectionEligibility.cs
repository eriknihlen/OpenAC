using AcDream.Core.Items;
using AcDream.Core.Physics;

namespace AcDream.Core.Selection;

/// <summary>
/// The objects no selection key and no automatic target ever lands on,
/// whatever kind of thing the key is looking for.
/// </summary>
public static class SelectionEligibility
{
    /// <summary>
    /// Whether an object is out of reach of the selection keys and of
    /// auto-target: it is cloaked, or it is hidden from the UI (bit 0x80 of
    /// its public description; traps and decoration carry it).
    /// </summary>
    public static bool IsHiddenFromSelection(
        PhysicsStateFlags physicsState,
        uint? publicWeenieBitfield)
        => (physicsState & PhysicsStateFlags.Cloaked) != 0
            || ((PublicWeenieFlags)(publicWeenieBitfield ?? 0u)
                & PublicWeenieFlags.UiHidden) != 0;
}
