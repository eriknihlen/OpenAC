//! A UI draw list turned into triangles: one vertex stream and the runs of
//! it that share a texture, in paint order. No GPU here, so the rules are
//! tested on their own.
//!
//! The rules are OpenAC's `TextRenderer` (the UI's sprite batcher):
//! - every command is two triangles per rectangle, or a fan from the first
//!   corner for a polygon, in screen pixels with the texture coordinates
//!   as given (past 1 they repeat: the samplers wrap);
//! - consecutive commands on the same texture share one run, so the order
//!   of painting is kept and a texture change starts a new run;
//! - the main layer is painted before the overlay (popups, tooltips, the
//!   drag ghost);
//! - with a fixed canvas every position is multiplied by the canvas scale,
//!   and the glyph atlases, sampled nearest at 1 : 1, are sampled linear
//!   (OpenAC's "linear twin" of a nearest texture);
//! - drawn at more or fewer pixels than one a unit (the UI scale), every
//!   position is multiplied by it and put on the nearest whole pixel, each
//!   edge on its own, so pieces that meet in units meet in pixels (no seams
//!   or blurred borders at 110%) and nothing that has size loses it;
//!   nearest textures stay nearest at a whole scale (200%: each texel two
//!   pixels) and are sampled linear at a fractional one; coloured triangles
//!   are multiplied but not snapped, because their soft edges are already
//!   one pixel wide and a snap would fold them;
//! - a solid fill, an untextured polygon or coloured triangles sample a
//!   1 x 1 white texture, so everything goes through one pipeline;
//! - a render surface id of 0 names nothing and draws nothing (never a
//!   placeholder: a 0 resolved to the magenta texture is the 1-pixel
//!   magenta edge bug).

use ac_ui::draw::{Color, ColorVertex, DrawCmd, DrawList, Filter, Quad, TextureRef, Vertex};

/// One corner as the GPU reads it: screen pixels, texture coordinate,
/// colour (straight alpha).
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct UiVertex {
    /// Screen x, y in pixels (origin top-left).
    pub position: [f32; 2],
    /// Texture u, v.
    pub uv: [f32; 2],
    /// Tint, multiplied into the texel.
    pub color: [f32; 4],
}

/// What a run samples.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum UiTexture {
    /// The 1 x 1 white texture (fills, untextured polygons, coloured
    /// triangles).
    White,
    /// A texture the draw list named.
    Named(TextureRef),
}

/// A texture and how it is sampled.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct UiTextureKey {
    /// The texture.
    pub texture: UiTexture,
    /// Nearest or linear (both repeat).
    pub filter: Filter,
}

/// A run of vertices drawn with one texture.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UiRun {
    /// What it samples.
    pub key: UiTextureKey,
    /// First vertex.
    pub first: u32,
    /// Vertex count (a multiple of 3).
    pub count: u32,
}

/// A frame of UI triangles.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct UiBatch {
    /// Every vertex, in paint order.
    pub vertices: Vec<UiVertex>,
    /// The runs over them, in paint order.
    pub runs: Vec<UiRun>,
    /// Commands that named render surface 0 and were dropped.
    pub skipped_zero_ids: u32,
}

impl UiBatch {
    /// Turn `list` into triangles, one UI unit a pixel.
    #[must_use]
    pub fn build(list: &DrawList) -> Self {
        Self::build_scaled(list, 1.0)
    }

    /// Turn `list` into triangles in framebuffer pixels, the UI drawn at
    /// `pixels_per_unit` pixels a unit (see the module documentation).
    #[must_use]
    pub fn build_scaled(list: &DrawList, pixels_per_unit: f32) -> Self {
        let mut batch = Self::default();
        let (sx, sy) = list.canvas_scale;
        let canvas = if sx.is_finite() && sy.is_finite() && sx > 0.0 && sy > 0.0 {
            (sx, sy)
        } else {
            (1.0, 1.0)
        };
        let ppu = if pixels_per_unit.is_finite() && pixels_per_unit > 0.0 {
            pixels_per_unit
        } else {
            1.0
        };
        let grid = Grid {
            scale: (canvas.0 * ppu, canvas.1 * ppu),
            snap: ppu != 1.0,
        };
        let linear = canvas != (1.0, 1.0) || ppu.fract() != 0.0;
        for cmd in list.main.iter().chain(list.overlay.iter()) {
            batch.push(cmd, grid, linear);
        }
        batch
    }

