//! Mounting the item, character and magic panels in the game window, as
//! OpenAC's `RetailUiRuntime` mounts them (`MountToolbar`, `MountCombat`,
//! `MountSpellbook`, `MountAppraisal`, `MountDialogFactory`,
//! `MountCharacter`, `MountInventory`, then the toolbar's panel buttons),
//! and the dat tables they read.

use std::sync::Arc;

use ac_dat::DatCollection;
use ac_dat::objects::{
    EXPERIENCE_TABLE_ID, ExperienceTable, SKILL_TABLE_ID, SPELL_COMPONENT_TABLE_ID,
};
use ac_ui::font::DEFAULT_FONT_ID;
use ac_ui::geometry::{AnchorEdges, ResizeEdges};
use ac_ui::host::ControllerGroup;
use ac_ui::import::{LayoutSource, import_infos, import_infos_rooted, import_rooted};
use ac_ui::panels::appraisal::{self, AppraisalController};
use ac_ui::panels::cell_template::{empty_sprite, empty_sprite_from_info, shortcut_digits};
use ac_ui::panels::character::{CharacterStatController, ExperienceCurves};
use ac_ui::panels::combat_bar::{self, CombatBarController, CombatBarLabels};
use ac_ui::panels::dialog::catalog_layout_id;
use ac_ui::panels::inventory::{self, InventoryController, InventorySprites};
use ac_ui::panels::magic::{
    self, FAVORITE_CELL_PX, SpellbookRowStyle, SpellbookWindowController, SpellcastingController,
    VISIBLE_FAVORITE_SLOTS, fit_favorite_slots,
};
use ac_ui::panels::panel_id;
use ac_ui::panels::paperdoll::{
    InventoryWindowController, PaperdollClickMap, PaperdollController, resolve_empty_sprites,
};
use ac_ui::panels::selected_object::SelectedObjectController;
use ac_ui::panels::toolbar::{
    self, StackSplitController, ToolbarController, ToolbarWindowController,
};
use ac_ui::widgets::ShortcutDigits;

use super::mount::{Chrome, MountOptions, configure_collapse, mount_window};
use super::window::{
    CHARACTER_WINDOW, COMBAT_WINDOW, EXAMINATION_WINDOW, INVENTORY_WINDOW, SPELLBOOK_WINDOW,
    TOOLBAR_WINDOW, WindowUi,
};

/// The toolbar's second row (shown only expanded).
const TOOLBAR_SECOND_ROW: [u32; 11] = [
    0x1000_06B6,
    0x1000_06B7,
    0x1000_06B8,
    0x1000_06B9,
    0x1000_06BA,
    0x1000_06BB,
    0x1000_06BC,
    0x1000_06BD,
    0x1000_06BE,
    0x1000_06BF,
    0x1000_06C0,
];

/// The shared main-panel frame: layout and element.
const MAIN_PANEL_FRAME: (u32, u32) = (0x2100_006E, 0x1000_05FE);

/// The stat rows' font.
const CHARACTER_ROW_FONT: u32 = 0x4000_0001;

/// The map from spell component to its weenie class.
const COMPONENT_CLASS_MAP_ID: u32 = 0x2700_0002;

impl WindowUi {
    /// The dat tables the panels read: the experience curves, the skill
    /// table, the spell components and each school's focus.
    pub(super) fn load_tables(&mut self, dats: &DatCollection) {
        match dats.get::<ExperienceTable>(EXPERIENCE_TABLE_ID) {
            Ok(Some(table)) => self.experience = Some(ExperienceCurves::from(&table)),
            Ok(None) => self
                .notices
                .push("ui: the experience table is not in the dats".to_owned()),
            Err(e) => self
                .notices
                .push(format!("ui: cannot read the experience table: {e}")),
        }
        match dats.get(SKILL_TABLE_ID) {
            Ok(table) => self.skill_table = table,
            Err(e) => self
                .notices
                .push(format!("ui: cannot read the skill table: {e}")),
        }
        match dats.get(SPELL_COMPONENT_TABLE_ID) {
            Ok(table) => self.components = table,
            Err(e) => self
                .notices
                .push(format!("ui: cannot read the spell components: {e}")),
        }
        match dats.get::<ac_dat::objects::EnumIdMap>(COMPONENT_CLASS_MAP_ID) {
            Ok(Some(map)) => {
                if let Some(table) = &self.components {
                    for (scid, _) in &table.components {
                        if let Some(wcid) = map.client_id(*scid) {
                            self.component_classes.insert(*scid, wcid);
                        }
                    }
                }
            }
            Ok(None) => self
                .notices
                .push("ui: the spell component class map is not in the dats".to_owned()),
            Err(e) => self.notices.push(format!(
                "ui: cannot read the spell component class map: {e}"
            )),
        }
        let foci = match ac_runtime::gameplay::tables::magic_packs(dats) {
            Ok(foci) => foci,
            Err(e) => {
                self.notices
                    .push(format!("ui: cannot read the foci map: {e}"));
                std::collections::HashMap::new()
            }
        };
        self.spell_components = ac_core::spells::SpellComponentRequirements::new(
            std::collections::HashMap::new(),
            self.component_classes.clone(),
            foci,
        );
    }

