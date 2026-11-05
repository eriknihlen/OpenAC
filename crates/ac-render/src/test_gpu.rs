//! The one GPU device every unit test in this crate draws with.
//!
//! The tests run on many short-lived threads of one process. A debug build
//! of wgpu would load the Vulkan SDK's validation layer (and D3D12's debug
//! layer) into every instance, and that layer crashes the whole test run
//! with an access violation when used that way: it reads a device another
//! thread is destroying, and its allocator faults on a new thread's heap.
//! The tests check wgpu's own
//! validation through error scopes, which needs no driver layer, so the
//! test instance keeps wgpu's debug labels but not the driver layers.
//!
//! One device, made on first use and never dropped, serves every test:
//! wgpu devices and queues are thread-safe and wgpu's error scopes are per
//! thread, so a test's scope still sees only its own errors, and the run
//! holds one device's memory instead of one per test.

use std::sync::OnceLock;

use crate::surface::{block_on, device_descriptor};

/// The shared device and queue, or `None` on a machine without an adapter
/// (the GPU tests are then skipped). Asks for what the window asks for when
/// the adapter has it: block-compressed textures.
pub(crate) fn device() -> Option<(wgpu::Device, wgpu::Queue)> {
    static DEVICE: OnceLock<Option<(wgpu::Device, wgpu::Queue)>> = OnceLock::new();
    DEVICE
        .get_or_init(|| new_device("ac-render unit tests"))
        .clone()
}

/// A device of its own, for a test that ends it (`destroy`): the shared one
/// must outlive every test. Vulkan only, or `None` (the test is skipped):
/// polling a destroyed device on D3D12's software adapter, the only one a
/// GPU-less Windows CI machine has, faults the whole test process.
pub(crate) fn own_device() -> Option<(wgpu::Device, wgpu::Queue)> {
    new_device_on("ac-render test, ended by its test", wgpu::Backends::VULKAN)
}

/// A fresh device with the window's settings, kept to its test: for a
/// test that reads the device's own memory blocks, which other tests'
/// resources on the shared device would fill.
pub(crate) fn fresh_device() -> Option<(wgpu::Device, wgpu::Queue)> {
    new_device("ac-render test, alone on its device")
}

fn new_device(label: &'static str) -> Option<(wgpu::Device, wgpu::Queue)> {
    new_device_on(label, wgpu::Backends::all())
}

fn new_device_on(
    label: &'static str,
    backends: wgpu::Backends,
) -> Option<(wgpu::Device, wgpu::Queue)> {
    let mut descriptor = wgpu::InstanceDescriptor::new_without_display_handle();
    descriptor.backends = backends;
    descriptor.flags =
        wgpu::InstanceFlags::from_build_config().difference(wgpu::InstanceFlags::VALIDATION);
    // As the window: FXC, which ships with Windows, not a dxcompiler.dll found on PATH.
    descriptor.backend_options.dx12.shader_compiler = wgpu::Dx12Compiler::Fxc;
    let instance = wgpu::Instance::new(descriptor);
    let adapter = block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
        compatible_surface: None,
        ..Default::default()
    }))
    .ok()?;
    let required_features = adapter.features() & wgpu::Features::TEXTURE_COMPRESSION_BC;
    block_on(adapter.request_device(&device_descriptor(label, required_features))).ok()
}
