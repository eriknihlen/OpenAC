//! The system clipboard for the UI's text fields: copy and cut write it,
//! paste reads it (OpenAC reads and writes the window's clipboard in the
//! field's key handler; here the field asks and the window answers).
//! Opened on first use, kept for the window's life.

/// Text on the system clipboard.
pub struct SystemClipboard {
    clipboard: Option<arboard::Clipboard>,
    failure_reported: bool,
}

impl std::fmt::Debug for SystemClipboard {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SystemClipboard")
            .field("open", &self.clipboard.is_some())
            .finish()
    }
}

impl Default for SystemClipboard {
    fn default() -> Self {
        Self::new()
    }
}

impl SystemClipboard {
    /// A clipboard opened when first used.
    #[must_use]
    pub fn new() -> Self {
        Self {
            clipboard: None,
            failure_reported: false,
        }
    }

    fn open(&mut self) -> Result<&mut arboard::Clipboard, String> {
        if self.clipboard.is_none() {
            self.clipboard = Some(arboard::Clipboard::new().map_err(|e| e.to_string())?);
        }
        self.clipboard
            .as_mut()
            .ok_or_else(|| "the clipboard is not open".to_owned())
    }

    /// The clipboard's text: empty when it holds none. `Err` names a failure
    /// the first time only (later ones read as empty).
    pub fn text(&mut self) -> Result<String, Option<String>> {
        let result = self.open().and_then(|c| match c.get_text() {
            Ok(text) => Ok(text),
            Err(arboard::Error::ContentNotAvailable) => Ok(String::new()),
            Err(e) => Err(e.to_string()),
        });
        result.map_err(|e| self.report(e))
    }

    /// Put `text` on the clipboard. `Err` as for [`SystemClipboard::text`].
    pub fn set_text(&mut self, text: &str) -> Result<(), Option<String>> {
        let result = self
            .open()
            .and_then(|c| c.set_text(text.to_owned()).map_err(|e| e.to_string()));
        result.map_err(|e| self.report(e))
    }

    fn report(&mut self, error: String) -> Option<String> {
        (!std::mem::replace(&mut self.failure_reported, true)).then_some(error)
    }
}
