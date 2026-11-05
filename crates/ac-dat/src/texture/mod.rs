//! Texture decoding: a render surface's stored pixels to 8-bit RGBA.
//!
//! Every pixel format found in the retail dats is decoded: uncompressed
//! colour (24-bit, 32-bit, 5:6:5, 4:4:4:4), alpha-only, paletted (8- and
//! 16-bit indices), block-compressed (DXT1/3/5) and whole JPEG files. The
//! output is always `width * height` pixels of red, green, blue, alpha.
//!
//! Two options change the result the way the surface that uses a texture
//! asks: a clip map turns its holes transparent (black pixels in
//! compressed textures, palette entries 0–7 in paletted ones), and an
//! additive surface's alpha-only texture puts the value in every channel
//! instead of white with that alpha.

mod bc;
pub mod jpeg;

use crate::objects::{Palette, PixelFormat, RenderSurface};
use crate::types::ColorArgb;

pub use jpeg::JpegError;

/// A decoded image.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DecodedTexture {
    /// Width in pixels.
    pub width: u32,
    /// Height in pixels.
    pub height: u32,
    /// Pixels, row by row from the top, four bytes each: red, green, blue,
    /// alpha.
    pub rgba: Vec<u8>,
}

/// How the surface using a texture wants it decoded.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DecodeOptions {
    /// The surface is a clip map: holes become transparent.
    pub clip_map: bool,
    /// The surface is additive: alpha-only pixels go to every channel.
    pub additive: bool,
}

/// Why a texture could not be decoded.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum TextureError {
    /// The surface has a zero width or height.
    #[error("texture has no size ({width}x{height})")]
    ZeroSize {
        /// Stored width.
        width: usize,
        /// Stored height.
        height: usize,
    },
    /// The image is too large to address.
    #[error("texture is too large ({width}x{height})")]
    TooLarge {
        /// Stored width.
        width: usize,
        /// Stored height.
        height: usize,
    },
    /// The stored pixels are fewer than the size needs.
    #[error("texture data is {len} bytes, {needed} needed")]
    DataTooShort {
        /// Bytes the size needs.
        needed: usize,
        /// Bytes stored.
        len: usize,
    },
    /// Block-compressed data is not a whole number of blocks.
    #[error("block-compressed data is {len} bytes, not a multiple of {block}")]
    PartialBlock {
        /// Bytes stored.
        len: usize,
        /// Block size.
        block: usize,
    },
    /// A paletted texture was decoded without a palette.
    #[error("paletted texture needs a palette")]
    MissingPalette,
    /// The palette has no colours.
    #[error("palette is empty")]
    EmptyPalette,
    /// A pixel indexes past the end of the palette.
    #[error("palette index {index} is outside a palette of {len} colours")]
    PaletteIndex {
        /// The index.
        index: usize,
        /// Colours in the palette.
        len: usize,
    },
    /// The pixel format is not one the game's textures use.
    #[error("pixel format {0:#x} is not decoded")]
    UnsupportedFormat(u32),
    /// A JPEG surface could not be decoded.
    #[error(transparent)]
    Jpeg(#[from] JpegError),
}

type Result<T> = core::result::Result<T, TextureError>;

