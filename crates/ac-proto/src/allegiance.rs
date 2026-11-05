//! Allegiances: swearing, breaking, the allegiance tree the server sends,
//! and the officer, name, lock, ban, chat and message-of-the-day commands.
//!
//! Game-action builders take the session's next action sequence number
//! and are sent on the UI queue.

use std::collections::HashSet;

use ac_bits::Reader;

use crate::client_commands::string_u32_action;
use crate::wire::{
    action, bare_action, check_count, read_bool32, read_string, string_action, u32_action,
};
use crate::{GameActionType, ProtoError, Result};

/// Record bit: the member is online.
const LOGGED_IN: u32 = 0x01;
#[cfg(test)]
/// Record bit: the record carries an allegiance age.
const HAS_ALLEGIANCE_AGE: u32 = 0x04;
/// Record bit: the record carries a level.
const HAS_PACKED_LEVEL: u32 = 0x08;
/// Record bit: the member may pass experience up.
const MAY_PASSUP_EXPERIENCE: u32 = 0x10;

/// One member in an allegiance tree.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AllegianceMember {
    /// Character id.
    pub character_id: u32,
    /// Patron's id (0 for the monarch).
    pub parent_guid: u32,
    /// Online now.
    pub is_logged_in: bool,
    /// Name.
    pub name: String,
    /// Allegiance rank.
    pub rank: u16,
    /// Level, when the record carries one (else 0).
    pub level: u32,
    /// Loyalty skill.
    pub loyalty: u16,
    /// Leadership skill.
    pub leadership: u16,
    /// Experience cached for the patron.
    pub cp_cached: u32,
    /// Experience passed up.
    pub cp_tithed: u32,
    /// Gender.
    pub gender: u8,
    /// Heritage group.
    pub heritage_group: u8,
    /// The member may pass experience up. Always false for the monarch.
    pub may_passup_experience: bool,
}

impl AllegianceMember {
    fn read(r: &mut Reader<'_>, parent_guid: u32) -> Result<Self> {
        let character_id = r.u32()?;
        let cp_cached = r.u32()?;
        let cp_tithed = r.u32()?;
        let bits = r.u32()?;
        let gender = r.u8()?;
        let heritage_group = r.u8()?;
        let rank = r.u16()?;
        let level = if bits & HAS_PACKED_LEVEL != 0 {
            r.u32()?
        } else {
            0
        };
        let loyalty = r.u16()?;
        let leadership = r.u16()?;
        // Time online and allegiance age when the record has an age, else
        // the older time-online double; eight bytes either way, not used.
        r.skip(8)?;
        let name = read_string(r)?;
        Ok(Self {
            character_id,
            parent_guid,
            is_logged_in: bits & LOGGED_IN != 0,
            name,
            rank,
            level,
            loyalty,
            leadership,
            cp_cached,
            cp_tithed,
            gender,
            heritage_group,
            // A record without a level is an older record, which always
            // allowed passing experience up.
            may_passup_experience: bits & MAY_PASSUP_EXPERIENCE != 0
                || bits & HAS_PACKED_LEVEL == 0,
        })
    }
}

/// The allegiance description shared by the update and info events. Fields
/// a record version predates are left at their defaults.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AllegianceProfile {
    /// Members in the whole allegiance.
    pub total_members: u32,
    /// Direct vassals.
    pub total_vassals: u32,
    /// Records in the tree, the monarch included.
    pub record_count: u16,
    /// Record version; later versions add fields.
    pub old_version: u16,
    /// Message of the day (version 3 and up).
    pub motd: String,
    /// Who set it.
    pub motd_set_by: String,
    /// Allegiance chat room (version 4 and up).
    pub chat_room_id: u32,
    /// Allegiance name (version 8 and up).
    pub allegiance_name: String,
    /// When the name was last set.
    pub name_last_set_time: u32,
    /// The allegiance is locked (version 10 and up).
    pub is_locked: bool,
    /// Pre-approved vassal (version 11 and up).
    pub approved_vassal: u32,
    /// The monarch, when the tree is not empty.
    pub monarch: Option<AllegianceMember>,
    /// Every other record, in wire order (each after its patron).
    pub records: Vec<AllegianceMember>,
}

