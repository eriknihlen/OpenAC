use std::collections::HashSet;

use ac_bits::Reader;

use super::DatObject;
use super::read::{legacy_string, list, packable_table_count};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// File id of the chat pose table.
pub const CHAT_POSE_TABLE_ID: u32 = 0x0E00_0007;

/// The poses a player can put in a line of speech (`hello *wave*`): which
/// motion each word names, and what is said about each motion. File id
/// `0x0E000007`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatPoseTable {
    /// File id.
    pub id: u32,
    /// The word used in speech against the motion name it plays, in file
    /// order.
    pub chat_poses: Vec<(String, String)>,
    /// The motion name against the lines said about it, in file order.
    pub chat_emotes: Vec<(String, ChatEmoteData)>,
}

/// What is said when a pose plays: to the player, and to everyone else.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatEmoteData {
    /// The player's own line (`wave.`).
    pub my_emote: String,
    /// The line others read (`waves.`; `%p` stands for his or her).
    pub other_emote: String,
}

impl DatObject for ChatPoseTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let chat_poses = string_keyed(r, "chat poses", legacy_string)?;
        let chat_emotes = string_keyed(r, "chat emotes", |r| {
            Ok(ChatEmoteData {
                my_emote: legacy_string(r)?,
                other_emote: legacy_string(r)?,
            })
        })?;
        Ok(Self {
            id,
            chat_poses,
            chat_emotes,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A packable table keyed by one-byte text; a repeated key is an error.
fn string_keyed<V>(
    r: &mut Reader<'_>,
    table: &'static str,
    mut value: impl FnMut(&mut Reader<'_>) -> Result<V>,
) -> Result<Vec<(String, V)>> {
    let count = packable_table_count(r)?;
    let pairs = list(r, count, |r| Ok((legacy_string(r)?, value(r)?)))?;
    let mut seen = HashSet::new();
    for (key, _) in &pairs {
        if !seen.insert(key.as_str()) {
            return Err(DecodeError::DuplicateName {
                table,
                key: key.clone(),
            });
        }
    }
    Ok(pairs)
}

#[cfg(test)]
mod tests {
    //! The layout of the chat pose table and its emote data, with a
    //! round-trip of known values.

    use super::*;
    use ac_bits::Writer;

    fn pstring(w: &mut Writer, text: &str) {
        let start = w.as_slice().len();
        w.u16(u16::try_from(text.len()).unwrap());
        for b in text.bytes() {
            w.u8(b);
        }
        while !(w.as_slice().len() - start).is_multiple_of(4) {
            w.u8(0);
        }
    }

    fn table(poses: &[(&str, &str)], emotes: &[(&str, &str, &str)]) -> Writer {
        let mut w = Writer::new();
        w.u32(CHAT_POSE_TABLE_ID);
        w.u16(u16::try_from(poses.len()).unwrap());
        w.u16(32);
        for (word, motion) in poses {
            pstring(&mut w, word);
            pstring(&mut w, motion);
        }
        w.u16(u16::try_from(emotes.len()).unwrap());
        w.u16(32);
        for (motion, mine, others) in emotes {
            pstring(&mut w, motion);
            pstring(&mut w, mine);
            pstring(&mut w, others);
        }
        w
    }

    #[test]
    fn poses_and_their_lines_read_back() {
        let w = table(
            &[("AFK", "AFKState"), ("Another", "AnotherState")],
            &[
                ("AFKState", "are afk.", "is afk."),
                (
                    "AnotherState",
                    "are in another state.",
                    "is in another state.",
                ),
            ],
        );
        let t = ChatPoseTable::unpack(&mut Reader::new(w.as_slice())).unwrap();
        assert_eq!(t.id, CHAT_POSE_TABLE_ID);
        assert_eq!(
            t.chat_poses,
            vec![
                ("AFK".to_owned(), "AFKState".to_owned()),
                ("Another".to_owned(), "AnotherState".to_owned()),
            ]
        );
        assert_eq!(t.chat_emotes.len(), 2);
        assert_eq!(t.chat_emotes[0].1.other_emote, "is afk.");
        assert_eq!(t.chat_emotes[1].0, "AnotherState");
        assert_eq!(t.chat_emotes[1].1.my_emote, "are in another state.");
        assert_eq!(t.chat_emotes[1].1.other_emote, "is in another state.");
    }

    #[test]
    fn a_repeated_word_is_an_error() {
        let w = table(&[("wave", "Wave"), ("wave", "WaveHigh")], &[]);
        assert!(matches!(
            ChatPoseTable::unpack(&mut Reader::new(w.as_slice())),
            Err(DecodeError::DuplicateName { .. })
        ));
    }

    #[test]
    fn a_cut_table_is_an_error() {
        let w = table(&[("wave", "Wave")], &[]);
        let bytes = &w.as_slice()[..w.as_slice().len() - 6];
        assert!(ChatPoseTable::unpack(&mut Reader::new(bytes)).is_err());
    }
}
