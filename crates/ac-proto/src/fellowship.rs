//! Fellowships: the requests to create, join, leave and run one, and the
//! events that describe it.
//!
//! The disband event ([`crate::GameEventType::FELLOWSHIP_DISBAND`]) carries
//! nothing to read: receiving it is the whole message. Game-action
//! builders take the session's next action sequence number and are sent on
//! the UI queue.

use ac_bits::Reader;

use crate::wire::{action, read_bool32, read_string, u32_action};
use crate::{GameActionType, Result};

/// One member of a fellowship.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FellowMember {
    /// Character id.
    pub guid: u32,
    /// Cached experience share.
    pub cp_cache: u32,
    /// Cached luminance share.
    pub lum_cache: u32,
    /// Level.
    pub level: u32,
    /// Maximum health.
    pub max_health: u32,
    /// Maximum stamina.
    pub max_stamina: u32,
    /// Maximum mana.
    pub max_mana: u32,
    /// Current health.
    pub current_health: u32,
    /// Current stamina.
    pub current_stamina: u32,
    /// Current mana.
    pub current_mana: u32,
    /// Loot-sharing word as sent; any non-zero value means sharing.
    pub share_loot: u32,
    /// Name.
    pub name: String,
}

impl FellowMember {
    fn read(r: &mut Reader<'_>, guid: u32) -> Result<Self> {
        Ok(Self {
            guid,
            cp_cache: r.u32()?,
            lum_cache: r.u32()?,
            level: r.u32()?,
            max_health: r.u32()?,
            max_stamina: r.u32()?,
            max_mana: r.u32()?,
            current_health: r.u32()?,
            current_stamina: r.u32()?,
            current_mana: r.u32()?,
            share_loot: r.u32()?,
            name: read_string(r)?,
        })
    }
}

/// A member who left, and when.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DepartedFellow {
    /// Character id.
    pub guid: u32,
    /// When they left, server time in seconds.
    pub departed_timestamp: i32,
}

/// The whole fellowship ([`crate::GameEventType::FELLOWSHIP_FULL_UPDATE`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FellowshipFullUpdate {
    /// The members, in wire order.
    pub members: Vec<FellowMember>,
    /// Fellowship name.
    pub name: String,
    /// The leader.
    pub leader_guid: u32,
    /// Experience is shared.
    pub share_xp: bool,
    /// Experience is split evenly.
    pub even_xp_split: bool,
    /// Anyone may recruit.
    pub open_fellow: bool,
    /// The fellowship is locked.
    pub locked: bool,
    /// Members who left.
    pub departed: Vec<DepartedFellow>,
}

impl FellowshipFullUpdate {
    /// Parse the event payload: the member table (16-bit count, 16-bit
    /// buckets, then id and member per entry), name, leader, four flag
    /// words, then the departed table (id and timestamp per entry).
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let count = r.u16()?;
        let _buckets = r.u16()?;
        let mut members = Vec::new();
        for _ in 0..count {
            let guid = r.u32()?;
            members.push(FellowMember::read(&mut r, guid)?);
        }
        let name = read_string(&mut r)?;
        let leader_guid = r.u32()?;
        let share_xp = read_bool32(&mut r)?;
        let even_xp_split = read_bool32(&mut r)?;
        let open_fellow = read_bool32(&mut r)?;
        let locked = read_bool32(&mut r)?;
        let count = r.u16()?;
        let _buckets = r.u16()?;
        let mut departed = Vec::new();
        for _ in 0..count {
            departed.push(DepartedFellow {
                guid: r.u32()?,
                departed_timestamp: r.i32()?,
            });
        }
        Ok(Self {
            members,
            name,
            leader_guid,
            share_xp,
            even_xp_split,
            open_fellow,
            locked,
            departed,
        })
    }
}

/// One member changed ([`crate::GameEventType::FELLOWSHIP_UPDATE_FELLOW`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FellowshipUpdateFellow {
    /// The member, its id first on the wire.
    pub member: FellowMember,
    /// What changed (3 is vitals).
    pub update_type: u32,
}

