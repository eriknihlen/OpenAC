use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// How a render surface's pixels are stored. Most values are Direct3D
/// format numbers; the `CUSTOM_*` ones are the game's own.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct PixelFormat(pub u32);

#[allow(missing_docs)]
impl PixelFormat {
    pub const UNKNOWN: Self = Self(0x00);
    /// 24-bit colour, stored blue, green, red.
    pub const R8G8B8: Self = Self(0x14);
    /// 32-bit colour with alpha, stored blue, green, red, alpha.
    pub const A8R8G8B8: Self = Self(0x15);
    /// 32-bit colour, stored blue, green, red, unused.
    pub const X8R8G8B8: Self = Self(0x16);
    /// 16-bit colour: 5 bits red, 6 green, 5 blue.
    pub const R5G6B5: Self = Self(0x17);
    pub const X1R5G5B5: Self = Self(0x18);
    pub const A1R5G5B5: Self = Self(0x19);
    /// 16-bit colour: 4 bits each of alpha, red, green, blue.
    pub const A4R4G4B4: Self = Self(0x1A);
    pub const R3G3B2: Self = Self(0x1B);
    /// 8-bit alpha only.
    pub const A8: Self = Self(0x1C);
    pub const A8R3G3B2: Self = Self(0x1D);
    pub const X4R4G4B4: Self = Self(0x1E);
    pub const A2B10G10R10: Self = Self(0x1F);
    pub const A8B8G8R8: Self = Self(0x20);
    pub const X8B8G8R8: Self = Self(0x21);
    pub const A2R10G10B10: Self = Self(0x23);
    pub const A8P8: Self = Self(0x28);
    /// 8-bit palette index.
    pub const P8: Self = Self(0x29);
    pub const L8: Self = Self(0x32);
    pub const A8L8: Self = Self(0x33);
    pub const A4L4: Self = Self(0x34);
    pub const V8U8: Self = Self(0x3C);
    pub const L6V5U5: Self = Self(0x3D);
    pub const X8L8V8U8: Self = Self(0x3E);
    pub const Q8W8V8U8: Self = Self(0x3F);
    pub const V16U16: Self = Self(0x40);
    pub const A2W10V10U10: Self = Self(0x43);
    pub const D16_LOCKABLE: Self = Self(0x46);
    pub const D32: Self = Self(0x47);
    pub const D15S1: Self = Self(0x49);
    pub const D24S8: Self = Self(0x4B);
    pub const D24X8: Self = Self(0x4D);
    pub const D24X4S4: Self = Self(0x4F);
    pub const D16: Self = Self(0x50);
    pub const VERTEX_DATA: Self = Self(0x64);
    /// 16-bit palette index.
    pub const INDEX16: Self = Self(0x65);
    pub const INDEX32: Self = Self(0x66);
    pub const CUSTOM_R8G8B8A8: Self = Self(0xF0);
    pub const CUSTOM_A8B8G8R8: Self = Self(0xF1);
    pub const CUSTOM_B8G8R8: Self = Self(0xF2);
    /// Landscape colour; laid out like [`PixelFormat::R8G8B8`].
    pub const CUSTOM_LSCAPE_R8G8B8: Self = Self(0xF3);
    /// Landscape alpha map; laid out like [`PixelFormat::A8`].
    pub const CUSTOM_LSCAPE_ALPHA: Self = Self(0xF4);
    /// A whole JPEG file; the surface's width and height are 0.
    pub const CUSTOM_RAW_JPEG: Self = Self(0x1F4);
    /// Block-compressed, 8 bytes per 4×4 block (BC1).
    pub const DXT1: Self = Self(0x3154_5844);
    pub const DXT2: Self = Self(0x3254_5844);
    pub const YUY2: Self = Self(0x3259_5559);
    /// Block-compressed with explicit 4-bit alpha (BC2).
    pub const DXT3: Self = Self(0x3354_5844);
    pub const DXT4: Self = Self(0x3454_5844);
    /// Block-compressed with interpolated alpha (BC3).
    pub const DXT5: Self = Self(0x3554_5844);
    pub const G8R8_G8B8: Self = Self(0x4247_5247);
    pub const R8G8_B8G8: Self = Self(0x4742_4752);
    pub const UYVY: Self = Self(0x5956_5955);
    pub const INVALID: Self = Self(0x7FFF_FFFF);

    /// True for the palette-indexed formats, which store a default palette
    /// id after the pixels.
    #[must_use]
    pub fn is_paletted(self) -> bool {
        self == Self::P8 || self == Self::INDEX16
    }

    /// A short name for display.
    #[must_use]
    pub fn name(self) -> &'static str {
        match self {
            Self::R8G8B8 => "R8G8B8",
            Self::A8R8G8B8 => "A8R8G8B8",
            Self::X8R8G8B8 => "X8R8G8B8",
            Self::R5G6B5 => "R5G6B5",
            Self::A4R4G4B4 => "A4R4G4B4",
            Self::A8 => "A8",
            Self::P8 => "P8",
            Self::INDEX16 => "INDEX16",
            Self::CUSTOM_LSCAPE_R8G8B8 => "CUSTOM_LSCAPE_R8G8B8",
            Self::CUSTOM_LSCAPE_ALPHA => "CUSTOM_LSCAPE_ALPHA",
            Self::CUSTOM_RAW_JPEG => "CUSTOM_RAW_JPEG",
            Self::DXT1 => "DXT1",
            Self::DXT3 => "DXT3",
            Self::DXT5 => "DXT5",
            _ => "other",
        }
    }
}

