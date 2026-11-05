//! How a handler inside a `<repeat>` row names its row. In a window binding
//! a handler is a name (`onclick="{Go}"` fires `Go`); in a row it belongs
//! to that row's item, so the plugin hears which one: each enclosing row as
//! `List[index]` (a repeat without `key`) or `List[index:key]`, outermost
//! first, then the handler's name, joined by `/`:
//! `Sections[0:a]/Rows[2:7]/SetOn`. The index is the row's place in the
//! list the window last read; the key is the item's key as text, with `%`
//! and `/` written `%25` and `%2F` (as a remembered row path stores it).
//! The plugin SDK reads the same text (`ac_plugin_abi::ui::RowAction`).

use crate::control_state::escape_key;

/// One enclosing row of a row handler.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RowStep {
    /// The list's binding name (on the window, or on the enclosing item).
    pub list: String,
    /// The row's place in the list.
    pub index: usize,
    /// The row's key as text (a repeat with `key`).
    pub key: Option<String>,
}

/// The text one row adds in front of a handler: `List[index]/` or
/// `List[index:key]/`.
#[must_use]
pub fn step_text(list: &str, index: usize, key: Option<&str>) -> String {
    match key {
        Some(k) => format!("{list}[{index}:{}]/", escape_key(k)),
        None => format!("{list}[{index}]/"),
    }
}

/// A handler name split into its rows and the handler; `None` for a
/// window handler (no `/`) or text that is not a row handler.
#[must_use]
pub fn split(handler: &str) -> Option<(Vec<RowStep>, &str)> {
    let (path, name) = handler.rsplit_once('/')?;
    if name.is_empty() {
        return None;
    }
    let steps = path
        .split('/')
        .map(parse_step)
        .collect::<Option<Vec<_>>>()?;
    Some((steps, name))
}

fn parse_step(text: &str) -> Option<RowStep> {
    let (list, rest) = text.split_once('[')?;
    let inside = rest.strip_suffix(']')?;
    let (index, key) = match inside.split_once(':') {
        Some((i, k)) => (i, Some(unescape_key(k))),
        None => (inside, None),
    };
    if list.is_empty() || index.is_empty() || !index.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    Some(RowStep {
        list: list.to_owned(),
        index: index.parse().ok()?,
        key,
    })
}

fn unescape_key(text: &str) -> String {
    text.replace("%2F", "/").replace("%25", "%")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn steps_read_back_with_their_keys_unescaped() {
        let text = format!(
            "{}{}SetOn",
            step_text("Sections", 0, Some("a/b")),
            step_text("Rows", 12, None)
        );
        assert_eq!(text, "Sections[0:a%2Fb]/Rows[12]/SetOn");
        let (steps, handler) = split(&text).unwrap();
        assert_eq!(handler, "SetOn");
        assert_eq!(
            steps,
            vec![
                RowStep {
                    list: "Sections".into(),
                    index: 0,
                    key: Some("a/b".into()),
                },
                RowStep {
                    list: "Rows".into(),
                    index: 12,
                    key: None,
                },
            ]
        );
        let odd = format!("{}Go", step_text("Rows", 1, Some("50%:x]")));
        assert_eq!(split(&odd).unwrap().0[0].key.as_deref(), Some("50%:x]"));
    }

    #[test]
    fn a_window_handler_or_malformed_text_is_no_row_handler() {
        assert_eq!(split("SetOn"), None);
        assert_eq!(split("Rows/SetOn"), None);
        assert_eq!(split("Rows[x]/SetOn"), None);
        assert_eq!(split("Rows[1]/"), None);
        assert_eq!(split("[1]/Go"), None);
    }
}
