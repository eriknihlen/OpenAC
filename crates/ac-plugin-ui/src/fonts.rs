//! The fonts a plugin draws canvas text with: the client's bundled Noto Sans
//! in either weight, or a TrueType or OpenType font the plugin ships, each
//! baked at one size into a coverage atlas. They are session state like
//! images: a plugin asks, is answered and can measure with or without a
//! window, and a window only uploads the atlases it is told about.
//!
//! Every request is counted once per distinct font, size and set of
//! characters and held as many times as asked. A plugin may hold
//! [`MAXIMUM_COUNT`] fonts, and its own fonts may take [`MAXIMUM_BYTES`],
//! counting the files and their atlases. The bundled font is shared between
//! plugins: a bake another plugin holds too costs nothing, one the plugin
//! holds alone counts its atlas. On a high-density display each font also
//! gets a sharper bake, which draws in its place while every measurement
//! still comes from the font itself. Those are the client's choice, so they
//! count against [`MAXIMUM_SHARP_BYTES`] instead, and a plugin that fits one
//! display fits every display.
//!
//! A font file comes from a plugin and is read outside its sandbox, so a file
//! must never bring the client down. Its structure is checked before the
//! parsers see it; the work its outlines take is measured, for the glyphs
//! asked for only, before the rasterizer is given them, and the rasterizer
//! reads a copy that names only those glyphs; a glyph's size is checked
//! before anything is allocated for it; kerning is read once, with a bound,
//! when the font is baked; and a panic in the parsers is a refusal like any
//! other. Handles are never reused.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::fmt;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Weak};

use ttf_parser::{Face, GlyphId};

mod cff;
mod kerning;
mod outline;
mod sfnt;

use kerning::Kerning;
pub use kerning::{MAXIMUM_KERNING_PAIRS, MAXIMUM_KERNING_WORK};

/// The regular font file, compiled in.
pub static NOTO_SANS_REGULAR: &[u8] =
    include_bytes!("../../../assets/fonts/NotoSans/NotoSans-Regular.ttf");

/// The SemiBold font file, compiled in.
pub static NOTO_SANS_SEMIBOLD: &[u8] =
    include_bytes!("../../../assets/fonts/NotoSans/NotoSans-SemiBold.ttf");

/// The most distinct fonts one plugin may hold.
pub const MAXIMUM_COUNT: usize = 16;
/// The most memory one plugin's own fonts may take: the files and their
/// atlases, and the atlases of bundled fonts it alone holds, bytes.
pub const MAXIMUM_BYTES: u64 = 16 * 1024 * 1024;
/// The largest font file a plugin may hand over, bytes: a file this size
/// still leaves room for an atlas, and fits one request.
pub const MAXIMUM_FILE_BYTES: usize = 15 * 1024 * 1024;
/// The most memory the sharper bakes of one plugin's fonts may take: its own
/// fonts', and those of bundled fonts it alone holds.
pub const MAXIMUM_SHARP_BYTES: u64 = 32 * 1024 * 1024;
/// The most characters one font may prepare.
pub const MAXIMUM_GLYPHS: usize = 2048;
/// The smallest size a font may be asked for, pixels.
pub const MINIMUM_PIXEL_SIZE: f32 = 6.0;
/// The largest size a font may be asked for, pixels.
pub const MAXIMUM_PIXEL_SIZE: f32 = 64.0;
/// The largest size a sharper bake is made at, pixels.
pub const MAXIMUM_SHARP_PIXEL_SIZE: f32 = 256.0;
/// The most code points a request's ranges may name.
pub const MAXIMUM_SCANNED_CODEPOINTS: u64 = 65_536;
/// The most ranges one request may list.
pub const MAXIMUM_RANGES: usize = 256;
/// The longest name a plugin may give a font file, bytes.
pub const MAXIMUM_NAME_BYTES: usize = 256;
/// The widest or tallest an atlas may be, pixels.
pub const MAXIMUM_ATLAS_SIDE: u32 = 2048;

/// The characters baked when a request names none: Latin, Greek and Cyrillic,
/// and general punctuation.
pub const DEFAULT_RANGES: [(u32, u32); 3] = [(0x20, 0x24F), (0x370, 0x52F), (0x2000, 0x206F)];

/// The atlas sizes tried, smallest first.
const ATLAS_SIZES: [(u32, u32); 7] = [
    (256, 256),
    (512, 256),
    (512, 512),
    (1024, 512),
    (1024, 1024),
    (2048, 1024),
    (2048, 2048),
];

/// A sharper bake's scale goes up in steps this size.
const SHARP_SCALE_STEP: f32 = 0.25;

/// How many times its line height a font's em may be. A font whose line
/// spans a sliver of its em would draw each glyph that many times the size
/// asked for.
const MAXIMUM_EM_PER_LINE: i32 = 4;

/// A character without a glyph draws as this, when it was baked.
const FALLBACK: char = '?';

/// The weights of the bundled font.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum FontWeight {
    /// Body text.
    #[default]
    Regular,
    /// The weight themed window titles are set in.
    SemiBold,
}

impl FontWeight {
    fn file(self) -> &'static [u8] {
        match self {
            Self::Regular => NOTO_SANS_REGULAR,
            Self::SemiBold => NOTO_SANS_SEMIBOLD,
        }
    }

    fn name(self) -> &'static str {
        match self {
            Self::Regular => "Regular",
            Self::SemiBold => "SemiBold",
        }
    }
}

/// One glyph as a font draws it: its box relative to the pen on the baseline
/// (y down), its advance and its place in the atlas, all at the size baked.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FontGlyph {
    /// The box's left edge from the pen.
    pub x: f32,
    /// The box's top edge from the baseline, y down.
    pub y: f32,
    /// The box's width.
    pub width: f32,
    /// The box's height.
    pub height: f32,
    /// How far the pen moves.
    pub advance: f32,
    /// The atlas area, left.
    pub u0: f32,
    /// The atlas area, top.
    pub v0: f32,
    /// The atlas area, right.
    pub u1: f32,
    /// The atlas area, bottom.
    pub v1: f32,
    /// The font's own number for it, which kerning is looked up by.
    pub index: u16,
}

