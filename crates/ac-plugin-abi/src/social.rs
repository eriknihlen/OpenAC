//! Fellowship, allegiance, login and logout, recalls, the selection,
//! dialogs and recovery (API group J).

use crate::world::Position;
use crate::{wire_enum, wire_record};

wire_record! {
    /// One fellow.
    pub struct FellowMember {
        /// Their object id.
        pub object_id: u32,
        /// Their name.
        pub name: String,
        /// Health now.
        pub current_health: u32,
        /// Health at most.
        pub max_health: u32,
        /// Stamina now.
        pub current_stamina: u32,
        /// Stamina at most.
        pub max_stamina: u32,
        /// Mana now.
        pub current_mana: u32,
        /// Mana at most.
        pub max_mana: u32,
        /// How far from the character, metres (0 for the character;
        /// infinite on the roster for a fellow the client cannot see).
        pub distance: f32,
        /// They share loot.
        pub share_loot: bool,
        /// Their level, as the fellowship roster last reported it.
        pub level: u32,
        /// Seconds since their vitals were last sampled live, if ever.
        pub vitals_age_seconds: Option<f64>,
    }
}

wire_record! {
    /// The character's fellowship.
    pub struct FellowshipState {
        /// The character is in a fellowship.
        pub is_in_fellowship: bool,
        /// Its name.
        pub name: String,
        /// The leader's object id.
        pub leader_id: u32,
        /// Anyone may recruit.
        pub is_open: bool,
        /// Locked.
        pub is_locked: bool,
        /// How many members, the character included.
        pub member_count: i32,
        /// The members share the experience they earn (false outside a
        /// fellowship).
        pub shares_experience: bool,
        /// Shared experience is split evenly; false when it is split by
        /// level because the members' levels are too far apart. Meaningful
        /// only while [`Self::shares_experience`] is true; false outside a
        /// fellowship.
        pub splits_experience_evenly: bool,
    }
}

wire_enum! {
    /// The answer to a fellowship command.
    pub enum FellowshipStatus: "fellowship status" {
        /// Not in the world.
        Unavailable = 0,
        /// Sent.
        Accepted = 1,
        /// The client refused it.
        Rejected = 2,
    }
}

wire_record! {
    /// Create a fellowship.
    pub struct FellowshipCreate {
        /// Its name.
        pub name: String,
        /// Experience is shared.
        pub share_experience: bool,
    }
}

wire_record! {
    /// The character's allegiance.
    pub struct AllegianceState {
        /// The character is in the world.
        pub is_available: bool,
        /// Grows with every change.
        pub revision: i64,
        /// The server described the allegiance.
        pub is_known: bool,
        /// Its name.
        pub name: String,
        /// The character's rank.
        pub rank: u32,
        /// Members in the whole allegiance.
        pub member_count: u32,
        /// The character's direct vassals.
        pub vassal_count: u32,
        /// The monarch, 0 for none.
        pub monarch_id: u32,
        /// Who the monarch is (the character itself when it heads the
        /// allegiance); none outside an allegiance or before the server
        /// stated it.
        pub monarch: Option<AllegianceMember>,
        /// Who the character is sworn to; none for a monarch, outside an
        /// allegiance or before the server stated it.
        pub patron: Option<AllegianceMember>,
        /// The characters sworn directly to this one, as the server listed
        /// them.
        pub vassals: Vec<AllegianceMember>,
    }
}

wire_record! {
    /// One member of the character's allegiance, as the server last
    /// described them.
    pub struct AllegianceMember {
        /// The member's character.
        pub object_id: u32,
        /// Its name.
        pub name: String,
        /// Its rank in the allegiance.
        pub rank: u32,
        /// Its level.
        pub level: u32,
        /// Heritage as the server numbers it: 1 Aluvian, 2 Gharu'ndim, 3 Sho,
        /// 4 Viamontian, 5 Shadowbound, 6 Gearknight, 7 Tumerok, 8 Lugian,
        /// 9 Empyrean, 10 Penumbraen, 11 Undead, 12 Olthoi, 13 Olthoi acid;
        /// 0 when not said.
        pub heritage_group: i32,
        /// Gender as the server numbers it: 1 male, 2 female; 0 when not
        /// said.
        pub gender: i32,
        /// Logged in when the server said so.
        pub is_online: bool,
    }
}

