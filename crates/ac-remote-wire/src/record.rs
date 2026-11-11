//! Recording a presentation stream to a file and replaying it: the test
//! tool that feeds a window the same stream twice.
//!
//! A recording is a header (the magic `OARW`, the stream-format version
//! byte and the [`shape`](crate::shape) as a `u64`, little-endian) and then
//! records, each the time since the stream's epoch in microseconds (`u64`),
//! the batch's length (`u32`) and the batch's bytes as
//! [`encode_batch`](crate::encode_batch) made them.

use std::fs::File;
use std::io::{self, BufReader, BufWriter, Read, Write};
use std::path::Path;
use std::time::{Duration, Instant};

use crate::{DecodeContext, DecodeError, PresentationEvent, STREAM_FORMAT, decode_batch, shape};

/// The first bytes of every recording.
pub const MAGIC: [u8; 4] = *b"OARW";

/// Why a recording could not be read.
#[derive(Debug, thiserror::Error)]
pub enum RecordError {
    /// The file could not be read.
    #[error("reading the recording: {0}")]
    Io(#[from] io::Error),
    /// The file does not start with [`MAGIC`].
    #[error("not a stream recording")]
    NotARecording,
    /// Recorded in another stream format.
    #[error("recorded in stream format {found}, this build reads {expected}")]
    Format {
        /// The recording's.
        found: u8,
        /// This build's.
        expected: u8,
    },
    /// Recorded by a build whose stream has another layout.
    #[error("recorded with stream shape {found:016x}, this build's is {expected:016x}")]
    Shape {
        /// The recording's.
        found: u64,
        /// This build's.
        expected: u64,
    },
    /// The file ends inside its header or a record.
    #[error("the recording ends inside a record")]
    Truncated,
    /// A record's batch does not decode.
    #[error("a recorded batch does not decode: {0}")]
    Decode(#[from] DecodeError),
}

/// The header's length: the magic, the format byte, the shape.
const HEADER_LEN: usize = MAGIC.len() + 1 + 8;

/// A record's own header: the time and the batch's length.
const RECORD_HEADER_LEN: usize = 8 + 4;

/// Writes a recording.
#[derive(Debug)]
pub struct Recorder<W: Write> {
    out: W,
}

impl Recorder<BufWriter<File>> {
    /// A new recording at `path` (an existing file is replaced).
    ///
    /// # Errors
    /// The file cannot be created or written.
    pub fn create(path: impl AsRef<Path>) -> io::Result<Self> {
        Self::new(BufWriter::new(File::create(path)?))
    }
}

impl<W: Write> Recorder<W> {
    /// A recording written to `out`, its header first.
    ///
    /// # Errors
    /// `out` cannot be written.
    pub fn new(mut out: W) -> io::Result<Self> {
        out.write_all(&MAGIC)?;
        out.write_all(&[STREAM_FORMAT])?;
        out.write_all(&shape().to_le_bytes())?;
        Ok(Self { out })
    }

    /// Appends one batch made at `at` since the stream's epoch (kept to the
    /// microsecond).
    ///
    /// # Errors
    /// `out` cannot be written; a batch longer than `u32::MAX` bytes.
    pub fn append(&mut self, at: Duration, batch: &[u8]) -> io::Result<()> {
        let len = u32::try_from(batch.len()).map_err(|_| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "a batch longer than u32::MAX bytes",
            )
        })?;
        let micros = u64::try_from(at.as_micros()).unwrap_or(u64::MAX);
        self.out.write_all(&micros.to_le_bytes())?;
        self.out.write_all(&len.to_le_bytes())?;
        self.out.write_all(batch)
    }

    /// Flushes and hands back the writer.
    ///
    /// # Errors
    /// `out` cannot be flushed.
    pub fn finish(mut self) -> io::Result<W> {
        self.out.flush()?;
        Ok(self.out)
    }
}

/// Reads a recording back, one batch at a time. After an error it ends.
#[derive(Debug)]
pub struct Replay<R: Read> {
    input: R,
    cx: DecodeContext,
    done: bool,
}

