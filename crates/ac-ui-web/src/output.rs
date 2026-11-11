//! What the module hands the page, by kind: each kind is a run of bytes
//! the page takes after a frame. All numbers are little-endian.

/// The kinds of output. A kind's bit in a frame's mask is `1 << (kind - 1)`.
pub mod kind {
    /// Triangles: 32 bytes a corner.
    pub const VERTICES: u32 = 1;
    /// Which texture each stretch of corners is drawn with: 20 bytes each.
    pub const RUNS: u32 = 2;
    /// Textures to create or replace.
    pub const TEXTURE_UPLOADS: u32 = 3;
    /// Textures to drop: 8 bytes each.
    pub const TEXTURE_RELEASES: u32 = 4;
    /// Frames to send to the session.
    pub const COMMAND_FRAMES: u32 = 5;
    /// Dat files wanted: 8 bytes each.
    pub const DAT_REQUESTS: u32 = 6;
    /// Settings to store.
    pub const STORAGE_WRITES: u32 = 7;
    /// Text for the clipboard.
    pub const CLIPBOARD_COPY: u32 = 8;
    /// The clipboard's text is wanted (no bytes).
    pub const CLIPBOARD_PASTE: u32 = 9;
    /// The pointer's shape: 20 bytes.
    pub const CURSOR: u32 = 10;
    /// Lines for the page's console.
    pub const LOG: u32 = 11;
    /// Squares a 3D renderer fills: 24 bytes each.
    pub const VIEWPORTS: u32 = 12;
    /// The largest kind.
    pub const LAST: u32 = VIEWPORTS;
}

/// One corner: canvas pixels from the top left, texture coordinate, colour
/// (straight alpha, 0 to 1).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Vertex {
    /// Position.
    pub position: [f32; 2],
    /// Texture coordinate.
    pub uv: [f32; 2],
    /// Red, green, blue, alpha.
    pub color: [f32; 4],
}

/// Which texture a run draws with.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Texture {
    /// One opaque white texel (untextured triangles).
    White,
    /// A render surface of the dats, by id.
    Surface(u32),
    /// A texture the module made (a glyph atlas, a composed icon), by its
    /// own id.
    Runtime(u32),
}

impl Texture {
    /// The kind and id as the page reads them.
    #[must_use]
    pub fn words(self) -> (u32, u32) {
        match self {
            Self::White => (0, 0),
            Self::Surface(id) => (1, id),
            Self::Runtime(id) => (2, id),
        }
    }
}

/// How a run's texture is sampled.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Filter {
    /// Blended between texels.
    Linear,
    /// The nearest texel.
    Nearest,
}

/// A texture's texels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Texels<'a> {
    /// Four bytes a texel: red, green, blue, alpha (straight), rows top to
    /// bottom.
    Rgba(&'a [u8]),
    /// One byte a texel: drawn as white with that alpha.
    Alpha(&'a [u8]),
}

/// One level of a texture.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Level<'a> {
    /// Width in texels.
    pub width: u32,
    /// Height in texels.
    pub height: u32,
    /// The texels: `width * height` of them.
    pub texels: Texels<'a>,
}

/// The pointer's shape.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Cursor {
    /// The page's own arrow.
    Default,
    /// A text caret.
    Text,
    /// Moving something.
    Move,
    /// Resizing left and right.
    ResizeHorizontal,
    /// Resizing up and down.
    ResizeVertical,
    /// Resizing along the falling diagonal.
    ResizeDiagonalDown,
    /// Resizing along the rising diagonal.
    ResizeDiagonalUp,
    /// A hand.
    Hand,
}

/// A frame's outputs, as the bytes the page takes.
#[derive(Debug, Default)]
pub struct Outputs {
    /// The bytes of each kind, by `kind - 1`.
    bytes: [Vec<u8>; kind::LAST as usize],
    /// The kinds that have something to take, even when it is no bytes.
    present: u32,
}

