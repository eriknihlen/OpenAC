//! The language filter: censors banned words in a chat line against the
//! pattern list the game ships.

const REPLACEMENT: &str = "****";

/// Replaces every space-delimited word that matches a pattern with `****`,
/// leaving punctuation attached to its word and every separator untouched.
/// Only the ASCII space separates words; a tab or a no-break space does not.
///
/// Words are compared lower-cased. A missing pattern list (`None`, the
/// table failed to load) fails closed and censors every word; a loaded but
/// empty list leaves the line untouched.
#[must_use]
pub fn censor(line: &str, patterns: Option<&[String]>) -> String {
    if patterns.is_some_and(<[String]>::is_empty) || line.is_empty() {
        return line.to_owned();
    }
    let mut out = String::with_capacity(line.len());
    for (index, word) in line.split(' ').enumerate() {
        if index > 0 {
            out.push(' ');
        }
        let banned =
            !word.is_empty() && patterns.is_none_or(|patterns| matches_any_pattern(word, patterns));
        out.push_str(if banned { REPLACEMENT } else { word });
    }
    out
}

/// Lower-cases one character the simple way (one character in, one out).
fn lower_simple(c: char) -> char {
    c.to_lowercase().next().unwrap_or(c)
}

fn matches_any_pattern(word: &str, patterns: &[String]) -> bool {
    let lowered: Vec<char> = word.chars().map(lower_simple).collect();
    patterns.iter().any(|pattern| {
        let pattern: Vec<char> = pattern.chars().collect();
        matches_chars(&lowered, &pattern)
    })
}

/// True when `word` matches `pattern`: `*` matches any run of characters,
/// including none; every other character must match exactly (no case
/// folding here); the whole word must be consumed.
#[must_use]
pub fn matches_pattern(word: &str, pattern: &str) -> bool {
    let word: Vec<char> = word.chars().collect();
    let pattern: Vec<char> = pattern.chars().collect();
    matches_chars(&word, &pattern)
}

fn matches_chars(word: &[char], pattern: &[char]) -> bool {
    let (mut w, mut p) = (0usize, 0usize);
    let mut star: Option<usize> = None;
    let mut resume_at = 0usize;
    while w < word.len() {
        if p < pattern.len() && pattern[p] == '*' {
            star = Some(p);
            p += 1;
            resume_at = w;
        } else if p < pattern.len() && pattern[p] == word[w] {
            w += 1;
            p += 1;
        } else if let Some(star) = star {
            p = star + 1;
            resume_at += 1;
            w = resume_at;
        } else {
            return false;
        }
    }
    while p < pattern.len() && pattern[p] == '*' {
        p += 1;
    }
    p == pattern.len()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn list(words: &[&str]) -> Vec<String> {
        words.iter().map(|w| (*w).to_owned()).collect()
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/ChatLanguageFilterTests.cs
    #[test]
    fn wildcard_consumes_the_whole_word() {
        let cases = [
            ("zork", "zork", true),
            ("zork", "Zork", false),
            ("zork", "zorks", false),
            ("zork", "zor*", true),
            ("zork", "*ork", true),
            ("zork", "z*k", true),
            ("zork", "*zork*", true),
            ("zork", "*", true),
            ("", "*", true),
            ("", "", true),
            ("zork", "", false),
            ("zork", "gnome", false),
        ];
        for (word, pattern, expected) in cases {
            assert_eq!(
                matches_pattern(word, pattern),
                expected,
                "{word} vs {pattern}"
            );
        }
    }

    #[test]
    fn censor_replaces_only_matching_words() {
        assert_eq!(censor("a zork b", Some(&list(&["zork"]))), "a **** b");
    }

    #[test]
    fn censor_multiple_spaces_preserves_spacing() {
        assert_eq!(censor("a  zork  b", Some(&list(&["zork"]))), "a  ****  b");
    }

    #[test]
    fn censor_only_ascii_space_is_a_word_boundary() {
        for line in ["a\tzork\tb", "a\nzork\r\nb", "a\u{00A0}zork b"] {
            assert_eq!(censor(line, Some(&list(&["zork"]))), line);
        }
    }

    #[test]
    fn censor_wildcard_pattern_swallows_a_tab_when_no_space_separates_it() {
        assert_eq!(censor("zork\tb", Some(&list(&["zork*"]))), "****");
    }

    #[test]
    fn censor_punctuation_stays_part_of_the_word() {
        assert_eq!(censor("zork!", Some(&list(&["zork"]))), "zork!");
    }

    #[test]
    fn censor_wildcard_pattern_covers_punctuation() {
        assert_eq!(censor("zork!", Some(&list(&["zork*"]))), "****");
    }

    #[test]
    fn censor_case_insensitive() {
        assert_eq!(censor("ZoRk", Some(&list(&["zork"]))), "****");
    }

    #[test]
    fn censor_no_patterns_returns_same_text() {
        assert_eq!(censor("hello world", Some(&[])), "hello world");
    }

    #[test]
    fn censor_patterns_missing_censors_every_word() {
        assert_eq!(censor("hello world", None), "**** ****");
    }

    #[test]
    fn censor_no_match_returns_same_text() {
        assert_eq!(censor("hello world", Some(&list(&["zork"]))), "hello world");
    }
}
