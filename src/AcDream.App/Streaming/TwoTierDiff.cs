using System.Collections.Generic;

namespace AcDream.App.Streaming;

public readonly record struct TwoTierDiff(
    IReadOnlyList<uint> ToLoadFar,    // entered far window from null (terrain only)
    IReadOnlyList<uint> ToLoadNear,   // entered near window from null (terrain + entities — first-tick or teleport)
    IReadOnlyList<uint> ToPromote,    // entered near window from far-resident (entities only)
    IReadOnlyList<uint> ToDemote,     // exited near window past hysteresis (drop entities)
    IReadOnlyList<uint> ToUnload);    // exited far window past hysteresis (drop terrain)
