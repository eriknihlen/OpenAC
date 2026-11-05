//! Characters: spending experience on attributes, vitals and skills,
//! training skills, and, at character selection, creating, deleting and
//! restoring characters.
//!
//! Advancement builders are game actions: they take the session's next
//! action sequence number and are sent on the UI queue. Create and delete
//! are plain messages on the login queue; restore is a plain message on
//! the control queue.

use ac_bits::{Reader, Writer};

use crate::wire::{action, read_string};
use crate::{GameActionType, Opcode, Result};

/// Skill advancement classes a create request carries; the server ends the
/// session on any other count.
pub const SKILL_ADVANCEMENT_CLASS_COUNT: usize = 55;

/// The look chosen at character creation.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CharacterAppearance {
    /// Eye texture strip.
    pub eyes_strip: u32,
    /// Nose texture strip.
    pub nose_strip: u32,
    /// Mouth texture strip.
    pub mouth_strip: u32,
    /// Hair color.
    pub hair_color: u32,
    /// Eye color.
    pub eye_color: u32,
    /// Hair style.
    pub hair_style: u32,
    /// Headgear style.
    pub headgear_style: u32,
    /// Headgear color.
    pub headgear_color: u32,
    /// Shirt style.
    pub shirt_style: u32,
    /// Shirt color.
    pub shirt_color: u32,
    /// Trousers style.
    pub trousers_style: u32,
    /// Trousers color.
    pub trousers_color: u32,
    /// Footwear style.
    pub footwear_style: u32,
    /// Footwear color.
    pub footwear_color: u32,
    /// Skin shade.
    pub skin_shade: f64,
    /// Hair shade.
    pub hair_shade: f64,
    /// Headgear shade.
    pub headgear_shade: f64,
    /// Shirt shade.
    pub shirt_shade: f64,
    /// Trousers shade.
    pub trousers_shade: f64,
    /// Footwear shade.
    pub footwear_shade: f64,
}

/// The starting attributes chosen at character creation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CharacterAttributes {
    /// Strength.
    pub strength: u32,
    /// Endurance.
    pub endurance: u32,
    /// Coordination.
    pub coordination: u32,
    /// Quickness.
    pub quickness: u32,
    /// Focus.
    pub focus: u32,
    /// Self.
    pub self_: u32,
}

/// A new character.
#[derive(Debug, Clone, PartialEq)]
pub struct CharacterCreateRequest {
    /// Heritage group.
    pub heritage: u32,
    /// Gender.
    pub gender: u32,
    /// Look.
    pub appearance: CharacterAppearance,
    /// Profession template.
    pub template: u32,
    /// Starting attributes.
    pub attributes: CharacterAttributes,
    /// Character slot.
    pub slot: u32,
    /// Class id.
    pub class_id: u32,
    /// Name.
    pub name: String,
    /// Starting town.
    pub start_area: u32,
    /// Create as an administrator.
    pub is_admin: bool,
    /// Create as an envoy.
    pub is_envoy: bool,
}

impl CharacterCreateRequest {
    /// The checksum the request ends with: the wrapping sum of heritage,
    /// gender, the three face strips, hair and eye color, the four styles,
    /// the template and the six attributes. Clothing colors, shades, slot
    /// and class are not part of it.
    #[must_use]
    pub fn checksum(&self) -> u32 {
        let a = &self.appearance;
        let b = &self.attributes;
        [
            self.heritage,
            self.gender,
            a.eyes_strip,
            a.nose_strip,
            a.mouth_strip,
            a.hair_color,
            a.eye_color,
            a.hair_style,
            a.headgear_style,
            a.shirt_style,
            a.trousers_style,
            a.footwear_style,
            self.template,
            b.strength,
            b.endurance,
            b.coordination,
            b.quickness,
            b.focus,
            b.self_,
        ]
        .into_iter()
        .fold(0u32, u32::wrapping_add)
    }
}

