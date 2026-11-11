//! The clipboard behind the UI's text fields: copy and cut write it, paste
//! reads it. The host hands one in (the system clipboard natively); until
//! it does, the window has none.

/// A clipboard the window reads and writes.
///
/// A failure is `Err(Some(message))` when it is worth telling the player
/// and `Err(None)` when it is not (a failure already told, or a read the
/// host answers later).
pub trait Clipboard {
    /// The clipboard's text: empty when it holds none.
    fn text(&mut self) -> Result<String, Option<String>>;

    /// Put `text` on the clipboard.
    fn set_text(&mut self, text: &str) -> Result<(), Option<String>>;
}

/// No clipboard: it holds nothing and keeps nothing.
#[derive(Debug, Default, Clone, Copy)]
pub struct NoClipboard;

impl Clipboard for NoClipboard {
    fn text(&mut self) -> Result<String, Option<String>> {
        Ok(String::new())
    }

    fn set_text(&mut self, _text: &str) -> Result<(), Option<String>> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_clipboard_holds_nothing() {
        let mut clipboard = NoClipboard;
        assert_eq!(clipboard.set_text("kept nowhere"), Ok(()));
        assert_eq!(clipboard.text(), Ok(String::new()));
    }
}
