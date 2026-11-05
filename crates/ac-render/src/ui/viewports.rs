//! The UI's side of the 3-D viewports (`crate::viewport`): which
//! viewports show this frame, how big, and what each shows; and, once the
//! renderer has drawn them, the textures the viewport widgets show.
//!
//! - The inventory figure (element 0x100001D5 of the inventory window)
//!   shows while the inventory window and the figure (not the slot view)
//!   show; it shows the player posed for his heritage.
//! - The examine portrait (element 0x10000148 of the examination window)
//!   shows on the creature and character pages, for the examined object.
//! - The creation previews show on the wizard's appearance and summary
//!   pages, the look composed from the wizard's choices.
//!
//! OpenAC: `RetailPaperdollFrameView`, `RetailCreatureAppraisalFrameView`,
//! `RetailChargenPreviewFrameView`, and the creation preview controller's
//! composition (`ChargenAppearanceFactory.TryCompose`).

use std::sync::Arc;

use ac_core::chargen::{ChargenAppearanceSelection, ChargenOptions, try_compose};
use ac_core::properties::PropertyInt;
use ac_dat::DatCollection;
use ac_runtime::session::creation::CreationAppearance;
use ac_ui::draw::TextureRef;
use ac_ui::panels::appraisal::{AppraisalController, AppraisalPage};
use ac_ui::panels::paperdoll::DOLL_VIEWPORT_ID;
use ac_ui::pregame::chargen::ChargenPreview;
use ac_ui::pregame::chargen::appearance::PreviewRequest;
use ac_ui::pregame::chargen::catalog::DatChargenCatalog;
use ac_ui::root::UiRoot;
use ac_ui::tree::ElementId;
use ac_ui::widgets::Viewport;

use super::window::{EXAMINATION_WINDOW, INVENTORY_WINDOW, WindowUi};
use crate::appearance::{AppearanceKey, PaletteSpec, SubPaletteRange};
use crate::scene::Scene;
use crate::viewport::{
    ChargenLook, ViewportOutcome, ViewportRequest, ViewportSlot, ViewportSubject,
};

/// The examine portrait's viewport in the examination window.
pub const EXAMINE_VIEWPORT_ID: u32 = 0x1000_0148;

/// What the window keeps for the viewports between frames.
pub struct UiViewports {
    catalog: DatChargenCatalog,
    /// The last creation look composed, by what it was composed from.
    composed: Option<(u32, u32, CreationAppearance, Option<ChargenLook>)>,
    /// The element each slot was last requested for.
    elements: [Option<ElementId>; ViewportSlot::ALL.len()],
    /// Flashes of the paperdoll figure asked for since the window last
    /// looked: the whole figure, or one bit per part.
    figure_flashes: Vec<(bool, u32)>,
}

impl std::fmt::Debug for UiViewports {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UiViewports")
            .field("elements", &self.elements)
            .finish_non_exhaustive()
    }
}

impl UiViewports {
    /// Viewports reading the creation tables from `dats`.
    #[must_use]
    pub fn new(dats: Arc<DatCollection>) -> Self {
        Self {
            catalog: DatChargenCatalog::new(dats),
            composed: None,
            elements: [None; ViewportSlot::ALL.len()],
            figure_flashes: Vec::new(),
        }
    }

    /// Flash the paperdoll figure (the selection changed): the whole
    /// figure, or the parts whose bits `part_mask` sets.
    pub fn flash_figure(&mut self, whole_figure: bool, part_mask: u32) {
        self.figure_flashes.push((whole_figure, part_mask));
    }

    /// The figure flashes asked for since the last call, oldest first.
    pub fn take_figure_flashes(&mut self) -> Vec<(bool, u32)> {
        std::mem::take(&mut self.figure_flashes)
    }