impl Outputs {
    /// The mask of the kinds that have something to take.
    #[must_use]
    pub fn mask(&self) -> u32 {
        self.present
    }

    /// Take the bytes of `kind` (none for a kind that does not exist or
    /// has nothing); the kind then has nothing until it is written again.
    pub fn take(&mut self, kind: u32) -> Vec<u8> {
        if !(1..=kind::LAST).contains(&kind) {
            return Vec::new();
        }
        self.present &= !(1 << (kind - 1));
        std::mem::take(&mut self.bytes[kind as usize - 1])
    }

    /// The bytes of `kind`, to write to; the kind then has something to
    /// take, even if nothing is written.
    fn of(&mut self, kind: u32) -> &mut Vec<u8> {
        self.present |= 1 << (kind - 1);
        &mut self.bytes[kind as usize - 1]
    }

    /// The whole picture: every corner and the runs that draw them, in
    /// paint order. An empty picture is still a picture (it draws nothing).
    pub fn picture(&mut self, vertices: &[Vertex], runs: &[(Texture, Filter, u32, u32)]) {
        let bytes = self.of(kind::VERTICES);
        bytes.clear();
        bytes.reserve(vertices.len() * 32);
        for vertex in vertices {
            let Vertex {
                position,
                uv,
                color,
            } = vertex;
            for value in position.iter().chain(uv).chain(color) {
                bytes.extend_from_slice(&value.to_le_bytes());
            }
        }
        let bytes = self.of(kind::RUNS);
        bytes.clear();
        for &(texture, filter, first, count) in runs {
            let (texture_kind, id) = texture.words();
            let filter = match filter {
                Filter::Linear => 0,
                Filter::Nearest => 1,
            };
            put(bytes, &[texture_kind, id, filter, first, count]);
        }
    }

