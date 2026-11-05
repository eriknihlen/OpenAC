//! The permissions a manifest may declare, each allowing the client
//! functions behind it.

/// A permission a manifest declares to be allowed the imports behind it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Permission {
    /// Read chat, filters, interceptors, send and post lines.
    Chat,
    /// HTTP requests and outgoing sockets (to the manifest's hosts or hosts
    /// the player approves).
    Network,
    /// A listening socket.
    Listen,
    /// The shared VTank profiles folder.
    Profiles,
    /// The system clipboard.
    Clipboard,
}

impl Permission {
    /// Every permission of vocabulary version 1, in display order.
    pub const ALL: [Self; 5] = [
        Self::Chat,
        Self::Network,
        Self::Listen,
        Self::Profiles,
        Self::Clipboard,
    ];

    /// The manifest name.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::Chat => "chat",
            Self::Network => "network",
            Self::Listen => "listen",
            Self::Profiles => "profiles",
            Self::Clipboard => "clipboard",
        }
    }

    /// The permission a manifest name stands for (case-insensitive).
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        Self::ALL
            .into_iter()
            .find(|p| p.name().eq_ignore_ascii_case(name.trim()))
    }
}

/// The permission vocabulary version this ABI knows.
pub const PERMISSIONS_VERSION: u32 = 1;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn permission_names_are_the_manifest_vocabulary() {
        assert_eq!(
            Permission::ALL.map(Permission::name),
            ["chat", "network", "listen", "profiles", "clipboard"]
        );
        assert_eq!(Permission::from_name(" Chat "), Some(Permission::Chat));
        assert_eq!(Permission::from_name("fileWrite"), None);
    }
}
