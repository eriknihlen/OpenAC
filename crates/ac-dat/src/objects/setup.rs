use ac_bits::Reader;

use super::{AnimationFrame, DatObject, check_unique};
use crate::error::DecodeError;
use crate::header::DatFileType;
use crate::types::{CylSphere, LightInfo, LocationType, Sphere, Vector3};

/// Setup flag bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SetupFlags(pub u32);

impl SetupFlags {
    /// A parent index per part follows the part list.
    pub const HAS_PARENT: u32 = 0x1;
    /// A default scale per part follows.
    pub const HAS_DEFAULT_SCALE: u32 = 0x2;
    /// The object may turn freely.
    pub const ALLOW_FREE_HEADING: u32 = 0x4;
    /// The object collides using its parts' physics BSP.
    pub const HAS_PHYSICS_BSP: u32 = 0x8;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// A multi-part object: the models it is built from, how they hang
/// together, its collision shapes and default tables. File id `0x02......`.
#[derive(Debug, Clone, PartialEq)]
pub struct Setup {
    /// File id.
    pub id: u32,
    /// Flags.
    pub flags: SetupFlags,
    /// Model id per part.
    pub parts: Vec<u32>,
    /// Parent part per part (`0xFFFFFFFF` for none); empty without
    /// [`SetupFlags::HAS_PARENT`].
    pub parent_index: Vec<u32>,
    /// Scale per part; empty without [`SetupFlags::HAS_DEFAULT_SCALE`].
    pub default_scale: Vec<Vector3>,
    /// Where held items attach, keyed by parent location, in file order.
    pub holding_locations: Vec<(i32, LocationType)>,
    /// Where this object attaches to others, keyed by parent location.
    pub connection_points: Vec<(i32, LocationType)>,
    /// Resting pose per placement, keyed by placement, in file order.
    pub placement_frames: Vec<(u32, AnimationFrame)>,
    /// Collision cylinders.
    pub cyl_spheres: Vec<CylSphere>,
    /// Collision spheres.
    pub spheres: Vec<Sphere>,
    /// Height, metres.
    pub height: f32,
    /// Radius, metres.
    pub radius: f32,
    /// Highest step the object can climb, metres.
    pub step_up_height: f32,
    /// Deepest step the object can descend, metres.
    pub step_down_height: f32,
    /// Sphere used for draw sorting.
    pub sorting_sphere: Sphere,
    /// Sphere used for mouse selection.
    pub selection_sphere: Sphere,
    /// Lights, keyed by index, in file order.
    pub lights: Vec<(i32, LightInfo)>,
    /// Default animation id.
    pub default_animation: u32,
    /// Default physics script id.
    pub default_script: u32,
    /// Default motion table id.
    pub default_motion_table: u32,
    /// Default sound table id.
    pub default_sound_table: u32,
    /// Default physics script table id.
    pub default_script_table: u32,
}

impl DatObject for Setup {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self, DecodeError> {
        let id = r.u32()?;
        let flags = SetupFlags(r.u32()?);
        let num_parts = r.u32()?;
        let mut parts = Vec::new();
        for _ in 0..num_parts {
            parts.push(r.u32()?);
        }
        let mut parent_index = Vec::new();
        if flags.contains(SetupFlags::HAS_PARENT) {
            for _ in 0..num_parts {
                parent_index.push(r.u32()?);
            }
        }
        let mut default_scale = Vec::new();
        if flags.contains(SetupFlags::HAS_DEFAULT_SCALE) {
            for _ in 0..num_parts {
                default_scale.push(Vector3::unpack(r)?);
            }
        }
        let holding_locations = read_locations(r, "holding locations")?;
        let connection_points = read_locations(r, "connection points")?;

