//! The social panel's allegiance page: the player's block (allegiance
//! name, followers, rank line), the monarch and patron blocks (with the
//! experience passed up), the vassal roster, the ignore-requests checkbox
//! and the swear, break and kick buttons (each confirmed first).

use std::collections::{HashMap, HashSet};

use ac_proto::allegiance::{AllegianceMember, AllegianceProfile};
use ac_runtime::character::option_id;
use ac_runtime::gameplay::{AllegianceCommand, GameplayCommand};
use ac_runtime::inventory::ObjectRef;
use ac_runtime::session::SessionCommand;

use crate::draw::Color;
use crate::panels::character::format_xp;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, PanelStrings, link_list_scrollbar};
use crate::root::UiRoot;
use crate::strings::{PLAYER_VARIABLE, compute_hash};
use crate::tree::ElementId;
use crate::widgets::{Button, ListBox};

use super::rank_titles::{compose_full_name, title};
use super::{RowTemplates, SocialView, find_button, find_text, set_line};

/// The page.
pub const PAGE_ID: u32 = 0x1000_0291;
/// The allegiance's name.
pub const SELF_NAME_ID: u32 = 0x1000_0251;
/// "Followers: N".
pub const SELF_FOLLOWERS_ID: u32 = 0x1000_0252;
/// The rank line.
pub const SELF_RANK_ID: u32 = 0x1000_0253;
/// The monarch block.
pub const MONARCH_FIELD_ID: u32 = 0x1000_0255;
/// "Monarch:" or "Patron/Monarch:".
pub const MONARCH_LABEL_ID: u32 = 0x1000_0256;
/// The monarch's name.
pub const MONARCH_NAME_ID: u32 = 0x1000_0257;
/// The monarch's followers.
pub const MONARCH_FOLLOWERS_ID: u32 = 0x1000_0258;
/// The patron block.
pub const PATRON_FIELD_ID: u32 = 0x1000_025A;
/// The patron's name.
pub const PATRON_NAME_ID: u32 = 0x1000_025C;
/// The vassal roster.
pub const VASSAL_LIST_ID: u32 = 0x1000_0260;
/// Ignore allegiance requests.
pub const IGNORE_REQUESTS_ID: u32 = 0x1000_0262;
/// Swear to the selection.
pub const SWEAR_BUTTON_ID: u32 = 0x1000_0263;
/// Break with the patron.
pub const BREAK_BUTTON_ID: u32 = 0x1000_0264;
/// Remove the selected vassal.
pub const KICK_BUTTON_ID: u32 = 0x1000_0265;
/// The monarch block's part shown when the monarch is the patron.
pub const MONARCH_IS_PATRON_ID: u32 = 0x1000_0490;
/// The experience passed up (in the monarch-is-patron part and the patron
/// block).
pub const EXPERIENCE_PASSED_UP_ID: u32 = 0x1000_0492;
/// A vassal row's name.
pub const ROW_NAME_ID: u32 = 0x1000_0268;
/// A vassal row's experience passed up.
pub const ROW_EXPERIENCE_ID: u32 = 0x1000_0269;
/// A vassal row's offline marker.
pub const ROW_OFFLINE_MARKER_ID: u32 = 0x1000_04AA;
/// The allegiance strings' table.
pub const STRING_TABLE_ID: u32 = 0x2300_0001;
/// The option captions' table.
pub const OPTION_STRING_TABLE_ID: u32 = 0x2300_0003;

const TEXT_COLOR: Color = Color::WHITE;
/// What a blanked name shows.
const BLANK: &str = " ";

/// What the page reads of the allegiance, borrowed for one update.
#[derive(Debug, Clone, Copy, Default)]
pub struct AllegianceSource<'a> {
    /// The allegiance the server described (`None`: no profile).
    pub profile: Option<&'a AllegianceProfile>,
    /// The player's rank in it.
    pub rank: u32,
    /// Moves on every change.
    pub revision: u64,
    /// The player's allegiance rank property (`None` when absent).
    pub rank_quality: Option<i32>,
}

#[derive(Debug, Clone, Copy)]
struct VassalRow {
    name: Option<ElementId>,
    experience: Option<ElementId>,
    offline: Option<ElementId>,
}

