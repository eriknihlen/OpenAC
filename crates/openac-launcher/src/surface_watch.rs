//! Keeping the window drawable when its GPU surface stops handing out
//! images: what to do after each attempt, which changes to log, and a CPU
//! copy of every egui texture so a rebuilt renderer gets them all back.
//!
//! A surface can stop giving images for a long time: the window covered or
//! the session locked (occluded), a busy or switching display (timeout), or
//! a display device swapped underneath the window, as a Remote Desktop
//! reconnect does (the surface or the whole device lost). Asking again at
//! once in those cases builds frames nobody sees as fast as the CPU allows,
//! so the window waits [`RETRY_DELAY`] between attempts, and a lost surface
//! or device is rebuilt from the window instead of being reconfigured.

use std::collections::HashMap;
use std::time::Duration;

/// How long the window waits before asking a surface that gave no image
/// again.
pub const RETRY_DELAY: Duration = Duration::from_millis(100);

/// The longest wait between two rebuilds of the GPU state. A rebuild that
/// does not end in a drawn frame doubles the wait from [`RETRY_DELAY`] up
/// to this, so a display that stays away costs a rebuild every few
/// seconds, not ten a second.
pub const MAX_REBUILD_WAIT: Duration = Duration::from_secs(5);

/// What the last attempt to get an image found.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SurfaceState {
    /// An image was handed out.
    Ready,
    /// The swapchain no longer matches the window; it was reconfigured.
    Outdated,
    /// No image arrived in time.
    Timeout,
    /// The window cannot be seen.
    Occluded,
    /// The surface reported a validation error.
    Validation,
    /// The surface is gone.
    Lost,
    /// The GPU device is gone.
    DeviceLost,
    /// Rebuilding the GPU state failed.
    RebuildFailed,
}

impl SurfaceState {
    fn describe(self) -> &'static str {
        match self {
            Self::Ready => "drawing",
            Self::Outdated => "outdated (reconfigured)",
            Self::Timeout => "timed out",
            Self::Occluded => "occluded",
            Self::Validation => "failed validation",
            Self::Lost => "lost",
            Self::DeviceLost => "GPU device lost",
            Self::RebuildFailed => "could not be rebuilt",
        }
    }
}

/// What the window does after an attempt.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NextAttempt {
    /// Draw into the image.
    Draw,
    /// Try again at once.
    Now,
    /// Try again after this long.
    After(Duration),
    /// Rebuild the GPU state from the window, then try again.
    Rebuild,
}

/// Follows the surface from attempt to attempt.
#[derive(Debug, Default)]
pub struct SurfaceWatch {
    last: Option<SurfaceState>,
    /// The wait before the next rebuild; zero until one has been tried
    /// since the last drawn frame.
    rebuild_wait: Duration,
}

impl SurfaceWatch {
    /// Records what an attempt found: what to do next, and a line to log
    /// when the state changed (the first image is not news).
    pub fn observe(&mut self, state: SurfaceState) -> (NextAttempt, Option<String>) {
        let previous = self.last.replace(state);
        if state == SurfaceState::Ready {
            self.rebuild_wait = Duration::ZERO;
        }
        let next = match state {
            SurfaceState::Ready => NextAttempt::Draw,
            // A reconfigured swapchain usually draws at once; one that stays
            // outdated (the window mid-change) waits like any other refusal.
            SurfaceState::Outdated if previous != Some(SurfaceState::Outdated) => NextAttempt::Now,
            SurfaceState::Outdated
            | SurfaceState::Timeout
            | SurfaceState::Occluded
            | SurfaceState::Validation
            | SurfaceState::RebuildFailed => NextAttempt::After(RETRY_DELAY),
            SurfaceState::Lost | SurfaceState::DeviceLost => NextAttempt::Rebuild,
        };
        let line = match previous {
            Some(was) if was == state => None,
            None if state == SurfaceState::Ready => None,
            Some(was) => Some(format!(
                "launcher window: surface {} (was {})",
                state.describe(),
                was.describe()
            )),
            None => Some(format!("launcher window: surface {}", state.describe())),
        };
        (next, line)
    }

