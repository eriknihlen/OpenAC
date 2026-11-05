//! The game-data check at login: the server asks which data-file versions
//! the client has, may announce an update, and ends the check; the client
//! echoes the end. This client cannot apply data updates, so an announced
//! update means the login cannot continue.

use ac_bits::{Reader, Writer};

use crate::{Opcode, ProtoError, Result};

/// Language id the interrogation response reports (English).
const ENGLISH_LANGUAGE: u32 = 1;

/// Largest version count the interrogation response will report.
pub const MAX_DATA_VERSION_COUNT: u32 = 100_000;

/// Installed data-file version counts, reported in the interrogation
/// response. Each is the number of update iterations the file has had.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DddDataVersions {
    /// The portal (client_portal.dat) iteration count.
    pub portal: u32,
    /// The cell (client_cell_1.dat) iteration count.
    pub cell: u32,
    /// The language (client_local_English.dat) iteration count.
    pub language: u32,
}

/// Body answering the server's data-version question
/// ([`Opcode::DddInterrogationResponse`]): opcode, language, then either
/// no version entries (`versions` is `None`) or one entry each for the
/// portal, cell and language files, then two zero words.
///
/// An entry is file type, file id, iteration count, then the iterations:
/// listed one by one (1, 2, ...) for a count of at most 2, otherwise as the
/// range "minus the count, then 1".
///
/// Sent on the database queue in reply to [`Opcode::DddInterrogation`].
pub fn build_interrogation_response(versions: Option<DddDataVersions>) -> Result<Vec<u8>> {
    let mut w = Writer::with_capacity(16);
    w.u32(Opcode::DddInterrogationResponse.value());
    w.u32(ENGLISH_LANGUAGE);
    w.u32(if versions.is_some() { 3 } else { 0 });
    if let Some(installed) = versions {
        write_version(&mut w, 0, 1, installed.portal)?;
        write_version(&mut w, 1, 2, installed.cell)?;
        write_version(&mut w, 1, 3, installed.language)?;
    }
    w.u32(0);
    w.u32(0);
    Ok(w.into_vec())
}

fn write_version(w: &mut Writer, file_type: u32, file_id: u32, count: u32) -> Result<()> {
    if count > MAX_DATA_VERSION_COUNT {
        return Err(ProtoError::DataVersionOutOfRange {
            count,
            max: MAX_DATA_VERSION_COUNT,
        });
    }
    w.u32(file_type);
    w.u32(file_id);
    w.u32(count);
    if count > 2 {
        w.u32(count.wrapping_neg());
        w.u32(1);
    } else {
        for iteration in 1..=count {
            w.u32(iteration);
        }
    }
    Ok(())
}

/// Body ending the game-data check from the client side: the bare
/// [`Opcode::DddEnd`]. Echo it once, the first time the server's end
/// arrives after its interrogation, unless an update was announced.
///
/// Sent on the database queue.
#[must_use]
pub fn build_data_check_complete() -> Vec<u8> {
    Opcode::DddEnd.bare_body()
}

/// The server's announcement of a data update ([`Opcode::DddBegin`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct DddBegin {
    /// Bytes of update data the server will send.
    pub expected_bytes: u32,
    /// Number of file iterations in the update.
    pub iteration_count: u32,
}

/// Smallest size of one iteration record: three words and two empty id
/// lists. Bounds the count before walking the records.
const MIN_ITERATION_SIZE: usize = 20;

impl DddBegin {
    /// Parse and validate an update announcement. The iteration records are
    /// walked to check the body's exact length but not kept.
    pub fn parse(body: &[u8]) -> Result<Self> {
        const WHAT: &str = "data-update announcement";
        let mut r = Reader::new(body);
        if body.len() < 12 {
            return Err(ProtoError::InvalidLength {
                what: WHAT,
                actual: body.len(),
            });
        }
        crate::expect_opcode(&mut r, Opcode::DddBegin)?;
        let expected_bytes = r.u32()?;
        let iteration_count = r.u32()?;
        let fits =
            usize::try_from(iteration_count).is_ok_and(|n| n <= r.remaining() / MIN_ITERATION_SIZE);
        if !fits {
            return Err(ProtoError::CountExceedsPayload {
                what: "data-update iteration",
                count: iteration_count,
            });
        }
        for _ in 0..iteration_count {
            r.skip(12)?;
            skip_ids(&mut r)?;
            skip_ids(&mut r)?;
        }
        if r.remaining() != 0 {
            return Err(ProtoError::InvalidLength {
                what: WHAT,
                actual: body.len(),
            });
        }
        Ok(Self {
            expected_bytes,
            iteration_count,
        })
    }

