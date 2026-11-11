//! What the page keeps for the panels, as the panels' own interfaces: the
//! stored values ([`PageStorage`], with the settings file's bytes kept in
//! it by [`KeptSettings`]), the clipboard ([`PageClipboard`]), the dats'
//! surfaces as they arrive ([`ArrivingSurfaces`]), and the rules of what a
//! frame hands over ([`PictureSent`], [`storage_records`], the source
//! numbers of the dats).

use std::cell::RefCell;
use std::collections::HashSet;
use std::io;
use std::rc::Rc;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};

use ac_dat::texture::DecodedTexture;
use ac_dat::{DatCollection, DatSource};
use ac_ui::draw::TextureRef;
use ac_ui::storage::{MemoryUiStorage, Presence, StorageWrite, UiStorage};
use ac_ui_window::ui::batch::{UiBatch, UiRun, UiTexture};
use ac_ui_window::ui::clipboard::Clipboard;
use ac_ui_window::ui::{UiPixels, UiTextureSource};

use crate::output::{Filter, Outputs, Texture, Vertex};

/// The id a dat request names for the dat's own header (the file's first
/// bytes) instead of a file in it.
pub const HEADER_ID: u32 = 0xFFFF_FFFF;

/// How many of a dat file's first bytes the page hands in for its header;
/// the header is the first [`ac_dat::DatHeader::SIZE`] of them.
pub const HEADER_BYTES: usize = 1024;

/// The page's number of a dat: 0 portal, 1 cell, 2 high-resolution,
/// 3 local language.
#[must_use]
pub fn source_number(source: DatSource) -> u32 {
    match source {
        DatSource::Portal => 0,
        DatSource::Cell => 1,
        DatSource::HighRes => 2,
        DatSource::Local => 3,
    }
}

/// The dat of the page's number; `None` for a number that names none.
#[must_use]
pub fn source_of(number: u32) -> Option<DatSource> {
    Some(match number {
        0 => DatSource::Portal,
        1 => DatSource::Cell,
        2 => DatSource::HighRes,
        3 => DatSource::Local,
        _ => return None,
    })
}

/// The four dats in the page's numbering order.
pub const SOURCES: [DatSource; 4] = [
    DatSource::Portal,
    DatSource::Cell,
    DatSource::HighRes,
    DatSource::Local,
];

fn lock(storage: &Mutex<MemoryUiStorage>) -> MutexGuard<'_, MemoryUiStorage> {
    // Every change is one map update, so a poisoned lock still holds a
    // usable store.
    storage.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The values the page keeps, shared by the panels' storage, the settings
/// stores and the module, which takes the writes back out for the page.
#[derive(Debug, Clone, Default)]
pub struct PageStorage(pub Arc<Mutex<MemoryUiStorage>>);

impl PageStorage {
    /// A value the page holds (not handed back: the page has it).
    pub fn insert(&self, key: &str, bytes: &[u8]) {
        lock(&self.0).insert(key, bytes.to_vec());
    }

    /// The writes since the last call, oldest first.
    #[must_use]
    pub fn take_writes(&self) -> Vec<StorageWrite> {
        lock(&self.0).take_writes()
    }
}

impl UiStorage for PageStorage {
    fn get(&self, key: &str) -> io::Result<Vec<u8>> {
        lock(&self.0).get(key)
    }

    fn put(&mut self, key: &str, bytes: &[u8]) -> io::Result<()> {
        lock(&self.0).put(key, bytes)
    }

    fn remove(&mut self, key: &str) -> io::Result<()> {
        lock(&self.0).remove(key)
    }

    fn list(&self, prefix: &str) -> io::Result<Vec<String>> {
        lock(&self.0).list(prefix)
    }

    fn presence(&self, key: &str) -> Option<Presence> {
        lock(&self.0).presence(key)
    }
}

/// A settings file kept in the page's storage under one key: exactly the
/// bytes the native settings file would hold.
#[derive(Debug, Clone)]
pub struct KeptSettings {
    storage: PageStorage,
    key: &'static str,
}

impl KeptSettings {
    /// The settings file under `key` of `storage`.
    #[must_use]
    pub fn new(storage: PageStorage, key: &'static str) -> Self {
        Self { storage, key }
    }
}

impl ac_host::settings::SettingsBytes for KeptSettings {
    fn read(&self) -> io::Result<Option<Vec<u8>>> {
        match lock(&self.storage.0).get(self.key) {
            Ok(bytes) => Ok(Some(bytes)),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error),
        }
    }

    fn write(&self, bytes: &[u8]) -> io::Result<()> {
        lock(&self.storage.0).put(self.key, bytes)
    }
}

