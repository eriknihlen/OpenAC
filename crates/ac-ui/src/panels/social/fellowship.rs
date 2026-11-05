//! The social panel's fellowship page: the empty frame with the name entry
//! and create button, or the fellowship's name, its roster (name,
//! "level/share%" and three vital meters per member), the member buttons
//! (leader, quit, open, recruit, dismiss, disband) and the four option
//! checkboxes.

use std::collections::{HashMap, HashSet};

use ac_runtime::character::option_id;
use ac_runtime::gameplay::{FellowView, FellowshipCommand, FellowshipView, GameplayCommand};
use ac_runtime::inventory::ObjectRef;
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, PanelStrings, link_list_scrollbar};
use crate::root::UiRoot;
use crate::strings::compute_hash;

use crate::tree::ElementId;
use crate::widgets::{Button, DatElement, Field, ListBox, Meter};

use super::{SocialView, find_button, find_text, set_line};

/// The page.
pub const PAGE_ID: u32 = 0x1000_0292;
/// Shown outside a fellowship.
pub const NOT_IN_FELLOWSHIP_FRAME_ID: u32 = 0x1000_026B;
/// Shown in one.
pub const IN_FELLOWSHIP_FRAME_ID: u32 = 0x1000_0275;
/// The roster.
pub const LIST_BOX_ID: u32 = 0x1000_0279;
/// The name entry.
pub const NAME_ENTRY_ID: u32 = 0x1000_026F;
/// Create a fellowship.
pub const CREATE_BUTTON_ID: u32 = 0x1000_0274;
/// The fellowship's name.
pub const FELLOWSHIP_NAME_ID: u32 = 0x1000_0276;
/// Hand the lead to the selected fellow.
pub const LEADER_BUTTON_ID: u32 = 0x1000_027B;
/// Leave.
pub const QUIT_BUTTON_ID: u32 = 0x1000_027C;
/// Open or close recruiting.
pub const OPEN_BUTTON_ID: u32 = 0x1000_027D;
/// Recruit the selection.
pub const RECRUIT_BUTTON_ID: u32 = 0x1000_027E;
/// Dismiss the selected fellow.
pub const DISMISS_BUTTON_ID: u32 = 0x1000_027F;
/// Disband.
pub const DISBAND_BUTTON_ID: u32 = 0x1000_0280;
/// Ignore fellowship requests.
pub const IGNORE_REQUESTS_ID: u32 = 0x1000_0270;
/// Accept fellowship requests on their own.
pub const AUTO_ACCEPT_ID: u32 = 0x1000_0271;
/// Share experience.
pub const SHARE_XP_ID: u32 = 0x1000_0272;
/// Share loot.
pub const SHARE_LOOT_ID: u32 = 0x1000_0273;
/// A row's name band.
pub const ROW_NAME_BAND_ID: u32 = 0x1000_0282;
/// A row's name.
pub const ROW_NAME_ID: u32 = 0x1000_0283;
/// A row's "level/share%".
pub const ROW_STATS_ID: u32 = 0x1000_0284;
/// A row's health meter.
pub const ROW_HEALTH_ID: u32 = 0x1000_0285;
/// A row's stamina meter.
pub const ROW_STAMINA_ID: u32 = 0x1000_0287;
/// A row's mana meter.
pub const ROW_MANA_ID: u32 = 0x1000_0289;

/// The option captions' string table.
pub const OPTION_STRING_TABLE_ID: u32 = 0x2300_0003;
/// The fellowship strings' table.
pub const FELLOWSHIP_STRING_TABLE_ID: u32 = 0x2300_0001;
/// The authored "level/share%" entry.
pub const STATS_TEMPLATE_KEY: u32 = 0x003B_5A03;
/// Its level variable.
pub const STATS_LEVEL_VARIABLE: u32 = 5_286_556;
/// Its percent variable.
pub const STATS_PERCENT_VARIABLE: u32 = 174_673_717;
/// The most members a fellowship holds.
pub const MAX_FELLOWSHIP_SIZE: usize = 9;

