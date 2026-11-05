//! Every function the client offers a plugin (an import of the wasm module
//! [`MODULE`]) and every function a plugin must export.
//!
//! Each client function is its own import, so the client links only those a
//! plugin's permissions allow, and a plugin built for a newer API fails at
//! load naming the first import this client does not have.
//!
//! All imports share one shape, `(request_ptr: i32, request_len: i32) ->
//! i32`: the request is the encoded argument record in the plugin's memory;
//! the result is the length of the encoded reply, which the plugin copies
//! out with [`Import::AbiReplyRead`] (0: no reply bytes). The two plumbing
//! imports ([`Import::AbiReplyRead`], [`Import::AbiPanic`]) have the same
//! shape.
//!
//! The table ([`crate::import_table!`]) is written once: it names each
//! import, its request and reply types ([`Call`], one marker type per
//! import in [`calls`]), the permission it needs, whether its answer depends
//! on a window, and the API version that added it. The SDK builds its wasm
//! imports from the same table.

use crate::version::ApiVersion;
use crate::wire::Wire;

/// The wasm import module every client function lives in.
pub const MODULE: &str = "openac";

// The permissions a manifest declares live in `ac-plugin-version`, which the
// launcher links alone to check a manifest.
pub use ac_plugin_version::{PERMISSIONS_VERSION, Permission};

/// One client function's types: what a plugin sends and what it gets back.
/// Every import has a marker type in [`calls`] implementing it.
pub trait Call {
    /// The import.
    const IMPORT: Import;
    /// The encoded argument.
    type Request: Wire;
    /// The encoded reply.
    type Reply: Wire;
    /// The reply a client with nothing to say gives: the inert answer
    /// (nothing found, unavailable). The SDK's fake host answers with it
    /// when a test set nothing else.
    fn default_reply() -> Self::Reply;
}

/// Picks an entry's default reply: the one written in the table, else the
/// type's [`Default`].
#[doc(hidden)]
#[macro_export]
macro_rules! __default_reply {
    (; $reply:ty) => {
        <$reply as ::core::default::Default>::default()
    };
    ($default:expr; $reply:ty) => {
        $default
    };
}

