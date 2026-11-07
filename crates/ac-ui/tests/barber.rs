//! The barber window. The fixture layout is built by hand in the shape of
//! the barber layout: the five spins with their two arrows, the nine
//! swatches and their marks, the gradient disc and the shade slider, the
//! two turn buttons, Accept, Cancel and the three option boxes. The
//! barber is the session's; the window draws a view and emits commands.
//! The last test reads the retail dats (`real_dats`).

mod common;

use std::sync::Arc;

use ac_core::chargen::barber::BarberToggle;
use ac_core::chargen::{ChargenGenderOptions, ChargenHairStyle};
use ac_dat::objects::LayoutDesc;
use ac_runtime::gameplay::{BarberCommand, BarberView, GameplayCommand};
use ac_runtime::session::SessionCommand;
use ac_runtime::session::creation::{AppearanceSlot, CreationAppearance, ShadeSlot};
use ac_ui::element_info::ElementInfo;
use ac_ui::host::{PanelController, UiHost};
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build, class};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::barber::{self as b, BarberController};
use ac_ui::pregame::chargen::appearance::{
    DECREMENT_ARROW_ID, GRAD_CIRCLE_ID, INCREMENT_ARROW_ID, Part, PreviewRequest, SHADE_SCROLL_ID,
    SWATCH_IDS, SWATCH_OVERLAY_IDS,
};
use ac_ui::root::ElementMessage;
use ac_ui::states;
use ac_ui::tree::ElementId;
use common::items::panel_outputs;

#[derive(Debug, Default)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<LayoutDesc>> {
        None
    }
}

