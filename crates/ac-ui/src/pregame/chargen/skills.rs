//! The wizard's skills page: every skill the heritage can pay for, in
//! four groups (specialized, trained, usable untrained, unusable
//! untrained) under authored headers, each row with its starting score,
//! the credits to raise and to lower it and the two arrows; the credits
//! left; and the info box of the selected skill (name and score,
//! description, training bonus, formula).

use std::collections::HashMap;

use ac_core::chargen::{
    ChargenHeritageOptions, ChargenOptions, ChargenSkillAdvancementClass as Class,
    ChargenSkillAdvancementSet, chargen_skill_name, chargen_skill_score,
};
use ac_core::player::SkillFormula;
use ac_runtime::session::creation::CreationCommand;

use super::PageContext;
use crate::draw::Color;
use crate::panels::RowTemplates;
use crate::pregame::rich_text;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, DatElement, ListBox, Text};

/// The skill list.
pub const LIST_ID: u32 = 0x1000_03F7;
/// The credits left.
pub const CREDITS_ID: u32 = 0x1000_03F9;
/// The info box's title.
pub const INFO_TITLE_ID: u32 = 0x1000_03FB;
/// The info box's text.
pub const INFO_TEXT_ID: u32 = 0x1000_03FC;
/// The info box's frame.
pub const INFO_FRAME_ID: u32 = 0x1000_03FA;
/// A header row's caption.
pub const HEADER_CAPTION_ID: u32 = 0x1000_02F6;
/// A row's name.
pub const ROW_NAME_ID: u32 = 0x1000_0301;
/// A row's score.
pub const ROW_LEVEL_ID: u32 = 0x1000_0302;
/// A row's raise cost.
pub const ROW_UP_COST_ID: u32 = 0x1000_0303;
/// A row's lower cost.
pub const ROW_DOWN_COST_ID: u32 = 0x1000_0306;
/// A row's raise arrow.
pub const ROW_UP_ID: u32 = 0x1000_0304;
/// A row's lower arrow.
pub const ROW_DOWN_ID: u32 = 0x1000_0305;
/// An arrow's state when it cannot be used.
pub const ARROW_GHOSTED_STATE: u32 = 0x1000_001A;
/// An arrow's state when it can.
pub const ARROW_ENABLED_STATE: u32 = 0x1000_001B;
/// The selected row's name colour.
pub const SELECTED_NAME_COLOR: Color = Color::WHITE;

/// A skill's group on the page.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Bucket {
    /// Specialized.
    Specialized,
    /// Trained.
    Trained,
    /// Usable untrained.
    UseableUntrained,
    /// Not usable untrained.
    UnuseableUntrained,
}

/// The groups in page order with their header strings.
pub const BUCKET_ORDER: [(Bucket, &str); 4] = [
    (Bucket::Specialized, "ID_CharGen_Specialized"),
    (Bucket::Trained, "ID_CharGen_Trained"),
    (Bucket::UseableUntrained, "ID_CharGen_UseableUntrained"),
    (Bucket::UnuseableUntrained, "ID_CharGen_UnuseableUntrained"),
];

/// A skill's group: its training, or for an untrained skill whether its
/// minimum level lets it be used untrained.
#[must_use]
pub fn bucket(level: Class, min_level: u32) -> Bucket {
    match level {
        Class::Specialized => Bucket::Specialized,
        Class::Trained => Bucket::Trained,
        _ if min_level <= 1 => Bucket::UseableUntrained,
        _ => Bucket::UnuseableUntrained,
    }
}

/// A cost the raise column shows: blank at 999 and over (not trainable).
#[must_use]
pub fn gated_cost(cost: i32) -> String {
    if cost < 999 {
        cost.to_string()
    } else {
        String::new()
    }
}

/// The formula line: "Formula : (2 x Strength) + Quickness / 3 +5".
#[must_use]
pub fn formula_text(formula: &SkillFormula) -> String {
    let first = formula.attribute1_multiplier >= 1 && formula.attribute1 != 0;
    let second = formula.attribute2_multiplier >= 1 && formula.attribute2 != 0;
    let term = |multiplier: i32, attribute: u32| {
        let name = attribute_name(attribute);
        if multiplier > 1 {
            format!("({multiplier} x {name})")
        } else {
            name.to_owned()
        }
    };
    let mut text = String::from("Formula : ");
    if first {
        text.push_str(&term(formula.attribute1_multiplier, formula.attribute1));
        if second {
            text.push_str(" + ");
        }
    }
    if second {
        text.push_str(&term(formula.attribute2_multiplier, formula.attribute2));
    }
    if formula.divisor != 1 {
        text.push_str(&format!(" / {}", formula.divisor));
    }
    if formula.additive_bonus != 0 {
        text.push_str(&format!(" +{}", formula.additive_bonus));
    }
    text
}

