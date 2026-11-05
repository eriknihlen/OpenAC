//! The indicator bar and the panels it opens: the seven indicator buttons
//! (link, helpful and harmful effects, vitae, burden, mini-game, end of
//! session), the positive and negative effects panels (the active
//! enchantments with their time left and description), the link-status
//! panel and the vitae panel.
//!
//! Every controller shows what the client hands it each update: the
//! player's spellbook (enchantments), the item table, the player's
//! strength, the link as the session reports it and the clocks. Clicks
//! become panel outputs (open a panel, ask to end the session).

use ac_core::items::ClientObjectTable;
use ac_core::items::burden::{encumbrance_capacity, load_ratio};
use ac_core::spells::{ActiveEnchantmentRecord, Spellbook};
use ac_runtime::session::{LifecycleCommand, LinkStatus, SessionCommand};

use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::icons::IconSource;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::transcript::wrap_text;
use crate::panels::{PanelOutput, RowTemplates, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::states;
use crate::text::Line;
use crate::tree::{Element, ElementId};
use crate::widgets::{Button, Image, ItemList, ScrollSource, Scrollbar, TemplateSlot, Text};

// ── Shared: detail text ─────────────────────────────────────────────────

/// Lines of `text` for the detail text `target`: each paragraph wrapped to
/// the element's width less its padding, an empty paragraph an empty line,
/// all in the element's default colour (OpenAC `IndicatorDetailText.Shape`).
#[must_use]
pub fn shape_detail_text(ui: &UiRoot, target: ElementId, text: &str) -> Vec<Line> {
    let Some(t) = ui.widget::<Text>(target) else {
        return Vec::new();
    };
    let width = ui.element(target).map_or(0.0, |e| e.width);
    let max_width = (width - 2.0 * t.padding).max(1.0);
    let color = t.default_color;
    let font = t.font.clone();
    let measure = |s: &str| match font.as_deref() {
        Some(f) => f.measure_width(s),
        None => s.chars().count() as f32 * 8.0,
    };
    let mut lines = Vec::new();
    for paragraph in text.split('\n') {
        if paragraph.is_empty() {
            lines.push(Line::new("", color));
            continue;
        }
        for line in wrap_text(paragraph, max_width, &measure) {
            lines.push(Line::new(line, color));
        }
    }
    lines
}

fn set_lines(ui: &mut UiRoot, id: ElementId, lines: Vec<Line>) {
    if let Some(t) = ui.widget_mut::<Text>(id)
        && t.content_lines() != lines
    {
        t.set_lines(lines);
    }
}

fn find_button(ui: &UiRoot, layout: &ImportedLayout, id: u32) -> Option<ElementId> {
    layout
        .find(id)
        .filter(|b| ui.widget::<Button>(*b).is_some())
}

/// Enter `state` unless it was the last one entered (a ghosted button's
/// look is not its requested state, so the request is remembered).
fn ensure_state(ui: &mut UiRoot, applied: &mut Vec<(ElementId, u32)>, id: ElementId, state: u32) {
    match applied.iter_mut().find(|(e, _)| *e == id) {
        Some((_, s)) if *s == state => return,
        Some((_, s)) => *s = state,
        None => applied.push((id, state)),
    }
    ui.set_retail_state(id, state);
}

// ── The indicator bar ───────────────────────────────────────────────────

/// The indicator bar's layout.
pub const BAR_LAYOUT_ID: u32 = 0x2100_0071;
/// The mini-game indicator.
pub const MINI_GAME_BUTTON_ID: u32 = 0x1000_00F3;
/// The vitae indicator.
pub const VITAE_BUTTON_ID: u32 = 0x1000_00F4;
/// The helpful-effects indicator.
pub const HELPFUL_BUTTON_ID: u32 = 0x1000_00F5;
/// The harmful-effects indicator.
pub const HARMFUL_BUTTON_ID: u32 = 0x1000_00F6;
/// The burden indicator.
pub const BURDEN_BUTTON_ID: u32 = 0x1000_00F7;
/// The link indicator.
pub const LINK_BUTTON_ID: u32 = 0x1000_00F8;
/// The end-of-session button.
pub const END_CHARACTER_SESSION_BUTTON_ID: u32 = 0x1000_00FA;
/// Each indicator's press highlight child.
pub const PRESS_HIGHLIGHT_ID: u32 = 0x1000_00F2;

/// Burden under the capacity.
pub const UNENCUMBERED_STATE: u32 = 14;
/// Burden up to twice the capacity.
pub const ENCUMBERED_STATE: u32 = 15;
/// Burden beyond.
pub const HEAVILY_ENCUMBERED_STATE: u32 = 16;
/// A packet in the last 5 seconds.
pub const CONNECTION_GOOD_STATE: u32 = 17;
/// No packet for 5 seconds.
pub const CONNECTION_UNCERTAIN_STATE: u32 = 18;
/// No packet for 20 seconds (flashes with the uncertain art).
pub const CONNECTION_BAD_STATE: u32 = 19;
/// No packet for 40 seconds, or no connection.
pub const CONNECTION_DISCONNECTED_STATE: u32 = 20;

/// The player's burden (property int `EncumbranceVal`).
const ENCUMBRANCE_VAL_PROPERTY: u32 = 5;
/// The player's carrying augmentation (property int 0xE6).
const ENCUMBRANCE_AUG_PROPERTY: u32 = 0xE6;
/// How often the link indicator reads the link, seconds.
pub const LINK_UPDATE_SECONDS: f64 = 4.0;
/// How often a bad link flashes, seconds.
pub const LINK_FLASH_SECONDS: f64 = 0.75;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum LinkSemantic {
    Good,
    Uncertain,
    Bad,
    Disconnected,
}

fn link_semantic(link: &LinkStatus) -> LinkSemantic {
    if !link.connected || link.seconds_since_last_packet >= 40.0 {
        LinkSemantic::Disconnected
    } else if link.seconds_since_last_packet >= 20.0 {
        LinkSemantic::Bad
    } else if link.seconds_since_last_packet >= 5.0 {
        LinkSemantic::Uncertain
    } else {
        LinkSemantic::Good
    }
}

/// The link indicator's art for `link`.
#[must_use]
pub fn link_state(link: &LinkStatus) -> u32 {
    match link_semantic(link) {
        LinkSemantic::Good => CONNECTION_GOOD_STATE,
        LinkSemantic::Uncertain => CONNECTION_UNCERTAIN_STATE,
        LinkSemantic::Bad => CONNECTION_BAD_STATE,
        LinkSemantic::Disconnected => CONNECTION_DISCONNECTED_STATE,
    }
}

/// The burden indicator's art for a load (burden over capacity).
#[must_use]
pub fn burden_state(load: f32) -> u32 {
    if load < 1.0 {
        UNENCUMBERED_STATE
    } else if load < 2.0 {
        ENCUMBERED_STATE
    } else {
        HEAVILY_ENCUMBERED_STATE
    }
}

/// What the indicators read, borrowed for one update.
#[derive(Debug, Clone, Copy)]
pub struct IndicatorsView<'a> {
    /// The player's spellbook (its enchantments).
    pub spellbook: Option<&'a Spellbook>,
    /// The item table.
    pub objects: &'a ClientObjectTable,
    /// The player's object id.
    pub player: u32,
    /// The player's current strength.
    pub strength: Option<i32>,
    /// The link as the session last reported it.
    pub link: LinkStatus,
    /// The client clock, seconds.
    pub now: f64,
}

