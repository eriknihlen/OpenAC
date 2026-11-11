//! What every session of the process reads movement from: the dats behind
//! a bounded cache, the region's terrain height table, and the motion
//! tables already decoded. Shared read-only (`Send + Sync`); each session
//! keeps its own physics engine built from it.

use std::collections::HashMap;
#[cfg(not(target_arch = "wasm32"))]
use std::path::Path;
use std::sync::{Arc, Mutex, PoisonError};

use ac_dat::CachedDats;
use ac_dat::objects::{Animation, EnumIdMap, MotionTable, Region, Setup};
use ac_motion::{AnimationLoader, AnimationSequencer, MotionTableRules};

/// The region record holding the terrain height table.
const REGION_ID: u32 = 0x1300_0000;

/// Why the movement content could not be opened.
#[derive(Debug, thiserror::Error)]
pub enum WorldContentError {
    /// No dat directory was named and none is installed.
    #[error("no dat directory")]
    NoDatDirectory,
    /// A dat could not be read.
    #[error(transparent)]
    Dat(#[from] ac_dat::DatError),
    /// The portal dat has no region record.
    #[error("the portal dat has no region record (0x{REGION_ID:08X})")]
    MissingRegion,
}

/// The dats, the height table and the decoded motion tables.
#[derive(Debug)]
pub struct WorldContent {
    dats: Arc<CachedDats>,
    height_table: [f32; 256],
    motion_tables: Mutex<HashMap<u32, Option<Arc<MotionTableRules>>>>,
    storage_weenie_class: u32,
}

/// The enum maps' category and key naming the storage chest's weenie
/// class.
const STORAGE_ENUM_CATEGORY: u32 = 0x0C;
const STORAGE_ENUM: u32 = 0x1000_0001;

/// The dat id the dats' enum maps give `key` in `category`: the master
/// map (the portal header's) names the category's map, which names the
/// id; 0 when either is missing.
fn did_by_enum(dats: &CachedDats, category: u32, key: u32) -> Result<u32, ac_dat::DatError> {
    let master_id = dats.uncached().portal.header().master_map_id as u32;
    if master_id == 0 {
        return Ok(0);
    }
    let Some(master) = dats.get::<EnumIdMap>(master_id)? else {
        return Ok(0);
    };
    let Some(map_id) = master.client_id(category).filter(|id| *id != 0) else {
        return Ok(0);
    };
    let Some(map) = dats.get::<EnumIdMap>(map_id)? else {
        return Ok(0);
    };
    Ok(map.client_id(key).unwrap_or(0))
}

/// Animations read through the shared dat cache.
#[derive(Debug)]
struct DatAnimations(Arc<CachedDats>);

impl AnimationLoader for DatAnimations {
    fn load_animation(&self, id: u32) -> Option<Arc<Animation>> {
        self.0.get::<Animation>(id).ok().flatten()
    }
}

impl WorldContent {
    /// Open the dats in `dir` (else the usual install place).
    ///
    /// # Errors
    /// No dat directory, a dat that cannot be read, or no region record.
    // A browser has no dat files to open (and simulates no world).
    #[cfg(not(target_arch = "wasm32"))]
    pub fn open(dir: Option<&Path>) -> Result<Self, WorldContentError> {
        let dir = dir
            .map(Path::to_path_buf)
            .or_else(ac_dat::default_dat_dir)
            .ok_or(WorldContentError::NoDatDirectory)?;
        let dats = CachedDats::open(&dir)?;
        let region = dats
            .get::<Region>(REGION_ID)?
            .ok_or(WorldContentError::MissingRegion)?;
        let storage_weenie_class = did_by_enum(&dats, STORAGE_ENUM_CATEGORY, STORAGE_ENUM)?;
        Ok(Self {
            height_table: region.land_defs.land_height_table,
            dats: Arc::new(dats),
            motion_tables: Mutex::new(HashMap::new()),
            storage_weenie_class,
        })
    }

    /// The weenie class of a house's storage chest, as the dats' enum maps
    /// name it (0 when they do not): an object of this class is a storage
    /// object, put into its cell as it is.
    #[must_use]
    pub fn storage_weenie_class(&self) -> u32 {
        self.storage_weenie_class
    }

    /// The dats.
    #[must_use]
    pub fn dats(&self) -> &CachedDats {
        &self.dats
    }

    /// The terrain height table.
    #[must_use]
    pub fn height_table(&self) -> &[f32; 256] {
        &self.height_table
    }

    /// The region record (terrain types and textures among it), read
    /// through the dat cache; none when it cannot be read.
    #[must_use]
    pub fn region(&self) -> Option<Arc<Region>> {
        self.dats.get::<Region>(REGION_ID).ok().flatten()
    }

    /// A setup.
    #[must_use]
    pub fn setup(&self, id: u32) -> Option<Arc<Setup>> {
        self.dats.get::<Setup>(id).ok().flatten()
    }

    /// The motion table's rules, decoded once per process.
    #[must_use]
    pub fn motion_table(&self, id: u32) -> Option<Arc<MotionTableRules>> {
        let mut tables = self
            .motion_tables
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        tables
            .entry(id)
            .or_insert_with(|| {
                self.dats
                    .get::<MotionTable>(id)
                    .ok()
                    .flatten()
                    .map(|t| Arc::new(MotionTableRules::new(t)))
            })
            .clone()
    }

    /// An animation sequencer for an object with this setup and motion
    /// table; `None` when either is missing.
    #[must_use]
    pub fn sequencer(&self, setup_id: u32, motion_table_id: u32) -> Option<AnimationSequencer> {
        let setup = self.setup(setup_id)?;
        let table = self.motion_table(motion_table_id)?;
        Some(AnimationSequencer::from_setup(
            &setup,
            table,
            Arc::new(DatAnimations(Arc::clone(&self.dats))),
        ))
    }
}