/// Create a character ([`Opcode::CharacterCreate`]): account, a version
/// word of 1, heritage, gender, the appearance, template, attributes, slot,
/// class, the skill advancement classes with their count, name, start
/// area, the admin and envoy words, then the checksum.
pub fn build_character_create(
    account_name: &str,
    request: &CharacterCreateRequest,
    skill_advancement_classes: &[u32; SKILL_ADVANCEMENT_CLASS_COUNT],
) -> Result<Vec<u8>> {
    let a = &request.appearance;
    let b = &request.attributes;
    let mut w = Writer::new();
    w.u32(Opcode::CharacterCreate.value());
    w.string16l(account_name)?;
    w.u32(1);
    w.u32(request.heritage);
    w.u32(request.gender);
    for word in [
        a.eyes_strip,
        a.nose_strip,
        a.mouth_strip,
        a.hair_color,
        a.eye_color,
        a.hair_style,
        a.headgear_style,
        a.headgear_color,
        a.shirt_style,
        a.shirt_color,
        a.trousers_style,
        a.trousers_color,
        a.footwear_style,
        a.footwear_color,
    ] {
        w.u32(word);
    }
    for shade in [
        a.skin_shade,
        a.hair_shade,
        a.headgear_shade,
        a.shirt_shade,
        a.trousers_shade,
        a.footwear_shade,
    ] {
        w.f64(shade);
    }
    w.u32(request.template);
    for word in [
        b.strength,
        b.endurance,
        b.coordination,
        b.quickness,
        b.focus,
        b.self_,
    ] {
        w.u32(word);
    }
    w.u32(request.slot);
    w.u32(request.class_id);
    w.u32(SKILL_ADVANCEMENT_CLASS_COUNT as u32);
    for &class in skill_advancement_classes {
        w.u32(class);
    }
    w.string16l(&request.name)?;
    w.u32(request.start_area);
    w.u32(u32::from(request.is_admin));
    w.u32(u32::from(request.is_envoy));
    w.u32(request.checksum());
    Ok(w.into_vec())
}

/// Delete the character in `character_slot` ([`Opcode::CharacterDelete`]):
/// account, then slot. The server answers with the bare opcode.
pub fn build_character_delete(account_name: &str, character_slot: u32) -> Result<Vec<u8>> {
    let mut w = Writer::new();
    w.u32(Opcode::CharacterDelete.value());
    w.string16l(account_name)?;
    w.u32(character_slot);
    Ok(w.into_vec())
}

/// True when `body` is the server's delete acknowledgement: the bare
/// [`Opcode::CharacterDelete`] and nothing else.
#[must_use]
pub fn is_character_delete_acknowledgement(body: &[u8]) -> bool {
    body == Opcode::CharacterDelete.value().to_le_bytes()
}

/// Restore a deleted character ([`Opcode::CharacterRestore`]); the server
/// answers with [`CharGenVerificationResponse`].
#[must_use]
pub fn build_character_restore(character_guid: u32) -> Vec<u8> {
    let mut w = Writer::with_capacity(8);
    w.u32(Opcode::CharacterRestore.value());
    w.u32(character_guid);
    w.into_vec()
}

/// The answer codes of [`CharGenVerificationResponse`].
pub mod chargen_code {
    /// Not set.
    pub const UNDEF: u32 = 0;
    /// Done; the character follows.
    pub const OK: u32 = 1;
    /// Still working.
    pub const PENDING: u32 = 2;
    /// The name is taken.
    pub const NAME_IN_USE: u32 = 3;
    /// The name is not allowed.
    pub const NAME_BANNED: u32 = 4;
    /// The request was malformed.
    pub const CORRUPT: u32 = 5;
    /// The database is down.
    pub const DATABASE_DOWN: u32 = 6;
    /// Administrator creation refused.
    pub const ADMIN_PRIVILEGE_DENIED: u32 = 7;
}

/// The character a successful create or restore produced.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreatedCharacter {
    /// Character id.
    pub guid: u32,
    /// Name.
    pub name: String,
    /// Seconds before it can be played.
    pub seconds_greyed_out: u32,
}

/// The answer to a character create or restore
/// ([`Opcode::CharGenVerificationResponse`]); the same message answers
/// both, so the session routes it by which request is outstanding.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CharGenVerificationResponse {
    /// Answer code (see [`chargen_code`]); any value is kept.
    pub code: u32,
    /// The character, present exactly when the code is
    /// [`chargen_code::OK`].
    pub character: Option<CreatedCharacter>,
}

impl CharGenVerificationResponse {
    /// Parse the body: opcode, code, and for an OK code the character id,
    /// name and greyed-out seconds. Bytes after a non-OK code are ignored.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::CharGenVerificationResponse)?;
        let code = r.u32()?;
        let character = if code == chargen_code::OK {
            Some(CreatedCharacter {
                guid: r.u32()?,
                name: read_string(&mut r)?,
                seconds_greyed_out: r.u32()?,
            })
        } else {
            None
        };
        Ok(Self { code, character })
    }

    /// True for [`chargen_code::OK`].
    #[must_use]
    pub fn is_ok(&self) -> bool {
        self.code == chargen_code::OK
    }
}

/// Spend `xp_spent` experience on an attribute (1 strength .. 6 self).
#[must_use]
pub fn build_raise_attribute(sequence: u32, attribute_id: u32, xp_spent: u32) -> Vec<u8> {
    id_and_word(
        sequence,
        GameActionType::RAISE_ATTRIBUTE,
        attribute_id,
        xp_spent,
    )
}

/// Spend `xp_spent` experience on a vital (its maximum).
#[must_use]
pub fn build_raise_vital(sequence: u32, vital_id: u32, xp_spent: u32) -> Vec<u8> {
    id_and_word(sequence, GameActionType::RAISE_VITAL, vital_id, xp_spent)
}

