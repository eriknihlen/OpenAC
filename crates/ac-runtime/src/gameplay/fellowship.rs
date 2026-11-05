//! The player's fellowship: the roster the server sends, member updates,
//! departures, the even-experience-split rule, the vitals subscription
//! (panel open or vitals asked for), and the leader hand-off before a quit.

use std::collections::HashMap;

use ac_proto::fellowship::{FellowMember, FellowshipFullUpdate, FellowshipUpdateFellow};

/// How long a departed member may rejoin a locked fellowship, seconds.
pub const DEPARTED_GRACE_SECONDS: i64 = 900;

/// One member as a host shows it.
#[derive(Debug, Clone, PartialEq)]
pub struct FellowView {
    /// Character id.
    pub guid: u32,
    /// Name.
    pub name: String,
    /// Level.
    pub level: u32,
    /// Health, current over maximum.
    pub health: (u32, u32),
    /// Stamina.
    pub stamina: (u32, u32),
    /// Mana.
    pub mana: (u32, u32),
    /// Shares loot.
    pub share_loot: bool,
    /// Seconds since the last live vitals sample, if any.
    pub vitals_age_seconds: Option<f64>,
}

/// The fellowship as a host shows it.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct FellowshipView {
    /// Counts changes.
    pub revision: u64,
    /// The player is in a fellowship.
    pub in_fellowship: bool,
    /// Its name.
    pub name: String,
    /// The leader.
    pub leader_guid: u32,
    /// Experience is shared.
    pub share_xp: bool,
    /// Experience is split evenly.
    pub even_xp_split: bool,
    /// Anyone may recruit.
    pub open: bool,
    /// Locked.
    pub locked: bool,
    /// The members, in the order the server listed them.
    pub members: Vec<FellowView>,
}

/// A member leaving, as the server announces it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Departure {
    /// This member quit.
    Quit(u32),
    /// This member was dismissed.
    Dismiss(u32),
    /// The fellowship was disbanded.
    Disband,
}

/// The fellowship owner.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct FellowshipState {
    /// In the order the server listed them (a leader hand-off picks the
    /// first other member).
    members: Vec<FellowMember>,
    vitals_updated_at: HashMap<u32, f64>,
    departed: HashMap<u32, i32>,
    panel_visible: bool,
    vitals_requested: bool,
    sent_vitals_subscription: bool,
    name: String,
    leader_guid: u32,
    share_xp: bool,
    even_xp_split: bool,
    open: bool,
    locked: bool,
    in_fellowship: bool,
    revision: u64,
}

impl FellowshipState {
    /// The player is in a fellowship.
    #[must_use]
    pub fn is_in_fellowship(&self) -> bool {
        self.in_fellowship
    }

    /// The leader (0 outside a fellowship).
    #[must_use]
    pub fn leader_guid(&self) -> u32 {
        if self.in_fellowship {
            self.leader_guid
        } else {
            0
        }
    }

    /// Experience is split evenly.
    #[must_use]
    pub fn even_xp_split(&self) -> bool {
        self.even_xp_split
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// One member.
    #[must_use]
    pub fn member(&self, guid: u32) -> Option<&FellowMember> {
        self.members.iter().find(|m| m.guid == guid)
    }

    /// The members' ids, in the fellowship's order.
    pub fn member_guids(&self) -> impl Iterator<Item = u32> + '_ {
        self.members.iter().map(|m| m.guid)
    }

    /// How many members.
    #[must_use]
    pub fn member_count(&self) -> usize {
        self.members.len()
    }

    /// The whole fellowship arrived. A roster carries vitals, but only a
    /// per-fellow update counts as a live sample.
    pub fn apply_full_update(&mut self, update: &FellowshipFullUpdate) {
        self.members.clear();
        for member in &update.members {
            self.upsert(member.clone());
        }
        let members = &self.members;
        self.vitals_updated_at
            .retain(|guid, _| members.iter().any(|m| m.guid == *guid));
        self.departed = update
            .departed
            .iter()
            .map(|d| (d.guid, d.departed_timestamp))
            .collect();
        self.name.clone_from(&update.name);
        self.leader_guid = update.leader_guid;
        self.share_xp = update.share_xp;
        self.even_xp_split = update.even_xp_split;
        self.open = update.open_fellow;
        self.locked = update.locked;
        self.in_fellowship = true;
        self.bump();
    }

    /// One member changed (or joined). In a locked fellowship only a member
    /// who left less than [`DEPARTED_GRACE_SECONDS`] ago may join.
    /// `now_unix_s` is wall-clock seconds; `now` the session clock.
    pub fn apply_update_fellow(
        &mut self,
        update: &FellowshipUpdateFellow,
        now_unix_s: i64,
        now: f64,
    ) {
        if !self.in_fellowship {
            return;
        }
        let guid = update.member.guid;
        let existing = self.members.iter().position(|m| m.guid == guid);
        if existing.is_none() && self.locked && !self.admissible_while_locked(guid, now_unix_s) {
            return;
        }
        self.upsert(update.member.clone());
        self.vitals_updated_at.insert(guid, now);
        self.recalculate_even_xp_split();
        self.bump();
    }

    fn upsert(&mut self, member: FellowMember) {
        match self.members.iter_mut().find(|m| m.guid == member.guid) {
            Some(slot) => *slot = member,
            None => self.members.push(member),
        }
    }

    fn admissible_while_locked(&self, guid: u32, now_unix_s: i64) -> bool {
        self.departed
            .get(&guid)
            .is_some_and(|&left| now_unix_s - i64::from(left) <= DEPARTED_GRACE_SECONDS)
    }

