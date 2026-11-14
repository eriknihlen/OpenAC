//! Which windowing system the window host uses: on Linux, X11 or Wayland,
//! decided once before the event loop exists.

/// The operating system, for the decision.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostOs {
    /// Windows (Win32 windows).
    Windows,
    /// macOS (no target yet; the decision is fixed).
    MacOs,
    /// Linux and other Unix desktops.
    Linux,
}

impl HostOs {
    /// The system this program was built for.
    #[must_use]
    pub const fn current() -> Self {
        if cfg!(windows) {
            Self::Windows
        } else if cfg!(target_os = "macos") {
            Self::MacOs
        } else {
            Self::Linux
        }
    }
}

/// The windowing system asked for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DisplayProtocol {
    /// Win32.
    Windows,
    /// Cocoa.
    Cocoa,
    /// X11.
    X11,
    /// Wayland.
    Wayland,
    /// Whatever the platform layer finds.
    #[default]
    Automatic,
}

/// The decision and why, for the log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DisplaySelection {
    /// What is asked for.
    pub protocol: DisplayProtocol,
    /// Why.
    pub reason: String,
}

/// The environment variable that forces X11 or Wayland on Linux.
pub const DISPLAY_PROTOCOL_VAR: &str = "OPENAC_DISPLAY_PROTOCOL";

/// Decide the windowing system. On Linux `OPENAC_DISPLAY_PROTOCOL`
/// (`auto`, `x11`, `wayland`, any case) wins; otherwise a Wayland session
/// with a Wayland display uses Wayland, an X display uses X11, a Wayland
/// display alone uses Wayland, and with neither the platform layer
/// chooses. Any other value of the variable is an error naming the choices.
pub fn resolve_display(
    os: HostOs,
    env: impl Fn(&str) -> Option<String>,
) -> Result<DisplaySelection, String> {
    let selection = |protocol, reason: &str| DisplaySelection {
        protocol,
        reason: reason.to_owned(),
    };
    match os {
        HostOs::Windows => {
            return Ok(selection(
                DisplayProtocol::Windows,
                "Windows graphical host",
            ));
        }
        HostOs::MacOs => return Ok(selection(DisplayProtocol::Cocoa, "macOS graphical host")),
        HostOs::Linux => {}
    }
    let set = |name: &str| env(name).filter(|v| !v.trim().is_empty());
    if let Some(configured) = set(DISPLAY_PROTOCOL_VAR) {
        let reason = format!(
            "{DISPLAY_PROTOCOL_VAR}={}",
            configured.trim().to_lowercase()
        );
        return match configured.trim().to_lowercase().as_str() {
            "auto" => Ok(selection(DisplayProtocol::Automatic, &reason)),
            "x11" => Ok(selection(DisplayProtocol::X11, &reason)),
            "wayland" => Ok(selection(DisplayProtocol::Wayland, &reason)),
            _ => Err(format!(
                "{DISPLAY_PROTOCOL_VAR} must be auto, x11, or wayland; received '{configured}'."
            )),
        };
    }
    let wayland_display = set("WAYLAND_DISPLAY").is_some();
    if set("XDG_SESSION_TYPE").is_some_and(|t| t.eq_ignore_ascii_case("wayland")) && wayland_display
    {
        return Ok(selection(
            DisplayProtocol::Wayland,
            "XDG_SESSION_TYPE=wayland and WAYLAND_DISPLAY is set",
        ));
    }
    if set("DISPLAY").is_some() {
        return Ok(selection(DisplayProtocol::X11, "DISPLAY is set"));
    }
    if wayland_display {
        return Ok(selection(
            DisplayProtocol::Wayland,
            "WAYLAND_DISPLAY is set",
        ));
    }
    Ok(selection(
        DisplayProtocol::Automatic,
        "no X11 or Wayland environment was detected",
    ))
}

