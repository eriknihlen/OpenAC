//! The loader thread: reads and prepares everything the window draws, so
//! the window thread only uploads finished data.
//!
//! The thread reads the process's shared [`Content`] (opening it on first
//! use; dat reads go through `&self` and any thread may read). It first
//! sends the window icon and the terrain atlas, then answers requests for
//! landblocks and models in order. Every model, GfxObj and surface is sent
//! once, before anything that refers to it, so a model that has arrived has
//! all its parts. Helper threads build landblock jobs ahead and warm the
//! caches for the models next in line; the loader thread alone sends, in
//! the order one thread would. Dropping the [`Loader`] stops the thread
//! (and its helpers) after the item it is working on and waits for it to
//! end.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread::{self, JoinHandle};

use ac_audio::ambient::AmbientRegion;
use ac_core::lighting::LightInfoSource;
use ac_core::sky::LoadedSkyDesc;
use ac_dat::objects::Region;
use ac_dat::texture::DecodedTexture;
use ac_physics::PhysicsEngine;
use glam::Vec3;

use crate::appearance::AppearanceKey;
use crate::appearance_residency::{SentLooks, SurfaceVariantKey};
use crate::assets::{
    self, GfxObjData, LandblockData, NearData, REGION_ID, SurfaceData, TerrainAtlasData,
};
use crate::content::{Content, ContentSlot};
use crate::meshing::MeshRef;
use crate::texture_detail::WorldTextureDetail;
use crate::world_frame::LandblockCoord;

/// Something the window wants prepared.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LoadRequest {
    /// A landblock's terrain; `near`: the landblock is in the near tier,
    /// whose work goes ahead of the far tier's.
    Landblock {
        /// The landblock.
        coord: LandblockCoord,
        /// In the near tier.
        near: bool,
    },
    /// A landblock's near-tier content: statics, scenery, buildings and
    /// interior cells (and their models).
    Near(LandblockCoord),
    /// A model (GfxObj or Setup) and its parts.
    Model(u32),
    /// A model drawn with an appearance description.
    Appearance(Box<AppearanceKey>),
    /// A GfxObj the window lost (it was released and is wanted again).
    GfxObj(u32),
    /// A surface the window lost.
    Surface(u32),
    /// A particle model, at full texture detail.
    EffectGfxObj(u32),
    /// A particle texture, at full detail.
    EffectSurface(u32),
    /// The camera's collision around a landblock (it and its neighbours).
    CameraCollision(LandblockCoord),
    /// The world's texture detail changed: the terrain atlas and every world
    /// surface already sent come again at the new detail (the sky's and the
    /// particles' keep their full detail), and everything sent from now on
    /// is made at it.
    TextureDetail(WorldTextureDetail),
    /// The window released these: send them again when next needed.
    Forget {
        /// Model ids.
        models: Vec<u32>,
        /// GfxObj ids.
        gfx_objs: Vec<u32>,
        /// Surface ids and recoloured surface handles.
        surfaces: Vec<u32>,
        /// Appearances.
        appearances: Vec<AppearanceKey>,
        /// Variant ids.
        variants: Vec<u32>,
    },
}

/// Something the loader prepared.
#[derive(Debug)]
pub enum Loaded {
    /// The dats could not be opened or the region read; nothing else follows.
    Failed(String),
    /// The window icon image (sent first; absent when it cannot be read).
    Icon(Arc<DecodedTexture>),
    /// The terrain texture arrays and blend tables.
    Atlas(Box<TerrainAtlasData>),
    /// A landblock.
    Landblock(Box<LandblockData>),
    /// The cell dat has no such landblock.
    NoLandblock(LandblockCoord),
    /// A landblock's near-tier content (the surfaces of its cell meshes
    /// were sent before it; its models follow).
    Near(LandblockCoord, Box<NearData>),
    /// A surface texture.
    Surface(Box<SurfaceData>),
    /// A GfxObj mesh (its surfaces were sent before it).
    GfxObj(Box<GfxObjData>),
    /// A model's parts (their GfxObjs were sent before it); `None` when the
    /// model does not exist.
    Model {
        /// Model id.
        id: u32,
        /// Parts in their resting pose.
        parts: Option<Vec<MeshRef>>,
        /// The box around its parts and its lights.
        info: ModelInfo,
    },
    /// A part's surface substitutions (their surfaces were sent before
    /// it): each listed surface is drawn as the surface sent under the
    /// paired handle.
    Variant {
        /// Variant id (never 0).
        id: u32,
        /// (surface id, handle), sorted by surface id.
        surfaces: Vec<(u32, u32)>,
    },
    /// A model drawn with an appearance (its GfxObjs, surfaces and variants
    /// were sent before it); `None` when the model does not exist.
    Appearance {
        /// The appearance.
        key: Box<AppearanceKey>,
        /// Parts in their resting pose, each with its variant.
        parts: Option<Vec<MeshRef>>,
        /// The box around its parts, model-local.
        bounds: Option<(Vec3, Vec3)>,
    },
    /// The camera's collision around `center` (`Err`: why it could not be
    /// built).
    CameraCollision {
        /// The landblock whose corner is the engine's origin.
        center: LandblockCoord,
        /// The engine.
        engine: Result<Box<PhysicsEngine>, String>,
    },
    /// The region's sky, ambient sounds and calendar origin (sent after
    /// the terrain atlas; absent when the region has no sky).
    Region {
        /// The sky.
        sky: Option<Box<LoadedSkyDesc>>,
        /// The ambient sound tables.
        ambient: Box<AmbientRegion>,
        /// Calendar origin, ticks.
        zero_time_of_year: f64,
    },
}

impl Loaded {
    /// What adopting it costs the frame's streaming work budget: a finished
    /// landblock (terrain or near content, or its absence) is one completion;
    /// its retained data counts as adopted CPU bytes, its placements as
    /// entity operations, and every mesh and texture as GPU upload bytes.
    #[must_use]
    pub fn work_cost(&self) -> crate::work_budget::StreamingWorkCost {
        use crate::work_budget::StreamingWorkCost;
        fn bytes<T>(items: &[T]) -> i64 {
            i64::try_from(std::mem::size_of_val(items)).unwrap_or(i64::MAX)
        }
        fn count(n: usize) -> i32 {
            i32::try_from(n).unwrap_or(i32::MAX)
        }
        fn gfx_bytes(gfx: &GfxObjData) -> i64 {
            bytes(&gfx.vertices).saturating_add(bytes(&gfx.indices))
        }
        match self {
            Loaded::Landblock(block) => {
                let terrain = bytes(&block.vertices);
                StreamingWorkCost {
                    completion_admissions: 1,
                    adopted_cpu_bytes: terrain,
                    gpu_upload_bytes: terrain,
                    ..StreamingWorkCost::default()
                }
            }
            Loaded::NoLandblock(_) => StreamingWorkCost {
                completion_admissions: 1,
                ..StreamingWorkCost::default()
            },
            Loaded::Near(_, near) => {
                let cell_meshes: i64 = near
                    .cells
                    .iter()
                    .filter_map(|c| c.mesh.as_ref())
                    .map(gfx_bytes)
                    .sum();
                let placements = near.statics.len()
                    + near.scenery.len()
                    + near.buildings.len()
                    + near
                        .cells
                        .iter()
                        .map(|c| 1 + c.statics.len())
                        .sum::<usize>();
                StreamingWorkCost {
                    completion_admissions: 1,
                    adopted_cpu_bytes: bytes(&near.statics)
                        .saturating_add(bytes(&near.scenery))
                        .saturating_add(cell_meshes),
                    entity_operations: count(placements),
                    gpu_upload_bytes: cell_meshes,
                    ..StreamingWorkCost::default()
                }
            }
            Loaded::Surface(surface) => StreamingWorkCost {
                gpu_upload_bytes: i64::try_from(surface.texture.gpu_bytes()).unwrap_or(i64::MAX),
                ..StreamingWorkCost::default()
            },
            Loaded::GfxObj(gfx) => StreamingWorkCost {
                gpu_upload_bytes: gfx_bytes(gfx),
                ..StreamingWorkCost::default()
            },
            // Adopting an atlas makes its textures; its pieces are
            // charged as they are written, a frame's budget at a time.
            Loaded::Model { .. } | Loaded::Appearance { .. } => StreamingWorkCost {
                entity_operations: 1,
                ..StreamingWorkCost::default()
            },
            Loaded::Failed(_)
            | Loaded::Atlas(_)
            | Loaded::Icon(_)
            | Loaded::Variant { .. }
            | Loaded::CameraCollision { .. }
            | Loaded::Region { .. } => StreamingWorkCost::default(),
        }
    }