    /// The creation look for `preview` (cached while the choices stay).
    fn look(&mut self, options: &ChargenOptions, preview: &ChargenPreview) -> Option<ChargenLook> {
        if let Some((heritage, gender, appearance, look)) = &self.composed
            && (*heritage, *gender, *appearance)
                == (preview.heritage, preview.gender, preview.appearance)
        {
            return look.clone();
        }
        let look = compose(options, &self.catalog, preview);
        self.composed = Some((
            preview.heritage,
            preview.gender,
            preview.appearance,
            look.clone(),
        ));
        look
    }
}

/// The wizard's look as the composition reads it.
#[must_use]
pub fn selection(a: &CreationAppearance) -> ChargenAppearanceSelection {
    ChargenAppearanceSelection {
        eyes_strip: a.eyes_strip,
        nose_strip: a.nose_strip,
        mouth_strip: a.mouth_strip,
        hair_style: a.hair_style,
        hair_color: a.hair_color,
        eye_color: a.eye_color,
        headgear_style: a.headgear_style,
        headgear_color: a.headgear_color,
        shirt_style: a.shirt_style,
        shirt_color: a.shirt_color,
        trousers_style: a.trousers_style,
        trousers_color: a.trousers_color,
        footwear_style: a.footwear_style,
        footwear_color: a.footwear_color,
        skin_shade: a.skin_shade,
        hair_shade: a.hair_shade,
        headgear_shade: a.headgear_shade,
        shirt_shade: a.shirt_shade,
        trousers_shade: a.trousers_shade,
        footwear_shade: a.footwear_shade,
    }
}

/// Compose the preview's look: the body, its parts, textures and palettes
/// as the renderer draws them, and the heritage's scenery. `None` when the
/// choices cannot be composed.
#[must_use]
pub fn compose(
    options: &ChargenOptions,
    catalog: &DatChargenCatalog,
    preview: &ChargenPreview,
) -> Option<ChargenLook> {
    let gender = i32::try_from(preview.gender).ok()?;
    let selection = selection(&preview.appearance);
    let result = try_compose(
        options,
        preview.heritage,
        gender,
        &selection,
        catalog,
        catalog,
        None,
    )
    .ok()?;
    let desc = &result.obj_desc;
    let key = AppearanceKey {
        setup_id: result.setup_id,
        palette: (!desc.sub_palettes.is_empty()).then(|| PaletteSpec {
            base_palette_id: result.base_palette_id,
            ranges: desc
                .sub_palettes
                .iter()
                .map(|s| SubPaletteRange::from_wire(s.sub_palette_id, s.offset, s.num_colors))
                .collect(),
        }),
        texture_changes: desc
            .texture_changes
            .iter()
            .map(|t| (t.part_index, t.old_texture_id, t.new_texture_id))
            .collect(),
        part_changes: desc
            .anim_part_changes
            .iter()
            .map(|p| (p.part_index, p.part_id))
            .collect(),
        // A character-creation preview keeps the parts as stored.
        close_degrades: false,
    };
    let backdrop_setup = options
        .try_get_heritage(preview.heritage)
        .map_or(0, |h| h.environment_setup_id);
    Some(ChargenLook {
        heritage: preview.heritage,
        gender,
        selection,
        key,
        backdrop_setup,
    })
}

/// Whether `id` and every element above it show.
fn shown(root: &UiRoot, id: ElementId) -> bool {
    let mut at = Some(id);
    while let Some(e) = at {
        if !root.element(e).is_some_and(|e| e.visible) {
            return false;
        }
        at = root.parent(e);
    }
    true
}

/// The pixel size and laid-out aspect of a viewport element, its texture
/// drawn at `pixels_per_unit` framebuffer pixels a UI unit (two on Retina);
/// `None` for one with no area.
fn extent(root: &UiRoot, id: ElementId, pixels_per_unit: f32) -> Option<((u32, u32), f32)> {
    let e = root.element(id)?;
    let (sx, sy) = root.canvas_scale();
    let (w, h) = (
        (e.width * sx * pixels_per_unit).round(),
        (e.height * sy * pixels_per_unit).round(),
    );
    (w >= 1.0 && h >= 1.0 && e.width > 0.0 && e.height > 0.0)
        .then(|| ((w as u32, h as u32), e.width / e.height))
}

