//! The per-cell object lists ("shadow objects"): which objects' collision
//! parts are in which cell.
//!
//! An object is registered at a seed cell; a flood from there works out the
//! cells its shape reaches (see [`crate::cell_transit`]) and a row for each
//! part goes into each of those cells' lists. The movement sweep then tests
//! only the objects listed in the cells it passes through. Objects with a
//! drawn part array also get a cell array and per-cell draw rows.

use std::collections::{HashMap, HashSet};

use glam::{Quat, Vec2, Vec3};

use crate::cell_transit::{self, CellTransitError};
use crate::collision_flags::EntityCollisionFlags;
use crate::geometry::Sphere;
use crate::shadow::{
    RetailCellArrayRoute, RetailPartEntry, ShadowCollisionType, ShadowEntry, ShadowPartBox,
    ShadowShape,
};
use crate::slot_map::SlotMap;
use crate::world::CollisionWorld;

/// How deep a chain of attached objects may go.
const MAX_ATTACH_CHAIN_DEPTH: usize = 64;

/// At most this many cylinders take part in a cylinder flood.
const RETAIL_SPHERE_CAP: usize = 10;

/// What a registration remembers so the object can be moved or re-flooded.
#[derive(Debug, Clone, PartialEq)]
pub struct RegistrationRecord {
    /// The cell the object was registered at.
    pub seed_cell_id: u32,
    /// The object's world position.
    pub entity_world_pos: Vec3,
    /// The object's world rotation.
    pub entity_world_rot: Quat,
    /// The object's physics state bits.
    pub state: u32,
    /// What kind of object it is.
    pub flags: EntityCollisionFlags,
    /// A landblock static (ends with its landblock).
    pub is_static: bool,
    /// Registered with a list of shapes.
    pub is_multi_part: bool,
    /// Single shape: model id.
    pub gfx_obj_id: u32,
    /// Single shape: radius.
    pub radius: f32,
    /// Single shape: kind.
    pub collision_type: ShadowCollisionType,
    /// Single shape: cylinder height.
    pub cyl_height: f32,
    /// Single shape: scale.
    pub scale: f32,
}

/// How a set-position commit updates an object's rows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum PhysicsShadowCommitAction {
    /// Move the rows in place.
    #[default]
    None,
    /// Re-flood from the new position.
    Recalculate,
    /// Put the rows in the cells the transition found.
    Replace,
    /// Keep the cells, move the rows.
    Preserve,
}

/// Arguments of a single-shape registration.
#[derive(Debug, Clone, PartialEq)]
pub struct SingleShape {
    /// The model id (collision trees), else anything.
    pub gfx_obj_id: u32,
    /// Radius, metres.
    pub radius: f32,
    /// The kind of shape.
    pub collision_type: ShadowCollisionType,
    /// Cylinder height, metres.
    pub cyl_height: f32,
    /// Scale.
    pub scale: f32,
}

impl Default for SingleShape {
    fn default() -> Self {
        Self {
            gfx_obj_id: 0,
            radius: 0.0,
            collision_type: ShadowCollisionType::Bsp,
            cyl_height: 0.0,
            scale: 1.0,
        }
    }
}

/// Where a registration is: the object's place, and the landblock that
/// gives its seed cell when none is named.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct RegistrationSite {
    /// The object's world position.
    pub world_pos: Vec3,
    /// The object's world rotation.
    pub rotation: Quat,
    /// World x of the landblock's corner.
    pub world_offset_x: f32,
    /// World y of the landblock's corner.
    pub world_offset_y: f32,
    /// The landblock (any id in it).
    pub landblock_id: u32,
    /// The seed cell; 0 derives an outdoor cell from the position.
    pub seed_cell_id: u32,
}

/// The per-cell object lists.
#[derive(Debug, Clone, Default)]
pub struct ShadowObjectRegistry {
    cells: HashMap<u32, Vec<ShadowEntry>>,
    entity_to_cells: SlotMap<Vec<u32>>,
    suspended_entities: HashSet<u32>,
    suspended_entity_cells: HashMap<u32, Vec<u32>>,
    withdrawn_prefixes_by_owner: HashMap<u32, HashSet<u32>>,
    entity_shapes: HashMap<u32, Vec<ShadowShape>>,
    entity_retail_part_arrays: HashMap<u32, Vec<ShadowShape>>,
    retail_cell_arrays: SlotMap<Vec<u32>>,
    retail_cell_array_routes: HashMap<u32, RetailCellArrayRoute>,
    retail_part_entries_by_cell: HashMap<u32, Vec<RetailPartEntry>>,
    child_parent: HashMap<u32, u32>,
    parent_children: HashMap<u32, Vec<u32>>,
    child_part_arrays: HashMap<u32, Vec<ShadowShape>>,
    entity_reg: SlotMap<RegistrationRecord>,
    retail_cell_array_revision: u64,
    cell_render_stamps: HashMap<u32, u64>,
    next_cell_render_revision: u64,
}

impl ShadowObjectRegistry {
    /// An empty registry.
    #[must_use]
    pub fn new() -> Self {
        Self {
            retail_cell_array_revision: 1,
            ..Self::default()
        }
    }

    // ── Revisions ──────────────────────────────────────────────────────

    /// A number that changes whenever the draw rows of a cell change; 0 for
    /// a cell with none. Assigned on first ask.
    pub fn cell_render_revision(&mut self, cell_id: u32) -> u64 {
        match self.retail_part_entries_by_cell.get(&cell_id) {
            Some(entries) if !entries.is_empty() => {}
            _ => return 0,
        }
        if let Some(stamp) = self.cell_render_stamps.get(&cell_id) {
            return *stamp;
        }
        self.next_cell_render_revision += 1;
        let revision = self.next_cell_render_revision;
        self.cell_render_stamps.insert(cell_id, revision);
        revision
    }

    fn mark_cell_render_changed(&mut self, cell_id: u32) {
        if self.cell_render_stamps.contains_key(&cell_id) {
            self.next_cell_render_revision += 1;
            self.cell_render_stamps
                .insert(cell_id, self.next_cell_render_revision);
        }
    }

    fn remove_cell_draw_list(&mut self, cell_id: u32) {
        self.retail_part_entries_by_cell.remove(&cell_id);
        self.cell_render_stamps.remove(&cell_id);
    }

    /// A number that changes whenever any object's cell array changes.
    #[must_use]
    pub fn retail_cell_array_revision(&self) -> u64 {
        self.retail_cell_array_revision
    }

    fn advance_retail_cell_array_revision(&mut self) {
        self.retail_cell_array_revision += 1;
    }

    // ── Floods ─────────────────────────────────────────────────────────