    /// Its stage name in the budget's diagnostics.
    #[must_use]
    pub fn stage(&self) -> &'static str {
        match self {
            Loaded::Failed(_) => "failed",
            Loaded::Icon(_) => "icon",
            Loaded::Atlas(_) => "terrain atlas",
            Loaded::Landblock(_) => "terrain",
            Loaded::NoLandblock(_) => "no landblock",
            Loaded::Near(..) => "near content",
            Loaded::Surface(_) => "surface",
            Loaded::GfxObj(_) => "mesh",
            Loaded::Model { .. } => "model",
            Loaded::Variant { .. } => "variant",
            Loaded::Appearance { .. } => "appearance",
            Loaded::CameraCollision { .. } => "camera collision",
            Loaded::Region { .. } => "region",
        }
    }
}

/// What the renderer keeps about a model besides its parts.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ModelInfo {
    /// The box around its parts, model-local.
    pub bounds: Option<(Vec3, Vec3)>,
    /// Its lights (a setup's).
    pub lights: Vec<LightInfoSource>,
}

/// Handle to the loader thread.
#[derive(Debug)]
pub struct Loader {
    requests: Option<Sender<LoadRequest>>,
    results: Receiver<Loaded>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

/// Helper threads a loader builds with: the machine's threads less two,
/// 1 to 8 (OpenAC's landblock build pool size).
#[must_use]
pub fn default_worker_count() -> usize {
    thread::available_parallelism()
        .map_or(1, std::num::NonZeroUsize::get)
        .saturating_sub(2)
        .clamp(1, 8)
}

impl Loader {
    /// Start a loader reading the process's content, with
    /// [`default_worker_count`] helper threads. `block_compression`: the
    /// GPU samples block-compressed textures, so those that may stay
    /// compressed are sent as stored.
    #[must_use]
    pub fn spawn(
        content: Arc<ContentSlot>,
        detail: WorldTextureDetail,
        block_compression: bool,
    ) -> Self {
        Self::spawn_with_workers(content, detail, block_compression, default_worker_count())
    }

    /// Start a loader with `workers` helper threads: they build landblock
    /// jobs ahead (nearest tier first, at most `workers` at a time) and
    /// decode the models waiting next; the loader thread itself sends
    /// everything, in the order one thread would. 0 builds everything on
    /// the loader thread.
    #[must_use]
    pub fn spawn_with_workers(
        content: Arc<ContentSlot>,
        detail: WorldTextureDetail,
        block_compression: bool,
        workers: usize,
    ) -> Self {
        let (requests, request_rx) = mpsc::channel();
        let (result_tx, results) = mpsc::channel();
        let stop = Arc::new(AtomicBool::new(false));
        let thread_stop = Arc::clone(&stop);
        let spawned = thread::Builder::new()
            .name("render-loader".into())
            .spawn(move || {
                run(
                    &content,
                    detail,
                    block_compression,
                    workers,
                    &request_rx,
                    &result_tx,
                    &thread_stop,
                );
            });
        match spawned {
            Ok(thread) => Self {
                requests: Some(requests),
                results,
                stop,
                thread: Some(thread),
            },
            Err(error) => {
                // No thread: report through a channel nobody else writes to.
                let (tx, rx) = mpsc::channel();
                let _ = tx.send(Loaded::Failed(format!("cannot start the loader: {error}")));
                Self {
                    requests: Some(requests),
                    results: rx,
                    stop,
                    thread: None,
                }
            }
        }
    }

    /// Ask for something; answered later through [`Loader::try_next`].
    pub fn request(&self, request: LoadRequest) {
        if let Some(requests) = &self.requests {
            // A stopped loader has already reported why.
            let _ = requests.send(request);
        }
    }

    /// The next finished item, if one is ready.
    #[must_use]
    pub fn try_next(&self) -> Option<Loaded> {
        self.results.try_recv().ok()
    }
}

impl Drop for Loader {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        // Closing the request channel wakes a loader waiting for work.
        self.requests = None;
        if let Some(thread) = self.thread.take() {
            // A loader that panicked has nothing left to release.
            let _ = thread.join();
        }
    }
}

struct Worker<'a> {
    content: Arc<Content>,
    out: &'a Sender<Loaded>,
    stop: &'a AtomicBool,
    models: HashSet<u32>,
    gfx_objs: HashSet<u32>,
    /// Each GfxObj's box, model-local.
    gfx_bounds: HashMap<u32, (Vec3, Vec3)>,
    surfaces: SentSurfaces,
    /// Appearances, their variants and recoloured surfaces sent.
    looks: SentLooks,
    /// The world's texture detail.
    detail: WorldTextureDetail,
    /// Block-compressed textures may stay compressed (the GPU samples them).
    block_compression: bool,
    /// World surfaces the helpers prepared ahead (at the detail they hold).
    prepared: Arc<PreparedSurfaces>,
    /// The sky's models, whose textures keep their full detail.
    full_detail_models: HashSet<u32>,
}

/// Work the loader queues between requests.
/// One landblock job.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum BlockWork {
    /// Its terrain.
    Terrain(LandblockCoord),
    /// Its near-tier content.
    Near(LandblockCoord),
}

impl BlockWork {
    fn coord(self) -> LandblockCoord {
        match self {
            Self::Terrain(c) | Self::Near(c) => c,
        }
    }
}

/// Landblock jobs waiting: the near tier's go first, each tier in the
/// order asked; a near job takes a waiting far job of the same landblock
/// along with it.
#[derive(Debug, Default)]
struct BlockQueue {
    near: VecDeque<BlockWork>,
    far: VecDeque<BlockWork>,
}

impl BlockQueue {
    fn push(&mut self, work: BlockWork, near: bool) {
        if !near {
            self.far.push_back(work);
            return;
        }
        let coord = work.coord();
        let mut i = 0;
        while i < self.far.len() {
            if self.far[i].coord() == coord {
                if let Some(taken) = self.far.remove(i) {
                    self.near.push_back(taken);
                }
            } else {
                i += 1;
            }
        }
        if !self.near.contains(&work) {
            self.near.push_back(work);
        }
    }

    fn pop(&mut self) -> Option<BlockWork> {
        self.near.pop_front().or_else(|| self.far.pop_front())
    }

