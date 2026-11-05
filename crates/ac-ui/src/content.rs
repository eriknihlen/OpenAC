//! UI content read from the dats: layouts (with the master property
//! table), fonts, strings and sprite sizes, each loaded once.

use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::Arc;

use ac_dat::objects::{
    Font, LayoutDesc, MASTER_PROPERTY_ID, MasterProperty, RenderSurface, StringInfo, StringTable,
};
use ac_dat::{DatCollection, DatError};

use crate::draw::SpriteSizes;
use crate::font::UiFont;
use crate::import::LayoutSource;
use crate::strings::StringResolver;

/// A dat read that failed (as opposed to an object that is not there).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContentError {
    /// What was being read.
    pub what: &'static str,
    /// Its id.
    pub id: u32,
    /// The error.
    pub message: String,
}

/// UI content from a dat collection. Single-threaded (the UI runs on the
/// window thread); caches with interior mutability.
pub struct DatContent {
    dats: Arc<DatCollection>,
    master: Arc<MasterProperty>,
    layouts: RefCell<HashMap<u32, Option<Arc<LayoutDesc>>>>,
    fonts: RefCell<HashMap<u32, Option<Arc<UiFont>>>>,
    sprites: RefCell<HashMap<u32, Option<(u32, u32)>>>,
    strings: StringResolver,
    errors: Rc<RefCell<Vec<ContentError>>>,
}

impl std::fmt::Debug for DatContent {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DatContent")
            .field("layouts", &self.layouts.borrow().len())
            .field("fonts", &self.fonts.borrow().len())
            .field("sprites", &self.sprites.borrow().len())
            .finish_non_exhaustive()
    }
}

/// Opening the content failed.
#[derive(Debug, thiserror::Error)]
pub enum ContentOpenError {
    /// The master property table could not be read.
    #[error("cannot read the master property table: {0}")]
    Dat(#[from] DatError),
    /// The portal dat has no master property table.
    #[error("the portal dat has no master property table 39000001")]
    NoMasterProperty,
}

impl DatContent {
    /// Content over `dats`; reads the master property table now.
    pub fn new(dats: Arc<DatCollection>) -> Result<Self, ContentOpenError> {
        let master: MasterProperty = dats
            .get(MASTER_PROPERTY_ID)?
            .ok_or(ContentOpenError::NoMasterProperty)?;
        let errors: Rc<RefCell<Vec<ContentError>>> = Rc::default();
        let table_dats = dats.clone();
        let table_errors = errors.clone();
        let strings = StringResolver::new(move |id| match table_dats.get::<StringTable>(id) {
            Ok(t) => t,
            Err(e) => {
                table_errors.borrow_mut().push(ContentError {
                    what: "string table",
                    id,
                    message: e.to_string(),
                });
                None
            }
        });
        Ok(Self {
            dats,
            master: Arc::new(master),
            layouts: RefCell::default(),
            fonts: RefCell::default(),
            sprites: RefCell::default(),
            strings,
            errors,
        })
    }

    /// The dats.
    #[must_use]
    pub fn dats(&self) -> &Arc<DatCollection> {
        &self.dats
    }

    /// The master property table.
    #[must_use]
    pub fn master_property(&self) -> &Arc<MasterProperty> {
        &self.master
    }

    /// The string resolver.
    #[must_use]
    pub fn strings(&self) -> &StringResolver {
        &self.strings
    }

    /// Reads that failed so far (missing objects are not errors), oldest
    /// first; the list is emptied.
    pub fn take_errors(&self) -> Vec<ContentError> {
        std::mem::take(&mut self.errors.borrow_mut())
    }

    fn record(&self, what: &'static str, id: u32, e: &DatError) {
        self.errors.borrow_mut().push(ContentError {
            what,
            id,
            message: e.to_string(),
        });
    }
}

impl LayoutSource for DatContent {
    fn layout(&self, id: u32) -> Option<Arc<LayoutDesc>> {
        if let Some(l) = self.layouts.borrow().get(&id) {
            return l.clone();
        }
        let l = match LayoutDesc::read(&self.dats.local, &self.master, id) {
            Ok(l) => l.map(Arc::new),
            Err(e) => {
                self.record("layout", id, &e);
                None
            }
        };
        self.layouts.borrow_mut().insert(id, l.clone());
        l
    }

    fn font(&self, id: u32) -> Option<Arc<UiFont>> {
        if let Some(f) = self.fonts.borrow().get(&id) {
            return f.clone();
        }
        let f = match self.dats.get::<Font>(id) {
            Ok(Some(font)) => UiFont::from_dat(&font, |s| self.sprite_size(s)).map(Arc::new),
            Ok(None) => None,
            Err(e) => {
                self.record("font", id, &e);
                None
            }
        };
        self.fonts.borrow_mut().insert(id, f.clone());
        f
    }

    fn string(&self, info: &StringInfo) -> Option<String> {
        self.strings.resolve_info(info)
    }
}

impl SpriteSizes for DatContent {
    fn sprite_size(&self, file: u32) -> Option<(u32, u32)> {
        if file == 0 {
            return None;
        }
        if let Some(s) = self.sprites.borrow().get(&file) {
            return *s;
        }
        let s = match self.dats.get::<RenderSurface>(file) {
            // A JPEG surface's header says 0 x 0: its size is the image's.
            Ok(Some(rs)) if rs.width == 0 || rs.height == 0 => {
                ac_dat::texture::decode(&rs, None, ac_dat::texture::DecodeOptions::default())
                    .ok()
                    .map(|d| (d.width, d.height))
            }
            Ok(rs) => rs.map(|rs| (rs.width, rs.height)),
            Err(e) => {
                self.record("render surface", file, &e);
                None
            }
        };
        self.sprites.borrow_mut().insert(file, s);
        s
    }
}
