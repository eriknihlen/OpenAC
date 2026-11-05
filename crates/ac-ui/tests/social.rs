//! The social panel: its tabs and the allegiance, fellowship, friends and
//! squelch pages, and the allegiance rank titles.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/SocialPanelControllerTests.cs`,
//! `SocialFellowshipPageControllerTests.cs`, `AllegianceRankTitleTableTests.cs`,
//! `FellowshipStatsTemplateLiveDatTests.cs` and
//! `AllegianceExperiencePassedUpTemplateLiveDatTests.cs`, on a synthetic
//! panel shaped like the retail one (layout 0x2100006E slot 0x1000018F; the
//! real one is imported in `real_dats`, OpenAC's fixture
//! `social_panel_2100006E_1000018F.json`). OpenAC's bindings are the
//! [`SocialView`] handed to each update; the commands its delegates send
//! are `SessionCommand`s; `ShowConfirmation` is `PanelOutput::Confirm`; its
//! `SelectionState` is the view's selection, and a page's own selection is
//! `PanelOutput::SelectObject`. The allegiance snapshots are a real
//! `AllegianceProfile` (the tree decides patron and vassals). A declaration
//! "accepted" in OpenAC is one made in the world here.

mod common;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use ac_core::social::{
    FriendEntry, FriendsState, FriendsUpdate, FriendsUpdateType, SquelchDatabase, SquelchInfo,
    SquelchState,
};
use ac_proto::allegiance::{AllegianceMember, AllegianceProfile};
use ac_runtime::character::{CharacterOptions, option_id};
use ac_runtime::gameplay::{
    AllegianceCommand, FellowView, FellowshipCommand, FellowshipView, GameplayCommand,
};
use ac_runtime::inventory::ObjectRef;
use ac_runtime::session::SessionCommand;
use ac_ui::ElementId;
use ac_ui::element_info::{ElementInfo, TabEntry, TemplateEntry};
use ac_ui::event::kind;
use ac_ui::host::UiHost;
use ac_ui::import::ImportedLayout;
use ac_ui::panels::social::rank_titles::{compose_full_name, title};
use ac_ui::panels::social::{
    self, AllegianceSource, SocialPanelController, SocialView, allegiance, fellowship, friends,
};
use ac_ui::panels::{PanelOutput, PanelStrings, panel_id};
use ac_ui::root::UiRoot;
use ac_ui::strings::compute_hash;
use ac_ui::tree::Element;
use ac_ui::widgets::{Button, Field, ListBox, Meter, Panel, Scrollbar, TabPanel, Text};
use common::items::*;
use common::panels::*;

const WINDOW: &str = "social";
const SELF: u32 = 100;
const FELLOW_TEMPLATE: TemplateEntry = TemplateEntry {
    layout: 0x2100_0030,
    element: 0x1000_0281,
};
const VASSAL_TEMPLATE: TemplateEntry = TemplateEntry {
    layout: 0x2100_0031,
    element: 0x1000_0266,
};
const FRIEND_TEMPLATE: TemplateEntry = TemplateEntry {
    layout: 0x2100_0032,
    element: 0x1000_0519,
};
const SQUELCH_TEMPLATE: TemplateEntry = TemplateEntry {
    layout: 0x2100_0033,
    element: 0x1000_053F,
};

macro_rules! assert_dbg_eq {
    ($a:expr, $b:expr $(,)?) => {
        assert_eq!(format!("{:?}", $a), format!("{:?}", $b))
    };
}

// ── The synthetic panel ─────────────────────────────────────────────────

fn tagged(ui: &mut UiRoot, parent: ElementId, id: u32, w: f32, h: f32) -> ElementId {
    let mut e = Element::at(0.0, 0.0, w, h);
    e.dat_element_id = id;
    ui.add(parent, e, Panel::default())
}

fn text(ui: &mut UiRoot, parent: ElementId, id: u32) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 200.0, 16.0);
    e.dat_element_id = id;
    Text::configure(&mut e);
    ui.add(parent, e, Text::default())
}

fn list_box(
    ui: &mut UiRoot,
    parent: ElementId,
    id: u32,
    template: TemplateEntry,
    scrollbar: u32,
) -> ElementId {
    let info = Arc::new(ElementInfo {
        id,
        kind: 5,
        width: 270.0,
        height: 200.0,
        template_list: vec![template],
        scrollbar_element_id: scrollbar,
        ..ElementInfo::default()
    });
    let mut e = Element::at(0.0, 0.0, 270.0, 200.0);
    e.dat_element_id = id;
    let l = ui.add(parent, e, ListBox::new(info));
    if scrollbar != 0 {
        let mut e = Element::at(270.0, 0.0, 16.0, 200.0);
        e.dat_element_id = scrollbar;
        ui.add(parent, e, Scrollbar::default());
    }
    l
}

fn field(ui: &mut UiRoot, parent: ElementId, id: u32) -> ElementId {
    let mut e = Element::at(0.0, 0.0, 200.0, 16.0);
    e.dat_element_id = id;
    Field::configure(&mut e);
    ui.add(parent, e, Field::default())
}

fn button(ui: &mut UiRoot, parent: ElementId, id: u32) {
    let mut ids = HashMap::new();
    add_std_button(ui, &mut ids, parent, id);
}

fn build_panel(ui: &mut UiRoot) -> ImportedLayout {
    let tabs = vec![
        TabEntry {
            button: 0x1000_028C,
            page: allegiance::PAGE_ID,
            is_default: true,
        },
        TabEntry {
            button: 0x1000_028E,
            page: fellowship::PAGE_ID,
            is_default: false,
        },
        TabEntry {
            button: 0x1000_0512,
            page: friends::FRIENDS_PAGE_ID,
            is_default: false,
        },
        TabEntry {
            button: 0x1000_053B,
            page: friends::SQUELCH_PAGE_ID,
            is_default: false,
        },
    ];
    let info = Arc::new(ElementInfo {
        id: social::SLOT_ELEMENT_ID,
        kind: 0x10,
        width: 300.0,
        height: 450.0,
        tab_table: tabs.clone(),
        ..ElementInfo::default()
    });
    let r = ui.root();
    let mut e = Element::at(0.0, 0.0, 300.0, 450.0);
    e.dat_element_id = social::SLOT_ELEMENT_ID;
    let root = ui.add(r, e, TabPanel::new(info));
    for t in &tabs {
        button(ui, root, t.button);
    }
    button(ui, root, social::CLOSE_BUTTON_ID);

    // Allegiance.
    let page = tagged(ui, root, allegiance::PAGE_ID, 300.0, 420.0);
    for id in [
        allegiance::SELF_NAME_ID,
        allegiance::SELF_FOLLOWERS_ID,
        allegiance::SELF_RANK_ID,
    ] {
        text(ui, page, id);
    }
    let monarch = tagged(ui, page, allegiance::MONARCH_FIELD_ID, 280.0, 60.0);
    for id in [
        allegiance::MONARCH_LABEL_ID,
        allegiance::MONARCH_NAME_ID,
        allegiance::MONARCH_FOLLOWERS_ID,
    ] {
        text(ui, monarch, id);
    }
    let sub = tagged(ui, monarch, allegiance::MONARCH_IS_PATRON_ID, 280.0, 20.0);
    text(ui, sub, allegiance::EXPERIENCE_PASSED_UP_ID);
    let patron = tagged(ui, page, allegiance::PATRON_FIELD_ID, 280.0, 60.0);
    text(ui, patron, allegiance::PATRON_NAME_ID);
    text(ui, patron, allegiance::EXPERIENCE_PASSED_UP_ID);
    list_box(
        ui,
        page,
        allegiance::VASSAL_LIST_ID,
        VASSAL_TEMPLATE,
        0x1000_0261,
    );
    for id in [
        allegiance::IGNORE_REQUESTS_ID,
        allegiance::SWEAR_BUTTON_ID,
        allegiance::BREAK_BUTTON_ID,
        allegiance::KICK_BUTTON_ID,
    ] {
        button(ui, page, id);
    }

    // Fellowship.
    let page = tagged(ui, root, fellowship::PAGE_ID, 300.0, 420.0);
    tagged(
        ui,
        page,
        fellowship::NOT_IN_FELLOWSHIP_FRAME_ID,
        300.0,
        400.0,
    );
    tagged(ui, page, fellowship::IN_FELLOWSHIP_FRAME_ID, 300.0, 400.0);
    field(ui, page, fellowship::NAME_ENTRY_ID);
    text(ui, page, fellowship::FELLOWSHIP_NAME_ID);
    list_box(
        ui,
        page,
        fellowship::LIST_BOX_ID,
        FELLOW_TEMPLATE,
        0x1000_027A,
    );
    for id in [
        fellowship::CREATE_BUTTON_ID,
        fellowship::LEADER_BUTTON_ID,
        fellowship::QUIT_BUTTON_ID,
        fellowship::OPEN_BUTTON_ID,
        fellowship::RECRUIT_BUTTON_ID,
        fellowship::DISMISS_BUTTON_ID,
        fellowship::DISBAND_BUTTON_ID,
        fellowship::IGNORE_REQUESTS_ID,
        fellowship::AUTO_ACCEPT_ID,
        fellowship::SHARE_XP_ID,
        fellowship::SHARE_LOOT_ID,
    ] {
        button(ui, page, id);
    }

    // Friends.
    let page = tagged(ui, root, friends::FRIENDS_PAGE_ID, 300.0, 420.0);
    list_box(
        ui,
        page,
        friends::FRIENDS_LIST_ID,
        FRIEND_TEMPLATE,
        0x1000_0518,
    );
    field(ui, page, friends::FRIEND_NAME_FIELD_ID);
    for id in [
        friends::ADD_FRIEND_BUTTON_ID,
        friends::REMOVE_FRIEND_BUTTON_ID,
        friends::APPEAR_OFFLINE_ID,
    ] {
        button(ui, page, id);
    }

    // Squelch.
    let page = tagged(ui, root, friends::SQUELCH_PAGE_ID, 300.0, 420.0);
    list_box(
        ui,
        page,
        friends::SQUELCH_LIST_ID,
        SQUELCH_TEMPLATE,
        0x1000_0543,
    );
    field(ui, page, friends::SQUELCH_NAME_FIELD_ID);
    for id in [
        friends::REMOVE_SQUELCH_BUTTON_ID,
        friends::SQUELCH_CHARACTER_BUTTON_ID,
        friends::SQUELCH_ACCOUNT_BUTTON_ID,
    ] {
        button(ui, page, id);
    }
    ui.set_visible(root, false);
    ImportedLayout::from_parts(root, HashMap::new())
}

