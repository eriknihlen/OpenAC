//! The text and text class the client shows for a server failure code
//! ("weenie error"), with or without a string parameter.
//!
//! 344 codes have text; any other code has none, and the caller shows
//! nothing for it. The text is the game's own, typos included (a literal
//! `$s` in a few templates is not a placeholder).

use super::LogTextType;
use super::client_text;

/// True for the two control-status codes the client never reports: they
/// mark an action that ended normally (`0x3B`, `0x3C`).
#[must_use]
pub fn is_silent_client_control_status(error_code: u32) -> bool {
    matches!(error_code, 0x003B | 0x003C)
}

/// The text for `error_code` with every `%s` replaced by `param`, or `None`
/// when the code has no text. Without a parameter the template comes back
/// as it is.
#[must_use]
pub fn format(error_code: u32, param: Option<&str>) -> Option<String> {
    resolve(error_code, param).map(|(text, _)| text)
}

/// The text (as [`format`]) and the text class for `error_code`, or `None`
/// when the code has no text.
#[must_use]
pub fn resolve(error_code: u32, param: Option<&str>) -> Option<(String, LogTextType)> {
    let (template, kind) = template(error_code)?;
    let text = match param {
        Some(param) if template.contains("%s") => template.replace("%s", param),
        _ => template.to_owned(),
    };
    Some((text, kind))
}

/// The raw template and text class for `error_code`.
#[must_use]
pub fn template(error_code: u32) -> Option<(&'static str, LogTextType)> {
    TABLE
        .binary_search_by_key(&error_code, |&(code, _, _)| code)
        .ok()
        .map(|index| (TABLE[index].1, TABLE[index].2))
}

