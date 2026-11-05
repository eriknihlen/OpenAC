//! Frame pacing: how fast the window draws.
//!
//! With vsync on, the display's refresh paces the frames. With vsync off the
//! window still never runs faster than the monitor refreshes: a software
//! limiter waits out the rest of each frame period. Only the explicit
//! uncapped diagnostic turns both off, for measuring the renderer.

use std::time::{Duration, Instant};

/// How frames are paced.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PacingPolicy {
    /// Present on the display's refresh.
    pub use_vsync: bool,
    /// Wait so frames come no faster than this many per second.
    pub software_limit_hz: Option<f64>,
}

/// The refresh rate assumed when the monitor does not report one.
pub const FALLBACK_REFRESH_HZ: f64 = 60.0;

impl PacingPolicy {
    /// The policy for a vsync preference, the uncapped diagnostic and the
    /// monitor's refresh rate.
    #[must_use]
    pub fn resolve(requested_vsync: bool, uncapped: bool, monitor_refresh_hz: Option<u32>) -> Self {
        if uncapped {
            return Self {
                use_vsync: false,
                software_limit_hz: None,
            };
        }
        if requested_vsync {
            return Self {
                use_vsync: true,
                software_limit_hz: None,
            };
        }
        let refresh = monitor_refresh_hz
            .filter(|hz| *hz > 0)
            .map_or(FALLBACK_REFRESH_HZ, f64::from);
        Self {
            use_vsync: false,
            software_limit_hz: Some(refresh),
        }
    }

    /// The swapchain present mode for this policy: wait for the refresh
    /// with vsync, else the lowest-latency mode the surface has.
    #[must_use]
    pub fn present_mode(&self) -> wgpu::PresentMode {
        if self.use_vsync {
            wgpu::PresentMode::AutoVsync
        } else {
            wgpu::PresentMode::AutoNoVsync
        }
    }
}

/// A monotonic clock.
pub trait PacingClock {
    /// Now.
    fn now(&mut self) -> Instant;
}

/// Something that blocks the thread for a while.
pub trait PacingWaiter {
    /// Block for about `duration`.
    fn wait(&mut self, duration: Duration);
}

/// The system clock.
#[derive(Debug, Default, Clone, Copy)]
pub struct SystemClock;

impl PacingClock for SystemClock {
    fn now(&mut self) -> Instant {
        Instant::now()
    }
}

/// Waits with the standard library's sleep, which uses a high-resolution
/// waitable timer on Windows and `nanosleep` elsewhere, so a 1 ms wait
/// lasts about 1 ms rather than a 15.6 ms scheduler tick.
#[derive(Debug, Default, Clone, Copy)]
pub struct SleepWaiter;

impl PacingWaiter for SleepWaiter {
    fn wait(&mut self, duration: Duration) {
        std::thread::sleep(duration);
    }
}

/// The software limiter: after each frame, wait until the next deadline.
/// A missed deadline starts a new schedule from now instead of running
/// catch-up frames.
#[derive(Debug)]
pub struct PacingController<C, W> {
    clock: C,
    waiter: W,
    policy: Option<PacingPolicy>,
    period: Option<Duration>,
    next_deadline: Option<Instant>,
}

impl Default for PacingController<SystemClock, SleepWaiter> {
    fn default() -> Self {
        Self::new(SystemClock, SleepWaiter)
    }
}

impl<C: PacingClock, W: PacingWaiter> PacingController<C, W> {
    /// A controller with no policy yet (it does not wait).
    pub fn new(clock: C, waiter: W) -> Self {
        Self {
            clock,
            waiter,
            policy: None,
            period: None,
            next_deadline: None,
        }
    }

    /// The policy in force.
    pub fn policy(&self) -> Option<PacingPolicy> {
        self.policy
    }

    /// Use `policy`. A change of rate starts the schedule again from now.
    pub fn apply(&mut self, policy: PacingPolicy) {
        if self.policy == Some(policy) {
            return;
        }
        self.policy = Some(policy);
        match policy.software_limit_hz {
            Some(hz) if !policy.use_vsync && hz.is_finite() && hz > 0.0 => {
                let period = Duration::from_secs_f64(1.0 / hz).max(Duration::from_nanos(1));
                self.period = Some(period);
                self.next_deadline = Some(self.clock.now() + period);
            }
            _ => {
                self.period = None;
                self.next_deadline = None;
            }
        }
    }

    /// The frame is done: wait for the deadline, then set the next one.
    /// Returns how long it waited.
    pub fn complete_frame(&mut self) -> Duration {
        let (Some(period), Some(deadline)) = (self.period, self.next_deadline) else {
            return Duration::ZERO;
        };
        let start = self.clock.now();
        let mut now = start;
        if now < deadline {
            while now < deadline {
                self.waiter.wait(deadline - now);
                now = self.clock.now();
            }
            let following = deadline + period;
            self.next_deadline = Some(if following > now {
                following
            } else {
                now + period
            });
        } else {
            self.next_deadline = Some(now + period);
        }
        now - start
    }
}

