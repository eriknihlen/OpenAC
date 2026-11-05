//! Authored UI text: string-table lookup, template composition, the retail
//! escape language, and the string-key hash.

use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{StringInfo, StringTable};

const META_CHARACTERS: &str = "[]!{}#\\|^$";

fn unescaped_char(c: char) -> Option<char> {
    match c {
        'n' => Some('\n'),
        'q' => Some('"'),
        'r' => Some('\r'),
        't' => Some('\t'),
        c if c != '\0' && META_CHARACTERS.contains(c) => Some(c),
        _ => None,
    }
}

fn escaped_char(c: char) -> Option<char> {
    match c {
        '\t' => Some('t'),
        '\n' => Some('n'),
        '\r' => Some('r'),
        '"' => Some('q'),
        c if c != '\0' && META_CHARACTERS.contains(c) => Some(c),
        _ => None,
    }
}

/// Decode the stored escapes: `\n`, `\q` (quote), `\r`, `\t`, and a
/// backslash before any metalanguage character. Any other pair stays as
/// written; NUL characters are dropped.
#[must_use]
pub fn unescape(value: &str) -> String {
    if !value.contains('\\') {
        return value.to_owned();
    }
    let chars: Vec<char> = value.chars().collect();
    let mut out = String::with_capacity(value.len());
    let mut i = 0;
    while i < chars.len() {
        let current = chars[i];
        let next = chars.get(i + 1).copied().unwrap_or('\0');
        match unescaped_char(next) {
            Some(u) if current == '\\' => {
                out.push(u);
                i += 2;
                continue;
            }
            _ => {}
        }
        if current != '\0' {
            out.push(current);
        }
        i += 1;
    }
    out
}

/// The storage form of `value`: the inverse of [`unescape`].
#[must_use]
pub fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.chars() {
        if let Some(e) = escaped_char(c) {
            out.push('\\');
            out.push(e);
        } else if c != '\0' {
            out.push(c);
        }
    }
    out
}

/// The string-table key of a name: the client's 4-bit rolling hash over the
/// low byte of each character, never `u32::MAX`.
#[must_use]
pub fn compute_hash(value: &str) -> u32 {
    let mut result: u32 = 0;
    for c in value.chars() {
        result = (result << 4).wrapping_add(u32::from(c as u32 as u8));
        let high = result & 0xF000_0000;
        if high != 0 {
            result = ((high >> 24) ^ result) & 0x0FFF_FFFF;
        }
    }
    if result == u32::MAX {
        u32::MAX - 1
    } else {
        result
    }
}

/// The template variable naming the player.
pub const PLAYER_VARIABLE: u32 = 0x0550_6DA2;

/// Resolves authored strings from string tables, loading each table once.
pub struct StringResolver {
    load: Box<dyn Fn(u32) -> Option<StringTable>>,
    tables: RefCell<HashMap<u32, Option<Arc<StringTable>>>>,
}

impl std::fmt::Debug for StringResolver {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StringResolver")
            .field("tables", &self.tables.borrow().len())
            .finish()
    }
}

impl StringResolver {
    /// A resolver that loads table `id` with `load` the first time it is
    /// asked for.
    pub fn new(load: impl Fn(u32) -> Option<StringTable> + 'static) -> Self {
        Self {
            load: Box::new(load),
            tables: RefCell::new(HashMap::new()),
        }
    }

    fn table(&self, id: u32) -> Option<Arc<StringTable>> {
        if let Some(t) = self.tables.borrow().get(&id) {
            return t.clone();
        }
        let t = (self.load)(id).map(Arc::new);
        self.tables.borrow_mut().insert(id, t.clone());
        t
    }

    /// The text a string-info property names (its token picks the variant).
    #[must_use]
    pub fn resolve_info(&self, info: &StringInfo) -> Option<String> {
        self.resolve(info.table_id, info.string_id, i32::from(info.token))
    }

