//! The physics block of the create-object and update-object messages:
//! which physics fields are present, their values, and the object's nine
//! update counters.

use ac_bits::Reader;

use crate::{MovementData, Position, ProtoError, Result, Vector3};

/// Physics description flag: a setup (model assembly) id follows.
pub const PD_SETUP: u32 = 0x0000_0001;
/// Physics description flag: a motion table id follows.
pub const PD_MOTION_TABLE: u32 = 0x0000_0002;
/// Physics description flag: a velocity follows.
pub const PD_VELOCITY: u32 = 0x0000_0004;
/// Physics description flag: an acceleration follows.
pub const PD_ACCELERATION: u32 = 0x0000_0008;
/// Physics description flag: an angular velocity follows.
pub const PD_OMEGA: u32 = 0x0000_0010;
/// Physics description flag: a parent attachment follows.
pub const PD_PARENT: u32 = 0x0000_0020;
/// Physics description flag: a list of child attachments follows.
pub const PD_CHILDREN: u32 = 0x0000_0040;
/// Physics description flag: a scale follows.
pub const PD_OBJ_SCALE: u32 = 0x0000_0080;
/// Physics description flag: a friction follows.
pub const PD_FRICTION: u32 = 0x0000_0100;
/// Physics description flag: an elasticity follows.
pub const PD_ELASTICITY: u32 = 0x0000_0200;
/// Physics description flag: the update counters follow (always sent).
pub const PD_TIMESTAMPS: u32 = 0x0000_0400;
/// Physics description flag: a sound table id follows.
pub const PD_SOUND_TABLE: u32 = 0x0000_0800;
/// Physics description flag: a physics script table id follows.
pub const PD_PHYSICS_SCRIPT_TABLE: u32 = 0x0000_1000;
/// Physics description flag: a default script follows.
pub const PD_DEFAULT_SCRIPT: u32 = 0x0000_2000;
/// Physics description flag: a default script intensity follows.
pub const PD_DEFAULT_SCRIPT_INTENSITY: u32 = 0x0000_4000;
/// Physics description flag: a position follows.
pub const PD_POSITION: u32 = 0x0000_8000;
/// Physics description flag: movement data follows.
pub const PD_MOVEMENT: u32 = 0x0001_0000;
/// Physics description flag: an animation frame (placement) follows;
/// read only when movement data is absent.
pub const PD_ANIMATION_FRAME: u32 = 0x0002_0000;
/// Physics description flag: a translucency follows.
pub const PD_TRANSLUCENCY: u32 = 0x0004_0000;

/// Most child attachments accepted in one description.
pub const MAX_CHILDREN: u32 = 1024;

/// The nine per-object update counters, in wire order.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PhysicsTimestamps {
    /// Position updates.
    pub position: u16,
    /// Movement updates.
    pub movement: u16,
    /// Physics state updates.
    pub state: u16,
    /// Velocity updates.
    pub vector: u16,
    /// Teleports.
    pub teleport: u16,
    /// Server-controlled movement era.
    pub server_controlled_move: u16,
    /// Forced positions.
    pub force_position: u16,
    /// Appearance updates.
    pub obj_desc: u16,
    /// The object's incarnation.
    pub instance: u16,
}

impl PhysicsTimestamps {
    /// Read the nine counters, then pad to 4 bytes.
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let t = Self {
            position: r.u16()?,
            movement: r.u16()?,
            state: r.u16()?,
            vector: r.u16()?,
            teleport: r.u16()?,
            server_controlled_move: r.u16()?,
            force_position: r.u16()?,
            obj_desc: r.u16()?,
            instance: r.u16()?,
        };
        r.align(4)?;
        Ok(t)
    }
}

/// An attachment: the other object and the attachment point.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PhysicsAttachment {
    /// The other object.
    pub guid: u32,
    /// The attachment point (parent location).
    pub location_id: u32,
}

/// A present movement field.
#[derive(Debug, Clone, PartialEq)]
pub enum PhysicsMovement {
    /// Present with a zero length: no data and no autonomy word.
    Empty,
    /// Movement data and the autonomy word that follows it.
    Present {
        /// The movement bytes as sent.
        raw: Vec<u8>,
        /// The movement bytes parsed.
        data: MovementData,
        /// True when the movement was started by the object's own client.
        is_autonomous: bool,
    },
}

/// The physics block. Fields appear on the wire in this order, each only
/// when its flag is set; movement and animation frame are exclusive.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsDesc {
    /// Which fields are present (`PD_*`).
    pub flags: u32,
    /// Physics state bits.
    pub state: u32,
    /// Movement.
    pub movement: Option<PhysicsMovement>,
    /// Animation frame (placement), when movement is absent.
    pub animation_frame: Option<u32>,
    /// Position.
    pub position: Option<Position>,
    /// Motion table id.
    pub motion_table_id: Option<u32>,
    /// Sound table id.
    pub sound_table_id: Option<u32>,
    /// Physics script table id.
    pub physics_script_table_id: Option<u32>,
    /// Setup (model assembly) id.
    pub setup_id: Option<u32>,
    /// Parent attachment.
    pub parent: Option<PhysicsAttachment>,
    /// Child attachments.
    pub children: Option<Vec<PhysicsAttachment>>,
    /// Scale.
    pub scale: Option<f32>,
    /// Friction.
    pub friction: Option<f32>,
    /// Elasticity.
    pub elasticity: Option<f32>,
    /// Translucency.
    pub translucency: Option<f32>,
    /// Velocity, meters per second.
    pub velocity: Option<Vector3>,
    /// Acceleration, meters per second squared.
    pub acceleration: Option<Vector3>,
    /// Angular velocity, radians per second.
    pub omega: Option<Vector3>,
    /// Default script type.
    pub default_script: Option<u32>,
    /// Default script intensity.
    pub default_script_intensity: Option<f32>,
    /// Update counters.
    pub timestamps: PhysicsTimestamps,
}