/// Pacing for one window: the vsync preference, the uncapped diagnostic
/// and the refresh rate of the monitor the window is on, resolved into one
/// policy that both the swapchain and the software limiter follow.
#[derive(Debug)]
pub struct DisplayPacing<C = SystemClock, W = SleepWaiter> {
    uncapped: bool,
    requested_vsync: bool,
    monitor_refresh_hz: Option<u32>,
    pacing: PacingController<C, W>,
}

impl DisplayPacing {
    /// Pacing with the system clock and sleep.
    #[must_use]
    pub fn new(requested_vsync: bool, uncapped: bool) -> Self {
        Self::with(requested_vsync, uncapped, PacingController::default())
    }
}

impl<C: PacingClock, W: PacingWaiter> DisplayPacing<C, W> {
    /// Pacing over a given controller; until a monitor is known the
    /// fallback refresh applies.
    pub fn with(requested_vsync: bool, uncapped: bool, pacing: PacingController<C, W>) -> Self {
        let mut this = Self {
            uncapped,
            requested_vsync,
            monitor_refresh_hz: None,
            pacing,
        };
        this.reapply();
        this
    }

    /// The policy in force.
    pub fn policy(&self) -> PacingPolicy {
        self.resolve()
    }

    fn resolve(&self) -> PacingPolicy {
        PacingPolicy::resolve(self.requested_vsync, self.uncapped, self.monitor_refresh_hz)
    }

    fn reapply(&mut self) -> PacingPolicy {
        let policy = self.resolve();
        self.pacing.apply(policy);
        policy
    }

    /// The player changed the vsync preference. Returns the new policy.
    pub fn set_vsync(&mut self, requested: bool) -> PacingPolicy {
        self.requested_vsync = requested;
        self.reapply()
    }

    /// The window moved to a monitor (or its mode changed): `None` when the
    /// monitor does not say. Returns the new policy.
    pub fn set_monitor_refresh(&mut self, refresh_hz: Option<u32>) -> PacingPolicy {
        self.monitor_refresh_hz = refresh_hz.filter(|hz| *hz > 0);
        self.reapply()
    }

