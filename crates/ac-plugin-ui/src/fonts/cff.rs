//! How much work a CFF font's glyphs take to draw, found by running their
//! charstrings the way the parser runs them but drawing nothing.
//!
//! A charstring may call subroutines ten deep, and each may call others many
//! times over, so the work of one glyph can grow without limit while the file
//! stays small. Which subroutine a call reaches depends on the numbers the
//! program has pushed, so the only sure measure is to run it. This follows
//! the parser's reading of the table and of each operator closely enough that
//! it never counts less than the parser would do, and stops at a budget.
//! Accented glyphs built by the deprecated `endchar` composition are refused.

use std::collections::HashMap;

use ttf_parser::GlyphId;

/// The charstring operators this reads differently from a plain number.
mod op {
    pub const HSTEM: u8 = 1;
    pub const VSTEM: u8 = 3;
    pub const VMOVETO: u8 = 4;
    pub const CALLSUBR: u8 = 10;
    pub const RETURN: u8 = 11;
    pub const ESCAPE: u8 = 12;
    pub const ENDCHAR: u8 = 14;
    pub const HSTEMHM: u8 = 18;
    pub const HINTMASK: u8 = 19;
    pub const CNTRMASK: u8 = 20;
    pub const RMOVETO: u8 = 21;
    pub const HMOVETO: u8 = 22;
    pub const VSTEMHM: u8 = 23;
    pub const SHORTINT: u8 = 28;
    pub const CALLGSUBR: u8 = 29;
    pub const FIXED: u8 = 255;
}

/// How deep subroutine calls may nest, as the parser allows.
const NESTING_LIMIT: u8 = 10;
/// The most numbers a charstring may push, as the parser allows.
const STACK_LIMIT: usize = 48;
/// The most numbers a dictionary operator reads, as the parser allows.
const DICT_OPERANDS: usize = 48;

const UNREADABLE: &str = "the font's outlines could not be read";

/// The work of drawing `glyphs` from the CFF table `table`: operators run,
/// subroutine calls and font-dictionary lookups; more than `budget` is
/// refused as soon as it is passed.
pub(super) fn charstring_work(
    table: &[u8],
    glyphs: &[GlyphId],
    budget: u64,
) -> Result<u64, String> {
    let font = Font::parse(table).ok_or(UNREADABLE)?;
    let mut run = Run {
        font: &font,
        work: 0,
        budget,
        dicts: HashMap::new(),
    };
    for glyph in glyphs {
        run.glyph(*glyph)?;
    }
    Ok(run.work)
}

/// An INDEX: a count of objects, their offsets, and their data.
#[derive(Clone, Copy, Default)]
struct Index<'a> {
    offsets: &'a [u8],
    offset_size: usize,
    data: &'a [u8],
}

impl<'a> Index<'a> {
    /// The INDEX at `at`, and where it ends.
    fn parse(table: &'a [u8], at: usize) -> Option<(Self, usize)> {
        let count = usize::from(read_u16(table, at)?);
        if count == 0 {
            return Some((Self::default(), at + 2));
        }
        let offset_size = usize::from(*table.get(at + 2)?);
        if !(1..=4).contains(&offset_size) {
            return None;
        }
        let start = at + 3;
        let end = start.checked_add((count + 1) * offset_size)?;
        let mut index = Self {
            offsets: table.get(start..end)?,
            offset_size,
            data: &[],
        };
        let Some(last) = index.offset(count) else {
            return Some((Self::default(), end));
        };
        index.data = table.get(end..end.checked_add(last)?)?;
        Some((index, end + last))
    }

    /// Where an INDEX at `at` ends, without reading its data.
    fn skip(table: &'a [u8], at: usize) -> Option<usize> {
        let count = usize::from(read_u16(table, at)?);
        if count == 0 {
            return Some(at + 2);
        }
        let offset_size = usize::from(*table.get(at + 2)?);
        if !(1..=4).contains(&offset_size) {
            return None;
        }
        let start = at + 3;
        let end = start.checked_add((count + 1) * offset_size)?;
        let index = Self {
            offsets: table.get(start..end)?,
            offset_size,
            data: &[],
        };
        Some(end + index.offset(count).unwrap_or(0))
    }

    fn offset(&self, at: usize) -> Option<usize> {
        if self.offset_size == 0 || at >= self.offsets.len() / self.offset_size {
            return None;
        }
        let bytes = self
            .offsets
            .get(at * self.offset_size..(at + 1) * self.offset_size)?;
        let n = bytes.iter().fold(0usize, |n, b| (n << 8) | usize::from(*b));
        n.checked_sub(1)
    }

