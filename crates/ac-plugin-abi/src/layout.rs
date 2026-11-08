//! The plugin API's layout fingerprint: the shape of everything that
//! crosses the boundary, written into every plugin, so a plugin built
//! against other record layouts is refused at load instead of failing on
//! the first reply that decodes wrongly.
//!
//! The rule: **a record never changes shape silently.** Adding, removing,
//! renaming, reordering or retyping a field, a variant or a tag of any
//! record, reply or event changes its shape, so the fingerprint changes,
//! and the plugins that use it must be rebuilt against the new SDK. The
//! one exception is an addition a later API minor version marks with its
//! version (`since`): a new event kind, or a new field at the end of a
//! record. A plugin built for an older version is compared with the shapes
//! its version had ([`core_shape`], [`Import::shape_at`]), is never sent
//! the event, and is sent the record without the field, so it keeps
//! loading unrebuilt.
//!
//! The SDK writes, into the plugin's `.wasm` ([`LAYOUT_SECTION`]), the
//! shapes it was built with: one for what every plugin decodes (the start
//! record, the events, the callbacks and their answers: [`CORE_SHAPE`]),
//! and one per import of its table ([`Import::shape`]: the name, the
//! request and the reply). The client and `plugincheck` refuse a module
//! whose core shape or import shapes differ from theirs at the API version
//! it was built for ([`core_shape`], [`Import::shape_at`]), or that has no
//! section. A plugin is not refused for an import it does not use, so a
//! new import alone needs no rebuild; nor for an event or a record field a
//! later minor version added.
//!
//! Nothing here is written by hand: each type's [`Wire::SHAPE`] is computed
//! by the compiler from the definition that also writes its encoding
//! ([`crate::wire_record!`] and its siblings), each import's from its table
//! entry, and the section from those.

use crate::callback::{Callback, CallbackAnswer};
use crate::imports::Import;
use crate::types::{Event, StartInfo};
use crate::version::ApiVersion;
use crate::wire::{Shape, Wire};

/// The custom section the SDK writes into every plugin's `.wasm`:
/// [`CORE_SHAPE`], then for each import of the SDK's table the
/// [`import_key`] of its name and its [`Import::shape`], every number a
/// little-endian `u64`.
pub const LAYOUT_SECTION: &str = "openac.layout";

/// The shape of what every plugin decodes or answers whatever it imports:
/// the start record, the events, the callbacks and their answers, at this
/// build's API version (what [`core_shape`] of [`API_CURRENT`](crate::API_CURRENT) answers).
pub const CORE_SHAPE: u64 = Shape::new("openac plugin api core")
    .of(StartInfo::SHAPE)
    .of(Event::SHAPE)
    .of(Callback::SHAPE)
    .of(CallbackAnswer::SHAPE)
    .finish();

/// The core shape a plugin built for API `version` has: the events that
/// version had, and every record in them and in the callbacks as that
/// version knew it ([`Wire::shape_at`]), so an event or a record field a
/// later minor version added does not change an older plugin's core shape
/// and it still loads (the host never sends it an event it cannot read,
/// and sends each record as its version knew it).
#[must_use]
pub fn core_shape(version: ApiVersion) -> u64 {
    Shape::new("openac plugin api core")
        .of(StartInfo::shape_at(version))
        .of(Event::shape_at(version))
        .of(Callback::shape_at(version))
        .of(CallbackAnswer::shape_at(version))
        .finish()
}

/// The [`LAYOUT_SECTION`] an SDK built for API `version` wrote: the core
/// shape and the shape of every import of its table (the imports that
/// version had, each as that version knew it). For [`API_CURRENT`](crate::API_CURRENT) it is
/// [`LAYOUT_SECTION_BYTES`].
#[must_use]
pub fn section_at(version: ApiVersion) -> Vec<u8> {
    let mut out = core_shape(version).to_le_bytes().to_vec();
    for import in Import::ALL {
        if import.since().is_at_most(version) {
            out.extend_from_slice(&import_key(import.name()).to_le_bytes());
            out.extend_from_slice(&import.shape_at(version).to_le_bytes());
        }
    }
    out
}

/// The whole layout in one number, for people (reports, the published
/// value the ABI's tests pin): [`CORE_SHAPE`] and every import's shape, in
/// any order (the table's order does not matter to a plugin).
pub const LAYOUT_FINGERPRINT: u64 = fingerprint();

/// The length of [`LAYOUT_SECTION_BYTES`].
pub const LAYOUT_SECTION_LEN: usize = 8 + 16 * Import::ALL.len();

/// The contents of [`LAYOUT_SECTION`] for this build of the ABI.
pub const LAYOUT_SECTION_BYTES: [u8; LAYOUT_SECTION_LEN] = section();

/// The key an import's name has in [`LAYOUT_SECTION`].
#[must_use]
pub const fn import_key(name: &str) -> u64 {
    Shape::new("import name").name(name).finish()
}

