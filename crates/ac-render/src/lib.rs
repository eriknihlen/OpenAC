//! Presentation for openAC: the window host and the world renderer.
//!
//! Presentation holds only what it can rebuild. A window can be attached to a running process and
//! detached again while the event loop, the sessions and the process keep running: the
//! [`WindowHost`] owns the platform event loop on the main thread and opens or closes a window on
//! command from any thread through a [`HostHandle`]. An attached window builds its world renderer
//! (terrain in two tiers, static objects, scenery, buildings and their interiors, and server
//! objects around the player, drawn through the game's portal visibility) from the session's
//! snapshot and deltas, reading the dats on a loader thread of its own.

pub mod animation;
pub mod appearance;
mod appearance_residency;
pub mod assets;
pub mod atmosphere;
pub mod attach;
pub mod audio;
pub mod automation;
pub mod block_compression;
pub mod camera;
pub mod camera_collision;
pub mod cells;
pub mod content;
pub mod degrade;
pub mod degrade_controller;
pub mod device_health;
pub mod display_mode;
pub mod draws;
pub mod effects;
pub mod emotes;
pub mod environment;
pub mod facts;
pub mod frame;
pub mod frustum;
pub mod gameplay_input;
pub mod gamma;
pub mod gpu;
mod gpu_timer;
mod host;
pub mod input;
pub mod lifetime;
pub mod lights;
mod loader;
pub mod membership;
pub mod meshing;
mod model_requests;
pub mod nearby_dump;
pub mod pacing;
pub mod platform;
pub mod portal_space;
pub mod profiler;
pub mod quality;
mod renderer;
pub mod retirement;
pub mod reveal;
pub mod scene;
mod screen_saver;
pub use ac_physics::scenery;
pub mod selection;
pub mod sky;
pub mod streaming;
mod surface;

/// A map keyed by ids (model, mesh, cell, static), looked up thousands of
/// times a frame: a fast hash, as the keys are not chosen by an attacker.
pub(crate) type IdMap<K, V> = rustc_hash::FxHashMap<K, V>;
/// A set of ids; see [`IdMap`].
pub(crate) type IdSet<K> = rustc_hash::FxHashSet<K>;
pub mod surface_image;
#[cfg(test)]
mod test_gpu;
pub mod texture_detail;
pub mod title;
pub mod translucency;
pub mod ui;
mod ui_scale;
pub mod unowned_cache;
pub mod viewport;
pub mod walk;
pub mod work_budget;
pub mod world_click;
pub mod world_frame;
pub mod world_lines;
pub mod world_shapes;
pub mod world_triangles;

pub use attach::DetachReason;
pub use host::{
    HostClosed, HostEvent, HostHandle, HostOptions, WindowHost, WindowSpec, screenshot_path,
};
pub use renderer::WorldStats;
pub use surface::{AdapterSummary, GraphicsOptions};
/// The clear color type of [`WindowSpec`], re-exported so callers need no direct `wgpu` dependency.
pub use wgpu::Color;

/// Errors from the window host and the GPU.
#[derive(Debug, thiserror::Error)]
pub enum RenderError {
    /// The platform event loop could not be created or failed while running.
    #[error("event loop: {0}")]
    EventLoop(#[from] winit::error::EventLoopError),
    /// The OS refused to create the window.
    #[error("create window: {0}")]
    CreateWindow(#[from] winit::error::OsError),
    /// The GPU API could not create a surface for the window.
    #[error("create surface: {0}")]
    CreateSurface(#[from] wgpu::CreateSurfaceError),
    /// No GPU adapter can present to the window.
    #[error("request adapter: {0}")]
    RequestAdapter(#[from] wgpu::RequestAdapterError),
    /// The adapter refused to create a device.
    #[error("request device: {0}")]
    RequestDevice(#[from] wgpu::RequestDeviceError),
    /// The surface reports no format or present mode for the chosen adapter.
    #[error("the window surface is not supported by the GPU adapter")]
    SurfaceUnsupported,
    /// The GPU failed in a way the window cannot draw past (lost device or
    /// surface, exhausted memory).
    #[error("{0}")]
    Gpu(gpu::GpuFault),
    /// No adapter can run the renderer; the message says what is missing
    /// and what to try.
    #[error("{0}")]
    GpuUnsupported(String),
    /// Acquiring a frame raised a validation error.
    #[error("validation error while acquiring a frame")]
    SurfaceValidation,
    /// A screenshot could not be taken or written.
    #[error("screenshot: {0}")]
    Screenshot(String),
}
