//! The journal file: plain text, one tag per line, `<NEWP>` opening each
//! page. Page order is file order.
//!
//! ```text
//! <NEWP>
//! <PNUM> 1
//! <LABE> Aerlinthe
//! <TITL> Recall ring
//! <NOTE> Talk to the archmage first.
//! <DAYS> 1
//! <HOUR> 2
//! <MINU> 3
//! <LOCX> 12.5
//! <LOCY> -8.25
//! <TIME> 90
//! ```

use super::JournalPage;
use crate::text::{format_up_to_six_decimals, parse_float_f32, parse_integer_i32};

const NEW_PAGE: &str = "<NEWP>";
const PAGE_NUMBER: &str = "<PNUM>";
const LABEL: &str = "<LABE>";
const TITLE: &str = "<TITL>";
const NOTES: &str = "<NOTE>";
const DAYS: &str = "<DAYS>";
const HOURS: &str = "<HOUR>";
const MINUTES: &str = "<MINU>";
const LOCATION_X: &str = "<LOCX>";
const LOCATION_Y: &str = "<LOCY>";
const RUNNING_TIME: &str = "<TIME>";

/// The game's own message for a file whose content does not start with a
/// page.
pub const MALFORMED_FILE_MESSAGE: &str =
    "Problem loading journal: Your journal file does not create a new page!";

/// The outcome of reading a journal file.
#[derive(Debug, Clone, PartialEq)]
pub struct JournalReadResult {
    /// The pages read, in file order.
    pub pages: Vec<JournalPage>,
    /// The game's message when the file is unusable, or `None`.
    pub error: Option<&'static str>,
}

/// The per-character file name, `Journal-<server>-<character>.txt`, with
/// anything a file name cannot carry (control characters, `"<>|:*?\/`)
/// turned into `_` so a name cannot redirect the write. `None` when either
/// name is blank.
#[must_use]
pub fn file_name_for(server_name: &str, character_name: &str) -> Option<String> {
    if server_name.trim().is_empty() || character_name.trim().is_empty() {
        return None;
    }
    Some(format!(
        "Journal-{}-{}.txt",
        sanitize(server_name),
        sanitize(character_name)
    ))
}

fn sanitize(value: &str) -> String {
    value
        .chars()
        .map(|c| {
            if c < ' ' || matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/') {
                '_'
            } else {
                c
            }
        })
        .collect()
}

/// The first `count` characters of `line`, or all of it.
fn prefix_chars(line: &str, count: usize) -> &str {
    line.char_indices()
        .nth(count)
        .map_or(line, |(cut, _)| &line[..cut])
}

/// Everything after the first `count` characters, or nothing.
fn suffix_after_chars(line: &str, count: usize) -> &str {
    line.char_indices()
        .nth(count)
        .map_or("", |(cut, _)| &line[cut..])
}

/// Reads a journal file. Lines without a tag belong to nothing and are
/// skipped; unknown tags are skipped too (a newer client's tag must not
/// make an older one refuse the file). A tag before the first `<NEWP>`,
/// or content with no page at all, is [`MALFORMED_FILE_MESSAGE`]. An empty
/// file is a character who has written nothing. Every page read is
/// clipped to the boxes' maximums.
#[must_use]
pub fn read(text: &str) -> JournalReadResult {
    let malformed = JournalReadResult {
        pages: Vec::new(),
        error: Some(MALFORMED_FILE_MESSAGE),
    };
    let mut pages = Vec::new();
    let mut current: Option<JournalPage> = None;
    let mut saw_any_content = false;

    for raw_line in text.split('\n') {
        let line = raw_line.trim_end_matches('\r');
        if line.is_empty() {
            continue;
        }
        if !line.starts_with('<') {
            saw_any_content = true;
            continue;
        }

        let tag = prefix_chars(line, 6);
        // The value starts after the tag and one separating space.
        let value = suffix_after_chars(line, 7);

        if tag == NEW_PAGE {
            if let Some(page) = current.take() {
                pages.push(page);
            }
            current = Some(JournalPage::default());
            saw_any_content = true;
            continue;
        }

        let Some(page) = current.as_mut() else {
            return malformed;
        };
        saw_any_content = true;
        match tag {
            LABEL => value.clone_into(&mut page.label),
            TITLE => value.clone_into(&mut page.title),
            NOTES => value.clone_into(&mut page.notes),
            DAYS => page.timer_days = parse_int(value),
            HOURS => page.timer_hours = parse_int(value),
            MINUTES => page.timer_minutes = parse_int(value),
            LOCATION_X => {
                page.location_x = parse_float(value);
                page.has_location = true;
            }
            LOCATION_Y => {
                page.location_y = parse_float(value);
                page.has_location = true;
            }
            // The running time is stored at single precision.
            RUNNING_TIME => page.running_timer_seconds = f64::from(parse_float(value)),
            _ => {}
        }
    }

    if let Some(page) = current {
        pages.push(page);
    }
    if pages.is_empty() && saw_any_content {
        return malformed;
    }
    JournalReadResult {
        pages: pages.into_iter().map(JournalPage::clipped).collect(),
        error: None,
    }
}

