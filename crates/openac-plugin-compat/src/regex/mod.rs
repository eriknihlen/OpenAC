//! `System.Text.RegularExpressions.Regex` as the plugins use it: .NET
//! pattern syntax and group numbering, `Match`, `Matches`, `Replace` with
//! .NET substitutions, `Split` with captured separators, `GetGroupNames`,
//! `Regex.Escape`.
//!
//! Patterns are translated into `fancy-regex` syntax (see `translate`).
//! Where .NET gives a match a 100 ms timeout, a backtracking step limit
//! stops a runaway pattern instead: deterministic, the same on every
//! machine, and reported as [`RegexError::StepLimitExceeded`] where .NET
//! throws `RegexMatchTimeoutException`. Positions are
//! byte offsets into the UTF-8 input, not UTF-16 indexes.

mod translate;

use translate::{Flags, Slot, translate};

/// The step limit used by [`DotNetRegex::new`]: `fancy-regex`'s default of
/// one million backtracking steps.
pub const DEFAULT_STEP_LIMIT: usize = 1_000_000;

/// Why a pattern does not compile or a match did not finish.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum RegexError {
    /// .NET refuses the pattern too (`RegexParseException`).
    #[error("invalid pattern {0}")]
    Invalid(String),
    /// .NET accepts the pattern but the kit does not reproduce it
    /// (balancing groups, conditionals, class subtraction, `RightToLeft`, ...).
    #[error("pattern not supported by the compatibility kit: {0}")]
    Unsupported(String),
    /// The match ran past the step limit (.NET: `RegexMatchTimeoutException`).
    #[error("regular expression exceeded its step limit")]
    StepLimitExceeded,
}

/// .NET's `RegexOptions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RegexOptions(u32);

impl RegexOptions {
    /// No options.
    pub const NONE: Self = Self(0);
    /// Case-insensitive matching.
    pub const IGNORE_CASE: Self = Self(1);
    /// `^` and `$` match at line breaks.
    pub const MULTILINE: Self = Self(2);
    /// Only named groups capture.
    pub const EXPLICIT_CAPTURE: Self = Self(4);
    /// Compiled to IL in .NET; no effect here.
    pub const COMPILED: Self = Self(8);
    /// `.` matches `\n` too.
    pub const SINGLELINE: Self = Self(16);
    /// Whitespace and `#` comments in the pattern are ignored.
    pub const IGNORE_PATTERN_WHITESPACE: Self = Self(32);
    /// Right-to-left matching (not supported).
    pub const RIGHT_TO_LEFT: Self = Self(64);
    /// ECMAScript behaviour (not supported).
    pub const ECMA_SCRIPT: Self = Self(256);
    /// Invariant-culture casing; the kit's casing is always culture-free.
    pub const CULTURE_INVARIANT: Self = Self(512);
    /// The non-backtracking engine (not supported).
    pub const NON_BACKTRACKING: Self = Self(1024);

    /// The options with .NET's numeric value (`(RegexOptions)bits`).
    #[must_use]
    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    const fn has(self, flag: Self) -> bool {
        self.0 & flag.0 != 0
    }
}

impl std::ops::BitOr for RegexOptions {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

/// A compiled .NET regular expression.
#[derive(Debug, Clone)]
pub struct DotNetRegex {
    pattern: String,
    exact: fancy_regex::Regex,
    /// For inputs that do not end in `\n` (see `translate`).
    no_final_newline: Option<fancy_regex::Regex>,
    slots: Vec<Slot>,
}

/// One group of a match: .NET's `Group`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegexGroup<'t> {
    /// Whether the group took part in the match (`Group.Success`).
    pub success: bool,
    /// Byte offset of the captured text (0 when it did not take part).
    pub index: usize,
    /// The captured text (`""` when it did not take part).
    pub value: &'t str,
}

/// A successful match: .NET's `Match`, with its groups in .NET number order.
#[derive(Debug, Clone)]
pub struct RegexMatch<'r, 't> {
    regex: &'r DotNetRegex,
    input: &'t str,
    /// Span of each slot of `regex.slots`, in the same order.
    spans: Vec<Option<(usize, usize)>>,
}

impl DotNetRegex {
    /// `new Regex(pattern, options)` with the default step limit.
    pub fn new(pattern: &str, options: RegexOptions) -> Result<Self, RegexError> {
        Self::with_step_limit(pattern, options, DEFAULT_STEP_LIMIT)
    }