    fn len(&self) -> usize {
        (self.offsets.len() / self.offset_size.max(1)).saturating_sub(1)
    }

    fn get(&self, at: usize) -> Option<&'a [u8]> {
        let start = self.offset(at)?;
        let end = self.offset(at.checked_add(1)?)?;
        self.data.get(start..end)
    }
}

fn read_u16(data: &[u8], at: usize) -> Option<u16> {
    Some(u16::from_be_bytes(
        data.get(at..at.checked_add(2)?)?.try_into().ok()?,
    ))
}

/// A dictionary: operands, then the operator they belong to.
struct Dict<'a> {
    data: &'a [u8],
    at: usize,
    operands_at: usize,
}

impl<'a> Dict<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self {
            data,
            at: 0,
            operands_at: 0,
        }
    }

    fn is_operator(b: u8) -> bool {
        matches!(b, 0..=27 | 31 | 255)
    }

    /// The next operator; two-byte operators are 1200 and up.
    fn next_operator(&mut self) -> Option<u16> {
        self.operands_at = self.at;
        let mut at = self.at;
        while at < self.data.len() {
            let b = self.data[at];
            at += 1;
            if Self::is_operator(b) {
                let mut operator = u16::from(b);
                if b == 12 {
                    operator = 1200 + u16::from(*self.data.get(at)?);
                    at += 1;
                }
                self.at = at;
                return Some(operator);
            }
            at = skip_number(self.data, b, at)?;
        }
        None
    }

    /// The current operator's operands.
    fn operands(&self) -> Option<Vec<f64>> {
        let mut operands = Vec::new();
        let mut at = self.operands_at;
        while at < self.data.len() {
            let b = self.data[at];
            at += 1;
            if Self::is_operator(b) {
                break;
            }
            let (n, next) = parse_number(self.data, b, at)?;
            at = next;
            operands.push(n);
            if operands.len() >= DICT_OPERANDS {
                break;
            }
        }
        Some(operands)
    }

    fn offset(&self) -> Option<usize> {
        match self.operands()?.as_slice() {
            [n] => usize::try_from(*n as i32).ok(),
            _ => None,
        }
    }

    fn range(&self) -> Option<std::ops::Range<usize>> {
        match self.operands()?.as_slice() {
            [len, start] => {
                let len = usize::try_from(*len as i32).ok()?;
                let start = usize::try_from(*start as i32).ok()?;
                Some(start..start.checked_add(len)?)
            }
            _ => None,
        }
    }
}

/// Where a dictionary number that starts with `b0` ends.
fn skip_number(data: &[u8], b0: u8, at: usize) -> Option<usize> {
    Some(match b0 {
        28 => at + 2,
        29 => at + 4,
        30 => {
            let mut at = at;
            while at < data.len() {
                let b = data[at];
                at += 1;
                if b >> 4 == 0xF || b & 0xF == 0xF {
                    break;
                }
            }
            at
        }
        32..=246 => at,
        247..=254 => at + 1,
        _ => return None,
    })
}

/// A dictionary number that starts with `b0`, and where it ends.
fn parse_number(data: &[u8], b0: u8, at: usize) -> Option<(f64, usize)> {
    let byte = |at: usize| data.get(at).map(|b| i32::from(*b));
    Some(match b0 {
        28 => (
            f64::from(i16::from_be_bytes(data.get(at..at + 2)?.try_into().ok()?)),
            at + 2,
        ),
        29 => (
            f64::from(i32::from_be_bytes(data.get(at..at + 4)?.try_into().ok()?)),
            at + 4,
        ),
        30 => parse_real(data, at)?,
        32..=246 => (f64::from(i32::from(b0) - 139), at),
        247..=250 => (
            f64::from((i32::from(b0) - 247) * 256 + byte(at)? + 108),
            at + 1,
        ),
        251..=254 => (
            f64::from(-(i32::from(b0) - 251) * 256 - byte(at)? - 108),
            at + 1,
        ),
        _ => return None,
    })
}

