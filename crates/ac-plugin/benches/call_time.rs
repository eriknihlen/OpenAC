//! What plugin calls cost in wall time: the test plugins run under the
//! host's own engine (its settings, its instance, its import loop) on
//! plugin-like work, each call timed by the host
//! (`PluginSession::last_call_time`) and from outside; and how soon after
//! its deadline a runaway loop is stopped.
//!
//! ```text
//! cargo bench -p ac-plugin --bench call_time
//! ```
//!
//! Not a pass/fail test: the numbers depend on the machine.
//!
//! `OPENAC_BENCH_WASM=<path to a plugin's .wasm>` also times loading that
//! module: compiled (no cache), compiled and written to an empty compile
//! cache, and loaded from that cache by a fresh engine (the next start).

use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_plugin::abi::{
    CharacterInfo, Import, LogLevel, calls, magic::SpellInfo, magic::SpellPage, serve,
};
use ac_plugin::fixtures::{fresh_root, install};
use ac_plugin::{
    Caller, HostVersion, PluginEngine, PluginSession, PluginSessionOptions, PluginSessionSettings,
    SessionApi, TimeLimits,
};

/// The deadline the runaway loop is stopped at here (the client's is
/// 10 s; the bench measures how far past it the stop comes).
const RUNAWAY_DEADLINE: Duration = Duration::from_millis(200);

/// A client that answers the spell calls with records the size of the
/// retail table's and everything else with its default.
#[derive(Debug, Default)]
struct Client {
    known: u32,
    table: u32,
}

fn spell(spell_id: u32) -> SpellInfo {
    SpellInfo {
        spell_id,
        name: format!("Fake Spell Of Testing {spell_id}"),
        description: "Increases the target's ability to test things by a good amount for \n                      a while, as the retail descriptions run on."
            .to_owned(),
        saying: "Zojak Tugeth".to_owned(),
        formula_component_ids: vec![1, 2, 3, 4, 5, 6, 7, 8],
        ..SpellInfo::default()
    }
}

impl SessionApi for Client {
    fn call(
        &mut self,
        _caller: Caller<'_>,
        import: Import,
        request: &[u8],
    ) -> Result<Vec<u8>, String> {
        let error = |e: ac_plugin::abi::DecodeError| e.to_string();
        match import {
            Import::SpellsKnown => {
                serve::<calls::SpellsKnown>(request, |_| (1..=self.known).map(spell).collect())
                    .map_err(error)
            }
            Import::SpellsPage => serve::<calls::SpellsPage>(request, |(first, count)| SpellPage {
                total: self.table,
                spells: (first..self.table.min(first.saturating_add(count.min(256))))
                    .map(spell)
                    .collect(),
            })
            .map_err(error),
            other => Ok(other.default_reply()),
        }
    }
    fn log(&mut self, _plugin_id: &str, _level: LogLevel, _message: &str) {}
    fn window_attached(&self) -> bool {
        false
    }
    fn post_chat(&mut self, _text: &str, _log_text_type: u32) {}
    fn character(&self) -> Option<CharacterInfo> {
        None
    }
}

fn session(root: &std::path::Path, allow: &[&str], engine: PluginEngine) -> PluginSession {
    PluginSession::new(
        Arc::new(engine),
        PluginSessionOptions {
            roots: vec![root.to_path_buf()],
            allow_list: Some(allow.iter().map(|s| (*s).to_owned()).collect()),
            settings: PluginSessionSettings::empty(),
            host_version: HostVersion::new(0, 1, 0),
            storage: ac_plugin::storage::StorageRoots::default(),
            slow_log: None,
        },
    )
}

/// One measured workload: the median call, timed by the host (the call
/// alone) and from outside (the event's delivery included).
struct Sample {
    name: &'static str,
    host: Duration,
    outside: Duration,
}

fn median(mut times: Vec<Duration>) -> Duration {
    times.sort();
    times[times.len() / 2]
}

