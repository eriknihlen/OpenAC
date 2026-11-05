//! Where each effect owner is: its root in the world, the local pose of each
//! of its parts, and its cell. Emitters attached to a part read the part's
//! pose (so they follow an animating arm); script hooks report at the root.
//!
//! Matrices are glam's column-vector convention: a part's world matrix is
//! `root * part`, a point goes through `m.transform_point3(p)`.

use std::collections::HashMap;
use std::fmt;

use glam::{Mat4, Quat, Vec3};

/// Read access to effect owners' poses.
pub trait EffectPoseSource {
    /// The owner's root, world space.
    fn root_pose(&self, owner: u32) -> Option<Mat4>;
    /// Part `part_index`'s pose relative to the owner's root.
    fn part_pose(&self, owner: u32, part_index: i32) -> Option<Mat4>;
    /// The owner's cell; `None` when the owner is unknown.
    fn cell_id(&self, owner: u32) -> Option<u32>;
}

/// The root matrix of an object at `position` turned by `rotation`.
#[must_use]
pub fn root_matrix(position: Vec3, rotation: Quat) -> Mat4 {
    Mat4::from_rotation_translation(rotation, position)
}

/// Part poses and availability flags of different lengths.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PoseError {
    /// Part poses given.
    pub parts: usize,
    /// Availability flags given.
    pub availability: usize,
}

impl fmt::Display for PoseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "part pose and availability counts must match ({} vs {})",
            self.parts, self.availability
        )
    }
}

impl std::error::Error for PoseError {}

#[derive(Debug, Clone)]
struct PoseRecord {
    root: Mat4,
    parts: Vec<Mat4>,
    available: Vec<bool>,
    cell_id: u32,
    lifetime_version: u64,
    change_version: u64,
}

/// The current pose of every effect owner. Every publish that changes
/// something (and every removal) records the owner as changed, for
/// [`Self::take_changed_owners`].
#[derive(Debug, Default)]
pub struct EffectPoseRegistry {
    poses: HashMap<u32, PoseRecord>,
    next_lifetime_version: u64,
    changed: Vec<u32>,
}

impl EffectPoseRegistry {
    /// An empty registry.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Owners with a pose.
    #[must_use]
    pub fn count(&self) -> usize {
        self.poses.len()
    }

    /// Publish `owner`'s whole pose. `availability` (one flag per part, all
    /// true when absent) says which parts draw; every part resolves for
    /// attachment either way. An owner id of 0 is ignored.
    pub fn publish(
        &mut self,
        owner: u32,
        root: Mat4,
        parts: &[Mat4],
        cell_id: u32,
        availability: Option<&[bool]>,
    ) -> Result<(), PoseError> {
        if owner == 0 {
            return Ok(());
        }
        if let Some(flags) = availability
            && flags.len() != parts.len()
        {
            return Err(PoseError {
                parts: parts.len(),
                availability: flags.len(),
            });
        }
        let mut changed = false;
        if !self.poses.contains_key(&owner) {
            let lifetime_version = self.next_lifetime_version();
            self.poses.insert(
                owner,
                PoseRecord {
                    root,
                    parts: Vec::new(),
                    available: Vec::new(),
                    cell_id,
                    lifetime_version,
                    change_version: 0,
                },
            );
            changed = true;
        }
        let Some(record) = self.poses.get_mut(&owner) else {
            return Ok(());
        };
        changed |= record.root != root || record.cell_id != cell_id;
        record.root = root;
        record.cell_id = cell_id;
        if record.parts.len() != parts.len() {
            record.parts = vec![Mat4::IDENTITY; parts.len()];
            record.available = vec![false; parts.len()];
            changed = true;
        }
        for (i, part) in parts.iter().enumerate() {
            let available = availability.is_none_or(|flags| flags[i]);
            changed |= record.parts[i] != *part || record.available[i] != available;
            record.parts[i] = *part;
            record.available[i] = available;
        }
        if changed {
            record.change_version += 1;
            self.changed.push(owner);
        }
        Ok(())
    }

    /// Move `owner`'s root (and cell), keeping its parts. False when the
    /// owner has no pose.
    pub fn update_root(&mut self, owner: u32, root: Mat4, cell_id: u32) -> bool {
        let Some(record) = self.poses.get_mut(&owner) else {
            return false;
        };
        if record.root == root && record.cell_id == cell_id {
            return true;
        }
        record.root = root;
        record.cell_id = cell_id;
        record.change_version += 1;
        self.changed.push(owner);
        true
    }

    /// Forget `owner`'s pose.
    pub fn remove(&mut self, owner: u32) -> bool {
        if self.poses.remove(&owner).is_none() {
            return false;
        }
        self.changed.push(owner);
        true
    }

    /// Forget every pose.
    pub fn clear(&mut self) {
        self.changed.extend(self.poses.keys().copied());
        self.poses.clear();
    }

    /// Owners whose pose changed (or went) since the last call, in order,
    /// possibly repeated.
    pub fn take_changed_owners(&mut self) -> Vec<u32> {
        std::mem::take(&mut self.changed)
    }

