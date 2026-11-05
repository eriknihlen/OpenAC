//! The chat transcript, pure: the retail colour of each text class, the
//! character budget a chat window keeps, word wrap, and the coloured runs
//! and clickable tag ranges of each wrapped line.
//!
//! Columns and lengths count characters.

use ac_core::chat::{ChatSpanRole, ChatTextSpan, ChatTextTag};

use crate::draw::Color;
use crate::text::{Line, LineKey, TextRun};

const WHITE: Color = Color::rgba(1.0, 1.0, 1.0, 1.0);
const YELLOW: Color = Color::rgba(1.0, 1.0, 0.247, 1.0);
const DARK_YELLOW: Color = Color::rgba(0.824, 0.824, 0.392, 1.0);
const BRIGHT_PURPLE: Color = Color::rgba(1.0, 0.498, 1.0, 1.0);
const DARK_RED: Color = Color::rgba(1.0, 0.247, 0.247, 1.0);
const LIGHT_RED: Color = Color::rgba(0.96, 0.459, 0.447, 1.0);
const LIGHT_BLUE: Color = Color::rgba(0.247, 0.749, 1.0, 1.0);
const PINK: Color = Color::rgba(1.0, 0.588, 0.588, 1.0);
const CYAN: Color = Color::rgba(0.247, 0.863, 0.863, 1.0);
const BLUE_GREY: Color = Color::rgba(0.706, 0.863, 0.941, 1.0);
const GREY: Color = Color::rgba(0.824, 0.824, 0.784, 1.0);
const ORANGE: Color = Color::rgba(0.933, 0.573, 0.118, 1.0);
const GREEN: Color = Color::rgba(0.5, 1.0, 0.498, 1.0);
const BRIGHT_RED: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);

/// The colour of each chat text class (log text type), as the chat window
/// draws it.
pub const CHAT_COLORS: [Color; 34] = [
    GREEN,         // 0x00 default
    GREEN,         // 0x01 all
    WHITE,         // 0x02 speech
    YELLOW,        // 0x03 tell
    DARK_YELLOW,   // 0x04 direct speech sent
    BRIGHT_PURPLE, // 0x05 system
    DARK_RED,      // 0x06 combat
    LIGHT_BLUE,    // 0x07 magic
    PINK,          // 0x08
    PINK,          // 0x09
    YELLOW,        // 0x0A social
    DARK_YELLOW,   // 0x0B social sent
    GREY,          // 0x0C emote
    CYAN,          // 0x0D advancement
    BLUE_GREY,     // 0x0E abuse
    DARK_RED,      // 0x0F help
    GREEN,         // 0x10 appraisal
    LIGHT_BLUE,    // 0x11 spellcasting
    ORANGE,        // 0x12 allegiance
    YELLOW,        // 0x13 fellowship
    GREEN,         // 0x14 world broadcast
    DARK_RED,      // 0x15
    LIGHT_RED,     // 0x16
    GREEN,         // 0x17 recall
    GREEN,         // 0x18
    GREEN,         // 0x19 salvaging
    BRIGHT_RED,    // 0x1A
    BLUE_GREY,     // 0x1B
    BLUE_GREY,     // 0x1C
    BLUE_GREY,     // 0x1D
    BLUE_GREY,     // 0x1E
    YELLOW,        // 0x1F admin tell
    BLUE_GREY,     // 0x20
    ORANGE,        // 0x21
];

/// The colour of text class `log_text_type`; `None` past the table.
#[must_use]
pub fn chat_color(log_text_type: u32) -> Option<Color> {
    CHAT_COLORS.get(log_text_type as usize).copied()
}

/// The timestamp's colour: the emote class's, whatever the line's colour.
#[must_use]
pub fn timestamp_color() -> Color {
    chat_color(0x0C).unwrap_or(Color::rgba(0.5, 0.5, 0.5, 1.0))
}

/// The characters a chat window keeps; older text is cut off the top.
pub const MAX_TRANSCRIPT_CHARACTERS: usize = 0x2710;