const fn fingerprint() -> u64 {
    // Each import is mixed on its own and the results summed, so the sum
    // does not depend on the table's order.
    let mut imports: u64 = 0;
    let mut at = 0;
    while at < Import::ALL.len() {
        imports = imports.wrapping_add(mix(Import::ALL[at].shape()));
        at += 1;
    }
    Shape::new("openac plugin api layout")
        .of(CORE_SHAPE)
        .of(imports)
        .finish()
}

/// Spreads every bit of `value` over the whole word (the SplitMix64
/// finaliser), so a sum of mixed values keeps each value's bits.
const fn mix(value: u64) -> u64 {
    let mut x = value;
    x ^= x >> 30;
    x = x.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    x ^= x >> 27;
    x = x.wrapping_mul(0x94d0_49bb_1331_11eb);
    x ^ (x >> 31)
}

const fn put(out: &mut [u8; LAYOUT_SECTION_LEN], at: usize, value: u64) {
    let bytes = value.to_le_bytes();
    let mut i = 0;
    while i < 8 {
        out[at + i] = bytes[i];
        i += 1;
    }
}

const fn section() -> [u8; LAYOUT_SECTION_LEN] {
    let mut out = [0u8; LAYOUT_SECTION_LEN];
    put(&mut out, 0, CORE_SHAPE);
    let mut at = 0;
    while at < Import::ALL.len() {
        let import = Import::ALL[at];
        put(&mut out, 8 + 16 * at, import_key(import.name()));
        put(&mut out, 16 + 16 * at, import.shape());
        at += 1;
    }
    out
}

/// A plugin's [`LAYOUT_SECTION`], read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginLayout {
    /// The plugin's [`CORE_SHAPE`].
    pub core: u64,
    /// Its imports' shapes by [`import_key`], sorted by key.
    imports: Vec<(u64, u64)>,
}

impl PluginLayout {
    /// Reads a section: a core shape and whole `(key, shape)` pairs.
    #[must_use]
    pub fn parse(bytes: &[u8]) -> Option<Self> {
        let (core, rest) = bytes.split_first_chunk::<8>()?;
        if rest.len() % 16 != 0 {
            return None;
        }
        let word = |chunk: &[u8]| u64::from_le_bytes(chunk.try_into().unwrap_or([0; 8]));
        let mut imports: Vec<(u64, u64)> = rest
            .chunks_exact(16)
            .map(|pair| (word(&pair[..8]), word(&pair[8..])))
            .collect();
        imports.sort_unstable();
        Some(Self {
            core: u64::from_le_bytes(*core),
            imports,
        })
    }

