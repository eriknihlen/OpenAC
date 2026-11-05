//! The driver must not lose a datagram that the socket received, however
//! short the waits it is asked for.
//!
//! On Windows a UDP read that times out (SO_RCVTIMEO) can drop a datagram
//! arriving just as the timeout fires: a receiver looping 1 ms read timeouts
//! lost 2 and 8 of 20,000 small loopback datagrams in two runs, where a
//! non-blocking receiver lost none. Over a live server each such loss costs
//! a retransmit request and a stall of 600 ms or more. This test sends the
//! same kind of stream to a real `UdpDriver` (loopback only) and reads it the
//! way `pump` does: short waits, over and over.

use std::net::{SocketAddr, UdpSocket};
use std::thread;
use std::time::{Duration, Instant};

use ac_net::{DriverOptions, UdpDriver};

/// Datagrams in the stream.
const COUNT: u32 = 10_000;

/// A small deterministic generator for the irregular gaps (xorshift32).
struct Gaps(u32);

impl Gaps {
    /// The next gap: mostly a fraction of a millisecond, now and then about
    /// the length of the reader's 1 ms wait, so arrivals land on every
    /// phase of it.
    fn next(&mut self) -> Duration {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 17;
        self.0 ^= self.0 << 5;
        let micros = if self.0.is_multiple_of(8) {
            800 + self.0 % 700
        } else {
            self.0 % 400
        };
        Duration::from_micros(u64::from(micros))
    }
}

/// Busy-waits `gap` (a sleep is far too coarse for sub-millisecond gaps).
fn spin(gap: Duration) {
    let until = Instant::now() + gap;
    while Instant::now() < until {
        std::hint::spin_loop();
    }
}

#[test]
fn no_datagram_is_lost_at_short_waits() {
    let loopback: SocketAddr = "127.0.0.1:0".parse().unwrap();
    let mut options = DriverOptions::new("127.0.0.1:9000".parse().unwrap());
    options.bind = Some(loopback);
    let mut driver = UdpDriver::bind(&options).unwrap();
    let target = driver.local_addr().unwrap();

    let sender = thread::spawn(move || {
        let socket = UdpSocket::bind(loopback).unwrap();
        let mut gaps = Gaps(0x2545_F491);
        for sequence in 0..COUNT {
            socket.send_to(&sequence.to_le_bytes(), target).unwrap();
            spin(gaps.next());
        }
    });

    let mut seen = vec![false; COUNT as usize];
    let mut received = 0u32;
    let mut last_arrival = Instant::now();
    while received < COUNT && last_arrival.elapsed() < Duration::from_secs(2) {
        // What `pump` asks for while it drains: a 1 ms wait.
        if let Some(datagram) = driver.recv(Duration::from_millis(1)).unwrap() {
            let sequence = u32::from_le_bytes(datagram[..4].try_into().unwrap());
            assert!(!seen[sequence as usize], "datagram {sequence} came twice");
            seen[sequence as usize] = true;
            received += 1;
            last_arrival = Instant::now();
        }
    }
    sender.join().unwrap();

    let lost: Vec<usize> = seen
        .iter()
        .enumerate()
        .filter(|(_, seen)| !**seen)
        .map(|(sequence, _)| sequence)
        .collect();
    assert!(
        lost.is_empty(),
        "lost {} of {COUNT} datagrams: {lost:?}",
        lost.len()
    );
    assert_eq!(driver.counters().received, u64::from(COUNT));
}
