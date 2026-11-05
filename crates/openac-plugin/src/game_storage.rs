//! Typed [`Host`] methods for API group N: storage. A plugin never names a
//! path: it names a [`StoragePlace`] (a root and a namespace) and a
//! relative `/`-separated key.

use ac_plugin_abi::calls;
use ac_plugin_abi::storage::{StorageKey, StoragePlace, StorageRoot, StorageWrite};

use crate::Host;

/// A lock other clients on this computer honour, from
/// [`Host::storage_lock`]; release it with [`Host::storage_unlock`] (the
/// client releases it when the plugin stops).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct StorageLock(pub u32);

fn key(place: &StoragePlace, key: &str) -> StorageKey {
    StorageKey {
        place: place.clone(),
        key: key.to_owned(),
    }
}

/// N. Storage.
impl Host<'_> {
    /// The bytes stored under `key`, none when nothing is.
    ///
    /// # Errors
    /// A bad key, a root this client does not have, or the file system.
    pub fn storage_read(
        &mut self,
        place: &StoragePlace,
        key_name: &str,
    ) -> Result<Option<Vec<u8>>, String> {
        self.call::<calls::StorageRead>(&key(place, key_name))
    }

    /// The text stored under `key` (a byte order mark honoured, as .NET
    /// reads a file), none when nothing is.
    ///
    /// # Errors
    /// As [`Host::storage_read`].
    pub fn storage_read_text(
        &mut self,
        place: &StoragePlace,
        key_name: &str,
    ) -> Result<Option<String>, String> {
        self.call::<calls::StorageReadText>(&key(place, key_name))
    }

    /// Replace what is stored under `key` with `bytes`, atomically.
    ///
    /// # Errors
    /// As [`Host::storage_read`]; the plugin's own files are read only.
    pub fn storage_write(
        &mut self,
        place: &StoragePlace,
        key_name: &str,
        bytes: Vec<u8>,
    ) -> Result<(), String> {
        self.call::<calls::StorageWrite>(&StorageWrite {
            place: place.clone(),
            key: key_name.to_owned(),
            bytes,
        })
    }

    /// Replace what is stored under `key` with `text` (UTF-8, no byte order
    /// mark), atomically.
    ///
    /// # Errors
    /// As [`Host::storage_write`].
    pub fn storage_write_text(
        &mut self,
        place: &StoragePlace,
        key_name: &str,
        text: &str,
    ) -> Result<(), String> {
        self.storage_write(place, key_name, text.as_bytes().to_vec())
    }

    /// The keys of the files beneath `prefix` (`""`: all), sorted ignoring
    /// case.
    ///
    /// # Errors
    /// As [`Host::storage_read`].
    pub fn storage_list(
        &mut self,
        place: &StoragePlace,
        prefix: &str,
    ) -> Result<Vec<String>, String> {
        self.call::<calls::StorageList>(&key(place, prefix))
    }

    /// Remove what is stored under `key`; false when nothing was.
    ///
    /// # Errors
    /// As [`Host::storage_write`].
    pub fn storage_delete(&mut self, place: &StoragePlace, key_name: &str) -> Result<bool, String> {
        self.call::<calls::StorageDelete>(&key(place, key_name))
    }

    /// Create the folder `prefix` names, with its parents.
    ///
    /// # Errors
    /// As [`Host::storage_write`].
    pub fn storage_ensure_folder(
        &mut self,
        place: &StoragePlace,
        prefix: &str,
    ) -> Result<bool, String> {
        self.call::<calls::StorageEnsureFolder>(&key(place, prefix))
    }

    /// Where a place is on disk, to show the player.
    pub fn storage_describe(&mut self, place: &StoragePlace) -> Option<String> {
        self.call::<calls::StorageDescribe>(place)
    }

    /// Take the lock `key` names.
    ///
    /// # Errors
    /// Another client holds it, or as [`Host::storage_read`].
    pub fn storage_lock(
        &mut self,
        place: &StoragePlace,
        key_name: &str,
    ) -> Result<StorageLock, String> {
        self.call::<calls::StorageLock>(&key(place, key_name))
            .map(StorageLock)
    }

    /// Release a lock.
    pub fn storage_unlock(&mut self, lock: StorageLock) -> bool {
        self.call::<calls::StorageUnlock>(&lock.0)
    }
}

/// The plugin's resource catalog (OpenAC `IPluginResourceCatalog`, over its
/// `FilePluginResourceCatalog`): the files the plugin shipped with, by
/// relative id, and data files layered with the player's own.
impl Host<'_> {
    /// A shipped resource's bytes, none when the package has no such file.
    ///
    /// # Errors
    /// An id that is blank, absolute or climbs out (`..`).
    pub fn resource_read(&mut self, resource_id: &str) -> Result<Option<Vec<u8>>, String> {
        self.storage_read(&StoragePlace::global(StorageRoot::PluginFiles), resource_id)
    }

    /// The shipped resources beneath `prefix` (`""`: all), sorted ignoring
    /// case.
    ///
    /// # Errors
    /// A prefix that is absolute or climbs out.
    pub fn resource_list(&mut self, prefix: &str) -> Result<Vec<String>, String> {
        self.storage_list(&StoragePlace::global(StorageRoot::PluginFiles), prefix)
    }

    /// The data files beneath `relative_directory` in every layer, the
    /// player's own (the plugin's data folder) over the package's: each
    /// relative path once, ignoring case, sorted ignoring case.
    ///
    /// # Errors
    /// A directory that is absolute or climbs out.
    pub fn resource_list_data_files(
        &mut self,
        relative_directory: &str,
    ) -> Result<Vec<String>, String> {
        self.call::<calls::ResourcesListDataFiles>(&relative_directory.to_owned())
    }
}