    /// Mount the toolbar, the spell bars, the spellbook, the examination
    /// window, the dialog catalog, the character window and the inventory,
    /// then bind the toolbar's panel buttons to the panels that exist.
    pub(super) fn mount_panels(&mut self) {
        if let Some(frame) =
            import_infos_rooted(&self.content, MAIN_PANEL_FRAME.0, MAIN_PANEL_FRAME.1)
        {
            self.panels.configure_frame(&frame);
        }
        let digits = Arc::new(shortcut_digits(&self.content));
        let results = [
            (TOOLBAR_WINDOW, self.mount_toolbar(&digits)),
            (COMBAT_WINDOW, self.mount_combat(&digits)),
            (SPELLBOOK_WINDOW, self.mount_spellbook()),
            (EXAMINATION_WINDOW, self.mount_examination()),
            (CHARACTER_WINDOW, self.mount_character()),
            (INVENTORY_WINDOW, self.mount_inventory(&digits)),
        ];
        for (name, result) in results {
            match result {
                Ok(()) => self.mounted.push(name),
                Err(e) => self.notices.push(format!("ui: {name}: {e}")),
            }
        }
        self.mount_external_container();
        self.mount_salvage();
        self.mount_book();
        self.mount_map_house();
        self.mount_character_info();
        self.mount_dealings();
        self.dialog_layout = catalog_layout_id(self.content.dats());
        if self.dialog_layout.is_none() {
            self.notices
                .push("ui: the dialog catalog is not in the dats".to_owned());
        }
        self.bind_panel_buttons();
    }

    /// OpenAC `BindToolbarPanelButtons` and `SyncToolbarWindowButtons`.
    pub(super) fn bind_panel_buttons(&mut self) {
        let panels = [
            panel_id::INVENTORY,
            panel_id::CHARACTER,
            panel_id::MAGIC,
            panel_id::SOCIAL,
            panel_id::JOURNAL,
            panel_id::OPTIONS,
            panel_id::MAP_HOUSE,
            panel_id::CHARACTER_INFORMATION,
        ]
        .into_iter()
        .filter_map(|p| {
            let window = self.panels.window_of(p)?;
            Some((p, self.host.root.is_window_visible(window)))
        })
        .collect::<Vec<_>>();
        self.host
            .with_controller::<ToolbarWindowController, _>(TOOLBAR_WINDOW, |c, ui| {
                c.toolbar
                    .bind_panel_buttons(ui, |p| panels.iter().any(|(id, _)| *id == p));
                for &(p, open) in &panels {
                    c.toolbar.set_panel_open(ui, p, open);
                }
            });
    }