impl FellowshipUpdateFellow {
    /// Parse the event payload: id, member, update type.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let guid = r.u32()?;
        Ok(Self {
            member: FellowMember::read(&mut r, guid)?,
            update_type: r.u32()?,
        })
    }
}

/// The member a quit ([`crate::GameEventType::FELLOWSHIP_QUIT`]) or
/// dismiss ([`crate::GameEventType::FELLOWSHIP_DISMISS`]) event names.
pub fn parse_member_event(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// The optional word of a fellow-update-done
/// ([`crate::GameEventType::FELLOWSHIP_FELLOW_UPDATE_DONE`]) or
/// fellow-stats-done ([`crate::GameEventType::FELLOWSHIP_FELLOW_STATS_DONE`])
/// event; an empty payload is valid and gives `None`.
#[must_use]
pub fn parse_done_value(payload: &[u8]) -> Option<u32> {
    Reader::new(payload).u32().ok()
}

/// Create a fellowship: name, then share-experience as a `u32`.
pub fn build_create(sequence: u32, name: &str, share_xp: bool) -> Result<Vec<u8>> {
    let mut w = action(sequence, GameActionType::FELLOWSHIP_CREATE);
    w.string16l(name)?;
    w.u32(u32::from(share_xp));
    Ok(w.into_vec())
}

/// Quit the fellowship; a leader who quits with `disband` ends it.
#[must_use]
pub fn build_quit(sequence: u32, disband: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::FELLOWSHIP_QUIT,
        u32::from(disband),
    )
}

/// Dismiss a member.
#[must_use]
pub fn build_dismiss(sequence: u32, member_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::FELLOWSHIP_DISMISS, member_guid)
}

/// Recruit a player.
#[must_use]
pub fn build_recruit(sequence: u32, target_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::FELLOWSHIP_RECRUIT, target_guid)
}

/// The fellowship panel opened (`true`) or closed; the server sends
/// updates only while it is open. This is not the openness setting.
#[must_use]
pub fn build_update_request(sequence: u32, panel_open: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::FELLOWSHIP_UPDATE_REQUEST,
        u32::from(panel_open),
    )
}

/// Make another member the leader.
#[must_use]
pub fn build_assign_new_leader(sequence: u32, new_leader_guid: u32) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::FELLOWSHIP_ASSIGN_NEW_LEADER,
        new_leader_guid,
    )
}