/// OpenAC's fake row resolvers: one row shape per list.
fn rows(ui: &mut UiRoot, layout: u32, element: u32) -> Option<ElementId> {
    let mut e = Element::at(0.0, 0.0, 270.0, 24.0);
    e.dat_element_id = element;
    let row = ui.insert(e, Panel::default());
    match layout {
        l if l == FELLOW_TEMPLATE.layout => {
            text(ui, row, fellowship::ROW_NAME_ID);
            text(ui, row, fellowship::ROW_STATS_ID);
            for id in [
                fellowship::ROW_HEALTH_ID,
                fellowship::ROW_STAMINA_ID,
                fellowship::ROW_MANA_ID,
            ] {
                let mut e = Element::at(0.0, 0.0, 50.0, 4.0);
                e.dat_element_id = id;
                ui.add(row, e, Meter::default());
            }
        }
        l if l == VASSAL_TEMPLATE.layout => {
            text(ui, row, allegiance::ROW_NAME_ID);
            text(ui, row, allegiance::ROW_EXPERIENCE_ID);
            tagged(ui, row, allegiance::ROW_OFFLINE_MARKER_ID, 8.0, 8.0);
        }
        l if l == FRIEND_TEMPLATE.layout => {
            text(ui, row, friends::FRIEND_ROW_NAME_ID);
        }
        _ => {
            // The squelch row: a text holding an inner text.
            let outer = text(ui, row, 0);
            text(ui, outer, 0);
        }
    }
    Some(row)
}

fn member(guid: u32, parent: u32, name: &str) -> AllegianceMember {
    AllegianceMember {
        character_id: guid,
        parent_guid: parent,
        is_logged_in: true,
        name: name.to_owned(),
        rank: 0,
        level: 0,
        loyalty: 0,
        leadership: 0,
        cp_cached: 0,
        cp_tithed: 0,
        gender: 0,
        heritage_group: 0,
        may_passup_experience: false,
    }
}

fn fellow(guid: u32, name: &str, level: u32) -> FellowView {
    FellowView {
        guid,
        name: name.to_owned(),
        level,
        health: (100, 100),
        stamina: (80, 80),
        mana: (60, 60),
        share_loot: false,
        vitals_age_seconds: None,
    }
}

fn options() -> CharacterOptions {
    let mut o = CharacterOptions::new();
    for id in [
        option_id::IGNORE_FELLOWSHIP_REQUESTS,
        option_id::FELLOWSHIP_AUTO_ACCEPT_REQUESTS,
        option_id::FELLOWSHIP_SHARE_XP,
        option_id::FELLOWSHIP_SHARE_LOOT,
        option_id::IGNORE_ALLEGIANCE_REQUESTS,
        option_id::APPEAR_OFFLINE,
    ] {
        o.set_bit(id, false);
    }
    o
}

struct Social {
    host: UiHost,
    root: ElementId,
    options: CharacterOptions,
    friends: FriendsState,
    squelch: SquelchState,
    fellowship: FellowshipView,
    profile: Option<AllegianceProfile>,
    rank: u32,
    revision: u64,
    rank_quality: Option<i32>,
    selected: Option<u32>,
    in_world: bool,
    strings: Option<FakeStrings>,
    names: HashMap<u32, String>,
    to_raise: Option<fn(u32) -> i64>,
    builds: usize,
    fail_rows: bool,
}

fn social() -> Social {
    social_with(None)
}

fn social_with(bind_strings: Option<&FakeStrings>) -> Social {
    let mut host = UiHost::new();
    host.root.set_size(1024.0, 768.0);
    let layout = build_panel(&mut host.root);
    host.root
        .register_window(WINDOW, layout.root, None, 0)
        .unwrap();
    let s: Option<&dyn PanelStrings> = bind_strings.map(|s| s as &dyn PanelStrings);
    let c = SocialPanelController::bind(&mut host.root, &layout, s).expect("binds");
    host.attach_controller(WINDOW, Box::new(c)).unwrap();
    let mut s = Social {
        host,
        root: layout.root,
        options: options(),
        friends: FriendsState::new(),
        squelch: SquelchState::new(),
        fellowship: FellowshipView::default(),
        profile: None,
        rank: 0,
        revision: 0,
        rank_quality: None,
        selected: None,
        in_world: true,
        strings: None,
        names: HashMap::new(),
        to_raise: None,
        builds: 0,
        fail_rows: false,
    };
    s.update();
    s
}

impl Social {
    fn update(&mut self) {
        let names = self.names.clone();
        let name_of = move |g: u32| names.get(&g).cloned();
        let to_raise = self.to_raise;
        let raise = move |l: u32| to_raise.map_or(0, |f| f(l));
        let view = SocialView {
            player: SELF,
            selected: self.selected,
            in_world: self.in_world,
            options: &self.options,
            friends: &self.friends,
            squelch: &self.squelch,
            fellowship: &self.fellowship,
            allegiance: AllegianceSource {
                profile: self.profile.as_ref(),
                rank: self.rank,
                revision: self.revision,
                rank_quality: self.rank_quality,
            },
            experience_to_raise: self
                .to_raise
                .is_some()
                .then_some(&raise as &dyn Fn(u32) -> i64),
            strings: self.strings.as_ref().map(|s| s as &dyn PanelStrings),
            name_of: Some(&name_of),
        };
        let builds = &mut self.builds;
        let fail = self.fail_rows;
        let mut templates = |ui: &mut UiRoot, l: u32, e: u32| {
            *builds += 1;
            if fail { None } else { rows(ui, l, e) }
        };
        self.host
            .with_controller::<SocialPanelController, _>(WINDOW, |c, ui| {
                c.update(ui, &view, &mut templates);
            })
            .unwrap();
    }

    fn with<R>(&mut self, f: impl FnOnce(&mut SocialPanelController, &mut UiRoot) -> R) -> R {
        self.host
            .with_controller::<SocialPanelController, _>(WINDOW, f)
            .unwrap()
    }

    fn find(&self, id: u32) -> ElementId {
        self.host.root.find_descendant(self.root, id).unwrap()
    }

    fn find_in(&self, scope: u32, id: u32) -> ElementId {
        let s = self.find(scope);
        self.host.root.find_descendant(s, id).unwrap()
    }

    fn visible(&self, id: u32) -> bool {
        self.host.root.element(self.find(id)).unwrap().visible
    }

    fn enabled(&self, id: u32) -> bool {
        self.host.root.element(self.find(id)).unwrap().enabled
    }

    fn text_in(&self, scope: u32, id: u32) -> String {
        text_of(&self.host.root, self.find_in(scope, id))
    }

    fn click(&mut self, element: ElementId) -> Vec<PanelOutput> {
        panel_outputs(&mut self.host);
        self.host.root.fire_event(kind::CLICK, element, None);
        self.host.pump();
        panel_outputs(&mut self.host)
    }

    fn click_id(&mut self, id: u32) -> Vec<PanelOutput> {
        let e = self.find(id);
        self.click(e)
    }

    fn rows_of(&self, list: u32) -> Vec<ElementId> {
        let l = self.find(list);
        let v = self.host.root.widget::<ListBox>(l).unwrap().viewport_id();
        v.map(|v| self.host.root.children(v)).unwrap_or_default()
    }

    fn show(&mut self) {
        self.host.root.show_window(WINDOW);
        self.host.pump();
    }

    fn hide(&mut self) {
        self.host.root.hide_window(WINDOW);
        self.host.pump();
    }
}

fn gameplay(outputs: &[PanelOutput]) -> Vec<GameplayCommand> {
    sessions(outputs)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Gameplay(g) => Some(g),
            _ => None,
        })
        .collect()
}

fn fellowship_commands(outputs: &[PanelOutput]) -> Vec<FellowshipCommand> {
    gameplay(outputs)
        .into_iter()
        .filter_map(|c| match c {
            GameplayCommand::Fellowship(f) => Some(f),
            _ => None,
        })
        .collect()
}

fn allegiance_commands(outputs: &[PanelOutput]) -> Vec<AllegianceCommand> {
    gameplay(outputs)
        .into_iter()
        .filter_map(|c| match c {
            GameplayCommand::Allegiance(a) => Some(a),
            _ => None,
        })
        .collect()
}

/// The confirmation asked and the command its yes sends.
fn confirmation(outputs: &[PanelOutput]) -> Option<(String, AllegianceCommand)> {
    outputs.iter().find_map(|o| match o {
        PanelOutput::Confirm { message, on_yes } => match on_yes.as_ref() {
            PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Allegiance(a))) => {
                Some((message.clone(), a.clone()))
            }
            _ => None,
        },
        _ => None,
    })
}

// ── The panel ───────────────────────────────────────────────────────────

// SocialPanelControllerTests.Bind_Succeeds_AndActivateTabs_SelectsTheAuthoredDefault
#[test]
fn activating_the_tabs_shows_the_authored_default_allegiance_page() {
    let mut s = social();
    s.with(|c, ui| {
        c.activate_tabs(ui);
        assert!(c.is_showing_allegiance(ui));
        assert!(!c.is_showing_fellowship(ui));
        let t = ui.widget::<TabPanel>(c.tab_panel()).unwrap();
        assert!(t.unresolved().is_empty());
    });
}

// ShowFellowship_SwitchesTheActiveTab
#[test]
fn show_fellowship_switches_the_active_tab() {
    let mut s = social();
    s.with(|c, ui| {
        c.activate_tabs(ui);
        c.show_fellowship(ui);
        assert!(c.is_showing_fellowship(ui));
        assert!(!c.is_showing_allegiance(ui));
    });
}

