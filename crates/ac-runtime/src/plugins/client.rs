//! API group L, the client window (OpenAC `IPluginClipboard`,
//! `IHostWindow`): what only an attached window can do, asked of it
//! through the session's window requests. With no window every call
//! answers false, as OpenAC's headless host does.

use crate::communication::WindowRequest;

use super::PluginBinding;

impl PluginBinding<'_> {
    fn ask_window(&mut self, request: WindowRequest) -> bool {
        if !self.window_attached {
            return false;
        }
        self.state.communication.request_window(request);
        true
    }

    pub(super) fn client_set_clipboard(&mut self, text: String) -> bool {
        self.ask_window(WindowRequest::SetClipboard(text))
    }

    pub(super) fn client_minimize(&mut self) -> bool {
        self.ask_window(WindowRequest::Minimize)
    }

    pub(super) fn client_restore(&mut self) -> bool {
        self.ask_window(WindowRequest::Restore)
    }

    /// As the window last reported it.
    pub(super) fn client_is_minimized(&self) -> bool {
        self.window_attached && self.state.plugin_services.window_minimized
    }
}
