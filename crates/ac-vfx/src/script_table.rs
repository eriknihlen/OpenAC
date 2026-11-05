//! Physics script tables: an object's table maps a script type ("hit",
//! "level up", "portal out", ...) and an intensity to one physics script.

use std::fmt;
use std::sync::Arc;

use ac_dat::objects::PhysicsScriptTable;

use crate::dats::VfxDats;

/// True for a physics script id (`0x33......`; the high byte is the type,
/// the other 24 bits the index).
#[must_use]
pub fn is_physics_script_did(did: u32) -> bool {
    did & 0xFF00_0000 == 0x3300_0000
}

/// True for a physics script table id (`0x34......`).
#[must_use]
pub fn is_physics_script_table_did(did: u32) -> bool {
    did & 0xFF00_0000 == 0x3400_0000
}

/// True for a sound table id (`0x20......`).
#[must_use]
pub fn is_sound_table_did(did: u32) -> bool {
    did & 0xFF00_0000 == 0x2000_0000
}

/// The script `table` (whose file id is `table_did`) plays for
/// `raw_script_type` at `intensity`: the first entry of that type, in
/// stored order, whose modifier is at least the intensity. `None` for a
/// table whose stored id differs, a missing type, an intensity above every
/// entry (NaN included) or an entry that is not a script id.
#[must_use]
pub fn select_script(
    table: &PhysicsScriptTable,
    table_did: u32,
    raw_script_type: u32,
    intensity: f32,
) -> Option<u32> {
    if table.id != table_did {
        return None;
    }
    let (_, data) = table
        .script_table
        .iter()
        .find(|(script_type, _)| *script_type == raw_script_type)?;
    // Stored order, never sorted: equal thresholds pick the first. IEEE
    // comparison: NaN passes no entry, -inf takes the first.
    let entry = data.scripts.iter().find(|e| intensity <= e.modifier)?;
    is_physics_script_did(entry.script_id).then_some(entry.script_id)
}

/// A table that failed to load.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TableLoadError(pub String);

impl fmt::Display for TableLoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for TableLoadError {}

type TableLoader =
    Box<dyn Fn(u32) -> Result<Option<Arc<PhysicsScriptTable>>, TableLoadError> + Send + Sync>;

/// Loads tables and picks scripts from them.
pub struct PhysicsScriptTableResolver {
    load: TableLoader,
}

impl fmt::Debug for PhysicsScriptTableResolver {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PhysicsScriptTableResolver")
            .finish_non_exhaustive()
    }
}

impl PhysicsScriptTableResolver {
    /// A resolver loading tables through `load`.
    pub fn new(
        load: impl Fn(u32) -> Result<Option<Arc<PhysicsScriptTable>>, TableLoadError>
        + Send
        + Sync
        + 'static,
    ) -> Self {
        Self {
            load: Box::new(load),
        }
    }

    /// A resolver loading tables from the dats.
    #[must_use]
    pub fn with_dats(dats: Arc<dyn VfxDats>) -> Self {
        Self::new(move |id| {
            dats.physics_script_table(id)
                .map_err(|e| TableLoadError(e.to_string()))
        })
    }

