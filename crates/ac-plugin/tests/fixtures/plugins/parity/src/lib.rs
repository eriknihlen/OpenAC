//! `parity`: the host-parity harness's plugin. The harness runs
//! one script against a session with a window attached and against one
//! without, through this plugin, and compares what each answered.
//!
//! - `/<verb> call <seq> <import> <hex>` calls the client function named
//!   `<import>` with the encoded request `<hex>` and logs
//!   `parity reply <seq> <hex>` (the encoded reply), or
//!   `parity refused <seq> <why>` when the request does not decode.
//! - Every event it hears is logged as `parity event <hex>` (the encoded
//!   event); its own verb is not an event, it is a call.
//! - Every callback is logged as `parity callback <hex>` and answered by
//!   the rule set for it: `/<verb> rule <tag> <handle> <mode> [<hex>...]`
//!   (`<tag>` the callback's tag, `<handle>` the filter, interceptor or
//!   classifier it is for). Modes: `pass`, `replace <from> <to>`,
//!   `suppress-prefix <prefix>`, `suppress-all`, `append <suffix>`,
//!   `drop-containing <text>`, `fixed <answer>`, `trap`; texts and answers
//!   hex-encoded. A typed line of its own verb is passed without a word.
//! - `/<verb> trap` panics (the client unloads it), and
//!   `/<verb> trap-on <event tag>` makes it panic on the next event of
//!   that kind.
//! - `/<verb> react <event tag> <import> <hex>` makes the call whenever an
//!   event of that kind arrives, inside the event, and logs
//!   `parity reaction <event tag> <hex>` (what a plugin reads at that
//!   moment); `/<verb> unreact <event tag>` stops it.
//! - Its first tick after each arrival (`LoginComplete`) is logged as
//!   `parity tick after arrival`.
//!
//! Its verb is its id after `openac.`, letters and digits only, so the
//! harness can install it several times under other ids.

use std::collections::BTreeMap;

use ac_plugin_abi::{Call, Wire};
use openac_plugin::{
    Callback, CallbackAnswer, Event, Host, InterceptDecision, Plugin, StartInfo, calls,
};

/// What a callback is answered with.
enum Rule {
    Pass,
    Replace(String, String),
    SuppressPrefix(String),
    SuppressAll,
    Append(String),
    DropContaining(String),
    Fixed(Vec<u8>),
    Trap,
}

#[derive(Default)]
pub struct Parity {
    verb: String,
    /// By callback tag and handle.
    rules: BTreeMap<(u32, u32), Rule>,
    /// Panic on the next event with this tag.
    trap_on: Option<u32>,
    /// Calls to make inside events, by event tag: (import, request).
    reactions: BTreeMap<u32, Vec<(String, Vec<u8>)>>,
    /// An arrival was heard and no tick has come since.
    arrived: bool,
}

macro_rules! relay {
    ($(
        $(#[$doc:meta])*
        $variant:ident = $name:ident ($request:ty) -> $reply:ty $(= $default:expr)?,
            permission: $perm:expr, window: $window:expr, since: ($maj:literal, $min:literal);
    )*) => {
        /// Calls the client function `name` with the encoded `request`; the
        /// encoded reply.
        fn relay(host: &mut Host<'_>, name: &str, request: &[u8]) -> Result<Vec<u8>, String> {
            // The plumbing is the SDK's own.
            if name == "abi_reply_read" || name == "abi_panic" {
                return Err(format!("{name} is plumbing"));
            }
            $(
                if name == stringify!($name) {
                    let request = <<calls::$variant as Call>::Request as Wire>::from_bytes(request)
                        .map_err(|e| e.to_string())?;
                    return Ok(host.call::<calls::$variant>(&request).to_bytes());
                }
            )*
            Err(format!("no client function {name}"))
        }
    };
}

ac_plugin_abi::import_table!(relay);

const HEX: &[u8; 16] = b"0123456789abcdef";

fn hex(bytes: &[u8]) -> String {
    let mut out = Vec::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(HEX[usize::from(byte >> 4)]);
        out.push(HEX[usize::from(byte & 0x0F)]);
    }
    String::from_utf8(out).unwrap_or_default()
}

