//! How `UdpDriver` waits, measured on loopback: how often an idle pump
//! loop wakes, and how long a datagram takes from `send_to` to the driver's
//! `recv` returning it. Nothing leaves the machine.
//!
//! The loop below waits the way `pump` does: up to `PUMP_WAIT` (10 ms) for
//! the first datagram, then 1 ms for each next one. The idle phase lasts
//! `IDLE` so the process's CPU time can be sampled from outside meanwhile:
//!
//! ```text
//! cargo run --release -p ac-net --example udp_wait_profile
//! ```

use std::net::{SocketAddr, UdpSocket};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use ac_net::{DriverOptions, UdpDriver};

/// The session's pump wait (`ac-runtime`'s `PUMP_WAIT`).
const PUMP_WAIT: Duration = Duration::from_millis(10);
/// The drain wait inside `pump`.
const DRAIN_WAIT: Duration = Duration::from_millis(1);
/// How long the idle phase runs.
const IDLE: Duration = Duration::from_secs(15);
/// Datagrams timed in the latency phase.
const SAMPLES: usize = 2_000;

/// One pass of `pump`'s receive loop; returns the datagrams it read.
fn pump_once(driver: &mut UdpDriver) -> Vec<Vec<u8>> {
    let deadline = Instant::now() + PUMP_WAIT;
    let mut timeout = PUMP_WAIT;
    let mut out = Vec::new();
    while let Some(datagram) = driver.recv(timeout).unwrap() {
        out.push(datagram);
        if Instant::now() >= deadline {
            break;
        }
        timeout = DRAIN_WAIT;
    }
    out
}

fn main() {
    let loopback: SocketAddr = "127.0.0.1:0".parse().unwrap();
    let mut options = DriverOptions::new("127.0.0.1:9000".parse().unwrap());
    options.bind = Some(loopback);
    let mut driver = UdpDriver::bind(&options).unwrap();
    let target = driver.local_addr().unwrap();

    // Idle: nothing arrives; count the pump passes.
    println!("pid {}: idle for {} s", std::process::id(), IDLE.as_secs());
    let started = Instant::now();
    let mut passes = 0u64;
    while started.elapsed() < IDLE {
        pump_once(&mut driver);
        passes += 1;
    }
    let seconds = started.elapsed().as_secs_f64();
    println!(
        "idle: {passes} passes in {seconds:.2} s = {:.1} passes/s, {:.2} ms each",
        passes as f64 / seconds,
        seconds * 1000.0 / passes as f64
    );

    // Latency: a datagram now and then, at a random phase of the wait.
    let origin = Instant::now();
    let sent_at = Arc::new(Mutex::new(vec![Duration::ZERO; SAMPLES]));
    let sender = {
        let sent_at = Arc::clone(&sent_at);
        thread::spawn(move || {
            let socket = UdpSocket::bind(loopback).unwrap();
            let mut state = 0x9E37_79B9u32;
            for sequence in 0..SAMPLES {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                thread::sleep(Duration::from_micros(u64::from(2_000 + state % 9_000)));
                sent_at.lock().unwrap()[sequence] = origin.elapsed();
                socket
                    .send_to(&(sequence as u32).to_le_bytes(), target)
                    .unwrap();
            }
        })
    };
    let mut latencies = Vec::with_capacity(SAMPLES);
    let mut last = Instant::now();
    while latencies.len() < SAMPLES && last.elapsed() < Duration::from_secs(2) {
        for datagram in pump_once(&mut driver) {
            let arrived = origin.elapsed();
            let sequence = u32::from_le_bytes(datagram[..4].try_into().unwrap()) as usize;
            let sent = sent_at.lock().unwrap()[sequence];
            latencies.push(arrived.saturating_sub(sent));
            last = Instant::now();
        }
    }
    sender.join().unwrap();
    latencies.sort();
    let at = |q: f64| latencies[((latencies.len() - 1) as f64 * q) as usize];
    println!(
        "latency over {} datagrams ({} lost): p50 {:?}, p99 {:?}, max {:?}",
        latencies.len(),
        SAMPLES - latencies.len(),
        at(0.5),
        at(0.99),
        at(1.0)
    );
}