    /// `new Regex(pattern, options, matchTimeout)`: the step limit takes the
    /// timeout's place.
    pub fn with_step_limit(
        pattern: &str,
        options: RegexOptions,
        step_limit: usize,
    ) -> Result<Self, RegexError> {
        for (flag, name) in [
            (RegexOptions::RIGHT_TO_LEFT, "RegexOptions.RightToLeft"),
            (RegexOptions::ECMA_SCRIPT, "RegexOptions.ECMAScript"),
            (
                RegexOptions::NON_BACKTRACKING,
                "RegexOptions.NonBacktracking",
            ),
        ] {
            if options.has(flag) {
                return Err(RegexError::Unsupported(name.to_owned()));
            }
        }
        let flags = Flags {
            ignore_case: options.has(RegexOptions::IGNORE_CASE),
            multiline: options.has(RegexOptions::MULTILINE),
            explicit_capture: options.has(RegexOptions::EXPLICIT_CAPTURE),
            singleline: options.has(RegexOptions::SINGLELINE),
            ignore_whitespace: options.has(RegexOptions::IGNORE_PATTERN_WHITESPACE),
        };
        let translation = translate(pattern, flags)?;
        let build = |text: &str| {
            fancy_regex::RegexBuilder::new(text)
                .backtrack_limit(step_limit)
                .build()
                .map_err(|e| RegexError::Unsupported(format!("{pattern:?}: {e}")))
        };
        Ok(Self {
            pattern: pattern.to_owned(),
            exact: build(&translation.pattern)?,
            no_final_newline: translation
                .pattern_no_final_newline
                .as_deref()
                .map(build)
                .transpose()?,
            slots: translation.slots,
        })
    }

    /// The pattern as written (`Regex.ToString()`).
    pub fn pattern(&self) -> &str {
        &self.pattern
    }

    fn engine(&self, input: &str) -> &fancy_regex::Regex {
        match &self.no_final_newline {
            Some(fast) if !input.ends_with('\n') => fast,
            _ => &self.exact,
        }
    }

    /// `GetGroupNames()`: `"0"`, the numbered groups, then the named ones,
    /// in group-number order.
    pub fn group_names(&self) -> Vec<&str> {
        self.slots.iter().map(|s| s.name.as_str()).collect()
    }

    /// `GetGroupNumbers()`.
    pub fn group_numbers(&self) -> Vec<usize> {
        self.slots.iter().map(|s| s.number).collect()
    }

    /// `GroupNumberFromName(name)`; `None` where .NET returns -1.
    pub fn group_number_from_name(&self, name: &str) -> Option<usize> {
        self.slots.iter().find(|s| s.name == name).map(|s| s.number)
    }

    fn match_at<'r, 't>(
        &'r self,
        input: &'t str,
        start: usize,
    ) -> Result<Option<RegexMatch<'r, 't>>, RegexError> {
        let captures = self
            .engine(input)
            .captures_from_pos(input, start)
            .map_err(|e| match e {
                fancy_regex::Error::RuntimeError(_) => RegexError::StepLimitExceeded,
                other => RegexError::Unsupported(format!("{:?}: {other}", self.pattern)),
            })?;
        Ok(captures.map(|c| RegexMatch {
            regex: self,
            input,
            spans: self
                .slots
                .iter()
                .map(|s| c.get(s.target).map(|m| (m.start(), m.end())))
                .collect(),
        }))
    }

    /// `IsMatch(input)`.
    pub fn is_match(&self, input: &str) -> Result<bool, RegexError> {
        Ok(self.match_at(input, 0)?.is_some())
    }

    /// `Match(input)`: the first match, or `None` (`Match.Success` false).
    pub fn find<'r, 't>(
        &'r self,
        input: &'t str,
    ) -> Result<Option<RegexMatch<'r, 't>>, RegexError> {
        self.match_at(input, 0)
    }

    /// `Match(input, startat)`: the first match at or after byte `start`
    /// (look-behinds and `^` still see the text before it). A start inside
    /// a character moves to the next character; past the end, no match.
    pub fn find_at<'r, 't>(
        &'r self,
        input: &'t str,
        start: usize,
    ) -> Result<Option<RegexMatch<'r, 't>>, RegexError> {
        if start > input.len() {
            return Ok(None);
        }
        let start = (start..=input.len())
            .find(|&i| input.is_char_boundary(i))
            .unwrap_or(input.len());
        self.match_at(input, start)
    }

