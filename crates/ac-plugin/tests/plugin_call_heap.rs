//! A call into a plugin must give back every heap block it takes. Each call
//! runs on a fiber of its own (see `instance.rs`); wasmtime's Windows fibers
//! leaked one small block per fiber until the patched copy named in the
//! workspace `Cargo.toml`, which cost a bot about 40 MB an hour. This file
//! counts the blocks a stream of calls leaves behind. It is a test binary of
//! its own so that its counting allocator sees nothing but this one test.

use std::alloc::{GlobalAlloc, Layout, System};
use std::pin::pin;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::task::{Context, Poll, Waker};

/// The system allocator, counting the blocks it has handed out and not yet
/// had back.
struct Counting;

static LIVE_BLOCKS: AtomicIsize = AtomicIsize::new(0);

// SAFETY: every method passes its arguments straight to the system
// allocator, which upholds `GlobalAlloc`'s contract; the counter has no say.
#[allow(unsafe_code)]
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        LIVE_BLOCKS.fetch_add(1, Ordering::Relaxed);
        // SAFETY: the caller's `layout` is passed on unchanged.
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        LIVE_BLOCKS.fetch_sub(1, Ordering::Relaxed);
        // SAFETY: `ptr` came from `alloc` above with this `layout`.
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

/// `(module (func (export "f")))`
const EMPTY_EXPORT: &[u8] = &[
    0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00, // magic, version
    0x01, 0x04, 0x01, 0x60, 0x00, 0x00, // type: () -> ()
    0x03, 0x02, 0x01, 0x00, // one function of that type
    0x07, 0x05, 0x01, 0x01, b'f', 0x00, 0x00, // export it as "f"
    0x0a, 0x04, 0x01, 0x02, 0x00, 0x0b, // its body: nothing
];

/// Polls `future` to its end with no executor, as the plugin host does.
fn finish<F: Future>(future: F) -> F::Output {
    let mut future = pin!(future);
    let mut context = Context::from_waker(Waker::noop());
    loop {
        if let Poll::Ready(output) = future.as_mut().poll(&mut context) {
            return output;
        }
    }
}

#[test]
fn plugin_calls_leave_no_heap_blocks_behind() {
    const CALLS: isize = 10_000;
    let engine = wasmtime::Engine::default();
    let module = wasmtime::Module::new(&engine, EMPTY_EXPORT).expect("the module compiles");
    let mut store = wasmtime::Store::new(&engine, ());
    let linker = wasmtime::Linker::new(&engine);
    let instance = finish(linker.instantiate_async(&mut store, &module)).expect("it instantiates");
    let export = instance
        .get_typed_func::<(), ()>(&mut store, "f")
        .expect("it exports f");
    let mut call = |times: isize| {
        for _ in 0..times {
            finish(export.call_async(&mut store, ())).expect("the call returns");
        }
    };
    // The first calls fill wasmtime's caches (the fiber stack among them).
    call(100);
    let before = LIVE_BLOCKS.load(Ordering::Relaxed);
    call(CALLS);
    let left = LIVE_BLOCKS.load(Ordering::Relaxed) - before;
    assert!(
        left < CALLS / 100,
        "{CALLS} plugin calls left {left} heap blocks behind"
    );
}
