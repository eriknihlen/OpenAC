//! The relay token: full control of a character, so it is never printed.

use std::fmt;

/// A secret text. Its `Debug` output names no part of it, and nothing in
/// this crate writes it anywhere but the one request header it is for.
#[derive(Clone, PartialEq, Eq)]
pub struct Secret(String);

impl Secret {
    /// Wraps `text`.
    #[must_use]
    pub fn new(text: impl Into<String>) -> Self {
        Self(text.into())
    }

    /// The text, for the request header.
    pub(crate) fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Secret(hidden)")
    }
}

#[cfg(test)]
mod tests {
    use super::Secret;

    #[test]
    fn debug_hides_the_text() {
        let secret = Secret::new("hunter2-token");
        let shown = format!("{secret:?} {secret:#?}");
        assert!(!shown.contains("hunter2"), "{shown}");
        assert_eq!(secret.expose(), "hunter2-token");
    }
}
