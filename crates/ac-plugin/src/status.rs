//! The session's status board (plugin conversion plan G3): a plugin
//! publishes values under keys, any plugin of the session reads them by the
//! publisher's id (MossTank publishes its `metaState`, MosswartMassacre
//! shows it). Scoped to the session; a plugin's values go when it stops.
//!
//! A plugin may subscribe to another's values (API 1.8): the changes are
//! gathered over a pass of the plugins and taken once at its end
//! ([`StatusBoard::take_changes`]), one per key with the value as it then
//! stands, so a key published in a loop costs each subscriber one notice
//! a pass (a publisher still sends one per key it changed).

use std::collections::{BTreeMap, BTreeSet};

use ac_plugin_abi::status::{MAX_SUBSCRIPTIONS, StatusChange};

/// A value's place: (publisher id in lower case, key).
type Slot = (String, String);

/// The values, by (publisher id in lower case, key).
#[derive(Debug, Default)]
pub struct StatusBoard {
    values: BTreeMap<Slot, String>,
    /// The values changed since the changes were last taken, each with
    /// what it was then.
    before: BTreeMap<Slot, Option<String>>,
    /// Subscriber id to the publishers it hears, both in lower case.
    subscriptions: BTreeMap<String, BTreeSet<String>>,
    /// For a subscription made since the changes were last taken: each
    /// key of its publisher already changed then, by (subscriber,
    /// publisher, key), with its value at the subscription, so a change
    /// made before it is not told.
    started: BTreeMap<(String, String, String), Option<String>>,
}

impl StatusBoard {
    /// `owner` publishes `value` under `key` (none withdraws it); false
    /// for a blank key.
    pub fn publish(&mut self, owner: &str, key: &str, value: Option<String>) -> bool {
        if key.trim().is_empty() {
            return false;
        }
        let slot = (owner.to_lowercase(), key.to_owned());
        let was = match value {
            Some(value) => self.values.insert(slot.clone(), value),
            None => self.values.remove(&slot),
        };
        // Held only while it differs from the value at the pass's start, or
        // a subscription made since then holds another value for it.
        let start = self.before.entry(slot.clone()).or_insert(was);
        if self.values.get(&slot) == start.as_ref() && !self.started_on(&slot) {
            self.before.remove(&slot);
        }
        true
    }

    /// Whether a subscription made since the changes were last taken holds
    /// the value `slot` had then.
    fn started_on(&self, (owner, key): &Slot) -> bool {
        self.started.keys().any(|(_, o, k)| o == owner && k == key)
    }

    /// What `owner` published under `key`.
    #[must_use]
    pub fn read(&self, owner: &str, key: &str) -> Option<String> {
        self.values
            .get(&(owner.to_lowercase(), key.to_owned()))
            .cloned()
    }

    /// Everything `owner` published, by key.
    #[must_use]
    pub fn capture(&self, owner: &str) -> Vec<(String, String)> {
        let owner = owner.to_lowercase();
        self.values
            .iter()
            .filter(|((o, _), _)| *o == owner)
            .map(|((_, k), v)| (k.clone(), v.clone()))
            .collect()
    }

    /// `subscriber` hears `owner`'s changes from now on; false for a blank
    /// owner, its own, or a subscription past [`MAX_SUBSCRIPTIONS`].
    pub fn subscribe(&mut self, subscriber: &str, owner: &str) -> bool {
        let owner = owner.trim().to_lowercase();
        let subscriber = subscriber.to_lowercase();
        if owner.is_empty() || owner == subscriber {
            return false;
        }
        let owners = self.subscriptions.entry(subscriber.clone()).or_default();
        if owners.contains(&owner) {
            return true;
        }
        if owners.len() >= MAX_SUBSCRIPTIONS {
            return false;
        }
        owners.insert(owner.clone());
        for (o, key) in self.before.keys().filter(|(o, _)| *o == owner) {
            let now = self.values.get(&(o.clone(), key.clone())).cloned();
            self.started
                .insert((subscriber.clone(), o.clone(), key.clone()), now);
        }
        true
    }

    /// `subscriber` no longer hears `owner`'s changes; false when it did
    /// not.
    pub fn unsubscribe(&mut self, subscriber: &str, owner: &str) -> bool {
        let subscriber = subscriber.to_lowercase();
        let Some(owners) = self.subscriptions.get_mut(&subscriber) else {
            return false;
        };
        let removed = owners.remove(&owner.trim().to_lowercase());
        if owners.is_empty() {
            self.subscriptions.remove(&subscriber);
        }
        removed
    }