// CloseButton_InvokesToggleCallback
#[test]
fn the_close_button_closes_the_social_panel() {
    let mut s = social();
    let out = s.click_id(social::CLOSE_BUTTON_ID);
    assert_dbg_eq!(out, [PanelOutput::ClosePanel(panel_id::SOCIAL)]);
}

// ── Fellowship page ─────────────────────────────────────────────────────

// Fellowship_NoFellowship_ShowsNotInFellowshipFrame / Bind_NotInFellowship_ShowsEmptyFrame
#[test]
fn outside_a_fellowship_the_empty_frame_shows() {
    let s = social();
    assert!(s.visible(fellowship::NOT_IN_FELLOWSHIP_FRAME_ID));
    assert!(!s.visible(fellowship::IN_FELLOWSHIP_FRAME_ID));
}

// Fellowship_HasFellowship_ShowsInFellowshipFrame / Bind_InFellowship_ShowsPopulatedFrame
#[test]
fn in_a_fellowship_the_populated_frame_shows() {
    let mut s = social();
    s.fellowship.in_fellowship = true;
    s.update();
    assert!(!s.visible(fellowship::NOT_IN_FELLOWSHIP_FRAME_ID));
    assert!(s.visible(fellowship::IN_FELLOWSHIP_FRAME_ID));
}

fn in_fellowship(s: &mut Social, members: Vec<FellowView>) {
    s.fellowship.in_fellowship = true;
    s.fellowship.revision += 1;
    s.fellowship.members = members;
}

// Tick_BuildsOneRowPerMember_WithNameLevelAndVitals
#[test]
fn one_row_per_member_with_name_level_and_vitals() {
    let mut s = social();
    s.fellowship.share_xp = true;
    s.fellowship.even_xp_split = true;
    let mut alice = fellow(0x5000_0001, "Alice", 12);
    alice.health = (55, 100);
    alice.mana = (10, 60);
    in_fellowship(&mut s, vec![alice]);
    s.update();
    let rows = s.rows_of(fellowship::LIST_BOX_ID);
    assert_eq!(rows.len(), 1);
    let ui = &s.host.root;
    let name = ui
        .find_descendant(rows[0], fellowship::ROW_NAME_ID)
        .unwrap();
    assert_eq!(text_of(ui, name), "Alice");
    let stats = ui
        .find_descendant(rows[0], fellowship::ROW_STATS_ID)
        .unwrap();
    // A one-member even split is the whole share.
    assert_eq!(text_of(ui, stats), "12/100%");
    let health = ui
        .find_descendant(rows[0], fellowship::ROW_HEALTH_ID)
        .unwrap();
    let m = ui.widget::<Meter>(health).unwrap();
    assert!((m.fill.unwrap() - 0.55).abs() < 1e-3);
    assert_eq!(m.label.as_deref(), Some("55/100"));
}

// Tick_SameMemberSet_UpdatesRowsInPlace_NoRebuild
#[test]
fn the_same_members_update_their_rows_in_place() {
    let mut s = social();
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    s.update();
    let first = s.rows_of(fellowship::LIST_BOX_ID);
    let mut alice = fellow(0x5000_0001, "Alice", 12);
    alice.health = (10, 100);
    in_fellowship(&mut s, vec![alice]);
    s.update();
    let now = s.rows_of(fellowship::LIST_BOX_ID);
    assert_eq!(first, now);
    let health = s
        .host
        .root
        .find_descendant(now[0], fellowship::ROW_HEALTH_ID)
        .unwrap();
    let fill = s.host.root.widget::<Meter>(health).unwrap().fill.unwrap();
    assert!((fill - 0.10).abs() < 1e-3);
}

// Tick_MembershipChange_RebuildsRoster
#[test]
fn a_membership_change_rebuilds_the_roster() {
    let mut s = social();
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    s.update();
    in_fellowship(
        &mut s,
        vec![
            fellow(0x5000_0001, "Alice", 12),
            fellow(0x5000_0002, "Bob", 10),
        ],
    );
    s.update();
    assert_eq!(s.rows_of(fellowship::LIST_BOX_ID).len(), 2);
}

// Tick_MemberLeaves_ShrinksRoster_AndClearsSelectionIfTheyWereSelected
#[test]
fn a_member_leaving_shrinks_the_roster_and_drops_their_selection() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    in_fellowship(
        &mut s,
        vec![fellow(SELF, "Alice", 12), fellow(0x5000_0002, "Bob", 10)],
    );
    s.update();
    let bob = s.rows_of(fellowship::LIST_BOX_ID)[1];
    let bob_name = s
        .host
        .root
        .find_descendant(bob, fellowship::ROW_NAME_ID)
        .unwrap();
    let out = s.click(bob_name);
    assert_dbg_eq!(out, [PanelOutput::SelectObject(0x5000_0002)]);
    s.update();
    let out = s.click_id(fellowship::DISMISS_BUTTON_ID);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::Dismiss(ObjectRef::Guid(0x5000_0002))]
    );
    in_fellowship(&mut s, vec![fellow(SELF, "Alice", 12)]);
    s.update();
    assert_eq!(s.rows_of(fellowship::LIST_BOX_ID).len(), 1);
    assert_eq!(s.with(|c, _| c.fellowship().unwrap().selected_fellow()), 0);
    assert!(!s.enabled(fellowship::DISMISS_BUTTON_ID));
}

// Tick_RowTemplatePermanentlyFailsToBuild_DoesNotRetryOnEveryVitalsTick
#[test]
fn a_row_that_never_builds_is_not_retried_on_every_vitals_tick() {
    let mut s = social();
    s.fail_rows = true;
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    s.update();
    assert!(s.rows_of(fellowship::LIST_BOX_ID).is_empty());
    let after_first = s.builds;
    assert!(after_first >= 1);
    s.fellowship.revision += 1;
    s.update();
    s.fellowship.revision += 1;
    s.update();
    assert_eq!(s.builds, after_first);
}

// RecruitButton_TargetIsAnExistingFellowWhoseRowFailedToBuild_StaysDisabled
#[test]
fn recruit_stays_disabled_for_a_fellow_whose_row_failed() {
    let mut s = social();
    s.fail_rows = true;
    s.fellowship.leader_guid = SELF;
    s.selected = Some(0x5000_0001);
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    s.update();
    assert!(!s.enabled(fellowship::RECRUIT_BUTTON_ID));
}

// WorldSelectionOfAFellow_EnablesDismissAndLeader_WithoutClickingTheirRow
#[test]
fn selecting_a_fellow_in_the_world_enables_dismiss_and_leader() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    in_fellowship(
        &mut s,
        vec![fellow(SELF, "Me", 20), fellow(0x5000_0001, "Alice", 12)],
    );
    s.update();
    assert!(!s.enabled(fellowship::DISMISS_BUTTON_ID));
    assert!(!s.enabled(fellowship::LEADER_BUTTON_ID));
    s.selected = Some(0x5000_0001);
    s.update();
    assert!(s.enabled(fellowship::DISMISS_BUTTON_ID));
    assert!(s.enabled(fellowship::LEADER_BUTTON_ID));
}

// WorldSelectionOfANonFellow_DoesNotClearAnExistingPanelSelection
#[test]
fn selecting_a_stranger_keeps_the_pages_selection() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    in_fellowship(
        &mut s,
        vec![fellow(SELF, "Me", 20), fellow(0x5000_0001, "Alice", 12)],
    );
    s.update();
    let alice = s.rows_of(fellowship::LIST_BOX_ID)[1];
    let name = s
        .host
        .root
        .find_descendant(alice, fellowship::ROW_NAME_ID)
        .unwrap();
    s.click(name);
    s.update();
    assert!(s.enabled(fellowship::DISMISS_BUTTON_ID));
    s.selected = Some(0x9999_9999);
    s.update();
    assert!(s.enabled(fellowship::DISMISS_BUTTON_ID));
}

fn open_close_strings() -> FakeStrings {
    let mut st = FakeStrings::default();
    st.plain.insert(
        (
            fellowship::FELLOWSHIP_STRING_TABLE_ID,
            compute_hash("ID_Fellowship_OpenFellowshipButtonText"),
        ),
        "Open".into(),
    );
    st.plain.insert(
        (
            fellowship::FELLOWSHIP_STRING_TABLE_ID,
            compute_hash("ID_Fellowship_CloseFellowshipButtonText"),
        ),
        "Close".into(),
    );
    st
}

// OpenButton_Click_FlipsCaptionImmediately_NotWaitingForTheNextTick / OpenButton_Click_TogglesTheCurrentOpenState
#[test]
fn the_open_button_toggles_and_flips_its_caption_at_once() {
    let st = open_close_strings();
    let mut s = social_with(Some(&st));
    s.fellowship.leader_guid = SELF;
    in_fellowship(&mut s, vec![fellow(SELF, "Me", 20)]);
    s.update();
    let b = s.find(fellowship::OPEN_BUTTON_ID);
    assert_eq!(
        s.host.root.widget::<Button>(b).unwrap().label.as_deref(),
        Some("Open")
    );
    let out = s.click(b);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::SetOpen(true)]
    );
    assert_eq!(
        s.host.root.widget::<Button>(b).unwrap().label.as_deref(),
        Some("Close")
    );
}

// Tick_NotInFellowship_ClearsAnyStaleRoster
#[test]
fn leaving_the_fellowship_clears_the_roster() {
    let mut s = social();
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    s.update();
    assert_eq!(s.rows_of(fellowship::LIST_BOX_ID).len(), 1);
    s.fellowship = FellowshipView::default();
    s.update();
    assert!(s.rows_of(fellowship::LIST_BOX_ID).is_empty());
}