impl Replay<BufReader<File>> {
    /// The recording at `path`, its instants decoded against now.
    ///
    /// # Errors
    /// The file cannot be read; [`RecordError`] for its header.
    pub fn open(path: impl AsRef<Path>) -> Result<Self, RecordError> {
        Self::from_reader(
            BufReader::new(File::open(path)?),
            DecodeContext::new(Instant::now()),
        )
    }
}

/// Fills `buf`: `Ok(false)` when the input ended before its first byte,
/// [`RecordError::Truncated`] when it ended after.
fn read_all_or_none(input: &mut impl Read, buf: &mut [u8]) -> Result<bool, RecordError> {
    let mut filled = 0;
    while filled < buf.len() {
        match input.read(&mut buf[filled..]) {
            Ok(0) if filled == 0 => return Ok(false),
            Ok(0) => return Err(RecordError::Truncated),
            Ok(n) => filled += n,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
            Err(error) => return Err(error.into()),
        }
    }
    Ok(true)
}

impl<R: Read> Replay<R> {
    /// The recording in `input`, decoded with `cx`.
    ///
    /// # Errors
    /// [`RecordError`] for its header.
    pub fn from_reader(mut input: R, cx: DecodeContext) -> Result<Self, RecordError> {
        let mut header = [0; HEADER_LEN];
        if !read_all_or_none(&mut input, &mut header)? {
            return Err(RecordError::Truncated);
        }
        if header[..MAGIC.len()] != MAGIC {
            return Err(RecordError::NotARecording);
        }
        let found = header[MAGIC.len()];
        if found != STREAM_FORMAT {
            return Err(RecordError::Format {
                found,
                expected: STREAM_FORMAT,
            });
        }
        let mut recorded = [0; 8];
        recorded.copy_from_slice(&header[MAGIC.len() + 1..]);
        let found = u64::from_le_bytes(recorded);
        if found != shape() {
            return Err(RecordError::Shape {
                found,
                expected: shape(),
            });
        }
        Ok(Self {
            input,
            cx,
            done: false,
        })
    }

    fn record(&mut self) -> Result<Option<(Duration, Vec<PresentationEvent>)>, RecordError> {
        let mut head = [0; RECORD_HEADER_LEN];
        if !read_all_or_none(&mut self.input, &mut head)? {
            return Ok(None);
        }
        let (micros, len) = head.split_at(8);
        let mut micros_bytes = [0; 8];
        micros_bytes.copy_from_slice(micros);
        let mut len_bytes = [0; 4];
        len_bytes.copy_from_slice(len);
        let at = Duration::from_micros(u64::from_le_bytes(micros_bytes));
        let len = u64::from(u32::from_le_bytes(len_bytes));
        // Read what is there rather than allocating what the length says:
        // a damaged length meets the end of the file, not a huge buffer.
        let mut batch = Vec::new();
        (&mut self.input).take(len).read_to_end(&mut batch)?;
        if batch.len() as u64 != len {
            return Err(RecordError::Truncated);
        }
        Ok(Some((at, decode_batch(&batch, &self.cx)?)))
    }
}

impl<R: Read> Iterator for Replay<R> {
    type Item = Result<(Duration, Vec<PresentationEvent>), RecordError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.done {
            return None;
        }
        let record = self.record();
        if !matches!(record, Ok(Some(_))) {
            self.done = true;
        }
        record.transpose()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::{EncodeContext, encode_batch};

    fn batch(e: &EncodeContext, text: &str) -> Vec<u8> {
        encode_batch(&[PresentationEvent::StatusText(text.into())], e).unwrap()
    }

    fn recording(e: &EncodeContext) -> Vec<u8> {
        let mut recorder = Recorder::new(Vec::new()).unwrap();
        recorder
            .append(Duration::from_micros(10), &batch(e, "one"))
            .unwrap();
        recorder
            .append(Duration::from_millis(2500), &batch(e, "two"))
            .unwrap();
        recorder.finish().unwrap()
    }

