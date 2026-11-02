//! The window releases server objects' looks nothing draws any more, and
//! the loader, told in the same request, sends them again when they come
//! back; what crosses a release in flight is caught.

use std::collections::{HashMap, HashSet, VecDeque};

use glam::Mat4;

use super::*;
use crate::unowned_cache::BoundedUnownedResourceCache;

const MIB: u64 = 1024 * 1024;
/// Every test part's GfxObj, and the surface of part `i` (0 and 1).
const GFX: u32 = 0x0100_0001;
const SURFACE: u32 = 0x0800_0100;

/// A server object's look: its setup, its part 0 retextured to `texture`.
fn look(setup: u32, texture: u32) -> AppearanceKey {
    AppearanceKey {
        setup_id: setup,
        texture_changes: vec![(0, 0x0500_0001, texture)],
        close_degrades: true,
        ..AppearanceKey::default()
    }
}

/// What the loader sends.
#[derive(Debug)]
enum Msg {
    /// A recoloured surface, with what it shows.
    Surface(u32, SurfaceVariantKey),
    Variant(u32, VariantSurfaces),
    Appearance(AppearanceKey, Vec<MeshRef>),
}

/// The loader's side: two parts, each texture change recolouring its part
/// (as `Worker::ensure_appearance` builds the changes).
#[derive(Default)]
struct Loader {
    sent: SentLooks,
    /// Recoloured surfaces a texture-detail change sends again, waiting.
    resizes: Vec<(SurfaceVariantKey, u32)>,
}

impl Loader {
    fn answer(&mut self, request: LoadRequest, out: &mut VecDeque<Msg>) {
        match request {
            LoadRequest::Forget {
                surfaces,
                appearances,
                variants,
                ..
            } => self.sent.forget(&appearances, &variants, &surfaces),
            LoadRequest::Appearance(key) => {
                let key = *key;
                if self.sent.was_sent(&key) {
                    return;
                }
                let mut parts = Vec::new();
                for i in 0..2u8 {
                    let surface = SURFACE + u32::from(i);
                    let changes = key
                        .texture_changes
                        .iter()
                        .filter(|(part, _, _)| *part == i)
                        .map(|&(_, _, new)| (surface, Some(new), None));
                    let variant = self.sent.part_variant(changes);
                    for (k, handle) in variant.new_surfaces {
                        out.push_back(Msg::Surface(handle, k));
                    }
                    if let Some(surfaces) = variant.new_variant {
                        out.push_back(Msg::Variant(variant.id, surfaces));
                    }
                    parts.push(MeshRef {
                        gfx_obj_id: GFX,
                        transform: Mat4::IDENTITY,
                        variant: variant.id,
                    });
                }
                self.sent.sent(key.clone());
                out.push_back(Msg::Appearance(key, parts));
            }
            other => panic!("not asked in these tests: {other:?}"),
        }
    }

    /// A texture-detail change: every recoloured surface sent is to go
    /// again (`Worker::change_detail`).
    fn change_detail(&mut self) {
        self.resizes = self.sent.surface_variants();
    }

    /// Send them, unless the window released one since (`Work::ResizeVariant`).
    fn resize(&mut self, out: &mut VecDeque<Msg>) {
        for (key, handle) in std::mem::take(&mut self.resizes) {
            if self.sent.holds_handle(&key, handle) {
                out.push_back(Msg::Surface(handle, key));
            }
        }
    }
}

/// What a part's surface draws as.
#[derive(Debug, Clone, PartialEq, Eq)]
enum Drawn {
    Plain(u32),
    Recoloured(SurfaceVariantKey),
    Missing(u32),
}

/// The window's side, adopting and sweeping through the same calls as
/// `WorldRenderer` (`adopt_loaded`, `sweep`): looks nothing draws go,
/// recoloured surfaces nothing owns join the unowned budget, and
/// everything released is forgotten in one request.
struct Window {
    looks: HeldLooks,
    /// Recoloured surfaces held, with what each shows.
    surfaces: HashMap<u32, SurfaceVariantKey>,
    unowned: BoundedUnownedResourceCache<u32>,
    retain: bool,
    requests: VecDeque<LoadRequest>,
    results: VecDeque<Msg>,
    loader: Loader,
}

impl Window {
    /// A budget of `budget` MiB; each recoloured surface is 1 MiB.
    fn new(budget: u64) -> Self {
        Self {
            looks: HeldLooks::default(),
            surfaces: HashMap::new(),
            unowned: BoundedUnownedResourceCache::new(budget * MIB, 256),
            retain: true,
            requests: VecDeque::new(),
            results: VecDeque::new(),
            loader: Loader::default(),
        }
    }

