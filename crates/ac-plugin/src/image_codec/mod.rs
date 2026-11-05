//! A plugin's own art decoded by the client (OpenAC `IPluginImages.FromStream`:
//! "PNG, JPEG, BMP, TGA or GIF"), to 8-bit RGBA rows from the top.
//!
//! PNG is the plugin icon reader ([`crate::icon::decode_png`]) and JPEG the
//! dat texture decoder (`ac_dat::texture::jpeg`); BMP, TGA and GIF are read
//! here, written from the formats' specifications. A GIF gives its first
//! frame on a canvas of its logical screen size, transparent where the frame
//! draws nothing. Malformed input is an error, never a panic, and nothing is
//! allocated for more than [`MAXIMUM_PIXELS`].

mod bmp;
mod gif;
#[cfg(test)]
mod tests;
mod tga;

/// The most pixels a decoder here allocates for. The image table refuses
/// an image wider or taller than its budget (2048) on the header's word
/// before any decode; this bounds a decode asked for directly.
pub const MAXIMUM_PIXELS: u64 = 1 << 24;

/// A decoded image.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Decoded {
    /// Width, pixels.
    pub width: u32,
    /// Height, pixels.
    pub height: u32,
    /// `width * height` pixels of red, green, blue, alpha, top row first.
    pub rgba: Vec<u8>,
}

/// Why an image did not decode.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum CodecError {
    /// Not one of the formats read here.
    #[error("unknown image type")]
    UnknownFormat,
    /// The file breaks its format; the text names what.
    #[error("corrupt {0}: {1}")]
    Corrupt(&'static str, &'static str),
    /// A legal variant of the format this decoder does not read.
    #[error("{0} {1} is not supported")]
    Unsupported(&'static str, &'static str),
    /// The file ends before its image does.
    #[error("{0} data ends early")]
    Truncated(&'static str),
    /// More pixels than [`MAXIMUM_PIXELS`].
    #[error("image is too large")]
    TooLarge,
    /// The JPEG decoder's error.
    #[error(transparent)]
    Jpeg(#[from] ac_dat::texture::jpeg::JpegError),
}

type Result<T> = std::result::Result<T, CodecError>;

/// The formats read here.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageFormat {
    /// PNG.
    Png,
    /// JPEG (JFIF).
    Jpeg,
    /// Windows bitmap.
    Bmp,
    /// GIF 87a or 89a.
    Gif,
    /// Truevision TGA (no signature: recognised by a header that makes
    /// sense, so it is tried last).
    Tga,
}

const PNG_SIGNATURE: [u8; 8] = [137, 80, 78, 71, 13, 10, 26, 10];

/// Which format `bytes` are in, if one read here.
#[must_use]
pub fn detect(bytes: &[u8]) -> Option<ImageFormat> {
    if bytes.starts_with(&PNG_SIGNATURE) {
        Some(ImageFormat::Png)
    } else if bmp::is_bmp(bytes) {
        Some(ImageFormat::Bmp)
    } else if gif::is_gif(bytes) {
        Some(ImageFormat::Gif)
    } else if bytes.starts_with(&[0xFF, 0xD8]) {
        Some(ImageFormat::Jpeg)
    } else if tga::is_tga(bytes) {
        Some(ImageFormat::Tga)
    } else {
        None
    }
}

/// The width and height the file's header declares, read from the header
/// alone; `None` when it is not a header read here.
#[must_use]
pub fn dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    match detect(bytes)? {
        ImageFormat::Png => {
            if bytes.get(12..16)? != b"IHDR" {
                return None;
            }
            Some((be32(bytes, 16)?, be32(bytes, 20)?))
        }
        ImageFormat::Jpeg => jpeg_dimensions(bytes),
        ImageFormat::Bmp => bmp::dimensions(bytes),
        ImageFormat::Gif => gif::dimensions(bytes),
        ImageFormat::Tga => tga::dimensions(bytes),
    }
}

/// Decodes `bytes` to RGBA.
///
/// # Errors
/// Not a format read here, or a file its decoder refuses.
pub fn decode(bytes: &[u8]) -> Result<Decoded> {
    match detect(bytes).ok_or(CodecError::UnknownFormat)? {
        ImageFormat::Png => {
            let image = crate::icon::decode_png(bytes)
                .ok_or(CodecError::Corrupt("PNG", "the image does not decode"))?;
            Ok(Decoded {
                width: image.width,
                height: image.height,
                rgba: image.rgba.as_ref().clone(),
            })
        }
        ImageFormat::Jpeg => {
            if let Some((w, h)) = jpeg_dimensions(bytes) {
                check_size(w, h)?;
            }
            let image = ac_dat::texture::jpeg::decode(bytes)?;
            Ok(Decoded {
                width: image.width,
                height: image.height,
                rgba: image.rgba,
            })
        }
        ImageFormat::Bmp => bmp::decode(bytes),
        ImageFormat::Gif => gif::decode(bytes),
        ImageFormat::Tga => tga::decode(bytes),
    }
}

/// Refuses an empty image or one past [`MAXIMUM_PIXELS`].
fn check_size(width: u32, height: u32) -> Result<()> {
    if u64::from(width) * u64::from(height) > MAXIMUM_PIXELS {
        return Err(CodecError::TooLarge);
    }
    Ok(())
}

fn be16(bytes: &[u8], at: usize) -> Option<u32> {
    let b = bytes.get(at..at + 2)?;
    Some(u32::from(u16::from_be_bytes([b[0], b[1]])))
}

fn be32(bytes: &[u8], at: usize) -> Option<u32> {
    let b = bytes.get(at..at + 4)?;
    Some(u32::from_be_bytes([b[0], b[1], b[2], b[3]]))
}

/// A JPEG's size from its frame header (any start-of-frame marker but the
/// Huffman and arithmetic table markers that share the range).
fn jpeg_dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    let mut at = 2;
    loop {
        // Fill bytes before a marker are allowed.
        if *bytes.get(at)? != 0xFF {
            return None;
        }
        while *bytes.get(at)? == 0xFF {
            at += 1;
        }
        let marker = *bytes.get(at)?;
        at += 1;
        match marker {
            0x01 | 0xD0..=0xD8 => continue,
            0xD9 | 0xDA => return None,
            _ => {}
        }
        let length = be16(bytes, at)? as usize;
        if length < 2 {
            return None;
        }
        if (0xC0..=0xCF).contains(&marker) && !matches!(marker, 0xC4 | 0xC8 | 0xCC) {
            return Some((be16(bytes, at + 5)?, be16(bytes, at + 3)?));
        }
        at += length;
    }
}

/// A little-endian reader that reports a short file as
/// [`CodecError::Truncated`].
struct Reader<'a> {
    data: &'a [u8],
    at: usize,
    format: &'static str,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8], format: &'static str) -> Self {
        Self {
            data,
            at: 0,
            format,
        }
    }

    fn take(&mut self, count: usize) -> Result<&'a [u8]> {
        let end = self
            .at
            .checked_add(count)
            .filter(|end| *end <= self.data.len())
            .ok_or(CodecError::Truncated(self.format))?;
        let slice = &self.data[self.at..end];
        self.at = end;
        Ok(slice)
    }

    fn skip(&mut self, count: usize) -> Result<()> {
        self.take(count).map(|_| ())
    }

    fn u8(&mut self) -> Result<u8> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16> {
        let b = self.take(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }

    fn u32(&mut self) -> Result<u32> {
        let b = self.take(4)?;
        Ok(u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }

    fn i32(&mut self) -> Result<i32> {
        let b = self.take(4)?;
        Ok(i32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }
}
