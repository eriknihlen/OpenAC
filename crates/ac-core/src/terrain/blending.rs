//! Which textures blend on each terrain cell.
//!
//! Every corner of a cell has a terrain type (grass, dirt, ...; 5 bits) and
//! a road value (2 bits). The eight values pack into a palette code; equal
//! codes always blend the same way, so the result is cached per code. A
//! cell draws one base texture, up to three terrain overlays and up to two
//! road overlays; each overlay is a texture faded in through an alpha map
//! (a corner or side gradient), rotated in quarter turns to fit the
//! corners it covers.
//!
//! Corner order throughout: 1 = south-west, 2 = south-east, 3 = north-east,
//! 4 = north-west. A "corner pattern" is a 4-bit mask of corners (bit 0 =
//! corner 1); rotating a pattern by a quarter turn shifts each bit to the
//! next corner.

use super::CellSplitDirection;

/// The blend recipe of one cell: texture-array layers and alpha-map layers
/// with rotations (quarter turns, 0..=3). [`SurfaceInfo::NONE`] marks an
/// absent layer.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct SurfaceInfo {
    /// Base terrain texture layer.
    pub base_layer: u8,
    /// Terrain overlays: texture layer, alpha layer, rotation.
    pub overlays: [Overlay; 3],
    /// Road texture layer, shared by both road overlays.
    pub road_layer: u8,
    /// Road overlays: alpha layer and rotation each.
    pub roads: [RoadOverlay; 2],
}

/// One terrain overlay of a cell.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Overlay {
    /// Texture layer, or [`SurfaceInfo::NONE`].
    pub layer: u8,
    /// Alpha-map layer, or [`SurfaceInfo::NONE`].
    pub alpha_layer: u8,
    /// Quarter turns applied to the alpha map.
    pub rotation: u8,
}

/// One road overlay of a cell (the texture is [`SurfaceInfo::road_layer`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RoadOverlay {
    /// Alpha-map layer, or [`SurfaceInfo::NONE`].
    pub alpha_layer: u8,
    /// Quarter turns applied to the alpha map.
    pub rotation: u8,
}

impl Overlay {
    /// No overlay.
    pub const NONE: Self = Self {
        layer: SurfaceInfo::NONE,
        alpha_layer: SurfaceInfo::NONE,
        rotation: 0,
    };
}

impl RoadOverlay {
    /// No road overlay.
    pub const NONE: Self = Self {
        alpha_layer: SurfaceInfo::NONE,
        rotation: 0,
    };
}

impl SurfaceInfo {
    /// Marks an absent layer.
    pub const NONE: u8 = 255;

    /// Only a base texture.
    #[must_use]
    pub fn base_only(base_layer: u8) -> Self {
        Self {
            base_layer,
            overlays: [Overlay::NONE; 3],
            road_layer: Self::NONE,
            roads: [RoadOverlay::NONE; 2],
        }
    }
}

/// An alpha map available for blending: its layer in the alpha texture
/// array and the corner pattern it covers unrotated.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AlphaMap {
    /// Alpha texture-array layer.
    pub layer: u8,
    /// Corner pattern (4-bit mask) the map covers at rotation 0.
    pub code: u32,
}

/// Everything blending needs from the region and the texture arrays.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerrainBlendingContext {
    /// Texture layer per terrain type (0..32); [`SurfaceInfo::NONE`] for a
    /// type without a texture.
    pub terrain_type_to_layer: [u8; 32],
    /// Texture layer of the road surface, or [`SurfaceInfo::NONE`].
    pub road_layer: u8,
    /// Alpha maps that fade one corner.
    pub corner_alphas: Vec<AlphaMap>,
    /// Alpha maps that fade a side (two or three corners).
    pub side_alphas: Vec<AlphaMap>,
    /// Alpha maps for road edges.
    pub road_alphas: Vec<AlphaMap>,
}

impl TerrainBlendingContext {
    /// A context with no textures and no alpha maps.
    #[must_use]
    pub fn empty() -> Self {
        Self {
            terrain_type_to_layer: [SurfaceInfo::NONE; 32],
            road_layer: SurfaceInfo::NONE,
            corner_alphas: Vec::new(),
            side_alphas: Vec::new(),
            road_alphas: Vec::new(),
        }
    }

