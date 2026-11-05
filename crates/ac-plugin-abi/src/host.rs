//! Host services (API group A): the clock, randomness and the client
//! process's numbers a sandboxed plugin has no other way to read.

use crate::wire_record;

wire_record! {
    /// The client's clocks now ([`crate::Import::HostClock`]).
    pub struct Clock {
        /// Wall time, UTC milliseconds since the Unix epoch.
        pub utc_unix_ms: i64,
        /// The local time zone's offset from UTC now, minutes (east
        /// positive).
        pub utc_offset_minutes: i32,
        /// A clock that never goes back, milliseconds since the session
        /// started (for stopwatches).
        pub monotonic_ms: u64,
        /// A fresh random seed (for a plugin's own generator).
        pub random_seed: u64,
        /// Wall time at the moment of the call, UTC, in 100-nanosecond
        /// ticks since the Unix epoch (the resolution of .NET's
        /// `DateTime.UtcNow`; add 621 355 968 000 000 000 for .NET ticks).
        pub utc_unix_100ns: i64,
        /// The local time zone's offset from UTC at that moment, seconds
        /// (east positive; the zones that are not whole minutes).
        pub utc_offset_seconds: i32,
    }
}

wire_record! {
    /// The client process's own numbers ([`crate::Import::HostProcessStats`]),
    /// what a C# plugin reads from `Process.GetCurrentProcess()`.
    pub struct ProcessStats {
        /// The working set, bytes (`WorkingSet64`).
        pub working_set_bytes: u64,
        /// CPU time used over wall time across all cores since this
        /// plugin's previous call, percent; 0 on its first call.
        pub cpu_percent: f32,
        /// Open handles (`HandleCount`).
        pub handle_count: u32,
    }
}

/// .NET's `DateTime` ticks (100 ns since 0001-01-01) at the Unix epoch.
pub const DOTNET_TICKS_AT_UNIX_EPOCH: i64 = 621_355_968_000_000_000;

impl Clock {
    /// The call's instant as .NET's `DateTime.UtcNow.Ticks`.
    #[must_use]
    pub fn utc_dotnet_ticks(&self) -> i64 {
        self.utc_unix_100ns
            .saturating_add(DOTNET_TICKS_AT_UNIX_EPOCH)
    }

    /// The call's instant in local time as .NET's `DateTime.Now.Ticks`.
    #[must_use]
    pub fn local_dotnet_ticks(&self) -> i64 {
        self.utc_dotnet_ticks()
            .saturating_add(i64::from(self.utc_offset_seconds) * 10_000_000)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // .NET: `new DateTime(1970, 1, 1).Ticks` is 621355968000000000; one
    // second is 10 000 000 ticks.
    #[test]
    fn ticks_follow_dotnet() {
        let clock = Clock {
            utc_unix_100ns: 15_000_000,
            utc_offset_seconds: 3600,
            ..Clock::default()
        };
        assert_eq!(clock.utc_dotnet_ticks(), 621_355_968_015_000_000);
        assert_eq!(clock.local_dotnet_ticks(), 621_356_004_015_000_000);
    }
}
