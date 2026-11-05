//! The work stack the tree walks use instead of recursion.
//!
//! The first 64 entries live inline, so walking any real tree allocates
//! nothing; deeper walks spill to the heap.

const INLINE: usize = 64;

pub(crate) struct NodeStack<T: Copy + Default> {
    inline: [T; INLINE],
    len: usize,
    spill: Vec<T>,
}

impl<T: Copy + Default> NodeStack<T> {
    pub(crate) fn new() -> Self {
        Self {
            inline: [T::default(); INLINE],
            len: 0,
            spill: Vec::new(),
        }
    }

    pub(crate) fn with(item: T) -> Self {
        let mut s = Self::new();
        s.push(item);
        s
    }

    pub(crate) fn push(&mut self, item: T) {
        // The spill holds only entries pushed while the inline part was
        // full, so it is always the top of the stack.
        if self.spill.is_empty() && self.len < INLINE {
            self.inline[self.len] = item;
            self.len += 1;
        } else {
            self.spill.push(item);
        }
    }

    pub(crate) fn pop(&mut self) -> Option<T> {
        if let Some(item) = self.spill.pop() {
            return Some(item);
        }
        if self.len == 0 {
            return None;
        }
        self.len -= 1;
        Some(self.inline[self.len])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn last_in_first_out_across_the_spill() {
        let mut s = NodeStack::<usize>::new();
        for i in 0..200 {
            s.push(i);
        }
        for i in (100..200).rev() {
            assert_eq!(s.pop(), Some(i));
        }
        s.push(1000);
        assert_eq!(s.pop(), Some(1000));
        for i in (0..100).rev() {
            assert_eq!(s.pop(), Some(i));
        }
        assert_eq!(s.pop(), None);
    }
}