#[derive(Debug, Clone)]
enum FontFile {
    Bundled(&'static [u8]),
    Own(Arc<Vec<u8>>),
}

impl FontFile {
    fn bytes(&self) -> &[u8] {
        match self {
            Self::Bundled(bytes) => bytes,
            Self::Own(bytes) => bytes,
        }
    }
}

/// A font baked at one size: its coverage atlas, one byte a pixel, drawn white
/// with that alpha, and what it takes to lay text out in it.
pub struct FontBake {
    /// The size baked, pixels from the highest ascender to the lowest
    /// descender.
    pub pixel_size: f32,
    /// How far apart two lines sit, pixels.
    pub line_height: f32,
    /// How far below the top of a line the baseline sits, pixels.
    pub ascent: f32,
    /// The atlas's width, pixels.
    pub atlas_width: u32,
    /// The atlas's height, pixels.
    pub atlas_height: u32,
    /// The atlas, rows from the top.
    pub coverage: Vec<u8>,
    glyphs: HashMap<char, FontGlyph>,
    file: FontFile,
    /// Font units to pixels.
    scale: f32,
    /// Read when the font was first baked; a sharper bake shares it.
    kerning: Arc<Kerning>,
}

impl fmt::Debug for FontBake {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("FontBake")
            .field("pixel_size", &self.pixel_size)
            .field("atlas_width", &self.atlas_width)
            .field("atlas_height", &self.atlas_height)
            .field("glyphs", &self.glyphs.len())
            .finish_non_exhaustive()
    }
}

impl FontBake {
    /// The glyph for a character, or the font's `?` when it lacks one and `?`
    /// was baked. Control characters have none.
    #[must_use]
    pub fn glyph(&self, c: char) -> Option<&FontGlyph> {
        if (c as u32) < 0x20 {
            return None;
        }
        self.glyphs.get(&c).or_else(|| self.glyphs.get(&FALLBACK))
    }

    /// How many characters were baked.
    #[must_use]
    pub fn glyph_count(&self) -> usize {
        self.glyphs.len()
    }

    /// How far the pen moves between two glyphs beyond their advances, pixels.
    #[must_use]
    pub fn kerning(&self, left: u16, right: u16) -> f32 {
        self.kerning.get(left, right) as f32 * self.scale
    }

    /// Each glyph of a line of text with where its pen stands, and the pen's
    /// travel over the whole line: advances plus kerning. A control character,
    /// or one with no glyph and no `?`, is skipped and breaks the kerning.
    pub fn lay_out(&self, text: &str, mut each: impl FnMut(char, &FontGlyph, f32)) -> f32 {
        let mut pen = 0.0;
        let mut previous = None;
        for c in text.chars() {
            let Some(glyph) = self.glyph(c) else {
                previous = None;
                continue;
            };
            if let Some(left) = previous {
                pen += self.kerning(left, glyph.index);
            }
            each(c, glyph, pen);
            pen += glyph.advance;
            previous = Some(glyph.index);
        }
        pen
    }

    /// The width of a line of text.
    #[must_use]
    pub fn measure_width(&self, text: &str) -> f32 {
        self.lay_out(text, |_, _, _| {})
    }

    fn atlas_bytes(&self) -> u64 {
        u64::from(self.atlas_width) * u64::from(self.atlas_height)
    }

    /// The least atlas area the glyphs could take at `scale` times this
    /// size: each box at least its size here, less the pixel either side
    /// rounding outwards added, times the scale.
    fn least_area_at(&self, scale: f32) -> f64 {
        let inner: f64 = self
            .glyphs
            .values()
            .map(|g| f64::from((g.width - 2.0).max(0.0)) * f64::from((g.height - 2.0).max(0.0)))
            .sum();
        inner * f64::from(scale) * f64::from(scale)
    }
}

/// Whether a file's header, table directory and every table's extent are
/// sound, and it has the tables a font needs, or why not. Runs before the
/// parsers see the file.
fn validate_sfnt(bytes: &[u8]) -> Result<(), String> {
    const NOT_A_FONT: &str = "the file is not a TrueType or OpenType font";
    let word = |at: usize| -> Option<u32> {
        Some(u32::from_be_bytes(bytes.get(at..at + 4)?.try_into().ok()?))
    };
    let version = word(0).ok_or(NOT_A_FONT)?;
    if version == u32::from_be_bytes(*b"ttcf") {
        return Err("font collections are not supported".to_owned());
    }
    if version != 0x0001_0000
        && version != u32::from_be_bytes(*b"OTTO")
        && version != u32::from_be_bytes(*b"true")
    {
        return Err(NOT_A_FONT.to_owned());
    }
    let tables = bytes
        .get(4..6)
        .map_or(0, |b| usize::from(u16::from_be_bytes([b[0], b[1]])));
    if tables == 0 || 12 + 16 * tables > bytes.len() {
        return Err(NOT_A_FONT.to_owned());
    }
    let mut tags = BTreeSet::new();
    for table in 0..tables {
        let record = 12 + 16 * table;
        let (Some(tag), Some(offset), Some(length)) =
            (word(record), word(record + 8), word(record + 12))
        else {
            return Err(NOT_A_FONT.to_owned());
        };
        if u64::from(offset) + u64::from(length) > bytes.len() as u64 {
            return Err(NOT_A_FONT.to_owned());
        }
        // The directory is sorted, each table once. The parser keeps the last
        // of two tables with one tag while a lookup by tag finds either, so a
        // second table could be drawn that was never checked.
        if tags.last().is_some_and(|last| *last >= tag.to_be_bytes()) {
            return Err(
                "the font's table directory is not sorted, or names a table twice".to_owned(),
            );
        }
        tags.insert(tag.to_be_bytes());
    }
    for required in [b"cmap", b"head", b"hhea", b"hmtx"] {
        if !tags.contains(required) {
            let name = String::from_utf8_lossy(required).into_owned();
            return Err(format!("the font is missing its '{name}' table"));
        }
    }
    let has_outlines = tags.contains(b"CFF ") || (tags.contains(b"glyf") && tags.contains(b"loca"));
    if !has_outlines {
        return Err("the font is missing its outlines ('glyf' and 'loca', or 'CFF ')".to_owned());
    }
    Ok(())
}

