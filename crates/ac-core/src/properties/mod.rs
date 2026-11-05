//! Property keys: the numeric ids of an object's typed properties.
//!
//! Objects carry seven property tables, one per payload type (32-bit int,
//! 64-bit int, bool, float, string, dat id, object id). The server names a
//! property by its table and a `u32` key, in update messages and in the
//! property bundles of object descriptions and appraisals.
//!
//! Each key type is a `u32` newtype, not a closed enum, so a key this client
//! has no name for still round-trips: `PropertyInt(9999)` is a valid value
//! whose [`name`](PropertyInt::name) is `None`.

/// Defines one property-key newtype: its named constants, the sorted
/// `ALL` table, name lookup both ways, and `Debug` that prints the name.
macro_rules! property_table {
    (
        $(#[$meta:meta])*
        $ty:ident {
            $($konst:ident = $value:literal => $name:literal,)*
        }
    ) => {
        $(#[$meta])*
        #[derive(Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Default)]
        pub struct $ty(pub u32);

        impl $ty {
            $(
                #[doc = concat!("`", $name, "` (", stringify!($value), ").")]
                pub const $konst: Self = Self($value);
            )*

            /// Every named key with its name, in ascending key order.
            pub const ALL: &'static [(Self, &'static str)] = &[$((Self::$konst, $name),)*];

            /// The key's name, or `None` for a key this client does not name.
            #[must_use]
            pub fn name(self) -> Option<&'static str> {
                Self::ALL
                    .binary_search_by_key(&self, |&(key, _)| key)
                    .ok()
                    .and_then(|index| Self::ALL.get(index))
                    .map(|&(_, name)| name)
            }

            /// The key with this exact name.
            #[must_use]
            pub fn from_name(name: &str) -> Option<Self> {
                Self::ALL
                    .iter()
                    .find(|&&(_, candidate)| candidate == name)
                    .map(|&(key, _)| key)
            }
        }

        impl From<u32> for $ty {
            fn from(value: u32) -> Self {
                Self(value)
            }
        }

        impl From<$ty> for u32 {
            fn from(key: $ty) -> u32 {
                key.0
            }
        }

        impl ::std::fmt::Debug for $ty {
            fn fmt(&self, f: &mut ::std::fmt::Formatter<'_>) -> ::std::fmt::Result {
                match self.name() {
                    Some(name) => write!(f, "{}::{}", stringify!($ty), name),
                    None => write!(f, "{}({})", stringify!($ty), self.0),
                }
            }
        }
    };
}

mod bools;
mod data_ids;
mod floats;
mod instance_ids;
mod int64s;
mod ints;
mod strings;

pub use bools::PropertyBool;
pub use data_ids::PropertyDataId;
pub use floats::PropertyFloat;
pub use instance_ids::PropertyInstanceId;
pub use int64s::PropertyInt64;
pub use ints::PropertyInt;
pub use strings::PropertyString;