        let num_placements = r.i32()?;
        let mut placement_frames = Vec::new();
        for _ in 0..num_placements {
            let key = r.u32()?;
            let frame = AnimationFrame::unpack(r, num_parts)?;
            check_unique("placement frames", &placement_frames, key)?;
            placement_frames.push((key, frame));
        }
        let num_cyl_spheres = r.u32()?;
        let mut cyl_spheres = Vec::new();
        for _ in 0..num_cyl_spheres {
            cyl_spheres.push(CylSphere::unpack(r)?);
        }
        let num_spheres = r.u32()?;
        let mut spheres = Vec::new();
        for _ in 0..num_spheres {
            spheres.push(Sphere::unpack(r)?);
        }
        let height = r.f32()?;
        let radius = r.f32()?;
        let step_up_height = r.f32()?;
        let step_down_height = r.f32()?;
        let sorting_sphere = Sphere::unpack(r)?;
        let selection_sphere = Sphere::unpack(r)?;
        let num_lights = r.i32()?;
        let mut lights = Vec::new();
        for _ in 0..num_lights {
            let key = r.i32()?;
            let light = LightInfo::unpack(r)?;
            check_unique("lights", &lights, key)?;
            lights.push((key, light));
        }
        Ok(Self {
            id,
            flags,
            parts,
            parent_index,
            default_scale,
            holding_locations,
            connection_points,
            placement_frames,
            cyl_spheres,
            spheres,
            height,
            radius,
            step_up_height,
            step_down_height,
            sorting_sphere,
            selection_sphere,
            lights,
            default_animation: r.u32()?,
            default_script: r.u32()?,
            default_motion_table: r.u32()?,
            default_sound_table: r.u32()?,
            default_script_table: r.u32()?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// A count, then `(parent location, location)` pairs.
fn read_locations(
    r: &mut Reader<'_>,
    table: &'static str,
) -> Result<Vec<(i32, LocationType)>, DecodeError> {
    let count = r.i32()?;
    let mut out = Vec::new();
    for _ in 0..count {
        let key = r.i32()?;
        let location = LocationType::unpack(r)?;
        check_unique(table, &out, key)?;
        out.push((key, location));
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::objects::HookData;
    use ac_bits::Writer;

    fn frame(w: &mut Writer, z: f32) {
        for v in [0.0, 0.0, z, 1.0, 0.0, 0.0, 0.0] {
            w.f32(v);
        }
    }

    fn sphere(w: &mut Writer, z: f32, r: f32) {
        for v in [0.0, 0.0, z, r] {
            w.f32(v);
        }
    }

    /// Two parts with every optional table present.
    fn full_sample() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x0200_0001);
        w.u32(SetupFlags::HAS_PARENT | SetupFlags::HAS_DEFAULT_SCALE | 0x4);
        w.u32(2);
        w.u32(0x0100_004E);
        w.u32(0x0100_004F);
        w.u32(0xFFFF_FFFF);
        w.u32(0);
        for _ in 0..2 {
            for v in [0.25f32, 0.5, 0.75] {
                w.f32(v);
            }
        }
        // Holding locations: 2.
        w.i32(2);
        w.i32(1);
        w.i32(1);
        frame(&mut w, 0.5);
        w.i32(2);
        w.i32(0);
        frame(&mut w, 0.25);
        // Connection points: 1.
        w.i32(1);
        w.i32(4);
        w.i32(0);
        frame(&mut w, 0.0);
        // Placements: 1, with one hook.
        w.i32(1);
        w.u32(0);
        frame(&mut w, 0.9625);
        frame(&mut w, 0.0);
        w.u32(1);
        w.u32(4); // animation done
        w.u32(1);
        // Cylinders: 1. Spheres: 2.
        w.u32(1);
        for v in [0.0f32, 0.0, -1.5, 0.3, 2.992] {
            w.f32(v);
        }
        w.u32(2);
        sphere(&mut w, 0.475, 0.48);
        sphere(&mut w, 1.35, 0.48);
        for v in [1.835f32, 0.678_822_5, 0.6, 1.5] {
            w.f32(v);
        }
        sphere(&mut w, 0.9125, 0.9175);
        sphere(&mut w, 0.9175, 0.9175);
        // Lights: 1.
        w.i32(1);
        w.i32(0);
        frame(&mut w, 0.28);
        w.bytes(&[0xFF, 0xFF, 0x80, 0xFF]);
        for v in [100.0f32, 4.0, 0.0] {
            w.f32(v);
        }
        for id in [
            0x0300_0001u32,
            0x3300_0002,
            0x0900_0001,
            0x2000_0001,
            0x3400_0004,
        ] {
            w.u32(id);
        }
        w.into_vec()
    }

    #[test]
    fn unpacks_every_table() {
        let bytes = full_sample();
        let mut r = Reader::new(&bytes);
        let s = Setup::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(s.parts, vec![0x0100_004E, 0x0100_004F]);
        assert_eq!(s.parent_index, vec![0xFFFF_FFFF, 0]);
        assert_eq!(s.default_scale.len(), 2);
        assert_eq!(s.default_scale[1].z, 0.75);
        assert_eq!(s.holding_locations.len(), 2);
        assert_eq!(s.holding_locations[1].0, 2);
        assert_eq!(s.holding_locations[1].1.frame.origin.z, 0.25);
        assert_eq!(s.connection_points[0].0, 4);
        assert_eq!(s.placement_frames.len(), 1);
        let (key, pf) = &s.placement_frames[0];
        assert_eq!(*key, 0);
        assert_eq!(pf.frames.len(), 2);
        assert_eq!(pf.frames[0].origin.z, 0.9625);
        assert_eq!(pf.hooks[0].data, HookData::AnimationDone);
        assert_eq!(pf.hooks[0].direction, 1);
        assert_eq!(s.cyl_spheres[0].height, 2.992);
        assert_eq!(s.spheres[1].origin.z, 1.35);
        assert_eq!(s.height, 1.835);
        assert_eq!(s.step_down_height, 1.5);
        assert_eq!(s.selection_sphere.radius, 0.9175);
        assert_eq!(s.lights[0].1.color.red, 0x80);
        assert_eq!(s.lights[0].1.intensity, 100.0);
        assert_eq!(s.default_animation, 0x0300_0001);
        assert_eq!(s.default_script_table, 0x3400_0004);
    }

    #[test]
    fn optional_per_part_tables_follow_the_flags() {
        let mut w = Writer::new();
        w.u32(0x0200_0279);
        w.u32(SetupFlags::ALLOW_FREE_HEADING);
        w.u32(1);
        w.u32(0x0100_0C22);
        w.i32(0); // holding
        w.i32(0); // connection
        w.i32(1); // placements
        w.u32(0);
        frame(&mut w, 0.0);
        w.u32(0); // hooks
        w.u32(0); // cylinders
        w.u32(0); // spheres
        for _ in 0..4 + 8 {
            w.f32(1.0);
        }
        w.i32(0); // lights
        for _ in 0..5 {
            w.u32(0);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let s = Setup::unpack(&mut r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert!(s.parent_index.is_empty());
        assert!(s.default_scale.is_empty());
        assert_eq!(s.parts, vec![0x0100_0C22]);
        assert!(s.flags.contains(SetupFlags::ALLOW_FREE_HEADING));
        assert!(!s.flags.contains(SetupFlags::HAS_PARENT));
    }

    #[test]
    fn truncated_setup_is_an_error_at_every_length() {
        let bytes = full_sample();
        for len in 0..bytes.len() {
            assert!(
                Setup::unpack(&mut Reader::new(&bytes[..len])).is_err(),
                "len {len}"
            );
        }
    }

    #[test]
    fn duplicate_holding_location_is_an_error() {
        let mut bytes = full_sample();
        // The second holding key (offset: 12 header + 8 parts + 8 parents +
        // 24 scales + 4 count + 36 first entry) becomes 1, like the first.
        let second_key = 12 + 8 + 8 + 24 + 4 + 36;
        bytes[second_key..second_key + 4].copy_from_slice(&1i32.to_le_bytes());
        assert!(matches!(
            Setup::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::DuplicateKey {
                table: "holding locations",
                key: 1
            })
        ));
    }
}
