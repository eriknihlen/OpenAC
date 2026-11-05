//! Which models the window has asked the loader for, and which it has
//! released since.
//!
//! The loader sends each model once and remembers that it did; a landblock's
//! near content names its models and the loader sends those it has not sent
//! yet right after it. The window releases models nothing uses any more and
//! tells the loader to forget them, so they are sent again when next wanted.
//! A release can cross a landblock on its way: the loader sent the
//! landblock, passed over a model it had already sent, and only then heard
//! that the window released it. Nobody sends that model again by itself, so
//! the window asks for every released model a newly arrived landblock names.

use std::collections::HashSet;

/// The window's side of model loading.
#[derive(Debug, Default)]
pub(crate) struct ModelRequests {
    /// Asked for and not released since.
    requested: HashSet<u32>,
    /// Released and not received since.
    released: HashSet<u32>,
}

impl ModelRequests {
    /// Ask for `id`: true when the request must go to the loader (it has not
    /// been asked for since it was last released).
    pub(crate) fn ask(&mut self, id: u32) -> bool {
        self.requested.insert(id)
    }

    /// `id` was asked for and not released since.
    #[cfg(test)]
    pub(crate) fn is_asked(&self, id: u32) -> bool {
        self.requested.contains(&id)
    }

    /// The window released `id` (and tells the loader to forget it).
    pub(crate) fn release(&mut self, id: u32) {
        self.requested.remove(&id);
        self.released.insert(id);
    }

    /// The loader sent `id`.
    pub(crate) fn arrived(&mut self, id: u32) {
        self.released.remove(&id);
    }