/// Writes the pages in the game's tag order. Every page is written whole,
/// empty fields included; the location only when one was recorded. A line
/// break inside a value (the notes box allows them) becomes a space, since
/// it would otherwise read back as a separate line.
#[must_use]
pub fn write(pages: &[JournalPage]) -> String {
    fn append(text: &mut String, tag: &str, value: &str) {
        text.push_str(tag);
        text.push(' ');
        text.extend(value.chars().map(|c| match c {
            '\n' | '\r' => ' ',
            c => c,
        }));
        text.push('\n');
    }

    let mut text = String::new();
    for (index, page) in pages.iter().enumerate() {
        text.push_str(NEW_PAGE);
        text.push('\n');
        append(&mut text, PAGE_NUMBER, &(index + 1).to_string());
        append(&mut text, LABEL, &page.label);
        append(&mut text, TITLE, &page.title);
        append(&mut text, NOTES, &page.notes);
        append(&mut text, DAYS, &page.timer_days.to_string());
        append(&mut text, HOURS, &page.timer_hours.to_string());
        append(&mut text, MINUTES, &page.timer_minutes.to_string());
        if page.has_location {
            append(
                &mut text,
                LOCATION_X,
                &format_up_to_six_decimals(f64::from(page.location_x)),
            );
            append(
                &mut text,
                LOCATION_Y,
                &format_up_to_six_decimals(f64::from(page.location_y)),
            );
        }
        append(
            &mut text,
            RUNNING_TIME,
            &format_up_to_six_decimals(page.running_timer_seconds),
        );
    }
    text
}

fn parse_int(value: &str) -> i32 {
    parse_integer_i32(value.trim()).unwrap_or(0)
}