    fn terrain_layer(&self, terrain_code: u32) -> u8 {
        self.terrain_type_to_layer
            .get(terrain_code as usize)
            .copied()
            .unwrap_or(SurfaceInfo::NONE)
    }
}

/// Packs a cell's corner road values (`[r1, r2, r3, r4]`, each 0..=3) and
/// terrain types (`[t1, t2, t3, t4]`, each 0..32) into its palette code:
/// bit 28 set, roads in bits 20-27 (`r1` highest), types in bits 0-19
/// (`t1` highest).
#[must_use]
pub fn get_pal_code(roads: [u32; 4], types: [u32; 4]) -> u32 {
    let [r1, r2, r3, r4] = roads;
    let [t1, t2, t3, t4] = types;
    let terrain_bits = (t1 << 15) | (t2 << 10) | (t3 << 5) | t4;
    let road_bits = (r1 << 26) | (r2 << 24) | (r3 << 22) | (r4 << 20);
    (1 << 28) | road_bits | terrain_bits
}

/// The four corner terrain types `[t1, t2, t3, t4]` of a palette code.
#[must_use]
pub fn extract_terrain_codes(pal_code: u32) -> [u32; 4] {
    [
        (pal_code >> 15) & 0x1F,
        (pal_code >> 10) & 0x1F,
        (pal_code >> 5) & 0x1F,
        pal_code & 0x1F,
    ]
}

/// A fixed pick in `0..count` derived from the palette code, so cells with
/// equal codes choose the same alpha-map variant. 0 when `count` is 0.
#[must_use]
pub fn pseudo_random_index(pal_code: u32, count: usize) -> usize {
    if count == 0 {
        return 0;
    }
    let v = 1_379_576_222_u32
        .wrapping_mul(pal_code)
        .wrapping_sub(1_372_186_442);
    // Exact in f64: v < 2^32, scaled by exactly 2^-32, times a small count.
    let pick = (f64::from(v) * TWO_POW_MINUS_32 * count as f64).floor();
    if pick >= count as f64 {
        0
    } else {
        pick as usize
    }
}

/// 2^-32, the scale that turns a u32 into a fraction of one.
const TWO_POW_MINUS_32: f64 = 1.0 / 4_294_967_296.0;

/// A corner pattern turned one quarter: 1 → 2 → 4 → 8 → 1, 3 → 6 → 12 → 9.
#[must_use]
pub fn rotate_terrain_code(code: u32) -> u32 {
    let code = code.wrapping_mul(2);
    if code >= 16 { code - 15 } else { code }
}

/// The road layout of a cell, from its palette code.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RoadCodes {
    /// Every corner is road: the cell is drawn with the road texture alone.
    AllRoad,
    /// Up to two road corner patterns to overlay (0 = none).
    Overlays(u32, u32),
}

/// The road corner patterns of a palette code. Three road corners become
/// two overlapping two-corner patterns.
#[must_use]
pub fn get_road_codes(pal_code: u32) -> RoadCodes {
    let mut mask = 0;
    if pal_code & 0x0C00_0000 != 0 {
        mask |= 1;
    }
    if pal_code & 0x0300_0000 != 0 {
        mask |= 2;
    }
    if pal_code & 0x00C0_0000 != 0 {
        mask |= 4;
    }
    if pal_code & 0x0030_0000 != 0 {
        mask |= 8;
    }
    match mask {
        0xF => RoadCodes::AllRoad,
        0x0 => RoadCodes::Overlays(0, 0),
        0xE => RoadCodes::Overlays(6, 12),
        0xD => RoadCodes::Overlays(9, 12),
        0xB => RoadCodes::Overlays(9, 3),
        0x7 => RoadCodes::Overlays(3, 6),
        _ => RoadCodes::Overlays(mask, 0),
    }
}

/// The alpha map and rotation that fade a terrain overlay onto the corners
/// in `terrain_code`: a single corner uses the corner maps, anything else
/// the side maps. The map is the palette code's pseudo-random pick; `None`
/// if no rotation of it fits.
#[must_use]
pub fn find_terrain_alpha(
    pal_code: u32,
    terrain_code: u32,
    ctx: &TerrainBlendingContext,
) -> Option<(u8, u8)> {
    let maps = if matches!(terrain_code, 1 | 2 | 4 | 8) {
        &ctx.corner_alphas
    } else {
        &ctx.side_alphas
    };
    let map = maps.get(pseudo_random_index(pal_code, maps.len()))?;
    let mut code = map.code;
    let mut rotation = 0;
    while code != terrain_code {
        code = rotate_terrain_code(code);
        rotation += 1;
        if rotation >= 4 {
            return None;
        }
    }
    Some((map.layer, rotation))
}