    /// A texture to create or replace: its levels, the image first.
    pub fn upload(&mut self, texture: Texture, levels: &[Level<'_>]) {
        let Some(first) = levels.first() else {
            return;
        };
        let (texture_kind, id) = texture.words();
        let format = match first.texels {
            Texels::Rgba(_) => 0,
            Texels::Alpha(_) => 1,
        };
        let count = u32::try_from(levels.len()).unwrap_or(u32::MAX);
        let bytes = self.of(kind::TEXTURE_UPLOADS);
        put(
            bytes,
            &[texture_kind, id, format, first.width, first.height, count],
        );
        for level in levels {
            put(bytes, &[level.width, level.height]);
            match level.texels {
                Texels::Rgba(texels) | Texels::Alpha(texels) => bytes.extend_from_slice(texels),
            }
        }
    }

    /// A texture to drop.
    pub fn release(&mut self, texture: Texture) {
        let (texture_kind, id) = texture.words();
        put(self.of(kind::TEXTURE_RELEASES), &[texture_kind, id]);
    }

    /// One frame to send to the session.
    pub fn command_frame(&mut self, frame: &[u8]) {
        put_bytes(self.of(kind::COMMAND_FRAMES), frame);
    }

    /// A dat file is wanted: `source` 0 portal, 1 cell, 2 high-resolution,
    /// 3 local language.
    pub fn dat_request(&mut self, source: u32, id: u32) {
        put(self.of(kind::DAT_REQUESTS), &[source, id]);
    }

    /// A setting to store under `key`.
    pub fn storage_write(&mut self, key: &str, value: &[u8]) {
        let bytes = self.of(kind::STORAGE_WRITES);
        put_bytes(bytes, key.as_bytes());
        put_bytes(bytes, value);
    }

    /// Text for the clipboard (the last one of a frame wins).
    pub fn clipboard_copy(&mut self, text: &str) {
        let bytes = self.of(kind::CLIPBOARD_COPY);
        bytes.clear();
        bytes.extend_from_slice(text.as_bytes());
    }

    /// The clipboard's text is wanted.
    pub fn clipboard_paste(&mut self) {
        self.of(kind::CLIPBOARD_PASTE);
    }

    /// The pointer's shape, and the texture and hot spot of a pointer the
    /// client draws itself (`None`: the page's own).
    pub fn cursor(&mut self, cursor: Cursor, own: Option<(Texture, u32, u32)>) {
        let shape = match cursor {
            Cursor::Default => 0,
            Cursor::Text => 1,
            Cursor::Move => 2,
            Cursor::ResizeHorizontal => 3,
            Cursor::ResizeVertical => 4,
            Cursor::ResizeDiagonalDown => 5,
            Cursor::ResizeDiagonalUp => 6,
            Cursor::Hand => 7,
        };
        let (texture, hot_x, hot_y) = own.unwrap_or((Texture::White, 0, 0));
        let (texture_kind, id) = texture.words();
        let bytes = self.of(kind::CURSOR);
        bytes.clear();
        put(bytes, &[shape, texture_kind, id, hot_x, hot_y]);
    }

    /// A line for the page's console.
    pub fn log(&mut self, line: &str) {
        let bytes = self.of(kind::LOG);
        bytes.extend_from_slice(line.as_bytes());
        bytes.push(b'\n');
    }
}

fn put(bytes: &mut Vec<u8>, words: &[u32]) {
    for word in words {
        bytes.extend_from_slice(&word.to_le_bytes());
    }
}

/// A length, then that many bytes.
fn put_bytes(bytes: &mut Vec<u8>, payload: &[u8]) {
    put(bytes, &[u32::try_from(payload.len()).unwrap_or(u32::MAX)]);
    bytes.extend_from_slice(payload);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn words(words: &[u32]) -> Vec<u8> {
        words.iter().flat_map(|w| w.to_le_bytes()).collect()
    }

    fn floats(values: &[f32]) -> Vec<u8> {
        values.iter().flat_map(|v| v.to_le_bytes()).collect()
    }

    fn bit(kind: u32) -> u32 {
        1 << (kind - 1)
    }

    #[test]
    fn a_picture_is_corners_of_32_bytes_and_runs_of_20() {
        let mut out = Outputs::default();
        let corner = Vertex {
            position: [1.5, 2.0],
            uv: [0.25, 0.75],
            color: [1.0, 0.5, 0.25, 1.0],
        };
        out.picture(
            &[corner; 3],
            &[
                (Texture::White, Filter::Linear, 0, 3),
                (Texture::Surface(0x0600_1234), Filter::Nearest, 3, 6),
                (Texture::Runtime(7), Filter::Linear, 9, 12),
            ],
        );
        assert_eq!(out.mask(), bit(kind::VERTICES) | bit(kind::RUNS));
        let one = floats(&[1.5, 2.0, 0.25, 0.75, 1.0, 0.5, 0.25, 1.0]);
        assert_eq!(one.len(), 32);
        assert_eq!(out.take(kind::VERTICES), one.repeat(3));
        assert_eq!(
            out.take(kind::RUNS),
            words(&[0, 0, 0, 0, 3, 1, 0x0600_1234, 1, 3, 6, 2, 7, 0, 9, 12])
        );
        // Taken: nothing until the next picture.
        assert_eq!(out.mask(), 0);
        assert!(out.take(kind::VERTICES).is_empty());
    }

    #[test]
    fn an_empty_picture_is_still_there_to_take() {
        let mut out = Outputs::default();
        assert_eq!(out.mask(), 0, "no picture: the last one stands");
        out.picture(&[], &[]);
        assert_eq!(out.mask(), bit(kind::VERTICES) | bit(kind::RUNS));
        assert!(out.take(kind::VERTICES).is_empty());
        assert!(out.take(kind::RUNS).is_empty());
        assert_eq!(out.mask(), 0);
        // A new picture replaces one not taken.
        let corner = Vertex {
            position: [0.0; 2],
            uv: [0.0; 2],
            color: [0.0; 4],
        };
        out.picture(&[corner; 6], &[(Texture::White, Filter::Linear, 0, 6)]);
        out.picture(&[corner; 3], &[(Texture::White, Filter::Linear, 0, 3)]);
        assert_eq!(out.take(kind::VERTICES).len(), 3 * 32);
        assert_eq!(out.take(kind::RUNS).len(), 20);
    }

    #[test]
    fn a_texture_upload_names_the_texture_its_format_and_every_level() {
        let mut out = Outputs::default();
        let image = [9u8; 8];
        let half = [8u8; 4];
        out.upload(
            Texture::Runtime(3),
            &[
                Level {
                    width: 2,
                    height: 1,
                    texels: Texels::Rgba(&image),
                },
                Level {
                    width: 1,
                    height: 1,
                    texels: Texels::Rgba(&half),
                },
            ],
        );
        let coverage = [1u8, 2, 3, 4, 5, 6];
        out.upload(
            Texture::Surface(5),
            &[Level {
                width: 3,
                height: 2,
                texels: Texels::Alpha(&coverage),
            }],
        );
        let mut expected = words(&[2, 3, 0, 2, 1, 2, 2, 1]);
        expected.extend_from_slice(&image);
        expected.extend_from_slice(&words(&[1, 1]));
        expected.extend_from_slice(&half);
        expected.extend_from_slice(&words(&[1, 5, 1, 3, 2, 1, 3, 2]));
        expected.extend_from_slice(&coverage);
        assert_eq!(out.mask(), bit(kind::TEXTURE_UPLOADS));
        assert_eq!(out.take(kind::TEXTURE_UPLOADS), expected);
    }

    #[test]
    fn the_small_records() {
        let mut out = Outputs::default();
        out.release(Texture::Surface(5));
        out.release(Texture::Runtime(6));
        assert_eq!(out.take(kind::TEXTURE_RELEASES), words(&[1, 5, 2, 6]));

        out.command_frame(&[1, 2, 3]);
        out.command_frame(&[]);
        let mut frames = words(&[3]);
        frames.extend_from_slice(&[1, 2, 3]);
        frames.extend_from_slice(&words(&[0]));
        assert_eq!(out.take(kind::COMMAND_FRAMES), frames);

        out.dat_request(3, 0x2100_0006);
        out.dat_request(2, 0x0600_0001);
        assert_eq!(
            out.take(kind::DAT_REQUESTS),
            words(&[3, 0x2100_0006, 2, 0x0600_0001])
        );

        out.storage_write("layout/a", &[7, 7]);
        let mut write = words(&[8]);
        write.extend_from_slice(b"layout/a");
        write.extend_from_slice(&words(&[2]));
        write.extend_from_slice(&[7, 7]);
        assert_eq!(out.take(kind::STORAGE_WRITES), write);

        out.clipboard_copy("first");
        out.clipboard_copy("last");
        assert_eq!(out.take(kind::CLIPBOARD_COPY), b"last");

        out.clipboard_paste();
        assert_eq!(out.mask(), bit(kind::CLIPBOARD_PASTE));
        assert!(out.take(kind::CLIPBOARD_PASTE).is_empty());
        assert_eq!(out.mask(), 0);

        out.cursor(Cursor::Text, None);
        assert_eq!(out.take(kind::CURSOR), words(&[1, 0, 0, 0, 0]));
        out.cursor(Cursor::Default, Some((Texture::Surface(9), 4, 5)));
        out.cursor(Cursor::Hand, Some((Texture::Surface(9), 4, 5)));
        assert_eq!(
            out.take(kind::CURSOR),
            words(&[7, 1, 9, 4, 5]),
            "the last wins"
        );

        out.log("one");
        out.log("two");
        assert_eq!(out.take(kind::LOG), b"one\ntwo\n");
    }

    #[test]
    fn a_kind_that_does_not_exist_has_nothing() {
        let mut out = Outputs::default();
        out.log("x");
        assert!(out.take(0).is_empty());
        assert!(out.take(kind::LAST + 1).is_empty());
        assert_eq!(out.mask(), bit(kind::LOG));
    }
}