impl PhysicsDesc {
    /// Read the block. The reader's start must be the message start (the
    /// counters pad against it).
    pub fn read(r: &mut Reader<'_>) -> Result<Self> {
        let flags = r.u32()?;
        let state = r.u32()?;
        let has = |bit: u32| flags & bit != 0;
        let opt_u32 = |r: &mut Reader<'_>, bit| -> Result<Option<u32>> {
            Ok(if has(bit) { Some(r.u32()?) } else { None })
        };
        let opt_f32 = |r: &mut Reader<'_>, bit| -> Result<Option<f32>> {
            Ok(if has(bit) { Some(r.f32()?) } else { None })
        };
        let opt_vec = |r: &mut Reader<'_>, bit| -> Result<Option<Vector3>> {
            Ok(if has(bit) {
                Some(Vector3::read(r)?)
            } else {
                None
            })
        };

        let mut movement = None;
        let mut animation_frame = None;
        if has(PD_MOVEMENT) {
            movement = Some(read_movement(r)?);
        } else if has(PD_ANIMATION_FRAME) {
            animation_frame = Some(r.u32()?);
        }

        let position = if has(PD_POSITION) {
            Some(Position::read(r)?)
        } else {
            None
        };
        let motion_table_id = opt_u32(r, PD_MOTION_TABLE)?;
        let sound_table_id = opt_u32(r, PD_SOUND_TABLE)?;
        let physics_script_table_id = opt_u32(r, PD_PHYSICS_SCRIPT_TABLE)?;
        let setup_id = opt_u32(r, PD_SETUP)?;
        let parent = if has(PD_PARENT) {
            Some(PhysicsAttachment {
                guid: r.u32()?,
                location_id: r.u32()?,
            })
        } else {
            None
        };
        let children = if has(PD_CHILDREN) {
            Some(read_children(r)?)
        } else {
            None
        };
        let scale = opt_f32(r, PD_OBJ_SCALE)?;
        let friction = opt_f32(r, PD_FRICTION)?;
        let elasticity = opt_f32(r, PD_ELASTICITY)?;
        let translucency = opt_f32(r, PD_TRANSLUCENCY)?;
        let velocity = opt_vec(r, PD_VELOCITY)?;
        let acceleration = opt_vec(r, PD_ACCELERATION)?;
        let omega = opt_vec(r, PD_OMEGA)?;
        let default_script = opt_u32(r, PD_DEFAULT_SCRIPT)?;
        let default_script_intensity = opt_f32(r, PD_DEFAULT_SCRIPT_INTENSITY)?;
        let timestamps = PhysicsTimestamps::read(r)?;

        Ok(Self {
            flags,
            state,
            movement,
            animation_frame,
            position,
            motion_table_id,
            sound_table_id,
            physics_script_table_id,
            setup_id,
            parent,
            children,
            scale,
            friction,
            elasticity,
            translucency,
            velocity,
            acceleration,
            omega,
            default_script,
            default_script_intensity,
            timestamps,
        })
    }
}

/// Movement: a byte length, that many bytes of movement data, then an
/// autonomy word — unless the length is zero, when nothing else follows.
fn read_movement(r: &mut Reader<'_>) -> Result<PhysicsMovement> {
    let length = r.u32()?;
    if length == 0 {
        return Ok(PhysicsMovement::Empty);
    }
    let len = usize::try_from(length).map_err(|_| ProtoError::CountExceedsPayload {
        what: "movement bytes",
        count: length,
    })?;
    if len > r.remaining() {
        return Err(ProtoError::CountExceedsPayload {
            what: "movement bytes",
            count: length,
        });
    }
    let raw = r.bytes(len)?;
    // The blob starts 4-aligned with the message, so padding inside it
    // measured from its own start matches the message's.
    let data = MovementData::read(&mut Reader::new(raw))?;
    let is_autonomous = r.u32()? != 0;
    Ok(PhysicsMovement::Present {
        raw: raw.to_vec(),
        data,
        is_autonomous,
    })
}

fn read_children(r: &mut Reader<'_>) -> Result<Vec<PhysicsAttachment>> {
    let count = r.i32()?;
    let count = u32::try_from(count)
        .ok()
        .filter(|&c| c <= MAX_CHILDREN)
        .ok_or(ProtoError::CountOutOfRange {
            what: "physics children",
            count: count as u32,
            max: MAX_CHILDREN,
        })?;
    if count as usize * 8 > r.remaining() {
        return Err(ProtoError::CountExceedsPayload {
            what: "physics children",
            count,
        });
    }
    (0..count)
        .map(|_| {
            Ok(PhysicsAttachment {
                guid: r.u32()?,
                location_id: r.u32()?,
            })
        })
        .collect()
}
