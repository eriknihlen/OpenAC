//! The smaller copies of an RGBA image a GPU samples when the image is
//! drawn much smaller than it is (mipmaps), so it shrinks smoothly instead
//! of breaking up. Built here on the CPU rather than by the GPU because the
//! interface blends with plain (not premultiplied) alpha: each level
//! averages colour weighted by alpha, so the colour of clear pixels never
//! bleeds into the edge of opaque ones.

use crate::images::DecodedImage;

/// Every level from the image itself down to 1 x 1, each half the size of
/// the one above (rounded down, at least 1), the way the GPU sizes them.
/// The first level is `rgba` itself, not a copy. Pixels that do not fill
/// the size give the image alone.
#[must_use]
pub fn build(rgba: Vec<u8>, width: u32, height: u32) -> Vec<DecodedImage> {
    let fills =
        width > 0 && height > 0 && rgba.len() as u64 >= u64::from(width) * u64::from(height) * 4;
    let mut levels = vec![DecodedImage {
        width,
        height,
        rgba,
    }];
    if !fills {
        return levels;
    }
    let (mut width, mut height) = (width as usize, height as usize);
    while width > 1 || height > 1 {
        let (next_width, next_height) = ((width / 2).max(1), (height / 2).max(1));
        let source = &levels[levels.len() - 1].rgba;
        let next = halve(source, width, height, next_width, next_height);
        (width, height) = (next_width, next_height);
        levels.push(DecodedImage {
            width: width as u32,
            height: height as u32,
            rgba: next,
        });
    }
    levels
}

fn halve(
    source: &[u8],
    width: usize,
    height: usize,
    next_width: usize,
    next_height: usize,
) -> Vec<u8> {
    let mut next = vec![0u8; next_width * next_height * 4];
    for y in 0..next_height {
        // The last row (and column) of the level below also takes in the
        // odd row the halving would otherwise drop.
        let top = y * 2;
        let bottom = if y == next_height - 1 {
            height - 1
        } else {
            (y * 2 + 1).min(height - 1)
        };
        for x in 0..next_width {
            let left = x * 2;
            let right = if x == next_width - 1 {
                width - 1
            } else {
                (x * 2 + 1).min(width - 1)
            };
            let (mut alpha, mut red, mut green, mut blue) = (0u64, 0u64, 0u64, 0u64);
            let (mut plain_red, mut plain_green, mut plain_blue) = (0u64, 0u64, 0u64);
            let mut count = 0u64;
            for sy in top..=bottom {
                for sx in left..=right {
                    let i = (sy * width + sx) * 4;
                    let a = u64::from(source[i + 3]);
                    alpha += a;
                    red += u64::from(source[i]) * a;
                    green += u64::from(source[i + 1]) * a;
                    blue += u64::from(source[i + 2]) * a;
                    plain_red += u64::from(source[i]);
                    plain_green += u64::from(source[i + 1]);
                    plain_blue += u64::from(source[i + 2]);
                    count += 1;
                }
            }
            let o = (y * next_width + x) * 4;
            if alpha > 0 {
                next[o] = average(red, alpha);
                next[o + 1] = average(green, alpha);
                next[o + 2] = average(blue, alpha);
            } else {
                next[o] = average(plain_red, count);
                next[o + 1] = average(plain_green, count);
                next[o + 2] = average(plain_blue, count);
            }
            next[o + 3] = average(alpha, count);
        }
    }
    next
}

/// `sum / count` rounded to the nearest byte, halves away from zero.
fn average(sum: u64, count: u64) -> u8 {
    (sum as f64 / count as f64).round() as u8
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Textures/RgbaMipChainTests.cs`.

    use super::*;

    fn solid(width: u32, height: u32, rgba: [u8; 4]) -> Vec<u8> {
        rgba.repeat((width * height) as usize)
    }

    // LevelsHalveDownToOnePixelTheWayTheGpuSizesThem.
    #[test]
    fn levels_halve_down_to_one_pixel_the_way_the_gpu_sizes_them() {
        for (width, height, extents) in [
            (1, 1, vec![1, 1]),
            (8, 8, vec![8, 8, 4, 4, 2, 2, 1, 1]),
            (5, 2, vec![5, 2, 2, 1, 1, 1]),
            (1, 4, vec![1, 4, 1, 2, 1, 1]),
        ] {
            let levels = build(solid(width, height, [1, 2, 3, 4]), width, height);
            let actual: Vec<u32> = levels.iter().flat_map(|l| [l.width, l.height]).collect();
            assert_eq!(actual, extents, "{width}x{height}");
            for level in &levels {
                assert_eq!(level.rgba.len() as u32, level.width * level.height * 4);
            }
        }
    }

    // TheFirstLevelIsTheImageItself.
    #[test]
    fn the_first_level_is_the_image_itself() {
        let rgba = solid(4, 4, [10, 20, 30, 40]);
        let at = rgba.as_ptr();
        assert_eq!(build(rgba, 4, 4)[0].rgba.as_ptr(), at);
    }

    // ASolidImageStaysTheSameColourAtEveryLevel.
    #[test]
    fn a_solid_image_stays_the_same_colour_at_every_level() {
        for level in build(solid(16, 8, [200, 100, 50, 255]), 16, 8) {
            assert_eq!(
                level.rgba,
                solid(level.width, level.height, [200, 100, 50, 255])
            );
        }
    }

    // ClearPixelsDoNotDarkenTheEdgeOfOpaqueOnes: white opaque on the left,
    // clear black on the right; a plain average would turn the edge grey.
    #[test]
    fn clear_pixels_do_not_darken_the_edge_of_opaque_ones() {
        let rgba = vec![
            255, 255, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0,
        ];
        assert_eq!(build(rgba, 2, 2)[1].rgba, vec![255, 255, 255, 128]);
    }

    // AFullyClearBlockStaysClear.
    #[test]
    fn a_fully_clear_block_stays_clear() {
        assert_eq!(build(solid(2, 2, [90, 90, 90, 0]), 2, 2)[1].rgba[3], 0);
    }

    // AnOddEdgeIsFoldedIntoTheLevelBelow: 3 x 1 red, red, blue; the single
    // pixel below covers all three, so blue shows rather than being dropped.
    #[test]
    fn an_odd_edge_is_folded_into_the_level_below() {
        let rgba = vec![255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255];
        let next = &build(rgba, 3, 1)[1];
        assert_eq!(next.width, 1);
        assert_eq!(next.rgba, vec![170, 0, 85, 255]);
    }
}