fn unhex(text: &str) -> Result<Vec<u8>, String> {
    fn nibble(c: u8) -> Result<u8, String> {
        match c {
            b'0'..=b'9' => Ok(c - b'0'),
            b'a'..=b'f' => Ok(c - b'a' + 10),
            b'A'..=b'F' => Ok(c - b'A' + 10),
            _ => Err(format!("not hex: {}", c as char)),
        }
    }
    let bytes = text.as_bytes();
    if bytes.len() % 2 != 0 {
        return Err("odd hex".to_owned());
    }
    bytes
        .chunks(2)
        .map(|pair| Ok((nibble(pair[0])? << 4) | nibble(pair[1])?))
        .collect()
}

fn unhex_text(text: Option<&str>) -> Result<String, String> {
    String::from_utf8(unhex(text.unwrap_or_default())?).map_err(|e| e.to_string())
}

impl Parity {
    fn command(&mut self, host: &mut Host<'_>, arguments: &str) {
        let mut words = arguments.split_whitespace();
        match words.next() {
            Some("call") => {
                let seq = words.next().unwrap_or("0").to_owned();
                let name = words.next().unwrap_or_default().to_owned();
                let request = words.next().unwrap_or_default();
                let reply = unhex(request).and_then(|request| relay(host, &name, &request));
                match reply {
                    Ok(reply) => host.info(&format!("parity reply {seq} {}", hex(&reply))),
                    Err(why) => host.info(&format!("parity refused {seq} {why}")),
                }
            }
            Some("rule") => {
                let tag = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                let handle = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                let mode = words.next().unwrap_or_default();
                let rule = match mode {
                    "pass" => Ok(Rule::Pass),
                    "replace" => unhex_text(words.next()).and_then(|from| {
                        unhex_text(words.next()).map(|to| Rule::Replace(from, to))
                    }),
                    "suppress-prefix" => unhex_text(words.next()).map(Rule::SuppressPrefix),
                    "suppress-all" => Ok(Rule::SuppressAll),
                    "append" => unhex_text(words.next()).map(Rule::Append),
                    "drop-containing" => unhex_text(words.next()).map(Rule::DropContaining),
                    "fixed" => unhex(words.next().unwrap_or_default()).map(Rule::Fixed),
                    "trap" => Ok(Rule::Trap),
                    other => Err(format!("no rule mode {other}")),
                };
                match rule {
                    Ok(rule) => {
                        self.rules.insert((tag, handle), rule);
                        host.info(&format!("parity rule {tag} {handle} {mode}"));
                    }
                    Err(why) => host.info(&format!("parity rule refused {why}")),
                }
            }
            Some("unrule") => {
                let tag = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                let handle = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                self.rules.remove(&(tag, handle));
                host.info(&format!("parity unrule {tag} {handle}"));
            }
            Some("react") => {
                let tag = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                let name = words.next().unwrap_or_default().to_owned();
                match unhex(words.next().unwrap_or_default()) {
                    Ok(request) => {
                        self.reactions.entry(tag).or_default().push((name, request));
                        host.info(&format!("parity react {tag}"));
                    }
                    Err(why) => host.info(&format!("parity react refused {why}")),
                }
            }
            Some("unreact") => {
                let tag = words.next().and_then(|t| t.parse().ok()).unwrap_or(0);
                self.reactions.remove(&tag);
                host.info(&format!("parity unreact {tag}"));
            }
            Some("trap") => panic!("parity was told to trap"),
            Some("trap-on") => {
                self.trap_on = words.next().and_then(|t| t.parse().ok());
                host.info(&format!("parity trap-on {:?}", self.trap_on));
            }
            other => host.info(&format!("parity unknown {other:?}")),
        }
    }