    /// Variant `token` (or the first, when out of range) of string
    /// `string_id` in table `table_id`, escapes decoded. `None` for a zero
    /// id, a missing table or string, or a string without text.
    #[must_use]
    pub fn resolve(&self, table_id: u32, string_id: u32, token: i32) -> Option<String> {
        if table_id == 0 || string_id == 0 {
            return None;
        }
        let table = self.table(table_id)?;
        let entry = table.get(string_id)?;
        if entry.strings.is_empty() {
            return None;
        }
        let index = usize::try_from(token)
            .ok()
            .filter(|&i| i < entry.strings.len())
            .unwrap_or(0);
        Some(unescape(&entry.strings[index]))
    }

    /// Every variant of a string, escapes decoded.
    #[must_use]
    pub fn resolve_all(&self, table_id: u32, string_id: u32) -> Option<Vec<String>> {
        if table_id == 0 || string_id == 0 {
            return None;
        }
        let table = self.table(table_id)?;
        let entry = table.get(string_id)?;
        if entry.strings.is_empty() {
            return None;
        }
        Some(entry.strings.iter().map(|s| unescape(s)).collect())
    }

    /// A template by name: its fragments with each variable's value between
    /// them (a missing variable is empty). Values are escaped before the
    /// whole is decoded, so they come out verbatim.
    #[must_use]
    pub fn resolve_template(
        &self,
        table_id: u32,
        key: &str,
        variables: &HashMap<u32, String>,
    ) -> Option<String> {
        self.resolve_template_hash(table_id, compute_hash(key), variables)
    }

