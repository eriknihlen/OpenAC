//! Held and wielded objects: a child hangs from one of its parent's holding
//! locations (a hand, the back, a shield arm), which sits on one of the
//! parent's parts, so it moves with that part's animation.
//!
//! The child's root is the parent part's frame times the holding
//! location's offset (both from the parent's Setup); the child's own parts
//! sit in the child Setup's placement frame for how it is held (else its
//! default placement). Children are posed parent first, so a child of a
//! child sees its parent's pose of this frame.

use std::collections::{HashMap, HashSet, VecDeque};
use std::hash::Hash;

use ac_dat::objects::Setup;
use ac_dat::types::Frame;
use glam::{Mat4, Vec3};

use super::pose::default_scale;
use crate::meshing::{PLACEMENT_DEFAULT, quat, vec3};

/// A child's pose relative to its parent's root.
#[derive(Debug, Clone, PartialEq)]
pub struct ChildPose {
    /// The child's root, parent-root-local.
    pub root_local: Mat4,
    /// Each child part's frame, child-root-local (no mesh scale).
    pub part_local: Vec<Mat4>,
    /// Each child part's mesh placement, parent-root-local.
    pub attached_parts: Vec<Mat4>,
}

fn frame_matrix(frame: &Frame) -> Mat4 {
    Mat4::from_translation(vec3(frame.origin)) * Mat4::from_quat(quat(frame.orientation))
}

/// Pose a child Setup held at `location` of a parent posed as
/// `parent_parts` (the parent's rigid part frames; `available` says which
/// exist), in placement `placement` at `child_scale`. `None` when the
/// parent has no such holding location or its part is not available. A
/// holding location on a part the pose does not list hangs from the
/// parent's root (OpenAC `EquippedChildAttachment.TryComposePoseInto`).
#[must_use]
pub fn compose_child(
    parent: &Setup,
    parent_parts: &[Mat4],
    available: Option<&[bool]>,
    child: &Setup,
    location: u32,
    placement: u32,
    child_scale: f32,
) -> Option<ChildPose> {
    let holding = parent
        .holding_locations
        .iter()
        .find(|(k, _)| *k as u32 == location)
        .map(|(_, l)| l)?;
    let parent_part = match usize::try_from(holding.part_id)
        .ok()
        .filter(|&p| p < parent_parts.len())
    {
        Some(p) => {
            if available.is_some_and(|a| !a.get(p).copied().unwrap_or(false)) {
                return None;
            }
            parent_parts[p]
        }
        None => Mat4::IDENTITY,
    };
    let root_local = parent_part * frame_matrix(&holding.frame);
    let placement_frame = child
        .placement_frames
        .iter()
        .find(|(k, _)| *k == placement)
        .or_else(|| {
            child
                .placement_frames
                .iter()
                .find(|(k, _)| *k == PLACEMENT_DEFAULT)
        })
        .map(|(_, f)| f);
    let count = child.parts.len();
    let mut part_local = Vec::with_capacity(count);
    let mut attached_parts = Vec::with_capacity(count);
    for i in 0..count {
        let (origin, rotation) = placement_frame
            .and_then(|f| f.frames.get(i))
            .map_or((Vec3::ZERO, glam::Quat::IDENTITY), |f| {
                (vec3(f.origin), quat(f.orientation))
            });
        let mut visual = Mat4::from_translation(origin)
            * Mat4::from_quat(rotation)
            * Mat4::from_scale(default_scale(child, i));
        if child_scale != 1.0 {
            visual = Mat4::from_scale(Vec3::splat(child_scale)) * visual;
        }
        part_local.push(Mat4::from_translation(origin * child_scale) * Mat4::from_quat(rotation));
        attached_parts.push(root_local * visual);
    }
    Some(ChildPose {
        root_local,
        part_local,
        attached_parts,
    })
}

