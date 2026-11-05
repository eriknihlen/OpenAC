use ac_bits::Reader;

use super::DatObject;
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::ColorArgb;

type Result<T> = core::result::Result<T, DecodeError>;

/// Surface type bits: how a surface is coloured and blended.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SurfaceType(pub u32);

impl SurfaceType {
    /// A solid colour.
    pub const BASE1_SOLID: u32 = 0x0000_0001;
    /// A texture.
    pub const BASE1_IMAGE: u32 = 0x0000_0002;
    /// A texture whose darkest palette entries (or black pixels) are holes.
    pub const BASE1_CLIP_MAP: u32 = 0x0000_0004;
    /// Translucent.
    pub const TRANSLUCENT: u32 = 0x0000_0010;
    /// Lit diffusely.
    pub const DIFFUSE: u32 = 0x0000_0020;
    /// Self-lit.
    pub const LUMINOUS: u32 = 0x0000_0040;
    /// Alpha blended.
    pub const ALPHA: u32 = 0x0000_0100;
    /// Inverse-alpha blended.
    pub const INV_ALPHA: u32 = 0x0000_0200;
    /// Additively blended.
    pub const ADDITIVE: u32 = 0x0001_0000;
    /// A detail texture.
    pub const DETAIL: u32 = 0x0002_0000;
    /// Gouraud shaded.
    pub const GOURAUD: u32 = 0x1000_0000;
    /// Stippled.
    pub const STIPPLED: u32 = 0x4000_0000;
    /// Perspective-correct.
    pub const PERSPECTIVE: u32 = 0x8000_0000;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }

    /// True when the surface stores a texture rather than a colour (either
    /// image bit is set).
    #[must_use]
    pub fn has_texture(self) -> bool {
        self.0 & (Self::BASE1_IMAGE | Self::BASE1_CLIP_MAP) != 0
    }
}

/// What a surface is painted with.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SurfaceBase {
    /// A texture and the palette it was authored with.
    Texture {
        /// Surface texture id (`0x05......`).
        texture: u32,
        /// Palette id (`0x04......`), 0 for none.
        palette: u32,
    },
    /// A solid colour.
    Solid(ColorArgb),
}

/// How a polygon face is painted. File id `0x08......`; the file stores no
/// id of its own.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Surface {
    /// File id.
    pub id: u32,
    /// Type bits.
    pub surface_type: SurfaceType,
    /// Texture or colour.
    pub base: SurfaceBase,
    /// Translucency, 0 (opaque) to 1 (invisible).
    pub translucency: f32,
    /// Self-lighting, 0 to 1.
    pub luminosity: f32,
    /// Diffuse lighting factor.
    pub diffuse: f32,
}

impl DatObject for Surface {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let surface_type = SurfaceType(r.u32()?);
        let base = if surface_type.has_texture() {
            SurfaceBase::Texture {
                texture: r.u32()?,
                palette: r.u32()?,
            }
        } else {
            SurfaceBase::Solid(ColorArgb::unpack(r)?)
        };
        Ok(Self {
            id: 0,
            surface_type,
            base,
            translucency: r.f32()?,
            luminosity: r.f32()?,
            diffuse: r.f32()?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// Texture kind of a [`SurfaceTexture`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TextureType(pub u8);

impl TextureType {
    /// Undefined.
    pub const UNDEFINED: u8 = 1;
    /// A 2D texture.
    pub const TEXTURE_2D: u8 = 2;
    /// A 3D texture.
    pub const TEXTURE_3D: u8 = 3;
    /// A cube map.
    pub const CUBE: u8 = 4;
    /// A 2D movie.
    pub const MOVIE_2D: u8 = 5;
}

/// A texture as a list of render surfaces (the first is the full-size
/// image). File id `0x05......`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SurfaceTexture {
    /// File id.
    pub id: u32,
    /// Data category.
    pub data_category: u32,
    /// Texture kind.
    pub texture_type: TextureType,
    /// Render surface ids (`0x06......`).
    pub textures: Vec<u32>,
}

impl DatObject for SurfaceTexture {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let data_category = r.u32()?;
        let texture_type = TextureType(r.u8()?);
        let count = r.i32()?;
        let mut textures = Vec::new();
        for _ in 0..count {
            textures.push(r.u32()?);
        }
        Ok(Self {
            id,
            data_category,
            texture_type,
            textures,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_bits::Writer;

    #[test]
    fn textured_surface() {
        let mut w = Writer::new();
        w.u32(SurfaceType::BASE1_CLIP_MAP | SurfaceType::TRANSLUCENT);
        w.u32(0x0500_1234);
        w.u32(0);
        for v in [0.5f32, 0.25, 1.0] {
            w.f32(v);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let s = Surface::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            s.base,
            SurfaceBase::Texture {
                texture: 0x0500_1234,
                palette: 0
            }
        );
        assert_eq!((s.translucency, s.luminosity, s.diffuse), (0.5, 0.25, 1.0));
        for len in 0..bytes.len() {
            assert!(Surface::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn solid_surface() {
        let mut w = Writer::new();
        w.u32(SurfaceType::BASE1_SOLID);
        w.bytes(&[1, 2, 3, 4]);
        for v in [0.0f32, 0.0, 1.0] {
            w.f32(v);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let s = Surface::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            s.base,
            SurfaceBase::Solid(ColorArgb {
                blue: 1,
                green: 2,
                red: 3,
                alpha: 4
            })
        );
    }

    #[test]
    fn surface_texture() {
        let mut w = Writer::new();
        w.u32(0x0500_0001);
        w.u32(0);
        w.u8(TextureType::TEXTURE_2D);
        w.i32(2);
        w.u32(0x0600_0001);
        w.u32(0x0600_0002);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let t = SurfaceTexture::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.texture_type, TextureType(2));
        assert_eq!(t.textures, vec![0x0600_0001, 0x0600_0002]);
        for len in 0..bytes.len() {
            assert!(SurfaceTexture::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }
}