    /// Ask for the looks of `scene` not here.
    fn ask(&mut self, scene: &[AppearanceKey]) {
        for key in scene {
            if !self.looks.contains(key) && self.looks.ask(key) {
                self.requests
                    .push_back(LoadRequest::Appearance(Box::new(key.clone())));
            }
        }
    }

    /// The loader answers every request so far, in order.
    fn loader_works(&mut self) {
        while let Some(request) = self.requests.pop_front() {
            self.loader.answer(request, &mut self.results);
        }
    }

    /// Adopt one result.
    fn adopt(&mut self, msg: Msg) {
        match msg {
            Msg::Surface(handle, key) => {
                let held = self.surfaces.contains_key(&handle);
                self.looks.surface_arrived(handle, held);
                self.surfaces.insert(handle, key);
            }
            Msg::Variant(id, surfaces) => self.looks.variant_arrived(id, surfaces),
            Msg::Appearance(key, parts) => {
                let held = &self.surfaces;
                if let Some(forget) = self
                    .looks
                    .appearance_arrived(key, Some(parts), None, |h| held.contains_key(&h))
                {
                    self.requests.push_back(forget);
                }
            }
        }
    }

    /// Adopt every result so far.
    fn adopt_all(&mut self) {
        while let Some(msg) = self.results.pop_front() {
            self.adopt(msg);
        }
    }

    /// Frames until the looks of `scene` are all here.
    fn show(&mut self, scene: &[AppearanceKey]) {
        for _ in 0..4 {
            self.ask(scene);
            self.loader_works();
            self.adopt_all();
        }
        assert!(
            scene.iter().all(|k| self.looks.contains(k)),
            "never arrived"
        );
    }

    fn sweep(&mut self, scene: &[AppearanceKey]) {
        let swept = sweep_looks(
            &mut self.looks,
            &mut self.unowned,
            |key| scene.contains(key),
            |_| HashSet::new(),
            self.surfaces.keys().map(|&h| (h, MIB)),
            self.retain,
        );
        for handle in &swept.gone_surfaces {
            self.surfaces.remove(handle);
        }
        if let Some(forget) = swept.into_forget(Vec::new(), Vec::new()) {
            self.requests.push_back(forget);
        }
    }

    /// How each part of `key` draws.
    fn drawn(&self, key: &AppearanceKey) -> Vec<Drawn> {
        let (parts, _) = self.looks.parts(key).expect("the look is here");
        parts
            .iter()
            .enumerate()
            .map(|(i, part)| {
                let surface = SURFACE + i as u32;
                let handle = if part.variant == 0 {
                    surface
                } else {
                    self.looks.handle(part.variant, surface).unwrap_or(surface)
                };
                if handle < FIRST_SURFACE_HANDLE {
                    Drawn::Plain(handle)
                } else {
                    self.surfaces
                        .get(&handle)
                        .map_or(Drawn::Missing(handle), |k| Drawn::Recoloured(k.clone()))
                }
            })
            .collect()
    }
}

/// How a look retextured to `texture` draws: part 0 recoloured, part 1 as
/// stored.
fn expected(texture: u32) -> Vec<Drawn> {
    vec![
        Drawn::Recoloured((SURFACE, Some(texture), None)),
        Drawn::Plain(SURFACE + 1),
    ]
}

/// OpenAC `CompositeTextureArrayCache.ReleaseOwner`: when the last object
/// with a look leaves, its look and variants go and its recoloured
/// surfaces become unowned, held within the budget; the loader forgets the
/// look and variants but not the surfaces still held.
#[test]
fn objects_leaving_free_their_looks_and_unown_their_recoloured_surfaces() {
    let mut w = Window::new(64);
    let (a, b) = (
        look(0x0200_0001, 0x0500_00A0),
        look(0x0200_0002, 0x0500_00B0),
    );
    w.show(&[a.clone(), b.clone()]);
    w.sweep(&[a.clone(), b.clone()]);
    assert!(w.requests.is_empty(), "nothing is released while drawn");
    assert_eq!(
        (w.looks.appearance_count(), w.looks.variant_count()),
        (2, 2)
    );
    assert_eq!(w.unowned.count(), 0, "the recoloured surfaces are owned");

    w.sweep(&[a]);
    assert_eq!(
        (w.looks.appearance_count(), w.looks.variant_count()),
        (1, 1)
    );
    assert_eq!(w.unowned.count(), 1, "b's recoloured surface is unowned");
    w.sweep(&[]);
    assert_eq!(
        (w.looks.appearance_count(), w.looks.variant_count()),
        (0, 0)
    );
    assert_eq!(w.unowned.count(), 2);
    assert_eq!(w.surfaces.len(), 2, "within the budget they stay");
    w.loader_works();
    assert_eq!(
        w.loader.sent.counts(),
        (0, 0, 2, 0),
        "the loader forgot the looks and variants, not the surfaces held"
    );
}

