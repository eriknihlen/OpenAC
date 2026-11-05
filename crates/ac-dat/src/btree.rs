//! The dat directory: a B-tree of file entries keyed by file id, each node
//! stored in its own block chain.

use ac_bits::{BitsError, Reader};

use crate::error::{DatError, Result};

/// Offsets that mean "no node": zero, and the fill pattern of unused memory.
const INVALID_OFFSET: i32 = 0xCDCD_CDCDu32 as i32;

/// Entry flag: the stored bytes are compressed.
const FLAG_COMPRESSED: u16 = 0x0001;

/// One directory entry: where a file lives and how big it is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DatEntry {
    /// Entry flags (bit 0: compressed).
    pub flags: u16,
    /// Entry format version.
    pub version: u16,
    /// File id.
    pub id: u32,
    /// Offset of the file's first block.
    pub offset: i32,
    /// Size of the stored bytes.
    pub size: u32,
    /// Timestamp, seconds since the Unix epoch.
    pub raw_date: u32,
    /// Iteration (patch generation) that last wrote the file.
    pub iteration: i32,
}

impl DatEntry {
    /// Size of an entry on disk.
    pub const SIZE: usize = 24;

    /// True when the stored bytes are compressed.
    #[must_use]
    pub fn is_compressed(&self) -> bool {
        self.flags & FLAG_COMPRESSED != 0
    }

    fn unpack(r: &mut Reader<'_>) -> core::result::Result<Self, BitsError> {
        Ok(Self {
            flags: r.u16()?,
            version: r.u16()?,
            id: r.u32()?,
            offset: r.i32()?,
            size: r.u32()?,
            raw_date: r.u32()?,
            iteration: r.i32()?,
        })
    }
}

/// One directory node: up to 61 entries sorted by id, and for an inner node
/// one more child than entries.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Node {
    /// Block offset the node was read from.
    pub offset: i32,
    /// Child node offsets; empty for a leaf.
    pub branches: Vec<i32>,
    /// Entries in id order.
    pub files: Vec<DatEntry>,
}

impl Node {
    const MAX_BRANCHES: usize = 62;
    const MAX_FILES: usize = 61;

    /// Bytes a node occupies: the branch slots, the entry count, the entry slots.
    pub const DISK_SIZE: usize = Self::MAX_BRANCHES * 4 + 4 + Self::MAX_FILES * DatEntry::SIZE;

    /// A node with no children is a leaf.
    pub fn is_leaf(&self) -> bool {
        self.branches.is_empty()
    }

    /// Child `index`, or 0 ("none") past the end of the branch list.
    pub fn child(&self, index: usize) -> i32 {
        self.branches.get(index).copied().unwrap_or(0)
    }

    /// Decode a node from its bytes.
    ///
    /// The branch slots are always all present on disk; the used ones are
    /// the run before the first zero, fill pattern, or repeated offset. An
    /// inner node then has exactly one more child than entries (missing
    /// children read as 0).
    pub fn unpack(offset: i32, data: &[u8]) -> Result<Self> {
        let mut r = Reader::new(data);
        r.seek(Self::MAX_BRANCHES * 4)?;
        let file_count = r.i32()?;
        let file_count = usize::try_from(file_count)
            .ok()
            .filter(|&count| count <= Self::MAX_FILES)
            .ok_or(DatError::BadNodeEntryCount {
                offset,
                count: file_count,
            })?;
        r.seek(0)?;

        let mut branches = Vec::new();
        let mut found_end = false;
        let mut last_branch = 0;
        for _ in 0..Self::MAX_BRANCHES {
            let branch = r.i32()?;
            if branch == 0 || branch == last_branch || branch == INVALID_OFFSET {
                found_end = true;
            }
            if !found_end {
                last_branch = branch;
                branches.push(branch);
            }
        }
        r.skip(4)?;

        let files = (0..file_count)
            .map(|_| DatEntry::unpack(&mut r))
            .collect::<core::result::Result<Vec<_>, _>>()?;

        if !branches.is_empty() && file_count > 0 {
            branches.resize(file_count + 1, 0);
        }

        Ok(Self {
            offset,
            branches,
            files,
        })
    }

    /// Binary-search this node for `id`. `Ok(index)` when found; otherwise
    /// `Err(child)` names the branch slot the search continues in.
    pub fn search(&self, id: u32) -> core::result::Result<usize, usize> {
        let mut left: isize = 0;
        let mut right = self.files.len() as isize - 1;
        let mut i: isize = 0;
        while left <= right {
            i = (left + right) / 2;
            let current = self.files[i as usize].id;
            if id == current {
                return Ok(i as usize);
            } else if id < current {
                right = i - 1;
            } else {
                left = i + 1;
            }
        }
        // The last probed entry decides the side; an empty node probes a
        // default entry with id 0.
        let probed = self.files.get(i as usize).map_or(0, |f| f.id);
        let mut child = i as usize;
        if id > probed {
            child += 1;
        }
        Err(child)
    }
}

