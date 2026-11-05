//! One plugin's images (OpenAC `PluginImageTable`): every image it asked
//! for, counted once per distinct request and held as many times as asked,
//! within a count, a byte and a size budget. Handles are never reused.

use std::sync::Arc;

use crate::model::{ImageSource, ImageView};

/// How much one plugin may hold (OpenAC `PluginImageBudget`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ImageBudget {
    /// Distinct images, client art included.
    pub maximum_count: usize,
    /// Bytes of the plugin's own pixels.
    pub maximum_bytes: u64,
    /// The widest or tallest the plugin's own image may be.
    pub maximum_dimension: u32,
}

impl Default for ImageBudget {
    /// 256 images, 32 MB of the plugin's own art, 2048 on a side.
    fn default() -> Self {
        Self {
            maximum_count: 256,
            maximum_bytes: 32 * 1024 * 1024,
            maximum_dimension: 2048,
        }
    }
}

/// A plugin's encoded image as the host decoded it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DecodedImage {
    /// Width, pixels.
    pub width: u32,
    /// Height, pixels.
    pub height: u32,
    /// 8-bit RGBA rows from the top.
    pub rgba: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Key {
    ClientArt(u32),
    SpellIcon(u32),
    ObjectIcon(u32),
    Rgba(String),
    /// An image file the plugin's markup draws: its path and the version
    /// of the file it was read from.
    File {
        path: String,
        version: String,
    },
}

#[derive(Debug, Clone)]
struct Entry {
    key: Key,
    view: ImageView,
    owned_bytes: u64,
    holds: u32,
}

/// What a request got.
#[derive(Debug, Clone, PartialEq)]
pub struct Acquired {
    /// The handle.
    pub handle: u32,
    /// Width.
    pub width: u32,
    /// Height.
    pub height: u32,
    /// The image when this request made it (not a repeat).
    pub added: Option<ImageView>,
}

/// One plugin's images. See the module documentation.
#[derive(Debug, Clone)]
pub struct ImageTable {
    budget: ImageBudget,
    entries: Vec<Entry>,
    next_handle: u32,
    owned_bytes: u64,
    reported: Vec<String>,
    release_generation: u64,
}

impl ImageTable {
    /// An empty table within `budget`.
    #[must_use]
    pub fn new(budget: ImageBudget) -> Self {
        Self {
            budget,
            entries: Vec::new(),
            next_handle: 0,
            owned_bytes: 0,
            reported: Vec::new(),
            release_generation: 0,
        }
    }

    /// An empty table whose handles start after `last` (the last handle an
    /// earlier table of the same plugin gave out): a plugin reloaded never
    /// gets a handle it, or a window still showing its old images, used
    /// before.
    #[must_use]
    pub fn after(budget: ImageBudget, last: u32) -> Self {
        Self {
            next_handle: last,
            ..Self::new(budget)
        }
    }

    /// The last handle given out (0: none yet).
    #[must_use]
    pub fn last_handle(&self) -> u32 {
        self.next_handle
    }

    /// Moves on each time an image is let go of completely: a request
    /// refused for want of room may succeed once this has moved.
    #[must_use]
    pub fn release_generation(&self) -> u64 {
        self.release_generation
    }

    /// Distinct images held.
    #[must_use]
    pub fn count(&self) -> usize {
        self.entries.len()
    }

    /// Bytes of the plugin's own pixels held.
    #[must_use]
    pub fn owned_bytes(&self) -> u64 {
        self.owned_bytes
    }

    /// Every handle held.
    #[must_use]
    pub fn handles(&self) -> Vec<u32> {
        self.entries.iter().map(|e| e.view.handle).collect()
    }

    /// Every image held.
    pub fn views(&self) -> impl Iterator<Item = &ImageView> {
        self.entries.iter().map(|e| &e.view)
    }

    /// The image behind a handle.
    #[must_use]
    pub fn resolve(&self, handle: u32) -> Option<&ImageView> {
        self.entries
            .iter()
            .find(|e| e.view.handle == handle)
            .map(|e| &e.view)
    }

    /// A report, the first time this key is reported; an empty string
    /// (nothing to log) after that.
    fn report_once(&mut self, key: String, message: String) -> String {
        if self.reported.contains(&key) {
            return String::new();
        }
        self.reported.push(key);
        message
    }

