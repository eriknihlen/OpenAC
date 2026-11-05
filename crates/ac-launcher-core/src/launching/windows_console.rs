//! Windows console glue for stopping a client gracefully: a child started
//! in its own process group is sent Ctrl+Break, which the client handles by
//! logging off. A console event reaches only processes attached to the
//! sender's console, so a launcher without a console allocates a hidden one
//! while it starts the child, and attaches to the child's console to send
//! the event.
//!
//! This is the crate's only unsafe code: four console calls with no
//! pointers but one fixed-size out-buffer.
#![allow(unsafe_code)]

use std::sync::Mutex;

use windows_sys::Win32::System::Console::{
    AllocConsole, AttachConsole, CTRL_BREAK_EVENT, FreeConsole, GenerateConsoleCtrlEvent,
    GetConsoleProcessList, GetConsoleWindow,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{SW_HIDE, ShowWindow};

/// Serializes console attach and detach: the console a process is attached
/// to is process-wide operating-system state, so two threads must not
/// change it at once.
static CONSOLE_GATE: Mutex<()> = Mutex::new(());

fn has_console() -> bool {
    let mut processes = [0u32; 1];
    // SAFETY: the buffer is a valid, writable array of one u32 and its
    // length is passed; the call only reports the count.
    unsafe { GetConsoleProcessList(processes.as_mut_ptr(), 1) != 0 }
}

/// Runs `start` with a console attached (a hidden one allocated for the
/// duration when this process has none), so the child shares a console
/// with whoever later sends it a console event.
pub fn with_console<T>(start: impl FnOnce() -> T) -> T {
    let _gate = CONSOLE_GATE
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut allocated = false;
    if !has_console() {
        // SAFETY: no arguments; failure is reported by the return value.
        allocated = unsafe { AllocConsole() } != 0;
        if allocated {
            // SAFETY: GetConsoleWindow takes no arguments; ShowWindow
            // accepts any window handle, including null.
            unsafe {
                let window = GetConsoleWindow();
                if !window.is_null() {
                    ShowWindow(window, SW_HIDE);
                }
            }
        }
    }
    let result = start();
    if allocated {
        // SAFETY: no arguments; detaches the console allocated above.
        unsafe {
            FreeConsole();
        }
    }
    result
}

/// Sends Ctrl+Break to the process group whose leader is `pid` (a child
/// started with `CREATE_NEW_PROCESS_GROUP`). False when it could not be sent.
pub fn send_ctrl_break(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    let _gate = CONSOLE_GATE
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    let mut attached = false;
    if !has_console() {
        // SAFETY: plain integer argument; failure is reported by the return.
        if unsafe { AttachConsole(pid) } == 0 {
            return false;
        }
        attached = true;
    }
    // SAFETY: plain integer arguments; the group id is the child's pid,
    // which names only the child's own process group.
    let sent = unsafe { GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid) } != 0;
    if attached {
        // SAFETY: no arguments; detaches the console attached above.
        unsafe {
            FreeConsole();
        }
    }
    sent
}
