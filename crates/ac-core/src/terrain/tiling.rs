//! How many times each terrain texture repeats across one 24 m cell.
//!
//! Each terrain texture in the region carries a repeat count; the shader
//! looks it up by texture-array layer. Base, overlay and road textures
//! sample at `cell_uv * repeat[layer]`; alpha maps stay at the unscaled
//! cell UV.

use std::fmt;

/// Texture-array layers the terrain shader can address.
pub const TERRAIN_LAYER_CAPACITY: usize = 36;

/// Bytes per element of the shader's repeat array: std140 pads each
/// `float` array element to 16 bytes.
pub const TILING_UNIFORM_ELEMENT_STRIDE_BYTES: usize = 16;

/// Size of the repeat-table uniform buffer.
pub const TILING_UNIFORM_BUFFER_BYTES: usize =
    TERRAIN_LAYER_CAPACITY * TILING_UNIFORM_ELEMENT_STRIDE_BYTES;

/// A terrain texture layer the shader cannot address.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TilingLayerOutOfRange {
    /// The offending layer.
    pub layer: u32,
}

impl fmt::Display for TilingLayerOutOfRange {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "terrain texture layer {} exceeds the shader capacity of {TERRAIN_LAYER_CAPACITY} layers",
            self.layer
        )
    }
}

impl std::error::Error for TilingLayerOutOfRange {}

/// The repeat count per layer from `(layer, repeat_count)` pairs; layers
/// not listed repeat once. The dat value is kept exactly, even 0.
///
/// # Errors
///
/// A layer at or beyond [`TERRAIN_LAYER_CAPACITY`].
pub fn build_tiling_table(
    entries: impl IntoIterator<Item = (u32, u32)>,
) -> Result<[f32; TERRAIN_LAYER_CAPACITY], TilingLayerOutOfRange> {
    let mut table = [1.0; TERRAIN_LAYER_CAPACITY];
    for (layer, repeat_count) in entries {
        let slot = table
            .get_mut(layer as usize)
            .ok_or(TilingLayerOutOfRange { layer })?;
        *slot = repeat_count as f32;
    }
    Ok(table)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values below: OpenAC
    // tests/AcDream.App.Tests/Rendering/TerrainTextureTilingTableTests.cs.

    #[test]
    fn maps_repeat_counts_to_layers() {
        let t = build_tiling_table([(0, 4), (7, 2), (32, 8)]).unwrap();
        assert_eq!(t.len(), TERRAIN_LAYER_CAPACITY);
        assert_eq!((t[0], t[7], t[32]), (4.0, 2.0, 8.0));
    }

    #[test]
    fn only_unlisted_layers_default_to_one() {
        let t = build_tiling_table([(3, 0)]).unwrap();
        assert_eq!((t[2], t[3], t[4]), (1.0, 0.0, 1.0));
    }

    #[test]
    fn rejects_layers_the_shader_cannot_address() {
        let err = build_tiling_table([(TERRAIN_LAYER_CAPACITY as u32, 1)]).unwrap_err();
        assert!(err.to_string().contains("exceeds the shader capacity"));
    }

    #[test]
    fn uniform_buffer_is_std140_padded() {
        assert_eq!(TILING_UNIFORM_ELEMENT_STRIDE_BYTES, 16);
        assert_eq!(TILING_UNIFORM_BUFFER_BYTES, 576);
        assert_ne!(TILING_UNIFORM_ELEMENT_STRIDE_BYTES, size_of::<f32>());
    }
}