    #[allow(clippy::too_many_arguments)]
    fn compute_contract_a_cell_array(
        world: &CollisionWorld,
        seed_cell_id: u32,
        world_pos: Vec3,
        world_rot: Quat,
        state: u32,
        collision_shapes: &[ShadowShape],
        part_array: &[ShadowShape],
        is_static: bool,
    ) -> Result<(Vec<u32>, RetailCellArrayRoute), CellTransitError> {
        let has_cylsphere = collision_shapes
            .iter()
            .any(|s| s.collision_type == ShadowCollisionType::Cylinder);
        let cylsphere_route = state & 0x1_0000 == 0 && has_cylsphere;
        if cylsphere_route {
            let spheres = flood_spheres(world_pos, world_rot, collision_shapes);
            let cells = cell_transit::build_shadow_cell_set(
                world,
                seed_cell_id,
                &spheres,
                spheres.len(),
                is_static,
            );
            Ok((cells, RetailCellArrayRoute::Cylsphere))
        } else {
            let (boxes, spheres) = bsp_parts(world_pos, world_rot, part_array);
            let cells = cell_transit::build_shadow_cell_set_from_parts(
                world,
                seed_cell_id,
                &boxes,
                &spheres,
                is_static,
            )?;
            Ok((cells, RetailCellArrayRoute::BoundingBox))
        }
    }

    fn publish_retail_cell_array(
        &mut self,
        entity_id: u32,
        cell_array: &[u32],
        route: RetailCellArrayRoute,
        part_array: &[ShadowShape],
    ) {
        if self.child_parent.contains_key(&entity_id) {
            if !part_array.is_empty() {
                self.child_part_arrays
                    .insert(entity_id, part_array.to_vec());
            }
            self.retail_cell_array_routes.insert(entity_id, route);
            self.publish_child_entries(entity_id);
            return;
        }
        if let Some(previous) = self.retail_cell_arrays.remove(entity_id) {
            self.remove_retail_part_entries_from_cells(entity_id, &previous);
            self.advance_retail_cell_array_revision();
        }
        self.retail_cell_array_routes.insert(entity_id, route);
        if cell_array.is_empty() {
            self.republish_attached_children(entity_id);
            return;
        }
        let ordered = cell_array.to_vec();
        self.retail_cell_arrays.insert(entity_id, ordered.clone());
        self.advance_retail_cell_array_revision();
        self.publish_retail_part_entries(entity_id, &ordered, part_array);
        self.republish_attached_children(entity_id);
    }

    fn publish_retail_product_from_exact_cells(&mut self, entity_id: u32, exact_cells: &[u32]) {
        let Some(part_array) = self.entity_retail_part_arrays.get(&entity_id) else {
            return;
        };
        if part_array.is_empty() {
            return;
        }
        let part_array = part_array.clone();
        let route = self
            .retail_cell_array_routes
            .get(&entity_id)
            .copied()
            .unwrap_or_default();
        self.publish_retail_cell_array(entity_id, exact_cells, route, &part_array);
    }

    fn clear_retail_cell_array(&mut self, entity_id: u32) {
        if let Some(cells) = self.retail_cell_arrays.remove(entity_id) {
            self.remove_retail_part_entries_from_cells(entity_id, &cells);
            self.advance_retail_cell_array_revision();
        }
        self.retail_cell_array_routes.remove(&entity_id);
        self.entity_retail_part_arrays.remove(&entity_id);
    }

    fn remove_retail_part_entries_from_cells(&mut self, entity_id: u32, cell_ids: &[u32]) {
        for &cell_id in cell_ids {
            let Some(entries) = self.retail_part_entries_by_cell.get_mut(&cell_id) else {
                continue;
            };
            let before = entries.len();
            // Each removed row marks the cell changed, as OpenAC does per
            // row; one bump per row keeps the revision count identical.
            entries.retain(|e| e.entity_id != entity_id);
            let removed = before - entries.len();
            let now_empty = entries.is_empty();
            for _ in 0..removed {
                self.mark_cell_render_changed(cell_id);
            }
            if now_empty {
                self.remove_cell_draw_list(cell_id);
            }
        }
    }

    fn publish_retail_part_entries(
        &mut self,
        entity_id: u32,
        ordered_cells: &[u32],
        part_array: &[ShadowShape],
    ) {
        let clip_planes_required = ordered_cells.len() > 1;
        for &cell_id in ordered_cells {
            self.retail_part_entries_by_cell.entry(cell_id).or_default();
            self.mark_cell_render_changed(cell_id);
            let entries = self
                .retail_part_entries_by_cell
                .get_mut(&cell_id)
                .expect("just inserted");
            for (part_index, part) in part_array.iter().enumerate() {
                entries.push(RetailPartEntry {
                    entity_id,
                    part_index,
                    gfx_obj_id: part.gfx_obj_id,
                    cell_id,
                    clip_planes_required,
                });
            }
        }
    }

    /// The cell array of an object (the cells it is drawn in), when it has
    /// one.
    #[must_use]
    pub fn retail_cell_array(&self, entity_id: u32) -> Option<&[u32]> {
        self.retail_cell_arrays.get(entity_id).map(Vec::as_slice)
    }

    /// The draw rows of a cell.
    #[must_use]
    pub fn retail_part_entries_in_cell(&self, cell_id: u32) -> &[RetailPartEntry] {
        self.retail_part_entries_by_cell
            .get(&cell_id)
            .map_or(&[], Vec::as_slice)
    }

    /// How an object's cell array was worked out.
    #[must_use]
    pub fn retail_cell_array_route(&self, entity_id: u32) -> RetailCellArrayRoute {
        self.retail_cell_array_routes
            .get(&entity_id)
            .copied()
            .unwrap_or_default()
    }

    // ── Attached children ──────────────────────────────────────────────

    /// Attaches a drawn child (a held item) to a root object: the child is
    /// drawn in the root's cells. False for a self-attach or a cycle.
    pub fn attach_child(
        &mut self,
        child_entity_id: u32,
        root_entity_id: u32,
        child_part_array: Vec<ShadowShape>,
    ) -> bool {
        if !self.try_validate_attach(child_entity_id, root_entity_id) {
            return false;
        }
        if self.child_parent.contains_key(&child_entity_id) {
            self.detach_child_core(child_entity_id, true);
        }
        self.child_parent.insert(child_entity_id, root_entity_id);
        self.parent_children
            .entry(root_entity_id)
            .or_default()
            .push(child_entity_id);
        self.child_part_arrays
            .insert(child_entity_id, child_part_array);
        self.publish_child_entries(child_entity_id);
        true
    }

    /// Detaches a child; false when it was not attached.
    pub fn detach_child(&mut self, child_entity_id: u32) -> bool {
        self.detach_child_core(child_entity_id, true)
    }

    fn detach_child_core(&mut self, child_entity_id: u32, remove_from_parent_list: bool) -> bool {
        let Some(&parent_id) = self.child_parent.get(&child_entity_id) else {
            return false;
        };

        if let Some(grandchildren) = self.parent_children.get(&child_entity_id)
            && !grandchildren.is_empty()
        {
            let to_detach = grandchildren.clone();
            for g in to_detach {
                self.detach_child_core(g, false);
            }
            self.parent_children.remove(&child_entity_id);
        }

        if let Some(cells) = self.retail_cell_arrays.remove(child_entity_id) {
            self.remove_retail_part_entries_from_cells(child_entity_id, &cells);
            self.advance_retail_cell_array_revision();
        }
        self.child_part_arrays.remove(&child_entity_id);
        self.child_parent.remove(&child_entity_id);

        if remove_from_parent_list && let Some(siblings) = self.parent_children.get_mut(&parent_id)
        {
            if let Some(pos) = siblings.iter().position(|s| *s == child_entity_id) {
                siblings.remove(pos);
            }
            if siblings.is_empty() {
                self.parent_children.remove(&parent_id);
            }
        }
        true
    }