/// A texture image: size, pixel format and the stored pixels. File ids
/// `0x06000000..=0x07FFFFFF`, in the portal and high-resolution dats.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RenderSurface {
    /// File id.
    pub id: u32,
    /// Data category.
    pub data_category: u32,
    /// Width in pixels (0 for JPEG, whose file holds its own size).
    pub width: u32,
    /// Height in pixels (0 for JPEG).
    pub height: u32,
    /// Pixel format.
    pub format: PixelFormat,
    /// The stored pixels.
    pub data: Vec<u8>,
    /// Palette for the paletted formats; 0 otherwise.
    pub default_palette_id: u32,
}

/// A render surface's fields, with where its pixels lie in the file bytes.
struct Header {
    id: u32,
    data_category: u32,
    width: u32,
    height: u32,
    format: PixelFormat,
    data: core::ops::Range<usize>,
    default_palette_id: u32,
}

impl Header {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let data_category = r.u32()?;
        let width = r.u32()?;
        let height = r.u32()?;
        let format = PixelFormat(r.u32()?);
        let len = r.i32()?;
        let len = usize::try_from(len).map_err(|_| DecodeError::NegativeLength(len))?;
        let at = r.position();
        r.bytes(len)?;
        let mut default_palette_id = 0;
        if format.is_paletted() {
            r.align(4)?;
            default_palette_id = r.u32()?;
        }
        Ok(Self {
            id,
            data_category,
            width,
            height,
            format,
            data: at..at + len,
            default_palette_id,
        })
    }

    fn with_data(self, data: Vec<u8>) -> RenderSurface {
        RenderSurface {
            id: self.id,
            data_category: self.data_category,
            width: self.width,
            height: self.height,
            format: self.format,
            data,
            default_palette_id: self.default_palette_id,
        }
    }
}

impl DatObject for RenderSurface {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let start = r.position();
        let all = r.peek_rest();
        let header = Header::unpack(r)?;
        let data = all[header.data.start - start..header.data.end - start].to_vec();
        Ok(header.with_data(data))
    }

    /// The pixels stay in the file's own buffer: the header before them and
    /// the palette id after them are cut away, without a copy of the pixels
    /// into a new buffer.
    fn unpack_owned(mut bytes: Vec<u8>) -> Result<Self> {
        let header = Header::unpack(&mut Reader::new(&bytes))?;
        bytes.truncate(header.data.end);
        bytes.drain(..header.data.start);
        Ok(header.with_data(bytes))
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    fn sample(format: PixelFormat, data: &[u8]) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0600_0001);
        w.u32(0);
        w.u32(2);
        w.u32(1);
        w.u32(format.0);
        w.i32(i32::try_from(data.len()).unwrap());
        w.bytes(data);
        if format.is_paletted() {
            w.align(4);
            w.u32(0x0400_0001);
        }
        w.into_vec()
    }

    #[test]
    fn paletted_surface_has_a_palette_id() {
        let bytes = sample(PixelFormat::INDEX16, &[1, 0, 2, 0]);
        let mut r = Reader::new(&bytes);
        let s = RenderSurface::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!((s.width, s.height, s.format), (2, 1, PixelFormat::INDEX16));
        assert_eq!(s.data, vec![1, 0, 2, 0]);
        assert_eq!(s.default_palette_id, 0x0400_0001);
        for len in 0..bytes.len() {
            assert!(RenderSurface::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn palette_id_is_read_at_a_four_byte_boundary() {
        let bytes = sample(PixelFormat::P8, &[1, 2]);
        let mut r = Reader::new(&bytes);
        let s = RenderSurface::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.default_palette_id, 0x0400_0001);
    }

    #[test]
    fn other_formats_have_none() {
        let bytes = sample(PixelFormat::R8G8B8, &[1, 2, 3, 4, 5, 6]);
        let mut r = Reader::new(&bytes);
        let s = RenderSurface::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.default_palette_id, 0);
    }

    /// Taking the file's bytes decodes exactly as reading them does, and
    /// fails where reading fails.
    #[test]
    fn taking_the_bytes_decodes_as_reading_them() {
        for (format, data) in [
            (PixelFormat::INDEX16, &[1u8, 0, 2, 0][..]),
            (PixelFormat::P8, &[1, 2]),
            (PixelFormat::R8G8B8, &[1, 2, 3, 4, 5, 6]),
            (PixelFormat::DXT1, &[9; 8]),
            (PixelFormat::A8, &[]),
        ] {
            let bytes = sample(format, data);
            let read = RenderSurface::unpack(&mut Reader::new(&bytes)).unwrap();
            let taken = RenderSurface::unpack_owned(bytes.clone()).unwrap();
            assert_eq!(taken, read);
            assert_eq!(taken.data, data);
            for len in 0..bytes.len() {
                assert_eq!(
                    RenderSurface::unpack_owned(bytes[..len].to_vec()),
                    RenderSurface::unpack(&mut Reader::new(&bytes[..len])),
                );
            }
        }
    }

    #[test]
    fn negative_length_is_an_error() {
        let mut bytes = sample(PixelFormat::A8, &[]);
        bytes[20..24].copy_from_slice(&(-1i32).to_le_bytes());
        assert_eq!(
            RenderSurface::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::NegativeLength(-1))
        );
    }
}