/// A real number written in nibbles.
fn parse_real(data: &[u8], mut at: usize) -> Option<(f64, usize)> {
    const LIMIT: usize = 64;
    let mut text = String::new();
    'read: loop {
        let b = *data.get(at)?;
        at += 1;
        for nibble in [b >> 4, b & 0xF] {
            if nibble == 0xF {
                break 'read;
            }
            if text.len() >= LIMIT {
                return None;
            }
            match nibble {
                0..=9 => text.push(char::from(b'0' + nibble)),
                10 => text.push('.'),
                11 => text.push('E'),
                12 => {
                    if text.len() + 1 >= LIMIT {
                        return None;
                    }
                    text.push_str("E-");
                }
                14 => text.push('-'),
                _ => return None,
            }
        }
    }
    Some((text.parse().ok()?, at))
}

/// Which font dictionary each glyph of a CID-keyed font uses.
enum FdSelect<'a> {
    /// One byte a glyph.
    Each(&'a [u8]),
    /// Ranges of glyphs.
    Ranges(&'a [u8]),
}

/// What a font's local subroutines are.
enum Kind<'a> {
    /// One set for every glyph.
    Simple(Index<'a>),
    /// Each glyph's font dictionary names its own.
    Keyed {
        fd_array: Index<'a>,
        fd_select: FdSelect<'a>,
    },
}

struct Font<'a> {
    table: &'a [u8],
    global: Index<'a>,
    char_strings: Index<'a>,
    kind: Kind<'a>,
}

impl<'a> Font<'a> {
    fn parse(table: &'a [u8]) -> Option<Self> {
        if *table.first()? != 1 {
            return None;
        }
        let header = usize::from(*table.get(2)?).max(4);
        let at = Index::skip(table, header)?;
        let (top, at) = Index::parse(table, at)?;
        let top = top.get(0)?;

        let mut char_strings = 0;
        let mut private = None;
        let mut keyed = false;
        let mut fd_array = None;
        let mut fd_select = None;
        let mut dict = Dict::new(top);
        while let Some(operator) = dict.next_operator() {
            match operator {
                17 => char_strings = dict.offset()?,
                18 => private = dict.range(),
                1230 => keyed = true,
                1236 => fd_array = dict.offset(),
                1237 => fd_select = dict.offset(),
                _ => {}
            }
        }
        if char_strings == 0 {
            return None;
        }
        let at = Index::skip(table, at)?;
        let (global, _) = Index::parse(table, at)?;
        let (char_strings, _) = Index::parse(table, char_strings)?;
        let glyphs = char_strings.len();

        let kind = if keyed {
            let (fd_array, _) = Index::parse(table, fd_array?)?;
            let at = fd_select?;
            let fd_select = match *table.get(at)? {
                0 => FdSelect::Each(table.get(at + 1..at + 1 + glyphs)?),
                3 => FdSelect::Ranges(table.get(at + 1..)?),
                _ => return None,
            };
            Kind::Keyed {
                fd_array,
                fd_select,
            }
        } else {
            let subrs = match private {
                Some(range) => local_subrs(table, range)?.unwrap_or_default(),
                None => Index::default(),
            };
            Kind::Simple(subrs)
        };
        Some(Self {
            table,
            global,
            char_strings,
            kind,
        })
    }
}

/// The local subroutines a private dictionary at `range` names: none when it
/// names none; unreadable when it names some that cannot be read.
fn local_subrs(table: &[u8], range: std::ops::Range<usize>) -> Option<Option<Index<'_>>> {
    let private = table.get(range.clone())?;
    let mut offset = None;
    let mut dict = Dict::new(private);
    while let Some(operator) = dict.next_operator() {
        if operator == 19 {
            offset = dict.offset();
        }
    }
    let Some(offset) = offset else {
        return Some(None);
    };
    let start = range.start.checked_add(offset)?;
    table.get(start..)?;
    Index::parse(table, start).map(|(index, _)| Some(index))
}

/// One glyph being run.
struct Glyph<'a> {
    id: GlyphId,
    stack: Vec<f32>,
    width: bool,
    stems: u32,
    ended: bool,
    local: Option<Index<'a>>,
}

/// Why a charstring stopped early: the parser would stop there too, or the
/// whole font is refused.
enum Stop {
    /// The parser gives up on this glyph; what was run is counted.
    Glyph,
    /// The font is refused.
    Font(String),
}

struct Run<'a> {
    font: &'a Font<'a>,
    work: u64,
    budget: u64,
    /// Each font dictionary of a CID-keyed font, read once: the bytes the
    /// parser reads to find its local subroutines, and those subroutines.
    dicts: HashMap<u8, (u64, Option<Index<'a>>)>,
}

