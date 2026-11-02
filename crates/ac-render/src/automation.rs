//! Hands-free checks of a live window: the UI probe script
//! (`OPENAC_UI_PROBE_SCRIPT`, [`ac_ui::automation::ScriptRunner`]) played
//! against this window, and the world lifecycle automation behind it
//! (`OPENAC_AUTOMATION_ARTIFACT_DIR`): named screenshots, checkpoints
//! written as JSON after the frame that took them, and signal files the
//! harness drops, all in one artifact folder.

use std::collections::{HashMap, VecDeque};
use std::fmt::Write as _;
use std::path::{Path, PathBuf};

use ac_core::items::ClientObjectTable;
use ac_ui::automation::{AutomationTarget, CheckpointStatus};
use ac_ui::input::InputAction;
use ac_ui::root::UiRoot;

use crate::frame::WorldOutcome;
use crate::renderer::WorldStats;
use crate::surface::AttachedWindow;
use crate::ui::window::UiScript;

/// The world lifecycle's state facts at a checkpoint.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct RevealFacts {
    /// The world is ready (the login reveal finished, a player is there).
    pub ready: bool,
    /// The normal world view was drawn.
    pub world_viewport_observed: bool,
    /// Portal space hides the world now.
    pub in_portal_space: bool,
    /// Portal arrivals shown so far.
    pub materializations: u32,
}

/// Whether the world is ready: a player is there and the login reveal's
/// readiness barrier has passed (not merely "not withholding yet": between
/// the player's arrival and the reveal's start nothing withholds, but
/// nothing is loaded either), and portal space does not hide the world.
#[must_use]
pub fn world_ready(
    has_player: bool,
    revealed: bool,
    withholding: bool,
    portal_hides: bool,
) -> bool {
    has_player && revealed && !withholding && !portal_hides
}

/// Whether the world view counts as seen: the normal world was drawn and
/// the world is ready. A frame drawn before readiness (character selection
/// over an empty world, the login reveal still withholding) does not count,
/// so `wait world-visible` waits for the world, not for any frame.
#[must_use]
pub fn world_viewport_observed(ready: bool, normal_world_drawn: bool) -> bool {
    ready && normal_world_drawn
}

/// An automation artifact's name: 1 to 80 letters, digits, `-` and `_`.
///
/// # Errors
/// The name, worded as OpenAC words it.
pub fn validate_artifact_name(name: &str) -> Result<(), String> {
    if name.trim().is_empty() || name.chars().count() > 80 {
        return Err("artifact name must contain 1-80 characters".into());
    }
    if name
        .chars()
        .any(|c| !(c.is_ascii_alphanumeric() || c == '-' || c == '_'))
    {
        return Err(format!(
            "artifact name '{name}' may contain only letters, digits, '-' and '_'"
        ));
    }
    Ok(())
}

/// The world lifecycle automation of one window.
#[derive(Debug)]
pub struct WorldAutomation {
    directory: PathBuf,
    sequence: u64,
    pending: VecDeque<(u64, String)>,
    finished: HashMap<u64, (CheckpointStatus, Option<String>)>,
    screenshots: HashMap<String, PathBuf>,
}

impl WorldAutomation {
    /// Artifacts in `directory`.
    #[must_use]
    pub fn new(directory: PathBuf) -> Self {
        Self {
            directory,
            sequence: 0,
            pending: VecDeque::new(),
            finished: HashMap::new(),
            screenshots: HashMap::new(),
        }
    }

    /// The artifact folder.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// Queue checkpoint `name`; its sequence.
    ///
    /// # Errors
    /// A bad name.
    pub fn request_checkpoint(&mut self, name: &str) -> Result<u64, String> {
        validate_artifact_name(name)?;
        self.sequence += 1;
        self.pending.push_back((self.sequence, name.to_owned()));
        Ok(self.sequence)
    }

    /// How checkpoint `sequence` went.
    #[must_use]
    pub fn status(&self, sequence: u64) -> (CheckpointStatus, Option<String>) {
        if self.pending.iter().any(|(s, _)| *s == sequence) {
            return (CheckpointStatus::Pending, None);
        }
        self.finished.get(&sequence).cloned().unwrap_or((
            CheckpointStatus::Failed,
            Some(format!("checkpoint {sequence} is unknown")),
        ))
    }

    /// Drop checkpoint `sequence` if not written yet.
    pub fn cancel(&mut self, sequence: u64) {
        if let Some(i) = self.pending.iter().position(|(s, _)| *s == sequence) {
            let (_, name) = self.pending.remove(i).unwrap_or_default();
            self.finished.insert(
                sequence,
                (
                    CheckpointStatus::Cancelled,
                    Some(format!(
                        "checkpoint '{name}' was cancelled before render-frame capture"
                    )),
                ),
            );
        }
    }