    /// The script for `raw_script_type` at `intensity` in table
    /// `table_did` (see [`select_script`]). A table that fails to load is
    /// an error, for the caller to report with its owner; it plays nothing.
    pub fn resolve(
        &self,
        table_did: u32,
        raw_script_type: u32,
        intensity: f32,
    ) -> Result<Option<u32>, TableLoadError> {
        if !is_physics_script_table_did(table_did) {
            return Ok(None);
        }
        let Some(table) = (self.load)(table_did)? else {
            return Ok(None);
        };
        Ok(select_script(&table, table_did, raw_script_type, intensity))
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.Core.Tests/Vfx/PhysicsScriptTableResolverTests.cs`.
    use super::*;
    use ac_dat::objects::{PhysicsScriptTableData, ScriptAndModData};

    const TABLE_DID: u32 = 0x3400_0005;
    const RAW_TYPE: u32 = 0x16;

    fn build_table(entries: &[(f32, u32)]) -> Arc<PhysicsScriptTable> {
        Arc::new(PhysicsScriptTable {
            id: TABLE_DID,
            script_table: vec![(
                RAW_TYPE,
                PhysicsScriptTableData {
                    scripts: entries
                        .iter()
                        .map(|&(modifier, script_id)| ScriptAndModData {
                            modifier,
                            script_id,
                        })
                        .collect(),
                },
            )],
        })
    }

    fn resolver_for(table: Arc<PhysicsScriptTable>) -> PhysicsScriptTableResolver {
        PhysicsScriptTableResolver::new(move |id| Ok((id == table.id).then(|| Arc::clone(&table))))
    }

    #[test]
    fn uses_first_stored_upper_threshold_without_sorting() {
        let r = resolver_for(build_table(&[(1.0, 0x3300_0011), (0.0, 0x3300_0022)]));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, -0.25), Ok(Some(0x3300_0011)));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 0.0), Ok(Some(0x3300_0011)));
    }

    #[test]
    fn equality_and_duplicate_threshold_choose_first() {
        let r = resolver_for(build_table(&[
            (0.5, 0x3300_0031),
            (0.5, 0x3300_0032),
            (1.0, 0x3300_0033),
        ]));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 0.5), Ok(Some(0x3300_0031)));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 0.75), Ok(Some(0x3300_0033)));
    }

    #[test]
    fn preserves_retail_ieee_comparison_boundaries() {
        let r = resolver_for(build_table(&[(0.0, 0x3300_0041), (1.0, 0x3300_0042)]));
        assert_eq!(
            r.resolve(TABLE_DID, RAW_TYPE, f32::NEG_INFINITY),
            Ok(Some(0x3300_0041))
        );
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, f32::INFINITY), Ok(None));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, f32::NAN), Ok(None));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 1.0001), Ok(None));
    }

    #[test]
    fn missing_or_invalid_inputs_return_no_script() {
        let r = resolver_for(build_table(&[(1.0, 0x3200_0001)]));
        assert_eq!(r.resolve(0, RAW_TYPE, 0.0), Ok(None));
        assert_eq!(r.resolve(0x3500_0001, RAW_TYPE, 0.0), Ok(None));
        assert_eq!(r.resolve(TABLE_DID, 0xA5A5_A5A5, 0.0), Ok(None));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 0.0), Ok(None));
    }

    #[test]
    fn did_validation_uses_retail_high_byte_and_accepts_large_indexes() {
        assert!(is_physics_script_did(0x3301_0000));
        assert!(is_physics_script_did(0x33FF_FFFF));
        assert!(is_physics_script_table_did(0x3401_0000));
        assert!(is_physics_script_table_did(0x34FF_FFFF));
        assert!(!is_physics_script_did(0x3401_0000));
        assert!(!is_physics_script_table_did(0x3301_0000));
    }

    #[test]
    fn accepts_high_index_table_and_script_ids() {
        let mut table = (*build_table(&[(1.0, 0x3301_0000)])).clone();
        table.id = 0x3401_0000;
        let r = resolver_for(Arc::new(table));
        assert_eq!(r.resolve(0x3401_0000, RAW_TYPE, 0.0), Ok(Some(0x3301_0000)));
    }

    #[test]
    fn rejects_mismatched_embedded_table_id() {
        let mut table = (*build_table(&[(1.0, 0x3300_0051)])).clone();
        table.id = 0x3400_0006;
        let table = Arc::new(table);
        let r = PhysicsScriptTableResolver::new(move |_| Ok(Some(Arc::clone(&table))));
        assert_eq!(r.resolve(TABLE_DID, RAW_TYPE, 0.0), Ok(None));
    }

    #[test]
    fn loader_failure_returns_no_play_and_preserves_the_error() {
        let r =
            PhysicsScriptTableResolver::new(|_| Err(TableLoadError("fixture DAT failure".into())));
        assert_eq!(
            r.resolve(TABLE_DID, RAW_TYPE, 0.0),
            Err(TableLoadError("fixture DAT failure".into()))
        );
    }
}
