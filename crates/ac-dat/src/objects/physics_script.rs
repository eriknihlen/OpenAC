use ac_bits::Reader;

use super::read::{self, keyed, list};
use super::{AnimationHook, DatObject};
use crate::error::DecodeError;
use crate::header::DatFileType;

type Result<T> = core::result::Result<T, DecodeError>;

/// A timed list of effects (particles, sounds, fades, ...). File id
/// `0x33......`.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsScript {
    /// File id.
    pub id: u32,
    /// Effects, in file order.
    pub script_data: Vec<PhysicsScriptData>,
}

impl DatObject for PhysicsScript {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = u64::from(r.u32()?);
        Ok(Self {
            id,
            script_data: list(r, count, PhysicsScriptData::unpack)?,
        })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// One effect and when it starts.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PhysicsScriptData {
    /// Start, seconds after the script starts.
    pub start_time: f64,
    /// The effect.
    pub hook: AnimationHook,
}

impl PhysicsScriptData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            start_time: r.f64()?,
            hook: AnimationHook::unpack(r)?,
        })
    }
}

/// Physics scripts per script type, each type a weighted list. File id
/// `0x34......`.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsScriptTable {
    /// File id.
    pub id: u32,
    /// Scripts keyed by script type, in file order.
    pub script_table: Vec<(u32, PhysicsScriptTableData)>,
}

impl DatObject for PhysicsScriptTable {
    const DAT: DatFileType = DatFileType::Portal;

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let id = r.u32()?;
        let count = u64::from(r.u32()?);
        let script_table = keyed(
            r,
            "script types",
            count,
            read::u32,
            PhysicsScriptTableData::unpack,
        )?;
        Ok(Self { id, script_table })
    }

    fn set_id(&mut self, id: u32) {
        self.id = id;
    }
}

/// The scripts of one type.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsScriptTableData {
    /// Scripts, each with the modifier value it applies from.
    pub scripts: Vec<ScriptAndModData>,
}

impl PhysicsScriptTableData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let count = u64::from(r.u32()?);
        Ok(Self {
            scripts: list(r, count, ScriptAndModData::unpack)?,
        })
    }
}

/// A script and the modifier value it is chosen for.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScriptAndModData {
    /// Modifier value.
    pub modifier: f32,
    /// Physics script id (`0x33......`).
    pub script_id: u32,
}

impl ScriptAndModData {
    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            modifier: r.f32()?,
            script_id: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::objects::HookData;
    use ac_bits::Writer;

    #[test]
    fn unpacks_a_script() {
        let mut w = Writer::new();
        w.u32(0x3300_0007);
        w.u32(2);
        w.f64(0.0);
        w.u32(14); // destroy particle
        w.u32(0);
        w.u32(3);
        w.f64(1.5);
        w.u32(12); // scale
        w.u32(0);
        w.f32(2.0);
        w.f32(0.5);
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let s = PhysicsScript::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(
            s.script_data[0].hook.data,
            HookData::DestroyParticle { emitter_id: 3 }
        );
        assert_eq!(s.script_data[1].start_time, 1.5);
        assert_eq!(
            s.script_data[1].hook.data,
            HookData::Scale {
                end: 2.0,
                time: 0.5
            }
        );
        for len in 0..bytes.len() {
            assert!(PhysicsScript::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
    }

    #[test]
    fn unpacks_a_script_table() {
        let mut w = Writer::new();
        w.u32(0x3400_0004);
        w.u32(2);
        for (key, n) in [(165u32, 1u32), (166, 2)] {
            w.u32(key);
            w.u32(n);
            for i in 0..n {
                w.f32(i as f32);
                w.u32(0x3300_125E + i);
            }
        }
        let bytes = w.into_vec();
        let r = &mut Reader::new(&bytes);
        let t = PhysicsScriptTable::unpack(r).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.script_table[1].0, 166);
        assert_eq!(
            t.script_table[1].1.scripts[1],
            ScriptAndModData {
                modifier: 1.0,
                script_id: 0x3300_125F
            }
        );
        for len in 0..bytes.len() {
            assert!(PhysicsScriptTable::unpack(&mut Reader::new(&bytes[..len])).is_err());
        }
        // Same script type twice.
        let mut bytes = bytes;
        bytes[24..28].copy_from_slice(&165u32.to_le_bytes());
        assert!(matches!(
            PhysicsScriptTable::unpack(&mut Reader::new(&bytes)),
            Err(DecodeError::DuplicateKey { key: 165, .. })
        ));
    }
}
