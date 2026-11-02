//! How long server objects' looks stay: the bookkeeping of appearances,
//! their part variants and recoloured surfaces, on both sides of the
//! loader's channel.
//!
//! The loader sends each look once ([`SentLooks`]): a recoloured surface
//! (under a handle), then the variants that name it, then the appearance
//! whose parts name the variants. The window keeps them ([`HeldLooks`])
//! while something draws them (OpenAC owner-scoped composite textures:
//! each recoloured texture is owned by the live objects drawn with it).
//! When the last object with a look goes, the look and its variants are
//! dropped and its recoloured surfaces become unowned, held within the
//! unowned texture budget with the plain textures, the oldest released
//! beyond it.
//!
//! The two sides stay in lockstep: everything the window drops it tells the
//! loader to forget in one `Forget` request, so the loader sends it again
//! when it is next wanted. The loader reads requests in the order they
//! were made and applies a forget as it reads it, while appearance work
//! waits in its own queue (newest first) and is done later: so a request
//! made after a forget is answered after it, and work still queued when a
//! forget is read is answered after it too. Only what the loader sent
//! before reading a forget can cross it, and that is caught on arrival: an
//! appearance naming a variant or a recoloured surface the window no
//! longer has is refused, forgotten and asked for again; a recoloured
//! surface sent again for a texture-detail change after its release is
//! held unowned and released again.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use ac_dat::objects::Palette;
use glam::Vec3;

use crate::appearance::{AppearanceKey, PaletteSpec};
use crate::loader::LoadRequest;
use crate::meshing::MeshRef;
use crate::unowned_cache::BoundedUnownedResourceCache;

/// Handles of recoloured surfaces start here, above every surface id
/// (`0x08xxxxxx`), so a handle never names a stored surface.
pub(crate) const FIRST_SURFACE_HANDLE: u32 = 0x1000_0000;

/// A recoloured surface: (surface, texture, palette).
pub(crate) type SurfaceVariantKey = (u32, Option<u32>, Option<PaletteSpec>);

/// A part's surface substitutions: (surface id, handle), sorted by surface
/// id.
pub(crate) type VariantSurfaces = Vec<(u32, u32)>;

/// A look's box (low and high corners, model-local), when known.
type Bounds = Option<(Vec3, Vec3)>;

/// What the window released (or refused) and the loader must forget.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
pub(crate) struct ReleasedLooks {
    /// Appearances.
    pub appearances: Vec<AppearanceKey>,
    /// Variant ids.
    pub variants: Vec<u32>,
    /// Recoloured surface handles.
    pub handles: Vec<u32>,
}

impl ReleasedLooks {
    pub(crate) fn is_empty(&self) -> bool {
        self.appearances.is_empty() && self.variants.is_empty() && self.handles.is_empty()
    }

    /// The one `Forget` request for these and the models, GfxObjs and
    /// surfaces released with them (the recoloured handles go with the
    /// surfaces); `None` when nothing was released.
    pub(crate) fn into_forget(
        self,
        models: Vec<u32>,
        gfx_objs: Vec<u32>,
        mut surfaces: Vec<u32>,
    ) -> Option<LoadRequest> {
        if self.is_empty() && models.is_empty() && gfx_objs.is_empty() && surfaces.is_empty() {
            return None;
        }
        surfaces.extend(self.handles);
        Some(LoadRequest::Forget {
            models,
            gfx_objs,
            surfaces,
            appearances: self.appearances,
            variants: self.variants,
        })
    }
}

/// What a sweep of the looks and the surfaces decided.
#[derive(Debug, Default)]
pub(crate) struct LooksSweep {
    /// Every surface in use: the kept looks' and the others owned.
    pub owned: HashSet<u32>,
    /// Surfaces, plain and recoloured, beyond the unowned budget (all
    /// unowned ones when nothing unowned is kept): to release.
    pub gone_surfaces: Vec<u32>,
    /// The looks and variants released.
    pub released: ReleasedLooks,
}