wire_enum! {
    /// The answer to an allegiance command.
    pub enum AllegianceStatus: "allegiance status" {
        /// Not in the world.
        Unavailable = 0,
        /// Sent.
        Sent = 1,
        /// Not someone the command can name.
        InvalidTarget = 2,
        /// The client did not send it.
        Refused = 3,
    }
}

wire_record! {
    /// The answer to an allegiance command.
    pub struct AllegianceResult {
        /// The verdict.
        pub status: AllegianceStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_record! {
    /// One character on the account's list.
    pub struct LoginCharacter {
        /// Its object id.
        pub object_id: u32,
        /// Its name.
        pub name: String,
        /// Its slot on the list.
        pub active_index: i32,
        /// It waits to be deleted.
        pub is_pending_delete: bool,
    }
}

wire_record! {
    /// The account's characters, the next login and whether a logout can
    /// be asked for.
    pub struct LoginState {
        /// A session is there.
        pub is_available: bool,
        /// The character entered at the next return to the list, 0 for
        /// none.
        pub next_login_id: u32,
        /// A logout can be asked for now.
        pub can_request_logout: bool,
        /// The characters, in list order.
        pub roster: Vec<LoginCharacter>,
    }
}

wire_enum! {
    /// A recall.
    pub enum RecallKind: "recall kind" {
        /// To the lifestone.
        Lifestone = 0,
        /// To the marketplace.
        Marketplace = 1,
        /// To the character's house.
        House = 2,
        /// To the allegiance's mansion.
        Mansion = 3,
        /// To the allegiance hometown.
        Allegiance = 4,
    }
}

wire_enum! {
    /// How a recall was answered.
    pub enum RecallStatus: "recall status" {
        /// Not in the world.
        Unavailable = 0,
        /// This client cannot.
        Unsupported = 1,
        /// Asked for.
        Started = 2,
        /// The client refused it.
        Refused = 3,
    }
}

wire_record! {
    /// A recall destination the client knows.
    pub struct RecallLocation {
        /// Which recall.
        pub kind: RecallKind,
        /// Where it lands.
        pub position: Position,
        /// Its name.
        pub name: String,
        /// Grows when the place is learned again.
        pub revision: i64,
        /// The place is known.
        pub is_known: bool,
    }
}

wire_record! {
    /// The latest recall a plugin asked for.
    pub struct RecallRequest {
        /// Grows with every request.
        pub revision: i64,
        /// Which recall.
        pub kind: RecallKind,
        /// How it was answered.
        pub status: RecallStatus,
    }
}

wire_record! {
    /// Recalls: the latest request and the destinations known.
    pub struct RecallState {
        /// The character is in the world.
        pub is_available: bool,
        /// The latest request.
        pub last_request: RecallRequest,
        /// The destinations known.
        pub locations: Vec<RecallLocation>,
    }
}

wire_record! {
    /// The answer to a recall.
    pub struct RecallResult {
        /// The verdict.
        pub status: RecallStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

wire_enum! {
    /// A selection step.
    pub enum SelectionAction: "selection action" {
        /// Back to the previous selection.
        PreviousSelection = 0,
        /// The previous other player.
        PreviousPlayer = 1,
        /// The next other player.
        NextPlayer = 2,
    }
}

wire_record! {
    /// The selection.
    pub struct SelectionState {
        /// What is selected.
        pub selected: Option<u32>,
        /// What was selected before.
        pub previous: Option<u32>,
    }
}

wire_record! {
    /// What holds the character busy.
    pub struct BusyState {
        /// Uses awaiting the server's done.
        pub busy_count: i32,
        /// An inventory request is in flight.
        pub pending_inventory: bool,
        /// The object whose description is awaited, 0 for none.
        pub awaiting_appraisal: u32,
        /// The last use's source.
        pub use_source: u32,
        /// The last use's target.
        pub use_target: u32,
        /// The last use's completion is awaited.
        pub awaiting_use_completion: bool,
    }
}

wire_record! {
    /// The answer to a recovery.
    pub struct RecoveryResult {
        /// Taken.
        pub accepted: bool,
        /// The busy count before.
        pub previous_count: i32,
        /// The busy count after.
        pub current_count: i32,
        /// What was done, in words.
        pub message: String,
    }
}
