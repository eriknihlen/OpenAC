//! The combat window's attack controls.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/CombatUiControllerTests.cs`
//! (the pages per mode, Medium by default, a height button charges while
//! held and swings on release, the option checkboxes, the label alignment,
//! Accuracy in missile mode) over a hand-built layout, and
//! `CombatLayoutConformanceTests.cs` on the real dats.

mod common;

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::combat::{AttackHeight, CombatMode};
use ac_runtime::character::option_id;
use ac_runtime::gameplay::{CombatAttackCommand, GameplayCommand, InputActivation};
use ac_runtime::session::SessionCommand;
use ac_runtime::world::AttackBarView;
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::PanelOutput;
use ac_ui::panels::combat_bar::{self as cb, CombatBarController, CombatBarLabels, CombatBarView};
use ac_ui::strings::compute_hash;
use ac_ui::tree::{Element, ElementId};
use ac_ui::widgets::{Button, Scrollbar, Text};
use common::items::panel_outputs;
use common::panels::{add_std_button, add_std_text};
use common::{at, panel};

fn labels() -> CombatBarLabels {
    CombatBarLabels {
        speed: "Speed".into(),
        power: "Power".into(),
        accuracy: "Accuracy".into(),
        repeat_attacks: "Repeat Attacks".into(),
        auto_target: "Auto Target".into(),
        keep_in_view: "Keep in View".into(),
        high: "High".into(),
        medium: "Medium".into(),
        low: "Low".into(),
    }
}

struct Bar {
    host: UiHost,
    ids: HashMap<u32, ElementId>,
}

fn bar() -> Bar {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let ui = &mut host.root;
    let r = ui.root();
    let root = panel(ui, r, at(0.0, 0.0, 610.0, 90.0));
    let mut ids = HashMap::new();
    for id in [cb::BASIC_PANEL_ID, cb::SPELLCASTING_PANEL_ID] {
        let mut e = at(0.0, 0.0, 610.0, 90.0);
        e.dat_element_id = id;
        ids.insert(id, panel(ui, root, e));
    }
    let mut e = Element::at(13.0, 14.0, 507.0, 14.0);
    e.dat_element_id = cb::POWER_CONTROL_ID;
    // As the importer builds it: a plain horizontal bar, which the
    // controller makes a value slider.
    let mut slider = Scrollbar::default();
    slider.horizontal = true;
    ids.insert(cb::POWER_CONTROL_ID, ui.add(root, e, slider));
    add_std_text(ui, &mut ids, root, cb::SPEED_LABEL_ID);
    add_std_text(ui, &mut ids, root, cb::POWER_LABEL_ID);
    for id in [cb::HIGH_BUTTON_ID, cb::MEDIUM_BUTTON_ID, cb::LOW_BUTTON_ID] {
        add_std_button(ui, &mut ids, root, id);
    }
    for id in [
        cb::REPEAT_ATTACKS_ID,
        cb::AUTO_TARGET_ID,
        cb::KEEP_IN_VIEW_ID,
    ] {
        let b = add_std_button(ui, &mut ids, root, id);
        ui.with::<Button, _>(b, |w, _| w.toggle_behavior = true);
    }
    ui.register_window("combat", root, None, 0).unwrap();
    let layout = ImportedLayout::from_parts(root, ids.clone());
    let c = CombatBarController::bind(ui, &layout, labels()).expect("binds");
    host.attach_controller("combat", Box::new(c)).unwrap();
    Bar { host, ids }
}

impl Bar {
    fn update(&mut self, view: &CombatBarView) -> Option<bool> {
        self.host
            .with_controller::<CombatBarController, _>("combat", |c, ui| c.update(ui, view))
            .unwrap()
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.ids[&id]).unwrap().visible
    }

    fn selected(&self, id: u32) -> bool {
        self.host
            .root
            .widget::<Button>(self.ids[&id])
            .unwrap()
            .selected()
    }

    fn fire(&mut self, id: u32, k: i32) {
        self.host.root.fire_event(k, self.ids[&id], None);
        self.host.pump();
    }

    /// The mouse pressed on `id` at `from_x` (element coordinates), moved
    /// to `to_x` and released: the widget itself reports what it does.
    fn drag(&mut self, id: u32, from_x: i32, to_x: i32) {
        let e = self.ids[&id];
        for (k, x) in [
            (kind::MOUSE_DOWN, from_x),
            (kind::MOUSE_MOVE, to_x),
            (kind::MOUSE_UP, to_x),
        ] {
            let event = ac_ui::event::UiEvent {
                kind: k,
                data1: x,
                data2: 7,
                target: Some(e),
                ..ac_ui::event::UiEvent::default()
            };
            self.host.root.send_event(e, &event);
        }
        self.host.pump();
    }

    fn text(&self, id: u32) -> String {
        let t = self.host.root.widget::<Text>(self.ids[&id]).unwrap();
        t.content_lines()
            .into_iter()
            .map(|l| l.text)
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn sessions(&mut self) -> Vec<SessionCommand> {
        panel_outputs(&mut self.host)
            .into_iter()
            .filter_map(|o| match o {
                PanelOutput::Session(c) => Some(c),
                _ => None,
            })
            .collect()
    }
}