    fn is_empty(&self) -> bool {
        self.near.is_empty() && self.far.is_empty()
    }
}

/// Landblock jobs waiting and being built. Jobs leave the queue in its
/// order (near tier first), at most `capacity` at a time to helper
/// threads, and their results are handed back in the order the jobs left,
/// whichever finishes first: the window receives what one thread would
/// have sent. A near job asked while far jobs are being built overtakes
/// every far job still waiting, not the ones already building.
struct BlockPipeline<R> {
    queue: BlockQueue,
    building: VecDeque<(BlockWork, Receiver<R>)>,
}

impl<R> Default for BlockPipeline<R> {
    fn default() -> Self {
        Self {
            queue: BlockQueue::default(),
            building: VecDeque::new(),
        }
    }
}

impl<R> BlockPipeline<R> {
    fn push(&mut self, work: BlockWork, near: bool) {
        self.queue.push(work, near);
    }

    fn is_empty(&self) -> bool {
        self.queue.is_empty() && self.building.is_empty()
    }

    /// The next job to send, with its result when a helper built it
    /// (`None`: build it here). Hands waiting jobs to `dispatch` first,
    /// until `capacity` are being built; capacity 0 builds nothing ahead.
    /// Waits for the oldest job being built.
    fn next(
        &mut self,
        capacity: usize,
        mut dispatch: impl FnMut(BlockWork) -> Receiver<R>,
    ) -> Option<(BlockWork, Option<R>)> {
        while self.building.len() < capacity {
            let Some(work) = self.queue.pop() else {
                break;
            };
            self.building.push_back((work, dispatch(work)));
        }
        match self.building.pop_front() {
            // A helper that failed dropped the reply: build it here.
            Some((work, result)) => Some((work, result.recv().ok())),
            None => self.queue.pop().map(|work| (work, None)),
        }
    }
}

/// A landblock job's result.
enum Built {
    /// A landblock's terrain; `None`: the cell dat has no such landblock.
    Terrain(LandblockCoord, Option<LandblockData>),
    /// A landblock's near-tier content.
    Near(LandblockCoord, NearData),
}

/// What a landblock job and a model warm-up read.
struct BuildContext {
    content: Arc<Content>,
    block_compression: bool,
    prepared: Arc<PreparedSurfaces>,
    region: Arc<Region>,
    height_table: [f32; 256],
    blending: ac_core::terrain::TerrainBlendingContext,
}

impl BuildContext {
    /// Build a landblock job. `surface_cache`: the terrain blend recipes
    /// built so far (a pure memo, one per thread).
    fn build(
        &self,
        work: BlockWork,
        surface_cache: &mut HashMap<u32, ac_core::terrain::SurfaceInfo>,
    ) -> Built {
        match work {
            BlockWork::Terrain(coord) => Built::Terrain(
                coord,
                assets::build_landblock(
                    &self.content,
                    coord,
                    &self.height_table,
                    &self.blending,
                    surface_cache,
                ),
            ),
            BlockWork::Near(coord) => Built::Near(
                coord,
                assets::build_near(&self.content, coord, &self.region),
            ),
        }
    }

    /// As [`BuildContext::build`], then warm the textures of a near job's
    /// cell meshes, which the loader sends ahead of it.
    fn build_and_warm(
        &self,
        work: BlockWork,
        surface_cache: &mut HashMap<u32, ac_core::terrain::SurfaceInfo>,
    ) -> Built {
        let built = self.build(work, surface_cache);
        if let Built::Near(_, near) = &built {
            for sub in near
                .cells
                .iter()
                .flat_map(|c| c.mesh.iter().flat_map(|m| &m.sub_meshes))
            {
                self.warm_surface(sub.surface_id);
            }
        }
        built
    }

    /// Read and decode what sending a model needs (its setup, GfxObjs with
    /// their level-of-detail ladders, surfaces and textures) into the
    /// content's caches, so the loader thread finds them there. Nothing
    /// is sent from here.
    fn warm_model(&self, id: u32) {
        let mut gfx: Vec<u32> = assets::model_parts(&self.content, id)
            .into_iter()
            .flatten()
            .map(|p| p.gfx_obj_id)
            .collect();
        let _ = assets::model_lights(&self.content, id);
        let mut seen = HashSet::new();
        while let Some(g) = gfx.pop() {
            if !seen.insert(g) {
                continue;
            }
            let Some(mesh) = assets::build_gfx_obj(&self.content, g) else {
                continue;
            };
            for sub in &mesh.sub_meshes {
                self.warm_surface(sub.surface_id);
            }
            gfx.extend(
                mesh.degrades
                    .iter()
                    .map(|l| l.gfx_obj_id)
                    .filter(|&l| l != 0),
            );
        }
    }
}

impl BuildContext {
    /// Prepare a world surface (decoded or kept compressed, shrunk to the
    /// texture detail, its mip chain built) for the loader to take, unless
    /// one is already waiting.
    fn warm_surface(&self, id: u32) {
        if self.prepared.contains(id) {
            return;
        }
        let detail = self.prepared.detail();
        let data = surface_for_world(&self.content, id, &detail, self.block_compression, false);
        self.prepared.put(id, detail, data);
    }
}

/// World surfaces the helpers prepared ahead, waiting for the loader thread
/// (which takes each one out). Bounded: the oldest beyond
/// [`PREPARED_AHEAD`] are dropped, and the loader prepares those itself.
/// It holds the world's texture detail too: a surface prepared at a detail
/// that has since changed is never taken.
struct PreparedSurfaces {
    waiting: std::sync::Mutex<PreparedQueue>,
}

/// The prepared surfaces, the order they arrived in and the detail they
/// are made at.
struct PreparedQueue {
    detail: WorldTextureDetail,
    by_id: HashMap<u32, SurfaceData>,
    order: VecDeque<u32>,
}

/// The world surfaces sent to the window (and not forgotten since), and
/// which of them went at full detail (a sky or particle model's, which
/// keeps that size whoever else uses it).
#[derive(Debug, Default)]
struct SentSurfaces {
    sent: HashSet<u32>,
    full: HashSet<u32>,
}

impl SentSurfaces {
    /// Note `id` as sent (`full`: at full detail); false when it was
    /// already.
    fn insert(&mut self, id: u32, full: bool) -> bool {
        if !self.sent.insert(id) {
            return false;
        }
        if full {
            self.full.insert(id);
        }
        true
    }

    fn remove(&mut self, id: u32) {
        self.sent.remove(&id);
        self.full.remove(&id);
    }

    /// Whether `id` was sent at the world's texture detail.
    fn at_world_detail(&self, id: u32) -> bool {
        self.sent.contains(&id) && !self.full.contains(&id)
    }

    /// The surfaces a texture-detail change sends again, in id order: every
    /// one sent at the world's detail.
    fn to_resize(&self) -> Vec<u32> {
        let mut ids: Vec<u32> = self
            .sent
            .iter()
            .copied()
            .filter(|id| !self.full.contains(id))
            .collect();
        ids.sort_unstable();
        ids
    }
}

/// World surfaces kept prepared ahead at most.
const PREPARED_AHEAD: usize = 128;