    /// `Matches(input)`: every match left to right; after an empty match the
    /// next search starts one character later.
    pub fn matches<'r, 't>(
        &'r self,
        input: &'t str,
    ) -> Result<Vec<RegexMatch<'r, 't>>, RegexError> {
        let mut out = Vec::new();
        let mut start = 0;
        while start <= input.len() {
            let Some(m) = self.match_at(input, start)? else {
                break;
            };
            let (begin, end) = m.span();
            start = if end == begin {
                match input[end..].chars().next() {
                    Some(c) => end + c.len_utf8(),
                    None => input.len() + 1,
                }
            } else {
                end
            };
            out.push(m);
        }
        Ok(out)
    }

    /// `Replace(input, replacement)` with .NET substitutions: `$1`, `${name}`,
    /// `$$`, `$&`, `` $` ``, `$'`, `$+`, `$_`; anything else after `$` is
    /// literal.
    pub fn replace(&self, input: &str, replacement: &str) -> Result<String, RegexError> {
        let parts = parse_replacement(replacement, self);
        self.replace_with(input, |m| {
            let mut out = String::new();
            for part in &parts {
                match part {
                    Substitution::Text(t) => out.push_str(t),
                    Substitution::Group(n) => out.push_str(m.group_number(*n).value),
                    Substitution::LastGroup => {
                        let last = m.regex.slots.last().map_or(0, |s| s.number);
                        out.push_str(m.group_number(last).value);
                    }
                    Substitution::Before => out.push_str(&m.input[..m.index()]),
                    Substitution::After => out.push_str(&m.input[m.span().1..]),
                    Substitution::Input => out.push_str(m.input),
                }
            }
            out
        })
    }

    /// `Replace(input, MatchEvaluator)`.
    pub fn replace_with(
        &self,
        input: &str,
        mut evaluator: impl FnMut(&RegexMatch<'_, '_>) -> String,
    ) -> Result<String, RegexError> {
        let mut out = String::new();
        let mut last = 0;
        for m in self.matches(input)? {
            let (begin, end) = m.span();
            out.push_str(&input[last..begin]);
            out.push_str(&evaluator(&m));
            last = end;
        }
        out.push_str(&input[last..]);
        Ok(out)
    }

    /// `Split(input)`: the text between matches, with each match's
    /// successful groups (in group-number order) between the pieces.
    pub fn split<'t>(&self, input: &'t str) -> Result<Vec<&'t str>, RegexError> {
        let matches = self.matches(input)?;
        if matches.is_empty() {
            return Ok(vec![input]);
        }
        let mut out = Vec::new();
        let mut last = 0;
        for m in &matches {
            let (begin, end) = m.span();
            out.push(&input[last..begin]);
            last = end;
            for (i, span) in m.spans.iter().enumerate().skip(1) {
                if span.is_some() {
                    out.push(m.group_at(i).value);
                }
            }
        }
        out.push(&input[last..]);
        Ok(out)
    }
}

impl<'r, 't> RegexMatch<'r, 't> {
    fn span(&self) -> (usize, usize) {
        self.spans[0].unwrap_or((0, 0))
    }

    fn group_at(&self, i: usize) -> RegexGroup<'t> {
        match self.spans.get(i).copied().flatten() {
            Some((begin, end)) => RegexGroup {
                success: true,
                index: begin,
                value: &self.input[begin..end],
            },
            None => RegexGroup {
                success: false,
                index: 0,
                value: "",
            },
        }
    }

    /// `Match.Value`.
    pub fn value(&self) -> &'t str {
        let (begin, end) = self.span();
        &self.input[begin..end]
    }

    /// `Match.Index`, as a byte offset.
    pub fn index(&self) -> usize {
        self.span().0
    }

    /// `Groups[number]`; an unknown number is a group that did not take part.
    pub fn group_number(&self, number: usize) -> RegexGroup<'t> {
        match self.regex.slots.iter().position(|s| s.number == number) {
            Some(i) => self.group_at(i),
            None => self.group_at(usize::MAX),
        }
    }

    /// `Groups[name]`: a name, or a number written as text; unknown names
    /// give a group that did not take part.
    pub fn group(&self, name: &str) -> RegexGroup<'t> {
        match self.regex.slots.iter().position(|s| s.name == name) {
            Some(i) => self.group_at(i),
            None => self.group_at(usize::MAX),
        }
    }
}

