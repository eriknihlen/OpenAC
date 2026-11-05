//! The wizard's heritage page: thirteen heritage buttons, the backdrop
//! art per heritage, and the description of the starting and bonus
//! skills.

use ac_core::chargen::ChargenHeritageGroup as H;
use ac_runtime::session::creation::CreationCommand;

use super::PageContext;
use crate::draw::Color;
use crate::pregame::rich_text;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::{Button, ScrollSource, Scrollbar};

/// Heritage buttons and the heritage each picks.
pub const HERITAGE_BY_BUTTON: [(u32, u32); 13] = [
    (0x1000_03BF, H::ALUVIAN.0),
    (0x1000_03C1, H::GHARUNDIM.0),
    (0x1000_03C2, H::SHO.0),
    (0x1000_03C3, H::VIAMONTIAN.0),
    (0x1000_0590, H::SHADOWBOUND.0),
    (0x1000_05A9, H::GEARKNIGHT.0),
    (0x1000_05E8, H::TUMEROK.0),
    (0x1000_05F1, H::LUGIAN.0),
    (0x1000_05C4, H::EMPYREAN.0),
    (0x1000_0591, H::PENUMBRAEN.0),
    (0x1000_05BF, H::UNDEAD.0),
    (0x1000_05C7, H::OLTHOI.0),
    (0x1000_05C8, H::OLTHOI_ACID.0),
];

/// The bonus-skills text of each heritage.
const BONUS_SKILLS_KEY: [(u32, &str); 10] = [
    (H::ALUVIAN.0, "ID_CharGen_AluvianText_BonusSkills_Trained"),
    (H::GHARUNDIM.0, "ID_CharGen_GaruText_BonusSkills_Trained"),
    (H::SHO.0, "ID_CharGen_ShoText_BonusSkills_Trained"),
    (H::VIAMONTIAN.0, "ID_CharGen_ViaText_BonusSkills_Trained"),
    (H::SHADOWBOUND.0, "ID_CharGen_ShadText_BonusSkills_Trained"),
    (H::PENUMBRAEN.0, "ID_CharGen_ShadText_BonusSkills_Trained"),
    (H::GEARKNIGHT.0, "ID_CharGen_GearText_BonusSkills_Trained"),
    (H::TUMEROK.0, "ID_CharGen_AunTText_BonusSkills_Trained"),
    (H::EMPYREAN.0, "ID_CharGen_EmpText_BonusSkills_Trained"),
    (H::UNDEAD.0, "ID_CharGen_UndText_BonusSkills_Trained"),
];

/// The backdrop's state per heritage.
pub const BACKDROP_STATE: [(u32, u32); 13] = [
    (H::ALUVIAN.0, 0x1000_0021),
    (H::GHARUNDIM.0, 0x1000_0022),
    (H::SHO.0, 0x1000_0023),
    (H::VIAMONTIAN.0, 0x1000_0024),
    (H::SHADOWBOUND.0, 0x1000_0058),
    (H::GEARKNIGHT.0, 0x1000_005A),
    (H::TUMEROK.0, 0x1000_005F),
    (H::LUGIAN.0, 0x1000_0060),
    (H::EMPYREAN.0, 0x1000_005C),
    (H::PENUMBRAEN.0, 0x1000_0059),
    (H::UNDEAD.0, 0x1000_005B),
    (H::OLTHOI.0, 0x1000_005D),
    (H::OLTHOI_ACID.0, 0x1000_005E),
];

/// The description text.
pub const DESCRIPTION_ID: u32 = 0x1000_03C4;
/// The backdrop.
pub const BACKDROP_ID: u32 = 0x1000_03BE;
/// A description's own scrollbar (under the text).
pub const DESCRIPTION_SCROLL_ID: u32 = 0x1000_02E7;
/// The header colour when the text's palette has none.
const HEADER_FALLBACK: Color = Color::rgba(0.0, 1.0, 0.0, 1.0);

/// The heritage page. See the module documentation.
#[derive(Debug, Clone)]
pub struct HeritagePage {
    buttons: Vec<(ElementId, u32, u32)>,
    description: Option<ElementId>,
    backdrop: Option<ElementId>,
}

impl HeritagePage {
    /// Bind the page under `root`.
    pub fn bind(ui: &mut UiRoot, root: ElementId) -> Self {
        let mut buttons = Vec::new();
        for (button_id, heritage) in HERITAGE_BY_BUTTON {
            if let Some(b) = ui
                .find_descendant(root, button_id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
            {
                if let Some(w) = ui.widget_mut::<Button>(b) {
                    w.listeners.click = true;
                }
                buttons.push((b, button_id, heritage));
            }
        }
        let description = ui.find_descendant(root, DESCRIPTION_ID);
        if let Some(d) = description
            && let Some(bar) = ui.find_descendant(d, DESCRIPTION_SCROLL_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(d);
        }
        Self {
            buttons,
            description,
            backdrop: ui.find_descendant(root, BACKDROP_ID),
        }
    }

    /// The heritage buttons with their dat ids and heritages.
    #[must_use]
    pub fn buttons(&self) -> &[(ElementId, u32, u32)] {
        &self.buttons
    }

    /// Show the snapshot's heritage.
    pub fn refresh(&self, ui: &mut UiRoot, ctx: &PageContext<'_>) {
        let heritage = ctx.snapshot.heritage_id;
        for &(b, _, h) in &self.buttons {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, h == heritage));
        }
        if let Some(backdrop) = self.backdrop
            && let Some(&(_, state)) = BACKDROP_STATE.iter().find(|(h, _)| *h == heritage)
        {
            ui.set_retail_state(backdrop, state);
        }
        let Some(d) = self.description else {
            return;
        };
        let header = rich_text::palette_color(ui, d, 1, HEADER_FALLBACK);
        let body = rich_text::palette_color(ui, d, 0, Color::WHITE);
        let text = ctx.text;
        let bonus = (heritage != 0)
            .then(|| BONUS_SKILLS_KEY.iter().find(|(h, _)| *h == heritage))
            .flatten()
            .and_then(|(_, key)| text(key));
        let parts = [
            (text("ID_CharGen_Heritage_StartingSkills_Header"), header),
            (text("ID_CharGen_Heritage_StartingSkills"), body),
            (
                text("ID_CharGen_Heritage_BonusSkills_Trained_Header"),
                header,
            ),
            (bonus, body),
        ];
        let segments: Vec<rich_text::Segment<'_>> =
            parts.iter().map(|(t, c)| (t.as_deref(), *c)).collect();
        let lines = rich_text::compose(ui, d, &segments);
        rich_text::set_lines(ui, d, lines);
    }

    /// A heritage button's click: its dat id and the heritage it picks.
    #[must_use]
    pub fn clicked(&self, element: ElementId, message: &ElementMessage) -> Option<(u32, u32)> {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return None;
        }
        self.buttons
            .iter()
            .find(|(b, _, _)| *b == element)
            .map(|&(_, id, h)| (id, h))
    }

    /// The random button: one of the table's heritages.
    #[must_use]
    pub fn randomize(
        ctx: &PageContext<'_>,
        random: &mut dyn FnMut(u32) -> u32,
    ) -> Option<CreationCommand> {
        let mut ids: Vec<u32> = ctx.options.heritages_by_id.keys().copied().collect();
        if ids.is_empty() {
            return None;
        }
        ids.sort_unstable();
        let pick = random(u32::try_from(ids.len()).unwrap_or(u32::MAX)) as usize;
        ids.get(pick).map(|&h| CreationCommand::SelectHeritage(h))
    }
}