/// Apply the decision to the event loop builder (Linux only; elsewhere
/// there is one windowing system). On Windows the loop also takes a close
/// request only for the game window: see [`drops_message`].
pub(crate) fn apply<T>(
    builder: &mut winit::event_loop::EventLoopBuilder<T>,
    protocol: DisplayProtocol,
    game_window: &GameWindow,
) {
    #[cfg(windows)]
    {
        use winit::platform::windows::EventLoopBuilderExtWindows;
        let game_window = game_window.clone();
        builder.with_msg_hook(move |message| windows_drops_message(message, &game_window));
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        use winit::platform::wayland::EventLoopBuilderExtWayland;
        use winit::platform::x11::EventLoopBuilderExtX11;
        match protocol {
            DisplayProtocol::X11 => {
                builder.with_x11();
            }
            DisplayProtocol::Wayland => {
                builder.with_wayland();
            }
            _ => {}
        }
    }
    #[cfg(not(all(unix, not(target_os = "macos"))))]
    {
        let _ = (builder, protocol);
    }
    let _ = game_window;
}

/// The native handle of the game window while one is open (0 when none),
/// kept by the window host and read by the event loop's message hook, both
/// on the loop's thread.
#[derive(Debug, Clone, Default)]
pub(crate) struct GameWindow(std::rc::Rc<std::cell::Cell<isize>>);

impl GameWindow {
    /// The game window `window` is open now.
    pub(crate) fn opened(&self, window: &winit::window::Window) {
        self.0.set(native_handle(window));
    }

    /// No game window is open.
    pub(crate) fn closed(&self) {
        self.0.set(0);
    }

    #[cfg(windows)]
    fn get(&self) -> isize {
        self.0.get()
    }
}

/// A window's Win32 handle, or 0 elsewhere.
fn native_handle(window: &winit::window::Window) -> isize {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
    match window.window_handle().map(|handle| handle.as_raw()) {
        Ok(RawWindowHandle::Win32(handle)) => handle.hwnd.get(),
        _ => 0,
    }
}

/// The Win32 close request (`WM_CLOSE`).
pub const WM_CLOSE: u32 = 0x0010;

/// Whether the event loop drops a queued Win32 message for `window`
/// instead of handing it on: a close request for any window of the loop's
/// thread but the game window (`game_window`, 0 when none is open). Such a
/// request comes from outside the client: `taskkill` without `/F` posts one
/// to every top-level window of the process. The game window takes it as
/// the player's close. Of the others none is one a player can close, and
/// one is the loop's own hidden window, which would destroy itself: every
/// later wake of the loop (a window opening on a session, the shutdown when
/// the session has ended) would then go nowhere, and the process would
/// outlive its session with no window.
#[must_use]
pub fn drops_message(message: u32, window: isize, game_window: isize) -> bool {
    message == WM_CLOSE && window != 0 && window != game_window
}

/// [`drops_message`] for the message the loop is about to dispatch.
#[cfg(windows)]
#[allow(unsafe_code)]
fn windows_drops_message(message: *const std::ffi::c_void, game_window: &GameWindow) -> bool {
    use windows_sys::Win32::UI::WindowsAndMessaging::MSG;
    // SAFETY: the event loop calls this hook with a pointer to the live MSG
    // it has just taken from the thread's queue, and reads it only after
    // the hook returns.
    let message = unsafe { &*message.cast::<MSG>() };
    drops_message(message.message, message.hwnd as isize, game_window.get())
}

/// The windowing system a window actually runs on (an automatic choice
/// is only known once the window exists).
pub(crate) fn window_protocol(window: &winit::window::Window) -> DisplayProtocol {
    use winit::raw_window_handle::HasWindowHandle;
    window
        .window_handle()
        .map_or(DisplayProtocol::Automatic, |h| {
            protocol_of_handle(&h.as_raw())
        })
}

/// The windowing system of a native window handle.
fn protocol_of_handle(handle: &winit::raw_window_handle::RawWindowHandle) -> DisplayProtocol {
    use winit::raw_window_handle::RawWindowHandle;
    match handle {
        RawWindowHandle::Wayland(_) => DisplayProtocol::Wayland,
        RawWindowHandle::Xlib(_) | RawWindowHandle::Xcb(_) => DisplayProtocol::X11,
        RawWindowHandle::Win32(_) => DisplayProtocol::Windows,
        RawWindowHandle::AppKit(_) => DisplayProtocol::Cocoa,
        _ => DisplayProtocol::Automatic,
    }
}