/// The characters to bake: those the ranges name that the font has.
fn collect_characters(
    face: &Face<'_>,
    ranges: &[(u32, u32)],
    maximum_glyphs: usize,
) -> Result<Vec<(char, GlyphId)>, String> {
    let subtables = sfnt::lookup_subtables(face);
    let mut scanned = 0u64;
    let mut present = BTreeSet::new();
    for &(first, last) in ranges {
        if last > 0x10_FFFF || last < first {
            return Err(format!(
                "the range U+{first:04X}-U+{last:04X} is not a range of code points"
            ));
        }
        scanned += u64::from(last - first) + 1;
        if scanned > MAXIMUM_SCANNED_CODEPOINTS {
            return Err(format!(
                "the ranges name more than {MAXIMUM_SCANNED_CODEPOINTS} code points"
            ));
        }
        for codepoint in first..=last {
            // A surrogate is no character, and no text can name it.
            let Some(c) = char::from_u32(codepoint) else {
                continue;
            };
            if let Some(glyph) = sfnt::glyph_index(&subtables, c).filter(|g| g.0 != 0) {
                present.insert((c, glyph.0));
            }
        }
    }
    if present.is_empty() {
        return Err("the font has none of the requested characters".to_owned());
    }
    if present.len() > maximum_glyphs {
        return Err(format!(
            "the font has {} of the requested characters; the most a font may prepare is {maximum_glyphs}",
            present.len()
        ));
    }
    Ok(present.into_iter().map(|(c, g)| (c, GlyphId(g))).collect())
}

/// Where each box goes in an atlas: left to right in rows, tallest first, with
/// a pixel of space; none when they do not all fit.
fn pack(boxes: &[(u32, u32)], width: u32, height: u32) -> Option<Vec<(u32, u32)>> {
    let mut order: Vec<usize> = (0..boxes.len()).collect();
    order.sort_by_key(|&at| std::cmp::Reverse(boxes[at].1));
    let mut places = vec![(0, 0); boxes.len()];
    let (mut x, mut y, mut row) = (1, 1, 0);
    for at in order {
        let (w, h) = boxes[at];
        if w == 0 || h == 0 {
            continue;
        }
        if x + w + 1 > width {
            x = 1;
            y += row + 1;
            row = 0;
        }
        if x + w + 1 > width || y + h + 1 > height {
            return None;
        }
        places[at] = (x, y);
        x += w + 1;
        row = row.max(h);
    }
    Some(places)
}

/// A font file read and checked once, ready to bake at any size: the
/// characters it has of those asked for, its line metrics, and the copy of it
/// the rasterizer reads. Its outlines' work was measured.
struct Prepared<'a> {
    face: Face<'a>,
    characters: Vec<(char, GlyphId)>,
    /// Each glyph the characters use, once.
    glyphs: Vec<GlyphId>,
    /// Ascender, descender and line gap, font units.
    line: (i32, i32, i32),
    raster: Vec<u8>,
}

fn prepare<'a>(
    bytes: &'a [u8],
    ranges: &[(u32, u32)],
    maximum_glyphs: usize,
) -> Result<Prepared<'a>, String> {
    validate_sfnt(bytes)?;
    let face = Face::parse(bytes, 0)
        .map_err(|_| "the file is not a TrueType or OpenType font".to_owned())?;
    let characters = collect_characters(&face, ranges, maximum_glyphs)?;
    let hhea = face.tables().hhea;
    let line = (
        i32::from(hhea.ascender),
        i32::from(hhea.descender),
        i32::from(hhea.line_gap),
    );
    let span = line.0 - line.1;
    if span <= 0 || i32::from(face.units_per_em()) > MAXIMUM_EM_PER_LINE * span {
        return Err("the font's line metrics are not usable".to_owned());
    }
    let mut glyphs: Vec<GlyphId> = characters.iter().map(|(_, g)| *g).collect();
    glyphs.sort_unstable();
    glyphs.dedup();
    // The rasterizer outlines the missing glyph as well.
    let mut drawn = glyphs.clone();
    if drawn.first() != Some(&GlyphId(0)) {
        drawn.insert(0, GlyphId(0));
    }
    outline::check_work(&face, &drawn)?;
    let raster = sfnt::raster_copy(&face, bytes, &characters)?;
    Ok(Prepared {
        face,
        characters,
        glyphs,
        line,
        raster,
    })
}

