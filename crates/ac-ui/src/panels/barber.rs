//! The barber window (layout 0x21000068): the hair, eyes, nose, mouth and
//! skin spins (a spin picks its part, its two arrows step its style), the
//! picked part's colour swatches, gradient disc and shade slider, the 3-D
//! preview with its two turn buttons, the heritage's one toggle, and the
//! Accept and Cancel buttons.
//!
//! The server opens it; the look being built is the session's barber,
//! which the window draws and changes by commands. Each opening sets the
//! window up anew, on the hair. The window shows while the barber is open.

use ac_core::chargen::{ChargenGenderOptions, ChargenHeritageGroup as H};
use ac_runtime::gameplay::{BarberCommand, BarberView, GameplayCommand};
use ac_runtime::session::SessionCommand;
use ac_runtime::session::creation::{AppearanceSlot, ShadeSlot, UNSET};

use crate::draw::Color;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::PanelOutput;
use crate::panels::items::emit;
use crate::pregame::chargen::appearance::{
    DECREMENT_ARROW_ID, GRAD_CIRCLE_ID, INCREMENT_ARROW_ID, Part, PreviewRequest, SHADE_SCROLL_ID,
    SWATCH_IDS, SWATCH_OVERLAY_IDS, SwatchSourcesDyn, SwatchTextures, cycle_index, swatch_colors,
};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::tree::ElementId;
use crate::widgets::{Button, DatElement, Scrollbar};

/// The game's main layout, which places the barber window.
pub const MAIN_LAYOUT_ID: u32 = 0x2100_000F;
/// The barber window's slot in it.
pub const SLOT_ID: u32 = 0x1000_0598;
/// The barber window's layout.
pub const LAYOUT_ID: u32 = 0x2100_0068;
/// Its root.
pub const ROOT_ID: u32 = 0x1000_0599;
/// The 3-D preview.
pub const VIEWPORT_ID: u32 = 0x1000_059B;
/// The spins and the part each picks.
pub const SPINS: [(u32, Part); 5] = [
    (0x1000_059E, Part::Hair),
    (0x1000_059F, Part::Eyes),
    (0x1000_05A0, Part::Nose),
    (0x1000_05A1, Part::Mouth),
    (0x1000_05A2, Part::Skin),
];
/// Turn the figure clockwise.
pub const ROTATE_CLOCKWISE_ID: u32 = 0x1000_05A4;
/// Turn it counter-clockwise.
pub const ROTATE_COUNTER_CLOCKWISE_ID: u32 = 0x1000_05A5;
/// Accept.
pub const APPLY_ID: u32 = 0x1000_05A6;
/// Cancel.
pub const CANCEL_ID: u32 = 0x1000_05A7;
/// The three option boxes; only the first is ever used.
pub const OPTION_IDS: [u32; 3] = [0x1000_05C9, 0x1000_05CA, 0x1000_05CB];
/// The skin spin's top in the nose's place, for a heritage without nose
/// and mouth.
pub const SKIN_TOP_WITHOUT_NOSE: f32 = 88.0;
/// Where Accept stands, left and top: below the toggle when one shows.
pub const APPLY_AT: (f32, f32) = (352.0, 274.0);
/// Where Cancel stands.
pub const CANCEL_AT: (f32, f32) = (232.0, 274.0);
/// The buttons' top under a toggle.
pub const BUTTONS_TOP_UNDER_TOGGLE: f32 = 298.0;
/// The table the spins' captions come from.
pub const CAPTION_TABLE: u32 = 0x2300_0002;
/// The table the toggle's caption comes from.
pub const TOGGLE_TABLE: u32 = 0x2300_0001;

/// A string of a table by key.
pub type TextLookup<'a> = &'a dyn Fn(u32, &str) -> Option<String>;

/// What a frame's update asks of the window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BarberUpdate {
    /// Show (true) or hide the window.
    pub visible: Option<bool>,
}