fn view(mode: CombatMode) -> CombatBarView {
    CombatBarView {
        mode,
        attack: AttackBarView {
            height: AttackHeight::MEDIUM,
            desired_power: 0.5,
            power_bar: 0.0,
        },
        ..CombatBarView::default()
    }
}

#[test]
fn the_mode_shows_the_physical_or_magic_page_and_medium_is_selected() {
    let mut b = bar();
    let shown: Vec<Option<bool>> = [
        CombatMode::NON_COMBAT,
        CombatMode::MELEE,
        CombatMode::MELEE,
        CombatMode::MAGIC,
    ]
    .iter()
    .map(|m| b.update(&view(*m)))
    .collect();
    // The window's visibility only when the mode changes.
    assert_eq!(shown, [Some(false), Some(true), None, Some(true)]);
    assert!(!b.visible(cb::BASIC_PANEL_ID));
    assert!(b.visible(cb::SPELLCASTING_PANEL_ID));
    assert!(!b.selected(cb::HIGH_BUTTON_ID));
    assert!(b.selected(cb::MEDIUM_BUTTON_ID));
    assert!(!b.selected(cb::LOW_BUTTON_ID));
    let s = b
        .host
        .root
        .widget::<Scrollbar>(b.ids[&cb::POWER_CONTROL_ID])
        .unwrap();
    assert_eq!(s.scalar_position(), 0.5);
}

#[test]
fn a_height_button_charges_while_held_and_swings_on_release() {
    let mut b = bar();
    b.update(&view(CombatMode::MELEE));
    b.sessions();
    b.fire(cb::HIGH_BUTTON_ID, kind::MOUSE_DOWN);
    b.fire(cb::HIGH_BUTTON_ID, kind::MOUSE_UP);
    assert_dbg_eq!(
        b.sessions(),
        [
            SessionCommand::Gameplay(GameplayCommand::AttackInput(
                CombatAttackCommand::HighAttack,
                InputActivation::Press
            )),
            SessionCommand::Gameplay(GameplayCommand::AttackInput(
                CombatAttackCommand::HighAttack,
                InputActivation::Release
            )),
        ]
    );
    // The bar fills from the session's level.
    let mut charging = view(CombatMode::MELEE);
    charging.attack.power_bar = 0.5;
    b.update(&charging);
    let s = b
        .host
        .root
        .widget::<Scrollbar>(b.ids[&cb::POWER_CONTROL_ID])
        .unwrap();
    assert_eq!(s.scalar_fill, Some(0.5));
    assert!(b.visible(cb::BASIC_PANEL_ID));
    assert!(!b.visible(cb::SPELLCASTING_PANEL_ID));
}

#[test]
fn an_option_checkbox_sets_its_character_option() {
    let mut b = bar();
    let mut v = view(CombatMode::MELEE);
    v.auto_target = true;
    b.update(&v);
    assert!(b.selected(cb::AUTO_TARGET_ID));
    b.sessions();
    b.fire(cb::AUTO_TARGET_ID, kind::MOUSE_DOWN);
    b.fire(cb::AUTO_TARGET_ID, kind::MOUSE_UP);
    b.fire(cb::AUTO_TARGET_ID, kind::CLICK);
    assert_dbg_eq!(
        b.sessions(),
        [SessionCommand::SetCharacterOption {
            option: option_id::AUTO_TARGET,
            value: false
        }]
    );
}

#[test]
fn the_power_slider_sets_the_attack_power() {
    let mut b = bar();
    b.update(&view(CombatMode::MELEE));
    b.sessions();
    let slider = b.ids[&cb::POWER_CONTROL_ID];
    b.host
        .with_controller::<CombatBarController, _>("combat", |c, ui| {
            assert!(c.handle(
                ui,
                slider,
                &ac_ui::root::ElementMessage::ScalarChanged(0.75)
            ));
        });
    assert_dbg_eq!(
        b.sessions(),
        [SessionCommand::Gameplay(GameplayCommand::SetAttackPower(
            0.75
        ))]
    );
}