/// Code, template, text class; sorted by code.
const TABLE: &[(u32, &str, LogTextType)] = &[
    (
        0x17,
        "You failed to go to non-combat mode.",
        LogTextType::ClientLocal,
    ),
    (0x1D, "You're too busy!", LogTextType::ClientLocal),
    (
        0x1E,
        "You must control both objects!",
        LogTextType::ClientLocal,
    ),
    (
        0x20,
        "You must control both objects!",
        LogTextType::ClientLocal,
    ),
    (0x23, "Unable to move to object!", LogTextType::ClientLocal),
    (
        0x24,
        client_text::CANT_JUMP_IN_AIR,
        LogTextType::ClientLocal,
    ),
    (
        0x26,
        "That is not a valid command.",
        LogTextType::ClientLocal,
    ),
    (
        0x28,
        "The item is under someone else's control!",
        LogTextType::ClientLocal,
    ),
    (0x29, "You cannot pick that up!", LogTextType::ClientLocal),
    (
        0x2A,
        "You are too encumbered to carry that!",
        LogTextType::ClientLocal,
    ),
    (0x2B, "%s cannot carry anymore.", LogTextType::Default),
    (0x36, "Action cancelled!", LogTextType::ClientLocal),
    (0x37, "Unable to move to object!", LogTextType::ClientLocal),
    (0x38, "Unable to move to object!", LogTextType::ClientLocal),
    (0x39, "Unable to move to object!", LogTextType::ClientLocal),
    (
        0x3A,
        "You can't do that... you're dead!",
        LogTextType::ClientLocal,
    ),
    (0x3D, "You charged too far!", LogTextType::ClientLocal),
    (
        0x3E,
        "You are too tired to do that!",
        LogTextType::ClientLocal,
    ),
    (
        0x48,
        client_text::CANT_JUMP_POSITION,
        LogTextType::ClientLocal,
    ),
    (0x49, client_text::CANT_JUMP_LOAD, LogTextType::ClientLocal),
    (0x4A, "Ack! You killed yourself!", LogTextType::Default),
    (0x4D, "Invalid PK status!", LogTextType::ClientLocal),
    (
        0x4E,
        "You fail to affect %s because you cannot affect anyone!",
        LogTextType::Magic,
    ),
    (
        0x4F,
        "You fail to affect %s because $s cannot be harmed!",
        LogTextType::Magic,
    ),
    (
        0x50,
        "You fail to affect %s because beneficial spells do not affect %s!",
        LogTextType::Magic,
    ),
    (
        0x51,
        "You fail to affect %s because you are not a player killer!",
        LogTextType::Magic,
    ),
    (
        0x52,
        "You fail to affect %s because %s is not a player killer!",
        LogTextType::Magic,
    ),
    (
        0x53,
        "You fail to affect %s because you are not the same sort of player killer as %s!",
        LogTextType::Magic,
    ),
    (
        0x54,
        "You fail to affect %s because you are acting across a house boundary!",
        LogTextType::Magic,
    ),
    (0x3EE, "The container is closed!", LogTextType::ClientLocal),
    (
        0x3EF,
        "%s is not accepting gifts right now.",
        LogTextType::Default,
    ),
    (
        0x3F1,
        "You failed to go to non-combat mode.",
        LogTextType::ClientLocal,
    ),
    (
        0x3F7,
        "You are too fatigued to attack!",
        LogTextType::ClientLocal,
    ),
    (
        0x3F8,
        "You are out of ammunition!",
        LogTextType::ClientLocal,
    ),
    (
        0x3F9,
        "Your missile attack misfired!",
        LogTextType::ClientLocal,
    ),
    (
        0x3FA,
        "You've attempted an impossible spell path!",
        LogTextType::ClientLocal,
    ),
    (
        0x3FE,
        "You don't know that spell!",
        LogTextType::ClientLocal,
    ),
    (0x3FF, "Incorrect target type", LogTextType::ClientLocal),
    (
        0x400,
        "You don't have all the components for this spell.",
        LogTextType::ClientLocal,
    ),
    (
        0x401,
        "You don't have enough Mana to cast this spell.",
        LogTextType::ClientLocal,
    ),
    (0x402, "Your spell fizzled.", LogTextType::Magic),
    (
        0x403,
        "Your spell's target is missing!",
        LogTextType::ClientLocal,
    ),
    (
        0x404,
        "Your projectile spell mislaunched!",
        LogTextType::ClientLocal,
    ),
    (
        0x407,
        "Your spell cannot be cast outside",
        LogTextType::ClientLocal,
    ),
    (
        0x408,
        "Your spell cannot be cast inside",
        LogTextType::ClientLocal,
    ),
    (
        0x40A,
        "You are unprepared to cast a spell",
        LogTextType::ClientLocal,
    ),
    (
        0x40B,
        "You've already sworn your Allegiance",
        LogTextType::ClientLocal,
    ),
    (
        0x40C,
        "You don't have enough experience available to swear Allegiance",
        LogTextType::ClientLocal,
    ),
    (
        0x413,
        "%s is already one of your followers",
        LogTextType::ClientLocal,
    ),
    (
        0x414,
        "You are not in an allegiance!",
        LogTextType::ClientLocal,
    ),
    (
        0x416,
        "%s cannot have any more Vassals",
        LogTextType::ClientLocal,
    ),
    (
        0x41D,
        "You must be the leader of a Fellowship",
        LogTextType::ClientLocal,
    ),
    (0x41E, "Your Fellowship is full", LogTextType::ClientLocal),
    (
        0x41F,
        "That Fellowship name is not permitted",
        LogTextType::ClientLocal,
    ),
    (
        0x422,
        "That channel doesn't exist.",
        LogTextType::ClientLocal,
    ),
    (
        0x423,
        "You can't use that channel.",
        LogTextType::ClientLocal,
    ),
    (
        0x424,
        "You're already on that channel.",
        LogTextType::ClientLocal,
    ),
    (
        0x425,
        "You're not currently on that channel.",
        LogTextType::ClientLocal,
    ),
    (
        0x427,
        "You cannot merge different stacks!",
        LogTextType::ClientLocal,
    ),
    (
        0x428,
        "You cannot merge enchanted items!",
        LogTextType::ClientLocal,
    ),
    (
        0x429,
        "You must control at least one stack!",
        LogTextType::ClientLocal,
    ),
    (0x432, "Your craft attempt fails.", LogTextType::ClientLocal),
    (0x433, "Your craft attempt fails.", LogTextType::ClientLocal),
    (
        0x434,
        "Given that number of items, you cannot craft anything.",
        LogTextType::ClientLocal,
    ),
    (0x435, "Your craft attempt fails.", LogTextType::ClientLocal),
    (
        0x437,
        "Either you or one of the items involved does not pass the requirements for this craft interaction.",
        LogTextType::ClientLocal,
    ),
    (
        0x438,
        "You do not have all the neccessary items.",
        LogTextType::ClientLocal,
    ),
    (
        0x439,
        "Not all the items are avaliable.",
        LogTextType::ClientLocal,
    ),
    (
        0x43A,
        "You must be at rest in peace mode to do trade skills.",
        LogTextType::ClientLocal,
    ),
    (
        0x43B,
        "You are not trained in that trade skill.",
        LogTextType::ClientLocal,
    ),
    (0x43C, "Your hands must be free.", LogTextType::ClientLocal),
    (0x43D, "You cannot link to that portal!", LogTextType::Magic),
    (
        0x43E,
        "You have solved this quest too recently!",
        LogTextType::Default,
    ),
    (
        0x43F,
        "You have solved this quest too many times!",
        LogTextType::Default,
    ),
    (
        0x445,
        "This item requires you to complete a specific quest before you can pick it up!",
        LogTextType::Default,
    ),
    (
        0x45C,
        "Player killers may not interact with that portal!",
        LogTextType::Magic,
    ),
    (
        0x45D,
        "Non-player killers may not interact with that portal!",
        LogTextType::Magic,
    ),
    (0x45E, "You do not own a house!", LogTextType::ClientLocal),
    (0x45F, "You do not own a house!", LogTextType::ClientLocal),
    (
        0x466,
        "You must purchase Asheron's Call: Dark Majesty to interact with that portal.",
        LogTextType::Magic,
    ),
    (
        0x469,
        "You have used all the hooks you are allowed to use for this house.",
        LogTextType::Default,
    ),
    (
        0x46A,
        "%s doesn't know what to do with that.",
        LogTextType::Default,
    ),
    (
        0x474,
        "You must complete a quest to interact with that portal.",
        LogTextType::Magic,
    ),
    (
        0x47F,
        "You must own a house to use this command.",
        LogTextType::ClientLocal,
    ),
    (
        0x480,
        "Your monarch does not own a mansion or a villa!",
        LogTextType::ClientLocal,
    ),
    (
        0x481,
        "Your monarch does not own a mansion or a villa!",
        LogTextType::ClientLocal,
    ),
    (
        0x482,
        "Your monarch has closed the mansion to the Allegiance.",
        LogTextType::ClientLocal,
    ),
    (
        0x488,
        "You must be above level %s to purchase this dwelling.",
        LogTextType::Default,
    ),
    (
        0x489,
        "You must be at or below level %s to purchase this dwelling.",
        LogTextType::Default,
    ),
    (
        0x48A,
        "You must be a monarch to purchase this dwelling.",
        LogTextType::Default,
    ),
    (
        0x48B,
        "You must be above allegiance rank %s to purchase this dwelling.",
        LogTextType::Default,
    ),
    (
        0x48C,
        "You must be at or below allegiance rank %s to purchase this dwelling.",
        LogTextType::Default,
    ),
    (
        0x48E,
        "Your offer of Allegiance has been ignored.",
        LogTextType::ClientLocal,
    ),
    (
        0x48F,
        "You are already involved in something!",
        LogTextType::ClientLocal,
    ),
    (
        0x490,
        "You must be a monarch to use this command.",
        LogTextType::ClientLocal,
    ),
    (
        0x491,
        "You must specify a character to boot.",
        LogTextType::ClientLocal,
    ),
    (0x492, "You can't boot yourself!", LogTextType::ClientLocal),
    (
        0x493,
        "That character does not exist.",
        LogTextType::ClientLocal,
    ),
    (
        0x494,
        "That person is not a member of your Allegiance!",
        LogTextType::ClientLocal,
    ),
    (
        0x495,
        "No patron from which to break!",
        LogTextType::ClientLocal,
    ),
    (
        0x496,
        "Your Allegiance has been dissolved!",
        LogTextType::Default,
    ),
    (
        0x497,
        "Your patron's Allegiance to you has been broken!",
        LogTextType::Default,
    ),
    (0x498, "You have moved too far!", LogTextType::ClientLocal),
    (
        0x499,
        "That is not a valid destination!",
        LogTextType::ClientLocal,
    ),
    (
        0x49A,
        "You must purchase Asheron's Call -- Dark Majesty to use this function.",
        LogTextType::ClientLocal,
    ),
    (
        0x49B,
        "You fail to link with the lifestone!",
        LogTextType::Magic,
    ),
    (
        0x49C,
        "You wandered too far to link with the lifestone!",
        LogTextType::Magic,
    ),
    (
        0x49D,
        "You successfully link with the lifestone!",
        LogTextType::Magic,
    ),
    (
        0x49E,
        "You must have linked with a lifestone in order to recall to it!",
        LogTextType::Magic,
    ),
    (
        0x49F,
        "You fail to recall to the lifestone!",
        LogTextType::Magic,
    ),
    (
        0x4A0,
        "You fail to link with the portal!",
        LogTextType::Magic,
    ),
    (
        0x4A1,
        "You successfully link with the portal!",
        LogTextType::Magic,
    ),
    (
        0x4A2,
        "You fail to recall to the portal!",
        LogTextType::Magic,
    ),
    (
        0x4A3,
        "You must have linked with a portal in order to recall to it!",
        LogTextType::Magic,
    ),
    (0x4A4, "You fail to summon the portal!", LogTextType::Magic),
    (
        0x4A5,
        "You must have linked with a portal in order to summon it!",
        LogTextType::Magic,
    ),
    (0x4A6, "You fail to teleport!", LogTextType::Magic),
    (
        0x4A7,
        "You have been teleported too recently!",
        LogTextType::Magic,
    ),
    (
        0x4A8,
        "You must be an Advocate to interact with that portal.",
        LogTextType::Magic,
    ),
    (
        0x4AA,
        "Players may not interact with that portal.",
        LogTextType::Magic,
    ),
    (
        0x4AB,
        "You are not powerful enough to interact with that portal!",
        LogTextType::Magic,
    ),
    (
        0x4AC,
        "You are too powerful to interact with that portal!",
        LogTextType::Magic,
    ),
    (
        0x4AD,
        "You cannot recall to that portal!",
        LogTextType::Magic,
    ),
    (0x4AE, "You cannot summon that portal!", LogTextType::Magic),
    (
        0x4AF,
        "The lock is already unlocked.",
        LogTextType::ClientLocal,
    ),
    (
        0x4B0,
        "You can't lock or unlock that!",
        LogTextType::ClientLocal,
    ),
    (
        0x4B1,
        "You can't lock or unlock what is open!",
        LogTextType::ClientLocal,
    ),
    (
        0x4B2,
        "The key doesn't fit this lock.",
        LogTextType::Default,
    ),
    (
        0x4B3,
        "The lock has been used too recently.",
        LogTextType::ClientLocal,
    ),
    (
        0x4B4,
        "You aren't trained in lockpicking!",
        LogTextType::ClientLocal,
    ),
    (
        0x4B5,
        "You must specify a character to query.",
        LogTextType::ClientLocal,
    ),
    (
        0x4B6,
        "Please use the allegiance panel to view your own information.",
        LogTextType::ClientLocal,
    ),
    (
        0x4B7,
        "You have used that command too recently.",
        LogTextType::ClientLocal,
    ),
    (
        0x4B8,
        "You do not own that salvage tool!",
        LogTextType::Default,
    ),
    (
        0x4B9,
        "You do not own that salvage tool!",
        LogTextType::Default,
    ),
    (
        0x4BA,
        "You do not own that salvage tool!",
        LogTextType::Default,
    ),
    (
        0x4BD,
        "You do not own that salvage tool!",
        LogTextType::Default,
    ),
    (0x4BE, "You do not own that item!", LogTextType::Default),
    (
        0x4BF,
        "The %s was not suitable for salvaging.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C0,
        "The %s contains the wrong material.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C1,
        "The material cannot be created.",
        LogTextType::Default,
    ),
    (
        0x4C2,
        "The list of items you are attempting to salvage is invalid.",
        LogTextType::Default,
    ),
    (
        0x4C3,
        "You cannot salvage items that you are trading!",
        LogTextType::Default,
    ),
    (
        0x4C4,
        "You must be a guest in this house to interact with that portal.",
        LogTextType::Magic,
    ),
    (
        0x4C5,
        "Your Allegiance Rank is too low to use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C6,
        "You must be %s to use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C7,
        "Your Arcane Lore skill is too low to use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C8,
        "That item doesn't have enough Mana.",
        LogTextType::ClientLocal,
    ),
    (
        0x4C9,
        "Your %s is too low to use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4CA,
        "Only %s may use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4CB,
        "You must have %s specialized to use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x4CC,
        "You have been involved in a player killer battle too recently to do that!",
        LogTextType::Magic,
    ),
    (
        0x4CE,
        "%s is too busy to accept gifts right now.",
        LogTextType::Default,
    ),
    (
        0x4CF,
        "%s cannot accept stacked objects. Try giving one at a time.",
        LogTextType::Default,
    ),
    (
        0x4D0,
        "You have failed to alter your skill.",
        LogTextType::Default,
    ),
    (
        0x4D1,
        "Your %s skill must be trained, not untrained or specialized, in order to be altered in this way!",
        LogTextType::Default,
    ),
    (
        0x4D2,
        "You do not have enough skill credits to specialize your %s skill.",
        LogTextType::Default,
    ),
    (
        0x4D3,
        "You have too many available experience points to be able to absorb the experience points from your %s skill. Please spend some of your experience points and try again.",
        LogTextType::Default,
    ),
    (
        0x4D4,
        "Your %s skill is already untrained!",
        LogTextType::Default,
    ),
    (
        0x4D5,
        "You are currently wielding items which require a certain level of %s.  Your %s skill cannot be lowered while you are wielding these items.  Please remove these items and try again.",
        LogTextType::Default,
    ),
    (
        0x4D6,
        "You have succeeded in specializing your %s skill!",
        LogTextType::Default,
    ),
    (
        0x4D7,
        "You have succeeded in lowering your %s skill from specialized to trained!",
        LogTextType::Default,
    ),
    (
        0x4D8,
        "You have succeeded in untraining your %s skill!",
        LogTextType::Default,
    ),
    (
        0x4D9,
        "Although you cannot untrain your %s skill, you have succeeded in recovering all the experience you had invested in it.",
        LogTextType::Default,
    ),
    (
        0x4DA,
        "You have too many credits invested in specialized skills already! Before you can specialize your %s skill, you will need to unspecialize some other skill.",
        LogTextType::Default,
    ),
    (
        0x4DD,
        "You have failed to alter your attributes.",
        LogTextType::Default,
    ),
    (0x4DE, "%s", LogTextType::Default),
    (0x4DF, "%s", LogTextType::Default),
    (
        0x4E0,
        "You are currently wielding items which require a certain level of skill. Your attributes cannot be transferred while you are wielding these items. Please remove these items and try again.",
        LogTextType::Default,
    ),
    (
        0x4E1,
        "You have succeeded in transferring your attributes!",
        LogTextType::Default,
    ),
    (
        0x4E2,
        "This hook is a duplicated housing object. You may not add items to a duplicated housing object. Please empty the hook and allow it to reset.",
        LogTextType::Default,
    ),
    (
        0x4E3,
        "That item is of the wrong type to be placed on this hook.",
        LogTextType::Default,
    ),
    (
        0x4E4,
        "This chest is a duplicated housing object. You may not add items to a duplicated housing object. Please empty everything -- including backpacks -- out of the chest and allow the chest to reset.",
        LogTextType::Default,
    ),
    (
        0x4E5,
        "This hook was a duplicated housing object. Since it is now empty, it will be deleted momentarily. Once it is gone, it is safe to use the other, non-duplicated hook that is here.",
        LogTextType::Default,
    ),
    (
        0x4E6,
        "This chest was a duplicated housing object. Since it is now empty, it will be deleted momentarily. Once it is gone, it is safe to use the other, non-duplicated chest that is here.",
        LogTextType::Default,
    ),
    (
        0x4E7,
        "You cannot swear allegiance to anyone because you own a monarch-only house. Please abandon your house and try again.",
        LogTextType::Default,
    ),
    (
        0x4E8,
        "The %s cannot be used while on a hook and only the owner may open the hook.",
        LogTextType::Default,
    ),
    (
        0x4E9,
        "The %s cannot be used while on a hook, use the '@house hooks on' command to make the hook openable.",
        LogTextType::Default,
    ),
    (
        0x4EA,
        "The %s can only be used while on a hook.",
        LogTextType::Default,
    ),
    (
        0x4EB,
        "You can't do that while in the air!",
        LogTextType::ClientLocal,
    ),
    (
        0x4EC,
        "You cannot modify your player killer status while you are recovering from a PK death.",
        LogTextType::Default,
    ),
    (
        0x4ED,
        "Advocates may not change their player killer status!",
        LogTextType::Default,
    ),
    (
        0x4EE,
        "Your level is too low to change your player killer status with this object.",
        LogTextType::Default,
    ),
    (
        0x4EF,
        "Your level is too high to change your player killer status with this object.",
        LogTextType::Default,
    ),
    (
        0x4F0,
        "You feel a harsh dissonance, and you sense that an act of killing you have committed recently is interfering with the conversion.",
        LogTextType::Default,
    ),
    (
        0x4F1,
        "Bael'Zharon's power flows through you again. You are once more a player killer.",
        LogTextType::Default,
    ),
    (
        0x4F2,
        "Bael'Zharon has granted you respite after your moment of weakness. You are temporarily no longer a player killer.",
        LogTextType::Default,
    ),
    (
        0x4F3,
        "Lite Player Killers may not interact with that portal!",
        LogTextType::Magic,
    ),
    (
        0x4F4,
        "%s fails to affect you because $s cannot affect anyone!",
        LogTextType::Magic,
    ),
    (
        0x4F5,
        "%s fails to affect you because you cannot be harmed!",
        LogTextType::Magic,
    ),
    (
        0x4F6,
        "%s fails to affect you because %s is not a player killer!",
        LogTextType::Magic,
    ),
    (
        0x4F7,
        "%s fails to affect you because you are not a player killer!",
        LogTextType::Magic,
    ),
    (
        0x4F8,
        "%s fails to affect you because you are not the same sort of player killer as %s!",
        LogTextType::Magic,
    ),
    (
        0x4F9,
        "%s fails to affect you across a house boundary!",
        LogTextType::Magic,
    ),
    (0x4FA, "%s is an invalid target.", LogTextType::Magic),
    (
        0x4FB,
        "You are an invalid target for the spell of %s.",
        LogTextType::Magic,
    ),
    (
        0x4FC,
        "You aren't trained in healing!",
        LogTextType::ClientLocal,
    ),
    (
        0x4FD,
        "You don't own that healing kit!",
        LogTextType::ClientLocal,
    ),
    (0x4FE, "You can't heal that!", LogTextType::ClientLocal),
    (
        0x4FF,
        "%s is already at full health!",
        LogTextType::ClientLocal,
    ),
    (0x500, "You aren't ready to heal!", LogTextType::ClientLocal),
    (
        0x501,
        "You can only use Healing Kits on player characters.",
        LogTextType::ClientLocal,
    ),
    (
        0x502,
        "The Lifestone's magic protects you from the attack!",
        LogTextType::Magic,
    ),
    (
        0x503,
        "The portal's residual energy protects you from the attack!",
        LogTextType::Magic,
    ),
    (
        0x504,
        "You are enveloped in a feeling of warmth as you are brought back into the protection of the Light. You are once again a Non-Player Killer.",
        LogTextType::Default,
    ),
    (
        0x505,
        "You're too close to your sanctuary!",
        LogTextType::ClientLocal,
    ),
    (
        0x506,
        "You can't do that -- you're trading!",
        LogTextType::ClientLocal,
    ),
    (
        0x507,
        "Only Non-Player Killers may enter PK Lite. Please see @help pklite for more details about this command.",
        LogTextType::Default,
    ),
    (
        0x508,
        "A cold wind touches your heart. You are now a Player Killer Lite.",
        LogTextType::Default,
    ),
    (
        0x509,
        "%s has no appropriate targets equipped for this spell.",
        LogTextType::Magic,
    ),
    (
        0x50A,
        "You have no appropriate targets equipped for %s's spell.",
        LogTextType::Magic,
    ),
    (
        0x50B,
        "%s is now an open fellowship; anyone may recruit new members.",
        LogTextType::Default,
    ),
    (
        0x50C,
        "%s is now a closed fellowship.",
        LogTextType::Default,
    ),
    (
        0x50D,
        "%s is now the leader of this fellowship.",
        LogTextType::Default,
    ),
    (
        0x50E,
        "You have passed leadership of the fellowship to %s",
        LogTextType::Default,
    ),
    (
        0x50F,
        "You do not belong to a Fellowship.",
        LogTextType::ClientLocal,
    ),
    (
        0x510,
        "You may not hook any more %s on your house.  You already have the maximum number of %s hooked or you are not permitted to hook any on your type of house.",
        LogTextType::Default,
    ),
    (
        0x512,
        "You are now using the maximum number of hooks.  You cannot use another hook until you take an item off one of your hooks.",
        LogTextType::Default,
    ),
    (
        0x513,
        "You are no longer using the maximum number of hooks.  You may again add items to your hooks.",
        LogTextType::Default,
    ),
    (
        0x514,
        "You now have the maximum number of %s hooked.  You cannot hook any additional %s until you remove one or more from your house.",
        LogTextType::Default,
    ),
    (
        0x515,
        "You no longer have the maximum number of %s hooked.  You may hook additional %s.",
        LogTextType::Default,
    ),
    (
        0x516,
        "You are not permitted to use that hook.",
        LogTextType::Default,
    ),
    (
        0x517,
        "%s is not close enough to your level.",
        LogTextType::Default,
    ),
    (
        0x518,
        "This fellowship is locked; %s cannot be recruited into the fellowship.",
        LogTextType::Default,
    ),
    (
        0x519,
        "The fellowship is locked, you were not added to the fellowship.",
        LogTextType::Default,
    ),
    (
        0x51A,
        "Only the original owner may use that item's magic.",
        LogTextType::ClientLocal,
    ),
    (
        0x51B,
        "You have entered the %s channel.",
        LogTextType::Default,
    ),
    (0x51C, "You have left the %s channel.", LogTextType::Default),
    (
        0x51E,
        "%s will not receive your message, please use urgent assistance to speak with an in-game representative",
        LogTextType::Default,
    ),
    (0x51F, "Message Blocked: %s", LogTextType::ClientLocal),
    (
        0x520,
        "You cannot add anymore people to the list of players that you can hear.",
        LogTextType::Default,
    ),
    (
        0x521,
        "%s has been added to the list of people you can hear.",
        LogTextType::Default,
    ),
    (
        0x522,
        "%s has been removed from the list of people you can hear.",
        LogTextType::Default,
    ),
    (
        0x523,
        "You are now deaf to player's screams.",
        LogTextType::Default,
    ),
    (
        0x524,
        "You can hear all players once again.",
        LogTextType::Default,
    ),
    (
        0x525,
        "You fail to remove %s from your loud list.",
        LogTextType::Default,
    ),
    (0x526, "You chicken out.", LogTextType::ClientLocal),
    (
        0x527,
        "You cannot posssibly succeed.",
        LogTextType::ClientLocal,
    ),
    (
        0x528,
        "The fellowship is locked; you cannot open locked fellowships.",
        LogTextType::Default,
    ),
    (0x529, "Trade Complete!", LogTextType::ClientLocal),
    (
        0x52A,
        "That is not a salvaging tool.",
        LogTextType::ClientLocal,
    ),
    (
        0x52B,
        "That person is not available now.",
        LogTextType::ClientLocal,
    ),
    (0x52C, "You are now snooping on %s.", LogTextType::Default),
    (
        0x52D,
        "You are no longer snooping on %s.",
        LogTextType::Default,
    ),
    (0x52E, "You fail to snoop on %s.", LogTextType::Default),
    (0x52F, "%s attempted to snoop on you.", LogTextType::Default),
    (
        0x530,
        "%s is already being snooped on, only one person may snoop on another at a time.",
        LogTextType::Default,
    ),
    (
        0x531,
        "%s is in limbo and cannot receive your message.",
        LogTextType::Default,
    ),
    (
        0x532,
        "You must wait 30 days after purchasing a house before you may purchase another with any character on the same account. This applies to all housing except apartments.",
        LogTextType::Default,
    ),
    (
        0x533,
        "You have been booted from your allegiance chat room. Use \"@allegiance chat on\" to rejoin. (%s).",
        LogTextType::Default,
    ),
    (
        0x534,
        "%s has been booted from the allegiance chat room.",
        LogTextType::Default,
    ),
    (
        0x535,
        "You do not have the authority within your allegiance to do that.",
        LogTextType::Default,
    ),
    (
        0x536,
        "The account of %s is already banned from the allegiance.",
        LogTextType::Default,
    ),
    (
        0x537,
        "The account of %s is not banned from the allegiance.",
        LogTextType::Default,
    ),
    (
        0x538,
        "The account of %s was not unbanned from the allegiance.",
        LogTextType::Default,
    ),
    (
        0x539,
        "The account of %s has been banned from the allegiance.",
        LogTextType::Default,
    ),
    (
        0x53A,
        "The account of %s is no longer banned from the allegiance.",
        LogTextType::Default,
    ),
    (0x53B, "Banned Characters:", LogTextType::Default),
    (
        0x53E,
        "%s is banned from the allegiance!",
        LogTextType::Default,
    ),
    (
        0x53F,
        "You are banned from %s's allegiance!",
        LogTextType::Default,
    ),
    (
        0x540,
        "You have the maximum number of accounts banned.!",
        LogTextType::Default,
    ),
    (
        0x541,
        "%s is now an allegiance officer.",
        LogTextType::Default,
    ),
    (
        0x542,
        "An unspecified error occurred while attempting to set %s as an allegiance officer.",
        LogTextType::Default,
    ),
    (
        0x543,
        "%s is no longer an allegiance officer.",
        LogTextType::Default,
    ),
    (
        0x544,
        "An unspecified error occurred while attempting to remove %s as an allegiance officer.",
        LogTextType::Default,
    ),
    (
        0x545,
        "You already have the maximum number of allegiance officers. You must remove some before you add any more.",
        LogTextType::Default,
    ),
    (
        0x546,
        "Your allegiance officers have been cleared.",
        LogTextType::Default,
    ),
    (
        0x547,
        "You must wait %s before communicating again!",
        LogTextType::Default,
    ),
    (
        0x548,
        "You cannot join any chat channels while gagged.",
        LogTextType::Default,
    ),
    (
        0x549,
        "Your allegiance officer status has been modified. You now hold the position of: %s.",
        LogTextType::Default,
    ),
    (
        0x54A,
        "You are no longer an allegiance officer.",
        LogTextType::Default,
    ),
    (
        0x54B,
        "%s is already an allegiance officer of that level.",
        LogTextType::Default,
    ),
    (
        0x54C,
        "Your allegiance does not have a hometown.",
        LogTextType::Default,
    ),
    (
        0x54D,
        "The %s is currently in use.",
        LogTextType::ClientLocal,
    ),
    (
        0x54E,
        "The hook does not contain a usable item. You cannot open the hook because you do not own the house to which it belongs.",
        LogTextType::Default,
    ),
    (
        0x54F,
        "The hook does not contain a usable item. Use the '@house hooks on'command to make the hook openable.",
        LogTextType::Default,
    ),
    (0x550, "Out of Range!", LogTextType::ClientLocal),
    (
        0x551,
        "You are not listening to the %s channel!",
        LogTextType::Default,
    ),
    (
        0x552,
        "You must purchase Asheron's Call -- Throne of Destiny to use this function.",
        LogTextType::ClientLocal,
    ),
    (
        0x553,
        "You must purchase Asheron's Call -- Throne of Destiny to use this item.",
        LogTextType::ClientLocal,
    ),
    (
        0x554,
        "You must purchase Asheron's Call -- Throne of Destiny to use this portal.",
        LogTextType::ClientLocal,
    ),
    (
        0x555,
        "You must purchase Asheron's Call -- Throne of Destiny to access this quest.",
        LogTextType::ClientLocal,
    ),
    (
        0x556,
        "You have failed to complete the augmentation.",
        LogTextType::Default,
    ),
    (
        0x557,
        "You have used this augmentation too many times already.",
        LogTextType::Default,
    ),
    (
        0x558,
        "You have used augmentations of this type too many times already.",
        LogTextType::Default,
    ),
    (
        0x559,
        "You do not have enough unspent experience available to purchase this augmentation.",
        LogTextType::Default,
    ),
    (0x55A, "%s", LogTextType::Default),
    (
        0x55B,
        "Congratulations! You have succeeded in acquiring the %s augmentation.",
        LogTextType::Default,
    ),
    (
        0x55C,
        "Although your augmentation will not allow you to untrain your %s skill, you have succeeded in recovering all the experience you had invested in it.",
        LogTextType::Default,
    ),
    (
        0x55D,
        "You must exit the Training Academy before that command will be available to you.",
        LogTextType::Default,
    ),
    (0x55E, "%s", LogTextType::Default),
    (
        0x55F,
        "Only Player Killer characters may use this command!",
        LogTextType::Default,
    ),
    (
        0x560,
        "Only Player Killer Lite characters may use this command!",
        LogTextType::Default,
    ),
    (
        0x561,
        "You may only have a maximum of 50 friends at once. If you wish to add more friends, you must first remove some.",
        LogTextType::ClientLocal,
    ),
    (
        0x562,
        "%s is already on your friends list!",
        LogTextType::Default,
    ),
    (
        0x563,
        "That character is not on your friends list!",
        LogTextType::Default,
    ),
    (
        0x564,
        "Only the character who owns the house may use this command.",
        LogTextType::Default,
    ),
    (
        0x565,
        "That allegiance name is invalid because it is empty. Please use the @allegiance name clear command to clear your allegiance name.",
        LogTextType::Default,
    ),
    (
        0x566,
        "That allegiance name is too long. Please choose another name.",
        LogTextType::Default,
    ),
    (
        0x567,
        "That allegiance name contains illegal characters. Please choose another name using only letters, spaces, - and '.",
        LogTextType::Default,
    ),
    (
        0x568,
        "That allegiance name is not appropriate. Please choose another name.",
        LogTextType::Default,
    ),
    (
        0x569,
        "That allegiance name is already in use. Please choose another name.",
        LogTextType::Default,
    ),
    (
        0x56A,
        "You may only change your allegiance name once every 24 hours. You may change your allegiance name again in %s.",
        LogTextType::Default,
    ),
    (
        0x56B,
        "Your allegiance name has been cleared.",
        LogTextType::Default,
    ),
    (
        0x56C,
        "That is already the name of your allegiance!",
        LogTextType::Default,
    ),
    (
        0x56D,
        "%s is the monarch and cannot be promoted or demoted.",
        LogTextType::Default,
    ),
    (
        0x56E,
        "That level of allegiance officer is now known as: %s.",
        LogTextType::Default,
    ),
    (
        0x56F,
        "That is an invalid officer level.",
        LogTextType::Default,
    ),
    (
        0x570,
        "That allegiance officer title is not appropriate.",
        LogTextType::Default,
    ),
    (
        0x571,
        "That allegiance name is too long. Please choose another name.",
        LogTextType::Default,
    ),
    (
        0x572,
        "All of your allegiance officer titles have been cleared.",
        LogTextType::Default,
    ),
    (
        0x573,
        "That allegiance title contains illegal characters. Please choose another name using only letters, spaces, - and '.",
        LogTextType::Default,
    ),
    (
        0x574,
        "Your allegiance is currently: %s.",
        LogTextType::Default,
    ),
    (0x575, "Your allegiance is now: %s.", LogTextType::Default),
    (
        0x576,
        "You may not accept the offer of allegiance from %s because your allegiance is locked.",
        LogTextType::Default,
    ),
    (
        0x577,
        "You may not swear allegiance at this time because the allegiance of %s is locked.",
        LogTextType::Default,
    ),
    (
        0x578,
        "You have pre-approved %s to join your allegiance.",
        LogTextType::Default,
    ),
    (
        0x579,
        "You have not pre-approved any vassals to join your allegiance.",
        LogTextType::Default,
    ),
    (
        0x57A,
        "%s is already a member of your allegiance!",
        LogTextType::Default,
    ),
    (
        0x57B,
        "%s has been pre-approved to join your allegiance.",
        LogTextType::Default,
    ),
    (
        0x57C,
        "You have cleared the pre-approved vassal for your allegiance.",
        LogTextType::Default,
    ),
    (
        0x57D,
        "That character is already gagged!",
        LogTextType::Default,
    ),
    (
        0x57E,
        "That character is not currently gagged!",
        LogTextType::Default,
    ),
    (
        0x57F,
        "Your allegiance chat privileges have been temporarily removed by %s. Until they are restored, you may not view or speak in the allegiance chat channel.",
        LogTextType::Default,
    ),
    (
        0x580,
        "%s is now temporarily unable to view or speak in allegiance chat. The gag will run out in 5 minutes, or %s may be explicitly ungagged before then.",
        LogTextType::Default,
    ),
    (
        0x581,
        "Your allegiance chat privileges have been restored.",
        LogTextType::Default,
    ),
    (
        0x582,
        "Your allegiance chat privileges have been restored by %s.",
        LogTextType::Default,
    ),
    (
        0x583,
        "You have restored allegiance chat privileges to %s.",
        LogTextType::Default,
    ),
    (
        0x584,
        "You cannot pick up more of that item!",
        LogTextType::ClientLocal,
    ),
    (
        0x585,
        "You are restricted to clothes and armor created for your race.",
        LogTextType::ClientLocal,
    ),
    (
        0x586,
        "That item was specifically created for another race.",
        LogTextType::ClientLocal,
    ),
    (
        0x587,
        "Olthoi cannot interact with that!",
        LogTextType::Magic,
    ),
    (
        0x588,
        "Olthoi cannot use regular lifestones! Asheron would not allow it!",
        LogTextType::Magic,
    ),
    (
        0x589,
        "The vendor looks at you in horror!",
        LogTextType::Magic,
    ),
    (0x58A, "%s cowers from you!", LogTextType::Default),
    (
        0x58B,
        "As a mindless engine of destruction an Olthoi cannot join a fellowship!",
        LogTextType::Magic,
    ),
    (
        0x58C,
        "The Olthoi only have an allegiance to the Olthoi Queen!",
        LogTextType::Magic,
    ),
    (0x58D, "You cannot use that item!", LogTextType::Magic),
    (
        0x58E,
        "This person will not interact with you!",
        LogTextType::Magic,
    ),
    (
        0x58F,
        "Only Olthoi may pass through this portal!",
        LogTextType::Magic,
    ),
    (
        0x590,
        "Olthoi may not pass through this portal!",
        LogTextType::Magic,
    ),
    (
        0x591,
        "You may not pass through this portal while Vitae weakens you!",
        LogTextType::Magic,
    ),
    (
        0x592,
        "This character must be two weeks old or have been created on an account at least two weeks old to use this portal!",
        LogTextType::Magic,
    ),
    (
        0x593,
        "Olthoi characters can only use Lifestone and PK Arena recalls!",
        LogTextType::Magic,
    ),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn table_is_strictly_sorted_for_binary_search() {
        assert!(TABLE.windows(2).all(|pair| pair[0].0 < pair[1].0));
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Chat/WeenieErrorMessagesTests.cs
    #[test]
    fn entered_and_left_channel_substitute_the_channel_name() {
        assert_eq!(
            format(0x051B, Some("General")).as_deref(),
            Some("You have entered the General channel.")
        );
        for name in ["Trade", "LFG", "Roleplay"] {
            assert_eq!(
                format(0x051B, Some(name)),
                Some(format!("You have entered the {name} channel."))
            );
        }
        assert_eq!(
            format(0x051C, Some("General")).as_deref(),
            Some("You have left the General channel.")
        );
    }

    #[test]
    fn code_0x051d_has_no_text() {
        assert_eq!(format(0x051D, None), None);
    }

    #[test]
    fn plain_rows() {
        let cases: [(u32, &str); 14] = [
            (0x052B, "That person is not available now."),
            (0x0529, "Trade Complete!"),
            (0x0026, "That is not a valid command."),
            (0x0414, "You are not in an allegiance!"),
            (0x050F, "You do not belong to a Fellowship."),
            (0x0036, "Action cancelled!"),
            (0x003D, "You charged too far!"),
            (0x004A, "Ack! You killed yourself!"),
            (0x0550, "Out of Range!"),
            (
                0x0504,
                "You are enveloped in a feeling of warmth as you are brought back into the protection of the Light. You are once again a Non-Player Killer.",
            ),
            (0x0505, "You're too close to your sanctuary!"),
            (
                0x04EC,
                "You cannot modify your player killer status while you are recovering from a PK death.",
            ),
            (
                0x04ED,
                "Advocates may not change their player killer status!",
            ),
            (
                0x04EE,
                "Your level is too low to change your player killer status with this object.",
            ),
        ];
        for (code, expected) in cases {
            assert_eq!(format(code, None).as_deref(), Some(expected), "{code:#06X}");
        }
    }

    #[test]
    fn unknown_codes_have_no_text() {
        assert_eq!(format(0xABCD, None), None);
        assert_eq!(format(0xDEAD, Some("Mana Stone")), None);
        assert_eq!(format(0xCAFE, Some("")), None);
    }

    #[test]
    fn parameterised_templates() {
        assert_eq!(
            format(0x0521, Some("Caith")).as_deref(),
            Some("Caith has been added to the list of people you can hear.")
        );
        assert_eq!(
            format(0x004F, Some("Drudge")).as_deref(),
            Some("You fail to affect Drudge because $s cannot be harmed!")
        );
        assert_eq!(
            format(0x04FF, Some("+Acdream")).as_deref(),
            Some("+Acdream is already at full health!")
        );
    }

    #[test]
    fn the_table_has_exactly_344_rows() {
        let count = (0..=0x600u32)
            .filter(|&id| resolve(id, None).is_some())
            .count();
        assert_eq!(count, 344);
        assert_eq!(TABLE.len(), 344);
    }

    #[test]
    fn code_0x4f8_substitutes_every_placeholder() {
        assert_eq!(
            resolve(0x4F8, Some("Someone")),
            Some((
                "Someone fails to affect you because you are not the same sort of player killer as Someone!".to_owned(),
                LogTextType::Magic
            ))
        );
    }

    #[test]
    fn corrected_rows_match_the_game_binary() {
        use LogTextType::{ClientLocal, Default, Magic};
        let cases: [(u32, &str, LogTextType); 23] = [
            (
                0x04F,
                "You fail to affect %s because $s cannot be harmed!",
                Magic,
            ),
            (0x3EE, "The container is closed!", ClientLocal),
            (0x408, "Your spell cannot be cast inside", ClientLocal),
            (
                0x48A,
                "You must be a monarch to purchase this dwelling.",
                Default,
            ),
            (
                0x4E8,
                "The %s cannot be used while on a hook and only the owner may open the hook.",
                Default,
            ),
            (
                0x051,
                "You fail to affect %s because you are not a player killer!",
                Magic,
            ),
            (
                0x053,
                "You fail to affect %s because you are not the same sort of player killer as %s!",
                Magic,
            ),
            (
                0x054,
                "You fail to affect %s because you are acting across a house boundary!",
                Magic,
            ),
            (
                0x466,
                "You must purchase Asheron's Call: Dark Majesty to interact with that portal.",
                Magic,
            ),
            (
                0x4A3,
                "You must have linked with a portal in order to recall to it!",
                Magic,
            ),
            (0x4B5, "You must specify a character to query.", ClientLocal),
            (
                0x4E0,
                "You are currently wielding items which require a certain level of skill. Your attributes cannot be transferred while you are wielding these items. Please remove these items and try again.",
                Default,
            ),
            (
                0x4F7,
                "%s fails to affect you because you are not a player killer!",
                Magic,
            ),
            (
                0x544,
                "An unspecified error occurred while attempting to remove %s as an allegiance officer.",
                Default,
            ),
            (
                0x54E,
                "The hook does not contain a usable item. You cannot open the hook because you do not own the house to which it belongs.",
                Default,
            ),
            (
                0x552,
                "You must purchase Asheron's Call -- Throne of Destiny to use this function.",
                ClientLocal,
            ),
            (
                0x553,
                "You must purchase Asheron's Call -- Throne of Destiny to use this item.",
                ClientLocal,
            ),
            (
                0x554,
                "You must purchase Asheron's Call -- Throne of Destiny to use this portal.",
                ClientLocal,
            ),
            (
                0x555,
                "You must purchase Asheron's Call -- Throne of Destiny to access this quest.",
                ClientLocal,
            ),
            (
                0x57F,
                "Your allegiance chat privileges have been temporarily removed by %s. Until they are restored, you may not view or speak in the allegiance chat channel.",
                Default,
            ),
            (
                0x582,
                "Your allegiance chat privileges have been restored by %s.",
                Default,
            ),
            (
                0x4E9,
                "The %s cannot be used while on a hook, use the '@house hooks on' command to make the hook openable.",
                Default,
            ),
            (
                0x518,
                "This fellowship is locked; %s cannot be recruited into the fellowship.",
                Default,
            ),
        ];
        for (code, text, kind) in cases {
            assert_eq!(
                resolve(code, None),
                Some((text.to_owned(), kind)),
                "{code:#05X}"
            );
        }
    }

    #[test]
    fn spot_pins_text_and_class() {
        use LogTextType::{ClientLocal, Default, Magic};
        let cases: [(u32, &str, LogTextType); 10] = [
            (0x017, "You failed to go to non-combat mode.", ClientLocal),
            (0x02A, "You are too encumbered to carry that!", ClientLocal),
            (0x4EB, "You can't do that while in the air!", ClientLocal),
            (0x550, "Out of Range!", ClientLocal),
            (0x402, "Your spell fizzled.", Magic),
            (0x49B, "You fail to link with the lifestone!", Magic),
            (
                0x593,
                "Olthoi characters can only use Lifestone and PK Arena recalls!",
                Magic,
            ),
            (0x04A, "Ack! You killed yourself!", Default),
            (0x50C, "%s is now a closed fellowship.", Default),
            (
                0x55F,
                "Only Player Killer characters may use this command!",
                Default,
            ),
        ];
        for (code, text, kind) in cases {
            assert_eq!(
                resolve(code, None),
                Some((text.to_owned(), kind)),
                "{code:#05X}"
            );
        }
    }

    #[test]
    fn jump_family_shares_the_client_refusal_text() {
        let air = Some((client_text::CANT_JUMP_IN_AIR, LogTextType::ClientLocal));
        let position = Some((client_text::CANT_JUMP_POSITION, LogTextType::ClientLocal));
        let load = Some((client_text::CANT_JUMP_LOAD, LogTextType::ClientLocal));
        assert_eq!(template(0x024), air);
        assert_eq!(template(0x048), position);
        assert_eq!(template(0x049), load);
    }

    #[test]
    fn code_0x4f4_keeps_the_dollar_s_typo() {
        assert_eq!(
            resolve(0x4F4, Some("A drudge")),
            Some((
                "A drudge fails to affect you because $s cannot affect anyone!".to_owned(),
                LogTextType::Magic
            ))
        );
    }

    #[test]
    fn fellowship_codes_the_failure_handler_skips_have_no_text() {
        for code in [
            0x0417, 0x0418, 0x0419, 0x041A, 0x041B, 0x041C, 0x04DB, 0x04DC,
        ] {
            assert_eq!(resolve(code, None), None, "{code:#06X}");
        }
    }

    #[test]
    fn silent_control_statuses() {
        assert!(is_silent_client_control_status(0x3B));
        assert!(is_silent_client_control_status(0x3C));
        assert!(!is_silent_client_control_status(0x36));
    }
}