/// One chat line as the transcript gets it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TranscriptLine {
    /// The chat log's sequence number (stable while older lines drop).
    pub sequence: u64,
    /// Text class (filter and colour).
    pub log_text_type: u32,
    /// The visible text.
    pub text: String,
    /// The text split into tagged and timestamp spans, when it has any.
    pub spans: Option<Vec<ChatTextSpan>>,
}

impl TranscriptLine {
    /// A line with no spans.
    #[must_use]
    pub fn plain(sequence: u64, log_text_type: u32, text: impl Into<String>) -> Self {
        Self {
            sequence,
            log_text_type,
            text: text.into(),
            spans: None,
        }
    }
}

/// A clickable range of a wrapped line: start column, length, tag.
pub type TagRange = (usize, usize, ChatTextTag);

fn chars(s: &str) -> usize {
    s.chars().count()
}

fn char_sub(s: &str, from: usize, to: usize) -> String {
    s.chars().skip(from).take(to.saturating_sub(from)).collect()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct BudgetStart {
    line_index: usize,
    char_offset: usize,
}

fn first_character_after_line_break(text: &[char], start: usize) -> usize {
    let mut i = start.min(text.len());
    while i < text.len() {
        if text[i] == '\r' || text[i] == '\n' {
            if text[i] == '\r' && i + 1 < text.len() && text[i + 1] == '\n' {
                i += 1;
            }
            return i + 1;
        }
        i += 1;
    }
    text.len()
}

fn find_budget_start(
    lines: &[TranscriptLine],
    accept: Option<&dyn Fn(u32) -> bool>,
    budget: usize,
) -> BudgetStart {
    let mut used: usize = 0;
    for i in (0..lines.len()).rev() {
        if accept.is_some_and(|a| !a(lines[i].log_text_type)) {
            continue;
        }
        let text: Vec<char> = lines[i].text.chars().collect();
        let cost = text.len() + 1;
        if used + cost <= budget {
            used += cost;
            continue;
        }
        let available = budget.saturating_sub(used + 1);
        if available > 0 {
            let minimum = text.len().saturating_sub(available);
            let offset = first_character_after_line_break(&text, minimum);
            if offset < text.len() {
                return BudgetStart {
                    line_index: i,
                    char_offset: offset,
                };
            }
            // The newest message alone must stay visible even when it has
            // no line break to cut at.
            if used == 0 && !text.is_empty() {
                return BudgetStart {
                    line_index: i,
                    char_offset: minimum,
                };
            }
        }
        return BudgetStart {
            line_index: i + 1,
            char_offset: 0,
        };
    }
    BudgetStart {
        line_index: 0,
        char_offset: 0,
    }
}

/// The index of the oldest line the budget keeps.
#[must_use]
pub fn first_line_within_budget(
    lines: &[TranscriptLine],
    accept: Option<&dyn Fn(u32) -> bool>,
    budget: usize,
) -> usize {
    find_budget_start(lines, accept, budget).line_index
}

fn slice_line(line: &TranscriptLine, offset: usize) -> TranscriptLine {
    if offset == 0 {
        return line.clone();
    }
    let text: String = line.text.chars().skip(offset).collect();
    let spans = line.spans.as_ref().map(|spans| {
        let mut sliced = Vec::new();
        let mut at = 0;
        for s in spans {
            let end = at + chars(&s.text);
            if end > offset {
                let from = offset.max(at) - at;
                sliced.push(ChatTextSpan {
                    text: s.text.chars().skip(from).collect(),
                    ..s.clone()
                });
            }
            at = end;
        }
        sliced
    });
    TranscriptLine {
        text,
        spans,
        ..line.clone()
    }
}

/// Whether a tagged span is drawn in the tag colour. Only a tell tag, the
/// one around a speaker's name, is; any other tag keeps the line's own
/// colour. A coordinate link comes only from a plugin's own line (every
/// other line has its coordinate tags made plain text) and is drawn as a
/// link too.
#[must_use]
pub fn draws_in_tag_color(tag: &ChatTextTag) -> bool {
    tag.is_tell() || tag.coordinate().is_some()
}

/// The coloured runs of the fragment `start..start + length` of a line:
/// text under a tell tag or a coordinate link in `tag_color`
/// ([`draws_in_tag_color`]), the timestamp in its fixed colour, the rest
/// (other tags included) in `line_color`. `None` when the fragment has
/// neither tags nor a timestamp (the line draws in one colour).
#[must_use]
pub fn runs_for_fragment(
    spans: &[ChatTextSpan],
    fragment_start: usize,
    fragment_length: usize,
    line_color: Color,
    tag_color: Color,
) -> Option<Vec<TextRun>> {
    let fragment_end = fragment_start + fragment_length;
    let mut runs = Vec::new();
    let mut saw_tag = false;
    let mut at = 0;
    for s in spans {
        let (span_start, span_end) = (at, at + chars(&s.text));
        at = span_end;
        let (from, to) = (span_start.max(fragment_start), span_end.min(fragment_end));
        if to <= from {
            continue;
        }
        let tagged = s.tag.is_some();
        let stamped = s.role == ChatSpanRole::Timestamp;
        saw_tag |= tagged || stamped;
        let color = if s.tag.as_ref().is_some_and(draws_in_tag_color) {
            tag_color
        } else if stamped {
            timestamp_color()
        } else {
            line_color
        };
        runs.push(TextRun::new(
            char_sub(&s.text, from - span_start, to - span_start),
            color,
        ));
    }
    saw_tag.then_some(runs)
}

/// The tagged ranges of the fragment, columns relative to the fragment.
#[must_use]
pub fn tagged_ranges_for_fragment(
    spans: &[ChatTextSpan],
    fragment_start: usize,
    fragment_length: usize,
) -> Option<Vec<TagRange>> {
    let fragment_end = fragment_start + fragment_length;
    let mut ranges: Option<Vec<TagRange>> = None;
    let mut at = 0;
    for s in spans {
        let (span_start, span_end) = (at, at + chars(&s.text));
        at = span_end;
        let Some(tag) = &s.tag else {
            continue;
        };
        let (from, to) = (span_start.max(fragment_start), span_end.min(fragment_end));
        if to <= from {
            continue;
        }
        ranges
            .get_or_insert_with(Vec::new)
            .push((from - fragment_start, to - from, tag.clone()));
    }
    ranges
}

/// Word-wrap a chat line: explicit line breaks (`\r\n`, `\r`, `\n`) start
/// new lines; each piece wraps at spaces, a word too wide for a line
/// breaks between characters.
#[must_use]
pub fn wrap_text(text: &str, max_w: f32, measure: &dyn Fn(&str) -> f32) -> Vec<String> {
    if text.is_empty() {
        return vec![String::new()];
    }
    let normalized = text.replace("\r\n", "\n").replace('\r', "\n");
    let mut out = Vec::new();
    for segment in normalized.split('\n') {
        wrap_single_line(segment, max_w, measure, &mut out);
    }
    out
}

fn wrap_single_line(text: &str, max_w: f32, measure: &dyn Fn(&str) -> f32, out: &mut Vec<String>) {
    if text.is_empty() || max_w <= 0.0 || measure(text) <= max_w {
        out.push(text.to_owned());
        return;
    }
    let mut line = String::new();
    for word in text.split(' ') {
        let sep = if line.is_empty() { "" } else { " " };
        if measure(&format!("{line}{sep}{word}")) <= max_w {
            line.push_str(sep);
            line.push_str(word);
            continue;
        }
        if !line.is_empty() && measure(word) <= max_w {
            out.push(std::mem::take(&mut line));
            line.push_str(word);
            continue;
        }
        if !line.is_empty() {
            line.push(' ');
        }
        for ch in word.chars() {
            if !line.is_empty() && measure(&format!("{line}{ch}")) > max_w {
                out.push(std::mem::take(&mut line));
            }
            line.push(ch);
        }
    }
    if !line.is_empty() {
        out.push(line);
    }
}

/// A laid-out transcript: the wrapped lines and, per line, its coloured
/// runs, its clickable tag ranges and its identity.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct TranscriptLayout {
    /// The wrapped lines, oldest first.
    pub lines: Vec<Line>,
    /// Runs per line (`None`: one colour).
    pub runs: Vec<Option<Vec<TextRun>>>,
    /// Tag ranges per line.
    pub tags: Vec<Option<Vec<TagRange>>>,
    /// Identity per line (message sequence, offset in the message).
    pub keys: Vec<LineKey>,
}