// FormatStatsText_MatchesD5Rules
#[test]
fn the_stats_text_follows_the_share_rules() {
    for (share_xp, even, count, expected) in [
        (false, true, 1, "12/0%"),
        // Even split of nine: 0.3111111 -> 31.
        (true, true, 9, "12/31%"),
        // Proportional: the only fellow carries the whole share.
        (true, false, 1, "12/100%"),
        (true, true, 6, "12/44%"),
        (true, true, 8, "12/34%"),
    ] {
        let mut v = FellowshipView {
            share_xp,
            even_xp_split: even,
            ..FellowshipView::default()
        };
        v.members = (0..count)
            .map(|i| fellow(0x5000_0001 + i, "Alice", 12))
            .collect();
        let raise: &dyn Fn(u32) -> i64 = &|l| i64::from(l) * 1000;
        assert_eq!(
            fellowship::stats_text(&v.members[0], &v, Some(raise), None),
            expected,
            "share {share_xp} even {even} count {count}"
        );
    }
}

// StatsText_GivesUpTheRowsOverlapPastTheList
#[test]
fn the_stats_text_gives_up_the_rows_overlap_past_the_list() {
    let mut s = social();
    let list = s.find(fellowship::LIST_BOX_ID);
    s.host.root.element_mut(list).unwrap().width = 271.0;
    in_fellowship(&mut s, vec![fellow(0x5000_0001, "Alice", 12)]);
    let mut templates = |ui: &mut UiRoot, l: u32, e: u32| {
        let row = rows(ui, l, e)?;
        ui.element_mut(row).unwrap().width = 279.0;
        let stats = ui.find_descendant(row, fellowship::ROW_STATS_ID).unwrap();
        let st = ui.element_mut(stats).unwrap();
        st.left = 199.0;
        st.width = 80.0;
        Some(row)
    };
    let opts = s.options.clone();
    let view = SocialView {
        player: SELF,
        selected: None,
        in_world: true,
        options: &opts,
        friends: &s.friends,
        squelch: &s.squelch,
        fellowship: &s.fellowship.clone(),
        allegiance: AllegianceSource::default(),
        experience_to_raise: None,
        strings: None,
        name_of: None,
    };
    s.host
        .with_controller::<SocialPanelController, _>(WINDOW, |c, ui| {
            c.update(ui, &view, &mut templates);
        })
        .unwrap();
    let row = s.rows_of(fellowship::LIST_BOX_ID)[0];
    let stats = s
        .host
        .root
        .find_descendant(row, fellowship::ROW_STATS_ID)
        .unwrap();
    let e = s.host.root.element(stats).unwrap();
    assert_eq!((e.left, e.width), (199.0, 72.0));
}

// FormatStatsText_ProportionalSplit_UsesNextLevelCostsAndTheAuthoredTemplate
#[test]
fn an_uneven_split_uses_next_level_costs_and_the_authored_entry() {
    let mut st = FakeStrings::default();
    st.templates.insert(
        (
            fellowship::FELLOWSHIP_STRING_TABLE_ID,
            fellowship::STATS_TEMPLATE_KEY,
        ),
        Box::new(|v| {
            format!(
                "{}/{}%",
                v[&fellowship::STATS_LEVEL_VARIABLE],
                v[&fellowship::STATS_PERCENT_VARIABLE]
            )
        }),
    );
    let mut s = social();
    s.strings = Some(st);
    s.to_raise = Some(|l| i64::from(l) * 100);
    s.fellowship.share_xp = true;
    s.fellowship.even_xp_split = false;
    in_fellowship(
        &mut s,
        vec![
            fellow(0x5000_0001, "Alice", 12),
            fellow(0x5000_0002, "Bob", 20),
        ],
    );
    s.update();
    let rows = s.rows_of(fellowship::LIST_BOX_ID);
    let stats = |i: usize| {
        let t = s
            .host
            .root
            .find_descendant(rows[i], fellowship::ROW_STATS_ID)
            .unwrap();
        text_of(&s.host.root, t)
    };
    // 1200 and 2000 of 3200: 37% and 62%.
    assert_eq!(stats(0), "12/37%");
    assert_eq!(stats(1), "20/62%");
}

// CreateButton_DisabledWhileNameFieldEmpty_EnabledOnceTyped
#[test]
fn create_is_enabled_only_with_a_typed_name() {
    let mut s = social();
    assert!(!s.enabled(fellowship::CREATE_BUTTON_ID));
    let f = s.find(fellowship::NAME_ENTRY_ID);
    s.host
        .root
        .widget_mut::<Field>(f)
        .unwrap()
        .set_text("The Wanderers");
    s.update();
    assert!(s.enabled(fellowship::CREATE_BUTTON_ID));
    s.host.root.widget_mut::<Field>(f).unwrap().set_text("   ");
    s.update();
    assert!(!s.enabled(fellowship::CREATE_BUTTON_ID));
}

// CreateButton_Click_SendsTypedName_AndTheShareXpOptionValue
#[test]
fn create_sends_the_typed_name_and_the_share_option() {
    let mut s = social();
    s.options.set_bit(option_id::FELLOWSHIP_SHARE_XP, true);
    let f = s.find(fellowship::NAME_ENTRY_ID);
    s.host
        .root
        .widget_mut::<Field>(f)
        .unwrap()
        .set_text("The Wanderers");
    s.update();
    let out = s.click_id(fellowship::CREATE_BUTTON_ID);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::Create {
            name: "The Wanderers".into(),
            share_xp: true
        }]
    );
}

// QuitButton_Click_SendsQuit_WithDisbandFalse / DisbandButton_Click_SendsQuit_WithDisbandTrue
#[test]
fn quit_and_disband_send_quit() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    in_fellowship(&mut s, vec![fellow(SELF, "Me", 20)]);
    s.update();
    let out = s.click_id(fellowship::QUIT_BUTTON_ID);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::Quit { disband: false }]
    );
    let out = s.click_id(fellowship::DISBAND_BUTTON_ID);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::Quit { disband: true }]
    );
}

// RecruitButton_Click_SendsTheCurrentWorldSelection
#[test]
fn recruit_sends_the_world_selection() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    s.selected = Some(0x6000_0001);
    in_fellowship(&mut s, vec![fellow(SELF, "Me", 20)]);
    s.update();
    let out = s.click_id(fellowship::RECRUIT_BUTTON_ID);
    assert_dbg_eq!(
        fellowship_commands(&out),
        [FellowshipCommand::Recruit(ObjectRef::Guid(0x6000_0001))]
    );
}

// DismissAndLeaderButtons_Click_TargetTheSelectedFellow
#[test]
fn dismiss_and_leader_target_the_selected_fellow() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    in_fellowship(
        &mut s,
        vec![fellow(SELF, "Me", 20), fellow(0x5000_0001, "Alice", 12)],
    );
    s.update();
    let row = s.rows_of(fellowship::LIST_BOX_ID)[1];
    let name = s
        .host
        .root
        .find_descendant(row, fellowship::ROW_NAME_ID)
        .unwrap();
    let out = s.click(name);
    assert_dbg_eq!(out, [PanelOutput::SelectObject(0x5000_0001)]);
    s.update();
    let mut out = s.click_id(fellowship::DISMISS_BUTTON_ID);
    out.extend(s.click_id(fellowship::LEADER_BUTTON_ID));
    assert_dbg_eq!(
        fellowship_commands(&out),
        [
            FellowshipCommand::Dismiss(ObjectRef::Guid(0x5000_0001)),
            FellowshipCommand::AssignLeader(ObjectRef::Guid(0x5000_0001))
        ]
    );
}

// ButtonStates_NonLeader_DisbandOpenLeaderDismiss_AreDisabled_QuitStaysEnabled / ButtonStates_Leader_DisbandAndOpen_AreEnabled
#[test]
fn the_buttons_follow_the_lead() {
    let mut s = social();
    s.fellowship.leader_guid = 0x1;
    in_fellowship(&mut s, vec![fellow(SELF, "Me", 20)]);
    s.update();
    assert!(s.enabled(fellowship::QUIT_BUTTON_ID));
    for id in [
        fellowship::DISBAND_BUTTON_ID,
        fellowship::OPEN_BUTTON_ID,
        fellowship::LEADER_BUTTON_ID,
        fellowship::DISMISS_BUTTON_ID,
    ] {
        assert!(!s.enabled(id), "{id:#X}");
    }
    s.fellowship.leader_guid = SELF;
    s.update();
    assert!(s.enabled(fellowship::DISBAND_BUTTON_ID));
    assert!(s.enabled(fellowship::OPEN_BUTTON_ID));
}

// RecruitButton_DisabledWhenFellowshipIsFull
#[test]
fn recruit_is_disabled_in_a_full_fellowship() {
    let mut s = social();
    s.fellowship.leader_guid = SELF;
    s.selected = Some(0x6000_0001);
    in_fellowship(&mut s, (0..9).map(|i| fellow(SELF + i, "F", 10)).collect());
    s.update();
    assert!(!s.enabled(fellowship::RECRUIT_BUTTON_ID));
}

// Checkbox_Click_TogglesAndWritesTheCharacterOption / Checkbox_RefreshesFromLiveState_WhenTheOtherSurfaceWrites
#[test]
fn a_checkbox_writes_its_option_and_shows_the_options_state() {
    let mut s = social();
    let b = s.find(fellowship::SHARE_XP_ID);
    assert!(
        s.host
            .root
            .widget::<Button>(b)
            .unwrap()
            .suppress_self_toggle
    );
    assert!(!s.host.root.widget::<Button>(b).unwrap().selected());
    let out = s.click(b);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::SetCharacterOption {
            option: option_id::FELLOWSHIP_SHARE_XP,
            value: true
        }]
    );
    s.options.set_bit(option_id::FELLOWSHIP_SHARE_XP, true);
    s.update();
    assert!(s.host.root.widget::<Button>(b).unwrap().selected());
    let out = s.click(b);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::SetCharacterOption {
            option: option_id::FELLOWSHIP_SHARE_XP,
            value: false
        }]
    );
    // Written elsewhere: the box follows.
    let ignore = s.find(fellowship::IGNORE_REQUESTS_ID);
    s.options
        .set_bit(option_id::IGNORE_FELLOWSHIP_REQUESTS, true);
    s.update();
    assert!(s.host.root.widget::<Button>(ignore).unwrap().selected());
}