impl LooksSweep {
    /// The one `Forget` request for this sweep, with the models and
    /// GfxObjs it released too.
    pub(crate) fn into_forget(self, models: Vec<u32>, gfx_objs: Vec<u32>) -> Option<LoadRequest> {
        self.released
            .into_forget(models, gfx_objs, self.gone_surfaces)
    }
}

/// One sweep of the looks and the surfaces the window holds: the looks
/// `used` does not name are dropped with the variants only they named;
/// `owned_elsewhere` (called after, with the looks kept) gives the other
/// surfaces in use; then every `resident` surface (with its bytes) nothing
/// owns is held unowned, recoloured ones like plain ones, and the oldest
/// beyond the budget (all of them when `retain` is off) are to go.
pub(crate) fn sweep_looks(
    looks: &mut HeldLooks,
    unowned: &mut BoundedUnownedResourceCache<u32>,
    used: impl Fn(&AppearanceKey) -> bool,
    owned_elsewhere: impl FnOnce(&HeldLooks) -> HashSet<u32>,
    resident: impl IntoIterator<Item = (u32, u64)>,
    retain: bool,
) -> LooksSweep {
    let released = looks.release_unused(used);
    let mut owned = owned_elsewhere(looks);
    owned.extend(looks.owned_surfaces());
    let gone_surfaces = unowned.settle(resident, |id| owned.contains(id), retain);
    LooksSweep {
        owned,
        gone_surfaces,
        released,
    }
}

// ── The loader's side ──────────────────────────────────────────────────

/// A part's variant as the loader sends it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct PartVariant {
    /// Its id (0: the part changes nothing).
    pub id: u32,
    /// Recoloured surfaces not sent yet, with their handles.
    pub new_surfaces: Vec<(SurfaceVariantKey, u32)>,
    /// Its surfaces, when the variant was not sent yet.
    pub new_variant: Option<VariantSurfaces>,
}

/// The looks the loader has sent and believes the window holds.
#[derive(Debug)]
pub(crate) struct SentLooks {
    appearances: HashSet<AppearanceKey>,
    /// Modified palettes built so far (`None`: the swap cannot be built).
    palettes: HashMap<PaletteSpec, Option<Arc<Palette>>>,
    /// Recoloured surfaces to their handles, and back.
    surface_variants: HashMap<SurfaceVariantKey, u32>,
    handle_keys: HashMap<u32, SurfaceVariantKey>,
    next_surface_handle: u32,
    /// Part surface maps to variant ids, and back.
    variants: HashMap<VariantSurfaces, u32>,
    variant_surfaces: HashMap<u32, VariantSurfaces>,
    next_variant: u32,
}

impl Default for SentLooks {
    fn default() -> Self {
        Self {
            appearances: HashSet::new(),
            palettes: HashMap::new(),
            surface_variants: HashMap::new(),
            handle_keys: HashMap::new(),
            next_surface_handle: FIRST_SURFACE_HANDLE,
            variants: HashMap::new(),
            variant_surfaces: HashMap::new(),
            next_variant: 1,
        }
    }
}

impl SentLooks {
    /// Whether `key` was sent (and not forgotten since).
    pub(crate) fn was_sent(&self, key: &AppearanceKey) -> bool {
        self.appearances.contains(key)
    }

    /// Note `key` as sent.
    pub(crate) fn sent(&mut self, key: AppearanceKey) {
        self.appearances.insert(key);
    }

    /// The modified palette of `spec`, built by `build` the first time.
    pub(crate) fn palette(
        &mut self,
        spec: &PaletteSpec,
        build: impl FnOnce() -> Option<Arc<Palette>>,
    ) -> Option<Arc<Palette>> {
        self.palettes
            .entry(spec.clone())
            .or_insert_with(build)
            .clone()
    }