fn attribute_name(id: u32) -> &'static str {
    match id {
        1 => "Strength",
        2 => "Endurance",
        3 => "Quickness",
        4 => "Coordination",
        5 => "Focus",
        6 => "Self",
        _ => "",
    }
}

/// The skill's train and specialize costs: the heritage's, else the
/// skill table's, else none.
#[must_use]
pub fn costs(options: &ChargenOptions, heritage: u32, skill: u32) -> (i32, i32) {
    if let Some(cost) = options
        .try_get_heritage(heritage)
        .and_then(|h| h.skill_costs_by_skill_id.get(&skill))
    {
        return (cost.normal_cost, cost.primary_cost);
    }
    options
        .global_skill_costs_by_skill_id
        .get(&skill)
        .map_or((0, 0), |c| (c.normal_cost, c.primary_cost))
}

fn costable(heritage: &ChargenHeritageOptions, options: &ChargenOptions, skill: u32) -> bool {
    heritage.skill_costs_by_skill_id.contains_key(&skill)
        || options.global_skill_costs_by_skill_id.contains_key(&skill)
}

/// What a row shows: score, raise and lower costs, and whether each
/// arrow can be used.
#[must_use]
pub fn row_values(
    level: Class,
    trained_cost: i32,
    specialized_cost: i32,
    credits_left: i32,
) -> (String, String, bool, bool) {
    match level {
        Class::Specialized => (
            "0".to_owned(),
            (specialized_cost - trained_cost).to_string(),
            false,
            specialized_cost != 0,
        ),
        Class::Trained => (
            gated_cost(specialized_cost - trained_cost),
            trained_cost.to_string(),
            credits_left >= specialized_cost - trained_cost,
            trained_cost != 0,
        ),
        _ => (
            gated_cost(trained_cost),
            "0".to_owned(),
            credits_left >= trained_cost,
            false,
        ),
    }
}

/// The command a raise arrow sends for a skill at `level`.
#[must_use]
pub fn advance(skill: u32, level: Class) -> Option<CreationCommand> {
    match level {
        Class::Inactive | Class::Untrained => Some(CreationCommand::TrainSkill(skill)),
        Class::Trained => Some(CreationCommand::SpecializeSkill(skill)),
        Class::Specialized => None,
    }
}

/// The command a lower arrow sends for a skill at `level`.
#[must_use]
pub fn retreat(skill: u32, level: Class) -> Option<CreationCommand> {
    match level {
        Class::Specialized => Some(CreationCommand::TrainSkill(skill)),
        Class::Trained => Some(CreationCommand::UntrainSkill(skill)),
        _ => None,
    }
}

#[derive(Debug, Clone)]
struct Row {
    root: ElementId,
    skill: u32,
    bucket: Bucket,
    name: Option<ElementId>,
    level: Option<ElementId>,
    up_cost: Option<ElementId>,
    down_cost: Option<ElementId>,
    up: Option<ElementId>,
    down: Option<ElementId>,
    unselected_color: Color,
}

fn level_of(ctx: &PageContext<'_>, skill: u32) -> Class {
    ctx.snapshot
        .skills
        .get(skill as usize)
        .copied()
        .unwrap_or(Class::Inactive)
}

fn min_level(options: &ChargenOptions, skill: u32) -> u32 {
    // A skill the skill table does not describe counts as usable.
    options
        .try_get_skill_detail(skill)
        .map_or(1, |d| d.min_level)
}

/// The skills page. See the module documentation.
#[derive(Debug, Clone)]
pub struct SkillsPage {
    list: Option<ElementId>,
    credits: Option<ElementId>,
    info_title: Option<ElementId>,
    info_text: Option<ElementId>,
    rows: Vec<Row>,
    built_for: Option<u32>,
    selected: Option<u32>,
}