/// The storage writes as the page reads them: a value, or a removal as the
/// reserved record.
pub fn storage_records(out: &mut Outputs, writes: Vec<StorageWrite>) {
    for write in writes {
        match write {
            StorageWrite::Put { key, bytes } => out.storage_write(&key, &bytes),
            StorageWrite::Remove { key } => out.storage_remove(&key),
        }
    }
}

/// What the text fields asked of the clipboard since the module last
/// looked.
#[derive(Debug, Default)]
pub struct ClipboardState {
    /// Text copied or cut, for the page's clipboard (the last wins).
    pub copied: Option<String>,
    /// A paste was asked for: the page answers with its clipboard's text.
    pub paste_wanted: bool,
}

/// The page's clipboard as the panels see it: a copy goes to the page, a
/// paste is asked of the page and arrives later as typed text.
#[derive(Debug, Clone, Default)]
pub struct PageClipboard(pub Rc<RefCell<ClipboardState>>);

impl Clipboard for PageClipboard {
    fn text(&mut self) -> Result<String, Option<String>> {
        self.0.borrow_mut().paste_wanted = true;
        // Answered later, by the page.
        Err(None)
    }

    fn set_text(&mut self, text: &str) -> Result<(), Option<String>> {
        self.0.borrow_mut().copied = Some(text.to_owned());
        Ok(())
    }
}

/// The panels' textures while their dat files arrive: a surface whose
/// decode asked for files the page has not handed in yet draws nothing
/// (one clear texel) instead of the missing-surface placeholder, and is
/// named so it can be read again once they are in.
#[derive(Debug)]
pub struct ArrivingSurfaces<'a, S> {
    /// Where the pixels come from.
    pub inner: &'a mut S,
    /// The dats the decode reads, asked what it wanted.
    pub dats: &'a DatCollection,
    /// The files the decodes wanted, to ask the page for.
    pub wanted: &'a mut Vec<(DatSource, u32)>,
    /// Textures drawn without their files yet.
    pub waiting: &'a mut HashSet<TextureRef>,
}

impl<S: UiTextureSource> UiTextureSource for ArrivingSurfaces<'_, S> {
    fn decode(&mut self, texture: TextureRef) -> Option<UiPixels> {
        let pixels = self.inner.decode(texture);
        let wanted = self.dats.take_wanted();
        if wanted.is_empty() {
            return pixels;
        }
        self.wanted.extend(wanted);
        self.waiting.insert(texture);
        Some(clear_texel())
    }

    fn released(&mut self) -> Vec<TextureRef> {
        self.inner.released()
    }
}

/// One fully transparent texel.
#[must_use]
pub fn clear_texel() -> UiPixels {
    UiPixels::Rgba(DecodedTexture {
        width: 1,
        height: 1,
        rgba: vec![0; 4],
    })
}

/// A source that reads dat surfaces only: for a frame drawn to learn what
/// it needs, without taking the composed images or the releases the real
/// frames hand over.
#[derive(Debug)]
pub struct SurfacesOnly<'a, S>(pub &'a mut S);

impl<S: UiTextureSource> UiTextureSource for SurfacesOnly<'_, S> {
    fn decode(&mut self, texture: TextureRef) -> Option<UiPixels> {
        match texture {
            TextureRef::Surface(_) => self.0.decode(texture),
            TextureRef::Runtime(_) => None,
        }
    }
}

/// A run as the page reads it.
type PageRun = (Texture, Filter, u32, u32);

/// The picture last handed over: a picture goes out only when it differs
/// from it, or when one is owed (the first frame, after a resize, after the
/// page lost its textures).
#[derive(Debug, Default)]
pub struct PictureSent {
    last: Option<(Vec<Vertex>, Vec<PageRun>)>,
}

impl PictureSent {
    /// The next picture goes out whatever it is.
    pub fn owe(&mut self) {
        self.last = None;
    }

    /// Hand over `vertices` and `runs` unless they are what was last
    /// handed over; true when they went out.
    pub fn offer(&mut self, vertices: Vec<Vertex>, runs: Vec<PageRun>, out: &mut Outputs) -> bool {
        if self
            .last
            .as_ref()
            .is_some_and(|(v, r)| *v == vertices && *r == runs)
        {
            return false;
        }
        out.picture(&vertices, &runs);
        self.last = Some((vertices, runs));
        true
    }
}

/// A batch and the runs of it that draw, as the page reads them.
#[must_use]
pub fn picture_of(batch: &UiBatch, runs: &[UiRun]) -> (Vec<Vertex>, Vec<PageRun>) {
    let vertices = batch
        .vertices
        .iter()
        .map(|v| Vertex {
            position: v.position,
            uv: v.uv,
            color: v.color,
        })
        .collect();
    let runs = runs
        .iter()
        .map(|run| {
            (
                texture_of(run.key.texture),
                match run.key.filter {
                    ac_ui::draw::Filter::Linear => Filter::Linear,
                    ac_ui::draw::Filter::Nearest => Filter::Nearest,
                },
                run.first,
                run.count,
            )
        })
        .collect();
    (vertices, runs)
}

