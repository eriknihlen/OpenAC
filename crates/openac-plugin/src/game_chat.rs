//! Typed [`Host`] methods for API group K: chat beyond posting and
//! submitting (those are in `host.rs`).

use ac_plugin_abi::{ChatLine, calls};

use crate::Host;

/// K. Chat.
impl Host<'_> {
    /// The retained lines (the latest 512) above `after_sequence`, oldest
    /// first. Needs the `chat` permission.
    pub fn chat_captured(&mut self, after_sequence: u64) -> Vec<ChatLine> {
        self.call::<calls::ChatCaptured>(&after_sequence)
    }

    /// Put text in the chat entry without sending it; false while the
    /// player is typing there. Needs the `chat` permission.
    pub fn chat_compose(&mut self, text: &str) -> bool {
        self.call::<calls::ChatCompose>(&text.to_owned())
    }

    /// The player is typing into the chat entry, so held movement keys go
    /// to text rather than the character.
    pub fn chat_input_active(&mut self) -> bool {
        self.call::<calls::ChatIsInputActive>(&())
    }

    /// Install an input interceptor: the client asks the plugin's
    /// [`crate::Plugin::callback`] with [`crate::Callback::ChatIntercept`]
    /// about every line the player sends (after the client's own commands,
    /// before plugin verbs and the server). At most 16 per plugin. Needs
    /// the `chat` permission.
    ///
    /// # Errors
    /// The plugin already has 16.
    pub fn chat_add_interceptor(&mut self) -> Result<u32, String> {
        self.call::<calls::ChatAddInterceptor>(&())
    }

    /// Remove one of this plugin's interceptors.
    pub fn chat_remove_interceptor(&mut self, handle: u32) -> bool {
        self.call::<calls::ChatRemoveInterceptor>(&handle)
    }

    /// Install a chat filter: before the transcript keeps a line (and
    /// before other plugins or a window see it) the client asks the
    /// plugin's [`crate::Plugin::callback`] with
    /// [`crate::Callback::ChatFilter`]; answering `CallbackAnswer::Bool(true)`
    /// drops it. Filters are asked in the order they were installed, across
    /// every plugin, and the first that drops a line decides. Status notices
    /// come as kind [`ac_plugin_abi::types::STATUS_TEXT_KIND`]; a dropped
    /// notice is still heard as [`crate::Event::StatusTextReceived`]. Needs
    /// the `chat` permission.
    pub fn chat_add_filter(&mut self) -> u32 {
        self.call::<calls::ChatAddFilter>(&())
    }

    /// Remove one of this plugin's filters.
    pub fn chat_remove_filter(&mut self, handle: u32) -> bool {
        self.call::<calls::ChatRemoveFilter>(&handle)
    }
}