/// Decode a render surface. Paletted formats need `palette` (normally the
/// surface's [`RenderSurface::default_palette_id`]); other formats ignore
/// it.
pub fn decode(
    surface: &RenderSurface,
    palette: Option<&Palette>,
    options: DecodeOptions,
) -> Result<DecodedTexture> {
    let data = surface.data.as_slice();
    if surface.format == PixelFormat::CUSTOM_RAW_JPEG {
        let image = jpeg::decode(data)?;
        return Ok(DecodedTexture {
            width: image.width,
            height: image.height,
            rgba: image.rgba,
        });
    }
    let (width, height) = (surface.width as usize, surface.height as usize);
    if width == 0 || height == 0 {
        return Err(TextureError::ZeroSize { width, height });
    }
    let pixels = width
        .checked_mul(height)
        .filter(|n| n.checked_mul(4).is_some())
        .ok_or(TextureError::TooLarge { width, height })?;
    let rgba = match surface.format {
        PixelFormat::R8G8B8 | PixelFormat::CUSTOM_LSCAPE_R8G8B8 => {
            convert(data, pixels, 3, |p| [p[2], p[1], p[0], 255])?
        }
        PixelFormat::A8R8G8B8 => convert(data, pixels, 4, |p| [p[2], p[1], p[0], p[3]])?,
        PixelFormat::X8R8G8B8 => convert(data, pixels, 4, |p| [p[2], p[1], p[0], 255])?,
        PixelFormat::R5G6B5 => convert(data, pixels, 2, |p| {
            let v = u16::from_le_bytes([p[0], p[1]]);
            [
                (((v & 0xF800) >> 11) << 3) as u8,
                (((v & 0x07E0) >> 5) << 2) as u8,
                ((v & 0x1F) << 3) as u8,
                255,
            ]
        })?,
        PixelFormat::A4R4G4B4 => convert(data, pixels, 2, |p| {
            let v = u16::from_le_bytes([p[0], p[1]]);
            let n = |shift: u16| ((v >> shift) & 0xF) as u8 * 17;
            [n(8), n(4), n(0), n(12)]
        })?,
        PixelFormat::A8 | PixelFormat::CUSTOM_LSCAPE_ALPHA => {
            if options.additive {
                convert(data, pixels, 1, |p| [p[0], p[0], p[0], p[0]])?
            } else {
                convert(data, pixels, 1, |p| [255, 255, 255, p[0]])?
            }
        }
        PixelFormat::P8 => paletted(data, pixels, 1, palette, options.clip_map)?,
        PixelFormat::INDEX16 => paletted(data, pixels, 2, palette, options.clip_map)?,
        PixelFormat::DXT1 => bc::decode(data, width, height, bc::Kind::Dxt1, options.clip_map)?,
        PixelFormat::DXT3 => bc::decode(data, width, height, bc::Kind::Dxt3, options.clip_map)?,
        PixelFormat::DXT5 => bc::decode(data, width, height, bc::Kind::Dxt5, options.clip_map)?,
        other => return Err(TextureError::UnsupportedFormat(other.0)),
    };
    Ok(DecodedTexture {
        width: surface.width,
        height: surface.height,
        rgba,
    })
}

/// Decode block-compressed pixels (DXT1, DXT3 or DXT5) of a `width` x
/// `height` image to RGBA, as [`decode`] does for a render surface of that
/// format: the texture-detail option uses it to shrink a texture that stays
/// compressed. Other formats are [`TextureError::UnsupportedFormat`].
pub fn decode_block_compressed(
    data: &[u8],
    width: u32,
    height: u32,
    format: PixelFormat,
    clip_map: bool,
) -> Result<Vec<u8>> {
    let kind = match format {
        PixelFormat::DXT1 => bc::Kind::Dxt1,
        PixelFormat::DXT3 => bc::Kind::Dxt3,
        PixelFormat::DXT5 => bc::Kind::Dxt5,
        other => return Err(TextureError::UnsupportedFormat(other.0)),
    };
    let (w, h) = (width as usize, height as usize);
    if w == 0 || h == 0 {
        return Err(TextureError::ZeroSize {
            width: w,
            height: h,
        });
    }
    if w.checked_mul(h).and_then(|n| n.checked_mul(4)).is_none() {
        return Err(TextureError::TooLarge {
            width: w,
            height: h,
        });
    }
    bc::decode(data, w, h, kind, clip_map)
}

/// Check the data covers `pixels` pixels of `bytes` each, then map each
/// pixel's bytes to RGBA.
fn convert(
    data: &[u8],
    pixels: usize,
    bytes: usize,
    f: impl Fn(&[u8]) -> [u8; 4],
) -> Result<Vec<u8>> {
    let needed = pixels * bytes;
    if data.len() < needed {
        return Err(TextureError::DataTooShort {
            needed,
            len: data.len(),
        });
    }
    let mut out = Vec::with_capacity(pixels * 4);
    for p in data[..needed].chunks_exact(bytes) {
        out.extend_from_slice(&f(p));
    }
    Ok(out)
}

