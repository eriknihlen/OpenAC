//! Turning a dat wave (a Windows wave format header and its data) into
//! playable PCM.

use std::io::Cursor;
use std::time::Duration;

use symphonia_bundle_mp3::{MpaDecoder, MpaReader};
use symphonia_core::codecs::audio::{AudioDecoder, AudioDecoderOptions};
use symphonia_core::formats::{FormatOptions, FormatReader};
use symphonia_core::io::{MediaSourceStream, MediaSourceStreamOptions};

/// The encoding named by the first two bytes of a wave format header.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct WaveFormatTag(pub u16);

impl WaveFormatTag {
    /// No header, or one too short to name an encoding.
    pub const UNKNOWN: Self = Self(0x0000);
    /// Microsoft PCM.
    pub const PCM: Self = Self(0x0001);
    /// Microsoft ADPCM (no retail wave uses it).
    pub const ADPCM: Self = Self(0x0002);
    /// MPEG layer 3 (one retail wave uses it).
    pub const MP3: Self = Self(0x0055);
}

/// A decoded wave: interleaved PCM, unsigned 8-bit or signed 16-bit
/// little-endian.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WaveData {
    /// Channels (1 or 2 in the retail dats).
    pub channel_count: u16,
    /// Frames per second.
    pub sample_rate: u32,
    /// 8 or 16 for every retail wave.
    pub bits_per_sample: u16,
    /// The samples.
    pub pcm_bytes: Vec<u8>,
    /// Playing time.
    pub duration: Duration,
}

impl WaveData {
    /// Whether the mixer can play this layout (mono or stereo, 8 or 16 bits).
    #[must_use]
    pub fn is_playable(&self) -> bool {
        matches!(self.channel_count, 1 | 2) && matches!(self.bits_per_sample, 8 | 16)
    }

    /// Bytes per frame (all channels of one sample instant).
    #[must_use]
    pub fn frame_bytes(&self) -> usize {
        usize::from(self.channel_count) * usize::from(self.bits_per_sample / 8)
    }

    /// Whole frames held.
    #[must_use]
    pub fn frame_count(&self) -> usize {
        match self.frame_bytes() {
            0 => 0,
            n => self.pcm_bytes.len() / n,
        }
    }
}

/// The encoding a header names; [`WaveFormatTag::UNKNOWN`] when it is too
/// short to name one.
#[must_use]
pub fn peek_format(header: &[u8]) -> WaveFormatTag {
    match header {
        [lo, hi, ..] => WaveFormatTag(u16::from_le_bytes([*lo, *hi])),
        _ => WaveFormatTag::UNKNOWN,
    }
}

/// Decode a wave. `None` when the header is shorter than 14 bytes, names no
/// channels or no rate, names an encoding other than PCM or MP3, or the MP3
/// data holds no decodable frame.
///
/// PCM data is the sample buffer as stored. MP3 data is decoded to 16-bit
/// PCM at the rate and channel count of its frames.
#[must_use]
pub fn decode(header: &[u8], data: &[u8]) -> Option<WaveData> {
    if header.len() < 14 {
        return None;
    }
    let format_tag = u16::from_le_bytes([header[0], header[1]]);
    let channels = u16::from_le_bytes([header[2], header[3]]);
    let sample_rate = u32::from_le_bytes([header[4], header[5], header[6], header[7]]);
    let bits_per_sample = if header.len() >= 16 {
        u16::from_le_bytes([header[14], header[15]])
    } else {
        16
    };
    if channels == 0 || sample_rate == 0 {
        return None;
    }
    match WaveFormatTag(format_tag) {
        WaveFormatTag::PCM => {
            let seconds = if bits_per_sample > 0 {
                data.len() as f64 * 8.0
                    / (f64::from(sample_rate) * f64::from(channels) * f64::from(bits_per_sample))
            } else {
                0.0
            };
            Some(WaveData {
                channel_count: channels,
                sample_rate,
                bits_per_sample: if bits_per_sample == 0 {
                    16
                } else {
                    bits_per_sample
                },
                pcm_bytes: data.to_vec(),
                duration: Duration::from_secs_f64(seconds),
            })
        }
        WaveFormatTag::MP3 => decode_mp3(data),
        _ => None,
    }
}