    /// The modified palette of `spec` built so far.
    pub(crate) fn built_palette(&self, spec: &PaletteSpec) -> Option<Arc<Palette>> {
        self.palettes.get(spec).cloned().flatten()
    }

    /// The handle of recoloured surface `key`, and whether it is new (to
    /// be sent). A handle is never given twice.
    pub(crate) fn surface_handle(&mut self, key: &SurfaceVariantKey) -> (u32, bool) {
        if let Some(&handle) = self.surface_variants.get(key) {
            return (handle, false);
        }
        let handle = self.next_surface_handle;
        self.next_surface_handle += 1;
        self.surface_variants.insert(key.clone(), handle);
        self.handle_keys.insert(handle, key.clone());
        (handle, true)
    }

    /// Whether `handle` is still the recoloured surface `key`'s.
    pub(crate) fn holds_handle(&self, key: &SurfaceVariantKey, handle: u32) -> bool {
        self.handle_keys.get(&handle) == Some(key)
    }

    /// The variant id of `surfaces` (sorted), and whether it is new (to be
    /// sent). An id is never given twice.
    fn variant(&mut self, surfaces: &VariantSurfaces) -> (u32, bool) {
        if let Some(&id) = self.variants.get(surfaces) {
            return (id, false);
        }
        let id = self.next_variant;
        self.next_variant += 1;
        self.variants.insert(surfaces.clone(), id);
        self.variant_surfaces.insert(id, surfaces.clone());
        (id, true)
    }

    /// The variant of a part whose surfaces `changes` recolour, and what
    /// of it is new: the recoloured surfaces and the variant the loader
    /// sends, in that order. Variant 0 (none) when nothing changes.
    pub(crate) fn part_variant(
        &mut self,
        changes: impl IntoIterator<Item = SurfaceVariantKey>,
    ) -> PartVariant {
        let mut new_surfaces = Vec::new();
        let mut surfaces: VariantSurfaces = changes
            .into_iter()
            .map(|key| {
                let (handle, new) = self.surface_handle(&key);
                let surface_id = key.0;
                if new {
                    new_surfaces.push((key, handle));
                }
                (surface_id, handle)
            })
            .collect();
        if surfaces.is_empty() {
            return PartVariant {
                id: 0,
                new_surfaces,
                new_variant: None,
            };
        }
        surfaces.sort_unstable();
        let (id, new) = self.variant(&surfaces);
        PartVariant {
            id,
            new_surfaces,
            new_variant: new.then_some(surfaces),
        }
    }

    /// Every recoloured surface held, in handle order.
    pub(crate) fn surface_variants(&self) -> Vec<(SurfaceVariantKey, u32)> {
        let mut all: Vec<(SurfaceVariantKey, u32)> = self
            .surface_variants
            .iter()
            .map(|(key, &handle)| (key.clone(), handle))
            .collect();
        all.sort_unstable_by_key(|&(_, handle)| handle);
        all
    }

    /// The window released these (`surfaces`: any surface ids; the
    /// recoloured handles among them are this side's): send them again
    /// when next wanted. A variant naming a forgotten recoloured surface is
    /// forgotten with it, and a palette no remaining look uses goes too.
    pub(crate) fn forget(
        &mut self,
        appearances: &[AppearanceKey],
        variants: &[u32],
        surfaces: &[u32],
    ) {
        let handles: Vec<u32> = surfaces
            .iter()
            .copied()
            .filter(|&id| id >= FIRST_SURFACE_HANDLE)
            .collect();
        if appearances.is_empty() && variants.is_empty() && handles.is_empty() {
            return;
        }
        for key in appearances {
            self.appearances.remove(key);
        }
        for id in variants {
            if let Some(surfaces) = self.variant_surfaces.remove(id) {
                self.variants.remove(&surfaces);
            }
        }
        if !handles.is_empty() {
            let gone: HashSet<u32> = handles.iter().copied().collect();
            for handle in &gone {
                if let Some(key) = self.handle_keys.remove(handle) {
                    self.surface_variants.remove(&key);
                }
            }
            let Self {
                variants,
                variant_surfaces,
                ..
            } = self;
            variant_surfaces.retain(|_, surfaces| {
                let keep = !surfaces.iter().any(|(_, h)| gone.contains(h));
                if !keep {
                    variants.remove(surfaces);
                }
                keep
            });
        }
        let in_use: HashSet<&PaletteSpec> = self
            .appearances
            .iter()
            .filter_map(|k| k.palette.as_ref())
            .chain(
                self.surface_variants
                    .keys()
                    .filter_map(|(_, _, p)| p.as_ref()),
            )
            .collect();
        self.palettes.retain(|spec, _| in_use.contains(spec));
    }

