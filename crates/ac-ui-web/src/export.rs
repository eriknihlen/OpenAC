//! The exports a page calls. Each one reads its arguments out of the
//! module's memory and calls [`Module`]; nothing is decided here.
//!
//! The page writes input into buffers it got from `ui_alloc` and passes
//! their address and length. Outputs are read from a buffer the module
//! owns: `ui_take`, `ui_shape` and `ui_dat_preload` return its address and
//! `ui_take_len` its length, valid until the next of those calls.

#![allow(unsafe_code)]

use std::cell::RefCell;

use crate::client::{ClientPanels, page_duration};
use crate::host::{Modifiers, Module, Panels, Session};

thread_local! {
    /// The one module of this instance. A page calls one export at a time.
    static MODULE: RefCell<Module<ClientPanels>> = RefCell::new(Module::new(ClientPanels::new()));
}

fn with<R>(call: impl FnOnce(&mut Module<ClientPanels>) -> R) -> R {
    MODULE.with(|module| call(&mut module.borrow_mut()))
}

/// The page's time `now` (milliseconds since its time zero) is the clock's
/// now: every export that carries the time hands it in first.
fn clock(now: f64) {
    ac_clock::set_now(page_duration(now));
}

/// The page's bytes at `ptr`.
fn bytes<'a>(ptr: u32, len: u32) -> &'a [u8] {
    if len == 0 {
        return &[];
    }
    // SAFETY: the page passes the address and length of a buffer it got
    // from `ui_alloc` and filled, and does not free or change it during
    // the call; the module has one thread, so nothing else touches it.
    unsafe { std::slice::from_raw_parts(ptr as usize as *const u8, len as usize) }
}

/// The page's text at `ptr`; what is not UTF-8 reads as empty.
fn text<'a>(ptr: u32, len: u32) -> &'a str {
    std::str::from_utf8(bytes(ptr, len)).unwrap_or_default()
}

/// Where the page reads `bytes` (the address is all it needs; the length
/// is `ui_take_len`).
fn address(bytes: &[u8]) -> u32 {
    bytes.as_ptr() as usize as u32
}

/// A buffer of `len` bytes for the page to write input into.
#[unsafe(no_mangle)]
pub extern "C" fn ui_alloc(len: u32) -> u32 {
    let buffer = vec![0u8; len as usize].into_boxed_slice();
    Box::into_raw(buffer).cast::<u8>() as usize as u32
}

/// Give back a buffer from `ui_alloc`, with the length it was asked for.
#[unsafe(no_mangle)]
pub extern "C" fn ui_free(ptr: u32, len: u32) {
    if ptr == 0 {
        return;
    }
    let slice = std::ptr::slice_from_raw_parts_mut(ptr as usize as *mut u8, len as usize);
    // SAFETY: `ptr` and `len` are those of a boxed slice `ui_alloc` gave
    // out and the page has not freed before.
    drop(unsafe { Box::from_raw(slice) });
}

/// The number of the interface this module speaks.
#[unsafe(no_mangle)]
pub extern "C" fn ui_interface_version() -> u32 {
    crate::INTERFACE_VERSION
}

/// The stream encoding's shape, as text.
#[unsafe(no_mangle)]
pub extern "C" fn ui_shape() -> u32 {
    with(|module| {
        let shape = module.panels().shape().into_bytes();
        address(module.hand(shape))
    })
}

/// The canvas, once. Returns 0.
#[unsafe(no_mangle)]
pub extern "C" fn ui_init(width: u32, height: u32, scale: f32, now: f64) -> u32 {
    clock(now);
    with(|module| module.panels().init(width, height, scale, now));
    0
}

/// Before `ui_init`: show one window without a session, read-only, from
/// the data `ui_offline_data` hands in (1 the inventory, 2 the character
/// window). Returns 0, or 1 for an unknown kind.
#[unsafe(no_mangle)]
pub extern "C" fn ui_offline(kind: u32) -> u32 {
    let Some(kind) = crate::offline::OfflineKind::from_number(kind) else {
        return 1;
    };
    with(|module| module.panels().set_offline(kind));
    0
}

/// The data the window without a session shows, as UTF-8 JSON (see
/// `offline`), again whenever it changed. Returns 0, or 1 when it cannot
/// be read (the reason goes to the log).
#[unsafe(no_mangle)]
pub extern "C" fn ui_offline_data(ptr: u32, len: u32) -> u32 {
    with(|module| u32::from(module.panels().offline_data_logged(bytes(ptr, len))))
}

/// The window without a session's own size, frame included, in pixels at
/// scale 1, as `width << 16 | height`; 0 until the panels are built (or
/// with a session).
#[unsafe(no_mangle)]
pub extern "C" fn ui_offline_size() -> u32 {
    with(|module| {
        module
            .panels()
            .offline_size()
            .map_or(0, |(w, h)| (w.min(0xFFFF) << 16) | h.min(0xFFFF))
    })
}

/// The canvas changed size.
#[unsafe(no_mangle)]
pub extern "C" fn ui_resize(width: u32, height: u32, scale: f32) {
    with(|module| module.panels().resize(width, height, scale));
}

/// What the browser may do with the session.
#[unsafe(no_mangle)]
pub extern "C" fn ui_session(generation: i64, controlling: u32, permissions: u32) {
    let session = Session::from_words(generation.cast_unsigned(), controlling, permissions);
    with(|module| module.panels().session(session));
}

