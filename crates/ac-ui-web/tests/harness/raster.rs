//! A small software rasterizer that draws the module's picture as the page
//! contract says a page draws it: triangles in canvas pixels, colour =
//! texel × vertex colour, straight alpha blended `SRC_ALPHA,
//! ONE_MINUS_SRC_ALPHA` (alpha `ONE, ONE_MINUS_SRC_ALPHA`), texture
//! coordinates repeating on both axes, nearest or bilinear sampling of the
//! first level, the 1 × 1 white texture for kind 0, and a run whose texture
//! was never uploaded skipped. Drawn over an opaque dark fill.

use std::collections::HashMap;

use super::Held;

/// The fill under the picture.
pub const BACKGROUND: [f32; 4] = [0.10, 0.12, 0.14, 1.0];

/// One corner: position, texture coordinate, colour.
#[derive(Debug, Clone, Copy)]
pub struct Corner {
    pub position: [f32; 2],
    pub uv: [f32; 2],
    pub color: [f32; 4],
}

/// The corners of a picture's vertex bytes (32 bytes each).
#[must_use]
pub fn vertices(bytes: &[u8]) -> Vec<Corner> {
    bytes
        .chunks_exact(32)
        .map(|c| {
            let f = |i: usize| f32::from_le_bytes([c[i], c[i + 1], c[i + 2], c[i + 3]]);
            Corner {
                position: [f(0), f(4)],
                uv: [f(8), f(12)],
                color: [f(16), f(20), f(24), f(28)],
            }
        })
        .collect()
}

/// A texture as a sampler sees it.
struct Sampler<'a> {
    width: u32,
    height: u32,
    rgba: &'a [u8],
    nearest: bool,
}

const WHITE: [u8; 4] = [255; 4];

impl Sampler<'_> {
    fn texel(&self, x: i64, y: i64) -> [f32; 4] {
        let (w, h) = (i64::from(self.width), i64::from(self.height));
        let (x, y) = (x.rem_euclid(w) as usize, y.rem_euclid(h) as usize);
        let at = (y * self.width as usize + x) * 4;
        let t = &self.rgba[at..at + 4];
        [
            f32::from(t[0]) / 255.0,
            f32::from(t[1]) / 255.0,
            f32::from(t[2]) / 255.0,
            f32::from(t[3]) / 255.0,
        ]
    }

    fn sample(&self, u: f32, v: f32) -> [f32; 4] {
        let (w, h) = (self.width as f32, self.height as f32);
        if self.nearest {
            return self.texel((u * w).floor() as i64, (v * h).floor() as i64);
        }
        // Bilinear between the four texel centres around the point.
        let (x, y) = (u * w - 0.5, v * h - 0.5);
        let (x0, y0) = (x.floor(), y.floor());
        let (fx, fy) = (x - x0, y - y0);
        let (x0, y0) = (x0 as i64, y0 as i64);
        let a = self.texel(x0, y0);
        let b = self.texel(x0 + 1, y0);
        let c = self.texel(x0, y0 + 1);
        let d = self.texel(x0 + 1, y0 + 1);
        let mut out = [0.0; 4];
        for i in 0..4 {
            let top = a[i] + (b[i] - a[i]) * fx;
            let bottom = c[i] + (d[i] - c[i]) * fx;
            out[i] = top + (bottom - top) * fy;
        }
        out
    }
}

