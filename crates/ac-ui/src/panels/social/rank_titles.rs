//! Allegiance rank titles: the title a member of a heritage and gender
//! holds at ranks 1..=10, and the name shown with it ("Duke Bob").

/// The player's allegiance rank property (the rank quality the self block
/// compares with the profile's rank).
pub const ALLEGIANCE_RANK_PROPERTY: u32 = 0x1E;

/// `name` with the rank title in front and one space, or `name` alone when
/// no title applies.
#[must_use]
pub fn compose_full_name(rank: i32, heritage_group: i32, gender: i32, name: &str) -> String {
    match title(rank, heritage_group, gender) {
        Some(t) if !t.is_empty() => format!("{t} {name}"),
        _ => name.to_owned(),
    }
}

/// The rank title, if any: gender 1 male, 2 female; heritage 1..=11
/// (10 shares the Shadowbound titles); some heritages author one gender's
/// table and use it for both.
#[must_use]
pub fn title(rank: i32, heritage_group: i32, gender: i32) -> Option<&'static str> {
    if !heritage_in_range(heritage_group) {
        return None;
    }
    let table: &[&str; 10] = match (gender, heritage_group) {
        (1, 1) => &ALUVIAN_MALE,
        (1, 2) => &GHARUNDIM_MALE,
        (1, 3) => &SHO_MALE,
        (1, 4) => &VIAMONTIAN_MALE,
        (1, 5 | 0xA) => &SHADOWBOUND_MALE,
        (1 | 2, 6) => &GEARKNIGHT_MALE,
        (1 | 2, 7) => &TUMEROK_MALE,
        (1 | 2, 8) => &LUGIAN_FEMALE,
        (1, 9) => &EMPYREAN_MALE,
        (1, 0xB) => &UNDEAD_MALE,
        (2, 1) => &ALUVIAN_FEMALE,
        (2, 2) => &GHARUNDIM_FEMALE,
        (2, 3) => &SHO_FEMALE,
        (2, 4) => &VIAMONTIAN_FEMALE,
        (2, 5 | 0xA) => &SHADOWBOUND_FEMALE,
        (2, 9) => &EMPYREAN_FEMALE,
        (2, 0xB) => &UNDEAD_FEMALE,
        _ => return None,
    };
    usize::try_from(rank - 1)
        .ok()
        .and_then(|i| table.get(i))
        .copied()
}

/// Heritage ids 1..=11, tested as one unsigned comparison.
fn heritage_in_range(heritage_group: i32) -> bool {
    (heritage_group.wrapping_sub(1) as u32) <= 0xA
}

const ALUVIAN_MALE: [&str; 10] = [
    "Yeoman",
    "Baronet",
    "Baron",
    "Reeve",
    "Thane",
    "Ealdor",
    "Duke",
    "Aetheling",
    "King",
    "High King",
];
const ALUVIAN_FEMALE: [&str; 10] = [
    "Yeoman",
    "Baronet",
    "Baroness",
    "Reeve",
    "Thane",
    "Ealdor",
    "Duchess",
    "Aetheling",
    "Queen",
    "High Queen",
];
const GHARUNDIM_MALE: [&str; 10] = [
    "Sayyid", "Shayk", "Maulan", "Mu'allim", "Naquib", "Qadi", "Mushir", "Amir", "Malik", "Sultan",
];
const GHARUNDIM_FEMALE: [&str; 10] = [
    "Sayyida",
    "Shayka",
    "Maulana",
    "Mu'allima",
    "Naquiba",
    "Qadiya",
    "Mushira",
    "Amira",
    "Malika",
    "Sultana",
];
const SHO_MALE: [&str; 10] = [
    "Jinin",
    "Jo-chueh",
    "Nan-chueh",
    "Shi-chueh",
    "Ta-chueh",
    "Kun-chueh",
    "Kou",
    "Taikou",
    "Ou",
    "Koutei",
];
const SHO_FEMALE: [&str; 10] = [
    "Jinin",
    "Jo-chueh",
    "Nan-chueh",
    "Shi-chueh",
    "Ta-chueh",
    "Kun-chueh",
    "Kou",
    "Taikou",
    "Jo-ou",
    "Koutei",
];
const VIAMONTIAN_MALE: [&str; 10] = [
    "Squire",
    "Banner",
    "Baron",
    "Viscount",
    "Count",
    "Marquis",
    "Duke",
    "Grand Duke",
    "King",
    "High King",
];
const VIAMONTIAN_FEMALE: [&str; 10] = [
    "Dame",
    "Banner",
    "Baroness",
    "Viscountess",
    "Countess",
    "Marquise",
    "Duchess",
    "Grand Duchess",
    "Queen",
    "High Queen",
];
const SHADOWBOUND_MALE: [&str; 10] = [
    "Tenebrous",
    "Shade",
    "Squire",
    "Knight",
    "Void Knight",
    "Void Lord",
    "Duke",
    "Archduke",
    "Highborn",
    "King",
];
const SHADOWBOUND_FEMALE: [&str; 10] = [
    "Tenebrous",
    "Shade",
    "Squire",
    "Knight",
    "Void Knight",
    "Void Lady",
    "Duchess",
    "Archduchess",
    "Highborn",
    "Queen",
];
const GEARKNIGHT_MALE: [&str; 10] = [
    "Tribunus",
    "Praefectus",
    "Optio",
    "Centurion",
    "Principes",
    "Legatus",
    "Consul",
    "Dux",
    "Secondus",
    "Primus",
];
const TUMEROK_MALE: [&str; 10] = [
    "Xutua", "Tuona", "Ona", "Nuona", "Turea", "Rea", "Nurea", "Kauh", "Sutah", "Tah",
];
const LUGIAN_FEMALE: [&str; 10] = [
    "Laigus", "Raigus", "Amploth", "Arintoth", "Obeloth", "Lithos", "Kantos", "Gigas", "Extas",
    "Tiatus",
];
const EMPYREAN_MALE: [&str; 10] = [
    "Ensign",
    "Corporal",
    "Lieutenant",
    "Commander",
    "Captain",
    "Commodore",
    "Admiral",
    "Warlord",
    "Ipharsin",
    "Aulin",
];
const EMPYREAN_FEMALE: [&str; 10] = [
    "Ensign",
    "Corporal",
    "Lieutenant",
    "Commander",
    "Captain",
    "Commodore",
    "Admiral",
    "Warlord",
    "Ipharsia",
    "Aulia",
];
const UNDEAD_MALE: [&str; 10] = [
    "Neophyte",
    "Acolyte",
    "Adept",
    "Esquire",
    "Squire",
    "Knight",
    "Count",
    "Viscount",
    "Highness",
    "Annointed",
];
const UNDEAD_FEMALE: [&str; 10] = [
    "Neophyte",
    "Acolyte",
    "Adept",
    "Esquire",
    "Squire",
    "Knight",
    "Countess",
    "Viscountess",
    "Highness",
    "Annointed",
];
