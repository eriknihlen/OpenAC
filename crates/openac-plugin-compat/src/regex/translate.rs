//! .NET regular-expression syntax rewritten into the syntax `fancy-regex`
//! reads, with .NET's meaning kept: `$` also matches before a final `\n`,
//! `\Z` likewise, `{,3}` and an unfinished `{` are literals, `(?n)` and
//! `RegexOptions.ExplicitCapture` make plain groups non-capturing, `(?x)`
//! comments are removed, and capture groups are numbered the .NET way
//! (unnamed groups first, then named ones).
//!
//! The output uses a small, explicit subset of the target syntax: every
//! literal other than an ASCII letter, digit or `_` is written as
//! `\x{HEX}`, every .NET capture group becomes an unnamed group, and every
//! back-reference names the target group by its number inside `(?:...)`.

use super::RegexError;

/// Flags that change how the rest of a group reads.
#[derive(Debug, Clone, Copy, Default)]
pub(super) struct Flags {
    pub ignore_case: bool,
    pub multiline: bool,
    pub explicit_capture: bool,
    pub singleline: bool,
    pub ignore_whitespace: bool,
}

/// A capture group of the .NET pattern, in order of its `(`.
#[derive(Debug, Clone, PartialEq, Eq)]
enum GroupKind {
    Unnamed,
    Named(String),
}

/// A .NET group number with its name (`"0"`, `"1"`, ..., or the given name).
#[derive(Debug, Clone)]
pub(super) struct Slot {
    pub number: usize,
    pub name: String,
    /// The `fancy-regex` group that holds it (0 for the whole match).
    pub target: usize,
}

/// The translated pattern and its groups in .NET number order.
#[derive(Debug)]
pub(super) struct Translation {
    /// The pattern with .NET's meaning on any input.
    pub pattern: String,
    /// The same pattern for an input that does not end in `\n`, where `$`
    /// and `\Z` are plain `\z` (no look-ahead, so the fast engine runs it).
    /// `None` when the pattern has no such anchor.
    pub pattern_no_final_newline: Option<String>,
    pub slots: Vec<Slot>,
}

pub(super) fn translate(pattern: &str, flags: Flags) -> Result<Translation, RegexError> {
    // First pass: find the capture groups, so back-references to later
    // groups and the .NET numbering are known before writing anything.
    let mut scan = Parser::new(pattern, flags, None, END_BEFORE_FINAL_NEWLINE);
    scan.alternation()?;
    if scan.pos < scan.chars.len() {
        return Err(invalid(pattern, "too many )'s"));
    }
    let slots = number_groups(pattern, &scan.groups)?;
    let emit = |end_anchor: &'static str| -> Result<String, RegexError> {
        let mut emit = Parser::new(pattern, flags, Some(&slots), end_anchor);
        if flags.ignore_case {
            emit.out.push_str("(?i)");
        }
        emit.alternation()?;
        Ok(emit.out)
    };
    let exact = emit(END_BEFORE_FINAL_NEWLINE)?;
    let plain = emit(r"\z")?;
    Ok(Translation {
        pattern_no_final_newline: (plain != exact).then_some(plain),
        pattern: exact,
        slots,
    })
}

/// .NET's `$` (without `Multiline`) and `\Z`: the end, or before a final `\n`.
const END_BEFORE_FINAL_NEWLINE: &str = r"(?=\n?\z)";

fn invalid(pattern: &str, why: &str) -> RegexError {
    RegexError::Invalid(format!("{pattern:?}: {why}"))
}

fn unsupported(pattern: &str, why: &str) -> RegexError {
    RegexError::Unsupported(format!("{pattern:?}: {why}"))
}