/// Bakes a prepared font at `pixel_size` into the smallest atlas of no more
/// than `maximum_atlas_bytes` that holds it.
fn bake_prepared(
    prepared: &Prepared<'_>,
    file: FontFile,
    pixel_size: f32,
    maximum_atlas_bytes: u64,
    kerning: Arc<Kerning>,
) -> Result<FontBake, String> {
    let face = &prepared.face;
    let characters = &prepared.characters;
    let (ascent, descent, gap) = prepared.line;
    let scale = pixel_size / (ascent - descent) as f32;
    let units_per_em = f32::from(face.units_per_em());
    let em = scale * units_per_em;
    // What the rasterizer will flatten, measured before it is asked to.
    let mut meter = outline::Meter::new(units_per_em, em);
    let mut measured = HashSet::new();
    for (c, glyph) in characters {
        if measured.insert(glyph.0) {
            meter.glyph(face, Some(*c), *glyph, scale)?;
        }
    }
    if measured.insert(0) {
        meter.glyph(face, None, GlyphId(0), scale)?;
    }
    let raster = fontdue::Font::from_bytes(
        prepared.raster.as_slice(),
        fontdue::FontSettings {
            collection_index: 0,
            // Outlines are flattened for the size they are drawn at.
            scale: em,
            load_substitutions: false,
        },
    )
    .map_err(|_| "the file is not a TrueType or OpenType font".to_owned())?;
    let mut boxes = Vec::with_capacity(characters.len());
    let mut area = 0u64;
    for (c, glyph) in characters {
        // Checked first: a hostile outline can claim a glyph bigger than
        // memory, and rasterizing allocates what it claims.
        let metrics = raster.metrics_indexed(glyph.0, em);
        let side = MAXIMUM_ATLAS_SIDE as usize;
        if metrics.width > side || metrics.height > side {
            return Err(format!(
                "U+{:04X} is {} x {} pixels, larger than an atlas",
                *c as u32, metrics.width, metrics.height
            ));
        }
        area += (metrics.width as u64 + 1) * (metrics.height as u64 + 1);
        boxes.push((metrics.width as u32, metrics.height as u32));
    }
    let mut chosen = None;
    let mut largest = None;
    for (width, height) in ATLAS_SIZES {
        let atlas = u64::from(width) * u64::from(height);
        if atlas > maximum_atlas_bytes {
            break;
        }
        largest = Some((width, height));
        if atlas >= area
            && let Some(places) = pack(&boxes, width, height)
        {
            chosen = Some((width, height, places));
            break;
        }
    }
    let Some((width, height, places)) = chosen else {
        return Err(match largest {
            Some((w, h)) => format!(
                "{} glyphs at {pixel_size} px do not fit a {w}x{h} atlas",
                characters.len()
            ),
            None => format!("no atlas fits in {maximum_atlas_bytes} bytes"),
        });
    };
    let mut coverage = vec![0u8; (width as usize) * (height as usize)];
    let mut glyphs = HashMap::with_capacity(characters.len());
    for (at, (c, glyph)) in characters.iter().enumerate() {
        let (metrics, pixels) = raster.rasterize_indexed(glyph.0, em);
        let (x, y) = places[at];
        let (w, h) = boxes[at];
        // What it draws must be what was measured and packed.
        if (metrics.width as u32, metrics.height as u32) != (w, h) {
            return Err(format!("U+{:04X} could not be drawn", *c as u32));
        }
        for row in 0..h as usize {
            let start = (y as usize + row) * width as usize + x as usize;
            coverage[start..start + w as usize]
                .copy_from_slice(&pixels[row * w as usize..(row + 1) * w as usize]);
        }
        let advance = f32::from(face.glyph_hor_advance(*glyph).unwrap_or(0)) * scale;
        let (fw, fh) = (width as f32, height as f32);
        glyphs.insert(
            *c,
            FontGlyph {
                x: metrics.xmin as f32,
                y: -(metrics.ymin + metrics.height as i32) as f32,
                width: w as f32,
                height: h as f32,
                advance,
                u0: x as f32 / fw,
                v0: y as f32 / fh,
                u1: (x + w) as f32 / fw,
                v1: (y + h) as f32 / fh,
                index: glyph.0,
            },
        );
    }
    let line_height = ((ascent - descent + gap) as f32 * scale).ceil();
    let baseline = (ascent as f32 * scale).round_ties_even();
    drop(raster);
    Ok(FontBake {
        pixel_size,
        line_height,
        ascent: baseline,
        atlas_width: width,
        atlas_height: height,
        coverage,
        glyphs,
        file,
        scale,
        kerning,
    })
}

/// `work` with a panic in the parsers as a refusal.
fn guarded<T>(work: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
    catch_unwind(AssertUnwindSafe(work))
        .unwrap_or_else(|_| Err("the file could not be read as a font".to_owned()))
}

/// Prepares and bakes the font in `file`, reading its kerning unless it is
/// given (a bundled weight's kerning is the same at every size).
fn bake_guarded(
    file: FontFile,
    pixel_size: f32,
    ranges: &[(u32, u32)],
    maximum_glyphs: usize,
    maximum_atlas_bytes: u64,
    kerning: Option<Arc<Kerning>>,
) -> Result<FontBake, String> {
    guarded(|| {
        let prepared = prepare(file.bytes(), ranges, maximum_glyphs)?;
        let kerning =
            kerning.unwrap_or_else(|| Arc::new(kerning::read(&prepared.face, &prepared.glyphs)));
        bake_prepared(
            &prepared,
            file.clone(),
            pixel_size,
            maximum_atlas_bytes,
            kerning,
        )
    })
}

/// Bakes the font in `bytes` at `pixel_size`: the characters `ranges` names
/// that it has, into the smallest atlas that holds them.
///
/// # Errors
/// Why the file cannot be used, never a panic, whatever the bytes are.
pub fn bake(
    bytes: Vec<u8>,
    pixel_size: f32,
    ranges: &[(u32, u32)],
    maximum_glyphs: usize,
) -> Result<FontBake, String> {
    if !pixel_size.is_finite() || pixel_size <= 0.0 {
        return Err("the size is not a positive number".to_owned());
    }
    bake_guarded(
        FontFile::Own(Arc::new(bytes)),
        pixel_size,
        ranges,
        maximum_glyphs,
        u64::MAX,
        None,
    )
}

/// A font's glyphs baked again at a multiple of its size.
#[derive(Debug, Clone)]
pub struct SharpBake {
    /// How many times the font's size it was baked at.
    pub scale: f32,
    /// The bake. Only its glyph images are drawn from; the font itself sets
    /// every advance, kerning and line metric, so text measures the same at
    /// every scale.
    pub font: Arc<FontBake>,
}

/// One font a plugin holds, as a window draws it.
#[derive(Debug, Clone)]
pub struct FontView {
    /// The plugin.
    pub owner: String,
    /// The plugin's handle (never reused).
    pub handle: u32,
    /// The font.
    pub font: Arc<FontBake>,
    /// Its sharper bake for the display in use, if it has one.
    pub sharp: Option<SharpBake>,
}