/// A part's style slot (skin has none).
fn style_slot(part: Part) -> Option<AppearanceSlot> {
    Some(match part {
        Part::Hair => AppearanceSlot::HairStyle,
        Part::Eyes => AppearanceSlot::EyesStrip,
        Part::Nose => AppearanceSlot::NoseStrip,
        Part::Mouth => AppearanceSlot::MouthStrip,
        _ => return None,
    })
}

fn style_count(part: Part, g: &ChargenGenderOptions) -> usize {
    match part {
        Part::Hair => g.hair_styles.len(),
        Part::Eyes => g.eye_strips.len(),
        Part::Nose => g.nose_strips.len(),
        Part::Mouth => g.mouth_strips.len(),
        _ => 0,
    }
}

/// How many swatches the part offers: the hair and eye colours, one skin
/// colour for the rest.
fn color_count(part: Part, g: Option<&ChargenGenderOptions>) -> usize {
    match (part, g) {
        (Part::Hair, Some(g)) => g.hair_colors.len(),
        (Part::Eyes, Some(g)) => g.eye_colors.len(),
        (Part::Hair | Part::Eyes, None) => 0,
        _ => 1,
    }
}

/// The swatch the mark stands on: the hair colour (the first while none
/// is chosen), the eye colour (none while none is chosen), the one skin
/// colour for the rest.
#[must_use]
pub fn marked_swatch(part: Part, view: &BarberView) -> Option<usize> {
    let a = &view.appearance;
    match part {
        Part::Hair if a.hair_color == UNSET => Some(0),
        Part::Hair => Some(a.hair_color as usize),
        Part::Eyes if a.eye_color == UNSET => None,
        Part::Eyes => Some(a.eye_color as usize),
        _ => Some(0),
    }
}

fn tint(rgb: ac_core::chargen::ChargenSwatchRgb) -> Color {
    Color::rgba(
        f32::from(rgb.r) / 255.0,
        f32::from(rgb.g) / 255.0,
        f32::from(rgb.b) / 255.0,
        1.0,
    )
}

fn command(ui: &mut UiRoot, command: BarberCommand) {
    emit(
        ui,
        PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Barber(command))),
    );
}

/// See the module documentation.
#[derive(Debug)]
pub struct BarberController {
    spins: Vec<(ElementId, Part)>,
    arrows: Vec<(ElementId, Part, i32)>,
    swatches: [Option<ElementId>; 9],
    overlays: [Option<ElementId>; 9],
    grad: Option<ElementId>,
    shade: Option<ElementId>,
    rotate_clockwise: Option<ElementId>,
    rotate_counter_clockwise: Option<ElementId>,
    apply: Option<ElementId>,
    cancel: Option<ElementId>,
    options: [Option<ElementId>; 3],
    viewport: Option<ElementId>,
    part: Part,
    view: BarberView,
    gender: Option<ChargenGenderOptions>,
    openings: u64,
    shown: bool,
    rotating: Option<PreviewRequest>,
    /// The window's own pick changed what it shows.
    redraw: bool,
    preview_requests: Vec<PreviewRequest>,
}

