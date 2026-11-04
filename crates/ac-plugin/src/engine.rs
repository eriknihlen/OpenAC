//! The WebAssembly engine one process shares, its cache of compiled
//! modules, and the check a module passes before it is instantiated: the
//! API version it was built for, the record layouts it was built with
//! (`ac_plugin_abi::layout`), every import it asks for against the
//! manifest's permissions, and the exports the calling convention needs.
//!
//! The engine is `wasmtime` with its Cranelift compiler: a module is
//! validated and compiled to machine code once per process when it is
//! first loaded, and the machine code is kept in the client's cache folder
//! ([`ModuleCache`]) so the next start loads it instead of compiling.
//! The engine counts no
//! instructions: a call is bounded only by its wall-clock deadline, through
//! epoch interruption, and the engine's watchdog thread moves the epoch on
//! ([`crate::watchdog`]).

use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::SystemTime;

use ac_plugin_abi::layout::{PluginLayout, core_shape};
use ac_plugin_abi::{
    API_CURRENT, API_MINIMUM, API_SECTION, ApiVersion, Import, LAYOUT_SECTION, MODULE, exports,
    is_supported,
};
use sha2::{Digest, Sha256};
use wasmtime::wasmparser::{Parser, Payload};
use wasmtime::{Config, Engine, ExternType, FuncType, ImportType, ValType};

use crate::manifest::PluginManifest;
use crate::watchdog::{Ticker, TimeLimits};

/// The engine and the modules it has compiled, shared by every session of
/// the process (each session gets its own instance and memory).
#[derive(Debug)]
pub struct PluginEngine {
    engine: Engine,
    modules: Mutex<HashMap<PathBuf, Cached>>,
    /// The compiled modules kept on disk, when the client has a cache
    /// folder.
    disk: Option<ModuleCache>,
    /// How many modules were loaded from `disk` rather than compiled.
    disk_hits: AtomicU64,
    /// How long a call may take.
    limits: TimeLimits,
    /// The thread that moves the epoch on, started with the first plugin
    /// (a process that runs none has no such thread); ended when the
    /// engine is dropped.
    ticker: OnceLock<Result<Ticker, String>>,
}

#[derive(Debug)]
struct Cached {
    length: u64,
    modified: Option<SystemTime>,
    module: PluginModule,
}

/// A compiled plugin module and the custom sections the client checks
/// (the compiled module keeps none).
#[derive(Debug, Clone)]
pub struct PluginModule {
    module: wasmtime::Module,
    api_section: Option<Vec<u8>>,
    layout_section: Option<Vec<u8>>,
}

impl PluginModule {
    /// Compiles `bytes` (validating them first), or loads their machine
    /// code from `disk` when it holds this module compiled by this engine,
    /// and keeps the client's custom sections.
    ///
    /// # Errors
    /// The bytes are not a valid module, in words.
    fn new(
        engine: &Engine,
        bytes: &[u8],
        disk: Option<&ModuleCache>,
        disk_hits: &AtomicU64,
    ) -> Result<Self, String> {
        let invalid = |error: &dyn std::fmt::Display| {
            format!("entry is not a valid WebAssembly module: {error}")
        };
        let module = match disk {
            Some(disk) => disk.load_or_compile(engine, bytes).map(|(module, cached)| {
                if cached {
                    disk_hits.fetch_add(1, Ordering::Relaxed);
                }
                module
            }),
            None => wasmtime::Module::new(engine, bytes),
        }
        .map_err(|error| invalid(&error))?;
        let mut api_section = None;
        let mut layout_section = None;
        for payload in Parser::new(0).parse_all(bytes) {
            if let Payload::CustomSection(section) = payload.map_err(|error| invalid(&error))? {
                if section.name() == API_SECTION && api_section.is_none() {
                    api_section = Some(section.data().to_vec());
                } else if section.name() == LAYOUT_SECTION && layout_section.is_none() {
                    layout_section = Some(section.data().to_vec());
                }
            }
        }
        Ok(Self {
            module,
            api_section,
            layout_section,
        })
    }

    /// The compiled module.
    pub(crate) fn module(&self) -> &wasmtime::Module {
        &self.module
    }

    /// What the module imports, in its order.
    pub fn imports(&self) -> impl ExactSizeIterator<Item = ImportType<'_>> + '_ {
        self.module.imports()
    }
}