impl PreparedSurfaces {
    fn new(detail: WorldTextureDetail) -> Self {
        Self {
            waiting: std::sync::Mutex::new(PreparedQueue {
                detail,
                by_id: HashMap::new(),
                order: VecDeque::new(),
            }),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, PreparedQueue> {
        // Inserts and removals only; a panic never leaves it half-updated.
        self.waiting
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }

    /// The detail surfaces are prepared at now.
    fn detail(&self) -> WorldTextureDetail {
        self.lock().detail
    }

    /// Prepare at `detail` from now on: what was prepared at another is
    /// dropped.
    fn set_detail(&self, detail: WorldTextureDetail) {
        let mut queue = self.lock();
        if queue.detail != detail {
            queue.detail = detail;
            queue.by_id.clear();
            queue.order.clear();
        }
    }

    fn contains(&self, id: u32) -> bool {
        self.lock().by_id.contains_key(&id)
    }

    /// Keep `data`, made at `detail`, for the loader; made at a detail no
    /// longer in use, it is dropped.
    fn put(&self, id: u32, detail: WorldTextureDetail, data: SurfaceData) {
        let mut queue = self.lock();
        if queue.detail != detail {
            return;
        }
        if queue.by_id.insert(id, data).is_none() {
            queue.order.push_back(id);
        }
        while queue.order.len() > PREPARED_AHEAD {
            if let Some(old) = queue.order.pop_front() {
                queue.by_id.remove(&old);
            }
        }
    }

    fn take(&self, id: u32) -> Option<SurfaceData> {
        let mut queue = self.lock();
        let data = queue.by_id.remove(&id)?;
        queue.order.retain(|&o| o != id);
        Some(data)
    }
}

/// Surface `id` as the window receives it: kept block compressed when
/// `block_compression` allows and it is not `full`, then [`sized`].
fn surface_for_world(
    content: &Content,
    id: u32,
    detail: &WorldTextureDetail,
    block_compression: bool,
    full: bool,
) -> SurfaceData {
    let data = assets::build_surface_with(content, id, id, None, None, block_compression && !full);
    sized(data, detail, full)
}

/// A surface at the world's environment texture detail with its mip chain
/// (unchanged, one level, when `full`). A surface shared by a sky or
/// particle model and another object keeps the size and levels it was
/// first sent with. A texture whose stored blocks cannot be decoded becomes
/// magenta, as any broken texture does.
fn sized(mut data: SurfaceData, detail: &WorldTextureDetail, full: bool) -> SurfaceData {
    if full || data.untextured {
        return data;
    }
    let image = std::mem::replace(
        &mut data.texture,
        crate::surface_image::SurfaceImage::rgba(Arc::new(DecodedTexture {
            width: 1,
            height: 1,
            rgba: vec![0xFF, 0x00, 0xFF, 0xFF],
        })),
    );
    if let Ok(prepared) = crate::surface_image::prepare(image, detail, false) {
        data.texture = prepared;
    }
    data
}

/// A job for the helper threads.
enum Task {
    /// Build a landblock job and hand the result back.
    Block(BlockWork, mpsc::Sender<Built>),
    /// Warm the caches for a model.
    Warm(u32),
}

/// Waiting tasks: landblock jobs before warm-ups.
#[derive(Default)]
struct TaskQueue {
    blocks: VecDeque<Task>,
    warm: VecDeque<Task>,
    closed: bool,
}

#[derive(Default)]
struct PoolShared {
    queue: std::sync::Mutex<TaskQueue>,
    ready: std::sync::Condvar,
}

impl PoolShared {
    fn queue(&self) -> std::sync::MutexGuard<'_, TaskQueue> {
        // The queue is a few pushes and pops; a panic never leaves it
        // half-updated.
        self.queue
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

/// The loader's helper threads. Dropping it lets each finish the task it
/// is on, then waits for them.
struct Pool {
    shared: Arc<PoolShared>,
    threads: Vec<JoinHandle<()>>,
}

impl Pool {
    fn spawn(workers: usize, context: &Arc<BuildContext>) -> Self {
        let shared = Arc::new(PoolShared::default());
        let threads = (0..workers)
            .filter_map(|i| {
                let shared = Arc::clone(&shared);
                let context = Arc::clone(context);
                thread::Builder::new()
                    .name(format!("render-loader-worker-{i}"))
                    .spawn(move || pool_worker(&shared, &context))
                    .ok()
            })
            .collect();
        Self { shared, threads }
    }

    /// Helper threads running.
    fn len(&self) -> usize {
        self.threads.len()
    }

    /// Build a landblock job on a helper thread.
    fn build(&self, work: BlockWork) -> Receiver<Built> {
        let (reply, result) = mpsc::channel();
        self.shared
            .queue()
            .blocks
            .push_back(Task::Block(work, reply));
        self.shared.ready.notify_one();
        result
    }

    /// Warm a model's caches when no landblock job waits.
    fn warm(&self, model: u32) {
        self.shared.queue().warm.push_back(Task::Warm(model));
        self.shared.ready.notify_one();
    }
}

impl Drop for Pool {
    fn drop(&mut self) {
        self.shared.queue().closed = true;
        self.shared.ready.notify_all();
        for thread in self.threads.drain(..) {
            // A helper that panicked has dropped its job's reply; the
            // loader builds that job itself.
            let _ = thread.join();
        }
    }
}

fn pool_worker(shared: &PoolShared, context: &BuildContext) {
    let mut surface_cache = HashMap::new();
    loop {
        let task = {
            let mut queue = shared.queue();
            loop {
                if queue.closed {
                    return;
                }
                if let Some(task) = queue.blocks.pop_front().or_else(|| queue.warm.pop_front()) {
                    break task;
                }
                queue = shared
                    .ready
                    .wait(queue)
                    .unwrap_or_else(std::sync::PoisonError::into_inner);
            }
        };
        match task {
            Task::Block(work, reply) => {
                // The loader may have stopped waiting; nothing to do then.
                let _ = reply.send(context.build_and_warm(work, &mut surface_cache));
            }
            Task::Warm(model) => context.warm_model(model),
        }
    }
}

/// Models waiting at the front of the loader's queue that the helpers warm
/// ahead of it. Kept well under the decoded-texture cache's 128 entries,
/// so a warmed texture is still cached when the loader reaches its model.
const WARM_AHEAD: usize = 32;

enum Work {
    Model(u32),
    Appearance(Box<AppearanceKey>),
    /// A GfxObj; true: at full texture detail.
    GfxObj(u32, bool),
    /// A surface; true: at full texture detail.
    Surface(u32, bool),
    /// A world surface sent before the texture detail changed: again, at
    /// the new detail.
    Resize(u32),
    /// A substituted surface (its key and handle) sent before the texture
    /// detail changed: again, at the new detail.
    ResizeVariant(SurfaceVariantKey, u32),
}

impl Worker<'_> {
    fn send(&self, item: Loaded) -> bool {
        !self.stop.load(Ordering::Relaxed) && self.out.send(item).is_ok()
    }

    /// Send a model, preceded by any of its GfxObjs and surfaces not sent
    /// yet. False once the window is gone.
    fn ensure_model(&mut self, id: u32) -> bool {
        if !self.models.insert(id) {
            return true;
        }
        // The sky keeps its textures' full detail.
        let full = self.full_detail_models.contains(&id);
        let parts = assets::model_parts(&self.content, id);
        for part in parts.iter().flatten() {
            if !self.ensure_gfx_obj(part.gfx_obj_id, full) {
                return false;
            }
        }
        let info = ModelInfo {
            bounds: parts
                .as_deref()
                .and_then(|p| assets::parts_bounds(p, |g| self.gfx_bounds.get(&g).copied())),
            lights: assets::model_lights(&self.content, id),
        };
        self.send(Loaded::Model { id, parts, info })
    }

    /// Send a GfxObj, preceded by its surfaces, then the models of its
    /// level-of-detail ladder. False once the window is gone.
    /// `full`: its textures keep their full detail (sky and particle
    /// models).
    fn ensure_gfx_obj(&mut self, id: u32, full: bool) -> bool {
        if !self.gfx_objs.insert(id) {
            return true;
        }
        let Some(gfx) = assets::build_gfx_obj(&self.content, id) else {
            return true;
        };
        if let Some(bounds) = assets::gfx_bounds(&gfx) {
            self.gfx_bounds.insert(id, bounds);
        }
        for sub in &gfx.sub_meshes {
            if !self.ensure_surface(sub.surface_id, full) {
                return false;
            }
        }
        let levels: Vec<u32> = gfx
            .degrades
            .iter()
            .map(|l| l.gfx_obj_id)
            .filter(|&l| l != 0 && l != id)
            .collect();
        if !self.send(Loaded::GfxObj(Box::new(gfx))) {
            return false;
        }
        levels.into_iter().all(|l| self.ensure_gfx_obj(l, full))
    }

    fn ensure_surface(&mut self, id: u32, full: bool) -> bool {
        if !self.surfaces.insert(id, full) {
            return true;
        }
        self.send_surface(id, full)
    }

    fn send_surface(&self, id: u32, full: bool) -> bool {
        // A world surface a helper prepared ahead is taken as it is; the
        // same function makes it here otherwise, so the bytes match.
        let data = match (!full).then(|| self.prepared.take(id)).flatten() {
            Some(data) => data,
            None => surface_for_world(
                &self.content,
                id,
                &self.detail,
                self.block_compression,
                full,
            ),
        };
        self.send(Loaded::Surface(Box::new(data)))
    }

    /// The texture detail changed: from now on everything is made at
    /// `detail`, and the work that sends again, at it, every world surface
    /// and substituted surface sent so far.
    fn change_detail(&mut self, detail: WorldTextureDetail) -> Vec<Work> {
        self.detail = detail;
        self.prepared.set_detail(detail);
        let variants = self.looks.surface_variants();
        self.surfaces
            .to_resize()
            .into_iter()
            .map(Work::Resize)
            .chain(
                variants
                    .into_iter()
                    .map(|(key, handle)| Work::ResizeVariant(key, handle)),
            )
            .collect()
    }

    /// Send world surface `id` again at the current detail, unless the
    /// window released it since (or it went at full detail).
    fn resize_surface(&self, id: u32) -> bool {
        if !self.surfaces.at_world_detail(id) {
            return true;
        }
        self.send_surface(id, false)
    }

    /// Make the substituted surface `key` under `handle` at the current
    /// detail.
    fn variant_surface(&self, key: &SurfaceVariantKey, handle: u32) -> SurfaceData {
        let (surface_id, texture, spec) = key;
        // A palette that cannot be built repaints nothing.
        let repaint = spec
            .as_ref()
            .and_then(|spec| self.looks.built_palette(spec));
        sized(
            assets::build_surface_with(
                &self.content,
                handle,
                *surface_id,
                *texture,
                repaint.as_deref(),
                self.block_compression,
            ),
            &self.detail,
            false,
        )
    }

    /// Send a landblock's near-tier content, preceded by the surfaces its
    /// cell meshes use. False once the window is gone.
    fn send_near(&mut self, coord: LandblockCoord, data: NearData) -> bool {
        for cell in &data.cells {
            for sub in cell.mesh.iter().flat_map(|m| &m.sub_meshes) {
                if !self.ensure_surface(sub.surface_id, false) {
                    return false;
                }
            }
        }
        self.send(Loaded::Near(coord, Box::new(data)))
    }

    /// The window released these: they are sent again when next wanted.
    /// Recoloured surfaces (handles) are forgotten with the variants that
    /// name them.
    fn forget(
        &mut self,
        models: &[u32],
        gfx_objs: &[u32],
        surfaces: &[u32],
        appearances: &[AppearanceKey],
        variants: &[u32],
    ) {
        for id in models {
            self.models.remove(id);
        }
        for id in gfx_objs {
            self.gfx_objs.remove(id);
        }
        for &id in surfaces {
            if id < crate::appearance_residency::FIRST_SURFACE_HANDLE {
                self.surfaces.remove(id);
            }
        }
        self.looks.forget(appearances, variants, surfaces);
    }

    /// Send a model drawn with an appearance, preceded by its GfxObjs, the
    /// substituted surfaces and the variants that are new. False once the
    /// window is gone.
    fn ensure_appearance(&mut self, key: AppearanceKey) -> bool {
        if self.looks.was_sent(&key) {
            return true;
        }
        let palette = match &key.palette {
            Some(spec) => {
                let content = &self.content;
                self.looks.palette(spec, || {
                    assets::modified_palette(content, spec).map(Arc::new)
                })
            }
            None => None,
        };
        let Some(parts) = assets::appearance_parts(&self.content, &key) else {
            self.looks.sent(key.clone());
            return self.send(Loaded::Appearance {
                key: Box::new(key),
                parts: None,
                bounds: None,
            });
        };
        let mut meshes = Vec::with_capacity(parts.len());
        for part in parts {
            if !self.ensure_gfx_obj(part.part.gfx_obj_id, false) {
                return false;
            }
            let changes = part.changes.iter().filter_map(|change| {
                // A palette that cannot be built repaints nothing.
                let repaint = change.repaint.then_some(()).and(palette.as_ref());
                if change.texture.is_none() && repaint.is_none() {
                    return None;
                }
                let spec = repaint.and(key.palette.clone());
                Some((change.surface_id, change.texture, spec))
            });
            let variant = self.looks.part_variant(changes);
            for (variant_key, handle) in &variant.new_surfaces {
                let data = self.variant_surface(variant_key, *handle);
                if !self.send(Loaded::Surface(Box::new(data))) {
                    return false;
                }
            }
            if let Some(surfaces) = variant.new_variant
                && !self.send(Loaded::Variant {
                    id: variant.id,
                    surfaces,
                })
            {
                return false;
            }
            meshes.push(MeshRef {
                variant: variant.id,
                ..part.part
            });
        }
        self.looks.sent(key.clone());
        let bounds = assets::parts_bounds(&meshes, |g| self.gfx_bounds.get(&g).copied());
        self.send(Loaded::Appearance {
            key: Box::new(key),
            parts: Some(meshes),
            bounds,
        })
    }
}

fn run(
    slot: &ContentSlot,
    detail: WorldTextureDetail,
    block_compression: bool,
    workers: usize,
    requests: &Receiver<LoadRequest>,
    out: &Sender<Loaded>,
    stop: &AtomicBool,
) {
    let content = match slot.get() {
        Ok(content) => content,
        Err(reason) => {
            let _ = out.send(Loaded::Failed(reason));
            return;
        }
    };
    if let Some(icon) = assets::window_icon(&content)
        && out.send(Loaded::Icon(icon)).is_err()
    {
        return;
    }
    let region = match content.dats.get::<Region>(REGION_ID) {
        Ok(Some(region)) => region,
        Ok(None) => {
            let _ = out.send(Loaded::Failed("the portal dat has no region".into()));
            return;
        }
        Err(error) => {
            let _ = out.send(Loaded::Failed(format!("cannot read the region: {error}")));
            return;
        }
    };
    let atlas = assets::build_terrain_atlas(&content, &region, detail);
    let blending = atlas.blending.clone();
    let height_table = region.land_defs.land_height_table;
    if stop.load(Ordering::Relaxed) || out.send(Loaded::Atlas(Box::new(atlas))).is_err() {
        return;
    }
    let sky = assets::region_sky(&content, &region);
    // Every model any day group's sky can show, prepared after the terrain,
    // in id order (the same order every time).
    let sky_models: Vec<u32> = sky
        .iter()
        .flat_map(|s| &s.day_groups)
        .flat_map(|g| {
            g.sky_objects.iter().map(|o| o.gfx_obj_id).chain(
                g.sky_times
                    .iter()
                    .flat_map(|t| t.replaces.iter().map(|r| r.gfx_obj_id)),
            )
        })
        .filter(|&id| id != 0)
        .collect::<std::collections::BTreeSet<u32>>()
        .into_iter()
        .collect();
    let region_item = Loaded::Region {
        sky: sky.map(Box::new),
        ambient: Box::new(AmbientRegion::from_region(&region)),
        zero_time_of_year: region.game_time.zero_time_of_year,
    };
    if stop.load(Ordering::Relaxed) || out.send(region_item).is_err() {
        return;
    }

    let mut worker = Worker {
        content,
        out,
        stop,
        models: HashSet::new(),
        gfx_objs: HashSet::new(),
        gfx_bounds: HashMap::new(),
        surfaces: SentSurfaces::default(),
        looks: SentLooks::default(),
        detail,
        block_compression,
        prepared: Arc::new(PreparedSurfaces::new(detail)),
        full_detail_models: sky_models.iter().copied().collect(),
    };
    let context = Arc::new(BuildContext {
        content: Arc::clone(&worker.content),
        block_compression,
        prepared: Arc::clone(&worker.prepared),
        region,
        height_table,
        blending,
    });
    let pool = (workers > 0).then(|| Pool::spawn(workers, &context));
    let capacity = pool.as_ref().map_or(0, Pool::len);
    let mut surface_cache = HashMap::new();
    // Landblock jobs first (the near tier's ahead of the far tier's), then
    // models, one job at a time, looking for new requests between jobs:
    // the ground appears quickly and objects fill in.
    // Models the window asked for (server objects) go ahead of statics.
    let mut models: VecDeque<Work> = sky_models.into_iter().map(Work::Model).collect();
    let mut blocks = BlockPipeline::<Built>::default();
    // Models handed to the helpers to warm and not yet sent from here.
    let mut warming: HashSet<u32> = HashSet::new();
    loop {
        let waiting = if models.is_empty() && blocks.is_empty() {
            match requests.recv() {
                Ok(request) => Some(request),
                Err(_) => return,
            }
        } else {
            None
        };
        for request in waiting.into_iter().chain(requests.try_iter()) {
            if stop.load(Ordering::Relaxed) {
                return;
            }
            match request {
                LoadRequest::Model(id) => models.push_front(Work::Model(id)),
                LoadRequest::Appearance(key) => models.push_front(Work::Appearance(key)),
                LoadRequest::GfxObj(id) => models.push_front(Work::GfxObj(id, false)),
                LoadRequest::Surface(id) => models.push_front(Work::Surface(id, false)),
                LoadRequest::EffectGfxObj(id) => models.push_front(Work::GfxObj(id, true)),
                LoadRequest::EffectSurface(id) => models.push_front(Work::Surface(id, true)),
                LoadRequest::Forget {
                    models: m,
                    gfx_objs,
                    surfaces,
                    appearances,
                    variants,
                } => worker.forget(&m, &gfx_objs, &surfaces, &appearances, &variants),
                LoadRequest::TextureDetail(detail) => {
                    let old = worker.detail;
                    // The terrain goes first: the window writes the new
                    // atlas over a few frames and swaps it in whole.
                    if detail.landscape != old.landscape {
                        let atlas =
                            assets::build_terrain_atlas(&worker.content, &context.region, detail);
                        if !worker.send(Loaded::Atlas(Box::new(atlas))) {
                            return;
                        }
                    }
                    let resize = worker.change_detail(detail);
                    if detail.environment != old.environment {
                        models.retain(|w| !matches!(w, Work::Resize(_) | Work::ResizeVariant(..)));
                        models.extend(resize);
                    }
                }
                LoadRequest::Landblock { coord, near } => {
                    blocks.push(BlockWork::Terrain(coord), near);
                }
                LoadRequest::Near(coord) => blocks.push(BlockWork::Near(coord), true),
                LoadRequest::CameraCollision(center) => {
                    let engine = crate::camera_collision::build_neighbourhood(
                        &worker.content.dats,
                        &height_table,
                        center,
                    )
                    .map(Box::new)
                    .map_err(|error| error.to_string());
                    if !worker.send(Loaded::CameraCollision { center, engine }) {
                        return;
                    }
                }
            }
        }
        // The helpers warm the caches for the models next in line (they
        // take landblock jobs first).
        if let Some(pool) = &pool {
            for work in models.iter().take(WARM_AHEAD) {
                if let Work::Model(id) = work
                    && !worker.models.contains(id)
                    && warming.insert(*id)
                {
                    pool.warm(*id);
                }
            }
        }
        // One landblock job at a time, looking for new requests between
        // jobs, so a near job asked later overtakes the far ones waiting
        // (the helpers build up to `capacity` of them ahead).
        let next = match &pool {
            Some(pool) => blocks.next(capacity, |work| pool.build(work)),
            // Nothing is built ahead: the dispatch is never called.
            None => blocks.next(0, |_| mpsc::channel().1),
        };
        if let Some((work, built)) = next {
            match built.unwrap_or_else(|| context.build(work, &mut surface_cache)) {
                Built::Terrain(coord, block) => {
                    let item = match block {
                        Some(block) => Loaded::Landblock(Box::new(block)),
                        None => Loaded::NoLandblock(coord),
                    };
                    if !worker.send(item) {
                        return;
                    }
                }
                Built::Near(coord, near) => {
                    let mut ids: Vec<u32> = near.model_ids().collect();
                    ids.sort_unstable();
                    ids.dedup();
                    if !worker.send_near(coord, near) {
                        return;
                    }
                    models.extend(ids.into_iter().map(Work::Model));
                }
            }
            continue;
        }
        let done = match models.pop_front() {
            Some(Work::Model(id)) => {
                warming.remove(&id);
                worker.ensure_model(id)
            }
            Some(Work::Appearance(key)) => worker.ensure_appearance(*key),
            Some(Work::GfxObj(id, full)) => worker.ensure_gfx_obj(id, full),
            Some(Work::Surface(id, full)) => worker.ensure_surface(id, full),
            Some(Work::Resize(id)) => worker.resize_surface(id),
            // Unless the window released it since.
            Some(Work::ResizeVariant(key, handle)) if worker.looks.holds_handle(&key, handle) => {
                let data = worker.variant_surface(&key, handle);
                worker.send(Loaded::Surface(Box::new(data)))
            }
            Some(Work::ResizeVariant(..)) | None => true,
        };
        if !done {
            return;
        }
    }
}

#[cfg(test)]
mod block_queue_tests {
    use super::*;

    fn at(x: u8) -> LandblockCoord {
        LandblockCoord { x, y: 0xB4 }
    }

    /// OpenAC `LandblockStreamerTests.LoadNear_OvertakesQueuedFarLoads`.
    #[test]
    fn a_near_job_overtakes_the_far_jobs_waiting() {
        let mut q = BlockQueue::default();
        for x in [0xAA, 0xBB, 0xCC] {
            q.push(BlockWork::Terrain(at(x)), false);
        }
        q.push(BlockWork::Terrain(at(0xDD)), true);
        assert_eq!(q.pop(), Some(BlockWork::Terrain(at(0xDD))));
        assert_eq!(q.pop(), Some(BlockWork::Terrain(at(0xAA))));
    }

    /// OpenAC `LandblockStreamerTests.PromoteToNear_OvertakesAndSupersedesQueuedFarLoadForSameLandblock`:
    /// the far job of a landblock promoted while it waits moves ahead with
    /// it (terrain before content), once.
    #[test]
    fn a_promotion_takes_its_waiting_far_job_along() {
        let mut q = BlockQueue::default();
        q.push(BlockWork::Terrain(at(0xA0)), false);
        q.push(BlockWork::Terrain(at(0xA9)), false);
        q.push(BlockWork::Terrain(at(0xA9)), true);
        q.push(BlockWork::Near(at(0xA9)), true);
        assert_eq!(q.pop(), Some(BlockWork::Terrain(at(0xA9))));
        assert_eq!(q.pop(), Some(BlockWork::Near(at(0xA9))));
        assert_eq!(q.pop(), Some(BlockWork::Terrain(at(0xA0))));
        assert_eq!(q.pop(), None);
        assert!(q.is_empty());
    }

    /// A dispatch that builds each job on its own thread, the earlier jobs
    /// slower, so they finish in the reverse of the order they left.
    fn reversed_builder(
        dispatched: &mut Vec<BlockWork>,
    ) -> impl FnMut(BlockWork) -> Receiver<BlockWork> + '_ {
        move |work| {
            let (tx, rx) = mpsc::channel();
            let delay = 40u64.saturating_sub(10 * dispatched.len() as u64);
            dispatched.push(work);
            thread::spawn(move || {
                thread::sleep(std::time::Duration::from_millis(delay));
                let _ = tx.send(work);
            });
            rx
        }
    }

    #[test]
    fn results_come_back_in_the_order_jobs_left_whichever_finishes_first() {
        let mut p = BlockPipeline::default();
        for x in 0..6 {
            p.push(BlockWork::Terrain(at(x)), x < 3);
        }
        let mut dispatched = Vec::new();
        let mut sent = Vec::new();
        while let Some((work, built)) = p.next(4, reversed_builder(&mut dispatched)) {
            assert_eq!(built, Some(work), "the result is the job's own");
            sent.push(work);
        }
        let expected: Vec<_> = (0..6).map(|x| BlockWork::Terrain(at(x))).collect();
        assert_eq!(sent, expected);
        assert_eq!(dispatched, expected);
        assert!(p.is_empty());
    }

    #[test]
    fn a_near_job_overtakes_waiting_far_jobs_but_not_those_building() {
        let mut p = BlockPipeline::default();
        for x in 0..6 {
            p.push(BlockWork::Terrain(at(x)), false);
        }
        let mut dispatched = Vec::new();
        // Two far jobs leave for the helpers; the first comes back.
        let (first, _) = p.next(2, reversed_builder(&mut dispatched)).unwrap();
        assert_eq!(first, BlockWork::Terrain(at(0)));
        p.push(BlockWork::Near(at(0xA9)), true);
        let rest: Vec<BlockWork> =
            std::iter::from_fn(|| p.next(2, reversed_builder(&mut dispatched)).map(|(w, _)| w))
                .collect();
        assert_eq!(
            rest,
            [
                BlockWork::Terrain(at(1)),
                BlockWork::Near(at(0xA9)),
                BlockWork::Terrain(at(2)),
                BlockWork::Terrain(at(3)),
                BlockWork::Terrain(at(4)),
                BlockWork::Terrain(at(5)),
            ]
        );
    }

    #[test]
    fn without_helpers_jobs_leave_one_at_a_time_in_queue_order() {
        let mut p = BlockPipeline::<()>::default();
        p.push(BlockWork::Terrain(at(1)), false);
        p.push(BlockWork::Terrain(at(2)), false);
        assert_eq!(
            p.next(0, |_| unreachable!()),
            Some((BlockWork::Terrain(at(1)), None))
        );
        p.push(BlockWork::Near(at(3)), true);
        assert_eq!(
            p.next(0, |_| unreachable!()),
            Some((BlockWork::Near(at(3)), None))
        );
        assert_eq!(
            p.next(0, |_| unreachable!()),
            Some((BlockWork::Terrain(at(2)), None))
        );
        assert_eq!(p.next(0, |_| unreachable!()), None);
    }

    #[test]
    fn a_failed_helper_leaves_the_job_to_the_loader() {
        let mut p = BlockPipeline::<()>::default();
        p.push(BlockWork::Near(at(7)), true);
        // The reply is dropped unanswered, as by a helper that panicked.
        let next = p.next(1, |_| mpsc::channel().1);
        assert_eq!(next, Some((BlockWork::Near(at(7)), None)));
    }
}

#[cfg(test)]
mod retail_tests {
    use super::*;