    /// The changes since the last take, for each subscriber of their
    /// publisher (by subscriber, then publisher and key): one per key, with
    /// its value now, and none for a key that is as it was at the take
    /// before, or at the subscription when that came later.
    pub fn take_changes(&mut self) -> Vec<(String, StatusChange)> {
        let before = std::mem::take(&mut self.before);
        let started = std::mem::take(&mut self.started);
        let mut told = Vec::new();
        for (subscriber, owners) in &self.subscriptions {
            for ((owner, key), was) in &before {
                if !owners.contains(owner) {
                    continue;
                }
                let value = self.values.get(&(owner.clone(), key.clone()));
                let was = started
                    .get(&(subscriber.clone(), owner.clone(), key.clone()))
                    .unwrap_or(was);
                if value == was.as_ref() {
                    continue;
                }
                told.push((
                    subscriber.clone(),
                    StatusChange {
                        owner: owner.clone(),
                        key: key.clone(),
                        value: value.cloned(),
                    },
                ));
            }
        }
        told
    }

    /// `id` stopped: its values go (its subscribers hear each withdrawn),
    /// and so do its subscriptions.
    pub fn release(&mut self, id: &str) {
        let id = id.to_lowercase();
        let (gone, kept): (BTreeMap<Slot, String>, _) = std::mem::take(&mut self.values)
            .into_iter()
            .partition(|((o, _), _)| *o == id);
        self.values = kept;
        for (slot, value) in gone {
            self.before.entry(slot).or_insert(Some(value));
        }
        self.subscriptions.remove(&id);
        self.started
            .retain(|(subscriber, _, _), _| *subscriber != id);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Plugin conversion plan G3: a value is read by its publisher's id (any
    // case), withdrawn by publishing none, and gone when the publisher stops.
    #[test]
    fn values_are_read_by_publisher_and_go_with_it() {
        let mut board = StatusBoard::default();
        assert!(board.publish("MossTank", "metaState", Some("Looting".into())));
        assert!(!board.publish("MossTank", " ", Some("x".into())));
        assert_eq!(
            board.read("mosstank", "metaState").as_deref(),
            Some("Looting")
        );
        assert_eq!(board.read("mosstank", "metastate"), None);
        board.publish("other", "k", Some("v".into()));
        assert_eq!(
            board.capture("MOSSTANK"),
            [("metaState".to_owned(), "Looting".to_owned())]
        );
        board.publish("MossTank", "metaState", None);
        assert_eq!(board.read("mosstank", "metaState"), None);
        board.release("OTHER");
        assert!(board.capture("other").is_empty());
    }

    // A subscriber hears one change per key per pass, with the value at
    // its end; a key set and set back in a pass, or a plugin it does not
    // subscribe to, tells it nothing.
    #[test]
    fn a_subscriber_hears_each_key_once_a_pass_with_its_last_value() {
        let mut board = StatusBoard::default();
        assert!(board.subscribe("Nav", "Vision"));
        board.publish("vision", "target", Some("a".into()));
        board.publish("vision", "target", Some("b".into()));
        board.publish("vision", "count", Some("1".into()));
        board.publish("other", "k", Some("v".into()));
        assert_eq!(
            board.take_changes(),
            [
                told("nav", "vision", "count", Some("1")),
                told("nav", "vision", "target", Some("b")),
            ]
        );
        assert!(board.take_changes().is_empty());
        board.publish("vision", "target", Some("c".into()));
        board.publish("vision", "target", Some("b".into()));
        assert!(board.take_changes().is_empty());
        board.publish("vision", "target", None);
        assert_eq!(
            board.take_changes(),
            [told("nav", "vision", "target", None)]
        );
    }

    // Every subscriber hears; one that withdrew does not; a subscription
    // may name a plugin before it publishes anything.
    #[test]
    fn subscriptions_come_and_go() {
        let mut board = StatusBoard::default();
        assert!(board.subscribe("a", "pub"));
        assert!(board.subscribe("b", "PUB"));
        assert!(board.subscribe("b", "pub"), "again is fine");
        assert!(!board.subscribe("pub", "Pub"), "its own");
        assert!(!board.subscribe("a", " "), "blank");
        board.publish("pub", "k", Some("1".into()));
        assert_eq!(
            board.take_changes(),
            [
                told("a", "pub", "k", Some("1")),
                told("b", "pub", "k", Some("1"))
            ]
        );
        assert!(board.unsubscribe("A", "Pub"));
        assert!(!board.unsubscribe("a", "pub"), "not subscribed");
        board.publish("pub", "k", Some("2".into()));
        assert_eq!(board.take_changes(), [told("b", "pub", "k", Some("2"))]);
    }

    #[test]
    fn a_plugin_subscribes_to_at_most_the_limit() {
        let mut board = StatusBoard::default();
        for n in 0..MAX_SUBSCRIPTIONS {
            assert!(board.subscribe("sub", &format!("p{n}")));
        }
        assert!(!board.subscribe("sub", "one-more"));
        assert!(board.subscribe("sub", "p0"), "one it has");
        assert!(board.unsubscribe("sub", "p0"));
        assert!(board.subscribe("sub", "one-more"));
    }

    // A publisher that stops withdraws every value it had at the pass's
    // start; one it set and that went with it in the same pass is not told.
    // A subscriber that stops hears nothing more and holds no subscription.
    #[test]
    fn stopping_withdraws_values_and_subscriptions() {
        let mut board = StatusBoard::default();
        board.subscribe("sub", "pub");
        board.publish("pub", "kept", Some("1".into()));
        board.take_changes();
        board.publish("pub", "brief", Some("2".into()));
        board.release("PUB");
        assert_eq!(board.take_changes(), [told("sub", "pub", "kept", None)]);
        board.release("sub");
        board.publish("pub", "kept", Some("3".into()));
        assert!(board.take_changes().is_empty());
        assert!(!board.unsubscribe("sub", "pub"));
    }

    // A publisher stopped and started again within a pass is heard as the
    // difference: a value it published again unchanged is not told, one
    // it changed is, and one it did not publish again is told withdrawn.
    #[test]
    fn a_publisher_restarted_within_a_pass_is_heard_as_the_difference() {
        let mut board = StatusBoard::default();
        board.subscribe("sub", "pub");
        board.publish("pub", "same", Some("1".into()));
        board.publish("pub", "changed", Some("1".into()));
        board.publish("pub", "dropped", Some("1".into()));
        board.take_changes();
        board.release("pub");
        board.publish("pub", "same", Some("1".into()));
        board.publish("pub", "changed", Some("2".into()));
        assert_eq!(
            board.take_changes(),
            [
                told("sub", "pub", "changed", Some("2")),
                told("sub", "pub", "dropped", None),
            ]
        );
    }

    // A subscription starts where it is made: a change made earlier in the
    // pass is not told, a later one is, and so is a subscription made
    // again after withdrawing it in the same pass.
    #[test]
    fn a_subscription_hears_only_changes_made_after_it() {
        let mut board = StatusBoard::default();
        board.publish("pub", "early", Some("1".into()));
        board.publish("pub", "both", Some("1".into()));
        board.subscribe("sub", "pub");
        board.publish("pub", "both", Some("2".into()));
        board.publish("pub", "late", Some("3".into()));
        assert_eq!(
            board.take_changes(),
            [
                told("sub", "pub", "both", Some("2")),
                told("sub", "pub", "late", Some("3"))
            ]
        );
        board.publish("pub", "early", Some("4".into()));
        board.unsubscribe("sub", "pub");
        board.subscribe("sub", "pub");
        board.publish("pub", "late", Some("5".into()));
        assert_eq!(
            board.take_changes(),
            [told("sub", "pub", "late", Some("5"))]
        );
        board.publish("pub", "both", Some("6".into()));
        assert_eq!(
            board.take_changes(),
            [told("sub", "pub", "both", Some("6"))]
        );
    }

    // A key changed earlier in the pass and set back after a subscription
    // is told: the subscriber read the changed value when it subscribed,
    // so the value at the pass's start is news to it. A withdrawal by a
    // publisher stopping is told the same way.
    #[test]
    fn a_key_set_back_after_a_subscription_is_told() {
        let mut board = StatusBoard::default();
        board.publish("pub", "k", Some("a".into()));
        board.take_changes();
        board.publish("pub", "k", Some("b".into()));
        board.subscribe("sub", "pub");
        board.publish("pub", "k", Some("a".into()));
        assert_eq!(board.take_changes(), [told("sub", "pub", "k", Some("a"))]);
        board.publish("pub", "new", Some("1".into()));
        board.subscribe("late", "pub");
        board.publish("pub", "new", None);
        assert_eq!(board.take_changes(), [told("late", "pub", "new", None)]);
    }

    // Nothing is held for a change that changed nothing: a withdrawal of a
    // key never published, or a key set back as it was.
    #[test]
    fn a_change_that_changes_nothing_is_not_held() {
        let mut board = StatusBoard::default();
        for n in 0..1000 {
            board.publish("pub", &format!("k{n}"), None);
            board.publish("pub", &format!("j{n}"), Some("x".into()));
            board.publish("pub", &format!("j{n}"), None);
        }
        assert!(board.before.is_empty());
        assert!(board.started.is_empty());
    }

    fn told(
        subscriber: &str,
        owner: &str,
        key: &str,
        value: Option<&str>,
    ) -> (String, StatusChange) {
        (
            subscriber.to_owned(),
            StatusChange {
                owner: owner.to_owned(),
                key: key.to_owned(),
                value: value.map(str::to_owned),
            },
        )
    }
}