    #[cfg(test)]
    pub(crate) fn counts(&self) -> (usize, usize, usize, usize) {
        (
            self.appearances.len(),
            self.variants.len(),
            self.surface_variants.len(),
            self.palettes.len(),
        )
    }
}

// ── The window's side ──────────────────────────────────────────────────

/// The looks the window holds.
#[derive(Debug, Default)]
pub(crate) struct HeldLooks {
    /// Server objects' models drawn with their appearance (`None`: the
    /// model does not exist).
    appearances: HashMap<AppearanceKey, Option<Vec<MeshRef>>>,
    /// Bounds of each appearance's parts.
    bounds: HashMap<AppearanceKey, Bounds>,
    /// Appearances asked for and not yet here.
    requested: HashSet<AppearanceKey>,
    /// Part surface substitutions: variant id to (surface id, handle).
    variants: HashMap<u32, VariantSurfaces>,
    /// Variants and recoloured surfaces that arrived since the last
    /// appearance: the loader sends an appearance's new ones just ahead of
    /// it, so they are kept until it comes.
    arriving_variants: HashSet<u32>,
    arriving_handles: HashSet<u32>,
}

impl HeldLooks {
    /// Whether `key` is here.
    pub(crate) fn contains(&self, key: &AppearanceKey) -> bool {
        self.appearances.contains_key(key)
    }

    /// The parts of `key` (`Some(None)`: the model does not exist; `None`:
    /// not here).
    pub(crate) fn get(&self, key: &AppearanceKey) -> Option<&Option<Vec<MeshRef>>> {
        self.appearances.get(key)
    }

    /// The parts of `key` and their bounds, when here and the model exists.
    pub(crate) fn parts(&self, key: &AppearanceKey) -> Option<(&[MeshRef], Bounds)> {
        let parts = self.appearances.get(key).and_then(Option::as_deref)?;
        Some((parts, self.bounds.get(key).copied().flatten()))
    }

    /// Every part of every look held.
    pub(crate) fn all_parts(&self) -> impl Iterator<Item = &MeshRef> {
        self.appearances.values().flatten().flatten()
    }

    /// Note `key` as asked for; false when it already was.
    pub(crate) fn ask(&mut self, key: &AppearanceKey) -> bool {
        if self.requested.contains(key) {
            return false;
        }
        self.requested.insert(key.clone());
        true
    }

    /// The surface a part with `variant` draws in place of `surface_id`.
    pub(crate) fn handle(&self, variant: u32, surface_id: u32) -> Option<u32> {
        let map = self.variants.get(&variant)?;
        map.binary_search_by_key(&surface_id, |&(s, _)| s)
            .ok()
            .map(|i| map[i].1)
    }

    /// A surface arrived (`held`: the window holds it already). A new
    /// recoloured surface is kept for the appearance it precedes; one sent
    /// again at a new texture detail is not new.
    pub(crate) fn surface_arrived(&mut self, id: u32, held: bool) {
        if id >= FIRST_SURFACE_HANDLE && !held {
            self.arriving_handles.insert(id);
        }
    }

