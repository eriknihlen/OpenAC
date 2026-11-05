//! Who may enter a house.

use std::collections::BTreeMap;

/// A house's entry restrictions: open to all, open to one allegiance, or
/// only to the listed guests.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct HouseRestrictionRecord {
    pub open_to_public: bool,
    /// The allegiance whose members may enter; 0 for none.
    pub allegiance_monarch_id: u32,
    /// Guest id → the guest's storage permission.
    pub guests: BTreeMap<u32, u32>,
}

impl HouseRestrictionRecord {
    /// Whether `mover` (whose monarch is `mover_monarch_id`) may enter: a
    /// public house admits everyone, an allegiance house admits that
    /// monarch's followers, and the guest list admits its guests.
    #[must_use]
    pub fn is_allowed_in(&self, mover_id: u32, mover_monarch_id: u32) -> bool {
        if self.open_to_public {
            return true;
        }
        if self.allegiance_monarch_id != 0 && mover_monarch_id == self.allegiance_monarch_id {
            return true;
        }
        mover_id != 0 && self.guests.contains_key(&mover_id)
    }
}

#[cfg(test)]
mod tests {
    //! Rule from OpenAC `src/AcDream.Core/Items/HouseRestrictions.cs`
    //! (`HouseRestrictionRecord.IsAllowedIn`); OpenAC covers it only through
    //! network tests, so these pin each branch.

    use super::*;

    #[test]
    fn public_house_admits_everyone() {
        let house = HouseRestrictionRecord {
            open_to_public: true,
            ..Default::default()
        };
        assert!(house.is_allowed_in(0, 0));
    }

    #[test]
    fn allegiance_house_admits_followers_of_its_monarch_only() {
        let house = HouseRestrictionRecord {
            allegiance_monarch_id: 0x5000_0009,
            ..Default::default()
        };
        assert!(house.is_allowed_in(0x5000_0001, 0x5000_0009));
        assert!(!house.is_allowed_in(0x5000_0001, 0x5000_0008));
        let no_allegiance = HouseRestrictionRecord::default();
        assert!(!no_allegiance.is_allowed_in(0x5000_0001, 0));
    }

    #[test]
    fn guest_list_admits_its_guests_but_never_guid_zero() {
        let mut house = HouseRestrictionRecord::default();
        house.guests.insert(0x5000_0002, 1);
        house.guests.insert(0, 1);
        assert!(house.is_allowed_in(0x5000_0002, 0));
        assert!(!house.is_allowed_in(0x5000_0003, 0));
        assert!(!house.is_allowed_in(0, 0));
    }
}