impl BarberController {
    /// Bind the window's elements under `layout`.
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout) -> Self {
        let root = layout.root;
        let find = |ui: &UiRoot, id: u32| layout.find(id).or_else(|| ui.find_descendant(root, id));
        let button = |ui: &mut UiRoot, id: u32| {
            let b = find(ui, id).filter(|b| ui.widget::<Button>(*b).is_some())?;
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
            Some(b)
        };
        let mut spins = Vec::new();
        let mut arrows = Vec::new();
        for (id, part) in SPINS {
            let Some(spin) = button(ui, id) else {
                continue;
            };
            spins.push((spin, part));
            for (arrow_id, step) in [(DECREMENT_ARROW_ID, -1), (INCREMENT_ARROW_ID, 1)] {
                let arrow = ui
                    .find_descendant(spin, arrow_id)
                    .filter(|a| ui.widget::<Button>(*a).is_some());
                if let Some(a) = arrow {
                    if let Some(w) = ui.widget_mut::<Button>(a) {
                        w.listeners.click = true;
                    }
                    arrows.push((a, part, step));
                }
            }
        }
        let mut swatches = [None; 9];
        for (slot, id) in swatches.iter_mut().zip(SWATCH_IDS) {
            *slot = button(ui, id);
        }
        let mut overlays = [None; 9];
        for (slot, id) in overlays.iter_mut().zip(SWATCH_OVERLAY_IDS) {
            *slot = find(ui, id);
        }
        let shade = find(ui, SHADE_SCROLL_ID).filter(|s| ui.widget::<Scrollbar>(*s).is_some());
        // The shade is a value slider: dragging it reports the new shade.
        if let Some(s) = shade.and_then(|s| ui.widget_mut::<Scrollbar>(s)) {
            s.scalar = true;
        }
        let mut options = [None; 3];
        for (slot, id) in options.iter_mut().zip(OPTION_IDS) {
            *slot = button(ui, id);
        }
        let c = Self {
            grad: find(ui, GRAD_CIRCLE_ID).filter(|g| ui.widget::<DatElement>(*g).is_some()),
            rotate_clockwise: button(ui, ROTATE_CLOCKWISE_ID),
            rotate_counter_clockwise: button(ui, ROTATE_COUNTER_CLOCKWISE_ID),
            apply: button(ui, APPLY_ID),
            cancel: button(ui, CANCEL_ID),
            viewport: find(ui, VIEWPORT_ID),
            spins,
            arrows,
            swatches,
            overlays,
            shade,
            options,
            part: Part::Hair,
            view: BarberView::default(),
            gender: None,
            openings: 0,
            shown: false,
            rotating: None,
            preview_requests: Vec::new(),
            redraw: false,
        };
        // Skin has no styles to step through: its arrows stay ghosted.
        c.ghost_arrows(ui, Part::Skin);
        c
    }

    /// The part picked.
    #[must_use]
    pub fn part(&self) -> Part {
        self.part
    }

    /// A spin's button.
    #[must_use]
    pub fn spin(&self, part: Part) -> Option<ElementId> {
        self.spins.iter().find(|(_, p)| *p == part).map(|(b, _)| *b)
    }

    /// A spin's arrows: step back, step forward.
    #[must_use]
    pub fn arrows(&self, part: Part) -> Vec<(ElementId, i32)> {
        self.arrows
            .iter()
            .filter(|(_, p, _)| *p == part)
            .map(|(a, _, step)| (*a, *step))
            .collect()
    }

    /// The swatches and their marks.
    #[must_use]
    pub fn swatches(&self) -> ([Option<ElementId>; 9], [Option<ElementId>; 9]) {
        (self.swatches, self.overlays)
    }

    /// The shade slider.
    #[must_use]
    pub fn shade(&self) -> Option<ElementId> {
        self.shade
    }

    /// The 3-D preview's element.
    #[must_use]
    pub fn viewport(&self) -> Option<ElementId> {
        self.viewport
    }

    /// The barber as the window last drew it.
    #[must_use]
    pub fn view(&self) -> &BarberView {
        &self.view
    }

    /// The window's own pick changed what it shows: it wants an update.
    #[must_use]
    pub fn wants_redraw(&self) -> bool {
        self.redraw
    }

    /// The turn requests for the 3-D preview since the last call.
    pub fn take_preview_requests(&mut self) -> Vec<PreviewRequest> {
        std::mem::take(&mut self.preview_requests)
    }

    fn ghost_arrows(&self, ui: &mut UiRoot, part: Part) {
        for &(arrow, p, _) in &self.arrows {
            if p == part {
                ui.set_retail_state(arrow, states::GHOSTED);
            }
        }
    }

    /// Draw `view`: set the window up on a new opening (the heritage's
    /// captions, parts and toggle, the hair picked), then the picked
    /// part's swatches, disc and shade. `gender` is the character's
    /// gender's choices; `sources` and `textures` paint the swatches;
    /// `text` reads a string of a table by key.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &BarberView,
        gender: Option<&ChargenGenderOptions>,
        sources: Option<&dyn SwatchSourcesDyn>,
        textures: Option<&mut dyn SwatchTextures>,
        text: TextLookup<'_>,
    ) -> BarberUpdate {
        self.view = view.clone();
        self.gender = gender.cloned();
        self.redraw = false;
        let mut update = BarberUpdate::default();
        if view.open && view.openings != self.openings {
            self.openings = view.openings;
            self.set_up(ui, text);
            // The figure starts still.
            self.rotating = None;
        }
        if view.open != self.shown {
            self.shown = view.open;
            update.visible = Some(view.open);
        }
        if view.open {
            self.refresh(ui, sources, textures);
        }
        update
    }

    /// A new opening: the heritage's captions and parts, the toggle, the
    /// buttons' places, and the hair picked.
    fn set_up(&mut self, ui: &mut UiRoot, text: TextLookup<'_>) {
        let heritage = self.view.heritage;
        self.part = Part::Hair;
        let captions = match H(heritage) {
            H::GEARKNIGHT => Some((
                "ID_CharGen_GearText_HairButton",
                "ID_CharGen_GearText_EyesButton",
                "ID_CharGen_GearText_SkinButton",
            )),
            H::OLTHOI | H::OLTHOI_ACID => Some((
                "ID_CharGen_OlthoiText_HairButton",
                "ID_CharGen_OlthoiText_EyesButton",
                "ID_CharGen_OlthoiText_SkinButton",
            )),
            _ => None,
        };
        if let Some((hair, eyes, skin)) = captions {
            for part in [Part::Nose, Part::Mouth] {
                if let Some(spin) = self.spin(part) {
                    ui.set_visible(spin, false);
                }
            }
            for (part, key) in [(Part::Hair, hair), (Part::Eyes, eyes), (Part::Skin, skin)] {
                if let Some(spin) = self.spin(part)
                    && let Some(caption) = text(CAPTION_TABLE, key)
                    && let Some(b) = ui.widget_mut::<Button>(spin)
                {
                    b.label = Some(caption);
                }
            }
            self.ghost_arrows(ui, Part::Eyes);
            if let Some(skin) = self.spin(Part::Skin) {
                if let Some(e) = ui.element_mut(skin) {
                    e.left = 0.0;
                    e.top = SKIN_TOP_WITHOUT_NOSE;
                }
                ui.reset_anchor_capture(skin);
            }
        }
        let toggle = self.view.toggle;
        if let Some(first) = self.options[0] {
            ui.set_visible(first, toggle.is_some());
            if let Some(t) = toggle
                && let Some(caption) = text(TOGGLE_TABLE, t.string_key())
                && let Some(b) = ui.widget_mut::<Button>(first)
            {
                b.label = Some(caption);
            }
        }
        for other in self.options[1..].iter().flatten() {
            ui.set_visible(*other, false);
        }
        let top = |at: (f32, f32)| {
            if toggle.is_some() {
                (at.0, BUTTONS_TOP_UNDER_TOGGLE)
            } else {
                at
            }
        };
        for (button, at) in [(self.apply, top(APPLY_AT)), (self.cancel, top(CANCEL_AT))] {
            if let Some(b) = button {
                if let Some(e) = ui.element_mut(b) {
                    e.left = at.0;
                    e.top = at.1;
                }
                ui.reset_anchor_capture(b);
            }
        }
    }

    /// The spins' highlight, the swatches and their mark, the disc and
    /// the shade slider for the picked part; the toggle's state.
    fn refresh(
        &self,
        ui: &mut UiRoot,
        sources: Option<&dyn SwatchSourcesDyn>,
        mut textures: Option<&mut dyn SwatchTextures>,
    ) {
        let view = &self.view;
        for &(spin, part) in &self.spins {
            let state = if part == self.part {
                states::HIGHLIGHT
            } else {
                states::NORMAL
            };
            ui.set_retail_state(spin, state);
        }
        if let Some(first) = self.options[0] {
            ui.with::<Button, _>(first, |w, ui| w.set_selected(ui, first, view.toggle_on));
        }
        let gender = self.gender.as_ref();
        let count = color_count(self.part, gender);
        let colors = match (gender, sources) {
            (Some(g), Some(src)) => swatch_colors(self.part, g, &view.appearance, src),
            _ => [None; 9],
        };
        let marked = marked_swatch(self.part, view);
        for (i, overlay) in self.overlays.iter().enumerate() {
            if let Some(o) = overlay {
                ui.set_visible(*o, marked == Some(i));
            }
        }
        for (i, swatch) in self.swatches.iter().enumerate() {
            let Some(b) = *swatch else {
                continue;
            };
            ui.set_visible(b, true);
            let offered = i < count;
            let rgb = if offered { colors[i] } else { None };
            let face = match (&mut textures, offered, rgb) {
                (Some(t), false, _) => t.blank_spot(),
                (Some(t), true, Some(c)) => t.active_spot(c),
                _ => None,
            };
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.tint = rgb.map_or(Color::WHITE, tint);
                w.color_key_face = face;
            }
        }
        let eyes = self.part == Part::Eyes;
        if let Some(g) = self.grad {
            let color = if eyes {
                None
            } else {
                marked.and_then(|i| colors.get(i).copied().flatten())
            };
            let texture = textures
                .as_mut()
                .and_then(|t| if eyes { t.grad_plug() } else { t.grad_disk() });
            if let Some(d) = ui.widget_mut::<DatElement>(g) {
                d.tint = color.map_or(Color::WHITE, tint);
                d.runtime_image = texture.map(Some);
            }
        }
        if let Some(bar) = self.shade {
            ui.set_visible(bar, !eyes);
            let shade = match self.part {
                Part::Hair => view.appearance.hair_shade,
                _ => view.appearance.skin_shade,
            };
            if !eyes && let Some(s) = ui.widget_mut::<Scrollbar>(bar) {
                s.set_scalar_position(shade.clamp(0.0, 1.0) as f32);
            }
        }
    }

    /// Pick `part`: a style outside the list is put back in range (past
    /// the end: the first; none: the last).
    fn select_part(&mut self, ui: &mut UiRoot, part: Part) {
        if let (Some(slot), Some(g)) = (style_slot(part), self.gender.as_ref()) {
            let count = style_count(part, g);
            let current = self.view.appearance.get(slot);
            let normalized = if current != UNSET && current as usize >= count {
                Some(0)
            } else if current == UNSET && count > 0 {
                Some((count - 1) as u32)
            } else {
                None
            };
            if let Some(n) = normalized {
                command(ui, BarberCommand::SetAppearance(slot, n));
            }
        }
        self.part = part;
    }

    /// A spin's arrow: step `part`'s style by `step` (wrapping), then pick
    /// the part.
    fn step_style(&mut self, ui: &mut UiRoot, part: Part, step: i32) {
        let (Some(slot), Some(g)) = (style_slot(part), self.gender.as_ref()) else {
            return;
        };
        let count = i32::try_from(style_count(part, g)).unwrap_or(i32::MAX);
        let next = cycle_index(self.view.appearance.get(slot), step, count, false);
        command(ui, BarberCommand::SetAppearance(slot, next));
        self.part = part;
    }

    /// A turn button: the same direction again stops the turn, the other
    /// way turns back and lets the other button go.
    fn rotate(&mut self, ui: &mut UiRoot, request: PreviewRequest) {
        if self.rotating == Some(request) {
            self.rotating = None;
        } else {
            let other = if request == PreviewRequest::RotateClockwise {
                self.rotate_counter_clockwise
            } else {
                self.rotate_clockwise
            };
            if let Some(b) = other {
                ui.set_retail_state(b, states::NORMAL);
            }
            self.rotating = Some(request);
        }
        self.preview_requests.push(request);
    }
}

