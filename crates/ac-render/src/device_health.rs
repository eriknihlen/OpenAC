//! Whether a window's GPU device still works, and the one way the window
//! waits on it.
//!
//! A lost device (a driver reset, a GPU removed, a desktop that took the
//! GPU away) ends the window and never the process: the session the window
//! follows plays on without it. Two things make that hold. A device known to
//! be lost is never polled again, and a poll that is the first to meet the
//! loss is contained here: wgpu's `Device::poll` panics instead of
//! returning an error when the driver says the device is gone.

use std::cell::Cell;
use std::panic::{AssertUnwindSafe, catch_unwind, resume_unwind};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use crate::gpu::GpuFault;

/// The start of the panic message wgpu's `Device::poll` raises for an error
/// it does not return.
const POLL_PANIC: &str = "Error in Device::poll";

thread_local! {
    /// How many polls this thread is inside, for the crash report.
    static POLLING: Cell<u32> = const { Cell::new(0) };
}

/// GPU faults raised outside the frame (wgpu's error handler and device-lost
/// callback run on wgpu's terms), collected for the window to act on, and
/// whether the device is lost.
#[derive(Debug, Default)]
pub(crate) struct DeviceHealth {
    faults: Mutex<Vec<GpuFault>>,
    /// Why the device was lost, once it is.
    lost: Mutex<Option<String>>,
    /// The window is ending its own device.
    releasing: AtomicBool,
}

impl DeviceHealth {
    /// Watch `device`: its uncaptured errors and its loss land here.
    pub(crate) fn watch(device: &wgpu::Device) -> Arc<Self> {
        let health = Arc::new(Self::default());
        let errors = Arc::clone(&health);
        device.on_uncaptured_error(Arc::new(move |error: wgpu::Error| {
            errors.push(GpuFault::from_wgpu(&error));
        }));
        let lost = Arc::clone(&health);
        device.set_device_lost_callback(move |reason, message| {
            // `Destroyed` is the device being ended on purpose: by the
            // window while it closes, which is no fault; by anyone else, the
            // device is gone under the window like any other loss.
            if reason == wgpu::DeviceLostReason::Destroyed && lost.releasing.load(Ordering::Acquire)
            {
                return;
            }
            let message = if message.is_empty() {
                "the device was destroyed".to_owned()
            } else {
                message
            };
            lost.push(GpuFault::DeviceLost(message));
        });
        health
    }

    /// A fault for the window; a lost device is remembered as lost.
    pub(crate) fn push(&self, fault: GpuFault) {
        if let GpuFault::DeviceLost(why) = &fault {
            self.lost
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .get_or_insert_with(|| why.clone());
        }
        self.faults
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .push(fault);
    }

    pub(crate) fn drain(&self) -> Vec<GpuFault> {
        std::mem::take(&mut *self.faults.lock().unwrap_or_else(PoisonError::into_inner))
    }

    /// The loss, when the device is known to be lost.
    pub(crate) fn lost(&self) -> Option<GpuFault> {
        self.lost
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
            .map(GpuFault::DeviceLost)
    }

    /// The window is ending its device: a `Destroyed` report from now on is
    /// its own doing.
    pub(crate) fn releasing(&self) {
        self.releasing.store(true, Ordering::Release);
    }

    /// Poll `device`: a device known to be lost is not polled (the loss is
    /// returned), and a poll that meets the loss returns it.
    pub(crate) fn poll(
        &self,
        device: &wgpu::Device,
        poll_type: wgpu::PollType,
    ) -> Result<wgpu::PollStatus, GpuFault> {
        self.poll_with(poll_type, |p| device.poll(p))
    }

    fn poll_with(
        &self,
        poll_type: wgpu::PollType,
        poll: impl FnOnce(wgpu::PollType) -> Result<wgpu::PollStatus, wgpu::PollError>,
    ) -> Result<wgpu::PollStatus, GpuFault> {
        if let Some(lost) = self.lost() {
            return Err(lost);
        }
        // wgpu 30's `Device::poll` returns only a timeout or a wrong
        // submission index as an error; for a device error (the driver
        // saying the device is lost, out of memory, or in an unexpected
        // state) it panics. wgpu marks the device lost before that panic,
        // so it is a lost device, not a defect: it is caught here, at the
        // poll alone, and becomes the window's `DeviceLost` fault. Any other
        // panic goes on.
        POLLING.with(|n| n.set(n.get() + 1));
        let outcome = catch_unwind(AssertUnwindSafe(|| poll(poll_type)));
        POLLING.with(|n| n.set(n.get() - 1));
        let polled = match outcome {
            Ok(polled) => polled,
            Err(payload) => match panic_text(&*payload) {
                Some(text) if text.starts_with(POLL_PANIC) => {
                    let fault = GpuFault::DeviceLost(one_line(text));
                    self.push(fault.clone());
                    return Err(fault);
                }
                _ => resume_unwind(payload),
            },
        };
        // The poll may have been where wgpu reported the loss.
        if let Some(lost) = self.lost() {
            return Err(lost);
        }
        polled.map_err(|e| GpuFault::Internal(format!("waiting for the GPU: {e}")))
    }
}