    /// A landblock's near content arrived naming `models`: those the window
    /// released and has not received since, nor asked for again, which the
    /// loader may have passed over. Each is marked asked; the caller sends
    /// the requests.
    pub(crate) fn landblock_arrived(
        &mut self,
        models: impl IntoIterator<Item = u32>,
        mut have: impl FnMut(u32) -> bool,
    ) -> Vec<u32> {
        let mut ask = Vec::new();
        for id in models {
            if self.released.contains(&id) && !have(id) && self.requested.insert(id) {
                ask.push(id);
            }
        }
        ask
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::content::ContentSlot;
    use crate::loader::{LoadRequest, Loaded, Loader};
    use crate::texture_detail::WorldTextureDetail;
    use crate::world_frame::LandblockCoord;
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    /// The loader's once-only sending, as `loader::Worker::ensure_model`
    /// and `forget` do it.
    #[derive(Default)]
    struct OnceSender {
        sent: HashSet<u32>,
    }

    impl OnceSender {
        fn send(&mut self, id: u32) -> Option<u32> {
            self.sent.insert(id).then_some(id)
        }
        fn forget(&mut self, id: u32) {
            self.sent.remove(&id);
        }
    }

    /// The order behind a portal that never ended: the loader sends a
    /// landblock and passes over a model it sent before; the window, not
    /// yet having taken that landblock, releases the model (nothing it
    /// holds uses it), and the loader forgets it. The landblock arrives
    /// lacking the model, and the destination's readiness waits for it.
    #[test]
    fn a_model_released_across_a_landblock_on_its_way_is_asked_for_again() {
        const SHARED: u32 = 0x0200_0001;
        const OWN: u32 = 0x0100_0002;
        let mut loader = OnceSender::default();
        let mut window = ModelRequests::default();
        let mut have: HashSet<u32> = HashSet::new();
        // An earlier landblock brought the shared model.
        have.extend(loader.send(SHARED));
        window.arrived(SHARED);
        // The destination's landblock names both; the loader passes over the
        // shared one and sends its own.
        let named = [SHARED, OWN];
        let sent: Vec<u32> = named.iter().filter_map(|&id| loader.send(id)).collect();
        assert_eq!(sent, [OWN]);
        // Before the window takes the landblock it releases the shared model.
        have.remove(&SHARED);
        window.release(SHARED);
        loader.forget(SHARED);
        for id in sent {
            have.insert(id);
            window.arrived(id);
        }
        // The landblock arrives: the shared model is asked for again, once.
        let ask = window.landblock_arrived(named, |id| have.contains(&id));
        assert_eq!(ask, [SHARED]);
        assert!(
            window
                .landblock_arrived(named, |id| have.contains(&id))
                .is_empty()
        );
        have.extend(ask.iter().filter_map(|&id| loader.send(id)));
        window.arrived(SHARED);
        assert!(named.iter().all(|id| have.contains(id)));
    }

    /// A landblock naming models the window never released asks for
    /// nothing: the loader sends them after the landblock.
    #[test]
    fn a_landblock_asks_only_for_released_models() {
        let mut window = ModelRequests::default();
        assert!(window.landblock_arrived([1, 2, 3], |_| false).is_empty());
        window.release(2);
        assert_eq!(window.landblock_arrived([1, 2, 3], |_| false), [2]);
        // Already here again (the loader resent it): nothing to ask.
        window.release(3);
        window.arrived(3);
        assert!(window.landblock_arrived([3], |_| true).is_empty());
        // A released model asked for again by a server object is not asked
        // twice.
        window.release(5);
        assert!(window.ask(5));
        assert!(window.landblock_arrived([5], |_| false).is_empty());
        assert!(window.is_asked(5));
    }

    /// The window's side of the real loader: the models it has, the near
    /// content of the one landblock it watches, and its requests.
    struct Window {
        requests: ModelRequests,
        have: HashSet<u32>,
        block: LandblockCoord,
        near: Option<Vec<u32>>,
    }

    impl Window {
        fn take(&mut self, item: Loaded) {
            match item {
                Loaded::Failed(reason) => panic!("{reason}"),
                Loaded::Model { id, .. } => {
                    self.requests.arrived(id);
                    self.have.insert(id);
                }
                Loaded::Near(coord, data) if coord == self.block => {
                    let mut ids: Vec<u32> = data.model_ids().collect();
                    ids.sort_unstable();
                    ids.dedup();
                    self.near = Some(ids);
                }
                _ => {}
            }
        }

        /// Take the loader's items until `done` or `limit`; whether `done`
        /// was met.
        fn pump(&mut self, loader: &Loader, limit: Duration, done: impl Fn(&Self) -> bool) -> bool {
            let start = Instant::now();
            while start.elapsed() < limit {
                if done(self) {
                    return true;
                }
                match loader.try_next() {
                    Some(item) => self.take(item),
                    None => std::thread::sleep(Duration::from_millis(5)),
                }
            }
            done(self)
        }
    }

    /// The same order against the real loader and Holtburg's landblock:
    /// the landblock is sent a second time (as after a portal away and
    /// back), the loader passes over its models, the window releases one
    /// before taking the landblock, and still receives it.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_real_loader_sends_a_released_model_a_landblock_names_again() {
        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let loader = Loader::spawn_with_workers(
            Arc::new(ContentSlot::new(dir)),
            WorldTextureDetail::default(),
            true,
            2,
        );
        let mut window = Window {
            requests: ModelRequests::default(),
            have: HashSet::new(),
            block: LandblockCoord { x: 0xA9, y: 0xB4 },
            near: None,
        };
        // The landblock and all its models arrive.
        loader.request(LoadRequest::Near(window.block));
        assert!(window.pump(&loader, Duration::from_secs(120), |w| {
            w.near
                .as_ref()
                .is_some_and(|ids| ids.iter().all(|id| w.have.contains(id)))
        }));
        let ids = window.near.take().expect("the landblock arrived");
        let shared = *ids.first().expect("Holtburg places models");
        // Sent again: the loader passes over every model (all sent before).
        loader.request(LoadRequest::Near(window.block));
        assert!(window.pump(&loader, Duration::from_secs(60), |w| w.near.is_some()));
        let held = window.near.take().expect("the landblock arrived again");
        // Give the loader time to pass over the models it already sent.
        std::thread::sleep(Duration::from_millis(500));
        // The window releases the shared model before taking the landblock.
        window.have.remove(&shared);
        window.requests.release(shared);
        loader.request(LoadRequest::Forget {
            models: vec![shared],
            gfx_objs: Vec::new(),
            surfaces: Vec::new(),
            appearances: Vec::new(),
            variants: Vec::new(),
        });
        // It takes the landblock and asks for what may have been passed over.
        let have = window.have.clone();
        for id in window
            .requests
            .landblock_arrived(held.iter().copied(), |id| have.contains(&id))
        {
            loader.request(LoadRequest::Model(id));
        }
        assert!(
            window.pump(&loader, Duration::from_secs(30), |w| w
                .have
                .contains(&shared)),
            "model {shared:#010X} never arrived again"
        );
    }
}
