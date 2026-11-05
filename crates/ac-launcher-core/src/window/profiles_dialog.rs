//! The Profiles dialog: every profile name, with New profile and Delete.
//! A profile is a name that accounts and characters carry; it exists, and
//! shows as a filter chip, even with nothing in it. Deleting one asks
//! first, then takes it off every account and character.
//!
//! The window ([`LauncherWindow`](super::LauncherWindow)) fills the list,
//! runs the edits through the profile store and keeps this state.

/// One profile in the dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileListEntry {
    /// The profile's name.
    pub name: String,
    /// "2 accounts, 3 characters", or "empty".
    pub members: String,
}

/// The dialog's state.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ProfilesDialog {
    /// The dialog is open.
    pub is_open: bool,
    /// Every profile, in the filter's order.
    pub entries: Vec<ProfileListEntry>,
    /// The New profile box.
    pub new_name: String,
    /// The profile a Delete asks about.
    pub delete_target: Option<String>,
    /// Why the last edit was refused.
    pub error: Option<String>,
}

impl ProfilesDialog {
    /// The delete question's heading.
    pub fn delete_title(&self) -> String {
        self.delete_target
            .as_ref()
            .map_or_else(String::new, |name| format!("Delete the profile {name}?"))
    }

    /// What deleting the profile takes with it.
    pub fn delete_message(&self) -> String {
        let Some(name) = &self.delete_target else {
            return String::new();
        };
        let members = self
            .entries
            .iter()
            .find(|entry| entry.name.eq_ignore_ascii_case(name))
            .map_or("empty", |entry| entry.members.as_str());
        if members == "empty" {
            format!("The profile {name} has no accounts or characters in it.")
        } else {
            format!(
                "This takes {name} off every account and character in it ({members}). The \
                 accounts and characters themselves stay."
            )
        }
    }
}

/// "2 accounts, 1 character", or "empty".
pub(crate) fn describe_members(accounts: usize, characters: usize) -> String {
    let count = |count: usize, noun: &str| match count {
        0 => None,
        1 => Some(format!("1 {noun}")),
        _ => Some(format!("{count} {noun}s")),
    };
    let parts: Vec<String> = [count(accounts, "account"), count(characters, "character")]
        .into_iter()
        .flatten()
        .collect();
    if parts.is_empty() {
        "empty".into()
    } else {
        parts.join(", ")
    }
}