fn has_matching_effect(book: &Spellbook, beneficial: bool) -> bool {
    book.active_enchantments().iter().any(|r| {
        matches!(r.bucket, 1 | 2)
            && book
                .try_get_metadata(r.spell_id)
                .is_some_and(|m| m.is_beneficial() == beneficial)
    })
}

fn has_vitae(book: &Spellbook) -> bool {
    book.active_enchantments()
        .iter()
        .any(|r| r.bucket == 4 && r.stat_mod_value.is_some_and(|v| v < 1.0))
}

/// The indicator bar's controller. See the module documentation.
#[derive(Debug)]
pub struct IndicatorBarController {
    link: ElementId,
    helpful: ElementId,
    harmful: ElementId,
    vitae: ElementId,
    burden: ElementId,
    mini_game: ElementId,
    end_session: ElementId,
    end_session_message: String,
    link_semantic: LinkSemantic,
    last_link_update: f64,
    last_link_flash: f64,
    applied: Vec<(ElementId, u32)>,
}

impl IndicatorBarController {
    /// Bind to the imported bar (`None` when any of the seven buttons is
    /// missing); `end_session_message` is the question asked before the
    /// session ends; `now` starts the link timers.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        end_session_message: String,
        now: f64,
    ) -> Option<Self> {
        let c = Self {
            link: find_button(ui, layout, LINK_BUTTON_ID)?,
            helpful: find_button(ui, layout, HELPFUL_BUTTON_ID)?,
            harmful: find_button(ui, layout, HARMFUL_BUTTON_ID)?,
            vitae: find_button(ui, layout, VITAE_BUTTON_ID)?,
            burden: find_button(ui, layout, BURDEN_BUTTON_ID)?,
            mini_game: find_button(ui, layout, MINI_GAME_BUTTON_ID)?,
            end_session: find_button(ui, layout, END_CHARACTER_SESSION_BUTTON_ID)?,
            end_session_message,
            link_semantic: LinkSemantic::Good,
            last_link_update: now,
            last_link_flash: now,
            applied: Vec::new(),
        };
        for b in c.buttons() {
            bind_click(ui, b);
        }
        ui.set_retail_state(c.link, CONNECTION_GOOD_STATE);
        ui.set_retail_state(c.mini_game, states::GHOSTED);
        ui.set_retail_state(c.end_session, states::NORMAL);
        Some(c)
    }

    fn buttons(&self) -> [ElementId; 7] {
        [
            self.link,
            self.helpful,
            self.harmful,
            self.vitae,
            self.burden,
            self.mini_game,
            self.end_session,
        ]
    }

    /// Build each indicator's authored press highlight (child 0x100000F2
    /// of its description in `layout`, which the button does not import)
    /// under the button.
    pub fn attach_press_highlights(
        &self,
        ui: &mut UiRoot,
        layout: &crate::element_info::ElementInfo,
        ctx: &crate::import::BuildContext<'_>,
    ) {
        fn find(
            info: &crate::element_info::ElementInfo,
            id: u32,
        ) -> Option<&crate::element_info::ElementInfo> {
            if info.id == id {
                return Some(info);
            }
            info.children.iter().find_map(|c| find(c, id))
        }
        for (button, id) in [
            (self.link, LINK_BUTTON_ID),
            (self.helpful, HELPFUL_BUTTON_ID),
            (self.harmful, HARMFUL_BUTTON_ID),
            (self.vitae, VITAE_BUTTON_ID),
            (self.burden, BURDEN_BUTTON_ID),
            (self.mini_game, MINI_GAME_BUTTON_ID),
            (self.end_session, END_CHARACTER_SESSION_BUTTON_ID),
        ] {
            let Some(info) = find(layout, id) else {
                continue;
            };
            for child in info.children.iter().filter(|c| c.id == PRESS_HIGHLIGHT_ID) {
                let built = crate::import::build(ui, child, ctx);
                ui.add_child(button, built.root);
            }
        }
    }

    /// The button of indicator `id`.
    #[must_use]
    pub fn button(&self, id: u32) -> Option<ElementId> {
        match id {
            LINK_BUTTON_ID => Some(self.link),
            HELPFUL_BUTTON_ID => Some(self.helpful),
            HARMFUL_BUTTON_ID => Some(self.harmful),
            VITAE_BUTTON_ID => Some(self.vitae),
            BURDEN_BUTTON_ID => Some(self.burden),
            MINI_GAME_BUTTON_ID => Some(self.mini_game),
            END_CHARACTER_SESSION_BUTTON_ID => Some(self.end_session),
            _ => None,
        }
    }

    /// Show the enchantments, the burden and the link as `view` has them.
    pub fn update(&mut self, ui: &mut UiRoot, view: &IndicatorsView<'_>) {
        let (helpful, harmful, vitae) = view.spellbook.map_or((false, false, false), |b| {
            (
                has_matching_effect(b, true),
                has_matching_effect(b, false),
                has_vitae(b),
            )
        });
        let lit = |on: bool| if on { states::NORMAL } else { states::GHOSTED };
        ensure_state(ui, &mut self.applied, self.helpful, lit(helpful));
        ensure_state(ui, &mut self.applied, self.harmful, lit(harmful));
        ensure_state(ui, &mut self.applied, self.vitae, lit(vitae));
        self.update_burden(ui, view);
        self.tick_link(ui, &view.link, view.now);
    }

    fn update_burden(&mut self, ui: &mut UiRoot, view: &IndicatorsView<'_>) {
        let player = view.objects.get(view.player);
        let strength = view.strength.unwrap_or(10);
        let augmentation = player.map_or(0, |p| p.properties.get_int(ENCUMBRANCE_AUG_PROPERTY, 0));
        let capacity = encumbrance_capacity(strength, augmentation);
        let burden = player
            .and_then(|p| p.properties.ints.get(&ENCUMBRANCE_VAL_PROPERTY).copied())
            .unwrap_or_else(|| view.objects.sum_carried_burden(view.player));
        let state = burden_state(load_ratio(capacity, burden));
        ensure_state(ui, &mut self.applied, self.burden, state);
    }

    fn tick_link(&mut self, ui: &mut UiRoot, link: &LinkStatus, now: f64) {
        if now - self.last_link_update >= LINK_UPDATE_SECONDS {
            let semantic = link_semantic(link);
            if semantic != self.link_semantic {
                self.link_semantic = semantic;
                ui.set_retail_state(self.link, link_state(link));
                if semantic == LinkSemantic::Bad {
                    self.last_link_flash = now;
                }
            }
            self.last_link_update = now;
        }
        if self.link_semantic == LinkSemantic::Bad
            && now - self.last_link_flash >= LINK_FLASH_SECONDS
        {
            let next = if ui.retail_state(self.link) == Some(CONNECTION_UNCERTAIN_STATE) {
                CONNECTION_BAD_STATE
            } else {
                CONNECTION_UNCERTAIN_STATE
            };
            ui.set_retail_state(self.link, next);
            self.last_link_flash = now;
        }
    }

    /// Light or ghost the mini-game indicator.
    pub fn set_mini_game_active(&self, ui: &mut UiRoot, active: bool) {
        ui.set_retail_state(
            self.mini_game,
            if active {
                states::NORMAL
            } else {
                states::GHOSTED
            },
        );
    }
}

