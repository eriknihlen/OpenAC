//! GPU time of the world pass, measured with timestamp queries when the
//! adapter has them and the frame profiler is on.
//!
//! The pass writes a timestamp at its start and end; the two are resolved
//! into a buffer and copied to one of a few read-back buffers, which is
//! read when the GPU has finished with it, a frame or two later. A frame
//! whose read-back buffer is still busy is not measured.

use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};
use std::time::Duration;

const SLOTS: usize = 3;
const BYTES: u64 = 2 * wgpu::QUERY_SIZE as u64;

const IDLE: u8 = 0;
const WAITING: u8 = 1;
const READY: u8 = 2;
const FAILED: u8 = 3;

#[derive(Debug)]
struct Slot {
    buffer: wgpu::Buffer,
    state: Arc<AtomicU8>,
    frame: u64,
}

/// The world pass's GPU timer.
#[derive(Debug)]
pub(crate) struct GpuTimer {
    queries: wgpu::QuerySet,
    resolve: wgpu::Buffer,
    slots: Vec<Slot>,
    /// Nanoseconds per timestamp tick.
    period_ns: f32,
    /// The slot this frame writes into, if one was free.
    current: Option<usize>,
}

impl GpuTimer {
    /// A timer, or `None` when the device cannot time passes.
    pub(crate) fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Option<Self> {
        if !device.features().contains(wgpu::Features::TIMESTAMP_QUERY) {
            return None;
        }
        let queries = device.create_query_set(&wgpu::QuerySetDescriptor {
            label: Some("world pass timer"),
            ty: wgpu::QueryType::Timestamp,
            count: 2,
        });
        let resolve = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("world pass timer resolve"),
            size: BYTES,
            usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let slots = (0..SLOTS)
            .map(|_| Slot {
                buffer: device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some("world pass timer readback"),
                    size: BYTES,
                    usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                    mapped_at_creation: false,
                }),
                state: Arc::new(AtomicU8::new(IDLE)),
                frame: 0,
            })
            .collect();
        Some(Self {
            queries,
            resolve,
            slots,
            period_ns: queue.get_timestamp_period(),
            current: None,
        })
    }

    /// Pick a free read-back buffer for `frame`; the pass is timed only
    /// when this returns the timestamp writes.
    pub(crate) fn begin(&mut self, frame: u64) -> Option<wgpu::RenderPassTimestampWrites<'_>> {
        self.current = self
            .slots
            .iter()
            .position(|s| s.state.load(Ordering::Acquire) == IDLE);
        let index = self.current?;
        self.slots[index].frame = frame;
        Some(wgpu::RenderPassTimestampWrites {
            query_set: &self.queries,
            beginning_of_pass_write_index: Some(0),
            end_of_pass_write_index: Some(1),
        })
    }

    /// After the pass: copy the two timestamps out.
    pub(crate) fn resolve(&mut self, encoder: &mut wgpu::CommandEncoder) {
        let Some(index) = self.current else {
            return;
        };
        encoder.resolve_query_set(&self.queries, 0..2, &self.resolve, 0);
        encoder.copy_buffer_to_buffer(&self.resolve, 0, &self.slots[index].buffer, 0, BYTES);
    }

    /// After submitting: ask for the read-back.
    pub(crate) fn submitted(&mut self) {
        let Some(index) = self.current.take() else {
            return;
        };
        let slot = &self.slots[index];
        slot.state.store(WAITING, Ordering::Release);
        let state = Arc::clone(&slot.state);
        slot.buffer
            .map_async(wgpu::MapMode::Read, .., move |result| {
                state.store(
                    if result.is_ok() { READY } else { FAILED },
                    Ordering::Release,
                );
            });
    }

    /// The frame was abandoned before submit.
    pub(crate) fn abandon(&mut self) {
        self.current = None;
    }

    /// Every finished measurement: (frame, GPU time).
    pub(crate) fn collect(&mut self) -> Vec<(u64, Duration)> {
        let mut done = Vec::new();
        for slot in &mut self.slots {
            match slot.state.load(Ordering::Acquire) {
                READY => {
                    if let Ok(view) = slot.buffer.get_mapped_range(..) {
                        let start = u64::from_le_bytes(view[0..8].try_into().unwrap_or([0; 8]));
                        let end = u64::from_le_bytes(view[8..16].try_into().unwrap_or([0; 8]));
                        drop(view);
                        let ticks = end.saturating_sub(start);
                        let nanos = (ticks as f64 * f64::from(self.period_ns)) as u64;
                        done.push((slot.frame, Duration::from_nanos(nanos)));
                    }
                    slot.buffer.unmap();
                    slot.state.store(IDLE, Ordering::Release);
                }
                FAILED => slot.state.store(IDLE, Ordering::Release),
                _ => {}
            }
        }
        done
    }
}
