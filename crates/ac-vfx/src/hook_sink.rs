//! Particle hooks turned into emitters. A create hook starts an emitter at
//! the owner's root or one of its parts (plus the hook's offset); its
//! logical id names it so a later destroy or stop hook can find it. A
//! blocking create does nothing while an emitter with the same logical id
//! is still alive; a normal create replaces it.

use std::collections::{BTreeSet, HashMap, HashSet};

use ac_dat::objects::{AnimationHook, HookData};
use glam::{Mat3, Mat4, Quat, Vec3};

use crate::desc::{EmitterDescFailure, EmitterDescFailureKind, vec3};
use crate::particles::{
    EmitterHandle, ParticleRenderPass, ParticleSystem, ParticleVisibilityPolicy, SpawnOptions,
};
use crate::poses::EffectPoseSource;

#[derive(Debug, Clone, Copy)]
struct EmitterBinding {
    owner: u32,
    part_index: i32,
    hook_offset_origin: Vec3,
    logical_id: u32,
}

/// Routes particle hooks to the [`ParticleSystem`] and keeps each owner's
/// emitters attached to its current pose.
#[derive(Debug, Default)]
pub struct ParticleHookSink {
    handles_by_key: HashMap<(u32, u32), EmitterHandle>,
    handles_by_entity: HashMap<u32, BTreeSet<EmitterHandle>>,
    bindings: HashMap<EmitterHandle, EmitterBinding>,
    render_pass_by_entity: HashMap<u32, ParticleRenderPass>,
    examination_owners: HashSet<u32>,
    hidden_presentation_owners: HashSet<u32>,
    reported_failures: HashSet<u32>,
    dirty_set: HashSet<u32>,
    dirty_order: Vec<u32>,
    retired: Vec<EmitterHandle>,
    diagnostics: Vec<String>,
    last_refresh_owner_visit_count: usize,
    last_refresh_binding_visit_count: usize,
}

impl ParticleHookSink {
    /// An empty sink.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Emitters bound to an owner.
    #[must_use]
    pub fn active_binding_count(&self) -> usize {
        self.bindings.len()
    }

    /// Logical (owner, emitter id) names held.
    #[must_use]
    pub fn logical_emitter_count(&self) -> usize {
        self.handles_by_key.len()
    }

    /// Owners with emitters.
    #[must_use]
    pub fn tracked_owner_count(&self) -> usize {
        self.handles_by_entity.len()
    }

    /// Owners with a render pass of their own.
    #[must_use]
    pub fn render_pass_owner_count(&self) -> usize {
        self.render_pass_by_entity.len()
    }

    /// Owners marked as examination objects.
    #[must_use]
    pub fn examination_owner_count(&self) -> usize {
        self.examination_owners.len()
    }

    /// Owners whose presentation is hidden.
    #[must_use]
    pub fn hidden_presentation_owner_count(&self) -> usize {
        self.hidden_presentation_owners.len()
    }

    /// Owners the last refresh visited.
    #[must_use]
    pub fn last_refresh_owner_visit_count(&self) -> usize {
        self.last_refresh_owner_visit_count
    }

    /// Bindings the last refresh visited.
    #[must_use]
    pub fn last_refresh_binding_visit_count(&self) -> usize {
        self.last_refresh_binding_visit_count
    }

    /// Messages about emitters that could not be created, since the last
    /// call. A missing emitter id is reported once.
    pub fn take_diagnostics(&mut self) -> Vec<String> {
        std::mem::take(&mut self.diagnostics)
    }

    /// Emitters that died (for the renderer to release what it holds for
    /// them), since the last call.
    pub fn take_retired_emitters(&mut self, system: &mut ParticleSystem) -> Vec<EmitterHandle> {
        self.sync_deaths(system);
        std::mem::take(&mut self.retired)
    }

    /// Handle a hook `owner` fired. Only the particle hooks do anything.
    pub fn on_hook(
        &mut self,
        system: &mut ParticleSystem,
        poses: &dyn EffectPoseSource,
        owner: u32,
        hook: &AnimationHook,
    ) {
        self.sync_deaths(system);
        match hook.data {
            HookData::CreateParticle {
                emitter_info_id,
                part_index,
                offset,
                emitter_id,
            } => self.spawn_from_hook(
                system,
                poses,
                owner,
                emitter_info_id,
                vec3(offset.origin),
                part_index as i32,
                emitter_id,
                false,
            ),
            HookData::CreateBlockingParticle {
                emitter_info_id,
                part_index,
                offset,
                emitter_id,
            } => self.spawn_from_hook(
                system,
                poses,
                owner,
                emitter_info_id,
                vec3(offset.origin),
                part_index as i32,
                emitter_id,
                true,
            ),
            HookData::DestroyParticle { emitter_id } => {
                self.destroy_logical(system, owner, emitter_id, false);
            }
            HookData::StopParticle { emitter_id } => {
                self.destroy_logical(system, owner, emitter_id, true);
            }
            _ => {}
        }
        self.sync_deaths(system);
    }

