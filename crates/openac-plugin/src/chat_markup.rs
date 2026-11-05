//! The markup a plugin's own chat line may carry ([`crate::Host::chat_post`],
//! [`crate::Host::chat_post_with`]; OpenAC `PluginChatMarkup`).

/// Escapes text so that it shows exactly as written in a line a plugin
/// posts, with no tag in it read as a link. Use it on every piece of text
/// the plugin did not write itself, such as a player's tell it repeats:
/// otherwise a `<COORD:World:…>` tag that player typed becomes a working
/// link under the plugin's name, showing one place and reporting another.
/// A coordinate pair the escaped text shows in plain words is still a link
/// to exactly the place it shows.
///
/// Puts a backslash before every `<`; the chat drops that backslash when it
/// shows the line, so the reader sees the original text.
#[must_use]
pub fn escape(text: &str) -> String {
    text.replace('<', "\\<")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escape_puts_a_backslash_before_every_angle_bracket() {
        // OpenAC PR #224 ChatTagMarkupTests.EscapeOfNothingIsEmpty.
        assert_eq!(escape(""), "");
        assert_eq!(escape("<b>"), "\\<b>");
        assert_eq!(escape("a<b<c"), "a\\<b\\<c");
    }
}
