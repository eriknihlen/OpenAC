//! What [`crate::export_plugin!`] expands to call. Not part of the SDK's
//! API.

use ac_plugin_abi::callback::Callback;
use ac_plugin_abi::{Event, StartInfo, Wire};

use crate::{Host, Plugin};

/// The length of the API version custom section.
pub const API_SECTION_LEN: usize = 4;

/// The API version custom section's bytes: [`ac_plugin_abi::API_CURRENT`]
/// as `MAJOR.MINOR`.
pub const API_SECTION_BYTES: [u8; API_SECTION_LEN] = *b"1.11";

/// The length of the layout fingerprint custom section.
pub const LAYOUT_SECTION_LEN: usize = ac_plugin_abi::layout::LAYOUT_SECTION_LEN;

/// The layout fingerprint custom section's bytes: the record shapes of the
/// ABI this SDK was built with ([`ac_plugin_abi::layout`]), which the
/// client compares with its own.
pub const LAYOUT_SECTION_BYTES: [u8; LAYOUT_SECTION_LEN] =
    ac_plugin_abi::layout::LAYOUT_SECTION_BYTES;

/// Starts a plugin from its encoded [`StartInfo`]; the error, if any, as a
/// UTF-8 message.
///
/// # Errors
/// The plugin's own refusal, or a start record that does not decode.
pub fn start<P: Plugin>(plugin: &mut P, host: &mut Host<'_>, start: &[u8]) -> Result<(), String> {
    let info = StartInfo::from_bytes(start)
        .map_err(|error| format!("the client's start record is malformed: {error}"))?;
    plugin.enable(host, &info)
}

/// Hands a plugin an encoded event.
///
/// # Panics
/// When the event does not decode: the contract is broken and the plugin
/// stops.
pub fn deliver<P: Plugin>(plugin: &mut P, host: &mut Host<'_>, bytes: &[u8]) {
    match Event::from_bytes(bytes) {
        Ok(event) => plugin.on_event(host, event),
        Err(error) => panic!("the client sent a malformed event: {error}"),
    }
}

/// Hands a plugin an encoded callback; its encoded answer.
///
/// # Panics
/// When the callback does not decode: the contract is broken.
#[must_use]
pub fn answer<P: Plugin>(plugin: &mut P, host: &mut Host<'_>, bytes: &[u8]) -> Vec<u8> {
    match Callback::from_bytes(bytes) {
        Ok(callback) => plugin.callback(host, callback).to_bytes(),
        Err(error) => panic!("the client sent a malformed callback: {error}"),
    }
}

#[cfg(target_arch = "wasm32")]
pub use wasm::*;

#[cfg(target_arch = "wasm32")]
#[allow(unsafe_code)]
mod wasm {
    use core::cell::RefCell;

    use ac_plugin_abi::Wire;

    use crate::{Host, Plugin};

    /// A buffer of `len` bytes for the client to fill; owned by whichever
    /// export it is handed to next.
    #[must_use]
    pub fn alloc(len: i32) -> i32 {
        let len = usize::try_from(len).unwrap_or(0);
        let buffer = vec![0u8; len].into_boxed_slice();
        Box::into_raw(buffer).cast::<u8>() as usize as i32
    }

    /// Takes back a buffer [`alloc`] gave out.
    fn take(ptr: i32, len: i32) -> Box<[u8]> {
        let len = usize::try_from(len).unwrap_or(0);
        let ptr = ptr as usize as *mut u8;
        // SAFETY: the client hands each export a buffer it got from
        // `alloc` with the same length, exactly once; `alloc` made it with
        // `Box::<[u8]>::into_raw` of that length.
        unsafe { Box::from_raw(core::ptr::slice_from_raw_parts_mut(ptr, len)) }
    }

    fn install_panic_report() {
        std::panic::set_hook(Box::new(|info| {
            crate::sys::report_panic(&info.to_string());
        }));
    }

    /// `openac_enable`.
    pub fn enable<P: Plugin>(slot: &RefCell<Option<P>>, ptr: i32, len: i32) -> i64 {
        install_panic_report();
        let start = take(ptr, len);
        let mut plugin = P::default();
        let mut host = Host::wasm();
        match super::start(&mut plugin, &mut host, &start) {
            Ok(()) => {
                *slot.borrow_mut() = Some(plugin);
                0
            }
            Err(message) => {
                // The plugin is unloaded after a refusal, so the message is
                // left for the client to read. It is never empty: 0 means
                // success.
                let message = if message.is_empty() {
                    "the plugin refused to start".to_owned()
                } else {
                    message
                };
                let message = message.into_bytes().into_boxed_slice();
                let len = message.len() as i64;
                let ptr = Box::into_raw(message).cast::<u8>() as usize as i64;
                (ptr << 32) | len
            }
        }
    }

    /// `openac_disable`.
    pub fn disable<P: Plugin>(slot: &RefCell<Option<P>>) {
        if let Some(mut plugin) = slot.borrow_mut().take() {
            plugin.disable(&mut Host::wasm());
        }
    }

    /// `openac_tick`.
    pub fn tick<P: Plugin>(slot: &RefCell<Option<P>>) {
        if let Some(plugin) = slot.borrow_mut().as_mut() {
            plugin.tick(&mut Host::wasm());
        }
    }

    /// `openac_event`.
    pub fn event<P: Plugin>(slot: &RefCell<Option<P>>, ptr: i32, len: i32) {
        let bytes = take(ptr, len);
        if let Some(plugin) = slot.borrow_mut().as_mut() {
            super::deliver(plugin, &mut Host::wasm(), &bytes);
        }
    }

    std::thread_local! {
        /// The last callback answer, kept until the next callback so the
        /// client can read it.
        static ANSWER: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    }

    /// `openac_callback`: `ptr << 32 | len` of the encoded answer.
    pub fn callback<P: Plugin>(slot: &RefCell<Option<P>>, ptr: i32, len: i32) -> i64 {
        let bytes = take(ptr, len);
        let answer = match slot.borrow_mut().as_mut() {
            Some(plugin) => super::answer(plugin, &mut Host::wasm(), &bytes),
            None => ac_plugin_abi::callback::CallbackAnswer::Nothing.to_bytes(),
        };
        ANSWER.with(|kept| {
            let mut kept = kept.borrow_mut();
            *kept = answer;
            let ptr = kept.as_ptr() as usize as i64;
            let len = kept.len() as i64;
            (ptr << 32) | len
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_api_section_is_the_current_api_version() {
        assert_eq!(
            std::str::from_utf8(&API_SECTION_BYTES).unwrap(),
            ac_plugin_abi::API_CURRENT.to_string()
        );
    }

    #[test]
    fn the_layout_section_is_the_abi_fingerprint() {
        // `export_plugin!` names the sections with literals (a link
        // section cannot take a constant): they must be the ABI's names.
        assert_eq!(ac_plugin_abi::API_SECTION, "openac.api");
        assert_eq!(ac_plugin_abi::LAYOUT_SECTION, "openac.layout");
        let layout = ac_plugin_abi::layout::PluginLayout::parse(&LAYOUT_SECTION_BYTES).unwrap();
        assert_eq!(layout.core, ac_plugin_abi::layout::CORE_SHAPE);
    }
}
