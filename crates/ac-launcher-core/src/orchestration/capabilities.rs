//! What this platform can launch, and the yes/no-with-a-reason answer every
//! launch question gets.

use std::time::Duration;

use crate::profiles::LaunchMode;

/// Whether something may be done now, and why not when it may not.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherCapability {
    /// Whether it may be done.
    pub is_available: bool,
    /// Why not, for the player.
    pub reason: Option<String>,
    /// How much longer the server may hold the account after an unclean
    /// exit, when that is why not (a window counts it down).
    pub held_for: Option<Duration>,
}

impl LauncherCapability {
    /// It may be done.
    pub const AVAILABLE: Self = Self {
        is_available: true,
        reason: None,
        held_for: None,
    };

    /// It may not be done, because `reason`.
    pub fn unavailable(reason: impl Into<String>) -> Self {
        Self {
            is_available: false,
            reason: Some(reason.into()),
            held_for: None,
        }
    }

    /// It may not be done for `remaining` more: the server may still hold
    /// the account.
    pub fn held(remaining: Duration) -> Self {
        let seconds = remaining.as_secs() + u64::from(remaining.subsec_nanos() > 0);
        Self {
            is_available: false,
            reason: Some(format!(
                "The server may still hold this account. Ready in {seconds} s."
            )),
            held_for: Some(remaining),
        }
    }
}

/// Why graphical launches are off on a platform the launcher does not know.
pub const UNSUPPORTED_PLATFORM_GRAPHICAL_LAUNCH_DISABLED_REASON: &str =
    "Graphical client launches are supported on Windows, Linux, and macOS.";

/// What the launcher may start on this operating system.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherPlatformCapabilities {
    /// Windows.
    pub is_windows: bool,
    /// Linux.
    pub is_linux: bool,
    /// macOS.
    pub is_macos: bool,
    /// Windowless sessions may run.
    pub can_run_headless: bool,
    /// The graphical client may run.
    pub can_launch_graphical_client: bool,
    /// The platform's name for the window.
    pub platform_name: String,
    /// Why graphical launches are off, when they are.
    pub graphical_launch_disabled_reason: Option<String>,
}

impl LauncherPlatformCapabilities {
    /// The capabilities of the operating system this was built for.
    pub fn detect() -> Self {
        Self::for_operating_system(
            cfg!(windows),
            cfg!(target_os = "linux"),
            cfg!(target_os = "macos"),
        )
    }

    /// The capabilities of the named operating system: both modes on
    /// Windows, Linux and macOS, none elsewhere.
    pub fn for_operating_system(is_windows: bool, is_linux: bool, is_macos: bool) -> Self {
        let supported = |name: &str, is_windows, is_linux, is_macos| Self {
            is_windows,
            is_linux,
            is_macos,
            can_run_headless: true,
            can_launch_graphical_client: true,
            platform_name: name.to_owned(),
            graphical_launch_disabled_reason: None,
        };
        if is_windows {
            return supported("Windows", true, false, false);
        }
        if is_linux {
            return supported("Linux", false, true, false);
        }
        if is_macos {
            return supported("macOS", false, false, true);
        }
        Self {
            is_windows: false,
            is_linux: false,
            is_macos: false,
            can_run_headless: false,
            can_launch_graphical_client: false,
            platform_name: "Unsupported".to_owned(),
            graphical_launch_disabled_reason: Some(
                UNSUPPORTED_PLATFORM_GRAPHICAL_LAUNCH_DISABLED_REASON.to_owned(),
            ),
        }
    }

    /// Whether `mode` may be launched here.
    pub fn for_launch_mode(&self, mode: LaunchMode) -> LauncherCapability {
        if mode == LaunchMode::Headless {
            return if self.can_run_headless {
                LauncherCapability::AVAILABLE
            } else {
                LauncherCapability::unavailable(
                    "Headless launches are supported only on Windows, Linux, and macOS.",
                )
            };
        }
        if self.can_launch_graphical_client {
            LauncherCapability::AVAILABLE
        } else {
            LauncherCapability::unavailable(
                self.graphical_launch_disabled_reason
                    .clone()
                    .unwrap_or_else(|| {
                        "The graphical client is unavailable on this platform.".to_owned()
                    }),
            )
        }
    }
}
