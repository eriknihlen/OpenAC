//! The client process's own numbers, as a C# plugin reads them from
//! `Process.GetCurrentProcess()`: the working set, the open handles and the
//! CPU time used, and the CPU use between two readings as a percentage of
//! all cores.
//!
//! On Windows the operating system answers (`K32GetProcessMemoryInfo`,
//! `GetProcessHandleCount`, `GetProcessTimes` through `windows-sys`). On
//! Linux `/proc/self` is read: `VmRSS` of `status`, the entries of `fd`,
//! and `utime + stime` of `stat` in the kernel's user-space clock ticks
//! (100 a second, fixed by its interface on x86 and ARM). Anything else, and
//! anything unreadable, is zero. No C code is compiled.

use std::time::{Duration, Instant};

/// One reading of the process.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ProcessReading {
    /// The working set (resident memory), bytes.
    pub working_set_bytes: u64,
    /// Open handles (file descriptors on Linux).
    pub handle_count: u32,
    /// CPU time used so far, user and kernel, all threads.
    pub cpu_time: Duration,
}

/// Reads the process now; zero for what the platform does not give.
#[must_use]
pub fn read() -> ProcessReading {
    #[cfg(windows)]
    {
        windows::read()
    }
    #[cfg(target_os = "linux")]
    {
        linux::read()
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        ProcessReading::default()
    }
}

/// The processors the process can run on (.NET's
/// `Environment.ProcessorCount`); at least 1.
#[must_use]
pub fn processor_count() -> u32 {
    std::thread::available_parallelism()
        .ok()
        .and_then(|n| u32::try_from(n.get()).ok())
        .unwrap_or(1)
}

/// CPU use between readings: CPU time over wall time across all cores, in
/// percent, since the previous [`CpuMeter::sample`] (or the meter's start).
#[derive(Debug, Clone, Copy)]
pub struct CpuMeter {
    last_cpu: Duration,
    last_wall: Instant,
}

impl CpuMeter {
    /// A meter whose first sample measures from `cpu` and `now`.
    #[must_use]
    pub fn new(cpu: Duration, now: Instant) -> Self {
        Self {
            last_cpu: cpu,
            last_wall: now,
        }
    }

    /// The CPU use since the last sample, percent of `processors` cores;
    /// 0 when no wall time passed. The reading becomes the next baseline.
    pub fn sample(&mut self, cpu: Duration, now: Instant, processors: u32) -> f32 {
        let cpu_ms = cpu.as_secs_f64() * 1000.0 - self.last_cpu.as_secs_f64() * 1000.0;
        let elapsed_ms = now.saturating_duration_since(self.last_wall).as_secs_f64()
            * 1000.0
            * f64::from(processors);
        self.last_cpu = cpu;
        self.last_wall = now;
        if elapsed_ms <= 0.0 {
            0.0
        } else {
            (cpu_ms / elapsed_ms * 100.0) as f32
        }
    }
}

#[cfg(windows)]
mod windows {
    //! The operating system's counters for the current process.

    use std::time::Duration;

    use windows_sys::Win32::Foundation::FILETIME;
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS,
    };
    use windows_sys::Win32::System::Threading::{
        GetCurrentProcess, GetProcessHandleCount, GetProcessTimes,
    };

    use super::ProcessReading;

    fn ticks(time: FILETIME) -> u64 {
        (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime)
    }

    #[allow(unsafe_code)]
    pub(super) fn read() -> ProcessReading {
        let mut memory = PROCESS_MEMORY_COUNTERS {
            cb: u32::try_from(std::mem::size_of::<PROCESS_MEMORY_COUNTERS>()).unwrap_or(0),
            ..PROCESS_MEMORY_COUNTERS::default()
        };
        let mut handles: u32 = 0;
        let mut creation = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        // SAFETY: `GetCurrentProcess` returns a pseudo-handle that needs no
        // closing and is valid for the whole process. Every out pointer is
        // to a live, aligned local of the type the function writes, and
        // `cb` is that structure's size. Each result is checked before its
        // value is used; a failed call leaves the zero it started with.
        let (memory_ok, handles_ok, times_ok) = unsafe {
            let process = GetCurrentProcess();
            (
                K32GetProcessMemoryInfo(process, &mut memory, memory.cb) != 0,
                GetProcessHandleCount(process, &mut handles) != 0,
                GetProcessTimes(process, &mut creation, &mut exit, &mut kernel, &mut user) != 0,
            )
        };
        let cpu_ticks = if times_ok {
            ticks(kernel).saturating_add(ticks(user))
        } else {
            0
        };
        ProcessReading {
            working_set_bytes: if memory_ok {
                u64::try_from(memory.WorkingSetSize).unwrap_or(u64::MAX)
            } else {
                0
            },
            handle_count: if handles_ok { handles } else { 0 },
            // FILETIME counts 100-nanosecond intervals.
            cpu_time: Duration::from_nanos(cpu_ticks.saturating_mul(100)),
        }
    }
}

