//! The outside-container window in the game window, as OpenAC's
//! `RetailUiRuntime.MountExternalContainer` mounts it
//! (`ExternalContainerController.CreateWindowOptions`), fed from the
//! session's open container and its range every frame one is open.

use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::ResizeEdges;
use ac_ui::import::{LayoutSource, import_rooted};
use ac_ui::panels::cell_template::empty_sprite;
use ac_ui::panels::external_container::{
    self, DEFAULT_CONTENT_WIDTH, ExternalContainerController, ExternalContainerFacts,
    ExternalContainerSprites, MINIMUM_CONTENT_WIDTH,
};
use ac_ui::panels::toolbar::ToolbarWindowController;
use ac_ui::widgets::NineSliceFrame;
use ac_ui::widgets::chrome::BORDER;

use ac_ui::UiHost;
use ac_ui::icons::IconSource;
use ac_ui::panels::appraisal::NameTables;

use super::facts::{InterfaceFacts, composed_names};
use super::mount::{Chrome, MountOptions, mount_window};
use super::window::{TOOLBAR_WINDOW, WindowUi};

/// The outside-container window's name.
pub const EXTERNAL_CONTAINER_WINDOW: &str = "external-container";

/// Where and how the window is mounted for a layout root at (`left`,
/// `top`) of `width` x `height` (OpenAC
/// `ExternalContainerController.CreateWindowOptions`).
#[must_use]
pub fn window_options(left: f32, top: f32, width: f32, height: f32) -> MountOptions {
    MountOptions {
        chrome: Chrome::NineSlice,
        left,
        top,
        content_width: Some(width.min(DEFAULT_CONTENT_WIDTH)),
        content_height: Some(height),
        min_width: Some(MINIMUM_CONTENT_WIDTH + 2.0 * BORDER as f32),
        visible: false,
        draggable: true,
        resizable: true,
        resize_x: true,
        resize_y: false,
        resizable_edges: ResizeEdges::LEFT | ResizeEdges::RIGHT,
        constrain_resize_to_parent: true,
        ..MountOptions::default()
    }
}

impl WindowUi {
    /// OpenAC `RetailUiRuntime.MountExternalContainer`.
    pub(super) fn mount_external_container(&mut self) {
        match self.mount_external_container_window() {
            Ok(()) => self.mounted.push(EXTERNAL_CONTAINER_WINDOW),
            Err(e) => self
                .notices
                .push(format!("ui: {EXTERNAL_CONTAINER_WINDOW}: {e}")),
        }
    }

    fn mount_external_container_window(&mut self) -> Result<(), String> {
        let sprites = ExternalContainerSprites {
            contents_empty: empty_sprite(
                &self.content,
                external_container::LAYOUT_ID,
                external_container::CONTENTS_LIST_ID,
            ),
            container_empty: empty_sprite(
                &self.content,
                external_container::LAYOUT_ID,
                external_container::CONTAINER_LIST_ID,
            ),
        };
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            external_container::LAYOUT_ID,
            external_container::ROOT_ID,
            font,
        )
        .ok_or("layout 0x21000008 root 0x10000063 is not in the dats")?;
        let ui = &mut self.host.root;
        let (left, top, width, height) = ui
            .element(layout.root)
            .map_or((0.0, 0.0, 0.0, 0.0), |e| (e.left, e.top, e.width, e.height));
        let frame = mount_window(
            ui,
            EXTERNAL_CONTAINER_WINDOW,
            layout.root,
            &window_options(left, top, width, height),
        )?;
        if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
            f.draw_center_fill = false;
        }
        let controller =
            ExternalContainerController::bind(ui, &layout, EXTERNAL_CONTAINER_WINDOW, sprites)
                .ok_or("required lists or the close button are missing")?;
        self.host
            .attach_controller(EXTERNAL_CONTAINER_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// Feed the outside-container window while the session has one open
    /// (or the window still shows the last one).
    pub(super) fn update_external_container(&mut self) {
        let selected = self.player_view.and_then(|v| v.selected);
        let Self {
            host,
            facts,
            icons,
            status,
            ..
        } = self;
        feed_external_container(host, facts, icons, &status.names, selected);
    }
}