/// The alpha map and rotation for a road overlay on the corners in
/// `road_code`: maps are tried in turn from the palette code's
/// pseudo-random pick, each in all four rotations. `None` if none fits.
#[must_use]
pub fn find_road_alpha(
    pal_code: u32,
    road_code: u32,
    ctx: &TerrainBlendingContext,
) -> Option<(u8, u8)> {
    let maps = &ctx.road_alphas;
    let start = pseudo_random_index(pal_code, maps.len());
    for i in 0..maps.len() {
        let map = maps[(i + start) % maps.len()];
        let mut code = map.code;
        for rotation in 0..4 {
            if code == road_code {
                return Some((map.layer, rotation));
            }
            code = rotate_terrain_code(code);
        }
    }
    None
}

/// The blend recipe of a palette code.
///
/// All-road cells draw the road texture alone. Otherwise the base is the
/// first corner type that repeats (or corner 1's type when all four
/// differ), each other type becomes an overlay over its corners, and road
/// overlays follow when the road texture exists.
#[must_use]
pub fn build_surface(pal_code: u32, ctx: &TerrainBlendingContext) -> SurfaceInfo {
    let corners = extract_terrain_codes(pal_code);
    let (road0, road1) = match get_road_codes(pal_code) {
        RoadCodes::AllRoad => return SurfaceInfo::base_only(ctx.road_layer),
        RoadCodes::Overlays(a, b) => (a, b),
    };

    let (layers, overlay_codes) = build_overlay_layers(&corners, ctx);
    let mut surface = SurfaceInfo::base_only(layers[0]);

    for (i, &code) in overlay_codes.iter().enumerate() {
        if code == 0 {
            continue;
        }
        if let Some((alpha_layer, rotation)) = find_terrain_alpha(pal_code, code, ctx) {
            surface.overlays[i] = Overlay {
                layer: layers[i + 1],
                alpha_layer,
                rotation,
            };
        }
    }

    if ctx.road_layer != SurfaceInfo::NONE
        && road0 != 0
        && let Some((alpha_layer, rotation)) = find_road_alpha(pal_code, road0, ctx)
    {
        surface.road_layer = ctx.road_layer;
        surface.roads[0] = RoadOverlay {
            alpha_layer,
            rotation,
        };
        if road1 != 0
            && let Some((alpha_layer, rotation)) = find_road_alpha(pal_code, road1, ctx)
        {
            surface.roads[1] = RoadOverlay {
                alpha_layer,
                rotation,
            };
        }
    }

    surface
}

/// Texture layers `[base, overlay 0..3]` and the corner pattern of each
/// overlay (0 = no overlay).
fn build_overlay_layers(corners: &[u32; 4], ctx: &TerrainBlendingContext) -> ([u8; 4], [u32; 3]) {
    for i in 0..4 {
        for j in i + 1..4 {
            if corners[i] == corners[j] {
                return build_with_duplicates(corners, ctx, i);
            }
        }
    }
    // Four different types: corner 1 is the base, corners 2-4 overlay.
    (corners.map(|t| ctx.terrain_layer(t)), [2, 4, 8])
}

/// Layers and patterns when some type repeats: the repeated type is the
/// base; the first other type overlays its corner (merged with the next
/// corner when that has the same type), and a further type overlays its
/// own corner.
fn build_with_duplicates(
    corners: &[u32; 4],
    ctx: &TerrainBlendingContext,
    duplicate_index: usize,
) -> ([u8; 4], [u32; 3]) {
    let primary = corners[duplicate_index];
    let mut layers = [
        ctx.terrain_layer(primary),
        SurfaceInfo::NONE,
        SurfaceInfo::NONE,
        SurfaceInfo::NONE,
    ];
    let mut codes = [0u32; 3];
    let mut secondary = None;

    for (k, &terrain) in corners.iter().enumerate() {
        if terrain == primary {
            continue;
        }
        match secondary {
            None => {
                codes[0] = 1 << k;
                secondary = Some(terrain);
                layers[1] = ctx.terrain_layer(terrain);
            }
            Some(second) => {
                if second == terrain && codes[0] == 1 << (k - 1) {
                    codes[0] += 1 << k;
                } else {
                    layers[2] = ctx.terrain_layer(terrain);
                    codes[1] = 1 << k;
                }
                break;
            }
        }
    }
    (layers, codes)
}

