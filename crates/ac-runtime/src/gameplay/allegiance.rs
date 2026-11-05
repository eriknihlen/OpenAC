//! The player's allegiance: the profile the server pushes (patron,
//! vassals, monarch, rank), and the one request the client makes on its own
//! when the character arrives in the world.

use ac_proto::allegiance::{AllegianceMember, AllegianceProfile, AllegianceUpdate};

/// One member as a host shows it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AllegianceMemberView {
    /// Character id.
    pub guid: u32,
    /// Name.
    pub name: String,
    /// Online now.
    pub online: bool,
    /// Allegiance rank.
    pub rank: u16,
    /// Level (0 when not sent).
    pub level: u32,
    /// Heritage as the server numbers it (0 when not sent).
    pub heritage_group: u8,
    /// Gender as the server numbers it (0 when not sent).
    pub gender: u8,
}

/// The allegiance as a host shows it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AllegianceView {
    /// Counts changes.
    pub revision: u64,
    /// The server stated the allegiance this session.
    pub has_server_seed: bool,
    /// The player's rank.
    pub rank: u32,
    /// Members in the whole allegiance.
    pub total_members: u32,
    /// The player's direct vassals.
    pub total_vassals: u32,
    /// The allegiance's name.
    pub name: String,
    /// The monarch.
    pub monarch: Option<AllegianceMemberView>,
    /// The player's patron.
    pub patron: Option<AllegianceMemberView>,
    /// The player's vassals.
    pub vassals: Vec<AllegianceMemberView>,
}

/// The allegiance owner.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AllegianceState {
    profile: Option<AllegianceProfile>,
    rank: u32,
    has_server_seed: bool,
    asked_on_entry: bool,
    revision: u64,
}

impl AllegianceState {
    /// The character arrived in the world: true the first time this
    /// session, when the update request must go out (the server says
    /// nothing about an allegiance until asked).
    pub fn note_entered_world(&mut self) -> bool {
        if self.asked_on_entry {
            return false;
        }
        self.asked_on_entry = true;
        true
    }

    /// The server stated the allegiance this session.
    #[must_use]
    pub fn has_server_seed(&self) -> bool {
        self.has_server_seed
    }

    /// The profile.
    #[must_use]
    pub fn profile(&self) -> Option<&AllegianceProfile> {
        self.profile.as_ref()
    }

    /// The player's rank, as the last update gave it.
    #[must_use]
    pub fn rank(&self) -> u32 {
        self.rank
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// The allegiance profile pushed by the server.
    pub fn apply_update(&mut self, update: &AllegianceUpdate) {
        self.profile = Some(update.profile.clone());
        self.rank = update.rank;
        self.has_server_seed = true;
        self.revision += 1;
    }

    /// A member logged in or out (the revision moves; the line is chat).
    pub fn apply_login_notification(&mut self) {
        self.revision += 1;
    }

    /// A swear or break finished (or was aborted).
    pub fn apply_update_done(&mut self) {
        self.revision += 1;
    }

    /// The allegiance as owned data, seen from `player`.
    #[must_use]
    pub fn view(&self, player: u32) -> AllegianceView {
        let Some(profile) = &self.profile else {
            return AllegianceView {
                revision: self.revision,
                ..AllegianceView::default()
            };
        };
        AllegianceView {
            revision: self.revision,
            has_server_seed: self.has_server_seed,
            rank: self.rank,
            total_members: profile.total_members,
            total_vassals: profile.total_vassals,
            name: profile.allegiance_name.clone(),
            monarch: profile.monarch.as_ref().map(member_view),
            patron: profile.find_patron(player).map(member_view),
            vassals: profile.find_vassals(player).map(member_view).collect(),
        }
    }

    /// The session ended: everything is asked again next time.
    pub fn reset_session(&mut self) {
        let revision = self.revision + u64::from(self.profile.is_some() || self.has_server_seed);
        *self = Self {
            revision,
            ..Self::default()
        };
    }
}

fn member_view(m: &AllegianceMember) -> AllegianceMemberView {
    AllegianceMemberView {
        guid: m.character_id,
        name: m.name.clone(),
        online: m.is_logged_in,
        rank: m.rank,
        level: m.level,
        heritage_group: m.heritage_group,
        gender: m.gender,
    }
}

/// The lines `/allegiance info` (the info response) prints, as the client
/// words them.
#[must_use]
pub fn info_lines(target: u32, profile: &AllegianceProfile) -> Vec<String> {
    let Some(me) = profile.find_data(target) else {
        return Vec::new();
    };
    let online = |m: &AllegianceMember| if m.is_logged_in { " *" } else { "" };
    let mut lines = vec![
        "Note: An asterisk (*) indicates that the character is currently online.".to_owned(),
        format!("Allegiance information for {}{}:", me.name, online(me)),
    ];
    if let Some(patron) = profile.find_patron(target) {
        lines.push(format!("   Patron: {}{}", patron.name, online(patron)));
    }
    let mut header = false;
    for vassal in profile.find_vassals(target) {
        if !header {
            lines.push("   Vassals: ".to_owned());
            header = true;
        }
        lines.push(format!("      {}{}", vassal.name, online(vassal)));
    }
    lines
}

#[cfg(test)]
#[path = "allegiance_tests.rs"]
mod tests;
