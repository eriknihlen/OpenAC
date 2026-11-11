//! The salvage window in the game window, as OpenAC's
//! `RetailUiRuntime.MountSalvage` mounts it: hidden, centred, resizable
//! sideways; opened by the use of a salvaging tool (the session's
//! `OpenSalvage`) and fed while it is open.

use ac_runtime::character::option_id;
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::ResizeEdges;
use ac_ui::import::{LayoutSource, import_rooted};
use ac_ui::panels::cell_template::empty_sprite;
use ac_ui::panels::salvage::{self, MINIMUM_CONTENT_WIDTH, SalvageController};
use ac_ui::panels::toolbar::ToolbarWindowController;
use ac_ui::widgets::NineSliceFrame;
use ac_ui::widgets::chrome::BORDER;

use ac_ui::UiHost;
use ac_ui::icons::IconSource;
use ac_ui::panels::appraisal::NameTables;

use super::facts::{InterfaceFacts, composed_names};
use super::mount::{Chrome, MountOptions, mount_window};
use super::window::{TOOLBAR_WINDOW, WindowUi};

/// The salvage window's name.
pub const SALVAGE_WINDOW: &str = "salvage";

/// Where and how the window is mounted for a layout root of `width` x
/// `height` on a screen of `screen` (OpenAC `RetailUiRuntime.MountSalvage`).
#[must_use]
pub fn window_options(width: f32, height: f32, screen: (f32, f32)) -> MountOptions {
    let inset = 2.0 * BORDER as f32;
    let width = width.min(MINIMUM_CONTENT_WIDTH.max(screen.0 - inset));
    MountOptions {
        chrome: Chrome::NineSlice,
        left: ((screen.0 - width - inset) * 0.5).max(0.0),
        top: ((screen.1 - height - inset) * 0.5).max(0.0),
        content_width: Some(width),
        content_height: Some(height),
        min_width: Some(MINIMUM_CONTENT_WIDTH),
        min_height: Some(height),
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
    /// OpenAC `RetailUiRuntime.MountSalvage`.
    pub(super) fn mount_salvage(&mut self) {
        match self.mount_salvage_window() {
            Ok(()) => self.mounted.push(SALVAGE_WINDOW),
            Err(e) => self.notices.push(format!("ui: {SALVAGE_WINDOW}: {e}")),
        }
    }

    fn mount_salvage_window(&mut self) -> Result<(), String> {
        let empty = empty_sprite(&self.content, salvage::LAYOUT_ID, salvage::ITEM_LIST_ID);
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            salvage::LAYOUT_ID,
            salvage::ROOT_ID,
            font,
        )
        .ok_or("layout 0x2100000C root 0x10000073 is not in the dats")?;
        let ui = &mut self.host.root;
        let screen = ui.size();
        let (width, height) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let frame = mount_window(
            ui,
            SALVAGE_WINDOW,
            layout.root,
            &window_options(width, height, screen),
        )?;
        if let Some(f) = ui.widget_mut::<NineSliceFrame>(frame) {
            f.draw_center_fill = false;
        }
        let controller = SalvageController::bind(ui, &layout, SALVAGE_WINDOW, empty)
            .ok_or("the item row or the salvage button is missing")?;
        self.host
            .attach_controller(SALVAGE_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// Open the window on the salvaging tools the session says were used,
    /// and feed it while it is open.
    pub(super) fn update_salvage(&mut self) {
        let selected = self.player_view.and_then(|v| v.selected);
        let Self {
            host,
            facts,
            icons,
            status,
            ..
        } = self;
        feed_salvage(host, facts, icons, &status.names, selected);
    }
}

/// Open the salvage window on the tools `facts` says were used and feed
/// it while it is open: its cells caption each staged item by its
/// composed name (`names` puts the material in front), as every item
/// surface does.
pub(super) fn feed_salvage(
    host: &mut UiHost,
    facts: &mut InterfaceFacts,
    icons: &mut dyn IconSource,
    names: &NameTables,
    selected: Option<u32>,
) {
    let opened = std::mem::take(&mut facts.salvage_opens);
    let open = host
        .with_controller::<SalvageController, _>(SALVAGE_WINDOW, |c, _| c.tool() != 0)
        .unwrap_or(false);
    if opened.is_empty() && !open {
        return;
    }
    let allow_multiple = facts
        .social
        .as_ref()
        .is_some_and(|s| s.options.get(option_id::SALVAGE_MULTIPLE));
    let split = host.with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, _| {
        c.split.quantity().clone()
    });
    let pending: Vec<u32> = facts.state.pending_item.into_iter().collect();
    let resolve = composed_names(names);
    let view = facts.items_view(selected, &pending, split.as_ref(), &resolve);
    host.with_controller::<SalvageController, _>(SALVAGE_WINDOW, |c, ui| {
        c.update(ui, &view, &opened, allow_multiple, icons);
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_ui::root::UiRoot;
    use ac_ui::tree::Element;
    use ac_ui::widgets::Panel;

    /// OpenAC `RetailUiRuntime.MountSalvage`: centred, hidden, at least
    /// 240 wide, resized sideways only.
    #[test]
    fn the_window_is_centred_hidden_and_resizes_only_sideways() {
        let mut ui = UiRoot::new();
        ui.set_size(1280.0, 720.0);
        let r = ui.root();
        let root = ui.add(r, Element::at(0.0, 0.0, 400.0, 80.0), Panel::default());
        let frame = mount_window(
            &mut ui,
            SALVAGE_WINDOW,
            root,
            &window_options(400.0, 80.0, (1280.0, 720.0)),
        )
        .unwrap();
        let f = ui.element(frame).unwrap();
        assert_eq!((f.left, f.top), (435.0, 315.0));
        assert_eq!((f.width, f.height), (410.0, 90.0));
        assert_eq!(f.min_width, 240.0);
        assert!(f.resizable && f.resize_x && !f.resize_y);
        assert_eq!(f.resizable_edges, ResizeEdges::LEFT | ResizeEdges::RIGHT);
        assert!(!ui.is_window_visible(SALVAGE_WINDOW));
    }

    /// GitHub issue #3 (the salvage window has the same gap): a staged item
    /// hovers with its composed name, the material in front.
    #[test]
    fn a_staged_item_hovers_with_its_material_in_front() {
        use ac_core::items::{ClientObject, EquipMask, ItemType};
        use ac_ui::element_info::ElementInfo;
        use ac_ui::import::ImportedLayout;
        use ac_ui::panels::items::ItemsView;
        use ac_ui::widgets::{DatElement, ItemList, ItemSlot};
        use std::collections::HashMap;
        use std::sync::Arc;
        const PLAYER: u32 = 0x5000_0001;
        const TOOL: u32 = 0x6000_0101;
        const BATON: u32 = 0x6000_0001;
        const RUBY: u32 = 0x26;
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let ui = &mut host.root;
        let r = ui.root();
        let root = ui.add(r, Element::at(0.0, 0.0, 320.0, 80.0), Panel::default());
        let list = ItemList::create(ui, root, Element::at(0.0, 0.0, 320.0, 32.0));
        let button = ui.add(
            root,
            Element::at(0.0, 40.0, 32.0, 16.0),
            DatElement::new(Arc::new(ElementInfo::default())),
        );
        let ids = HashMap::from([
            (salvage::ITEM_LIST_ID, list),
            (salvage::SALVAGE_BUTTON_ID, button),
        ]);
        let layout = ImportedLayout::from_parts(root, ids);
        ui.register_window(SALVAGE_WINDOW, root, None, 0).unwrap();
        let c = SalvageController::bind(ui, &layout, SALVAGE_WINDOW, 0).unwrap();
        host.attach_controller(SALVAGE_WINDOW, Box::new(c)).unwrap();

        let mut facts = InterfaceFacts::default();
        let mut events = Vec::new();
        let mut player = ClientObject::new(PLAYER);
        player.item_type = ItemType::CREATURE;
        player.items_capacity = 102;
        facts.objects.add_or_update(player, &mut events);
        let mut tool = ClientObject::new(TOOL);
        tool.item_type = ItemType::TINKERING_TOOL;
        facts.objects.add_or_update(tool, &mut events);
        let mut baton = ClientObject::new(BATON);
        baton.name = "Slashing Baton".into();
        baton.item_type = ItemType::MELEE_WEAPON;
        baton.material_type = Some(RUBY);
        facts.objects.add_or_update(baton, &mut events);
        for (slot, id) in [TOOL, BATON].into_iter().enumerate() {
            facts
                .objects
                .move_item(id, PLAYER, slot as i32, EquipMask::NONE, None, &mut events);
        }
        facts.state.player = PLAYER;
        let names = NameTables {
            materials: HashMap::from([(RUBY, "Ruby".to_owned())]),
            ..NameTables::default()
        };
        let icons = &mut ac_ui::icons::NoIcons;
        facts.salvage_opens = vec![TOOL];
        feed_salvage(&mut host, &mut facts, icons, &names, None);
        let view = ItemsView::new(&facts.objects, PLAYER);
        let staged = host
            .with_controller::<SalvageController, _>(SALVAGE_WINDOW, |c, ui| {
                c.add_item(ui, BATON, &view, true)
            })
            .unwrap();
        assert!(staged);
        feed_salvage(&mut host, &mut facts, icons, &names, None);
        let cell = host
            .root
            .widget::<ItemList>(list)
            .and_then(|l| l.item(0))
            .unwrap();
        let slot = host.root.widget::<ItemSlot>(cell).unwrap();
        assert_eq!(slot.object_id, BATON);
        assert_eq!(slot.tooltip.as_deref(), Some("Ruby Slashing Baton"));
    }
}
