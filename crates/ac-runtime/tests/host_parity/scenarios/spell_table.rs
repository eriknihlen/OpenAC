//! What a plugin reads about one spell from the client's own spell table,
//! on both arms (OpenAC `Scenarios/SpellTableParityTests.cs`). The table is
//! the session's (its game tables); the arms are given the same one.
//!
//! OpenAC reads the caster and target effects, the formula version, the
//! display order and the component loss; this API's spell record does not
//! carry those five, so the scenario reads the fields it does carry from
//! the same record (Heal Self VI as OpenAC stages it), each asserted.

use std::collections::HashMap;
use std::sync::Arc;

use ac_core::spells::{SpellComponentRequirements, SpellMetadata, SpellTable};
use ac_plugin::abi::calls;
use ac_runtime::gameplay::GameTables;

use crate::host_parity::arm::{Setup, run_with};
use crate::host_parity::world;

const SPELL: u32 = 1640;

// OpenAC `SpellTableParityTests.ASpellsTableFieldsReachAPluginOnBothClients`.
#[test]
fn a_spells_table_fields_reach_a_plugin_on_both_clients() {
    let spell = SpellMetadata {
        school: "Life Magic".into(),
        icon_id: 67,
        mana_cost: 50,
        sort_key: 1417,
        difficulty: 300,
        target_mask: 0x0C,
        generation: 6,
        is_untargeted: true,
        caster_effect: 0x39,
        target_effect: 0x1F,
        formula_version: 1,
        component_loss: 0.4,
        ..SpellMetadata::new(SPELL, "Heal Self VI", 3)
    };
    let tables = GameTables::from_parts(
        SpellTable::create([spell]).unwrap(),
        SpellComponentRequirements::default(),
        HashMap::new(),
    );
    let setup = Setup {
        tables: Some(Arc::new(tables)),
        ..Setup::default()
    };
    run_with(&setup, &[], true, |arm, transcript| {
        world::stage(arm);
        transcript.step("read the spell");
        let spell = arm.call::<calls::SpellsGet>(&SPELL);
        transcript.record("spell", &spell);
        let spell = spell.unwrap_or_else(|| panic!("{}: the spell is in the table", arm.name()));
        assert_eq!(spell.name, "Heal Self VI");
        assert_eq!(spell.family, 3);
        assert_eq!(spell.difficulty, 300);
        assert_eq!(spell.mana_cost, 50);
        assert_eq!(spell.icon_id, 67);
        assert_eq!(spell.target_mask, 0x0C);
        assert!(spell.is_untargeted);
    });
}
