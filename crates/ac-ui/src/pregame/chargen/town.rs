//! The wizard's starting-town page: four town buttons, the page's art per
//! town, and the description.

use ac_runtime::session::creation::CreationCommand;

use super::PageContext;
use crate::draw::Color;
use crate::pregame::rich_text;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::{Button, Text};

/// Town buttons and the starting area each picks (Holtburg, Shoushi,
/// Yaraq, Sanamar).
pub const START_AREA_BY_BUTTON: [(u32, i32); 4] = [
    (0x1000_040D, 0),
    (0x1000_040F, 1),
    (0x1000_040E, 2),
    (0x1000_040B, 3),
];

/// The page's state per starting area.
pub const PAGE_STATE: [(i32, u32); 4] = [
    (0, 0x1000_0034),
    (1, 0x1000_0037),
    (2, 0x1000_0036),
    (3, 0x1000_0035),
];

/// The description per starting area.
const TOWN_TEXT_KEY: [(i32, &str); 4] = [
    (0, "ID_CharGen_HoltText"),
    (1, "ID_CharGen_ShoushiText"),
    (2, "ID_CharGen_YaraqText"),
    (3, "ID_CharGen_SanamarText"),
];

/// The description text.
pub const DESCRIPTION_ID: u32 = 0x1000_0409;

/// The description: how to choose, a blank line, the town's text.
#[must_use]
pub fn description(start_area: i32, text: &dyn Fn(&str) -> Option<String>) -> String {
    let how_to = text("ID_CharGen_TownHowTo");
    let town = TOWN_TEXT_KEY
        .iter()
        .find(|(a, _)| *a == start_area)
        .and_then(|(_, key)| text(key));
    if how_to.is_none() && town.is_none() {
        return String::new();
    }
    format!(
        "{}\n\n{}\n",
        how_to.unwrap_or_default(),
        town.unwrap_or_default()
    )
}

/// The town page. See the module documentation.
#[derive(Debug, Clone)]
pub struct TownPage {
    root: ElementId,
    buttons: Vec<(ElementId, i32)>,
    description: Option<ElementId>,
}

impl TownPage {
    /// Bind the page under `root`.
    pub fn bind(ui: &mut UiRoot, root: ElementId) -> Self {
        let mut buttons = Vec::new();
        for (id, area) in START_AREA_BY_BUTTON {
            if let Some(b) = ui
                .find_descendant(root, id)
                .filter(|b| ui.widget::<Button>(*b).is_some())
            {
                if let Some(w) = ui.widget_mut::<Button>(b) {
                    w.listeners.click = true;
                }
                buttons.push((b, area));
            }
        }
        Self {
            root,
            buttons,
            description: ui
                .find_descendant(root, DESCRIPTION_ID)
                .filter(|d| ui.widget::<Text>(*d).is_some()),
        }
    }

    /// The town buttons with their starting areas.
    #[must_use]
    pub fn buttons(&self) -> &[(ElementId, i32)] {
        &self.buttons
    }

    /// Show the snapshot's town.
    pub fn refresh(&self, ui: &mut UiRoot, ctx: &PageContext<'_>) {
        let area = ctx.snapshot.start_area;
        for &(b, a) in &self.buttons {
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, a == area));
        }
        if let Some(&(_, state)) = PAGE_STATE.iter().find(|(a, _)| *a == area) {
            ui.set_retail_state(self.root, state);
        }
        let Some(d) = self.description else {
            return;
        };
        let text = description(area, ctx.text);
        let color = ui
            .widget::<Text>(d)
            .map_or(Color::WHITE, |t| t.default_color);
        let lines = rich_text::compose(ui, d, &[(Some(&text), color)]);
        rich_text::set_lines(ui, d, lines);
    }

    /// What a message on the page asks; `None` when it is not the page's.
    #[must_use]
    pub fn on_message(
        &self,
        element: ElementId,
        message: &ElementMessage,
    ) -> Option<Vec<CreationCommand>> {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return None;
        }
        self.buttons
            .iter()
            .find(|(b, _)| *b == element)
            .map(|&(_, area)| vec![CreationCommand::SelectStartArea(area)])
    }

    /// The random button: one of the first four towns the table offers.
    #[must_use]
    pub fn randomize(
        ctx: &PageContext<'_>,
        random: &mut dyn FnMut(u32) -> u32,
    ) -> Option<CreationCommand> {
        let bound = ctx.options.starter_areas.len().min(4);
        let bound = u32::try_from(bound).ok().filter(|&b| b > 0)?;
        Some(CreationCommand::SelectStartArea(
            i32::try_from(random(bound)).unwrap_or(0),
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_description_is_the_how_to_then_the_town() {
        // OpenAC `CharacterCreationTownPage.ComposeDescription`.
        let text = |key: &str| Some(format!("<{key}>"));
        assert_eq!(
            description(2, &text),
            "<ID_CharGen_TownHowTo>\n\n<ID_CharGen_YaraqText>\n"
        );
        assert_eq!(description(9, &text), "<ID_CharGen_TownHowTo>\n\n\n");
        assert_eq!(description(0, &|_: &str| None), "");
    }
}
