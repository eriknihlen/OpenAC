//! Chat-line markup: the server and client embed tags such as
//! `<Tell:IIDString:1342177290:Dww>Dww<\Tell>` in a line so the speaker's
//! name can be clicked. A tag opens with a well-formed `<TYPE:FORMAT:DATA>`
//! whose type and format are names in the tag table; any bracketed text
//! closes it. Tags are hidden; other bracketed text is shown as written
//! ([`parse`]). A plugin's own line has its own link reading
//! ([`parse_links`]).

use crate::text::parse_float_f64;

/// An open tag: its type, its data format and its payload.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ChatTextTag {
    /// The tag type, e.g. `Tell`.
    pub tag_type: String,
    /// The payload format, e.g. `IIDString`.
    pub format: String,
    /// The payload; may itself contain colons.
    pub data: String,
}

impl ChatTextTag {
    /// For a `COORD:World:…` tag, the map coordinates it names as
    /// `(east_west, north_south)`, east and north positive.
    #[must_use]
    pub fn coordinate(&self) -> Option<(f64, f64)> {
        if !self.tag_type.eq_ignore_ascii_case("COORD")
            || !self.format.eq_ignore_ascii_case("World")
        {
            return None;
        }
        parse_coordinate_pair(&self.data)
    }

    /// Whether this is a tell tag, the kind that marks a speaker's name. It
    /// is the only kind drawn in the tag colour, and the only kind a click
    /// acts on.
    #[must_use]
    pub fn is_tell(&self) -> bool {
        self.tag_type.eq_ignore_ascii_case(TELL_TYPE_NAME)
    }

    /// For an `IIDString` payload (`<id>:<name>`, the format named in any
    /// case), the object id and name, neither empty. The name runs to the
    /// end of the data, colons and all; the id is read as
    /// [`parse_tag_number`] reads it.
    #[must_use]
    pub fn iid_string(&self) -> Option<(u32, &str)> {
        if !self.format.eq_ignore_ascii_case("IIDString") {
            return None;
        }
        let split = self.data.find(':')?;
        if split == 0 || split == self.data.len() - 1 {
            return None;
        }
        let object_id = parse_tag_number(&self.data[..split])?;
        Some((object_id, &self.data[split + 1..]))
    }
}

/// Reads `"12.3N, 45.6E"` (north-south first, then east-west) into
/// `(east_west, north_south)`, south and west negative.
#[must_use]
pub fn parse_coordinate_pair(text: &str) -> Option<(f64, f64)> {
    if text.trim().is_empty() {
        return None;
    }
    let parts: Vec<&str> = text.split(',').map(str::trim).collect();
    let [north_south, east_west] = parts.as_slice() else {
        return None;
    };
    let north_south = parse_coordinate(north_south, 'N', 'S')?;
    let east_west = parse_coordinate(east_west, 'E', 'W')?;
    Some((east_west, north_south))
}

fn parse_coordinate(text: &str, positive: char, negative: char) -> Option<f64> {
    let mut chars = text.chars();
    let suffix = chars.next_back()?.to_ascii_uppercase();
    if text.chars().count() < 2 || (suffix != positive && suffix != negative) {
        return None;
    }
    let magnitude = parse_float_f64(chars.as_str().trim())?;
    if magnitude.is_nan() || magnitude < 0.0 {
        return None;
    }
    Some(if suffix == negative {
        -magnitude
    } else {
        magnitude
    })
}

/// What a span of a line is, for drawing.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ChatSpanRole {
    /// The line's own text.
    #[default]
    Body,
    /// The timestamp prefix.
    Timestamp,
}

/// A run of visible text under one tag (or none).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatTextSpan {
    /// The visible text.
    pub text: String,
    /// The tag open over it.
    pub tag: Option<ChatTextTag>,
    /// What the span is.
    pub role: ChatSpanRole,
}

/// The type name of a tell tag, the tag around a speaker's name.
pub const TELL_TYPE_NAME: &str = "Tell";

/// The names a tag's type and format may take, and the number each stands
/// for, as the game data lists them. Case does not matter. A format is one
/// of the four payload shapes, 1 to 4.
const TAG_NAMES: [(&str, u32); 6] = [
    ("Invalid", 0),
    ("DID", 1),
    ("IID", 2),
    ("IIDEnum", 3),
    ("IIDString", 4),
    (TELL_TYPE_NAME, 0x1000_0001),
];