/// .NET's numbering: unnamed groups 1..=U in order; a numeric name is its
/// own number; other names take the free numbers after U in order of first
/// appearance. Each occurrence's target is its 1-based position among all
/// capture groups, which is how the translated pattern numbers them.
fn number_groups(pattern: &str, groups: &[GroupKind]) -> Result<Vec<Slot>, RegexError> {
    let unnamed = groups.iter().filter(|g| **g == GroupKind::Unnamed).count();
    let mut slots: Vec<Slot> = vec![Slot {
        number: 0,
        name: "0".to_owned(),
        target: 0,
    }];
    let mut next_unnamed = 1;
    let mut pending_names: Vec<(String, usize)> = Vec::new();
    for (i, group) in groups.iter().enumerate() {
        let target = i + 1;
        match group {
            GroupKind::Unnamed => {
                slots.push(Slot {
                    number: next_unnamed,
                    name: next_unnamed.to_string(),
                    target,
                });
                next_unnamed += 1;
            }
            GroupKind::Named(name) => match name.parse::<usize>() {
                Ok(number) => slots.push(Slot {
                    number,
                    name: number.to_string(),
                    target,
                }),
                Err(_) => pending_names.push((name.clone(), target)),
            },
        }
    }
    let mut next_free = unnamed + 1;
    for (name, target) in pending_names {
        while slots.iter().any(|s| s.number == next_free) {
            next_free += 1;
        }
        if slots.iter().any(|s| s.name == name) {
            return Err(unsupported(pattern, "a group name used twice"));
        }
        slots.push(Slot {
            number: next_free,
            name,
            target,
        });
        next_free += 1;
    }
    slots.sort_by_key(|s| s.number);
    if slots.windows(2).any(|w| w[0].number == w[1].number) {
        return Err(unsupported(pattern, "a group number used twice"));
    }
    Ok(slots)
}

struct Parser<'a> {
    source: &'a str,
    chars: Vec<char>,
    pos: usize,
    flags: Flags,
    out: String,
    /// Capture groups seen, in order (first pass).
    groups: Vec<GroupKind>,
    /// The numbering, when emitting.
    slots_by_occurrence: Option<&'a [Slot]>,
    /// What `$` (without `Multiline`) and `\Z` become.
    end_anchor: &'static str,
}

/// A character-class item.
enum ClassItem {
    Char(char),
    /// `\d`, `\P{L}`, ...: written as is.
    Escape(String),
}

impl<'a> Parser<'a> {
    fn new(
        source: &'a str,
        flags: Flags,
        slots: Option<&'a [Slot]>,
        end_anchor: &'static str,
    ) -> Self {
        Self {
            source,
            chars: source.chars().collect(),
            pos: 0,
            flags,
            out: String::new(),
            groups: Vec::new(),
            slots_by_occurrence: slots,
            end_anchor,
        }
    }

    fn peek(&self) -> Option<char> {
        self.chars.get(self.pos).copied()
    }

    fn peek_at(&self, offset: usize) -> Option<char> {
        self.chars.get(self.pos + offset).copied()
    }

    fn starts_with(&self, text: &str) -> bool {
        text.chars()
            .enumerate()
            .all(|(i, c)| self.peek_at(i) == Some(c))
    }

    fn invalid(&self, why: &str) -> RegexError {
        invalid(self.source, why)
    }

    fn unsupported(&self, why: &str) -> RegexError {
        unsupported(self.source, why)
    }

    /// .NET's `ScanBlank`: `(?#...)` comments always; whitespace and `#`
    /// line comments under `IgnorePatternWhitespace`.
    fn skip_blank(&mut self) -> Result<(), RegexError> {
        loop {
            if self.flags.ignore_whitespace {
                while let Some(c) = self.peek() {
                    if matches!(c, ' ' | '\t' | '\n' | '\r' | '\u{b}' | '\u{c}') {
                        self.pos += 1;
                    } else if c == '#' {
                        while let Some(c) = self.peek() {
                            self.pos += 1;
                            if c == '\n' {
                                break;
                            }
                        }
                    } else {
                        break;
                    }
                }
            }
            if self.starts_with("(?#") {
                while let Some(c) = self.peek() {
                    self.pos += 1;
                    if c == ')' {
                        break;
                    }
                }
                if self.chars.get(self.pos - 1) != Some(&')') {
                    return Err(self.invalid("unterminated (?#...) comment"));
                }
                continue;
            }
            return Ok(());
        }
    }

    fn alternation(&mut self) -> Result<(), RegexError> {
        loop {
            self.sequence()?;
            if self.peek() == Some('|') {
                self.pos += 1;
                self.out.push('|');
            } else {
                return Ok(());
            }
        }
    }

