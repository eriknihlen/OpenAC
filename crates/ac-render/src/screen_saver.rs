//! Keeping the screen awake while the window is fullscreen, as OpenAC's
//! window library does.
//!
//! - Wayland: the window's surface carries an idle inhibitor, so the
//!   compositor neither blanks the screen nor starts its screen saver while
//!   the surface is shown.
//! - X11: the X server's screen saver is turned off, and its settings are
//!   put back when the window leaves fullscreen or closes.
//! - Windows: the windowing layer already refuses the screen saver for a
//!   fullscreen window; nothing is done here.
//!
//! What holds the screen uses the window's own display connection, so it is
//! let go while that connection is open: when the window closes, or when
//! the event loop ends ([`ScreenSaver::release`]). An X11 hold keeps the
//! window, and with it the connection, alive. On Wayland the event loop and
//! each of its windows keep the connection open until the last of them is
//! dropped; a Wayland hold is still left alone (not destroyed) when dropped
//! during a panic, since the order of that unwinding is not ours to rely on.

use std::sync::Arc;

use winit::window::Window;

/// Keeps the screen awake while its window is fullscreen; dropped, it lets
/// go.
#[derive(Debug, Default)]
pub(crate) struct ScreenSaver {
    #[cfg(all(unix, not(target_os = "macos")))]
    platform: linux::Platform,
}

impl ScreenSaver {
    /// Follow `window`'s fullscreen state: hold the screen awake while it
    /// is fullscreen, let go when it is not. Lines for the log go to `log`.
    pub(crate) fn follow(&mut self, window: &Arc<Window>, log: &mut Vec<String>) {
        let fullscreen = window.fullscreen().is_some();
        #[cfg(all(unix, not(target_os = "macos")))]
        self.platform.keep_awake(window, fullscreen, log);
        #[cfg(not(all(unix, not(target_os = "macos"))))]
        let _ = (fullscreen, log);
    }

    /// Let go now, and of everything reached on the window's display
    /// connection (the event loop is ending, the connection still open).
    pub(crate) fn release(&mut self) {
        *self = Self::default();
    }
}

#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// The X server's screen-saver settings (`XGetScreenSaver`'s four values).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct SaverSettings {
    /// Seconds of idle time before the saver starts; 0 is off.
    pub(crate) timeout: i32,
    /// Seconds between the saver's pattern changes.
    pub(crate) interval: i32,
    /// Whether the saver blanks the screen.
    pub(crate) prefer_blanking: i32,
    /// Whether the saver may use exposures.
    pub(crate) allow_exposures: i32,
}

#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// X11's `DontPreferBlanking`.
const DONT_PREFER_BLANKING: i32 = 0;
#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// X11's `DefaultExposures`.
const DEFAULT_EXPOSURES: i32 = 2;

#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// The settings that keep the screen saver off while fullscreen: no
/// timeout, and the server's defaults for the rest.
#[must_use]
pub(crate) const fn saver_off() -> SaverSettings {
    SaverSettings {
        timeout: 0,
        interval: 0,
        prefer_blanking: DONT_PREFER_BLANKING,
        allow_exposures: DEFAULT_EXPOSURES,
    }
}

#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// What to do with the hold, from whether one is held and whether the
/// window is fullscreen; `refused` is set once holding failed, so a window
/// whose system cannot keep the screen awake is not asked again on every
/// resize.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Step {
    /// Take the hold.
    Hold,
    /// Let it go.
    Release,
    /// Nothing changes.
    Stay,
}

#[cfg(any(test, all(unix, not(target_os = "macos"))))]
/// The step for a hold `held`, a window `fullscreen`, holding `refused`.
#[must_use]
pub(crate) const fn step(held: bool, fullscreen: bool, refused: bool) -> Step {
    match (held, fullscreen) {
        (false, true) if !refused => Step::Hold,
        (true, false) => Step::Release,
        _ => Step::Stay,
    }
}

// Platform glue: the window's own display connection, through raw handles.
#[allow(unsafe_code)]
#[cfg(all(unix, not(target_os = "macos")))]
mod linux {
    use std::ffi::c_void;
    use std::ptr::NonNull;
    use std::sync::Arc;

    use winit::raw_window_handle::{
        HasDisplayHandle, HasWindowHandle, RawDisplayHandle, RawWindowHandle,
    };
    use winit::window::Window;

    use super::{Step, step};

    /// What holds the screen awake; letting go is dropping it.
    enum Hold {
        /// An idle inhibitor on the window's surface.
        Wayland { _inhibitor: wayland::Inhibitor },
        /// The X server's screen saver turned off.
        X11 { _saver_off: Box<x11::SaverOff> },
    }

