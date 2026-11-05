//! Speech poses: a word such as `*wave*` or `<wave>` inside a line of
//! speech plays a motion, tells onlookers about it and tells the player,
//! and is taken out of what is said. The words come from the chat pose
//! table in the dats.

use std::collections::HashMap;

use ac_dat::objects::ChatPoseTable;

/// One pose: the motion it plays and the two lines about it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatPose {
    /// The motion command it plays.
    pub motion_command: u32,
    /// The player's own line (`wave.`), printed as "You wave.".
    pub self_text: String,
    /// The onlookers' line (`waves.`), sent to the server.
    pub others_text: String,
}

/// The poses speech can carry, by word (matched without regard to case).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ChatPoseCatalog {
    poses: HashMap<String, ChatPose>,
}

impl ChatPoseCatalog {
    /// A catalog of named poses (tests, tools). An empty word is skipped; a
    /// repeated word keeps the last pose.
    #[must_use]
    pub fn of(poses: impl IntoIterator<Item = (String, ChatPose)>) -> Self {
        let mut table = HashMap::new();
        for (word, pose) in poses {
            if !word.is_empty() {
                table.insert(fold(&word), pose);
            }
        }
        Self { poses: table }
    }

    /// The catalog the dats' chat pose table gives. A word whose motion
    /// name names no motion command is skipped; a motion with no lines has
    /// empty ones.
    #[must_use]
    pub fn from_table(table: &ChatPoseTable) -> Self {
        let emotes: HashMap<String, (&str, &str)> = table
            .chat_emotes
            .iter()
            .map(|(motion, data)| {
                (
                    fold(motion),
                    (data.my_emote.as_str(), data.other_emote.as_str()),
                )
            })
            .collect();
        let mut poses = HashMap::new();
        for (word, motion_name) in &table.chat_poses {
            if word.is_empty() {
                continue;
            }
            let Some(motion) = motion_command(motion_name) else {
                continue;
            };
            let (self_text, others_text) =
                emotes.get(&fold(motion_name)).copied().unwrap_or(("", ""));
            poses.insert(
                fold(word),
                ChatPose {
                    motion_command: motion,
                    self_text: self_text.to_owned(),
                    others_text: others_text.to_owned(),
                },
            );
        }
        Self { poses }
    }

    /// How many words name a pose.
    #[must_use]
    pub fn len(&self) -> usize {
        self.poses.len()
    }

    /// True when no word names a pose (no dats).
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.poses.is_empty()
    }

    /// The pose `command` names, its onlookers' line read with his (`male`)
    /// or her for `%p`.
    #[must_use]
    pub fn resolve(&self, command: &str, male: bool) -> Option<ChatPose> {
        let pose = self.poses.get(&fold(command))?;
        let possessive = if male { "his" } else { "her" };
        Some(ChatPose {
            others_text: pose.others_text.replace("%p", possessive),
            ..pose.clone()
        })
    }
}

/// Take the poses out of a line of speech: each `*word*` or `<word>` that
/// names a pose with a motion is handed to `execute`, in order, and removed;
/// anything else stays as typed. The rest of the line, trimmed.
pub fn extract_poses(
    text: &str,
    mut resolve: impl FnMut(&str) -> Option<ChatPose>,
    mut execute: impl FnMut(ChatPose),
) -> String {
    let mut remaining = text.to_owned();
    let mut cursor = 0;
    while cursor < remaining.len() {
        let star = remaining[cursor..].find('*').map(|i| i + cursor);
        let angle = remaining[cursor..].find('<').map(|i| i + cursor);
        // The earlier opener picks the closer.
        let (open, close) = match (star, angle) {
            (None, angle) => (angle, '>'),
            (Some(star), Some(angle)) if angle < star => (Some(angle), '>'),
            (Some(star), _) => (Some(star), '*'),
        };
        let Some(open) = open else {
            break;
        };
        let Some(end) = remaining[open + 1..].find(close).map(|i| i + open + 1) else {
            cursor = open + 1;
            continue;
        };
        match resolve(&remaining[open + 1..end]) {
            Some(pose) if pose.motion_command != 0 => {
                execute(pose);
                remaining.replace_range(open..=end, "");
                cursor = open;
            }
            _ => cursor = end + 1,
        }
    }
    remaining.trim().to_owned()
}

/// The motion command a name in the pose table names, the case ignored.
fn motion_command(name: &str) -> Option<u32> {
    ac_motion::motion_command::ALL
        .iter()
        .find(|(known, _)| known.eq_ignore_ascii_case(name))
        .map(|&(_, id)| id)
}

/// The form a word is looked up in: letter case does not matter.
fn fold(word: &str) -> String {
    word.to_lowercase()
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.Runtime.Tests/Chat/RetailPublicChatParserTests.cs`
    //! and the catalog half of `LiveChatCommandRouteTests.cs`.
    use super::*;
    use ac_dat::objects::ChatEmoteData;

    fn pose(motion: u32) -> ChatPose {
        ChatPose {
            motion_command: motion,
            self_text: String::new(),
            others_text: String::new(),
        }
    }

    #[test]
    fn invalid_and_unmatched_tokens_remain_literal() {
        let text = extract_poses(
            "*unknown* and *unfinished",
            |_| None,
            |_| panic!("must not execute"),
        );
        assert_eq!(text, "*unknown* and *unfinished");
    }

    #[test]
    fn multiple_valid_star_and_angle_tokens_are_removed_in_order() {
        let mut motions = Vec::new();
        let text = extract_poses(
            "a *one* b <two> c",
            |command| match command {
                "one" => Some(pose(1)),
                "two" => Some(pose(2)),
                _ => None,
            },
            |p| motions.push(p.motion_command),
        );
        assert_eq!(text, "a  b  c");
        assert_eq!(motions, [1, 2]);
    }

    #[test]
    fn a_pose_with_no_motion_stays_in_the_line() {
        // OpenAC `RetailPublicChatParser`: only a non-zero motion is taken.
        let text = extract_poses("hi *nod*", |_| Some(pose(0)), |_| panic!("no motion"));
        assert_eq!(text, "hi *nod*");
    }

    #[test]
    fn an_empty_catalog_resolves_nothing() {
        // `TheSessionCarriesThePoseTableAndIsEmptyUntilItIsRead`.
        assert!(ChatPoseCatalog::default().resolve("wave", true).is_none());
    }

    #[test]
    fn words_match_without_case_and_name_the_possessive() {
        let catalog = ChatPoseCatalog::from_table(&ChatPoseTable {
            id: 0,
            chat_poses: vec![
                ("wave".to_owned(), "WAVE".to_owned()),
                ("dance".to_owned(), "NotAMotion".to_owned()),
            ],
            chat_emotes: vec![(
                "Wave".to_owned(),
                ChatEmoteData {
                    my_emote: "wave.".to_owned(),
                    other_emote: "waves %p hand.".to_owned(),
                },
            )],
        });
        assert_eq!(catalog.len(), 1);
        let her = catalog.resolve("WAVE", false).unwrap();
        assert_eq!(her.motion_command, ac_motion::motion_command::WAVE);
        assert_eq!(her.self_text, "wave.");
        assert_eq!(her.others_text, "waves her hand.");
        assert_eq!(
            catalog.resolve("wave", true).unwrap().others_text,
            "waves his hand."
        );
        assert!(catalog.resolve("dance", true).is_none());
    }
}