// FellowshipPageVisible_Declares0x00A6_OnlyWhenWindowShownANDFellowshipActive / SetPageVisible_SendsPanelOpen_OnlyOnATransition
#[test]
fn the_fellowship_page_declares_itself_only_when_shown_on_its_tab() {
    let mut s = social();
    s.with(|c, ui| c.activate_tabs(ui));
    panel_outputs(&mut s.host);
    s.show();
    assert!(fellowship_commands(&panel_outputs(&mut s.host)).is_empty());
    s.with(|c, ui| c.show_fellowship(ui));
    assert_dbg_eq!(
        fellowship_commands(&panel_outputs(&mut s.host)),
        [FellowshipCommand::SetPanelOpen(true)]
    );
    s.with(|c, ui| c.show_fellowship(ui));
    assert!(fellowship_commands(&panel_outputs(&mut s.host)).is_empty());
    s.hide();
    assert_dbg_eq!(
        fellowship_commands(&panel_outputs(&mut s.host)),
        [FellowshipCommand::SetPanelOpen(false)]
    );
}

// Reconnect_ReDeclares0x00A6_AfterWorldEntry_NotDuringPreWorldReset / SetPageVisible_DoesNotLatch_WhenDeclarationDropped_SoItRetriesInWorld
#[test]
fn a_reconnect_declares_the_fellowship_page_again_in_the_world() {
    let mut s = social();
    s.with(|c, ui| c.activate_tabs(ui));
    s.show();
    s.with(|c, ui| c.show_fellowship(ui));
    assert_dbg_eq!(
        fellowship_commands(&panel_outputs(&mut s.host)),
        [FellowshipCommand::SetPanelOpen(true)]
    );
    s.in_world = false;
    s.with(|c, _| c.reset_session_declaration());
    s.update();
    assert!(fellowship_commands(&panel_outputs(&mut s.host)).is_empty());
    s.with(|c, ui| c.redeclare_after_world_entry(ui));
    assert_eq!(
        fellowship_commands(&panel_outputs(&mut s.host)),
        [FellowshipCommand::SetPanelOpen(true)]
    );
}

// Reconnect_StaysSilent_WhenFellowshipPageIsNotActuallyOpen
#[test]
fn a_reconnect_stays_silent_when_the_fellowship_page_is_not_open() {
    let mut s = social();
    s.with(|c, ui| c.activate_tabs(ui));
    s.show();
    panel_outputs(&mut s.host);
    s.with(|c, _| c.reset_session_declaration());
    s.with(|c, ui| c.redeclare_after_world_entry(ui));
    assert!(fellowship_commands(&panel_outputs(&mut s.host)).is_empty());
}

#[test]
fn a_declaration_out_of_the_world_waits_for_the_world() {
    let mut s = social();
    s.in_world = false;
    s.update();
    s.with(|c, ui| c.activate_tabs(ui));
    s.show();
    s.with(|c, ui| c.show_fellowship(ui));
    assert!(fellowship_commands(&panel_outputs(&mut s.host)).is_empty());
    s.in_world = true;
    s.update();
    assert_dbg_eq!(
        fellowship_commands(&panel_outputs(&mut s.host)),
        [FellowshipCommand::SetPanelOpen(true)]
    );
}

// ── Allegiance page ─────────────────────────────────────────────────────

fn tree(monarch: AllegianceMember, records: Vec<AllegianceMember>) -> AllegianceProfile {
    AllegianceProfile {
        record_count: u16::try_from(records.len() + 1).unwrap(),
        monarch: Some(monarch),
        records,
        ..AllegianceProfile::default()
    }
}

// Allegiance_NoProfile_HidesBlocksAndBlanksNames
#[test]
fn without_a_profile_the_blocks_hide_and_the_names_blank() {
    let s = social();
    assert!(!s.visible(allegiance::MONARCH_FIELD_ID));
    assert!(!s.visible(allegiance::PATRON_FIELD_ID));
    assert_eq!(
        s.text_in(allegiance::MONARCH_FIELD_ID, allegiance::MONARCH_NAME_ID),
        " "
    );
    assert_eq!(
        s.text_in(allegiance::PATRON_FIELD_ID, allegiance::PATRON_NAME_ID),
        " "
    );
}

// Allegiance_HasMonarch_NotSelf_ShowsMonarchBlock_AndRendersData
#[test]
fn a_monarch_other_than_the_player_shows_its_block() {
    let mut s = social();
    let mut p = tree(member(200, 0, "Queen Alice"), vec![]);
    p.total_members = 3;
    s.profile = Some(p);
    s.revision = 1;
    s.update();
    assert!(s.visible(allegiance::MONARCH_FIELD_ID));
    assert_eq!(
        s.text_in(allegiance::MONARCH_FIELD_ID, allegiance::MONARCH_NAME_ID),
        "Queen Alice"
    );
    assert_eq!(
        s.text_in(
            allegiance::MONARCH_FIELD_ID,
            allegiance::MONARCH_FOLLOWERS_ID
        ),
        "Followers: 2"
    );
}

// Allegiance_IsMonarch_HidesMonarchBlock
#[test]
fn the_player_as_monarch_hides_the_monarch_block() {
    let mut s = social();
    s.profile = Some(tree(member(SELF, 0, "Me"), vec![]));
    s.update();
    assert!(!s.visible(allegiance::MONARCH_FIELD_ID));
}

// Allegiance_HasPatron_NotMonarch_ShowsPatronBlock_AndRendersName
#[test]
fn a_patron_who_is_not_the_monarch_shows_the_patron_block() {
    let mut s = social();
    s.profile = Some(tree(
        member(200, 0, "Queen Alice"),
        vec![member(300, 200, "Sir Bob"), member(SELF, 300, "Me")],
    ));
    s.update();
    assert!(s.visible(allegiance::PATRON_FIELD_ID));
    assert_eq!(
        s.text_in(allegiance::PATRON_FIELD_ID, allegiance::PATRON_NAME_ID),
        "Sir Bob"
    );
}

// Allegiance_PatronIsMonarch_HidesPatronBlock_RevealsMonarchSubBlock
#[test]
fn a_patron_who_is_the_monarch_shows_in_the_monarch_block() {
    let mut s = social();
    let mut p = tree(member(200, 0, "Queen Alice"), vec![member(SELF, 200, "Me")]);
    p.total_members = 2;
    s.profile = Some(p);
    s.update();
    assert!(s.visible(allegiance::MONARCH_FIELD_ID));
    assert!(!s.visible(allegiance::PATRON_FIELD_ID));
    assert!(s.visible(allegiance::MONARCH_IS_PATRON_ID));
}

// Allegiance_MemberNames_CarryTheRankTitle_AndSelfRankLineNamesTheTitle
#[test]
fn member_names_carry_their_rank_title_and_the_rank_line_names_the_players() {
    let mut s = social();
    let mut alice = member(200, 0, "Alice");
    (alice.rank, alice.gender, alice.heritage_group) = (9, 2, 1);
    let mut bob = member(300, 200, "Bob");
    (bob.rank, bob.gender, bob.heritage_group) = (7, 1, 1);
    let mut carol = member(SELF, 300, "Carol");
    (carol.rank, carol.gender, carol.heritage_group) = (2, 2, 1);
    let mut dave = member(401, SELF, "Dave");
    (dave.rank, dave.gender, dave.heritage_group, dave.cp_tithed) = (1, 1, 1, 10);
    let mut eve = member(402, SELF, "Eve");
    (eve.rank, eve.gender, eve.heritage_group, eve.cp_tithed) = (0, 2, 1, 20);
    let mut p = tree(alice, vec![bob, carol, dave, eve]);
    p.total_members = 5;
    s.profile = Some(p);
    s.rank = 2;
    s.revision = 1;
    s.rank_quality = Some(2);
    s.update();
    assert_eq!(
        s.text_in(allegiance::MONARCH_FIELD_ID, allegiance::MONARCH_NAME_ID),
        "Queen Alice"
    );
    assert_eq!(
        s.text_in(allegiance::PATRON_FIELD_ID, allegiance::PATRON_NAME_ID),
        "Duke Bob"
    );
    assert_eq!(
        s.text_in(allegiance::PAGE_ID, allegiance::SELF_RANK_ID),
        "Rank: Baronet [2]"
    );
    let mut names: Vec<String> = s
        .rows_of(allegiance::VASSAL_LIST_ID)
        .into_iter()
        .map(|r| {
            let t = s
                .host
                .root
                .find_descendant(r, allegiance::ROW_NAME_ID)
                .unwrap();
            text_of(&s.host.root, t)
        })
        .collect();
    names.sort();
    assert_eq!(names, ["Eve", "Yeoman Dave"]);
}

// Allegiance_SelfRankLine_UsesTheAuthoredTemplates_AndTheBuffedFormWhenTheQualityIsHigher
#[test]
fn the_rank_line_uses_the_authored_entries_and_the_buffed_form() {
    let mut st = FakeStrings::default();
    let var = |v: &HashMap<u32, String>, k: &str| v[&compute_hash(k)].clone();
    st.templates.insert(
        (
            allegiance::STRING_TABLE_ID,
            compute_hash("ID_Allegiance_RankBuffed"),
        ),
        Box::new(move |v| {
            format!(
                "R {} {}+{}",
                var(v, "TITLE"),
                var(v, "RANK"),
                var(v, "RANKBUFF")
            )
        }),
    );
    st.templates.insert(
        (
            allegiance::STRING_TABLE_ID,
            compute_hash("ID_Allegiance_Rank"),
        ),
        Box::new(move |v| format!("R {} {}", var(v, "TITLE"), var(v, "RANK"))),
    );
    let mut carol = member(SELF, 0, "Carol");
    (carol.rank, carol.gender, carol.heritage_group) = (2, 2, 1);
    let own = Some(&carol);
    let s: Option<&dyn PanelStrings> = Some(&st);
    assert_eq!(
        allegiance::rank_line_text(2, own, Some(2), s),
        "R Baronet 2"
    );
    // A rank quality above the profile's rank shows the difference.
    assert_eq!(
        allegiance::rank_line_text(2, own, Some(4), s),
        "R Baronet 4+2"
    );
    // A missing quality reads as 0.
    assert_eq!(
        allegiance::rank_line_text(2, own, None, s),
        "R Baronet 0+-2"
    );
    // Without the entries.
    assert_eq!(
        allegiance::rank_line_text(2, own, Some(4), None),
        "Rank: Baronet [4 (+2)]"
    );
}

