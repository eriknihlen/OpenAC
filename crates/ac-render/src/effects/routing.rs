//! Where a blended draw goes: the game's two alpha lists (a clip-map list
//! and an alpha list, drained at a building, at a full sort cell, at the
//! landscape's end and at the frame's end; `draws::StreamBuilder`) or
//! straight onto the screen, by the draw's surface kind and its material's
//! opacity. Particles use it to choose how each particle is
//! drawn: a quad always waits for the alpha list; a particle model's
//! sub-mesh waits unless it is opaque and fully opaque in colour, which is
//! drawn at once with depth written.
//!
//! The lists hold quads, models and sub-meshes alike; a draw "at once" is
//! drawn opaque, with depth written, in its place in its list.

use crate::draws::ParticleBlend;
use crate::meshing::Translucency;

/// What a routed sub-mesh does with the two lists.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AlphaAction {
    /// Drawn at once; never touches a list.
    Immediate,
    /// Appended to a list; drawn when the list drains.
    Append,
    /// Appended to the clip-map list and drawn at once as well.
    AppendClipAndImmediate,
}

/// Which list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AlphaList {
    /// The alpha list.
    #[default]
    Alpha,
    /// The clip-map list.
    Clip,
}

/// A routing decision.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AlphaDecision {
    /// What happens.
    pub action: AlphaAction,
    /// The list (when appended).
    pub list: AlphaList,
    /// The draw overrides the clip map (the clip-and-immediate row).
    pub override_clipmap: bool,
}

impl AlphaDecision {
    const IMMEDIATE: Self = Self {
        action: AlphaAction::Immediate,
        list: AlphaList::Alpha,
        override_clipmap: false,
    };
}

/// Subset mask bit: positive stippling.
pub const MASK_POSITIVE_STIPPLE: u8 = 0x01;
/// Subset mask bit: the alpha family (blended, additive, inverse alpha).
pub const MASK_ALPHA_FAMILY: u8 = 0x02;
/// Subset mask bit: translucent.
pub const MASK_TRANSLUCENT: u8 = 0x04;
/// Subset mask bit: clip map.
pub const MASK_CLIP_MAP: u8 = 0x08;
/// The delay mask the world draws with: alpha family, translucent and clip
/// map wait for the lists.
pub const DEFAULT_DELAY_MASK: u8 = 0x0E;

/// A subset's mask: the alpha family wins over clip map, clip map over
/// translucent; positive stippling is or-ed in.
#[must_use]
pub fn construct_subset_mask(
    has_alpha_family_bit: bool,
    has_clip_map_bit: bool,
    has_translucent_bit: bool,
    has_positive_stippling: bool,
) -> u8 {
    let mut mask = if has_alpha_family_bit {
        MASK_ALPHA_FAMILY
    } else if has_clip_map_bit {
        MASK_CLIP_MAP
    } else if has_translucent_bit {
        MASK_TRANSLUCENT
    } else {
        0
    };
    if has_positive_stippling {
        mask |= MASK_POSITIVE_STIPPLE;
    }
    mask
}

/// The mask of a surface kind.
#[must_use]
pub fn mask_from_translucency(kind: Translucency) -> u8 {
    match kind {
        Translucency::ClipMap => MASK_CLIP_MAP,
        Translucency::Opaque => 0,
        Translucency::AlphaBlend | Translucency::Additive | Translucency::InvAlpha => {
            MASK_ALPHA_FAMILY
        }
    }
}

/// Route a subset: drawn at once while the sky draws, without a delay mask
/// or with a detail surface; a multi-pass clip map goes to the clip list
/// and is drawn at once too; a masked subset waits (clip maps in the clip
/// list); a translucent-delay material with alpha waits; the rest is drawn
/// at once.
#[must_use]
pub fn route(
    currently_drawing_sky: bool,
    delay_mask: u8,
    detail_surface_active: bool,
    multi_pass_alpha: bool,
    subset_mask: u8,
    material_has_alpha: bool,
) -> AlphaDecision {
    if currently_drawing_sky || delay_mask == 0 || detail_surface_active {
        return AlphaDecision::IMMEDIATE;
    }
    let clip_map = subset_mask & MASK_CLIP_MAP != 0;
    if multi_pass_alpha && clip_map {
        return AlphaDecision {
            action: AlphaAction::AppendClipAndImmediate,
            list: AlphaList::Clip,
            override_clipmap: true,
        };
    }
    if delay_mask & subset_mask != 0 {
        return AlphaDecision {
            action: AlphaAction::Append,
            list: if clip_map {
                AlphaList::Clip
            } else {
                AlphaList::Alpha
            },
            override_clipmap: false,
        };
    }
    if delay_mask & MASK_TRANSLUCENT != 0 && material_has_alpha {
        return AlphaDecision {
            action: AlphaAction::Append,
            list: AlphaList::Alpha,
            override_clipmap: false,
        };
    }
    AlphaDecision::IMMEDIATE
}