    /// What one item says, enough to compare two streams.
    fn key(item: &Loaded) -> String {
        match item {
            Loaded::Landblock(b) => format!("terrain {:?}", b.coord),
            Loaded::Near(c, n) => format!(
                "near {c:?} {} {} {}",
                n.statics.len(),
                n.scenery.len(),
                n.cells.len()
            ),
            Loaded::NoLandblock(c) => format!("none {c:?}"),
            Loaded::Surface(s) => {
                // The texture itself (FNV-1a over every level), so a prepared
                // chain that differs shows.
                let mut hash = 0xCBF2_9CE4_8422_2325u64;
                for (_, _, bytes) in s.texture.levels() {
                    for &b in bytes {
                        hash = (hash ^ u64::from(b)).wrapping_mul(0x0100_0000_01B3);
                    }
                }
                format!(
                    "surface {:08X} {:?} {} {hash:016X}",
                    s.id,
                    s.texture.block_format(),
                    s.texture.level_count()
                )
            }
            Loaded::GfxObj(g) => format!("mesh {:08X} {}", g.id, g.vertices.len()),
            Loaded::Model { id, parts, .. } => {
                format!("model {id:08X} {:?}", parts.as_ref().map(Vec::len))
            }
            other => other.stage().to_string(),
        }
    }