    /// [`StringResolver::resolve_template`] by key hash.
    #[must_use]
    pub fn resolve_template_hash(
        &self,
        table_id: u32,
        key_hash: u32,
        variables: &HashMap<u32, String>,
    ) -> Option<String> {
        if table_id == 0 {
            return None;
        }
        let table = self.table(table_id)?;
        let entry = table.get(key_hash)?;
        if entry.strings.is_empty() {
            return None;
        }
        let mut composed = String::new();
        for (i, fragment) in entry.strings.iter().enumerate() {
            composed.push_str(fragment);
            if let Some(value) = entry.variables.get(i).and_then(|v| variables.get(v)) {
                composed.push_str(&escape(value));
            }
        }
        Some(unescape(&composed))
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/RetailStringEscapesTests.cs`
    //! and `DatStringResolverTemplateTests.cs`.

    use super::*;
    use ac_dat::objects::StringTableString;

    const TABLE_ID: u32 = 0x2300_0001;

    fn resolver(key: &str, fragments: &[&str], variables: &[u32]) -> StringResolver {
        let entry = StringTableString {
            data_id: 0,
            strings: fragments.iter().map(|s| (*s).to_owned()).collect(),
            variables: variables.to_vec(),
            is_var_name_table_worth_packing: false,
        };
        let table = StringTable {
            id: TABLE_ID,
            language: 1,
            strings: vec![(compute_hash(key), entry)],
        };
        StringResolver::new(move |id| (id == TABLE_ID).then(|| table.clone()))
    }

    #[test]
    fn unescape_decodes_the_four_character_escapes() {
        assert_eq!(unescape("line one\\nline two"), "line one\nline two");
        assert_eq!(unescape("a\\tb"), "a\tb");
        assert_eq!(unescape("a\\rb"), "a\rb");
        assert_eq!(unescape("say \\qhi\\q"), "say \"hi\"");
    }

    #[test]
    fn unescape_decodes_every_metalanguage_self_escape() {
        for c in META_CHARACTERS.chars() {
            assert_eq!(unescape(&format!("\\{c}")), c.to_string());
        }
    }

    #[test]
    fn unescape_keeps_unrecognised_pairs() {
        for raw in [
            "\\z",
            "C:\\path\\dir",
            "ends with \\",
            "\\N upper is not an escape",
        ] {
            assert_eq!(unescape(raw), raw);
        }
        assert_eq!(unescape("\\\\n"), "\\n");
        assert_eq!(unescape(""), "");
        assert_eq!(unescape("Please Wait"), "Please Wait");
    }

    #[test]
    fn escape_is_the_storage_inverse() {
        for (plain, stored) in [
            ("line one\nline two", "line one\\nline two"),
            ("a\tb", "a\\tb"),
            ("a\rb", "a\\rb"),
            ("say \"hi\"", "say \\qhi\\q"),
            ("[x]", "\\[x\\]"),
            ("back\\slash", "back\\\\slash"),
            ("plain", "plain"),
        ] {
            assert_eq!(escape(plain), stored);
        }
        for value in [
            "plain name",
            "Odd\\Name",
            "multi\nline",
            "tabs\tand \"quotes\"",
            "[]!{}#\\|^$",
            "",
        ] {
            assert_eq!(unescape(&escape(value)), value);
        }
    }

    #[test]
    fn player_variable_is_the_retail_hash() {
        assert_eq!(compute_hash("PLAYER"), PLAYER_VARIABLE);
    }

    #[test]
    fn templates_compose_fragments_and_variables() {
        let r = resolver(
            "ID_Allegiance_SwearConfirmation",
            &["Do you wish to swear to ", "?"],
            &[PLAYER_VARIABLE],
        );
        let vars = HashMap::from([(PLAYER_VARIABLE, "+Horan".to_owned())]);
        assert_eq!(
            r.resolve_template(TABLE_ID, "ID_Allegiance_SwearConfirmation", &vars)
                .as_deref(),
            Some("Do you wish to swear to +Horan?")
        );
        assert_eq!(
            r.resolve_template(TABLE_ID, "ID_Allegiance_SwearConfirmation", &HashMap::new())
                .as_deref(),
            Some("Do you wish to swear to ?")
        );
        let r = resolver(
            "ID_Fellowship_FellowshipRequest",
            &[
                "",
                " has invited you to join their fellowship. Do you accept?",
            ],
            &[PLAYER_VARIABLE],
        );
        let vars = HashMap::from([(PLAYER_VARIABLE, "+Acdream".to_owned())]);
        assert_eq!(
            r.resolve_template(TABLE_ID, "ID_Fellowship_FellowshipRequest", &vars)
                .as_deref(),
            Some("+Acdream has invited you to join their fellowship. Do you accept?")
        );
    }

    #[test]
    fn resolution_decodes_escapes_and_keeps_variables_verbatim() {
        let r = resolver(
            "ID_Confirm_Exit",
            &["This will exit your character from the game world.\\n\\nAre you sure?"],
            &[],
        );
        assert_eq!(
            r.resolve(TABLE_ID, compute_hash("ID_Confirm_Exit"), 0)
                .as_deref(),
            Some("This will exit your character from the game world.\n\nAre you sure?")
        );
        let r = resolver("ID_Variants", &["one\\nline", "two\\tcol"], &[]);
        assert_eq!(
            r.resolve_all(TABLE_ID, compute_hash("ID_Variants")),
            Some(vec!["one\nline".to_owned(), "two\tcol".to_owned()])
        );
        assert_eq!(
            r.resolve(TABLE_ID, compute_hash("ID_Variants"), 1)
                .as_deref(),
            Some("two\tcol")
        );
        assert_eq!(
            r.resolve(TABLE_ID, compute_hash("ID_Variants"), 7)
                .as_deref(),
            Some("one\nline")
        );
        let r = resolver(
            "ID_Delete_Confirmation",
            &["Delete ", "?\\nType 'DELETE' to confirm."],
            &[PLAYER_VARIABLE],
        );
        let vars = HashMap::from([(PLAYER_VARIABLE, "Odd\\nName".to_owned())]);
        assert_eq!(
            r.resolve_template(TABLE_ID, "ID_Delete_Confirmation", &vars)
                .as_deref(),
            Some("Delete Odd\\nName?\nType 'DELETE' to confirm.")
        );
    }

    #[test]
    fn unknown_keys_and_zero_ids_resolve_to_none() {
        let r = resolver("ID_Known", &["x"], &[]);
        assert_eq!(
            r.resolve_template(TABLE_ID, "ID_Unknown", &HashMap::new()),
            None
        );
        assert_eq!(r.resolve(TABLE_ID, 0, 0), None);
        assert_eq!(r.resolve(0, compute_hash("ID_Known"), 0), None);
        assert_eq!(r.resolve(0x2300_0099, compute_hash("ID_Known"), 0), None);
    }
}