    fn sequence(&mut self) -> Result<(), RegexError> {
        loop {
            self.skip_blank()?;
            let Some(c) = self.peek() else {
                return Ok(());
            };
            let quantifiable = match c {
                '|' | ')' => return Ok(()),
                '(' => self.group()?,
                '[' => {
                    self.class()?;
                    true
                }
                '\\' => self.escape()?,
                '.' => {
                    self.pos += 1;
                    self.out
                        .push_str(if self.flags.singleline { "(?s:.)" } else { "." });
                    true
                }
                '^' => {
                    self.pos += 1;
                    self.out.push_str(if self.flags.multiline {
                        "(?m:^)"
                    } else {
                        r"\A"
                    });
                    true
                }
                '$' => {
                    self.pos += 1;
                    self.out.push_str(if self.flags.multiline {
                        "(?m:$)"
                    } else {
                        self.end_anchor
                    });
                    true
                }
                '*' | '+' | '?' => return Err(self.invalid("quantifier following nothing")),
                '{' if self.quantifier_at(self.pos).is_some() => {
                    return Err(self.invalid("quantifier following nothing"));
                }
                c => {
                    self.pos += 1;
                    push_literal(&mut self.out, c);
                    true
                }
            };
            if quantifiable {
                self.quantifier()?;
            }
        }
    }

    /// `{n}`, `{n,}` or `{n,m}` at `at`: the bounds and the length.
    fn quantifier_at(&self, at: usize) -> Option<(u32, Option<u32>, usize)> {
        if self.chars.get(at) != Some(&'{') {
            return None;
        }
        let mut i = at + 1;
        let digits = |i: &mut usize| {
            let start = *i;
            while self.chars.get(*i).is_some_and(char::is_ascii_digit) {
                *i += 1;
            }
            (*i > start).then(|| self.chars[start..*i].iter().collect::<String>())
        };
        let low: u32 = digits(&mut i)?.parse().ok()?;
        let high = match self.chars.get(i) {
            Some('}') => Some(low),
            Some(',') => {
                i += 1;
                match digits(&mut i) {
                    Some(h) => Some(h.parse().ok()?),
                    None => None,
                }
            }
            _ => return None,
        };
        if self.chars.get(i) != Some(&'}') {
            return None;
        }
        Some((low, high, i + 1 - at))
    }

    fn quantifier(&mut self) -> Result<(), RegexError> {
        self.skip_blank()?;
        let written = match self.peek() {
            Some(c @ ('*' | '+' | '?')) => {
                self.pos += 1;
                self.out.push(c);
                true
            }
            Some('{') => match self.quantifier_at(self.pos) {
                Some((low, high, len)) => {
                    let text: String = self.chars[self.pos..self.pos + len].iter().collect();
                    if high.is_some_and(|h| h < low) {
                        return Err(self.invalid("illegal {x,y} with x > y"));
                    }
                    self.pos += len;
                    self.out.push_str(&text);
                    true
                }
                None => false,
            },
            _ => false,
        };
        if !written {
            return Ok(());
        }
        self.skip_blank()?;
        if self.peek() == Some('?') {
            self.pos += 1;
            self.out.push('?');
            self.skip_blank()?;
        }
        let nested = match self.peek() {
            Some('*' | '+' | '?') => true,
            Some('{') => self.quantifier_at(self.pos).is_some(),
            _ => false,
        };
        if nested {
            return Err(self.invalid("nested quantifier"));
        }
        Ok(())
    }

    /// Reads a group name or number after `(?<` / `(?'`, up to `close`.
    fn group_name(&mut self, close: char) -> Result<String, RegexError> {
        let start = self.pos;
        while let Some(c) = self.peek() {
            if c == close {
                break;
            }
            if c == '-' {
                return Err(self.unsupported("balancing groups"));
            }
            if !is_word_char(c) {
                return Err(self.invalid("invalid group name"));
            }
            self.pos += 1;
        }
        let name: String = self.chars[start..self.pos].iter().collect();
        if self.peek() != Some(close) || name.is_empty() {
            return Err(self.invalid("invalid group name"));
        }
        if name.starts_with(|c: char| c.is_ascii_digit())
            && !name.chars().all(|c| c.is_ascii_digit())
        {
            return Err(self.invalid("invalid group name"));
        }
        self.pos += 1;
        Ok(name)
    }