fn element(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        x,
        y,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

fn window_info() -> ElementInfo {
    let mut root = element(b::ROOT_ID, class::CONTAINER, 0.0, 0.0, 500.0, 440.0);
    let mut choices = element(0x1000_059D, class::CONTAINER, 8.0, 4.0, 175.0, 230.0);
    for (i, (id, _)) in b::SPINS.iter().enumerate() {
        let mut spin = element(*id, class::BUTTON, 0.0, 44.0 * i as f32, 200.0, 42.0);
        spin.children.push(element(
            DECREMENT_ARROW_ID,
            class::BUTTON,
            0.0,
            0.0,
            20.0,
            20.0,
        ));
        spin.children.push(element(
            INCREMENT_ARROW_ID,
            class::BUTTON,
            180.0,
            0.0,
            20.0,
            20.0,
        ));
        choices.children.push(spin);
    }
    let mut colours = element(0x1000_059C, class::CONTAINER, 2.0, 228.0, 220.0, 220.0);
    for (i, id) in SWATCH_IDS.iter().enumerate() {
        colours.children.push(element(
            *id,
            class::BUTTON,
            20.0 * i as f32,
            0.0,
            20.0,
            20.0,
        ));
    }
    for (i, id) in SWATCH_OVERLAY_IDS.iter().enumerate() {
        colours.children.push(element(
            *id,
            class::CONTAINER,
            20.0 * i as f32,
            0.0,
            20.0,
            20.0,
        ));
    }
    colours.children.push(element(
        GRAD_CIRCLE_ID,
        class::CONTAINER,
        0.0,
        40.0,
        100.0,
        100.0,
    ));
    colours.children.push(element(
        SHADE_SCROLL_ID,
        class::SCROLLBAR,
        0.0,
        150.0,
        200.0,
        16.0,
    ));
    root.children.push(choices);
    root.children.push(colours);
    root.children.push(element(
        b::VIEWPORT_ID,
        class::VIEWPORT,
        236.0,
        4.0,
        200.0,
        200.0,
    ));
    for (id, x) in [
        (b::ROTATE_CLOCKWISE_ID, 232.0),
        (b::ROTATE_COUNTER_CLOCKWISE_ID, 382.0),
    ] {
        root.children
            .push(element(id, class::BUTTON, x, 216.0, 60.0, 54.0));
    }
    root.children.push(element(
        b::APPLY_ID,
        class::BUTTON,
        352.0,
        274.0,
        88.0,
        33.0,
    ));
    root.children.push(element(
        b::CANCEL_ID,
        class::BUTTON,
        232.0,
        274.0,
        88.0,
        33.0,
    ));
    for (i, id) in b::OPTION_IDS.iter().enumerate() {
        root.children.push(element(
            *id,
            class::BUTTON,
            232.0,
            274.0 + 20.0 * i as f32,
            140.0,
            14.0,
        ));
    }
    root
}

fn gender() -> ChargenGenderOptions {
    ChargenGenderOptions {
        gender_key: 1,
        hair_styles: vec![ChargenHairStyle::default(); 4],
        hair_colors: vec![1, 2, 3],
        eye_strips: vec![Default::default(); 3],
        eye_colors: vec![4, 5],
        nose_strips: vec![Default::default(); 2],
        mouth_strips: vec![Default::default(); 2],
        ..ChargenGenderOptions::default()
    }
}

fn view(heritage: u32, openings: u64) -> BarberView {
    BarberView {
        revision: openings,
        openings,
        open: true,
        heritage,
        gender: 1,
        appearance: CreationAppearance {
            hair_style: 3,
            hair_color: 1,
            hair_shade: 0.25,
            eyes_strip: 0,
            eye_color: 1,
            nose_strip: 1,
            mouth_strip: 0,
            skin_shade: 0.75,
            ..CreationAppearance::default()
        },
        toggle: BarberToggle::of(heritage),
        toggle_on: false,
        preview_setup: 0,
    }
}

struct Harness {
    host: UiHost,
    c: BarberController,
    layout: ImportedLayout,
    view: BarberView,
}

impl Harness {
    fn new(heritage: u32) -> Self {
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let ctx = BuildContext {
            source: &NoSource,
            default_font: None,
            source_layout_did: b::LAYOUT_ID,
            media_seconds: 0.0,
        };
        let layout = build(&mut host.root, &window_info(), &ctx);
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let c = BarberController::bind(&mut host.root, &layout);
        let mut h = Self {
            host,
            c,
            layout,
            view: view(heritage, 1),
        };
        let shown = h.update();
        assert_eq!(shown, Some(true));
        h
    }

    fn update(&mut self) -> Option<bool> {
        let text = |table: u32, key: &str| Some(format!("{table:08X}:{key}"));
        let gender = gender();
        self.c
            .update(
                &mut self.host.root,
                &self.view,
                Some(&gender),
                None,
                None,
                &text,
            )
            .visible
    }

    fn id(&self, id: u32) -> ElementId {
        self.layout.find(id).unwrap()
    }

    fn send(&mut self, element: ElementId, message: ElementMessage) -> Vec<BarberCommand> {
        assert!(
            self.c
                .on_element_message(&mut self.host.root, element, &message)
        );
        self.host.pump();
        commands(&panel_outputs(&mut self.host))
    }

    fn click(&mut self, element: ElementId) -> Vec<BarberCommand> {
        self.send(element, ElementMessage::Clicked { x: 1, y: 1 })
    }

    fn visible(&self, element: ElementId) -> bool {
        self.host.root.element(element).unwrap().visible
    }

    fn spin(&self, part: Part) -> ElementId {
        self.c.spin(part).unwrap()
    }

    fn arrow(&self, part: Part, step: i32) -> ElementId {
        self.c
            .arrows(part)
            .into_iter()
            .find(|(_, s)| *s == step)
            .unwrap()
            .0
    }
}

fn commands(outputs: &[PanelOutput]) -> Vec<BarberCommand> {
    outputs
        .iter()
        .filter_map(|o| match o {
            PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Barber(c))) => Some(*c),
            _ => None,
        })
        .collect()
}

const ALUVIAN: u32 = 1;
const GEARKNIGHT: u32 = 6;
const UNDEAD: u32 = 11;

#[test]
fn an_opening_shows_the_window_on_the_hair() {
    let h = Harness::new(ALUVIAN);
    assert_eq!(h.c.part(), Part::Hair);
    // Skin has no styles: its arrows are ghosted.
    assert!(
        !h.host
            .root
            .element(h.arrow(Part::Skin, 1))
            .unwrap()
            .enabled()
    );
    // The mark is on the hair colour, the slider shows the hair shade.
    let (_, marks) = h.c.swatches();
    assert!(h.visible(marks[1].unwrap()));
    assert!(!h.visible(marks[0].unwrap()));
    assert!(h.visible(h.c.shade().unwrap()));
    // No toggle for a human: the option boxes hide, Accept stays put.
    for id in b::OPTION_IDS {
        assert!(!h.visible(h.id(id)));
    }
    let apply = h.host.root.element(h.id(b::APPLY_ID)).unwrap();
    assert_eq!((apply.left, apply.top), b::APPLY_AT);
}