    /// OpenAC `RetailUiRuntime.MountToolbar`, with the selected-object strip.
    fn mount_toolbar(&mut self, digits: &Arc<ShortcutDigits>) -> Result<(), String> {
        let layout = self.import(toolbar::LAYOUT_ID)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let ui = &mut self.host.root;
        let bar = ToolbarController::bind(ui, &layout, Some(digits.clone()), font);
        let split = StackSplitController::bind(ui, &layout);
        let selected =
            SelectedObjectController::bind(ui, &layout, self.content.font(DEFAULT_FONT_ID));
        let height = ui.element(layout.root).map_or(0.0, |e| e.height);
        let frame = mount_window(
            ui,
            TOOLBAR_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::CollapsibleNineSlice,
                left: 10.0,
                top: 300.0,
                content_width: Some(300.0),
                content_height: Some(height),
                resizable: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::RIGHT,
                content_click_through: Some(true),
                ..MountOptions::default()
            },
        )?;
        let row = TOOLBAR_SECOND_ROW
            .iter()
            .filter_map(|&id| layout.find(id))
            .collect();
        configure_collapse(ui, frame, row, height);
        self.host
            .attach_controller(
                TOOLBAR_WINDOW,
                Box::new(ToolbarWindowController {
                    toolbar: bar,
                    split,
                    selected: Some(selected),
                }),
            )
            .map_err(|e| e.to_string())
    }

    /// OpenAC `RetailUiRuntime.MountCombat`: the spell bars, widened to 18
    /// favourite cells; the window shows in magic mode.
    fn mount_combat(&mut self, digits: &Arc<ShortcutDigits>) -> Result<(), String> {
        let info = import_infos(&self.content, magic::COMBAT_LAYOUT_ID)
            .ok_or("layout 0x21000073 is not in the dats")?;
        let empty = empty_sprite_from_info(&self.content, &info, magic::FAVORITE_LIST_ID);
        let layout = self.import(magic::COMBAT_LAYOUT_ID)?;
        // The attack controls' captions (OpenAC `CombatUiLabels.Resolve`).
        let strings = self.content.strings();
        let labels = CombatBarLabels::resolve(&info, &|s| strings.resolve_info(s), &|key| {
            strings.resolve(
                combat_bar::UI_STRING_TABLE_ID,
                ac_ui::strings::compute_hash(key),
                0,
            )
        });
        let ui = &mut self.host.root;
        let width = fit_favorite_slots(ui, &layout, VISIBLE_FAVORITE_SLOTS, FAVORITE_CELL_PX)
            .ok_or("no favourite list")?;
        // The attack controls first, then the spell bars, as OpenAC groups
        // them.
        let bar = CombatBarController::bind(ui, &layout, labels)
            .ok_or("required attack controls are missing")?;
        let controller = SpellcastingController::bind(ui, &layout, Some(digits.clone()), empty)
            .ok_or("required magic controls are missing")?;
        let list_width = layout
            .find(magic::FAVORITE_LIST_ID)
            .and_then(|l| ui.element(l))
            .map_or(0.0, |e| e.width);
        let (root_width, root_height) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.width, e.height));
        let screen_height = ui.size().1;
        mount_window(
            ui,
            COMBAT_WINDOW,
            layout.root,
            &MountOptions {
                left: 0.0,
                top: (screen_height - root_height - 10.0).max(0.0),
                content_width: Some(width),
                dat_constraint_source: Some(info),
                outer_anchors: AnchorEdges::LEFT | AnchorEdges::BOTTOM,
                visible: false,
                draggable: false,
                content_click_through: Some(false),
                resizable: true,
                resize_x: true,
                resize_y: false,
                resizable_edges: ResizeEdges::LEFT | ResizeEdges::RIGHT,
                min_width: Some(root_width - list_width + 9.0 * FAVORITE_CELL_PX),
                max_width: Some(f32::MAX),
                constrain_resize_to_parent: true,
                ..MountOptions::default()
            },
        )?;
        let group = ControllerGroup::new(vec![Box::new(bar), Box::new(controller)]);
        self.host
            .attach_controller(COMBAT_WINDOW, Box::new(group))
            .map_err(|e| e.to_string())
    }

    /// OpenAC `RetailUiRuntime.MountSpellbook` (the components page:
    /// `super::components_page`).
    fn mount_spellbook(&mut self) -> Result<(), String> {
        let font = self.content.font(DEFAULT_FONT_ID);
        let style = SpellbookRowStyle::load(&self.content).ok_or("the spell row template")?;
        let row_font = if style.font_did == 0 {
            font.clone()
        } else {
            self.content.font(style.font_did).or_else(|| font.clone())
        };
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            magic::SPELLBOOK_LAYOUT_ID,
            magic::SPELLBOOK_ROOT_ID,
            font,
        )
        .ok_or("layout 0x21000034 is not in the dats")?;
        let category_names = self.component_category_names();
        let ui = &mut self.host.root;
        let mut controller = SpellbookWindowController::bind(ui, &layout, style, row_font)
            .ok_or("required controls are missing")?;
        if let Some(page) = ac_ui::panels::spell_components::ComponentsPageController::bind(
            ui,
            &layout,
            category_names,
        ) {
            controller.attach_components(page);
        }
        mount_window(
            ui,
            SPELLBOOK_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 18.0,
                top: 18.0,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(SPELLBOOK_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.panels.register(
            &mut self.host.root,
            panel_id::MAGIC,
            SPELLBOOK_WINDOW,
            false,
        );
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountAppraisal`.
    fn mount_examination(&mut self) -> Result<(), String> {
        let font = self.content.font(DEFAULT_FONT_ID);
        let layout = import_rooted(
            &mut self.host.root,
            &self.content,
            appraisal::LAYOUT_ID,
            appraisal::ROOT_ID,
            font,
        )
        .ok_or("layout 0x2100006B is not in the dats")?;
        let ui = &mut self.host.root;
        let controller =
            AppraisalController::bind(ui, &layout).ok_or("required controls are missing")?;
        let (left, top) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.left, e.top));
        mount_window(
            ui,
            EXAMINATION_WINDOW,
            layout.root,
            &MountOptions {
                left,
                top,
                authored_geometry_revision: 1,
                visible: false,
                min_width: Some(310.0),
                min_height: Some(400.0),
                constrain_resize_to_parent: true,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(EXAMINATION_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())
    }

    /// OpenAC `RetailUiRuntime.MountCharacter` (the titles list is not
    /// ported).
    fn mount_character(&mut self) -> Result<(), String> {
        let layout = self.import(0x2100_002E)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let row_font = self
            .content
            .font(CHARACTER_ROW_FONT)
            .or_else(|| font.clone());
        let constraint = import_infos_rooted(&self.content, MAIN_PANEL_FRAME.0, MAIN_PANEL_FRAME.1);
        let ui = &mut self.host.root;
        let controller = CharacterStatController::bind(ui, &layout, font, row_font);
        mount_window(
            ui,
            CHARACTER_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left: 540.0,
                top: 18.0,
                content_height: Some(362.0),
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                dat_constraint_source: constraint,
                dat_constraint_source_is_outer_frame: true,
                visible: false,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM,
                content_click_through: Some(false),
                ..MountOptions::default()
            },
        )?;
        self.host
            .attach_controller(CHARACTER_WINDOW, Box::new(controller))
            .map_err(|e| e.to_string())?;
        self.panels.register(
            &mut self.host.root,
            panel_id::CHARACTER,
            CHARACTER_WINDOW,
            false,
        );
        Ok(())
    }

    /// OpenAC `RetailUiRuntime.MountInventory`: the inventory and the
    /// paperdoll share one window.
    fn mount_inventory(&mut self, digits: &Arc<ShortcutDigits>) -> Result<(), String> {
        let layout = self.import(inventory::LAYOUT_ID)?;
        let font = self.content.font(DEFAULT_FONT_ID);
        let sprite = |layout, list| empty_sprite(&self.content, layout, list);
        let sprites = InventorySprites {
            contents_empty: sprite(inventory::CONTENTS_LAYOUT_ID, inventory::CONTENTS_GRID_ID),
            side_bag_empty: sprite(inventory::BACKPACK_LAYOUT_ID, inventory::CONTAINER_LIST_ID),
            main_pack_empty: sprite(inventory::BACKPACK_LAYOUT_ID, inventory::TOP_CONTAINER_ID),
        };
        let doll_sprites = resolve_empty_sprites(&self.content);
        let click_map = PaperdollClickMap::load(self.content.dats()).map(Arc::new);
        if click_map.is_none() {
            self.notices
                .push("ui: inventory: the paperdoll click map is not in the dats".to_owned());
        }
        let ui = &mut self.host.root;
        let (left, top) = ui
            .element(layout.root)
            .map_or((0.0, 0.0), |e| (e.left, e.top));
        mount_window(
            ui,
            INVENTORY_WINDOW,
            layout.root,
            &MountOptions {
                chrome: Chrome::NineSlice,
                left,
                top,
                visible: false,
                resize_x: false,
                resize_y: true,
                resizable_edges: ResizeEdges::BOTTOM,
                constrain_resize_to_parent: true,
                content_anchors: AnchorEdges::LEFT | AnchorEdges::TOP | AnchorEdges::BOTTOM,
                ..MountOptions::default()
            },
        )?;
        let contents_empty = sprites.contents_empty;
        let inventory =
            InventoryController::bind(ui, &layout, sprites, font.clone(), Some(digits.clone()));
        let paperdoll =
            PaperdollController::bind(ui, &layout, contents_empty, &doll_sprites, click_map, font);
        self.host
            .attach_controller(
                INVENTORY_WINDOW,
                Box::new(InventoryWindowController {
                    inventory,
                    paperdoll,
                }),
            )
            .map_err(|e| e.to_string())?;
        self.panels.register(
            &mut self.host.root,
            panel_id::INVENTORY,
            INVENTORY_WINDOW,
            false,
        );
        Ok(())
    }
}