    fn held(&mut self, key: &Key) -> Option<Acquired> {
        let entry = self.entries.iter_mut().find(|e| e.key == *key)?;
        entry.holds += 1;
        Some(Acquired {
            handle: entry.view.handle,
            width: entry.view.width,
            height: entry.view.height,
            added: None,
        })
    }

    fn room_for_one_more(&mut self, what: &str) -> Result<(), String> {
        if self.entries.len() < self.budget.maximum_count {
            return Ok(());
        }
        Err(self.report_once(
            "count".to_owned(),
            format!(
                "{what} refused: the plugin already holds {} images, which is the most it may",
                self.budget.maximum_count
            ),
        ))
    }

    fn add(
        &mut self,
        key: Key,
        owner: &str,
        source: ImageSource,
        width: u32,
        height: u32,
        owned_bytes: u64,
    ) -> Acquired {
        self.next_handle += 1;
        let view = ImageView {
            owner: owner.to_owned(),
            handle: self.next_handle,
            width,
            height,
            source,
        };
        self.owned_bytes += owned_bytes;
        self.entries.push(Entry {
            key,
            view: view.clone(),
            owned_bytes,
            holds: 1,
        });
        Acquired {
            handle: view.handle,
            width,
            height,
            added: Some(view),
        }
    }

    /// Client art, a spell icon or an object icon: shared with every other
    /// reader, not counted against the byte budget. `size` is `None` when
    /// the client cannot draw it.
    ///
    /// # Errors
    /// Over the count, or nothing the client can draw (the report, empty
    /// when already reported).
    pub fn acquire_shared(
        &mut self,
        owner: &str,
        source: ImageSource,
        size: Option<(u32, u32)>,
        what: &str,
    ) -> Result<Acquired, String> {
        let key = match &source {
            ImageSource::ClientArt(id) => Key::ClientArt(*id),
            ImageSource::SpellIcon { spell, .. } => Key::SpellIcon(*spell),
            ImageSource::ObjectIcon(id) => Key::ObjectIcon(*id),
            ImageSource::Rgba(_) | ImageSource::File(_) => return Err(String::new()),
        };
        let id = match key {
            Key::ClientArt(id) | Key::SpellIcon(id) | Key::ObjectIcon(id) => id,
            Key::Rgba(_) | Key::File { .. } => 0,
        };
        if id == 0 {
            return Err(String::new());
        }
        if let Some(held) = self.held(&key) {
            return Ok(held);
        }
        self.room_for_one_more(what)?;
        let Some((width, height)) = size.filter(|(w, h)| *w > 0 && *h > 0) else {
            return Err(self.report_once(
                format!("missing:{what}"),
                format!("{what} is not something the client can draw"),
            ));
        };
        Ok(self.add(key, owner, source, width, height, 0))
    }

    /// The plugin's own pixels (8-bit RGBA rows), under its own name for
    /// them; a second request under the same name is the same image held
    /// once more.
    ///
    /// # Errors
    /// Over the count, the size or the byte budget, or pixels that do not
    /// fill the size (the report).
    pub fn acquire_rgba(
        &mut self,
        owner: &str,
        name: &str,
        width: u32,
        height: u32,
        rgba: Vec<u8>,
    ) -> Result<Acquired, String> {
        if name.trim().is_empty() {
            return Err("an image needs a name".to_owned());
        }
        let key = Key::Rgba(name.to_owned());
        if let Some(held) = self.held(&key) {
            return Ok(held);
        }
        let what = format!("image '{name}'");
        self.room_for_one_more(&what)?;
        if width == 0 || height == 0 {
            return Err(self.report_once(
                format!("empty:{name}"),
                format!("image '{name}' has nothing in it"),
            ));
        }
        let max = self.budget.maximum_dimension;
        if width > max || height > max {
            return Err(self.report_once(
                format!("dimension:{name}"),
                format!(
                    "image '{name}' is {width}x{height}; the most a plugin image may be is {max} on a side"
                ),
            ));
        }
        let bytes = u64::from(width) * u64::from(height) * 4;
        if rgba.len() as u64 != bytes {
            return Err(self.report_once(
                format!("pixels:{name}"),
                format!(
                    "image '{name}' is {width}x{height} but carries {} bytes, not {bytes}",
                    rgba.len()
                ),
            ));
        }
        if self.owned_bytes + bytes > self.budget.maximum_bytes {
            return Err(self.report_once(
                format!("bytes:{name}"),
                format!(
                    "image '{name}' would take the plugin's own art to {} bytes; the budget is {}",
                    self.owned_bytes + bytes,
                    self.budget.maximum_bytes
                ),
            ));
        }
        Ok(self.add(
            key,
            owner,
            ImageSource::Rgba(Arc::new(rgba)),
            width,
            height,
            bytes,
        ))
    }

