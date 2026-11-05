//! [`Host`]: every client function a plugin can call.
//!
//! [`Host::call`] reaches any client function by its marker type
//! (`host.call::<calls::ItemsUse>(&id)`); the typed methods here and in the
//! group modules wrap the common ones. On wasm each call goes to its own
//! import, so a plugin's module imports only the functions it actually uses
//! (and needs only the permissions behind those). Natively the same
//! requests and replies go through a [`Transport`], normally
//! [`crate::testing::FakeHost`].

use ac_plugin_abi::host::{Clock, ProcessStats};
use ac_plugin_abi::{
    CharacterInfo, ChatPost, CommandDef, Import, LogLevel, LogRequest, Wire, calls,
};

use crate::Callable;

/// Where a native build's calls go: the encoded request for an import in,
/// the encoded reply out, exactly as the client handles them.
#[cfg(not(target_arch = "wasm32"))]
pub trait Transport {
    /// One call.
    fn call(&mut self, import: Import, request: &[u8]) -> Vec<u8>;
}

/// The client, as a plugin sees it. Handed to every [`crate::Plugin`] call.
pub struct Host<'a> {
    #[cfg(not(target_arch = "wasm32"))]
    pub(crate) transport: &'a mut dyn Transport,
    #[cfg(target_arch = "wasm32")]
    _call: core::marker::PhantomData<&'a mut ()>,
}

impl std::fmt::Debug for Host<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Host")
    }
}

/// A reply the client sent that does not decode: the contract is broken, so
/// the plugin stops (a panic is a trap, and the client reports it).
pub(crate) fn decode<T: Wire>(import: Import, reply: &[u8]) -> T {
    match T::from_bytes(reply) {
        Ok(value) => value,
        Err(error) => panic!(
            "the client's reply to {} is malformed: {error}",
            import.name()
        ),
    }
}

impl<'a> Host<'a> {
    /// The host a wasm export hands the plugin.
    #[cfg(target_arch = "wasm32")]
    #[doc(hidden)]
    #[must_use]
    pub fn wasm() -> Self {
        Self {
            _call: core::marker::PhantomData,
        }
    }

    /// A host whose calls go to `transport` (native builds: tests).
    #[cfg(not(target_arch = "wasm32"))]
    #[must_use]
    pub fn with_transport(transport: &'a mut dyn Transport) -> Self {
        Self { transport }
    }

    /// Calls the client function `C` with `request` and returns its reply.
    pub fn call<C: Callable>(&mut self, request: &C::Request) -> C::Reply {
        let bytes = request.to_bytes();
        #[cfg(target_arch = "wasm32")]
        let reply = crate::sys::read_reply(C::raw(&bytes));
        #[cfg(not(target_arch = "wasm32"))]
        let reply = self.transport.call(C::IMPORT, &bytes);
        decode(C::IMPORT, &reply)
    }

    /// Write a line to the session's log.
    pub fn log(&mut self, level: LogLevel, message: &str) {
        self.call::<calls::HostLog>(&LogRequest {
            level,
            message: message.to_owned(),
        });
    }

    /// [`Self::log`] at [`LogLevel::Info`].
    pub fn info(&mut self, message: &str) {
        self.log(LogLevel::Info, message);
    }

    /// [`Self::log`] at [`LogLevel::Warn`].
    pub fn warn(&mut self, message: &str) {
        self.log(LogLevel::Warn, message);
    }

    /// [`Self::log`] at [`LogLevel::Error`].
    pub fn error(&mut self, message: &str) {
        self.log(LogLevel::Error, message);
    }

    /// Whether a window is attached to the session right now. Everything
    /// else a plugin can do behaves the same either way; the
    /// [`crate::Event::WindowAttached`] event says when this changes.
    #[must_use]
    pub fn window_attached(&mut self) -> bool {
        self.call::<calls::HostWindowAttached>(&())
    }

    /// One of this plugin's settings for this session (the session
    /// document's `pluginSettings`), or `None`.
    #[must_use]
    pub fn setting(&mut self, key: &str) -> Option<String> {
        self.call::<calls::HostSetting>(&key.to_owned())
    }

    /// All of this plugin's settings for this session, by key.
    #[must_use]
    pub fn settings(&mut self) -> Vec<(String, String)> {
        self.call::<calls::HostSettings>(&())
    }

    /// The clocks: wall time, the local offset from UTC, a monotonic clock
    /// and a fresh random seed.
    #[must_use]
    pub fn clock(&mut self) -> Clock {
        self.call::<calls::HostClock>(&())
    }

