//! How much work a font's outlines take, measured before the rasterizer is
//! given them.
//!
//! A glyph's outline is a program of sorts: a TrueType glyph can be built of
//! other glyphs, each of which can be built of others, and a CFF glyph calls
//! subroutines that call subroutines. A small file can make one glyph cost
//! billions of steps that way, and the rasterizer cannot be stopped once it
//! starts. So the work is measured first, for the glyphs a request draws
//! only, by walking the same structure the parser walks: the parts of each
//! composite glyph once each (a part used twice costs twice but is read
//! once), or the charstring bytes each CFF glyph runs. Then the outlines are
//! followed at the size baked, counting the line segments the rasterizer
//! flattens their curves into, and their extent. A font over any limit is
//! refused before the rasterizer sees it.

use std::collections::{HashMap, HashSet};

use ttf_parser::{Face, GlyphId, OutlineBuilder, Tag};

use super::{MAXIMUM_ATLAS_SIDE, cff};

/// The most outline work one request may take: glyph parts and points read
/// (TrueType), or charstring operators run (CFF), over every glyph drawn.
/// Thousands of ordinary glyphs take a few hundred thousand.
pub(super) const MAXIMUM_OUTLINE_WORK: u64 = 4_000_000;

/// The most line segments one bake's curves may be flattened into. The
/// bundled font's default set takes about seventy thousand at 256 pixels.
pub(super) const MAXIMUM_SEGMENTS: u64 = 1_000_000;

/// How deep composite glyphs may nest: as deep as the parser follows them.
const MAXIMUM_NESTING: u32 = 32;

/// Whether the outlines of `glyphs` take no more than [`MAXIMUM_OUTLINE_WORK`]
/// to read, or why not. Runs once per font, whatever size it is baked at.
pub(super) fn check_work(face: &Face<'_>, glyphs: &[GlyphId]) -> Result<(), String> {
    let work = if face.tables().glyf.is_some() {
        // The parser draws TrueType outlines when the font has both.
        let mut walk = GlyfWalk::new(face).ok_or(UNREADABLE)?;
        let mut total = 0u64;
        for glyph in glyphs {
            total = total.saturating_add(walk.work(*glyph, 0)?);
        }
        total
    } else if face.tables().cff.is_some() {
        let table = face
            .raw_face()
            .table(Tag::from_bytes(b"CFF "))
            .ok_or(UNREADABLE)?;
        cff::charstring_work(table, glyphs, MAXIMUM_OUTLINE_WORK)?
    } else {
        0
    };
    if work > MAXIMUM_OUTLINE_WORK {
        return Err(TOO_COMPLEX.to_owned());
    }
    Ok(())
}

const UNREADABLE: &str = "the font's outlines could not be read";
const TOO_COMPLEX: &str = "the font's outlines are too complex to draw";

/// The TrueType outlines, and what each glyph costs to read with every part
/// it is built of.
struct GlyfWalk<'a> {
    loca: ttf_parser::loca::Table<'a>,
    glyf: &'a [u8],
    known: HashMap<u16, u64>,
    open: HashSet<u16>,
}

impl<'a> GlyfWalk<'a> {
    fn new(face: &Face<'a>) -> Option<Self> {
        let raw = face.raw_face();
        let loca = ttf_parser::loca::Table::parse(
            std::num::NonZeroU16::new(face.number_of_glyphs())?,
            face.tables().head.index_to_location_format,
            raw.table(Tag::from_bytes(b"loca"))?,
        )?;
        Some(Self {
            loca,
            glyf: raw.table(Tag::from_bytes(b"glyf"))?,
            known: HashMap::new(),
            open: HashSet::new(),
        })
    }

    /// One glyph's work: itself, its points, and the work of each part it
    /// names, as often as it names it. A glyph that is (through its parts)
    /// a part of itself, or nests deeper than the parser follows, is
    /// refused.
    fn work(&mut self, glyph: GlyphId, depth: u32) -> Result<u64, String> {
        if let Some(work) = self.known.get(&glyph.0) {
            return Ok(*work);
        }
        if depth >= MAXIMUM_NESTING || !self.open.insert(glyph.0) {
            return Err(TOO_COMPLEX.to_owned());
        }
        let mut work = 1u64;
        let data = self.loca.glyph_range(glyph).and_then(|r| self.glyf.get(r));
        if let Some(data) = data {
            let contours = read_u16(data, 0).map_or(0, |c| c as i16);
            if contours > 0 {
                // The last contour's last point, plus one.
                let last = 10 + 2 * (contours as usize - 1);
                let points = read_u16(data, last).map_or(0, |p| u64::from(p) + 1);
                work = work.saturating_add(points);
            } else if contours < 0 {
                for part in parts(data) {
                    work = work.saturating_add(1);
                    let drawn = self
                        .loca
                        .glyph_range(part)
                        .is_some_and(|r| self.glyf.get(r).is_some());
                    if drawn {
                        work = work.saturating_add(self.work(part, depth + 1)?);
                    }
                }
            }
        }
        self.open.remove(&glyph.0);
        self.known.insert(glyph.0, work);
        Ok(work)
    }
}

fn read_u16(data: &[u8], at: usize) -> Option<u16> {
    Some(u16::from_be_bytes(data.get(at..at + 2)?.try_into().ok()?))
}