    /// Everything a loader with `workers` helpers sends for the 5 x 5
    /// landblocks around Holtburg (the 3 x 3 in the near tier).
    fn stream(workers: usize) -> Vec<String> {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        // Every request waits before the loader starts, so both loaders see
        // the same requests in one batch, and the channel is closed behind
        // them: the loader sends all they ask for, then ends, closing the
        // results. The stream's end is the loader's, not a pause in it.
        let (requests, request_rx) = mpsc::channel();
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        for coord in crate::world_frame::landblocks_around(center, 2) {
            let near = coord.x.abs_diff(center.x) <= 1 && coord.y.abs_diff(center.y) <= 1;
            requests
                .send(LoadRequest::Landblock { coord, near })
                .expect("queued");
            if near {
                requests.send(LoadRequest::Near(coord)).expect("queued");
            }
        }
        drop(requests);
        let (out, results) = mpsc::channel();
        let loader = thread::spawn(move || {
            run(
                &ContentSlot::new(dir),
                WorldTextureDetail::default(),
                true,
                workers,
                &request_rx,
                &out,
                &AtomicBool::new(false),
            );
        });
        let items = results
            .iter()
            .map(|item| {
                assert!(!matches!(item, Loaded::Failed(_)), "{}", key(&item));
                key(&item)
            })
            .collect();
        loader.join().expect("the loader ends");
        items
    }