/// Each member's share of an even split, by member count (1..=10).
pub const EVEN_SPLIT_SHARE: [f32; 10] = [
    1.0,
    0.75,
    0.6,
    0.55,
    0.5,
    0.45,
    0.4,
    0.35,
    0.311_111_1,
    0.28,
];

const MEMBER_NAME_COLOR: Color = Color::WHITE;

/// The four checkboxes: element, option, retail option name.
const CHECKBOXES: [(u32, u32, &str); 4] = [
    (
        IGNORE_REQUESTS_ID,
        option_id::IGNORE_FELLOWSHIP_REQUESTS,
        "IgnoreFellowshipRequests",
    ),
    (
        AUTO_ACCEPT_ID,
        option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
        "FellowshipAutoAcceptRequests",
    ),
    (
        SHARE_XP_ID,
        option_id::FELLOWSHIP_SHARE_XP,
        "FellowshipShareXP",
    ),
    (
        SHARE_LOOT_ID,
        option_id::FELLOWSHIP_SHARE_LOOT,
        "FellowshipShareLoot",
    ),
];

#[derive(Debug, Clone, Copy)]
struct RowWidgets {
    band: Option<ElementId>,
    name: Option<ElementId>,
    stats: Option<ElementId>,
    health: Option<ElementId>,
    stamina: Option<ElementId>,
    mana: Option<ElementId>,
}

/// The fellowship page's controller.
#[derive(Debug)]
pub struct FellowshipPage {
    not_in: ElementId,
    in_fellowship: ElementId,
    list: Option<ElementId>,
    name_field: Option<ElementId>,
    create: Option<ElementId>,
    fellowship_name: Option<ElementId>,
    leader: Option<ElementId>,
    quit: Option<ElementId>,
    open: Option<ElementId>,
    recruit: Option<ElementId>,
    dismiss: Option<ElementId>,
    disband: Option<ElementId>,
    checkboxes: Vec<(ElementId, u32)>,
    open_caption: Option<String>,
    close_caption: Option<String>,
    rows: HashMap<u32, RowWidgets>,
    row_owner: HashMap<ElementId, u32>,
    member_guids: HashSet<u32>,
    selected_fellow: u32,
    last_roster_revision: Option<u64>,
    last_open_state: Option<bool>,
    last_name: Option<String>,
    page_visible: bool,
    options: HashMap<u32, bool>,
    fellowship: FellowshipView,
    world_selection: Option<u32>,
    player: u32,
}

/// "level/share%": no sharing shows 0; an even split takes the member
/// count's share; otherwise each member's share is the experience their
/// next level costs over everyone's.
#[must_use]
pub fn stats_text(
    member: &FellowView,
    fellowship: &FellowshipView,
    experience_to_raise: Option<&dyn Fn(u32) -> i64>,
    strings: Option<&dyn PanelStrings>,
) -> String {
    let mut share = 0.0_f64;
    if fellowship.share_xp {
        if fellowship.even_xp_split {
            share = even_split_share(fellowship.members.len());
        } else if let Some(to_raise) = experience_to_raise {
            #[allow(clippy::cast_precision_loss)]
            let sum: f64 = fellowship
                .members
                .iter()
                .map(|f| to_raise(f.level) as f64)
                .sum();
            if sum > 0.0 {
                #[allow(clippy::cast_precision_loss)]
                let own = to_raise(member.level) as f64;
                share = own / sum;
            }
        }
    }
    // Truncated, as the game prints it.
    #[allow(clippy::cast_possible_truncation)]
    let percent = (share * 100.0) as i32;
    strings
        .and_then(|s| {
            s.template(
                FELLOWSHIP_STRING_TABLE_ID,
                STATS_TEMPLATE_KEY,
                &HashMap::from([
                    (STATS_LEVEL_VARIABLE, member.level.to_string()),
                    (STATS_PERCENT_VARIABLE, percent.to_string()),
                ]),
            )
        })
        .unwrap_or_else(|| format!("{}/{percent}%", member.level))
}

