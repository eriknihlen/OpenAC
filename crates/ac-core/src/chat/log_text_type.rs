//! The text class every chat line carries. It picks the line's color and
//! which chat windows show it; the server sends it with most messages.

/// The class of a chat line, as the wire numbers it (34 values).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum LogTextType {
    /// Plain text with no class of its own.
    Default = 0x00,
    /// Every class (a filter value, not a line class).
    All = 0x01,
    /// Local speech.
    Speech = 0x02,
    /// A tell received.
    Tell = 0x03,
    /// A tell the player sent.
    SpeechDirectSend = 0x04,
    /// System text.
    System = 0x05,
    /// Combat text.
    Combat = 0x06,
    /// Magic text.
    Magic = 0x07,
    /// A numbered channel heard.
    Channel = 0x08,
    /// A numbered channel the player sent on.
    ChannelSend = 0x09,
    /// Allegiance-tree speech heard.
    Social = 0x0A,
    /// Allegiance-tree speech the player sent.
    SocialSend = 0x0B,
    /// An emote.
    Emote = 0x0C,
    /// Character advancement.
    Advancement = 0x0D,
    /// The abuse channel.
    Abuse = 0x0E,
    /// The help channel.
    Help = 0x0F,
    /// Appraisal text.
    Appraisal = 0x10,
    /// Spellcasting words.
    Spellcasting = 0x11,
    /// Allegiance text.
    Allegiance = 0x12,
    /// Fellowship text.
    Fellowship = 0x13,
    /// A world-wide broadcast.
    WorldBroadcast = 0x14,
    /// Combat text about an enemy's attack on the player.
    CombatEnemy = 0x15,
    /// Combat text about the player's own attack.
    CombatSelf = 0x16,
    /// Recall text.
    Recall = 0x17,
    /// Crafting text.
    Craft = 0x18,
    /// Salvaging text.
    Salvaging = 0x19,
    /// Client-side status text. It goes to the on-screen status box at the
    /// top of the view, never to the scrolling chat window.
    ClientLocal = 0x1A,
    /// The General chat room.
    TurbineGeneral = 0x1B,
    /// The Trade chat room.
    TurbineTrade = 0x1C,
    /// The LFG chat room.
    TurbineLfg = 0x1D,
    /// The Roleplay chat room.
    TurbineRoleplay = 0x1E,
    /// A tell from an administrator.
    AdminTell = 0x1F,
    /// The Society chat room.
    TurbineSociety = 0x20,
    /// Reserved.
    Reserved21 = 0x21,
}

impl LogTextType {
    /// Every class in wire order.
    pub const ALL: [Self; 34] = [
        Self::Default,
        Self::All,
        Self::Speech,
        Self::Tell,
        Self::SpeechDirectSend,
        Self::System,
        Self::Combat,
        Self::Magic,
        Self::Channel,
        Self::ChannelSend,
        Self::Social,
        Self::SocialSend,
        Self::Emote,
        Self::Advancement,
        Self::Abuse,
        Self::Help,
        Self::Appraisal,
        Self::Spellcasting,
        Self::Allegiance,
        Self::Fellowship,
        Self::WorldBroadcast,
        Self::CombatEnemy,
        Self::CombatSelf,
        Self::Recall,
        Self::Craft,
        Self::Salvaging,
        Self::ClientLocal,
        Self::TurbineGeneral,
        Self::TurbineTrade,
        Self::TurbineLfg,
        Self::TurbineRoleplay,
        Self::AdminTell,
        Self::TurbineSociety,
        Self::Reserved21,
    ];

    /// The wire value.
    #[must_use]
    pub const fn as_u32(self) -> u32 {
        self as u32
    }

    /// The class with this wire value, if it is one.
    #[must_use]
    pub fn from_u32(value: u32) -> Option<Self> {
        usize::try_from(value)
            .ok()
            .and_then(|index| Self::ALL.get(index).copied())
    }

    /// Turns a text class a plugin supplied into one safe to put in the
    /// transcript. A value outside the defined range, and [`Self::ClientLocal`]
    /// itself (which goes to the status box, where plugins have no business
    /// posting), become [`Self::Default`].
    #[must_use]
    pub fn from_plugin_value(value: i32) -> Self {
        match u32::try_from(value).ok().and_then(Self::from_u32) {
            Some(Self::ClientLocal) | None => Self::Default,
            Some(kind) => kind,
        }
    }
}

impl From<LogTextType> for u32 {
    fn from(value: LogTextType) -> u32 {
        value.as_u32()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wire_values_are_dense_and_in_order() {
        for (index, kind) in LogTextType::ALL.iter().enumerate() {
            assert_eq!(kind.as_u32() as usize, index);
            assert_eq!(LogTextType::from_u32(kind.as_u32()), Some(*kind));
        }
        assert_eq!(LogTextType::from_u32(0x22), None);
    }

    // OpenAC tests/AcDream.Core.Tests/Chat/LogTextTypeCodecTests.cs
    #[test]
    fn an_ordinary_plugin_value_passes_through_unchanged() {
        assert_eq!(
            LogTextType::from_plugin_value(LogTextType::Magic.as_u32() as i32),
            LogTextType::Magic
        );
    }

    #[test]
    fn client_local_from_a_plugin_falls_back_to_default() {
        assert_eq!(LogTextType::from_plugin_value(0x1A), LogTextType::Default);
    }

    #[test]
    fn an_out_of_range_plugin_value_falls_back_to_default() {
        for value in [-1, 9999] {
            assert_eq!(LogTextType::from_plugin_value(value), LogTextType::Default);
        }
    }
}