    /// The wait to keep after a rebuild before trying another: it doubles
    /// with every rebuild until a frame is drawn again.
    pub fn next_rebuild_wait(&mut self) -> Duration {
        let wait = self.rebuild_wait.max(RETRY_DELAY);
        self.rebuild_wait = (wait * 2).min(MAX_REBUILD_WAIT);
        wait
    }
}

/// A CPU copy of every texture egui has handed the renderer, kept current
/// with egui's changes, so a renderer built on a new device can be given
/// all of them again.
#[derive(Debug, Default)]
pub struct TextureMirror {
    textures: HashMap<egui::TextureId, (egui::ColorImage, egui::TextureOptions)>,
}

impl TextureMirror {
    /// Applies one of egui's texture changes: a whole image, or a patch of
    /// an image already held.
    pub fn apply(&mut self, id: egui::TextureId, delta: &egui::epaint::ImageDelta) {
        let egui::ImageData::Color(image) = &delta.image;
        match delta.pos {
            None => {
                self.textures.insert(id, ((**image).clone(), delta.options));
            }
            Some([x, y]) => {
                let Some((target, options)) = self.textures.get_mut(&id) else {
                    return;
                };
                *options = delta.options;
                let [width, height] = image.size;
                let [target_width, target_height] = target.size;
                // A patch reaching past the image it belongs to is not one
                // egui makes; it is left out rather than written wrongly.
                if x + width > target_width || y + height > target_height {
                    return;
                }
                for row in 0..height {
                    let from = row * width;
                    let to = (y + row) * target_width + x;
                    target.pixels[to..to + width]
                        .copy_from_slice(&image.pixels[from..from + width]);
                }
            }
        }
    }

    /// Forgets a texture egui freed.
    pub fn free(&mut self, id: egui::TextureId) {
        self.textures.remove(&id);
    }

    /// Every texture held, as a whole-image change.
    pub fn full_images(&self) -> Vec<(egui::TextureId, egui::epaint::ImageDelta)> {
        self.textures
            .iter()
            .map(|(id, (image, options))| {
                (*id, egui::epaint::ImageDelta::full(image.clone(), *options))
            })
            .collect()
    }

