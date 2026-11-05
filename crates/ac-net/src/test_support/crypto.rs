use std::collections::HashSet;

use crate::crypto::Isaac;

/// The server's tolerance for skipped keystream words: how many words it
/// will search ahead for a packet's key.
pub const MAXIMUM_EFFORT_LEVEL: usize = 256;

/// The server's view of the client's keystream. A packet whose key is not
/// the current word is searched for up to 256 words ahead; every word
/// skipped stays in an "orphan" set until a packet uses it. Each orphan
/// permanently narrows the search, so a client that re-keys resends or
/// resends accepted packets slowly kills its session.
#[derive(Debug)]
pub struct ServerCrypto {
    keystream: Isaac,
    orphans: HashSet<u32>,
    current: u32,
}

impl ServerCrypto {
    pub fn new(seed: u32) -> Self {
        let mut keystream = Isaac::new(seed);
        let current = keystream.next_word();
        Self {
            keystream,
            orphans: HashSet::new(),
            current,
        }
    }

    /// Search room left (256 with no orphans).
    pub fn headroom(&self) -> usize {
        MAXIMUM_EFFORT_LEVEL - self.orphans.len()
    }

    pub fn orphan_count(&self) -> usize {
        self.orphans.len()
    }

    pub fn consume_key(&mut self, key: u32) {
        if self.current == key {
            self.current = self.keystream.next_word();
        } else {
            self.orphans.remove(&key);
        }
    }

    pub fn search(&mut self, key: u32) -> bool {
        if self.current == key || self.orphans.contains(&key) {
            return true;
        }
        let already = self.orphans.len();
        for _ in 0..MAXIMUM_EFFORT_LEVEL.saturating_sub(already) {
            self.orphans.insert(self.current);
            let skipped = self.current;
            self.consume_key(skipped);
            if self.current == key {
                return true;
            }
        }
        false
    }
}
