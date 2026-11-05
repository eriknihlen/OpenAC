//! What kind each console line is, for a launcher that shows only what a
//! player would see.
//!
//! A launcher that sets [`CONSOLE_KINDS_VARIABLE`] to `1` asks the client to
//! begin every console line a player would see with a mark: the record
//! separator character and one letter for the line's [`ConsoleLineKind`].
//! A line without a mark is the client's own log (the session's life, its
//! plugins, the window, diagnostics). The mark is a control character, so it
//! cannot be typed into chat and no text of the game starts with it.
//! Without the variable nothing is marked, so a person reading the console
//! in a terminal never sees one.

/// The environment variable a launcher sets to `1` to have console lines
/// marked with their kind.
pub const CONSOLE_KINDS_VARIABLE: &str = "OPENAC_CONSOLE_KINDS";

/// The character every mark begins with (ASCII record separator).
pub const MARK: char = '\u{1e}';

/// Whether the launcher asked for marked console lines (`read` looks up an
/// environment variable).
pub fn console_kinds_requested(read: impl Fn(&str) -> Option<String>) -> bool {
    read(CONSOLE_KINDS_VARIABLE).is_some_and(|value| value.trim() == "1")
}

/// A console line a player would see.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConsoleLineKind {
    /// A line the game's main chat window shows.
    Chat,
    /// A line of the status box at the top of the game view.
    Status,
    /// The console's answer to a typed line, or a question for the player.
    Reply,
    /// A line the player typed into the console.
    Typed,
}

impl ConsoleLineKind {
    /// The letter after [`MARK`].
    pub fn letter(self) -> char {
        match self {
            Self::Chat => 'c',
            Self::Status => 's',
            Self::Reply => 'r',
            Self::Typed => 't',
        }
    }

    /// The kind a letter names.
    pub fn from_letter(letter: char) -> Option<Self> {
        match letter {
            'c' => Some(Self::Chat),
            's' => Some(Self::Status),
            'r' => Some(Self::Reply),
            't' => Some(Self::Typed),
            _ => None,
        }
    }
}

/// `text` with every one of its lines marked as `kind`: a chat message the
/// server sent with line breaks in it stays one message of that kind.
pub fn mark_lines(kind: ConsoleLineKind, text: &str) -> String {
    let mut out = String::with_capacity(text.len() + 4);
    for (index, line) in text.split('\n').enumerate() {
        if index > 0 {
            out.push('\n');
        }
        out.push(MARK);
        out.push(kind.letter());
        out.push_str(line);
    }
    out
}

/// A line's kind and its text without the mark; `None` for an unmarked line
/// (the client's log).
pub fn read_mark(line: &str) -> (Option<ConsoleLineKind>, &str) {
    let mut chars = line.chars();
    if chars.next() == Some(MARK)
        && let Some(kind) = chars.next().and_then(ConsoleLineKind::from_letter)
    {
        return (Some(kind), chars.as_str());
    }
    (None, line)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn marking_is_asked_for_with_one() {
        assert!(console_kinds_requested(|name| (name
            == CONSOLE_KINDS_VARIABLE)
            .then(|| " 1 ".into())));
        assert!(!console_kinds_requested(|_| None));
        assert!(!console_kinds_requested(|_| Some("0".into())));
    }

    #[test]
    fn a_marked_line_reads_back_as_its_kind_and_text() {
        for kind in [
            ConsoleLineKind::Chat,
            ConsoleLineKind::Status,
            ConsoleLineKind::Reply,
            ConsoleLineKind::Typed,
        ] {
            let marked = mark_lines(kind, "[say] You say, \"hi\"");
            assert_eq!(read_mark(&marked), (Some(kind), "[say] You say, \"hi\""));
        }
    }

    #[test]
    fn every_line_of_a_message_is_marked() {
        let marked = mark_lines(
            ConsoleLineKind::Chat,
            "[system] Welcome\n  powered by ACEmulator",
        );
        let lines: Vec<_> = marked.split('\n').map(read_mark).collect();
        assert_eq!(
            lines,
            [
                (Some(ConsoleLineKind::Chat), "[system] Welcome"),
                (Some(ConsoleLineKind::Chat), "  powered by ACEmulator"),
            ]
        );
    }

    #[test]
    fn an_unmarked_line_is_the_log() {
        assert_eq!(
            read_mark("-- lifecycle: generation 1 InWorld"),
            (None, "-- lifecycle: generation 1 InWorld")
        );
        // A mark with a letter no kind has is not a mark.
        assert_eq!(read_mark("\u{1e}x text"), (None, "\u{1e}x text"));
        assert_eq!(read_mark(""), (None, ""));
    }
}