impl PartialEq for FontView {
    fn eq(&self, other: &Self) -> bool {
        self.owner == other.owner
            && self.handle == other.handle
            && Arc::ptr_eq(&self.font, &other.font)
            && match (&self.sharp, &other.sharp) {
                (Some(a), Some(b)) => a.scale == b.scale && Arc::ptr_eq(&a.font, &b.font),
                (None, None) => true,
                _ => false,
            }
    }
}

/// What a font request is answered with; all zero when it was refused.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct FontInfo {
    /// The handle.
    pub handle: u32,
    /// The size prepared: the size asked for, to the nearest quarter pixel.
    pub pixel_size: f32,
    /// How far apart two lines sit, pixels.
    pub line_height: f32,
    /// How far below the top of a line the baseline sits, pixels.
    pub ascent: f32,
}

/// A size in quarter pixels: how sizes are told apart, and what is prepared.
fn quarters(pixel_size: f32) -> u32 {
    (pixel_size * 4.0).round_ties_even() as u32
}

/// `value` rounded down to a quarter step, and no less than 1.
fn quarter_step_below(value: f32) -> f32 {
    if !value.is_finite() {
        return 1.0;
    }
    ((value / SHARP_SCALE_STEP + 1e-3).floor() * SHARP_SCALE_STEP).max(1.0)
}

/// The scale a sharper bake is made at on a display with `density` pixels a
/// unit: that density rounded down to a quarter step.
fn sharp_scale(density: f32) -> f32 {
    quarter_step_below(density)
}

/// A bake of the bundled font: its weight, its size in quarter pixels, and
/// its scale in quarter steps (4 for the font itself, more for a sharper
/// bake).
pub(crate) type BundledKey = (FontWeight, u32, u32);

/// The scale of the bundled font itself, in quarter steps.
const UNSCALED: u32 = 4;

/// The bundled font's bakes, which every plugin shares: each stays while a
/// plugin holds it.
#[derive(Debug, Default)]
pub(crate) struct BundledBakes {
    bakes: HashMap<(FontWeight, u32), Weak<FontBake>>,
    /// The sharper bakes, by weight, size and scale in quarter steps.
    sharp: HashMap<(FontWeight, u32, u32), Weak<FontBake>>,
    /// Each weight's kerning, the same at every size.
    kerning: HashMap<FontWeight, Weak<Kerning>>,
}

impl BundledBakes {
    fn acquire(
        &mut self,
        weight: FontWeight,
        quarter_pixels: u32,
    ) -> Result<Arc<FontBake>, String> {
        if let Some(bake) = self
            .bakes
            .get(&(weight, quarter_pixels))
            .and_then(Weak::upgrade)
        {
            return Ok(bake);
        }
        let kerning = self.kerning.get(&weight).and_then(Weak::upgrade);
        let bake = Arc::new(bake_guarded(
            FontFile::Bundled(weight.file()),
            quarter_pixels as f32 / 4.0,
            &DEFAULT_RANGES,
            MAXIMUM_GLYPHS,
            u64::MAX,
            kerning,
        )?);
        self.kerning.insert(weight, Arc::downgrade(&bake.kerning));
        self.bakes.retain(|_, held| held.strong_count() > 0);
        self.bakes
            .insert((weight, quarter_pixels), Arc::downgrade(&bake));
        Ok(bake)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
enum Key {
    Bundled {
        weight: FontWeight,
        quarter_pixels: u32,
    },
    Own {
        name: String,
        quarter_pixels: u32,
        ranges: Vec<(u32, u32)>,
    },
}

#[derive(Debug)]
struct Entry {
    key: Key,
    handle: u32,
    font: Arc<FontBake>,
    sharp: Option<SharpBake>,
    /// The density [`Self::sharp`] was prepared for.
    prepared_for: f32,
    /// Sharper bakes that failed: the scale in quarter steps, and the room
    /// there was. Not tried again unless there is more room.
    sharp_failed: Vec<(u32, u64)>,
    /// Zero for the bundled font; file and atlas for the plugin's own.
    owned_bytes: u64,
    holds: u32,
}

impl Entry {
    /// The bundled bakes it draws with: the font and its sharper bake.
    fn bundled_keys(&self) -> impl Iterator<Item = BundledKey> + '_ {
        let base = match self.key {
            Key::Bundled {
                weight,
                quarter_pixels,
            } => Some((weight, quarter_pixels)),
            Key::Own { .. } => None,
        };
        base.into_iter().flat_map(move |(weight, q)| {
            let sharp = self.sharp.as_ref().map(|s| (weight, q, quarters(s.scale)));
            std::iter::once((weight, q, UNSCALED)).chain(sharp)
        })
    }
}

/// What a request came to.
#[derive(Debug)]
pub(crate) struct AcquiredFont {
    pub(crate) info: FontInfo,
    /// The font when this request made it (not a repeat).
    pub(crate) added: Option<FontView>,
    /// What to log about a font that was made, though not all of it.
    pub(crate) notice: Option<String>,
}

/// What a report is about. Each kind is reported to a plugin for at most
/// [`REPORTS_PER_KIND`] distinct requests, so what is remembered stays small
/// whatever the plugin asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
enum Refusal {
    Size,
    Count,
    Name,
    Ranges,
    FileSize,
    Budget,
    Unreadable,
    Bundled,
    Sharp,
    Holds,
    Handles,
    /// A font was made with only part of its kerning.
    Kerning,
}

/// How many distinct reports of one kind a plugin is given; one more says
/// the rest are left out.
const REPORTS_PER_KIND: usize = 8;

/// The most characters of a plugin's name for a file a report shows.
const SHOWN_NAME_CHARS: usize = 64;

/// A plugin's name for a file as a report shows it.
fn shown(name: &str) -> String {
    if name.chars().count() <= SHOWN_NAME_CHARS {
        return name.to_owned();
    }
    let mut shown: String = name.chars().take(SHOWN_NAME_CHARS).collect();
    shown.push_str("...");
    shown
}

/// One plugin's fonts. See the module documentation.
#[derive(Debug)]
pub(crate) struct FontTable {
    entries: Vec<Entry>,
    next_handle: u32,
    owned_bytes: u64,
    sharp_bytes: u64,
    /// The reports given, by kind.
    reported: BTreeMap<Refusal, Vec<String>>,
}

impl FontTable {
    /// An empty table whose handles start after `last`.
    pub(crate) fn after(last: u32) -> Self {
        Self {
            entries: Vec::new(),
            next_handle: last,
            owned_bytes: 0,
            sharp_bytes: 0,
            reported: BTreeMap::new(),
        }
    }

