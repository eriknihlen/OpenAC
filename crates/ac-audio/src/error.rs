//! Errors.

use std::sync::Arc;

use ac_dat::DatError;

/// Errors from the audio crate.
#[derive(Debug, Clone, thiserror::Error)]
pub enum AudioError {
    /// A dat read failed (not a missing object: a broken one).
    #[error("reading {kind} {id:#010x} from the dats: {source}")]
    Dat {
        /// What was being read.
        kind: &'static str,
        /// Its id.
        id: u32,
        /// The underlying error.
        #[source]
        source: Arc<DatError>,
    },
    /// The output device could not be opened or started.
    #[error("audio device: {0}")]
    Device(String),
}