    /// The plugin's own encoded art (OpenAC `AcquireDecoded`), under its own
    /// name for it: a second request under the same name is the same image
    /// held once more, and `bytes` are not read again. A header that
    /// declares a size over the budget is refused on that word, before
    /// `decode` allocates anything; a header `dimensions` cannot read falls
    /// through to `decode`, which says why in its own words.
    ///
    /// # Errors
    /// Over the count, the size or the byte budget, or a file that does not
    /// decode (the report).
    pub fn acquire_decoded(
        &mut self,
        owner: &str,
        name: &str,
        bytes: &[u8],
        dimensions: impl FnOnce(&[u8]) -> Option<(u32, u32)>,
        decode: impl FnOnce(&[u8]) -> Result<DecodedImage, String>,
    ) -> Result<Acquired, String> {
        if name.trim().is_empty() {
            return Err("an image needs a name".to_owned());
        }
        let key = Key::Rgba(name.to_owned());
        if let Some(held) = self.held(&key) {
            return Ok(held);
        }
        self.room_for_one_more(&format!("image '{name}'"))?;
        if let Some((width, height)) = dimensions(bytes) {
            self.within_maximum_dimension(name, width, height)?;
        }
        let image = match decode(bytes) {
            Ok(image) => image,
            Err(failure) => {
                return Err(self.report_once(
                    format!("decode:{name}"),
                    format!("image '{name}' could not be decoded: {failure}"),
                ));
            }
        };
        if image.width == 0 || image.height == 0 {
            return Err(self.report_once(
                format!("empty:{name}"),
                format!("image '{name}' decoded to nothing"),
            ));
        }
        self.within_maximum_dimension(name, image.width, image.height)?;
        let owned = u64::from(image.width) * u64::from(image.height) * 4;
        if image.rgba.len() as u64 != owned {
            return Err(self.report_once(
                format!("pixels:{name}"),
                format!("image '{name}' decoded to the wrong number of bytes"),
            ));
        }
        if self.owned_bytes + owned > self.budget.maximum_bytes {
            return Err(self.report_once(
                format!("bytes:{name}"),
                format!(
                    "image '{name}' would take the plugin's own art to {} bytes; the budget is {}",
                    self.owned_bytes + owned,
                    self.budget.maximum_bytes
                ),
            ));
        }
        Ok(self.add(
            key,
            owner,
            ImageSource::Rgba(Arc::new(image.rgba)),
            image.width,
            image.height,
            owned,
        ))
    }

    fn within_maximum_dimension(
        &mut self,
        name: &str,
        width: u32,
        height: u32,
    ) -> Result<(), String> {
        let max = self.budget.maximum_dimension;
        if width <= max && height <= max {
            return Ok(());
        }
        Err(self.report_once(
            format!("dimension:{name}"),
            format!(
                "image '{name}' is {width}x{height}; the most a plugin image may be is {max} on a side"
            ),
        ))
    }

