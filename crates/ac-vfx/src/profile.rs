//! An effect owner's script and sound defaults: from its Setup for a dat
//! object, replaced by the server's physics description for a live one.

use ac_dat::objects::Setup;

use crate::script_table::{is_physics_script_did, is_physics_script_table_did, is_sound_table_did};

/// The effect fields of the server's physics description (each absent when
/// the server did not send it).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct NetworkEffectDescription {
    /// Physics script table id.
    pub physics_script_table_id: Option<u32>,
    /// Sound table id.
    pub sound_table_id: Option<u32>,
    /// Default script type.
    pub default_script_type: Option<u32>,
    /// Default script intensity.
    pub default_script_intensity: Option<f32>,
}

/// Which script table, sound table and default script an owner uses.
#[derive(Debug, Clone, PartialEq)]
pub struct EntityEffectProfile {
    setup_default_script_did: Option<u32>,
    current_physics_script_table_did: Option<u32>,
    current_sound_table_did: Option<u32>,
    raw_default_script_type: u32,
    default_script_intensity: f32,
    has_network_description: bool,
}

impl EntityEffectProfile {
    /// A dat object's profile from its Setup's default script, script table
    /// and sound table ids (ids of the wrong type read as none).
    #[must_use]
    pub fn dat_static(default_script: u32, script_table: u32, sound_table: u32) -> Self {
        Self {
            setup_default_script_did: is_physics_script_did(default_script)
                .then_some(default_script),
            current_physics_script_table_did: is_physics_script_table_did(script_table)
                .then_some(script_table),
            current_sound_table_did: is_sound_table_did(sound_table).then_some(sound_table),
            raw_default_script_type: 0,
            default_script_intensity: 0.0,
            has_network_description: false,
        }
    }

    /// A dat object's profile: its Setup's defaults stay in force.
    #[must_use]
    pub fn create_dat_static(setup: &Setup) -> Self {
        Self::dat_static(
            setup.default_script,
            setup.default_script_table,
            setup.default_sound_table,
        )
    }

    /// A live object's profile: the Setup's default script, the rest from
    /// the server's description.
    #[must_use]
    pub fn create_live(setup: &Setup, physics: &NetworkEffectDescription) -> Self {
        Self::live(
            setup.default_script,
            setup.default_script_table,
            setup.default_sound_table,
            physics,
        )
    }

    /// [`Self::create_live`] from the Setup's ids.
    #[must_use]
    pub fn live(
        default_script: u32,
        script_table: u32,
        sound_table: u32,
        physics: &NetworkEffectDescription,
    ) -> Self {
        let mut profile = Self::dat_static(default_script, script_table, sound_table);
        profile.apply_network_description(physics);
        profile
    }

    /// Replace every server-owned field. An absent field reads as zero; it
    /// never brings back the Setup's table.
    pub fn apply_network_description(&mut self, physics: &NetworkEffectDescription) {
        let table = physics.physics_script_table_id.unwrap_or(0);
        let sound = physics.sound_table_id.unwrap_or(0);
        self.current_physics_script_table_did = is_physics_script_table_did(table).then_some(table);
        self.current_sound_table_did = is_sound_table_did(sound).then_some(sound);
        self.raw_default_script_type = physics.default_script_type.unwrap_or(0);
        self.default_script_intensity = physics.default_script_intensity.unwrap_or(0.0);
        self.has_network_description = true;
    }

    /// The Setup's default script.
    #[must_use]
    pub fn setup_default_script_did(&self) -> Option<u32> {
        self.setup_default_script_did
    }

    /// The physics script table in force.
    #[must_use]
    pub fn current_physics_script_table_did(&self) -> Option<u32> {
        self.current_physics_script_table_did
    }

    /// The sound table in force.
    #[must_use]
    pub fn current_sound_table_did(&self) -> Option<u32> {
        self.current_sound_table_did
    }

    /// The script type the default-script hook plays.
    #[must_use]
    pub fn raw_default_script_type(&self) -> u32 {
        self.raw_default_script_type
    }

    /// The intensity the default-script hook plays at.
    #[must_use]
    pub fn default_script_intensity(&self) -> f32 {
        self.default_script_intensity
    }

    /// The server has described the object.
    #[must_use]
    pub fn has_network_description(&self) -> bool {
        self.has_network_description
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Vfx/EntityEffectProfileTests.cs`.
    use super::*;

    // The fixture Setup: default script, script table, sound table.
    const SCRIPT: u32 = 0x3300_00AA;
    const TABLE: u32 = 0x3400_00BB;
    const SOUND: u32 = 0x2000_00DD;

    fn live(physics: &NetworkEffectDescription) -> EntityEffectProfile {
        EntityEffectProfile::live(SCRIPT, TABLE, SOUND, physics)
    }

    #[test]
    fn dat_static_retains_setup_direct_script_and_typed_table() {
        let p = EntityEffectProfile::dat_static(SCRIPT, TABLE, SOUND);
        assert_eq!(p.setup_default_script_did(), Some(0x3300_00AA));
        assert_eq!(p.current_physics_script_table_did(), Some(0x3400_00BB));
        assert_eq!(p.current_sound_table_did(), Some(0x2000_00DD));
        assert!(!p.has_network_description());
    }

    #[test]
    fn live_physics_description_without_pe_table_clears_setup_table() {
        let p = live(&NetworkEffectDescription::default());
        assert_eq!(p.setup_default_script_did(), Some(0x3300_00AA));
        assert_eq!(p.current_physics_script_table_did(), None);
        assert_eq!(p.current_sound_table_did(), None);
        assert_eq!(p.raw_default_script_type(), 0);
        assert_eq!(p.default_script_intensity(), 0.0);
        assert!(p.has_network_description());
    }

    #[test]
    fn live_physics_description_replaces_table_and_drives_typed_default() {
        let p = live(&NetworkEffectDescription {
            physics_script_table_id: Some(0x3400_00CC),
            sound_table_id: Some(0x2000_00EE),
            default_script_type: Some(0xA5A5_A5A5),
            default_script_intensity: Some(0.75),
        });
        assert_eq!(p.current_physics_script_table_did(), Some(0x3400_00CC));
        assert_eq!(p.current_sound_table_did(), Some(0x2000_00EE));
        assert_eq!(p.raw_default_script_type(), 0xA5A5_A5A5);
        assert_eq!(p.default_script_intensity(), 0.75);
    }

    #[test]
    fn later_network_description_with_present_zero_does_not_restore_setup() {
        let mut p = live(&NetworkEffectDescription {
            physics_script_table_id: Some(0x3400_00CC),
            default_script_type: Some(7),
            default_script_intensity: Some(1.0),
            ..Default::default()
        });
        p.apply_network_description(&NetworkEffectDescription {
            physics_script_table_id: Some(0),
            sound_table_id: Some(0),
            default_script_type: Some(0),
            default_script_intensity: Some(0.0),
        });
        assert_eq!(p.current_physics_script_table_did(), None);
        assert_eq!(p.current_sound_table_did(), None);
        assert_eq!(p.raw_default_script_type(), 0);
        assert_eq!(p.default_script_intensity(), 0.0);
    }
}