impl PanelController for IndicatorBarController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return false;
        }
        let panel = if element == self.helpful {
            panel_id::POSITIVE_EFFECTS
        } else if element == self.harmful {
            panel_id::NEGATIVE_EFFECTS
        } else if element == self.link {
            panel_id::LINK_STATUS
        } else if element == self.vitae {
            panel_id::VITAE
        } else if element == self.burden {
            panel_id::CHARACTER_INFORMATION
        } else if element == self.mini_game {
            panel_id::MINI_GAME
        } else if element == self.end_session {
            emit(
                ui,
                PanelOutput::Confirm {
                    message: self.end_session_message.clone(),
                    on_yes: Box::new(PanelOutput::Session(SessionCommand::Lifecycle(
                        LifecycleCommand::EndCharacterSession,
                    ))),
                },
            );
            return true;
        } else {
            return false;
        };
        emit(ui, PanelOutput::TogglePanel(panel));
        true
    }
}

// ── The effects panels ──────────────────────────────────────────────────

/// The effects panels' layout.
pub const EFFECTS_LAYOUT_ID: u32 = 0x2100_001B;
/// The positive effects panel's root.
pub const POSITIVE_ROOT_ID: u32 = 0x1000_011F;
/// The negative effects panel's root.
pub const NEGATIVE_ROOT_ID: u32 = 0x1000_0121;
/// The detail panels' close button.
pub const CLOSE_ID: u32 = 0x1000_00FC;
/// The effect list.
pub const LIST_ID: u32 = 0x1000_0123;
/// The effect list's scrollbar.
pub const LIST_SCROLLBAR_ID: u32 = 0x1000_0124;
/// The description text.
pub const INFO_TEXT_ID: u32 = 0x1000_0126;
/// The description's scrollbar.
pub const INFO_SCROLLBAR_ID: u32 = 0x1000_0127;
/// The row template.
pub const ROW_TEMPLATE_ID: u32 = 0x1000_0128;
/// A row's icon.
pub const ROW_ICON_ID: u32 = 0x1000_0129;
/// A row's spell name.
pub const ROW_LABEL_ID: u32 = 0x1000_012A;
/// A row's time left.
pub const ROW_DURATION_ID: u32 = 0x1000_012B;