/// The allegiance page's controller.
#[derive(Debug)]
pub struct AllegiancePage {
    self_name: Option<ElementId>,
    self_followers: Option<ElementId>,
    self_rank: Option<ElementId>,
    monarch_field: ElementId,
    monarch_label: Option<ElementId>,
    monarch_name: Option<ElementId>,
    monarch_followers: Option<ElementId>,
    monarch_is_patron: Option<ElementId>,
    monarch_experience: Option<ElementId>,
    patron_field: ElementId,
    patron_name: Option<ElementId>,
    patron_experience: Option<ElementId>,
    list: Option<ElementId>,
    ignore_requests: Option<ElementId>,
    swear: Option<ElementId>,
    break_button: Option<ElementId>,
    kick: Option<ElementId>,
    monarch_caption: Option<String>,
    patron_slash_monarch_caption: Option<String>,
    rows: HashMap<u32, VassalRow>,
    row_owner: HashMap<ElementId, u32>,
    vassal_guids: HashSet<u32>,
    selected_vassal: u32,
    last_roster_revision: Option<u64>,
    subscribed: bool,
    ignore_option: bool,
    swear_question: Option<(u32, String)>,
    break_question: Option<(u32, String)>,
    kick_questions: HashMap<u32, String>,
    player: u32,
}

/// Rank title and name, as every member name on the page shows.
#[must_use]
pub fn full_name(member: &AllegianceMember) -> String {
    compose_full_name(
        i32::from(member.rank),
        i32::from(member.heritage_group),
        i32::from(member.gender),
        &member.name,
    )
}

/// The experience passed up: the number with its digits grouped, in the
/// authored entry when the string table has it.
#[must_use]
pub fn experience_passed_up_text(tithed: u32, strings: Option<&dyn PanelStrings>) -> String {
    let value = format_xp(u64::from(tithed));
    strings
        .and_then(|s| {
            s.template(
                STRING_TABLE_ID,
                compute_hash("ID_Allegiance_VassalExperiencePassedUp"),
                &HashMap::from([(compute_hash("VALUE"), value.clone())]),
            )
        })
        .unwrap_or(value)
}

/// The self block's rank line: the title from the player's own record, the
/// profile's rank in brackets, and the "(+n)" form when the player's rank
/// quality differs from it (an absent quality reads as 0).
#[must_use]
pub fn rank_line_text(
    profile_rank: u32,
    own: Option<&AllegianceMember>,
    rank_quality: Option<i32>,
    strings: Option<&dyn PanelStrings>,
) -> String {
    let profile_rank = i64::from(profile_rank);
    let title = own
        .and_then(|s| {
            title(
                i32::from(s.rank),
                i32::from(s.heritage_group),
                i32::from(s.gender),
            )
        })
        .unwrap_or("");
    let q = i64::from(rank_quality.unwrap_or(0));
    let template = |key: &str, vars: &[(&str, String)]| {
        strings.and_then(|s| {
            s.template(
                STRING_TABLE_ID,
                compute_hash(key),
                &vars
                    .iter()
                    .map(|(k, v)| (compute_hash(k), v.clone()))
                    .collect(),
            )
        })
    };
    if q != -1 && q != profile_rank {
        let (plain, delta) = (q.to_string(), (q - profile_rank).to_string());
        return template(
            "ID_Allegiance_RankBuffed",
            &[
                ("TITLE", title.to_owned()),
                ("RANK", plain.clone()),
                ("RANKBUFF", delta.clone()),
            ],
        )
        .unwrap_or_else(|| format!("Rank: {title} [{plain} (+{delta})]"));
    }
    let rank = profile_rank.to_string();
    template(
        "ID_Allegiance_Rank",
        &[("TITLE", title.to_owned()), ("RANK", rank.clone())],
    )
    .unwrap_or_else(|| format!("Rank: {title} [{rank}]"))
}

