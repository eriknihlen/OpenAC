//! What the plugin host needs from the session it lives in, and what it
//! tells the session's host.
//!
//! The runtime implements [`SessionApi`] once, over its own state, on the
//! session's thread: every plugin call that reads or changes the game has
//! that one implementation, with or without a window.

use ac_plugin_abi::{ApiVersion, CharacterInfo, Event, Import, LogLevel};

/// The plugin making a call, as the session sees it.
#[derive(Debug, Clone, Copy)]
pub struct Caller<'a> {
    /// Its id (from its manifest).
    pub plugin_id: &'a str,
    /// The API version it was built for (from its manifest): its requests
    /// are read, and its replies written, as that version knows them.
    pub api_version: ApiVersion,
}

/// The session, as the plugin host reaches it during a plugin call.
pub trait SessionApi {
    /// A log line from (or about) a plugin.
    fn log(&mut self, plugin_id: &str, level: LogLevel, message: &str);

    /// Whether a window is attached to the session.
    fn window_attached(&self) -> bool;

    /// Show a line in the chat transcript, locally (it goes wherever the
    /// client's own text goes: the transcript, the chat log, attached
    /// windows, other plugins).
    fn post_chat(&mut self, text: &str, log_text_type: u32);

    /// The character in the world, if one is.
    fn character(&self) -> Option<CharacterInfo>;

    /// Every client function about the game (all but the plugin host's own
    /// services): the encoded request in, the encoded reply out. The
    /// caller's permission for `import` is already checked.
    ///
    /// A session that does not implement a function answers its inert
    /// reply (nothing found, unavailable).
    ///
    /// # Errors
    /// The request does not decode: the plugin broke the contract and is
    /// stopped.
    fn call(
        &mut self,
        caller: Caller<'_>,
        import: Import,
        request: &[u8],
    ) -> Result<Vec<u8>, String> {
        let _ = request;
        Ok(import.default_reply_at(caller.api_version))
    }

    /// Plugin `plugin_id` was unloaded (stopped, faulted, refused to start
    /// or reloaded): the session lets go of everything it held for it
    /// (combat control, walks and their pauses, its world labels).
    fn released(&mut self, plugin_id: &str) {
        let _ = plugin_id;
    }

    /// Whether the plugins are in a stay in the world (they heard
    /// `LoginComplete` and not yet `Logoff`): a plugin enabled during one
    /// hears the login it missed. The default is no stay.
    fn in_stay(&self) -> bool {
        false
    }

    /// The session's plugin UI (panels, canvases, images, labels, lines),
    /// which the session owns; `None` in a session without one.
    fn plugin_ui(&mut self) -> Option<&mut ac_plugin_ui::PluginUi> {
        None
    }

    /// Every object placed in the world now, as a plugin enabled now hears
    /// them replayed (OpenAC `RuntimeWorldEntityProjection.Subscribe`).
    fn entities(&self) -> Vec<ac_plugin_abi::world::EntitySnapshot> {
        Vec::new()
    }

    /// A spell's icon render surface, if the client knows the spell.
    fn spell_icon(&self, _spell: u32) -> Option<u32> {
        None
    }

    /// A live object's icon, if the client knows the object and it has one.
    fn object_icon(&self, _object: u32) -> Option<u32> {
        None
    }

    /// Right after a plugin call returns, with no plugin running: carries
    /// out the chat-box lines the plugins submitted (`chat_submit`), in
    /// order, as typed lines. What it cannot carry out now (no connection)
    /// stays in `submits` for the session to route later
    /// ([`crate::PluginSession::take_submits`]). The default carries out
    /// none.
    fn submit_now(&mut self, submits: &mut Vec<String>) {
        let _ = submits;
    }

    /// The chat lines ([`Event::ChatReceived`]) and status notices
    /// ([`Event::StatusTextReceived`]) that appeared in the session since
    /// this was last asked (a plugin's own posts, the local lines its
    /// submits wrote, the lines the chat filters kept), each once, in the
    /// order they appeared: the plugins hear them right after the call
    /// that caused them, before anything else, and the session does not
    /// hand them out again. The default has none.
    fn new_chat_events(&mut self) -> Vec<Event> {
        Vec::new()
    }
}

/// A plugin was loaded or failed (the host passes these to the status file,
/// `pluginLoaded` and `pluginFailed`, and the console), or a call into it
/// was over the slow log's threshold.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginReport {
    /// It loaded and enabled.
    Loaded {
        /// Its id.
        id: String,
    },
    /// It could not load, refused to start, or stopped with a fault (and
    /// was unloaded).
    Failed {
        /// Its id (or its folder's name when its manifest is unreadable).
        id: String,
        /// Why.
        error: String,
    },
    /// One call into it took longer than the slow log's threshold
    /// ([`crate::PluginSessionOptions::slow_log`], a diagnostic): which
    /// call, how long, how much of it was the client's own work, and the
    /// client functions it called. Hosts write it to the client log.
    SlowCall {
        /// Its id.
        id: String,
        /// `event object created took 23.4 ms (client 9.1 ms, most in
        /// world_object ×412 (9.0 ms)); imports: world_object ×412`.
        message: String,
    },
}