impl WindowUi {
    /// The paperdoll figure flashes asked for since the last call (OpenAC
    /// `PaperdollFigureLightingRelay`): the whole figure, or one bit per
    /// part.
    pub fn take_figure_flashes(&mut self) -> Vec<(bool, u32)> {
        self.viewports.take_figure_flashes()
    }

    /// The viewports that show this frame and what each shows, and the
    /// creation preview's button presses since the last frame. Their textures
    /// have `pixels_per_unit` texels a UI unit.
    pub fn viewport_requests(
        &mut self,
        scene: &Scene,
        pixels_per_unit: f32,
    ) -> (Vec<ViewportRequest>, Vec<PreviewRequest>) {
        let mut requests = Vec::new();
        let root_frame = |ui: &UiRoot, name: &str| ui.window(name).map(|w| w.frame);
        // The inventory figure.
        let root = &self.host.root;
        let doll = root_frame(root, INVENTORY_WINDOW)
            .and_then(|frame| root.find_descendant(frame, DOLL_VIEWPORT_ID))
            .filter(|id| root.widget::<Viewport>(*id).is_some());
        if let Some(id) = doll
            && shown(root, id)
            && let Some((size, aspect)) = extent(root, id, pixels_per_unit)
        {
            requests.push((
                id,
                ViewportRequest {
                    slot: ViewportSlot::Paperdoll,
                    size,
                    aspect,
                    subject: ViewportSubject::Paperdoll {
                        heritage: self.heritage(),
                    },
                },
            ));
        }
        // The examine portrait.
        let examined = self
            .host
            .with_controller::<AppraisalController, _>(EXAMINATION_WINDOW, |c, _| {
                matches!(c.page(), AppraisalPage::Creature | AppraisalPage::Character)
                    .then(|| c.current_object())
            })
            .flatten()
            .filter(|guid| *guid != 0);
        let root = &self.host.root;
        let portrait = root_frame(root, EXAMINATION_WINDOW)
            .and_then(|frame| root.find_descendant(frame, EXAMINE_VIEWPORT_ID))
            .filter(|id| root.widget::<Viewport>(*id).is_some());
        if let (Some(guid), Some(id)) = (examined, portrait)
            && shown(root, id)
            && let Some((size, aspect)) = extent(root, id, pixels_per_unit)
        {
            requests.push((
                id,
                ViewportRequest {
                    slot: ViewportSlot::Examine,
                    size,
                    aspect,
                    subject: ViewportSubject::Examine { guid },
                },
            ));
        }
        // The creation previews.
        let mut controls = Vec::new();
        let preview = match self.pregame.as_mut().and_then(|p| p.creation_mut()) {
            Some(creation) => {
                controls = creation.take_preview_requests();
                creation.preview()
            }
            None => None,
        };
        if let (Some(preview), Some(options)) = (preview, scene.pregame().options.clone())
            && shown(&self.host.root, preview.viewport)
            && let Some((size, aspect)) = extent(&self.host.root, preview.viewport, pixels_per_unit)
            && let Some(look) = self.viewports.look(&options, &preview)
        {
            requests.push((
                preview.viewport,
                ViewportRequest {
                    slot: if preview.summary {
                        ViewportSlot::ChargenSummary
                    } else {
                        ViewportSlot::ChargenAppearance
                    },
                    size,
                    aspect,
                    subject: ViewportSubject::Chargen(Box::new(look)),
                },
            ));
        }
        let mut out = Vec::with_capacity(requests.len());
        for (element, request) in requests {
            self.viewports.elements[request.slot.index()] = Some(element);
            out.push(request);
        }
        (out, controls)
    }

