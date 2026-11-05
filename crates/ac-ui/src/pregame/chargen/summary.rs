//! The wizard's summary page: the name entry, the overview list
//! (profession, gender, heritage, town, attributes, vitals, credits left,
//! specialized and trained skills with their scores), the how-to text with
//! name suggestions for the heritage, and the 3-D preview.

use ac_core::chargen::{
    ChargenHeritageGroup as H, ChargenHeritageOptions, ChargenOptions,
    ChargenSkillAdvancementClass as Class, ChargenSkillAdvancementSet, chargen_skill_name,
    chargen_skill_score,
};
use ac_runtime::session::creation::{CreationCommand, TEMPLATE_UNSET};

use super::PageContext;
use crate::draw::Color;
use crate::panels::RowTemplates;
use crate::pregame::rich_text;
use crate::root::{ElementMessage, UiRoot};
use crate::tree::ElementId;
use crate::widgets::{Field, ListBox, ScrollSource, Scrollbar, Text};

/// The overview list.
pub const LIST_ID: u32 = 0x1000_0400;
/// The overview list's scrollbar.
pub const SCROLL_ID: u32 = 0x1000_0401;
/// The name entry.
pub const NAME_ID: u32 = 0x1000_0402;
/// The how-to text.
pub const HOW_TO_ID: u32 = 0x1000_0404;
/// The 3-D preview.
pub const VIEWPORT_ID: u32 = 0x1000_0406;
/// A line row's text.
pub const SINGLE_LINE_ID: u32 = 0x1000_02F9;
/// A header row's text.
pub const HEADER_TEXT_ID: u32 = 0x1000_00FE;
/// A pair row's key.
pub const KEY_TEXT_ID: u32 = 0x1000_02FC;
/// A pair row's value.
pub const VALUE_TEXT_ID: u32 = 0x1000_02FD;
/// The how-to text's own scrollbar.
pub const HOW_TO_SCROLL_ID: u32 = 0x1000_02E7;
/// The longest name the entry takes.
pub const MAX_NAME_LENGTH: usize = 32;

/// The name suggestions per heritage: (male, female).
const NAME_SUGGESTIONS: [(u32, &str, &str); 4] = [
    (
        H::ALUVIAN.0,
        "ID_CharGen_AluMaleNames",
        "ID_CharGen_AluFemaleNames",
    ),
    (
        H::GHARUNDIM.0,
        "ID_CharGen_GharuMaleNames",
        "ID_CharGen_GharuFemaleNames",
    ),
    (
        H::SHO.0,
        "ID_CharGen_ShoMaleNames",
        "ID_CharGen_ShoFemaleNames",
    ),
    (
        H::VIAMONTIAN.0,
        "ID_CharGen_ViaMaleNames",
        "ID_CharGen_ViaFemaleNames",
    ),
];

/// The characters a name may have: letters, space, apostrophe, hyphen.
#[must_use]
pub fn name_filter(c: char) -> bool {
    ((c as u32) < 0x100 && c.is_ascii_alphabetic()) || matches!(c, ' ' | '\'' | '-')
}

/// One row of the overview.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OverviewRow {
    /// A line of text.
    Line(String),
    /// A group header.
    Header(String),
    /// A name and its number.
    Pair(String, i32),
}

/// The overview the summary lists.
#[must_use]
pub fn overview(ctx: &PageContext<'_>) -> Vec<OverviewRow> {
    let s = ctx.snapshot;
    let Some(heritage) = ctx.options.try_get_heritage(s.heritage_id) else {
        return Vec::new();
    };
    let a = &s.attributes;
    let mut rows = vec![
        OverviewRow::Line(format!(
            "Profession: {}",
            profession_name(heritage, s.template)
        )),
        OverviewRow::Line(format!("Gender: {}", gender_name(heritage, s.gender_key))),
        OverviewRow::Line(format!("Heritage: {}", heritage.name)),
        OverviewRow::Line(format!(
            "Starting Town: {}",
            starter_area_name(ctx.options, s.start_area)
        )),
        OverviewRow::Header("Attributes".to_owned()),
    ];
    for (name, value) in [
        ("Strength", a.strength),
        ("Endurance", a.endurance),
        ("Coordination", a.coordination),
        ("Quickness", a.quickness),
        ("Focus", a.focus),
        ("Self", a.self_),
        ("Health", a.endurance / 2),
        ("Stamina", a.endurance),
        ("Mana", a.self_),
        ("Skill Credits", s.remaining_skill_credits),
    ] {
        rows.push(OverviewRow::Pair(name.to_owned(), value));
    }
    for (header, class) in [
        ("Specialized Skills", Class::Specialized),
        ("Trained Skills", Class::Trained),
    ] {
        rows.push(OverviewRow::Header(header.to_owned()));
        for skill in 1..ChargenSkillAdvancementSet::SLOT_COUNT as u32 {
            if s.skills.get(skill as usize).copied() != Some(class) {
                continue;
            }
            let score = chargen_skill_score(ctx.options, skill, a, class);
            rows.push(OverviewRow::Pair(
                chargen_skill_name(ctx.options, skill),
                i32::try_from(score).unwrap_or(i32::MAX),
            ));
        }
    }
    rows
}