// Allegiance_VassalList_PopulatesOneRowPerVassal / Allegiance_OfflineCue_IsTheMarkerOnly_NameStaysWhite
#[test]
fn one_row_per_vassal_with_the_offline_marker_as_the_only_cue() {
    let mut s = social();
    let mut offline = member(402, SELF, "Offline Vassal");
    offline.is_logged_in = false;
    s.profile = Some(tree(
        member(SELF, 0, "Me"),
        vec![member(401, SELF, "Online Vassal"), offline],
    ));
    s.revision = 1;
    s.update();
    let rows = s.rows_of(allegiance::VASSAL_LIST_ID);
    assert_eq!(rows.len(), 2);
    let ui = &s.host.root;
    for row in rows {
        let name = ui.find_descendant(row, allegiance::ROW_NAME_ID).unwrap();
        let marker = ui
            .find_descendant(row, allegiance::ROW_OFFLINE_MARKER_ID)
            .unwrap();
        let line = ui.widget::<Text>(name).unwrap().content_lines()[0].clone();
        let online = line.text == "Online Vassal";
        assert_eq!(ui.element(marker).unwrap().visible, !online);
        assert_eq!(line.color, ac_ui::draw::Color::WHITE);
    }
}

// Allegiance_Swear_ShowsConfirmation_AndSendsOnAccept
#[test]
fn swear_asks_first_then_swears_to_the_selection() {
    let mut s = social();
    s.profile = Some(tree(member(SELF, 0, "Me"), vec![]));
    s.selected = Some(500);
    s.names.insert(500, "Target Player".into());
    s.update();
    assert!(s.enabled(allegiance::SWEAR_BUTTON_ID));
    let out = s.click_id(allegiance::SWEAR_BUTTON_ID);
    let (message, yes) = confirmation(&out).expect("asks");
    assert_eq!(message, "Target Player");
    assert_dbg_eq!(yes, AllegianceCommand::Swear(ObjectRef::Guid(500)));
}

// Allegiance_Break_TargetsCurrentPatron
#[test]
fn break_targets_the_current_patron() {
    let mut s = social();
    s.profile = Some(tree(
        member(200, 0, "Queen"),
        vec![member(300, 200, "Sir Bob"), member(SELF, 300, "Me")],
    ));
    s.update();
    assert!(s.enabled(allegiance::BREAK_BUTTON_ID));
    assert!(!s.enabled(allegiance::SWEAR_BUTTON_ID));
    let out = s.click_id(allegiance::BREAK_BUTTON_ID);
    let (message, yes) = confirmation(&out).expect("asks");
    assert_eq!(message, "Sir Bob");
    assert_dbg_eq!(yes, AllegianceCommand::Break(ObjectRef::Guid(300)));
}

// Allegiance_Kick_TargetsSelectedVassalRow
#[test]
fn kick_targets_the_selected_vassal_row() {
    let mut s = social();
    s.profile = Some(tree(
        member(SELF, 0, "Me"),
        vec![member(401, SELF, "Vassal One")],
    ));
    s.revision = 1;
    s.update();
    assert!(!s.enabled(allegiance::KICK_BUTTON_ID));
    let row = s.rows_of(allegiance::VASSAL_LIST_ID)[0];
    let name = s
        .host
        .root
        .find_descendant(row, allegiance::ROW_NAME_ID)
        .unwrap();
    s.click(name);
    s.update();
    assert!(s.enabled(allegiance::KICK_BUTTON_ID));
    let out = s.click_id(allegiance::KICK_BUTTON_ID);
    let (message, yes) = confirmation(&out).expect("asks");
    assert_eq!(message, "Vassal One");
    assert_dbg_eq!(yes, AllegianceCommand::Kick(ObjectRef::Guid(401)));
}

#[test]
fn the_questions_go_through_the_authored_entries() {
    let mut st = FakeStrings::default();
    st.templates.insert(
        (
            allegiance::STRING_TABLE_ID,
            compute_hash("ID_Allegiance_SwearConfirmation"),
        ),
        Box::new(|v| format!("Swear to {}?", v[&ac_ui::strings::PLAYER_VARIABLE])),
    );
    let s: Option<&dyn PanelStrings> = Some(&st);
    assert_eq!(
        allegiance::question(s, "ID_Allegiance_SwearConfirmation", "Bob"),
        "Swear to Bob?"
    );
    assert_eq!(
        allegiance::question(s, "ID_Allegiance_KickConfirmation", "Bob"),
        "Bob"
    );
}

// Allegiance_ExperiencePassedUp_IsGroupedAndAuthored
#[test]
fn the_experience_passed_up_is_grouped_and_authored() {
    let mut st = FakeStrings::default();
    let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
    let record = seen.clone();
    st.templates.insert(
        (
            allegiance::STRING_TABLE_ID,
            compute_hash("ID_Allegiance_VassalExperiencePassedUp"),
        ),
        Box::new(move |v| {
            assert_eq!(v.len(), 1);
            let value = v[&compute_hash("VALUE")].clone();
            record.lock().unwrap().push(value.clone());
            format!("[{value}]")
        }),
    );
    let mut s = social();
    s.strings = Some(st);
    let mut me = member(SELF, 300, "Me");
    me.cp_tithed = 987_654;
    let mut vassal = member(401, SELF, "Vassal One");
    vassal.cp_tithed = 1_500_000;
    s.profile = Some(tree(
        member(200, 0, "Monarch"),
        vec![member(300, 200, "Patron"), me, vassal],
    ));
    s.revision = 1;
    s.update();
    let row = s.rows_of(allegiance::VASSAL_LIST_ID)[0];
    let xp = s
        .host
        .root
        .find_descendant(row, allegiance::ROW_EXPERIENCE_ID)
        .unwrap();
    assert_eq!(text_of(&s.host.root, xp), "[1,500,000]");
    assert_eq!(
        s.text_in(
            allegiance::PATRON_FIELD_ID,
            allegiance::EXPERIENCE_PASSED_UP_ID
        ),
        "[987,654]"
    );
    let seen = seen.lock().unwrap();
    assert!(seen.contains(&"1,500,000".to_owned()));
    assert!(seen.contains(&"987,654".to_owned()));
}

// Allegiance_ExperiencePassedUp_WithoutTheAuthoredEntry_IsStillGrouped
#[test]
fn the_experience_passed_up_is_grouped_without_the_entry() {
    assert_eq!(
        allegiance::experience_passed_up_text(1_500_000, None),
        "1,500,000"
    );
}

// AllegiancePageVisible_DeclaresSubscription_OnWindowShown_AndClearsOnHidden
#[test]
fn the_allegiance_page_subscribes_while_shown() {
    let mut s = social();
    s.with(|c, ui| c.activate_tabs(ui));
    panel_outputs(&mut s.host);
    s.show();
    assert_dbg_eq!(
        allegiance_commands(&panel_outputs(&mut s.host)),
        [AllegianceCommand::Subscribe(true)]
    );
    s.hide();
    assert_dbg_eq!(
        allegiance_commands(&panel_outputs(&mut s.host)),
        [AllegianceCommand::Subscribe(false)]
    );
}

// Reconnect_ReDeclaresSubscription_AfterWorldEntry_EvenWhilePanelClosed
#[test]
fn a_reconnect_subscribes_again_even_with_the_panel_closed() {
    let mut s = social();
    s.with(|c, ui| c.activate_tabs(ui));
    panel_outputs(&mut s.host);
    s.with(|c, _| c.reset_session_declaration());
    assert!(allegiance_commands(&panel_outputs(&mut s.host)).is_empty());
    s.with(|c, ui| c.redeclare_after_world_entry(ui));
    assert_dbg_eq!(
        allegiance_commands(&panel_outputs(&mut s.host)),
        [AllegianceCommand::Subscribe(true)]
    );
}

// ── Friends and squelch ─────────────────────────────────────────────────

fn friend(id: u32, name: &str, online: bool) -> FriendEntry {
    FriendEntry {
        id,
        name: name.to_owned(),
        online,
        appear_offline: false,
        friends: Vec::new(),
        friend_of: Vec::new(),
    }
}

// Friends_PopulatesOneRowPerEntry / Friends_ReactsToRevisionChange_OnTick
#[test]
fn the_friends_page_lists_one_row_per_friend_and_follows_the_list() {
    let mut s = social();
    s.show();
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::FULL,
        entries: vec![friend(1, "Alice", true), friend(2, "Bob", false)],
    });
    s.update();
    assert_eq!(s.rows_of(friends::FRIENDS_LIST_ID).len(), 2);
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::ADD,
        entries: vec![friend(3, "Carol", true)],
    });
    s.update();
    assert_eq!(s.rows_of(friends::FRIENDS_LIST_ID).len(), 3);
}

// Friends_RevisionBumpWhileHidden_DoesNotRebuild_ButRebuildsOnShow
#[test]
fn the_friends_page_rebuilds_only_while_shown() {
    let mut s = social();
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::ADD,
        entries: vec![friend(1, "Alice", true)],
    });
    s.update();
    assert!(s.rows_of(friends::FRIENDS_LIST_ID).is_empty());
    s.show();
    s.update();
    assert_eq!(s.rows_of(friends::FRIENDS_LIST_ID).len(), 1);
}