    /// The helpers change how fast the window gets its items, not which
    /// items or in what order.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn helpers_send_the_same_items_in_the_same_order_as_one_thread() {
        let alone = stream(0);
        let terrain = alone
            .iter()
            .filter(|k| k.starts_with("terrain Landblock") || k.starts_with("none"))
            .count();
        assert_eq!(terrain, 25, "{alone:?}");
        assert!(alone.iter().filter(|k| k.starts_with("near")).count() == 9);
        assert!(alone.iter().any(|k| k.starts_with("model")));
        assert!(
            alone
                .iter()
                .any(|k| k.starts_with("surface") && k.contains("Some(Bc1)")),
            "block-compressed surfaces are sent"
        );
        let helped = stream(4);
        let first = helped.iter().zip(&alone).position(|(a, b)| a != b);
        assert!(
            first.is_none() && helped.len() == alone.len(),
            "first difference at {first:?} of {} / {}: {:?} against {:?}",
            helped.len(),
            alone.len(),
            first.map(|i| &helped[i.saturating_sub(2)..(i + 3).min(helped.len())]),
            first.map(|i| &alone[i.saturating_sub(2)..(i + 3).min(alone.len())]),
        );
    }
}

#[cfg(test)]
mod texture_detail_tests {
    use super::*;
    use crate::texture_detail::ImageScale;