    pub(crate) fn last_handle(&self) -> u32 {
        self.next_handle
    }

    pub(crate) fn count(&self) -> usize {
        self.entries.len()
    }

    #[cfg(test)]
    pub(crate) fn owned_bytes(&self) -> u64 {
        self.owned_bytes
    }

    /// Every bake of the bundled font the plugin draws with.
    pub(crate) fn bundled_keys(&self) -> impl Iterator<Item = BundledKey> + '_ {
        self.entries.iter().flat_map(Entry::bundled_keys)
    }

    /// The atlases of the bundled bakes no other plugin holds: the fonts', and
    /// their sharper bakes'.
    fn alone_bytes(&self, others: &HashSet<BundledKey>) -> (u64, u64) {
        let (mut base, mut sharp) = (0, 0);
        for entry in &self.entries {
            for key in entry.bundled_keys() {
                if others.contains(&key) {
                    continue;
                }
                if key.2 == UNSCALED {
                    base += entry.font.atlas_bytes();
                } else if let Some(s) = &entry.sharp {
                    sharp += s.font.atlas_bytes();
                }
            }
        }
        (base, sharp)
    }

    /// A report to log, or nothing: the same report is logged once, and a kind
    /// [`REPORTS_PER_KIND`] times, then once more saying the rest are left
    /// out.
    fn once(&mut self, why: Refusal, message: String) -> String {
        let given = self.reported.entry(why).or_default();
        if given.contains(&message) || given.len() > REPORTS_PER_KIND {
            return String::new();
        }
        given.push(message.clone());
        if given.len() > REPORTS_PER_KIND {
            return format!("{message} (further reports like this are left out)");
        }
        message
    }

    fn size_refused(&mut self, pixel_size: f32) -> Option<String> {
        if pixel_size.is_finite() && (MINIMUM_PIXEL_SIZE..=MAXIMUM_PIXEL_SIZE).contains(&pixel_size)
        {
            return None;
        }
        Some(self.once(
            Refusal::Size,
            format!(
                "a font size of {pixel_size} px was refused; sizes run from {MINIMUM_PIXEL_SIZE} to {MAXIMUM_PIXEL_SIZE} px"
            ),
        ))
    }

    /// The font held once more, if it is held: refused when it is held as
    /// many times as a count can say.
    fn hold_again(&mut self, key: &Key) -> Option<Result<AcquiredFont, String>> {
        let at = self.entries.iter().position(|e| e.key == *key)?;
        let Some(holds) = self.entries[at].holds.checked_add(1) else {
            return Some(Err(self.once(
                Refusal::Holds,
                "a font was refused: the plugin holds it as many times as it may".to_owned(),
            )));
        };
        self.entries[at].holds = holds;
        Some(Ok(AcquiredFont {
            info: info_of(&self.entries[at]),
            added: None,
            notice: None,
        }))
    }

    fn room_refused(&mut self, what: &str) -> Option<String> {
        if self.entries.len() < MAXIMUM_COUNT {
            return None;
        }
        Some(self.once(
            Refusal::Count,
            format!("{what} refused: the plugin already holds {MAXIMUM_COUNT} fonts, which is the most it may"),
        ))
    }

    fn add(
        &mut self,
        owner: &str,
        key: Key,
        font: Arc<FontBake>,
        owned_bytes: u64,
    ) -> Result<AcquiredFont, String> {
        let Some(handle) = self.next_handle.checked_add(1) else {
            return Err(self.once(
                Refusal::Handles,
                "a font was refused: the plugin has used every font handle there is".to_owned(),
            ));
        };
        self.next_handle = handle;
        let entry = Entry {
            key,
            handle,
            font,
            sharp: None,
            prepared_for: 1.0,
            sharp_failed: Vec::new(),
            owned_bytes,
            holds: 1,
        };
        let info = info_of(&entry);
        let added = view_of(owner, &entry);
        self.owned_bytes += owned_bytes;
        self.entries.push(entry);
        Ok(AcquiredFont {
            info,
            added: Some(added),
            notice: None,
        })
    }

    /// The bundled font at a size and weight. `others` are the bundled bakes
    /// other plugins hold.
    pub(crate) fn acquire_bundled(
        &mut self,
        owner: &str,
        pixel_size: f32,
        weight: FontWeight,
        bundled: &mut BundledBakes,
        others: &HashSet<BundledKey>,
    ) -> Result<AcquiredFont, String> {
        if let Some(report) = self.size_refused(pixel_size) {
            return Err(report);
        }
        let quarter_pixels = quarters(pixel_size);
        let key = Key::Bundled {
            weight,
            quarter_pixels,
        };
        if let Some(answer) = self.hold_again(&key) {
            return answer;
        }
        let what = format!(
            "the bundled font's {} weight at {pixel_size} px",
            weight.name()
        );
        if let Some(report) = self.room_refused(&what) {
            return Err(report);
        }
        let font = match bundled.acquire(weight, quarter_pixels) {
            Ok(font) => font,
            Err(why) => {
                return Err(self.once(
                    Refusal::Bundled,
                    format!("{what} could not be prepared: {why}"),
                ));
            }
        };
        // Held by no other plugin, its atlas is this plugin's to pay for.
        let cost = if others.contains(&(weight, quarter_pixels, UNSCALED)) {
            0
        } else {
            font.atlas_bytes()
        };
        let used = self.owned_bytes + self.alone_bytes(others).0;
        if used + cost > MAXIMUM_BYTES {
            return Err(self.once(
                Refusal::Budget,
                format!(
                    "{what} would take the plugin's fonts to {} bytes; the budget is {MAXIMUM_BYTES}",
                    used + cost
                ),
            ));
        }
        self.add(owner, key, font, 0)
    }