    /// Draw `owner`'s emitters (present and future) in `pass`.
    pub fn set_entity_render_pass(
        &mut self,
        system: &mut ParticleSystem,
        owner: u32,
        pass: ParticleRenderPass,
    ) {
        self.render_pass_by_entity.insert(owner, pass);
        self.refresh_owner_visibility_policy(system, owner);
    }

    /// Back to the scene pass.
    pub fn clear_entity_render_pass(&mut self, system: &mut ParticleSystem, owner: u32) {
        self.render_pass_by_entity.remove(&owner);
        self.refresh_owner_visibility_policy(system, owner);
    }

    /// Mark `owner` as an examination preview (its emitters skip the world
    /// view test).
    pub fn set_entity_examination_object(
        &mut self,
        system: &mut ParticleSystem,
        owner: u32,
        is_examination_object: bool,
    ) {
        if is_examination_object {
            self.examination_owners.insert(owner);
        } else {
            self.examination_owners.remove(&owner);
        }
        self.refresh_owner_visibility_policy(system, owner);
    }

    /// Hide or show `owner`'s emitters without ending them. Hiding pauses
    /// them at once; showing takes effect at the next refresh, from the
    /// owner's current pose, with no catch-up emission.
    pub fn set_entity_presentation_visible(
        &mut self,
        system: &mut ParticleSystem,
        owner: u32,
        visible: bool,
    ) {
        if visible {
            self.hidden_presentation_owners.remove(&owner);
            self.mark_owner_dirty(owner);
            return;
        }
        self.hidden_presentation_owners.insert(owner);
        if let Some(handles) = self.handles_by_entity.get(&owner) {
            for &handle in handles {
                system.set_emitter_presentation_visible(handle, false);
                system.set_emitter_simulation_enabled(handle, false);
            }
        }
    }

    /// `owner`'s pose changed: its emitters follow at the next refresh.
    pub fn on_pose_changed(&mut self, owner: u32) {
        self.mark_owner_dirty(owner);
    }

    /// Move the emitters of every owner whose pose changed to the owner's
    /// current pose. An owner without a pose hides (and pauses) its
    /// emitters until it has one again.
    pub fn refresh_attached_emitters(
        &mut self,
        system: &mut ParticleSystem,
        poses: &dyn EffectPoseSource,
    ) {
        self.sync_deaths(system);
        self.last_refresh_owner_visit_count = 0;
        self.last_refresh_binding_visit_count = 0;
        let owners = std::mem::take(&mut self.dirty_order);
        self.dirty_set.clear();
        for owner in owners {
            let Some(handles) = self.handles_by_entity.get(&owner) else {
                continue;
            };
            self.last_refresh_owner_visit_count += 1;
            let visible = !self.hidden_presentation_owners.contains(&owner);
            for &handle in handles {
                let Some(binding) = self.bindings.get(&handle) else {
                    continue;
                };
                self.last_refresh_binding_visit_count += 1;
                match resolve_anchor(poses, owner, binding.part_index, binding.hook_offset_origin) {
                    Some((anchor, rotation, owner_position)) => {
                        system.update_emitter_anchor(handle, anchor, Some(rotation));
                        system.update_emitter_owner_position(handle, owner_position);
                        system.update_emitter_owner_cell(handle, poses.cell_id(owner).unwrap_or(0));
                        system.set_emitter_presentation_visible(handle, visible);
                        system.set_emitter_simulation_enabled(handle, visible);
                    }
                    None => {
                        system.set_emitter_presentation_visible(handle, false);
                        system.set_emitter_simulation_enabled(handle, false);
                    }
                }
            }
        }
        self.sync_deaths(system);
    }

    /// Stop every emitter of `owner` (fading out, or at once) and forget
    /// the owner.
    pub fn stop_all_for_entity(&mut self, system: &mut ParticleSystem, owner: u32, fade_out: bool) {
        if let Some(handles) = self.handles_by_entity.remove(&owner) {
            for handle in handles {
                if let Some(binding) = self.bindings.remove(&handle)
                    && binding.logical_id != 0
                {
                    self.handles_by_key.remove(&(owner, binding.logical_id));
                }
                if fade_out {
                    // A fading emitter needs its clock to retire.
                    system.set_emitter_simulation_enabled(handle, true);
                }
                system.stop_emitter(handle, fade_out);
            }
        }
        self.clear_entity_render_pass(system, owner);
        self.examination_owners.remove(&owner);
        self.hidden_presentation_owners.remove(&owner);
        self.sync_deaths(system);
    }

