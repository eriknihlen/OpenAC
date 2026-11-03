//! The SDK for openAC plugins.
//!
//! A plugin is an ordinary Rust crate of type `cdylib` built for
//! `wasm32-unknown-unknown` (with `panic = "abort"`: a panic stops the
//! plugin and the client unloads it). It implements [`Plugin`] and names its
//! type once with [`export_plugin!`]:
//!
//! ```
//! use openac_plugin::{Event, Host, Plugin, StartInfo};
//!
//! #[derive(Default)]
//! struct Hello;
//!
//! impl Plugin for Hello {
//!     fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
//!         host.register_command("hello", "Say hello")?;
//!         Ok(())
//!     }
//!
//!     fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
//!         if let Event::Command(call) = event {
//!             host.chat_post(&format!("Hello, {}!", call.arguments));
//!         }
//!     }
//! }
//!
//! openac_plugin::export_plugin!(Hello);
//! ```
//!
//! Everything the client offers goes through [`Host`]. The same code builds
//! natively, where [`testing::FakeHost`] stands in for the client so plugin
//! logic runs under `cargo test`.

pub mod chat_coordinates;
pub mod chat_markup;
mod game;
mod game_chat;
mod game_client;
mod game_items;
mod game_navigation;
mod game_net;
mod game_peers;
mod game_social;
mod game_storage;
mod game_trade;
mod host;
mod plugin_services;
#[cfg(target_arch = "wasm32")]
mod sys;
#[cfg(target_arch = "wasm32")]
mod sys_ui;
#[cfg(not(target_arch = "wasm32"))]
pub mod testing;
pub mod ui;

#[doc(hidden)]
pub mod __export;

pub use ac_plugin_abi::callback::{Callback, CallbackAnswer, InterceptDecision};
pub use ac_plugin_abi::{
    API_CURRENT, ApiVersion, Call, CharacterInfo, ChatLine, ChatLinkClicked, ChatPost, CommandCall,
    CommandCompletion, CommandDef, Event, Import, LogLevel, StartInfo, TICK_SECONDS, calls,
    character, combat, host as client, items, keys, magic, navigation, net, peers, projectile,
    services, social, storage, trade, world,
};
pub use game::KnownSpellCache;
pub use game_navigation::WalkPause;
pub use game_storage::StorageLock;
pub use host::Host;
#[cfg(not(target_arch = "wasm32"))]
pub use host::Transport;

/// A client function a plugin can call through [`Host::call`]: every marker
/// type in [`calls`].
pub trait Callable: Call {
    /// Calls the import with the encoded request; the reply's length.
    #[cfg(target_arch = "wasm32")]
    #[doc(hidden)]
    fn raw(request: &[u8]) -> i32;
}

#[cfg(not(target_arch = "wasm32"))]
impl<C: Call> Callable for C {}

/// A plugin: constructed with [`Default`] when the client loads it, then
/// enabled; ticked every 15 ms and handed events until it is disabled.
///
/// The client never calls a plugin re-entrantly: a call that would run
/// plugin code again (a [`Host::chat_submit`] of a plugin verb, say) runs
/// after the current call returns.
pub trait Plugin: Default + 'static {
    /// Start: register verbs and read settings here. An error refuses the
    /// load; the client then releases everything the plugin registered.
    ///
    /// # Errors
    /// Why the plugin cannot start, shown to the player.
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String>;

    /// Stop. The client releases every registration afterwards anyway.
    fn disable(&mut self, host: &mut Host<'_>) {
        let _ = host;
    }

    /// One tick: every 15 ms of client time, exactly [`TICK_SECONDS`] each,
    /// with or without a window.
    fn tick(&mut self, host: &mut Host<'_>) {
        let _ = host;
    }

    /// Something happened.
    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        let _ = (host, event);
    }

    /// The client asks something the plugin registered to answer (a chat
    /// filter or input interceptor, a loot classifier, a service). Answer
    /// [`CallbackAnswer::Nothing`] for anything not registered.
    fn callback(&mut self, host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        let _ = (host, callback);
        CallbackAnswer::Nothing
    }
}

/// Writes the wasm exports for a [`Plugin`] type. Use it once, at the
/// crate root of a `cdylib`; it expands to nothing on native targets.
///
/// It also writes the API version and the record layouts this SDK was
/// built with ([`ac_plugin_abi::layout`]) into the `.wasm`. A client whose
/// layouts differ for anything the plugin uses refuses it at load ("built
/// against a different plugin API layout"): rebuild the plugin against
/// that client's SDK revision.
#[macro_export]
macro_rules! export_plugin {
    ($plugin:ty) => {
        #[cfg(target_arch = "wasm32")]
        const _: () = {
            ::std::thread_local! {
                static PLUGIN: ::core::cell::RefCell<::core::option::Option<$plugin>> =
                    const { ::core::cell::RefCell::new(::core::option::Option::None) };
            }

            // The API version this plugin was built for, where the client
            // reads it before loading anything.
            #[unsafe(link_section = "openac.api")]
            #[used]
            static OPENAC_API: [u8; $crate::__export::API_SECTION_LEN] =
                $crate::__export::API_SECTION_BYTES;

            // The layout of the records this plugin was built with: the
            // client refuses the plugin when its own layout differs.
            #[unsafe(link_section = "openac.layout")]
            #[used]
            static OPENAC_LAYOUT: [u8; $crate::__export::LAYOUT_SECTION_LEN] =
                $crate::__export::LAYOUT_SECTION_BYTES;

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_alloc(len: i32) -> i32 {
                $crate::__export::alloc(len)
            }

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_enable(ptr: i32, len: i32) -> i64 {
                PLUGIN.with(|slot| $crate::__export::enable::<$plugin>(slot, ptr, len))
            }

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_disable() {
                PLUGIN.with(|slot| $crate::__export::disable::<$plugin>(slot));
            }

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_tick() {
                PLUGIN.with(|slot| $crate::__export::tick::<$plugin>(slot));
            }

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_event(ptr: i32, len: i32) {
                PLUGIN.with(|slot| $crate::__export::event::<$plugin>(slot, ptr, len));
            }

            #[unsafe(no_mangle)]
            pub extern "C" fn openac_callback(ptr: i32, len: i32) -> i64 {
                PLUGIN.with(|slot| $crate::__export::callback::<$plugin>(slot, ptr, len))
            }
        };
    };
}
