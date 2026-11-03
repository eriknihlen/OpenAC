//! The launcher application: the window model, polled and drawn each frame,
//! with the probe script and screenshots.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_launcher_core::window::icon::PluginIcon;
use ac_launcher_core::window::{LauncherWindow, Orchestration};

use crate::script::{Script, ScriptAction};
use crate::shell::{CapturedFrame, LauncherUi};
use crate::ui;

/// How often the window wakes while nothing else asks (status files,
/// server health), as OpenAC's 250 ms status timer.
const POLL_INTERVAL: Duration = Duration::from_millis(250);

/// The launcher's state for drawing.
pub struct LauncherApp {
    /// The window model.
    pub window: LauncherWindow,
    orchestrator: Arc<dyn Orchestration>,
    script: Option<Script>,
    screenshot: Option<PathBuf>,
    capture_next: bool,
    /// The probe script asked for the GPU state to be rebuilt.
    rebuild_gpu: bool,
    exit: bool,
    started: bool,
    /// Decoded plugin icons as textures, by id, version and side.
    pub icons: HashMap<(String, String, bool), egui::TextureHandle>,
}

impl std::fmt::Debug for LauncherApp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherApp")
            .field("window", &self.window)
            .finish_non_exhaustive()
    }
}

impl LauncherApp {
    /// The app over `window`.
    pub fn new(
        window: LauncherWindow,
        orchestrator: Arc<dyn Orchestration>,
        script: Option<Script>,
    ) -> Self {
        Self {
            window,
            orchestrator,
            script,
            screenshot: None,
            capture_next: false,
            rebuild_gpu: false,
            exit: false,
            started: false,
            icons: HashMap::new(),
        }
    }

    /// The texture for a plugin icon, uploaded once.
    pub fn icon_texture(
        &mut self,
        ctx: &egui::Context,
        id: &str,
        version: &str,
        installed: bool,
        icon: &PluginIcon,
    ) -> egui::TextureHandle {
        let key = (id.to_lowercase(), version.to_owned(), installed);
        self.icons
            .entry(key)
            .or_insert_with(|| {
                let image = egui::ColorImage::from_rgba_unmultiplied(
                    [icon.width as usize, icon.height as usize],
                    &icon.rgba,
                );
                ctx.load_texture(
                    format!("plugin-icon-{id}-{version}-{installed}"),
                    image,
                    egui::TextureOptions::LINEAR,
                )
            })
            .clone()
    }
}

impl LauncherUi for LauncherApp {
    fn update(&mut self, root: &mut egui::Ui, window_active: bool) {
        let ctx = root.ctx().clone();
        if !self.started {
            self.started = true;
            crate::theme::apply(&ctx);
            // As OpenAC's window opening: content and update checks, then the plugin list,
            // without holding up the window.
            self.window.start_background_initialization();
            self.window.plugins_check_now();
        }
        let now = Instant::now();
        self.window.poll();
        self.window.poll_server_health(window_active, now);
        self.window.poll_update_check(now);
        self.window.refresh_consoles();
        if let Some(script) = self.script.as_mut() {
            match script.tick(&mut self.window, now) {
                Some(ScriptAction::Screenshot(path)) => {
                    self.screenshot = Some(path);
                    self.capture_next = true;
                }
                Some(ScriptAction::WindowSize(width, height)) => {
                    ctx.send_viewport_cmd(egui::ViewportCommand::InnerSize(egui::vec2(
                        width, height,
                    )));
                }
                Some(ScriptAction::RebuildGpu) => self.rebuild_gpu = true,
                Some(ScriptAction::Quit) => self.exit = true,
                None => {}
            }
            if script.is_finished() {
                self.script = None;
            }
            ctx.request_repaint_after(Duration::from_millis(50));
        }
        ui::draw(self, root);
        ctx.request_repaint_after(POLL_INTERVAL);
    }

    fn wants_screenshot(&mut self) -> bool {
        std::mem::take(&mut self.capture_next)
    }

    fn wants_gpu_rebuild(&mut self) -> bool {
        std::mem::take(&mut self.rebuild_gpu)
    }

    fn frame_captured(&mut self, frame: CapturedFrame) {
        if let Some(path) = self.screenshot.take() {
            match crate::png::write(&path, frame.width, frame.height, &frame.rgba) {
                Ok(()) => println!(
                    "script: screenshot {} ({} x {})",
                    path.display(),
                    frame.width,
                    frame.height
                ),
                Err(error) => println!("script: screenshot {} failed: {error}", path.display()),
            }
        }
    }

    fn close_requested(&mut self) -> bool {
        // While clients run, the player confirms first.
        self.window.request_close()
    }

    fn wants_exit(&mut self) -> bool {
        // A confirmed close, or a staged launcher update's helper that waits for this process
        // to end: the window first shows its clients closing, then closes.
        self.exit || self.window.should_exit()
    }

    fn on_exit(&mut self) {
        self.window.dispose();
        // Every session the launcher started is stopped (all at once, each up to five seconds
        // before a kill); after the closing view there is none left to stop.
        self.orchestrator.dispose();
    }
}