    fn destroy_logical(
        &mut self,
        system: &mut ParticleSystem,
        owner: u32,
        logical_id: u32,
        fade_out: bool,
    ) {
        if logical_id == 0 {
            return;
        }
        let Some(&handle) = self.handles_by_key.get(&(owner, logical_id)) else {
            return;
        };
        // A stopped emitter stays in the table (and keeps blocking) until
        // its last particle dies; a destroyed one leaves at once.
        if fade_out {
            system.set_emitter_simulation_enabled(handle, true);
        } else {
            self.handles_by_key.remove(&(owner, logical_id));
        }
        system.stop_emitter(handle, fade_out);
    }

    #[allow(clippy::too_many_arguments)]
    fn spawn_from_hook(
        &mut self,
        system: &mut ParticleSystem,
        poses: &dyn EffectPoseSource,
        owner: u32,
        emitter_info_id: u32,
        offset_origin: Vec3,
        part_index: i32,
        logical_id: u32,
        is_blocking: bool,
    ) {
        if logical_id != 0
            && let Some(&existing) = self.handles_by_key.get(&(owner, logical_id))
        {
            if is_blocking && system.is_emitter_alive(existing) {
                return;
            }
            self.handles_by_key.remove(&(owner, logical_id));
            system.stop_emitter(existing, false);
            self.sync_deaths(system);
        }

        let Some((anchor, rotation, owner_position)) =
            resolve_anchor(poses, owner, part_index, offset_origin)
        else {
            self.diagnostics.push(format!(
                "No live effect pose for owner 0x{owner:08X}, part {part_index}; \
                 emitter 0x{emitter_info_id:08X} was not created."
            ));
            return;
        };

        let render_pass = self
            .render_pass_by_entity
            .get(&owner)
            .copied()
            .unwrap_or_default();
        let options = SpawnOptions {
            rotation,
            attached_object_id: owner,
            attached_part_index: part_index,
            render_pass,
            visibility_policy: self.resolve_visibility_policy(owner),
        };
        let handle = match system.spawn_emitter_by_id(emitter_info_id, anchor, options) {
            Ok(handle) => handle,
            Err(failure) => {
                if self.reported_failures.insert(emitter_info_id) {
                    self.diagnostics
                        .push(resolution_diagnostic(emitter_info_id, owner, failure));
                }
                return;
            }
        };
        system.update_emitter_owner_position(handle, owner_position);
        system.update_emitter_owner_cell(handle, poses.cell_id(owner).unwrap_or(0));
        if self.hidden_presentation_owners.contains(&owner) {
            system.set_emitter_presentation_visible(handle, false);
            system.set_emitter_simulation_enabled(handle, false);
        }

        self.bindings.insert(
            handle,
            EmitterBinding {
                owner,
                part_index,
                hook_offset_origin: offset_origin,
                logical_id,
            },
        );
        self.handles_by_entity
            .entry(owner)
            .or_default()
            .insert(handle);
        if logical_id != 0 {
            self.handles_by_key.insert((owner, logical_id), handle);
        }
    }

    fn resolve_visibility_policy(&self, owner: u32) -> ParticleVisibilityPolicy {
        if self.examination_owners.contains(&owner) {
            return ParticleVisibilityPolicy::Examination;
        }
        match self.render_pass_by_entity.get(&owner) {
            Some(pass) if *pass != ParticleRenderPass::Scene => ParticleVisibilityPolicy::PassOwned,
            _ => ParticleVisibilityPolicy::World,
        }
    }

    fn refresh_owner_visibility_policy(&mut self, system: &mut ParticleSystem, owner: u32) {
        let Some(handles) = self.handles_by_entity.get(&owner) else {
            return;
        };
        let policy = self.resolve_visibility_policy(owner);
        for &handle in handles {
            system.set_emitter_visibility_policy(handle, policy);
        }
    }

    fn mark_owner_dirty(&mut self, owner: u32) {
        if self.handles_by_entity.contains_key(&owner) && self.dirty_set.insert(owner) {
            self.dirty_order.push(owner);
        }
    }