/// One stream frame. Returns 0, or 1 when it cannot be read.
#[unsafe(no_mangle)]
pub extern "C" fn ui_stream(ptr: u32, len: u32, now: f64) -> u32 {
    clock(now);
    with(|module| u32::from(module.panels().stream(bytes(ptr, len), now).is_err()))
}

/// The attach ended.
#[unsafe(no_mangle)]
pub extern "C" fn ui_detached() {
    with(|module| module.panels().detached());
}

/// One animation frame: the mask of the kinds that have something to take.
#[unsafe(no_mangle)]
pub extern "C" fn ui_frame(now: f64) -> u32 {
    clock(now);
    with(|module| module.frame(now))
}

/// Take the bytes of one kind of output.
#[unsafe(no_mangle)]
pub extern "C" fn ui_take(kind: u32) -> u32 {
    with(|module| address(module.take(kind)))
}

/// The length of what was last taken or handed.
#[unsafe(no_mangle)]
pub extern "C" fn ui_take_len() -> u32 {
    with(|module| u32::try_from(module.taken().len()).unwrap_or(u32::MAX))
}

/// A key went down or up. Returns whether the panels used it.
#[unsafe(no_mangle)]
pub extern "C" fn ui_key(
    down: u32,
    code_ptr: u32,
    code_len: u32,
    modifiers: u32,
    repeat: u32,
    now: f64,
) -> u32 {
    clock(now);
    let (code, modifiers) = (text(code_ptr, code_len), Modifiers::from_bits(modifiers));
    with(|module| {
        u32::from(
            module
                .panels()
                .key(down != 0, code, modifiers, repeat != 0, now),
        )
    })
}

/// Text was typed.
#[unsafe(no_mangle)]
pub extern "C" fn ui_text(ptr: u32, len: u32) -> u32 {
    with(|module| u32::from(module.panels().text(text(ptr, len))))
}

/// The pointer moved.
#[unsafe(no_mangle)]
pub extern "C" fn ui_pointer_move(x: f32, y: f32, now: f64) -> u32 {
    clock(now);
    with(|module| u32::from(module.panels().pointer_move(x, y, now)))
}

/// A pointer button went down or up.
#[unsafe(no_mangle)]
pub extern "C" fn ui_pointer_button(
    button: u32,
    down: u32,
    x: f32,
    y: f32,
    modifiers: u32,
    now: f64,
) -> u32 {
    clock(now);
    let modifiers = Modifiers::from_bits(modifiers);
    with(|module| {
        u32::from(
            module
                .panels()
                .pointer_button(button, down != 0, (x, y), modifiers, now),
        )
    })
}

/// The pointer left the canvas.
#[unsafe(no_mangle)]
pub extern "C" fn ui_pointer_leave() {
    with(|module| module.panels().pointer_leave());
}

/// The wheel turned.
#[unsafe(no_mangle)]
pub extern "C" fn ui_wheel(lines: f32, x: f32, y: f32, modifiers: u32) -> u32 {
    let modifiers = Modifiers::from_bits(modifiers);
    with(|module| u32::from(module.panels().wheel(lines, (x, y), modifiers)))
}

/// The canvas gained or lost the keyboard.
#[unsafe(no_mangle)]
pub extern "C" fn ui_focus(focused: u32) {
    with(|module| module.panels().focus(focused != 0));
}

/// The clipboard's text.
#[unsafe(no_mangle)]
pub extern "C" fn ui_paste(ptr: u32, len: u32) {
    with(|module| module.panels().paste(text(ptr, len)));
}

/// A dat file's bytes, or with `found` 0 the news that there is none.
#[unsafe(no_mangle)]
pub extern "C" fn ui_dat(source: u32, id: u32, ptr: u32, len: u32, found: u32) {
    let file = (found != 0).then(|| bytes(ptr, len));
    with(|module| module.panels().dat(source, id, file));
}

/// The dat files worth handing in before `ui_init`: 8 bytes each, source
/// then id.
#[unsafe(no_mangle)]
pub extern "C" fn ui_dat_preload() -> u32 {
    with(|module| {
        let list = module
            .panels()
            .dat_preload()
            .into_iter()
            .flat_map(|(source, id)| [source.to_le_bytes(), id.to_le_bytes()].concat())
            .collect();
        address(module.hand(list))
    })
}

/// A stored setting, or with `found` 0 the news that there is none.
#[unsafe(no_mangle)]
pub extern "C" fn ui_storage(key_ptr: u32, key_len: u32, ptr: u32, len: u32, found: u32) {
    let value = (found != 0).then(|| bytes(ptr, len));
    with(|module| module.panels().storage(text(key_ptr, key_len), value));
}

/// The wall clock, as milliseconds since 1970-01-01 UTC (the page's
/// `Date.now()`): before `ui_init` and about once a second.
#[unsafe(no_mangle)]
pub extern "C" fn ui_wall_clock(unix_ms: f64) {
    // Whole milliseconds; a value past what an i64 holds saturates.
    ac_clock::set_unix_millis(unix_ms as i64);
}

/// The page lost every texture (its GPU context): each texture in use is
/// uploaded again, and the picture sent again, with the next frame.
#[unsafe(no_mangle)]
pub extern "C" fn ui_textures_lost() {
    with(|module| module.panels().textures_lost());
}