    /// One group; returns whether a quantifier may follow.
    fn group(&mut self) -> Result<bool, RegexError> {
        self.pos += 1;
        let saved = self.flags;
        if self.peek() != Some('?') {
            if self.flags.explicit_capture {
                self.out.push_str("(?:");
            } else {
                self.capture(GroupKind::Unnamed);
            }
            return self.close_group(saved).map(|()| true);
        }
        self.pos += 1;
        match self.peek() {
            Some(':') => {
                self.pos += 1;
                self.out.push_str("(?:");
            }
            Some('=') | Some('!') => {
                let c = self.peek().unwrap_or('=');
                self.pos += 1;
                self.out.push_str("(?");
                self.out.push(c);
            }
            Some('>') => {
                self.pos += 1;
                self.out.push_str("(?>");
            }
            Some('<') if matches!(self.peek_at(1), Some('=') | Some('!')) => {
                let c = self.peek_at(1).unwrap_or('=');
                self.pos += 2;
                self.out.push_str("(?<");
                self.out.push(c);
            }
            Some(close @ ('<' | '\'')) => {
                self.pos += 1;
                let close = if close == '<' { '>' } else { '\'' };
                let name = self.group_name(close)?;
                self.capture(GroupKind::Named(name));
            }
            Some('(') => return Err(self.unsupported("conditional groups")),
            _ => {
                // Inline options: (?imnsx-imnsx) or (?imnsx-imnsx:...).
                let mut on = true;
                let mut any = false;
                let mut flags = self.flags;
                loop {
                    match self.peek() {
                        Some('-') if on => on = false,
                        Some('i') => flags.ignore_case = on,
                        Some('m') => flags.multiline = on,
                        Some('n') => flags.explicit_capture = on,
                        Some('s') => flags.singleline = on,
                        Some('x') => flags.ignore_whitespace = on,
                        Some(')') | Some(':') if any => break,
                        _ => return Err(self.invalid("unrecognized grouping construct")),
                    }
                    any = true;
                    self.pos += 1;
                }
                let case_changed = flags.ignore_case != self.flags.ignore_case;
                let case_flag = if flags.ignore_case { "i" } else { "-i" };
                if self.peek() == Some(')') {
                    // Applies to the rest of the enclosing group.
                    self.pos += 1;
                    self.flags = flags;
                    if case_changed {
                        self.out.push_str(&format!("(?{case_flag})"));
                    }
                    return Ok(false);
                }
                self.pos += 1;
                self.flags = flags;
                if case_changed {
                    self.out.push_str(&format!("(?{case_flag}:"));
                } else {
                    self.out.push_str("(?:");
                }
            }
        }
        self.close_group(saved).map(|()| true)
    }

    fn capture(&mut self, kind: GroupKind) {
        self.groups.push(kind);
        self.out.push('(');
    }

    fn close_group(&mut self, saved: Flags) -> Result<(), RegexError> {
        self.alternation()?;
        if self.peek() != Some(')') {
            return Err(self.invalid("not enough )'s"));
        }
        self.pos += 1;
        self.out.push(')');
        self.flags = saved;
        Ok(())
    }

    /// The translated group number of a .NET group number or name.
    fn target_of(&self, reference: &str) -> Option<usize> {
        let slots = self.slots_by_occurrence?;
        match reference.parse::<usize>() {
            Ok(n) => slots.iter().find(|s| s.number == n).map(|s| s.target),
            Err(_) => slots.iter().find(|s| s.name == reference).map(|s| s.target),
        }
    }

    fn is_defined(&self, reference: &str) -> bool {
        // In the first pass every reference is taken as defined.
        self.slots_by_occurrence.is_none() || self.target_of(reference).is_some()
    }

    fn push_backref(&mut self, reference: &str) {
        let target = self.target_of(reference).unwrap_or(0);
        self.out.push_str(&format!(r"(?:\{target})"));
    }