#[test]
fn dragging_the_power_slider_sets_the_attack_power() {
    let mut b = bar();
    b.update(&view(CombatMode::MELEE));
    b.sessions();
    // Grab the thumb (16 wide, at half power on the 507-wide bar) and drag
    // it past the bar's far end: full power.
    b.drag(cb::POWER_CONTROL_ID, 253, 600);
    let powers: Vec<f32> = b
        .sessions()
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Gameplay(GameplayCommand::SetAttackPower(p)) => Some(p),
            _ => None,
        })
        .collect();
    assert_eq!(powers.last(), Some(&1.0), "{powers:?}");
    let s = b
        .host
        .root
        .widget::<Scrollbar>(b.ids[&cb::POWER_CONTROL_ID])
        .unwrap();
    assert_eq!(s.scalar_position(), 1.0);
}

#[test]
fn the_power_labels_keep_their_retail_alignment() {
    let b = bar();
    let speed = b
        .host
        .root
        .widget::<Text>(b.ids[&cb::SPEED_LABEL_ID])
        .unwrap();
    let power = b
        .host
        .root
        .widget::<Text>(b.ids[&cb::POWER_LABEL_ID])
        .unwrap();
    assert!(!speed.centered && !speed.right_aligned);
    assert!(!power.centered && power.right_aligned);
}

#[test]
fn the_power_label_reads_accuracy_in_missile_mode_and_back() {
    let mut b = bar();
    assert_eq!(b.text(cb::POWER_LABEL_ID), "Power");
    b.update(&view(CombatMode::MISSILE));
    assert_eq!(b.text(cb::POWER_LABEL_ID), "Accuracy");
    b.update(&view(CombatMode::MELEE));
    assert_eq!(b.text(cb::POWER_LABEL_ID), "Power");
}

#[test]
fn the_option_caption_keys_hash_to_the_reference_goldens() {
    // OpenAC CombatLayoutConformanceTests.RetailStringHash_MatchesTheReferenceGolden.
    assert_eq!(
        compute_hash("ID_CombatPanelOption_AutoRepeatAttack"),
        0x0718_F89B
    );
    assert_eq!(compute_hash("ID_CombatPanelOption_AutoTarget"), 0x0FB7_B944);
    assert_eq!(
        compute_hash("ID_CombatPanelOption_ViewCombatTarget"),
        0x0936_C254
    );
}

mod real_dats {
    //! OpenAC `CombatLayoutConformanceTests` on the installed dats.

    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import, import_infos};
    use std::path::PathBuf;

    fn content() -> DatContent {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        DatContent::new(Arc::new(
            ac_dat::DatCollection::open(dir).expect("open the retail dats"),
        ))
        .expect("master property table")
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_authored_combat_layout_binds_with_its_captions_and_slider_art() {
        let c = content();
        let info = import_infos(&c, cb::LAYOUT_ID).expect("layout 0x21000073");
        let strings = c.strings();
        let labels = CombatBarLabels::resolve(&info, &|s| strings.resolve_info(s), &|k| {
            strings.resolve(cb::UI_STRING_TABLE_ID, compute_hash(k), 0)
        });
        // Every caption is authored (none fell back to the English words
        // by accident: the dat's own words are the same).
        assert_eq!(labels.speed, "Speed");
        assert_eq!(labels.power, "Power");
        assert_eq!(labels.accuracy, "Accuracy");
        assert_eq!(labels.high, "High");
        assert_eq!(labels.medium, "Medium");
        assert_eq!(labels.low, "Low");
        assert!(!labels.repeat_attacks.is_empty());
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout =
            import(&mut host.root, &c, cb::LAYOUT_ID, c.font(DEFAULT_FONT_ID)).expect("builds");
        let ui = &mut host.root;
        let power = layout.find(cb::POWER_CONTROL_ID).unwrap();
        let s = ui
            .widget::<Scrollbar>(power)
            .expect("the power control is a slider");
        assert_eq!(s.track_sprite, 0x0600_74CA);
        assert_eq!(s.thumb_sprite, 0x0600_1923);
        assert_eq!(s.scalar_fill_sprite, 0x0600_1200);
        assert_eq!(s.scalar_range_sprite, 0x0600_715E);
        assert!(!s.scalar_fill_from_right);
        let repeat = layout.find(cb::REPEAT_ATTACKS_ID).unwrap();
        assert!(ui.widget::<Button>(repeat).unwrap().toggle_behavior);
        let mut bar = CombatBarController::bind(ui, &layout, labels).expect("binds");
        // The bound power control is a value slider (a drag reports it).
        assert!(ui.widget::<Scrollbar>(power).unwrap().scalar);
        assert_eq!(bar.update(ui, &view(CombatMode::MELEE)), Some(true));
        let high = layout.find(cb::HIGH_BUTTON_ID).unwrap();
        assert_eq!(
            ui.widget::<Button>(high).unwrap().label.as_deref(),
            Some("High")
        );
    }
}
