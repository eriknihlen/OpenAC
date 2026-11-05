//! Turning what a windowless session writes into coloured runs for the
//! session console: a line coloured with terminal escapes keeps exactly
//! those colours; an uncoloured line takes the colour the game shows its
//! tag's kind of line in. A line the client marked with its kind (see
//! `ac_host_core::console_kind`) is shown without the mark; the console
//! shows only marked lines unless its Verbose is on.

use ac_host_core::console_kind::{ConsoleLineKind, read_mark};

/// One stretch of console text in one colour.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionConsoleRun {
    /// The text, with no escape sequences in it.
    pub text: String,
    /// The colour as `0xRRGGBB`, or `None` for the console's default.
    pub color: Option<u32>,
    /// Whether the session marked the text as a quiet notice of its own.
    pub dim: bool,
}

const ESCAPE: char = '\u{1b}';

/// What a typed line is shown behind.
pub const TYPED_PREFIX: &str = "> ";

/// One console line: whether a player would see it, and its runs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionConsoleLine {
    /// The kind the client marked it with; `None` for the client's log.
    pub kind: Option<ConsoleLineKind>,
    /// The text in its colours, without the mark.
    pub runs: Vec<SessionConsoleRun>,
}

/// Reads one line the session wrote: its mark, then its runs. A line the
/// player typed is shown behind [`TYPED_PREFIX`].
pub fn read_line(line: &str) -> SessionConsoleLine {
    let (kind, text) = read_mark(line);
    let runs = if kind == Some(ConsoleLineKind::Typed) {
        vec![SessionConsoleRun {
            text: format!("{TYPED_PREFIX}{text}"),
            color: None,
            dim: false,
        }]
    } else {
        parse_line(text)
    };
    SessionConsoleLine { kind, runs }
}

/// Splits one line into its runs.
pub fn parse_line(line: &str) -> Vec<SessionConsoleRun> {
    if !line.contains(ESCAPE) {
        return vec![SessionConsoleRun {
            text: line.to_owned(),
            color: color_for_tag(line),
            dim: line.starts_with("-- "),
        }];
    }
    let mut runs = Vec::new();
    let mut color = None;
    let mut dim = false;
    let mut position = 0;
    while position < line.len() {
        let Some(offset) = line[position..].find(ESCAPE) else {
            add(&mut runs, &line[position..], color, dim);
            break;
        };
        let escape = position + offset;
        add(&mut runs, &line[position..escape], color, dim);
        // Only "set graphics" sequences mean anything here: ESC [ numbers m.
        let end = if line[escape + 1..].starts_with('[') {
            line[escape + 2..].find('m').map(|index| escape + 2 + index)
        } else {
            None
        };
        match end {
            Some(end) => {
                apply(&line[escape + 2..end], &mut color, &mut dim);
                position = end + 1;
            }
            None => position = escape + ESCAPE.len_utf8(),
        }
    }
    runs
}

/// The colour of a line the session did not colour itself, from the tag
/// it puts in front of every chat line; `None` when there is no tag or the
/// kind has no colour of its own.
pub fn color_for_tag(line: &str) -> Option<u32> {
    if line.chars().count() < 3 || !line.starts_with('[') {
        return None;
    }
    let close = line.find(']')?;
    match &line[1..close] {
        "say" => Some(0xFF_FFFF),
        "tell" | "admin" | "social" | "fellowship" => Some(0xFF_FF3F),
        "system" => Some(0xFF_7FFF),
        "combat" | "help" => Some(0xFF_3F3F),
        "magic" | "spell" => Some(0x3F_BFFF),
        "channel" => Some(0xFF_9696),
        "emote" => Some(0xD2_D2C8),
        "advance" => Some(0x3F_DCDC),
        "allegiance" => Some(0xEE_921E),
        "abuse" | "general" | "trade" | "lfg" | "roleplay" | "society" => Some(0xB4_DCF0),
        "all" | "msg" | "appraise" | "broadcast" | "recall" | "craft" | "salvage" => {
            Some(0x7F_FF7F)
        }
        _ => None,
    }
}

fn add(runs: &mut Vec<SessionConsoleRun>, text: &str, color: Option<u32>, dim: bool) {
    if !text.is_empty() {
        runs.push(SessionConsoleRun {
            text: text.to_owned(),
            color,
            dim,
        });
    }
}