fn parse_float(value: &str) -> f32 {
    parse_float_f32(value.trim()).unwrap_or(0.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn titled(title: &str) -> JournalPage {
        JournalPage {
            title: title.to_owned(),
            ..JournalPage::default()
        }
    }

    fn labelled(label: &str) -> JournalPage {
        JournalPage {
            label: label.to_owned(),
            ..JournalPage::default()
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Journal/JournalFileTests.cs
    #[test]
    fn a_page_round_trips_through_the_file() {
        let page = JournalPage {
            label: "Aerlinthe".to_owned(),
            title: "Recall ring".to_owned(),
            notes: "Talk to the archmage first.".to_owned(),
            timer_days: 1,
            timer_hours: 2,
            timer_minutes: 3,
            location_x: 12.5,
            location_y: -8.25,
            has_location: true,
            running_timer_seconds: 90.0,
        };
        let result = read(&write(std::slice::from_ref(&page)));
        assert_eq!(result.error, None);
        assert_eq!(result.pages, [page]);
    }

    #[test]
    fn several_pages_keep_their_file_order() {
        let pages = [labelled("one"), labelled("two"), labelled("three")];
        let result = read(&write(&pages));
        let labels: Vec<&str> = result.pages.iter().map(|p| p.label.as_str()).collect();
        assert_eq!(labels, ["one", "two", "three"]);
    }

    #[test]
    fn a_file_that_does_not_open_with_a_page_marker_is_refused() {
        let result = read("<TITL> orphaned\n<NEWP>\n");
        assert_eq!(result.error, Some(MALFORMED_FILE_MESSAGE));
        assert!(result.pages.is_empty());
    }

    #[test]
    fn an_empty_file_is_a_character_who_has_written_nothing() {
        let result = read("");
        assert_eq!(result.error, None);
        assert!(result.pages.is_empty());
    }

    #[test]
    fn content_with_no_page_is_refused() {
        let result = read("just some text\n");
        assert_eq!(result.error, Some(MALFORMED_FILE_MESSAGE));
    }

    #[test]
    fn an_unknown_tag_is_skipped_rather_than_refused() {
        let result = read("<NEWP>\n<TITL> kept\n<ZZZZ> whatever\n");
        assert_eq!(result.error, None);
        assert_eq!(result.pages.len(), 1);
        assert_eq!(result.pages[0].title, "kept");
    }

    #[test]
    fn the_written_form_is_the_games_tag_order() {
        let page = JournalPage {
            label: "L".to_owned(),
            title: "T".to_owned(),
            ..JournalPage::default()
        };
        let text = write(&[page]);
        let tags: Vec<&str> = text
            .split('\n')
            .filter(|line| !line.is_empty())
            .map(|line| prefix_chars(line, 6))
            .collect();
        assert_eq!(
            tags,
            [
                "<NEWP>", "<PNUM>", "<LABE>", "<TITL>", "<NOTE>", "<DAYS>", "<HOUR>", "<MINU>",
                "<TIME>"
            ]
        );
    }

    #[test]
    fn a_recorded_location_of_zero_is_still_a_recorded_location() {
        let page = JournalPage {
            has_location: true,
            ..JournalPage::default()
        };
        let result = read(&write(&[page]));
        assert!(result.pages[0].has_location);
    }

    #[test]
    fn a_page_with_no_location_does_not_gain_one() {
        let result = read(&write(&[titled("T")]));
        assert!(!result.pages[0].has_location);
    }

    #[test]
    fn a_newline_inside_notes_cannot_split_the_page() {
        let page = JournalPage {
            notes: "first line\nsecond line".to_owned(),
            title: "kept".to_owned(),
            ..JournalPage::default()
        };
        let result = read(&write(&[page]));
        assert_eq!(result.pages.len(), 1);
        assert_eq!(result.pages[0].notes, "first line second line");
        assert_eq!(result.pages[0].title, "kept");
    }

    #[test]
    fn overlong_fields_are_clipped_to_their_maximums() {
        let text = format!(
            "<NEWP>\n<LABE> {}\n<TITL> {}\n",
            "a".repeat(100),
            "b".repeat(100)
        );
        let result = read(&text);
        assert_eq!(
            result.pages[0].label.len(),
            crate::journal::MAX_LABEL_LENGTH
        );
        assert_eq!(
            result.pages[0].title.len(),
            crate::journal::MAX_TITLE_LENGTH
        );
    }

    #[test]
    fn the_file_name_follows_the_games_pattern() {
        assert_eq!(
            file_name_for("Frostfell", "Acdream").as_deref(),
            Some("Journal-Frostfell-Acdream.txt")
        );
        assert_eq!(
            file_name_for("a/b", r"c\d").as_deref(),
            Some("Journal-a_b-c_d.txt")
        );
        assert_eq!(file_name_for(" ", "Acdream"), None);
    }

    #[test]
    fn numbers_are_written_the_us_way() {
        let page = JournalPage {
            location_x: 1.5,
            location_y: 2.25,
            has_location: true,
            ..JournalPage::default()
        };
        let text = write(&[page]);
        assert!(text.contains("<LOCX> 1.5\n"));
        assert!(text.contains("<LOCY> 2.25\n"));
        assert!(!text.contains(','));
    }

    #[test]
    fn crlf_lines_and_bad_numbers_read_leniently() {
        let result = read("<NEWP>\r\n<DAYS> x\r\n<HOUR> 4\r\n<LOCX> 1,5\r\n");
        let page = &result.pages[0];
        assert_eq!(page.timer_days, 0);
        assert_eq!(page.timer_hours, 4);
        assert_eq!(page.location_x, 0.0);
        assert!(page.has_location);
    }
}