#[cfg(test)]
mod golden;

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Properties/PropertyEnumConformanceTests.cs`.

    use super::*;

    /// One family's table, as plain (name, value) rows.
    fn rows(family: &str) -> Vec<(&'static str, u32)> {
        fn collect<T: Copy + Into<u32>>(
            all: &'static [(T, &'static str)],
        ) -> Vec<(&'static str, u32)> {
            all.iter().map(|&(key, name)| (name, key.into())).collect()
        }
        match family {
            "PropertyInt" => collect(PropertyInt::ALL),
            "PropertyInt64" => collect(PropertyInt64::ALL),
            "PropertyBool" => collect(PropertyBool::ALL),
            "PropertyFloat" => collect(PropertyFloat::ALL),
            "PropertyString" => collect(PropertyString::ALL),
            "PropertyDataId" => collect(PropertyDataId::ALL),
            "PropertyInstanceId" => collect(PropertyInstanceId::ALL),
            other => panic!("unknown property family {other}"),
        }
    }

    fn golden(family: &str) -> &'static [(&'static str, u32)] {
        match family {
            "PropertyInt" => golden::PROPERTY_INT,
            "PropertyInt64" => golden::PROPERTY_INT64,
            "PropertyBool" => golden::PROPERTY_BOOL,
            "PropertyFloat" => golden::PROPERTY_FLOAT,
            "PropertyString" => golden::PROPERTY_STRING,
            "PropertyDataId" => golden::PROPERTY_DATA_ID,
            "PropertyInstanceId" => golden::PROPERTY_INSTANCE_ID,
            other => panic!("unknown property family {other}"),
        }
    }

    const FAMILIES: [&str; 7] = [
        "PropertyInt",
        "PropertyInt64",
        "PropertyBool",
        "PropertyFloat",
        "PropertyString",
        "PropertyDataId",
        "PropertyInstanceId",
    ];

    fn sorted(mut v: Vec<(&'static str, u32)>) -> Vec<(&'static str, u32)> {
        v.sort_by(|a, b| a.1.cmp(&b.1).then(a.0.cmp(b.0)));
        v
    }

    #[test]
    fn table_matches_golden_table_exactly() {
        for family in FAMILIES {
            assert_eq!(
                sorted(rows(family)),
                sorted(golden(family).to_vec()),
                "{family}"
            );
        }
    }

    #[test]
    fn no_two_members_share_a_value() {
        for family in FAMILIES {
            let mut values: Vec<u32> = golden(family).iter().map(|&(_, v)| v).collect();
            values.sort_unstable();
            let before = values.len();
            values.dedup();
            assert_eq!(before, values.len(), "{family}");
        }
    }

    /// `name()` binary-searches `ALL`, so the table must be strictly ascending.
    #[test]
    fn all_is_strictly_ascending() {
        for family in FAMILIES {
            let values: Vec<u32> = rows(family).iter().map(|&(_, v)| v).collect();
            assert!(values.windows(2).all(|w| w[0] < w[1]), "{family}");
        }
    }

    fn lookup(family: &str, value: u32) -> Option<&'static str> {
        match family {
            "PropertyInt" => PropertyInt(value).name(),
            "PropertyInt64" => PropertyInt64(value).name(),
            "PropertyBool" => PropertyBool(value).name(),
            "PropertyFloat" => PropertyFloat(value).name(),
            "PropertyString" => PropertyString(value).name(),
            "PropertyDataId" => PropertyDataId(value).name(),
            "PropertyInstanceId" => PropertyInstanceId(value).name(),
            other => panic!("unknown property family {other}"),
        }
    }

    fn reverse(family: &str, name: &str) -> Option<u32> {
        match family {
            "PropertyInt" => PropertyInt::from_name(name).map(u32::from),
            "PropertyInt64" => PropertyInt64::from_name(name).map(u32::from),
            "PropertyBool" => PropertyBool::from_name(name).map(u32::from),
            "PropertyFloat" => PropertyFloat::from_name(name).map(u32::from),
            "PropertyString" => PropertyString::from_name(name).map(u32::from),
            "PropertyDataId" => PropertyDataId::from_name(name).map(u32::from),
            "PropertyInstanceId" => PropertyInstanceId::from_name(name).map(u32::from),
            other => panic!("unknown property family {other}"),
        }
    }

    #[test]
    fn attested_member_has_exact_wire_value() {
        for &(family, name, value) in golden::ATTESTED {
            assert_eq!(
                lookup(family, value),
                Some(name),
                "{family}.{name} ({value})"
            );
            assert_eq!(reverse(family, name), Some(value), "{family}.{name}");
        }
    }

    #[test]
    fn named_constants_carry_their_wire_values() {
        assert_eq!(PropertyInt::ITEM_TYPE, PropertyInt(1));
        assert_eq!(PropertyInt::STACK_SIZE, PropertyInt(12));
        assert_eq!(PropertyString::PLURAL_NAME, PropertyString(20));
        assert_eq!(PropertyDataId::SETUP, PropertyDataId(1));
        assert_eq!(PropertyInstanceId::CONTAINER, PropertyInstanceId(2));
    }

    #[test]
    fn unknown_keys_stay_representable() {
        let unknown = PropertyInt(0xFFFF_FFF0);
        assert_eq!(unknown.name(), None);
        assert_eq!(format!("{unknown:?}"), "PropertyInt(4294967280)");
        assert_eq!(
            format!("{:?}", PropertyInt::ITEM_TYPE),
            "PropertyInt::ItemType"
        );
        assert_eq!(PropertyInt::from_name("NoSuchProperty"), None);
        assert_eq!(PropertyInt::default(), PropertyInt::UNDEF);
    }
}
