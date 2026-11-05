//! `wire_enum!`: a `u32` newtype with named constants, for enums and bit
//! sets that arrive on the wire. A newtype rather than a Rust `enum`, so a
//! value the client has no name for (a future bit, a server extension) is
//! kept instead of rejected.
//!
//! `wire_enum! { values Name { CONST = 1 => "Name", ... } }` gives the
//! constants, the `NAMED` table, `name`, `from_name`, conversions and a
//! `Debug` that prints the name. `flags` instead of `values` adds the bit-set
//! operations (`contains`, `intersects`, `|`, `&`, `^`, `!`, ...).

macro_rules! wire_enum {
    (values $($rest:tt)*) => {
        $crate::wire_enum::wire_enum!(@common $($rest)*);
    };
    (flags $($rest:tt)*) => {
        $crate::wire_enum::wire_enum!(@common $($rest)*);
        $crate::wire_enum::wire_enum!(@bits $($rest)*);
    };
    (@common
        $(#[$meta:meta])*
        $ty:ident {
            $($(#[$cmeta:meta])* $konst:ident = $value:literal => $name:literal,)*
        }
    ) => {
        $(#[$meta])*
        #[derive(Clone, Copy, PartialEq, Eq, Hash, Default)]
        pub struct $ty(pub u32);

        impl $ty {
            $($(#[$cmeta])* pub const $konst: Self = Self($value);)*

            /// Every named value with its name, in declaration order.
            /// Aliases (two names for one value) are all listed.
            pub const NAMED: &'static [(Self, &'static str)] = &[$((Self::$konst, $name),)*];

            /// The raw value.
            #[must_use]
            pub const fn bits(self) -> u32 {
                self.0
            }

            /// The first name declared for exactly this value, if any.
            #[must_use]
            pub fn name(self) -> Option<&'static str> {
                Self::NAMED
                    .iter()
                    .find(|&&(value, _)| value == self)
                    .map(|&(_, name)| name)
            }

            /// The value with this exact name.
            #[must_use]
            pub fn from_name(name: &str) -> Option<Self> {
                Self::NAMED
                    .iter()
                    .find(|&&(_, candidate)| candidate == name)
                    .map(|&(value, _)| value)
            }
        }

        impl From<u32> for $ty {
            fn from(value: u32) -> Self {
                Self(value)
            }
        }

        impl From<$ty> for u32 {
            fn from(value: $ty) -> u32 {
                value.0
            }
        }

        impl ::std::fmt::Debug for $ty {
            fn fmt(&self, f: &mut ::std::fmt::Formatter<'_>) -> ::std::fmt::Result {
                match self.name() {
                    Some(name) => write!(f, "{}::{}", stringify!($ty), name),
                    None => write!(f, "{}({:#010X})", stringify!($ty), self.0),
                }
            }
        }
    };
    (@bits $(#[$meta:meta])* $ty:ident { $($body:tt)* }) => {
        impl $ty {
            /// True if every bit of `other` is set.
            #[must_use]
            pub const fn contains(self, other: Self) -> bool {
                self.0 & other.0 == other.0
            }

            /// True if any bit of `other` is set.
            #[must_use]
            pub const fn intersects(self, other: Self) -> bool {
                self.0 & other.0 != 0
            }

            /// True if no bit is set.
            #[must_use]
            pub const fn is_empty(self) -> bool {
                self.0 == 0
            }

            /// Sets every bit of `other`.
            pub fn insert(&mut self, other: Self) {
                self.0 |= other.0;
            }

            /// Clears every bit of `other`.
            pub fn remove(&mut self, other: Self) {
                self.0 &= !other.0;
            }

            /// Sets or clears every bit of `other`.
            pub fn set(&mut self, other: Self, on: bool) {
                if on {
                    self.insert(other);
                } else {
                    self.remove(other);
                }
            }
        }

        impl ::std::ops::BitOr for $ty {
            type Output = Self;
            fn bitor(self, rhs: Self) -> Self {
                Self(self.0 | rhs.0)
            }
        }

        impl ::std::ops::BitOrAssign for $ty {
            fn bitor_assign(&mut self, rhs: Self) {
                self.0 |= rhs.0;
            }
        }

        impl ::std::ops::BitAnd for $ty {
            type Output = Self;
            fn bitand(self, rhs: Self) -> Self {
                Self(self.0 & rhs.0)
            }
        }

        impl ::std::ops::BitAndAssign for $ty {
            fn bitand_assign(&mut self, rhs: Self) {
                self.0 &= rhs.0;
            }
        }

        impl ::std::ops::BitXor for $ty {
            type Output = Self;
            fn bitxor(self, rhs: Self) -> Self {
                Self(self.0 ^ rhs.0)
            }
        }

        impl ::std::ops::Not for $ty {
            type Output = Self;
            fn not(self) -> Self {
                Self(!self.0)
            }
        }
    };
}

pub(crate) use wire_enum;
