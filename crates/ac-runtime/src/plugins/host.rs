//! API group A: host services a sandboxed plugin can reach only through the
//! client (the clocks, randomness, the process's numbers, ending the
//! session).

use std::collections::hash_map::Entry;

use ac_host::process_stats::{self, CpuMeter};
use ac_plugin::abi::host::{Clock, ProcessStats};

use super::PluginBinding;
use crate::session::SessionCommand;

impl PluginBinding<'_> {
    /// The clocks now: wall time, the local zone's offset, the session's
    /// monotonic clock and a fresh seed.
    pub(super) fn clock(&mut self) -> Clock {
        let utc = self.wall_unix_ms;
        // The pass's wall time for the milliseconds (every call in a pass
        // agrees), the instant of the call for the fine ticks.
        let precise = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(utc.saturating_mul(10_000), |d| {
                i64::try_from(d.as_nanos() / 100).unwrap_or(i64::MAX)
            });
        let services = &mut self.state.plugin_services;
        Clock {
            utc_unix_ms: utc,
            utc_offset_minutes: services.utc_offset_seconds(utc) / 60,
            monotonic_ms: services.monotonic_ms(),
            random_seed: services.next_seed(),
            utc_unix_100ns: precise,
            utc_offset_seconds: services.utc_offset_seconds(precise / 10_000),
        }
    }

    /// The client process's working set, handles and CPU use since this
    /// plugin's previous call (0 on its first), as the C# plugins read
    /// `Process.GetCurrentProcess()`.
    pub(super) fn process_stats(&mut self, plugin_id: &str) -> ProcessStats {
        let reading = process_stats::read();
        let now = std::time::Instant::now();
        let cpu_percent = match self
            .state
            .plugin_services
            .cpu_meters
            .entry(plugin_id.to_owned())
        {
            Entry::Occupied(mut meter) => {
                meter
                    .get_mut()
                    .sample(reading.cpu_time, now, process_stats::processor_count())
            }
            Entry::Vacant(slot) => {
                slot.insert(CpuMeter::new(reading.cpu_time, now));
                0.0
            }
        };
        ProcessStats {
            working_set_bytes: reading.working_set_bytes,
            cpu_percent,
            handle_count: reading.handle_count,
        }
    }

    /// Forget a plugin's CPU baseline (it unloaded).
    pub(super) fn release_process_stats(&mut self, plugin_id: &str) {
        self.state.plugin_services.cpu_meters.remove(plugin_id);
    }

    /// End the session gracefully (OpenAC's `Window.RequestClose` on a
    /// headless host: log off, then only this session ends). With no
    /// connection there is nothing to close.
    pub(super) fn request_close(&mut self) -> bool {
        if self.link.is_none() {
            return false;
        }
        self.deferred.push(SessionCommand::Shutdown);
        true
    }
}
