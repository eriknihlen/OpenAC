//! The status board's change notices (API 1.8): a plugin subscribes to
//! another plugin's values and hears [`crate::Event::StatusChanged`] when
//! one of them changes, instead of reading them again and again.
//!
//! A plugin publishes values under keys (`status_publish`) and any plugin
//! reads them by the publisher's id (`status_read`, `status_capture`).
//! A subscriber (`status_subscribe`) hears at most one notice per key per
//! pass of the plugins, carrying the value as it stands at the end of the
//! pass: a publisher writing the same key many times in a pass sends one
//! notice, and a key set and set back within a pass sends none. A
//! subscription may name a plugin that is not loaded yet; it lasts until
//! the subscriber withdraws it (`status_unsubscribe`) or stops. When a
//! publisher stops, its values go, and a subscriber hears each withdrawn;
//! one stopped and started again within a pass is heard as the difference
//! (a value it published again unchanged is not told). Subscribing tells
//! nothing of the values already there, nor of changes made earlier in the
//! pass: read them with `status_capture`.

use crate::wire_record;

/// How many plugins one plugin may subscribe to at once.
pub const MAX_SUBSCRIPTIONS: usize = 64;

wire_record! {
    /// One of a subscribed plugin's values changed.
    pub struct StatusChange {
        /// The plugin that published it (its id in lower case).
        pub owner: String,
        /// The key.
        pub key: String,
        /// The value now; none once it is withdrawn or its plugin stopped.
        pub value: Option<String>,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Wire;

    #[test]
    fn a_change_round_trips() {
        let change = StatusChange {
            owner: "openac.vision".to_owned(),
            key: "target".to_owned(),
            value: Some("Drudge".to_owned()),
        };
        assert_eq!(
            StatusChange::from_bytes(&change.to_bytes()).unwrap(),
            change
        );
    }
}