    impl std::fmt::Debug for Hold {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str(match self {
                Self::Wayland { .. } => "Hold::Wayland",
                Self::X11 { .. } => "Hold::X11",
            })
        }
    }

    /// The hold and what is needed to take it again.
    #[derive(Default)]
    pub(super) struct Platform {
        // Drop order: the hold goes before the manager it came from.
        hold: Option<Hold>,
        /// The compositor's idle-inhibit manager, reached on first use.
        wayland: Option<wayland::Manager>,
        /// Holding failed once; not asked again for this window.
        refused: bool,
    }

    impl std::fmt::Debug for Platform {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.debug_struct("Platform")
                .field("hold", &self.hold)
                .field("wayland", &self.wayland.is_some())
                .field("refused", &self.refused)
                .finish()
        }
    }

    impl Platform {
        pub(super) fn keep_awake(
            &mut self,
            window: &Arc<Window>,
            fullscreen: bool,
            log: &mut Vec<String>,
        ) {
            match step(self.hold.is_some(), fullscreen, self.refused) {
                Step::Stay => {}
                Step::Release => self.hold = None,
                Step::Hold => match self.take(window) {
                    Ok(hold) => self.hold = Some(hold),
                    Err(error) => {
                        self.refused = true;
                        log.push(format!(
                            "display: the screen may blank while fullscreen ({error})"
                        ));
                    }
                },
            }
        }

        fn take(&mut self, window: &Arc<Window>) -> Result<Hold, String> {
            let display = window.display_handle().map_err(|e| e.to_string())?.as_raw();
            let surface = window.window_handle().map_err(|e| e.to_string())?.as_raw();
            match (display, surface) {
                (RawDisplayHandle::Wayland(display), RawWindowHandle::Wayland(surface)) => {
                    if self.wayland.is_none() {
                        // SAFETY: the display is the window's own connection,
                        // open now (the event loop is running). It stays open
                        // while the event loop or any of its windows is alive;
                        // the manager is dropped before both
                        // (`ScreenSaver::release` on the loop's end, or with
                        // the window), and is left alone when dropped during
                        // a panic.
                        self.wayland = Some(unsafe { wayland::Manager::new(display.display) }?);
                    }
                    let manager = self.wayland.as_mut().ok_or("no idle-inhibit manager")?;
                    // SAFETY: the surface is the window's live surface; it
                    // is used only for this request.
                    unsafe { manager.inhibit(surface.surface) }.map(|inhibitor| Hold::Wayland {
                        _inhibitor: inhibitor,
                    })
                }
                (RawDisplayHandle::Xlib(display), _) => {
                    let display: NonNull<c_void> = display.display.ok_or("no X display")?;
                    // SAFETY: the display is the window's own X connection;
                    // the window holds it open, and the hold holds the
                    // window.
                    unsafe { x11::SaverOff::new(display, Arc::clone(window)) }.map(|saver_off| {
                        Hold::X11 {
                            _saver_off: Box::new(saver_off),
                        }
                    })
                }
                _ => Err("this windowing system has no screen-saver control here".into()),
            }
        }
    }

    mod wayland {
        use std::ffi::c_void;
        use std::ptr::NonNull;

        use wayland_client::backend::{Backend, ObjectId};
        use wayland_client::globals::{GlobalListContents, registry_queue_init};
        use wayland_client::protocol::{wl_registry, wl_surface::WlSurface};
        use wayland_client::{Connection, Dispatch, EventQueue, Proxy, QueueHandle, delegate_noop};
        use wayland_protocols::wp::idle_inhibit::zv1::client::zwp_idle_inhibit_manager_v1::ZwpIdleInhibitManagerV1;
        use wayland_protocols::wp::idle_inhibit::zv1::client::zwp_idle_inhibitor_v1::ZwpIdleInhibitorV1;

        /// The event handler of this connection's queue (nothing it
        /// receives matters).
        pub(super) struct Events;

        impl Dispatch<wl_registry::WlRegistry, GlobalListContents> for Events {
            fn event(
                _: &mut Self,
                _: &wl_registry::WlRegistry,
                _: wl_registry::Event,
                _: &GlobalListContents,
                _: &Connection,
                _: &QueueHandle<Self>,
            ) {
            }
        }

        delegate_noop!(Events: ignore ZwpIdleInhibitManagerV1);
        delegate_noop!(Events: ignore ZwpIdleInhibitorV1);

        /// The compositor's idle-inhibit manager, on an event queue of its
        /// own on the window's display connection.
        pub(super) struct Manager {
            connection: Connection,
            queue: EventQueue<Events>,
            manager: ZwpIdleInhibitManagerV1,
        }

        impl Manager {
            /// # Safety
            /// `display` is a live `wl_display` that stays connected for
            /// longer than the manager and its inhibitors live.
            pub(super) unsafe fn new(display: NonNull<c_void>) -> Result<Self, String> {
                // SAFETY: by the caller; the connection is not ours and is
                // not closed when this one is dropped.
                let backend = unsafe { Backend::from_foreign_display(display.as_ptr().cast()) };
                let connection = Connection::from_backend(backend);
                let (globals, queue) =
                    registry_queue_init::<Events>(&connection).map_err(|e| e.to_string())?;
                let manager = globals
                    .bind::<ZwpIdleInhibitManagerV1, _, _>(&queue.handle(), 1..=1, ())
                    .map_err(|_| "the compositor has no idle-inhibit protocol".to_owned())?;
                Ok(Self {
                    connection,
                    queue,
                    manager,
                })
            }

            /// An idle inhibitor on `surface`.
            ///
            /// # Safety
            /// `surface` is a live `wl_surface` of this manager's display.
            pub(super) unsafe fn inhibit(
                &mut self,
                surface: NonNull<c_void>,
            ) -> Result<Inhibitor, String> {
                // Registry news since the last request is not used.
                let _ = self.queue.dispatch_pending(&mut Events);
                // SAFETY: by the caller.
                let id =
                    unsafe { ObjectId::from_ptr(WlSurface::interface(), surface.as_ptr().cast()) }
                        .map_err(|e| e.to_string())?;
                let surface =
                    WlSurface::from_id(&self.connection, id).map_err(|e| e.to_string())?;
                let inhibitor = self
                    .manager
                    .create_inhibitor(&surface, &self.queue.handle(), ());
                self.connection.flush().map_err(|e| e.to_string())?;
                Ok(Inhibitor {
                    connection: self.connection.clone(),
                    inhibitor,
                })
            }
        }

        impl Drop for Manager {
            fn drop(&mut self) {
                if std::thread::panicking() {
                    leave_alone(&self.connection);
                    return;
                }
                self.manager.destroy();
                let _ = self.connection.flush();
            }
        }

        /// During a panic the event loop's display connection may already
        /// be closed, so nothing on it is touched: this connection is kept
        /// (never dropped), and its objects are not destroyed.
        fn leave_alone(connection: &Connection) {
            std::mem::forget(connection.clone());
        }

        /// The screen kept awake while the surface is shown; dropped, the
        /// inhibitor is destroyed.
        pub(super) struct Inhibitor {
            connection: Connection,
            inhibitor: ZwpIdleInhibitorV1,
        }

        impl Drop for Inhibitor {
            fn drop(&mut self) {
                if std::thread::panicking() {
                    leave_alone(&self.connection);
                    return;
                }
                self.inhibitor.destroy();
                let _ = self.connection.flush();
            }
        }
    }

    mod x11 {
        use std::ffi::c_void;
        use std::ptr::NonNull;
        use std::sync::Arc;

        use winit::window::Window;
        use x11_dl::xlib::{Display, Xlib};

        use crate::screen_saver::{SaverSettings, saver_off};

        /// The X server's screen saver turned off; dropped, its settings
        /// from before are put back.
        pub(super) struct SaverOff {
            xlib: Xlib,
            display: NonNull<Display>,
            saved: SaverSettings,
            /// The window, which holds `display` open.
            _window: Arc<Window>,
        }

        impl SaverOff {
            /// # Safety
            /// `display` is `window`'s X display connection (which the
            /// window holds open).
            pub(super) unsafe fn new(
                display: NonNull<c_void>,
                window: Arc<Window>,
            ) -> Result<Self, String> {
                let xlib = Xlib::open().map_err(|e| e.to_string())?;
                let display = display.cast::<Display>();
                let (mut timeout, mut interval, mut prefer_blanking, mut allow_exposures) =
                    (0, 0, 0, 0);
                // SAFETY: `display` is live (by the caller) and the four
                // out-pointers are valid locals.
                unsafe {
                    (xlib.XGetScreenSaver)(
                        display.as_ptr(),
                        &raw mut timeout,
                        &raw mut interval,
                        &raw mut prefer_blanking,
                        &raw mut allow_exposures,
                    );
                }
                let saved = SaverSettings {
                    timeout,
                    interval,
                    prefer_blanking,
                    allow_exposures,
                };
                let held = Self {
                    xlib,
                    display,
                    saved,
                    _window: window,
                };
                held.set(saver_off());
                Ok(held)
            }

            fn set(&self, settings: SaverSettings) {
                // SAFETY: the display is open while `self` holds the window
                // (see `new`).
                unsafe {
                    (self.xlib.XSetScreenSaver)(
                        self.display.as_ptr(),
                        settings.timeout,
                        settings.interval,
                        settings.prefer_blanking,
                        settings.allow_exposures,
                    );
                    (self.xlib.XFlush)(self.display.as_ptr());
                }
            }
        }

        impl Drop for SaverOff {
            fn drop(&mut self) {
                self.set(self.saved);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_hold_follows_fullscreen() {
        assert_eq!(step(false, true, false), Step::Hold);
        assert_eq!(step(true, true, false), Step::Stay);
        assert_eq!(step(true, false, false), Step::Release);
        assert_eq!(step(false, false, false), Step::Stay);
    }

    /// A system that cannot keep the screen awake is told once, not on
    /// every resize, and a hold already taken is still let go.
    #[test]
    fn a_refused_hold_is_not_asked_again() {
        assert_eq!(step(false, true, true), Step::Stay);
        assert_eq!(step(true, false, true), Step::Release);
    }

    /// The X11 values that turn the saver off: no timeout, no blanking
    /// preference, the default exposures (`XSetScreenSaver(0, 0,
    /// DontPreferBlanking, DefaultExposures)`).
    #[test]
    fn the_x11_saver_off_has_no_timeout() {
        assert_eq!(
            saver_off(),
            SaverSettings {
                timeout: 0,
                interval: 0,
                prefer_blanking: 0,
                allow_exposures: 2,
            }
        );
    }
}