/// Paletted pixels. In a clip map, indices 0–7 are holes.
fn paletted(
    data: &[u8],
    pixels: usize,
    bytes: usize,
    palette: Option<&Palette>,
    clip_map: bool,
) -> Result<Vec<u8>> {
    let palette = palette.ok_or(TextureError::MissingPalette)?;
    let colors = &palette.colors;
    if colors.is_empty() {
        return Err(TextureError::EmptyPalette);
    }
    let needed = pixels * bytes;
    if data.len() < needed {
        return Err(TextureError::DataTooShort {
            needed,
            len: data.len(),
        });
    }
    let mut out = Vec::with_capacity(pixels * 4);
    for p in data[..needed].chunks_exact(bytes) {
        let index = if bytes == 1 {
            usize::from(p[0])
        } else {
            usize::from(u16::from_le_bytes([p[0], p[1]]))
        };
        let c = colors.get(index).ok_or(TextureError::PaletteIndex {
            index,
            len: colors.len(),
        })?;
        if clip_map && index < 8 {
            out.extend_from_slice(&[0, 0, 0, 0]);
        } else {
            out.extend_from_slice(&[c.red, c.green, c.blue, c.alpha]);
        }
    }
    Ok(out)
}

/// A 1×1 texture of a solid colour, its alpha scaled by the surface's
/// opacity (`1 - translucency`, clamped to 0..1).
#[must_use]
pub fn solid_color(color: ColorArgb, translucency: f32) -> DecodedTexture {
    let opacity = (1.0 - translucency).clamp(0.0, 1.0);
    let alpha = (f32::from(color.alpha) * opacity).clamp(0.0, 255.0) as u8;
    DecodedTexture {
        width: 1,
        height: 1,
        rgba: vec![color.red, color.green, color.blue, alpha],
    }
}

