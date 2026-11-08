//! Windows console glue for stopping a client gracefully: a child started
//! in its own process group is sent Ctrl+Break, which the client handles by
//! logging off. A console event reaches only processes attached to the
//! sender's console, so a launcher without a console allocates a hidden one
//! while it starts the child, and attaches to the child's console to send
//! the event.
//!
//! Taking a console points the process's standard handles at it, and
//! letting it go leaves them naming handles that are closed; a later start
//! that inherits them fails with "The handle is invalid". So the standard
//! handles are put back as they were whenever the console is let go.
//!
//! This is the crate's only unsafe code: console calls with no pointers but
//! one fixed-size out-buffer.
#![allow(unsafe_code)]

use std::sync::Mutex;

use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::System::Console::{
    AllocConsole, AttachConsole, CTRL_BREAK_EVENT, FreeConsole, GenerateConsoleCtrlEvent,
    GetConsoleProcessList, GetConsoleWindow, GetStdHandle, STD_ERROR_HANDLE, STD_HANDLE,
    STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, SetStdHandle,
};

const STANDARD_HANDLES: [STD_HANDLE; 3] = [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE];

/// The process's standard handles before it takes a console.
struct StandardHandles([HANDLE; 3]);

impl StandardHandles {
    fn save() -> Self {
        // SAFETY: plain integer arguments; the handles are only stored.
        Self(STANDARD_HANDLES.map(|id| unsafe { GetStdHandle(id) }))
    }

    /// Lets the console go and puts the saved standard handles back.
    fn free_console_and_restore(self) {
        // SAFETY: no arguments, then integer ids with the handles this
        // process had before it took the console.
        unsafe {
            FreeConsole();
            for (id, handle) in STANDARD_HANDLES.into_iter().zip(self.0) {
                SetStdHandle(id, handle);
            }
        }
    }
}
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
    let mut allocated = None;
    if !has_console() {
        let saved = StandardHandles::save();
        // SAFETY: no arguments; failure is reported by the return value.
        if unsafe { AllocConsole() } != 0 {
            allocated = Some(saved);
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
    if let Some(saved) = allocated {
        saved.free_console_and_restore();
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
    let mut attached = None;
    if !has_console() {
        let saved = StandardHandles::save();
        // SAFETY: plain integer argument; failure is reported by the return.
        if unsafe { AttachConsole(pid) } == 0 {
            return false;
        }
        attached = Some(saved);
    }
    // SAFETY: plain integer arguments; the group id is the child's pid,
    // which names only the child's own process group.
    let sent = unsafe { GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid) } != 0;
    if let Some(saved) = attached {
        saved.free_console_and_restore();
    }
    sent
}