#[test]
fn the_arrows_step_the_style_round_and_pick_the_part() {
    let mut h = Harness::new(ALUVIAN);
    // Hair style 3 of 4: forward wraps to the first.
    let up = h.arrow(Part::Hair, 1);
    assert_eq!(
        h.click(up),
        [BarberCommand::SetAppearance(AppearanceSlot::HairStyle, 0)]
    );
    // Eyes 0 of 3: back wraps to the last, and the eyes are picked.
    let down = h.arrow(Part::Eyes, -1);
    assert_eq!(
        h.click(down),
        [BarberCommand::SetAppearance(AppearanceSlot::EyesStrip, 2)]
    );
    assert_eq!(h.c.part(), Part::Eyes);
    assert!(h.c.wants_redraw());
    h.update();
    assert!(!h.c.wants_redraw());
    // The eyes have no shade; the mark is on the eye colour.
    assert!(!h.visible(h.c.shade().unwrap()));
    let (_, marks) = h.c.swatches();
    assert!(h.visible(marks[1].unwrap()));
}

#[test]
fn a_spin_picks_its_part_and_puts_a_style_back_in_range() {
    let mut h = Harness::new(ALUVIAN);
    let nose = h.spin(Part::Nose);
    assert!(h.click(nose).is_empty());
    assert_eq!(h.c.part(), Part::Nose);
    h.view.appearance.mouth_strip = 7;
    h.update();
    let mouth = h.spin(Part::Mouth);
    assert_eq!(
        h.click(mouth),
        [BarberCommand::SetAppearance(AppearanceSlot::MouthStrip, 0)]
    );
    // Nose, mouth and skin all shade the skin; the mark is on the one
    // skin colour.
    h.update();
    let (_, marks) = h.c.swatches();
    assert!(h.visible(marks[0].unwrap()));
    let shade = h.c.shade().unwrap();
    assert_eq!(
        h.send(shade, ElementMessage::ScalarChanged(0.5)),
        [BarberCommand::SetShade(ShadeSlot::Skin, 0.5)]
    );
}

#[test]
fn a_swatch_picks_a_colour_the_part_offers() {
    let mut h = Harness::new(ALUVIAN);
    let (swatches, _) = h.c.swatches();
    assert_eq!(
        h.click(swatches[2].unwrap()),
        [BarberCommand::SetAppearance(AppearanceSlot::HairColor, 2)]
    );
    // Only three hair colours.
    assert!(h.click(swatches[3].unwrap()).is_empty());
    let shade = h.c.shade().unwrap();
    assert_eq!(
        h.send(shade, ElementMessage::ScalarChanged(0.125)),
        [BarberCommand::SetShade(ShadeSlot::Hair, 0.125)]
    );
    // The nose's one skin colour asks the session nothing.
    let nose = h.spin(Part::Nose);
    h.click(nose);
    assert!(h.click(swatches[0].unwrap()).is_empty());
}

#[test]
fn accept_and_cancel_ask_the_session_and_a_close_hides_the_window() {
    let mut h = Harness::new(ALUVIAN);
    let apply = h.id(b::APPLY_ID);
    assert_eq!(h.click(apply), [BarberCommand::Finish]);
    let cancel = h.id(b::CANCEL_ID);
    assert_eq!(h.click(cancel), [BarberCommand::Cancel]);
    h.view.open = false;
    assert_eq!(h.update(), Some(false));
    assert_eq!(h.update(), None);
    // The next opening shows it again, on the hair.
    let eyes = h.spin(Part::Eyes);
    h.view.open = true;
    h.click(eyes);
    h.view.openings = 2;
    assert_eq!(h.update(), Some(true));
    assert_eq!(h.c.part(), Part::Hair);
}

#[test]
fn a_gearknight_has_no_nose_or_mouth_and_its_own_captions() {
    let h = Harness::new(GEARKNIGHT);
    assert!(!h.visible(h.spin(Part::Nose)));
    assert!(!h.visible(h.spin(Part::Mouth)));
    let skin = h.host.root.element(h.spin(Part::Skin)).unwrap();
    assert_eq!((skin.left, skin.top), (0.0, b::SKIN_TOP_WITHOUT_NOSE));
    assert!(
        !h.host
            .root
            .element(h.arrow(Part::Eyes, 1))
            .unwrap()
            .enabled()
    );
    let label = |part| {
        h.host
            .root
            .widget::<ac_ui::widgets::Button>(h.spin(part))
            .unwrap()
            .label
            .clone()
    };
    assert_eq!(
        label(Part::Hair).as_deref(),
        Some("23000002:ID_CharGen_GearText_HairButton")
    );
    assert_eq!(
        label(Part::Skin).as_deref(),
        Some("23000002:ID_CharGen_GearText_SkinButton")
    );
}