/// Visits attachment trees parent first (OpenAC `AttachmentUpdateOrder`).
#[derive(Debug)]
pub struct AttachmentUpdateOrder<K> {
    visiting: HashSet<K>,
    completed: HashSet<K>,
    failed_set: HashSet<K>,
    failed: Vec<K>,
    subtree: Vec<K>,
}

impl<K> Default for AttachmentUpdateOrder<K> {
    fn default() -> Self {
        Self {
            visiting: HashSet::new(),
            completed: HashSet::new(),
            failed_set: HashSet::new(),
            failed: Vec::new(),
            subtree: Vec::new(),
        }
    }
}

impl<K: Copy + Eq + Hash> AttachmentUpdateOrder<K> {
    /// Call `update` on every child after its parent (when the parent is a
    /// child too). A child whose parent failed is not updated and fails
    /// itself; the failed children are returned, parents first.
    pub fn for_each_parent_first<C>(
        &mut self,
        children: &[(K, C)],
        parent_of: impl Fn(&C) -> Option<K>,
        mut update: impl FnMut(K) -> bool,
    ) -> &[K] {
        self.visiting.clear();
        self.completed.clear();
        self.failed_set.clear();
        self.failed.clear();
        let index: HashMap<K, usize> = children
            .iter()
            .enumerate()
            .map(|(i, (k, _))| (*k, i))
            .collect();
        for (child, _) in children {
            self.visit(*child, children, &index, &parent_of, &mut update);
        }
        &self.failed
    }

    fn visit<C>(
        &mut self,
        child: K,
        children: &[(K, C)],
        index: &HashMap<K, usize>,
        parent_of: &impl Fn(&C) -> Option<K>,
        update: &mut impl FnMut(K) -> bool,
    ) {
        if self.completed.contains(&child) {
            return;
        }
        let Some(&i) = index.get(&child) else {
            return;
        };
        if !self.visiting.insert(child) {
            return;
        }
        let parent = parent_of(&children[i].1);
        if let Some(p) = parent
            && index.contains_key(&p)
        {
            self.visit(p, children, index, parent_of, update);
        }
        self.visiting.remove(&child);
        if !self.completed.insert(child) {
            return;
        }
        let mut succeeded = parent.is_none_or(|p| !self.failed_set.contains(&p));
        if succeeded {
            succeeded = update(child);
        }
        if !succeeded && self.failed_set.insert(child) {
            self.failed.push(child);
        }
    }

    /// `root` and everything attached below it, children before parents.
    pub fn collect_subtree_post_order<C>(
        &mut self,
        children: &[(K, C)],
        root: K,
        parent_of: impl Fn(&C) -> Option<K>,
    ) -> &[K] {
        self.subtree.clear();
        self.visiting.clear();
        self.collect(root, children, &parent_of);
        &self.subtree
    }

    fn collect<C>(&mut self, root: K, children: &[(K, C)], parent_of: &impl Fn(&C) -> Option<K>) {
        if !self.visiting.insert(root) {
            return;
        }
        for (candidate, child) in children {
            if parent_of(child) == Some(root) {
                self.collect(*candidate, children, parent_of);
            }
        }
        self.subtree.push(root);
    }

