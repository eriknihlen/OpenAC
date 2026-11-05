//! Fixed text the client prints on its own: refusals it decides locally
//! (no server round trip) and the options panel's change notices.

/// Jump refused: the player's stance does not allow it.
pub const CANT_JUMP_POSITION: &str = "You can't jump from this position";
/// Jump refused: already airborne.
pub const CANT_JUMP_IN_AIR: &str = "You can't jump while in the air";
/// Jump refused: carrying too much.
pub const CANT_JUMP_LOAD: &str = "You're too loaded down to jump";
/// Jump refused: not enough stamina.
pub const CANT_JUMP_STAMINA: &str = "You're too tired to jump!";
/// Jump refused: jumped a moment ago.
pub const CANT_JUMP_RECENT: &str = "You've jumped too recently!";
/// The secure trade window closed.
pub const TRADE_CANCELLED: &str = "The trade has been cancelled.";
/// Movement refused: out of stamina.
pub const TOO_TIRED: &str = "You are too tired to move!";
/// Sitting refused in combat mode.
pub const CANT_SIT_COMBAT: &str = "You can't sit down while in combat mode";
/// Lying down refused in combat mode.
pub const CANT_LIE_DOWN_COMBAT: &str = "You can't lie down while in combat mode";
/// Crouching refused in combat mode.
pub const CANT_CROUCH_COMBAT: &str = "You can't crouch while in combat mode";
/// Emotes refused in combat mode.
pub const CANT_EMOTE_COMBAT: &str = "You can't use chat emotes in combat mode";
/// Emotes refused from the current stance.
pub const CANT_EMOTE_POSITION: &str = "You can't use chat emotes from this position";
/// A chat-room message refused because chat rooms are off or the room is
/// unknown.
pub const TURBINE_CHAT_UNAVAILABLE: &str = "Turbine chat is not available.";
/// Logging off refused while airborne.
pub const CANT_LOG_OFF_MID_AIR: &str = "Cannot log off while in mid-air.";
/// An attack refused for want of a target.
pub const MUST_SELECT_COMBAT_TARGET: &str =
    "You must select a valid combat target before attacking";

/// The options panel's notice when "mouse turning defaults" reset the
/// camera stiffness.
#[must_use]
pub fn camera_stiffness_changed(from: f32, to: f32) -> String {
    format!("Camera Stiffness was changed from {from:.6} to the mouse turning default of {to:.6}.")
}

/// The options panel's notice when "mouse turning defaults" reset the
/// camera adjustment.
#[must_use]
pub fn camera_adjustment_changed(from: f32, to: f32) -> String {
    format!("Camera Adjustment was changed from {from:.6} to the mouse turning default of {to:.6}.")
}

/// The options panel's notice when "mouse turning defaults" reset the
/// mouse sensitivity.
#[must_use]
pub fn mouse_sensitivity_changed(from: f32, to: f32) -> String {
    format!("Mouse Sensitivity was changed from {from:.6} to the mouse turning default of {to:.6}.")
}

/// The notice when "mouse turning defaults" turned off align-to-slope.
pub const ALIGN_TO_SLOPE_CHANGED: &str =
    "Align To Slope was changed from TRUE to the mouse turning default of FALSE.";

/// The notice when "mouse turning defaults" turned on inverted mouselook.
pub const INVERT_MOUSE_LOOK_AXES_CHANGED: &str =
    "Invert Mouselook Axes was changed from FALSE to the mouse turning default of TRUE.";

/// The notice when "mouse turning defaults" turned on turn-to-face-camera.
pub const TURN_TO_FACE_CAMERA_CHANGED: &str =
    "Turn to Face Camera was changed from FALSE to the mouse turning default of TRUE.";

/// The support site the help requests point at.
pub const SUPPORT_URL: &str =
    "http://support.turbine.com/ics/support/ticketnewwizard.asp?style=classic";

/// Shown when the browser for an urgent-assistance request cannot be opened.
#[must_use]
pub fn urgent_assistance_unavailable() -> String {
    format!(
        "An error occurred while trying to launch your web browser.\n\
         The web site to submit an urgent assistance request is listed below. \
         Please go there to complete your request.\n{SUPPORT_URL}"
    )
}

/// Shown when the browser for an abuse report cannot be opened.
#[must_use]
pub fn report_abuse_unavailable() -> String {
    format!(
        "An error occurred while trying to launch your web browser.\n\
         The web site to submit an abuse report is listed below. \
         Please go there to complete your request.\n{SUPPORT_URL}"
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn camera_notices_use_six_decimals_and_a_period() {
        assert_eq!(
            camera_stiffness_changed(0.5, 1.0),
            "Camera Stiffness was changed from 0.500000 to the mouse turning default of 1.000000."
        );
        assert_eq!(
            mouse_sensitivity_changed(-0.25, 0.1),
            "Mouse Sensitivity was changed from -0.250000 to the mouse turning default of 0.100000."
        );
    }

    #[test]
    fn browser_failure_notices_end_with_the_support_url() {
        let text = urgent_assistance_unavailable();
        assert!(text.starts_with(
            "An error occurred while trying to launch your web browser.\nThe web site to submit an urgent assistance request is listed below. Please go there"
        ));
        assert!(text.ends_with(&format!("complete your request.\n{SUPPORT_URL}")));
        assert!(report_abuse_unavailable().contains("submit an abuse report is listed below. "));
    }
}
