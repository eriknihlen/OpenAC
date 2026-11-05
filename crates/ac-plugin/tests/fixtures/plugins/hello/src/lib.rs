//! `hello`: the smallest real plugin.
//!
//! - `/hello` posts a greeting to the transcript with the character's name
//!   and whether a window is attached.
//! - `/hello say <text>` says the text aloud (a chat-box submit).
//! - `/hello ticks` posts how many 15 ms ticks it has had.
//! - A spoken chat line containing `ping hello` is answered with a posted
//!   line.
//! - The setting `greeting` replaces "Hello".
//! - `/hello classify <scoped id>` asks another plugin's loot classifier
//!   and posts its verdict.
//! - `/hello store <text>` writes the text to its storage, reads it back
//!   and lists its keys; it also tries the profiles root, which it may not.
//! - `/hello intercept` installs an input interceptor: a sent line with
//!   `secret` in it is dropped, `shout <text>` becomes `<TEXT>`.
//! - `/hello filter <word>` installs a chat filter: a line (or status
//!   notice) with the word in it never reaches the transcript;
//!   `/hello unfilter` removes it.
//! - Asked to complete `/hello <text>`, it offers its sub-commands that
//!   start with the text.

use openac_plugin::items::LootContext;
use openac_plugin::storage::{StoragePlace, StorageRoot};
use openac_plugin::{
    Callback, CallbackAnswer, CommandCompletion, Event, Host, InterceptDecision, Plugin, StartInfo,
};

/// Its sub-commands, offered as completions of `/hello`.
const SUBCOMMANDS: [(&str, Option<&str>); 7] = [
    ("classify", Some("ask another plugin's loot classifier")),
    ("filter", None),
    ("intercept", None),
    ("say", Some("say text aloud")),
    ("store", None),
    ("ticks", None),
    ("unfilter", None),
];

#[derive(Default)]
pub struct Hello {
    greeting: String,
    ticks: u64,
    heard: u64,
    interceptor: u32,
    filter: u32,
    filtered: String,
}

impl Plugin for Hello {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        self.greeting = host
            .setting("greeting")
            .unwrap_or_else(|| "Hello".to_owned());
        host.register_command("hello", "Greets you (hello say <text>, hello ticks)")?;
        let window = host.window_attached();
        host.info(&format!(
            "hello {} enabled on API {} (client {}), window attached: {window}",
            info.plugin_id, info.api_version, info.client_version,
        ));
        Ok(())
    }

    fn disable(&mut self, host: &mut Host<'_>) {
        host.info(&format!("hello disabled after {} ticks", self.ticks));
    }

    fn tick(&mut self, _host: &mut Host<'_>) {
        self.ticks += 1;
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        match event {
            Event::Command(call) => {
                let (sub, rest) = match call.arguments.split_once(' ') {
                    Some((sub, rest)) => (sub, rest.trim()),
                    None => (call.arguments.as_str(), ""),
                };
                match sub {
                    "say" if !rest.is_empty() => host.chat_submit(rest),
                    "ticks" => host.chat_post(&format!("hello: {} ticks", self.ticks)),
                    "store" => {
                        let place = StoragePlace::character(StorageRoot::PluginData, "+Horan");
                        let written = host.storage_write_text(&place, "notes/hello.txt", rest);
                        let read = host.storage_read_text(&place, "notes/hello.txt");
                        let keys = host.storage_list(&place, "");
                        let profiles =
                            host.storage_list(&StoragePlace::global(StorageRoot::Profiles), "");
                        host.chat_post(&format!(
                            "hello store: {written:?} {read:?} {keys:?} {}",
                            profiles.is_err()
                        ));
                    }
                    "filter" if !rest.is_empty() => {
                        self.filtered = rest.to_owned();
                        if self.filter == 0 {
                            self.filter = host.chat_add_filter();
                        }
                        host.chat_post("hello filter installed");
                    }
                    "unfilter" => {
                        let removed = host.chat_remove_filter(self.filter);
                        self.filter = 0;
                        host.chat_post(&format!("hello unfiltered: {removed}"));
                    }
                    "intercept" => match host.chat_add_interceptor() {
                        Ok(handle) => {
                            self.interceptor = handle;
                            host.chat_post("hello intercepts");
                        }
                        Err(why) => host.chat_post(&format!("hello cannot intercept: {why}")),
                    },
                    // Asks another plugin's loot classifier (the shared
                    // registry) and posts its verdict.
                    "classify" if !rest.is_empty() => {
                        let verdict = host.classify(rest, "", LootContext::default());
                        let text = verdict.map_or_else(
                            || "none".to_owned(),
                            |v| format!("{:?} by {}", v.action, v.rule_name),
                        );
                        host.chat_post(&format!("hello classify {rest}: {text}"));
                    }
                    _ => {
                        let name = host
                            .character()
                            .map_or_else(|| "stranger".to_owned(), |c| c.name);
                        let window = if host.window_attached() {
                            "a window is attached"
                        } else {
                            "no window is attached"
                        };
                        host.chat_post(&format!("{}, {name}! ({window})", self.greeting));
                    }
                }
            }
            Event::ChatReceived(line) => {
                // Only speech (a line with a speaker): the plugin's own
                // posted answer has none, so it never answers itself.
                if !line.sender.is_empty() && line.text.contains("ping hello") {
                    self.heard += 1;
                    host.chat_post(&format!("hello heard #{}: {}", self.heard, line.display_text));
                }
            }
            Event::WindowAttached(attached) => {
                host.info(&format!("hello: window attached: {attached}"));
            }
            // A status notice (never a chat line): answered in chat, in
            // order with the lines it hears.
            Event::StatusTextReceived(text) => {
                host.chat_post(&format!("hello status: {text}"));
            }
            _ => {}
        }
    }

    fn callback(&mut self, _host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        let (handle, line) = match callback {
            Callback::ChatFilter((handle, line)) if handle == self.filter && handle != 0 => {
                return CallbackAnswer::Bool(line.text.contains(&self.filtered));
            }
            // Its sub-commands that start with what was typed.
            Callback::CommandComplete(call) => {
                let typed = call.arguments.to_lowercase();
                return CallbackAnswer::Completions(
                    SUBCOMMANDS
                        .iter()
                        .filter(|(name, _)| name.starts_with(&typed))
                        .map(|(name, description)| CommandCompletion {
                            text: (*name).to_owned(),
                            description: description.map(str::to_owned),
                        })
                        .collect(),
                );
            }
            Callback::ChatIntercept(asked) => asked,
            _ => return CallbackAnswer::Nothing,
        };
        if handle != self.interceptor {
            return CallbackAnswer::Nothing;
        }
        let decision = if line.contains("secret") {
            InterceptDecision::Suppress
        } else if let Some(rest) = line.strip_prefix("shout ") {
            InterceptDecision::Rewrite(rest.to_uppercase())
        } else {
            InterceptDecision::Pass
        };
        CallbackAnswer::Intercept(decision)
    }
}

openac_plugin::export_plugin!(Hello);