    /// The shape the plugin was built with for the import named `name`;
    /// none when its SDK did not have it.
    #[must_use]
    pub fn import_shape(&self, name: &str) -> Option<u64> {
        let key = import_key(name);
        self.imports
            .binary_search_by_key(&key, |&(k, _)| k)
            .ok()
            .map(|at| self.imports[at].1)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::API_CURRENT;
    use crate::{wire_enum, wire_record, wire_union};

    /// The fingerprint this ABI was last published with. A failure here
    /// means a record, reply, event or import changed: allowed while API
    /// 1.0 is unfrozen, but never silently. Update this value in the same
    /// commit; when an existing record changed shape (not only a new
    /// import, event or `since`-marked field), say in the commit message
    /// which plugins must be rebuilt. API 1.6 (canvas hover and leave)
    /// published the same value as 1.5: it added two values of a pointer
    /// event's kind byte, not a shape. API 1.7 (plugin services, the
    /// server's last position on the world-object records) changed it,
    /// API 1.8 (the status board's change notices) again, API 1.9 (a
    /// creature's appraisal profile on the item properties) again, and API
    /// 1.10 (every enchantment on the character, an import) again.
    const PUBLISHED: u64 = 0x4f98_d3ca_2e58_145f;

    /// The fingerprint API 1.7 was published with. A released version's
    /// layout never changes: what a later version adds is marked `since`.
    const PUBLISHED_1_7: u64 = 0x018f_6e7f_ded2_7246;

    /// The fingerprint API 1.8 was published with.
    const PUBLISHED_1_8: u64 = 0xce10_d6e2_b705_0b25;

    /// The fingerprint API 1.9 was published with.
    const PUBLISHED_1_9: u64 = 0xc38c_91a4_a884_79fa;

    /// API 1.9 is published: it keeps the fingerprint it was published
    /// with, whatever later versions add.
    #[test]
    fn api_1_9_keeps_its_published_fingerprint() {
        let v1_9 = fingerprint_at(ApiVersion::new(1, 9));
        assert_eq!(
            v1_9, PUBLISHED_1_9,
            "API 1.9's layout changed (fingerprint {v1_9:016x}, published {PUBLISHED_1_9:016x}): \
             mark what was added `since` the version that adds it"
        );
    }

    /// The import API 1.10 added: every enchantment on the character.
    const API_1_10_ENCHANTMENTS: [Import; 1] = [Import::CharacterAllEnchantments];

    /// API 1.10 added only an import, marked `since`: a plugin built for
    /// API 1.9 keeps this version's core shape, lacks the import, and has
    /// every other import as it is now.
    #[test]
    fn api_1_9_plugins_keep_their_core_and_import_shapes() {
        let v1_9 = ApiVersion::new(1, 9);
        assert_eq!(core_shape(v1_9), CORE_SHAPE);
        let section = PluginLayout::parse(&section_at(v1_9)).unwrap();
        for import in Import::ALL {
            let new = API_1_10_ENCHANTMENTS.contains(import);
            assert_eq!(import.since() == ApiVersion::new(1, 10), new, "{import:?}");
            let expected = (!new).then(|| import.shape());
            assert_eq!(section.import_shape(import.name()), expected, "{import:?}");
        }
        assert_ne!(section_at(v1_9), LAYOUT_SECTION_BYTES);
    }

    /// API 1.8 is published: it keeps the fingerprint it was published
    /// with, whatever later versions add.
    #[test]
    fn api_1_8_keeps_its_published_fingerprint() {
        let v1_8 = fingerprint_at(ApiVersion::new(1, 8));
        assert_eq!(
            v1_8, PUBLISHED_1_8,
            "API 1.8's layout changed (fingerprint {v1_8:016x}, published {PUBLISHED_1_8:016x}): \
             mark what was added `since` the version that adds it"
        );
    }

    /// The core shape every plugin built for API 1.8 carries.
    const API_1_8_CORE: u64 = 0x10f9_c342_fe3f_bffe;

    /// The calls API 1.9 changed, with the shape each had in API 1.8: the
    /// item properties gained a creature's appraisal profile, and these
    /// answer with them or (the loot classifier's) carry them.
    const API_1_8_CHANGED: [(Import, u64); 6] = [
        (Import::WorldProperties, 0x3a3e_b4d1_0afa_9d45),
        (Import::ItemsProperties, 0x52a0_c22a_9d22_8185),
        (Import::LootProperties, 0x2d29_ef7b_9a3a_ccbe),
        (Import::LootClassify, 0xc69b_a16d_59fa_81e0),
        (Import::LootNeedsIdentification, 0xb506_b9d8_938c_7c88),
        (Import::VendorProperties, 0x3e25_6437_dc41_c6b0),
    ];

    /// An import's shape as API 1.8 had it: its shape before API 1.9
    /// changed it, else its shape now.
    fn shape_before_1_9(import: Import) -> u64 {
        API_1_8_CHANGED
            .iter()
            .find(|(i, _)| *i == import)
            .map_or_else(|| import.shape(), |(_, shape)| *shape)
    }

    /// API 1.9 added a field at the end of the item properties, which
    /// changes the core shape (a loot classifier's question carries them)
    /// and the calls in [`API_1_8_CHANGED`]. A plugin built for API 1.8
    /// keeps its own shapes for all of them, and every other import it has
    /// is as it was.
    #[test]
    fn api_1_8_plugins_keep_their_core_and_import_shapes() {
        let v1_8 = ApiVersion::new(1, 8);
        assert_eq!(core_shape(v1_8), API_1_8_CORE);
        assert_ne!(CORE_SHAPE, API_1_8_CORE);
        for (import, shape) in API_1_8_CHANGED {
            assert_eq!(import.shape_at(v1_8), shape, "{import:?}");
            assert_ne!(import.shape(), shape, "{import:?}");
        }
        let section = PluginLayout::parse(&section_at(v1_8)).unwrap();
        for import in Import::ALL {
            let expected = import
                .since()
                .is_at_most(v1_8)
                .then(|| shape_before_1_9(*import));
            assert_eq!(section.import_shape(import.name()), expected, "{import:?}");
        }
    }

    /// [`LAYOUT_FINGERPRINT`] as an SDK built for API `version` had it: its
    /// core shape and its imports, each as that version knew it.
    fn fingerprint_at(version: ApiVersion) -> u64 {
        let imports = Import::ALL
            .iter()
            .filter(|import| import.since().is_at_most(version))
            .fold(0u64, |sum, import| {
                sum.wrapping_add(mix(import.shape_at(version)))
            });
        Shape::new("openac plugin api layout")
            .of(core_shape(version))
            .of(imports)
            .finish()
    }

    /// API 1.7 is published: it keeps the fingerprint it was published
    /// with, whatever later versions add.
    #[test]
    fn api_1_7_keeps_its_published_fingerprint() {
        assert_eq!(fingerprint_at(API_CURRENT), LAYOUT_FINGERPRINT);
        let v1_7 = fingerprint_at(ApiVersion::new(1, 7));
        assert_eq!(
            v1_7, PUBLISHED_1_7,
            "API 1.7's layout changed (fingerprint {v1_7:016x}, published {PUBLISHED_1_7:016x}): \
             mark what was added `since` the version that adds it"
        );
    }

    /// The core shape every plugin built for API 1.7 carries.
    const API_1_7_CORE: u64 = 0x22ad_a6ef_92b2_144c;

    /// The imports API 1.8 added for the status board's change notices.
    const API_1_8_STATUS: [Import; 2] = [Import::StatusSubscribe, Import::StatusUnsubscribe];

    /// API 1.8 added the status board's change notices: an event and two
    /// imports, each marked `since`. A plugin built for API 1.7 keeps its
    /// core shape, has neither import (nor one a later version added), and
    /// every other import it has is as it was.
    #[test]
    fn api_1_7_plugins_keep_their_core_and_import_shapes() {
        let v1_7 = ApiVersion::new(1, 7);
        assert_eq!(core_shape(v1_7), API_1_7_CORE);
        assert_ne!(CORE_SHAPE, API_1_7_CORE);
        let section = PluginLayout::parse(&section_at(v1_7)).unwrap();
        for import in Import::ALL {
            let new = API_1_8_STATUS.contains(import);
            assert_eq!(import.since() == ApiVersion::new(1, 8), new, "{import:?}");
            let expected = import
                .since()
                .is_at_most(v1_7)
                .then(|| shape_before_1_9(*import));
            assert_eq!(section.import_shape(import.name()), expected, "{import:?}");
        }
    }

    /// The core shape every plugin built for API 1.5 carries, and API 1.6's.
    const API_1_5_CORE: u64 = 0x8bc5_7806_eb44_8b6f;

    /// API 1.6 added only the pointer kinds hover and leave, which the host
    /// sends only to plugins built for 1.6 or later: no record, event or
    /// import changed shape, so a plugin built for 1.5 has 1.6's core shape
    /// and section. API 1.7 changed both, so neither is this version's.
    #[test]
    fn api_1_5_plugins_have_the_api_1_6_shapes() {
        let (v1_5, v1_6) = (ApiVersion::new(1, 5), ApiVersion::new(1, 6));
        assert_eq!(core_shape(v1_5), API_1_5_CORE);
        assert_eq!(core_shape(v1_6), API_1_5_CORE);
        assert_eq!(section_at(v1_5), section_at(v1_6));
        assert_ne!(section_at(v1_6), LAYOUT_SECTION_BYTES);
    }

    #[test]
    fn the_fingerprint_is_the_published_one() {
        assert_eq!(
            LAYOUT_FINGERPRINT, PUBLISHED,
            "the plugin API layout changed (fingerprint {LAYOUT_FINGERPRINT:016x}, published \
             {PUBLISHED:016x}): update PUBLISHED and rebuild the plugins it affects"
        );
    }

    /// The core shape every plugin built for API 1.0 carries. An addition
    /// in a later minor version must leave it as it was, or every 1.0
    /// plugin stops loading.
    const API_1_0_CORE: u64 = 0x7d62_9f69_c4d5_e3b3;

    #[test]
    fn api_1_0_plugins_keep_their_core_shape() {
        assert_eq!(core_shape(ApiVersion::new(1, 0)), API_1_0_CORE);
        assert_ne!(CORE_SHAPE, API_1_0_CORE);
        assert_eq!(core_shape(API_CURRENT), CORE_SHAPE);
    }

    /// The core shape every plugin built for API 1.1 carries (an event
    /// that version added, records as they were then).
    const API_1_1_CORE: u64 = 0x30da_79e6_987e_c597;

    /// The import shapes an API 1.1 SDK wrote for the calls whose records
    /// API 1.2 gave a field (the house hook's item types on the inventory
    /// and world-object records); every other import is as it was.
    const API_1_1_IMPORTS: [(Import, u64); 11] = [
        (Import::WorldObjects, 0x001b_e2ad_09b2_7238),
        (Import::WorldObject, 0x1579_5703_0d8b_7bcc),
        (Import::ItemsOwned, 0xed66_6352_01bd_02ed),
        (Import::LootContents, 0xf000_6f51_7695_a450),
        (Import::LootClassify, 0x62f1_8b7c_4679_4184),
        (Import::LootNeedsIdentification, 0x99d6_7d8a_8f61_8fa8),
        (Import::LootNotifyLooted, 0x4014_883b_f67a_6245),
        (Import::UiTheme, API_1_3_UI_THEME),
        (Import::UiCanvasPaint, API_1_3_UI_CANVAS_PAINT),
        (Import::UiPanelRegister, API_1_3_UI_PANEL_REGISTER),
        (
            Import::UiPanelRegisterTyped,
            API_1_3_UI_PANEL_REGISTER_TYPED,
        ),
    ];

    #[test]
    fn api_1_1_plugins_keep_their_core_and_import_shapes() {
        let v1_1 = ApiVersion::new(1, 1);
        assert_eq!(core_shape(v1_1), API_1_1_CORE);
        assert_ne!(CORE_SHAPE, API_1_1_CORE);
        for (import, shape) in API_1_1_IMPORTS {
            assert_eq!(import.shape_at(v1_1), shape, "{import:?}");
            assert_ne!(import.shape(), shape, "{import:?}");
        }
        for import in Import::ALL {
            if import.since().is_at_most(v1_1)
                && !API_1_1_IMPORTS.iter().any(|(i, _)| i == import)
                && !API_1_4_CHANGED.iter().any(|(i, _)| i == import)
            {
                assert_eq!(
                    import.shape_at(v1_1),
                    shape_before_1_7(*import),
                    "{import:?}"
                );
            }
        }
        for (import, shape) in API_1_4_CHANGED {
            assert_eq!(import.shape_at(v1_1), shape, "{import:?}");
        }
    }

    /// The core shape every plugin built for API 1.2 or 1.3 carries (the
    /// theme kinds as they were then).
    const API_1_3_CORE: u64 = 0xd3a8_7cd5_8663_029c;

    /// The shape an API 1.3 SDK wrote for the call that answers the theme,
    /// whose kind API 1.4 gave more variants.
    const API_1_3_UI_THEME: u64 = 0x93b3_e387_5bf3_f44c;

    /// The shape an API 1.3 SDK wrote for painting a canvas, whose text API
    /// 1.4 gave a font.
    const API_1_3_UI_CANVAS_PAINT: u64 = 0xf6ff_2420_525c_7a85;

    /// The shapes an API 1.3 SDK wrote for registering a window, to which API
    /// 1.4 added the window's own dock icon.
    const API_1_3_UI_PANEL_REGISTER: u64 = 0x0686_b6f5_994b_6ffa;
    const API_1_3_UI_PANEL_REGISTER_TYPED: u64 = 0x1c74_d74c_9019_e501;

    /// The calls API 1.4 changed, with the shape each had before.
    const API_1_3_CHANGED: [(Import, u64); 4] = [
        (Import::UiTheme, API_1_3_UI_THEME),
        (Import::UiCanvasPaint, API_1_3_UI_CANVAS_PAINT),
        (Import::UiPanelRegister, API_1_3_UI_PANEL_REGISTER),
        (
            Import::UiPanelRegisterTyped,
            API_1_3_UI_PANEL_REGISTER_TYPED,
        ),
    ];

    /// The imports API 1.4 added: not in an older plugin's section at all.
    const API_1_4_FONTS: [Import; 5] = [
        Import::UiFontBundled,
        Import::UiFontLoad,
        Import::UiFontMeasure,
        Import::UiFontRelease,
        Import::UiFontCount,
    ];

    /// The core shape every plugin built for API 1.4 carries (a path
    /// preview's plan without the grid it reused or the worker's time).
    const API_1_4_CORE: u64 = 0xc98c_762b_1ce4_4a3c;

    /// The calls API 1.5 changed, with the shape each had before: the
    /// indoor cells gained their doorways and the cells seen from them, and
    /// the floorplan's cells their bounds and floor height.
    const API_1_4_CHANGED: [(Import, u64); 2] = [
        (Import::DungeonIndoorCells, 0xca99_6144_2871_7333),
        (Import::DungeonFloorplan, 0xa845_70db_d1b5_681c),
    ];

    /// API 1.5 added fields at the end of a path preview's plan, which
    /// changes the core shape, and to the records of the calls in
    /// [`API_1_4_CHANGED`]: an API 1.4 plugin keeps its own shapes for all
    /// of them, and every other import it has is as it was.
    #[test]
    fn api_1_4_plugins_keep_their_core_and_import_shapes() {
        let v1_4 = ApiVersion::new(1, 4);
        assert_eq!(core_shape(v1_4), API_1_4_CORE);
        assert_ne!(CORE_SHAPE, API_1_4_CORE);
        for (import, shape) in API_1_4_CHANGED {
            assert_eq!(import.shape_at(v1_4), shape, "{import:?}");
            assert_ne!(import.shape(), shape, "{import:?}");
        }
        let section = PluginLayout::parse(&section_at(v1_4)).unwrap();
        for import in Import::ALL {
            let expected = import
                .since()
                .is_at_most(v1_4)
                .then(|| import.shape_at(v1_4));
            assert_eq!(section.import_shape(import.name()), expected, "{import:?}");
            if expected.is_some() && !API_1_4_CHANGED.iter().any(|(i, _)| i == import) {
                assert_eq!(
                    import.shape_at(v1_4),
                    shape_before_1_7(*import),
                    "{import:?}"
                );
            }
        }
    }

    /// The imports API 1.5 added for drawing the landscape: not in an
    /// older plugin's section at all, so a plugin built for 1.4 or before
    /// loads as it did.
    const API_1_5_LANDSCAPE: [Import; 2] =
        [Import::LandscapeTerrain, Import::LandscapeTerrainPalette];

    #[test]
    fn the_landscape_imports_are_new_in_api_1_5() {
        let v1_4 = PluginLayout::parse(&section_at(ApiVersion::new(1, 4))).unwrap();
        let v1_5 = PluginLayout::parse(&section_at(ApiVersion::new(1, 5))).unwrap();
        for import in API_1_5_LANDSCAPE {
            assert_eq!(import.since(), ApiVersion::new(1, 5), "{import:?}");
            assert_eq!(v1_4.import_shape(import.name()), None, "{import:?}");
            assert_eq!(
                v1_5.import_shape(import.name()),
                Some(import.shape()),
                "{import:?}"
            );
        }
        assert_eq!(core_shape(ApiVersion::new(1, 4)), API_1_4_CORE);
    }

    /// The calls API 1.7 changed, with the shape each had in API 1.5 and
    /// 1.6: the world-object and hostile-target records gained the server's
    /// last position.
    const API_1_6_CHANGED: [(Import, u64); 3] = [
        (Import::WorldObjects, 0x2b96_526a_8808_fe9b),
        (Import::WorldObject, 0x9034_777b_f0a1_0329),
        (Import::CombatHostileTargets, 0x4011_5314_7d89_c991),
    ];

    /// An import's shape as API 1.6 had it: its shape before API 1.7
    /// changed it, else its shape before API 1.9 did.
    fn shape_before_1_7(import: Import) -> u64 {
        API_1_6_CHANGED
            .iter()
            .find(|(i, _)| *i == import)
            .map_or_else(|| shape_before_1_9(import), |(_, shape)| *shape)
    }

    /// The imports API 1.7 added for plugin services.
    const API_1_7_SERVICES: [Import; 4] = [
        Import::ServiceRegister,
        Import::ServiceUnregister,
        Import::Services,
        Import::ServiceCall,
    ];

    /// API 1.7 added a field at the end of the world-object and
    /// hostile-target records, which changes the core shape (an event
    /// carries a world object) and the calls in [`API_1_6_CHANGED`], and
    /// plugin services: their imports, and the event, the callback and the
    /// answer they carry, each marked `since`. A plugin built for API 1.5
    /// or 1.6 keeps its own shapes for all of them, has none of the new
    /// imports, and every other import it has is as it was.
    #[test]
    fn api_1_5_and_1_6_plugins_keep_their_core_and_import_shapes() {
        let current = PluginLayout::parse(&section_at(API_CURRENT)).unwrap();
        for version in [ApiVersion::new(1, 5), ApiVersion::new(1, 6)] {
            assert_eq!(core_shape(version), API_1_5_CORE, "{version}");
            assert_ne!(CORE_SHAPE, API_1_5_CORE);
            for (import, shape) in API_1_6_CHANGED {
                assert_eq!(import.shape_at(version), shape, "{import:?} {version}");
                assert_ne!(import.shape(), shape, "{import:?}");
            }
            let section = PluginLayout::parse(&section_at(version)).unwrap();
            for import in Import::ALL {
                let expected = import
                    .since()
                    .is_at_most(version)
                    .then(|| shape_before_1_7(*import));
                assert_eq!(
                    section.import_shape(import.name()),
                    expected,
                    "{import:?} {version}"
                );
            }
        }
        for import in API_1_7_SERVICES {
            assert_eq!(import.since(), ApiVersion::new(1, 7), "{import:?}");
            assert_eq!(
                current.import_shape(import.name()),
                Some(import.shape()),
                "{import:?}"
            );
        }
    }

    /// API 1.3 added only imports (the world triangles and markers): an API
    /// 1.2 plugin's section leaves those out, and its other shapes are an API
    /// 1.3 plugin's.
    #[test]
    fn api_1_2_plugins_keep_their_core_and_import_shapes() {
        let v1_2 = ApiVersion::new(1, 2);
        assert_eq!(core_shape(v1_2), API_1_3_CORE);
        let new_imports = [
            Import::UiTrianglesCreate,
            Import::UiTrianglesSet,
            Import::UiTrianglesRemove,
            Import::UiMarkersCreate,
            Import::UiMarkersSetIcons,
            Import::UiMarkersSetShapes,
            Import::UiMarkersRemove,
        ];
        let section = PluginLayout::parse(&section_at(v1_2)).unwrap();
        for import in Import::ALL {
            let absent = new_imports.contains(import)
                || API_1_4_FONTS.contains(import)
                || !import.since().is_at_most(v1_2);
            let expected = (!absent).then(|| import.shape_at(v1_2));
            assert_eq!(section.import_shape(import.name()), expected, "{import:?}");
        }
        for (import, shape) in API_1_3_CHANGED {
            assert_eq!(import.shape_at(v1_2), shape, "{import:?}");
        }
    }

    /// API 1.4 gave the theme's kind more variants, a canvas's text a font and
    /// a window its own dock icon, which changes the core shape and the calls
    /// that carry them: an API 1.3 plugin keeps those, and has no font imports.
    #[test]
    fn api_1_3_plugins_keep_their_core_and_import_shapes() {
        let v1_3 = ApiVersion::new(1, 3);
        assert_eq!(core_shape(v1_3), API_1_3_CORE);
        assert_ne!(CORE_SHAPE, API_1_3_CORE);
        for (import, shape) in API_1_3_CHANGED {
            assert_eq!(import.shape_at(v1_3), shape, "{import:?}");
            assert_ne!(import.shape(), shape, "{import:?}");
        }
        let section = PluginLayout::parse(&section_at(v1_3)).unwrap();
        for import in Import::ALL {
            if API_1_4_FONTS.contains(import) {
                assert_eq!(section.import_shape(import.name()), None, "{import:?}");
            } else if !API_1_3_CHANGED.iter().any(|(changed, _)| changed == import)
                && !API_1_4_CHANGED.iter().any(|(changed, _)| changed == import)
            {
                assert_eq!(
                    import.shape_at(v1_3),
                    shape_before_1_7(*import),
                    "{import:?}"
                );
            }
        }
        for (import, shape) in API_1_4_CHANGED {
            assert_eq!(import.shape_at(v1_3), shape, "{import:?}");
        }
    }

    #[test]
    fn the_section_at_this_version_is_the_one_the_sdk_writes() {
        assert_eq!(section_at(API_CURRENT), LAYOUT_SECTION_BYTES);
        let older = PluginLayout::parse(&section_at(ApiVersion::new(1, 1))).unwrap();
        assert_eq!(older.core, API_1_1_CORE);
        assert_eq!(
            older.import_shape(Import::WorldObjects.name()),
            Some(API_1_1_IMPORTS[0].1)
        );
    }

    mod before {
        crate::wire_record! {
            /// A corpse as an older SDK knew it.
            pub struct Loot {
                /// Its id.
                pub id: u32,
                /// Its name.
                pub name: String,
            }
        }
    }

    mod after {
        crate::wire_record! {
            /// The same corpse with a field added at the end.
            pub struct Loot {
                /// Its id.
                pub id: u32,
                /// Its name.
                pub name: String,
                /// Whether its appraisal was answered.
                pub answered: bool,
            }
        }
    }

    mod renamed {
        crate::wire_record! {
            /// The same fields, one renamed.
            pub struct Loot {
                /// Its id.
                pub object_id: u32,
                /// Its name.
                pub name: String,
            }
        }
    }

    mod reordered {
        crate::wire_record! {
            /// The same fields in another order.
            pub struct Loot {
                /// Its name.
                pub name: String,
                /// Its id.
                pub id: u32,
            }
        }
    }

    mod retyped {
        crate::wire_record! {
            /// The same fields, one of another type.
            pub struct Loot {
                /// Its id.
                pub id: u64,
                /// Its name.
                pub name: String,
            }
        }
    }

    wire_record! {
        /// A reply holding the older record, as `loot_corpses` does.
        pub struct OldReply {
            /// The corpses.
            pub corpses: Vec<Option<before::Loot>>,
        }
    }

    wire_record! {
        /// The same reply holding the newer record.
        pub struct OldReplyNewLoot {
            /// The corpses.
            pub corpses: Vec<Option<after::Loot>>,
        }
    }

    #[test]
    fn any_change_to_a_record_changes_its_shape() {
        let shapes = [
            before::Loot::SHAPE,
            after::Loot::SHAPE,
            renamed::Loot::SHAPE,
            reordered::Loot::SHAPE,
            retyped::Loot::SHAPE,
        ];
        for (i, a) in shapes.iter().enumerate() {
            for b in &shapes[i + 1..] {
                assert_ne!(a, b);
            }
        }
    }

    #[test]
    fn a_change_inside_a_record_reaches_everything_that_holds_it() {
        assert_ne!(OldReply::SHAPE, OldReplyNewLoot::SHAPE);
        assert_ne!(Vec::<before::Loot>::SHAPE, Vec::<after::Loot>::SHAPE);
        assert_ne!(Vec::<u32>::SHAPE, Option::<u32>::SHAPE);
        assert_ne!(<(u32, String)>::SHAPE, <(String, u32)>::SHAPE);
        assert_ne!(
            Result::<before::Loot, String>::SHAPE,
            Result::<after::Loot, String>::SHAPE
        );
    }

    mod marked {
        crate::wire_record! {
            /// The same corpse with a field a later minor version added.
            pub struct Loot {
                /// Its id.
                pub id: u32,
                /// Its name.
                pub name: String,
                /// Whether its appraisal was answered.
                since (1, 7) pub answered: bool,
            }
        }
    }

    // A field marked `since` reads, for a plugin built before it, exactly
    // as the record did then: the same shape, the same bytes, and what such
    // a plugin sends reads with the field at its default. Inside a list
    // and an option the same holds.
    #[test]
    fn a_field_marked_since_is_left_out_for_older_plugins() {
        let older = ApiVersion::new(1, 6);
        let newer = ApiVersion::new(1, 7);
        const { assert!(marked::Loot::VERSIONED) };
        const { assert!(!before::Loot::VERSIONED) };
        assert_eq!(marked::Loot::shape_at(older), before::Loot::SHAPE);
        assert_eq!(marked::Loot::shape_at(newer), after::Loot::SHAPE);
        assert_eq!(marked::Loot::SHAPE, after::Loot::SHAPE);
        assert_eq!(
            Vec::<Option<marked::Loot>>::shape_at(older),
            Vec::<Option<before::Loot>>::SHAPE
        );
        assert_ne!(OldReply::SHAPE, OldReplyNewLoot::SHAPE);

        let loot = marked::Loot {
            id: 7,
            name: "Corpse".into(),
            answered: true,
        };
        let list = vec![Some(loot.clone()), None];
        let sent = list.to_bytes_at(older);
        assert_eq!(
            Vec::<Option<before::Loot>>::from_bytes(&sent).unwrap(),
            vec![
                Some(before::Loot {
                    id: 7,
                    name: "Corpse".into(),
                }),
                None
            ]
        );
        assert_eq!(list.to_bytes_at(newer), list.to_bytes());

        let from_older = before::Loot {
            id: 7,
            name: "Corpse".into(),
        }
        .to_bytes();
        assert_eq!(
            marked::Loot::from_bytes_at(&from_older, older).unwrap(),
            marked::Loot {
                answered: false,
                ..loot.clone()
            }
        );
        assert_eq!(
            marked::Loot::from_bytes_at(&loot.to_bytes(), newer).unwrap(),
            loot
        );
        // Bytes from a plugin of the newer version are not read as the
        // older shape.
        assert!(marked::Loot::from_bytes_at(&loot.to_bytes(), older).is_err());
    }

    wire_enum! {
        /// An enum.
        pub enum Two: "two" {
            /// First.
            A = 0,
            /// Second.
            B = 1,
        }
    }

    wire_enum! {
        /// The same enum with a variant added.
        pub enum Three: "three" {
            /// First.
            A = 0,
            /// Second.
            B = 1,
            /// Third.
            C = 2,
        }
    }

    wire_enum! {
        /// The same variants, one tag moved.
        pub enum Moved: "moved" {
            /// First.
            A = 0,
            /// Second.
            B = 2,
        }
    }

    wire_union! {
        /// A union.
        pub enum Small: "small" {
            /// Nothing.
            0 => Empty,
            /// A number.
            1 => Number(u32),
        }
    }

    wire_union! {
        /// The same union, its payload retyped.
        pub enum SmallRetyped: "small" {
            /// Nothing.
            0 => Empty,
            /// A number.
            1 => Number(i32),
        }
    }

    #[test]
    fn variants_tags_and_payloads_are_part_of_the_shape() {
        assert_ne!(Two::SHAPE, Three::SHAPE);
        assert_ne!(Two::SHAPE, Moved::SHAPE);
        assert_ne!(Small::SHAPE, SmallRetyped::SHAPE);
    }

    #[test]
    fn every_import_has_its_own_shape_and_key() {
        let mut shapes: Vec<u64> = Import::ALL.iter().map(|i| i.shape()).collect();
        shapes.sort_unstable();
        shapes.dedup();
        assert_eq!(shapes.len(), Import::ALL.len());
        let mut keys: Vec<u64> = Import::ALL.iter().map(|i| import_key(i.name())).collect();
        keys.sort_unstable();
        keys.dedup();
        assert_eq!(keys.len(), Import::ALL.len());
    }

    #[test]
    fn the_section_reads_back_as_this_layout() {
        let layout = PluginLayout::parse(&LAYOUT_SECTION_BYTES).unwrap();
        assert_eq!(layout.core, CORE_SHAPE);
        for import in Import::ALL {
            assert_eq!(
                layout.import_shape(import.name()),
                Some(import.shape()),
                "{import:?}"
            );
        }
        assert_eq!(layout.import_shape("no_such_import"), None);
        // A section from an SDK whose table had fewer imports reads too.
        let older = PluginLayout::parse(&LAYOUT_SECTION_BYTES[..8 + 16]).unwrap();
        assert_eq!(
            older.import_shape(Import::ALL[0].name()),
            Some(Import::ALL[0].shape())
        );
        assert_eq!(older.import_shape(Import::ALL[1].name()), None);
    }

    #[test]
    fn a_malformed_section_is_none() {
        assert_eq!(PluginLayout::parse(b""), None);
        assert_eq!(PluginLayout::parse(b"1.0"), None);
        assert_eq!(PluginLayout::parse(&LAYOUT_SECTION_BYTES[..9]), None);
        assert_eq!(PluginLayout::parse(&LAYOUT_SECTION_BYTES[..8 + 15]), None);
    }
}