    /// How many textures are held.
    #[cfg(test)]
    fn len(&self) -> usize {
        self.textures.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn image(width: usize, height: usize, value: u8) -> egui::ColorImage {
        egui::ColorImage::new(
            [width, height],
            vec![egui::Color32::from_gray(value); width * height],
        )
    }

    #[test]
    fn the_first_image_draws_quietly() {
        let mut watch = SurfaceWatch::default();
        assert_eq!(
            watch.observe(SurfaceState::Ready),
            (NextAttempt::Draw, None)
        );
        assert_eq!(
            watch.observe(SurfaceState::Ready),
            (NextAttempt::Draw, None)
        );
    }

    #[test]
    fn a_surface_that_gives_no_image_is_asked_again_later_never_at_once() {
        for state in [
            SurfaceState::Timeout,
            SurfaceState::Occluded,
            SurfaceState::Validation,
            SurfaceState::RebuildFailed,
        ] {
            let mut watch = SurfaceWatch::default();
            watch.observe(SurfaceState::Ready);
            for _ in 0..3 {
                assert_eq!(watch.observe(state).0, NextAttempt::After(RETRY_DELAY));
            }
        }
    }

    #[test]
    fn an_outdated_surface_retries_at_once_only_the_first_time() {
        let mut watch = SurfaceWatch::default();
        watch.observe(SurfaceState::Ready);
        assert_eq!(watch.observe(SurfaceState::Outdated).0, NextAttempt::Now);
        assert_eq!(
            watch.observe(SurfaceState::Outdated).0,
            NextAttempt::After(RETRY_DELAY)
        );
        watch.observe(SurfaceState::Ready);
        assert_eq!(watch.observe(SurfaceState::Outdated).0, NextAttempt::Now);
    }

    #[test]
    fn a_lost_surface_or_device_is_rebuilt() {
        let mut watch = SurfaceWatch::default();
        assert_eq!(watch.observe(SurfaceState::Lost).0, NextAttempt::Rebuild);
        assert_eq!(
            watch.observe(SurfaceState::DeviceLost).0,
            NextAttempt::Rebuild
        );
    }

    #[test]
    fn rebuilds_back_off_until_a_frame_is_drawn() {
        let mut watch = SurfaceWatch::default();
        let waits: Vec<u128> = (0..8)
            .map(|_| watch.next_rebuild_wait().as_millis())
            .collect();
        assert_eq!(waits, [100, 200, 400, 800, 1600, 3200, 5000, 5000]);
        watch.observe(SurfaceState::Lost);
        assert_eq!(watch.next_rebuild_wait(), MAX_REBUILD_WAIT);
        watch.observe(SurfaceState::Ready);
        assert_eq!(watch.next_rebuild_wait(), RETRY_DELAY);
    }

    #[test]
    fn each_change_is_logged_once() {
        let mut watch = SurfaceWatch::default();
        watch.observe(SurfaceState::Ready);
        assert_eq!(
            watch.observe(SurfaceState::Occluded).1.as_deref(),
            Some("launcher window: surface occluded (was drawing)")
        );
        assert_eq!(watch.observe(SurfaceState::Occluded).1, None);
        assert_eq!(
            watch.observe(SurfaceState::Ready).1.as_deref(),
            Some("launcher window: surface drawing (was occluded)")
        );
        let mut fresh = SurfaceWatch::default();
        assert_eq!(
            fresh.observe(SurfaceState::Timeout).1.as_deref(),
            Some("launcher window: surface timed out")
        );
    }

    #[test]
    fn the_mirror_keeps_whole_images_and_patches() {
        let mut mirror = TextureMirror::default();
        let id = egui::TextureId::Managed(0);
        let options = egui::TextureOptions::LINEAR;
        mirror.apply(
            id,
            &egui::epaint::ImageDelta::full(image(4, 3, 10), options),
        );
        mirror.apply(
            id,
            &egui::epaint::ImageDelta::partial([1, 1], image(2, 2, 200), options),
        );
        let images = mirror.full_images();
        assert_eq!(images.len(), 1);
        let (got_id, delta) = &images[0];
        assert_eq!(*got_id, id);
        assert_eq!(delta.pos, None);
        let egui::ImageData::Color(full) = &delta.image;
        assert_eq!(full.size, [4, 3]);
        let gray: Vec<u8> = full.pixels.iter().map(|p| p.r()).collect();
        assert_eq!(gray, [10, 10, 10, 10, 10, 200, 200, 10, 10, 200, 200, 10]);
    }

    #[test]
    fn a_patch_outside_its_image_or_for_an_unknown_texture_changes_nothing() {
        let mut mirror = TextureMirror::default();
        let id = egui::TextureId::Managed(1);
        let options = egui::TextureOptions::NEAREST;
        mirror.apply(
            egui::TextureId::Managed(9),
            &egui::epaint::ImageDelta::partial([0, 0], image(1, 1, 5), options),
        );
        assert_eq!(mirror.len(), 0);
        mirror.apply(id, &egui::epaint::ImageDelta::full(image(2, 2, 1), options));
        mirror.apply(
            id,
            &egui::epaint::ImageDelta::partial([1, 1], image(2, 2, 9), options),
        );
        let images = mirror.full_images();
        let egui::ImageData::Color(full) = &images[0].1.image;
        assert!(full.pixels.iter().all(|p| p.r() == 1));
    }

    #[test]
    fn a_freed_texture_is_not_sent_again() {
        let mut mirror = TextureMirror::default();
        let options = egui::TextureOptions::LINEAR;
        mirror.apply(
            egui::TextureId::Managed(0),
            &egui::epaint::ImageDelta::full(image(1, 1, 1), options),
        );
        mirror.apply(
            egui::TextureId::Managed(1),
            &egui::epaint::ImageDelta::full(image(1, 1, 2), options),
        );
        mirror.free(egui::TextureId::Managed(0));
        let ids: Vec<_> = mirror.full_images().into_iter().map(|(id, _)| id).collect();
        assert_eq!(ids, [egui::TextureId::Managed(1)]);
    }
}