    /// After a frame: write every queued checkpoint with what the frame
    /// drew; the timeline lines go to `log`.
    pub fn after_frame(
        &mut self,
        reveal: RevealFacts,
        world: &WorldOutcome,
        stats: Option<&WorldStats>,
        log: &mut Vec<String>,
    ) {
        while let Some((sequence, name)) = self.pending.pop_front() {
            let json = checkpoint_json(sequence, &name, reveal, world, stats);
            let timeline = self.directory.join("world-lifecycle.checkpoints.jsonl");
            let written = std::fs::create_dir_all(&self.directory)
                .and_then(|()| {
                    std::fs::write(
                        self.directory.join(format!("checkpoint-{name}.json")),
                        format!("{json}\n"),
                    )
                })
                .and_then(|()| {
                    use std::io::Write as _;
                    let mut file = std::fs::OpenOptions::new()
                        .create(true)
                        .append(true)
                        .open(&timeline)?;
                    writeln!(file, "{json}")
                });
            let status = match written {
                Ok(()) => {
                    log.push(format!(
                        "[world-gate] checkpoint name={name} sequence={sequence} path={}",
                        timeline.display()
                    ));
                    (CheckpointStatus::Succeeded, None)
                }
                Err(e) => (
                    CheckpointStatus::Failed,
                    Some(format!(
                        "checkpoint '{name}' sequence {sequence} failed: {e}"
                    )),
                ),
            };
            self.finished.insert(sequence, status);
        }
    }

    /// The path a screenshot named `name` is to be written to (an earlier
    /// file of that name is removed), or `None` when this run already asked
    /// for it: a name is captured once, and asking again (as a waiting
    /// script command does every frame) only waits for that capture (OpenAC
    /// `FrameScreenshotController.TryRequest`).
    ///
    /// # Errors
    /// A bad name, or the folder cannot be made.
    pub fn screenshot_path(&mut self, name: &str) -> Result<Option<PathBuf>, String> {
        validate_artifact_name(name)?;
        if self.screenshots.contains_key(name) {
            return Ok(None);
        }
        std::fs::create_dir_all(&self.directory).map_err(|e| e.to_string())?;
        let path = self.directory.join(format!("{name}.png"));
        let _ = std::fs::remove_file(&path);
        self.screenshots.insert(name.to_owned(), path.clone());
        Ok(Some(path))
    }

    /// The screenshot named `name` was asked for here and is written.
    #[must_use]
    pub fn screenshot_complete(&self, name: &str) -> bool {
        self.screenshots.get(name).is_some_and(|p| p.is_file())
    }

    /// Whether the harness dropped `signals/<name>.signal`.
    ///
    /// # Errors
    /// A bad name.
    pub fn signal_published(&self, name: &str) -> Result<bool, String> {
        validate_artifact_name(name)?;
        Ok(self
            .directory
            .join("signals")
            .join(format!("{name}.signal"))
            .is_file())
    }
}

/// One checkpoint as one JSON line.
#[must_use]
pub fn checkpoint_json(
    sequence: u64,
    name: &str,
    reveal: RevealFacts,
    world: &WorldOutcome,
    stats: Option<&WorldStats>,
) -> String {
    let mut out = String::new();
    let _ = write!(out, "{{\"sequence\":{sequence},\"name\":");
    ac_host::json::write_string(&mut out, name);
    out.push_str(",\"timestampUtc\":");
    ac_host::json::write_string(
        &mut out,
        &crate::profiler::utc_iso8601(std::time::SystemTime::now()),
    );
    let _ = write!(
        out,
        ",\"processId\":{},\"reveal\":{{\"isReady\":{},\"worldViewportObserved\":{},\"inPortalSpace\":{},\"materializations\":{}}}",
        std::process::id(),
        reveal.ready,
        reveal.world_viewport_observed,
        reveal.in_portal_space,
        reveal.materializations
    );
    let _ = write!(
        out,
        ",\"render\":{{\"world\":{{\"visibleLandblocks\":{},\"totalLandblocks\":{},\"normalWorldDrawn\":{}}}}}",
        world.visible_landblocks, world.total_landblocks, world.normal_world_drawn
    );
    out.push_str(",\"resources\":");
    match stats {
        Some(s) => {
            let _ = write!(
                out,
                "{{\"loadedLandblocks\":{},\"visibleLandblocks\":{},\"objectsDrawn\":{},\"cellsDrawn\":{},\"meshes\":{},\"surfaces\":{},\"appearances\":{},\"variants\":{},\"recolouredSurfaces\":{},\"recolouredSurfaceBytes\":{},\"animatedObjects\":{},\"animatedStatics\":{},\"particleEmitters\":{},\"particles\":{},\"viewerCell\":{}}}",
                s.landblocks,
                s.landblocks_drawn,
                s.objects_drawn,
                s.cells_drawn,
                s.meshes,
                s.surfaces,
                s.appearances,
                s.variants,
                s.recoloured_surfaces,
                s.recoloured_surface_bytes,
                s.animated_objects,
                s.animated_statics,
                s.emitters,
                s.particles,
                s.viewer_cell
            );
        }
        None => out.push_str("null"),
    }
    out.push('}');
    out
}