/// The operating system's localized name of a key by scan code (the low
/// seven bits, and whether it is an extended key), for the key
/// configuration screen when the dat names none (OpenAC
/// `PlatformKeyNameProvider.ForCurrentProcess`: Windows only).
#[must_use]
pub fn key_name_provider() -> Option<Box<ac_ui::input::key_names::OsKeyName>> {
    #[cfg(windows)]
    {
        Some(Box::new(windows_key_name))
    }
    #[cfg(not(windows))]
    {
        None
    }
}

/// `GetKeyNameTextW` with the scan code in bits 16..23 and the extended
/// flag in bit 24 of its key parameter.
#[cfg(windows)]
#[allow(unsafe_code)]
fn windows_key_name(scan: u8, extended: bool) -> Option<String> {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::GetKeyNameTextW;
    let mut buffer = [0u16; 64];
    let parameter = (i32::from(scan) << 16) | if extended { 1 << 24 } else { 0 };
    // SAFETY: the buffer is a live, writable array of 64 UTF-16 units and
    // its length is passed as the limit; the call writes at most that many
    // units and returns how many it wrote.
    let length = unsafe { GetKeyNameTextW(parameter, buffer.as_mut_ptr(), 64) };
    let length = usize::try_from(length).ok().filter(|&n| n > 0)?;
    Some(String::from_utf16_lossy(
        &buffer[..length.min(buffer.len())],
    ))
}

/// The size in pixels of a monitor's desktop, or `None` when the system no
/// longer knows that monitor. Windows can drop a monitor while the display
/// is changing (a Remote Desktop connect or disconnect), and the windowing
/// library's own size query panics then.
#[must_use]
pub(crate) fn monitor_size(monitor: &winit::monitor::MonitorHandle) -> Option<(i32, i32)> {
    #[cfg(windows)]
    {
        use winit::platform::windows::MonitorHandleExtWindows;
        windows_monitor_size(monitor.hmonitor())
    }
    #[cfg(not(windows))]
    {
        let size = monitor.size();
        Some((
            i32::try_from(size.width).unwrap_or(0),
            i32::try_from(size.height).unwrap_or(0),
        ))
    }
}

