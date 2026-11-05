// A clip region: the convex screen polygon (pixels, y down) a draw reached
// through a portal view is limited to. A pixel is outside when any edge's
// a x + b y + c is positive. Slot 0 is the whole screen.
struct ClipRegion {
    count: u32,
    _p0: u32,
    _p1: u32,
    _p2: u32,
    edges: array<vec4<f32>, 32>,
};

fn outside_clip(slot: u32, p: vec2<f32>) -> bool {
    if (slot == 0u) {
        return false;
    }
    let n = min(clips[slot].count, 32u);
    for (var i = 0u; i < n; i = i + 1u) {
        let e = clips[slot].edges[i];
        if (e.x * p.x + e.y * p.y + e.z > 0.0) {
            return true;
        }
    }
    return false;
}