impl Default for PluginEngine {
    fn default() -> Self {
        Self::new()
    }
}

impl PluginEngine {
    /// An engine with epoch interruption (each call's deadline) and no fuel
    /// metering, that compiles each module once, on all cores. A plugin's
    /// import is answered by the session between two steps of the plugin's
    /// call (the call is driven as a future, see `instance.rs`), which
    /// needs wasmtime's async support.
    ///
    /// # Panics
    /// Only when wasmtime cannot run on this computer at all (its compiler
    /// has no backend for the processor).
    #[must_use]
    pub fn new() -> Self {
        let mut config = Config::new();
        config
            .consume_fuel(false)
            .epoch_interruption(true)
            .parallel_compilation(true);
        let engine = Engine::new(&config)
            .unwrap_or_else(|error| panic!("the plugin engine cannot start: {error}"));
        Self {
            engine,
            modules: Mutex::new(HashMap::new()),
            disk: None,
            disk_hits: AtomicU64::new(0),
            limits: TimeLimits::default(),
            ticker: OnceLock::new(),
        }
    }

    /// [`Self::new`] with other time limits: shorter deadlines, so a test
    /// of a stuck plugin does not wait ten seconds, or a slow-call warning
    /// out of reach, so a test comparing two sessions does not depend on
    /// how loaded the machine is.
    #[cfg(feature = "test-fixtures")]
    #[must_use]
    pub fn with_time_limits(limits: TimeLimits) -> Self {
        let mut engine = Self::new();
        engine.limits = limits;
        engine
    }

    /// How long a call may take.
    #[must_use]
    pub fn time_limits(&self) -> TimeLimits {
        self.limits
    }

    /// Starts the watchdog thread if it is not running yet.
    ///
    /// # Errors
    /// The thread could not be started: no plugin may run without it (a
    /// stuck one would hold its session forever).
    pub(crate) fn start_watchdog(&self) -> Result<(), String> {
        self.ticker
            .get_or_init(|| Ticker::start(&self.engine))
            .as_ref()
            .map(|_| ())
            .map_err(Clone::clone)
    }

    /// [`Self::new`], keeping compiled modules in `directory` (the client's
    /// `<cache>/plugin-modules`, `ac_host::PathSet::plugin_modules_directory`)
    /// so a plugin is compiled once per plugin and client version, not once
    /// per start.
    ///
    /// # Panics
    /// As [`Self::new`].
    #[must_use]
    pub fn with_module_cache(directory: PathBuf) -> Self {
        let mut engine = Self::new();
        engine.disk = Some(ModuleCache::new(&engine.engine, directory));
        engine
    }

    pub(crate) fn engine(&self) -> &Engine {
        &self.engine
    }

    /// The compiled modules kept on disk, if this engine keeps them.
    #[must_use]
    pub fn module_cache(&self) -> Option<&ModuleCache> {
        self.disk.as_ref()
    }

    /// How many modules this engine loaded from its [`ModuleCache`] rather
    /// than compiled.
    #[must_use]
    pub fn module_cache_hits(&self) -> u64 {
        self.disk_hits.load(Ordering::Relaxed)
    }

    /// The module in `path`, compiled once per process; a file whose size
    /// or time changed since is read again (so `/plugin reload` picks up a
    /// rebuilt plugin).
    ///
    /// # Errors
    /// The file cannot be read or is not a valid module.
    pub fn module(&self, path: &Path) -> Result<PluginModule, String> {
        let metadata =
            std::fs::metadata(path).map_err(|_| format!("entry not found: {}", path.display()))?;
        let length = metadata.len();
        let modified = metadata.modified().ok();
        {
            // A poisoned cache only means another session's load panicked
            // part-way; the map itself is still consistent.
            let modules = self
                .modules
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            if let Some(cached) = modules.get(path)
                && cached.length == length
                && cached.modified == modified
            {
                return Ok(cached.module.clone());
            }
        }
        let bytes = std::fs::read(path)
            .map_err(|error| format!("entry could not be read: {}: {error}", path.display()))?;
        // Compiled outside the lock: another session's load of another
        // plugin does not wait for this one.
        let module = PluginModule::new(&self.engine, &bytes, self.disk.as_ref(), &self.disk_hits)?;
        self.modules
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .insert(
                path.to_path_buf(),
                Cached {
                    length,
                    modified,
                    module: module.clone(),
                },
            );
        Ok(module)
    }
}