    fn recalculate_even_xp_split(&mut self) {
        if !self.share_xp {
            return;
        }
        let min_level = self
            .members
            .iter()
            .map(|m| m.level)
            .min()
            .unwrap_or(u32::MAX);
        let max_level = self.members.iter().map(|m| m.level).max().unwrap_or(0);
        self.even_xp_split = true;
        let Some(leader) = self.members.iter().find(|m| m.guid == self.leader_guid) else {
            return;
        };
        if min_level < 50 && (max_level > leader.level + 5 || min_level + 5 < leader.level) {
            self.even_xp_split = false;
        }
    }

    /// The fellowship panel opened or closed. `Some(subscribe)` when the
    /// vitals subscription must be sent.
    pub fn set_panel_visible(&mut self, visible: bool) -> Option<bool> {
        self.panel_visible = visible;
        self.resolve_vitals_subscription()
    }

    /// Something asked for member vitals (or stopped asking). `Some` as
    /// [`Self::set_panel_visible`].
    pub fn set_vitals_requested(&mut self, requested: bool) -> Option<bool> {
        self.vitals_requested = requested;
        self.resolve_vitals_subscription()
    }

    fn resolve_vitals_subscription(&mut self) -> Option<bool> {
        let subscribe = self.panel_visible || self.vitals_requested;
        if self.sent_vitals_subscription == subscribe {
            return None;
        }
        self.sent_vitals_subscription = subscribe;
        Some(subscribe)
    }

    /// The line the player reads for a departure, worded from the roster
    /// as it stands before the departure is applied. `None` outside a
    /// fellowship or for someone not on the roster.
    #[must_use]
    pub fn departure_notice(&self, departure: Departure, player: u32) -> Option<String> {
        if !self.in_fellowship {
            return None;
        }
        let name = |guid: u32| self.member(guid).map(|m| m.name.as_str());
        match departure {
            Departure::Quit(guid) if guid == player => Some(format!(
                "You are no longer a member of the {} Fellowship.",
                self.name
            )),
            Departure::Quit(guid) => name(guid).map(|n| format!("{n} has left your Fellowship.")),
            Departure::Dismiss(guid) if guid == player => name(self.leader_guid)
                .map(|leader| format!("{leader} has dismissed you from the Fellowship.")),
            Departure::Dismiss(guid) => name(guid).map(|n| {
                if self.leader_guid == player {
                    format!("You dismiss {n} from your Fellowship.")
                } else {
                    format!("{n} has been dismissed from the Fellowship.")
                }
            }),
            Departure::Disband if self.leader_guid == player => {
                Some("You have disbanded your Fellowship.".to_owned())
            }
            Departure::Disband => name(self.leader_guid)
                .map(|leader| format!("{leader} has disbanded your Fellowship.")),
        }
    }

    /// A member quit; the player's own quit ends the fellowship here.
    pub fn apply_quit(&mut self, quitter: u32, player: u32) {
        self.apply_departure(quitter, player);
    }

    /// A member was dismissed; the player's own dismissal ends it.
    pub fn apply_dismiss(&mut self, dismissed: u32, player: u32) {
        self.apply_departure(dismissed, player);
    }

    fn apply_departure(&mut self, guid: u32, player: u32) {
        if !self.in_fellowship {
            return;
        }
        if guid == player {
            self.clear();
            return;
        }
        if let Some(index) = self.members.iter().position(|m| m.guid == guid) {
            self.members.remove(index);
            self.vitals_updated_at.remove(&guid);
            self.recalculate_even_xp_split();
            self.bump();
        }
    }

    /// The fellowship was disbanded.
    pub fn apply_disband(&mut self) {
        self.clear();
    }

    /// A leader quitting (not disbanding) first hands the lead to another
    /// member: `Some(new leader)` when that request must go out first.
    #[must_use]
    pub fn requires_leader_handoff_before_quit(&self, player: u32, disband: bool) -> Option<u32> {
        if disband || !self.in_fellowship || self.leader_guid != player {
            return None;
        }
        self.members
            .iter()
            .map(|m| m.guid)
            .find(|&guid| guid != player)
    }

    /// The fellowship as owned data; vitals ages against session time `now`.
    #[must_use]
    pub fn view(&self, now: f64) -> FellowshipView {
        FellowshipView {
            revision: self.revision,
            in_fellowship: self.in_fellowship,
            name: self.name.clone(),
            leader_guid: self.leader_guid,
            share_xp: self.share_xp,
            even_xp_split: self.even_xp_split,
            open: self.open,
            locked: self.locked,
            members: self
                .members
                .iter()
                .map(|m| FellowView {
                    guid: m.guid,
                    name: m.name.clone(),
                    level: m.level,
                    health: (m.current_health, m.max_health),
                    stamina: (m.current_stamina, m.max_stamina),
                    mana: (m.current_mana, m.max_mana),
                    share_loot: m.share_loot != 0,
                    vitals_age_seconds: self
                        .vitals_updated_at
                        .get(&m.guid)
                        .map(|&at| (now - at).max(0.0)),
                })
                .collect(),
        }
    }

    /// The session ended.
    pub fn reset_session(&mut self) {
        self.clear();
    }

    fn clear(&mut self) {
        let changed = !self.members.is_empty()
            || self.in_fellowship
            || !self.name.is_empty()
            || self.leader_guid != 0
            || self.share_xp
            || self.even_xp_split
            || self.open
            || self.locked;
        let revision = self.revision;
        *self = Self {
            revision,
            ..Self::default()
        };
        if changed {
            self.bump();
        }
    }

    fn bump(&mut self) {
        self.revision += 1;
    }
}

#[cfg(test)]
#[path = "fellowship_tests.rs"]
mod tests;