    /// A font the plugin ships, whose file it hands over. `others` are the
    /// bundled bakes other plugins hold.
    pub(crate) fn acquire_own(
        &mut self,
        owner: &str,
        name: &str,
        bytes: &[u8],
        pixel_size: f32,
        ranges: &[(u32, u32)],
        others: &HashSet<BundledKey>,
    ) -> Result<AcquiredFont, String> {
        if let Some(report) = self.size_refused(pixel_size) {
            return Err(report);
        }
        // Checked before either is copied or remembered.
        if name.len() > MAXIMUM_NAME_BYTES {
            return Err(self.once(
                Refusal::Name,
                format!(
                    "a font name of {} bytes was refused; the longest a name may be is {MAXIMUM_NAME_BYTES}",
                    name.len()
                ),
            ));
        }
        if ranges.len() > MAXIMUM_RANGES {
            return Err(self.once(
                Refusal::Ranges,
                format!(
                    "a font request listing {} ranges was refused; the most it may list is {MAXIMUM_RANGES}",
                    ranges.len()
                ),
            ));
        }
        let quarter_pixels = quarters(pixel_size);
        let ranges: Vec<(u32, u32)> = if ranges.is_empty() {
            DEFAULT_RANGES.to_vec()
        } else {
            ranges.to_vec()
        };
        let key = Key::Own {
            name: name.to_owned(),
            quarter_pixels,
            ranges: ranges.clone(),
        };
        if let Some(answer) = self.hold_again(&key) {
            return answer;
        }
        let what = format!("font '{}'", shown(name));
        if let Some(report) = self.room_refused(&what) {
            return Err(report);
        }
        if bytes.len() > MAXIMUM_FILE_BYTES {
            return Err(self.once(
                Refusal::FileSize,
                format!("{what} is larger than the {MAXIMUM_FILE_BYTES} bytes a font file may be"),
            ));
        }
        let used = self.owned_bytes + self.alone_bytes(others).0;
        if bytes.len() as u64 > MAXIMUM_BYTES.saturating_sub(used) {
            return Err(self.once(
                Refusal::Budget,
                format!(
                    "{what} would take the plugin's fonts past the budget of {MAXIMUM_BYTES} bytes"
                ),
            ));
        }
        let size = quarter_pixels as f32 / 4.0;
        let file = FontFile::Own(Arc::new(bytes.to_vec()));
        let font = match bake_guarded(file, size, &ranges, MAXIMUM_GLYPHS, u64::MAX, None) {
            Ok(font) => font,
            Err(why) => {
                return Err(self.once(
                    Refusal::Unreadable,
                    format!("{what} at {size} px could not be prepared: {why}"),
                ));
            }
        };
        let owned = bytes.len() as u64 + font.atlas_bytes();
        if used + owned > MAXIMUM_BYTES {
            return Err(self.once(
                Refusal::Budget,
                format!(
                    "{what} at {size} px would take the plugin's fonts to {} bytes; the budget is {MAXIMUM_BYTES}",
                    used + owned
                ),
            ));
        }
        let cut_short = font.kerning.cut_short;
        let mut acquired = self.add(owner, key, Arc::new(font), owned)?;
        if cut_short {
            let notice = self.once(
                Refusal::Kerning,
                format!(
                    "{what} at {size} px is kerned only in part: its kerning takes more than {MAXIMUM_KERNING_WORK} steps or {MAXIMUM_KERNING_PAIRS} pairs to read"
                ),
            );
            acquired.notice = Some(notice).filter(|n| !n.is_empty());
        }
        Ok(acquired)
    }

    /// Lets go of one hold: whether the plugin held it, and whether that was
    /// the last.
    pub(crate) fn release(&mut self, handle: u32) -> Option<bool> {
        let at = self.entries.iter().position(|e| e.handle == handle)?;
        self.entries[at].holds -= 1;
        if self.entries[at].holds > 0 {
            return Some(false);
        }
        let entry = self.entries.swap_remove(at);
        self.owned_bytes -= entry.owned_bytes;
        if !matches!(entry.key, Key::Bundled { .. }) {
            self.sharp_bytes -= entry.sharp.as_ref().map_or(0, |s| s.font.atlas_bytes());
        }
        Some(true)
    }

    /// The width of `text` in a font and its line height: nothing for a font
    /// the plugin does not hold, and for empty text.
    pub(crate) fn measure(&self, handle: u32, text: &str) -> (f32, f32) {
        match self.entries.iter().find(|e| e.handle == handle) {
            Some(entry) if !text.is_empty() => {
                (entry.font.measure_width(text), entry.font.line_height)
            }
            _ => (0.0, 0.0),
        }
    }

