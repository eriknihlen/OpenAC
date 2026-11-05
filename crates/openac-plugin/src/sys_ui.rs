//! The plugin UI's wasm imports (platform glue), shaped like every other
//! client function (`(request_ptr, request_len) -> reply_len`, the reply
//! copied out with `abi_reply_read`). A plugin that never calls one never
//! imports it.

#![allow(unsafe_code)]

mod raw {
    #[link(wasm_import_module = "openac")]
    unsafe extern "C" {
        pub(super) fn ui_panel_register(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_remove(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_set(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_show(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_view_exists(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_view_visible(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_control_exists(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_control_label(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_control_visible(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_register_typed(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_set_typed(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_dialog_confirm(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_dialog_withdraw(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_panel_show_modal(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_client_window(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_register(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_paint(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_set(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_state(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_available(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_canvas_remove(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_measure_text(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_font_bundled(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_font_load(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_font_measure(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_font_release(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_font_count(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_theme(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_client_art(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_spell_icon(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_object_icon(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_rgba(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_decode(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_release(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_image_count(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_labels_show(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_lines_create(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_lines_set(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_lines_remove(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_triangles_create(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_triangles_set(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_triangles_remove(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_markers_create(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_markers_set_icons(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_markers_set_shapes(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_markers_remove(ptr: *const u8, len: usize) -> i32;
        pub(super) fn ui_projectile_debug(ptr: *const u8, len: usize) -> i32;
    }
}

macro_rules! wrappers {
    ($($name:ident),* $(,)?) => {$(
        /// The import of the same name: request bytes in, reply bytes out.
        pub(crate) fn $name(request: &[u8]) -> Vec<u8> {
            // SAFETY: the client reads exactly `request.len()` bytes from
            // the pointer during the call and keeps no reference afterwards;
            // the slice outlives the call.
            let len = unsafe { raw::$name(request.as_ptr(), request.len()) };
            crate::sys::read_reply(len)
        }
    )*};
}

wrappers!(
    ui_panel_register,
    ui_panel_remove,
    ui_panel_set,
    ui_panel_show,
    ui_view_exists,
    ui_view_visible,
    ui_control_exists,
    ui_control_label,
    ui_control_visible,
    ui_panel_register_typed,
    ui_panel_set_typed,
    ui_dialog_confirm,
    ui_dialog_withdraw,
    ui_panel_show_modal,
    ui_client_window,
    ui_canvas_register,
    ui_canvas_paint,
    ui_canvas_set,
    ui_canvas_state,
    ui_canvas_available,
    ui_canvas_remove,
    ui_measure_text,
    ui_font_bundled,
    ui_font_load,
    ui_font_measure,
    ui_font_release,
    ui_font_count,
    ui_theme,
    ui_image_client_art,
    ui_image_spell_icon,
    ui_image_object_icon,
    ui_image_rgba,
    ui_image_decode,
    ui_image_release,
    ui_image_count,
    ui_labels_show,
    ui_lines_create,
    ui_lines_set,
    ui_lines_remove,
    ui_triangles_create,
    ui_triangles_set,
    ui_triangles_remove,
    ui_markers_create,
    ui_markers_set_icons,
    ui_markers_set_shapes,
    ui_markers_remove,
    ui_projectile_debug,
);
