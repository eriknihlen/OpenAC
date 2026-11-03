//! `suite`: one of a suite of plugins that ask each other things. A test
//! installs it twice under two ids, one to answer and one to ask, so the
//! host's plugin-to-plugin calls (and whose time an answer is) can be
//! tested. It works by the client's clock, so the session must answer it, and it
//! needs no permission.
//!
//! - It registers the loot classifier `rules`; asked with the profile
//!   `work:<ms>` it works that long before answering Keep, with `loop` it
//!   never answers (stopped at the callback's deadline), with anything
//!   else it answers Keep at once.
//! - Its verb is the last part of its id (`/suite` for `openac.suite`).
//!   `/<verb> ask <scoped id> <profile> [<ms>]` works `<ms>` (0 when left
//!   out), asks the classifier with the profile, works `<ms>` again, then
//!   logs `<verb> ask: <verdict>`.

use openac_plugin::items::{LootAction, LootClassification, LootContext};
use openac_plugin::{Callback, CallbackAnswer, Event, Host, Plugin, StartInfo};

#[derive(Default)]
pub struct Suite {
    verb: String,
    classifier: u32,
}

impl Plugin for Suite {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        let id = info.plugin_id.as_str();
        self.verb = id.rsplit('.').next().unwrap_or(id).to_owned();
        host.register_command(&self.verb, "Asks the other plugins of the suite")?;
        self.classifier = host.register_loot_classifier("rules", "Suite Rules")?;
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        let Event::Command(call) = event else {
            return;
        };
        let words: Vec<&str> = call.arguments.split_whitespace().collect();
        match words.as_slice() {
            ["ask", id, profile, rest @ ..] => {
                let around = rest.first().and_then(|ms| ms.parse().ok()).unwrap_or(0);
                work(host, around);
                let verdict = host.classify(id, profile, LootContext::default());
                work(host, around);
                let text = verdict.map_or_else(|| "none".to_owned(), |v| format!("{:?}", v.action));
                host.info(&format!("{} ask: {text}", self.verb));
            }
            _ => host.info(&format!(
                "{}: unknown order '{}'",
                self.verb, call.arguments
            )),
        }
    }

    fn callback(&mut self, host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        match callback {
            Callback::LootClassify(question) if question.handle == self.classifier => {
                match question.profile.split_once(':') {
                    Some(("work", ms)) => work(host, ms.parse().unwrap_or(0)),
                    _ if question.profile == "loop" => spin(u64::MAX),
                    _ => {}
                }
                CallbackAnswer::Classification(Some(LootClassification {
                    matched: true,
                    action: LootAction::Keep,
                    rule_name: "suite".to_owned(),
                    priority: 0,
                    keep_count: 0,
                }))
            }
            _ => CallbackAnswer::Nothing,
        }
    }
}

/// Works until the client's clock says `ms` milliseconds have passed.
fn work(host: &mut Host<'_>, ms: i64) {
    let start = host.clock().utc_unix_100ns;
    while host.clock().utc_unix_100ns - start < ms * 10_000 {
        spin(100_000);
    }
}

/// Turns `rounds` times (`u64::MAX`: until it is stopped).
fn spin(rounds: u64) {
    let mut spins: u64 = 0;
    while spins < rounds {
        spins = std::hint::black_box(spins.wrapping_add(1));
    }
}

openac_plugin::export_plugin!(Suite);
