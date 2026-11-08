//! The windowed launcher has no console and no standard handles. Starting a
//! client borrows a hidden console for the duration of the start, and the
//! operating system points the process's standard handles at that console;
//! once the console is let go those handles name nothing. A later start
//! that inherits the standard handles (the self-update helper's) then
//! failed with "The handle is invalid" (os error 6).
//!
//! This test changes process-wide console state, so it is the only test in
//! its binary.
#![cfg(windows)]
#![allow(unsafe_code)]

use std::process::Command;
use std::time::{Duration, Instant};

use ac_launcher_core::launching::{request_console_stop, spawn_stoppable};
use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::System::Console::{
    FreeConsole, GetStdHandle, STD_ERROR_HANDLE, STD_HANDLE, STD_INPUT_HANDLE, STD_OUTPUT_HANDLE,
    SetStdHandle,
};

const STANDARD: [STD_HANDLE; 3] = [STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE];

fn quick_exit() -> Command {
    let mut command = Command::new("cmd.exe");
    command.args(["/d", "/c", "exit 0"]);
    command
}

#[test]
fn borrowing_a_console_leaves_the_standard_handles_usable() {
    // SAFETY: plain integer arguments.
    let saved: Vec<HANDLE> = STANDARD
        .iter()
        .map(|&id| unsafe { GetStdHandle(id) })
        .collect();
    // Become what the windowed launcher is: no console, no standard handles.
    // SAFETY: no arguments, then integer ids with a null handle.
    unsafe {
        FreeConsole();
        for id in STANDARD {
            SetStdHandle(id, std::ptr::null_mut());
        }
    }

    let client = spawn_stoppable(&mut quick_exit()).and_then(|mut child| child.wait());
    // Stopping a client attaches to its console for the moment of the stop.
    let mut lingering = Command::new("cmd.exe");
    lingering.args(["/d", "/c", "ping -n 30 127.0.0.1 >nul"]);
    let stopped = spawn_stoppable(&mut lingering).map(|mut child| {
        // A child this new may not be on its console yet; try until it is.
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut sent = request_console_stop(child.id());
        while !sent && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(50));
            sent = request_console_stop(child.id());
        }
        let _ = child.kill();
        let _ = child.wait();
        sent
    });
    // The self-update helper is started with the standard handles inherited.
    let helper = quick_exit().spawn().and_then(|mut child| child.wait());
    // SAFETY: integer ids with no arguments.
    let after: Vec<HANDLE> = STANDARD
        .iter()
        .map(|&id| unsafe { GetStdHandle(id) })
        .collect();

    // SAFETY: integer ids with the handles this process had at the start.
    unsafe {
        for (&id, &handle) in STANDARD.iter().zip(&saved) {
            SetStdHandle(id, handle);
        }
    }

    assert!(client.is_ok(), "starting a client failed: {client:?}");
    assert!(
        matches!(stopped, Ok(true)),
        "the console stop was not sent: {stopped:?}"
    );
    assert!(
        helper.is_ok(),
        "a later inheriting start failed: {helper:?}"
    );
    assert!(
        after.iter().all(|handle| handle.is_null()),
        "the console borrow left standard handles behind: {after:?}"
    );
}