/// Packs a recipe and the cell's split into the four words every vertex of
/// the cell carries:
///
/// - `d0`: base layer, 255, overlay 0 layer, overlay 0 alpha (one byte each,
///   low first);
/// - `d1`: overlay 1 layer and alpha, overlay 2 layer and alpha;
/// - `d2`: road layer, road 0 alpha, road 1 layer (255 without a road 1
///   alpha), road 1 alpha;
/// - `d3`: two bits of rotation each for overlays 0-2 (bits 2-7) and roads
///   0-1 (bits 8-11), then the split direction (bit 12).
#[must_use]
pub fn fill_cell_data(s: &SurfaceInfo, split: CellSplitDirection) -> [u32; 4] {
    let pair = |layer: u8, alpha: u8| u32::from(layer) | (u32::from(alpha) << 8);
    let [o0, o1, o2] = s.overlays;
    let [rd0, rd1] = s.roads;
    let rd1_layer = if rd1.alpha_layer == SurfaceInfo::NONE {
        SurfaceInfo::NONE
    } else {
        s.road_layer
    };

    let d0 = pair(s.base_layer, SurfaceInfo::NONE) | (pair(o0.layer, o0.alpha_layer) << 16);
    let d1 = pair(o1.layer, o1.alpha_layer) | (pair(o2.layer, o2.alpha_layer) << 16);
    let d2 = pair(s.road_layer, rd0.alpha_layer) | (pair(rd1_layer, rd1.alpha_layer) << 16);
    let d3 = (u32::from(o0.rotation) << 2)
        | (u32::from(o1.rotation) << 4)
        | (u32::from(o2.rotation) << 6)
        | (u32::from(rd0.rotation) << 8)
        | (u32::from(rd1.rotation) << 10)
        | ((split as u32 & 1) << 12);
    [d0, d1, d2, d3]
}

#[cfg(test)]
mod tests {
    use super::*;

    const GRASS: u32 = 1;
    const DIRT: u32 = 4;

    /// The palette code as the game client packs it: OpenAC
    /// tests/AcDream.Core.Tests/Terrain/ClientReference.cs `GetPalCode`.
    fn client_pal_code(r: [i32; 4], t: [i32; 4]) -> u32 {
        let [r0, r1, r2, r3] = r;
        let [t0, t1, t2, t3] = t;
        (t3 + (1 << 28)
            + 32 * (t2 + 32 * (t1 + 32 * (t0 + 32 * (r3 + 4 * (r2 + 4 * (r1 + 4 * r0)))))))
            as u32
    }

    fn pal(r: [u32; 4], t: [u32; 4]) -> u32 {
        get_pal_code(r, t)
    }

    // OpenAC ClientConformanceTests.PalCode_*.
    #[test]
    fn pal_code_matches_client_samples() {
        for (r, t) in [
            ([0, 0, 0, 0], [1, 1, 1, 1]),
            ([1, 0, 0, 0], [5, 10, 15, 20]),
            ([3, 3, 3, 3], [31, 31, 31, 31]),
            ([0, 1, 2, 3], [0, 5, 10, 15]),
            ([2, 0, 1, 3], [8, 4, 12, 16]),
        ] {
            assert_eq!(
                pal(r, t),
                client_pal_code(r.map(|v| v as i32), t.map(|v| v as i32))
            );
        }
    }

