//! `suite`: one of a suite of plugins that ask each other things. A test
//! installs it twice under two ids, one to answer and one to ask, so the
//! host's plugin-to-plugin calls (and whose time an answer is) can be
//! tested. It works by the client's clock, so the session must answer it,
//! and it has the `chat` permission for its filter and interceptor. Its verb is the last part of its id
//! (`/suite` for `openac.suite`); what it learns it logs, `<verb> ...`.
//!
//! Loot classifier:
//! - It registers the loot classifier `rules`; asked with the profile
//!   `work:<ms>` it works that long before answering Keep, with `loop` it
//!   never answers (stopped at the callback's deadline), with anything
//!   else it answers Keep at once.
//! - `/<verb> ask <scoped id> <profile> [<ms>]` works `<ms>` (0 when left
//!   out), asks the classifier with the profile, works `<ms>` again, then
//!   logs `<verb> ask: <verdict>`.
//!
//! Services:
//! - `/<verb> offer <name>` offers a service, `/<verb> offer-long <name>
//!   <bytes>` offers one with a description that long, `/<verb> withdraw
//!   <name>` withdraws it, `/<verb> list` logs the services on offer.
//! - `/<verb> call <scoped id> <request> [<ms>]` works `<ms>`, calls the
//!   service with the request's text, works `<ms>` again, then logs
//!   `<verb> call: Ok(<reply>)` or `Err(<error>)`; `/<verb> call-big <scoped
//!   id>` calls it with a request one byte over the limit.
//! - `/<verb> call-until <scoped id> <request> <most>` calls the service
//!   again and again, at most `<most>` times, until a call fails, then logs
//!   `<verb> call-until: <calls> calls, <error>` (`no error` when none
//!   failed).
//! - Its services answer by the request: `work:<ms>` works that long, then
//!   replies `done`; `loop` never answers; `panic` panics; `refuse` refuses
//!   with `not now`; `big` replies, and `refuse-big` refuses, one byte
//!   over the limit; `nest:<scoped
//!   id>` calls that service from inside the answer and replies with what
//!   came of it; `nothing` gives no answer; anything else replies
//!   `<verb> heard <caller>: <request>`.
//! - It logs every service that comes or goes, `<verb> heard service <id>
//!   <owner> <available>`.
//!
//! Callbacks the client asks (they need the `chat` permission):
//! - `/<verb> filter <scoped id>` installs a chat filter, and `/<verb>
//!   intercept <scoped id>` an input interceptor, that call that service
//!   with an empty request when asked, log `<verb> filter: <result>` or
//!   `<verb> intercept: <result>`, and let the line through.
//!
//! As it starts, by its verb:
//! - `offerer` offers the service `boom`.
//! - `starter` calls `openac.offerer/boom` with `panic` twice, logging
//!   `starter start: <first> <second>`.

use openac_plugin::items::{LootAction, LootClassification, LootContext};
use openac_plugin::services::MAX_PAYLOAD;
use openac_plugin::{Callback, CallbackAnswer, Event, Host, InterceptDecision, Plugin, StartInfo};

#[derive(Default)]
pub struct Suite {
    verb: String,
    classifier: u32,
    /// The services it offers: (name, handle).
    offered: Vec<(String, u32)>,
    /// The service its chat filter or input interceptor calls.
    target: String,
}