/// Twice the signed area of (a, b, p).
fn edge(a: [f32; 2], b: [f32; 2], p: [f32; 2]) -> f32 {
    (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
}

/// Whether a pixel centre exactly on the edge from `a` to `b` is drawn.
/// Both triangles that share an edge are wound the same way, so they run
/// along it in opposite directions, and exactly one of them owns it: no
/// pixel of a seam is drawn twice or left out.
fn owns(a: [f32; 2], b: [f32; 2]) -> bool {
    let (dx, dy) = (b[0] - a[0], b[1] - a[1]);
    dy > 0.0 || (dy == 0.0 && dx < 0.0)
}

/// Draw `runs` of `corners` over the fill: straight RGBA rows,
/// `width` × `height`.
#[must_use]
pub fn render(
    width: u32,
    height: u32,
    corners: &[Corner],
    runs: &[[u32; 5]],
    textures: &HashMap<(u32, u32), Held>,
) -> Vec<u8> {
    let mut frame = vec![BACKGROUND; (width * height) as usize];
    for &[texture_kind, id, filter, first, count] in runs {
        let sampler = if texture_kind == 0 {
            Sampler {
                width: 1,
                height: 1,
                rgba: &WHITE,
                nearest: true,
            }
        } else {
            let Some(held) = textures.get(&(texture_kind, id)) else {
                continue;
            };
            Sampler {
                width: held.width,
                height: held.height,
                rgba: &held.rgba,
                nearest: filter == 1,
            }
        };
        let end = (first + count) as usize;
        for triangle in corners[first as usize..end.min(corners.len())].chunks_exact(3) {
            fill(&mut frame, width, height, triangle, &sampler);
        }
    }
    frame
        .iter()
        .flat_map(|p| p.map(|c| (c.clamp(0.0, 1.0) * 255.0).round() as u8))
        .collect()
}

fn fill(frame: &mut [[f32; 4]], width: u32, height: u32, t: &[Corner], sampler: &Sampler<'_>) {
    let (mut a, mut b, c) = (t[0], t[1], t[2]);
    let mut area = edge(a.position, b.position, c.position);
    if area == 0.0 || !area.is_finite() {
        return;
    }
    if area < 0.0 {
        std::mem::swap(&mut a, &mut b);
        area = -area;
    }
    let xs = [a.position[0], b.position[0], c.position[0]];
    let ys = [a.position[1], b.position[1], c.position[1]];
    let min = |v: [f32; 3]| v[0].min(v[1]).min(v[2]);
    let max = |v: [f32; 3]| v[0].max(v[1]).max(v[2]);
    let x0 = (min(xs).floor().max(0.0)) as u32;
    let y0 = (min(ys).floor().max(0.0)) as u32;
    let x1 = (max(xs).ceil().min(width as f32)) as u32;
    let y1 = (max(ys).ceil().min(height as f32)) as u32;
    let owned = [
        owns(b.position, c.position),
        owns(c.position, a.position),
        owns(a.position, b.position),
    ];
    for y in y0..y1 {
        for x in x0..x1 {
            let p = [x as f32 + 0.5, y as f32 + 0.5];
            let w = [
                edge(b.position, c.position, p),
                edge(c.position, a.position, p),
                edge(a.position, b.position, p),
            ];
            let inside = w
                .iter()
                .zip(owned)
                .all(|(&w, owned)| w > 0.0 || (w == 0.0 && owned));
            if !inside {
                continue;
            }
            let (l0, l1, l2) = (w[0] / area, w[1] / area, w[2] / area);
            let mix = |f: fn(&Corner) -> f32| l0 * f(&a) + l1 * f(&b) + l2 * f(&c);
            let u = mix(|c| c.uv[0]);
            let v = mix(|c| c.uv[1]);
            let texel = sampler.sample(u, v);
            let src = [
                texel[0] * mix(|c| c.color[0]),
                texel[1] * mix(|c| c.color[1]),
                texel[2] * mix(|c| c.color[2]),
                texel[3] * mix(|c| c.color[3]),
            ];
            let dst = &mut frame[(y * width + x) as usize];
            let alpha = src[3].clamp(0.0, 1.0);
            for i in 0..3 {
                dst[i] = src[i] * alpha + dst[i] * (1.0 - alpha);
            }
            dst[3] = alpha + dst[3] * (1.0 - alpha);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn corner(x: f32, y: f32) -> Corner {
        Corner {
            position: [x, y],
            uv: [0.0, 0.0],
            color: [1.0, 0.0, 0.0, 1.0],
        }
    }

    #[test]
    fn two_triangles_of_a_square_cover_it_once() {
        let quad = [
            corner(1.0, 1.0),
            corner(3.0, 1.0),
            corner(3.0, 3.0),
            corner(1.0, 1.0),
            corner(3.0, 3.0),
            corner(1.0, 3.0),
        ];
        let rgba = render(4, 4, &quad, &[[0, 0, 0, 0, 6]], &HashMap::new());
        let red: Vec<usize> = (0..16).filter(|&i| rgba[i * 4] == 255).collect();
        assert_eq!(red, [5, 6, 9, 10]);
    }
}