/// The window as a script's target.
pub(crate) struct WindowTarget<'a> {
    pub(crate) window: &'a mut AttachedWindow,
    pub(crate) empty: UiRoot,
}

impl AutomationTarget for WindowTarget<'_> {
    fn ui(&self) -> &UiRoot {
        self.window.ui_root().unwrap_or(&self.empty)
    }

    fn objects(&self) -> Option<&ClientObjectTable> {
        self.window.ui_objects()
    }

    fn mouse_move(&mut self, x: i32, y: i32) {
        self.window.script_ui(&UiScript::Move { x, y });
    }

    fn mouse_button(&mut self, x: i32, y: i32, pressed: bool) {
        self.window.script_ui(&if pressed {
            UiScript::Press { x, y }
        } else {
            UiScript::Release { x, y }
        });
    }

    fn submit_command(&mut self, text: &str) -> Result<(), String> {
        self.window.submit_line(text)
    }

    fn press_input(&mut self, action: InputAction) -> Result<(), String> {
        self.window.automation_action(action, None)
    }

    fn set_input_held(&mut self, action: InputAction, held: bool) -> Result<(), String> {
        self.window.automation_action(action, Some(held))
    }

    fn mouse_look(&mut self, dx: f32, dy: f32) -> Result<(), String> {
        self.window.automation_mouse_look(dx, dy);
        Ok(())
    }

    fn world_ready(&self) -> Option<bool> {
        Some(self.window.reveal_facts().ready)
    }

    fn world_visible(&self) -> Option<bool> {
        Some(self.window.reveal_facts().world_viewport_observed)
    }

    fn portal_materializations(&self) -> Option<u32> {
        Some(self.window.reveal_facts().materializations)
    }

    fn framebuffer(&self) -> Option<(u32, u32)> {
        Some(self.window.framebuffer_size())
    }

    fn request_checkpoint(&mut self, name: &str) -> Result<u64, String> {
        self.window
            .world_automation()
            .ok_or("checkpoints require OPENAC_AUTOMATION_ARTIFACT_DIR")?
            .request_checkpoint(name)
    }

    fn checkpoint_status(&self, sequence: u64) -> (CheckpointStatus, Option<String>) {
        self.window.world_automation_ref().map_or(
            (
                CheckpointStatus::Failed,
                Some("checkpoints require OPENAC_AUTOMATION_ARTIFACT_DIR".into()),
            ),
            |a| a.status(sequence),
        )
    }

    fn cancel_checkpoint(&mut self, sequence: u64) {
        if let Some(a) = self.window.world_automation() {
            a.cancel(sequence);
        }
    }

    fn request_screenshot(&mut self, name: &str) -> Result<(), String> {
        let path = self
            .window
            .world_automation()
            .ok_or("screenshots require OPENAC_AUTOMATION_ARTIFACT_DIR")?
            .screenshot_path(name)?;
        if let Some(path) = path {
            self.window.request_screenshot(path);
        }
        Ok(())
    }

    fn screenshot_complete(&self, name: &str) -> bool {
        self.window
            .world_automation_ref()
            .is_some_and(|a| a.screenshot_complete(name))
    }

    fn signal_published(&self, name: &str) -> Result<bool, String> {
        self.window
            .world_automation_ref()
            .ok_or("automation signals require OPENAC_AUTOMATION_ARTIFACT_DIR")?
            .signal_published(name)
    }

    fn set_potato_mode(&mut self, on: bool) -> Result<(), String> {
        self.window.automation_display(|d| d.potato_mode = on)
    }

    fn set_ui_only(&mut self, on: bool) -> Result<(), String> {
        self.window.automation_display(|d| d.ui_only = on)
    }

    fn set_ui_only_when_unfocused(&mut self, on: bool) -> Result<(), String> {
        self.window
            .automation_display(|d| d.ui_only_when_unfocused = on)
    }

    fn set_window_focused(&mut self, focused: bool) -> Result<(), String> {
        self.window.focus_changed(focused);
        Ok(())
    }

    fn resize(&mut self, width: u32, height: u32) -> Result<(), String> {
        self.window.automation_resize(width, height)
    }

    fn close_client(&mut self) -> Result<(), String> {
        self.window.request_close();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_viewport_is_not_observed_before_the_world_is_ready() {
        // OpenAC RuntimeWorldTransitStateTests: a viewport acknowledged
        // before readiness is refused ("viewport-before-ready") and leaves
        // WorldViewportObserved false; after readiness it is accepted.
        assert!(!world_viewport_observed(false, true));
        assert!(!world_viewport_observed(false, false));
        assert!(!world_viewport_observed(true, false));
        assert!(world_viewport_observed(true, true));
    }

    #[test]
    fn the_world_is_ready_only_once_the_login_reveal_has_passed() {
        // OpenAC RuntimeWorldTransitState: Readiness.IsReady comes from the
        // reveal's readiness barrier, so the frame after ENTER (a player,
        // the reveal not begun, nothing withholding) is not ready yet.
        assert!(!world_ready(true, false, false, false));
        assert!(!world_ready(true, false, true, false));
        assert!(!world_ready(false, true, false, false));
        assert!(!world_ready(true, true, false, true));
        assert!(world_ready(true, true, false, false));
    }

    #[test]
    fn artifact_names_are_letters_digits_dashes_and_underscores() {
        // OpenAC `AutomationArtifactName.TryValidate`.
        assert!(validate_artifact_name("before-travel_1").is_ok());
        assert!(validate_artifact_name("").is_err());
        assert!(validate_artifact_name("a b").is_err());
        assert!(validate_artifact_name("../x").is_err());
        assert!(validate_artifact_name(&"x".repeat(81)).is_err());
        assert!(validate_artifact_name(&"x".repeat(80)).is_ok());
    }

    #[test]
    fn a_checkpoint_is_written_after_the_frame_and_to_the_timeline() {
        // OpenAC WorldLifecycleAutomationControllerTests: one JSON per
        // checkpoint and a line per checkpoint in the timeline, written
        // after the frame that took them, with the frame's world counts.
        let dir = std::env::temp_dir().join(format!(
            "openac-world-automation-test-{}",
            std::process::id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        let mut a = WorldAutomation::new(dir.clone());
        let first = a.request_checkpoint("before").unwrap();
        let second = a.request_checkpoint("after").unwrap();
        assert_eq!((first, second), (1, 2));
        assert_eq!(a.status(first).0, CheckpointStatus::Pending);
        assert!(a.request_checkpoint("bad name").is_err());
        let world = WorldOutcome {
            visible_landblocks: 4,
            total_landblocks: 9,
            normal_world_drawn: true,
        };
        let stats = WorldStats {
            landblocks: 9,
            ..WorldStats::default()
        };
        let mut log = Vec::new();
        a.after_frame(
            RevealFacts {
                ready: true,
                world_viewport_observed: true,
                in_portal_space: false,
                materializations: 1,
            },
            &world,
            Some(&stats),
            &mut log,
        );
        assert_eq!(a.status(first).0, CheckpointStatus::Succeeded);
        assert_eq!(a.status(second).0, CheckpointStatus::Succeeded);
        let one = std::fs::read_to_string(dir.join("checkpoint-before.json")).unwrap();
        assert!(one.contains("\"name\":\"before\""), "{one}");
        assert!(one.contains("\"visibleLandblocks\":4"), "{one}");
        assert!(one.contains("\"normalWorldDrawn\":true"), "{one}");
        assert!(one.contains("\"isReady\":true"), "{one}");
        assert!(one.contains("\"loadedLandblocks\":9"), "{one}");
        assert!(one.contains("\"recolouredSurfaceBytes\":0"), "{one}");
        let timeline =
            std::fs::read_to_string(dir.join("world-lifecycle.checkpoints.jsonl")).unwrap();
        assert_eq!(timeline.lines().count(), 2);
        assert!(log.iter().all(|l| l.starts_with("[world-gate] checkpoint")));
        // A cancelled one is never written.
        let third = a.request_checkpoint("never").unwrap();
        a.cancel(third);
        assert_eq!(a.status(third).0, CheckpointStatus::Cancelled);
        a.after_frame(RevealFacts::default(), &world, None, &mut log);
        assert!(!dir.join("checkpoint-never.json").exists());
        // Signals are files the harness drops.
        assert!(!a.signal_published("go").unwrap());
        std::fs::create_dir_all(dir.join("signals")).unwrap();
        std::fs::write(dir.join("signals").join("go.signal"), "").unwrap();
        assert!(a.signal_published("go").unwrap());
        assert!(a.signal_published("../go").is_err());
        // A screenshot is complete once its file is written.
        let path = a.screenshot_path("shot").unwrap().unwrap();
        assert!(!a.screenshot_complete("shot"));
        // Asking again while it is pending (a waiting script command asks
        // every frame) captures nothing new.
        assert_eq!(a.screenshot_path("shot").unwrap(), None);
        std::fs::write(&path, b"png").unwrap();
        assert!(a.screenshot_complete("shot"));
        assert_eq!(a.screenshot_path("shot").unwrap(), None);
        assert!(a.screenshot_complete("shot"));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