fn profession_name(heritage: &ChargenHeritageOptions, template: u32) -> String {
    if template == TEMPLATE_UNSET {
        return "None".to_owned();
    }
    heritage
        .templates
        .get(template as usize)
        .map_or_else(|| "None".to_owned(), |t| t.name.clone())
}

fn gender_name(heritage: &ChargenHeritageOptions, gender: u32) -> String {
    i32::try_from(gender)
        .ok()
        .and_then(|g| heritage.genders_by_key.get(&g))
        .map_or_else(|| "None".to_owned(), |g| g.name.clone())
}

fn starter_area_name(options: &ChargenOptions, area: i32) -> String {
    options
        .try_get_starter_area(area)
        .map_or_else(|| "None".to_owned(), |a| a.name.clone())
}

/// The how-to text: the how-to, the heritage's name suggestions for the
/// gender (female for gender 2, else male), then its end.
#[must_use]
pub fn how_to(heritage: u32, gender: u32, text: &dyn Fn(&str) -> Option<String>) -> String {
    let mut s = String::new();
    if let Some(t) = text("ID_CharGen_SummaryHowTo") {
        s.push_str(&t);
    }
    if let Some(&(_, male, female)) = NAME_SUGGESTIONS.iter().find(|(h, _, _)| *h == heritage)
        && let Some(t) = text(if gender == 2 { female } else { male })
    {
        s.push_str(&t);
    }
    if let Some(t) = text("ID_CharGen_SummaryHowToEnd") {
        s.push_str(&t);
    }
    s
}

/// The summary page. See the module documentation.
#[derive(Debug, Clone)]
pub struct SummaryPage {
    list: Option<ElementId>,
    name: Option<ElementId>,
    how_to: Option<ElementId>,
    viewport: Option<ElementId>,
    last_committed: String,
    name_focused: bool,
}

