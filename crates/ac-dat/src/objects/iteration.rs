use ac_bits::Reader;

use super::{DatObject, check_unique};
use crate::error::DecodeError;
use crate::header::DatFileType;

/// The iteration (patch generation) record found in every dat.
///
/// The record lists runs of iterations; a run count that goes negative
/// ends the list.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Iteration {
    /// The dat's current iteration.
    pub current_iteration: i32,
    /// `(starting iteration, consecutive count)` in file order.
    pub iterations: Vec<(i32, i32)>,
}

impl Default for Iteration {
    /// What a dat without an iteration record reports: iteration 1.
    fn default() -> Self {
        Self {
            current_iteration: 1,
            iterations: vec![(1, -1)],
        }
    }
}

impl DatObject for Iteration {
    const DAT: DatFileType = DatFileType::Undefined;

    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let current_iteration = r.i32()?;
        let mut remaining = current_iteration;
        let mut iterations = Vec::new();
        while remaining > 0 {
            let consecutive = r.i32()?;
            let starting = r.i32()?;
            check_unique("iteration runs", &iterations, starting)?;
            iterations.push((starting, consecutive));
            remaining = remaining.wrapping_add(consecutive);
        }
        Ok(Self {
            current_iteration,
            iterations,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn single_run() {
        // The shape every retail dat has: current N, one run of -N from 1.
        let mut w = Writer::new();
        w.i32(2072);
        w.i32(-2072);
        w.i32(1);
        let it = Iteration::unpack(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(it.current_iteration, 2072);
        assert_eq!(it.iterations, vec![(1, -2072)]);
    }

    #[test]
    fn several_runs_until_the_count_is_used_up() {
        let mut w = Writer::new();
        w.i32(5);
        w.i32(-3);
        w.i32(1);
        w.i32(-2);
        w.i32(10);
        w.i32(0x7777); // not read
        let it = Iteration::unpack(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(it.iterations, vec![(1, -3), (10, -2)]);
    }

    #[test]
    fn truncated_and_duplicate_runs_are_errors() {
        let mut w = Writer::new();
        w.i32(5);
        w.i32(-1);
        assert!(Iteration::unpack(&mut Reader::new(w.as_slice())).is_err());
        let mut w = Writer::new();
        w.i32(5);
        w.i32(-1);
        w.i32(1);
        w.i32(-1);
        w.i32(1);
        assert!(matches!(
            Iteration::unpack(&mut Reader::new(w.as_slice())),
            Err(DecodeError::DuplicateKey { key: 1, .. })
        ));
    }
}
