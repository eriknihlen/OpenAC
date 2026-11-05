//! How see-through a server object is drawn. An object can arrive already
//! translucent (a phantom weapon, a shade) and that value is its floor: a
//! translucency asked for later, such as the camera fading the player out up
//! close, raises it but never takes the object below it.

/// The translucency to draw at: `requested` (0 when none) raised to the
/// object's own (`original`, when it has a finite one).
#[must_use]
pub fn effective(requested: f32, original: Option<f32>) -> f32 {
    let floor = original.filter(|v| v.is_finite()).unwrap_or(0.0);
    if requested < floor { floor } else { requested }
}

/// The translucency the world draw gives object `guid`: the player's fade
/// (`player_fade`) applies to the player only, every object keeps at least
/// its own (`own`).
#[must_use]
pub fn object_translucency(
    guid: u32,
    player_guid: Option<u32>,
    player_fade: f32,
    own: Option<f32>,
) -> f32 {
    if guid == 0 {
        return 0.0;
    }
    let requested = if Some(guid) == player_guid {
        player_fade
    } else {
        0.0
    };
    effective(requested, own)
}

/// The opacity a translucency draws at, 0..=1.
#[must_use]
pub fn opacity(translucency: f32) -> f32 {
    1.0 - translucency.clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    //! OpenAC `ObjectTranslucencyTests` and `LiveObjectTranslucencyTests`
    //! (the examine copy has no counterpart: no examine view yet).
    use super::*;

    #[test]
    fn an_object_is_never_drawn_less_translucent_than_it_arrived() {
        for (requested, original, expected) in [
            (0.0, None, 0.0),
            (0.0, Some(0.5), 0.5),
            (0.2, Some(0.5), 0.5),
            (0.8, Some(0.5), 0.8),
            (1.0, Some(0.5), 1.0),
            (0.3, Some(f32::NAN), 0.3),
        ] {
            assert_eq!(effective(requested, original), expected);
        }
    }

    #[test]
    fn the_player_fades_and_other_objects_keep_their_own_value() {
        const PLAYER: u32 = 0x5000_000A;
        const SHIELD: u32 = 0x8000_1234;
        let own = |guid: u32| (guid == SHIELD).then_some(0.5);
        let for_guid = |guid: u32| object_translucency(guid, Some(PLAYER), 0.8, own(guid));
        assert_eq!(for_guid(PLAYER), 0.8);
        assert_eq!(for_guid(SHIELD), 0.5);
        assert_eq!(for_guid(0x8000_9999), 0.0);
        assert_eq!(for_guid(0), 0.0);
    }

    #[test]
    fn opacity_is_one_minus_the_clamped_translucency() {
        assert_eq!(opacity(0.0), 1.0);
        assert_eq!(opacity(0.25), 0.75);
        assert_eq!(opacity(1.5), 0.0);
        assert_eq!(opacity(-1.0), 1.0);
    }
}