fn apply(parameters: &str, color: &mut Option<u32>, dim: &mut bool) {
    let parts: Vec<&str> = if parameters.is_empty() {
        vec!["0"]
    } else {
        parameters.split(';').collect()
    };
    let mut index = 0;
    while index < parts.len() {
        if let Ok(code) = parts[index].parse::<i32>() {
            match code {
                0 => {
                    *color = None;
                    *dim = false;
                }
                2 => *dim = true,
                22 => *dim = false,
                39 => *color = None,
                38 if index + 4 < parts.len() && parts[index + 1] == "2" => {
                    if let (Ok(red), Ok(green), Ok(blue)) = (
                        parts[index + 2].parse::<u8>(),
                        parts[index + 3].parse::<u8>(),
                        parts[index + 4].parse::<u8>(),
                    ) {
                        *color =
                            Some(u32::from(red) << 16 | u32::from(green) << 8 | u32::from(blue));
                        index += 4;
                    }
                }
                _ => {}
            }
        }
        index += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from OpenAC tests/.../Launching/SessionConsoleTextTests.cs.

    const ESC: &str = "\u{1b}";

    #[test]
    fn a_coloured_line_is_shown_in_that_colour_with_no_escapes_left_in_it() {
        let runs = parse_line(&format!(
            "{ESC}[38;2;63;191;255m[magic] You resist the spell{ESC}[0m"
        ));
        assert_eq!(
            runs,
            [SessionConsoleRun {
                text: "[magic] You resist the spell".into(),
                color: Some(0x3F_BFFF),
                dim: false
            }]
        );
    }

    #[test]
    fn a_session_notice_is_dim_whether_or_not_it_says_so() {
        let marked = parse_line(&format!("{ESC}[2m-- entered world{ESC}[0m"));
        assert_eq!(marked.len(), 1);
        assert_eq!(marked[0].text, "-- entered world");
        assert!(marked[0].dim);
        let plain = parse_line("-- entered world");
        assert!(plain[0].dim);
    }

    #[test]
    fn an_uncoloured_line_takes_its_colour_from_its_tag() {
        for (line, expected) in [
            ("[say] You say, \"hello\"", 0xFF_FFFF),
            ("[tell] Bob tells you, \"hi\"", 0xFF_FF3F),
            ("[combat] You evaded Drudge!", 0xFF_3F3F),
            ("[magic] You resist the spell", 0x3F_BFFF),
            ("[msg] [MossTank] Loaded settings profile", 0x7F_FF7F),
        ] {
            let runs = parse_line(line);
            assert_eq!(runs.len(), 1);
            assert_eq!(runs[0].text, line);
            assert_eq!(runs[0].color, Some(expected), "{line}");
        }
    }

    #[test]
    fn a_line_with_no_tag_has_no_colour_of_its_own() {
        let runs = parse_line("  powered by ACEmulator");
        assert_eq!(runs[0].color, None);
        assert!(!runs[0].dim);
    }

    #[test]
    fn an_escape_that_is_not_a_colour_is_dropped_and_the_text_kept() {
        let runs = parse_line(&format!("before{ESC}[1;31mafter{ESC}[Kend"));
        let joined: String = runs.iter().take(2).map(|run| run.text.as_str()).collect();
        assert_eq!(joined, "beforeafter");
        assert!(runs.iter().all(|run| !run.text.contains(ESCAPE)));
    }

    #[test]
    fn a_marked_line_is_shown_without_its_mark_and_in_its_tags_colour() {
        let line = read_line("\u{1e}c[tell] Bob tells you, \"hi\"");
        assert_eq!(line.kind, Some(ConsoleLineKind::Chat));
        assert_eq!(
            line.runs,
            [SessionConsoleRun {
                text: "[tell] Bob tells you, \"hi\"".into(),
                color: Some(0xFF_FF3F),
                dim: false
            }]
        );
    }

    #[test]
    fn a_typed_line_is_shown_behind_the_prompt() {
        let line = read_line("\u{1e}t/objects");
        assert_eq!(line.kind, Some(ConsoleLineKind::Typed));
        assert_eq!(line.runs[0].text, "> /objects");
    }

    #[test]
    fn an_unmarked_line_is_the_log() {
        let line = read_line("-- lifecycle: generation 1 InWorld");
        assert_eq!(line.kind, None);
        assert!(line.runs[0].dim);
    }
}