#[cfg(target_os = "linux")]
mod linux {
    //! `/proc/self`.

    use std::time::Duration;

    use super::ProcessReading;

    /// The kernel's user-space clock tick (`USER_HZ`), which `/proc` times
    /// are counted in.
    const USER_HZ: u64 = 100;

    pub(super) fn read() -> ProcessReading {
        ProcessReading {
            working_set_bytes: std::fs::read_to_string("/proc/self/status")
                .ok()
                .and_then(|text| resident_bytes(&text))
                .unwrap_or(0),
            handle_count: std::fs::read_dir("/proc/self/fd")
                .map(|entries| u32::try_from(entries.count()).unwrap_or(u32::MAX))
                .unwrap_or(0),
            cpu_time: std::fs::read_to_string("/proc/self/stat")
                .ok()
                .and_then(|text| cpu_ticks(&text))
                .map_or(Duration::ZERO, |ticks| {
                    Duration::from_millis(ticks.saturating_mul(1000) / USER_HZ)
                }),
        }
    }

    /// `VmRSS:   1234 kB` in bytes.
    pub(super) fn resident_bytes(status: &str) -> Option<u64> {
        let line = status.lines().find(|l| l.starts_with("VmRSS:"))?;
        let kib: u64 = line
            .trim_start_matches("VmRSS:")
            .split_whitespace()
            .next()?
            .parse()
            .ok()?;
        Some(kib.saturating_mul(1024))
    }

    /// `utime + stime` (fields 14 and 15) of `stat`; the name in
    /// parentheses may hold spaces, so fields count from its closing one.
    pub(super) fn cpu_ticks(stat: &str) -> Option<u64> {
        let rest = &stat[stat.rfind(')')? + 1..];
        let mut fields = rest.split_whitespace().skip(11);
        let user: u64 = fields.next()?.parse().ok()?;
        let system: u64 = fields.next()?.parse().ok()?;
        Some(user.saturating_add(system))
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn proc_text_is_read_by_field() {
            assert_eq!(
                resident_bytes("Name:\tx\nVmRSS:\t   2048 kB\n"),
                Some(2048 * 1024)
            );
            assert_eq!(resident_bytes("Name:\tx\n"), None);
            let stat = "42 (a b) S 1 2 3 4 5 6 7 8 9 10 250 30 0 0";
            assert_eq!(cpu_ticks(stat), Some(280));
            assert_eq!(cpu_ticks("garbage"), None);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // The C# plugin's `ProcessTelemetry.SampleCpuUsage`: CPU milliseconds
    // over elapsed milliseconds times the processor count, times 100, and 0
    // when no time passed.
    #[test]
    fn cpu_use_is_cpu_time_over_wall_time_across_all_cores() {
        let start = Instant::now();
        let mut meter = CpuMeter::new(Duration::from_millis(1000), start);
        let one_second = start + Duration::from_secs(1);
        // 2 s of CPU in 1 s of wall time on 8 cores: a quarter of them.
        let percent = meter.sample(Duration::from_millis(3000), one_second, 8);
        assert!((percent - 25.0).abs() < 1e-4, "{percent}");
        // The reading is the next baseline; no time since is 0.
        assert_eq!(
            meter.sample(Duration::from_millis(3500), one_second, 8),
            0.0
        );
        let two_seconds = start + Duration::from_secs(2);
        let percent = meter.sample(Duration::from_millis(3750), two_seconds, 1);
        assert!((percent - 25.0).abs() < 1e-4, "{percent}");
    }

    #[test]
    fn the_running_process_has_a_working_set_and_handles_where_the_platform_tells() {
        let reading = read();
        assert!(processor_count() >= 1);
        if cfg!(any(windows, target_os = "linux")) {
            assert!(reading.working_set_bytes > 1024 * 1024, "{reading:?}");
            assert!(reading.handle_count > 0, "{reading:?}");
            // Burn a little CPU so the counter has moved.
            let mut x = 0u64;
            for i in 0..5_000_000u64 {
                x = x.wrapping_mul(31).wrapping_add(i);
            }
            std::hint::black_box(x);
            assert!(read().cpu_time >= reading.cpu_time);
        } else {
            assert_eq!(reading, ProcessReading::default());
        }
    }
}