/// One member's share of an even split of `count` members (0 outside
/// 1..=10).
#[must_use]
pub fn even_split_share(count: usize) -> f64 {
    match count {
        1..=10 => f64::from(EVEN_SPLIT_SHARE[count - 1]),
        _ => 0.0,
    }
}

fn set_meter(ui: &mut UiRoot, meter: Option<ElementId>, (current, max): (u32, u32)) {
    if let Some(m) = meter.and_then(|m| ui.widget_mut::<Meter>(m)) {
        #[allow(clippy::cast_precision_loss)]
        let fill = if max > 0 {
            current as f32 / max as f32
        } else {
            0.0
        };
        m.fill = Some(fill);
        m.label = Some(format!("{current}/{max}"));
    }
}

impl FellowshipPage {
    /// Bind to the fellowship page `page` (`None` without its two frames).
    pub fn bind(
        ui: &mut UiRoot,
        page: ElementId,
        strings: Option<&dyn PanelStrings>,
    ) -> Option<Self> {
        let not_in = ui.find_descendant(page, NOT_IN_FELLOWSHIP_FRAME_ID)?;
        let in_fellowship = ui.find_descendant(page, IN_FELLOWSHIP_FRAME_ID)?;
        let list = ui
            .find_descendant(page, LIST_BOX_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some());
        if let Some(l) = list {
            link_list_scrollbar(ui, page, l);
        }
        let name_field = ui
            .find_descendant(page, NAME_ENTRY_ID)
            .filter(|f| ui.widget::<Field>(*f).is_some());
        let button = |ui: &mut UiRoot, id| find_button(ui, page, id);
        let open = button(ui, OPEN_BUTTON_ID);
        let string = |key: &str| {
            strings.and_then(|s| s.string(FELLOWSHIP_STRING_TABLE_ID, compute_hash(key)))
        };
        let mut checkboxes = Vec::new();
        for (id, option, retail_name) in CHECKBOXES {
            let Some(b) = button(ui, id) else {
                continue;
            };
            let key = format!("ID_PlayerOption_{retail_name}");
            let label = strings.and_then(|s| s.string(OPTION_STRING_TABLE_ID, compute_hash(&key)));
            let tooltip = strings.and_then(|s| {
                s.string(OPTION_STRING_TABLE_ID, compute_hash(&format!("{key}_Help")))
            });
            if let Some(w) = ui.widget_mut::<Button>(b) {
                if label.is_some() {
                    w.label = label;
                }
                if tooltip.is_some() {
                    w.tooltip_text = tooltip;
                }
                // The checkbox shows the option, not its own toggling.
                w.suppress_self_toggle = true;
            }
            checkboxes.push((b, option));
        }
        let page = Self {
            not_in,
            in_fellowship,
            list,
            name_field,
            create: button(ui, CREATE_BUTTON_ID),
            fellowship_name: find_text(ui, page, FELLOWSHIP_NAME_ID),
            leader: button(ui, LEADER_BUTTON_ID),
            quit: button(ui, QUIT_BUTTON_ID),
            open,
            recruit: button(ui, RECRUIT_BUTTON_ID),
            dismiss: button(ui, DISMISS_BUTTON_ID),
            disband: button(ui, DISBAND_BUTTON_ID),
            checkboxes,
            open_caption: string("ID_Fellowship_OpenFellowshipButtonText"),
            close_caption: string("ID_Fellowship_CloseFellowshipButtonText"),
            rows: HashMap::new(),
            row_owner: HashMap::new(),
            member_guids: HashSet::new(),
            selected_fellow: 0,
            last_roster_revision: None,
            last_open_state: None,
            last_name: None,
            page_visible: false,
            options: HashMap::new(),
            fellowship: FellowshipView::default(),
            world_selection: None,
            player: 0,
        };
        page.show_frames(ui);
        Some(page)
    }

    fn show_frames(&self, ui: &mut UiRoot) {
        let inside = self.fellowship.in_fellowship;
        ui.set_visible(self.not_in, !inside);
        ui.set_visible(self.in_fellowship, inside);
    }