enum Substitution {
    Text(String),
    Group(usize),
    LastGroup,
    Before,
    After,
    Input,
}

/// .NET's `RegexParser.ParseReplacement` / `ScanDollar`.
fn parse_replacement(replacement: &str, regex: &DotNetRegex) -> Vec<Substitution> {
    let chars: Vec<char> = replacement.chars().collect();
    let is_slot = |n: usize| regex.slots.iter().any(|s| s.number == n);
    let mut parts = Vec::new();
    let mut text = String::new();
    let mut i = 0;
    let flush = |text: &mut String, parts: &mut Vec<Substitution>, part: Substitution| {
        if !text.is_empty() {
            parts.push(Substitution::Text(std::mem::take(text)));
        }
        parts.push(part);
    };
    while i < chars.len() {
        let c = chars[i];
        i += 1;
        if c != '$' || i >= chars.len() {
            text.push(c);
            continue;
        }
        let next = chars[i];
        if next == '{' {
            // ${number} or ${name}
            let close = chars[i + 1..].iter().position(|&c| c == '}');
            if let Some(len) = close {
                let inner: String = chars[i + 1..i + 1 + len].iter().collect();
                let target = if !inner.is_empty() && inner.chars().all(|c| c.is_ascii_digit()) {
                    inner.parse::<usize>().ok().filter(|&n| is_slot(n))
                } else if !inner.is_empty()
                    && inner.chars().all(|c| c.is_alphanumeric() || c == '_')
                {
                    regex.group_number_from_name(&inner)
                } else {
                    None
                };
                if let Some(n) = target {
                    flush(&mut text, &mut parts, Substitution::Group(n));
                    i += len + 2;
                    continue;
                }
            }
            text.push('$');
            continue;
        }
        if next.is_ascii_digit() {
            let start = i;
            while i < chars.len() && chars[i].is_ascii_digit() {
                i += 1;
            }
            let digits: String = chars[start..i].iter().collect();
            if let Some(n) = digits.parse::<usize>().ok().filter(|&n| is_slot(n)) {
                flush(&mut text, &mut parts, Substitution::Group(n));
            } else {
                // Not a group: the "$" is literal and the digits follow as text.
                text.push('$');
                i = start;
            }
            continue;
        }
        let special = match next {
            '$' => Some(None),
            '&' => Some(Some(Substitution::Group(0))),
            '`' => Some(Some(Substitution::Before)),
            '\'' => Some(Some(Substitution::After)),
            '+' => Some(Some(Substitution::LastGroup)),
            '_' => Some(Some(Substitution::Input)),
            _ => None,
        };
        match special {
            Some(None) => {
                text.push('$');
                i += 1;
            }
            Some(Some(part)) => {
                flush(&mut text, &mut parts, part);
                i += 1;
            }
            None => text.push('$'),
        }
    }
    if !text.is_empty() {
        parts.push(Substitution::Text(text));
    }
    parts
}

/// `Regex.Escape(text)`: `\ * + ? | { [ ( ) ^ $ . #` and whitespace escaped
/// (`\t`, `\n`, `\f`, `\r`, `\ `); `]` and `}` are left alone.
pub fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for c in text.chars() {
        match c {
            '\\' | '*' | '+' | '?' | '|' | '{' | '[' | '(' | ')' | '^' | '$' | '.' | '#' => {
                out.push('\\');
                out.push(c);
            }
            ' ' => out.push_str("\\ "),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\u{c}' => out.push_str("\\f"),
            c => out.push(c),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runaway_patterns_stop_at_the_step_limit() {
        // A back-reference keeps the backtracking engine in charge; nested
        // quantifiers over the same text make it exponential.
        let re = DotNetRegex::new(r"^(\w+\s?)+\1(?<!a)$", RegexOptions::NONE).unwrap();
        let input = "a".repeat(40);
        assert_eq!(re.is_match(&input), Err(RegexError::StepLimitExceeded));
        // A generous limit lets an ordinary pattern finish.
        let ok = DotNetRegex::new(r"^(\w+)\s\1$", RegexOptions::NONE).unwrap();
        assert_eq!(ok.is_match("hello hello"), Ok(true));
    }
}