/// How a particle draws.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParticleSubmission {
    /// A quad.
    Billboard,
    /// A sub-mesh of a particle model whose surface is of `Translucency`,
    /// coloured `color_argb`.
    Mesh(Translucency, u32),
}

/// Route a particle: a quad is of the alpha family; a model's sub-mesh by
/// its surface, its colour's alpha below 255 counting as material alpha.
#[must_use]
pub fn route_particle(submission: ParticleSubmission) -> AlphaDecision {
    let (mask, has_alpha) = match submission {
        ParticleSubmission::Billboard => (MASK_ALPHA_FAMILY, false),
        ParticleSubmission::Mesh(kind, color) => {
            (mask_from_translucency(kind), (color >> 24) & 0xFF != 0xFF)
        }
    };
    route(false, DEFAULT_DELAY_MASK, false, false, mask, has_alpha)
}

/// The blend a particle model's sub-mesh draws with: at once, opaque with
/// depth written; from a list, additive and inverse alpha as their surface
/// says and everything else straight alpha, depth not written.
#[must_use]
pub fn mesh_particle_blend(kind: Translucency, color_argb: u32) -> ParticleBlend {
    let decision = route_particle(ParticleSubmission::Mesh(kind, color_argb));
    resolve_mesh_particle_blend(kind, decision.action == AlphaAction::Immediate)
}

/// The pipeline state of a particle model's sub-mesh (`opaque_depth_state`:
/// it is drawn at once).
#[must_use]
pub fn resolve_mesh_particle_blend(kind: Translucency, opaque_depth_state: bool) -> ParticleBlend {
    if opaque_depth_state {
        return ParticleBlend::Opaque;
    }
    match kind {
        Translucency::Additive => ParticleBlend::Additive,
        Translucency::InvAlpha => ParticleBlend::InvAlpha,
        _ => ParticleBlend::Alpha,
    }
}

#[cfg(test)]
mod tests {
    //! Cases from OpenAC `tests/AcDream.App.Tests/Rendering/RetailAlphaMeshRouterTests.cs`
    //! and `ParticleRendererRouteTests.cs` (the routing and pipeline-state
    //! cases; the queue cases are in `draws::tests`, the GPU-recording
    //! ones have no counterpart).
    use super::*;

    #[test]
    fn route_matches_the_traced_boundary() {
        use AlphaAction::{Append, AppendClipAndImmediate, Immediate};
        use AlphaList::{Alpha, Clip};
        // sky, detail, multipass, mask, has alpha -> action, list, override.
        let cases = [
            (true, false, false, 0x02, false, Immediate, Alpha, false),
            (false, true, true, 0x08, false, Immediate, Alpha, false),
            (
                false,
                false,
                true,
                0x08,
                false,
                AppendClipAndImmediate,
                Clip,
                true,
            ),
            (
                false,
                false,
                true,
                0x09,
                false,
                AppendClipAndImmediate,
                Clip,
                true,
            ),
            (false, false, true, 0x02, false, Append, Alpha, false),
            (false, false, false, 0x02, false, Append, Alpha, false),
            (false, false, false, 0x08, false, Append, Clip, false),
            (false, false, false, 0x04, false, Append, Alpha, false),
            (false, false, false, 0x01, true, Append, Alpha, false),
            (false, false, false, 0x00, true, Append, Alpha, false),
            (false, false, false, 0x00, false, Immediate, Alpha, false),
        ];
        for (sky, detail, multipass, mask, alpha, action, list, override_clip) in cases {
            let d = route(sky, DEFAULT_DELAY_MASK, detail, multipass, mask, alpha);
            assert_eq!(
                d.action, action,
                "{sky} {detail} {multipass} {mask:#x} {alpha}"
            );
            if action != Immediate {
                assert_eq!(d.list, list);
                assert_eq!(d.override_clipmap, override_clip);
            }
        }
    }

    /// The branch table restated row by row.
    fn restated(
        sky: bool,
        delay: u8,
        detail: bool,
        multi: bool,
        mask: u8,
        alpha: bool,
    ) -> AlphaDecision {
        if sky || delay == 0 || detail {
            return AlphaDecision::IMMEDIATE;
        }
        let clip = mask & 0b1000 == 0b1000;
        if multi && clip {
            return AlphaDecision {
                action: AlphaAction::AppendClipAndImmediate,
                list: AlphaList::Clip,
                override_clipmap: true,
            };
        }
        if delay & mask != 0 {
            return AlphaDecision {
                action: AlphaAction::Append,
                list: if clip {
                    AlphaList::Clip
                } else {
                    AlphaList::Alpha
                },
                override_clipmap: false,
            };
        }
        if delay & 0b0100 == 0b0100 && alpha {
            return AlphaDecision {
                action: AlphaAction::Append,
                list: AlphaList::Alpha,
                override_clipmap: false,
            };
        }
        AlphaDecision::IMMEDIATE
    }