// Squelch_PopulatesCharacterAndAccountRows
#[test]
fn the_squelch_page_lists_characters_then_accounts() {
    let mut s = social();
    s.show();
    s.squelch.replace(SquelchDatabase {
        accounts: HashMap::from([("BadAccount".to_owned(), 1)]),
        characters: HashMap::from([(
            7,
            SquelchInfo {
                name: "Grief".into(),
                account_wide: false,
                message_types: HashSet::new(),
            },
        )]),
        global: SquelchInfo::default(),
    });
    s.update();
    let rows = s.rows_of(friends::SQUELCH_LIST_ID);
    assert_eq!(rows.len(), 2);
    let names: Vec<String> = rows
        .iter()
        .map(|r| {
            let inner = s.host.root.children(s.host.root.children(*r)[0])[0];
            text_of(&s.host.root, inner)
        })
        .collect();
    assert_eq!(names, ["Grief", "BadAccount"]);
}

// Friends_ScrollbarModel_IsWiredToListBoxScroll / Squelch_ScrollbarModel_IsWiredToListBoxScroll
#[test]
fn the_lists_scrollbars_scroll_the_lists() {
    let s = social();
    for (list, bar) in [
        (friends::FRIENDS_LIST_ID, 0x1000_0518),
        (friends::SQUELCH_LIST_ID, 0x1000_0543),
    ] {
        let l = s.find(list);
        let b = s.find(bar);
        assert_dbg_eq!(
            s.host.root.widget::<Scrollbar>(b).unwrap().model,
            ac_ui::widgets::ScrollSource::Element(l)
        );
    }
}

// Friends_LongRoster_IsReachableViaScrollbar
#[test]
fn a_long_friends_list_overflows_its_box() {
    let mut s = social();
    s.show();
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::FULL,
        entries: (0..40)
            .map(|i| friend(i, &format!("Friend{i}"), true))
            .collect(),
    });
    s.update();
    s.host.draw((1024.0, 768.0), &ac_ui::draw::NoSprites);
    let l = s.find(friends::FRIENDS_LIST_ID);
    let m = s.host.root.scroll_model(l).unwrap();
    assert!(m.content_height > m.view_height);
}

#[test]
fn the_friends_buttons_add_remove_and_appear_offline() {
    let mut s = social();
    s.show();
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::FULL,
        entries: vec![friend(1, "Alice", true)],
    });
    s.update();
    let f = s.find(friends::FRIEND_NAME_FIELD_ID);
    s.host
        .root
        .widget_mut::<Field>(f)
        .unwrap()
        .set_text("  Bob ");
    let out = s.click_id(friends::ADD_FRIEND_BUTTON_ID);
    assert_dbg_eq!(sessions(&out), [SessionCommand::AddFriend("Bob".into())]);
    assert_eq!(s.host.root.widget::<Field>(f).unwrap().text(), "");
    assert!(sessions(&s.click_id(friends::REMOVE_FRIEND_BUTTON_ID)).is_empty());
    let row = s.rows_of(friends::FRIENDS_LIST_ID)[0];
    let name = s
        .host
        .root
        .find_descendant(row, friends::FRIEND_ROW_NAME_ID)
        .unwrap();
    s.click(name);
    let out = s.click_id(friends::REMOVE_FRIEND_BUTTON_ID);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::RemoveFriend("Alice".into())]
    );
    let out = s.click_id(friends::APPEAR_OFFLINE_ID);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::SetCharacterOption {
            option: option_id::APPEAR_OFFLINE,
            value: true
        }]
    );
}

// The retail friends and squelch pages (OpenAC leaves the authored
// Ghosted state, so
// its add and remove buttons never act): remove is live with a row
// selected, add below fifty names; typing makes add live, clearing the
// entry ghosts it, and an add ghosts it again.
#[test]
fn the_friends_and_squelch_buttons_come_alive_as_retail_updates_them() {
    let mut s = social();
    let enabled = |s: &Social, id| s.host.root.element(s.find(id)).unwrap().enabled;
    for id in [
        friends::ADD_FRIEND_BUTTON_ID,
        friends::REMOVE_FRIEND_BUTTON_ID,
    ] {
        s.host
            .root
            .set_retail_state(s.find(id), ac_ui::states::GHOSTED);
    }
    s.show();
    s.friends.apply(&FriendsUpdate {
        update_type: FriendsUpdateType::FULL,
        entries: vec![friend(1, "Alice", true)],
    });
    s.update();
    assert!(enabled(&s, friends::ADD_FRIEND_BUTTON_ID));
    assert!(
        !enabled(&s, friends::REMOVE_FRIEND_BUTTON_ID),
        "no selection"
    );
    let row = s.rows_of(friends::FRIENDS_LIST_ID)[0];
    let name = s
        .host
        .root
        .find_descendant(row, friends::FRIEND_ROW_NAME_ID)
        .unwrap();
    s.click(name);
    assert!(enabled(&s, friends::REMOVE_FRIEND_BUTTON_ID));

    let f = s.find(friends::FRIEND_NAME_FIELD_ID);
    s.host.root.widget_mut::<Field>(f).unwrap().set_text("Bob");
    s.host
        .root
        .send_message(f, ac_ui::root::ElementMessage::TextChanged("Bob".into()));
    s.host.pump();
    assert!(enabled(&s, friends::ADD_FRIEND_BUTTON_ID));
    let out = s.click_id(friends::ADD_FRIEND_BUTTON_ID);
    assert_dbg_eq!(sessions(&out), [SessionCommand::AddFriend("Bob".into())]);
    assert!(
        !enabled(&s, friends::ADD_FRIEND_BUTTON_ID),
        "ghosts after an add"
    );
    s.host
        .root
        .send_message(f, ac_ui::root::ElementMessage::TextChanged(String::new()));
    s.host.pump();
    assert!(!enabled(&s, friends::ADD_FRIEND_BUTTON_ID), "empty entry");

    // The squelch page: remove with a selection, both squelch buttons live.
    s.squelch.replace(SquelchDatabase {
        accounts: HashMap::from([("BadAccount".to_owned(), 1)]),
        ..SquelchDatabase::default()
    });
    s.update();
    assert!(enabled(&s, friends::SQUELCH_CHARACTER_BUTTON_ID));
    assert!(enabled(&s, friends::SQUELCH_ACCOUNT_BUTTON_ID));
    assert!(!enabled(&s, friends::REMOVE_SQUELCH_BUTTON_ID));
}

#[test]
fn the_squelch_buttons_squelch_and_unsquelch() {
    let mut s = social();
    s.show();
    s.squelch.replace(SquelchDatabase {
        accounts: HashMap::from([("BadAccount".to_owned(), 1)]),
        ..SquelchDatabase::default()
    });
    s.update();
    let f = s.find(friends::SQUELCH_NAME_FIELD_ID);
    s.host
        .root
        .widget_mut::<Field>(f)
        .unwrap()
        .set_text("Grief");
    let out = s.click_id(friends::SQUELCH_CHARACTER_BUTTON_ID);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::Squelch {
            add: true,
            name: "Grief".into(),
            account: false
        }]
    );
    s.host
        .root
        .widget_mut::<Field>(f)
        .unwrap()
        .set_text("Grief");
    let out = s.click_id(friends::SQUELCH_ACCOUNT_BUTTON_ID);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::Squelch {
            add: true,
            name: "Grief".into(),
            account: true
        }]
    );
    let row = s.rows_of(friends::SQUELCH_LIST_ID)[0];
    let inner = s.host.root.children(s.host.root.children(row)[0])[0];
    s.click(inner);
    let out = s.click_id(friends::REMOVE_SQUELCH_BUTTON_ID);
    assert_dbg_eq!(
        sessions(&out),
        [SessionCommand::Squelch {
            add: false,
            name: "BadAccount".into(),
            account: true
        }]
    );
}

// ── Rank titles (AllegianceRankTitleTableTests) ─────────────────────────

#[test]
fn every_title_table_matches_the_reference() {
    let cases: [(i32, i32, [&str; 10]); 17] = [
        (
            1,
            1,
            [
                "Yeoman",
                "Baronet",
                "Baron",
                "Reeve",
                "Thane",
                "Ealdor",
                "Duke",
                "Aetheling",
                "King",
                "High King",
            ],
        ),
        (
            1,
            2,
            [
                "Yeoman",
                "Baronet",
                "Baroness",
                "Reeve",
                "Thane",
                "Ealdor",
                "Duchess",
                "Aetheling",
                "Queen",
                "High Queen",
            ],
        ),
        (
            2,
            1,
            [
                "Sayyid", "Shayk", "Maulan", "Mu'allim", "Naquib", "Qadi", "Mushir", "Amir",
                "Malik", "Sultan",
            ],
        ),
        (
            2,
            2,
            [
                "Sayyida",
                "Shayka",
                "Maulana",
                "Mu'allima",
                "Naquiba",
                "Qadiya",
                "Mushira",
                "Amira",
                "Malika",
                "Sultana",
            ],
        ),
        (
            3,
            1,
            [
                "Jinin",
                "Jo-chueh",
                "Nan-chueh",
                "Shi-chueh",
                "Ta-chueh",
                "Kun-chueh",
                "Kou",
                "Taikou",
                "Ou",
                "Koutei",
            ],
        ),
        (
            3,
            2,
            [
                "Jinin",
                "Jo-chueh",
                "Nan-chueh",
                "Shi-chueh",
                "Ta-chueh",
                "Kun-chueh",
                "Kou",
                "Taikou",
                "Jo-ou",
                "Koutei",
            ],
        ),
        (
            4,
            1,
            [
                "Squire",
                "Banner",
                "Baron",
                "Viscount",
                "Count",
                "Marquis",
                "Duke",
                "Grand Duke",
                "King",
                "High King",
            ],
        ),
        (
            4,
            2,
            [
                "Dame",
                "Banner",
                "Baroness",
                "Viscountess",
                "Countess",
                "Marquise",
                "Duchess",
                "Grand Duchess",
                "Queen",
                "High Queen",
            ],
        ),
        (
            5,
            1,
            [
                "Tenebrous",
                "Shade",
                "Squire",
                "Knight",
                "Void Knight",
                "Void Lord",
                "Duke",
                "Archduke",
                "Highborn",
                "King",
            ],
        ),
        (
            5,
            2,
            [
                "Tenebrous",
                "Shade",
                "Squire",
                "Knight",
                "Void Knight",
                "Void Lady",
                "Duchess",
                "Archduchess",
                "Highborn",
                "Queen",
            ],
        ),
        (
            6,
            1,
            [
                "Tribunus",
                "Praefectus",
                "Optio",
                "Centurion",
                "Principes",
                "Legatus",
                "Consul",
                "Dux",
                "Secondus",
                "Primus",
            ],
        ),
        (
            7,
            1,
            [
                "Xutua", "Tuona", "Ona", "Nuona", "Turea", "Rea", "Nurea", "Kauh", "Sutah", "Tah",
            ],
        ),
        (
            8,
            2,
            [
                "Laigus", "Raigus", "Amploth", "Arintoth", "Obeloth", "Lithos", "Kantos", "Gigas",
                "Extas", "Tiatus",
            ],
        ),
        (
            9,
            1,
            [
                "Ensign",
                "Corporal",
                "Lieutenant",
                "Commander",
                "Captain",
                "Commodore",
                "Admiral",
                "Warlord",
                "Ipharsin",
                "Aulin",
            ],
        ),
        (
            9,
            2,
            [
                "Ensign",
                "Corporal",
                "Lieutenant",
                "Commander",
                "Captain",
                "Commodore",
                "Admiral",
                "Warlord",
                "Ipharsia",
                "Aulia",
            ],
        ),
        (
            0xB,
            1,
            [
                "Neophyte",
                "Acolyte",
                "Adept",
                "Esquire",
                "Squire",
                "Knight",
                "Count",
                "Viscount",
                "Highness",
                "Annointed",
            ],
        ),
        (
            0xB,
            2,
            [
                "Neophyte",
                "Acolyte",
                "Adept",
                "Esquire",
                "Squire",
                "Knight",
                "Countess",
                "Viscountess",
                "Highness",
                "Annointed",
            ],
        ),
    ];
    for (heritage, gender, titles) in cases {
        for (i, expected) in titles.iter().enumerate() {
            let rank = i32::try_from(i).unwrap() + 1;
            assert_eq!(
                title(rank, heritage, gender),
                Some(*expected),
                "heritage {heritage} gender {gender} rank {rank}"
            );
        }
    }
}

