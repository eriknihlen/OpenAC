//! `controls\controls.ini` in the game folder: a player's style sheet of
//! `[section]` blocks with `key=value` lines. The interface reads colour
//! overrides from it (`#AARRGGBB`), such as a plugin panel's title colour.
//! Section and key names ignore case; a later duplicate wins; `;` and `#`
//! start a comment line; lines outside a section are ignored.

use std::collections::HashMap;
use std::path::Path;

use crate::draw::Color;

/// See the module documentation.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ControlsIni {
    /// Lower-cased section name → lower-cased key → value.
    sections: HashMap<String, HashMap<String, String>>,
}

impl ControlsIni {
    /// Read the file at `path`; a missing file is an empty sheet.
    ///
    /// # Errors
    /// The file exists but cannot be read.
    pub fn load(path: &Path) -> std::io::Result<Self> {
        match std::fs::read(path) {
            Ok(bytes) => Ok(Self::parse(&String::from_utf8_lossy(&bytes))),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(Self::default()),
            Err(e) => Err(e),
        }
    }

    /// The sheet in `text`.
    #[must_use]
    pub fn parse(text: &str) -> Self {
        let mut sections: HashMap<String, HashMap<String, String>> = HashMap::new();
        let mut current: Option<String> = None;
        for raw in text.split('\n') {
            let line = raw.trim();
            if line.is_empty() || line.starts_with(';') || line.starts_with('#') {
                continue;
            }
            if line.len() >= 2 && line.starts_with('[') && line.ends_with(']') {
                let name = line[1..line.len() - 1].trim().to_lowercase();
                // A repeated section starts afresh, as the source's parser
                // replaces it.
                sections.insert(name.clone(), HashMap::new());
                current = Some(name);
                continue;
            }
            let (Some(at), Some(section)) = (line.find('='), current.as_ref()) else {
                continue;
            };
            if at == 0 {
                continue;
            }
            if let Some(keys) = sections.get_mut(section) {
                keys.insert(
                    line[..at].trim().to_lowercase(),
                    line[at + 1..].trim().to_owned(),
                );
            }
        }
        Self { sections }
    }

    /// The value of `key` in `section`.
    #[must_use]
    pub fn get(&self, section: &str, key: &str) -> Option<&str> {
        self.sections
            .get(&section.to_lowercase())?
            .get(&key.to_lowercase())
            .map(String::as_str)
    }

    /// The colour of `key` in `section`: exactly `#AARRGGBB` (alpha first),
    /// else none.
    #[must_use]
    pub fn color(&self, section: &str, key: &str) -> Option<Color> {
        let value = self.get(section, key)?;
        let hex = value
            .strip_prefix('#')
            .filter(|h| value.len() == 9 && h.bytes().all(|b| b.is_ascii_hexdigit()))?;
        let argb = u32::from_str_radix(hex, 16).ok()?;
        let channel = |shift: u32| ((argb >> shift) & 0xFF) as f32 / 255.0;
        Some(Color {
            r: channel(16),
            g: channel(8),
            b: channel(0),
            a: channel(24),
        })
    }
}

#[cfg(test)]
mod tests {
    //! Golden values: OpenAC `tests/AcDream.App.Tests/UI/ControlsIniTests.cs`.

    use super::*;

    #[test]
    fn parse_reads_section_tokens() {
        let ini = ControlsIni::parse(
            "[title]\nheight=19\ncolor=#FFFFFFFF\nfont=font://Verdana-10-bold\n\
             [body]\nbgcolor=#00000000\ncolor_border=#FF4F657D\n",
        );
        assert_eq!(ini.get("title", "height"), Some("19"));
        assert_eq!(ini.get("title", "font"), Some("font://Verdana-10-bold"));
        assert_eq!(ini.get("title", "missing"), None);
        assert_eq!(ini.get("nosuch", "height"), None);
    }

    #[test]
    fn a_colour_is_alpha_first_hex() {
        let ini = ControlsIni::parse("[body]\ncolor_border=#FF4F657D\n");
        let c = ini.color("body", "color_border").unwrap();
        assert!((c.a - 0xFF as f32 / 255.0).abs() < 1e-5);
        assert!((c.r - 0x4F as f32 / 255.0).abs() < 1e-5);
        assert!((c.g - 0x65 as f32 / 255.0).abs() < 1e-5);
        assert!((c.b - 0x7D as f32 / 255.0).abs() < 1e-5);
    }

    #[test]
    fn a_missing_file_is_an_empty_sheet() {
        let ini = ControlsIni::load(Path::new("Z:/does/not/exist/controls.ini")).unwrap();
        assert_eq!(ini.get("title", "height"), None);
    }

    #[test]
    fn names_ignore_case_comments_and_strays_are_skipped() {
        let ini = ControlsIni::parse(
            "stray=1\n; comment\n# comment\r\n[Title]\r\n Color = #80102030 \r\n=x\nnoequals\n",
        );
        assert_eq!(ini.get("stray", "stray"), None);
        assert_eq!(ini.get("TITLE", "COLOR"), Some("#80102030"));
        let c = ini.color("title", "color").unwrap();
        assert!((c.a - 128.0 / 255.0).abs() < 1e-5);
        // Not exactly nine characters, or not hex: no colour.
        for bad in ["#FFF", "FF4F657DAA", "#GG4F657D", "#FF4F657D0", "#+F4F657D"] {
            let ini = ControlsIni::parse(&format!("[t]\nc={bad}\n"));
            assert_eq!(ini.color("t", "c"), None, "{bad}");
        }
    }
}