impl<'a> Run<'a> {
    fn glyph(&mut self, id: GlyphId) -> Result<(), String> {
        let Some(program) = self.font.char_strings.get(usize::from(id.0)) else {
            return Ok(());
        };
        let local = match &self.font.kind {
            Kind::Simple(subrs) => Some(*subrs),
            Kind::Keyed { .. } => None,
        };
        let mut glyph = Glyph {
            id,
            stack: Vec::with_capacity(STACK_LIMIT),
            width: false,
            stems: 0,
            ended: false,
            local,
        };
        match self.run(&mut glyph, program, 0) {
            Ok(()) | Err(Stop::Glyph) => Ok(()),
            Err(Stop::Font(why)) => Err(why),
        }
    }

    fn spend(&mut self, work: u64) -> Result<(), Stop> {
        self.work = self.work.saturating_add(work);
        if self.work > self.budget {
            return Err(Stop::Font(
                "the font's outlines are too complex to draw".to_owned(),
            ));
        }
        Ok(())
    }

    fn push(glyph: &mut Glyph<'_>, n: f32) -> Result<(), Stop> {
        if glyph.stack.len() >= STACK_LIMIT {
            return Err(Stop::Glyph);
        }
        glyph.stack.push(n);
        Ok(())
    }

    /// The stem hints before a mask or a drawing operator: an odd count
    /// carries the width first.
    fn stems(glyph: &mut Glyph<'_>) {
        let mut len = glyph.stack.len();
        if len % 2 == 1 && !glyph.width {
            glyph.width = true;
            len -= 1;
        }
        glyph.stems = glyph.stems.saturating_add((len >> 1) as u32);
        glyph.stack.clear();
    }

    fn run(&mut self, glyph: &mut Glyph<'a>, program: &[u8], depth: u8) -> Result<(), Stop> {
        let mut at = 0;
        while at < program.len() {
            self.spend(1)?;
            let b = program[at];
            at += 1;
            match b {
                0 | 2 | 9 | 13 | 15..=17 => return Err(Stop::Glyph),
                op::HSTEM | op::VSTEM | op::HSTEMHM | op::VSTEMHM => Self::stems(glyph),
                op::HINTMASK | op::CNTRMASK => {
                    let mut len = glyph.stack.len();
                    glyph.stack.clear();
                    if len % 2 == 1 {
                        len -= 1;
                        glyph.width = true;
                    }
                    glyph.stems = glyph.stems.saturating_add((len >> 1) as u32);
                    at = at.saturating_add(((glyph.stems + 7) >> 3) as usize);
                }
                op::RMOVETO => {
                    if glyph.stack.len() == 3 {
                        glyph.width = true;
                    }
                    glyph.stack.clear();
                }
                op::HMOVETO | op::VMOVETO => {
                    if glyph.stack.len() == 2 {
                        glyph.width = true;
                    }
                    glyph.stack.clear();
                }
                op::CALLSUBR | op::CALLGSUBR => {
                    let Some(index) = glyph.stack.pop() else {
                        return Err(Stop::Glyph);
                    };
                    if depth == NESTING_LIMIT {
                        return Err(Stop::Glyph);
                    }
                    let subrs = if b == op::CALLGSUBR {
                        self.font.global
                    } else {
                        self.local_subrs(glyph)?.ok_or(Stop::Glyph)?
                    };
                    let called = subroutine(&subrs, index).ok_or(Stop::Glyph)?;
                    self.run(glyph, called, depth + 1)?;
                    if glyph.ended {
                        if at < program.len() {
                            return Err(Stop::Glyph);
                        }
                        break;
                    }
                }
                op::RETURN => break,
                op::ESCAPE => {
                    let Some(&second) = program.get(at) else {
                        return Err(Stop::Glyph);
                    };
                    at += 1;
                    // The four flex operators draw and clear; nothing else is
                    // read.
                    if !(34..=37).contains(&second) {
                        return Err(Stop::Glyph);
                    }
                    glyph.stack.clear();
                }
                op::ENDCHAR => {
                    let len = glyph.stack.len();
                    if len == 4 || (len == 5 && !glyph.width) {
                        return Err(Stop::Font(
                            "the font builds accented glyphs from others, which is not supported"
                                .to_owned(),
                        ));
                    }
                    if at < program.len() {
                        return Err(Stop::Glyph);
                    }
                    glyph.ended = true;
                    break;
                }
                op::SHORTINT => {
                    let bytes = program.get(at..at + 2).ok_or(Stop::Glyph)?;
                    at += 2;
                    Self::push(glyph, f32::from(i16::from_be_bytes([bytes[0], bytes[1]])))?;
                }
                32..=246 => Self::push(glyph, f32::from(i16::from(b) - 139))?,
                247..=250 => {
                    let b1 = *program.get(at).ok_or(Stop::Glyph)?;
                    at += 1;
                    let n = (i16::from(b) - 247) * 256 + i16::from(b1) + 108;
                    Self::push(glyph, f32::from(n))?;
                }
                251..=254 => {
                    let b1 = *program.get(at).ok_or(Stop::Glyph)?;
                    at += 1;
                    let n = -(i16::from(b) - 251) * 256 - i16::from(b1) - 108;
                    Self::push(glyph, f32::from(n))?;
                }
                op::FIXED => {
                    let bytes = program.get(at..at + 4).ok_or(Stop::Glyph)?;
                    at += 4;
                    let n = i32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
                    Self::push(glyph, n as f32 / 65536.0)?;
                }
                // Every other operator draws with what is pushed and clears it.
                _ => glyph.stack.clear(),
            }
        }
        Ok(())
    }