impl AllegianceProfile {
    /// Read the description, version gates and tree included. A tree whose
    /// monarch has id 0, or whose record names a patron not yet seen, its
    /// own id as patron, id 0 or a repeated id, is an error for the whole
    /// message.
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let mut p = Self {
            total_members: r.u32()?,
            total_vassals: r.u32()?,
            record_count: r.u16()?,
            old_version: r.u16()?,
            ..Self::default()
        };
        let version = p.old_version;
        if version >= 6 {
            // Officers: 16-bit count, 16-bit buckets, id and level each.
            let count = r.u16()?;
            let _buckets = r.u16()?;
            r.skip(usize::from(count) * 8)?;
        } else if version >= 1 {
            // The single spokesperson of older records.
            r.skip(4)?;
        }
        if version >= 9 {
            let count = r.i32()?;
            if count > 0 {
                check_count(r, count as u32, 2, "allegiance officer titles")?;
                for _ in 0..count {
                    read_string(r)?;
                }
            }
        }
        if version >= 2 {
            // Monarch and spokesperson broadcast times and counts.
            r.skip(16)?;
        }
        if version >= 3 {
            p.motd = read_string(r)?;
            p.motd_set_by = read_string(r)?;
        }
        if version >= 4 {
            p.chat_room_id = r.u32()?;
        }
        if version >= 7 {
            // Bind point: cell, origin, rotation.
            r.skip(32)?;
        }
        if version >= 8 {
            p.allegiance_name = read_string(r)?;
            p.name_last_set_time = r.u32()?;
        }
        if version >= 10 {
            p.is_locked = read_bool32(r)?;
        }
        if version >= 11 {
            p.approved_vassal = r.u32()?;
        }
        if p.record_count > 0 {
            let mut monarch = AllegianceMember::read(r, 0)?;
            if monarch.character_id == 0 {
                return Err(ProtoError::Inconsistent {
                    what: "allegiance tree",
                });
            }
            let mut known = HashSet::from([monarch.character_id]);
            for _ in 1..p.record_count {
                let parent = r.u32()?;
                let record = AllegianceMember::read(r, parent)?;
                if record.character_id == 0
                    || !known.contains(&parent)
                    || parent == record.character_id
                    || !known.insert(record.character_id)
                {
                    return Err(ProtoError::Inconsistent {
                        what: "allegiance tree",
                    });
                }
                p.records.push(record);
            }
            monarch.may_passup_experience = false;
            p.monarch = Some(monarch);
        }
        Ok(p)
    }

    /// The record of `guid`, the monarch included.
    #[must_use]
    pub fn find_data(&self, guid: u32) -> Option<&AllegianceMember> {
        self.monarch
            .iter()
            .chain(&self.records)
            .find(|m| m.character_id == guid)
    }

    /// The patron of `guid` (`None` for the monarch or an unknown id).
    #[must_use]
    pub fn find_patron(&self, guid: u32) -> Option<&AllegianceMember> {
        if self
            .monarch
            .as_ref()
            .is_some_and(|m| m.character_id == guid)
        {
            return None;
        }
        let record = self.records.iter().find(|m| m.character_id == guid)?;
        self.find_data(record.parent_guid)
    }

    /// The direct vassals of `guid`, last on the wire first (the order the
    /// tree lists them in).
    pub fn find_vassals(&self, guid: u32) -> impl Iterator<Item = &AllegianceMember> {
        self.records
            .iter()
            .rev()
            .filter(move |m| m.parent_guid == guid)
    }
}

/// The player's allegiance ([`crate::GameEventType::ALLEGIANCE_UPDATE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AllegianceUpdate {
    /// The player's rank.
    pub rank: u32,
    /// The allegiance.
    pub profile: AllegianceProfile,
}

impl AllegianceUpdate {
    /// Parse the event payload: rank, then the allegiance description.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            rank: r.u32()?,
            profile: AllegianceProfile::read(&mut r)?,
        })
    }
}

/// Another player's allegiance
/// ([`crate::GameEventType::ALLEGIANCE_INFO_RESPONSE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AllegianceInfoResponse {
    /// Whose allegiance.
    pub target_guid: u32,
    /// The allegiance.
    pub profile: AllegianceProfile,
}

impl AllegianceInfoResponse {
    /// Parse the event payload: target, then the allegiance description.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            target_guid: r.u32()?,
            profile: AllegianceProfile::read(&mut r)?,
        })
    }
}