/// The longest name in the tag table ("IIDString").
const LONGEST_TAG_NAME: usize = 9;

/// The number a name in the tag table stands for (any case).
fn tag_name_value(name: &[u8]) -> Option<u32> {
    TAG_NAMES
        .iter()
        .find(|(known, _)| known.as_bytes().eq_ignore_ascii_case(name))
        .map(|&(_, value)| value)
}

/// Reads the tags in a chat line, character by character, the way the chat
/// window's text does.
///
/// A `<` is matched with the first `>` after it. With no tag open, the
/// bracketed text opens one when it is a well-formed `<TYPE:FORMAT:DATA>`
/// start tag, and is then not shown; any other bracketed text is shown as
/// written. With a tag open, any bracketed text at all ends it and is not
/// shown, even text that is itself a start tag (that one does not open). A
/// `<` with no `>` after it is shown.
///
/// The character straight after a hidden tag is always shown, even when it
/// is a `<`: the reading resumes one character later. Joining the spans'
/// text always gives the visible line.
#[must_use]
pub fn parse(text: &str) -> Vec<ChatTextSpan> {
    read(text, false)
}

/// A chat line that is not a plugin's own, as it is shown: its tags read by
/// [`parse`], and a coordinate tag someone wrote in it kept as plain text.
/// That tag is hidden and its caption shown, but the tag is not a link.
#[must_use]
pub fn parse_line(text: &str) -> Vec<ChatTextSpan> {
    without_coordinate_tags(read(text, true))
}

/// The text [`parse_line`] shows: the line with its tags hidden.
#[must_use]
pub fn to_shown_text(text: &str) -> String {
    parse_line(text).into_iter().map(|s| s.text).collect()
}

fn read(text: &str, read_coordinate_tags: bool) -> Vec<ChatTextSpan> {
    let push = |spans: &mut Vec<ChatTextSpan>, run: &str, open: &Option<ChatTextTag>| {
        spans.push(ChatTextSpan {
            text: run.to_owned(),
            tag: open.clone(),
            role: ChatSpanRole::Body,
        });
    };
    let mut spans = Vec::new();
    if text.is_empty() {
        return spans;
    }
    if !text.contains('<') {
        push(&mut spans, text, &None);
        return spans;
    }

    let bytes = text.as_bytes();
    let mut run = String::with_capacity(text.len());
    let mut open: Option<ChatTextTag> = None;
    let mut i = 0;
    // The first `>` at or after the reading position, or none left. Kept
    // across brackets so a line full of `<` is read in one pass.
    let mut next_close = text.find('>');

    while i < text.len() {
        if bytes[i] == b'<' {
            if next_close.is_some_and(|close| close < i) {
                next_close = text[i..].find('>').map(|offset| i + offset);
            }
            if let Some(close) = next_close {
                // An open tag ends at any bracketed text.
                let mut hidden = open.is_some();
                let mut next = None;
                if !hidden && may_be_start_tag(&bytes[i + 1..close], read_coordinate_tags) {
                    let inner = &text[i + 1..close];
                    next = read_start_tag(inner).or_else(|| {
                        if read_coordinate_tags {
                            parse_link_tag(inner)
                        } else {
                            None
                        }
                    });
                    hidden = next.is_some();
                }
                if hidden {
                    if !run.is_empty() {
                        push(&mut spans, &run, &open);
                    }
                    run.clear();
                    open = next;
                    i = close + 1;
                    // Shown whatever it is.
                    if let Some(c) = text[i..].chars().next() {
                        run.push(c);
                        i += c.len_utf8();
                    }
                    continue;
                }
            }
        }
        let c = text[i..].chars().next().unwrap_or('\0');
        run.push(c);
        i += c.len_utf8();
    }
    if !run.is_empty() {
        push(&mut spans, &run, &open);
    }
    spans
}

/// Whether the bracketed text begins with a type a tag can have: a name in
/// the tag table (or COORD, when coordinate tags are read) followed by a
/// colon. Only such text is worth reading further; every other bracket is
/// decided without looking past its first ten characters.
fn may_be_start_tag(inner: &[u8], read_coordinate_tags: bool) -> bool {
    let window = &inner[..inner.len().min(LONGEST_TAG_NAME + 1)];
    let Some(colon) = window.iter().position(|&b| b == b':') else {
        return false;
    };
    let tag_type = &inner[..colon];
    tag_name_value(tag_type).is_some()
        || (read_coordinate_tags && tag_type.eq_ignore_ascii_case(b"COORD"))
}

