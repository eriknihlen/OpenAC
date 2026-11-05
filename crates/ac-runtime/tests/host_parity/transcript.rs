//! What one arm did and saw, as ordered lines (OpenAC `ParityTranscript`):
//! `key = value` under a step heading. A scenario writes the same lines on
//! both arms; the two transcripts then have to be the same text.

use std::fmt::Debug;

use ac_plugin::abi::Import;

/// One line: the step and what was asked, and what this arm answered.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Line {
    pub key: String,
    pub value: String,
}

/// A line the two arms may answer differently (OpenAC
/// `ParityExpectedDifference`): only a line whose answer the import census
/// marks window-dependent (`Import::needs_window`), so closing the
/// difference takes the annotation with it.
#[derive(Debug, Clone, Copy)]
pub struct Difference {
    /// The line's key (`step/key`).
    pub key: &'static str,
    /// The window-dependent client function that answers it.
    pub import: Import,
}

#[derive(Debug, Default)]
pub struct Transcript {
    lines: Vec<Line>,
    step: String,
}

impl Transcript {
    pub fn new() -> Self {
        Self {
            lines: Vec::new(),
            step: "start".to_owned(),
        }
    }

    /// Names the step the following lines belong to.
    pub fn step(&mut self, name: &str) {
        self.step = name.to_owned();
        self.lines.push(Line {
            key: format!("{name}/"),
            value: "--".to_owned(),
        });
    }

    /// Records one thing this arm answered (its `Debug` form).
    pub fn record(&mut self, key: &str, value: impl Debug) {
        self.lines.push(Line {
            key: format!("{}/{key}", self.step),
            value: format!("{value:?}"),
        });
    }

    /// Records an object id the way OpenAC's transcript writes one.
    pub fn record_id(&mut self, key: &str, id: u32) {
        self.lines.push(Line {
            key: format!("{}/{key}", self.step),
            value: format!("0x{id:08X}"),
        });
    }
}

impl std::fmt::Display for Transcript {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        for line in &self.lines {
            writeln!(f, "{} = {}", line.key, line.value)?;
        }
        Ok(())
    }
}

/// Compares what the two arms wrote down (OpenAC
/// `ParityScenario.CompareTranscripts`): the same steps, the same keys, the
/// same values except where a named difference allows it, and then only if
/// the line really differs.
pub fn compare(windowed: &Transcript, windowless: &Transcript, expected: &[Difference]) {
    for difference in expected {
        assert!(
            difference.import.needs_window(),
            "the scenario excuses {} against {}, which the import census does not mark \
             window-dependent: make the arms agree, or the census says otherwise",
            difference.key,
            difference.import.name()
        );
    }
    assert!(
        windowed.lines.len() == windowless.lines.len(),
        "The two arms did not take the same steps.\n--- windowed\n{windowed}\n--- \
         windowless\n{windowless}"
    );
    let mut unexplained = Vec::new();
    let mut explained_but_identical = Vec::new();
    for (index, (left, right)) in windowed.lines.iter().zip(&windowless.lines).enumerate() {
        assert_eq!(
            left.key, right.key,
            "The two arms recorded different things at line {index}.\n--- windowed\n{windowed}\n\
             --- windowless\n{windowless}"
        );
        let differs = left.value != right.value;
        let explained = expected.iter().any(|d| d.key == left.key);
        if differs && !explained {
            unexplained.push(format!(
                "{}: windowed {}, windowless {}",
                left.key, left.value, right.value
            ));
        } else if !differs && explained {
            explained_but_identical.push(left.key.clone());
        }
    }
    assert!(
        unexplained.is_empty(),
        "The two arms answered a plugin differently and nothing says why:\n  {}\n--- windowed\n\
         {windowed}\n--- windowless\n{windowless}",
        unexplained.join("\n  ")
    );
    assert!(
        explained_but_identical.is_empty(),
        "A difference is recorded for a line the two arms now answer identically: {}",
        explained_but_identical.join(", ")
    );
}