/// The median call of `runs` runs of `line`.
fn measure(
    name: &'static str,
    plugins: &mut PluginSession,
    client: &mut Client,
    line: &str,
    runs: usize,
) -> Sample {
    // Once first: warms the caches.
    assert!(plugins.handle_line(line, client), "{line}");
    let (outside, host): (Vec<Duration>, Vec<Duration>) = (0..runs)
        .map(|_| {
            let start = Instant::now();
            assert!(plugins.handle_line(line, client), "{line}");
            let outside = start.elapsed();
            (
                outside,
                plugins.last_call_time("openac.probe").expect("loaded"),
            )
        })
        .unzip();
    Sample {
        name,
        host: median(host),
        outside: median(outside),
    }
}

/// A runaway loop to its deadline: the time to the fault, median of `runs`.
fn runaway(runs: usize) -> Duration {
    median(
        (0..runs)
            .map(|run| {
                let root = fresh_root(&format!("call-time-loop-{run}"));
                install(&root, "misbehave");
                let engine = PluginEngine::with_time_limits(TimeLimits {
                    call: RUNAWAY_DEADLINE,
                    ..TimeLimits::default()
                });
                let mut plugins = session(&root, &["openac.misbehave"], engine);
                let mut client = Client::default();
                plugins.start(&mut client);
                let start = Instant::now();
                assert!(plugins.handle_line("/crash loop", &mut client));
                let time = start.elapsed();
                assert!(plugins.loaded_ids().is_empty(), "the loop was stopped");
                let _ = std::fs::remove_dir_all(&root);
                time
            })
            .collect(),
    )
}

fn main() {
    let root = fresh_root("call-time");
    install(&root, "probe");
    let mut plugins = session(&root, &["openac.probe"], PluginEngine::new());
    let mut client = Client {
        known: 730,
        table: 6266,
    };
    plugins.start(&mut client);
    let samples = [
        measure(
            "known self buffs x8 (730 records each)",
            &mut plugins,
            &mut client,
            "/probe known 8",
            7,
        ),
        measure(
            "whole spell table, paged (6266 records)",
            &mut plugins,
            &mut client,
            "/probe spells",
            7,
        ),
        measure(
            "census (every read without a permission)",
            &mut plugins,
            &mut client,
            "/probe census",
            7,
        ),
    ];
    plugins.shutdown(&mut client);
    let _ = std::fs::remove_dir_all(&root);

    println!("{:<44} {:>10} {:>12}", "workload", "call ms", "outside ms");
    for sample in &samples {
        println!(
            "{:<44} {:>10.2} {:>12.2}",
            sample.name,
            sample.host.as_secs_f64() * 1000.0,
            sample.outside.as_secs_f64() * 1000.0
        );
    }
    println!(
        "{:<44} {:>10.1} (deadline {} ms)",
        "runaway loop, stopped after (ms)",
        runaway(5).as_secs_f64() * 1000.0,
        RUNAWAY_DEADLINE.as_millis()
    );
    if let Some(wasm) = std::env::var_os("OPENAC_BENCH_WASM") {
        load_times(std::path::Path::new(&wasm));
    }
}

/// The time to load `wasm`: compiled, compiled into an empty cache, and
/// from that cache (median of 3 each, a fresh engine every time).
fn load_times(wasm: &std::path::Path) {
    let cache = fresh_root("call-time-module-cache");
    let time = |engine: &PluginEngine| {
        let start = Instant::now();
        engine.module(wasm).expect("the module loads");
        start.elapsed()
    };
    let compiled = median((0..3).map(|_| time(&PluginEngine::new())).collect());
    let first = median(
        (0..3)
            .map(|_| {
                let _ = std::fs::remove_dir_all(&cache);
                time(&PluginEngine::with_module_cache(cache.clone()))
            })
            .collect(),
    );
    let cached = median(
        (0..3)
            .map(|_| {
                let engine = PluginEngine::with_module_cache(cache.clone());
                let elapsed = time(&engine);
                assert_eq!(engine.module_cache_hits(), 1, "loaded from the cache");
                elapsed
            })
            .collect(),
    );
    let _ = std::fs::remove_dir_all(&cache);
    println!();
    println!(
        "{}: {} bytes; compiled {:.0} ms, first load (compiled and cached) {:.0} ms, cached \
         load {:.1} ms",
        wasm.display(),
        std::fs::metadata(wasm).map_or(0, |m| m.len()),
        compiled.as_secs_f64() * 1000.0,
        first.as_secs_f64() * 1000.0,
        cached.as_secs_f64() * 1000.0
    );
}