/// The tag a bracketed `TYPE:FORMAT:DATA` stands for, or `None` when it is
/// not one. TYPE and FORMAT must be names in the tag table, FORMAT one of
/// the four payload shapes, and DATA must fit that shape.
fn read_start_tag(inner: &str) -> Option<ChatTextTag> {
    let first = inner.find(':')?;
    let tag_type = &inner[..first];
    tag_name_value(tag_type.as_bytes())?;
    let rest = &inner[first + 1..];
    let second = rest.find(':')?;
    let format = &rest[..second];
    let shape = tag_name_value(format.as_bytes())?;
    if !(1..=4).contains(&shape) {
        return None;
    }
    let data = &rest[second + 1..];
    if data.is_empty() {
        return None;
    }
    let fits = match shape {
        // DID, IID: one number.
        1 | 2 => parse_tag_number(data).is_some(),
        // IIDEnum: a number, a colon and a name in the tag table.
        3 => id_and_name(data).is_some_and(|name| tag_name_value(name.as_bytes()).is_some()),
        // IIDString: a number, a colon and any name.
        _ => id_and_name(data).is_some(),
    };
    fits.then(|| ChatTextTag {
        tag_type: tag_type.to_owned(),
        format: format.to_owned(),
        data: data.to_owned(),
    })
}

/// The name of an `id:name` payload whose id is a tag number, neither empty.
fn id_and_name(data: &str) -> Option<&str> {
    let split = data.find(':')?;
    if split == 0 || split == data.len() - 1 {
        return None;
    }
    parse_tag_number(&data[..split])?;
    Some(&data[split + 1..])
}

/// Reads a tag's number the way the C library reads an unsigned long in
/// base 0, and accepts it only when that reading uses the whole text and
/// does not overflow: optional leading white space, an optional sign (a
/// minus wraps), then decimal, `0x` hexadecimal or `0` octal digits.
#[must_use]
pub fn parse_tag_number(text: &str) -> Option<u32> {
    let bytes = text.as_bytes();
    let mut i = 0;
    while i < bytes.len() && matches!(bytes[i], b' ' | b'\t' | b'\n' | 0x0B | 0x0C | b'\r') {
        i += 1;
    }
    let mut negative = false;
    if i < bytes.len() && matches!(bytes[i], b'+' | b'-') {
        negative = bytes[i] == b'-';
        i += 1;
    }
    let mut radix = 10u64;
    if i < bytes.len() && bytes[i] == b'0' {
        if i + 1 < bytes.len() && matches!(bytes[i + 1], b'x' | b'X') {
            radix = 16;
            i += 2;
        } else {
            radix = 8;
        }
    }
    let digits = &bytes[i..];
    if digits.is_empty() {
        return None; // no digits at all
    }
    let mut magnitude = 0u64;
    for &b in digits {
        let digit = match b {
            b'0'..=b'9' => u64::from(b - b'0'),
            b'a'..=b'z' => u64::from(b - b'a' + 10),
            b'A'..=b'Z' => u64::from(b - b'A' + 10),
            _ => u64::MAX,
        };
        if digit >= radix {
            return None; // the reading stops short of the end
        }
        magnitude = magnitude * radix + digit;
        if magnitude > u64::from(u32::MAX) {
            return None; // out of range
        }
    }
    let value = u32::try_from(magnitude).ok()?;
    Some(if negative {
        value.wrapping_neg()
    } else {
        value
    })
}

/// `TYPE:FORMAT[:DATA]` with any type and format, the start of a plugin's
/// link tag; anything else (a closing tag included) is none.
fn parse_start_tag(inner: &str) -> Option<ChatTextTag> {
    let first = inner.find(':')?;
    if first == 0 || first == inner.len() - 1 {
        return None;
    }
    let (tag_type, rest) = (&inner[..first], &inner[first + 1..]);
    let (format, data) = rest.split_once(':').unwrap_or((rest, ""));
    Some(ChatTextTag {
        tag_type: tag_type.to_owned(),
        format: format.to_owned(),
        data: data.to_owned(),
    })
}

