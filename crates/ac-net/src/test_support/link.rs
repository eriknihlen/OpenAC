use std::collections::HashSet;

/// Which way a datagram travels.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Dir {
    ClientToServer,
    ServerToClient,
}

type Predicate = Box<dyn FnMut(usize, &[u8]) -> bool>;

#[derive(Default)]
struct DirectionState {
    transmit_index: usize,
    pending_drops: usize,
    drop_indices: HashSet<usize>,
    predicates: Vec<Predicate>,
    dropped: usize,
    delivered: usize,
}

/// A scripted link: drop the next N datagrams, drop by per-direction
/// transmit index, or drop whatever a predicate selects.
#[derive(Default)]
pub struct ScriptedLink {
    c2s: DirectionState,
    s2c: DirectionState,
}

impl std::fmt::Debug for ScriptedLink {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ScriptedLink").finish_non_exhaustive()
    }
}

impl ScriptedLink {
    fn state(&mut self, dir: Dir) -> &mut DirectionState {
        match dir {
            Dir::ClientToServer => &mut self.c2s,
            Dir::ServerToClient => &mut self.s2c,
        }
    }

    pub fn drop_next(&mut self, dir: Dir, count: usize) {
        self.state(dir).pending_drops += count;
    }

    pub fn drop_at(&mut self, dir: Dir, transmit_index: usize) {
        self.state(dir).drop_indices.insert(transmit_index);
    }

    pub fn drop_when(&mut self, dir: Dir, predicate: impl FnMut(usize, &[u8]) -> bool + 'static) {
        self.state(dir).predicates.push(Box::new(predicate));
    }

    pub fn dropped(&self, dir: Dir) -> usize {
        match dir {
            Dir::ClientToServer => self.c2s.dropped,
            Dir::ServerToClient => self.s2c.dropped,
        }
    }

    /// True when the datagram survives.
    pub fn transmit(&mut self, dir: Dir, datagram: &[u8]) -> bool {
        let state = self.state(dir);
        let index = state.transmit_index;
        state.transmit_index += 1;
        let mut drop = state.drop_indices.remove(&index);
        if !drop && state.pending_drops > 0 {
            state.pending_drops -= 1;
            drop = true;
        }
        if !drop {
            for predicate in &mut state.predicates {
                if predicate(index, datagram) {
                    drop = true;
                    break;
                }
            }
        }
        if drop {
            state.dropped += 1;
        } else {
            state.delivered += 1;
        }
        !drop
    }
}