/// Whether the panic being reported is a lost device's that a poll here
/// contains: the crash report and the panic message leave it out, since the
/// window reports the loss itself and the process goes on.
#[must_use]
pub fn contained_device_loss(payload: &(dyn std::any::Any + Send)) -> bool {
    POLLING.with(Cell::get) > 0 && panic_text(payload).is_some_and(|t| t.starts_with(POLL_PANIC))
}

fn panic_text(payload: &(dyn std::any::Any + Send)) -> Option<&str> {
    payload
        .downcast_ref::<String>()
        .map(String::as_str)
        .or_else(|| payload.downcast_ref::<&'static str>().copied())
}

/// wgpu's multi-line error tree on one line.
fn one_line(text: &str) -> String {
    text.lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
        .collect::<Vec<_>>()
        .join(" / ")
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::gpu;
    use std::panic::{AssertUnwindSafe, catch_unwind};

    /// What wgpu's `Device::poll` panics with when the driver reports the
    /// device lost (a game window opened on a headless session, 2026-10-24).
    fn wgpu_poll_panic() -> String {
        format!(
            "Error in {}: {}",
            "Device::poll", "Validation Error\n\nCaused by:\n  Parent device is lost\n"
        )
    }

    fn lost(result: &Result<wgpu::PollStatus, GpuFault>) -> bool {
        matches!(result, Err(GpuFault::DeviceLost(_)))
    }

    #[test]
    fn a_device_ended_under_the_window_is_lost_and_never_polled_again() {
        let Some((device, _queue)) = crate::test_gpu::own_device() else {
            return;
        };
        let health = DeviceHealth::watch(&device);
        device.destroy();
        // wgpu reports the loss the next time it looks at the device.
        let polled = health.poll(&device, wgpu::PollType::wait_indefinitely());
        assert!(lost(&polled), "{polled:?}");
        assert!(health.lost().is_some());
        let faults = health.drain();
        assert!(
            faults
                .iter()
                .any(|f| matches!(f, GpuFault::DeviceLost(_)) && gpu::is_fatal(f)),
            "the window closes: {faults:?}"
        );
        // Known lost: nothing reaches the device any more.
        let again = health.poll_with(wgpu::PollType::wait_indefinitely(), |_| {
            panic!("a lost device was polled")
        });
        assert!(lost(&again), "{again:?}");
    }

    #[test]
    fn the_window_ending_its_own_device_is_not_a_loss() {
        let Some((device, _queue)) = crate::test_gpu::own_device() else {
            return;
        };
        let health = DeviceHealth::watch(&device);
        health.releasing();
        device.destroy();
        let polled = health.poll(&device, wgpu::PollType::wait_indefinitely());
        assert!(polled.is_ok(), "{polled:?}");
        assert!(health.lost().is_none());
        assert!(health.drain().is_empty());
    }

    #[test]
    fn a_poll_that_panics_on_a_lost_device_is_a_device_lost_fault() {
        let health = DeviceHealth::default();
        let polled = health.poll_with(wgpu::PollType::Poll, |_| panic!("{}", wgpu_poll_panic()));
        match &polled {
            Err(GpuFault::DeviceLost(why)) => {
                assert!(why.contains("Parent device is lost"), "{why}");
            }
            other => panic!("{other:?}"),
        }
        assert!(health.lost().is_some());
        assert!(
            health
                .drain()
                .iter()
                .any(|f| matches!(f, GpuFault::DeviceLost(_)))
        );
        let again = health.poll_with(wgpu::PollType::wait_indefinitely(), |_| {
            panic!("a lost device was polled")
        });
        assert!(lost(&again), "{again:?}");
    }

    #[test]
    fn any_other_panic_in_a_poll_is_not_taken_for_a_lost_device() {
        let health = DeviceHealth::default();
        let outcome = catch_unwind(AssertUnwindSafe(|| {
            health.poll_with(wgpu::PollType::Poll, |_| panic!("a bug of ours"))
        }));
        assert!(outcome.is_err(), "the panic goes on");
        assert!(health.lost().is_none());
    }

    #[test]
    fn the_crash_report_leaves_out_only_a_loss_a_poll_contains() {
        let wgpu_payload: Box<dyn std::any::Any + Send> = Box::new(wgpu_poll_panic());
        let other: Box<dyn std::any::Any + Send> = Box::new("a bug of ours");
        assert!(!contained_device_loss(&*wgpu_payload), "outside a poll");
        let mut inside = (false, true);
        let health = DeviceHealth::default();
        let _ = health.poll_with(wgpu::PollType::Poll, |_| {
            inside = (
                contained_device_loss(&*wgpu_payload),
                contained_device_loss(&*other),
            );
            Ok(wgpu::PollStatus::QueueEmpty)
        });
        assert_eq!(inside, (true, false));
    }
}