/// Scale every alpha by the surface's opacity (`1 - translucency`); a
/// translucency of 0 or less changes nothing.
pub fn apply_translucency(texture: &mut DecodedTexture, translucency: f32) {
    if translucency <= 0.0 {
        return;
    }
    let scale = (1.0 - translucency).clamp(0.0, 1.0);
    for a in texture.rgba.iter_mut().skip(3).step_by(4) {
        *a = (f32::from(*a) * scale) as u8;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn surface(format: PixelFormat, width: u32, height: u32, data: &[u8]) -> RenderSurface {
        RenderSurface {
            id: 0x0600_0001,
            data_category: 0,
            width,
            height,
            format,
            data: data.to_vec(),
            default_palette_id: 0,
        }
    }

    fn rgba(s: &RenderSurface, options: DecodeOptions) -> Vec<u8> {
        decode(s, None, options).unwrap().rgba
    }

    #[test]
    fn uncompressed_colour_is_stored_blue_first() {
        let s = surface(PixelFormat::R8G8B8, 1, 1, &[1, 2, 3]);
        assert_eq!(rgba(&s, DecodeOptions::default()), [3, 2, 1, 255]);
        let s = surface(PixelFormat::CUSTOM_LSCAPE_R8G8B8, 1, 1, &[1, 2, 3]);
        assert_eq!(rgba(&s, DecodeOptions::default()), [3, 2, 1, 255]);
        let s = surface(PixelFormat::A8R8G8B8, 1, 1, &[1, 2, 3, 4]);
        assert_eq!(rgba(&s, DecodeOptions::default()), [3, 2, 1, 4]);
        let s = surface(PixelFormat::X8R8G8B8, 1, 1, &[1, 2, 3, 4]);
        assert_eq!(rgba(&s, DecodeOptions::default()), [3, 2, 1, 255]);
    }

    #[test]
    fn sixteen_bit_colour() {
        // 5:6:5 shifts without repeating the top bits.
        let s = surface(PixelFormat::R5G6B5, 1, 1, &0xFFFFu16.to_le_bytes());
        assert_eq!(rgba(&s, DecodeOptions::default()), [248, 252, 248, 255]);
        // 4:4:4:4 scales each nibble by 17.
        let s = surface(PixelFormat::A4R4G4B4, 1, 1, &0x1234u16.to_le_bytes());
        assert_eq!(rgba(&s, DecodeOptions::default()), [34, 51, 68, 17]);
    }

    #[test]
    fn alpha_only_is_white_or_grey() {
        let s = surface(PixelFormat::A8, 2, 1, &[0x40, 0xFF]);
        assert_eq!(
            rgba(&s, DecodeOptions::default()),
            [255, 255, 255, 0x40, 255, 255, 255, 0xFF]
        );
        let additive = DecodeOptions {
            additive: true,
            ..DecodeOptions::default()
        };
        let s = surface(PixelFormat::CUSTOM_LSCAPE_ALPHA, 1, 1, &[0x40]);
        assert_eq!(rgba(&s, additive), [0x40; 4]);
    }

    fn palette(n: usize) -> Palette {
        Palette {
            id: 0x0400_0001,
            colors: (0..n)
                .map(|i| ColorArgb {
                    blue: i as u8,
                    green: 1,
                    red: 2,
                    alpha: 3,
                })
                .collect(),
        }
    }

    #[test]
    fn paletted_pixels_look_up_colours() {
        let p = palette(300);
        let s = surface(PixelFormat::P8, 2, 1, &[9, 7]);
        let out = decode(&s, Some(&p), DecodeOptions::default()).unwrap();
        assert_eq!(out.rgba, [2, 1, 9, 3, 2, 1, 7, 3]);
        let clip = DecodeOptions {
            clip_map: true,
            ..DecodeOptions::default()
        };
        let out = decode(&s, Some(&p), clip).unwrap();
        assert_eq!(out.rgba, [2, 1, 9, 3, 0, 0, 0, 0]);
        let s = surface(PixelFormat::INDEX16, 1, 1, &280u16.to_le_bytes());
        let out = decode(&s, Some(&p), DecodeOptions::default()).unwrap();
        assert_eq!(out.rgba, [2, 1, 24, 3]);
    }

    #[test]
    fn paletted_errors() {
        let s = surface(PixelFormat::P8, 1, 1, &[9]);
        assert_eq!(
            decode(&s, None, DecodeOptions::default()),
            Err(TextureError::MissingPalette)
        );
        assert_eq!(
            decode(&s, Some(&palette(0)), DecodeOptions::default()),
            Err(TextureError::EmptyPalette)
        );
        assert_eq!(
            decode(&s, Some(&palette(4)), DecodeOptions::default()),
            Err(TextureError::PaletteIndex { index: 9, len: 4 })
        );
    }

    #[test]
    fn size_errors() {
        let s = surface(PixelFormat::A8R8G8B8, 2, 2, &[0; 15]);
        assert_eq!(
            decode(&s, None, DecodeOptions::default()),
            Err(TextureError::DataTooShort {
                needed: 16,
                len: 15
            })
        );
        let s = surface(PixelFormat::A8, 0, 4, &[]);
        assert_eq!(
            decode(&s, None, DecodeOptions::default()),
            Err(TextureError::ZeroSize {
                width: 0,
                height: 4
            })
        );
        let s = surface(PixelFormat::L8, 1, 1, &[0]);
        assert_eq!(
            decode(&s, None, DecodeOptions::default()),
            Err(TextureError::UnsupportedFormat(0x32))
        );
        let s = surface(PixelFormat::CUSTOM_RAW_JPEG, 0, 0, b"not a jpeg");
        assert_eq!(
            decode(&s, None, DecodeOptions::default()),
            Err(TextureError::Jpeg(JpegError::NotJpeg))
        );
    }

    #[test]
    fn solid_colour_and_translucency() {
        let c = ColorArgb {
            blue: 3,
            green: 2,
            red: 1,
            alpha: 200,
        };
        assert_eq!(solid_color(c, 0.0).rgba, [1, 2, 3, 200]);
        // 200 * 0.75 = 150.
        assert_eq!(solid_color(c, 0.25).rgba, [1, 2, 3, 150]);
        assert_eq!(solid_color(c, 2.0).rgba, [1, 2, 3, 0]);
        assert_eq!(solid_color(c, -1.0).rgba, [1, 2, 3, 200]);

        let mut t = solid_color(c, 0.0);
        apply_translucency(&mut t, 0.5);
        assert_eq!(t.rgba, [1, 2, 3, 100]);
        apply_translucency(&mut t, -0.5);
        assert_eq!(t.rgba, [1, 2, 3, 100]);
    }
}