fn is_coordinate_type(tag_type: &str) -> bool {
    tag_type.eq_ignore_ascii_case("COORD")
}

/// A start tag a plugin's line may carry: a well-formed
/// `COORD:World:pair`.
fn parse_link_tag(inner: &str) -> Option<ChatTextTag> {
    parse_start_tag(inner)
        .filter(|tag| is_coordinate_type(&tag.tag_type) && tag.coordinate().is_some())
}

/// `\TYPE`, closing the open tag of that type (any case).
fn is_end_tag(inner: &str, open: &ChatTextTag) -> bool {
    inner.len() == open.tag_type.len() + 1
        && inner.starts_with('\\')
        && inner[1..].eq_ignore_ascii_case(&open.tag_type)
}

/// Reads the link tags in a line a plugin posted. Only a well-formed
/// `<COORD:World:pair>` opens a link, and only its own `<\COORD>` closes
/// it; every other angle-bracket text is shown as written. A link left open
/// runs to the end of the line. A backslash directly before `<` is dropped
/// and that `<` is shown as written, never read as a tag (how the plugin
/// SDK's escape makes text show literally).
#[must_use]
pub fn parse_links(text: &str) -> Vec<ChatTextSpan> {
    let mut spans = Vec::new();
    if text.is_empty() {
        return spans;
    }
    let push = |spans: &mut Vec<ChatTextSpan>, run: &str, open: &Option<ChatTextTag>| {
        spans.push(ChatTextSpan {
            text: run.to_owned(),
            tag: open.clone(),
            role: ChatSpanRole::Body,
        });
    };
    let bytes = text.as_bytes();
    let mut run = String::with_capacity(text.len());
    let mut open: Option<ChatTextTag> = None;
    let mut i = 0;
    while i < text.len() {
        if bytes[i] == b'\\' && bytes.get(i + 1) == Some(&b'<') {
            // Escaped: shown, never a tag.
            run.push('<');
            i += 2;
            continue;
        }
        if bytes[i] == b'<'
            && let Some(offset) = text[i + 1..].find('>')
        {
            let close = i + 1 + offset;
            let inner = &text[i + 1..close];
            let next = parse_link_tag(inner);
            let markup = next.is_some() || open.as_ref().is_some_and(|o| is_end_tag(inner, o));
            if markup {
                if !run.is_empty() {
                    push(&mut spans, &run, &open);
                }
                run.clear();
                open = next;
                i = close + 1;
                continue;
            }
        }
        let c = text[i..].chars().next().unwrap_or('\0');
        run.push(c);
        i += c.len_utf8();
    }
    if !run.is_empty() {
        push(&mut spans, &run, &open);
    }
    spans
}

/// Whether a plugin's line holds at least one link tag [`parse_links`]
/// reads: a well-formed `<COORD:World:42.1N, 33.5E>`. Any other
/// angle-bracket text, such as "Press <Ctrl:F>", and an escaped tag are not
/// tags.
#[must_use]
pub fn contains_tag(text: &str) -> bool {
    text.contains('<') && parse_links(text).iter().any(|s| s.tag.is_some())
}

/// A plugin's line with its link tags removed: what [`parse_links`] shows.
#[must_use]
pub fn to_plain_text(text: &str) -> String {
    parse_links(text).into_iter().map(|s| s.text).collect()
}

/// The spans with every coordinate tag dropped and its caption kept as
/// plain text. What a player or the server writes may hold a coordinate
/// tag; its caption is shown in the line's colour and its hidden value is
/// never a link. A caption that is itself a coordinate pair is linked like
/// any shown pair, to exactly that pair (the chat feed links the pairs a
/// line shows).
#[must_use]
pub fn without_coordinate_tags(mut spans: Vec<ChatTextSpan>) -> Vec<ChatTextSpan> {
    for span in &mut spans {
        if span
            .tag
            .as_ref()
            .is_some_and(|tag| is_coordinate_type(&tag.tag_type))
        {
            span.tag = None;
        }
    }
    spans
}

#[cfg(test)]
mod tests {
    use super::*;