    /// Show what the renderer made of each viewport requested this frame.
    pub fn show_viewports(&mut self, outcomes: &[(ViewportSlot, ViewportOutcome)]) {
        for &(slot, outcome) in outcomes {
            let Some(element) = self.viewports.elements[slot.index()] else {
                continue;
            };
            let texture = match outcome {
                ViewportOutcome::Drawn => Some(slot.texture()),
                ViewportOutcome::Clear => None,
                ViewportOutcome::Keep => continue,
            };
            if let Some(viewport) = self.host.root.widget_mut::<Viewport>(element) {
                viewport.texture = texture;
            }
        }
    }

    /// The texture a viewport widget shows.
    #[must_use]
    pub fn viewport_texture(&self, slot: ViewportSlot) -> Option<TextureRef> {
        let element = self.viewports.elements[slot.index()]?;
        self.host.root.widget::<Viewport>(element)?.texture
    }

    /// The player's heritage: his object's property, else his own numbers'
    /// (the description writes both; 0 before it arrives).
    fn heritage(&self) -> u32 {
        let key = PropertyInt::HERITAGE_GROUP.0;
        let player = self.facts.state.player;
        let from_object = self
            .facts
            .objects
            .get(player)
            .map_or(0, |o| o.properties.get_int(key, 0));
        let heritage = if from_object > 0 {
            from_object
        } else {
            self.facts
                .numbers()
                .and_then(|n| n.int_properties().get(&key).copied())
                .unwrap_or(0)
        };
        u32::try_from(heritage).unwrap_or(0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_viewport_texture_has_a_texel_for_every_framebuffer_pixel_it_covers() {
        let mut ui = UiRoot::new();
        ui.set_size(800.0, 600.0);
        let root = ui.root();
        let id = ui.add(
            root,
            ac_ui::tree::Element::at(10.0, 10.0, 120.0, 200.0),
            Viewport::default(),
        );
        assert_eq!(extent(&ui, id, 1.0), Some(((120, 200), 0.6)));
        // A Retina window: two pixels a UI unit, the same laid-out aspect.
        assert_eq!(extent(&ui, id, 2.0), Some(((240, 400), 0.6)));
    }

    #[test]
    fn every_choice_reaches_the_composition() {
        let a = CreationAppearance {
            eyes_strip: 1,
            nose_strip: 2,
            mouth_strip: 3,
            hair_style: 4,
            hair_color: 5,
            eye_color: 6,
            headgear_style: 7,
            headgear_color: 8,
            shirt_style: 9,
            shirt_color: 10,
            trousers_style: 11,
            trousers_color: 12,
            footwear_style: 13,
            footwear_color: 14,
            skin_shade: 0.1,
            hair_shade: 0.2,
            headgear_shade: 0.3,
            shirt_shade: 0.4,
            trousers_shade: 0.5,
            footwear_shade: 0.6,
        };
        let s = selection(&a);
        assert_eq!(
            (
                s.eyes_strip,
                s.nose_strip,
                s.mouth_strip,
                s.hair_style,
                s.hair_color
            ),
            (1, 2, 3, 4, 5)
        );
        assert_eq!(
            (
                s.eye_color,
                s.headgear_style,
                s.headgear_color,
                s.shirt_style,
                s.shirt_color
            ),
            (6, 7, 8, 9, 10)
        );
        assert_eq!(
            (
                s.trousers_style,
                s.trousers_color,
                s.footwear_style,
                s.footwear_color
            ),
            (11, 12, 13, 14)
        );
        assert_eq!(
            (
                s.skin_shade,
                s.hair_shade,
                s.headgear_shade,
                s.shirt_shade,
                s.trousers_shade,
                s.footwear_shade
            ),
            (0.1, 0.2, 0.3, 0.4, 0.5, 0.6)
        );
        // Nothing chosen stays nothing chosen.
        assert_eq!(
            selection(&CreationAppearance::default()),
            ChargenAppearanceSelection::default()
        );
    }
}
