//! Fans animation hooks out to every subsystem that wants them (sound,
//! particles, lights, combat, ...).

use std::fmt;

use ac_dat::objects::AnimationHook;
use glam::Vec3;

/// Receives the hooks objects fire.
pub trait AnimationHookSink {
    /// Object `entity_id` at `entity_world_position` fired `hook`.
    fn on_hook(&mut self, entity_id: u32, entity_world_position: Vec3, hook: &AnimationHook);
}

/// Ignores every hook.
#[derive(Debug, Clone, Copy, Default)]
pub struct NullAnimationHookSink;

impl AnimationHookSink for NullAnimationHookSink {
    fn on_hook(&mut self, _entity_id: u32, _entity_world_position: Vec3, _hook: &AnimationHook) {}
}

/// Identifies a registered sink, for unregistering it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct SinkId(u64);

/// Delivers every hook to each registered sink, in registration order.
#[derive(Default)]
pub struct AnimationHookRouter {
    sinks: Vec<(SinkId, Box<dyn AnimationHookSink + Send>)>,
    next_id: u64,
}

impl fmt::Debug for AnimationHookRouter {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("AnimationHookRouter")
            .field("sinks", &self.sinks.len())
            .finish()
    }
}

impl AnimationHookRouter {
    /// An empty router.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a sink; the id unregisters it.
    pub fn register(&mut self, sink: Box<dyn AnimationHookSink + Send>) -> SinkId {
        let id = SinkId(self.next_id);
        self.next_id += 1;
        self.sinks.push((id, sink));
        id
    }

    /// Remove a sink and hand it back; `None` when it is not registered.
    pub fn unregister(&mut self, id: SinkId) -> Option<Box<dyn AnimationHookSink + Send>> {
        let index = self.sinks.iter().position(|(sink_id, _)| *sink_id == id)?;
        Some(self.sinks.remove(index).1)
    }

    /// Number of registered sinks.
    #[must_use]
    pub fn sink_count(&self) -> usize {
        self.sinks.len()
    }
}

impl AnimationHookSink for AnimationHookRouter {
    fn on_hook(&mut self, entity_id: u32, entity_world_position: Vec3, hook: &AnimationHook) {
        for (_, sink) in &mut self.sinks {
            sink.on_hook(entity_id, entity_world_position, hook);
        }
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `AnimationHookRouterTests` (a sink that throws has
    //! no Rust counterpart; a deliberate divergence).
    use super::*;
    use ac_dat::objects::{AttackCone, HookData};
    use std::sync::{Arc, Mutex};

    type Log = Arc<Mutex<Vec<(u32, Vec3, AnimationHook)>>>;

    struct Recording(Log);

    impl AnimationHookSink for Recording {
        fn on_hook(&mut self, entity_id: u32, pos: Vec3, hook: &AnimationHook) {
            self.0.lock().unwrap().push((entity_id, pos, *hook));
        }
    }

    fn recording() -> (Box<dyn AnimationHookSink + Send>, Log) {
        let log = Log::default();
        (Box::new(Recording(log.clone())), log)
    }

    fn sound() -> AnimationHook {
        AnimationHook {
            direction: 0,
            data: HookData::Sound { id: 1 },
        }
    }

    #[test]
    fn a_registered_sink_receives_the_hook() {
        let mut router = AnimationHookRouter::new();
        let (sink, log) = recording();
        router.register(sink);
        let hook = sound();
        router.on_hook(42, Vec3::new(1.0, 2.0, 3.0), &hook);
        assert_eq!(*log.lock().unwrap(), [(42, Vec3::new(1.0, 2.0, 3.0), hook)]);
    }

    #[test]
    fn every_sink_receives_the_hook() {
        let mut router = AnimationHookRouter::new();
        let logs: Vec<Log> = (0..3)
            .map(|_| {
                let (sink, log) = recording();
                router.register(sink);
                log
            })
            .collect();
        let attack = AnimationHook {
            direction: 1,
            data: HookData::Attack(AttackCone::default()),
        };
        router.on_hook(1, Vec3::ZERO, &attack);
        assert!(logs.iter().all(|l| l.lock().unwrap().len() == 1));
    }

    #[test]
    fn unregister_removes_the_sink() {
        let mut router = AnimationHookRouter::new();
        let (a, log_a) = recording();
        let (b, log_b) = recording();
        let id_a = router.register(a);
        router.register(b);
        assert!(router.unregister(id_a).is_some());
        assert!(router.unregister(id_a).is_none());
        router.on_hook(1, Vec3::ZERO, &sound());
        assert!(log_a.lock().unwrap().is_empty());
        assert_eq!(log_b.lock().unwrap().len(), 1);
        assert_eq!(router.sink_count(), 1);
    }

    #[test]
    fn the_null_sink_accepts_anything() {
        let mut sink = NullAnimationHookSink;
        sink.on_hook(1, Vec3::ZERO, &sound());
    }
}
