//! `rawbytes`: logs the bytes of every event and callback the client hands
//! it, as they arrived and without decoding them (`event <hex>`,
//! `callback <hex>`), so a test can see exactly what a plugin built for an
//! older API version is sent. With each event it also logs the theme the
//! `ui_theme` import answers (`ui_theme <kind> <name>`). It registers a loot
//! classifier `rules` (answering nothing) so another plugin can ask it a
//! question. `/rawpaint <version>` paints a canvas with bytes it encodes itself,
//! as that API version knows them, and `/rawpanel <version>` registers a window
//! and a typed window the same way, so a test can see what the client reads from
//! a plugin built for an older version.
//!
//! Its exports are written here instead of by `export_plugin!`: the SDK's
//! own would decode the bytes as this SDK's records first.

use std::sync::atomic::{AtomicU32, Ordering};

use ac_plugin_abi::ui::{CanvasPaint, PaintCmd, PanelRegistration, TypedPanelRegistration};
use ac_plugin_abi::{ApiVersion, Wire};
use openac_plugin::ui::{CanvasSpec, Color};
use openac_plugin::{Host, Plugin, StartInfo};

/// The canvas `/rawpaint` paints.
static CANVAS: AtomicU32 = AtomicU32::new(0);

#[derive(Default)]
pub struct RawBytes;

impl Plugin for RawBytes {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        host.register_loot_classifier("rules", "Raw Rules")?;
        let canvas = host.register_canvas(&CanvasSpec::new("raw", 40, 40))?;
        CANVAS.store(canvas.id(), Ordering::Relaxed);
        host.register_command("rawpaint", "Paints the canvas as an API version (rawpaint 1.2)")?;
        host.register_command("rawpanel", "Registers windows as an API version (rawpanel 1.3)")?;
        Ok(())
    }
}

/// `/rawpaint <version>`: one line of text, encoded as that version knows it.
pub fn paint_as(version: &str) {
    let Some(version) = ApiVersion::parse(version.trim()) else {
        return;
    };
    let paint = CanvasPaint {
        canvas: CANVAS.load(Ordering::Relaxed),
        commands: vec![PaintCmd::Text(
            "raw".into(),
            1.0,
            2.0,
            Color::default(),
            false,
            0,
        )],
    };
    paint_raw(&paint.to_bytes_at(version));
}

/// `/rawpanel <version>`: a window and a typed window naming a dock icon, encoded
/// as that version knows them.
pub fn panels_as(version: &str) {
    let Some(version) = ApiVersion::parse(version.trim()) else {
        return;
    };
    let plain = PanelRegistration {
        window_id: "raw".into(),
        title: "Raw".into(),
        icon_text: None,
        icon_surface_id: 0,
        start_visible: false,
        show_in_side_panel: true,
        markup: "<panel/>".into(),
        binding_type: "Raw".into(),
        bindings: Vec::new(),
        icon_file: Some("raw.png".into()),
    };
    let typed = TypedPanelRegistration {
        window_id: "rawtyped".into(),
        title: "Raw Typed".into(),
        icon_text: None,
        icon_surface_id: 0,
        start_visible: false,
        show_in_side_panel: true,
        markup: "<panel/>".into(),
        binding_type: "Raw".into(),
        bindings: Vec::new(),
        item_types: Vec::new(),
        icon_file: Some("rawtyped.png".into()),
    };
    register_raw(&plain.to_bytes_at(version), &typed.to_bytes_at(version));
}

/// Registers with bytes as they are.
#[cfg(target_arch = "wasm32")]
#[allow(unsafe_code)]
fn register_raw(plain: &[u8], typed: &[u8]) {
    #[link(wasm_import_module = "openac")]
    unsafe extern "C" {
        fn ui_panel_register(ptr: *const u8, len: usize) -> i32;
        fn ui_panel_register_typed(ptr: *const u8, len: usize) -> i32;
    }
    // SAFETY: the client reads `len` bytes from `ptr`, which are each slice.
    let _ = unsafe { ui_panel_register(plain.as_ptr(), plain.len()) };
    // SAFETY: as above.
    let _ = unsafe { ui_panel_register_typed(typed.as_ptr(), typed.len()) };
}