/// Recoloured surfaces beyond the unowned budget are released, oldest
/// first, and the loader forgets them; UI Only keeps none.
#[test]
fn over_budget_recoloured_surfaces_are_released_and_forgotten() {
    let mut w = Window::new(2);
    let looks: Vec<AppearanceKey> = (0..4)
        .map(|i| look(0x0200_0001 + i, 0x0500_00A0 + i))
        .collect();
    for key in &looks {
        w.show(std::slice::from_ref(key));
        w.sweep(&[]);
    }
    assert_eq!(w.surfaces.len(), 2, "two 1 MiB surfaces fit in 2 MiB");
    let mut kept: Vec<u32> = w.surfaces.values().filter_map(|k| k.1).collect();
    kept.sort_unstable();
    assert_eq!(kept, vec![0x0500_00A2, 0x0500_00A3], "the oldest went");
    w.loader_works();
    assert_eq!(w.loader.sent.counts(), (0, 0, 2, 0));

    w.retain = false;
    w.sweep(&[]);
    assert!(w.surfaces.is_empty(), "UI Only keeps nothing unowned");
    w.loader_works();
    assert_eq!(w.loader.sent.counts(), (0, 0, 0, 0));
}

/// An object coming back after its look was released gets it rebuilt:
/// fresh recoloured surfaces under new handles, a new variant, nothing
/// drawn as stored or missing.
#[test]
fn a_returning_object_gets_its_look_rebuilt_after_eviction() {
    let mut w = Window::new(0);
    let a = look(0x0200_0001, 0x0500_00A0);
    w.show(std::slice::from_ref(&a));
    let old_parts = w.looks.parts(&a).unwrap().0.to_vec();
    let old_handles: Vec<u32> = w.surfaces.keys().copied().collect();
    w.sweep(&[]);
    assert!(w.surfaces.is_empty(), "nothing fits a zero budget");
    w.loader_works();

    w.show(std::slice::from_ref(&a));
    assert_eq!(w.drawn(&a), expected(0x0500_00A0));
    let new_parts = w.looks.parts(&a).unwrap().0;
    assert_ne!(new_parts[0].variant, old_parts[0].variant, "a new variant");
    assert!(
        w.surfaces.keys().all(|h| !old_handles.contains(h)),
        "new handles"
    );
}

/// A look still held when it comes back is drawn as it was, nothing sent
/// again.
#[test]
fn a_look_kept_unowned_is_reused_when_its_object_returns() {
    let mut w = Window::new(64);
    let a = look(0x0200_0001, 0x0500_00A0);
    w.show(std::slice::from_ref(&a));
    w.sweep(&[]);
    w.loader_works();
    w.show(std::slice::from_ref(&a));
    assert_eq!(w.drawn(&a), expected(0x0500_00A0));
    assert_eq!(w.surfaces.len(), 1, "its recoloured surface was reused");
    w.sweep(std::slice::from_ref(&a));
    assert_eq!(w.unowned.count(), 0, "owned again");
}

/// Ids are never given twice, so one released can never name another
/// part's surfaces; a forgotten handle is no longer resized.
#[test]
fn variant_ids_and_handles_are_never_reused() {
    let mut sent = SentLooks::default();
    let first = sent.part_variant([(SURFACE, Some(0x0500_00A0), None)]);
    assert_eq!(first.id, 1);
    let handle = first.new_surfaces[0].1;
    sent.forget(&[], &[first.id], &[handle]);
    assert!(!sent.holds_handle(&(SURFACE, Some(0x0500_00A0), None), handle));
    let second = sent.part_variant([(SURFACE, Some(0x0500_00B0), None)]);
    assert_eq!(second.id, 2, "id 1 is not given again");
    assert_eq!(second.new_surfaces[0].1, handle + 1, "nor its handle");
    // The same look again after its forget: new ids, sent again.
    let again = sent.part_variant([(SURFACE, Some(0x0500_00A0), None)]);
    assert_eq!(again.id, 3);
    assert!(again.new_variant.is_some() && again.new_surfaces.len() == 1);
}

/// Forgetting a recoloured surface forgets the variants naming it (they
/// are rebuilt with its new handle), and a palette no look uses goes.
#[test]
fn a_forgotten_surface_takes_its_variants_and_unused_palettes_along() {
    let mut sent = SentLooks::default();
    let spec = PaletteSpec {
        base_palette_id: 0x0400_0001,
        ranges: Vec::new(),
    };
    let key = AppearanceKey {
        palette: Some(spec.clone()),
        ..look(0x0200_0001, 0x0500_00A0)
    };
    assert!(sent.palette(&spec, || None).is_none());
    let v = sent.part_variant([(SURFACE, None, Some(spec.clone()))]);
    sent.sent(key.clone());
    assert_eq!(sent.counts(), (1, 1, 1, 1));
    sent.forget(std::slice::from_ref(&key), &[], &[]);
    assert_eq!(
        sent.counts(),
        (0, 1, 1, 1),
        "the held surface still uses it"
    );
    sent.forget(&[], &[], &[v.new_surfaces[0].1]);
    assert_eq!(sent.counts(), (0, 0, 0, 0));
}