    /// True when nothing is drawn.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.vertices.is_empty()
    }

    fn push(&mut self, cmd: &DrawCmd, grid: Grid, linear: bool) {
        match cmd {
            DrawCmd::Fill { x, y, w, h, color } => {
                let quad = Quad {
                    x: *x,
                    y: *y,
                    w: *w,
                    h: *h,
                    ..Quad::default()
                };
                self.quad(white(), quad, *color, grid);
            }
            DrawCmd::Sprite {
                texture,
                filter,
                quad,
                tint,
            } => {
                let Some(texture) = named(*texture) else {
                    self.skipped_zero_ids += 1;
                    return;
                };
                let key = UiTextureKey {
                    texture,
                    filter: sampled(*filter, linear),
                };
                self.quad(key, *quad, *tint, grid);
            }
            DrawCmd::Polygon {
                texture,
                vertices,
                color,
            } => {
                let key = match texture {
                    None => white(),
                    Some(t) => match named(*t) {
                        Some(texture) => UiTextureKey {
                            texture,
                            filter: Filter::Linear,
                        },
                        None => {
                            self.skipped_zero_ids += 1;
                            return;
                        }
                    },
                };
                self.fan(key, vertices, *color, grid);
            }
            DrawCmd::Triangles { vertices } => self.triangles(vertices, grid.scale),
            DrawCmd::Glyphs {
                texture,
                tint,
                quads,
            } => {
                let Some(texture) = named(*texture) else {
                    self.skipped_zero_ids += 1;
                    return;
                };
                let key = UiTextureKey {
                    texture,
                    filter: sampled(Filter::Nearest, linear),
                };
                for quad in quads {
                    self.quad(key, *quad, *tint, grid);
                }
            }
        }
    }

    /// The run `key` continues, or a new one.
    fn run(&mut self, key: UiTextureKey, added: u32) {
        let first = u32::try_from(self.vertices.len()).unwrap_or(u32::MAX) - added;
        match self.runs.last_mut() {
            Some(run) if run.key == key => run.count += added,
            _ => self.runs.push(UiRun {
                key,
                first,
                count: added,
            }),
        }
    }

    fn quad(&mut self, key: UiTextureKey, q: Quad, color: Color, grid: Grid) {
        if q.w <= 0.0 || q.h <= 0.0 {
            return;
        }
        let (x0, x1) = grid.span(q.x, q.x + q.w, grid.scale.0);
        let (y0, y1) = grid.span(q.y, q.y + q.h, grid.scale.1);
        let c = [color.r, color.g, color.b, color.a];
        let v = |x: f32, y: f32, u: f32, v: f32| UiVertex {
            position: [x, y],
            uv: [u, v],
            color: c,
        };
        // OpenAC's winding: (tl, br, tr), (tl, bl, br).
        self.vertices.extend_from_slice(&[
            v(x0, y0, q.u0, q.v0),
            v(x1, y1, q.u1, q.v1),
            v(x1, y0, q.u1, q.v0),
            v(x0, y0, q.u0, q.v0),
            v(x0, y1, q.u0, q.v1),
            v(x1, y1, q.u1, q.v1),
        ]);
        self.run(key, 6);
    }

    fn fan(&mut self, key: UiTextureKey, corners: &[Vertex], color: Color, grid: Grid) {
        if corners.len() < 3 {
            return;
        }
        let c = [color.r, color.g, color.b, color.a];
        let v = |p: &Vertex| UiVertex {
            position: [grid.point(p.x, grid.scale.0), grid.point(p.y, grid.scale.1)],
            uv: [p.u, p.v],
            color: c,
        };
        let mut added = 0;
        for i in 1..corners.len() - 1 {
            self.vertices
                .extend_from_slice(&[v(&corners[0]), v(&corners[i]), v(&corners[i + 1])]);
            added += 3;
        }
        self.run(key, added);
    }

    fn triangles(&mut self, corners: &[ColorVertex], (sx, sy): (f32, f32)) {
        let whole = corners.len() - corners.len() % 3;
        if whole == 0 {
            return;
        }
        self.vertices
            .extend(corners[..whole].iter().map(|p| UiVertex {
                position: [p.x * sx, p.y * sy],
                uv: [0.0, 0.0],
                color: [p.color.r, p.color.g, p.color.b, p.color.a],
            }));
        self.run(white(), u32::try_from(whole).unwrap_or(u32::MAX));
    }
}