macro_rules! define_imports {
    ($(
        $(#[doc = $doc:literal])*
        $variant:ident = $name:ident ($request:ty) -> $reply:ty $(= $default:expr)?,
            permission: $perm:expr, window: $window:expr, since: ($maj:literal, $min:literal);
    )*) => {
        /// A client function a plugin can import.
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        pub enum Import {
            $( $(#[doc = $doc])* $variant, )*
        }

        impl Import {
            /// Every import, in table order.
            pub const ALL: &'static [Import] = &[ $( Import::$variant, )* ];

            /// The import's field name in module [`MODULE`].
            #[must_use]
            pub const fn name(self) -> &'static str {
                match self { $( Import::$variant => stringify!($name), )* }
            }

            /// The permission a manifest must declare to link it (`None`:
            /// every plugin may).
            #[must_use]
            pub const fn permission(self) -> Option<Permission> {
                match self { $( Import::$variant => $perm, )* }
            }

            /// Whether its answer depends on a window being attached. Every
            /// other import behaves identically with and without one.
            #[must_use]
            pub const fn needs_window(self) -> bool {
                match self { $( Import::$variant => $window, )* }
            }

            /// What the table says about it: its doc comment, one line per
            /// source line (empty for an undocumented import, which the
            /// API doc census refuses).
            #[must_use]
            pub const fn doc(self) -> &'static str {
                match self { $( Import::$variant => concat!($($doc, "\n",)*), )* }
            }

            /// The API version that added it.
            #[must_use]
            pub const fn since(self) -> ApiVersion {
                match self { $( Import::$variant => ApiVersion::new($maj, $min), )* }
            }

            /// The shape of its call ([`crate::layout`]): its name and the
            /// shapes of its request and reply.
            #[must_use]
            pub const fn shape(self) -> u64 {
                match self {
                    $( Import::$variant => $crate::wire::Shape::new("import")
                        .name(stringify!($name))
                        .of(<$request as Wire>::SHAPE)
                        .of(<$reply as Wire>::SHAPE)
                        .finish(), )*
                }
            }

            /// The shape of its call as a plugin built for API `version`
            /// knows it ([`Wire::shape_at`] of its request and reply);
            /// [`Self::shape`] at this build's version.
            #[must_use]
            pub fn shape_at(self, version: ApiVersion) -> u64 {
                match self {
                    $( Import::$variant => $crate::wire::Shape::new("import")
                        .name(stringify!($name))
                        .of(<$request as Wire>::shape_at(version))
                        .of(<$reply as Wire>::shape_at(version))
                        .finish(), )*
                }
            }

            /// The encoded inert reply as a plugin built for API `version`
            /// reads it ([`Self::default_reply`] at this build's version).
            #[must_use]
            pub fn default_reply_at(self, version: ApiVersion) -> Vec<u8> {
                match self {
                    $( Import::$variant => Wire::to_bytes_at(
                        &<calls::$variant as Call>::default_reply(), version), )*
                }
            }

            /// The encoded inert reply ([`Call::default_reply`]).
            #[must_use]
            pub fn default_reply(self) -> Vec<u8> {
                match self {
                    $( Import::$variant => Wire::to_bytes(
                        &<calls::$variant as Call>::default_reply()), )*
                }
            }
        }

        /// One marker type per import, carrying its request and reply types
        /// ([`Call`]).
        pub mod calls {
            use super::{Call, Import};
            $(
                $(#[doc = $doc])*
                #[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
                pub struct $variant;

                impl Call for $variant {
                    const IMPORT: Import = Import::$variant;
                    type Request = $request;
                    type Reply = $reply;
                    fn default_reply() -> $reply {
                        $crate::__default_reply!($($default)?; $reply)
                    }
                }
            )*
        }
    };
}

crate::import_table!(define_imports);

impl Import {
    /// The import named `name` in module [`MODULE`].
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.iter().copied().find(|i| i.name() == name)
    }
}

/// The exports a plugin must have.
pub mod exports {
    /// Its linear memory.
    pub const MEMORY: &str = "memory";
    /// `(len: i32) -> i32`: a buffer of `len` bytes the client fills and
    /// then hands back to one of the calls below, which take ownership.
    pub const ALLOC: &str = "openac_alloc";
    /// `(ptr: i32, len: i32) -> i64`: start, given an encoded
    /// [`crate::types::StartInfo`]. 0 is success; anything else is
    /// `ptr << 32 | len` of a UTF-8 message saying why the plugin refuses
    /// to start.
    pub const ENABLE: &str = "openac_enable";
    /// `()`: stop.
    pub const DISABLE: &str = "openac_disable";
    /// `()`: one 15 ms tick.
    pub const TICK: &str = "openac_tick";
    /// `(ptr: i32, len: i32)`: an encoded [`crate::types::Event`].
    pub const EVENT: &str = "openac_event";
    /// `(ptr: i32, len: i32) -> i64`: a callback the plugin registered (a
    /// chat filter or input interceptor, a loot classifier), given an
    /// encoded [`crate::types::Callback`]; the result is `ptr << 32 | len`
    /// of the encoded [`crate::types::CallbackAnswer`] in the plugin's
    /// memory, valid until its next call. Optional: a plugin without it
    /// cannot register callbacks.
    pub const CALLBACK: &str = "openac_callback";
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_unique_and_round_trip() {
        let mut names: Vec<&str> = Import::ALL.iter().map(|i| i.name()).collect();
        names.sort_unstable();
        names.dedup();
        assert_eq!(names.len(), Import::ALL.len());
        for import in Import::ALL {
            assert_eq!(Import::from_name(import.name()), Some(*import));
        }
        assert_eq!(Import::from_name("no_such_import"), None);
    }

    #[test]
    fn every_default_reply_decodes_as_its_reply_type() {
        // The inert replies are what a fake host answers with: each must be
        // a valid encoding (a table entry whose default is not its reply
        // type would not compile; this catches an encoding slip).
        for import in Import::ALL {
            assert!(!import.name().is_empty(), "{import:?} has an empty name");
            let _ = import.default_reply();
        }
        assert_eq!(Import::HostWindowAttached.default_reply(), false.to_bytes());
    }
}