/// The time left of `enchantment` at `now` as the effects rows show it:
/// "h:mm:ss" from an hour up, else "m:ss"; "" for a permanent one; "--:--"
/// when it cannot be computed.
#[must_use]
pub fn format_remaining(enchantment: &ActiveEnchantmentRecord, now: f64) -> String {
    if enchantment.duration < 0.0 {
        return String::new();
    }
    let remaining = (enchantment.start_time + enchantment.duration - now).max(0.0);
    if !remaining.is_finite() {
        return "--:--".to_owned();
    }
    // Whole seconds, as a time span's parts are.
    let total = remaining.min(f64::from(i32::MAX)) as i64;
    let (hours, minutes, seconds) = (total / 3600, (total / 60) % 60, total % 60);
    if hours >= 1 {
        format!("{hours}:{minutes:02}:{seconds:02}")
    } else {
        format!("{minutes}:{seconds:02}")
    }
}

/// What an effects panel reads, borrowed for one update.
#[derive(Debug, Clone, Copy)]
pub struct EffectsView<'a> {
    /// The player's spellbook.
    pub spellbook: &'a Spellbook,
    /// The enchantments' clock now (the session's), seconds.
    pub now: f64,
    /// The "show spell durations" character option.
    pub show_duration: bool,
}

/// The authored row template.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EffectRowTemplate {
    /// Its layout.
    pub layout: u32,
    /// Its element.
    pub element: u32,
    /// Its width (the list's cell width).
    pub width: f32,
    /// Its height (the list's cell height).
    pub height: f32,
}

#[derive(Debug, Clone)]
struct EffectRow {
    cell: ElementId,
    spell_id: u32,
    identity: u32,
    duration: Option<ElementId>,
}

/// The visible enchantments of one side: in effect, helpful (`positive`)
/// or harmful by their spell, by name ignoring case.
fn visible_enchantments(book: &Spellbook, positive: bool) -> Vec<ActiveEnchantmentRecord> {
    let mut list: Vec<ActiveEnchantmentRecord> = book
        .enchantments_in_effect_snapshot()
        .into_iter()
        .filter(|r| {
            book.try_get_metadata(r.spell_id)
                .is_some_and(|m| m.is_beneficial() == positive)
        })
        .collect();
    let key = |r: &ActiveEnchantmentRecord| {
        book.try_get_metadata(r.spell_id)
            .map_or_else(|| r.spell_id.to_string(), |m| m.name.clone())
            .to_lowercase()
    };
    list.sort_by_cached_key(key);
    list
}

/// An effects panel's controller. See the module documentation.
#[derive(Debug)]
pub struct EffectsController {
    positive: bool,
    list: ElementId,
    info: Option<ElementId>,
    close: Option<ElementId>,
    template: EffectRowTemplate,
    select_prompt: String,
    rows: Vec<EffectRow>,
    selected: Option<u32>,
    rendered_revision: Option<u64>,
    last_duration_update: Option<f64>,
    info_value: Option<String>,
}