// Penumbraen_AliasesShadowbound / Penumbraen_MatchesShadowboundExactlyAcrossAllRanksAndGenders
#[test]
fn penumbraen_shares_the_shadowbound_titles() {
    for gender in [1, 2] {
        for rank in 0..=11 {
            assert_eq!(title(rank, 0xA, gender), title(rank, 5, gender));
        }
    }
}

// Gearknight_MaleFunctionReusedForBothGenders / Tumerok_... / Lugian_FemaleFunctionReusedForBothGenders
#[test]
fn single_table_heritages_use_it_for_both_genders() {
    for heritage in [6, 7, 8] {
        for rank in 1..=10 {
            assert_eq!(title(rank, heritage, 1), title(rank, heritage, 2));
            assert!(title(rank, heritage, 1).is_some());
        }
    }
}

// Heritage_OutsideRange_ReturnsNullForBothGenders / Gender_Unrecognized_ReturnsNull / Rank_OutsideOneToTen_ReturnsNull
#[test]
fn out_of_range_inputs_have_no_title() {
    for heritage in [0, -1, 12, 13] {
        assert_eq!(title(1, heritage, 1), None);
        assert_eq!(title(1, heritage, 2), None);
    }
    for gender in [0, 3, -1] {
        assert_eq!(title(1, 1, gender), None);
    }
    for rank in [0, -1, 11] {
        assert_eq!(title(rank, 1, 1), None);
    }
}

// ComposeFullName_ValidRank_PrefixesTitleWithSingleSpace / ComposeFullName_NoTitleResolved_ReturnsPlainNameUnmodified / ComposeFullName_UnrecognizedHeritage_ReturnsPlainName
#[test]
fn the_full_name_prefixes_the_title_with_one_space() {
    assert_eq!(compose_full_name(9, 1, 2, "Alice"), "Queen Alice");
    assert_eq!(compose_full_name(0, 1, 2, "Alice"), "Alice");
    assert_eq!(compose_full_name(11, 1, 2, "Alice"), "Alice");
    assert_eq!(compose_full_name(5, 99, 1, "Bob"), "Bob");
}

// ── Real dats ───────────────────────────────────────────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{LayoutSource, import_rooted};
    use ac_ui::panels::DatRowTemplates;
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

    // FellowshipStatsTemplateLiveDatTests.ResolveTemplate_ComposesLevelSlashSharePercent
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_fellowship_stats_entry_composes_level_slash_share() {
        let c = content();
        let text = c.strings().resolve_template_hash(
            0x2300_0001,
            0x003B_5A03,
            &HashMap::from([(5_286_556, "12".to_owned()), (174_673_717, "31".to_owned())]),
        );
        assert_eq!(text.as_deref(), Some("12/31%"));
    }

    // AllegianceExperiencePassedUpTemplateLiveDatTests.ResolveTemplate_PlacesTheGroupedValueInTheAuthoredEntry
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_experience_passed_up_entry_holds_the_grouped_value() {
        let c = content();
        let text = c.strings().resolve_template(
            0x2300_0001,
            "ID_Allegiance_VassalExperiencePassedUp",
            &HashMap::from([(compute_hash("VALUE"), "1,500,000".to_owned())]),
        );
        assert_eq!(text.as_deref(), Some("1,500,000"));
    }

    // SocialPanelControllerTests.Bind_RootBuildsAsUiTabPanel / TabTable_MatchesLiveDatPairing_AllegianceIsDefault
    // and SocialPanelLiveMountProbeTests on the installed layout.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_social_panel_imports_binds_and_shows_its_pages() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import_rooted(
            &mut host.root,
            &c,
            social::HOST_LAYOUT_ID,
            social::SLOT_ELEMENT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("social layout");
        let tabs = host
            .root
            .widget::<TabPanel>(layout.root)
            .expect("the root is a tab panel")
            .tabs()
            .to_vec();
        assert_eq!(tabs.len(), 4);
        for (button, page, default) in [
            (0x1000_028C, allegiance::PAGE_ID, true),
            (0x1000_028E, fellowship::PAGE_ID, false),
            (0x1000_0512, friends::FRIENDS_PAGE_ID, false),
            (0x1000_053B, friends::SQUELCH_PAGE_ID, false),
        ] {
            assert!(tabs.contains(&TabEntry {
                button,
                page,
                is_default: default
            }));
        }
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        host.root
            .register_window(WINDOW, layout.root, None, 0)
            .unwrap();
        let strings: &dyn PanelStrings = c.strings();
        let mut ctl =
            SocialPanelController::bind(&mut host.root, &layout, Some(strings)).expect("binds");
        assert!(ctl.fellowship().is_some());
        assert!(ctl.allegiance().is_some());
        assert!(ctl.friends().is_some());
        assert!(ctl.squelch().is_some());
        ctl.activate_tabs(&mut host.root);
        assert!(ctl.is_showing_allegiance(&host.root));
        host.attach_controller(WINDOW, Box::new(ctl)).unwrap();
        host.root.show_window(WINDOW);
        host.pump();

        let options = options();
        let friends_state = {
            let mut f = FriendsState::new();
            f.apply(&FriendsUpdate {
                update_type: FriendsUpdateType::FULL,
                entries: vec![friend(1, "Alice", true), friend(2, "Bob", false)],
            });
            f
        };
        let squelch_state = SquelchState::new();
        let fellowship_view = FellowshipView {
            in_fellowship: true,
            revision: 1,
            name: "The Wanderers".into(),
            leader_guid: SELF,
            share_xp: true,
            even_xp_split: true,
            members: vec![fellow(SELF, "Me", 20), fellow(0x5000_0001, "Alice", 12)],
            ..FellowshipView::default()
        };
        let profile = tree(
            member(200, 0, "Queen Alice"),
            vec![member(SELF, 200, "Me"), member(401, SELF, "Vassal One")],
        );
        let view = SocialView {
            player: SELF,
            selected: None,
            in_world: true,
            options: &options,
            friends: &friends_state,
            squelch: &squelch_state,
            fellowship: &fellowship_view,
            allegiance: AllegianceSource {
                profile: Some(&profile),
                rank: 1,
                revision: 1,
                rank_quality: Some(1),
            },
            experience_to_raise: None,
            strings: Some(strings),
            name_of: None,
        };
        let mut rows = DatRowTemplates::new(&c, c.font(DEFAULT_FONT_ID));
        host.with_controller::<SocialPanelController, _>(WINDOW, |w, ui| {
            w.update(ui, &view, &mut rows);
            w.show_fellowship(ui);
            w.update(ui, &view, &mut rows);
        });
        let find = |id| host.root.find_descendant(layout.root, id).unwrap();
        // The fellowship roster: two rows from the authored template.
        let list = find(fellowship::LIST_BOX_ID);
        let lb = host.root.widget::<ListBox>(list).unwrap();
        assert_eq!(lb.item_count(&host.root), 2);
        let row = host.root.children(lb.viewport_id().unwrap())[1];
        let name = host
            .root
            .find_descendant(row, fellowship::ROW_NAME_ID)
            .expect("fellow row name");
        assert_eq!(text_of(&host.root, name), "Alice");
        let stats = host
            .root
            .find_descendant(row, fellowship::ROW_STATS_ID)
            .expect("fellow row stats");
        assert_eq!(text_of(&host.root, stats), "12/75%");
        // The vassal roster and the monarch block.
        let vassals = find(allegiance::VASSAL_LIST_ID);
        assert_eq!(
            host.root
                .widget::<ListBox>(vassals)
                .unwrap()
                .item_count(&host.root),
            1
        );
        assert_eq!(
            text_of(&host.root, find(allegiance::MONARCH_NAME_ID)),
            "Queen Alice"
        );
        // The open button's authored caption.
        let open = find(fellowship::OPEN_BUTTON_ID);
        assert!(host.root.widget::<Button>(open).unwrap().label.is_some());
        let drawn = host.draw((1024.0, 768.0), &c);
        assert!(!drawn.main.is_empty());
        assert!(c.take_errors().is_empty());
    }
}