    /// A variant arrived.
    pub(crate) fn variant_arrived(&mut self, id: u32, surfaces: VariantSurfaces) {
        self.arriving_variants.insert(id);
        self.variants.insert(id, surfaces);
    }

    /// An appearance arrived. `has_surface`: whether the window holds a
    /// surface. An appearance naming a variant the window lacks, or one
    /// whose recoloured surface it lacks, crossed a forget in flight: it is
    /// refused (`Some`: the `Forget` the loader must read before it is asked for
    /// again; it is no longer marked as asked).
    pub(crate) fn appearance_arrived(
        &mut self,
        key: AppearanceKey,
        parts: Option<Vec<MeshRef>>,
        bounds: Bounds,
        has_surface: impl Fn(u32) -> bool,
    ) -> Option<LoadRequest> {
        self.arriving_variants.clear();
        self.arriving_handles.clear();
        self.requested.remove(&key);
        let mut refused = ReleasedLooks::default();
        for part in parts.iter().flatten() {
            if part.variant == 0 || refused.variants.contains(&part.variant) {
                continue;
            }
            match self.variants.get(&part.variant) {
                None => refused.variants.push(part.variant),
                Some(map) => {
                    let missing: Vec<u32> = map
                        .iter()
                        .map(|&(_, h)| h)
                        .filter(|&h| !has_surface(h))
                        .collect();
                    if !missing.is_empty() {
                        refused.variants.push(part.variant);
                        refused.handles.extend(missing);
                    }
                }
            }
        }
        if refused.variants.is_empty() {
            self.bounds.insert(key.clone(), bounds);
            self.appearances.insert(key, parts);
            return None;
        }
        for id in &refused.variants {
            self.variants.remove(id);
        }
        refused.appearances.push(key);
        refused.into_forget(Vec::new(), Vec::new(), Vec::new())
    }

    /// Drop the appearances `used` does not hold and the variants no kept
    /// appearance names; returns them (no handles: the recoloured surfaces
    /// go to the unowned budget, see [`HeldLooks::owned_surfaces`]). What
    /// arrived ahead of an appearance is kept only while one is still asked
    /// for: with none outstanding it was sent for nothing (a resend of a
    /// surface released since) and is owned no longer.
    pub(crate) fn release_unused(
        &mut self,
        used: impl Fn(&AppearanceKey) -> bool,
    ) -> ReleasedLooks {
        self.requested.retain(|key| used(key));
        if self.requested.is_empty() {
            self.arriving_variants.clear();
            self.arriving_handles.clear();
        }
        let gone: Vec<AppearanceKey> = self
            .appearances
            .keys()
            .filter(|key| !used(key))
            .cloned()
            .collect();
        for key in &gone {
            self.appearances.remove(key);
            self.bounds.remove(key);
        }
        let named: HashSet<u32> = self
            .all_parts()
            .map(|p| p.variant)
            .chain(self.arriving_variants.iter().copied())
            .collect();
        let mut variants: Vec<u32> = self
            .variants
            .keys()
            .copied()
            .filter(|id| !named.contains(id))
            .collect();
        variants.sort_unstable();
        for id in &variants {
            self.variants.remove(id);
        }
        ReleasedLooks {
            appearances: gone,
            variants,
            handles: Vec::new(),
        }
    }

    /// The surfaces the kept variants draw with (each original surface
    /// and its handle) and the recoloured surfaces still arriving: owned.
    pub(crate) fn owned_surfaces(&self) -> impl Iterator<Item = u32> + '_ {
        self.variants
            .values()
            .flat_map(|map| map.iter().flat_map(|&(s, h)| [s, h]))
            .chain(self.arriving_handles.iter().copied())
    }

    /// Appearances held.
    pub(crate) fn appearance_count(&self) -> usize {
        self.appearances.len()
    }

    /// Variants held.
    pub(crate) fn variant_count(&self) -> usize {
        self.variants.len()
    }
}

#[cfg(test)]
#[path = "appearance_residency_tests.rs"]
mod tests;