    /// Forget the bindings of emitters the system retired.
    fn sync_deaths(&mut self, system: &mut ParticleSystem) {
        for handle in system.take_died_emitters() {
            self.retired.push(handle);
            let Some(binding) = self.bindings.remove(&handle) else {
                continue;
            };
            let key = (binding.owner, binding.logical_id);
            if binding.logical_id != 0 && self.handles_by_key.get(&key) == Some(&handle) {
                self.handles_by_key.remove(&key);
            }
            if let Some(handles) = self.handles_by_entity.get_mut(&binding.owner) {
                handles.remove(&handle);
                if handles.is_empty() {
                    self.handles_by_entity.remove(&binding.owner);
                }
            }
        }
    }
}

fn resolution_diagnostic(emitter_info_id: u32, owner: u32, failure: EmitterDescFailure) -> String {
    match failure.kind {
        EmitterDescFailureKind::InvalidHardwareGfxObjId => format!(
            "ParticleEmitterInfo 0x{emitter_info_id:08X} has no hardware GfxObj; the emitter \
             was skipped (first owner 0x{owner:08X})."
        ),
        EmitterDescFailureKind::MissingHardwareGfxObj => format!(
            "ParticleEmitterInfo 0x{emitter_info_id:08X} references missing hardware GfxObj \
             0x{:08X}; the emitter was skipped (first owner 0x{owner:08X}).",
            failure.related_dat_id
        ),
        EmitterDescFailureKind::MissingEmitterInfo => format!(
            "ParticleEmitterInfo 0x{emitter_info_id:08X} for owner 0x{owner:08X} was not \
             found; no fallback was created."
        ),
    }
}

/// The emitter's anchor (the hook offset through the owner's root, or root
/// times part), its orientation, and the owner's root position.
fn resolve_anchor(
    poses: &dyn EffectPoseSource,
    owner: u32,
    part_index: i32,
    offset_origin: Vec3,
) -> Option<(Vec3, Quat, Vec3)> {
    let root = poses.root_pose(owner)?;
    let owner_position = root.w_axis.truncate();
    let frame = if part_index == -1 {
        root
    } else {
        root * poses.part_pose(owner, part_index)?
    };
    let anchor = frame.transform_point3(offset_origin);
    let rotation = decompose_rotation(&frame)?;
    Some((anchor, rotation.normalize(), owner_position))
}

/// The rotation of an affine matrix: its axes are orthonormalized (a
/// collapsed axis is rebuilt from the others), a mirrored basis is turned
/// back by flipping its longest axis, and a matrix whose basis is not a
/// rotation after that (a skew) has none.
pub(crate) fn decompose_rotation(m: &Mat4) -> Option<Quat> {
    const EPSILON: f32 = 0.0001;
    let mut basis = [
        m.x_axis.truncate(),
        m.y_axis.truncate(),
        m.z_axis.truncate(),
    ];
    let scales = [basis[0].length(), basis[1].length(), basis[2].length()];
    // Axes from longest (a) to shortest (c).
    let (a, b, c) = if scales[0] < scales[1] {
        if scales[1] < scales[2] {
            (2, 1, 0)
        } else if scales[0] < scales[2] {
            (1, 2, 0)
        } else {
            (1, 0, 2)
        }
    } else if scales[0] < scales[2] {
        (2, 0, 1)
    } else if scales[1] < scales[2] {
        (0, 2, 1)
    } else {
        (0, 1, 2)
    };
    let canonical = [Vec3::X, Vec3::Y, Vec3::Z];
    if scales[a] < EPSILON {
        basis[a] = canonical[a];
    }
    basis[a] = basis[a] / basis[a].length();
    if scales[b] < EPSILON {
        let abs = basis[a].abs();
        let cc = if abs.x < abs.y {
            if abs.y < abs.z || abs.x < abs.z { 0 } else { 2 }
        } else if abs.x < abs.z || abs.y < abs.z {
            1
        } else {
            2
        };
        basis[b] = basis[a].cross(canonical[cc]);
    }
    basis[b] = basis[b] / basis[b].length();
    if scales[c] < EPSILON {
        basis[c] = basis[a].cross(basis[b]);
    }
    basis[c] = basis[c] / basis[c].length();

    let mut det = Mat3::from_cols(basis[0], basis[1], basis[2]).determinant();
    if det < 0.0 {
        basis[a] = -basis[a];
        det = -det;
    }
    let deviation = (det - 1.0) * (det - 1.0);
    if EPSILON < deviation {
        return None;
    }
    Some(Quat::from_mat3(&Mat3::from_cols(
        basis[0], basis[1], basis[2],
    )))
}

#[cfg(test)]
mod tests;
