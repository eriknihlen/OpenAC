//! Attach, detach and re-attach a window while a "session" thread keeps ticking.
//!
//! Opens a window (cleared to blue) for 2 s, closes it, runs with no window for 2 s, opens a
//! second window (cleared to green) for 2 s, closes it and exits. A separate thread prints a tick
//! every 250 ms the whole time; the ticks never stop or skip while windows come and go.
//!
//! Run: `cargo run -p ac-render --example attach_detach`

use std::error::Error;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Receiver;
use std::thread;
use std::time::{Duration, Instant};

use ac_render::{Color, HostEvent, HostHandle, WindowHost, WindowSpec};

/// How long to wait for the host to answer a command before calling it a failure.
const ANSWER_TIMEOUT: Duration = Duration::from_secs(10);

fn main() -> Result<(), Box<dyn Error>> {
    let start = Instant::now();
    let (host, handle, events) = WindowHost::new(ac_render::HostOptions {
        dat_dir: None,
        ..Default::default()
    })?;

    // Stands in for a game session: its own thread, never touched by the window.
    let running = Arc::new(AtomicBool::new(true));
    let session = {
        let running = Arc::clone(&running);
        thread::spawn(move || {
            let mut tick = 0u32;
            while running.load(Ordering::Relaxed) {
                println!(
                    "[{:5.2}s] session tick {tick}",
                    start.elapsed().as_secs_f64()
                );
                tick += 1;
                thread::sleep(Duration::from_millis(250));
            }
            tick
        })
    };

    // Plays the owner: attaches and detaches from another thread, then shuts the host down.
    let driver = {
        let handle = handle.clone();
        thread::spawn(move || {
            let result = script(&handle, &events, start);
            // Always stop the host, so the main thread returns even if the script failed.
            let shutdown = handle.shutdown();
            result.and(shutdown.map_err(|e| e.to_string()))
        })
    };

    // The event loop owns the main thread until shutdown.
    let host_result = host.run();

    let script_result = driver.join().expect("driver thread panicked");
    running.store(false, Ordering::Relaxed);
    let ticks = session.join().expect("session thread panicked");
    host_result?;
    script_result?;
    println!(
        "[{:5.2}s] clean exit; the session ticked {ticks} times without interruption",
        start.elapsed().as_secs_f64()
    );
    Ok(())
}

fn script(handle: &HostHandle, events: &Receiver<HostEvent>, start: Instant) -> Result<(), String> {
    let windows = [
        ("openAC attach/detach: first window", Color::BLUE),
        ("openAC attach/detach: second window", Color::GREEN),
    ];
    for (i, (title, clear_color)) in windows.into_iter().enumerate() {
        if i > 0 {
            println!(
                "[{:5.2}s] no window for 2 s; the process keeps running headless",
                start.elapsed().as_secs_f64()
            );
            thread::sleep(Duration::from_secs(2));
        }

        handle
            .attach(WindowSpec {
                title: title.to_owned(),
                width: 800,
                height: 600,
                clear_color,
            })
            .map_err(|e| e.to_string())?;
        match expect(events)? {
            HostEvent::Attached(adapter) => println!(
                "[{:5.2}s] window {} attached on {:?} ({})",
                start.elapsed().as_secs_f64(),
                i + 1,
                adapter.backend,
                adapter.name
            ),
            other => return Err(format!("expected Attached, got {other:?}")),
        }

        thread::sleep(Duration::from_secs(2));

        handle.detach().map_err(|e| e.to_string())?;
        match expect(events)? {
            HostEvent::Detached {
                reason,
                frames_presented,
            } => {
                println!(
                    "[{:5.2}s] window {} detached ({reason:?}) after {frames_presented} frames",
                    start.elapsed().as_secs_f64(),
                    i + 1
                );
                if frames_presented == 0 {
                    return Err("the window presented no frames".to_owned());
                }
            }
            other => return Err(format!("expected Detached, got {other:?}")),
        }
    }
    Ok(())
}

fn expect(events: &Receiver<HostEvent>) -> Result<HostEvent, String> {
    events
        .recv_timeout(ANSWER_TIMEOUT)
        .map_err(|e| format!("no answer from the window host: {e}"))
}
