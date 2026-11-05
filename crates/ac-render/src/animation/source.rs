//! Where the window's animation reads Setups, motion tables and animations
//! from: the process's dats behind their object caches, with the decoded
//! motion-table rules kept per window.

use std::collections::HashMap;
use std::sync::Arc;

use ac_dat::objects::{Animation, MotionTable, Setup};
use ac_motion::{AnimationLoader, MotionTableRules};

use crate::content::Content;

/// The dat records an animated object needs. Implementations cache; a
/// missing record is `None` (the object then keeps its resting pose).
pub trait AnimationSource {
    /// A Setup (`0x02......`).
    fn setup(&self, id: u32) -> Option<Arc<Setup>>;
    /// A motion table's rules (`0x09......`).
    fn motion_table(&mut self, id: u32) -> Option<Arc<MotionTableRules>>;
    /// The rules of an empty motion table: objects that only play their
    /// Setup's default animation.
    fn empty_motion_table(&mut self) -> Arc<MotionTableRules>;
    /// Resolves animations for the sequencers.
    fn animations(&self) -> Arc<dyn AnimationLoader + Send + Sync>;
}

/// A motion table with nothing in it.
#[must_use]
pub fn empty_table() -> MotionTable {
    MotionTable {
        id: 0,
        default_style: 0,
        style_defaults: Vec::new(),
        cycles: Vec::new(),
        modifiers: Vec::new(),
        links: Vec::new(),
    }
}

/// Animations read through the process's dat cache.
#[derive(Debug)]
struct ContentAnimations(Arc<Content>);

impl AnimationLoader for ContentAnimations {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        self.0.dats.get::<Animation>(id).ok().flatten()
    }
}

/// [`AnimationSource`] over the process's dats.
pub struct ContentAnimationSource {
    content: Arc<Content>,
    loader: Arc<dyn AnimationLoader + Send + Sync>,
    tables: HashMap<u32, Option<Arc<MotionTableRules>>>,
    empty: Arc<MotionTableRules>,
}

impl std::fmt::Debug for ContentAnimationSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ContentAnimationSource")
            .field("tables", &self.tables.len())
            .finish_non_exhaustive()
    }
}

impl ContentAnimationSource {
    /// A source over `content`.
    #[must_use]
    pub fn new(content: Arc<Content>) -> Self {
        Self {
            loader: Arc::new(ContentAnimations(Arc::clone(&content))),
            content,
            tables: HashMap::new(),
            empty: Arc::new(MotionTableRules::new(Arc::new(empty_table()))),
        }
    }
}

impl AnimationSource for ContentAnimationSource {
    fn setup(&self, id: u32) -> Option<Arc<Setup>> {
        if id >> 24 != 0x02 {
            return None;
        }
        self.content.dats.get::<Setup>(id).ok().flatten()
    }

    fn motion_table(&mut self, id: u32) -> Option<Arc<MotionTableRules>> {
        let content = &self.content;
        self.tables
            .entry(id)
            .or_insert_with(|| {
                content
                    .dats
                    .get::<MotionTable>(id)
                    .ok()
                    .flatten()
                    .map(|t| Arc::new(MotionTableRules::new(t)))
            })
            .clone()
    }

    fn empty_motion_table(&mut self) -> Arc<MotionTableRules> {
        Arc::clone(&self.empty)
    }

    fn animations(&self) -> Arc<dyn AnimationLoader + Send + Sync> {
        Arc::clone(&self.loader)
    }
}
