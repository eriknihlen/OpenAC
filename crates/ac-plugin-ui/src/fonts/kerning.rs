//! A font's kerning, read once when it is baked: every pair of the glyphs it
//! baked that the font moves closer or apart, so laying text out is a lookup
//! and never reads the font.
//!
//! OpenType kerning is the `kern` feature's pair adjustments in `GPOS`: each
//! lookup the feature names applies once, in lookup order, and the first
//! subtable of a lookup that knows a pair gives its value. A font with no
//! such kerning may have the older `kern` table instead, whose first subtable
//! that knows a pair gives its value. The work is bounded: a lookup the
//! feature names twice (or two that are one table), or a subtable a lookup
//! lists twice, is read once; reading stops after [`MAXIMUM_KERNING_WORK`]
//! steps, keeping what it found; and no more than [`MAXIMUM_KERNING_PAIRS`]
//! pairs are kept. A font cut short by a limit says so.

use std::collections::{HashMap, HashSet};

use ttf_parser::gpos::{ClassMatrix, PairAdjustment, PairSet, PositioningSubtable};
use ttf_parser::kern::Format as KernFormat;
use ttf_parser::opentype_layout::ClassDefinition;
use ttf_parser::{Face, GlyphId, Tag};

/// The most steps reading one font's kerning may take: pairs looked up and
/// records read. The bundled font's default set takes about six hundred
/// thousand.
pub const MAXIMUM_KERNING_WORK: u64 = 16_000_000;

/// The most pairs one font kerns; more are left out. The bundled font's
/// default set kerns about fifteen thousand.
pub const MAXIMUM_KERNING_PAIRS: usize = 131_072;

/// The pairs a font kerns, font units, none of them zero.
#[derive(Debug, Default)]
pub(crate) struct Kerning {
    pairs: HashMap<(u16, u16), i32>,
    /// Reading stopped at a limit, so some pairs may be missing.
    pub(crate) cut_short: bool,
}

impl Kerning {
    /// How far a pair is kerned, font units.
    #[must_use]
    pub(crate) fn get(&self, left: u16, right: u16) -> i32 {
        self.pairs.get(&(left, right)).copied().unwrap_or(0)
    }
}

/// The steps taken, and whether the budget ran out.
struct Budget {
    left: u64,
}

impl Budget {
    /// Takes a step; false once there are none left.
    fn step(&mut self) -> bool {
        if self.left == 0 {
            return false;
        }
        self.left -= 1;
        true
    }
}

/// Which glyphs were baked, as a set over every glyph number.
struct Baked(Vec<u64>);

impl Baked {
    fn new(glyphs: &[GlyphId]) -> Self {
        let mut bits = vec![0u64; 1024];
        for g in glyphs {
            bits[usize::from(g.0 >> 6)] |= 1 << (g.0 & 63);
        }
        Self(bits)
    }

    fn has(&self, g: GlyphId) -> bool {
        self.0[usize::from(g.0 >> 6)] & (1 << (g.0 & 63)) != 0
    }
}

/// The kerning among `glyphs` (each once).
pub(super) fn read(face: &Face<'_>, glyphs: &[GlyphId]) -> Kerning {
    let mut budget = Budget {
        left: MAXIMUM_KERNING_WORK,
    };
    let mut pairs = HashMap::new();
    if !from_gpos(face, glyphs, &mut budget, &mut pairs) {
        from_kern_table(face, glyphs, &mut budget, &mut pairs);
    }
    let cut_short = budget.left == 0 || pairs.len() >= MAXIMUM_KERNING_PAIRS;
    pairs.retain(|_, v| *v != 0);
    pairs.shrink_to_fit();
    Kerning { pairs, cut_short }
}

/// One pair adjustment subtable as it applies to one first glyph.
enum Rule<'a> {
    /// The pairs it lists for the glyph.
    Pairs(PairSet<'a>),
    /// The row of its class matrix for the glyph's class, and the class of
    /// each glyph as a second glyph.
    Classes {
        first: u16,
        second: &'a [u16],
        matrix: ClassMatrix<'a>,
    },
}