    /// A parent's pose came back: realize the children waiting for it, and
    /// theirs, breadth first; a child that does not realize stops its
    /// branch.
    pub fn realize_descendants(
        &mut self,
        parent: K,
        waiting_for: impl Fn(K) -> Vec<K>,
        mut realize: impl FnMut(K) -> bool,
    ) {
        let mut queue = VecDeque::from([parent]);
        let mut visited = HashSet::from([parent]);
        while let Some(current) = queue.pop_front() {
            for child in waiting_for(current) {
                if realize(child) && visited.insert(child) {
                    queue.push_back(child);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `AttachmentUpdateOrderTests` and
    //! `EquippedChildAttachmentTests`. OpenAC's row-vector products are
    //! written here in glam's column order.
    use super::*;
    use ac_dat::objects::AnimationFrame;
    use ac_dat::types::{LocationType, Quaternion, Vector3};

    const LEFT_HAND: u32 = 2;
    const RIGHT_HAND: u32 = 1;
    const PLACEMENT_LEFT_HAND: u32 = 3;
    const PLACEMENT_RIGHT_HAND_COMBAT: u32 = 1;

    #[test]
    fn a_reinserted_parent_still_updates_before_its_grandchild() {
        let children = [(20u32, Some(10u32)), (10, None)];
        let mut calls = Vec::new();
        AttachmentUpdateOrder::default().for_each_parent_first(
            &children,
            |p| *p,
            |id| {
                calls.push(id);
                true
            },
        );
        assert_eq!(calls, [10, 20]);
    }

    #[test]
    fn a_failed_parent_suppresses_and_reports_its_descendants() {
        let children = [(20u32, Some(10u32)), (10, None)];
        let mut calls = Vec::new();
        let mut order = AttachmentUpdateOrder::default();
        let failed = order.for_each_parent_first(
            &children,
            |p| *p,
            |id| {
                calls.push(id);
                id != 10
            },
        );
        assert_eq!(failed, [10, 20]);
        assert_eq!(calls, [10]);
    }

    #[test]
    fn ancestor_teardown_collects_the_whole_subtree_post_order() {
        let children = [(20u32, Some(10u32)), (30, Some(20))];
        let mut order = AttachmentUpdateOrder::default();
        assert_eq!(
            order.collect_subtree_post_order(&children, 10, |p| *p),
            [30, 20, 10]
        );
    }

    #[test]
    fn pose_recovery_realizes_the_descendant_chain_parent_first() {
        let waiting = HashMap::from([(10u32, vec![20u32]), (20, vec![30])]);
        let mut realized = Vec::new();
        AttachmentUpdateOrder::default().realize_descendants(
            10,
            |p| waiting.get(&p).cloned().unwrap_or_default(),
            |c| {
                realized.push(c);
                true
            },
        );
        assert_eq!(realized, [20, 30]);
    }

    #[test]
    fn pose_recovery_stops_at_an_unrealized_intermediate_parent() {
        let waiting = HashMap::from([(10u32, vec![20u32]), (20, vec![30])]);
        let mut attempted = Vec::new();
        AttachmentUpdateOrder::default().realize_descendants(
            10,
            |p| waiting.get(&p).cloned().unwrap_or_default(),
            |c| {
                attempted.push(c);
                false
            },
        );
        assert_eq!(attempted, [20]);
    }

    fn frame_at(x: f32) -> Frame {
        Frame {
            origin: Vector3 { x, y: 0.0, z: 0.0 },
            orientation: Quaternion {
                w: 1.0,
                x: 0.0,
                y: 0.0,
                z: 0.0,
            },
        }
    }

    fn placement(id: u32, x: f32) -> (u32, AnimationFrame) {
        (
            id,
            AnimationFrame {
                frames: vec![frame_at(x)],
                hooks: Vec::new(),
            },
        )
    }

    fn parent_with(location: u32, part_id: i32, x: f32) -> Setup {
        let mut s = crate::animation::test_support::setup(0);
        s.holding_locations = vec![(
            location as i32,
            LocationType {
                part_id,
                frame: frame_at(x),
            },
        )];
        s
    }

    fn child(placements: Vec<(u32, AnimationFrame)>) -> Setup {
        let mut s = crate::animation::test_support::setup(1);
        s.placement_frames = placements;
        s
    }

    fn x(m: Mat4) -> f32 {
        m.w_axis.x
    }

    #[test]
    fn the_child_combines_its_placement_the_holding_frame_and_the_animated_hand() {
        let parent = parent_with(LEFT_HAND, 1, 2.0);
        let pose = [
            Mat4::IDENTITY,
            Mat4::from_translation(Vec3::new(10.0, 0.0, 0.0)),
        ];
        let c = child(vec![placement(PLACEMENT_LEFT_HAND, 3.0)]);
        let p = compose_child(
            &parent,
            &pose,
            None,
            &c,
            LEFT_HAND,
            PLACEMENT_LEFT_HAND,
            1.0,
        )
        .unwrap();
        assert_eq!(x(p.root_local), 12.0);
        assert_eq!(x(p.part_local[0]), 3.0);
        assert_eq!(x(p.attached_parts[0]), 15.0);
    }

    #[test]
    fn a_missing_placement_falls_back_to_the_default() {
        let parent = parent_with(RIGHT_HAND, -1, 4.0);
        let c = child(vec![placement(PLACEMENT_DEFAULT, 6.0)]);
        let p = compose_child(
            &parent,
            &[],
            None,
            &c,
            RIGHT_HAND,
            PLACEMENT_RIGHT_HAND_COMBAT,
            1.0,
        )
        .unwrap();
        assert_eq!(x(p.attached_parts[0]), 10.0);
    }

    #[test]
    fn a_missing_holding_location_refuses_the_attachment() {
        let empty = crate::animation::test_support::setup(0);
        assert!(compose_child(&empty, &[], None, &empty, RIGHT_HAND, 1, 1.0).is_none());
    }

    #[test]
    fn an_unavailable_hand_part_refuses_without_falling_back_to_the_root() {
        let parent = parent_with(RIGHT_HAND, 1, 4.0);
        let c = child(vec![placement(PLACEMENT_DEFAULT, 1.0)]);
        let pose = [Mat4::IDENTITY, Mat4::from_translation(Vec3::X * 10.0)];
        assert!(
            compose_child(
                &parent,
                &pose,
                Some(&[true, false]),
                &c,
                RIGHT_HAND,
                PLACEMENT_DEFAULT,
                1.0
            )
            .is_none()
        );
    }

    #[test]
    fn a_part_beyond_the_pose_hangs_from_the_root() {
        let parent = parent_with(RIGHT_HAND, 99, 4.0);
        let c = child(vec![placement(PLACEMENT_DEFAULT, 1.0)]);
        let pose = [Mat4::from_translation(Vec3::X * 10.0)];
        let p = compose_child(
            &parent,
            &pose,
            Some(&[true]),
            &c,
            RIGHT_HAND,
            PLACEMENT_DEFAULT,
            1.0,
        )
        .unwrap();
        assert_eq!(x(p.root_local), 4.0);
    }

    #[test]
    fn nested_child_roots_compose_through_the_immediate_parent() {
        let top = Mat4::from_translation(Vec3::X * 100.0);
        let child_world = top * Mat4::from_translation(Vec3::X * 10.0);
        let grandchild_world = child_world * Mat4::from_translation(Vec3::X * 2.0);
        assert_eq!(x(grandchild_world), 112.0);
    }

    #[test]
    fn the_rigid_pose_excludes_default_scale_and_scales_only_the_origin() {
        let parent = parent_with(RIGHT_HAND, -1, 4.0);
        let mut c = child(vec![placement(PLACEMENT_DEFAULT, 3.0)]);
        c.default_scale = vec![Vector3 {
            x: 5.0,
            y: 6.0,
            z: 7.0,
        }];
        let p = compose_child(&parent, &[], None, &c, RIGHT_HAND, PLACEMENT_DEFAULT, 2.0).unwrap();
        let rigid = p.part_local[0];
        assert_eq!(rigid.w_axis.truncate(), Vec3::new(6.0, 0.0, 0.0));
        assert_eq!(
            Vec3::new(rigid.x_axis.x, rigid.y_axis.y, rigid.z_axis.z),
            Vec3::ONE
        );
        let visual = p.attached_parts[0];
        assert_eq!(
            Vec3::new(visual.x_axis.x, visual.y_axis.y, visual.z_axis.z),
            Vec3::new(10.0, 12.0, 14.0)
        );
    }
}