    /// An image file the plugin ships, which its markup names by path,
    /// read, decoded and given its smaller levels already, away from the
    /// session's thread (OpenAC `AcquireFile`). It is held under its path
    /// and the version of the file it was read from, so a window built
    /// after the file changed gets the new image while a window still
    /// showing the old one keeps it until that window lets go. Its smaller
    /// levels count against the byte budget with the image itself.
    ///
    /// # Errors
    /// Over the count, the size or the byte budget, or an image with no
    /// pixels (the report, empty when already reported).
    pub fn acquire_file(
        &mut self,
        owner: &str,
        image: &Arc<crate::file_images::FileImage>,
    ) -> Result<Acquired, String> {
        let key = Key::File {
            path: image.path.clone(),
            version: image.version.clone(),
        };
        if let Some(held) = self.held(&key) {
            return Ok(held);
        }
        let name = image.path.as_str();
        self.room_for_one_more(&format!("image '{name}'"))?;
        let (width, height) = image.size();
        if width == 0 || height == 0 {
            return Err(self.report_once(
                format!("empty:{name}"),
                format!("image '{name}' has nothing in it"),
            ));
        }
        self.within_maximum_dimension(name, width, height)?;
        let bytes = image.bytes();
        if self.owned_bytes + bytes > self.budget.maximum_bytes {
            return Err(self.report_once(
                format!("bytes:{name}"),
                format!(
                    "image '{name}' would take the plugin's own art to {} bytes; the budget is {}",
                    self.owned_bytes + bytes,
                    self.budget.maximum_bytes
                ),
            ));
        }
        Ok(self.add(
            key,
            owner,
            ImageSource::File(Arc::clone(image)),
            width,
            height,
            bytes,
        ))
    }