/// The `GPOS` kerning: false when the font has none, so the `kern` table is
/// read instead.
fn from_gpos(
    face: &Face<'_>,
    glyphs: &[GlyphId],
    budget: &mut Budget,
    pairs: &mut HashMap<(u16, u16), i32>,
) -> bool {
    let Some(gpos) = face.tables().gpos else {
        return false;
    };
    let kern = Tag::from_bytes(b"kern");
    // The lookups named, as a set over every lookup number: listed in order,
    // each once.
    let mut named = vec![0u64; 1024];
    for feature in gpos.features {
        if !budget.step() {
            break;
        }
        if feature.tag != kern {
            continue;
        }
        // By number: the parser's own walk of a list overflows at its
        // longest (65,535 entries).
        let indices = feature.lookup_indices;
        for at in 0..indices.len() {
            if !budget.step() {
                break;
            }
            let Some(index) = indices.get(at) else {
                continue;
            };
            named[usize::from(index >> 6)] |= 1 << (index & 63);
        }
    }
    let lookups: Vec<u16> = (0..=u16::MAX)
        .filter(|i| named[usize::from(i >> 6)] & (1 << (i & 63)) != 0)
        .collect();
    if lookups.is_empty() {
        return false;
    }
    let raw = face
        .raw_face()
        .table(Tag::from_bytes(b"GPOS"))
        .unwrap_or(&[]);
    let mut seen_lookups = HashSet::new();
    for index in lookups {
        let Some(lookup) = gpos.lookups.get(index) else {
            continue;
        };
        let places = Places::of(raw, index);
        // Two of the feature's lookups that are one table are one lookup.
        if let Some(place) = places.as_ref().map(|p| p.lookup)
            && !seen_lookups.insert(place)
        {
            continue;
        }
        let mut subtables = Vec::new();
        let mut seen = HashSet::new();
        for at in 0..lookup.subtables.len() {
            if !budget.step() {
                return true;
            }
            // A subtable the lookup lists again adds nothing: the first
            // already answered for every pair it knows. Another lookup that
            // lists it applies it again.
            if let Some(place) = places.as_ref().and_then(|p| p.subtable(at))
                && !seen.insert(place)
            {
                continue;
            }
            if let Some(PositioningSubtable::Pair(pair)) =
                lookup.subtables.get::<PositioningSubtable>(at)
            {
                subtables.push(pair);
            }
        }
        if !apply_lookup(&subtables, glyphs, budget, pairs) {
            return true;
        }
    }
    true
}

/// Applies one lookup's pair subtables to every pair of `glyphs`; false once
/// the budget runs out.
fn apply_lookup(
    subtables: &[PairAdjustment<'_>],
    glyphs: &[GlyphId],
    budget: &mut Budget,
    pairs: &mut HashMap<(u16, u16), i32>,
) -> bool {
    if subtables.is_empty() {
        return true;
    }
    // Each glyph's class as a second glyph, once per class-based subtable.
    let mut seconds: Vec<Vec<u16>> = Vec::new();
    for subtable in subtables {
        let classes = match subtable {
            PairAdjustment::Format2 { classes, .. } => second_classes(&classes.1, glyphs, budget),
            PairAdjustment::Format1 { .. } => Some(Vec::new()),
        };
        let Some(classes) = classes else {
            return false;
        };
        seconds.push(classes);
    }
    for &left in glyphs {
        let mut rules = Vec::new();
        for (subtable, second) in subtables.iter().zip(&seconds) {
            if !budget.step() {
                return false;
            }
            match *subtable {
                PairAdjustment::Format1 { coverage, sets } => {
                    if let Some(set) = coverage.get(left).and_then(|i| sets.get(i)) {
                        rules.push(Rule::Pairs(set));
                    }
                }
                PairAdjustment::Format2 {
                    coverage,
                    classes,
                    matrix,
                } => {
                    if coverage.contains(left) {
                        rules.push(Rule::Classes {
                            first: classes.0.get(left),
                            second,
                            matrix,
                        });
                    }
                }
            }
        }
        if rules.is_empty() {
            continue;
        }
        for (at, &right) in glyphs.iter().enumerate() {
            for rule in &rules {
                if !budget.step() {
                    return false;
                }
                let found = match rule {
                    Rule::Pairs(set) => set.get(right).map(|(first, _)| first.x_advance),
                    Rule::Classes {
                        first,
                        second,
                        matrix,
                    } => matrix
                        .get((*first, second[at]))
                        .map(|(first, _)| first.x_advance),
                };
                if let Some(units) = found {
                    // Zero moves nothing; the pair is answered all the same.
                    if units != 0 {
                        add(pairs, (left.0, right.0), i32::from(units));
                    }
                    break;
                }
            }
        }
    }
    true
}

/// Each glyph's class in a class definition; none once the budget runs out.
fn second_classes(
    classes: &ClassDefinition<'_>,
    glyphs: &[GlyphId],
    budget: &mut Budget,
) -> Option<Vec<u16>> {
    glyphs
        .iter()
        .map(|g| budget.step().then(|| classes.get(*g)))
        .collect()
}

/// Adds to a pair's kerning; a new pair past [`MAXIMUM_KERNING_PAIRS`] is
/// left out.
fn add(pairs: &mut HashMap<(u16, u16), i32>, pair: (u16, u16), units: i32) {
    if let Some(sum) = pairs.get_mut(&pair) {
        *sum += units;
    } else if pairs.len() < MAXIMUM_KERNING_PAIRS {
        pairs.insert(pair, units);
    }
}

/// Where a lookup's subtables start in the `GPOS` table, through an
/// extension when they have one, so one listed twice is read once.
struct Places<'a> {
    gpos: &'a [u8],
    lookup: usize,
    extension: bool,
}