/// The page's name of a texture.
#[must_use]
pub fn texture_of(texture: UiTexture) -> Texture {
    match texture {
        UiTexture::White => Texture::White,
        UiTexture::Named(TextureRef::Surface(id)) => Texture::Surface(id),
        UiTexture::Named(TextureRef::Runtime(id)) => Texture::Runtime(id),
    }
}

/// The page's name of a named texture.
#[must_use]
pub fn texture_ref(texture: TextureRef) -> Texture {
    texture_of(UiTexture::Named(texture))
}

#[cfg(test)]
mod tests {
    use ac_host::settings::{SettingsBytes, SettingsStore, UiWindowPlacement};

    use super::*;
    use crate::output::kind;

    #[test]
    fn the_page_numbers_the_dats_portal_cell_high_resolution_local() {
        for (number, source) in SOURCES.iter().enumerate() {
            let number = number as u32;
            assert_eq!(source_number(*source), number);
            assert_eq!(source_of(number), Some(*source));
        }
        assert_eq!(source_of(4), None);
    }

    #[test]
    fn writes_go_out_as_records_and_a_removal_as_the_reserved_one() {
        let storage = PageStorage::default();
        storage.insert("keymaps/old.keymap", b"x");
        let mut window: Box<dyn UiStorage> = Box::new(storage.clone());
        assert_eq!(window.get("keymaps/old.keymap").unwrap(), b"x");
        assert!(storage.take_writes().is_empty(), "what the page put in");
        window.put("keybinds.json", b"{}").unwrap();
        window.remove("keymaps/old.keymap").unwrap();
        let mut out = Outputs::default();
        storage_records(&mut out, storage.take_writes());
        let mut expected = Outputs::default();
        expected.storage_write("keybinds.json", b"{}");
        expected.storage_remove("keymaps/old.keymap");
        assert_eq!(
            out.take(kind::STORAGE_WRITES),
            expected.take(kind::STORAGE_WRITES)
        );
    }

    #[test]
    fn a_settings_file_kept_in_the_storage_holds_the_native_files_bytes() {
        let storage = PageStorage::default();
        let kept = KeptSettings::new(storage.clone(), "window-layouts/settings.json");
        assert_eq!(kept.read().unwrap(), None);
        let store = SettingsStore::with_bytes("page", Arc::new(kept));
        let placement = UiWindowPlacement {
            layout: ac_host::settings::UiWindowLayout::default(),
            screen_width: 800,
            screen_height: 600,
        };
        store
            .save_window_placement("Someone", "chat", &placement)
            .unwrap();
        let writes = storage.take_writes();
        let [StorageWrite::Put { key, bytes }] = writes.as_slice() else {
            panic!("one write: {writes:?}");
        };
        assert_eq!(key, "window-layouts/settings.json");
        let text = String::from_utf8(bytes.clone()).unwrap();
        assert!(text.contains("\"Someone\""), "{text}");
    }

    #[test]
    fn a_picture_goes_out_when_it_changed_or_is_owed() {
        let corner = Vertex {
            position: [1.0, 2.0],
            uv: [0.0, 0.0],
            color: [1.0; 4],
        };
        let runs = vec![(Texture::White, Filter::Linear, 0, 3)];
        let mut sent = PictureSent::default();
        let mut out = Outputs::default();
        assert!(sent.offer(vec![corner; 3], runs.clone(), &mut out));
        out.take(kind::VERTICES);
        out.take(kind::RUNS);
        assert!(!sent.offer(vec![corner; 3], runs.clone(), &mut out));
        assert_eq!(out.mask(), 0, "the last picture stands");
        let moved = Vertex {
            position: [1.5, 2.0],
            ..corner
        };
        assert!(sent.offer(vec![moved; 3], runs.clone(), &mut out));
        out.take(kind::VERTICES);
        out.take(kind::RUNS);
        sent.owe();
        assert!(sent.offer(vec![moved; 3], runs, &mut out), "owed again");
    }

    #[test]
    fn a_paste_is_asked_of_the_page_and_a_copy_handed_to_it() {
        let state = PageClipboard::default();
        let mut clipboard: Box<dyn Clipboard> = Box::new(state.clone());
        assert_eq!(clipboard.text(), Err(None));
        clipboard.set_text("first").unwrap();
        clipboard.set_text("last").unwrap();
        let state = state.0.borrow();
        assert!(state.paste_wanted);
        assert_eq!(state.copied.as_deref(), Some("last"));
    }
}