    #[test]
    fn a_recording_replays_its_batches_in_order() {
        let epoch = Instant::now();
        let e = EncodeContext::new(epoch);
        let bytes = recording(&e);
        assert_eq!(&bytes[..4], b"OARW");
        let replay = Replay::from_reader(bytes.as_slice(), DecodeContext::new(epoch)).unwrap();
        let records: Vec<_> = replay.map(Result::unwrap).collect();
        assert_eq!(
            records,
            [
                (
                    Duration::from_micros(10),
                    vec![PresentationEvent::StatusText("one".into())]
                ),
                (
                    Duration::from_millis(2500),
                    vec![PresentationEvent::StatusText("two".into())]
                ),
            ]
        );
    }

    #[test]
    fn a_recording_on_disk_replays() {
        let epoch = Instant::now();
        let e = EncodeContext::new(epoch);
        let dir = std::env::temp_dir().join(format!("ac-remote-wire-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("stream.oarw");
        let mut recorder = Recorder::create(&path).unwrap();
        recorder.append(Duration::ZERO, &batch(&e, "disk")).unwrap();
        drop(recorder.finish().unwrap());
        let records: Vec<_> = Replay::open(&path).unwrap().map(Result::unwrap).collect();
        assert_eq!(
            records,
            [(
                Duration::ZERO,
                vec![PresentationEvent::StatusText("disk".into())]
            )]
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn another_builds_recording_is_refused() {
        let e = EncodeContext::new(Instant::now());
        let mut bytes = recording(&e);
        bytes[5] ^= 0xFF;
        let d = DecodeContext::new(Instant::now());
        assert!(matches!(
            Replay::from_reader(bytes.as_slice(), d.clone()),
            Err(RecordError::Shape { expected, .. }) if expected == shape()
        ));
        let mut other_format = recording(&e);
        other_format[4] = STREAM_FORMAT + 1;
        assert!(matches!(
            Replay::from_reader(other_format.as_slice(), d.clone()),
            Err(RecordError::Format { .. })
        ));
        assert!(matches!(
            Replay::from_reader(&b"RIFF\x01\0\0\0\0\0\0\0\0"[..], d),
            Err(RecordError::NotARecording)
        ));
    }

    #[test]
    fn a_cut_recording_is_an_error_not_a_panic() {
        let e = EncodeContext::new(Instant::now());
        let bytes = recording(&e);
        let header = 4 + 1 + 8;
        let first = 12 + batch(&e, "one").len();
        for end in 0..bytes.len() {
            let d = DecodeContext::new(Instant::now());
            match Replay::from_reader(&bytes[..end], d) {
                Err(error) => {
                    assert!(end < header, "{end}: {error}");
                    assert!(matches!(error, RecordError::Truncated), "{end}: {error}");
                }
                Ok(replay) => {
                    let results: Vec<_> = replay.collect();
                    let whole = usize::from(end >= header + first);
                    // The whole records read; a cut one is an error, and
                    // nothing follows it.
                    assert!(results[..whole].iter().all(Result::is_ok), "{end}");
                    let cut = end != header && end != header + first;
                    assert_eq!(results.len(), whole + usize::from(cut), "{end}");
                    if cut {
                        assert!(
                            matches!(results[whole], Err(RecordError::Truncated)),
                            "{end}"
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn a_lying_length_allocates_nothing() {
        let mut bytes = Recorder::new(Vec::new()).unwrap().finish().unwrap();
        bytes.extend_from_slice(&0u64.to_le_bytes());
        bytes.extend_from_slice(&u32::MAX.to_le_bytes());
        bytes.extend_from_slice(&[1, 2, 3]);
        let d = DecodeContext::new(Instant::now());
        let results: Vec<_> = Replay::from_reader(bytes.as_slice(), d).unwrap().collect();
        assert!(matches!(results[..], [Err(RecordError::Truncated)]));
    }
}