impl SkillsPage {
    /// Bind the page under `root`.
    pub fn bind(ui: &mut UiRoot, root: ElementId) -> Self {
        let list = ui
            .find_descendant(root, LIST_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some());
        if let Some(l) = list {
            crate::panels::link_list_scrollbar(ui, root, l);
        }
        let info_text = ui
            .find_descendant(root, INFO_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
        // The description pane ends where the info frame ends.
        if let Some(t) = info_text
            && let Some(frame) = ui.find_descendant(root, INFO_FRAME_ID)
        {
            let frame_bottom = ui.element(frame).map_or(0.0, |e| e.top + e.height);
            if let Some(e) = ui.element_mut(t)
                && frame_bottom < e.top + e.height
            {
                e.height = frame_bottom - e.top;
            }
        }
        Self {
            list,
            credits: ui.find_descendant(root, CREDITS_ID),
            info_title: ui
                .find_descendant(root, INFO_TITLE_ID)
                .filter(|t| ui.widget::<Text>(*t).is_some()),
            info_text,
            rows: Vec::new(),
            built_for: None,
            selected: None,
        }
    }

    /// The skill rows, top first: (row, skill id).
    #[must_use]
    pub fn rows(&self) -> Vec<(ElementId, u32)> {
        self.rows.iter().map(|r| (r.root, r.skill)).collect()
    }

    /// A skill row's arrows: (raise, lower).
    #[must_use]
    pub fn arrows(&self, skill: u32) -> Option<(Option<ElementId>, Option<ElementId>)> {
        self.rows
            .iter()
            .find(|r| r.skill == skill)
            .map(|r| (r.up, r.down))
    }

    /// The selected skill.
    #[must_use]
    pub fn selected(&self) -> Option<u32> {
        self.selected
    }

    /// The info box's title and text.
    #[must_use]
    pub fn info(&self) -> (Option<ElementId>, Option<ElementId>) {
        (self.info_title, self.info_text)
    }

    /// Show the snapshot: the rows rebuilt when the heritage or a skill's
    /// group changed (the selection kept unless the heritage changed),
    /// then every row's numbers, the info box and the credits.
    pub fn refresh(
        &mut self,
        ui: &mut UiRoot,
        ctx: &PageContext<'_>,
        templates: &mut dyn RowTemplates,
    ) {
        let heritage = ctx.snapshot.heritage_id;
        let heritage_changed = self.built_for != Some(heritage);
        let buckets_changed = !heritage_changed
            && self.rows.iter().any(|r| {
                bucket(level_of(ctx, r.skill), min_level(ctx.options, r.skill)) != r.bucket
            });
        if heritage_changed || buckets_changed {
            let kept = if heritage_changed {
                None
            } else {
                self.selected
            };
            self.rebuild(ui, ctx, templates);
            self.built_for = Some(heritage);
            if let Some(skill) = kept.filter(|s| self.rows.iter().any(|r| r.skill == *s)) {
                self.selected = Some(skill);
                self.apply_selection(ui);
            }
        }
        for row in &self.rows {
            Self::refresh_row(ui, row, ctx);
        }
        self.refresh_info(ui, ctx);
        if let Some(b) = self.credits.and_then(|c| ui.widget_mut::<Button>(c)) {
            b.value_label = Some(ctx.snapshot.remaining_skill_credits.to_string());
        }
    }

    fn rebuild(
        &mut self,
        ui: &mut UiRoot,
        ctx: &PageContext<'_>,
        templates: &mut dyn RowTemplates,
    ) {
        self.rows.clear();
        self.selected = None;
        self.clear_info(ui);
        let Some(list) = self.list else {
            return;
        };
        let entries = ui
            .widget::<ListBox>(list)
            .map(|l| l.templates.clone())
            .unwrap_or_default();
        ui.with::<ListBox, _>(list, |l, ui| l.flush(ui));
        let Some(heritage) = ctx.options.try_get_heritage(ctx.snapshot.heritage_id) else {
            return;
        };
        if entries.len() < 2 {
            return;
        }
        let mut by_bucket: HashMap<Bucket, Vec<(u32, String)>> = HashMap::new();
        for skill in 1..ChargenSkillAdvancementSet::SLOT_COUNT as u32 {
            if !costable(heritage, ctx.options, skill) {
                continue;
            }
            let b = bucket(level_of(ctx, skill), min_level(ctx.options, skill));
            by_bucket
                .entry(b)
                .or_default()
                .push((skill, chargen_skill_name(ctx.options, skill)));
        }
        for skills in by_bucket.values_mut() {
            skills.sort_by(|a, b| a.1.cmp(&b.1));
        }
        let (header, row) = (&entries[0], &entries[1]);
        for (b, key) in BUCKET_ORDER {
            if let Some(h) = templates.build_row(ui, header.layout, header.element) {
                ui.with::<ListBox, _>(list, |l, ui| l.add_prebuilt_row(ui, list, h));
                if let Some(text) = (ctx.text)(key)
                    && let Some(caption) = ui
                        .find_descendant(h, HEADER_CAPTION_ID)
                        .and_then(|c| ui.widget_mut::<Button>(c))
                {
                    caption.label = Some(text);
                }
            }
            for (skill, name) in by_bucket.remove(&b).unwrap_or_default() {
                let Some(r) = templates.build_row(ui, row.layout, row.element) else {
                    continue;
                };
                ui.with::<ListBox, _>(list, |l, ui| l.add_prebuilt_row(ui, list, r));
                self.rows.push(Self::bind_row(ui, r, skill, &name, b));
            }
        }
    }

    fn bind_row(ui: &mut UiRoot, root: ElementId, skill: u32, name: &str, bucket: Bucket) -> Row {
        let text = |ui: &UiRoot, id| {
            ui.find_descendant(root, id)
                .filter(|t| ui.widget::<Text>(*t).is_some())
        };
        let arrow = |ui: &mut UiRoot, id| {
            let b = ui
                .find_descendant(root, id)
                .filter(|b| ui.widget::<Button>(*b).is_some())?;
            if let Some(w) = ui.widget_mut::<Button>(b) {
                w.listeners.click = true;
            }
            Some(b)
        };
        let name_text = text(ui, ROW_NAME_ID);
        if let Some(n) = name_text {
            rich_text::set_line(ui, n, name);
        }
        let unselected_color = name_text
            .and_then(|n| ui.widget::<Text>(n))
            .map_or(Color::WHITE, |t| t.default_color);
        if let Some(d) = ui.widget_mut::<DatElement>(root) {
            d.emits_clicks = true;
        }
        if let Some(e) = ui.element_mut(root) {
            e.click_through = false;
        }
        Row {
            root,
            skill,
            bucket,
            name: name_text,
            level: text(ui, ROW_LEVEL_ID),
            up_cost: text(ui, ROW_UP_COST_ID),
            down_cost: text(ui, ROW_DOWN_COST_ID),
            up: arrow(ui, ROW_UP_ID),
            down: arrow(ui, ROW_DOWN_ID),
            unselected_color,
        }
    }

    fn refresh_row(ui: &mut UiRoot, row: &Row, ctx: &PageContext<'_>) {
        let level = level_of(ctx, row.skill);
        let (trained, specialized) = costs(ctx.options, ctx.snapshot.heritage_id, row.skill);
        let score = chargen_skill_score(ctx.options, row.skill, &ctx.snapshot.attributes, level);
        if let Some(t) = row.level {
            rich_text::set_line(ui, t, &score.to_string());
        }
        let (up, down, up_enabled, down_enabled) = row_values(
            level,
            trained,
            specialized,
            ctx.snapshot.remaining_skill_credits,
        );
        if let Some(t) = row.up_cost {
            rich_text::set_line(ui, t, &up);
        }
        if let Some(t) = row.down_cost {
            rich_text::set_line(ui, t, &down);
        }
        let state = |enabled| {
            if enabled {
                ARROW_ENABLED_STATE
            } else {
                ARROW_GHOSTED_STATE
            }
        };
        if let Some(b) = row.up {
            ui.set_retail_state(b, state(up_enabled));
        }
        if let Some(b) = row.down {
            ui.set_retail_state(b, state(down_enabled));
        }
    }

    fn apply_selection(&self, ui: &mut UiRoot) {
        for row in &self.rows {
            if let Some(t) = row.name.and_then(|n| ui.widget_mut::<Text>(n)) {
                let color = if Some(row.skill) == self.selected {
                    SELECTED_NAME_COLOR
                } else {
                    row.unselected_color
                };
                t.default_color = color;
                let lines: Vec<Line> = t
                    .content_lines()
                    .into_iter()
                    .map(|l| Line::new(l.text, color))
                    .collect();
                t.set_lines(lines);
            }
        }
    }

    fn refresh_info(&self, ui: &mut UiRoot, ctx: &PageContext<'_>) {
        let Some(skill) = self.selected else {
            self.clear_info(ui);
            return;
        };
        let level = level_of(ctx, skill);
        let score = chargen_skill_score(ctx.options, skill, &ctx.snapshot.attributes, level);
        let name = chargen_skill_name(ctx.options, skill);
        if let Some(t) = self.info_title {
            rich_text::set_line(ui, t, &format!("{name} ({score})"));
        }
        let Some(t) = self.info_text else {
            return;
        };
        let color = ui
            .widget::<Text>(t)
            .map_or(Color::WHITE, |w| w.default_color);
        let bonus = match level {
            Class::Trained => "Training Bonus  +5",
            Class::Specialized => "Specialization Bonus  +10",
            _ => "",
        };
        let detail = ctx.options.try_get_skill_detail(skill);
        let mut lines = Vec::new();
        if let Some(d) = detail.filter(|d| !d.description.is_empty()) {
            lines.extend(rich_text::compose(ui, t, &[(Some(&d.description), color)]));
        }
        if !bonus.is_empty() {
            lines.push(Line::new(bonus, color));
        }
        if let Some(d) = detail {
            lines.push(Line::new(formula_text(&d.formula), color));
        }
        rich_text::set_lines(ui, t, lines);
    }

    fn clear_info(&self, ui: &mut UiRoot) {
        for t in [self.info_title, self.info_text].into_iter().flatten() {
            rich_text::set_line(ui, t, "");
        }
    }

    /// What a message on the page asks; `None` when it is not the page's.
    pub fn on_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
        ctx: &PageContext<'_>,
    ) -> Option<Vec<CreationCommand>> {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return None;
        }
        let row = self
            .rows
            .iter()
            .find(|r| r.root == element || r.up == Some(element) || r.down == Some(element))?;
        let skill = row.skill;
        let level = level_of(ctx, skill);
        let command = if row.up == Some(element) {
            advance(skill, level)
        } else if row.down == Some(element) {
            retreat(skill, level)
        } else {
            None
        };
        self.selected = Some(skill);
        self.apply_selection(ui);
        self.refresh_info(ui, ctx);
        Some(command.into_iter().collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_untrained_skill_groups_by_its_minimum_level() {
        // OpenAC `CharacterCreationUiControllerTests.
        // SkillsPage_UntrainedSkill_BucketsByMinLevel`.
        assert_eq!(bucket(Class::Untrained, 1), Bucket::UseableUntrained);
        assert_eq!(bucket(Class::Untrained, 0), Bucket::UseableUntrained);
        assert_eq!(bucket(Class::Inactive, 2), Bucket::UnuseableUntrained);
        assert_eq!(bucket(Class::Trained, 2), Bucket::Trained);
        assert_eq!(bucket(Class::Specialized, 9), Bucket::Specialized);
    }

    #[test]
    fn a_specialized_row_raises_for_nothing_and_lowers_for_the_difference() {
        // OpenAC `CharacterCreationUiControllerTests.
        // SkillsPage_SpecializedCostText_UpCostIsLiteralZero_DownCostUnconditional`
        // and `SkillsPage_ArrowStates_GatedOnCreditsAndFreeSkillLocksDownArrow`.
        assert_eq!(
            row_values(Class::Specialized, 4, 12, 0),
            ("0".into(), "8".into(), false, true)
        );
        assert_eq!(
            row_values(Class::Trained, 4, 12, 7),
            ("8".into(), "4".into(), false, true)
        );
        assert_eq!(
            row_values(Class::Trained, 0, 4, 4),
            ("4".into(), "0".into(), true, false)
        );
        assert_eq!(
            row_values(Class::Untrained, 6, 12, 6),
            ("6".into(), "0".into(), true, false)
        );
        assert_eq!(
            row_values(Class::Untrained, 999, 999, 50),
            (String::new(), "0".into(), false, false)
        );
    }

    #[test]
    fn the_arrows_step_one_training_level() {
        // OpenAC `CharacterCreationSkillsPage.Advance` / `Retreat`.
        assert_eq!(
            advance(6, Class::Untrained),
            Some(CreationCommand::TrainSkill(6))
        );
        assert_eq!(
            advance(6, Class::Inactive),
            Some(CreationCommand::TrainSkill(6))
        );
        assert_eq!(
            advance(6, Class::Trained),
            Some(CreationCommand::SpecializeSkill(6))
        );
        assert_eq!(advance(6, Class::Specialized), None);
        assert_eq!(
            retreat(6, Class::Specialized),
            Some(CreationCommand::TrainSkill(6))
        );
        assert_eq!(
            retreat(6, Class::Trained),
            Some(CreationCommand::UntrainSkill(6))
        );
        assert_eq!(retreat(6, Class::Untrained), None);
    }

    #[test]
    fn the_formula_names_its_terms() {
        // OpenAC `CharacterCreationSkillsPage.ComposeFormula`.
        let f = SkillFormula {
            additive_bonus: 0,
            attribute1_multiplier: 1,
            attribute2_multiplier: 1,
            divisor: 3,
            attribute1: 4,
            attribute2: 3,
        };
        assert_eq!(formula_text(&f), "Formula : Coordination + Quickness / 3");
        let f = SkillFormula {
            additive_bonus: 5,
            attribute1_multiplier: 2,
            attribute2_multiplier: 0,
            divisor: 1,
            attribute1: 1,
            attribute2: 0,
        };
        assert_eq!(formula_text(&f), "Formula : (2 x Strength) +5");
    }
}