/// Feed the outside-container window from `facts`: its cells caption each
/// item by its composed name (`names` puts the material in front), as
/// every item surface does.
pub(super) fn feed_external_container(
    host: &mut UiHost,
    facts: &InterfaceFacts,
    icons: &mut dyn IconSource,
    names: &NameTables,
    selected: Option<u32>,
) {
    let external = ExternalContainerFacts {
        container: facts.state.external_container,
        in_range: facts.state.external_container_in_range,
    };
    let shown = host
        .with_controller::<ExternalContainerController, _>(EXTERNAL_CONTAINER_WINDOW, |c, _| {
            c.open_container() != 0
        })
        .unwrap_or(false);
    if external.container == 0 && !shown {
        return;
    }
    let split = host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, _| {
        c.split.quantity().clone()
    });
    let pending: Vec<u32> = facts.state.pending_item.into_iter().collect();
    let resolve = composed_names(names);
    let view = facts.items_view(selected, &pending, split.as_ref(), &resolve);
    host.with_controller::<ExternalContainerController, _>(EXTERNAL_CONTAINER_WINDOW, |c, ui| {
        c.update(ui, &view, external, icons);
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_ui::root::UiRoot;
    use ac_ui::tree::{Element, ElementId};
    use ac_ui::widgets::Panel;

    /// OpenAC `ExternalContainerControllerTests.WindowPolicy_AddsOuterFrameAndAllowsHorizontalResize`.
    #[test]
    fn the_window_adds_its_frame_and_resizes_only_sideways() {
        let mut ui = UiRoot::new();
        ui.set_size(1280.0, 720.0);
        let r = ui.root();
        let root = ui.add(r, Element::at(0.0, 400.0, 800.0, 110.0), Panel::default());
        let frame = mount_window(
            &mut ui,
            EXTERNAL_CONTAINER_WINDOW,
            root,
            &window_options(0.0, 400.0, 800.0, 110.0),
        )
        .unwrap();
        let f = ui.element(frame).unwrap();
        assert_eq!((f.width, f.height), (710.0, 120.0));
        assert_eq!(f.min_width, 170.0);
        assert!(f.draggable && f.resizable && f.resize_x && !f.resize_y);
        assert_eq!(f.resizable_edges, ResizeEdges::LEFT | ResizeEdges::RIGHT);
        assert!(f.constrain_resize_to_parent);
        assert_eq!(ui.element(root).unwrap().width, 700.0);
        assert!(!ui.is_window_visible(EXTERNAL_CONTAINER_WINDOW));
    }

    /// The outside-container window mounted on a synthetic layout shaped
    /// like the retail one: its three lists and its close button.
    fn external_container_host() -> (UiHost, ElementId) {
        use ac_ui::element_info::ElementInfo;
        use ac_ui::widgets::{DatElement, ItemList};
        use std::collections::HashMap;
        use std::sync::Arc;
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let ui = &mut host.root;
        let r = ui.root();
        let root = ui.add(r, Element::at(0.0, 0.0, 800.0, 110.0), Panel::default());
        let mut ids = HashMap::new();
        for (id, x, y, w, h) in [
            (external_container::TOP_CONTAINER_ID, 0.0, 0.0, 36.0, 36.0),
            (
                external_container::CONTAINER_LIST_ID,
                40.0,
                0.0,
                680.0,
                36.0,
            ),
            (external_container::CONTENTS_LIST_ID, 0.0, 40.0, 784.0, 32.0),
        ] {
            ids.insert(id, ItemList::create(ui, root, Element::at(x, y, w, h)));
        }
        let close = ui.add(
            root,
            Element::at(0.0, 80.0, 16.0, 16.0),
            DatElement::new(Arc::new(ElementInfo::default())),
        );
        ids.insert(external_container::CLOSE_BUTTON_ID, close);
        let contents = ids[&external_container::CONTENTS_LIST_ID];
        let layout = ac_ui::import::ImportedLayout::from_parts(root, ids);
        ui.register_window(EXTERNAL_CONTAINER_WINDOW, root, None, 0)
            .unwrap();
        let c = ExternalContainerController::bind(
            ui,
            &layout,
            EXTERNAL_CONTAINER_WINDOW,
            ExternalContainerSprites::default(),
        )
        .unwrap();
        host.attach_controller(EXTERNAL_CONTAINER_WINDOW, Box::new(c))
            .unwrap();
        (host, contents)
    }

    /// GitHub issue #3: an item in a corpse hovers with its composed name,
    /// the material in front, as it does in the player's own inventory.
    #[test]
    fn a_corpse_item_hovers_with_its_material_in_front() {
        use ac_core::items::{ClientObject, EquipMask, ItemType, ItemUseable, PublicWeenieFlags};
        use ac_ui::widgets::{ItemList, ItemSlot};
        const PLAYER: u32 = 0x5000_0001;
        const CORPSE: u32 = 0x7000_0001;
        const BATON: u32 = 0x6000_0001;
        const RUBY: u32 = 0x26;
        let (mut host, contents) = external_container_host();
        let mut facts = InterfaceFacts::default();
        let mut events = Vec::new();
        let mut corpse = ClientObject::new(CORPSE);
        corpse.name = "Corpse of Drudge".into();
        corpse.item_type = ItemType::CONTAINER;
        corpse.items_capacity = 120;
        corpse.containers_capacity = 10;
        corpse.public_weenie_bitfield =
            Some(PublicWeenieFlags::OPENABLE | PublicWeenieFlags::CORPSE);
        corpse.useability = Some(ItemUseable::REMOTE);
        facts.objects.add_or_update(corpse, &mut events);
        let mut baton = ClientObject::new(BATON);
        baton.name = "Slashing Baton".into();
        baton.item_type = ItemType::MELEE_WEAPON;
        baton.material_type = Some(RUBY);
        facts.objects.add_or_update(baton, &mut events);
        facts
            .objects
            .move_item(BATON, CORPSE, 0, EquipMask::NONE, None, &mut events);
        facts.state.player = PLAYER;
        facts.state.external_container = CORPSE;
        facts.state.external_container_in_range = true;
        let names = NameTables {
            materials: std::collections::HashMap::from([(RUBY, "Ruby".to_owned())]),
            ..NameTables::default()
        };
        feed_external_container(&mut host, &facts, &mut ac_ui::icons::NoIcons, &names, None);
        let cell = host
            .root
            .widget::<ItemList>(contents)
            .and_then(|l| l.item(0))
            .unwrap();
        let slot = host.root.widget::<ItemSlot>(cell).unwrap();
        assert_eq!(slot.object_id, BATON);
        assert_eq!(slot.tooltip.as_deref(), Some("Ruby Slashing Baton"));
    }
}