    #[test]
    fn route_matches_the_restated_branch_table_across_every_cell() {
        for mask in 0u8..=9 {
            for bits in 0..16u8 {
                let (sky, detail, multi, alpha) =
                    (bits & 1 != 0, bits & 2 != 0, bits & 4 != 0, bits & 8 != 0);
                let actual = route(sky, DEFAULT_DELAY_MASK, detail, multi, mask, alpha);
                let expected = restated(sky, DEFAULT_DELAY_MASK, detail, multi, mask, alpha);
                assert_eq!(actual.action, expected.action);
                if actual.action != AlphaAction::Immediate {
                    assert_eq!(actual.list, expected.list);
                    assert_eq!(actual.override_clipmap, expected.override_clipmap);
                }
            }
        }
    }

    #[test]
    fn mask_from_translucency_maps_every_kind() {
        assert_eq!(
            mask_from_translucency(Translucency::AlphaBlend),
            MASK_ALPHA_FAMILY
        );
        assert_eq!(
            mask_from_translucency(Translucency::Additive),
            MASK_ALPHA_FAMILY
        );
        assert_eq!(
            mask_from_translucency(Translucency::InvAlpha),
            MASK_ALPHA_FAMILY
        );
        assert_eq!(mask_from_translucency(Translucency::ClipMap), MASK_CLIP_MAP);
        assert_eq!(mask_from_translucency(Translucency::Opaque), 0);
    }

    #[test]
    fn subset_masks_prefer_alpha_then_clip_map_and_or_in_stippling() {
        assert_eq!(
            construct_subset_mask(true, true, true, false),
            MASK_ALPHA_FAMILY
        );
        assert_eq!(
            construct_subset_mask(false, true, true, false),
            MASK_CLIP_MAP
        );
        assert_eq!(
            construct_subset_mask(false, false, true, true),
            MASK_TRANSLUCENT | MASK_POSITIVE_STIPPLE
        );
    }

    #[test]
    fn an_opaque_mesh_particle_with_colour_alpha_waits_for_the_alpha_list() {
        let d = route_particle(ParticleSubmission::Mesh(Translucency::Opaque, 0x80FF_FFFF));
        assert_eq!(d.action, AlphaAction::Append);
        assert_eq!(d.list, AlphaList::Alpha);
        assert_eq!(
            mesh_particle_blend(Translucency::Opaque, 0x80FF_FFFF),
            ParticleBlend::Alpha
        );
    }

    #[test]
    fn a_fully_opaque_mesh_particle_draws_at_once_with_depth_written() {
        let d = route_particle(ParticleSubmission::Mesh(Translucency::Opaque, 0xFFFF_FFFF));
        assert_eq!(d.action, AlphaAction::Immediate);
        assert_eq!(
            resolve_mesh_particle_blend(Translucency::Opaque, true),
            ParticleBlend::Opaque
        );
        assert_eq!(
            mesh_particle_blend(Translucency::Opaque, 0xFFFF_FFFF),
            ParticleBlend::Opaque
        );
    }

    #[test]
    fn a_billboard_always_waits_for_the_alpha_list() {
        let d = route_particle(ParticleSubmission::Billboard);
        assert_eq!(d.action, AlphaAction::Append);
        assert_eq!(d.list, AlphaList::Alpha);
    }

    #[test]
    fn listed_mesh_particles_blend_by_their_surface() {
        assert_eq!(
            mesh_particle_blend(Translucency::Additive, 0xFFFF_FFFF),
            ParticleBlend::Additive
        );
        assert_eq!(
            mesh_particle_blend(Translucency::InvAlpha, 0xFFFF_FFFF),
            ParticleBlend::InvAlpha
        );
        assert_eq!(
            mesh_particle_blend(Translucency::AlphaBlend, 0xFFFF_FFFF),
            ParticleBlend::Alpha
        );
        // A clip map goes to the clip list and draws straight alpha.
        let d = route_particle(ParticleSubmission::Mesh(Translucency::ClipMap, 0xFFFF_FFFF));
        assert_eq!((d.action, d.list), (AlphaAction::Append, AlphaList::Clip));
        assert_eq!(
            mesh_particle_blend(Translucency::ClipMap, 0xFFFF_FFFF),
            ParticleBlend::Alpha
        );
    }
}