    /// Log the character off and end this session, gracefully (after the
    /// current call returns). False when there is no session to end.
    pub fn request_close(&mut self) -> bool {
        self.call::<calls::HostRequestClose>(&())
    }

    /// The client process's own numbers, what a C# plugin reads from
    /// `Process.GetCurrentProcess()`: its working set in bytes, its open
    /// handles, and its CPU use since this plugin's previous call, percent
    /// of all cores (0 on the first call, which sets the baseline). Zeros
    /// where the client's platform does not give them.
    #[must_use]
    pub fn process_stats(&mut self) -> ProcessStats {
        self.call::<calls::HostProcessStats>(&())
    }

    /// Register a chat verb (`/verb`), delivered as
    /// [`crate::Event::Command`]. The client's own commands always win.
    ///
    /// # Errors
    /// The verb is malformed or already taken.
    pub fn register_command(&mut self, verb: &str, description: &str) -> Result<(), String> {
        self.register_command_def(&CommandDef {
            verb: verb.to_owned(),
            aliases: Vec::new(),
            description: description.to_owned(),
        })
    }

    /// Register a chat verb with aliases.
    ///
    /// # Errors
    /// A name is malformed or already taken.
    pub fn register_command_def(&mut self, definition: &CommandDef) -> Result<(), String> {
        self.call::<calls::CommandsRegister>(definition)
    }

    /// Remove one of this plugin's verbs; false when it had none by that
    /// name.
    pub fn unregister_command(&mut self, verb: &str) -> bool {
        self.call::<calls::CommandsUnregister>(&verb.to_owned())
    }

    /// Every plugin's verbs as registered (verb, aliases, description), in
    /// registration order (OpenAC `IPluginCommandRegistry.Definitions`).
    pub fn command_definitions(&mut self) -> Vec<CommandDef> {
        self.call::<calls::CommandsDefinitions>(&())
    }

    /// The help list: `/verb - description` for every plugin verb, ordered
    /// by verb ignoring case, one per line (OpenAC `GetHelp`).
    pub fn command_help(&mut self) -> String {
        self.call::<calls::CommandsHelp>(&())
    }

    /// Completions for a partly typed command (`verb` or one of its
    /// aliases, what follows it, and the whole line), best first, from the
    /// plugin owning the verb (OpenAC `CompleteAsync`). A plugin offers
    /// completions for its own verbs by answering
    /// [`crate::Callback::CommandComplete`] with
    /// [`crate::CallbackAnswer::Completions`]; asking about its own verb
    /// answers nothing (it asks its own code directly).
    pub fn complete_command(
        &mut self,
        verb: &str,
        arguments: &str,
        raw: &str,
    ) -> Vec<crate::CommandCompletion> {
        self.call::<calls::CommandsComplete>(&crate::CommandCall {
            verb: verb.to_owned(),
            arguments: arguments.to_owned(),
            raw: raw.to_owned(),
        })
    }

    /// Submit a line as if typed into the chat box (permission `chat`). It
    /// runs after the current plugin call returns.
    pub fn chat_submit(&mut self, line: &str) {
        self.call::<calls::ChatSubmit>(&line.to_owned());
    }

    /// Show a line in the chat transcript, locally (permission `chat`).
    /// A `<COORD:World:42.1N, 33.5E>caption<\COORD>` tag in the line shows
    /// its caption as a link that raises `Event::ChatLinkClicked`, and so
    /// does a coordinate pair written in plain text
    /// ([`crate::chat_coordinates::parse_all`] reads the same pairs). No
    /// other angle-bracket text is a tag; it is shown as written, and a
    /// backslash directly before a `<` is dropped. Escape any text the
    /// plugin did not write itself (a player's words it repeats) with
    /// [`crate::chat_markup::escape`]: a tag in that text would otherwise
    /// become a link the plugin vouches for.
    pub fn chat_post(&mut self, text: &str) {
        self.chat_post_with(text, 0);
    }

    /// Show a line with a text class (retail `LogTextType`); tags are read
    /// as in [`Self::chat_post`], and text the plugin did not write should
    /// be escaped the same way. On the status line (the client-local
    /// class), which has no links, only a tag's caption is shown.
    pub fn chat_post_with(&mut self, text: &str, log_text_type: u32) {
        self.call::<calls::ChatPost>(&ChatPost {
            text: text.to_owned(),
            log_text_type,
        });
    }

    /// The character in the world, or `None` (at character selection, say).
    #[must_use]
    pub fn character(&mut self) -> Option<CharacterInfo> {
        self.call::<calls::CharacterInfo>(&())
    }
}