/// Spend `xp_spent` experience on a skill.
#[must_use]
pub fn build_raise_skill(sequence: u32, skill_id: u32, xp_spent: u32) -> Vec<u8> {
    id_and_word(sequence, GameActionType::RAISE_SKILL, skill_id, xp_spent)
}

/// Spend `credits` skill credits to train a skill.
#[must_use]
pub fn build_train_skill(sequence: u32, skill_id: u32, credits: u32) -> Vec<u8> {
    id_and_word(sequence, GameActionType::TRAIN_SKILL, skill_id, credits)
}

fn id_and_word(sequence: u32, action_type: GameActionType, id: u32, value: u32) -> Vec<u8> {
    let mut w = action(sequence, action_type);
    w.u32(id);
    w.u32(value);
    w.into_vec()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/CharacterActionsTests.cs` and
    //! the body shapes of `WorldSessionCombatTests.cs`.

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    #[test]
    fn advancement_requests_carry_an_id_and_a_u32() {
        assert_eq!(
            build_raise_attribute(1, 5, 12_345_678),
            words(&[0xF7B1, 1, 0x45, 5, 12_345_678])
        );
        for xp in [0, 1, u32::MAX] {
            assert_eq!(
                build_raise_vital(1, 1, xp),
                words(&[0xF7B1, 1, 0x44, 1, xp])
            );
            assert_eq!(
                build_raise_skill(1, 8, xp),
                words(&[0xF7B1, 1, 0x46, 8, xp])
            );
        }
        assert_eq!(build_train_skill(1, 8, 4), words(&[0xF7B1, 1, 0x47, 8, 4]));
    }

    // Character creation, deletion and restore: cases ported from OpenAC
    // `tests/AcDream.Core.Net.Tests/Messages/CharacterCreateTests.cs`,
    // `CharacterDeleteTests.cs`, `CharacterRestoreTests.cs` and
    // `CharGenVerificationResponseTests.cs`.

    fn request() -> CharacterCreateRequest {
        CharacterCreateRequest {
            heritage: 1,
            gender: 0,
            appearance: CharacterAppearance {
                eyes_strip: 2,
                nose_strip: 3,
                mouth_strip: 4,
                hair_color: 5,
                eye_color: 6,
                hair_style: 7,
                headgear_style: 8,
                headgear_color: 9,
                shirt_style: 10,
                shirt_color: 11,
                trousers_style: 12,
                trousers_color: 13,
                footwear_style: 14,
                footwear_color: 15,
                skin_shade: 0.1,
                hair_shade: 0.2,
                headgear_shade: 0.3,
                shirt_shade: 0.4,
                trousers_shade: 0.5,
                footwear_shade: 0.6,
            },
            template: 16,
            attributes: CharacterAttributes {
                strength: 17,
                endurance: 18,
                coordination: 19,
                quickness: 20,
                focus: 21,
                self_: 22,
            },
            slot: 0,
            class_id: 1,
            name: "Testcdream".into(),
            start_area: 23,
            is_admin: false,
            is_envoy: false,
        }
    }

    #[test]
    fn create_follows_the_retail_field_order() {
        let request = request();
        let skills: [u32; SKILL_ADVANCEMENT_CLASS_COUNT] = core::array::from_fn(|i| i as u32);
        let body = build_character_create("testaccount", &request, &skills).unwrap();
        let mut r = Reader::new(&body);
        assert_eq!(r.u32(), Ok(0xF656));
        assert_eq!(read_string(&mut r).as_deref(), Ok("testaccount"));
        assert_eq!(r.u32(), Ok(1));
        assert_eq!((r.u32(), r.u32()), (Ok(1), Ok(0)));
        for expected in 2..=15 {
            assert_eq!(r.u32(), Ok(expected));
        }
        for expected in [0.1, 0.2, 0.3, 0.4, 0.5, 0.6] {
            assert_eq!(r.f64(), Ok(expected));
        }
        for expected in 16..=22 {
            assert_eq!(r.u32(), Ok(expected));
        }
        assert_eq!((r.u32(), r.u32()), (Ok(0), Ok(1)));
        assert_eq!(r.u32(), Ok(55));
        for expected in 0..55 {
            assert_eq!(r.u32(), Ok(expected));
        }
        assert_eq!(read_string(&mut r).as_deref(), Ok("Testcdream"));
        assert_eq!(r.u32(), Ok(23));
        assert_eq!((r.u32(), r.u32()), (Ok(0), Ok(0)));
        assert_eq!(r.u32(), Ok(request.checksum()));
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn create_with_short_strings_and_flags() {
        let request = CharacterCreateRequest {
            heritage: 1,
            gender: 0,
            appearance: CharacterAppearance::default(),
            template: 0,
            attributes: CharacterAttributes::default(),
            slot: 0,
            class_id: 1,
            name: "ab".into(),
            start_area: 0,
            is_admin: true,
            is_envoy: true,
        };
        let body = build_character_create("cd", &request, &[0; 55]).unwrap();
        assert_eq!(body[..8], [0x56, 0xF6, 0, 0, 2, 0, b'c', b'd']);
        // Opcode, account, version, heritage, gender, 14 words, 6 doubles,
        // template, 6 attributes, slot, class, count, 55 classes, name,
        // start area, admin, envoy, checksum.
        assert_eq!(
            body.len(),
            4 + 4 + 4 + 8 + 56 + 48 + 4 + 24 + 8 + 4 + 220 + 4 + 4 + 8 + 4
        );
        let tail = &body[body.len() - 12..];
        assert_eq!(tail, words(&[1, 1, 1]));
    }

    #[test]
    fn create_checksum() {
        let request = request();
        assert_eq!(request.checksum(), 205);
        let mut mutated = request.clone();
        mutated.appearance.headgear_color += 1000;
        mutated.appearance.shirt_color += 1000;
        mutated.appearance.trousers_color += 1000;
        mutated.appearance.footwear_color += 1000;
        mutated.appearance.skin_shade += 5.0;
        mutated.appearance.hair_shade += 5.0;
        mutated.slot += 7;
        mutated.class_id += 7;
        assert_eq!(mutated.checksum(), 205);
    }

    #[test]
    fn delete_request_and_acknowledgement() {
        let body = build_character_delete("testaccount", 3).unwrap();
        assert_eq!(body.len(), 24);
        assert_eq!(body[4..6], [11, 0]);
        assert_eq!(body[6..17], *b"testaccount");
        assert_eq!(body[17..20], [0, 0, 0]);
        assert_eq!(body[20..], 3u32.to_le_bytes());
        assert_eq!(
            build_character_delete("ab", 0x11).unwrap(),
            [0x55, 0xF6, 0, 0, 2, 0, b'a', b'b', 0x11, 0, 0, 0]
        );
        assert!(is_character_delete_acknowledgement(
            &0xF655u32.to_le_bytes()
        ));
        assert!(!is_character_delete_acknowledgement(
            &build_character_delete("acct", 1).unwrap()
        ));
        assert!(!is_character_delete_acknowledgement(&[0x55, 0xF6, 0]));
        assert!(!is_character_delete_acknowledgement(
            &0xF656u32.to_le_bytes()
        ));
        assert!(!is_character_delete_acknowledgement(&[]));
    }

    #[test]
    fn restore_request() {
        assert_eq!(
            build_character_restore(0x5000_0001),
            [0xD9, 0xF7, 0, 0, 0x01, 0, 0, 0x50]
        );
    }

    fn verification(code: u32, character: Option<(u32, &str, u32)>) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF643);
        w.u32(code);
        if let Some((guid, name, seconds)) = character {
            w.u32(guid);
            w.string16l(name).unwrap();
            w.u32(seconds);
        }
        w.into_vec()
    }

    #[test]
    fn verification_ok_carries_the_character() {
        let body = verification(1, Some((0x5000_000B, "+NewChar", 0)));
        let parsed = CharGenVerificationResponse::parse(&body).unwrap();
        assert!(parsed.is_ok());
        assert_eq!(
            parsed.character,
            Some(CreatedCharacter {
                guid: 0x5000_000B,
                name: "+NewChar".into(),
                seconds_greyed_out: 0
            })
        );
        assert_every_prefix_fails(&body, CharGenVerificationResponse::parse);
        let restored = verification(1, Some((0x5000_0003, "Restored", 45)));
        assert_eq!(
            CharGenVerificationResponse::parse(&restored)
                .unwrap()
                .character
                .unwrap()
                .seconds_greyed_out,
            45
        );
    }

    #[test]
    fn verification_other_codes_are_the_flag_only() {
        for code in [0, 2, 3, 4, 5, 6, 7, 99] {
            let parsed = CharGenVerificationResponse::parse(&verification(code, None)).unwrap();
            assert_eq!(parsed.code, code);
            assert!(!parsed.is_ok());
            assert!(parsed.character.is_none());
        }
        // Trailing bytes after a failure code are ignored, not misread.
        let parsed =
            CharGenVerificationResponse::parse(&words(&[0xF643, 3, 0xDEAD_BEEF, 0x1234_5678]))
                .unwrap();
        assert!(parsed.character.is_none());
        assert!(CharGenVerificationResponse::parse(&words(&[0xDEAD_BEEF])).is_err());
        assert!(CharGenVerificationResponse::parse(&words(&[0xF643, 1])).is_err());
        assert!(CharGenVerificationResponse::parse(&words(&[0xF643])).is_err());
    }
}