impl<'a> Places<'a> {
    fn of(gpos: &'a [u8], index: u16) -> Option<Self> {
        let list = word(gpos, 8)?;
        let lookup = list + word(gpos, list + 2 + 2 * usize::from(index))?;
        Some(Self {
            gpos,
            lookup,
            extension: word(gpos, lookup)? == 9,
        })
    }

    fn subtable(&self, at: u16) -> Option<usize> {
        let place = self.lookup + word(self.gpos, self.lookup + 6 + 2 * usize::from(at))?;
        if !self.extension {
            return Some(place);
        }
        let offset = self.gpos.get(place + 4..place + 8)?;
        Some(place + u32::from_be_bytes(offset.try_into().ok()?) as usize)
    }
}

fn word(data: &[u8], at: usize) -> Option<usize> {
    Some(usize::from(u16::from_be_bytes(
        data.get(at..at.checked_add(2)?)?.try_into().ok()?,
    )))
}
/// The older `kern` table: horizontal subtables that are not variable, the
/// first that knows a pair giving its value.
fn from_kern_table(
    face: &Face<'_>,
    glyphs: &[GlyphId],
    budget: &mut Budget,
    pairs: &mut HashMap<(u16, u16), i32>,
) {
    let Some(kern) = face.tables().kern else {
        return;
    };
    let baked = Baked::new(glyphs);
    // Pairs a subtable answered, even with nothing: later ones do not.
    let mut known: HashSet<(u16, u16)> = HashSet::new();
    for subtable in kern.subtables {
        if !budget.step() {
            return;
        }
        if !subtable.horizontal || subtable.variable {
            continue;
        }
        if let KernFormat::Format0(table) = &subtable.format {
            for at in 0..table.pairs.len() {
                if !budget.step() {
                    return;
                }
                let Some(pair) = table.pairs.get(at) else {
                    continue;
                };
                let (left, right) = (pair.left(), pair.right());
                if baked.has(left) && baked.has(right) && known.insert((left.0, right.0)) {
                    add(pairs, (left.0, right.0), i32::from(pair.value));
                }
            }
            continue;
        }
        for &left in glyphs {
            for &right in glyphs {
                if !budget.step() {
                    return;
                }
                if known.contains(&(left.0, right.0)) {
                    continue;
                }
                // Only a pair it moves is answered here, so what is remembered
                // stays as small as what is kerned.
                if let Some(units) = subtable.glyphs_kerning(left, right).filter(|u| *u != 0) {
                    known.insert((left.0, right.0));
                    add(pairs, (left.0, right.0), i32::from(units));
                }
            }
        }
    }
}