    fn answer(&self, tag: u32, handle: u32, callback: &Callback) -> CallbackAnswer {
        let Some(rule) = self.rules.get(&(tag, handle)) else {
            return CallbackAnswer::Nothing;
        };
        let line = match callback {
            Callback::ChatIntercept((_, line)) => line.as_str(),
            Callback::ChatFilter((_, line)) => line.text.as_str(),
            _ => "",
        };
        let intercept = |decision| CallbackAnswer::Intercept(decision);
        match rule {
            Rule::Pass => intercept(InterceptDecision::Pass),
            Rule::Replace(from, to) if line.contains(from.as_str()) => {
                intercept(InterceptDecision::Rewrite(line.replace(from.as_str(), to)))
            }
            Rule::Replace(..) => intercept(InterceptDecision::Pass),
            Rule::SuppressPrefix(prefix) if line.starts_with(prefix.as_str()) => {
                intercept(InterceptDecision::Suppress)
            }
            Rule::SuppressPrefix(_) => intercept(InterceptDecision::Pass),
            Rule::SuppressAll => intercept(InterceptDecision::Suppress),
            Rule::Append(suffix) => intercept(InterceptDecision::Rewrite(format!("{line}{suffix}"))),
            Rule::DropContaining(text) => CallbackAnswer::Bool(line.contains(text.as_str())),
            Rule::Fixed(answer) => CallbackAnswer::from_bytes(answer).unwrap_or_default(),
            Rule::Trap => panic!("parity's callback rule traps"),
        }
    }
}

fn event_tag(bytes: &[u8]) -> Option<u32> {
    bytes
        .get(..4)
        .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
}

impl Plugin for Parity {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        let name = info.plugin_id.strip_prefix("openac.").unwrap_or(&info.plugin_id);
        self.verb = name.chars().filter(char::is_ascii_alphanumeric).collect();
        host.register_command(&self.verb, "The parity harness's relay")?;
        host.info(&format!("parity enable {}", info.plugin_id));
        Ok(())
    }

    fn tick(&mut self, host: &mut Host<'_>) {
        if std::mem::take(&mut self.arrived) {
            host.info("parity tick after arrival");
        }
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        if let Event::Command(call) = &event
            && call.verb == self.verb
        {
            let arguments = call.arguments.clone();
            self.command(host, &arguments);
            return;
        }
        let bytes = event.to_bytes();
        let tag = event_tag(&bytes);
        if self.trap_on.is_some() && self.trap_on == tag {
            panic!("parity traps on this event");
        }
        if matches!(event, Event::LoginComplete) {
            self.arrived = true;
        }
        host.info(&format!("parity event {}", hex(&bytes)));
        if let Some(reactions) = tag.and_then(|t| self.reactions.get(&t)) {
            let tag = tag.unwrap_or_default();
            for (name, request) in reactions.clone() {
                match relay(host, &name, &request) {
                    Ok(reply) => host.info(&format!("parity reaction {tag} {}", hex(&reply))),
                    Err(why) => host.info(&format!("parity reaction refused {tag} {why}")),
                }
            }
        }
    }

    fn callback(&mut self, host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        let (tag, handle) = match &callback {
            Callback::ChatFilter((handle, _)) => (0, *handle),
            Callback::ChatIntercept((handle, line)) => {
                // The harness's own calls go by untouched and unrecorded.
                let own = format!("/{} ", self.verb);
                if line.starts_with(&own) {
                    return CallbackAnswer::Intercept(InterceptDecision::Pass);
                }
                (1, *handle)
            }
            Callback::LootClassify(question) => (2, question.handle),
            Callback::LootNeedsIdentification(question) => (3, question.handle),
            Callback::LootLooted((handle, _)) => (4, *handle),
            Callback::LootRemoved((handle, _)) => (5, *handle),
            Callback::CommandComplete(_) => (6, 0),
            Callback::ServiceRequest(request) => (7, request.handle),
            // A kind a later API version adds.
            _ => return CallbackAnswer::Nothing,
        };
        host.info(&format!("parity callback {}", hex(&callback.to_bytes())));
        self.answer(tag, handle, &callback)
    }
}

openac_plugin::export_plugin!(Parity);