impl EffectsController {
    /// Bind to the imported panel: an item list filling the authored list
    /// host (one column of template-sized cells), its scrollbar and the
    /// description's, the close button. `None` without the list host.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        positive: bool,
        template: EffectRowTemplate,
        select_prompt: String,
    ) -> Option<Self> {
        let host = layout.find(LIST_ID)?;
        let list = if ui.widget::<ItemList>(host).is_some() {
            host
        } else {
            let (w, h) = ui.element(host).map(|e| (e.width, e.height))?;
            let list = ItemList::create(ui, host, Element::at(0.0, 0.0, w, h));
            ui.set_anchors(list, AnchorEdges::ALL);
            // Margins now: the first layout pass skips a hidden panel,
            // which may have grown before it is first shown.
            ui.capture_current_anchor_baseline(list);
            list
        };
        if let Some(l) = ui.widget_mut::<ItemList>(list) {
            l.columns = 1;
            l.cell_width = template.width;
            l.cell_height = template.height;
        }
        let bar = |ui: &UiRoot, id| {
            layout
                .find(id)
                .filter(|b| ui.widget::<Scrollbar>(*b).is_some())
        };
        if let Some(b) = bar(ui, LIST_SCROLLBAR_ID)
            && let Some(s) = ui.widget_mut::<Scrollbar>(b)
        {
            s.model = ScrollSource::Element(list);
        }
        let info = layout
            .find(INFO_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some());
        if let Some(i) = info {
            if let Some(t) = ui.widget_mut::<Text>(i) {
                t.preserve_end_on_layout = false;
                t.wheel_scroll_enabled = true;
            }
            if let Some(e) = ui.element_mut(i) {
                e.click_through = false;
            }
            if let Some(b) = bar(ui, INFO_SCROLLBAR_ID)
                && let Some(s) = ui.widget_mut::<Scrollbar>(b)
            {
                s.model = ScrollSource::Element(i);
            }
        }
        let close = layout.find(CLOSE_ID).filter(|b| bind_click(ui, *b));
        let mut c = Self {
            positive,
            list,
            info,
            close,
            template,
            select_prompt,
            rows: Vec::new(),
            selected: None,
            rendered_revision: None,
            last_duration_update: None,
            info_value: None,
        };
        c.update_info(ui, None);
        Some(c)
    }

    /// The selected spell.
    #[must_use]
    pub fn selected_spell(&self) -> Option<u32> {
        self.selected
    }

    /// The item list holding the rows.
    #[must_use]
    pub fn list(&self) -> ElementId {
        self.list
    }

    /// The rows' cells, in order.
    #[must_use]
    pub fn row_cells(&self) -> Vec<ElementId> {
        self.rows.iter().map(|r| r.cell).collect()
    }

    /// Show `view`: the rows rebuilt when the enchantments changed (or the
    /// panel was shown), the times left at most once a second.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &EffectsView<'_>,
        rows: &mut dyn RowTemplates,
        icons: &mut dyn IconSource,
    ) {
        let revision = view.spellbook.enchantment_revision();
        if self.rendered_revision != Some(revision) {
            self.rendered_revision = Some(revision);
            self.rebuild(ui, view, rows, icons);
            return;
        }
        self.tick(ui, view);
    }

    /// The rows rebuilt on the next update (the panel was shown).
    pub fn invalidate(&mut self) {
        self.rendered_revision = None;
    }

    fn tick(&mut self, ui: &mut UiRoot, view: &EffectsView<'_>) {
        let now = view.now;
        if !now.is_finite() {
            return;
        }
        if let Some(last) = self.last_duration_update
            && now >= last
            && now - last < 1.0
        {
            return;
        }
        self.last_duration_update = Some(now);
        let enchantments = visible_enchantments(view.spellbook, self.positive);
        for e in &enchantments {
            let Some(row) = self.rows.iter().find(|r| r.identity == e.identity()) else {
                continue;
            };
            let text = if view.show_duration {
                format_remaining(e, now)
            } else {
                String::new()
            };
            if let Some(d) = row.duration {
                let color = ui
                    .widget::<Text>(d)
                    .map_or(crate::draw::Color::WHITE, |t| t.default_color);
                set_lines(ui, d, vec![Line::new(text, color)]);
            }
        }
    }

    fn rebuild(
        &mut self,
        ui: &mut UiRoot,
        view: &EffectsView<'_>,
        templates: &mut dyn RowTemplates,
        icons: &mut dyn IconSource,
    ) {
        self.last_duration_update = None;
        let list = self.list;
        ui.with::<ItemList, _>(list, |l, ui| l.flush(ui));
        self.rows.clear();
        let enchantments = visible_enchantments(view.spellbook, self.positive);
        if let Some(l) = ui.widget_mut::<ItemList>(list) {
            l.defer_layout();
        }
        for e in &enchantments {
            let Some(content) =
                templates.build_row(ui, self.template.layout, self.template.element)
            else {
                continue;
            };
            let metadata = view.spellbook.try_get_metadata(e.spell_id);
            let name = metadata.map_or_else(|| format!("Spell {}", e.spell_id), |m| m.name.clone());
            let icon = metadata.and_then(|_| icons.spell_icon(e.spell_id));
            if let Some(host) = ui.find_descendant(content, ROW_ICON_ID) {
                let (w, h) = ui.element(host).map_or((0.0, 0.0), |e| (e.width, e.height));
                let mut el = Element::at(0.0, 0.0, w, h).with_anchors(AnchorEdges::ALL);
                Image::configure(&mut el);
                ui.add(host, el, Image { texture: icon });
            }
            if let Some(label) = ui.find_descendant(content, ROW_LABEL_ID) {
                let color = ui
                    .widget::<Text>(label)
                    .map_or(crate::draw::Color::WHITE, |t| t.default_color);
                set_lines(ui, label, vec![Line::new(name, color)]);
            }
            let duration = ui.find_descendant(content, ROW_DURATION_ID);
            if let Some(d) = duration {
                let text = if view.show_duration {
                    format_remaining(e, view.now)
                } else {
                    String::new()
                };
                let color = ui
                    .widget::<Text>(d)
                    .map_or(crate::draw::Color::WHITE, |t| t.default_color);
                set_lines(ui, d, vec![Line::new(text, color)]);
            }
            let cell = TemplateSlot::create(
                ui,
                content,
                e.spell_id,
                states::NORMAL,
                Some(states::HIGHLIGHT),
            );
            ui.with::<ItemList, _>(list, |l, ui| l.add_item(ui, list, cell));
            self.rows.push(EffectRow {
                cell,
                spell_id: e.spell_id,
                identity: e.identity(),
                duration,
            });
        }
        let defer_end = |ui: &mut UiRoot| {
            ui.with::<ItemList, _>(list, |l, ui| l.end_defer_layout(ui, list));
        };
        defer_end(ui);
        if let Some(selected) = self.selected
            && !self.rows.iter().any(|r| r.spell_id == selected)
        {
            self.selected = None;
        }
        self.sync_selection(ui);
        self.update_info(ui, Some(view.spellbook));
    }

    fn sync_selection(&self, ui: &mut UiRoot) {
        for r in &self.rows {
            TemplateSlot::set_selected(ui, r.cell, Some(r.spell_id) == self.selected);
        }
    }

    fn update_info(&mut self, ui: &mut UiRoot, book: Option<&Spellbook>) {
        let Some(info) = self.info else {
            return;
        };
        let mut value = self.select_prompt.clone();
        if let (Some(spell), Some(book)) = (self.selected, book)
            && book
                .active_enchantments()
                .iter()
                .any(|r| r.spell_id == spell)
            && let Some(m) = book.try_get_metadata(spell)
        {
            value = format!("{}\n\n{}", m.name, m.description);
        }
        let width = ui.element(info).map(|e| e.width);
        let key = format!("{value}\u{0}{width:?}");
        if self.info_value.as_deref() == Some(key.as_str()) {
            return;
        }
        self.info_value = Some(key);
        let lines = shape_detail_text(ui, info, &value);
        set_lines(ui, info, lines);
    }

    /// A row was clicked: select its spell, or deselect it when it was the
    /// selection (every layer of one spell shares the selection).
    pub fn select(&mut self, ui: &mut UiRoot, spell_id: u32, book: Option<&Spellbook>) {
        self.selected = if self.selected == Some(spell_id) {
            None
        } else {
            Some(spell_id)
        };
        self.sync_selection(ui);
        self.update_info(ui, book);
    }

    /// The spell of `cell` when it is one of the rows.
    #[must_use]
    pub fn row_spell(&self, cell: ElementId) -> Option<u32> {
        self.rows
            .iter()
            .find(|r| r.cell == cell)
            .map(|r| r.spell_id)
    }

    /// The close button.
    #[must_use]
    pub fn close_button(&self) -> Option<ElementId> {
        self.close
    }
}