    fn try_validate_attach(&self, child_entity_id: u32, parent_id: u32) -> bool {
        if child_entity_id == parent_id {
            return false;
        }
        let mut current = parent_id;
        for _ in 0..MAX_ATTACH_CHAIN_DEPTH {
            let Some(&next) = self.child_parent.get(&current) else {
                return true;
            };
            if next == child_entity_id {
                return false;
            }
            current = next;
        }
        false
    }

    fn resolve_attach_root(&self, entity_id: u32) -> u32 {
        let mut current = entity_id;
        for _ in 0..MAX_ATTACH_CHAIN_DEPTH {
            let Some(&parent) = self.child_parent.get(&current) else {
                return current;
            };
            current = parent;
        }
        current
    }

    fn publish_child_entries(&mut self, child_entity_id: u32) {
        if let Some(previous) = self.retail_cell_arrays.remove(child_entity_id) {
            self.remove_retail_part_entries_from_cells(child_entity_id, &previous);
            self.advance_retail_cell_array_revision();
        }
        let Some(part_array) = self.child_part_arrays.get(&child_entity_id).cloned() else {
            return;
        };
        let root = self.resolve_attach_root(child_entity_id);
        if root == child_entity_id {
            return;
        }
        let Some(root_cells) = self.retail_cell_arrays.get(root) else {
            return;
        };
        if root_cells.is_empty() {
            return;
        }
        let root_cells = root_cells.clone();
        self.retail_cell_arrays
            .insert(child_entity_id, root_cells.clone());
        self.advance_retail_cell_array_revision();
        self.publish_retail_part_entries(child_entity_id, &root_cells, &part_array);
    }

    fn republish_attached_children(&mut self, root_id: u32) {
        let Some(children) = self.parent_children.get(&root_id) else {
            return;
        };
        if children.is_empty() {
            return;
        }
        let children = children.clone();
        for child_id in children {
            self.publish_child_entries(child_id);
            self.republish_attached_children(child_id);
        }
    }

    // ── Registration ───────────────────────────────────────────────────

    /// Registers an object with one shape. The flood runs first; when it
    /// reaches no cell the previous registration (if any) is kept.
    #[allow(clippy::too_many_arguments)]
    pub fn register(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        shape: &SingleShape,
        site: RegistrationSite,
        state: u32,
        flags: EntityCollisionFlags,
        is_static: bool,
        part_array: Option<&[ShadowShape]>,
    ) -> Result<(), CellTransitError> {
        let seed = if site.seed_cell_id != 0 {
            site.seed_cell_id
        } else {
            derive_outdoor_seed(
                site.world_pos,
                site.world_offset_x,
                site.world_offset_y,
                site.landblock_id,
            )
        };
        if seed == 0 {
            return Ok(());
        }

        let part_array = part_array.filter(|p| !p.is_empty());
        let mut retail_route = RetailCellArrayRoute::None;
        let cell_set = if let Some(parts) = part_array {
            let collision_shapes: Vec<ShadowShape> =
                if shape.collision_type == ShadowCollisionType::Cylinder {
                    vec![ShadowShape::cylinder(
                        shape.gfx_obj_id,
                        Vec3::ZERO,
                        Quat::IDENTITY,
                        shape.scale,
                        shape.radius,
                        shape.cyl_height,
                    )]
                } else {
                    Vec::new()
                };
            let (cells, route) = Self::compute_contract_a_cell_array(
                world,
                seed,
                site.world_pos,
                site.rotation,
                state,
                &collision_shapes,
                parts,
                is_static,
            )?;
            retail_route = route;
            cells
        } else {
            let spheres = [Sphere::new(site.world_pos, shape.radius)];
            cell_transit::build_shadow_cell_set(world, seed, &spheres, 1, is_static)
        };
        if cell_set.is_empty() {
            return Ok(());
        }

        self.deregister_core(entity_id);

        let entry = ShadowEntry::single(
            entity_id,
            shape.gfx_obj_id,
            site.world_pos,
            site.rotation,
            shape.radius,
            shape.collision_type,
            shape.cyl_height,
            shape.scale,
            state,
            flags,
        );
        for &cell_id in &cell_set {
            self.add_entry_to_cell(entry, cell_id);
        }
        self.entity_to_cells.insert(entity_id, cell_set.clone());
        self.entity_reg.insert(
            entity_id,
            RegistrationRecord {
                seed_cell_id: seed,
                entity_world_pos: site.world_pos,
                entity_world_rot: site.rotation,
                state,
                flags,
                is_static,
                is_multi_part: false,
                gfx_obj_id: shape.gfx_obj_id,
                radius: shape.radius,
                collision_type: shape.collision_type,
                cyl_height: shape.cyl_height,
                scale: shape.scale,
            },
        );

        if let Some(parts) = part_array {
            self.entity_retail_part_arrays
                .insert(entity_id, parts.to_vec());
            self.publish_retail_cell_array(entity_id, &cell_set, retail_route, parts);
        }
        Ok(())
    }

