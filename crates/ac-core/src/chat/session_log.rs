//! The rule half of the chat session log (`@log <name>`): the file name.
//! Writing the file is the runtime's job.

/// The log file name for what the player typed: an extensionless name gets
/// `.txt`; a name with an extension is kept as is.
#[must_use]
pub fn ensure_extension(name: &str) -> String {
    if has_extension(name) {
        name.to_owned()
    } else {
        format!("{name}.txt")
    }
}

/// True when the last path component has a dot followed by something.
fn has_extension(path: &str) -> bool {
    let file_name = path.rsplit(['/', '\\']).next().unwrap_or(path);
    file_name
        .rfind('.')
        .is_some_and(|dot| dot + 1 < file_name.len())
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/ChatSessionLogTests.cs
    #[test]
    fn an_extensionless_name_gains_dot_txt() {
        for (given, expected) in [
            ("aclog", "aclog.txt"),
            ("aclog.txt", "aclog.txt"),
            ("aclog.log", "aclog.log"),
            ("chat.old", "chat.old"),
        ] {
            assert_eq!(ensure_extension(given), expected);
        }
    }

    #[test]
    fn a_dot_in_a_directory_name_is_not_an_extension() {
        assert_eq!(ensure_extension("logs.d/aclog"), "logs.d/aclog.txt");
        assert_eq!(ensure_extension(r"logs.d\aclog"), r"logs.d\aclog.txt");
        assert_eq!(ensure_extension("aclog."), "aclog..txt");
    }
}