    /// An escape outside a class; returns whether a quantifier may follow.
    fn escape(&mut self) -> Result<bool, RegexError> {
        self.pos += 1;
        let Some(c) = self.peek() else {
            return Err(self.invalid("illegal \\ at end of pattern"));
        };
        match c {
            '1'..='9' => {
                let start = self.pos;
                while self.peek().is_some_and(|c| c.is_ascii_digit()) {
                    self.pos += 1;
                }
                let number: String = self.chars[start..self.pos].iter().collect();
                if self.is_defined(&number) {
                    self.push_backref(&number);
                    return Ok(true);
                }
                if number.parse::<u32>().is_ok_and(|n| n <= 9) {
                    return Err(self.invalid("reference to undefined group number"));
                }
                // Not a group: an octal escape.
                self.pos = start;
                let ch = self.octal()?;
                push_literal(&mut self.out, ch);
                Ok(true)
            }
            'k' | '<' | '\'' => {
                let angled_start = if c == 'k' { self.pos + 1 } else { self.pos };
                let open = self.chars.get(angled_start).copied();
                let close = match open {
                    Some('<') => Some('>'),
                    Some('\'') => Some('\''),
                    _ => None,
                };
                if let Some(close) = close {
                    let mut i = angled_start + 1;
                    while self.chars.get(i).is_some_and(|&ch| is_word_char(ch)) {
                        i += 1;
                    }
                    let name: String = self.chars[angled_start + 1..i].iter().collect();
                    if !name.is_empty() && self.chars.get(i) == Some(&close) {
                        if self.is_defined(&name) {
                            self.pos = i + 1;
                            self.push_backref(&name);
                            return Ok(true);
                        }
                        if c == 'k' {
                            return Err(self.invalid("reference to undefined group name"));
                        }
                    }
                }
                if c == 'k' {
                    return Err(self.invalid("malformed \\k<...> named back reference"));
                }
                // \< or \' that is not a back-reference: the character itself.
                self.pos += 1;
                push_literal(&mut self.out, c);
                Ok(true)
            }
            'A' | 'z' | 'G' | 'b' | 'B' => {
                self.pos += 1;
                self.out.push('\\');
                self.out.push(c);
                Ok(true)
            }
            'Z' => {
                self.pos += 1;
                self.out.push_str(self.end_anchor);
                Ok(true)
            }
            _ => {
                match self.class_escape()? {
                    ClassItem::Char(ch) => push_literal(&mut self.out, ch),
                    ClassItem::Escape(text) => self.out.push_str(&text),
                }
                Ok(true)
            }
        }
    }

    /// `\d`, `\p{..}` and the character escapes (`ScanCharEscape`), with
    /// the position on the character after the backslash.
    fn class_escape(&mut self) -> Result<ClassItem, RegexError> {
        let Some(c) = self.peek() else {
            return Err(self.invalid("illegal \\ at end of pattern"));
        };
        self.pos += 1;
        let ch = match c {
            'd' | 'D' | 'w' | 'W' | 's' | 'S' => return Ok(ClassItem::Escape(format!("\\{c}"))),
            'p' | 'P' => {
                if self.peek() != Some('{') {
                    return Err(self.invalid("malformed \\p{X} character escape"));
                }
                let start = self.pos;
                while let Some(ch) = self.peek() {
                    self.pos += 1;
                    if ch == '}' {
                        let body: String = self.chars[start + 1..self.pos - 1].iter().collect();
                        if body.is_empty() {
                            return Err(self.invalid("malformed \\p{X} character escape"));
                        }
                        return Ok(ClassItem::Escape(format!("\\{c}{{{body}}}")));
                    }
                }
                return Err(self.invalid("malformed \\p{X} character escape"));
            }
            'x' => self.hex(2)?,
            'u' => self.hex(4)?,
            'a' => '\u{7}',
            'b' => '\u{8}',
            'e' => '\u{1b}',
            'f' => '\u{c}',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'v' => '\u{b}',
            'c' => {
                let Some(mut letter) = self.peek() else {
                    return Err(self.invalid("missing control character"));
                };
                self.pos += 1;
                if letter.is_ascii_lowercase() {
                    letter = letter.to_ascii_uppercase();
                }
                let code = (letter as u32).wrapping_sub('@' as u32);
                if code >= 0x20 {
                    return Err(self.invalid("unrecognized control character"));
                }
                char::from_u32(code).unwrap_or('\0')
            }
            '0'..='7' => {
                self.pos -= 1;
                self.octal()?
            }
            c if is_word_char(c) => return Err(self.invalid("unrecognized escape sequence")),
            c => c,
        };
        Ok(ClassItem::Char(ch))
    }

