//! The map and house panel in the game window, as OpenAC's
//! `RetailUiRuntime.MountMapHousePanel` mounts it (the main panel frame's
//! slot 0x1000018C at (240, 160), main panel 16, F10), fed the in-game
//! date, the player's cell and the house every frame it shows.

use std::time::Instant;

use ac_core::world::CalendarDate;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::AnchorEdges;
use ac_ui::import::{BuildContext, LayoutSource, build, import_infos_rooted};
use ac_ui::panels::main_panels::RESTORE_PREVIOUS_PROPERTY;
use ac_ui::panels::map_house::{self, MapFacts, MapHousePanelController};
use ac_ui::panels::{DatRowTemplates, panel_id};
use ac_ui::widgets::NineSliceFrame;

use super::mount::{Chrome, MountOptions, mount_window};
use super::social_panels::authors_full_panel_center;
use super::window::WindowUi;
use crate::scene::Scene;

/// The map and house panel's name.
pub const MAP_HOUSE_WINDOW: &str = "map-house";

/// What the map reads besides the session's facts: the in-game date (the
/// renderer's calendar) and when it was last fed.
#[derive(Debug, Default)]
pub struct MapWindowState {
    calendar: Option<CalendarDate>,
    last_fed: Option<Instant>,
}

impl WindowUi {
    /// The in-game date and hour (the renderer's calendar).
    pub fn set_calendar(&mut self, date: Option<CalendarDate>) {
        self.map_state.calendar = date;
    }

    /// OpenAC `RetailUiRuntime.MountMapHousePanel`.
    pub(super) fn mount_map_house(&mut self) {
        match self.mount_map_house_window() {
            Ok(()) => self.mounted.push(MAP_HOUSE_WINDOW),
            Err(e) => self.notices.push(format!("ui: {MAP_HOUSE_WINDOW}: {e}")),
        }
    }

    fn mount_map_house_window(&mut self) -> Result<(), String> {
        let info = import_infos_rooted(
            &self.content,
            map_house::HOST_LAYOUT_ID,
            map_house::SLOT_ELEMENT_ID,
        )
        .ok_or("layout 0x2100006E slot 0x1000018C is not in the dats")?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let ctx = BuildContext {
            source: &self.content,
            default_font: font.clone(),
            source_layout_did: map_house::HOST_LAYOUT_ID,
            media_seconds: self.host.root.media_seconds(),
        };
        let layout = build(&mut self.host.root, &info, &ctx);
        let mut builder = DatRowTemplates::new(&self.content, font);
        let ui = &mut self.host.root;
        let mut controller = MapHousePanelController::bind(ui, &info, &layout, &mut builder)
            .ok_or("the panel's root is not a tab panel")?;
        controller.activate_tabs(ui);
        let frame = mount_window(
            ui,
            MAP_HOUSE_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 240.0,
                top: 160.0,
                visible: false,
                resize_x: false,
                resize_y: false,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::ALL,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
            f.draw_center_fill = !authors_full_panel_center(&info);
        }
        self.host
            .attach_controller(MAP_HOUSE_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        let restore = info.effective_bool(RESTORE_PREVIOUS_PROPERTY, None) == Some(true);
        self.panels.register(
            &mut self.host.root,
            panel_id::MAP_HOUSE,
            MAP_HOUSE_WINDOW,
            restore,
        );
        Ok(())
    }

    /// Feed the panel every frame (OpenAC ticks it): the map refreshes
    /// every five seconds, the house's lines when they change.
    pub(super) fn update_map_house(&mut self, scene: &Scene) {
        if !self.host.has_controller(MAP_HOUSE_WINDOW) {
            return;
        }
        let now = Instant::now();
        let dt = self
            .map_state
            .last_fed
            .replace(now)
            .map_or(0.0, |t| now.duration_since(t).as_secs_f64());
        let player = self.facts.state.player;
        let player_cell = scene
            .object_by_guid(player)
            .and_then(|o| o.position.as_ref())
            .map_or(0, |p| p.cell_id);
        let house = self.facts.house.as_ref();
        let facts = MapFacts {
            calendar: self.map_state.calendar,
            player_cell,
            house_cell: house.and_then(|h| h.position_cell),
        };
        let lines = house.map(|h| h.lines.clone()).unwrap_or_default();
        let Self { host, content, .. } = self;
        let mut rows = DatRowTemplates::new(&*content, content.font(DEFAULT_FONT_ID));
        host.with_controller::<MapHousePanelController, _>(MAP_HOUSE_WINDOW, |c, ui| {
            c.update(ui, &facts, dt, &lines, &mut rows);
        });
    }

    /// F10 (OpenAC `ToggleWorldPanel`): the panel opens or closes.
    pub(super) fn toggle_map_house(&mut self) -> bool {
        if !self.host.has_controller(MAP_HOUSE_WINDOW) {
            return false;
        }
        self.panels.toggle(&mut self.host.root, panel_id::MAP_HOUSE);
        true
    }
}