    /// The local subroutines of the glyph: a CID-keyed font's are found
    /// through its font dictionary the first time one is called. The parser
    /// reads that dictionary and its private dictionary again for every
    /// glyph, so every glyph pays for the bytes, though they are read here
    /// once.
    fn local_subrs(&mut self, glyph: &mut Glyph<'a>) -> Result<Option<Index<'a>>, Stop> {
        if glyph.local.is_none()
            && let Kind::Keyed {
                fd_array,
                fd_select,
            } = &self.font.kind
        {
            let (dict, walked) = font_dict_index(fd_select, glyph.id);
            self.spend(walked)?;
            let Some(dict) = dict else {
                return Ok(None);
            };
            let table = self.font.table;
            let (read, subrs) = *self.dicts.entry(dict).or_insert_with(|| {
                let Some(font_dict) = fd_array.get(usize::from(dict)) else {
                    return (0, None);
                };
                let range = private_range(font_dict);
                let private = range.clone().map_or(0, |r| {
                    r.end
                        .min(table.len())
                        .saturating_sub(r.start.min(table.len()))
                });
                let subrs = range.and_then(|r| local_subrs(table, r)).flatten();
                ((font_dict.len() + private) as u64, subrs)
            });
            self.spend(read)?;
            glyph.local = subrs;
        }
        Ok(glyph.local)
    }
}
/// The subroutine a call's number reaches, with the bias the count sets.
fn subroutine<'a>(subrs: &Index<'a>, n: f32) -> Option<&'a [u8]> {
    let len = subrs.len();
    let bias = if len < 1240 {
        107
    } else if len < 33900 {
        1131
    } else {
        32768
    };
    if !(n >= i32::MIN as f32 && n < i32::MAX as f32) {
        return None;
    }
    let index = (n as i32).checked_add(bias)?;
    subrs.get(usize::try_from(index).ok()?)
}

/// The private dictionary a font dictionary names.
fn private_range(font_dict: &[u8]) -> Option<std::ops::Range<usize>> {
    let mut dict = Dict::new(font_dict);
    while let Some(operator) = dict.next_operator() {
        if operator == 18 {
            return dict.range();
        }
    }
    None
}

/// Which font dictionary a glyph uses, and how many ranges were walked to
/// find it.
fn font_dict_index(select: &FdSelect<'_>, glyph: GlyphId) -> (Option<u8>, u64) {
    match select {
        FdSelect::Each(bytes) => (bytes.get(usize::from(glyph.0)).copied(), 1),
        FdSelect::Ranges(data) => {
            let Some(ranges) = read_u16(data, 0).filter(|n| *n > 0) else {
                return (None, 1);
            };
            let first = |at: usize| read_u16(data, at);
            let Some(mut start) = first(2) else {
                return (None, 1);
            };
            let Some(mut index) = data.get(4).copied() else {
                return (None, 1);
            };
            let mut at = 5;
            for walked in 1..=u64::from(ranges) {
                let Some(next) = first(at) else {
                    return (None, walked);
                };
                if (start..next).contains(&glyph.0) {
                    return (Some(index), walked);
                }
                let Some(i) = data.get(at + 2).copied() else {
                    return (None, walked);
                };
                index = i;
                start = next;
                at += 3;
            }
            (None, u64::from(ranges))
        }
    }
}