impl Plugin for Suite {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        let id = info.plugin_id.as_str();
        self.verb = id.rsplit('.').next().unwrap_or(id).to_owned();
        host.register_command(&self.verb, "Asks the other plugins of the suite")?;
        self.classifier = host.register_loot_classifier("rules", "Suite Rules")?;
        match self.verb.as_str() {
            "offerer" => {
                let handle = host
                    .register_service_raw("boom", "goes off")
                    .map_err(|e| e.to_string())?;
                self.offered.push(("boom".to_owned(), handle));
            }
            "starter" => {
                let first = call_service(host, "openac.offerer/boom", b"panic".to_vec());
                let second = call_service(host, "openac.offerer/boom", b"panic".to_vec());
                host.info(&format!("starter start: {first} {second}"));
            }
            _ => {}
        }
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        let call = match event {
            Event::Command(call) => call,
            Event::ServiceChanged(change) => {
                host.info(&format!(
                    "{} heard service {} {} {}",
                    self.verb, change.id, change.owner, change.available
                ));
                return;
            }
            _ => return,
        };
        let words: Vec<&str> = call.arguments.split_whitespace().collect();
        let verb = self.verb.clone();
        match words.as_slice() {
            ["ask", id, profile, rest @ ..] => {
                let around = rest.first().and_then(|ms| ms.parse().ok()).unwrap_or(0);
                work(host, around);
                let verdict = host.classify(id, profile, LootContext::default());
                work(host, around);
                let text = verdict.map_or_else(|| "none".to_owned(), |v| format!("{:?}", v.action));
                host.info(&format!("{verb} ask: {text}"));
            }
            ["offer", name] => {
                let offered = host.register_service_raw(name, &format!("{verb}'s {name}"));
                if let Ok(handle) = offered {
                    self.offered.push(((*name).to_owned(), handle));
                }
                host.info(&format!("{verb} offer {name}: {offered:?}"));
            }
            ["offer-long", name, bytes] => {
                let description = "d".repeat(bytes.parse().unwrap_or(0));
                let offered = host.register_service_raw(name, &description);
                if let Ok(handle) = offered {
                    self.offered.push(((*name).to_owned(), handle));
                }
                host.info(&format!("{verb} offer {name}: {offered:?}"));
            }
            ["filter", id] => {
                self.target = (*id).to_owned();
                let handle = host.chat_add_filter();
                host.info(&format!("{verb} filter on: {handle}"));
            }
            ["intercept", id] => {
                self.target = (*id).to_owned();
                let handle = host.chat_add_interceptor();
                host.info(&format!("{verb} intercept on: {handle:?}"));
            }
            ["withdraw", name] => {
                let handle = self
                    .offered
                    .iter()
                    .find(|(n, _)| n == name)
                    .map_or(0, |(_, h)| *h);
                let withdrawn = host.unregister_service(handle);
                host.info(&format!("{verb} withdraw {name}: {withdrawn}"));
            }
            ["list"] => {
                let ids: Vec<String> = host
                    .services()
                    .into_iter()
                    .map(|s| format!("{} ({}, {})", s.id, s.owner, s.description))
                    .collect();
                host.info(&format!("{verb} list: {}", ids.join(", ")));
            }
            ["call", id, request, rest @ ..] => {
                let around = rest.first().and_then(|ms| ms.parse().ok()).unwrap_or(0);
                work(host, around);
                let reply = call_service(host, id, request.as_bytes().to_vec());
                work(host, around);
                host.info(&format!("{verb} call: {reply}"));
            }
            ["call-until", id, request, most] => {
                let most: u32 = most.parse().unwrap_or(0);
                let mut calls = 0;
                let mut last = "no error".to_owned();
                while calls < most {
                    calls += 1;
                    let reply = call_service(host, id, request.as_bytes().to_vec());
                    if reply.starts_with("Err(") {
                        last = reply;
                        break;
                    }
                }
                host.info(&format!("{verb} call-until: {calls} calls, {last}"));
            }
            ["call-big", id] => {
                let reply = call_service(host, id, vec![b'x'; MAX_PAYLOAD + 1]);
                host.info(&format!("{verb} call: {reply}"));
            }
            _ => host.info(&format!("{verb}: unknown order '{}'", call.arguments)),
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
            Callback::ChatFilter(_) => {
                let result = call_service(host, &self.target.clone(), Vec::new());
                host.info(&format!("{} filter: {result}", self.verb));
                CallbackAnswer::Bool(false)
            }
            Callback::ChatIntercept(_) => {
                let result = call_service(host, &self.target.clone(), Vec::new());
                host.info(&format!("{} intercept: {result}", self.verb));
                CallbackAnswer::Intercept(InterceptDecision::Pass)
            }
            Callback::ServiceRequest(request)
                if self.offered.iter().any(|(_, h)| *h == request.handle) =>
            {
                let text = String::from_utf8_lossy(&request.payload).into_owned();
                let reply = match (text.as_str(), text.split_once(':')) {
                    (_, Some(("work", ms))) => {
                        work(host, ms.parse().unwrap_or(0));
                        "done".to_owned()
                    }
                    (_, Some(("nest", id))) => call_service(host, id, Vec::new()),
                    ("loop", _) => {
                        spin(u64::MAX);
                        String::new()
                    }
                    ("panic", _) => panic!("suite's service panicked"),
                    ("refuse", _) => return CallbackAnswer::Service(Err("not now".to_owned())),
                    ("big", _) => return CallbackAnswer::Service(Ok(vec![b'x'; MAX_PAYLOAD + 1])),
                    ("refuse-big", _) => {
                        return CallbackAnswer::Service(Err("x".repeat(MAX_PAYLOAD + 1)));
                    }
                    ("nothing", _) => return CallbackAnswer::Nothing,
                    _ => format!("{} heard {}: {text}", self.verb, request.caller_id),
                };
                CallbackAnswer::Service(Ok(reply.into_bytes()))
            }
            _ => CallbackAnswer::Nothing,
        }
    }
}

/// Calls service `id` with `payload`; what came of it, in words.
fn call_service(host: &mut Host<'_>, id: &str, payload: Vec<u8>) -> String {
    match host.call_service_raw(id, &payload) {
        Ok(reply) => format!("Ok({})", String::from_utf8_lossy(&reply)),
        Err(error) => format!("Err({error:?})"),
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
