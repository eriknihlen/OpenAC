//! On Windows the window host's event loop keeps an unseen window of its
//! own, through which every command from another thread wakes it. A close
//! request posted to every window of the process from outside (`taskkill`
//! without `/F` does that) must not take that window down: the host must
//! still end on shutdown, so that a client whose game window was already
//! gone (a browser took the session over) exits after its session logs off.
//!
//! Its own test binary without the test harness: the event loop must run on
//! the process's main thread, and a host that never ends is caught by a
//! watchdog that fails the run.

fn main() {
    #[cfg(windows)]
    windows::run();
}

#[cfg(windows)]
#[allow(unsafe_code)]
mod windows {
    use std::time::Duration;

    use ac_render::{HostOptions, WindowHost};
    use windows_sys::Win32::Foundation::{HWND, LPARAM};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindowThreadProcessId, PostMessageW, WM_CLOSE,
    };

    /// Longer than the host ever needs to end; a host that has not ended by
    /// then never will.
    const PATIENCE: Duration = Duration::from_secs(30);

    pub fn run() {
        let (host, handle, _events) = WindowHost::new(HostOptions::default()).expect("event loop");
        // No game window is open, as when a browser has taken the session
        // over; the event loop still keeps a window of its own.
        let windows = windows_of_this_process();
        assert!(
            !windows.is_empty(),
            "the event loop keeps a window of its own"
        );
        for window in windows {
            // SAFETY: a window handle this process enumerated a moment ago;
            // posting to one that is gone fails harmlessly.
            let posted = unsafe { PostMessageW(window, WM_CLOSE, 0, 0) };
            assert_ne!(posted, 0, "the close request was posted");
        }
        // Queued after the close requests, as a session's end follows the
        // player's `taskkill`.
        handle.shutdown().expect("the host takes commands");
        std::thread::spawn(|| {
            std::thread::sleep(PATIENCE);
            eprintln!("the window host did not end on shutdown after a close request");
            std::process::exit(1);
        });
        host.run().expect("the event loop ran");
        println!("the window host ended on shutdown after a close request to every window");
    }

    fn windows_of_this_process() -> Vec<HWND> {
        unsafe extern "system" fn each(window: HWND, found: LPARAM) -> i32 {
            // SAFETY: `found` is the address of the vector below, alive for
            // the whole enumeration, which runs on this thread.
            let found = unsafe { &mut *(found as *mut Vec<HWND>) };
            let mut process = 0u32;
            // SAFETY: a window handle the enumeration passed; `process` is a
            // live, writable u32.
            unsafe { GetWindowThreadProcessId(window, &raw mut process) };
            if process == std::process::id() {
                found.push(window);
            }
            1
        }
        let mut found: Vec<HWND> = Vec::new();
        // SAFETY: the callback reads `found` only during this call.
        unsafe { EnumWindows(Some(each), &raw mut found as LPARAM) };
        found
    }
}