    #[test]
    fn pal_code_matches_client_exhaustively() {
        for r in 0..256u32 {
            let r = [r >> 6, (r >> 4) & 3, (r >> 2) & 3, r & 3];
            let t = [5, 10, 15, 20];
            assert_eq!(
                pal(r, t),
                client_pal_code(r.map(|v| v as i32), [5, 10, 15, 20])
            );
        }
        for t in 0..(1u32 << 20) {
            let t = [t >> 15, (t >> 10) & 31, (t >> 5) & 31, t & 31];
            let r = [1, 0, 2, 3];
            assert_eq!(
                pal(r, t),
                client_pal_code([1, 0, 2, 3], t.map(|v| v as i32))
            );
        }
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/Terrain/TerrainBlendingTests.cs.

    #[test]
    fn pal_code_hand_computed() {
        assert_eq!(pal([0; 4], [GRASS; 4]), 0x1000_8421);
        assert_eq!(pal([0; 4], [0; 4]), 0x1000_0000);
        let no_road = pal([0; 4], [GRASS; 4]);
        let with_road = pal([1, 0, 0, 0], [GRASS; 4]);
        assert_eq!(no_road ^ with_road, 1 << 26);
        assert_eq!(pal([3; 4], [DIRT; 4]) & (1 << 28), 1 << 28);
        assert_eq!(pal([0; 4], [31; 4]) & 0x0FF0_0000, 0);
        assert_ne!(no_road, pal([0; 4], [GRASS, DIRT, GRASS, DIRT]));
    }

    #[test]
    fn extract_terrain_codes_inverts_pal_code() {
        let p = pal([0; 4], [GRASS, DIRT, GRASS, DIRT]);
        assert_eq!(extract_terrain_codes(p), [GRASS, DIRT, GRASS, DIRT]);
    }

    #[test]
    fn rotate_terrain_code_cycles() {
        assert_eq!(rotate_terrain_code(1), 2);
        assert_eq!(rotate_terrain_code(2), 4);
        assert_eq!(rotate_terrain_code(4), 8);
        assert_eq!(rotate_terrain_code(8), 1);
        assert_eq!(rotate_terrain_code(3), 6);
        assert_eq!(rotate_terrain_code(6), 12);
        assert_eq!(rotate_terrain_code(12), 9);
    }

    #[test]
    fn road_codes() {
        assert_eq!(
            get_road_codes(pal([0; 4], [GRASS; 4])),
            RoadCodes::Overlays(0, 0)
        );
        assert_eq!(get_road_codes(pal([1; 4], [GRASS; 4])), RoadCodes::AllRoad);
        assert_eq!(
            get_road_codes(pal([1, 0, 0, 0], [GRASS; 4])),
            RoadCodes::Overlays(1, 0)
        );
        assert_eq!(
            get_road_codes(pal([0, 1, 1, 1], [GRASS; 4])),
            RoadCodes::Overlays(6, 12)
        );
    }

    #[test]
    fn pseudo_random_index_range_and_zero_count() {
        for seed in 1..20u32 {
            assert!(pseudo_random_index(seed * 1000, 5) < 5);
        }
        assert_eq!(pseudo_random_index(0x1234_5678, 0), 0);
        assert_eq!(
            pseudo_random_index(0xDEAD_BEEF, 4),
            pseudo_random_index(0xDEAD_BEEF, 4)
        );
    }

    #[test]
    fn pseudo_random_index_golden() {
        // The client scales by exactly 2^-32 (a float constant), so the
        // pick is floor(v * n / 2^32) with no rounding at all.
        let v = 1_379_576_222_u32
            .wrapping_mul(0x1000_8421)
            .wrapping_sub(1_372_186_442);
        for n in 1..10usize {
            let expected = ((u64::from(v) * n as u64) >> 32) as usize;
            assert_eq!(pseudo_random_index(0x1000_8421, n), expected);
        }
    }

    fn ctx(layers: &[(u32, u8)], road_layer: u8) -> TerrainBlendingContext {
        let mut c = TerrainBlendingContext::empty();
        for &(t, l) in layers {
            c.terrain_type_to_layer[t as usize] = l;
        }
        c.road_layer = road_layer;
        c.corner_alphas = (0..4)
            .map(|i| AlphaMap {
                layer: i,
                code: 1 << i,
            })
            .collect();
        c.side_alphas = vec![AlphaMap { layer: 4, code: 3 }];
        c.road_alphas = [(5, 1), (6, 2), (7, 4)]
            .map(|(layer, code)| AlphaMap { layer, code })
            .to_vec();
        c
    }

    #[test]
    fn build_surface_single_type_is_base_only() {
        let c = ctx(&[(GRASS, 7), (DIRT, 11)], SurfaceInfo::NONE);
        let s = build_surface(pal([0; 4], [GRASS; 4]), &c);
        assert_eq!(s, SurfaceInfo::base_only(7));
    }

    #[test]
    fn build_surface_all_road_uses_the_road_texture() {
        let c = ctx(&[(GRASS, 7)], 24);
        let s = build_surface(pal([1; 4], [GRASS; 4]), &c);
        assert_eq!(s.base_layer, 24);
        assert_eq!(s.overlays[0].layer, SurfaceInfo::NONE);
    }

    #[test]
    fn build_surface_two_and_two_overlays_the_other_type() {
        let c = ctx(&[(GRASS, 7), (DIRT, 11)], SurfaceInfo::NONE);
        let s = build_surface(pal([0; 4], [GRASS, GRASS, DIRT, DIRT]), &c);
        assert_eq!(s.base_layer, 7);
        assert_eq!(s.overlays[0].layer, 11);
        // Corners 3 and 4 (pattern 12) are the side map's 3 turned twice.
        assert_eq!((s.overlays[0].alpha_layer, s.overlays[0].rotation), (4, 2));
    }

    #[test]
    fn duplicates_merge_adjacent_secondary_corners() {
        let c = ctx(&[(1, 1), (2, 2), (3, 3)], SurfaceInfo::NONE);
        // Types [1, 2, 2, 1]: base 1; corners 2 and 3 (pattern 6) overlay 2.
        let (layers, codes) = build_overlay_layers(&[1, 2, 2, 1], &c);
        assert_eq!(layers, [1, 2, SurfaceInfo::NONE, SurfaceInfo::NONE]);
        assert_eq!(codes, [6, 0, 0]);
        // Types [1, 2, 1, 3]: base 1; corner 2 overlays 2, corner 4 overlays 3.
        let (layers, codes) = build_overlay_layers(&[1, 2, 1, 3], &c);
        assert_eq!(layers, [1, 2, 3, SurfaceInfo::NONE]);
        assert_eq!(codes, [2, 8, 0]);
        // Four different types: corner 1 is the base.
        let (layers, codes) = build_overlay_layers(&[1, 2, 3, 0], &c);
        assert_eq!(layers, [1, 2, 3, SurfaceInfo::NONE]);
        assert_eq!(codes, [2, 4, 8]);
    }

    #[test]
    fn empty_alpha_lists_mean_no_overlay() {
        let mut c = ctx(&[(GRASS, 7), (DIRT, 11)], 24);
        c.corner_alphas.clear();
        c.side_alphas.clear();
        c.road_alphas.clear();
        let s = build_surface(pal([1, 0, 0, 0], [GRASS, DIRT, DIRT, DIRT]), &c);
        assert_eq!(s, SurfaceInfo::base_only(11));
    }

    fn sample_surface() -> SurfaceInfo {
        SurfaceInfo {
            base_layer: 0x01,
            overlays: [
                Overlay {
                    layer: 0x02,
                    alpha_layer: 0x03,
                    rotation: 1,
                },
                Overlay {
                    layer: 0x04,
                    alpha_layer: 0x05,
                    rotation: 2,
                },
                Overlay {
                    layer: 0x06,
                    alpha_layer: 0x07,
                    rotation: 3,
                },
            ],
            road_layer: 0x08,
            roads: [
                RoadOverlay {
                    alpha_layer: 0x09,
                    rotation: 1,
                },
                RoadOverlay {
                    alpha_layer: 0x0A,
                    rotation: 2,
                },
            ],
        }
    }

    #[test]
    fn fill_cell_data_bit_layout() {
        let [d0, d1, d2, d3] = fill_cell_data(&sample_surface(), CellSplitDirection::SeToNw);
        assert_eq!(d0, 0x0302_FF01);
        assert_eq!(d1, 0x0706_0504);
        assert_eq!(d2, 0x0A08_0908);
        let expected_d3 = (1 << 2) | (2 << 4) | (3 << 6) | (1 << 8) | (2 << 10) | (1 << 12);
        assert_eq!(d3, expected_d3);
    }

    #[test]
    fn fill_cell_data_without_road_1_leaves_its_slot_empty() {
        let mut s = SurfaceInfo::base_only(0x01);
        s.road_layer = 0x08;
        s.roads[0] = RoadOverlay {
            alpha_layer: 0x09,
            rotation: 0,
        };
        let [_, _, d2, _] = fill_cell_data(&s, CellSplitDirection::SwToNe);
        assert_eq!(d2, 0xFFFF_0908);
    }
}