    /// Exactly `count` hex digits.
    fn hex(&mut self, count: usize) -> Result<char, RegexError> {
        let digits: String = self
            .chars
            .get(self.pos..self.pos + count)
            .map_or_else(String::new, |d| d.iter().collect());
        if digits.len() != count || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
            return Err(self.invalid("insufficient hex digits"));
        }
        self.pos += count;
        let value = u32::from_str_radix(&digits, 16).unwrap_or(0);
        char::from_u32(value).ok_or_else(|| self.unsupported("a lone surrogate escape"))
    }

    /// Up to three octal digits, at most `\377`.
    fn octal(&mut self) -> Result<char, RegexError> {
        let mut value = 0u32;
        let mut count = 0;
        while count < 3 {
            match self.peek() {
                Some(d @ '0'..='7') if value * 8 + (d as u32 - '0' as u32) <= 0o377 => {
                    value = value * 8 + (d as u32 - '0' as u32);
                    self.pos += 1;
                    count += 1;
                }
                _ => break,
            }
        }
        Ok(char::from_u32(value).unwrap_or('\0'))
    }

    /// A character class (`ScanCharClass`).
    fn class(&mut self) -> Result<(), RegexError> {
        self.pos += 1;
        let mut negated = false;
        if self.peek() == Some('^') {
            negated = true;
            self.pos += 1;
        }
        let mut items: Vec<String> = Vec::new();
        let mut first = true;
        let mut range_start: Option<char> = None;
        loop {
            let Some(c) = self.peek() else {
                return Err(self.invalid("unterminated [] set"));
            };
            self.pos += 1;
            if c == ']' && !first {
                break;
            }
            first = false;
            let item = if c == '\\' {
                if self.peek() == Some('-') {
                    self.pos += 1;
                    ClassItem::Char('-')
                } else {
                    self.class_escape()?
                }
            } else if c == '-' && self.peek() == Some('[') && !items.is_empty() {
                return Err(self.unsupported("character class subtraction"));
            } else {
                ClassItem::Char(c)
            };
            match (range_start.take(), item) {
                (Some(low), ClassItem::Char(high)) => {
                    if high < low {
                        return Err(self.invalid("[x-y] range in reverse order"));
                    }
                    items.pop();
                    items.push(format!("{}-{}", hex_literal(low), hex_literal(high)));
                }
                (Some(_), ClassItem::Escape(_)) => {
                    return Err(self.invalid("cannot include a class in a character range"));
                }
                (None, ClassItem::Escape(text)) => items.push(text),
                (None, ClassItem::Char(ch)) => {
                    items.push(hex_literal(ch));
                    // "a-" followed by anything but "]" starts a range.
                    if self.peek() == Some('-') && self.peek_at(1).is_some_and(|n| n != ']') {
                        if self.peek_at(1) == Some('[') {
                            return Err(self.unsupported("character class subtraction"));
                        }
                        self.pos += 1;
                        range_start = Some(ch);
                    }
                }
            }
        }
        self.out.push('[');
        if negated {
            self.out.push('^');
        }
        if items.is_empty() {
            // "[^]" cannot be written; .NET reads "[]" and "[^]" as needing a "]" anyway.
            return Err(self.invalid("unterminated [] set"));
        }
        for item in items {
            self.out.push_str(&item);
        }
        self.out.push(']');
        Ok(())
    }
}

/// `RegexCharClass.IsBoundaryWordChar`, as group names use it.
fn is_word_char(c: char) -> bool {
    c.is_alphanumeric() || c == '_' || c == '\u{200d}' || c == '\u{200c}'
}

fn hex_literal(c: char) -> String {
    if c.is_ascii_alphanumeric() || c == '_' {
        c.to_string()
    } else {
        format!("\\x{{{:X}}}", c as u32)
    }
}

fn push_literal(out: &mut String, c: char) {
    out.push_str(&hex_literal(c));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn t(pattern: &str) -> String {
        translate(pattern, Flags::default()).unwrap().pattern
    }

    #[test]
    fn rewrites() {
        assert_eq!(t("abc$"), r"abc(?=\n?\z)");
        assert_eq!(t("x{,3}"), r"x\x{7B}\x{2C}3\x{7D}");
        assert_eq!(t("a b"), r"a\x{20}b");
        assert_eq!(t(r"(\w)\1"), r"(\w)(?:\1)");
        assert_eq!(t("(?<n>a)(b)"), "(a)(b)");
        assert_eq!(t("[]a-c]"), r"[\x{5D}a-c]");
        let tr = translate("(?<n>a)(b)", Flags::default()).unwrap();
        let names: Vec<_> = tr
            .slots
            .iter()
            .map(|s| (s.name.as_str(), s.target))
            .collect();
        assert_eq!(names, [("0", 0), ("1", 2), ("n", 1)]);
    }
}