/// An appearance the loader sent before it read a forget names a variant
/// the window has just released: it is refused, forgotten and asked for
/// again, and arrives whole.
#[test]
fn an_appearance_crossing_a_release_is_refused_and_rebuilt() {
    let mut w = Window::new(0);
    // Two setups recoloured alike share the recoloured surface and variant.
    let (a, b) = (
        look(0x0200_0001, 0x0500_00A0),
        look(0x0200_0002, 0x0500_00A0),
    );
    w.show(std::slice::from_ref(&a));
    // b is asked for and the loader answers it (naming a's variant, sent
    // already) before it reads the release that follows.
    w.ask(std::slice::from_ref(&b));
    w.loader_works();
    w.sweep(std::slice::from_ref(&b));
    assert!(w.surfaces.is_empty() && w.looks.variant_count() == 0);
    w.adopt_all();
    assert!(!w.looks.contains(&b), "b names a released variant: refused");
    w.show(std::slice::from_ref(&b));
    assert_eq!(w.drawn(&b), expected(0x0500_00A0));
}

/// The loader sends a look's recoloured surfaces and variants just ahead
/// of it; a sweep that falls between keeps them for it.
#[test]
fn a_look_arriving_across_a_sweep_keeps_its_variants_and_surfaces() {
    let mut w = Window::new(0);
    w.retain = false;
    let a = look(0x0200_0001, 0x0500_00A0);
    w.ask(std::slice::from_ref(&a));
    w.loader_works();
    let appearance = w.results.pop_back().expect("the appearance");
    w.adopt_all();
    w.sweep(std::slice::from_ref(&a));
    assert!(w.requests.is_empty(), "nothing released ahead of its look");
    w.adopt(appearance);
    assert!(w.requests.is_empty(), "the look arrived whole");
    assert_eq!(w.drawn(&a), expected(0x0500_00A0));
}

/// A texture-detail change the loader acts on before it reads a release
/// sends a recoloured surface the window has just let go: it comes back
/// unowned and is released again, and the look is rebuilt whole when its
/// object returns.
#[test]
fn a_late_detail_resend_of_a_released_surface_is_released_again() {
    let mut w = Window::new(0);
    let a = look(0x0200_0001, 0x0500_00A0);
    w.show(std::slice::from_ref(&a));
    w.loader.change_detail();
    w.sweep(&[]);
    assert!(w.surfaces.is_empty(), "nothing fits a zero budget");
    // The loader sends it again before it reads the release.
    w.loader.resize(&mut w.results);
    w.adopt_all();
    assert_eq!(w.surfaces.len(), 1, "the resend came back");
    w.loader_works();
    w.sweep(&[]);
    assert!(
        w.surfaces.is_empty(),
        "no look owns the resend: released again"
    );
    w.loader_works();
    assert_eq!(w.loader.sent.counts(), (0, 0, 0, 0));
    w.show(std::slice::from_ref(&a));
    assert_eq!(w.drawn(&a), expected(0x0500_00A0));
}

/// A resend the loader comes to after it read the release is not sent.
#[test]
fn a_detail_resend_read_after_the_release_is_skipped() {
    let mut w = Window::new(0);
    let a = look(0x0200_0001, 0x0500_00A0);
    w.show(std::slice::from_ref(&a));
    w.loader.change_detail();
    w.sweep(&[]);
    w.loader_works();
    w.loader.resize(&mut w.results);
    assert!(w.results.is_empty());
}

/// A recoloured surface the window holds, sent again at a new detail, is
/// not mistaken for one arriving ahead of an appearance: unowned it stays
/// unowned while other looks are on their way.
#[test]
fn a_detail_resend_of_a_held_surface_stays_unowned() {
    let mut w = Window::new(64);
    let (a, b) = (
        look(0x0200_0001, 0x0500_00A0),
        look(0x0200_0002, 0x0500_00B0),
    );
    w.show(std::slice::from_ref(&a));
    w.sweep(&[]);
    assert_eq!(w.unowned.count(), 1);
    w.loader_works();
    w.loader.change_detail();
    w.loader.resize(&mut w.results);
    w.adopt_all();
    // b is asked for and not here yet.
    w.ask(std::slice::from_ref(&b));
    w.sweep(std::slice::from_ref(&b));
    assert_eq!(w.unowned.count(), 1, "a's surface is still unowned");
}