/// The first bytes of a cache file.
const CACHE_MAGIC: &[u8; 8] = b"OACPMOD1";

/// The extension of a cache file.
const CACHE_EXTENSION: &str = "cwasm";

/// Compiled plugin modules kept on disk: one file per module and engine,
/// named by the SHA-256 of the `.wasm` and of everything the machine code
/// depends on (wasmtime's version, the engine's settings, the processor's
/// features: `Engine::precompile_compatibility_hash`). A plugin update, a
/// client update or another processor therefore misses and compiles again.
///
/// A file holds [`CACHE_MAGIC`], its own name's two hashes, the SHA-256 of
/// the machine code, then the machine code (`Module::serialize`). One whose
/// header, key or hash does not match (a torn write, a disk error, an edit)
/// is ignored and written again; only a file that matches all three is
/// handed to wasmtime.
#[derive(Debug, Clone)]
pub struct ModuleCache {
    directory: PathBuf,
    /// The SHA-256 of the engine's compatibility hash.
    engine_key: [u8; 32],
}

/// Feeds a `Hash` into SHA-256 (the standard hasher's output is neither
/// wide nor stable enough to name a file by).
struct Sha256Hasher(Sha256);

impl Hasher for Sha256Hasher {
    fn finish(&self) -> u64 {
        // Only `write` is used; the digest is read with `finalize`.
        0
    }

    fn write(&mut self, bytes: &[u8]) {
        self.0.update(bytes);
    }
}

fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write as _;
    let mut text = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        let _ = write!(text, "{byte:02x}");
    }
    text
}

impl ModuleCache {
    /// The cache in `directory` for modules compiled by `engine`.
    #[must_use]
    fn new(engine: &Engine, directory: PathBuf) -> Self {
        let mut hasher = Sha256Hasher(Sha256::new());
        engine.precompile_compatibility_hash().hash(&mut hasher);
        Self {
            directory,
            engine_key: hasher.0.finalize().into(),
        }
    }

    /// The folder.
    #[must_use]
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// The file `bytes` compiled by this engine is kept in, and its key
    /// (the module's hash, then the engine's).
    #[must_use]
    pub fn path_of(&self, bytes: &[u8]) -> PathBuf {
        let (module_key, _) = self.key(bytes);
        self.directory.join(format!(
            "{}-{}.{CACHE_EXTENSION}",
            hex(&module_key),
            hex(&self.engine_key[..8])
        ))
    }

    fn key(&self, bytes: &[u8]) -> ([u8; 32], [u8; 64]) {
        let module_key: [u8; 32] = Sha256::digest(bytes).into();
        let mut key = [0u8; 64];
        key[..32].copy_from_slice(&module_key);
        key[32..].copy_from_slice(&self.engine_key);
        (module_key, key)
    }

    /// The module `bytes` compiles to, and whether it came from its cache
    /// file: from there when that holds it, else compiled and written there
    /// (a write that fails only means the next start compiles again).
    ///
    /// # Errors
    /// The bytes are not a valid module.
    fn load_or_compile(
        &self,
        engine: &Engine,
        bytes: &[u8],
    ) -> wasmtime::Result<(wasmtime::Module, bool)> {
        let path = self.path_of(bytes);
        let (_, key) = self.key(bytes);
        if let Some(module) = std::fs::read(&path)
            .ok()
            .and_then(|file| Self::load(engine, &key, &file))
        {
            return Ok((module, true));
        }
        let module = wasmtime::Module::new(engine, bytes)?;
        if let Ok(code) = module.serialize() {
            self.store(&path, &key, &code);
        }
        Ok((module, false))
    }

