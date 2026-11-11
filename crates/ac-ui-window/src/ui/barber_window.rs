//! The barber window in the game window: its slot of the game's main
//! layout (the barber layout at the slot's place, hidden, neither moved nor
//! resized), shown while the session's barber is open and fed its look.

use std::sync::Arc;

use ac_dat::DatCollection;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::import::{LayoutSource, import_infos_rooted, import_rooted};
use ac_ui::panels::barber::{self, BarberController};
use ac_ui::pregame::chargen::catalog::DatChargenCatalog;
use ac_ui::pregame::chargen::swatch::DatSwatchTextures;
use ac_ui::strings::compute_hash;

use crate::scene::Scene;

use super::facts::Dirty;
use super::mount::{Chrome, MountOptions, mount_window};
use super::window::WindowUi;

/// The barber window's name.
pub const BARBER_WINDOW: &str = "barber";

/// What the barber window paints its swatches from.
#[derive(Default)]
pub struct BarberWindow {
    swatches: Option<(DatChargenCatalog, DatSwatchTextures)>,
}

impl std::fmt::Debug for BarberWindow {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BarberWindow").finish_non_exhaustive()
    }
}

/// Where and how the window is mounted: at its slot's place in the main
/// layout, hidden until the barber opens.
#[must_use]
pub fn window_options(left: f32, top: f32) -> MountOptions {
    MountOptions {
        chrome: Chrome::Imported,
        left,
        top,
        visible: false,
        draggable: false,
        resizable: false,
        resize_x: false,
        resize_y: false,
        content_click_through: Some(false),
        ..MountOptions::default()
    }
}

impl WindowUi {
    /// Mount the barber window.
    pub(super) fn mount_barber(&mut self, dats: &Arc<DatCollection>) {
        match self.mount_barber_window(dats) {
            Ok(()) => self.mounted.push(BARBER_WINDOW),
            Err(e) => self.notices.push(format!("ui: {BARBER_WINDOW}: {e}")),
        }
    }

    fn mount_barber_window(&mut self, dats: &Arc<DatCollection>) -> Result<(), String> {
        let missing = "layout 0x2100000F slot 0x10000598 is not in the dats";
        let slot = import_infos_rooted(&self.content, barber::MAIN_LAYOUT_ID, barber::SLOT_ID)
            .ok_or(missing)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            barber::MAIN_LAYOUT_ID,
            barber::SLOT_ID,
            font,
        )
        .ok_or(missing)?;
        let ui = &mut self.host.root;
        mount_window(
            ui,
            BARBER_WINDOW,
            layout.root,
            &window_options(slot.x, slot.y),
        )?;
        let controller = BarberController::bind(ui, &layout);
        self.host
            .attach_controller(BARBER_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.barber.swatches = Some((
            DatChargenCatalog::new(Arc::clone(dats)),
            DatSwatchTextures::new(Arc::clone(dats), self.images.uploader()),
        ));
        Ok(())
    }

    /// Feed the window the barber when it changed (or its own pick did):
    /// an opening shows it, a close hides it.
    pub(super) fn update_barber(&mut self, dirty: Dirty, scene: &Scene) {
        let redraw = self
            .host
            .with_controller::<BarberController, _>(BARBER_WINDOW, |c, _| c.wants_redraw())
            .unwrap_or(false);
        if !dirty.barber && !redraw {
            return;
        }
        let Some(view) = self.facts.barber.clone() else {
            return;
        };
        let options = scene.pregame().options.clone();
        let gender = options.as_deref().and_then(|o| {
            o.try_get_heritage(view.heritage)?
                .genders_by_key
                .get(&i32::try_from(view.gender).ok()?)
                .cloned()
        });
        let Self {
            host,
            barber,
            content,
            ..
        } = self;
        let strings = content.strings();
        let text = |table: u32, key: &str| strings.resolve(table, compute_hash(key), 0);
        let (sources, textures) = match barber.swatches.as_mut() {
            Some((catalog, textures)) => (
                Some(catalog as &dyn ac_ui::pregame::chargen::appearance::SwatchSourcesDyn),
                Some(textures as &mut dyn ac_ui::pregame::chargen::appearance::SwatchTextures),
            ),
            None => (None, None),
        };
        let update = host.with_controller::<BarberController, _>(BARBER_WINDOW, |c, ui| {
            c.update(ui, &view, gender.as_ref(), sources, textures, &text)
        });
        match update.and_then(|u| u.visible) {
            Some(true) => {
                host.root.show_window(BARBER_WINDOW);
            }
            Some(false) => {
                host.root.hide_window(BARBER_WINDOW);
            }
            None => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_ui::root::UiRoot;
    use ac_ui::tree::Element;
    use ac_ui::widgets::Panel;

    /// The barber window stands at its slot's place, hidden, fixed.
    #[test]
    fn the_window_is_hidden_at_its_slot_and_fixed() {
        let mut ui = UiRoot::new();
        ui.set_size(1280.0, 720.0);
        let r = ui.root();
        let root = ui.add(r, Element::at(0.0, 0.0, 500.0, 440.0), Panel::default());
        let frame =
            mount_window(&mut ui, BARBER_WINDOW, root, &window_options(20.0, 20.0)).unwrap();
        let f = ui.element(frame).unwrap();
        assert_eq!(
            (f.left, f.top, f.width, f.height),
            (20.0, 20.0, 500.0, 440.0)
        );
        assert!(!f.resizable);
        assert!(!ui.is_window_visible(BARBER_WINDOW));
    }
}