    const EIGHTH: WorldTextureDetail = WorldTextureDetail {
        landscape: ImageScale::Eighth,
        environment: ImageScale::Eighth,
    };

    fn solid(id: u32) -> SurfaceData {
        SurfaceData {
            id,
            texture: crate::surface_image::SurfaceImage::rgba(Arc::new(DecodedTexture {
                width: 1,
                height: 1,
                rgba: vec![0, 0, 0, 0xFF],
            })),
            translucency: crate::meshing::Translucency::Opaque,
            opacity: 1.0,
            alpha_cutoff: 0.05,
            untextured: true,
            luminosity: 0.0,
            diffuse: 1.0,
            disable_fog: false,
        }
    }

    /// A detail change sends again every world surface still sent, in id
    /// order, but not one that went at full detail (a sky or particle
    /// model's) nor one the window released.
    #[test]
    fn a_detail_change_resizes_the_world_surfaces_still_sent() {
        let mut sent = SentSurfaces::default();
        assert!(sent.insert(0x0800_0003, false));
        assert!(sent.insert(0x0800_0001, false));
        assert!(sent.insert(0x0800_0002, true));
        assert!(sent.insert(0x0800_0004, false));
        assert!(!sent.insert(0x0800_0004, true), "already sent");
        sent.remove(0x0800_0003);
        assert_eq!(sent.to_resize(), [0x0800_0001, 0x0800_0004]);
        assert!(sent.at_world_detail(0x0800_0001));
        assert!(!sent.at_world_detail(0x0800_0002), "full detail");
        assert!(!sent.at_world_detail(0x0800_0003), "released");
    }

    /// A surface a helper made at the old detail is never taken after the
    /// change, even when it finishes after it.
    #[test]
    fn a_surface_prepared_at_the_old_detail_is_never_taken() {
        let prepared = PreparedSurfaces::new(WorldTextureDetail::FULL);
        prepared.put(1, WorldTextureDetail::FULL, solid(1));
        prepared.set_detail(EIGHTH);
        assert!(prepared.take(1).is_none(), "dropped by the change");
        // A helper that read the old detail before the change finishes now.
        prepared.put(2, WorldTextureDetail::FULL, solid(2));
        assert!(prepared.take(2).is_none(), "made at the old detail");
        prepared.put(3, EIGHTH, solid(3));
        assert_eq!(prepared.take(3).map(|s| s.id), Some(3));
        assert_eq!(prepared.detail(), EIGHTH);
    }

    /// Against the retail dats: after a detail change the loader sends the
    /// terrain atlas again at the new landscape detail and every world
    /// surface it sent before (those with a mip chain; the sky's keep their
    /// one full level) again at the new environment detail, while the
    /// window keeps running.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn a_detail_change_sends_the_atlas_and_the_world_surfaces_again() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let (requests, request_rx) = mpsc::channel();
        let center = LandblockCoord { x: 0xA9, y: 0xB4 };
        requests
            .send(LoadRequest::Landblock {
                coord: center,
                near: true,
            })
            .expect("queued");
        requests.send(LoadRequest::Near(center)).expect("queued");
        let (out, results) = mpsc::channel();
        let loader = thread::spawn(move || {
            run(
                &ContentSlot::new(dir),
                WorldTextureDetail::FULL,
                true,
                2,
                &request_rx,
                &out,
                &AtomicBool::new(false),
            );
        });
        let size = |s: &SurfaceData| (s.texture.width(), s.texture.height());
        let mut atlases = Vec::new();
        let mut before: HashMap<u32, ((u32, u32), u32)> = HashMap::new();
        // Up to the landblock's content: its cell surfaces were sent ahead
        // of it, its models are still to come.
        for item in results.iter() {
            match item {
                Loaded::Failed(reason) => panic!("{reason}"),
                Loaded::Atlas(atlas) => atlas_sizes(&mut atlases, &atlas),
                Loaded::Surface(s) => {
                    before.insert(s.id, (size(&s), s.texture.level_count()));
                }
                Loaded::Near(..) => break,
                _ => {}
            }
        }
        requests
            .send(LoadRequest::TextureDetail(EIGHTH))
            .expect("queued");
        drop(requests);
        let mut after: HashMap<u32, (u32, u32)> = HashMap::new();
        for item in results.iter() {
            match item {
                Loaded::Failed(reason) => panic!("{reason}"),
                Loaded::Atlas(atlas) => atlas_sizes(&mut atlases, &atlas),
                Loaded::Surface(s) if atlases.len() == 2 => {
                    after.insert(s.id, size(&s));
                }
                _ => {}
            }
        }
        loader.join().expect("the loader ends");
        assert_eq!(atlases.len(), 2, "the atlas again after the change");
        assert_eq!(
            atlases[1],
            (
                EIGHTH.landscape_size(atlases[0].0),
                EIGHTH.landscape_size(atlases[0].1)
            )
        );
        let world: Vec<u32> = before
            .iter()
            .filter(|(_, (_, levels))| *levels > 1)
            .map(|(id, _)| *id)
            .collect();
        assert!(!world.is_empty(), "the landblock's cells have textures");
        for id in world {
            let ((w, h), _) = before[&id];
            assert_eq!(
                after.get(&id).copied(),
                Some((EIGHTH.environment_size(w), EIGHTH.environment_size(h))),
                "surface {id:08X} ({w} x {h}) again at an eighth"
            );
        }
    }

    fn atlas_sizes(sizes: &mut Vec<(u32, u32)>, atlas: &crate::assets::TerrainAtlasData) {
        let first = atlas.layers.first().expect("terrain layers");
        sizes.push((first.width, first.height));
    }
}
