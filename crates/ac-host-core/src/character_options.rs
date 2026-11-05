//! The character options a session document may declare.
//!
//! A character's options live on the server, so declaring one changes the
//! character itself. Only options an unattended session genuinely needs are
//! declarable: what it accepts from others, what it does in a fight, and which
//! chat channels it listens to. Options about drawing or panels are left out
//! on purpose: meaningless without a window, and a surprise for a player whose
//! character was borrowed for a run. Both hosts read this one list, so a
//! document means the same thing whichever host starts it.

/// One declarable character option. The document spells it exactly as the
/// variant is named.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[allow(missing_docs)]
pub enum CharacterOption {
    IgnoreAllegianceRequests,
    IgnoreFellowshipRequests,
    IgnoreTradeRequests,
    AllowGive,
    FellowshipShareXP,
    AcceptLootPermits,
    FellowshipShareLoot,
    FellowshipAutoAcceptRequests,
    DisplayAllegianceLogonNotifications,
    UseChargeAttack,
    UseCraftSuccessDialog,
    AutoRepeatAttack,
    LeadMissileTargets,
    UseFastMissiles,
    ConfirmVolatileRareUse,
    AppearOffline,
    ListenToAllegianceChat,
    ListenToGeneralChat,
    ListenToTradeChat,
    ListenToLFGChat,
    ListenToRoleplayChat,
    ListenToSocietyChat,
    MainPackPreferred,
    ToggleRun,
    AutoTarget,
    SalvageMultiple,
}

impl CharacterOption {
    /// Every declarable option.
    pub const ALL: [Self; 26] = [
        Self::IgnoreAllegianceRequests,
        Self::IgnoreFellowshipRequests,
        Self::IgnoreTradeRequests,
        Self::AllowGive,
        Self::FellowshipShareXP,
        Self::AcceptLootPermits,
        Self::FellowshipShareLoot,
        Self::FellowshipAutoAcceptRequests,
        Self::DisplayAllegianceLogonNotifications,
        Self::UseChargeAttack,
        Self::UseCraftSuccessDialog,
        Self::AutoRepeatAttack,
        Self::LeadMissileTargets,
        Self::UseFastMissiles,
        Self::ConfirmVolatileRareUse,
        Self::AppearOffline,
        Self::ListenToAllegianceChat,
        Self::ListenToGeneralChat,
        Self::ListenToTradeChat,
        Self::ListenToLFGChat,
        Self::ListenToRoleplayChat,
        Self::ListenToSocietyChat,
        Self::MainPackPreferred,
        Self::ToggleRun,
        Self::AutoTarget,
        Self::SalvageMultiple,
    ];

    /// The name a document uses.
    pub const fn name(self) -> &'static str {
        match self {
            Self::IgnoreAllegianceRequests => "IgnoreAllegianceRequests",
            Self::IgnoreFellowshipRequests => "IgnoreFellowshipRequests",
            Self::IgnoreTradeRequests => "IgnoreTradeRequests",
            Self::AllowGive => "AllowGive",
            Self::FellowshipShareXP => "FellowshipShareXP",
            Self::AcceptLootPermits => "AcceptLootPermits",
            Self::FellowshipShareLoot => "FellowshipShareLoot",
            Self::FellowshipAutoAcceptRequests => "FellowshipAutoAcceptRequests",
            Self::DisplayAllegianceLogonNotifications => "DisplayAllegianceLogonNotifications",
            Self::UseChargeAttack => "UseChargeAttack",
            Self::UseCraftSuccessDialog => "UseCraftSuccessDialog",
            Self::AutoRepeatAttack => "AutoRepeatAttack",
            Self::LeadMissileTargets => "LeadMissileTargets",
            Self::UseFastMissiles => "UseFastMissiles",
            Self::ConfirmVolatileRareUse => "ConfirmVolatileRareUse",
            Self::AppearOffline => "AppearOffline",
            Self::ListenToAllegianceChat => "ListenToAllegianceChat",
            Self::ListenToGeneralChat => "ListenToGeneralChat",
            Self::ListenToTradeChat => "ListenToTradeChat",
            Self::ListenToLFGChat => "ListenToLFGChat",
            Self::ListenToRoleplayChat => "ListenToRoleplayChat",
            Self::ListenToSocietyChat => "ListenToSocietyChat",
            Self::MainPackPreferred => "MainPackPreferred",
            Self::ToggleRun => "ToggleRun",
            Self::AutoTarget => "AutoTarget",
            Self::SalvageMultiple => "SalvageMultiple",
        }
    }

    /// The option a document name stands for; exact spelling only.
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|option| option.name() == name)
    }
}

/// What is wrong with a declared map, in words for whoever wrote the
/// document, or `None` when it is fine. `declared` is in document order.
pub fn describe(session_id: &str, declared: &[(String, bool)]) -> Option<String> {
    for (name, _) in declared {
        if CharacterOption::from_name(name).is_none() {
            return Some(format!(
                "Session '{session_id}' characterOptions declares '{name}', which is not a \
                 declarable character option name."
            ));
        }
    }
    let is_true = |option: CharacterOption| {
        declared
            .iter()
            .any(|(name, value)| *value && name == option.name())
    };
    if is_true(CharacterOption::IgnoreFellowshipRequests)
        && is_true(CharacterOption::FellowshipAutoAcceptRequests)
    {
        return Some(format!(
            "Session '{session_id}' characterOptions declares both 'IgnoreFellowshipRequests' \
             and 'FellowshipAutoAcceptRequests' as true; the two exclude one another, so the \
             combination is unsatisfiable -- turning one on always clears the other."
        ));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_round_trip_and_are_unique() {
        for option in CharacterOption::ALL {
            assert_eq!(CharacterOption::from_name(option.name()), Some(option));
        }
        let mut names: Vec<_> = CharacterOption::ALL.iter().map(|o| o.name()).collect();
        names.sort_unstable();
        names.dedup();
        assert_eq!(names.len(), 26);
    }

    #[test]
    fn lookup_is_exact() {
        assert_eq!(CharacterOption::from_name("autotarget"), None);
        assert_eq!(CharacterOption::from_name("ShowHelm"), None);
    }
}
