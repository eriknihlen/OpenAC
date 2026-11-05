//! The wasm imports (platform glue). Every client function has the shape
//! `(request_ptr, request_len) -> reply_len`; the reply is then copied out
//! with `abi_reply_read`. The externs are generated from the ABI's import
//! table, one per client function, and each is reached only through its
//! own [`crate::Callable`] implementation: a plugin that never calls one
//! never imports it (the linker keeps only the imports a module uses).

#![allow(unsafe_code)]

macro_rules! externs {
    ($(
        $(#[$doc:meta])*
        $variant:ident = $name:ident ($request:ty) -> $reply:ty $(= $default:expr)?,
            permission: $perm:expr, window: $window:expr, since: ($maj:literal, $min:literal);
    )*) => {
        mod raw {
            #[link(wasm_import_module = "openac")]
            unsafe extern "C" {
                $( pub(super) fn $name(ptr: *const u8, len: usize) -> i32; )*
            }
        }

        $(
            impl crate::Callable for ac_plugin_abi::calls::$variant {
                fn raw(request: &[u8]) -> i32 {
                    // SAFETY: the client reads exactly `request.len()` bytes
                    // from the pointer during the call and keeps no
                    // reference afterwards; the slice outlives the call.
                    unsafe { raw::$name(request.as_ptr(), request.len()) }
                }
            }
        )*
    };
}

ac_plugin_abi::import_table!(externs);

/// Reads the reply of the call just made: `len` bytes.
pub(crate) fn read_reply(len: i32) -> Vec<u8> {
    let len = usize::try_from(len).unwrap_or(0);
    let mut reply = vec![0u8; len];
    if len > 0 {
        // SAFETY: `reply` is a live, writable buffer of exactly `len` bytes;
        // the client copies at most `len` bytes into it and keeps no
        // reference after returning.
        unsafe {
            raw::abi_reply_read(reply.as_mut_ptr().cast_const(), len);
        }
    }
    reply
}

/// Tells the client why the plugin is about to trap.
pub(crate) fn report_panic(message: &str) {
    // SAFETY: as for every import: the client only reads `message.len()`
    // bytes from the pointer during the call.
    unsafe {
        raw::abi_panic(message.as_ptr(), message.len());
    }
}