impl PanelController for BarberController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if let ElementMessage::ScalarChanged(scalar) = message {
            if Some(element) != self.shade {
                return false;
            }
            let slot = match self.part {
                Part::Hair => Some(ShadeSlot::Hair),
                Part::Nose | Part::Mouth | Part::Skin => Some(ShadeSlot::Skin),
                _ => None,
            };
            if let Some(slot) = slot {
                command(
                    ui,
                    BarberCommand::SetShade(slot, f64::from(*scalar).clamp(0.0, 1.0)),
                );
            }
            return true;
        }
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return false;
        }
        if let Some(&(_, part, step)) = self.arrows.iter().find(|(a, _, _)| *a == element) {
            if part != Part::Skin {
                self.step_style(ui, part, step);
            }
        } else if let Some(&(_, part)) = self.spins.iter().find(|(b, _)| *b == element) {
            self.select_part(ui, part);
        } else if let Some(i) = self.swatches.iter().position(|s| *s == Some(element)) {
            if i < color_count(self.part, self.gender.as_ref()) {
                let slot = match self.part {
                    Part::Hair => Some(AppearanceSlot::HairColor),
                    Part::Eyes => Some(AppearanceSlot::EyeColor),
                    _ => None,
                };
                if let Some(slot) = slot {
                    command(ui, BarberCommand::SetAppearance(slot, i as u32));
                }
            }
        } else if Some(element) == self.rotate_clockwise {
            self.rotate(ui, PreviewRequest::RotateClockwise);
        } else if Some(element) == self.rotate_counter_clockwise {
            self.rotate(ui, PreviewRequest::RotateCounterClockwise);
        } else if Some(element) == self.apply {
            command(ui, BarberCommand::Finish);
        } else if Some(element) == self.cancel {
            command(ui, BarberCommand::Cancel);
        } else if Some(element) == self.options[0] {
            if self.view.toggle.is_some() {
                // A toggle box flipped itself on the press's release.
                let on = ui
                    .widget::<Button>(element)
                    .map_or(!self.view.toggle_on, |w| {
                        if w.toggle_behavior {
                            w.selected()
                        } else {
                            !self.view.toggle_on
                        }
                    });
                command(ui, BarberCommand::SetToggle(on));
            }
        } else {
            return false;
        }
        // A pick of the window's own (the part) changes what it shows.
        self.redraw = true;
        true
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_mark_stands_on_the_chosen_colour_or_the_one_skin_colour() {
        let mut view = BarberView::default();
        view.appearance.hair_color = 2;
        view.appearance.eye_color = UNSET;
        assert_eq!(marked_swatch(Part::Hair, &view), Some(2));
        assert_eq!(marked_swatch(Part::Eyes, &view), None);
        assert_eq!(marked_swatch(Part::Nose, &view), Some(0));
        view.appearance.hair_color = UNSET;
        assert_eq!(marked_swatch(Part::Hair, &view), Some(0));
    }

    #[test]
    fn hair_and_eyes_offer_their_colours_and_the_rest_one() {
        let g = ChargenGenderOptions {
            hair_colors: vec![1, 2, 3],
            eye_colors: vec![4, 5],
            ..ChargenGenderOptions::default()
        };
        assert_eq!(color_count(Part::Hair, Some(&g)), 3);
        assert_eq!(color_count(Part::Eyes, Some(&g)), 2);
        assert_eq!(color_count(Part::Skin, Some(&g)), 1);
        assert_eq!(color_count(Part::Mouth, None), 1);
    }
}