    /// Registers an object with a list of shapes (one row per shape per
    /// cell). With no shapes it is registered for drawing only (when it has
    /// a part array) or removed.
    #[allow(clippy::too_many_arguments)]
    pub fn register_multi_part(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        shapes: &[ShadowShape],
        site: RegistrationSite,
        state: u32,
        flags: EntityCollisionFlags,
        is_static: bool,
        part_array: Option<&[ShadowShape]>,
    ) -> Result<(), CellTransitError> {
        if shapes.is_empty() {
            match part_array {
                Some(parts) if !parts.is_empty() => {
                    return self.register_render_only(
                        world, entity_id, site, state, flags, is_static, parts,
                    );
                }
                _ => {
                    self.deregister(entity_id);
                    return Ok(());
                }
            }
        }

        let seed = if site.seed_cell_id != 0 {
            site.seed_cell_id
        } else {
            derive_outdoor_seed(
                site.world_pos,
                site.world_offset_x,
                site.world_offset_y,
                site.landblock_id,
            )
        };
        if seed == 0 {
            return Ok(());
        }

        let part_array = part_array.filter(|p| !p.is_empty());
        let mut retail_route = RetailCellArrayRoute::None;
        let cell_set = if let Some(parts) = part_array {
            let (cells, route) = Self::compute_contract_a_cell_array(
                world,
                seed,
                site.world_pos,
                site.rotation,
                state,
                shapes,
                parts,
                is_static,
            )?;
            retail_route = route;
            cells
        } else {
            let has_bsp = shapes
                .iter()
                .any(|s| s.collision_type == ShadowCollisionType::Bsp);
            if has_bsp {
                let (boxes, spheres) = bsp_parts(site.world_pos, site.rotation, shapes);
                cell_transit::build_shadow_cell_set_from_parts(
                    world, seed, &boxes, &spheres, is_static,
                )?
            } else {
                let spheres = flood_spheres(site.world_pos, site.rotation, shapes);
                cell_transit::build_shadow_cell_set(world, seed, &spheres, spheres.len(), is_static)
            }
        };
        if cell_set.is_empty() {
            return Ok(());
        }

        self.deregister_core(entity_id);
        self.entity_shapes.insert(entity_id, shapes.to_vec());

        for shape in shapes {
            let entry = part_entry(
                entity_id,
                shape,
                site.world_pos,
                site.rotation,
                state,
                flags,
            );
            for &cell_id in &cell_set {
                self.add_entry_to_cell(entry, cell_id);
            }
        }

        self.entity_to_cells.insert(entity_id, cell_set.clone());
        self.entity_reg.insert(
            entity_id,
            multi_part_record(seed, site.world_pos, site.rotation, state, flags, is_static),
        );

        if let Some(parts) = part_array {
            self.entity_retail_part_arrays
                .insert(entity_id, parts.to_vec());
            self.publish_retail_cell_array(entity_id, &cell_set, retail_route, parts);
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    fn register_render_only(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        site: RegistrationSite,
        state: u32,
        flags: EntityCollisionFlags,
        is_static: bool,
        part_array: &[ShadowShape],
    ) -> Result<(), CellTransitError> {
        let seed = if site.seed_cell_id != 0 {
            site.seed_cell_id
        } else {
            derive_outdoor_seed(
                site.world_pos,
                site.world_offset_x,
                site.world_offset_y,
                site.landblock_id,
            )
        };
        if seed == 0 {
            return Ok(());
        }

        let (cell_set, retail_route) = Self::compute_contract_a_cell_array(
            world,
            seed,
            site.world_pos,
            site.rotation,
            state,
            &[],
            part_array,
            is_static,
        )?;
        if cell_set.is_empty() {
            return Ok(());
        }

        self.deregister_core(entity_id);
        self.entity_shapes.insert(entity_id, Vec::new());
        self.entity_reg.insert(
            entity_id,
            multi_part_record(seed, site.world_pos, site.rotation, state, flags, is_static),
        );
        self.entity_retail_part_arrays
            .insert(entity_id, part_array.to_vec());
        self.publish_retail_cell_array(entity_id, &cell_set, retail_route, part_array);
        Ok(())
    }

    /// Replaces a multi-part object's shapes (and state) without moving it:
    /// its rows are rebuilt in the cells it already occupies. An object not
    /// yet registered multi-part is registered (and suspended when asked).
    #[allow(clippy::too_many_arguments)]
    pub fn replace_multi_part_payload(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        shapes: &[ShadowShape],
        site: RegistrationSite,
        state: u32,
        flags: EntityCollisionFlags,
        is_static: bool,
        suspend_if_new: bool,
        part_array: Option<&[ShadowShape]>,
    ) -> Result<(), CellTransitError> {
        let prior = match self.entity_reg.get(entity_id) {
            Some(r) if r.is_multi_part => r.clone(),
            _ => {
                if shapes.is_empty() && part_array.is_none_or(<[ShadowShape]>::is_empty) {
                    return Ok(());
                }
                self.register_multi_part(
                    world, entity_id, shapes, site, state, flags, is_static, part_array,
                )?;
                if suspend_if_new {
                    self.suspend(entity_id);
                }
                return Ok(());
            }
        };

        let suspended = self.suspended_entities.contains(&entity_id);
        self.entity_shapes.insert(entity_id, shapes.to_vec());
        self.entity_reg.insert(
            entity_id,
            RegistrationRecord {
                entity_world_pos: site.world_pos,
                entity_world_rot: site.rotation,
                state,
                flags,
                ..prior
            },
        );

        if let Some(parts) = part_array.filter(|p| !p.is_empty()) {
            self.entity_retail_part_arrays
                .insert(entity_id, parts.to_vec());
            if let Some(retail_cells) = self.retail_cell_arrays.get(entity_id)
                && !retail_cells.is_empty()
            {
                let retail_cells = retail_cells.clone();
                self.remove_retail_part_entries_from_cells(entity_id, &retail_cells);
                self.publish_retail_part_entries(entity_id, &retail_cells, parts);
                self.republish_attached_children(entity_id);
            }
        }

        if suspended {
            return Ok(());
        }
        let Some(cells) = self.entity_to_cells.get(entity_id).cloned() else {
            return Ok(());
        };

        for cell_id in &cells {
            if let Some(entries) = self.cells.get_mut(cell_id) {
                entries.retain(|e| e.entity_id != entity_id);
            }
        }
        for shape in shapes {
            let entry = part_entry(
                entity_id,
                shape,
                site.world_pos,
                site.rotation,
                state,
                flags,
            );
            for &cell_id in &cells {
                self.add_entry_to_cell(entry, cell_id);
            }
        }
        Ok(())
    }

    fn add_entry_to_cell(&mut self, entry: ShadowEntry, cell_id: u32) {
        self.cells.entry(cell_id).or_default().push(entry);
    }

    /// Moves a registered object: re-floods it from the new position with
    /// its retained shapes.
    pub fn update_position(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        site: RegistrationSite,
    ) -> Result<(), CellTransitError> {
        let Some(reg) = self.entity_reg.get(entity_id).cloned() else {
            return Ok(());
        };
        if site.seed_cell_id == 0
            && derive_outdoor_seed(
                site.world_pos,
                site.world_offset_x,
                site.world_offset_y,
                site.landblock_id,
            ) == 0
        {
            return Ok(());
        }
        let retained_part_array = self.entity_retail_part_arrays.get(&entity_id).cloned();

        if reg.is_multi_part
            && let Some(shapes) = self.entity_shapes.get(&entity_id).cloned()
        {
            return self.register_multi_part(
                world,
                entity_id,
                &shapes,
                site,
                reg.state,
                reg.flags,
                reg.is_static,
                retained_part_array.as_deref(),
            );
        }

        self.register(
            world,
            entity_id,
            &SingleShape {
                gfx_obj_id: reg.gfx_obj_id,
                radius: reg.radius,
                collision_type: reg.collision_type,
                cyl_height: reg.cyl_height,
                scale: reg.scale,
            },
            site,
            reg.state,
            reg.flags,
            reg.is_static,
            retained_part_array.as_deref(),
        )
    }

    /// Commits a set-position result to an object's rows.
    #[allow(clippy::too_many_arguments)]
    pub fn commit_set_position(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        world_position: Vec3,
        world_rotation: Quat,
        seed_cell_id: u32,
        world_offset_x: f32,
        world_offset_y: f32,
        action: PhysicsShadowCommitAction,
        cross_cell_ids: &[u32],
    ) -> Result<(), CellTransitError> {
        let Some(registration) = self.entity_reg.get(entity_id).cloned() else {
            return Ok(());
        };
        match action {
            PhysicsShadowCommitAction::None | PhysicsShadowCommitAction::Preserve => {
                self.refresh_position_rows(
                    entity_id,
                    &registration,
                    world_position,
                    world_rotation,
                    seed_cell_id,
                );
                Ok(())
            }
            PhysicsShadowCommitAction::Recalculate => self.update_position(
                world,
                entity_id,
                RegistrationSite {
                    world_pos: world_position,
                    rotation: world_rotation,
                    world_offset_x,
                    world_offset_y,
                    landblock_id: seed_cell_id & 0xFFFF_0000,
                    seed_cell_id,
                },
            ),
            PhysicsShadowCommitAction::Replace => {
                if cross_cell_ids.is_empty() {
                    self.refresh_position_rows(
                        entity_id,
                        &registration,
                        world_position,
                        world_rotation,
                        seed_cell_id,
                    );
                } else {
                    self.replace_position_rows(
                        entity_id,
                        &registration,
                        world_position,
                        world_rotation,
                        seed_cell_id,
                        cross_cell_ids,
                    );
                }
                Ok(())
            }
        }
    }

    fn refresh_position_rows(
        &mut self,
        entity_id: u32,
        registration: &RegistrationRecord,
        world_position: Vec3,
        world_rotation: Quat,
        seed_cell_id: u32,
    ) {
        let retained = self
            .entity_to_cells
            .get(entity_id)
            .or_else(|| self.suspended_entity_cells.get(&entity_id))
            .filter(|c| !c.is_empty())
            .cloned();
        if let Some(retained) = retained {
            self.replace_position_rows(
                entity_id,
                registration,
                world_position,
                world_rotation,
                seed_cell_id,
                &retained,
            );
            return;
        }

        let moved = RegistrationRecord {
            seed_cell_id,
            entity_world_pos: world_position,
            entity_world_rot: world_rotation,
            ..registration.clone()
        };

        if !self.entity_to_cells.contains_key(entity_id)
            && !self.suspended_entity_cells.contains_key(&entity_id)
            && seed_cell_id != 0
            && self
                .entity_retail_part_arrays
                .get(&entity_id)
                .is_some_and(|p| !p.is_empty())
        {
            self.suspended_entities.remove(&entity_id);
            self.entity_reg.insert(entity_id, moved);
            self.publish_retail_product_from_exact_cells(entity_id, &[seed_cell_id]);
            return;
        }

        self.entity_reg.insert(entity_id, moved);
    }

    fn replace_position_rows(
        &mut self,
        entity_id: u32,
        registration: &RegistrationRecord,
        world_position: Vec3,
        world_rotation: Quat,
        seed_cell_id: u32,
        cell_ids: &[u32],
    ) {
        if let Some(previous) = self.entity_to_cells.get(entity_id).cloned() {
            for cell_id in previous {
                if let Some(entries) = self.cells.get_mut(&cell_id) {
                    entries.retain(|e| e.entity_id != entity_id);
                }
            }
        }

        self.suspended_entities.remove(&entity_id);
        self.suspended_entity_cells.remove(&entity_id);
        self.entity_reg.insert(
            entity_id,
            RegistrationRecord {
                seed_cell_id,
                entity_world_pos: world_position,
                entity_world_rot: world_rotation,
                ..registration.clone()
            },
        );

        let mut exact_cells: Vec<u32> = Vec::with_capacity(cell_ids.len());
        for &cell_id in cell_ids {
            if cell_id == 0 || exact_cells.contains(&cell_id) {
                continue;
            }
            exact_cells.push(cell_id);
        }

        let shapes = if registration.is_multi_part {
            self.entity_shapes.get(&entity_id).cloned()
        } else {
            None
        };
        let wrote_collision_entries = match &shapes {
            Some(shapes) => {
                for shape in shapes {
                    let entry = part_entry(
                        entity_id,
                        shape,
                        world_position,
                        world_rotation,
                        registration.state,
                        registration.flags,
                    );
                    for &cell_id in &exact_cells {
                        self.add_entry_to_cell(entry, cell_id);
                    }
                }
                !shapes.is_empty()
            }
            None => {
                let entry = ShadowEntry::single(
                    entity_id,
                    registration.gfx_obj_id,
                    world_position,
                    world_rotation,
                    registration.radius,
                    registration.collision_type,
                    registration.cyl_height,
                    registration.scale,
                    registration.state,
                    registration.flags,
                );
                for &cell_id in &exact_cells {
                    self.add_entry_to_cell(entry, cell_id);
                }
                true
            }
        };

        if exact_cells.is_empty() || !wrote_collision_entries {
            self.entity_to_cells.remove(entity_id);
        } else {
            self.entity_to_cells.insert(entity_id, exact_cells.clone());
        }
        if let Some(withdrawn) = self.withdrawn_prefixes_by_owner.get_mut(&entity_id) {
            for cell in &exact_cells {
                withdrawn.remove(&(cell & 0xFFFF_0000));
            }
            if withdrawn.is_empty() {
                self.withdrawn_prefixes_by_owner.remove(&entity_id);
            }
        }
        self.publish_retail_product_from_exact_cells(entity_id, &exact_cells);
    }

    /// Takes an object out of every cell while keeping its registration
    /// (it can come back); false when it is not registered.
    pub fn suspend(&mut self, entity_id: u32) -> bool {
        if !self.entity_reg.contains_key(entity_id) {
            return false;
        }
        if let Some(cell_ids) = self.entity_to_cells.remove(entity_id) {
            for cell_id in &cell_ids {
                if let Some(list) = self.cells.get_mut(cell_id) {
                    list.retain(|e| e.entity_id != entity_id);
                }
            }
            self.suspended_entity_cells.insert(entity_id, cell_ids);
        }
        if let Some(retail_cells) = self.retail_cell_arrays.remove(entity_id) {
            self.remove_retail_part_entries_from_cells(entity_id, &retail_cells);
            self.advance_retail_cell_array_revision();
            self.republish_attached_children(entity_id);
        }
        self.suspended_entities.insert(entity_id);
        true
    }

    /// Re-floods every object rooted in or reaching into a landblock (after
    /// the landblock's cells arrived).
    pub fn reflood_landblock(
        &mut self,
        world: &CollisionWorld,
        landblock_id: u32,
    ) -> Result<(), CellTransitError> {
        for owner in self.capture_reflood_owners_for_landblock(landblock_id) {
            self.reflood_owner_for_landblock(world, owner, landblock_id)?;
        }
        Ok(())
    }

    /// The objects a landblock re-flood would touch, sorted.
    #[must_use]
    pub fn capture_reflood_owners_for_landblock(&self, landblock_id: u32) -> Vec<u32> {
        let lb_prefix = landblock_id & 0xFFFF_0000;
        let mut to_reflood = HashSet::new();
        for (key, reg) in self.entity_reg.iter() {
            if self.suspended_entities.contains(&key) {
                continue;
            }
            if reg.seed_cell_id & 0xFFFF_0000 == lb_prefix {
                to_reflood.insert(key);
                continue;
            }
            if let Some(cells) = self.entity_to_cells.get(key)
                && cells.iter().any(|c| c & 0xFFFF_0000 == lb_prefix)
            {
                to_reflood.insert(key);
            }
            if self
                .withdrawn_prefixes_by_owner
                .get(&key)
                .is_some_and(|p| p.contains(&lb_prefix))
            {
                to_reflood.insert(key);
            }
        }
        let mut ordered: Vec<u32> = to_reflood.into_iter().collect();
        ordered.sort_unstable();
        ordered
    }

    /// Re-floods one object from its retained registration. A removed or
    /// suspended object is left alone.
    pub fn reflood_owner_for_landblock(
        &mut self,
        world: &CollisionWorld,
        entity_id: u32,
        landblock_id: u32,
    ) -> Result<(), CellTransitError> {
        let lb_prefix = landblock_id & 0xFFFF_0000;
        if self.suspended_entities.contains(&entity_id) {
            return Ok(());
        }
        let Some(reg) = self.entity_reg.get(entity_id).cloned() else {
            return Ok(());
        };

        let withdrawn_before = self.withdrawn_prefixes_by_owner.get(&entity_id).cloned();
        let retained_part_array = self.entity_retail_part_arrays.get(&entity_id).cloned();
        let site = RegistrationSite {
            world_pos: reg.entity_world_pos,
            rotation: reg.entity_world_rot,
            world_offset_x: 0.0,
            world_offset_y: 0.0,
            landblock_id: lb_prefix,
            seed_cell_id: reg.seed_cell_id,
        };

        if reg.is_multi_part
            && let Some(shapes) = self.entity_shapes.get(&entity_id).cloned()
        {
            self.register_multi_part(
                world,
                entity_id,
                &shapes,
                site,
                reg.state,
                reg.flags,
                reg.is_static,
                retained_part_array.as_deref(),
            )?;
        } else {
            self.register(
                world,
                entity_id,
                &SingleShape {
                    gfx_obj_id: reg.gfx_obj_id,
                    radius: reg.radius,
                    collision_type: reg.collision_type,
                    cyl_height: reg.cyl_height,
                    scale: reg.scale,
                },
                site,
                reg.state,
                reg.flags,
                reg.is_static,
                retained_part_array.as_deref(),
            )?;
        }

        if let Some(w) = withdrawn_before {
            self.withdrawn_prefixes_by_owner.insert(entity_id, w);
        }

        let refreshed_in_block = self
            .entity_to_cells
            .get(entity_id)
            .is_some_and(|cells| cells.iter().any(|c| c & 0xFFFF_0000 == lb_prefix));
        if refreshed_in_block
            && let Some(withdrawn) = self.withdrawn_prefixes_by_owner.get_mut(&entity_id)
        {
            withdrawn.remove(&lb_prefix);
            if withdrawn.is_empty() {
                self.withdrawn_prefixes_by_owner.remove(&entity_id);
            }
        }
        Ok(())
    }

    /// Changes the physics state carried by an object's rows.
    pub fn update_physics_state(&mut self, entity_id: u32, new_state: u32) {
        if let Some(reg) = self.entity_reg.get_mut(entity_id) {
            reg.state = new_state;
        }
        let Some(cell_ids) = self.entity_to_cells.get(entity_id) else {
            return;
        };
        for cell_id in cell_ids {
            if let Some(list) = self.cells.get_mut(cell_id) {
                for e in list.iter_mut().filter(|e| e.entity_id == entity_id) {
                    e.state = new_state;
                }
            }
        }
    }

    /// Changes the player/killer/impenetrable flags of an object from its
    /// public weenie bitfield.
    pub fn update_pwd_bitfield_flags(&mut self, entity_id: u32, pwd_bitfield: u32) {
        let decoded = EntityCollisionFlags::from_pwd_bitfield(pwd_bitfield);
        let merge = |flags: EntityCollisionFlags| {
            EntityCollisionFlags(
                (flags.0 & !EntityCollisionFlags::PWD_BITFIELD_DERIVED_MASK) | decoded.0,
            )
        };
        if let Some(reg) = self.entity_reg.get_mut(entity_id) {
            let merged = merge(reg.flags);
            if merged == reg.flags {
                return;
            }
            reg.flags = merged;
        }
        let Some(cell_ids) = self.entity_to_cells.get(entity_id) else {
            return;
        };
        for cell_id in cell_ids {
            if let Some(list) = self.cells.get_mut(cell_id) {
                for e in list.iter_mut().filter(|e| e.entity_id == entity_id) {
                    e.flags = merge(e.flags);
                }
            }
        }
    }

    /// Removes an object (and detaches its children, and itself from its
    /// parent).
    pub fn deregister(&mut self, entity_id: u32) {
        if let Some(children) = self.parent_children.get(&entity_id)
            && !children.is_empty()
        {
            let to_detach = children.clone();
            for c in to_detach {
                self.detach_child_core(c, false);
            }
            self.parent_children.remove(&entity_id);
        }
        self.detach_child_core(entity_id, true);
        self.deregister_core(entity_id);
    }

    fn deregister_core(&mut self, entity_id: u32) {
        if let Some(cell_ids) = self.entity_to_cells.remove(entity_id) {
            for cell_id in &cell_ids {
                if let Some(list) = self.cells.get_mut(cell_id) {
                    list.retain(|e| e.entity_id != entity_id);
                }
            }
        }
        self.entity_shapes.remove(&entity_id);
        self.entity_reg.remove(entity_id);
        self.suspended_entities.remove(&entity_id);
        self.suspended_entity_cells.remove(&entity_id);
        self.withdrawn_prefixes_by_owner.remove(&entity_id);
        self.clear_retail_cell_array(entity_id);
    }

    /// Removes every static object rooted in a landblock.
    pub fn deregister_static_owners_for_landblock(&mut self, landblock_id: u32) {
        for owner in self.capture_static_owners_for_landblock(landblock_id) {
            self.deregister_static_owner_for_landblock(owner, landblock_id);
        }
    }

    /// The static objects rooted in a landblock, sorted.
    #[must_use]
    pub fn capture_static_owners_for_landblock(&self, landblock_id: u32) -> Vec<u32> {
        let prefix = landblock_id & 0xFFFF_0000;
        let mut owners: Vec<u32> = self
            .entity_reg
            .iter()
            .filter(|(_, r)| r.is_static && r.seed_cell_id & 0xFFFF_0000 == prefix)
            .map(|(k, _)| k)
            .collect();
        owners.sort_unstable();
        owners
    }

    /// Removes one static object if it is (still) rooted in the landblock.
    pub fn deregister_static_owner_for_landblock(&mut self, entity_id: u32, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        if self
            .entity_reg
            .get(entity_id)
            .is_some_and(|r| r.is_static && r.seed_cell_id & 0xFFFF_0000 == prefix)
        {
            self.deregister(entity_id);
        }
    }

    /// Drops a landblock's cells from the lists. Objects that reached into
    /// it remember the landblock (to be re-flooded when it returns); objects
    /// left with no cell are removed, except live (non-static) ones, which
    /// keep their registration.
    pub fn remove_landblock(&mut self, landblock_id: u32) {
        let lb_prefix = landblock_id & 0xFFFF_0000;
        let mut touched_owners: HashSet<u32> = HashSet::new();

        let touching: Vec<u32> = self
            .entity_to_cells
            .iter()
            .filter(|(_, cells)| cells.iter().any(|c| c & 0xFFFF_0000 == lb_prefix))
            .map(|(k, _)| k)
            .collect();
        for entity_id in touching {
            touched_owners.insert(entity_id);
            self.withdrawn_prefixes_by_owner
                .entry(entity_id)
                .or_default()
                .insert(lb_prefix);
        }

        self.cells
            .retain(|cell_id, _| cell_id & 0xFFFF_0000 != lb_prefix);

        let mut entities_to_remove = Vec::new();
        for (key, cells) in self.entity_to_cells.iter_mut() {
            cells.retain(|c| c & 0xFFFF_0000 != lb_prefix);
            if cells.is_empty() {
                entities_to_remove.push(key);
            }
        }
        for eid in entities_to_remove {
            self.entity_to_cells.remove(eid);
            // A streamed-out live object is still alive: keep its
            // registration and shapes for the re-flood; statics end with
            // their landblock.
            let ends = self.entity_reg.get(eid).is_none_or(|r| r.is_static);
            if ends {
                self.entity_shapes.remove(&eid);
                self.entity_reg.remove(eid);
                self.suspended_entities.remove(&eid);
                self.suspended_entity_cells.remove(&eid);
                self.withdrawn_prefixes_by_owner.remove(&eid);
            }
        }
        self.remove_retail_product_for_prefix(lb_prefix);
    }

    fn remove_retail_product_for_prefix(&mut self, lb_prefix: u32) {
        let cells: Vec<u32> = self
            .retail_part_entries_by_cell
            .keys()
            .copied()
            .filter(|c| c & 0xFFFF_0000 == lb_prefix)
            .collect();
        for c in cells {
            self.remove_cell_draw_list(c);
        }

        let mut emptied = Vec::new();
        let mut cell_arrays_touched = false;
        for (owner, cells) in self.retail_cell_arrays.iter_mut() {
            let before = cells.len();
            cells.retain(|c| c & 0xFFFF_0000 != lb_prefix);
            if cells.len() != before {
                cell_arrays_touched = true;
            }
            if cells.is_empty() {
                emptied.push(owner);
            }
        }
        if cell_arrays_touched || !emptied.is_empty() {
            self.advance_retail_cell_array_revision();
        }
        for owner in emptied {
            self.retail_cell_arrays.remove(owner);
            let ends_with_landblock = self.entity_reg.get(owner).is_none_or(|r| r.is_static);
            if !ends_with_landblock {
                continue;
            }
            self.retail_cell_array_routes.remove(&owner);
            self.entity_retail_part_arrays.remove(&owner);
            self.entity_shapes.remove(&owner);
            self.entity_reg.remove(owner);
            self.suspended_entities.remove(&owner);
            self.suspended_entity_cells.remove(&owner);
            self.withdrawn_prefixes_by_owner.remove(&owner);
        }
    }

    /// Takes one object out of one landblock: a static rooted there is
    /// removed; any other object loses its cells there (remembering the
    /// landblock for a later re-flood).
    pub fn retire_owner_from_landblock(&mut self, entity_id: u32, landblock_id: u32) {
        let prefix = landblock_id & 0xFFFF_0000;
        if self
            .entity_reg
            .get(entity_id)
            .is_some_and(|r| r.is_static && r.seed_cell_id & 0xFFFF_0000 == prefix)
        {
            self.deregister_core(entity_id);
            return;
        }
        let mut touched = false;
        if let Some(retail_cells) = self.retail_cell_arrays.get(entity_id).cloned() {
            let mut kept = retail_cells.clone();
            for index in (0..retail_cells.len()).rev() {
                let cell_id = retail_cells[index];
                if cell_id & 0xFFFF_0000 != prefix {
                    continue;
                }
                touched = true;
                kept.remove(index);
                self.advance_retail_cell_array_revision();
                if let Some(rows) = self.retail_part_entries_by_cell.get_mut(&cell_id) {
                    let before = rows.len();
                    rows.retain(|e| e.entity_id != entity_id);
                    let removed = before - rows.len();
                    let empty = rows.is_empty();
                    for _ in 0..removed {
                        self.mark_cell_render_changed(cell_id);
                    }
                    if empty {
                        self.remove_cell_draw_list(cell_id);
                    }
                }
            }
            if kept.is_empty() {
                self.retail_cell_arrays.remove(entity_id);
                self.advance_retail_cell_array_revision();
            } else {
                self.retail_cell_arrays.insert(entity_id, kept);
            }
        }
        let Some(cells) = self.entity_to_cells.get(entity_id).cloned() else {
            return;
        };
        let mut kept = cells.clone();
        for index in (0..cells.len()).rev() {
            let cell_id = cells[index];
            if cell_id & 0xFFFF_0000 != prefix {
                continue;
            }
            touched = true;
            kept.remove(index);
            if let Some(entries) = self.cells.get_mut(&cell_id) {
                entries.retain(|e| e.entity_id != entity_id);
                if entries.is_empty() {
                    self.cells.remove(&cell_id);
                }
            }
        }
        if !touched {
            return;
        }
        self.withdrawn_prefixes_by_owner
            .entry(entity_id)
            .or_default()
            .insert(prefix);
        if kept.is_empty() {
            self.entity_to_cells.remove(entity_id);
        } else {
            self.entity_to_cells.insert(entity_id, kept);
        }
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// The rows in a cell.
    #[must_use]
    pub fn objects_in_cell(&self, cell_id: u32) -> &[ShadowEntry] {
        self.cells.get(&cell_id).map_or(&[], Vec::as_slice)
    }

    /// The cells an object's rows are in.
    #[must_use]
    pub fn owner_cells(&self, entity_id: u32) -> &[u32] {
        self.entity_to_cells
            .get(entity_id)
            .map_or(&[], Vec::as_slice)
    }

    /// Objects with rows in some cell.
    #[must_use]
    pub fn total_registered(&self) -> usize {
        self.entity_to_cells.len()
    }

    /// Objects with a registration (including suspended ones).
    #[must_use]
    pub fn retained_registration_count(&self) -> usize {
        self.entity_reg.len()
    }

    /// Landblocks objects are waiting to be re-flooded into, counted per
    /// object.
    #[must_use]
    pub fn withdrawn_prefix_marker_count(&self) -> usize {
        self.withdrawn_prefixes_by_owner
            .values()
            .map(HashSet::len)
            .sum()
    }

    /// Suspended objects.
    #[must_use]
    pub fn suspended_registration_count(&self) -> usize {
        self.suspended_entities.len()
    }

    /// True when the object has rows in the landblock.
    #[must_use]
    pub fn has_owner_rows_in_landblock(&self, owner_id: u32, landblock_id: u32) -> bool {
        self.entity_to_cells.get(owner_id).is_some_and(|cells| {
            cells
                .iter()
                .any(|c| c & 0xFFFF_0000 == landblock_id & 0xFFFF_0000)
        })
    }

    /// True when the object is registered (maybe suspended).
    #[must_use]
    pub fn has_logical_owner(&self, entity_id: u32) -> bool {
        self.entity_reg.contains_key(entity_id)
    }

    /// The registration of an object.
    #[must_use]
    pub fn registration(&self, entity_id: u32) -> Option<&RegistrationRecord> {
        self.entity_reg.get(entity_id)
    }

    /// The physics state and static flag of a registered object.
    #[must_use]
    pub fn collision_owner(&self, entity_id: u32) -> Option<(u32, bool)> {
        self.entity_reg
            .get(entity_id)
            .map(|r| (r.state, r.is_static))
    }

    /// True when the object touches the landblock: rooted there, with rows
    /// there, or waiting to be re-flooded there.
    #[must_use]
    pub fn owner_touches_landblock(&self, entity_id: u32, landblock_id: u32) -> bool {
        let prefix = landblock_id & 0xFFFF_0000;
        let Some(record) = self.entity_reg.get(entity_id) else {
            return false;
        };
        if record.seed_cell_id & 0xFFFF_0000 == prefix {
            return true;
        }
        if self
            .entity_to_cells
            .get(entity_id)
            .is_some_and(|cells| cells.iter().any(|c| c & 0xFFFF_0000 == prefix))
        {
            return true;
        }
        self.withdrawn_prefixes_by_owner
            .get(&entity_id)
            .is_some_and(|w| w.contains(&prefix))
    }

    /// Removes everything.
    pub fn clear(&mut self) {
        let revision = self.retail_cell_array_revision + 1;
        let next = self.next_cell_render_revision;
        *self = Self::default();
        self.retail_cell_array_revision = revision;
        self.next_cell_render_revision = next;
    }

    /// Every registered object's rows, each object's taken from the first
    /// of its cells that holds any.
    #[must_use]
    pub fn capture_entries(&self) -> Vec<ShadowEntry> {
        let mut results = Vec::new();
        for (entity_id, cells) in self.entity_to_cells.iter() {
            self.add_first_cell_entries(entity_id, cells, &mut results, None);
        }
        results
    }

    /// One object's rows, as [`Self::capture_entries`] gives them.
    #[must_use]
    pub fn capture_entries_for(&self, entity_id: u32) -> Vec<ShadowEntry> {
        let mut results = Vec::new();
        if let Some(cells) = self.entity_to_cells.get(entity_id) {
            self.add_first_cell_entries(entity_id, cells, &mut results, None);
        }
        results
    }

    /// The rows that reach within `radius` of a ground point (horizontal
    /// distance to the part, less its radius).
    #[must_use]
    pub fn capture_entries_near(&self, centre: Vec2, radius: f32) -> Vec<ShadowEntry> {
        let mut results = Vec::new();
        for (entity_id, cells) in self.entity_to_cells.iter() {
            self.add_first_cell_entries(entity_id, cells, &mut results, Some((centre, radius)));
        }
        results
    }

    fn add_first_cell_entries(
        &self,
        entity_id: u32,
        cells: &[u32],
        results: &mut Vec<ShadowEntry>,
        near: Option<(Vec2, f32)>,
    ) {
        for cell_id in cells {
            let Some(list) = self.cells.get(cell_id) else {
                continue;
            };
            let mut any_found = false;
            for entry in list {
                if entry.entity_id != entity_id {
                    continue;
                }
                any_found = true;
                if let Some((centre, radius)) = near
                    && Vec2::new(entry.position.x, entry.position.y).distance(centre)
                        > radius + entry.radius
                {
                    continue;
                }
                results.push(*entry);
            }
            if any_found {
                return;
            }
        }
    }
}

fn derive_outdoor_seed(
    world_pos: Vec3,
    world_offset_x: f32,
    world_offset_y: f32,
    landblock_id: u32,
) -> u32 {
    if landblock_id == 0 {
        return 0;
    }
    let local_x = world_pos.x - world_offset_x;
    let local_y = world_pos.y - world_offset_y;
    let cx = (local_x / 24.0).clamp(0.0, 7.0) as u32;
    let cy = (local_y / 24.0).clamp(0.0, 7.0) as u32;
    (landblock_id & 0xFFFF_0000) | (cx * 8 + cy + 1)
}

fn part_entry(
    entity_id: u32,
    shape: &ShadowShape,
    entity_pos: Vec3,
    entity_rot: Quat,
    state: u32,
    flags: EntityCollisionFlags,
) -> ShadowEntry {
    ShadowEntry {
        entity_id,
        gfx_obj_id: shape.gfx_obj_id,
        position: entity_pos + entity_rot * shape.local_position,
        rotation: entity_rot * shape.local_rotation,
        radius: shape.radius,
        collision_type: shape.collision_type,
        cyl_height: shape.cyl_height,
        scale: shape.scale,
        state,
        flags,
        local_position: shape.local_position,
        local_rotation: shape.local_rotation,
    }
}

fn multi_part_record(
    seed: u32,
    pos: Vec3,
    rot: Quat,
    state: u32,
    flags: EntityCollisionFlags,
    is_static: bool,
) -> RegistrationRecord {
    RegistrationRecord {
        seed_cell_id: seed,
        entity_world_pos: pos,
        entity_world_rot: rot,
        state,
        flags,
        is_static,
        is_multi_part: true,
        gfx_obj_id: 0,
        radius: 0.0,
        collision_type: ShadowCollisionType::Bsp,
        cyl_height: 0.0,
        scale: 1.0,
    }
}

/// The flood spheres of an object: its cylinders when it has any (at most
/// ten), else its spheres; each at its part's bounds centre.
fn flood_spheres(entity_pos: Vec3, entity_rot: Quat, shapes: &[ShadowShape]) -> Vec<Sphere> {
    let any_cyl = shapes
        .iter()
        .any(|s| s.collision_type == ShadowCollisionType::Cylinder);
    let only = if any_cyl {
        ShadowCollisionType::Cylinder
    } else {
        ShadowCollisionType::Sphere
    };
    let cap = if only == ShadowCollisionType::Cylinder {
        RETAIL_SPHERE_CAP
    } else {
        usize::MAX
    };
    let mut spheres = Vec::new();
    for s in shapes {
        if s.collision_type != only {
            continue;
        }
        if spheres.len() >= cap {
            break;
        }
        let part_pos = entity_pos + entity_rot * s.local_position;
        let part_rot = entity_rot * s.local_rotation;
        spheres.push(Sphere::new(part_pos + part_rot * s.bounds_center, s.radius));
    }
    spheres
}

/// The boxes and spheres of an object's collision-tree parts (paired by
/// index).
fn bsp_parts(
    entity_pos: Vec3,
    entity_rot: Quat,
    shapes: &[ShadowShape],
) -> (Vec<ShadowPartBox>, Vec<Sphere>) {
    let mut boxes = Vec::new();
    let mut spheres = Vec::new();
    for s in shapes {
        if s.collision_type != ShadowCollisionType::Bsp {
            continue;
        }
        boxes.push(ShadowPartBox::from_shape(s, entity_pos, entity_rot));
        let part_pos = entity_pos + entity_rot * s.local_position;
        let part_rot = entity_rot * s.local_rotation;
        spheres.push(Sphere::new(part_pos + part_rot * s.bounds_center, s.radius));
    }
    (boxes, spheres)
}