/// Decode raw MPEG audio frames to 16-bit PCM.
fn decode_mp3(data: &[u8]) -> Option<WaveData> {
    let source = MediaSourceStream::new(
        Box::new(Cursor::new(data.to_vec())),
        MediaSourceStreamOptions::default(),
    );
    let mut reader = MpaReader::try_new(source, FormatOptions::default()).ok()?;
    let params = reader
        .tracks()
        .first()?
        .codec_params
        .as_ref()?
        .audio()?
        .clone();
    // No gapless trimming: the stream is played as decoded, delay included.
    let mut options = AudioDecoderOptions::default();
    options.gapless = false;
    let mut decoder = MpaDecoder::try_new(&params, &options).ok()?;
    let mut samples: Vec<i16> = Vec::new();
    let mut packet_samples: Vec<i16> = Vec::new();
    let mut spec: Option<(u32, usize)> = None;
    // A frame that fails to read or decode refuses the whole wave rather
    // than playing a truncated one.
    while let Some(packet) = reader.next_packet().ok()? {
        let buffer = decoder.decode(&packet).ok()?;
        let this = (buffer.spec().rate(), buffer.spec().channels().count());
        if *spec.get_or_insert(this) != this {
            // A stream whose frames change format mid-way is not a sound
            // the mixer can hold as one buffer.
            return None;
        }
        buffer.copy_to_vec_interleaved(&mut packet_samples);
        samples.extend_from_slice(&packet_samples);
    }
    let (rate, channels) = spec?;
    let channels = u16::try_from(channels).ok().filter(|c| *c > 0)?;
    if samples.is_empty() || rate == 0 {
        return None;
    }
    let frames = samples.len() / usize::from(channels);
    let pcm_bytes = samples.iter().flat_map(|s| s.to_le_bytes()).collect();
    Some(WaveData {
        channel_count: channels,
        sample_rate: rate,
        bits_per_sample: 16,
        pcm_bytes,
        duration: Duration::from_secs_f64(frames as f64 / f64::from(rate)),
    })
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Audio/WaveDecoderTests.cs`.
    //! The MP3 cases differ on purpose: OpenAC refuses every MP3 wave, we
    //! decode them, so a header of MP3
    //! with no frames in its data is what decodes to nothing here.

    use super::*;

    fn pcm_header(channels: u16, rate: u32, bits: u16) -> Vec<u8> {
        let mut h = vec![0u8; 18];
        h[0..2].copy_from_slice(&1u16.to_le_bytes());
        h[2..4].copy_from_slice(&channels.to_le_bytes());
        h[4..8].copy_from_slice(&rate.to_le_bytes());
        let avg = rate * u32::from(channels) * u32::from(bits / 8);
        h[8..12].copy_from_slice(&avg.to_le_bytes());
        h[12..14].copy_from_slice(&(channels * (bits / 8)).to_le_bytes());
        h[14..16].copy_from_slice(&bits.to_le_bytes());
        h
    }

    #[test]
    fn decode_pcm_header_returns_wave_data() {
        let data = vec![0u8; 22050 * 2];
        let d = decode(&pcm_header(1, 22050, 16), &data).unwrap();
        assert_eq!(d.channel_count, 1);
        assert_eq!(d.sample_rate, 22050);
        assert_eq!(d.bits_per_sample, 16);
        assert_eq!(d.pcm_bytes, data);
        let s = d.duration.as_secs_f64();
        assert!((0.99..=1.01).contains(&s), "{s}");
    }

    #[test]
    fn decode_stereo_pcm_works() {
        let data = vec![0u8; 44100 * 2 * 2];
        let d = decode(&pcm_header(2, 44100, 16), &data).unwrap();
        assert_eq!(d.channel_count, 2);
        assert_eq!(d.sample_rate, 44100);
        assert_eq!(d.frame_count(), 44100);
    }

    #[test]
    fn decode_mp3_header_with_no_frames_returns_none() {
        let mut header = vec![0u8; 30];
        header[0] = 0x55;
        header[2] = 0x01;
        header[4] = 0x44;
        header[5] = 0xAC;
        assert_eq!(decode(&header, &[0u8; 1024]), None);
    }

    #[test]
    fn decode_adpcm_header_returns_none() {
        let mut header = vec![0u8; 20];
        header[0] = 0x02;
        assert_eq!(decode(&header, &[0u8; 1024]), None);
    }

    #[test]
    fn decode_truncated_header_returns_none() {
        assert_eq!(decode(&[0u8; 5], &[0u8; 100]), None);
    }

    #[test]
    fn decode_zero_channels_or_rate_returns_none() {
        assert_eq!(decode(&pcm_header(0, 22050, 16), &[0u8; 4]), None);
        assert_eq!(decode(&pcm_header(1, 0, 16), &[0u8; 4]), None);
    }

    #[test]
    fn a_fourteen_byte_header_defaults_to_sixteen_bits() {
        let header = &pcm_header(1, 8000, 8)[..14];
        let d = decode(header, &[0u8; 16]).unwrap();
        assert_eq!(d.bits_per_sample, 16);
    }

    #[test]
    fn peek_format_returns_the_tag() {
        assert_eq!(peek_format(&pcm_header(1, 22050, 16)), WaveFormatTag::PCM);
        assert_eq!(peek_format(&[0x55, 0x00, 0x01, 0x00]), WaveFormatTag::MP3);
        assert_eq!(peek_format(&[]), WaveFormatTag::UNKNOWN);
    }
}
