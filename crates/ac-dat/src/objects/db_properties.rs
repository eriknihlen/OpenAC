//! A property collection file (`0x78000000..=0x7FFFFFFF`, portal dat):
//! typed properties keyed by id, the defaults and captions the interface
//! reads for some of its options.

use ac_bits::Reader;

use super::read::{self, keyed};
use super::ui_property::{MasterProperty, Property};
use crate::database::DatFile;
use crate::error::{DatError, DecodeError};

type Result<T> = core::result::Result<T, DecodeError>;

/// A property collection.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DbProperties {
    /// File id.
    pub id: u32,
    /// The properties, keyed by id, in file order.
    pub properties: Vec<(u32, Property)>,
}

impl DbProperties {
    /// Decode a collection from its file bytes: the id, a bucket-count
    /// byte, a count byte, then each key and its stored property.
    pub fn unpack(r: &mut Reader<'_>, master: &MasterProperty) -> Result<Self> {
        let id = r.u32()?;
        let _buckets = r.u8()?;
        let count = u64::from(r.u8()?);
        let properties = keyed(r, "db properties", count, read::u32, |r| {
            Property::unpack(r, master)
        })?;
        Ok(Self { id, properties })
    }

    /// Read collection `id` from `dat` (the portal dat); `None` when
    /// absent. The id is set to the file id.
    pub fn read(
        dat: &DatFile,
        master: &MasterProperty,
        id: u32,
    ) -> core::result::Result<Option<Self>, DatError> {
        let Some(bytes) = dat.get_bytes(id)? else {
            return Ok(None);
        };
        let mut properties = Self::unpack(&mut Reader::new(&bytes), master)
            .map_err(|source| DatError::Decode { id, source })?;
        properties.id = id;
        Ok(Some(properties))
    }

    /// The property stored under `key`.
    #[must_use]
    pub fn get(&self, key: u32) -> Option<&Property> {
        self.properties
            .iter()
            .find(|(k, _)| *k == key)
            .map(|(_, p)| p)
    }
}