    /// Lets go of one hold: `None` for a handle not held, `Some(true)` when
    /// that was the last hold (the image is gone), `Some(false)` otherwise.
    pub fn release(&mut self, handle: u32) -> Option<bool> {
        if handle == 0 {
            return None;
        }
        let index = self.entries.iter().position(|e| e.view.handle == handle)?;
        let entry = &mut self.entries[index];
        entry.holds -= 1;
        if entry.holds > 0 {
            return Some(false);
        }
        self.owned_bytes -= entry.owned_bytes;
        self.entries.remove(index);
        self.release_generation += 1;
        Some(true)
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.App.Tests/Plugins/PluginImageTableTests.cs`
    //! (decoded PNGs become RGBA pixels of the same size).

    use super::*;

    fn pixels(side: u32) -> Vec<u8> {
        vec![0x80; (side * side * 4) as usize]
    }

    fn table(budget: ImageBudget) -> ImageTable {
        ImageTable::new(budget)
    }

    #[test]
    fn the_same_request_twice_is_one_image_held_twice() {
        let mut t = table(ImageBudget::default());
        let first = t.acquire_rgba("p", "map", 64, 64, pixels(64)).unwrap();
        let second = t.acquire_rgba("p", "map", 64, 64, pixels(64)).unwrap();
        assert_ne!(first.handle, 0);
        assert_eq!(first.handle, second.handle);
        assert!(first.added.is_some() && second.added.is_none());
        assert_eq!(t.count(), 1);
        assert_eq!(t.owned_bytes(), 64 * 64 * 4);
        assert_eq!(t.release(first.handle), Some(false));
        assert!(t.resolve(first.handle).is_some());
        assert_eq!(t.release(first.handle), Some(true));
        assert_eq!((t.count(), t.owned_bytes()), (0, 0));
    }

    #[test]
    fn a_released_handle_is_refused_and_never_reused() {
        let mut t = table(ImageBudget::default());
        let image = t.acquire_rgba("p", "map", 64, 64, pixels(64)).unwrap();
        assert_eq!(t.release(image.handle), Some(true));
        assert_eq!(t.release(image.handle), None);
        assert_eq!(t.release(image.handle), None);
        assert!(t.resolve(image.handle).is_none());
        let again = t.acquire_rgba("p", "map", 64, 64, pixels(64)).unwrap();
        assert_ne!(again.handle, image.handle);
        assert_eq!(t.release(0), None);
        assert_eq!(t.release(42), None);
    }

    #[test]
    fn the_count_budget_refuses_the_image_past_it() {
        let mut t = table(ImageBudget {
            maximum_count: 2,
            maximum_bytes: 1 << 30,
            maximum_dimension: 2048,
        });
        assert!(t.acquire_rgba("p", "a", 32, 32, pixels(32)).is_ok());
        assert!(t.acquire_rgba("p", "b", 32, 32, pixels(32)).is_ok());
        let third = t.acquire_rgba("p", "c", 32, 32, pixels(32)).unwrap_err();
        assert!(third.contains("holds 2 images"), "{third}");
        // Shared art counts against the same ceiling.
        assert!(
            t.acquire_shared(
                "p",
                ImageSource::ClientArt(0x0600_1234),
                Some((32, 32)),
                "art"
            )
            .is_err()
        );
        // A repeat of something held is not one more.
        assert!(t.acquire_rgba("p", "a", 32, 32, pixels(32)).is_ok());
    }

    #[test]
    fn the_byte_budget_refuses_the_image_that_would_cross_it() {
        let one = 32 * 32 * 4;
        let mut t = table(ImageBudget {
            maximum_count: 256,
            maximum_bytes: one * 2,
            maximum_dimension: 2048,
        });
        let a = t.acquire_rgba("p", "a", 32, 32, pixels(32)).unwrap();
        t.acquire_rgba("p", "b", 32, 32, pixels(32)).unwrap();
        let third = t.acquire_rgba("p", "c", 32, 32, pixels(32)).unwrap_err();
        assert!(third.contains("budget is"), "{third}");
        assert_eq!(t.owned_bytes(), one * 2);
        assert_eq!(t.release(a.handle), Some(true));
        assert!(t.acquire_rgba("p", "c", 32, 32, pixels(32)).is_ok());
    }

    #[test]
    fn an_image_wider_than_the_maximum_dimension_is_refused() {
        let mut t = table(ImageBudget {
            maximum_count: 256,
            maximum_bytes: 1 << 30,
            maximum_dimension: 32,
        });
        let big = t.acquire_rgba("p", "big", 64, 64, pixels(64)).unwrap_err();
        assert!(big.contains("64x64"), "{big}");
        let small = t.acquire_rgba("p", "small", 32, 32, pixels(32)).unwrap();
        assert_eq!((small.width, small.height), (32, 32));
        // Pixels that do not fill the size are refused.
        assert!(t.acquire_rgba("p", "short", 2, 2, vec![0; 3]).is_err());
    }

    #[test]
    fn client_art_is_shared_never_owned_and_reported_once_when_missing() {
        let mut t = table(ImageBudget::default());
        let art = t
            .acquire_shared(
                "p",
                ImageSource::ClientArt(0x0600_1234),
                Some((32, 32)),
                "art",
            )
            .unwrap();
        let spell = t
            .acquire_shared(
                "p",
                ImageSource::SpellIcon {
                    spell: 2,
                    icon: 0x0600_0001,
                },
                Some((32, 32)),
                "spell",
            )
            .unwrap();
        let item = t
            .acquire_shared(
                "p",
                ImageSource::ObjectIcon(0x8000_0001),
                Some((32, 32)),
                "item",
            )
            .unwrap();
        assert_eq!(t.owned_bytes(), 0);
        assert_eq!(t.count(), 3);
        for h in [art.handle, spell.handle, item.handle] {
            assert_eq!(t.release(h), Some(true));
        }
        let missing = t
            .acquire_shared(
                "p",
                ImageSource::ClientArt(0x0600_9999),
                None,
                "client art 0x06009999",
            )
            .unwrap_err();
        assert!(missing.contains("0x06009999"), "{missing}");
        let again = t
            .acquire_shared(
                "p",
                ImageSource::ClientArt(0x0600_9999),
                None,
                "client art 0x06009999",
            )
            .unwrap_err();
        assert!(again.is_empty(), "reported once");
        assert!(
            t.acquire_shared("p", ImageSource::ClientArt(0), Some((1, 1)), "art")
                .is_err()
        );
        assert_eq!(t.count(), 0);
    }

    fn decoded(side: u32) -> Result<DecodedImage, String> {
        Ok(DecodedImage {
            width: side,
            height: side,
            rgba: pixels(side),
        })
    }

    // OpenAC `PluginImageTableTests.ADecodeWiderThanTheMaximumDimensionIsRefusedBeforeUpload`:
    // a decode past the maximum is refused naming its size; a smaller one is
    // held at its own size.
    #[test]
    fn a_decode_wider_than_the_maximum_dimension_is_refused() {
        let mut t = table(ImageBudget {
            maximum_count: 256,
            maximum_bytes: 1 << 30,
            maximum_dimension: 32,
        });
        let big = t
            .acquire_decoded("p", "big", &[], |_| None, |_| decoded(64))
            .unwrap_err();
        assert!(big.contains("64x64"), "{big}");
        assert_eq!(t.count(), 0);
        let small = t
            .acquire_decoded("p", "small", &[], |_| None, |_| decoded(32))
            .unwrap();
        assert_eq!((small.width, small.height), (32, 32));
        assert_eq!(t.owned_bytes(), 32 * 32 * 4);
    }

    // OpenAC `PluginImageTableTests.AnOversizedHeaderIsRefusedBeforeAnyDecodeIsAttempted`:
    // a header declaring 16384 x 16384 is refused on that word; the decode is
    // never run and the report names the size, not a decode failure.
    #[test]
    fn an_oversized_header_is_refused_before_any_decode_is_attempted() {
        let mut t = table(ImageBudget::default());
        let mut decoded_at_all = false;
        let refused = t
            .acquire_decoded(
                "p",
                "huge",
                &[],
                |_| Some((16_384, 16_384)),
                |_| {
                    decoded_at_all = true;
                    decoded(1)
                },
            )
            .unwrap_err();
        assert!(!decoded_at_all);
        assert!(refused.contains("16384x16384"), "{refused}");
        assert!(!refused.contains("could not be decoded"), "{refused}");
    }

    // OpenAC `PluginImageTableTests.AStreamThatCannotBeDecodedIsRefusedWithoutThrowing`:
    // the decoder's words are reported once; nothing is held.
    #[test]
    fn a_file_that_cannot_be_decoded_is_refused_and_reported_once() {
        let mut t = table(ImageBudget::default());
        let first = t
            .acquire_decoded(
                "p",
                "garbage",
                &[1, 2, 3, 4],
                |_| None,
                |_| Err("unknown image type".to_owned()),
            )
            .unwrap_err();
        assert!(
            first.contains("could not be decoded: unknown image type"),
            "{first}"
        );
        let again = t
            .acquire_decoded(
                "p",
                "garbage",
                &[1, 2, 3, 4],
                |_| None,
                |_| Err("unknown image type".to_owned()),
            )
            .unwrap_err();
        assert!(again.is_empty(), "reported once");
        let empty = t
            .acquire_decoded("p", "empty", &[], |_| None, |_| decoded(0))
            .unwrap_err();
        assert!(empty.contains("decoded to nothing"), "{empty}");
        assert_eq!(t.count(), 0);
    }

    // OpenAC `AcquireDecoded`: a name already held is the same image held once
    // more, and the file is not opened (here: decoded) again. A name the plugin
    // gave its own pixels is the same name space.
    #[test]
    fn the_same_name_twice_is_one_image_decoded_once() {
        let mut t = table(ImageBudget::default());
        let first = t
            .acquire_decoded("p", "map.png", &[], |_| Some((8, 8)), |_| decoded(8))
            .unwrap();
        let second = t
            .acquire_decoded(
                "p",
                "map.png",
                &[],
                |_| panic!("not read"),
                |_| panic!("not decoded"),
            )
            .unwrap();
        assert_eq!(first.handle, second.handle);
        assert!(second.added.is_none());
        let rgba = t.acquire_rgba("p", "map.png", 8, 8, pixels(8)).unwrap();
        assert_eq!(rgba.handle, first.handle);
        assert_eq!(t.count(), 1);
    }

    // ── File images (OpenAC PR #271's PluginImageTableTests) ────────────

    fn file_image(
        path: &str,
        version: &str,
        width: u32,
        height: u32,
    ) -> Arc<crate::file_images::FileImage> {
        Arc::new(crate::file_images::FileImage {
            path: path.to_owned(),
            version: version.to_owned(),
            levels: crate::mips::build(vec![0; (width * height * 4) as usize], width, height),
            full_path: None,
        })
    }

    // AFileIsOneImagePerPathAndVersionHeldOncePerAsker.
    #[test]
    fn a_file_is_one_image_per_path_and_version_held_once_per_asker() {
        let mut t = table(ImageBudget::default());
        let first = t
            .acquire_file("p", &file_image("icons/sword.png", "1", 16, 16))
            .unwrap();
        let second = t
            .acquire_file("p", &file_image("icons/sword.png", "1", 16, 16))
            .unwrap();
        let changed = t
            .acquire_file("p", &file_image("icons/sword.png", "2", 8, 8))
            .unwrap();
        assert_eq!(first.handle, second.handle);
        assert_ne!(first.handle, changed.handle);
        assert!(first.added.is_some() && second.added.is_none() && changed.added.is_some());
        // The window still showing the old version keeps it after another
        // lets go.
        assert_eq!(t.release(first.handle), Some(false));
        assert!(t.resolve(second.handle).is_some());
        assert_eq!(t.release(second.handle), Some(true));
        assert!(t.resolve(second.handle).is_none());
        assert_eq!(t.resolve(changed.handle).map(|v| v.width), Some(8));
    }

    // AFileAndAStreamOfTheSameNameAreDifferentImages.
    #[test]
    fn a_file_and_a_stream_of_the_same_name_are_different_images() {
        let mut t = table(ImageBudget::default());
        let stream = t
            .acquire_decoded("p", "icons/sword.png", &[], |_| None, |_| decoded(32))
            .unwrap();
        let file = t
            .acquire_file("p", &file_image("icons/sword.png", "1", 16, 16))
            .unwrap();
        assert_ne!(stream.handle, file.handle);
        assert_eq!(t.count(), 2);
    }

    // AFilesSmallerLevelsCountAgainstTheByteBudget.
    #[test]
    fn a_files_smaller_levels_count_against_the_byte_budget() {
        let image = 16 * 16 * 4;
        let mut t = table(ImageBudget {
            maximum_count: 256,
            maximum_bytes: image + 1,
            maximum_dimension: 2048,
        });
        let refused = t
            .acquire_file("p", &file_image("icons/sword.png", "1", 16, 16))
            .unwrap_err();
        assert!(refused.contains("budget is"), "{refused}");
        assert_eq!(t.count(), 0);
        assert!(
            t.acquire_file("p", &file_image("icons/dot.png", "1", 1, 1))
                .is_ok()
        );
        assert_eq!(t.owned_bytes(), 4);
    }

    // FilesShareTheCountWithThePluginsOtherImages.
    #[test]
    fn files_share_the_count_with_the_plugins_other_images() {
        let mut t = table(ImageBudget {
            maximum_count: 2,
            maximum_bytes: 1024 * 1024,
            maximum_dimension: 2048,
        });
        assert!(t.acquire_rgba("p", "map", 32, 32, pixels(32)).is_ok());
        assert!(
            t.acquire_file("p", &file_image("icons/a.png", "1", 4, 4))
                .is_ok()
        );
        let refused = t
            .acquire_file("p", &file_image("icons/b.png", "1", 4, 4))
            .unwrap_err();
        assert!(refused.contains("2 images"), "{refused}");
    }

    // AFileWiderThanTheMaximumDimensionIsRefused.
    #[test]
    fn a_file_wider_than_the_maximum_dimension_is_refused() {
        let mut t = table(ImageBudget {
            maximum_count: 256,
            maximum_bytes: 1024 * 1024,
            maximum_dimension: 8,
        });
        let refused = t
            .acquire_file("p", &file_image("icons/wide.png", "1", 16, 4))
            .unwrap_err();
        assert!(refused.contains("16x4"), "{refused}");
        assert_eq!(t.count(), 0);
    }

    // The release generation moves on each image let go of completely, and
    // only then (OpenAC `ReleaseGeneration`).
    #[test]
    fn the_release_generation_moves_when_an_image_is_let_go_completely() {
        let mut t = table(ImageBudget::default());
        let a = t.acquire_rgba("p", "a", 2, 2, pixels(2)).unwrap();
        t.acquire_rgba("p", "a", 2, 2, pixels(2)).unwrap();
        assert_eq!(t.release_generation(), 0);
        assert_eq!(t.release(a.handle), Some(false));
        assert_eq!(t.release_generation(), 0);
        assert_eq!(t.release(a.handle), Some(true));
        assert_eq!(t.release_generation(), 1);
        assert_eq!(t.release(a.handle), None);
        assert_eq!(t.release_generation(), 1);
    }
}
