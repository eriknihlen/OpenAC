//! Chat rooms ("Turbine chat"): the room ids the server assigns at login
//! and the per-session cookie each outgoing room message carries.

/// The rooms the server assigned; 0 means no such room (no society, say).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct TurbineChatRooms {
    /// The allegiance room.
    pub allegiance: u32,
    /// General.
    pub general: u32,
    /// Trade.
    pub trade: u32,
    /// Looking for group.
    pub lfg: u32,
    /// Roleplay.
    pub roleplay: u32,
    /// Olthoi.
    pub olthoi: u32,
    /// The top-level society room (0 when the player has no society).
    pub society: u32,
    /// Celestial Hand.
    pub society_celestial_hand: u32,
    /// Eldrytch Web.
    pub society_eldrytch_web: u32,
    /// Radiant Blood.
    pub society_radiant_blood: u32,
}

/// A chat room by role.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TurbineChannel {
    /// The allegiance room.
    Allegiance,
    /// General.
    General,
    /// Trade.
    Trade,
    /// Looking for group.
    Lfg,
    /// Roleplay.
    Roleplay,
    /// The top-level society room.
    Society,
    /// Celestial Hand.
    SocietyCelestialHand,
    /// Eldrytch Web.
    SocietyEldrytchWeb,
    /// Radiant Blood.
    SocietyRadiantBlood,
    /// Olthoi.
    Olthoi,
}

/// The chat-room state for one session.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TurbineChatState {
    enabled: bool,
    rooms: TurbineChatRooms,
    next_context_id: u32,
}

impl Default for TurbineChatState {
    fn default() -> Self {
        Self::new()
    }
}

impl TurbineChatState {
    /// Disabled, no rooms, cookies starting at 1.
    #[must_use]
    pub fn new() -> Self {
        Self {
            enabled: false,
            rooms: TurbineChatRooms::default(),
            next_context_id: 1,
        }
    }

    /// True once the server has assigned the rooms.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// The assigned rooms.
    #[must_use]
    pub fn rooms(&self) -> &TurbineChatRooms {
        &self.rooms
    }

    /// The next outgoing message's cookie: 1, 2, 3, ..., skipping 0 when it
    /// wraps.
    pub fn next_context_id(&mut self) -> u32 {
        let cookie = self.next_context_id;
        self.next_context_id = self.next_context_id.wrapping_add(1);
        if self.next_context_id == 0 {
            self.next_context_id = 1;
        }
        cookie
    }

    /// The server assigned the rooms; chat rooms are now on.
    pub fn on_channels_received(&mut self, rooms: TurbineChatRooms) {
        self.enabled = true;
        self.rooms = rooms;
    }

    /// Back to the start-of-session state.
    pub fn reset(&mut self) {
        *self = Self::new();
    }

    /// The room id for a role, 0 when not assigned.
    #[must_use]
    pub fn room_for(&self, channel: TurbineChannel) -> u32 {
        let rooms = &self.rooms;
        match channel {
            TurbineChannel::Allegiance => rooms.allegiance,
            TurbineChannel::General => rooms.general,
            TurbineChannel::Trade => rooms.trade,
            TurbineChannel::Lfg => rooms.lfg,
            TurbineChannel::Roleplay => rooms.roleplay,
            TurbineChannel::Olthoi => rooms.olthoi,
            TurbineChannel::Society => rooms.society,
            TurbineChannel::SocietyCelestialHand => rooms.society_celestial_hand,
            TurbineChannel::SocietyEldrytchWeb => rooms.society_eldrytch_web,
            TurbineChannel::SocietyRadiantBlood => rooms.society_radiant_blood,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rooms(first: u32) -> TurbineChatRooms {
        TurbineChatRooms {
            allegiance: first,
            general: first + 1,
            trade: first + 2,
            lfg: first + 3,
            roleplay: first + 4,
            olthoi: first + 5,
            society: first + 6,
            society_celestial_hand: first + 7,
            society_eldrytch_web: first + 8,
            society_radiant_blood: first + 9,
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/TurbineChatStateTests.cs
    #[test]
    fn reset_clears_rooms_and_restarts_the_cookie_at_one() {
        let mut state = TurbineChatState::new();
        state.on_channels_received(rooms(1));
        assert_eq!(state.next_context_id(), 1);
        assert_eq!(state.next_context_id(), 2);
        state.reset();
        assert!(!state.enabled());
        assert_eq!(*state.rooms(), TurbineChatRooms::default());
        assert_eq!(state.next_context_id(), 1);
        state.reset();
        assert_eq!(state.next_context_id(), 1);
    }

    #[test]
    fn initial_state_is_disabled_with_zero_rooms() {
        let mut state = TurbineChatState::new();
        assert!(!state.enabled());
        assert_eq!(state.rooms().general, 0);
        assert_eq!(state.rooms().trade, 0);
        assert_eq!(state.rooms().allegiance, 0);
        assert_eq!(state.next_context_id(), 1);
        assert_eq!(state.next_context_id(), 2);
    }

    #[test]
    fn channels_received_populates_every_room_and_enables() {
        let mut state = TurbineChatState::new();
        state.on_channels_received(rooms(0xA0));
        assert!(state.enabled());
        assert_eq!(*state.rooms(), rooms(0xA0));
    }

    #[test]
    fn context_ids_are_monotonic_and_skip_zero_on_wrap() {
        let mut state = TurbineChatState::new();
        for expected in 1..=4 {
            assert_eq!(state.next_context_id(), expected);
        }
        state.next_context_id = u32::MAX;
        assert_eq!(state.next_context_id(), u32::MAX);
        assert_eq!(state.next_context_id(), 1);
    }

    #[test]
    fn room_for_returns_the_configured_room() {
        let mut state = TurbineChatState::new();
        state.on_channels_received(rooms(1));
        let expected = [
            (TurbineChannel::Allegiance, 1),
            (TurbineChannel::General, 2),
            (TurbineChannel::Trade, 3),
            (TurbineChannel::Lfg, 4),
            (TurbineChannel::Roleplay, 5),
            (TurbineChannel::Olthoi, 6),
            (TurbineChannel::Society, 7),
            (TurbineChannel::SocietyCelestialHand, 8),
            (TurbineChannel::SocietyEldrytchWeb, 9),
            (TurbineChannel::SocietyRadiantBlood, 10),
        ];
        for (channel, room) in expected {
            assert_eq!(state.room_for(channel), room, "{channel:?}");
        }
    }
}