/// The glyphs a composite glyph is built of, read as the parser reads them:
/// a part whose record is cut short ends the list.
fn parts(data: &[u8]) -> Vec<GlyphId> {
    const WORDS: u16 = 0x0001;
    const XY_VALUES: u16 = 0x0002;
    const SCALE: u16 = 0x0008;
    const MORE: u16 = 0x0020;
    const X_AND_Y_SCALE: u16 = 0x0040;
    const TWO_BY_TWO: u16 = 0x0080;
    let mut parts = Vec::new();
    let mut at = 10;
    while let (Some(flags), Some(glyph)) = (read_u16(data, at), read_u16(data, at + 2)) {
        at += 4;
        if flags & XY_VALUES != 0 {
            at += if flags & WORDS != 0 { 4 } else { 2 };
        }
        if flags & TWO_BY_TWO != 0 {
            at += 8;
        } else if flags & X_AND_Y_SCALE != 0 {
            at += 4;
        } else if flags & SCALE != 0 {
            at += 2;
        }
        if at > data.len() {
            break;
        }
        parts.push(GlyphId(glyph));
        if flags & MORE == 0 {
            break;
        }
    }
    parts
}

/// Follows one glyph's outline as the rasterizer flattens it at `em` pixels
/// an em: the segments its curves become and the extent they cover, in font
/// units. Past the budget it stops subdividing and only counts the calls.
pub(super) struct Meter {
    /// Curves are split until each piece's triangle is no larger than this
    /// (the rasterizer's own tolerance).
    max_area: f32,
    /// Segments left in the budget.
    left: u64,
    over: bool,
    previous: (f32, f32),
    start: (f32, f32),
    bounds: Option<(f32, f32, f32, f32)>,
}

impl Meter {
    /// A meter for a font of `units_per_em` drawn `em` pixels an em.
    pub(super) fn new(units_per_em: f32, em: f32) -> Self {
        // The rasterizer's error threshold: three pixels, as an area.
        const THRESHOLD: f32 = 3.0;
        Self {
            max_area: THRESHOLD * 2.0 * (units_per_em / em),
            left: MAXIMUM_SEGMENTS,
            over: false,
            previous: (0.0, 0.0),
            start: (0.0, 0.0),
            bounds: None,
        }
    }

    /// Measures one glyph, the one for character `c` or else the font's
    /// missing glyph: its extent in pixels at `scale` pixels a unit, or why
    /// it cannot be drawn.
    pub(super) fn glyph(
        &mut self,
        face: &Face<'_>,
        c: Option<char>,
        glyph: GlyphId,
        scale: f32,
    ) -> Result<(), String> {
        self.bounds = None;
        face.outline_glyph(glyph, self);
        if self.over {
            return Err(TOO_COMPLEX.to_owned());
        }
        if let Some((x0, y0, x1, y1)) = self.bounds {
            let (w, h) = ((x1 - x0) * scale, (y1 - y0) * scale);
            // A pixel more on each side for the rounding outwards.
            let side = MAXIMUM_ATLAS_SIDE as f32 - 2.0;
            if !(w <= side && h <= side) {
                let which = c.map_or_else(
                    || "the font's missing glyph".to_owned(),
                    |c| format!("U+{:04X}", c as u32),
                );
                return Err(format!(
                    "{which} is {} x {} pixels, larger than an atlas",
                    w.ceil(),
                    h.ceil()
                ));
            }
        }
        Ok(())
    }

    fn segment(&mut self, from: (f32, f32), to: (f32, f32)) {
        if self.left == 0 {
            self.over = true;
            return;
        }
        self.left -= 1;
        for (x, y) in [from, to] {
            let b = self.bounds.get_or_insert((x, y, x, y));
            *b = (b.0.min(x), b.1.min(y), b.2.max(x), b.3.max(y));
        }
    }

    /// Splits a curve at its middle until each piece is flat enough, as the
    /// rasterizer does, counting the pieces.
    fn flatten(&mut self, point: impl Fn(f32) -> (f32, f32), end: (f32, f32)) {
        let mut stack = vec![(self.previous, 0.0f32, end, 1.0f32)];
        while let Some((a, at, c, ct)) = stack.pop() {
            if self.over {
                break;
            }
            let bt = (at + ct) * 0.5;
            let b = point(bt);
            let area = (b.0 - a.0) * (c.1 - a.1) - (c.0 - a.0) * (b.1 - a.1);
            if area.abs() > self.max_area {
                stack.push((a, at, b, bt));
                stack.push((b, bt, c, ct));
            } else {
                self.segment(a, c);
            }
        }
        self.previous = end;
    }
}

impl OutlineBuilder for Meter {
    fn move_to(&mut self, x: f32, y: f32) {
        self.start = (x, y);
        self.previous = (x, y);
    }

    fn line_to(&mut self, x: f32, y: f32) {
        self.segment(self.previous, (x, y));
        self.previous = (x, y);
    }

    fn quad_to(&mut self, x1: f32, y1: f32, x: f32, y: f32) {
        let a = self.previous;
        self.flatten(
            |t| {
                let m = 1.0 - t;
                let (p, q, r) = (m * m, 2.0 * m * t, t * t);
                (p * a.0 + q * x1 + r * x, p * a.1 + q * y1 + r * y)
            },
            (x, y),
        );
    }

    fn curve_to(&mut self, x1: f32, y1: f32, x2: f32, y2: f32, x: f32, y: f32) {
        let a = self.previous;
        self.flatten(
            |t| {
                let m = 1.0 - t;
                let (p, q, r, s) = (m * m * m, 3.0 * m * m * t, 3.0 * m * t * t, t * t * t);
                (
                    p * a.0 + q * x1 + r * x2 + s * x,
                    p * a.1 + q * y1 + r * y2 + s * y,
                )
            },
            (x, y),
        );
    }

    fn close(&mut self) {
        if self.start != self.previous {
            self.segment(self.previous, self.start);
        }
        self.previous = self.start;
    }
}