fn white() -> UiTextureKey {
    UiTextureKey {
        texture: UiTexture::White,
        filter: Filter::Nearest,
    }
}

/// A named texture, or `None` for surface 0 (nothing).
fn named(texture: TextureRef) -> Option<UiTexture> {
    match texture {
        TextureRef::Surface(0) => None,
        other => Some(UiTexture::Named(other)),
    }
}

/// Nearest sampling becomes linear when the canvas is scaled or the UI is
/// drawn at a fractional scale.
fn sampled(filter: Filter, linear: bool) -> Filter {
    if linear { Filter::Linear } else { filter }
}

/// How a UI coordinate becomes a framebuffer coordinate: multiplied by the
/// canvas scale times the pixels a unit, then, when the UI is scaled, put
/// on the nearest whole pixel.
#[derive(Debug, Clone, Copy, PartialEq)]
struct Grid {
    /// Framebuffer pixels per drawn coordinate, along x and y.
    scale: (f32, f32),
    /// Snap to whole pixels.
    snap: bool,
}

impl Grid {
    fn point(self, v: f32, scale: f32) -> f32 {
        let p = v * scale;
        if self.snap { p.round() } else { p }
    }

    /// The edges `lo` and `hi` (lo below hi) in pixels, each snapped on its
    /// own so that edges shared in units stay shared; a span that would
    /// snap to nothing keeps the one pixel its middle falls in.
    fn span(self, lo: f32, hi: f32, scale: f32) -> (f32, f32) {
        let (a, b) = (self.point(lo, scale), self.point(hi, scale));
        if self.snap && b <= a {
            let pixel = ((lo + hi) * 0.5 * scale).floor();
            (pixel, pixel + 1.0)
        } else {
            (a, b)
        }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Rendering/TextRendererLinearTwinTests.cs`
    //! (the linear twin only when the canvas is scaled, identity for other
    //! textures) and `TextRenderer.NextSpriteSeg` / `DrawConvexPolygon`
    //! (runs per texture in order, fans), plus the zero-id guard (a zero
    //! texture id must never resolve to the magenta placeholder).
    use super::*;

    fn sprite(id: u32, filter: Filter, x: f32) -> DrawCmd {
        DrawCmd::Sprite {
            texture: TextureRef::Surface(id),
            filter,
            quad: Quad {
                x,
                y: 0.0,
                w: 10.0,
                h: 10.0,
                u0: 0.0,
                v0: 0.0,
                u1: 1.0,
                v1: 1.0,
            },
            tint: Color::WHITE,
        }
    }

    fn list(main: Vec<DrawCmd>, overlay: Vec<DrawCmd>, scale: (f32, f32)) -> DrawList {
        DrawList {
            main,
            overlay,
            canvas_scale: scale,
        }
    }

    fn keys(batch: &UiBatch) -> Vec<(UiTexture, Filter, u32)> {
        batch
            .runs
            .iter()
            .map(|r| (r.key.texture, r.key.filter, r.count))
            .collect()
    }

    #[test]
    fn a_sprite_is_two_triangles_with_its_texture_coordinates() {
        let batch = UiBatch::build(&list(
            vec![sprite(0x0600_0001, Filter::Linear, 5.0)],
            vec![],
            (1.0, 1.0),
        ));
        assert_eq!(batch.vertices.len(), 6);
        let corners: Vec<([f32; 2], [f32; 2])> =
            batch.vertices.iter().map(|v| (v.position, v.uv)).collect();
        assert_eq!(
            corners,
            [
                ([5.0, 0.0], [0.0, 0.0]),
                ([15.0, 10.0], [1.0, 1.0]),
                ([15.0, 0.0], [1.0, 0.0]),
                ([5.0, 0.0], [0.0, 0.0]),
                ([5.0, 10.0], [0.0, 1.0]),
                ([15.0, 10.0], [1.0, 1.0]),
            ]
        );
    }

    #[test]
    fn consecutive_commands_on_one_texture_share_a_run_and_order_is_kept() {
        let a = 0x0600_000A;
        let b = 0x0600_000B;
        let batch = UiBatch::build(&list(
            vec![
                sprite(a, Filter::Linear, 0.0),
                sprite(a, Filter::Linear, 10.0),
                sprite(b, Filter::Linear, 20.0),
                sprite(a, Filter::Linear, 30.0),
            ],
            vec![],
            (1.0, 1.0),
        ));
        let named = |id| UiTexture::Named(TextureRef::Surface(id));
        assert_eq!(
            keys(&batch),
            [
                (named(a), Filter::Linear, 12),
                (named(b), Filter::Linear, 6),
                (named(a), Filter::Linear, 6),
            ]
        );
        assert_eq!(
            batch.runs.iter().map(|r| r.first).collect::<Vec<_>>(),
            [0, 12, 18]
        );
    }

    #[test]
    fn the_overlay_is_painted_after_the_main_layer() {
        let batch = UiBatch::build(&list(
            vec![sprite(1, Filter::Linear, 0.0)],
            vec![sprite(2, Filter::Linear, 0.0)],
            (1.0, 1.0),
        ));
        let order: Vec<UiTexture> = batch.runs.iter().map(|r| r.key.texture).collect();
        assert_eq!(
            order,
            [
                UiTexture::Named(TextureRef::Surface(1)),
                UiTexture::Named(TextureRef::Surface(2))
            ]
        );
    }

    #[test]
    fn a_fixed_canvas_scales_positions_and_samples_glyphs_linear() {
        let glyphs = DrawCmd::Glyphs {
            texture: TextureRef::Surface(7),
            tint: Color::WHITE,
            quads: vec![Quad {
                x: 10.0,
                y: 10.0,
                w: 4.0,
                h: 8.0,
                u0: 0.0,
                v0: 0.0,
                u1: 0.5,
                v1: 0.5,
            }],
        };
        // CanvasScale One: the nearest atlas stays nearest.
        let one = UiBatch::build(&list(vec![glyphs.clone()], vec![], (1.0, 1.0)));
        assert_eq!(one.runs[0].key.filter, Filter::Nearest);
        assert_eq!(one.vertices[1].position, [14.0, 18.0]);
        // CanvasScale not One: positions scale, nearest becomes linear, the
        // texture is the same.
        let scaled = UiBatch::build(&list(vec![glyphs], vec![], (2.4, 1.8)));
        assert_eq!(scaled.runs[0].key.filter, Filter::Linear);
        assert_eq!(
            scaled.runs[0].key.texture,
            UiTexture::Named(TextureRef::Surface(7))
        );
        let p = scaled.vertices[1].position;
        assert!((p[0] - 14.0 * 2.4).abs() < 1e-4 && (p[1] - 18.0 * 1.8).abs() < 1e-4);
        // A linear sprite is unaffected by the twin rule.
        let sprite = UiBatch::build(&list(
            vec![sprite(5, Filter::Linear, 0.0)],
            vec![],
            (2.4, 1.8),
        ));
        assert_eq!(sprite.runs[0].key.filter, Filter::Linear);
    }

    #[test]
    fn a_polygon_is_a_fan_from_its_first_corner() {
        let corners: Vec<Vertex> = (0..5)
            .map(|i| Vertex::new(i as f32, 0.0, 0.0, 0.0))
            .collect();
        let batch = UiBatch::build(&list(
            vec![DrawCmd::Polygon {
                texture: None,
                vertices: corners,
                color: Color::BLACK,
            }],
            vec![],
            (1.0, 1.0),
        ));
        let xs: Vec<f32> = batch.vertices.iter().map(|v| v.position[0]).collect();
        assert_eq!(xs, [0.0, 1.0, 2.0, 0.0, 2.0, 3.0, 0.0, 3.0, 4.0]);
        assert_eq!(batch.runs[0].key, white());
    }

    #[test]
    fn fills_sample_white_and_join_untextured_polygons() {
        let fill = DrawCmd::Fill {
            x: 0.0,
            y: 0.0,
            w: 2.0,
            h: 2.0,
            color: Color::rgba(1.0, 0.0, 0.0, 0.5),
        };
        let poly = DrawCmd::Polygon {
            texture: None,
            vertices: vec![
                Vertex::new(0.0, 0.0, 0.0, 0.0),
                Vertex::new(1.0, 0.0, 0.0, 0.0),
                Vertex::new(1.0, 1.0, 0.0, 0.0),
            ],
            color: Color::WHITE,
        };
        let batch = UiBatch::build(&list(vec![fill, poly], vec![], (1.0, 1.0)));
        assert_eq!(keys(&batch), [(UiTexture::White, Filter::Nearest, 9)]);
        assert_eq!(batch.vertices[0].color, [1.0, 0.0, 0.0, 0.5]);
    }

    #[test]
    fn triangles_keep_each_corners_colour_and_join_the_white_run() {
        let red = Color::rgba(1.0, 0.0, 0.0, 1.0);
        let clear = Color::rgba(0.0, 0.0, 1.0, 0.0);
        let fill = DrawCmd::Fill {
            x: 0.0,
            y: 0.0,
            w: 2.0,
            h: 2.0,
            color: Color::WHITE,
        };
        let triangles = DrawCmd::Triangles {
            vertices: vec![
                ColorVertex::new(1.0, 2.0, red),
                ColorVertex::new(3.0, 2.0, clear),
                ColorVertex::new(1.0, 4.0, red),
            ],
        };
        let batch = UiBatch::build(&list(vec![fill, triangles], vec![], (2.0, 3.0)));
        assert_eq!(keys(&batch), [(UiTexture::White, Filter::Nearest, 9)]);
        assert_eq!(batch.vertices[6].position, [2.0, 6.0]);
        assert_eq!(batch.vertices[7].position, [6.0, 6.0]);
        assert_eq!(batch.vertices[7].color, [0.0, 0.0, 1.0, 0.0]);
    }

    #[test]
    fn a_partial_triangle_is_dropped() {
        let two_corners = DrawCmd::Triangles {
            vertices: vec![ColorVertex::default(); 2],
        };
        let batch = UiBatch::build(&list(vec![two_corners], vec![], (1.0, 1.0)));
        assert!(batch.is_empty() && batch.runs.is_empty());
    }

    #[test]
    fn surface_zero_draws_nothing() {
        let batch = UiBatch::build(&list(
            vec![
                sprite(0, Filter::Linear, 0.0),
                DrawCmd::Glyphs {
                    texture: TextureRef::Surface(0),
                    tint: Color::WHITE,
                    quads: vec![Quad::default()],
                },
            ],
            vec![],
            (1.0, 1.0),
        ));
        assert!(batch.is_empty() && batch.runs.is_empty());
        assert_eq!(batch.skipped_zero_ids, 2);
    }

    #[test]
    fn empty_rectangles_add_nothing() {
        let mut s = sprite(3, Filter::Linear, 0.0);
        if let DrawCmd::Sprite { quad, .. } = &mut s {
            quad.w = 0.0;
        }
        let batch = UiBatch::build(&list(vec![s], vec![], (1.0, 1.0)));
        assert!(batch.is_empty() && batch.runs.is_empty());
    }

    fn xs(batch: &UiBatch) -> Vec<f32> {
        batch.vertices.iter().map(|v| v.position[0]).collect()
    }

    /// Two border pieces that meet at unit 37: at 110% the join is at
    /// 40.7 pixels, so unsnapped the GPU blends both across two pixels
    /// (a fuzzy edge and a seam). Snapped, both end and start at pixel 41.
    #[test]
    fn at_a_fractional_scale_every_edge_lands_on_a_whole_pixel_and_joins_stay_joined() {
        let piece = |x: f32, w: f32| DrawCmd::Fill {
            x,
            y: 3.0,
            w,
            h: 5.0,
            color: Color::WHITE,
        };
        let batch = UiBatch::build_scaled(
            &list(
                vec![piece(0.0, 37.0), piece(37.0, 13.0)],
                vec![],
                (1.0, 1.0),
            ),
            1.1,
        );
        for v in &batch.vertices {
            assert_eq!(v.position[0].fract(), 0.0, "{:?}", v.position);
            assert_eq!(v.position[1].fract(), 0.0, "{:?}", v.position);
        }
        // tl, br, tr, tl, bl, br of each.
        let x = xs(&batch);
        assert_eq!((x[0], x[1]), (0.0, 41.0));
        assert_eq!((x[6], x[7]), (41.0, 55.0));
        assert_eq!(
            (batch.vertices[0].position[1], batch.vertices[1].position[1]),
            (3.0, 9.0)
        );
    }

    #[test]
    fn one_hundred_percent_draws_exactly_what_the_list_says() {
        let fill = DrawCmd::Fill {
            x: 10.25,
            y: 0.5,
            w: 3.5,
            h: 1.0,
            color: Color::WHITE,
        };
        let batch = UiBatch::build_scaled(&list(vec![fill], vec![], (1.0, 1.0)), 1.0);
        assert_eq!(batch.vertices[1].position, [13.75, 1.5]);
        assert_eq!(
            batch,
            UiBatch::build(&list(
                vec![DrawCmd::Fill {
                    x: 10.25,
                    y: 0.5,
                    w: 3.5,
                    h: 1.0,
                    color: Color::WHITE,
                }],
                vec![],
                (1.0, 1.0)
            ))
        );
    }

    /// Nearest art stays nearest at 200% and 300% (each texel a block of
    /// whole pixels) and is smoothed only at a fractional scale.
    #[test]
    fn nearest_art_is_smoothed_only_at_a_fractional_scale() {
        let filter = |ppu: f32| {
            UiBatch::build_scaled(
                &list(vec![sprite(9, Filter::Nearest, 0.0)], vec![], (1.0, 1.0)),
                ppu,
            )
            .runs[0]
                .key
                .filter
        };
        assert_eq!(filter(1.0), Filter::Nearest);
        assert_eq!(filter(2.0), Filter::Nearest);
        assert_eq!(filter(3.0), Filter::Nearest);
        assert_eq!(filter(1.1), Filter::Linear);
        assert_eq!(filter(0.5), Filter::Linear);
        assert_eq!(filter(1.5), Filter::Linear);
        // At 200% a sprite at unit 5 of 10 units is pixels 10 to 30.
        let batch = UiBatch::build_scaled(
            &list(vec![sprite(9, Filter::Nearest, 5.0)], vec![], (1.0, 1.0)),
            2.0,
        );
        assert_eq!(batch.vertices[1].position, [30.0, 20.0]);
        assert_eq!(batch.vertices[0].position, [10.0, 0.0]);
    }

    /// At 50% a one-unit line is half a pixel: it keeps one pixel rather
    /// than vanishing.
    #[test]
    fn at_half_scale_a_thin_line_keeps_a_pixel() {
        let line = DrawCmd::Fill {
            x: 11.0,
            y: 4.0,
            w: 1.0,
            h: 1.0,
            color: Color::WHITE,
        };
        let batch = UiBatch::build_scaled(&list(vec![line], vec![], (1.0, 1.0)), 0.5);
        let x = xs(&batch);
        assert_eq!((x[0], x[1]), (5.0, 6.0));
        let y = (batch.vertices[0].position[1], batch.vertices[1].position[1]);
        assert_eq!(y, (2.0, 3.0));
    }

    #[test]
    fn a_polygons_corners_land_on_whole_pixels_when_scaled() {
        let corners = vec![
            Vertex::new(0.0, 0.0, 0.0, 0.0),
            Vertex::new(37.0, 0.0, 1.0, 0.0),
            Vertex::new(37.0, 9.0, 1.0, 1.0),
        ];
        let batch = UiBatch::build_scaled(
            &list(
                vec![DrawCmd::Polygon {
                    texture: None,
                    vertices: corners,
                    color: Color::WHITE,
                }],
                vec![],
                (1.0, 1.0),
            ),
            1.1,
        );
        assert_eq!(xs(&batch), [0.0, 41.0, 41.0]);
        assert_eq!(batch.vertices[2].position[1], 10.0);
    }
}