    /// Every part's local pose and whether it draws.
    #[must_use]
    pub fn part_poses(&self, owner: u32) -> Option<(&[Mat4], &[bool])> {
        self.poses
            .get(&owner)
            .map(|r| (r.parts.as_slice(), r.available.as_slice()))
    }

    /// Changes each time `owner` gets a pose after having none (0: none).
    #[must_use]
    pub fn lifetime_version(&self, owner: u32) -> u64 {
        self.poses.get(&owner).map_or(0, |r| r.lifetime_version)
    }

    /// Counts the owner's pose changes (0: none).
    #[must_use]
    pub fn change_version(&self, owner: u32) -> u64 {
        self.poses.get(&owner).map_or(0, |r| r.change_version)
    }

    fn next_lifetime_version(&mut self) -> u64 {
        self.next_lifetime_version = self.next_lifetime_version.wrapping_add(1);
        if self.next_lifetime_version == 0 {
            self.next_lifetime_version = 1;
        }
        self.next_lifetime_version
    }
}

impl EffectPoseSource for EffectPoseRegistry {
    fn root_pose(&self, owner: u32) -> Option<Mat4> {
        self.poses.get(&owner).map(|r| r.root)
    }

    /// Any index inside the owner's part count resolves, drawn or not: an
    /// attachment asks where the part is, not whether it draws.
    fn part_pose(&self, owner: u32, part_index: i32) -> Option<Mat4> {
        let index = usize::try_from(part_index).ok()?;
        self.poses.get(&owner)?.parts.get(index).copied()
    }

    fn cell_id(&self, owner: u32) -> Option<u32> {
        self.poses.get(&owner).map(|r| r.cell_id)
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/Vfx/EntityEffectPoseRegistryTests.cs`
    //! (the registry half; the hook-queue cases belong to the presentation).
    use super::*;

    fn translation(x: f32, y: f32, z: f32) -> Mat4 {
        Mat4::from_translation(Vec3::new(x, y, z))
    }

    #[test]
    fn publish_preserves_indexed_parts_and_update_root_does_not_replace_them() {
        let mut r = EffectPoseRegistry::new();
        let parts = [translation(1.0, 0.0, 0.0), translation(0.0, 2.0, 0.0)];
        r.publish(7, Mat4::IDENTITY, &parts, 0x0101_0001, None)
            .unwrap();
        assert!(r.update_root(7, translation(5.0, 0.0, 0.0), 0x0101_0002));
        assert_eq!(r.part_pose(7, 1), Some(parts[1]));
        assert_eq!(r.root_pose(7), Some(translation(5.0, 0.0, 0.0)));
        assert_eq!(r.cell_id(7), Some(0x0101_0002));
    }

    #[test]
    fn non_drawing_middle_part_keeps_its_index_and_still_resolves() {
        let mut r = EffectPoseRegistry::new();
        let parts = [Mat4::IDENTITY, translation(0.0, 0.0, 3.0), Mat4::IDENTITY];
        r.publish(9, Mat4::IDENTITY, &parts, 0, Some(&[true, false, true]))
            .unwrap();
        assert_eq!(r.part_pose(9, 1), Some(parts[1]));
        assert_eq!(r.part_poses(9).unwrap().1, [true, false, true]);
        assert_eq!(r.part_pose(9, 3), None);
        assert_eq!(r.part_pose(9, -1), None);
    }

    #[test]
    fn mismatched_availability_is_an_error() {
        let mut r = EffectPoseRegistry::new();
        assert!(
            r.publish(1, Mat4::IDENTITY, &[Mat4::IDENTITY], 0, Some(&[]))
                .is_err()
        );
    }

    #[test]
    fn change_notifications_suppress_equivalent_publishes() {
        let mut r = EffectPoseRegistry::new();
        let parts = [Mat4::IDENTITY];
        r.publish(3, Mat4::IDENTITY, &parts, 0x0101_0001, None)
            .unwrap();
        assert_eq!(r.take_changed_owners(), [3]);
        r.publish(3, Mat4::IDENTITY, &parts, 0x0101_0001, None)
            .unwrap();
        assert!(r.update_root(3, Mat4::IDENTITY, 0x0101_0001));
        assert!(r.take_changed_owners().is_empty());
        assert_eq!(r.change_version(3), 1);

        r.publish(3, translation(1.0, 0.0, 0.0), &parts, 0x0101_0001, None)
            .unwrap();
        assert!(r.remove(3));
        assert_eq!(r.take_changed_owners(), [3, 3]);
    }

    #[test]
    fn lifetime_version_changes_when_an_owner_is_recreated() {
        let mut r = EffectPoseRegistry::new();
        r.publish(4, Mat4::IDENTITY, &[], 0, None).unwrap();
        let first = r.lifetime_version(4);
        r.remove(4);
        assert_eq!(r.lifetime_version(4), 0);
        r.publish(4, Mat4::IDENTITY, &[], 0, None).unwrap();
        assert_ne!(r.lifetime_version(4), first);
    }
}