    const TELL_LINE: &str = r#"<Tell:IIDString:1342177290:Dww>Dww<\Tell> tells you, "hello""#;

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatTagMarkupTests.cs
    #[test]
    fn a_tell_line_splits_into_a_tagged_name_and_plain_remainder() {
        let spans = parse(TELL_LINE);
        assert_eq!(spans.len(), 2);
        assert_eq!(spans[0].text, "Dww");
        let tag = spans[0].tag.as_ref().expect("tagged");
        assert_eq!(tag.tag_type, "Tell");
        assert_eq!(tag.format, "IIDString");
        assert_eq!(spans[1].text, r#" tells you, "hello""#);
        assert_eq!(spans[1].tag, None);
    }

    #[test]
    fn the_tag_carries_the_speakers_id_and_name() {
        let spans = parse(TELL_LINE);
        let tagged: Vec<_> = spans.iter().filter_map(|s| s.tag.as_ref()).collect();
        assert_eq!(tagged.len(), 1);
        assert_eq!(tagged[0].iid_string(), Some((1_342_177_290, "Dww")));
    }

    #[test]
    fn any_bracketed_text_that_is_not_a_tag_closes_the_open_one() {
        for closer in [r"<\Tell>", "<Tell>", "<anything>"] {
            let spans = parse(&format!("<Tell:IIDString:1:A>A{closer}B"));
            assert_eq!(spans.len(), 2);
            assert_eq!(spans[0].text, "A");
            assert!(spans[0].tag.is_some());
            assert_eq!(spans[1].text, "B");
            assert_eq!(spans[1].tag, None);
        }
    }

    #[test]
    fn plain_text_is_one_untagged_span() {
        let spans = parse("You say, \"hi\"");
        assert_eq!(spans.len(), 1);
        assert_eq!(spans[0].text, "You say, \"hi\"");
        assert_eq!(spans[0].tag, None);
    }

    #[test]
    fn an_unterminated_bracket_is_ordinary_text() {
        let spans = parse("is 3 < 4 really");
        assert_eq!(spans.len(), 1);
        assert_eq!(spans[0].text, "is 3 < 4 really");
        assert_eq!(spans[0].tag, None);
    }

    #[test]
    fn the_concatenated_spans_reproduce_the_visible_line() {
        let visible: String = parse(TELL_LINE).into_iter().map(|s| s.text).collect();
        assert_eq!(visible, r#"Dww tells you, "hello""#);
    }

    // ── The reading rule: OpenAC chat tag fix ChatTagMarkupTests ──

    #[test]
    fn a_malformed_or_unknown_tag_is_not_a_tag_and_is_shown_as_written() {
        // AMalformedOrUnknownTagIsNotATagAndIsShownAsWritten.
        for (markup, why) in [
            ("<Tell:IIDString:notanumber:Dww>x", "a non-numeric id"),
            ("<Tell:IIDString:1>x", "no name"),
            ("<Tell:IIDString:>x", "empty payload"),
            ("<Tell:DID:1:x>x", "a DID payload that is not one number"),
            (
                "<Tell:IIDEnum:1:Nope>x",
                "an IIDEnum name not in the tag table",
            ),
            ("<Tell:Tell:1:Bob>x", "a format that is not a payload shape"),
            ("<COORD:World:1N, 2E>x", "a type not in the tag table"),
            ("<Tell:IIDString:4294967296:Bob>x", "an id that overflows"),
        ] {
            let spans = parse(markup);
            assert_eq!(spans.len(), 1, "{why}");
            assert_eq!(spans[0].tag, None, "{why}");
            assert_eq!(spans[0].text, markup, "{why}");
        }
    }

    fn texts_of(spans: &[ChatTextSpan]) -> Vec<&str> {
        spans.iter().map(|s| s.text.as_str()).collect()
    }

    #[test]
    fn bracketed_text_that_is_not_a_tag_is_shown_when_no_tag_is_open() {
        // BracketedTextThatIsNotATagIsShownWhenNoTagIsOpen.
        let spans = parse("a <hello> b <> c <x:y> d");
        assert_eq!(spans.len(), 1);
        assert_eq!(spans[0].text, "a <hello> b <> c <x:y> d");
        assert_eq!(spans[0].tag, None);
    }

    #[test]
    fn a_line_full_of_brackets_is_read_in_one_pass_and_shown_as_written() {
        // ALineFullOfBracketsIsReadInOnePassAndShownAsWritten.
        let brackets = "<".repeat(20000) + "Tell:IIDString:1:Bob";
        let late = "<".repeat(20000) + "a>";
        assert_eq!(texts_of(&parse(&brackets)), [brackets.as_str()]);
        assert_eq!(texts_of(&parse(&late)), [late.as_str()]);
        assert_eq!(to_shown_text(&late), late);
    }

    #[test]
    fn a_start_tag_while_a_tag_is_open_only_closes_it() {
        // AStartTagWhileATagIsOpenOnlyClosesIt.
        let spans = parse("<Tell:IIDString:1:A>A<Tell:IIDString:2:B>B");
        assert_eq!(texts_of(&spans), ["A", "B"]);
        assert_eq!(spans[0].tag.as_ref().unwrap().data, "1:A");
        assert_eq!(spans[1].tag, None);
    }

    #[test]
    fn an_empty_pair_of_brackets_closes_an_open_tag() {
        // AnEmptyPairOfBracketsClosesAnOpenTag.
        let spans = parse("<Tell:IIDString:1:A>A<>B");
        assert_eq!(texts_of(&spans), ["A", "B"]);
        assert!(spans[0].tag.is_some());
        assert_eq!(spans[1].tag, None);
    }

    #[test]
    fn an_unterminated_bracket_inside_a_tag_is_shown_in_that_tag() {
        // AnUnterminatedBracketInsideATagIsShownInThatTag.
        let spans = parse("<Tell:IIDString:1:A>A < b");
        assert_eq!(texts_of(&spans), ["A < b"]);
        assert!(spans[0].tag.as_ref().unwrap().is_tell());
    }

    #[test]
    fn the_character_right_after_a_hidden_tag_is_shown_even_when_it_is_a_bracket() {
        // TheCharacterRightAfterAHiddenTagIsShownEvenWhenItIsABracket.
        let spans = parse(r"<Tell:IIDString:1:A>A<\Tell><Tell:IIDString:2:B>B");
        assert_eq!(texts_of(&spans).concat(), "A<Tell:IIDString:2:B>B");
        assert_eq!(spans[0].text, "A");
        assert!(spans[1..].iter().all(|s| s.tag.is_none()));
    }

    #[test]
    fn a_bracket_is_matched_with_the_first_closing_bracket_after_it() {
        // ABracketIsMatchedWithTheFirstClosingBracketAfterIt: "<a<Tell:...>"
        // is not a tag, so its "<" is shown; the scan then finds the start
        // tag inside it.
        let spans = parse("<a<Tell:IIDString:1:B>B");
        assert_eq!(texts_of(&spans), ["<a", "B"]);
        assert_eq!(spans[0].tag, None);
        assert!(spans[1].tag.as_ref().unwrap().is_tell());
    }

    #[test]
    fn every_type_in_the_tag_table_opens_a_tag_but_only_a_tell_tag_is_a_tell() {
        // EveryTypeInTheTagTableOpensATagButOnlyATellTagIsATell.
        for (markup, tell) in [
            ("<tell:iidstring:1:Bob>Bob", true),
            ("<Tell:IID:0x5000000A>Bob", true),
            ("<DID:DID:0x06001234>icon", false),
            ("<IID:IIDEnum:7:Tell>x", false),
            ("<Invalid:IIDString: 12:Bob>Bob", false),
        ] {
            let tag = parse(markup)[0].tag.clone().expect(markup);
            assert_eq!(tag.is_tell(), tell, "{markup}");
        }
    }

    #[test]
    fn a_tag_number_is_read_like_the_c_library() {
        // ATagNumberIsReadLikeTheCLibrary.
        for (text, expected) in [
            ("1342177290", Some(1_342_177_290)),
            ("0x5000000A", Some(0x5000_000A)),
            ("017", Some(15)),
            ("  +7", Some(7)),
            ("-1", Some(0xFFFF_FFFF)),
            ("4294967295", Some(0xFFFF_FFFF)),
            ("4294967296", None),
            ("08", None),
            ("0x", None),
            ("12 ", None),
            ("", None),
            ("-", None),
        ] {
            assert_eq!(parse_tag_number(text), expected, "{text:?}");
        }
    }

    #[test]
    fn a_name_containing_a_colon_survives_intact() {
        let spans = parse("<Tell:IIDString:5:Odd:Name>x");
        let tag = spans[0].tag.as_ref().expect("tagged");
        assert_eq!(tag.iid_string(), Some((5, "Odd:Name")));
    }

    #[test]
    fn a_world_coordinate_tag_reads_north_then_east() {
        // A coordinate tag is a plugin's link (OpenAC fix/chat-hide-tags
        // `CoordinateTag_ParsesInvariantWorldCoordinates` reads it
        // with `ParseLinks`).
        let spans = parse_links(r"<COORD:World:12.5N, 40.1W>here<\COORD>");
        let tag = spans[0].tag.as_ref().expect("tagged");
        assert_eq!(tag.coordinate(), Some((-40.1, 12.5)));
        assert_eq!(parse_coordinate_pair("1.5s,2e"), Some((2.0, -1.5)));
        assert_eq!(parse_coordinate_pair("1,5N, 2E"), None);
        assert_eq!(parse_coordinate_pair("-1N, 2E"), None);
        assert_eq!(parse_coordinate_pair("N, 2E"), None);
    }

    // ── A plugin's line: OpenAC PR #224 ChatTagMarkupTests ──

    #[test]
    fn only_a_well_formed_coordinate_tag_is_a_link_tag() {
        // OnlyAWellFormedCoordinateTagIsALinkTag.
        for (text, expected) in [
            ("Press <Ctrl:F> to follow", false),
            ("<a:b:c>", false),
            (r"<Tell:IIDString:1:Bob>Bob<\Tell>", false),
            (r"<COORD:World:somewhere>x<\COORD>", false),
            (r"<COORD:World:1N, 2E>x<\COORD>", true),
            (r"Press <Ctrl:F>, then <coord:world:1N, 2E>x<\coord>", true),
        ] {
            assert_eq!(contains_tag(text), expected, "{text}");
        }
    }

    #[test]
    fn parse_links_keeps_every_other_angle_bracket_text_as_written() {
        // ParseLinksKeepsEveryOtherAngleBracketTextAsWritten.
        let spans =
            parse_links(r"<Ctrl:F> <COORD:World:1N, 2E>go <b:c> there<\Other><\COORD> <\COORD>end");
        let texts: Vec<&str> = spans.iter().map(|s| s.text.as_str()).collect();
        assert_eq!(
            texts,
            ["<Ctrl:F> ", r"go <b:c> there<\Other>", r" <\COORD>end"]
        );
        assert_eq!(spans[0].tag, None);
        assert_eq!(spans[1].tag.as_ref().unwrap().tag_type, "COORD");
        assert_eq!(spans[2].tag, None);
    }

    /// The plugin SDK's escape (`openac_plugin::chat_markup::escape`, tested
    /// in its own crate): a backslash before every `<`.
    fn escape(text: &str) -> String {
        text.replace('<', "\\<")
    }

    #[test]
    fn escaped_text_shows_as_written_and_holds_no_tag() {
        // EscapedTextShowsAsWrittenAndHoldsNoTag.
        for text in [
            r"<COORD:World:0N, 0E>the bridge<\COORD>",
            r"<coord:world:1N, 2E>x",
            r"a\<COORD:World:1N, 2E>b<\COORD>",
            r"trailing \ and \\< and <<COORD:World:1N, 2E>>",
            r"Press <Ctrl:F>",
            "no markup at all",
        ] {
            let escaped = escape(text);
            assert!(!contains_tag(&escaped), "{text}");
            assert_eq!(to_plain_text(&escaped), text);
            assert!(parse_links(&escaped).iter().all(|s| s.tag.is_none()));
        }
    }

    #[test]
    fn without_coordinate_tags_keeps_the_caption_and_other_tags() {
        // WithoutCoordinateTagsKeepsTheCaptionAndOtherTags (OpenAC
        // chat tag fix reads it with `ParseLine`).
        let spans = parse_line(
            r#"<Tell:IIDString:1:Bob>Bob<\Tell> says, "<COORD:World:0N, 0E>42.1N 33.5E<\COORD>""#,
        );
        let texts: Vec<&str> = spans.iter().map(|s| s.text.as_str()).collect();
        assert_eq!(texts, ["Bob", " says, \"", "42.1N 33.5E", "\""]);
        assert_eq!(spans[0].tag.as_ref().unwrap().tag_type, "Tell");
        assert!(spans[1..].iter().all(|s| s.tag.is_none()));
    }
}