    /// A frame was presented: wait out the rest of the period if software
    /// pacing is on. Returns how long it waited.
    pub fn frame_rendered(&mut self) -> Duration {
        self.pacing.complete_frame()
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/FramePacingPolicyTests.cs`,
    //! `FramePacingControllerTests.cs` and `DisplayFramePacingControllerTests.cs`.
    use super::*;
    use std::cell::RefCell;
    use std::rc::Rc;

    #[test]
    fn a_vsync_request_uses_the_display_without_a_software_limit() {
        let p = PacingPolicy::resolve(true, false, Some(144));
        assert!(p.use_vsync);
        assert_eq!(p.software_limit_hz, None);
        assert_eq!(p.present_mode(), wgpu::PresentMode::AutoVsync);
    }

    #[test]
    fn vsync_off_limits_to_the_monitor_refresh() {
        for hz in [60, 120, 144, 240] {
            let p = PacingPolicy::resolve(false, false, Some(hz));
            assert!(!p.use_vsync);
            assert_eq!(p.software_limit_hz, Some(f64::from(hz)));
            assert_eq!(p.present_mode(), wgpu::PresentMode::AutoNoVsync);
        }
    }

    #[test]
    fn a_missing_or_invalid_refresh_uses_the_fallback() {
        for hz in [None, Some(0)] {
            let p = PacingPolicy::resolve(false, false, hz);
            assert_eq!(p.software_limit_hz, Some(FALLBACK_REFRESH_HZ));
        }
    }

    #[test]
    fn the_uncapped_diagnostic_turns_off_both() {
        for vsync in [false, true] {
            let p = PacingPolicy::resolve(vsync, true, Some(144));
            assert!(!p.use_vsync);
            assert_eq!(p.software_limit_hz, None);
        }
    }

    /// A clock the test moves; the waiter moves it by what it was asked to
    /// wait plus an optional overshoot, and records each wait in ms.
    #[derive(Clone)]
    struct Fake {
        base: Instant,
        now_ms: Rc<RefCell<u64>>,
        waits: Rc<RefCell<Vec<u64>>>,
        overshoot_ms: Rc<RefCell<u64>>,
    }

    impl Fake {
        fn new() -> Self {
            Self {
                base: Instant::now(),
                now_ms: Rc::default(),
                waits: Rc::default(),
                overshoot_ms: Rc::default(),
            }
        }
        fn set(&self, ms: u64) {
            *self.now_ms.borrow_mut() = ms;
        }
        fn now_ms(&self) -> u64 {
            *self.now_ms.borrow()
        }
        fn waits(&self) -> Vec<u64> {
            self.waits.borrow().clone()
        }
        fn controller(&self) -> PacingController<Fake, Fake> {
            PacingController::new(self.clone(), self.clone())
        }
    }

    impl PacingClock for Fake {
        fn now(&mut self) -> Instant {
            self.base + Duration::from_millis(self.now_ms())
        }
    }

    impl PacingWaiter for Fake {
        fn wait(&mut self, duration: Duration) {
            let ms = duration.as_millis() as u64;
            self.waits.borrow_mut().push(ms);
            let overshoot = *self.overshoot_ms.borrow();
            *self.now_ms.borrow_mut() += ms + overshoot;
        }
    }

    fn limit(hz: f64) -> PacingPolicy {
        PacingPolicy {
            use_vsync: false,
            software_limit_hz: Some(hz),
        }
    }

    #[test]
    fn software_pacing_waits_only_for_the_rest_of_the_period() {
        let f = Fake::new();
        let mut c = f.controller();
        c.apply(limit(10.0));
        f.set(40);
        c.complete_frame();
        assert_eq!(f.waits(), [60]);
        f.set(150);
        c.complete_frame();
        assert_eq!(f.waits(), [60, 50]);
    }

    #[test]
    fn a_missed_deadline_rebases_instead_of_running_catch_up_frames() {
        let f = Fake::new();
        let mut c = f.controller();
        c.apply(limit(10.0));
        f.set(250);
        c.complete_frame();
        assert!(f.waits().is_empty());
        // The next deadline is 350, not one of the elapsed 100/200/300.
        f.set(300);
        c.complete_frame();
        assert_eq!(f.waits(), [50]);
    }

    #[test]
    fn a_wait_overshoot_rebases_the_next_deadline() {
        let f = Fake::new();
        let mut c = f.controller();
        c.apply(limit(10.0));
        *f.overshoot_ms.borrow_mut() = 150;
        c.complete_frame();
        assert_eq!(f.now_ms(), 250);
        *f.overshoot_ms.borrow_mut() = 0;
        f.set(300);
        c.complete_frame();
        assert_eq!(f.waits(), [100, 50]);
    }

    #[test]
    fn vsync_and_uncapped_policies_never_wait() {
        let f = Fake::new();
        let mut c = f.controller();
        c.apply(PacingPolicy::resolve(true, false, None));
        f.set(1000);
        c.complete_frame();
        c.apply(PacingPolicy::resolve(false, true, None));
        f.set(2000);
        c.complete_frame();
        assert!(f.waits().is_empty());
    }

    #[test]
    fn a_rate_change_restarts_the_schedule_from_now() {
        let f = Fake::new();
        let mut c = f.controller();
        c.apply(limit(10.0));
        f.set(25);
        c.apply(limit(20.0));
        f.set(50);
        c.complete_frame();
        assert_eq!(f.waits(), [25]);
    }

    #[test]
    fn display_pacing_uses_the_fallback_until_a_monitor_is_known() {
        let f = Fake::new();
        let mut d = DisplayPacing::with(false, false, f.controller());
        assert_eq!(d.policy().software_limit_hz, Some(FALLBACK_REFRESH_HZ));
        assert_eq!(
            d.set_monitor_refresh(Some(144)).software_limit_hz,
            Some(144.0)
        );
        // A monitor that stops reporting (a display hand-off) falls back.
        assert_eq!(
            d.set_monitor_refresh(None).software_limit_hz,
            Some(FALLBACK_REFRESH_HZ)
        );
    }

    #[test]
    fn display_pacing_resolves_preference_and_monitor_into_one_policy() {
        let f = Fake::new();
        let mut d = DisplayPacing::with(true, false, f.controller());
        d.set_monitor_refresh(Some(120));
        assert!(d.policy().use_vsync);
        let off = d.set_vsync(false);
        assert_eq!((off.use_vsync, off.software_limit_hz), (false, Some(120.0)));
        assert!(d.set_vsync(true).use_vsync);
    }

    #[test]
    fn explicit_uncapped_display_pacing_disables_both() {
        let f = Fake::new();
        let mut d = DisplayPacing::with(true, true, f.controller());
        d.set_monitor_refresh(Some(144));
        assert_eq!(d.policy(), PacingPolicy::resolve(false, true, None));
        f.set(1000);
        assert_eq!(d.frame_rendered(), Duration::ZERO);
    }

    #[test]
    fn display_pacing_delegates_the_wait_to_the_controller() {
        let f = Fake::new();
        let mut d = DisplayPacing::with(false, false, f.controller());
        d.set_monitor_refresh(Some(10));
        f.set(30);
        assert_eq!(d.frame_rendered(), Duration::from_millis(70));
    }
}
