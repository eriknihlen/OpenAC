//! Typed [`Host`] methods for API group L: the client window. Every call
//! answers false with no window attached.

use ac_plugin_abi::calls;

use crate::Host;

/// L. The client window.
impl Host<'_> {
    /// Put text on the system clipboard. Needs the `clipboard` permission.
    pub fn set_clipboard(&mut self, text: &str) -> bool {
        self.call::<calls::ClientSetClipboard>(&text.to_owned())
    }

    /// Minimize the window.
    pub fn minimize_window(&mut self) -> bool {
        self.call::<calls::ClientMinimize>(&())
    }

    /// Restore the minimized window.
    pub fn restore_window(&mut self) -> bool {
        self.call::<calls::ClientRestore>(&())
    }

    /// The window is minimized.
    pub fn window_minimized(&mut self) -> bool {
        self.call::<calls::ClientIsMinimized>(&())
    }
}