/// `GetMonitorInfoW`'s monitor rectangle, as a width and a height.
#[cfg(windows)]
#[allow(unsafe_code)]
fn windows_monitor_size(hmonitor: isize) -> Option<(i32, i32)> {
    use windows_sys::Win32::Graphics::Gdi::{GetMonitorInfoW, MONITORINFO};
    let mut info = MONITORINFO {
        cbSize: u32::try_from(size_of::<MONITORINFO>()).ok()?,
        ..MONITORINFO::default()
    };
    // A monitor handle is an opaque number, not an address.
    let handle = std::ptr::without_provenance_mut(hmonitor as usize);
    // SAFETY: `info` is a live, writable MONITORINFO whose `cbSize` names
    // its own size; the call writes at most that many bytes, and an unknown
    // handle makes it fail without writing.
    let ok = unsafe { GetMonitorInfoW(handle, &raw mut info) };
    if ok == 0 {
        return None;
    }
    let rect = info.rcMonitor;
    Some((rect.right - rect.left, rect.bottom - rect.top))
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Platform/GraphicalWindowBackendSelectionTests.cs`.
    use super::*;

    fn env(vars: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
        let vars: Vec<(String, String)> = vars
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        move |name| vars.iter().find(|(k, _)| k == name).map(|(_, v)| v.clone())
    }

    /// Only a close request for a window other than the game window is
    /// dropped: the game window's close and every other message reach
    /// their windows.
    #[test]
    fn only_the_game_window_takes_a_close_request() {
        let (game, hidden) = (0x1234, 0x5678);
        assert!(!drops_message(WM_CLOSE, game, game), "the player's close");
        assert!(drops_message(WM_CLOSE, hidden, game));
        assert!(drops_message(WM_CLOSE, hidden, 0), "no game window open");
        assert!(!drops_message(WM_CLOSE, 0, 0), "a thread message");
        const WM_PAINT: u32 = 0x000F;
        assert!(!drops_message(WM_PAINT, hidden, game));
        assert!(!drops_message(WM_PAINT, game, game));
    }

    #[test]
    fn windows_always_selects_win32() {
        let s =
            resolve_display(HostOs::Windows, env(&[(DISPLAY_PROTOCOL_VAR, "wayland")])).unwrap();
        assert_eq!(s.protocol, DisplayProtocol::Windows);
    }

    #[test]
    fn an_explicit_linux_protocol_wins() {
        for (configured, expected) in [
            ("auto", DisplayProtocol::Automatic),
            ("x11", DisplayProtocol::X11),
            ("wayland", DisplayProtocol::Wayland),
            (" WAYLAND ", DisplayProtocol::Wayland),
        ] {
            let s = resolve_display(
                HostOs::Linux,
                env(&[
                    (DISPLAY_PROTOCOL_VAR, configured),
                    ("XDG_SESSION_TYPE", "x11"),
                    ("DISPLAY", ":0"),
                ]),
            )
            .unwrap();
            assert_eq!(s.protocol, expected, "{configured}");
        }
    }

    #[test]
    fn an_invalid_explicit_protocol_is_actionable() {
        let error =
            resolve_display(HostOs::Linux, env(&[(DISPLAY_PROTOCOL_VAR, "mir")])).unwrap_err();
        assert!(error.contains("auto, x11, or wayland"));
        assert!(error.contains("mir"));
    }

    #[test]
    fn the_linux_environment_selects_the_expected_protocol() {
        let cases: [(&[(&str, &str)], DisplayProtocol); 4] = [
            (
                &[
                    ("XDG_SESSION_TYPE", "wayland"),
                    ("WAYLAND_DISPLAY", "wayland-0"),
                    ("DISPLAY", ":0"),
                ],
                DisplayProtocol::Wayland,
            ),
            (
                &[
                    ("XDG_SESSION_TYPE", "x11"),
                    ("WAYLAND_DISPLAY", "wayland-0"),
                    ("DISPLAY", ":0"),
                ],
                DisplayProtocol::X11,
            ),
            (
                &[("WAYLAND_DISPLAY", "wayland-0")],
                DisplayProtocol::Wayland,
            ),
            (&[], DisplayProtocol::Automatic),
        ];
        for (vars, expected) in cases {
            assert_eq!(
                resolve_display(HostOs::Linux, env(vars)).unwrap().protocol,
                expected
            );
        }
    }

    #[test]
    fn a_window_handle_names_its_windowing_system() {
        use std::ptr::NonNull;
        use winit::raw_window_handle::{
            AppKitWindowHandle, RawWindowHandle, WaylandWindowHandle, Win32WindowHandle,
            XcbWindowHandle, XlibWindowHandle,
        };
        let surface = NonNull::<std::ffi::c_void>::dangling();
        assert_eq!(
            protocol_of_handle(&RawWindowHandle::Wayland(WaylandWindowHandle::new(surface))),
            DisplayProtocol::Wayland
        );
        assert_eq!(
            protocol_of_handle(&RawWindowHandle::Xlib(XlibWindowHandle::new(7))),
            DisplayProtocol::X11
        );
        assert_eq!(
            protocol_of_handle(&RawWindowHandle::Xcb(XcbWindowHandle::new(
                std::num::NonZeroU32::new(7).unwrap()
            ))),
            DisplayProtocol::X11
        );
        assert_eq!(
            protocol_of_handle(&RawWindowHandle::Win32(Win32WindowHandle::new(
                std::num::NonZeroIsize::new(7).unwrap()
            ))),
            DisplayProtocol::Windows
        );
        assert_eq!(
            protocol_of_handle(&RawWindowHandle::AppKit(AppKitWindowHandle::new(surface))),
            DisplayProtocol::Cocoa
        );
    }

    /// A monitor Windows no longer knows (here a handle that never named
    /// one) has no size rather than ending the client.
    #[cfg(windows)]
    #[test]
    fn a_monitor_the_system_no_longer_knows_has_no_size() {
        assert_eq!(windows_monitor_size(0), None);
        assert_eq!(windows_monitor_size(0x0bad_f00d), None);
    }
}