    /// Readies every font for a display of `density` pixels a unit: each gets a
    /// sharper bake at that multiple of its size, or the largest lower quarter
    /// step that fits, and the bake for any other density goes. A font already
    /// prepared for it is left alone. `others` are the bundled bakes other
    /// plugins hold. The views that changed, and what to log for a font drawn
    /// less sharply than it could be.
    pub(crate) fn prepare_density(
        &mut self,
        owner: &str,
        density: f32,
        bundled: &mut BundledBakes,
        others: &HashSet<BundledKey>,
    ) -> (Vec<FontView>, Vec<String>) {
        let wanted = sharp_scale(density);
        let mut changed = Vec::new();
        let mut reports = Vec::new();
        for at in 0..self.entries.len() {
            if self.entries[at].prepared_for == wanted {
                continue;
            }
            let shared = matches!(self.entries[at].key, Key::Bundled { .. });
            let held = self.entries[at]
                .sharp
                .take()
                .map_or(0, |s| s.font.atlas_bytes());
            if !shared {
                self.sharp_bytes -= held;
            }
            let used = self.sharp_bytes + self.alone_bytes(others).1;
            let room = MAXIMUM_SHARP_BYTES.saturating_sub(used);
            let outcome = sharpen(&self.entries[at], wanted, room, bundled, others);
            let entry = &mut self.entries[at];
            entry.prepared_for = wanted;
            entry.sharp = outcome.sharp;
            entry.sharp_failed.extend(outcome.failed);
            if !shared {
                self.sharp_bytes += entry.sharp.as_ref().map_or(0, |s| s.font.atlas_bytes());
            }
            let entry = &self.entries[at];
            changed.push(view_of(owner, entry));
            if let Some(why) = outcome.shortfall {
                let drawn = entry.sharp.as_ref().map_or(1.0, |s| s.scale);
                let message = format!(
                    "a font at {} px is drawn at {drawn}x on a display of {wanted}x, so it is less sharp than it could be: {why}",
                    entry.font.pixel_size
                );
                let report = self.once(Refusal::Sharp, message);
                if !report.is_empty() {
                    reports.push(report);
                }
            }
        }
        (changed, reports)
    }

    pub(crate) fn views(&self, owner: &str) -> Vec<FontView> {
        self.entries.iter().map(|e| view_of(owner, e)).collect()
    }

    pub(crate) fn handles(&self) -> Vec<u32> {
        self.entries.iter().map(|e| e.handle).collect()
    }
}

/// What making a sharper bake came to.
struct Sharpened {
    sharp: Option<SharpBake>,
    /// Why it is not at the scale wanted.
    shortfall: Option<String>,
    /// Scales that failed, with the room there was.
    failed: Vec<(u32, u64)>,
}

/// The sharper bake of `entry` at `scale`, or the largest lower step that
/// fits in `room` bytes. The file is read and checked once, and only when a
/// step is worth trying: a step whose glyphs cannot fit, or that failed
/// before with no more room than now, is passed over without baking.
fn sharpen(
    entry: &Entry,
    scale: f32,
    room: u64,
    bundled: &mut BundledBakes,
    others: &HashSet<BundledKey>,
) -> Sharpened {
    let size = entry.font.pixel_size;
    let mut out = Sharpened {
        sharp: None,
        shortfall: None,
        failed: Vec::new(),
    };
    let atlas_room = room.min(u64::from(MAXIMUM_ATLAS_SIDE) * u64::from(MAXIMUM_ATLAS_SIDE));
    let ranges = match &entry.key {
        Key::Bundled { .. } => DEFAULT_RANGES.to_vec(),
        Key::Own { ranges, .. } => ranges.clone(),
    };
    let file = &entry.font.file;
    let mut prepared = None;
    let mut step = quarter_step_below(scale.min(MAXIMUM_SHARP_PIXEL_SIZE / size));
    if step < scale {
        out.shortfall = Some(format!(
            "no sharper bake is made larger than {MAXIMUM_SHARP_PIXEL_SIZE} px"
        ));
    }
    while step > 1.0 {
        let q = quarters(step);
        let bundled_key = match &entry.key {
            Key::Bundled {
                weight,
                quarter_pixels,
            } => Some((*weight, *quarter_pixels, q)),
            Key::Own { .. } => None,
        };
        // Another plugin's bake of the bundled font is shared as it is.
        if let Some(key) = bundled_key
            && let Some(bake) = bundled.sharp.get(&key).and_then(Weak::upgrade)
            && (others.contains(&key) || bake.atlas_bytes() <= room)
        {
            out.sharp = Some(SharpBake {
                scale: step,
                font: bake,
            });
            return out;
        }
        let failed_before = entry
            .sharp_failed
            .iter()
            .any(|&(at, then)| at == q && room <= then);
        if failed_before {
            out.shortfall
                .get_or_insert_with(|| format!("it did not fit at {step}x before"));
            step -= SHARP_SCALE_STEP;
            continue;
        }
        if entry.font.least_area_at(step) > atlas_room as f64 {
            out.shortfall.get_or_insert_with(|| {
                format!(
                    "its glyphs at {} px cannot fit the {atlas_room} bytes left for sharper bakes",
                    size * step
                )
            });
            step -= SHARP_SCALE_STEP;
            continue;
        }
        let ready = prepared
            .get_or_insert_with(|| guarded(|| prepare(file.bytes(), &ranges, MAXIMUM_GLYPHS)));
        let ready = match ready {
            Ok(ready) => ready,
            Err(why) => {
                out.shortfall.get_or_insert_with(|| why.clone());
                return out;
            }
        };
        let baked = guarded(|| {
            bake_prepared(
                ready,
                file.clone(),
                size * step,
                room,
                Arc::clone(&entry.font.kerning),
            )
        });
        match baked {
            Ok(bake) => {
                let bake = Arc::new(bake);
                if let Some(key) = bundled_key {
                    bundled.sharp.retain(|_, held| held.strong_count() > 0);
                    bundled.sharp.insert(key, Arc::downgrade(&bake));
                }
                out.sharp = Some(SharpBake {
                    scale: step,
                    font: bake,
                });
                return out;
            }
            Err(why) => {
                out.failed.push((q, room));
                out.shortfall.get_or_insert(why);
            }
        }
        step -= SHARP_SCALE_STEP;
    }
    out
}

fn info_of(entry: &Entry) -> FontInfo {
    FontInfo {
        handle: entry.handle,
        pixel_size: entry.font.pixel_size,
        line_height: entry.font.line_height,
        ascent: entry.font.ascent,
    }
}

fn view_of(owner: &str, entry: &Entry) -> FontView {
    FontView {
        owner: owner.to_owned(),
        handle: entry.handle,
        font: Arc::clone(&entry.font),
        sharp: entry.sharp.clone(),
    }
}

#[cfg(test)]
mod tests;