    /// The module in a cache `file`, when its header, key and hash match.
    fn load(engine: &Engine, key: &[u8; 64], file: &[u8]) -> Option<wasmtime::Module> {
        let header = CACHE_MAGIC.len() + key.len() + 32;
        if file.len() < header || &file[..CACHE_MAGIC.len()] != CACHE_MAGIC {
            return None;
        }
        let (stored_key, rest) = file[CACHE_MAGIC.len()..].split_at(key.len());
        let (stored_hash, code) = rest.split_at(32);
        if stored_key != key || Sha256::digest(code).as_slice() != stored_hash {
            return None;
        }
        // SAFETY: `Module::deserialize` runs the bytes as machine code
        // without checking them, so they must be what `Module::serialize`
        // wrote for this engine. They are: the file sits in the client's
        // own cache folder, which only this client writes; it is named
        // after and carries the hash of the `.wasm` it was compiled from
        // and of this engine's compatibility hash (wasmtime's version and
        // settings, the processor's features), both checked above; and the
        // SHA-256 of the code itself is checked above, so a torn or damaged
        // file never reaches here. wasmtime checks its own header and the
        // engine's compatibility once more and refuses a mismatch with an
        // error. Someone able to write this folder can also replace the
        // client's own executable beside it, so the cache adds no new way
        // in.
        #[allow(unsafe_code)]
        let module = unsafe { wasmtime::Module::deserialize(engine, code) };
        module.ok()
    }

    /// Writes a cache file, through a temporary file renamed into place so
    /// a reader never sees half of one.
    fn store(&self, path: &Path, key: &[u8; 64], code: &[u8]) {
        if std::fs::create_dir_all(&self.directory).is_err() {
            return;
        }
        let mut file = Vec::with_capacity(CACHE_MAGIC.len() + key.len() + 32 + code.len());
        file.extend_from_slice(CACHE_MAGIC);
        file.extend_from_slice(key);
        file.extend_from_slice(&Sha256::digest(code));
        file.extend_from_slice(code);
        let temporary =
            path.with_extension(format!("{CACHE_EXTENSION}.{}.tmp", std::process::id()));
        if std::fs::write(&temporary, &file).is_ok() && std::fs::rename(&temporary, path).is_err() {
            let _ = std::fs::remove_file(&temporary);
        }
    }
}

/// What the client checked a module for, and the imports to link.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckedModule {
    /// The API version in the module's `openac.api` section.
    pub api_version: ApiVersion,
    /// The client functions it imports, all allowed.
    pub imports: Vec<Import>,
}

/// Whether the manifest's API version is one this client loads.
///
/// # Errors
/// OpenAC's wording, with both versions.
pub fn check_api_version(manifest: &PluginManifest) -> Result<(), String> {
    if is_supported(manifest.api_version) {
        Ok(())
    } else {
        Err(format!(
            "plugin '{}' declares apiVersion {}, but this build supports {API_MINIMUM}..{API_CURRENT}",
            manifest.id, manifest.api_version
        ))
    }
}

/// The words every refusal of [`check_layout`] carries.
pub const LAYOUT_REFUSAL: &str = "built against a different plugin API layout";

/// Whether the module, built for API `api_version`, was built against this
/// client's record layouts (`ac_plugin_abi::layout`): its `openac.layout`
/// section must hold this client's core shape at that version (the start
/// record, the events that version had, the callbacks:
/// `ac_plugin_abi::layout::core_shape`) and, for every client function it
/// imports, this client's shape of that function at that version
/// (`Import::shape_at`). A function it does not import is not compared, so
/// a plugin built before an import was added still loads; nor is an event
/// a later version added, which the session never sends it, or a record
/// field a later version added, which the session leaves out of what it
/// sends it. A module without the section was built by an SDK from
/// before the fingerprint and is refused the same way (its records may
/// differ from this client's). Imports this client does not have are left
/// to [`check_module`].
///
/// # Errors
/// The section is missing or malformed, or a shape differs; the message
/// carries [`LAYOUT_REFUSAL`] and tells the author to rebuild.
pub fn check_layout(module: &PluginModule, api_version: ApiVersion) -> Result<(), String> {
    let refuse = |why: String| {
        Err(format!(
            "the .wasm was {LAYOUT_REFUSAL} ({why}); rebuild it against this client's SDK"
        ))
    };
    let Some(section) = &module.layout_section else {
        return refuse(format!(
            "it has no {LAYOUT_SECTION} section: an SDK from before layout fingerprints"
        ));
    };
    let Some(built) = PluginLayout::parse(section) else {
        return refuse(format!("its {LAYOUT_SECTION} section is malformed"));
    };
    if built.core != core_shape(api_version) {
        return refuse("its start record, events or callbacks differ from this client's".into());
    }
    for import in module.imports() {
        if import.module() != MODULE {
            continue;
        }
        let name = import.name();
        if let Some(known) = Import::from_name(name)
            && built.import_shape(name) != Some(known.shape_at(api_version))
        {
            return refuse(format!(
                "the request or reply of {MODULE}.{name} differs from this client's"
            ));
        }
    }
    Ok(())
}