#[test]
fn the_undead_toggle_shows_its_box_and_moves_the_buttons_down() {
    let mut h = Harness::new(UNDEAD);
    let first = h.id(b::OPTION_IDS[0]);
    assert!(h.visible(first));
    assert!(!h.visible(h.id(b::OPTION_IDS[1])));
    assert_eq!(
        h.host
            .root
            .widget::<ac_ui::widgets::Button>(first)
            .unwrap()
            .label
            .as_deref(),
        Some("23000001:ID_Barber_Undead_NoFlame")
    );
    for (id, at) in [(b::APPLY_ID, b::APPLY_AT), (b::CANCEL_ID, b::CANCEL_AT)] {
        let e = h.host.root.element(h.id(id)).unwrap();
        assert_eq!((e.left, e.top), (at.0, b::BUTTONS_TOP_UNDER_TOGGLE));
    }
    assert_eq!(h.click(first), [BarberCommand::SetToggle(true)]);
    h.view.toggle_on = true;
    h.update();
    assert!(
        h.host
            .root
            .widget::<ac_ui::widgets::Button>(first)
            .unwrap()
            .selected()
    );
}

#[test]
fn the_turn_buttons_turn_the_preview_and_the_same_one_stops_it() {
    let mut h = Harness::new(ALUVIAN);
    let clockwise = h.id(b::ROTATE_CLOCKWISE_ID);
    let counter = h.id(b::ROTATE_COUNTER_CLOCKWISE_ID);
    h.click(clockwise);
    h.click(clockwise);
    h.click(counter);
    assert_eq!(
        h.c.take_preview_requests(),
        [
            PreviewRequest::RotateClockwise,
            PreviewRequest::RotateClockwise,
            PreviewRequest::RotateCounterClockwise,
        ]
    );
    assert!(h.c.take_preview_requests().is_empty());
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::import_rooted;
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

    /// The main layout's barber slot builds the barber layout: every spin
    /// with its arrows, the nine swatches and marks, the disc, the slider,
    /// the preview, the turn buttons, Accept, Cancel and the option boxes.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_barber_slot_binds_every_part_of_the_window() {
        let content = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let layout = import_rooted(
            &mut host.root,
            &content,
            b::MAIN_LAYOUT_ID,
            b::SLOT_ID,
            content.font(DEFAULT_FONT_ID),
        )
        .expect("the barber slot");
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let mut c = BarberController::bind(&mut host.root, &layout);
        let text = |_: u32, _: &str| None;
        let shown = c
            .update(
                &mut host.root,
                &view(ALUVIAN, 1),
                Some(&gender()),
                None,
                None,
                &text,
            )
            .visible;
        assert_eq!(shown, Some(true));
        let state = |part| host.root.retail_state(c.spin(part).unwrap());
        assert_eq!(state(Part::Hair), Some(states::HIGHLIGHT));
        assert_eq!(state(Part::Eyes), Some(states::NORMAL));
        let skin_arrow = c.arrows(Part::Skin)[0].0;
        assert!(!host.root.element(skin_arrow).unwrap().enabled());
        for (_, part) in b::SPINS {
            assert!(c.spin(part).is_some(), "{part:?} spin");
            assert_eq!(c.arrows(part).len(), 2, "{part:?} arrows");
        }
        let (swatches, marks) = c.swatches();
        assert!(swatches.iter().all(Option::is_some));
        assert!(marks.iter().all(Option::is_some));
        assert!(c.shade().is_some());
        assert!(c.viewport().is_some());
        for id in [
            b::ROTATE_CLOCKWISE_ID,
            b::ROTATE_COUNTER_CLOCKWISE_ID,
            b::APPLY_ID,
            b::CANCEL_ID,
        ]
        .into_iter()
        .chain(b::OPTION_IDS)
        {
            let e = layout.find(id).expect("element");
            assert!(
                host.root.widget::<ac_ui::widgets::Button>(e).is_some(),
                "{id:#x} is a button"
            );
        }
        let apply = host
            .root
            .element(layout.find(b::APPLY_ID).unwrap())
            .unwrap();
        assert_eq!((apply.left, apply.top), b::APPLY_AT);
        let cancel = host
            .root
            .element(layout.find(b::CANCEL_ID).unwrap())
            .unwrap();
        assert_eq!((cancel.left, cancel.top), b::CANCEL_AT);
    }
}