/// A member logged in or out
/// ([`crate::GameEventType::ALLEGIANCE_LOGIN_NOTIFICATION`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AllegianceLoginNotification {
    /// The member.
    pub character_guid: u32,
    /// True for a login.
    pub is_logged_in: bool,
}

impl AllegianceLoginNotification {
    /// Parse the event payload: member, logged-in word.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            character_guid: r.u32()?,
            is_logged_in: read_bool32(&mut r)?,
        })
    }
}

/// The error code of an update-done
/// ([`crate::GameEventType::ALLEGIANCE_UPDATE_DONE`]) or update-aborted
/// ([`crate::GameEventType::ALLEGIANCE_UPDATE_ABORTED`]) event.
pub fn parse_update_result(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// Swear allegiance to a patron.
#[must_use]
pub fn build_swear(sequence: u32, patron_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::SWEAR_ALLEGIANCE, patron_guid)
}

/// Break allegiance with a patron or a vassal.
#[must_use]
pub fn build_break(sequence: u32, target_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::BREAK_ALLEGIANCE, target_guid)
}

/// Kick a vassal: the break action aimed at the vassal.
#[must_use]
pub fn build_kick(sequence: u32, vassal_guid: u32) -> Vec<u8> {
    build_break(sequence, vassal_guid)
}

/// Turn allegiance panel updates on or off (a `u32` 1 or 0).
#[must_use]
pub fn build_update_request(sequence: u32, on: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::ALLEGIANCE_UPDATE_REQUEST,
        u32::from(on),
    )
}

/// Ask for a player's allegiance by name.
pub fn build_info_request(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::ALLEGIANCE_INFO_REQUEST,
        player_name,
    )
}

/// Break allegiance with a member and boot them; `account_boot` boots
/// every character of their account.
pub fn build_break_allegiance_boot(
    sequence: u32,
    player_name: &str,
    account_boot: bool,
) -> Result<Vec<u8>> {
    string_u32_action(
        sequence,
        GameActionType::BREAK_ALLEGIANCE_BOOT,
        player_name,
        u32::from(account_boot),
    )
}

/// Boot a member from allegiance chat, with a reason.
pub fn build_chat_boot(sequence: u32, player_name: &str, reason: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::ALLEGIANCE_CHAT_BOOT);
    w.string16l(player_name)?;
    w.string16l(reason)?;
    Ok(w.into_vec())
}

/// Gag (`true`) or ungag a member on allegiance chat.
pub fn build_chat_gag(sequence: u32, player_name: &str, enabled: bool) -> Result<Vec<u8>> {
    string_u32_action(
        sequence,
        GameActionType::ALLEGIANCE_CHAT_GAG,
        player_name,
        u32::from(enabled),
    )
}

/// Ban a player from the allegiance.
pub fn build_add_ban(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::ADD_ALLEGIANCE_BAN, player_name)
}

/// Lift a ban.
pub fn build_remove_ban(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::REMOVE_ALLEGIANCE_BAN, player_name)
}

/// List the bans.
#[must_use]
pub fn build_list_bans(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::LIST_ALLEGIANCE_BANS)
}

/// Make a member an officer of `officer_level`: name, then level.
pub fn build_set_officer(sequence: u32, player_name: &str, officer_level: u32) -> Result<Vec<u8>> {
    string_u32_action(
        sequence,
        GameActionType::SET_ALLEGIANCE_OFFICER,
        player_name,
        officer_level,
    )
}

/// Remove an officer.
pub fn build_remove_officer(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::REMOVE_ALLEGIANCE_OFFICER,
        player_name,
    )
}

/// List the officers.
#[must_use]
pub fn build_list_officers(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::LIST_ALLEGIANCE_OFFICERS)
}

/// Clear the officers.
#[must_use]
pub fn build_clear_officers(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_ALLEGIANCE_OFFICERS)
}

/// Name an officer level: level, then title.
pub fn build_set_officer_title(sequence: u32, officer_level: u32, title: &str) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::SET_ALLEGIANCE_OFFICER_TITLE);
    w.u32(officer_level);
    w.string16l(title)?;
    Ok(w.into_vec())
}

/// List the officer titles.
#[must_use]
pub fn build_list_officer_titles(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::LIST_ALLEGIANCE_OFFICER_TITLES)
}

/// Clear the officer titles.
#[must_use]
pub fn build_clear_officer_titles(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_ALLEGIANCE_OFFICER_TITLES)
}