/// The value types of the calling convention.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Kind {
    I32,
    I64,
}

/// Whether `ty` takes exactly `params` and returns exactly `results`.
fn signature_is(ty: &FuncType, params: &[Kind], results: &[Kind]) -> bool {
    let same = |types: &mut dyn ExactSizeIterator<Item = ValType>, kinds: &[Kind]| {
        types.len() == kinds.len()
            && types.zip(kinds).all(|(ty, kind)| match kind {
                Kind::I32 => matches!(ty, ValType::I32),
                Kind::I64 => matches!(ty, ValType::I64),
            })
    };
    same(&mut ty.params(), params) && same(&mut ty.results(), results)
}

/// Checks a module against its manifest before anything of it runs.
///
/// # Errors
/// The first problem, in words that name the import or export at fault.
pub fn check_module(
    module: &PluginModule,
    manifest: &PluginManifest,
) -> Result<CheckedModule, String> {
    let section = module.api_section.as_deref().ok_or_else(|| {
        format!(
            "the .wasm has no {API_SECTION} section (it was not built with the openac-plugin SDK)"
        )
    })?;
    let api_version = std::str::from_utf8(section)
        .ok()
        .and_then(ApiVersion::parse)
        .ok_or_else(|| format!("the .wasm's {API_SECTION} section is malformed"))?;
    if api_version != manifest.api_version {
        return Err(format!(
            "the .wasm was built for API {api_version} but plugin.json says {}",
            manifest.api_version
        ));
    }
    check_layout(module, api_version)?;

    let mut imports = Vec::new();
    for import in module.imports() {
        let (module_name, name) = (import.module(), import.name());
        if module_name != MODULE {
            return Err(format!(
                "imports {module_name}.{name}, which this client does not provide (plugins get \
                 only the {MODULE} functions; no WASI)"
            ));
        }
        let Some(known) = Import::from_name(name) else {
            return Err(format!(
                "imports {MODULE}.{name}, which API {API_CURRENT} of this client does not have \
                 (the plugin was built for API {api_version})"
            ));
        };
        if !known.since().is_at_most(api_version) {
            return Err(format!(
                "imports {MODULE}.{name}, which API {} added (the plugin was built for API \
                 {api_version})",
                known.since()
            ));
        }
        let ExternType::Func(ty) = import.ty() else {
            return Err(format!(
                "imports {MODULE}.{name} as something other than a function"
            ));
        };
        if !signature_is(&ty, &[Kind::I32, Kind::I32], &[Kind::I32]) {
            return Err(format!("imports {MODULE}.{name} with the wrong signature"));
        }
        if let Some(permission) = known.permission()
            && !manifest.allows(permission)
        {
            return Err(format!(
                "imports {MODULE}.{name}, which needs the '{}' permission plugin.json does not \
                 declare",
                permission.name()
            ));
        }
        if !imports.contains(&known) {
            imports.push(known);
        }
    }

    let expected: [(&str, &[Kind], &[Kind]); 5] = [
        (exports::ALLOC, &[Kind::I32], &[Kind::I32]),
        (exports::ENABLE, &[Kind::I32, Kind::I32], &[Kind::I64]),
        (exports::DISABLE, &[], &[]),
        (exports::TICK, &[], &[]),
        (exports::EVENT, &[Kind::I32, Kind::I32], &[]),
    ];
    let has_memory = module
        .module
        .exports()
        .any(|e| e.name() == exports::MEMORY && matches!(e.ty(), ExternType::Memory(_)));
    if !has_memory {
        return Err(format!("the .wasm does not export its {}", exports::MEMORY));
    }
    for (name, params, results) in expected {
        let export = module
            .module
            .exports()
            .find(|e| e.name() == name)
            .ok_or_else(|| format!("the .wasm does not export {name}"))?;
        match export.ty() {
            ExternType::Func(ty) if signature_is(&ty, params, results) => {}
            _ => return Err(format!("the .wasm exports {name} with the wrong signature")),
        }
    }
    Ok(CheckedModule {
        api_version,
        imports,
    })
}
