//! `misbehave`: fails on request, so the host's containment can be tested.
//!
//! - setting `refuse`: `enable` refuses to start with that text.
//! - setting `panic-on-enable`: `enable` panics.
//! - `/crash panic`: panics.
//! - `/crash loop`: never returns (stopped at the call's deadline).
//! - `/crash oom`: allocates until memory runs out.
//! - `/crash say`: submits a chat line (needs the `chat` permission).
//! - `/crash filter`: installs a chat filter that panics when asked.
//! - setting `panic-on-tick`: panics on the given tick number.
//! - chat line `burn`: calls a few client functions, then never returns
//!   (stopped at the deadline in the chat event).
//! - chat line `spin`: the same client functions, then a million rounds of
//!   work (a call that finishes soon, for the slow log).
//! - chat line `slow`: the same client functions, then work until the
//!   client's clock says [`SLOW_MS`] have passed (a slow call that
//!   finishes).

use openac_plugin::{Callback, CallbackAnswer, Event, Host, Plugin, StartInfo};

#[derive(Default)]
pub struct Misbehave {
    ticks: u64,
    panic_on_tick: Option<u64>,
    hoard: Vec<Vec<u8>>,
}

impl Plugin for Misbehave {
    fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
        host.register_command("crash", "Fails on purpose")?;
        if let Some(reason) = host.setting("refuse") {
            return Err(reason);
        }
        if host.setting("panic-on-enable").is_some() {
            panic!("misbehave panicked in enable");
        }
        self.panic_on_tick = host.setting("panic-on-tick").and_then(|t| t.parse().ok());
        Ok(())
    }

    fn disable(&mut self, host: &mut Host<'_>) {
        host.info("misbehave disabled");
    }

    fn tick(&mut self, _host: &mut Host<'_>) {
        self.ticks += 1;
        if self.panic_on_tick == Some(self.ticks) {
            panic!("misbehave panicked on tick {}", self.ticks);
        }
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        if let Event::ChatReceived(line) = &event {
            match line.text.as_str() {
                "burn" => {
                    calls_some_imports(host);
                    spin(u64::MAX);
                }
                "spin" => {
                    calls_some_imports(host);
                    spin(SPIN);
                }
                // Past the slow-call time but far short of the deadline:
                // the call goes on and is reported as slow.
                "slow" => {
                    calls_some_imports(host);
                    let start = host.clock().utc_unix_100ns;
                    while host.clock().utc_unix_100ns - start < SLOW_MS * 10_000 {
                        spin(SPIN);
                    }
                }
                _ => {}
            }
            return;
        }
        let Event::Command(call) = event else {
            return;
        };
        match call.arguments.as_str() {
            "panic" => panic!("misbehave was told to panic"),
            "loop" => {
                let mut spins: u64 = 0;
                loop {
                    spins = std::hint::black_box(spins.wrapping_add(1));
                }
            }
            "oom" => loop {
                self.hoard.push(vec![1u8; 1 << 20]);
            },
            "say" => host.chat_submit("misbehave says hi"),
            "filter" => {
                host.chat_add_filter();
            }
            other => host.info(&format!("misbehave: unknown order '{other}'")),
        }
    }

    fn callback(&mut self, _host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        match callback {
            Callback::ChatFilter(_) => panic!("misbehave's filter panicked"),
            _ => CallbackAnswer::Nothing,
        }
    }
}

/// How many rounds the `spin` chat line turns: a millisecond or so of
/// compiled code.
const SPIN: u64 = 1_000_000;

/// How long the `slow` chat line works by the client's clock, milliseconds:
/// past the slow-call time (100 ms), far short of the deadline (10 s).
const SLOW_MS: i64 = 150;

/// A known pattern of client calls for the reports to name:
/// `host_setting` once, `host_window_attached` three times, `host_clock`
/// twice, in that order.
fn calls_some_imports(host: &mut Host<'_>) {
    let _ = host.setting("unused");
    for _ in 0..3 {
        let _ = host.window_attached();
    }
    for _ in 0..2 {
        let _ = host.clock();
    }
}

/// Turns `rounds` times (`u64::MAX`: until it is stopped).
fn spin(rounds: u64) {
    let mut spins: u64 = 0;
    while spins < rounds {
        spins = std::hint::black_box(spins.wrapping_add(1));
    }
}

openac_plugin::export_plugin!(Misbehave);