    /// The fellow selected on the page (0: none).
    #[must_use]
    pub fn selected_fellow(&self) -> u32 {
        self.selected_fellow
    }

    /// The page is declared open (the window shows and this is the active
    /// tab).
    #[must_use]
    pub fn page_visible(&self) -> bool {
        self.page_visible
    }

    /// Show `view`'s fellowship.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        view: &SocialView<'_>,
        rows: &mut dyn super::RowTemplates,
    ) {
        self.fellowship.clone_from(view.fellowship);
        self.world_selection = view.selected;
        self.player = view.player;
        for (_, option, _) in CHECKBOXES {
            self.options.insert(option, view.options.get(option));
        }
        self.show_frames(ui);
        self.refresh_checkboxes(ui);
        self.refresh_create_button(ui);
        if !view.fellowship.in_fellowship {
            if !self.rows.is_empty() {
                if let Some(l) = self.list {
                    ui.with::<ListBox, _>(l, |b, ui| b.flush(ui));
                }
                self.rows.clear();
                self.row_owner.clear();
            }
            self.member_guids.clear();
            self.selected_fellow = 0;
            self.last_roster_revision = None;
            self.last_open_state = None;
            self.last_name = None;
            return;
        }
        if self.last_name.as_deref() != Some(view.fellowship.name.as_str()) {
            self.last_name = Some(view.fellowship.name.clone());
            set_line(
                ui,
                self.fellowship_name,
                &view.fellowship.name,
                MEMBER_NAME_COLOR,
            );
        }
        if self.last_roster_revision != Some(view.fellowship.revision) {
            self.last_roster_revision = Some(view.fellowship.revision);
            self.refresh_roster(ui, view, rows);
        }
        if let Some(id) = view.selected
            && self.member_guids.contains(&id)
        {
            self.set_selected_fellow(ui, id, view);
        }
        self.refresh_buttons(ui);
        self.refresh_open_caption(ui);
    }

    fn refresh_checkboxes(&self, ui: &mut UiRoot) {
        for &(b, option) in &self.checkboxes {
            let on = self.options.get(&option).copied().unwrap_or(false);
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, on));
        }
    }

    fn name_text(&self, ui: &UiRoot) -> String {
        self.name_field
            .and_then(|f| ui.widget::<Field>(f))
            .map(Field::text)
            .unwrap_or_default()
    }

    fn refresh_create_button(&self, ui: &mut UiRoot) {
        if let Some(c) = self.create {
            let has_name = !self.name_text(ui).trim().is_empty();
            ui.set_enabled(c, has_name);
        }
    }

    fn refresh_open_caption(&mut self, ui: &mut UiRoot) {
        let Some(b) = self.open else {
            return;
        };
        if self.last_open_state == Some(self.fellowship.open) {
            return;
        }
        self.last_open_state = Some(self.fellowship.open);
        self.set_open_caption(ui, b, self.fellowship.open);
    }

    fn set_open_caption(&self, ui: &mut UiRoot, button: ElementId, open: bool) {
        let label = if open {
            self.close_caption.clone()
        } else {
            self.open_caption.clone()
        };
        if label.is_some()
            && let Some(w) = ui.widget_mut::<Button>(button)
        {
            w.label = label;
        }
    }

    fn refresh_buttons(&self, ui: &mut UiRoot) {
        let me = self.player;
        let leader = self.fellowship.leader_guid;
        let is_leader = leader != 0 && leader == me;
        let has_selection = self.selected_fellow != 0;
        let selected_is_self = has_selection && self.selected_fellow == me;
        let set = |ui: &mut UiRoot, b: Option<ElementId>, on: bool| {
            if let Some(b) = b {
                ui.set_enabled(b, on);
            }
        };
        set(ui, self.quit, true);
        set(ui, self.disband, is_leader);
        set(ui, self.open, is_leader);
        let acts_on_fellow = is_leader && has_selection && !selected_is_self;
        set(ui, self.leader, acts_on_fellow);
        set(ui, self.dismiss, acts_on_fellow);
        let target_valid = self
            .world_selection
            .is_some_and(|id| id != me && !self.member_guids.contains(&id));
        let not_full = self.fellowship.members.len() < MAX_FELLOWSHIP_SIZE;
        set(
            ui,
            self.recruit,
            target_valid && not_full && (is_leader || self.fellowship.open),
        );
    }

    fn refresh_roster(
        &mut self,
        ui: &mut UiRoot,
        view: &SocialView<'_>,
        rows: &mut dyn super::RowTemplates,
    ) {
        if self.list.is_none() {
            return;
        }
        let members = &view.fellowship.members;
        let changed = members.len() != self.member_guids.len()
            || members.iter().any(|m| !self.member_guids.contains(&m.guid));
        if changed {
            self.rebuild_roster(ui, view, rows);
        } else {
            for m in members {
                self.update_row(ui, m, view);
            }
        }
        if self.selected_fellow != 0 && !self.member_guids.contains(&self.selected_fellow) {
            self.selected_fellow = 0;
        }
    }

    fn rebuild_roster(
        &mut self,
        ui: &mut UiRoot,
        view: &SocialView<'_>,
        templates: &mut dyn super::RowTemplates,
    ) {
        let Some(list) = self.list else {
            return;
        };
        ui.with::<ListBox, _>(list, |b, ui| b.flush_preserving_scroll(ui));
        self.rows.clear();
        self.row_owner.clear();
        self.member_guids = view.fellowship.members.iter().map(|m| m.guid).collect();
        let template = ui
            .widget::<ListBox>(list)
            .and_then(|b| b.templates.first().copied());
        let list_width = ui.element(list).map_or(0.0, |e| e.width);
        for m in &view.fellowship.members {
            let Some(t) = template else {
                break;
            };
            let Some(row) = templates.build_row(ui, t.layout, t.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
            let widgets = RowWidgets {
                band: ui
                    .find_descendant(row, ROW_NAME_BAND_ID)
                    .filter(|b| ui.widget::<DatElement>(*b).is_some()),
                name: find_text(ui, row, ROW_NAME_ID),
                stats: find_text(ui, row, ROW_STATS_ID),
                health: ui.find_descendant(row, ROW_HEALTH_ID),
                stamina: ui.find_descendant(row, ROW_STAMINA_ID),
                mana: ui.find_descendant(row, ROW_MANA_ID),
            };
            for t in [widgets.name, widgets.stats].into_iter().flatten() {
                bind_click(ui, t);
                self.row_owner.insert(t, m.guid);
            }
            if let Some(stats) = widgets.stats {
                // The authored row is wider than the list: the right-aligned
                // text gives up the part that would sit under the scrollbar.
                let row_width = ui.element(row).map_or(0.0, |e| e.width);
                let overlap = row_width - list_width;
                if let Some(e) = ui.element_mut(stats)
                    && overlap > 0.0
                    && e.width > overlap
                {
                    e.width -= overlap;
                }
            }
            self.rows.insert(m.guid, widgets);
        }
        for m in &view.fellowship.members {
            self.update_row(ui, m, view);
        }
    }

    fn update_row(&self, ui: &mut UiRoot, member: &FellowView, view: &SocialView<'_>) {
        let Some(w) = self.rows.get(&member.guid).copied() else {
            return;
        };
        set_line(ui, w.name, &member.name, MEMBER_NAME_COLOR);
        if let Some(band) = w.band.and_then(|b| ui.widget_mut::<DatElement>(b)) {
            band.active_state = if self.selected_fellow == member.guid {
                "Highlight".to_owned()
            } else {
                String::new()
            };
        }
        if w.stats.is_some() {
            let text = stats_text(
                member,
                view.fellowship,
                view.experience_to_raise,
                view.strings,
            );
            set_line(ui, w.stats, &text, MEMBER_NAME_COLOR);
        }
        set_meter(ui, w.health, member.health);
        set_meter(ui, w.stamina, member.stamina);
        set_meter(ui, w.mana, member.mana);
    }

    fn set_selected_fellow(&mut self, ui: &mut UiRoot, guid: u32, view: &SocialView<'_>) {
        if self.selected_fellow == guid {
            return;
        }
        self.selected_fellow = guid;
        for m in &view.fellowship.members {
            self.update_row(ui, m, view);
        }
    }

    fn select_band(&self, ui: &mut UiRoot) {
        for (guid, w) in &self.rows {
            if let Some(band) = w.band.and_then(|b| ui.widget_mut::<DatElement>(b)) {
                band.active_state = if self.selected_fellow == *guid {
                    "Highlight".to_owned()
                } else {
                    String::new()
                };
            }
        }
    }

    /// Declare the page open or closed to the server (the window shows and
    /// this is the active tab); a declaration counts only in the world.
    pub fn set_page_visible(&mut self, ui: &mut UiRoot, visible: bool, in_world: bool) {
        if self.page_visible == visible {
            return;
        }
        if in_world {
            fellowship(ui, FellowshipCommand::SetPanelOpen(visible));
            self.page_visible = visible;
        }
    }

    /// The session ended: the next declaration is sent again.
    pub fn reset_page_visible_latch(&mut self) {
        self.page_visible = false;
    }

    /// Handle a click on `element`; true when it is the page's.
    pub fn on_click(&mut self, ui: &mut UiRoot, element: ElementId) -> bool {
        if let Some(&guid) = self.row_owner.get(&element) {
            self.selected_fellow = guid;
            self.select_band(ui);
            emit(ui, PanelOutput::SelectObject(guid));
            return true;
        }
        if Some(element) == self.create {
            let name = self.name_text(ui);
            if !name.trim().is_empty() {
                let share_xp = self
                    .options
                    .get(&option_id::FELLOWSHIP_SHARE_XP)
                    .copied()
                    .unwrap_or(false);
                fellowship(ui, FellowshipCommand::Create { name, share_xp });
            }
            return true;
        }
        if Some(element) == self.recruit {
            if let Some(target) = self.world_selection {
                fellowship(ui, FellowshipCommand::Recruit(ObjectRef::Guid(target)));
            }
            return true;
        }
        if Some(element) == self.dismiss {
            if self.selected_fellow != 0 {
                fellowship(
                    ui,
                    FellowshipCommand::Dismiss(ObjectRef::Guid(self.selected_fellow)),
                );
            }
            return true;
        }
        if Some(element) == self.leader {
            if self.selected_fellow != 0 {
                fellowship(
                    ui,
                    FellowshipCommand::AssignLeader(ObjectRef::Guid(self.selected_fellow)),
                );
            }
            return true;
        }
        if Some(element) == self.quit {
            fellowship(ui, FellowshipCommand::Quit { disband: false });
            return true;
        }
        if Some(element) == self.disband {
            fellowship(ui, FellowshipCommand::Quit { disband: true });
            return true;
        }
        if Some(element) == self.open {
            let next = !self.fellowship.open;
            fellowship(ui, FellowshipCommand::SetOpen(next));
            // The caption follows the click at once.
            self.last_open_state = Some(next);
            self.set_open_caption(ui, element, next);
            return true;
        }
        if let Some(&(_, option)) = self.checkboxes.iter().find(|(b, _)| *b == element) {
            let next = !self.options.get(&option).copied().unwrap_or(false);
            self.options.insert(option, next);
            emit(
                ui,
                PanelOutput::Session(SessionCommand::SetCharacterOption {
                    option,
                    value: next,
                }),
            );
            return true;
        }
        false
    }

    /// The name entry changed: the create button follows it.
    pub fn on_text_changed(&self, ui: &mut UiRoot, element: ElementId) -> bool {
        if Some(element) == self.name_field {
            self.refresh_create_button(ui);
            return true;
        }
        false
    }
}

fn fellowship(ui: &mut UiRoot, c: FellowshipCommand) {
    emit(
        ui,
        PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Fellowship(c))),
    );
}
