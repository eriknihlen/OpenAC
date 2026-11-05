//! The dat objects effects read, behind one trait so tests can supply them
//! from memory and the client from the (cached) dat files.

use std::sync::Arc;

use ac_dat::objects::{
    GfxObj, GfxObjDegradeInfo, ParticleEmitter as DatParticleEmitter, PhysicsScriptTable, Surface,
};
use ac_dat::{CachedDats, DatCollection, DatError};

/// Read access to the dat objects the effects code needs.
pub trait VfxDats: Send + Sync {
    /// A particle emitter description (`0x32......`).
    fn particle_emitter(&self, id: u32) -> Result<Option<Arc<DatParticleEmitter>>, DatError>;
    /// A model (`0x01......`).
    fn gfx_obj(&self, id: u32) -> Result<Option<Arc<GfxObj>>, DatError>;
    /// A model's degrade table (`0x11......`).
    fn gfx_obj_degrade_info(&self, id: u32) -> Result<Option<Arc<GfxObjDegradeInfo>>, DatError>;
    /// A surface (`0x08......`).
    fn surface(&self, id: u32) -> Result<Option<Arc<Surface>>, DatError>;
    /// A physics script table (`0x34......`).
    fn physics_script_table(&self, id: u32) -> Result<Option<Arc<PhysicsScriptTable>>, DatError>;
    /// The raw bytes of a portal-dat file (physics scripts are decoded from
    /// these so their stored id can be checked).
    fn portal_bytes(&self, id: u32) -> Result<Option<Vec<u8>>, DatError>;
}

impl VfxDats for DatCollection {
    fn particle_emitter(&self, id: u32) -> Result<Option<Arc<DatParticleEmitter>>, DatError> {
        Ok(self.get(id)?.map(Arc::new))
    }

    fn gfx_obj(&self, id: u32) -> Result<Option<Arc<GfxObj>>, DatError> {
        Ok(self.get(id)?.map(Arc::new))
    }

    fn gfx_obj_degrade_info(&self, id: u32) -> Result<Option<Arc<GfxObjDegradeInfo>>, DatError> {
        Ok(self.get(id)?.map(Arc::new))
    }

    fn surface(&self, id: u32) -> Result<Option<Arc<Surface>>, DatError> {
        Ok(self.get(id)?.map(Arc::new))
    }

    fn physics_script_table(&self, id: u32) -> Result<Option<Arc<PhysicsScriptTable>>, DatError> {
        Ok(self.get(id)?.map(Arc::new))
    }

    fn portal_bytes(&self, id: u32) -> Result<Option<Vec<u8>>, DatError> {
        self.portal.get_bytes(id)
    }
}

impl VfxDats for CachedDats {
    fn particle_emitter(&self, id: u32) -> Result<Option<Arc<DatParticleEmitter>>, DatError> {
        self.get(id)
    }

    fn gfx_obj(&self, id: u32) -> Result<Option<Arc<GfxObj>>, DatError> {
        self.get(id)
    }

    fn gfx_obj_degrade_info(&self, id: u32) -> Result<Option<Arc<GfxObjDegradeInfo>>, DatError> {
        self.get(id)
    }

    fn surface(&self, id: u32) -> Result<Option<Arc<Surface>>, DatError> {
        self.get(id)
    }

    fn physics_script_table(&self, id: u32) -> Result<Option<Arc<PhysicsScriptTable>>, DatError> {
        self.get(id)
    }

    fn portal_bytes(&self, id: u32) -> Result<Option<Vec<u8>>, DatError> {
        self.uncached().portal.get_bytes(id)
    }
}