#[cfg(not(target_arch = "wasm32"))]
fn register_raw(_plain: &[u8], _typed: &[u8]) {}

/// Paints with bytes as they are.
#[cfg(target_arch = "wasm32")]
#[allow(unsafe_code)]
fn paint_raw(bytes: &[u8]) {
    #[link(wasm_import_module = "openac")]
    unsafe extern "C" {
        fn ui_canvas_paint(ptr: *const u8, len: usize) -> i32;
    }
    // SAFETY: the client reads `len` bytes from `ptr`, which are `bytes`.
    let _ = unsafe { ui_canvas_paint(bytes.as_ptr(), bytes.len()) };
}

#[cfg(not(target_arch = "wasm32"))]
fn paint_raw(_bytes: &[u8]) {}

/// `bytes` as lower-case hex.
#[must_use]
pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(target_arch = "wasm32")]
#[allow(unsafe_code)]
mod exports {
    use std::cell::RefCell;

    use ac_plugin_abi::Wire;
    use openac_plugin::__export;
    use openac_plugin::{CallbackAnswer, Event, Host, LogLevel};

    use super::{RawBytes, hex, paint_as, panels_as};

    std::thread_local! {
        static PLUGIN: RefCell<Option<RawBytes>> = const { RefCell::new(None) };
        static ANSWER: RefCell<Vec<u8>> = const { RefCell::new(Vec::new()) };
    }

    #[unsafe(link_section = "openac.api")]
    #[used]
    static OPENAC_API: [u8; __export::API_SECTION_LEN] = __export::API_SECTION_BYTES;

    #[unsafe(link_section = "openac.layout")]
    #[used]
    static OPENAC_LAYOUT: [u8; __export::LAYOUT_SECTION_LEN] = __export::LAYOUT_SECTION_BYTES;

    /// The bytes the client wrote into a buffer `openac_alloc` gave it.
    fn take(ptr: i32, len: i32) -> Box<[u8]> {
        let len = usize::try_from(len).unwrap_or(0);
        let ptr = ptr as usize as *mut u8;
        // SAFETY: the client hands each export a buffer it got from
        // `openac_alloc` (the SDK's `alloc`, a boxed slice of this length),
        // exactly once.
        unsafe { Box::from_raw(core::ptr::slice_from_raw_parts_mut(ptr, len)) }
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_alloc(len: i32) -> i32 {
        __export::alloc(len)
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_enable(ptr: i32, len: i32) -> i64 {
        PLUGIN.with(|slot| __export::enable::<RawBytes>(slot, ptr, len))
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_disable() {
        PLUGIN.with(|slot| __export::disable::<RawBytes>(slot));
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_tick() {
        PLUGIN.with(|slot| __export::tick::<RawBytes>(slot));
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_event(ptr: i32, len: i32) {
        let bytes = take(ptr, len);
        let mut host = Host::wasm();
        host.log(LogLevel::Info, &format!("event {}", hex(&bytes)));
        let theme = host.theme();
        host.log(
            LogLevel::Info,
            &format!("ui_theme {:?} {}", theme.kind, theme.display_name),
        );
        if let Ok(Event::Command(call)) = Event::from_bytes(&bytes) {
            if call.verb == "rawpanel" {
                panels_as(&call.arguments);
            } else {
                paint_as(&call.arguments);
            }
        }
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn openac_callback(ptr: i32, len: i32) -> i64 {
        let bytes = take(ptr, len);
        Host::wasm().log(LogLevel::Info, &format!("callback {}", hex(&bytes)));
        ANSWER.with(|kept| {
            let mut kept = kept.borrow_mut();
            *kept = CallbackAnswer::Nothing.to_bytes();
            let ptr = kept.as_ptr() as usize as i64;
            let len = kept.len() as i64;
            (ptr << 32) | len
        })
    }
}