/// Let anyone recruit (`true`) or only the leader.
#[must_use]
pub fn build_change_openness(sequence: u32, open: bool) -> Vec<u8> {
    u32_action(
        sequence,
        GameActionType::FELLOWSHIP_CHANGE_OPENNESS,
        u32::from(open),
    )
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `FellowshipEventsTests.cs` and the fellowship cases of
    //! `SocialActionsTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    fn member(w: &mut Writer, guid: u32, stats: [u32; 10], name: &str) {
        w.u32(guid);
        for s in stats {
            w.u32(s);
        }
        w.string16l(name).unwrap();
    }

    fn two_member_update() -> Vec<u8> {
        let mut w = Writer::new();
        w.u16(2);
        w.u16(16);
        member(
            &mut w,
            0x5000_0001,
            [100, 50, 20, 100, 100, 100, 80, 90, 70, 0x10],
            "Leader",
        );
        member(
            &mut w,
            0x5000_0002,
            [0, 0, 15, 90, 90, 90, 90, 90, 90, 0],
            "Second",
        );
        w.string16l("TestFellowship").unwrap();
        for word in [0x5000_0001, 1, 1, 0, 0] {
            w.u32(word);
        }
        w.u16(1);
        w.u16(32);
        w.u32(0x5000_0099);
        w.i32(1_700_000_000);
        w.into_vec()
    }

    #[test]
    fn full_update_two_members_one_departed() {
        let update = FellowshipFullUpdate::parse(&two_member_update()).unwrap();
        assert_eq!(update.members.len(), 2);
        let leader = &update.members[0];
        assert_eq!(leader.guid, 0x5000_0001);
        assert_eq!(leader.name, "Leader");
        assert_eq!(leader.cp_cache, 100);
        assert_eq!(leader.lum_cache, 50);
        assert_eq!(leader.level, 20);
        assert_eq!(leader.current_health, 80);
        // The loot word is kept raw, not narrowed to a bool.
        assert_eq!(leader.share_loot, 0x10);
        assert_eq!(update.members[1].name, "Second");
        assert_eq!(update.name, "TestFellowship");
        assert_eq!(update.leader_guid, 0x5000_0001);
        assert!(update.share_xp && update.even_xp_split);
        assert!(!update.open_fellow && !update.locked);
        assert_eq!(
            update.departed,
            [DepartedFellow {
                guid: 0x5000_0099,
                departed_timestamp: 1_700_000_000
            }]
        );
        assert_every_prefix_fails(&two_member_update(), FellowshipFullUpdate::parse);
    }

    #[test]
    fn full_update_empty() {
        let mut w = Writer::new();
        w.u16(0);
        w.u16(16);
        w.string16l("Empty").unwrap();
        for _ in 0..5 {
            w.u32(0);
        }
        w.u16(0);
        w.u16(32);
        let update = FellowshipFullUpdate::parse(w.as_slice()).unwrap();
        assert!(update.members.is_empty() && update.departed.is_empty());
        assert_eq!(update.name, "Empty");
        assert!(FellowshipFullUpdate::parse(&[1, 0]).is_err());
    }

    #[test]
    fn update_fellow_reads_the_id_first() {
        let mut w = Writer::new();
        member(
            &mut w,
            0x5000_0005,
            [10, 5, 3, 100, 80, 60, 90, 70, 50, 2],
            "Vitals",
        );
        w.u32(3);
        let update = FellowshipUpdateFellow::parse(w.as_slice()).unwrap();
        assert_eq!(update.member.guid, 0x5000_0005);
        assert_eq!(update.member.name, "Vitals");
        assert_eq!(update.member.current_health, 90);
        assert_eq!(update.member.share_loot, 2);
        assert_eq!(update.update_type, 3);
        assert_every_prefix_fails(w.as_slice(), FellowshipUpdateFellow::parse);
    }

    #[test]
    fn small_events() {
        assert_eq!(parse_member_event(&words(&[0x5000_0009])), Ok(0x5000_0009));
        assert!(parse_member_event(&[]).is_err());
        assert_eq!(parse_done_value(&[]), None);
        assert_eq!(parse_done_value(&words(&[42])), Some(42));
    }

    #[test]
    fn requests_golden_bytes() {
        assert_eq!(
            build_create(7, "Team", true).unwrap(),
            [
                0xB1, 0xF7, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0xA2, 0x00, 0x00, 0x00, 0x04, 0x00,
                0x54, 0x65, 0x61, 0x6D, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            ]
        );
        let body = build_create(1, "X", false).unwrap();
        assert_eq!(body.len(), 20);
        assert_eq!(body[16..], [0, 0, 0, 0]);
        assert_eq!(build_quit(1, true), words(&[0xF7B1, 1, 0xA3, 1]));
        assert_eq!(build_dismiss(1, 0xDEAD), words(&[0xF7B1, 1, 0xA4, 0xDEAD]));
        assert_eq!(build_recruit(1, 0xBEEF), words(&[0xF7B1, 1, 0xA5, 0xBEEF]));
        assert_eq!(build_update_request(9, true), words(&[0xF7B1, 9, 0xA6, 1]));
        assert_eq!(
            build_assign_new_leader(2, 0x5000_1234),
            words(&[0xF7B1, 2, 0x0290, 0x5000_1234])
        );
        assert_eq!(
            build_change_openness(4, false),
            words(&[0xF7B1, 4, 0x0291, 0])
        );
    }
}