/// An effects window's controller: the panel and the clicks queued for
/// the next update (they need the spellbook).
#[derive(Debug)]
pub struct EffectsWindowController {
    /// The panel.
    pub effects: EffectsController,
    /// The main panel id it closes.
    pub panel: u32,
    clicked: Vec<u32>,
}

impl EffectsWindowController {
    /// A window controller for `effects`, main panel `panel`.
    #[must_use]
    pub fn new(effects: EffectsController, panel: u32) -> Self {
        Self {
            effects,
            panel,
            clicked: Vec::new(),
        }
    }

    /// Show `view`, applying the clicks since the last update.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &EffectsView<'_>,
        rows: &mut dyn RowTemplates,
        icons: &mut dyn IconSource,
    ) {
        self.effects.update(ui, view, rows, icons);
        for spell in std::mem::take(&mut self.clicked) {
            self.effects.select(ui, spell, Some(view.spellbook));
        }
    }
}

impl PanelController for EffectsWindowController {
    fn on_shown(&mut self, _ui: &mut UiRoot) {
        self.effects.invalidate();
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return false;
        }
        if Some(element) == self.effects.close_button() {
            emit(ui, PanelOutput::ClosePanel(self.panel));
            return true;
        }
        if let Some(spell) = self.effects.row_spell(element) {
            self.clicked.push(spell);
            return true;
        }
        false
    }
}

// ── The link-status panel ───────────────────────────────────────────────

/// The link-status panel's layout.
pub const LINK_LAYOUT_ID: u32 = 0x2100_001D;
/// Its root.
pub const LINK_ROOT_ID: u32 = 0x1000_0167;
/// Its text.
pub const LINK_MAIN_TEXT_ID: u32 = 0x1000_0169;
/// How often the open panel refreshes, seconds.
pub const LINK_REFRESH_SECONDS: f64 = 5.0;
/// How often the open panel pings again, seconds.
pub const LINK_PING_SECONDS: f64 = 120.0;