/// Lay out the lines a window shows: the newest that fit the character
/// budget, those its filter accepts, wrapped to `max_w`. A line takes its
/// class's colour; a class past the table keeps the colour before it
/// (`default_color` for the first line).
#[must_use]
pub fn build_lines(
    lines: &[TranscriptLine],
    max_w: f32,
    measure: &dyn Fn(&str) -> f32,
    accept: Option<&dyn Fn(u32) -> bool>,
    default_color: Color,
    tag_color: Option<Color>,
) -> TranscriptLayout {
    let mut out = TranscriptLayout::default();
    if lines.is_empty() {
        return out;
    }
    let mut current = default_color;
    let start = find_budget_start(lines, accept, MAX_TRANSCRIPT_CHARACTERS);
    for (index, original) in lines.iter().enumerate().skip(start.line_index) {
        if accept.is_some_and(|a| !a(original.log_text_type)) {
            continue;
        }
        let sliced;
        let d = if index == start.line_index && start.char_offset > 0 {
            sliced = slice_line(original, start.char_offset);
            &sliced
        } else {
            original
        };
        if let Some(c) = chat_color(d.log_text_type) {
            current = c;
        }
        // Where this message's text starts in the message the log holds
        // (the oldest one may have lost its head to the budget).
        let message_offset = if index == start.line_index {
            start.char_offset
        } else {
            0
        };
        let mut search_from_byte = 0usize;
        let mut search_from_char = 0usize;
        for frag in wrap_text(&d.text, max_w, measure) {
            out.lines.push(Line::new(frag.clone(), current));
            let found = if frag.is_empty() {
                Some(search_from_byte)
            } else {
                d.text[search_from_byte..]
                    .find(frag.as_str())
                    .map(|b| search_from_byte + b)
            };
            let at_char = match found {
                Some(b) => {
                    let c = search_from_char + chars(&d.text[search_from_byte..b]);
                    if !frag.is_empty() {
                        search_from_byte = b + frag.len();
                        search_from_char = c + chars(&frag);
                    }
                    Some(c)
                }
                None => None,
            };
            out.keys.push(LineKey {
                source: d.sequence as i64,
                start: message_offset + at_char.unwrap_or(search_from_char),
            });
            match (&d.spans, at_char) {
                (Some(spans), Some(at)) if !spans.is_empty() && !frag.is_empty() => {
                    let len = chars(&frag);
                    out.runs.push(runs_for_fragment(
                        spans,
                        at,
                        len,
                        current,
                        tag_color.unwrap_or(current),
                    ));
                    out.tags.push(tagged_ranges_for_fragment(spans, at, len));
                }
                _ => {
                    out.runs.push(None);
                    out.tags.push(None);
                }
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/ChatTranscriptRunsTests.cs`,
    //! `ChatTranscriptRendererTests.cs` and `ChatTagClickTests.cs`.

    use super::*;
    use crate::text::runs_match_line;

    const LINE: Color = Color::rgba(0.8, 0.8, 0.8, 1.0);
    const TAG_GREEN: Color = Color::rgba(0.0, 178.0 / 255.0, 0.0, 1.0);

    fn measure(t: &str) -> f32 {
        t.chars().count() as f32 * 10.0
    }

    fn by_char(t: &str) -> f32 {
        t.chars().count() as f32
    }

    fn span(text: &str, tag: Option<ChatTextTag>) -> ChatTextSpan {
        ChatTextSpan {
            text: text.into(),
            tag,
            role: ChatSpanRole::Body,
        }
    }

    fn tell_tag(name: &str) -> ChatTextTag {
        ChatTextTag {
            tag_type: "Tell".into(),
            format: "IIDString".into(),
            data: format!("1342177290:{name}"),
        }
    }

    fn tell_spans(name: &str, rest: &str) -> Vec<ChatTextSpan> {
        vec![span(name, Some(tell_tag(name))), span(rest, None)]
    }

    fn stamp(text: &str) -> ChatTextSpan {
        ChatTextSpan {
            text: text.into(),
            tag: None,
            role: ChatSpanRole::Timestamp,
        }
    }

    #[test]
    fn the_name_takes_the_tag_colour_and_the_rest_the_line_colour() {
        let runs =
            runs_for_fragment(&tell_spans("Dww", " tells you"), 0, 13, LINE, TAG_GREEN).unwrap();
        assert_eq!(
            runs,
            vec![
                TextRun::new("Dww", TAG_GREEN),
                TextRun::new(" tells you", LINE)
            ]
        );
    }

    #[test]
    fn a_tag_that_is_not_a_tell_takes_the_line_colour() {
        // OpenAC chat tag fix
        // ChatTranscriptRunsTests.ATagThatIsNotATellTakesTheLineColour.
        let tag = |tag_type: &str, format: &str, data: &str| ChatTextTag {
            tag_type: tag_type.into(),
            format: format.into(),
            data: data.into(),
        };
        let spans = vec![
            span("Dww", Some(tell_tag("Dww"))),
            span(" found ", None),
            span("a gem", Some(tag("IID", "IIDString", "1342177290:gem"))),
            span(" and ", None),
            span("this", Some(tag("DID", "DID", "0x06001234"))),
        ];
        let runs = runs_for_fragment(&spans, 0, "Dww found a gem and this".len(), LINE, TAG_GREEN)
            .unwrap();
        let colors: Vec<Color> = runs.iter().map(|r| r.color).collect();
        assert_eq!(colors, [TAG_GREEN, LINE, LINE, LINE, LINE]);
    }

    #[test]
    fn a_fragment_with_no_tag_gets_no_runs() {
        assert!(
            runs_for_fragment(
                &tell_spans("Dww", " tells you, hello there"),
                10,
                5,
                LINE,
                TAG_GREEN
            )
            .is_none()
        );
    }

    #[test]
    fn a_tag_straddling_a_wrap_is_split_across_both_fragments() {
        let spans = tell_spans("Bartholomew", " tells you");
        let first = runs_for_fragment(&spans, 0, 5, LINE, TAG_GREEN).unwrap();
        let second = runs_for_fragment(&spans, 5, 6, LINE, TAG_GREEN).unwrap();
        assert_eq!(first, vec![TextRun::new("Barth", TAG_GREEN)]);
        assert_eq!(second, vec![TextRun::new("olomew", TAG_GREEN)]);
    }

    #[test]
    fn runs_always_reproduce_the_fragment_they_cover() {
        let spans = tell_spans("Dww", " tells you, \"hi\"");
        let line: String = spans.iter().map(|s| s.text.as_str()).collect();
        let n = line.chars().count();
        for start in 0..n {
            for len in 1..=n - start {
                if let Some(runs) = runs_for_fragment(&spans, start, len, LINE, TAG_GREEN) {
                    let fragment = char_sub(&line, start, start + len);
                    assert!(
                        runs_match_line(&runs, &fragment),
                        "[{start}..{})",
                        start + len
                    );
                }
            }
        }
    }

    #[test]
    fn build_lines_emits_one_run_entry_per_wrapped_fragment() {
        let lines = vec![
            TranscriptLine {
                spans: Some(tell_spans("Dww", " tells you, hello there friend")),
                ..TranscriptLine::plain(1, 0x03, "Dww tells you, hello there friend")
            },
            TranscriptLine::plain(2, 0x05, "Welcome."),
        ];
        let l = build_lines(&lines, 150.0, &measure, None, LINE, Some(TAG_GREEN));
        assert!(l.lines.len() > 2);
        assert_eq!(l.lines.len(), l.runs.len());
        assert_eq!(l.runs[0].as_ref().unwrap()[0].color, TAG_GREEN);
        assert!(l.runs.last().unwrap().is_none());
    }

    #[test]
    fn without_a_tag_colour_the_runs_take_the_line_colour() {
        let lines = vec![TranscriptLine {
            spans: Some(tell_spans("Dww", " tells you")),
            ..TranscriptLine::plain(1, 0x03, "Dww tells you")
        }];
        let l = build_lines(&lines, 1000.0, &measure, None, LINE, None);
        let runs = l.runs[0].as_ref().unwrap();
        assert!(runs.iter().all(|r| r.color == runs[0].color));
    }

    #[test]
    fn the_budget_keeps_short_histories_and_drops_the_oldest() {
        let plain = |t: String, ty| TranscriptLine::plain(0, ty, t);
        let short = vec![plain("one".into(), 2), plain("two".into(), 2)];
        assert_eq!(
            first_line_within_budget(&short, None, MAX_TRANSCRIPT_CHARACTERS),
            0
        );
        let four: Vec<TranscriptLine> = ['a', 'b', 'c', 'd']
            .iter()
            .map(|c| plain(c.to_string().repeat(10), 2))
            .collect();
        assert_eq!(first_line_within_budget(&four, None, 25), 2);
        let filtered = vec![
            plain("a".repeat(10), 0x02),
            plain("x".repeat(100), 0x06),
            plain("b".repeat(10), 0x02),
        ];
        let accept = |t: u32| t == 0x02;
        assert_eq!(first_line_within_budget(&filtered, Some(&accept), 25), 0);
        assert_eq!(MAX_TRANSCRIPT_CHARACTERS, 0x2710);
    }

    #[test]
    fn build_lines_renders_only_the_lines_inside_the_budget() {
        let lines: Vec<TranscriptLine> = (0..40)
            .map(|i| {
                let c = char::from(b'a' + (i % 26) as u8);
                TranscriptLine::plain(i, 2, c.to_string().repeat(500))
            })
            .collect();
        let l = build_lines(&lines, 100_000.0, &measure, None, LINE, None);
        assert!(l.lines.len() < lines.len());
        assert_eq!(l.lines.last().unwrap().text, lines.last().unwrap().text);
    }

    #[test]
    fn an_oversized_multiline_message_keeps_its_newest_complete_lines() {
        let response: Vec<String> = (0..1500).map(|i| format!("@command-{i:04}")).collect();
        let lines = vec![TranscriptLine::plain(1, 2, response.join("\n"))];
        let l = build_lines(&lines, 100_000.0, &measure, None, LINE, None);
        assert!(!l.lines.is_empty());
        assert_eq!(l.lines.last().unwrap().text, "@command-1499");
        assert!(!l.lines.iter().any(|x| x.text == "@command-0000"));
        assert!(l.lines.iter().all(|x| !x.text.trim().is_empty()));
    }

    #[test]
    fn a_multiline_message_within_budget_renders_every_line() {
        let lines = vec![TranscriptLine::plain(
            1,
            2,
            "@acecommands\n@help\n@teleport",
        )];
        let l = build_lines(&lines, 100_000.0, &measure, None, LINE, None);
        let texts: Vec<&str> = l.lines.iter().map(|x| x.text.as_str()).collect();
        assert_eq!(texts, ["@acecommands", "@help", "@teleport"]);
    }

    #[test]
    fn the_timestamp_is_grey_whatever_the_message_colour() {
        let spans = vec![stamp("13:05:09 "), span("Dww says, \"hi\"", None)];
        let runs = runs_for_fragment(&spans, 0, 9 + 14, LINE, TAG_GREEN).unwrap();
        assert_eq!(runs.len(), 2);
        assert_eq!(runs[0].text, "13:05:09 ");
        assert_ne!(runs[0].color, LINE);
        assert_eq!(runs[1].color, LINE);
        let plain = vec![stamp("13:05:09 "), span("Welcome.", None)];
        assert!(runs_for_fragment(&plain, 0, 17, LINE, TAG_GREEN).is_some());
        let both = vec![
            stamp("13:05:09 "),
            span(
                "Dww",
                Some(ChatTextTag {
                    tag_type: "Tell".into(),
                    format: "IIDString".into(),
                    data: "1:Dww".into(),
                }),
            ),
            span(" tells you", None),
        ];
        let runs = runs_for_fragment(&both, 0, 22, LINE, TAG_GREEN).unwrap();
        assert_eq!(runs.len(), 3);
        assert_eq!(runs[1].color, TAG_GREEN);
        assert_ne!(runs[0].color, TAG_GREEN);
        assert_ne!(runs[0].color, runs[2].color);
    }

    #[test]
    fn wrap_text_splits_on_every_line_break_and_wraps_long_segments() {
        assert_eq!(
            wrap_text("line one\nline two", 1000.0, &by_char),
            ["line one", "line two"]
        );
        assert_eq!(
            wrap_text("line one\r\nline two", 1000.0, &by_char),
            ["line one", "line two"]
        );
        let l = wrap_text(
            "short\nthis segment is much too long to fit on one line",
            10.0,
            &by_char,
        );
        assert_eq!(l[0], "short");
        assert!(l.len() > 2);
        assert!(l.iter().all(|x| by_char(x) <= 10.0));
        assert_eq!(
            l[1..].join(" "),
            "this segment is much too long to fit on one line"
        );
        assert_eq!(
            wrap_text("no newlines here", 1000.0, &by_char),
            ["no newlines here"]
        );
        assert_eq!(
            wrap_text("first\n\nthird", 1000.0, &by_char),
            ["first", "", "third"]
        );
    }

    #[test]
    fn in_table_classes_take_their_colour_and_unknown_ones_carry_the_last() {
        let off_white = Color::rgba(0.8, 0.8, 0.8, 1.0);
        let one = [TranscriptLine::plain(1, 0x02, "hello")];
        let a = build_lines(&one, 1000.0, &by_char, None, off_white, None);
        let b = build_lines(
            &one,
            1000.0,
            &by_char,
            None,
            Color::rgba(0.0, 1.0, 0.0, 1.0),
            None,
        );
        assert_eq!(a.lines[0].color, chat_color(0x02).unwrap());
        assert_eq!(b.lines[0].color, chat_color(0x02).unwrap());
        let mystery = [TranscriptLine::plain(1, 0xFF, "mystery")];
        assert_eq!(
            build_lines(&mystery, 1000.0, &by_char, None, off_white, None).lines[0].color,
            off_white
        );
        let pair = [
            TranscriptLine::plain(1, 0x03, "says hi"),
            TranscriptLine::plain(2, 0xFF, "mystery"),
        ];
        let l = build_lines(&pair, 1000.0, &by_char, None, off_white, None);
        assert_eq!(l.lines[0].color, chat_color(0x03).unwrap());
        assert_eq!(l.lines[1].color, chat_color(0x03).unwrap());
        assert!(
            build_lines(&[], 1000.0, &by_char, None, off_white, None)
                .lines
                .is_empty()
        );
    }

    #[test]
    fn tag_ranges_are_relative_to_the_fragment() {
        let spans = tell_spans("Dww", " tells you, \"hi\"");
        let r = tagged_ranges_for_fragment(&spans, 0, 19).unwrap();
        assert_eq!(r.len(), 1);
        assert_eq!((r[0].0, r[0].1), (0, 3));
        let r = tagged_ranges_for_fragment(&spans, 1, 5).unwrap();
        assert_eq!((r[0].0, r[0].1), (0, 2));
        assert!(tagged_ranges_for_fragment(&spans, 5, 5).is_none());
        assert!(tagged_ranges_for_fragment(&[span("plain", None)], 0, 5).is_none());
    }

    #[test]
    fn line_keys_survive_the_budget_cut() {
        let lines = vec![TranscriptLine::plain(7, 2, "ab\ncd")];
        let l = build_lines(&lines, 1000.0, &by_char, None, LINE, None);
        assert_eq!(
            l.keys[0],
            LineKey {
                source: 7,
                start: 0
            }
        );
        assert_eq!(
            l.keys[1],
            LineKey {
                source: 7,
                start: 3
            }
        );
    }
}