impl AllegiancePage {
    /// Bind to the allegiance page `page` (`None` without its monarch and
    /// patron blocks).
    pub fn bind(
        ui: &mut UiRoot,
        page: ElementId,
        strings: Option<&dyn PanelStrings>,
    ) -> Option<Self> {
        let monarch_field = ui.find_descendant(page, MONARCH_FIELD_ID)?;
        let patron_field = ui.find_descendant(page, PATRON_FIELD_ID)?;
        let monarch_is_patron = ui.find_descendant(monarch_field, MONARCH_IS_PATRON_ID);
        let monarch_experience =
            monarch_is_patron.and_then(|b| find_text(ui, b, EXPERIENCE_PASSED_UP_ID));
        let patron_experience = find_text(ui, patron_field, EXPERIENCE_PASSED_UP_ID);
        let list = ui
            .find_descendant(page, VASSAL_LIST_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some());
        if let Some(l) = list {
            link_list_scrollbar(ui, page, l);
        }
        let ignore_requests = find_button(ui, page, IGNORE_REQUESTS_ID);
        if let Some(b) = ignore_requests {
            let key = "ID_PlayerOption_IgnoreAllegianceRequests";
            let label = strings.and_then(|s| s.string(OPTION_STRING_TABLE_ID, compute_hash(key)));
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
                w.suppress_self_toggle = true;
            }
        }
        let caption =
            |key: &str| strings.and_then(|s| s.string(STRING_TABLE_ID, compute_hash(key)));
        Some(Self {
            self_name: find_text(ui, page, SELF_NAME_ID),
            self_followers: find_text(ui, page, SELF_FOLLOWERS_ID),
            self_rank: find_text(ui, page, SELF_RANK_ID),
            monarch_field,
            monarch_label: find_text(ui, monarch_field, MONARCH_LABEL_ID),
            monarch_name: find_text(ui, monarch_field, MONARCH_NAME_ID),
            monarch_followers: find_text(ui, monarch_field, MONARCH_FOLLOWERS_ID),
            monarch_is_patron,
            monarch_experience,
            patron_field,
            patron_name: find_text(ui, patron_field, PATRON_NAME_ID),
            patron_experience,
            list,
            ignore_requests,
            swear: find_button(ui, page, SWEAR_BUTTON_ID),
            break_button: find_button(ui, page, BREAK_BUTTON_ID),
            kick: find_button(ui, page, KICK_BUTTON_ID),
            monarch_caption: caption("ID_Allegiance_MonarchLabel"),
            patron_slash_monarch_caption: caption("ID_Allegiance_PatronSlashMonarchLabel"),
            rows: HashMap::new(),
            row_owner: HashMap::new(),
            vassal_guids: HashSet::new(),
            selected_vassal: 0,
            last_roster_revision: None,
            subscribed: false,
            ignore_option: false,
            kick_questions: HashMap::new(),
            swear_question: None,
            break_question: None,
            player: 0,
        })
    }

    /// The vassal selected on the page (0: none).
    #[must_use]
    pub fn selected_vassal(&self) -> u32 {
        self.selected_vassal
    }

    /// The page asked the server to keep the allegiance up to date.
    #[must_use]
    pub fn subscribed(&self) -> bool {
        self.subscribed
    }

    /// Show `view`'s allegiance.
    pub fn update(&mut self, ui: &mut UiRoot, view: &SocialView<'_>, rows: &mut dyn RowTemplates) {
        let source = view.allegiance;

        self.player = view.player;
        self.ignore_option = view.options.get(option_id::IGNORE_ALLEGIANCE_REQUESTS);
        let me = view.player;
        let monarch = source.profile.and_then(|p| p.monarch.as_ref());
        let patron = source.profile.and_then(|p| p.find_patron(me));
        let own = source.profile.and_then(|p| p.find_data(me));
        let strings = view.strings;

        // The player's block.
        set_line(
            ui,
            self.self_name,
            source.profile.map_or("", |p| p.allegiance_name.as_str()),
            TEXT_COLOR,
        );
        let total_vassals = source.profile.map_or(0, |p| p.total_vassals);
        set_line(
            ui,
            self.self_followers,
            &format!("Followers: {total_vassals}"),
            TEXT_COLOR,
        );
        set_line(
            ui,
            self.self_rank,
            &rank_line_text(source.rank, own, source.rank_quality, strings),
            TEXT_COLOR,
        );

        // The monarch's block.
        let tithed = own.map_or(0, |o| o.cp_tithed);
        let has_monarch = monarch.is_some_and(|m| m.character_id != me);
        ui.set_visible(self.monarch_field, has_monarch);
        if let Some(m) = monarch.filter(|_| has_monarch) {
            let total = source.profile.map_or(0, |p| p.total_members);
            set_line(ui, self.monarch_name, &full_name(m), TEXT_COLOR);
            set_line(
                ui,
                self.monarch_followers,
                &format!("Followers: {}", total.saturating_sub(1)),
                TEXT_COLOR,
            );
            ui.set_enabled(self.monarch_field, m.is_logged_in);
            let patron_is_monarch = patron.is_some_and(|p| p.character_id == m.character_id);
            let label = if patron_is_monarch {
                &self.patron_slash_monarch_caption
            } else {
                &self.monarch_caption
            };
            if let Some(label) = label.clone() {
                set_line(ui, self.monarch_label, &label, TEXT_COLOR);
            }
            if let Some(b) = self.monarch_is_patron {
                ui.set_visible(b, patron_is_monarch);
            }
            if patron_is_monarch {
                set_line(
                    ui,
                    self.monarch_experience,
                    &experience_passed_up_text(tithed, strings),
                    TEXT_COLOR,
                );
            }
        } else {
            set_line(ui, self.monarch_name, BLANK, TEXT_COLOR);
            set_line(ui, self.monarch_followers, BLANK, TEXT_COLOR);
            if let Some(b) = self.monarch_is_patron {
                ui.set_visible(b, false);
            }
        }

        // The patron's block.
        let has_patron =
            patron.is_some_and(|p| monarch.is_none_or(|m| p.character_id != m.character_id));
        ui.set_visible(self.patron_field, has_patron);
        if let Some(p) = patron.filter(|_| has_patron) {
            set_line(ui, self.patron_name, &full_name(p), TEXT_COLOR);
            ui.set_enabled(self.patron_field, p.is_logged_in);
            set_line(
                ui,
                self.patron_experience,
                &experience_passed_up_text(tithed, strings),
                TEXT_COLOR,
            );
        } else {
            set_line(ui, self.patron_name, BLANK, TEXT_COLOR);
        }

        if self.last_roster_revision != Some(source.revision) {
            self.last_roster_revision = Some(source.revision);
            self.refresh_roster(ui, source, me, strings, rows);
        }
        if let Some(b) = self.ignore_requests {
            let on = self.ignore_option;
            ui.with::<Button, _>(b, |w, ui| w.set_selected(ui, b, on));
        }
        self.break_question = patron.map(|p| {
            (
                p.character_id,
                question(strings, "ID_Allegiance_BreakConfirmation", &p.name),
            )
        });
        self.swear_question = view.selected.and_then(|target| {
            let name = view.name_of.and_then(|f| f(target))?;
            (!name.is_empty()).then(|| {
                (
                    target,
                    question(strings, "ID_Allegiance_SwearConfirmation", &name),
                )
            })
        });
        let target_valid = view
            .selected
            .is_some_and(|id| id != me && source.profile.and_then(|p| p.find_data(id)).is_none());
        self.refresh_buttons(ui, patron.is_some(), target_valid);
    }

    fn refresh_buttons(&self, ui: &mut UiRoot, has_patron: bool, target_valid: bool) {
        if let Some(b) = self.swear {
            ui.set_enabled(b, !has_patron && target_valid);
        }
        if let Some(b) = self.break_button {
            ui.set_enabled(b, has_patron);
        }
        if let Some(b) = self.kick {
            ui.set_enabled(b, self.selected_vassal != 0);
        }
    }

    fn refresh_roster(
        &mut self,
        ui: &mut UiRoot,
        source: super::allegiance::AllegianceSource<'_>,
        me: u32,
        strings: Option<&dyn PanelStrings>,
        templates: &mut dyn RowTemplates,
    ) {
        let Some(list) = self.list else {
            return;
        };
        let vassals: Vec<&AllegianceMember> = source
            .profile
            .map(|p| p.find_vassals(me).collect())
            .unwrap_or_default();
        let changed = vassals.len() != self.vassal_guids.len()
            || vassals
                .iter()
                .any(|v| !self.vassal_guids.contains(&v.character_id));
        if changed {
            ui.with::<ListBox, _>(list, |b, ui| b.flush_preserving_scroll(ui));
            self.rows.clear();
            self.row_owner.clear();
            self.vassal_guids = vassals.iter().map(|v| v.character_id).collect();
            let template = ui
                .widget::<ListBox>(list)
                .and_then(|b| b.templates.first().copied());
            for v in &vassals {
                let Some(t) = template else {
                    break;
                };
                let Some(row) = templates.build_row(ui, t.layout, t.element) else {
                    continue;
                };
                ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
                let widgets = VassalRow {
                    name: find_text(ui, row, ROW_NAME_ID),
                    experience: find_text(ui, row, ROW_EXPERIENCE_ID),
                    offline: ui.find_descendant(row, ROW_OFFLINE_MARKER_ID),
                };
                if let Some(n) = widgets.name {
                    bind_click(ui, n);
                    self.row_owner.insert(n, v.character_id);
                }
                self.rows.insert(v.character_id, widgets);
            }
        }
        self.kick_questions = vassals
            .iter()
            .map(|v| {
                (
                    v.character_id,
                    question(strings, "ID_Allegiance_KickConfirmation", &v.name),
                )
            })
            .collect();
        for v in &vassals {
            self.update_row(ui, v, strings);
        }
        if self.selected_vassal != 0 && !self.vassal_guids.contains(&self.selected_vassal) {
            self.selected_vassal = 0;
        }
    }

    fn update_row(
        &self,
        ui: &mut UiRoot,
        vassal: &AllegianceMember,
        strings: Option<&dyn PanelStrings>,
    ) {
        let Some(w) = self.rows.get(&vassal.character_id).copied() else {
            return;
        };
        set_line(ui, w.name, &full_name(vassal), TEXT_COLOR);
        set_line(
            ui,
            w.experience,
            &experience_passed_up_text(vassal.cp_tithed, strings),
            TEXT_COLOR,
        );
        if let Some(m) = w.offline {
            ui.set_visible(m, !vassal.is_logged_in);
        }
    }

    /// Ask the server to keep the allegiance up to date (or stop) as the
    /// page shows or hides; a request counts only in the world.
    pub fn set_page_visible(&mut self, ui: &mut UiRoot, visible: bool, in_world: bool) {
        if self.subscribed == visible {
            return;
        }
        if in_world {
            allegiance(ui, AllegianceCommand::Subscribe(visible));
            self.subscribed = visible;
        }
    }

    /// The session ended: the next request is sent again.
    pub fn reset_page_visible_latch(&mut self) {
        self.subscribed = false;
    }

    /// The character entered the world: ask for updates again, whether or
    /// not the page shows.
    pub fn redeclare_after_world_entry(&mut self, ui: &mut UiRoot) {
        allegiance(ui, AllegianceCommand::Subscribe(true));
        self.subscribed = true;
    }

    /// The question asked before swearing to the selection (target and
    /// text), as of the last update.
    #[must_use]
    pub fn swear_question(&self) -> Option<&(u32, String)> {
        self.swear_question.as_ref()
    }

    /// Handle a click on `element`; true when it is the page's. Swear,
    /// break and kick ask first ([`PanelOutput::Confirm`]).
    pub fn on_click(&mut self, ui: &mut UiRoot, element: ElementId) -> bool {
        if let Some(&guid) = self.row_owner.get(&element) {
            // The kick button follows at the next update.
            self.selected_vassal = guid;
            return true;
        }
        if Some(element) == self.swear {
            if let Some((target, message)) = self.swear_question.clone() {
                confirm(
                    ui,
                    message,
                    AllegianceCommand::Swear(ObjectRef::Guid(target)),
                );
            }
            return true;
        }
        if Some(element) == self.break_button {
            if let Some((guid, message)) = self.break_question.clone() {
                confirm(ui, message, AllegianceCommand::Break(ObjectRef::Guid(guid)));
            }
            return true;
        }
        if Some(element) == self.kick {
            let vassal = self.selected_vassal;
            if vassal != 0
                && let Some(message) = self.kick_questions.get(&vassal).cloned()
            {
                confirm(
                    ui,
                    message,
                    AllegianceCommand::Kick(ObjectRef::Guid(vassal)),
                );
            }
            return true;
        }
        if Some(element) == self.ignore_requests {
            let next = !self.ignore_option;
            self.ignore_option = next;
            emit(
                ui,
                PanelOutput::Session(SessionCommand::SetCharacterOption {
                    option: option_id::IGNORE_ALLEGIANCE_REQUESTS,
                    value: next,
                }),
            );
            return true;
        }
        false
    }
}

/// The confirmation text for `key` naming `name` (the bare name without
/// the authored entry).
#[must_use]
pub fn question(strings: Option<&dyn PanelStrings>, key: &str, name: &str) -> String {
    strings
        .and_then(|s| {
            s.template(
                STRING_TABLE_ID,
                compute_hash(key),
                &HashMap::from([(PLAYER_VARIABLE, name.to_owned())]),
            )
        })
        .unwrap_or_else(|| name.to_owned())
}

fn confirm(ui: &mut UiRoot, message: String, on_yes: AllegianceCommand) {
    emit(
        ui,
        PanelOutput::Confirm {
            message,
            on_yes: Box::new(PanelOutput::Session(SessionCommand::Gameplay(
                GameplayCommand::Allegiance(on_yes),
            ))),
        },
    );
}

fn allegiance(ui: &mut UiRoot, c: AllegianceCommand) {
    emit(
        ui,
        PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Allegiance(c))),
    );
}