/// Ask for the allegiance name.
#[must_use]
pub fn build_query_name(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::QUERY_ALLEGIANCE_NAME)
}

/// Set the allegiance name.
pub fn build_set_name(sequence: u32, name: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::SET_ALLEGIANCE_NAME, name)
}

/// Clear the allegiance name.
#[must_use]
pub fn build_clear_name(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_ALLEGIANCE_NAME)
}

/// Lock, unlock or query the lock (the action code as the server defines
/// it).
#[must_use]
pub fn build_lock_action(sequence: u32, lock_action: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::DO_ALLEGIANCE_LOCK_ACTION,
        lock_action,
    )
}

/// Pre-approve a vassal for a locked allegiance.
pub fn build_set_approved_vassal(sequence: u32, player_name: &str) -> Result<Vec<u8>> {
    string_action(
        sequence,
        GameActionType::SET_ALLEGIANCE_APPROVED_VASSAL,
        player_name,
    )
}

/// An allegiance house action (the action code as the server defines it).
#[must_use]
pub fn build_house_action(sequence: u32, house_action: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::DO_ALLEGIANCE_HOUSE_ACTION,
        house_action,
    )
}

/// Ask for the message of the day.
#[must_use]
pub fn build_query_motd(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::QUERY_MOTD)
}

/// Set the message of the day.
pub fn build_set_motd(sequence: u32, motd: &str) -> Result<Vec<u8>> {
    string_action(sequence, GameActionType::SET_MOTD, motd)
}