    /// True if the server announced any update. This client cannot apply
    /// one, so the login stops here.
    #[must_use]
    pub fn requires_update(self) -> bool {
        self.expected_bytes != 0 || self.iteration_count != 0
    }
}

fn skip_ids(r: &mut Reader<'_>) -> Result<()> {
    let count = r.u32()?;
    let n = usize::try_from(count)
        .ok()
        .filter(|&n| n <= r.remaining() / 4)
        .ok_or(ProtoError::CountExceedsPayload {
            what: "data-update item",
            count,
        })?;
    r.skip(4 * n)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/DataCheckTests.cs`.

    use super::*;
    use crate::test_util::words;

    #[test]
    fn reports_installed_versions_with_complete_envelope() {
        let body = build_interrogation_response(Some(DddDataVersions {
            portal: 3,
            cell: 1,
            language: 0,
        }))
        .unwrap();
        assert_eq!(
            body,
            words(&[
                0xF7E6,
                1,
                3,
                0,
                1,
                3,
                3u32.wrapping_neg(),
                1,
                1,
                2,
                1,
                1,
                1,
                3,
                0,
                0,
                0,
            ])
        );
    }

    #[test]
    fn unknown_versions_report_no_entries() {
        assert_eq!(
            build_interrogation_response(None).unwrap(),
            words(&[0xF7E6, 1, 0, 0, 0])
        );
    }

    #[test]
    fn count_of_two_lists_both_iterations() {
        let body = build_interrogation_response(Some(DddDataVersions {
            portal: 2,
            cell: 0,
            language: 0,
        }))
        .unwrap();
        assert_eq!(body[12..32], words(&[0, 1, 2, 1, 2]));
    }

    #[test]
    fn count_above_the_limit_is_an_error() {
        let versions = DddDataVersions {
            portal: MAX_DATA_VERSION_COUNT + 1,
            cell: 0,
            language: 0,
        };
        assert_eq!(
            build_interrogation_response(Some(versions)),
            Err(ProtoError::DataVersionOutOfRange {
                count: MAX_DATA_VERSION_COUNT + 1,
                max: MAX_DATA_VERSION_COUNT
            })
        );
        let at_limit = DddDataVersions {
            portal: MAX_DATA_VERSION_COUNT,
            ..versions
        };
        assert!(build_interrogation_response(Some(at_limit)).is_ok());
    }

    #[test]
    fn data_check_complete_is_the_bare_opcode() {
        assert_eq!(build_data_check_complete(), [0xEA, 0xF7, 0x00, 0x00]);
    }

    #[test]
    fn empty_begin_needs_no_update() {
        let parsed = DddBegin::parse(&words(&[0xF7E7, 0, 0])).unwrap();
        assert!(!parsed.requires_update());
    }

    #[test]
    fn announced_updates_require_an_update() {
        // Full update and purge-only shapes from DataCheckTests.BuildUpdate.
        let full = DddBegin::parse(&words(&[0xF7E7, 100, 1, 0, 1, 1, 1, 123, 0])).unwrap();
        assert_eq!(
            full,
            DddBegin {
                expected_bytes: 100,
                iteration_count: 1
            }
        );
        assert!(full.requires_update());
        let purge = DddBegin::parse(&words(&[0xF7E7, 0, 1, 1, 2, 1, 0, 1, 123])).unwrap();
        assert!(purge.requires_update());
    }

    #[test]
    fn malformed_begin_is_rejected() {
        // One iteration announced but no record follows.
        assert!(DddBegin::parse(&words(&[0xF7E7, 1, 1])).is_err());
        // Trailing word after zero iterations; count that cannot fit.
        assert!(DddBegin::parse(&words(&[0xF7E7, 0, 0, 0])).is_err());
        assert!(DddBegin::parse(&words(&[0xF7E7, 0, u32::MAX, 0])).is_err());
        // Wrong opcode.
        assert!(matches!(
            DddBegin::parse(&words(&[0xF7E6, 0, 0])),
            Err(ProtoError::WrongOpcode { .. })
        ));
        // Item list count that cannot fit.
        assert!(DddBegin::parse(&words(&[0xF7E7, 0, 1, 1, 2, 3, u32::MAX, 0])).is_err());
    }

    #[test]
    fn every_truncated_prefix_is_an_error() {
        let valid = words(&[0xF7E7, 100, 1, 0, 1, 1, 1, 123, 0]);
        crate::test_util::assert_every_prefix_fails(&valid, DddBegin::parse);
    }
}