/// True when `offset` can name a node.
pub(crate) fn is_valid_offset(offset: i32) -> bool {
    offset != 0 && offset != INVALID_OFFSET
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use ac_bits::Writer;

    pub(crate) fn entry(id: u32, offset: i32, size: u32) -> DatEntry {
        DatEntry {
            flags: 0,
            version: 2,
            id,
            offset,
            size,
            raw_date: 1_117_233_940,
            iteration: 1,
        }
    }

    /// Node bytes with the given branch slots and entries.
    pub(crate) fn node_bytes(branches: &[i32], files: &[DatEntry]) -> Vec<u8> {
        let mut w = Writer::new();
        for i in 0..Node::MAX_BRANCHES {
            w.i32(branches.get(i).copied().unwrap_or(0));
        }
        w.i32(files.len() as i32);
        for f in files {
            w.u16(f.flags);
            w.u16(f.version);
            w.u32(f.id);
            w.i32(f.offset);
            w.u32(f.size);
            w.u32(f.raw_date);
            w.i32(f.iteration);
        }
        w.pad(Node::DISK_SIZE - w.len());
        w.into_vec()
    }

    #[test]
    fn disk_size_matches_the_layout() {
        assert_eq!(Node::DISK_SIZE, 1716);
    }

    #[test]
    fn leaf_node_has_no_branches() {
        let bytes = node_bytes(&[], &[entry(1, 0x400, 8), entry(5, 0x500, 8)]);
        let node = Node::unpack(0x100, &bytes).unwrap();
        assert!(node.is_leaf());
        assert_eq!(node.files.len(), 2);
        assert_eq!(node.files[1], entry(5, 0x500, 8));
        assert_eq!(node.search(5), Ok(1));
        assert_eq!(node.search(3), Err(1));
    }

    #[test]
    fn inner_node_branch_count_follows_entry_count() {
        // Three children for two entries, then the fill pattern.
        let bytes = node_bytes(
            &[0x1000, 0x2000, 0x3000, INVALID_OFFSET, 0x4000],
            &[entry(10, 0, 0), entry(20, 0, 0)],
        );
        let node = Node::unpack(0x100, &bytes).unwrap();
        assert_eq!(node.branches, vec![0x1000, 0x2000, 0x3000]);
        assert_eq!(node.search(5), Err(0));
        assert_eq!(node.search(15), Err(1));
        assert_eq!(node.search(25), Err(2));
        assert_eq!(node.search(20), Ok(1));
    }

    #[test]
    fn repeated_branch_ends_the_list_and_missing_children_read_as_zero() {
        let bytes = node_bytes(
            &[0x1000, 0x1000, 0x3000],
            &[entry(10, 0, 0), entry(20, 0, 0)],
        );
        let node = Node::unpack(0x100, &bytes).unwrap();
        assert_eq!(node.branches, vec![0x1000, 0, 0]);
        assert_eq!(node.child(1), 0);
        assert_eq!(node.child(7), 0);
    }

    #[test]
    fn surplus_branches_are_trimmed_to_entries_plus_one() {
        let bytes = node_bytes(&[0x1000, 0x2000, 0x3000, 0x4000], &[entry(10, 0, 0)]);
        let node = Node::unpack(0x100, &bytes).unwrap();
        assert_eq!(node.branches, vec![0x1000, 0x2000]);
    }

    #[test]
    fn empty_inner_node_searches_toward_child_one() {
        let bytes = node_bytes(&[0x1000, 0x2000], &[]);
        let node = Node::unpack(0x100, &bytes).unwrap();
        assert!(!node.is_leaf());
        assert_eq!(node.search(7), Err(1));
    }

    #[test]
    fn entry_count_out_of_range_is_an_error() {
        let mut bytes = node_bytes(&[], &[]);
        bytes[248..252].copy_from_slice(&62i32.to_le_bytes());
        assert!(matches!(
            Node::unpack(0x100, &bytes),
            Err(DatError::BadNodeEntryCount { count: 62, .. })
        ));
        bytes[248..252].copy_from_slice(&(-1i32).to_le_bytes());
        assert!(matches!(
            Node::unpack(0x100, &bytes),
            Err(DatError::BadNodeEntryCount { count: -1, .. })
        ));
    }

    #[test]
    fn truncated_node_is_an_error() {
        let bytes = node_bytes(&[], &[entry(1, 0x400, 8)]);
        assert!(Node::unpack(0x100, &bytes[..100]).is_err());
        assert!(Node::unpack(0x100, &bytes[..260]).is_err());
    }

    #[test]
    fn compressed_flag() {
        let mut e = entry(1, 0, 0);
        assert!(!e.is_compressed());
        e.flags = 1;
        assert!(e.is_compressed());
    }
}
