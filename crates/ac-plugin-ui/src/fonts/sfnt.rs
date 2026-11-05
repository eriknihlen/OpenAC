//! The copy of a font the rasterizer reads, and the character map lookups a
//! request is answered from.
//!
//! The rasterizer outlines every glyph a font's character map names, and
//! walks every code point of every subtable to find them, before it is asked
//! for a single glyph. A plugin's font could name a great many, so it is given
//! a copy instead: the same outline and metric tables, byte for byte, and a
//! character map that names only the characters being baked. The glyphs keep
//! their numbers, so kerning and advances read from the original agree.

use ttf_parser::cmap::{Format, Subtable};
use ttf_parser::{Face, GlyphId, Tag};

/// The most character map subtables a lookup consults, in the font's order.
/// Fonts have one to four; more is no font a plugin needs.
const MAXIMUM_LOOKUP_SUBTABLES: usize = 8;

/// The tables the rasterizer reads, copied as they are.
const RASTER_TABLES: [&[u8; 4]; 7] = [
    b"CFF ", b"glyf", b"head", b"hhea", b"hmtx", b"loca", b"maxp",
];

/// The font's Unicode subtables a character is looked up in, in its order:
/// each lookup is a search, never a walk. The many-to-one form, which can
/// only be walked, is not read.
pub(super) fn lookup_subtables<'a>(face: &Face<'a>) -> Vec<Subtable<'a>> {
    let Some(cmap) = face.tables().cmap else {
        return Vec::new();
    };
    cmap.subtables
        .into_iter()
        .filter(|s| s.is_unicode() && !matches!(s.format, Format::ManyToOneRangeMappings(_)))
        .take(MAXIMUM_LOOKUP_SUBTABLES)
        .collect()
}

/// The glyph for a character: the first subtable that names it.
pub(super) fn glyph_index(subtables: &[Subtable<'_>], c: char) -> Option<GlyphId> {
    subtables.iter().find_map(|s| s.glyph_index(u32::from(c)))
}

/// The copy of the font in `bytes` the rasterizer reads: its outline and
/// metric tables as the parser found them, and a character map naming only
/// `characters` (sorted by character, each once).
pub(super) fn raster_copy(
    face: &Face<'_>,
    bytes: &[u8],
    characters: &[(char, GlyphId)],
) -> Result<Vec<u8>, String> {
    let mut tables: Vec<([u8; 4], Vec<u8>)> = RASTER_TABLES
        .iter()
        .filter_map(|tag| {
            let data = face.raw_face().table(Tag::from_bytes(tag))?;
            Some((**tag, data.to_vec()))
        })
        .collect();
    tables.push((*b"cmap", character_map(characters)));
    // The parser finds a table by a binary search of the sorted directory.
    tables.sort_by_key(|(tag, _)| *tag);

    let count = tables.len() as u16;
    let mut out = Vec::new();
    out.extend_from_slice(
        bytes
            .get(0..4)
            .ok_or("the file is not a TrueType or OpenType font")?,
    );
    let power = 1u16 << (15 - count.leading_zeros().min(15));
    let search_range = power * 16;
    out.extend_from_slice(&count.to_be_bytes());
    out.extend_from_slice(&search_range.to_be_bytes());
    out.extend_from_slice(&(power.trailing_zeros() as u16).to_be_bytes());
    out.extend_from_slice(&(count * 16 - search_range).to_be_bytes());
    let mut offset = 12 + 16 * tables.len();
    for (tag, data) in &tables {
        out.extend_from_slice(tag);
        out.extend_from_slice(&[0; 4]);
        out.extend_from_slice(&(offset as u32).to_be_bytes());
        out.extend_from_slice(&(data.len() as u32).to_be_bytes());
        offset += data.len().next_multiple_of(4);
    }
    for (_, data) in &tables {
        out.extend_from_slice(data);
        out.resize(out.len().next_multiple_of(4), 0);
    }
    Ok(out)
}

/// A character map with one Unicode subtable (segmented coverage), a group
/// for each character.
pub(super) fn character_map(characters: &[(char, GlyphId)]) -> Vec<u8> {
    let groups = characters.len() as u32;
    let mut out = Vec::with_capacity(28 + 12 * characters.len());
    // Version, one subtable: Windows, full Unicode, at offset 12.
    for word in [0u16, 1, 3, 10] {
        out.extend_from_slice(&word.to_be_bytes());
    }
    out.extend_from_slice(&12u32.to_be_bytes());
    out.extend_from_slice(&12u16.to_be_bytes());
    out.extend_from_slice(&0u16.to_be_bytes());
    out.extend_from_slice(&(16 + 12 * groups).to_be_bytes());
    out.extend_from_slice(&0u32.to_be_bytes());
    out.extend_from_slice(&groups.to_be_bytes());
    for (c, glyph) in characters {
        let code = u32::from(*c);
        out.extend_from_slice(&code.to_be_bytes());
        out.extend_from_slice(&code.to_be_bytes());
        out.extend_from_slice(&u32::from(glyph.0).to_be_bytes());
    }
    out
}