impl SummaryPage {
    /// Bind the page under `root`.
    pub fn bind(ui: &mut UiRoot, root: ElementId) -> Self {
        let list = ui
            .find_descendant(root, LIST_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some());
        if let Some(l) = list {
            crate::panels::link_list_scrollbar(ui, root, l);
        }
        let name = ui
            .find_descendant(root, NAME_ID)
            .filter(|f| ui.widget::<Field>(*f).is_some());
        if let Some(f) = name.and_then(|f| ui.widget_mut::<Field>(f)) {
            f.character_filter = Some(Box::new(name_filter));
            f.clear_on_submit = false;
            f.record_history = false;
        }
        let how_to = ui
            .find_descendant(root, HOW_TO_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
        if let Some(h) = how_to
            && let Some(bar) = ui.find_descendant(h, HOW_TO_SCROLL_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(bar)
        {
            s.model = ScrollSource::Element(h);
        }
        Self {
            list,
            name,
            how_to,
            viewport: ui.find_descendant(root, VIEWPORT_ID),
            last_committed: String::new(),
            name_focused: false,
        }
    }

    /// The name entry.
    #[must_use]
    pub fn name_field(&self) -> Option<ElementId> {
        self.name
    }

    /// The overview list.
    #[must_use]
    pub fn list(&self) -> Option<ElementId> {
        self.list
    }

    /// The how-to text.
    #[must_use]
    pub fn how_to_text(&self) -> Option<ElementId> {
        self.how_to
    }

    /// The 3-D preview's element.
    #[must_use]
    pub fn viewport(&self) -> Option<ElementId> {
        self.viewport
    }

    /// Show the snapshot: the name (unless the player is typing it), the
    /// overview, the how-to text.
    pub fn refresh(
        &mut self,
        ui: &mut UiRoot,
        ctx: &PageContext<'_>,
        templates: &mut dyn RowTemplates,
    ) {
        let s = ctx.snapshot;
        if let Some(f) = self.name
            && let Some(field) = ui.widget_mut::<Field>(f)
            && !field.is_focused()
            && field.text() != s.name
        {
            field.set_text(&s.name);
            self.last_committed.clone_from(&s.name);
        }
        self.rebuild_list(ui, ctx, templates);
        if let Some(h) = self.how_to {
            let text = how_to(s.heritage_id, s.gender_key, ctx.text);
            if !text.is_empty() {
                let color = ui
                    .widget::<Text>(h)
                    .map_or(Color::WHITE, |t| t.default_color);
                let lines = rich_text::compose(ui, h, &[(Some(&text), color)]);
                rich_text::set_lines(ui, h, lines);
            }
        }
    }

    fn rebuild_list(
        &self,
        ui: &mut UiRoot,
        ctx: &PageContext<'_>,
        templates: &mut dyn RowTemplates,
    ) {
        let Some(list) = self.list else {
            return;
        };
        let entries = ui
            .widget::<ListBox>(list)
            .map(|l| l.templates.clone())
            .unwrap_or_default();
        if entries.len() < 3 {
            return;
        }
        ui.with::<ListBox, _>(list, |l, ui| l.flush(ui));
        for row in overview(ctx) {
            let (template, texts): (_, Vec<(u32, String)>) = match row {
                OverviewRow::Line(t) => (&entries[0], vec![(SINGLE_LINE_ID, t)]),
                OverviewRow::Header(t) => (&entries[1], vec![(HEADER_TEXT_ID, t)]),
                OverviewRow::Pair(k, v) => (
                    &entries[2],
                    vec![(KEY_TEXT_ID, k), (VALUE_TEXT_ID, v.to_string())],
                ),
            };
            let Some(r) = templates.build_row(ui, template.layout, template.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |l, ui| l.add_prebuilt_row(ui, list, r));
            for (id, text) in texts {
                if let Some(t) = ui
                    .find_descendant(r, id)
                    .filter(|t| ui.widget::<Text>(*t).is_some())
                {
                    rich_text::set_line(ui, t, &text);
                }
            }
        }
    }

    /// The name entry lost the keyboard since the last call: its text is
    /// committed.
    pub fn follow_focus(&mut self, ui: &mut UiRoot) -> Option<NameCommit> {
        let field = self.name?;
        let focused = ui.widget::<Field>(field).is_some_and(Field::is_focused);
        let lost = self.name_focused && !focused;
        self.name_focused = focused;
        if !lost {
            return None;
        }
        let text = ui.widget::<Field>(field).map(Field::text)?;
        Some(self.commit(ui, &text))
    }

    /// What a message on the page asks; `None` when it is not the page's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> Option<NameCommit> {
        match message {
            ElementMessage::Submitted(text) if Some(element) == self.name => {
                Some(self.commit(ui, text))
            }
            _ => None,
        }
    }

    fn commit(&mut self, ui: &mut UiRoot, text: &str) -> NameCommit {
        if text.chars().count() > MAX_NAME_LENGTH {
            if let Some(f) = self.name.and_then(|f| ui.widget_mut::<Field>(f)) {
                f.set_text(&self.last_committed);
            }
            return NameCommit::TooLong;
        }
        self.last_committed = text.to_owned();
        NameCommit::Set(CreationCommand::SetName(text.to_owned()))
    }
}

/// A committed name.
#[derive(Debug, Clone, PartialEq)]
pub enum NameCommit {
    /// Set it.
    Set(CreationCommand),
    /// Longer than the game takes: the entry went back to the last name
    /// and the player is told.
    TooLong,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_name_takes_letters_spaces_apostrophes_and_hyphens() {
        // OpenAC `SummaryNameField_NameInputFilter_RejectsDigitsAndSymbols`.
        for c in ['a', 'Z', ' ', '\'', '-'] {
            assert!(name_filter(c), "{c}");
        }
        for c in ['1', '!', '_', '.', 'é'] {
            assert!(!name_filter(c), "{c}");
        }
    }

    #[test]
    fn the_how_to_names_suggestions_for_the_four_human_heritages() {
        // OpenAC `SummaryHowToText_ComposesHowToPlusNameSuggestionsPlusHowToEnd_ForNamedHeritages`
        // and `..._SkipsNameSuggestions_ForHeritagesWithNoRealString`.
        let text = |key: &str| Some(format!("[{key}]"));
        assert_eq!(
            how_to(H::SHO.0, 2, &text),
            "[ID_CharGen_SummaryHowTo][ID_CharGen_ShoFemaleNames][ID_CharGen_SummaryHowToEnd]"
        );
        assert_eq!(
            how_to(H::ALUVIAN.0, 1, &text),
            "[ID_CharGen_SummaryHowTo][ID_CharGen_AluMaleNames][ID_CharGen_SummaryHowToEnd]"
        );
        assert_eq!(
            how_to(H::LUGIAN.0, 1, &text),
            "[ID_CharGen_SummaryHowTo][ID_CharGen_SummaryHowToEnd]"
        );
    }
}