/// Clear the message of the day.
#[must_use]
pub fn build_clear_motd(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CLEAR_MOTD)
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `AllegianceProfileVersionGateTests.cs`, `AllegianceSmallEventsTests.cs`,
    //! `AllegianceRequestsTests.cs`, the allegiance parts of
    //! `ClientCommandResponsesTests.cs` and `ClientCommandRequestsTests.cs`,
    //! and the body shapes of `WorldSessionAdministrationCommandTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{BareBuilder, assert_every_prefix_fails, words};

    const MONARCH: u32 = 0x5000_0001;

    struct Rec(u32, u32, bool, &'static str);

    fn record(w: &mut Writer, rec: &Rec) {
        let mut bits = HAS_ALLEGIANCE_AGE | HAS_PACKED_LEVEL;
        if rec.2 {
            bits |= LOGGED_IN;
        }
        w.u32(rec.0);
        w.u32(0);
        w.u32(0);
        w.u32(bits);
        w.u8(0);
        w.u8(0);
        w.u16(1);
        w.u32(5);
        w.u16(0);
        w.u16(0);
        w.u32(0);
        w.u32(0);
        w.string16l(rec.3).unwrap();
    }

    fn profile(leading: u32, version: u16, records: &[Rec], officers: u16, titles: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(leading);
        w.u32(records.len() as u32);
        w.u32(0);
        w.u16(records.len() as u16);
        w.u16(version);
        if version >= 6 {
            w.u16(officers);
            w.u16(256);
            for i in 0..u32::from(officers) {
                w.u32(0x6000_0000 + i);
                w.u32(2);
            }
        } else if version >= 1 {
            w.u32(0xDEAD_BEEF);
        }
        if version >= 9 {
            w.u32(titles);
            for i in 0..titles {
                w.string16l(&format!("Title{i}")).unwrap();
            }
        }
        if version >= 2 {
            for word in [1, 2, 3, 4] {
                w.u32(word);
            }
        }
        if version >= 3 {
            w.string16l("TheMotd").unwrap();
            w.string16l("MotdSetter").unwrap();
        }
        if version >= 4 {
            w.u32(0x7777_0000);
        }
        if version >= 7 {
            w.u32(0);
            for v in [1.0f32, 2.0, 3.0, 1.0, 0.0, 0.0, 0.0] {
                w.f32(v);
            }
        }
        if version >= 8 {
            w.string16l("TestAllegiance").unwrap();
            w.u32(0x8888_0000);
        }
        if version >= 10 {
            w.u32(1);
        }
        if version >= 11 {
            w.u32(0x9999_0000);
        }
        for (i, rec) in records.iter().enumerate() {
            if i > 0 {
                w.u32(rec.1);
            }
            record(&mut w, rec);
        }
        w.into_vec()
    }

    fn info_at(version: u16, officers: u16, titles: u32) -> AllegianceProfile {
        let wire = profile(
            MONARCH,
            version,
            &[Rec(MONARCH, 0, true, "Monarch")],
            officers,
            titles,
        );
        let parsed = AllegianceInfoResponse::parse(&wire).unwrap();
        assert_eq!(parsed.target_guid, MONARCH);
        assert_eq!(parsed.profile.monarch.as_ref().unwrap().name, "Monarch");
        assert_eq!(parsed.profile.old_version, version);
        parsed.profile
    }

    #[test]
    fn every_version_gate() {
        for version in 0..=11 {
            info_at(version, 2, 2);
        }
        assert_eq!(info_at(2, 0, 0).motd, "");
        let v3 = info_at(3, 0, 0);
        assert_eq!(
            (v3.motd.as_str(), v3.motd_set_by.as_str()),
            ("TheMotd", "MotdSetter")
        );
        assert_eq!(info_at(3, 0, 0).chat_room_id, 0);
        assert_eq!(info_at(4, 0, 0).chat_room_id, 0x7777_0000);
        assert_eq!(info_at(7, 0, 0).allegiance_name, "");
        let v8 = info_at(8, 0, 0);
        assert_eq!(v8.allegiance_name, "TestAllegiance");
        assert_eq!(v8.name_last_set_time, 0x8888_0000);
        assert!(!info_at(9, 0, 0).is_locked);
        assert!(info_at(10, 0, 0).is_locked);
        assert_eq!(info_at(10, 0, 0).approved_vassal, 0);
        assert_eq!(info_at(11, 0, 0).approved_vassal, 0x9999_0000);
    }

    #[test]
    fn malformed_trees_discard_the_whole_message() {
        let cases: [&[Rec]; 5] = [
            &[
                Rec(MONARCH, 0, true, "Monarch"),
                Rec(0x5000_0005, 0x5000_FFFF, true, "Orphan"),
            ],
            &[
                Rec(MONARCH, 0, true, "Monarch"),
                Rec(0x5000_0006, 0x5000_0006, true, "Self"),
            ],
            &[
                Rec(MONARCH, 0, true, "Monarch"),
                Rec(0x5000_0007, MONARCH, true, "First"),
                Rec(0x5000_0007, MONARCH, true, "Again"),
            ],
            &[Rec(0, 0, true, "Zero")],
            &[
                Rec(MONARCH, 0, true, "Monarch"),
                Rec(0, MONARCH, true, "ZeroChild"),
            ],
        ];
        for records in cases {
            assert!(matches!(
                AllegianceInfoResponse::parse(&profile(MONARCH, 11, records, 0, 0)),
                Err(ProtoError::Inconsistent { .. })
            ));
        }
        let zero_parent = [
            Rec(MONARCH, 0, true, "Monarch"),
            Rec(0x5000_0008, 0, true, "Zero"),
        ];
        assert!(AllegianceInfoResponse::parse(&profile(MONARCH, 11, &zero_parent, 0, 0)).is_err());
        let orphan = [
            Rec(MONARCH, 0, true, "Monarch"),
            Rec(0x5000_0005, 0x5000_FFFF, true, "O"),
        ];
        assert!(AllegianceUpdate::parse(&profile(3, 11, &orphan, 0, 0)).is_err());
    }

    #[test]
    fn tree_lookups() {
        let chain = [
            Rec(MONARCH, 0, true, "Monarch"),
            Rec(0x5000_0002, MONARCH, false, "Patron"),
            Rec(0x5000_0003, 0x5000_0002, true, "Self"),
        ];
        let p = AllegianceInfoResponse::parse(&profile(0x5000_0003, 11, &chain, 0, 0))
            .unwrap()
            .profile;
        assert_eq!(p.records.len(), 2);
        assert_eq!(p.find_data(0x5000_0003).unwrap().name, "Self");
        assert_eq!(p.find_patron(0x5000_0003).unwrap().name, "Patron");
        assert_eq!(p.find_patron(0x5000_0002).unwrap().name, "Monarch");
        assert!(p.find_patron(MONARCH).is_none());
        assert!(!p.find_data(0x5000_0002).unwrap().is_logged_in);

        let siblings = [
            Rec(MONARCH, 0, true, "Monarch"),
            Rec(0x5000_0010, MONARCH, true, "VassalA"),
            Rec(0x5000_0011, MONARCH, true, "VassalB"),
            Rec(0x5000_0012, MONARCH, true, "VassalC"),
        ];
        let p = AllegianceInfoResponse::parse(&profile(MONARCH, 11, &siblings, 0, 0))
            .unwrap()
            .profile;
        let names: Vec<_> = p.find_vassals(MONARCH).map(|m| m.name.as_str()).collect();
        assert_eq!(names, ["VassalC", "VassalB", "VassalA"]);
    }

    fn newest_header(w: &mut Writer, record_count: u16) {
        w.u32(MONARCH);
        w.u32(u32::from(record_count));
        w.u32(0);
        w.u16(record_count);
        w.u16(11);
        w.u16(0);
        w.u16(256);
        w.u32(0);
        for _ in 0..4 {
            w.u32(0);
        }
        w.string16l("").unwrap();
        w.string16l("").unwrap();
        w.u32(0);
        w.u32(0);
        for v in [0.0f32, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
        w.string16l("Alle").unwrap();
        w.u32(0);
        w.u32(0);
        w.u32(0);
    }

    #[test]
    fn member_records_surface_every_field() {
        let mut w = Writer::new();
        newest_header(&mut w, 1);
        w.u32(MONARCH);
        w.u32(12345);
        w.u32(67890);
        w.u32(HAS_ALLEGIANCE_AGE | HAS_PACKED_LEVEL | LOGGED_IN);
        w.u8(2);
        w.u8(3);
        w.u16(7);
        w.u32(42);
        w.u16(200);
        w.u16(150);
        w.u32(0);
        w.u32(0);
        w.string16l("Monarch").unwrap();
        let p = AllegianceInfoResponse::parse(w.as_slice()).unwrap().profile;
        let m = p.monarch.unwrap();
        assert_eq!(
            (m.rank, m.level, m.loyalty, m.leadership),
            (7, 42, 200, 150)
        );
        assert_eq!((m.cp_cached, m.cp_tithed), (12345, 67890));
        assert_eq!((m.gender, m.heritage_group), (2, 3));
        assert!(!m.may_passup_experience);
    }

    #[test]
    fn records_without_a_level_may_pass_experience_up() {
        let mut w = Writer::new();
        newest_header(&mut w, 2);
        w.u32(MONARCH);
        w.u32(0);
        w.u32(0);
        // Every flag, the pass-up bit included: the monarch is still false.
        w.u32(0x1C);
        w.u16(0);
        w.u16(0);
        w.u32(0);
        w.u16(0);
        w.u16(0);
        w.u32(0);
        w.u32(0);
        w.string16l("Monarch").unwrap();
        w.u32(MONARCH);
        w.u32(0x5000_0005);
        w.u32(0);
        w.u32(0);
        w.u32(HAS_ALLEGIANCE_AGE);
        w.u16(0);
        w.u16(0);
        w.u16(0);
        w.u16(0);
        w.u32(0);
        w.u32(0);
        w.string16l("Vassal").unwrap();
        let p = AllegianceInfoResponse::parse(w.as_slice()).unwrap().profile;
        assert!(!p.monarch.unwrap().may_passup_experience);
        assert!(p.records[0].may_passup_experience);
        assert_eq!(p.records[0].level, 0);
        assert_every_prefix_fails(w.as_slice(), AllegianceInfoResponse::parse);
    }

    #[test]
    fn update_shares_the_profile_reader() {
        let records = [
            Rec(MONARCH, 0, true, "Monarch"),
            Rec(0x5000_0002, MONARCH, true, "Vassal"),
        ];
        let update = AllegianceUpdate::parse(&profile(3, 11, &records, 0, 0)).unwrap();
        assert_eq!(update.rank, 3);
        assert_eq!(update.profile.monarch.unwrap().name, "Monarch");
        assert_eq!(update.profile.records[0].name, "Vassal");
        assert_eq!(update.profile.allegiance_name, "TestAllegiance");
        assert!(update.profile.is_locked);
        assert_eq!(update.profile.approved_vassal, 0x9999_0000);
        // No records: no monarch, nothing else to read.
        let empty = AllegianceInfoResponse::parse(&profile(0x5000_0099, 11, &[], 0, 0)).unwrap();
        assert_eq!(empty.profile.record_count, 0);
        assert!(empty.profile.monarch.is_none() && empty.profile.records.is_empty());
    }

    #[test]
    fn small_events() {
        assert_eq!(
            AllegianceLoginNotification::parse(&words(&[0x5000_0042, 1])),
            Ok(AllegianceLoginNotification {
                character_guid: 0x5000_0042,
                is_logged_in: true
            })
        );
        assert!(
            !AllegianceLoginNotification::parse(&words(&[0x5000_0042, 0]))
                .unwrap()
                .is_logged_in
        );
        assert!(AllegianceLoginNotification::parse(&[0; 4]).is_err());
        assert_eq!(parse_update_result(&words(&[0x40B])), Ok(0x40B));
        assert!(parse_update_result(&[]).is_err());
    }

    #[test]
    fn swear_break_kick_and_update_golden_bytes() {
        assert_eq!(build_swear(3, 0xAAAA), words(&[0xF7B1, 3, 0x1D, 0xAAAA]));
        assert_eq!(build_break(5, 0xBBBB), words(&[0xF7B1, 5, 0x1E, 0xBBBB]));
        // Kick is the break action.
        assert_eq!(
            build_kick(6, 0x5000_0042),
            words(&[0xF7B1, 6, 0x1E, 0x5000_0042])
        );
        assert_eq!(build_update_request(4, true), words(&[0xF7B1, 4, 0x1F, 1]));
        assert_eq!(build_update_request(7, false), words(&[0xF7B1, 7, 0x1F, 0]));
    }

    #[test]
    fn administration_bodies() {
        const BJORN: [u8; 8] = [5, 0, 0x42, 0x6A, 0xF8, 0x72, 0x6E, 0];
        for (body, action, value) in [
            (
                build_break_allegiance_boot(14, "Bjørn", true).unwrap(),
                0x0277,
                1,
            ),
            (build_chat_gag(15, "Bjørn", false).unwrap(), 0x0041, 0),
            (build_set_officer(17, "Bjørn", 3).unwrap(), 0x003B, 3),
        ] {
            assert_eq!(body.len(), 24);
            assert_eq!(body[8..12], u32::to_le_bytes(action));
            assert_eq!(body[12..20], BJORN);
            assert_eq!(body[20..], u32::to_le_bytes(value));
        }
        let title = build_set_officer_title(18, 2, "Marshal").unwrap();
        assert_eq!(title.len(), 28);
        assert_eq!(title[..16], words(&[0xF7B1, 18, 0x3C, 2]));
        assert_eq!(title[16..25], *b"\x07\x00Marshal");
        assert_eq!(title[25..], [0, 0, 0]);
        let boot = build_chat_boot(19, "Bob", "No.").unwrap();
        assert_eq!(boot.len(), 28);
        assert_eq!(boot[12..20], *b"\x03\x00Bob\x00\x00\x00");
        assert_eq!(boot[20..25], *b"\x03\x00No.");
        for (body, action) in [
            (build_add_ban(11, "Bjørn").unwrap(), 0x02A1),
            (build_remove_ban(11, "Bjørn").unwrap(), 0x02A2),
            (build_remove_officer(11, "Bjørn").unwrap(), 0x02A5),
            (build_set_name(11, "Bjørn").unwrap(), 0x0033),
            (build_set_approved_vassal(11, "Bjørn").unwrap(), 0x0040),
            (build_set_motd(11, "Bjørn").unwrap(), 0x0254),
            (build_info_request(11, "Bjørn").unwrap(), 0x027B),
        ] {
            assert_eq!(body[..12], words(&[0xF7B1, 11, action]));
            assert_eq!(body[12..], BJORN);
        }
        let bare: [(BareBuilder, u32); 9] = [
            (build_query_name, 0x0030),
            (build_clear_name, 0x0031),
            (build_list_officer_titles, 0x003D),
            (build_clear_officer_titles, 0x003E),
            (build_list_bans, 0x02A3),
            (build_list_officers, 0x02A6),
            (build_clear_officers, 0x02A7),
            (build_query_motd, 0x0255),
            (build_clear_motd, 0x0256),
        ];
        for (build, action) in bare {
            assert_eq!(build(0x1234), words(&[0xF7B1, 0x1234, action]));
        }
        assert_eq!(build_lock_action(12, 6), words(&[0xF7B1, 12, 0x3F, 6]));
        assert_eq!(build_house_action(12, 5), words(&[0xF7B1, 12, 0x42, 5]));
    }
}
