//! Which particle models and textures each live emitter holds, so the
//! renderer keeps them on the GPU while an emitter draws with them and lets
//! them go when the last such emitter retires.
//!
//! An emitter takes its references when it first draws; they are released
//! when the effects report it retired (its particles all died, or its owner
//! left). Releasing is infallible here: dropping a count never fails, so
//! OpenAC's retry ledger for failed releases has nothing to retry.

use std::collections::HashMap;

use ac_vfx::EmitterHandle;

/// What one emitter holds.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
struct Held {
    gfx_obj: Option<u32>,
    surface: Option<u32>,
}

/// Per-emitter references to particle models and textures. See the module
/// documentation.
#[derive(Debug, Default)]
pub struct ParticleResourceLedger {
    by_emitter: HashMap<EmitterHandle, Held>,
    gfx_objs: HashMap<u32, u32>,
    surfaces: HashMap<u32, u32>,
}

impl ParticleResourceLedger {
    /// Nothing held.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Emitter `handle` draws its particles with model `gfx_obj_id`. An
    /// emitter draws one model for its whole life; a second, different one
    /// replaces the first.
    pub fn hold_gfx_obj(&mut self, handle: EmitterHandle, gfx_obj_id: u32) {
        let held = self.by_emitter.entry(handle).or_default();
        if held.gfx_obj == Some(gfx_obj_id) {
            return;
        }
        if let Some(old) = held.gfx_obj.replace(gfx_obj_id) {
            release(&mut self.gfx_objs, old);
        }
        *self.gfx_objs.entry(gfx_obj_id).or_default() += 1;
    }

    /// Emitter `handle` textures its quads with `surface_id`.
    pub fn hold_surface(&mut self, handle: EmitterHandle, surface_id: u32) {
        let held = self.by_emitter.entry(handle).or_default();
        if held.surface == Some(surface_id) {
            return;
        }
        if let Some(old) = held.surface.replace(surface_id) {
            release(&mut self.surfaces, old);
        }
        *self.surfaces.entry(surface_id).or_default() += 1;
    }

    /// Emitter `handle` retired: release what it held. A second retirement
    /// of the same handle does nothing.
    pub fn retire(&mut self, handle: EmitterHandle) {
        let Some(held) = self.by_emitter.remove(&handle) else {
            return;
        };
        if let Some(id) = held.gfx_obj {
            release(&mut self.gfx_objs, id);
        }
        if let Some(id) = held.surface {
            release(&mut self.surfaces, id);
        }
    }

    /// Particle models some live emitter holds.
    pub fn gfx_objs(&self) -> impl Iterator<Item = u32> + '_ {
        self.gfx_objs.keys().copied()
    }

    /// Textures some live emitter holds.
    pub fn surfaces(&self) -> impl Iterator<Item = u32> + '_ {
        self.surfaces.keys().copied()
    }

    /// Emitters holding something.
    #[must_use]
    pub fn emitter_count(&self) -> usize {
        self.by_emitter.len()
    }
}

fn release(counts: &mut HashMap<u32, u32>, id: u32) {
    if let Some(count) = counts.get_mut(&id) {
        *count -= 1;
        if *count == 0 {
            counts.remove(&id);
        }
    }
}

#[cfg(test)]
mod tests {
    //! After OpenAC `ParticleEmitterRetirementTrackerTests` (retirement
    //! releases the mesh, the per-emitter state and the texture, once per
    //! handle; its failure-and-retry cases have no counterpart since a
    //! release cannot fail here) and `ParticleSubmissionOrdering`'s
    //! `ParticleMeshReferenceTracker` (one model per emitter).
    use super::*;

    #[test]
    fn retirement_releases_the_model_and_the_texture_of_its_emitter() {
        let mut l = ParticleResourceLedger::new();
        l.hold_gfx_obj(42, 0x0100_0001);
        l.hold_surface(42, 0x0800_0001);
        assert_eq!(l.gfx_objs().collect::<Vec<_>>(), [0x0100_0001]);
        assert_eq!(l.surfaces().collect::<Vec<_>>(), [0x0800_0001]);
        l.retire(42);
        assert_eq!(l.gfx_objs().count(), 0);
        assert_eq!(l.surfaces().count(), 0);
        assert_eq!(l.emitter_count(), 0);
    }

    #[test]
    fn a_shared_resource_stays_until_its_last_emitter_retires() {
        let mut l = ParticleResourceLedger::new();
        l.hold_surface(1, 0x0800_0001);
        l.hold_surface(2, 0x0800_0001);
        // Holding again from the same emitter counts once.
        l.hold_surface(2, 0x0800_0001);
        l.retire(1);
        assert_eq!(l.surfaces().collect::<Vec<_>>(), [0x0800_0001]);
        l.retire(2);
        assert_eq!(l.surfaces().count(), 0);
    }

    #[test]
    fn retiring_the_same_handle_twice_releases_once() {
        let mut l = ParticleResourceLedger::new();
        l.hold_gfx_obj(7, 0x0100_0002);
        l.hold_gfx_obj(8, 0x0100_0002);
        l.retire(7);
        l.retire(7);
        assert_eq!(l.gfx_objs().collect::<Vec<_>>(), [0x0100_0002]);
    }

    #[test]
    fn a_different_model_replaces_the_first() {
        let mut l = ParticleResourceLedger::new();
        l.hold_gfx_obj(3, 0x0100_0001);
        l.hold_gfx_obj(3, 0x0100_0002);
        assert_eq!(l.gfx_objs().collect::<Vec<_>>(), [0x0100_0002]);
    }
}
