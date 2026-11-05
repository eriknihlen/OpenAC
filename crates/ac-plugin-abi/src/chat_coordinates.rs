//! Compass coordinate pairs in chat text, such as `42.1N 33.5E`: the one
//! matcher the plugin SDK's parser and the client's chat share, so a pair a
//! chat line shows (any line but combat text) is a link exactly when a
//! plugin would read it.
//!
//! A pair is a number and a compass letter, twice, separated by `,` `;` `/`
//! `|` or white space: one north-or-south and one east-or-west value, in
//! either order. A number may not continue a word or another number, and a
//! letter may not start a word, so `10n 20east` and `v2.1N 3E` hold no pair.
//! Pure code, no imports: nothing here changes the plugin ABI's layout.

/// One pair found in a text.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CoordinatePair {
    /// Where the pair starts, a byte offset into the text.
    pub start: usize,
    /// Its length in bytes.
    pub len: usize,
    /// East-west; west is negative.
    pub east_west: f64,
    /// North-south; south is negative.
    pub north_south: f64,
}

/// A character that belongs to a word: a letter, a digit or an underscore.
fn is_word(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}

/// One number of a pair: digits, optionally a point and more digits; its
/// value and where it ends.
fn number(chars: &[(usize, char)], at: usize) -> Option<(f64, usize)> {
    let digit = |i: usize| chars.get(i).is_some_and(|(_, c)| c.is_ascii_digit());
    let mut end = at;
    while digit(end) {
        end += 1;
    }
    if end == at {
        return None;
    }
    if chars.get(end).is_some_and(|(_, c)| *c == '.') && digit(end + 1) {
        end += 1;
        while digit(end) {
            end += 1;
        }
    }
    let text: String = chars[at..end].iter().map(|(_, c)| c).collect();
    text.parse().ok().map(|value| (value, end))
}

fn skip_space(chars: &[(usize, char)], mut at: usize) -> usize {
    while chars.get(at).is_some_and(|(_, c)| c.is_whitespace()) {
        at += 1;
    }
    at
}

/// A compass letter (any case) at `at` that does not start a word: the
/// letter and where it ends.
fn direction(chars: &[(usize, char)], at: usize) -> Option<(char, usize)> {
    let letter = chars
        .get(at)
        .map(|(_, c)| c.to_ascii_uppercase())
        .filter(|c| matches!(c, 'N' | 'S' | 'E' | 'W'))?;
    if chars.get(at + 1).is_some_and(|(_, c)| is_word(*c)) {
        return None;
    }
    Some((letter, at + 1))
}

/// One pair starting at character `at`: the two numbers and letters, and
/// the character where the match ends.
fn pair_at(chars: &[(usize, char)], at: usize) -> Option<(f64, char, f64, char, usize)> {
    if at > 0
        && chars
            .get(at - 1)
            .is_some_and(|(_, c)| is_word(*c) || *c == '.')
    {
        return None;
    }
    let (a, next) = number(chars, at)?;
    let (ad, after_letter) = direction(chars, skip_space(chars, next))?;
    let next = skip_space(chars, after_letter);
    let next = if chars
        .get(next)
        .is_some_and(|(_, c)| matches!(c, ',' | ';' | '/' | '|'))
    {
        skip_space(chars, next + 1)
    } else if next > after_letter {
        next
    } else {
        return None;
    };
    let (b, next) = number(chars, next)?;
    let (bd, end) = direction(chars, skip_space(chars, next))?;
    Some((a, ad, b, bd, end))
}

/// Every pair in `text`, in the order they appear, with where each stands.
/// A match that is two latitudes or two longitudes is passed over.
#[must_use]
pub fn find(text: &str) -> Vec<CoordinatePair> {
    let chars: Vec<(usize, char)> = text.char_indices().collect();
    let byte_at = |i: usize| chars.get(i).map_or(text.len(), |(b, _)| *b);
    let mut found = Vec::new();
    let mut at = 0;
    while at < chars.len() {
        let Some((mut a, mut ad, mut b, mut bd, end)) = pair_at(&chars, at) else {
            at += 1;
            continue;
        };
        let (start, stop) = (byte_at(at), byte_at(end));
        at = end;
        if !matches!(ad, 'N' | 'S') || !matches!(bd, 'E' | 'W') {
            if !matches!(ad, 'E' | 'W') || !matches!(bd, 'N' | 'S') {
                continue;
            }
            std::mem::swap(&mut a, &mut b);
            std::mem::swap(&mut ad, &mut bd);
        }
        found.push(CoordinatePair {
            start,
            len: stop - start,
            east_west: if bd == 'W' { -b } else { b },
            north_south: if ad == 'S' { -a } else { a },
        });
    }
    found
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_pair_is_found_with_where_it_stands() {
        let text = "meet at 42.1N 33.5E now";
        let found = find(text);
        assert_eq!(found.len(), 1);
        let pair = found[0];
        assert_eq!(&text[pair.start..pair.start + pair.len], "42.1N 33.5E");
        assert_eq!((pair.east_west, pair.north_south), (33.5, 42.1));
    }

    #[test]
    fn a_pair_inside_a_word_is_not_one() {
        // OpenAC PR #224
        // PluginChatCoordinateWorkflowTests.CoordinateParserDoesNotReadAPairInsideAWord,
        // ChatCoordinateLinksTests.APairInsideAWordIsNotLinked.
        for text in ["10n 20east", "10north 20e", "v2.1N 3E", "x10n 20e"] {
            assert!(find(text).is_empty(), "{text}");
        }
        // Between punctuation it is one (CoordinateParserReadsAPairBetweenPunctuation).
        let found = find("Your corpse is located at (42.1N, 33.5E).");
        assert_eq!(found.len(), 1);
        assert_eq!((found[0].east_west, found[0].north_south), (33.5, 42.1));
    }

    #[test]
    fn offsets_are_bytes_past_wider_characters() {
        let text = "é 1N 2E";
        let pair = find(text)[0];
        assert_eq!(&text[pair.start..pair.start + pair.len], "1N 2E");
    }
}