/// The link-status panel's authored strings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkStatusStrings {
    /// What the indicator is.
    pub description: String,
    /// The colours.
    pub legend: String,
    /// The forty-second warning.
    pub disconnect_warning: String,
    /// Before the loss percentage.
    pub packet_loss_prefix: String,
    /// Before the round trip.
    pub ping_prefix: String,
}

impl LinkStatusStrings {
    /// OpenAC's English fallbacks (used where the dat has no string).
    #[must_use]
    pub fn english() -> Self {
        Self {
            description: "The Link Indicator shows the current status of your connection to the game servers.".to_owned(),
            legend: "\n\nGREEN = your link is good.\nYELLOW = no packets for at least 5 sec.\nRED = no packets for at least 20 sec.".to_owned(),
            disconnect_warning: "\n\nIf approximately forty seconds pass without receiving a packet, you will be disconnected from the server.".to_owned(),
            packet_loss_prefix: "\n\n\nPacket loss for the last 10 sec: ".to_owned(),
            ping_prefix: "\n\nRoundtrip Ping time to Server: ".to_owned(),
        }
    }

    /// The panel's text for `link`.
    #[must_use]
    pub fn body(&self, link: &LinkStatus) -> String {
        let ping = match link.round_trip_seconds {
            Some(s) if s.is_finite() && s >= 0.0 => format!("{:.0}", s * 1000.0),
            _ => "????".to_owned(),
        };
        format!(
            "{}{}{}{}{:.2}{}{}",
            self.description,
            self.legend,
            self.disconnect_warning,
            self.packet_loss_prefix,
            link.packet_loss_percent,
            self.ping_prefix,
            ping
        )
    }
}

/// The link-status panel's controller.
#[derive(Debug)]
pub struct LinkStatusController {
    text: ElementId,
    close: Option<ElementId>,
    strings: LinkStatusStrings,
    panel: u32,
    visible: bool,
    please_ping: bool,
    next_update: f64,
    last_ping: Option<f64>,
}

impl LinkStatusController {
    /// Bind to the imported panel (`None` without its text).
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        strings: LinkStatusStrings,
    ) -> Option<Self> {
        let text = layout
            .find(LINK_MAIN_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        let close = layout.find(CLOSE_ID).filter(|b| bind_click(ui, *b));
        Some(Self {
            text,
            close,
            strings,
            panel: panel_id::LINK_STATUS,
            visible: false,
            please_ping: false,
            next_update: -1.0,
            last_ping: None,
        })
    }

    /// The panel's text element.
    #[must_use]
    pub fn text(&self) -> ElementId {
        self.text
    }

    /// One frame at `now` (client seconds): while open, the text every
    /// five seconds, a ping on opening and every two minutes.
    pub fn update(&mut self, ui: &mut UiRoot, link: &LinkStatus, now: f64) {
        if !self.visible || !now.is_finite() || now < self.next_update {
            return;
        }
        self.next_update = now + LINK_REFRESH_SECONDS;
        self.refresh(ui, link, now);
    }

    fn refresh(&mut self, ui: &mut UiRoot, link: &LinkStatus, now: f64) {
        let lines = shape_detail_text(ui, self.text, &self.strings.body(link));
        set_lines(ui, self.text, lines);
        let periodic = self.last_ping.is_some_and(|t| now - t >= LINK_PING_SECONDS);
        if !self.please_ping && !periodic {
            return;
        }
        self.last_ping = Some(now);
        self.please_ping = false;
        emit(ui, PanelOutput::RequestLinkPing);
    }
}

impl PanelController for LinkStatusController {
    fn on_shown(&mut self, _ui: &mut UiRoot) {
        // The refresh needs the link and the clock: the next update does it.
        self.visible = true;
        self.please_ping = true;
        self.next_update = f64::NEG_INFINITY;
    }

    fn on_hidden(&mut self, _ui: &mut UiRoot) {
        self.visible = false;
        self.please_ping = false;
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close && matches!(message, ElementMessage::Clicked { .. }) {
            emit(ui, PanelOutput::ClosePanel(self.panel));
            return true;
        }
        false
    }
}

// ── The vitae panel ─────────────────────────────────────────────────────

/// The vitae panel's layout.
pub const VITAE_LAYOUT_ID: u32 = 0x2100_0020;
/// Its root.
pub const VITAE_ROOT_ID: u32 = 0x1000_01C1;
/// Its text.
pub const VITAE_MAIN_TEXT_ID: u32 = 0x1000_01C3;
/// The player's experience toward the next vitae step (property int 0x81).
pub const VITAE_CP_POOL_PROPERTY: u32 = 0x81;
/// The player's level at death (property int 0x8B).
pub const DEATH_LEVEL_PROPERTY: u32 = 0x8B;
/// The player's level (property int 0x19).
pub const LEVEL_PROPERTY: u32 = 0x19;

/// The vitae panel's authored strings.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VitaeStrings {
    /// No vitae lost.
    pub full_strength: String,
    /// Before the lost percentage.
    pub lost_prefix: String,
    /// After it.
    pub lost_suffix: String,
    /// Before the skills' percentage.
    pub skills_prefix: String,
    /// After it.
    pub skills_suffix: String,
    /// Before the experience to recover.
    pub recovery_prefix: String,
    /// After it.
    pub recovery_suffix: String,
}

impl VitaeStrings {
    /// OpenAC's English fallbacks.
    #[must_use]
    pub fn english() -> Self {
        Self {
            full_strength: "Your Vitae, or life force, is at full strength.".to_owned(),
            lost_prefix: "Due to your recent death, you have temporarily lost ".to_owned(),
            lost_suffix: "% of your Vitae, or life force.".to_owned(),
            skills_prefix: "\n\nThis means that your health, stamina, mana, and skills are temporarily reduced by ".to_owned(),
            skills_suffix: "%.  A reduction of less than 15% will not hinder you much, but beware losing much more than that.".to_owned(),
            recovery_prefix: "\n\nYou will regain 1% of your Vitae once you earn ".to_owned(),
            recovery_suffix: " more experience.".to_owned(),
        }
    }
}

/// The experience pool that recovers the next point of vitae at `vitae`
/// (0..1) for a character of `level`.
#[must_use]
pub fn vitae_cp_pool_threshold(vitae: f32, level: i32) -> i32 {
    (((f64::from(level).powf(2.5) * 2.5) + 20.0) * f64::from(vitae).powf(5.0) + 0.5) as i32
}

/// `experience` with thousands separators ("61,000,000").
#[must_use]
pub fn format_experience(experience: i32) -> String {
    let digits = experience.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if experience < 0 {
        format!("-{out}")
    } else {
        out
    }
}

/// The player's vitae: the last finite vitae enchantment's factor, else 1.
#[must_use]
pub fn current_vitae(book: Option<&Spellbook>) -> f32 {
    book.and_then(|b| {
        b.active_enchantment_snapshot()
            .iter()
            .filter(|r| r.bucket == 4)
            .filter_map(|r| r.stat_mod_value)
            .rfind(|v| v.is_finite())
    })
    .unwrap_or(1.0)
}

/// The vitae panel's text for the player.
#[must_use]
pub fn vitae_text(
    strings: &VitaeStrings,
    book: Option<&Spellbook>,
    objects: &ClientObjectTable,
    player: u32,
) -> String {
    let vitae = current_vitae(book);
    let lost = 100 - (vitae * 100.0) as i32;
    if lost <= 0 {
        return strings.full_strength.clone();
    }
    let p = objects.get(player);
    let pool = p.map_or(0, |p| p.properties.get_int(VITAE_CP_POOL_PROPERTY, 0));
    let level = p.map_or(0, |p| {
        p.properties
            .ints
            .get(&DEATH_LEVEL_PROPERTY)
            .copied()
            .unwrap_or_else(|| p.properties.get_int(LEVEL_PROPERTY, 0))
    });
    let remaining = vitae_cp_pool_threshold(vitae, level) - pool;
    format!(
        "{}{lost}{}{}{lost}{}{}{}{}",
        strings.lost_prefix,
        strings.lost_suffix,
        strings.skills_prefix,
        strings.skills_suffix,
        strings.recovery_prefix,
        format_experience(remaining),
        strings.recovery_suffix
    )
}

/// The vitae panel's controller.
#[derive(Debug)]
pub struct VitaeController {
    text: ElementId,
    close: Option<ElementId>,
    strings: VitaeStrings,
    shown: Option<String>,
}

impl VitaeController {
    /// Bind to the imported panel (`None` without its text).
    pub fn bind(ui: &mut UiRoot, layout: &ImportedLayout, strings: VitaeStrings) -> Option<Self> {
        let text = layout
            .find(VITAE_MAIN_TEXT_ID)
            .filter(|t| ui.widget::<Text>(*t).is_some())?;
        let close = layout.find(CLOSE_ID).filter(|b| bind_click(ui, *b));
        Some(Self {
            text,
            close,
            strings,
            shown: None,
        })
    }

    /// The panel's text element.
    #[must_use]
    pub fn text(&self) -> ElementId {
        self.text
    }

    /// Show the player's vitae.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        book: Option<&Spellbook>,
        objects: &ClientObjectTable,
        player: u32,
    ) {
        let body = vitae_text(&self.strings, book, objects, player);
        if self.shown.as_deref() == Some(body.as_str()) {
            return;
        }
        let lines = shape_detail_text(ui, self.text, &body);
        set_lines(ui, self.text, lines);
        self.shown = Some(body);
    }
}

impl PanelController for VitaeController {
    fn on_shown(&mut self, _ui: &mut UiRoot) {
        self.shown = None;
    }

    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close && matches!(message, ElementMessage::Clicked { .. }) {
            emit(ui, PanelOutput::ClosePanel(panel_id::VITAE));
            return true;
        }
        false
    }
}

// ── The mini-game panel ─────────────────────────────────────────────────

pub use super::mini_game::{
    MINI_GAME_CLOSE_ID, MINI_GAME_LAYOUT_ID, MINI_GAME_ROOT_ID, MiniGameController,
};
