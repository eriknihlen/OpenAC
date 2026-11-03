//! A session's plugins: found in the plugin roots, loaded in order, given
//! events and the tick, reloaded while the session runs, and unloaded (in
//! reverse order at the end, or alone after a fault).
//!
//! A plugin is reloaded when the player asks (`/plugin reload <id>` or
//! `all`) or when its code, markup or `plugin.json` changes on disk and
//! those files then stay quiet for a second ([`crate::reload`]); a plugin
//! that failed to start, or stopped with a fault, is tried again the same
//! way. The new copy is read and checked before the running one is let go,
//! so an update this client cannot run leaves the running copy alone.
//!
//! The session owns one [`PluginSession`] and drives it on its own thread.
//! Plugins are never re-entered: everything a plugin call asks for that
//! would run plugin code again (a chat-box submit, a chat line other
//! plugins hear) is carried out right after the call returns, before the
//! next call ([`SessionApi::submit_now`], [`SessionApi::new_chat_events`]),
//! or by the session after the pass when it cannot be done then
//! ([`PluginSession::take_submits`]).

use std::collections::{HashMap, HashSet, VecDeque};
use std::path::PathBuf;
use std::sync::Arc;

use ac_plugin_abi::callback::{Callback, CallbackAnswer, InterceptDecision};
use ac_plugin_abi::ui::UiEvent;
use ac_plugin_abi::verbs::split_command_line;
use ac_plugin_abi::{
    API_CURRENT, ApiVersion, ChatLine, Event, Import, LogLevel, Permission, StartInfo, Wire,
};

use crate::api::{PluginReport, SessionApi};
use crate::commands::CommandRegistry;
use crate::discovery::{self, Discovered};
use crate::engine::CheckedModule;
use crate::engine::{PluginEngine, check_api_version, check_module};
use crate::host_calls::{Context, Others, Services, handle_import};
use crate::instance::{Handled, ImportHandler, Instance, SlowCall};
use crate::manifest::PluginManifest;
use crate::reload::FolderWatch;
use crate::settings::PluginSessionSettings;
use crate::tick::TickClock;
use crate::version::{HostVersion, incompatibility};
use crate::watchdog::{Entry, duration_words};

/// The id the plugin manager's own log lines carry.
pub const MANAGER_ID: &str = "plugins";

/// The verb of the plugin manager (`/plugin`, `/plugin reload <id>`).
pub const MANAGER_VERB: &str = "plugin";

/// What `/plugin reload` takes to reload every plugin this session was
/// asked to run.
pub const ALL_PLUGINS: &str = "all";

/// How often the client log names a plugin's slow calls of one entry call,
/// at most: once a minute, session time.
pub const SLOW_CALL_LOG_INTERVAL_SECONDS: f64 = 60.0;

/// The slow calls ([`crate::SLOW_CALL`]) the session has reported: a
/// warning in the log per plugin and entry call at most every
/// [`SLOW_CALL_LOG_INTERVAL_SECONDS`], and one line in chat per plugin.
#[derive(Debug, Default)]
struct SlowCalls {
    /// When each (plugin, entry call) was last logged, session seconds.
    logged: HashMap<(String, String), f64>,
    /// The plugins already named in chat.
    told: HashSet<String>,
}

impl SlowCalls {
    fn report(
        &mut self,
        now: f64,
        id: &str,
        name: &str,
        slow: &SlowCall,
        api: &mut dyn SessionApi,
    ) {
        let key = (id.to_ascii_lowercase(), slow.entry.clone());
        let due = self
            .logged
            .get(&key)
            .is_none_or(|last| now - last >= SLOW_CALL_LOG_INTERVAL_SECONDS);
        if due {
            self.logged.insert(key, now);
            api.log(
                id,
                LogLevel::Warn,
                &format!(
                    "plugin '{id}' was slow in {}: {} ms (it keeps running; it is stopped past \
                     {}); last imports: {}",
                    slow.entry,
                    slow.time.as_millis(),
                    duration_words(slow.deadline),
                    slow.imports
                ),
            );
        }
        if self.told.insert(id.to_ascii_lowercase()) {
            api.post_chat(
                &format!(
                    "[plugin] {name} was slow ({}, {} ms); it keeps running",
                    slow.entry,
                    slow.time.as_millis()
                ),
                0,
            );
        }
    }
}

/// How a session's plugins are found and configured.
#[derive(Debug, Clone)]
pub struct PluginSessionOptions {
    /// The folders whose sub-folders are plugins, in search order.
    pub roots: Vec<PathBuf>,
    /// The session document's `plugins`: `None` loads every plugin found
    /// (in discovery order), an empty list loads none, otherwise exactly
    /// these ids in this order.
    pub allow_list: Option<Vec<String>>,
    /// The session document's `pluginSettings`.
    pub settings: PluginSessionSettings,
    /// This client's version, for the manifests' version fields.
    pub host_version: HostVersion,
    /// Where plugin storage lives on this computer.
    pub storage: crate::storage::StorageRoots,
    /// The slow log's threshold, wall time (off when `None`): every plugin
    /// call that takes longer is reported ([`PluginReport::SlowCall`]) with
    /// its entry call and imports. A diagnostic,
    /// [`crate::watchdog::SLOW_LOG_VARIABLE`].
    pub slow_log: Option<std::time::Duration>,
}

/// One loaded plugin.
#[derive(Debug)]
pub(crate) struct Loaded {
    pub(crate) manifest: PluginManifest,
    pub(crate) directory: PathBuf,
    pub(crate) instance: Instance,
}

/// A plugin this session was asked to run that is not running (it failed
/// to start, stopped with a fault, or a reload stopped after the running
/// copy was gone): where it lives, so the next attempt can find it.
#[derive(Debug, Clone)]
struct Offline {
    /// The id it is asked for by (its folder's name when its manifest was
    /// unreadable).
    id: String,
    directory: PathBuf,
    manifest: Option<PluginManifest>,
}

/// A new copy read and checked, not yet running.
struct Prepared {
    manifest: PluginManifest,
    module: crate::engine::PluginModule,
    checked: CheckedModule,
}

/// One session's plugins.
#[derive(Debug)]
pub struct PluginSession {
    engine: Arc<PluginEngine>,
    options: PluginSessionOptions,
    plugins: Vec<Loaded>,
    failures: Vec<(String, String)>,
    services: Services,
    reports: Vec<PluginReport>,
    events: VecDeque<Event>,
    /// Events for one plugin only, with its id.
    targeted: VecDeque<(String, Event)>,
    clock: TickClock,
    /// How many rounds of ticks the pumps have raised.
    ticks_raised: u64,
    /// The time the session's pumps have taken in, seconds.
    seconds: f64,
    slow_calls: SlowCalls,
    window_attached: bool,
    started: bool,
    /// The plugins are hearing lines a call just wrote ([`Self::settle`]);
    /// what those calls write in turn waits for the next settle.
    settling: bool,
    /// The objects each plugin enabled since the last pump heard replayed:
    /// a spawn of one of them queued meanwhile is not delivered twice.
    replayed: Vec<(String, Vec<u32>)>,
    /// The plugins asked for that are not running, by id.
    offline: Vec<Offline>,
    /// Reloads asked for (an id, or [`ALL_PLUGINS`]), carried out at the
    /// end of the next pump.
    requested_reloads: Vec<String>,
    /// The folders of the plugins this session runs or tried to.
    watch: FolderWatch,
}

fn same_id(a: &str, b: &str) -> bool {
    a.eq_ignore_ascii_case(b)
}

/// The roots without blanks and repeats (paths compared ignoring case on
/// Windows, as OpenAC compares them).
fn distinct_roots(roots: &[PathBuf]) -> Vec<PathBuf> {
    let mut seen: Vec<String> = Vec::new();
    let mut distinct = Vec::new();
    for root in roots {
        if root.as_os_str().is_empty() {
            continue;
        }
        let full = std::path::absolute(root).unwrap_or_else(|_| root.clone());
        let text = full.to_string_lossy();
        let key = if cfg!(windows) {
            text.to_lowercase()
        } else {
            text.into_owned()
        };
        if !seen.contains(&key) {
            seen.push(key);
            distinct.push(full);
        }
    }
    distinct
}

/// Candidates and errors gathered for one plugin id during discovery.
#[derive(Debug, Default)]
struct Found {
    id: String,
    candidates: Vec<Discovered>,
    errors: Vec<String>,
    /// Where a plugin refused before it was loaded lives (an unreadable
    /// manifest, a version this client does not meet), so a later change to
    /// its files can try it again.
    refused: Option<Offline>,
}

fn entry<'a>(found: &'a mut Vec<Found>, id: &str) -> &'a mut Found {
    if let Some(index) = found.iter().position(|f| same_id(&f.id, id)) {
        &mut found[index]
    } else {
        found.push(Found {
            id: id.to_owned(),
            ..Found::default()
        });
        found.last_mut().expect("just pushed")
    }
}

impl PluginSession {
    /// A session's plugin host, nothing loaded yet.
    #[must_use]
    pub fn new(engine: Arc<PluginEngine>, options: PluginSessionOptions) -> Self {
        Self {
            engine,
            options,
            plugins: Vec::new(),
            failures: Vec::new(),
            services: Services::default(),
            reports: Vec::new(),
            events: VecDeque::new(),
            targeted: VecDeque::new(),
            clock: TickClock::new(),
            ticks_raised: 0,
            seconds: 0.0,
            slow_calls: SlowCalls::default(),
            window_attached: false,
            started: false,
            settling: false,
            replayed: Vec::new(),
            offline: Vec::new(),
            requested_reloads: Vec::new(),
            watch: FolderWatch::default(),
        }
    }

    /// The loaded plugins' ids, in load order.
    #[must_use]
    pub fn loaded_ids(&self) -> Vec<String> {
        self.plugins.iter().map(|p| p.manifest.id.clone()).collect()
    }

    /// The wall time of plugin `id`'s last finished call, the client's
    /// work on its imports included; `None` when it is not loaded.
    #[must_use]
    pub fn last_call_time(&self, id: &str) -> Option<std::time::Duration> {
        self.plugins
            .iter()
            .find(|p| same_id(&p.manifest.id, id))
            .map(|p| p.instance.last_time())
    }

    /// The registered verbs of every plugin, for the help list.
    #[must_use]
    pub fn commands(&self) -> &CommandRegistry {
        &self.services.commands
    }

    /// What happened since the last call (loads and failures), oldest
    /// first.
    pub fn take_reports(&mut self) -> Vec<PluginReport> {
        std::mem::take(&mut self.reports)
    }

    /// Lines plugins submitted to the chat box, oldest first: the session
    /// routes each as a typed line.
    pub fn take_submits(&mut self) -> Vec<String> {
        std::mem::take(&mut self.services.submits)
    }

    /// Finds and loads the session's plugins (once).
    pub fn start(&mut self, api: &mut dyn SessionApi) {
        if self.started {
            return;
        }
        self.started = true;
        self.window_attached = api.window_attached();
        let requested: Option<Vec<String>> = self.options.allow_list.as_ref().map(|list| {
            let mut distinct: Vec<String> = Vec::new();
            for id in list {
                if !distinct.iter().any(|d| same_id(d, id)) {
                    distinct.push(id.clone());
                }
            }
            distinct
        });
        if requested.as_ref().is_some_and(Vec::is_empty) {
            return;
        }
        let found = self.discover(requested.as_deref(), api);
        let order: Vec<String> = match &requested {
            Some(ids) => ids.clone(),
            None => found.iter().map(|f| f.id.clone()).collect(),
        };
        let mut found = found;
        for id in order {
            let index = found.iter().position(|f| same_id(&f.id, &id));
            let item = index.map_or_else(
                || Found {
                    id: id.clone(),
                    ..Found::default()
                },
                |index| found.remove(index),
            );
            self.load_one(&id, item, None, api);
        }
        for loaded in &self.plugins {
            self.watch.watch(&loaded.directory, &loaded.manifest.id);
        }
        for offline in &self.offline {
            self.watch.watch(&offline.directory, &offline.id);
        }
    }

    /// Every plugin folder under the roots, grouped by id. `requested`
    /// limits it to those ids; then an incompatible plugin is an error for
    /// its id, else it is skipped silently.
    fn discover(&mut self, requested: Option<&[String]>, api: &mut dyn SessionApi) -> Vec<Found> {
        let wanted = |id: &str| requested.is_none_or(|ids| ids.iter().any(|r| same_id(r, id)));
        let mut found: Vec<Found> = Vec::new();
        for root in distinct_roots(&self.options.roots) {
            let results = match discovery::scan(&root) {
                Ok(results) => results,
                Err(error) => {
                    api.log(
                        MANAGER_ID,
                        LogLevel::Error,
                        &format!(
                            "plugin discovery failed for root '{}': {error}",
                            root.display()
                        ),
                    );
                    continue;
                }
            };
            for result in results {
                match &result.manifest {
                    Err(error) => {
                        let id = result.directory_name();
                        if id.trim().is_empty() || !wanted(&id) {
                            continue;
                        }
                        let error = error.clone();
                        let item = entry(&mut found, &id);
                        item.errors.push(error);
                        item.refused.get_or_insert_with(|| Offline {
                            id: id.clone(),
                            directory: result.directory.clone(),
                            manifest: None,
                        });
                    }
                    Ok(manifest) => {
                        let id = manifest.id.clone();
                        if !wanted(&id) {
                            continue;
                        }
                        if let Some(why) = incompatibility(manifest, self.options.host_version) {
                            if requested.is_some() {
                                let item = entry(&mut found, &id);
                                item.errors.push(format!("plugin '{id}' {why}."));
                                item.refused.get_or_insert_with(|| Offline {
                                    id: id.clone(),
                                    directory: result.directory.clone(),
                                    manifest: Some(manifest.clone()),
                                });
                            }
                            continue;
                        }
                        entry(&mut found, &id).candidates.push(result);
                    }
                }
            }
        }
        found
    }

    /// Loads the one plugin `id` from what discovery found, at `position`
    /// in the load order (the end when `None`), and reports the outcome.
    fn load_one(
        &mut self,
        id: &str,
        found: Found,
        position: Option<usize>,
        api: &mut dyn SessionApi,
    ) {
        let Found {
            mut candidates,
            mut errors,
            refused,
            ..
        } = found;
        let mut failed_at = refused;
        if candidates.len() > 1 {
            let folders: Vec<String> = candidates
                .iter()
                .map(|c| c.directory.display().to_string())
                .collect();
            errors.push(format!(
                "plugin '{id}' is declared in more than one plugin folder: {}.",
                folders.join(", ")
            ));
            candidates.clear();
            // Which folder is meant is the player's to settle; nothing
            // retries either copy.
            failed_at = None;
        }
        for candidate in candidates {
            if let Ok(manifest) = &candidate.manifest {
                failed_at = Some(Offline {
                    id: manifest.id.clone(),
                    directory: candidate.directory.clone(),
                    manifest: Some(manifest.clone()),
                });
            }
            match self.load_candidate(candidate, position, api) {
                Ok(manifest) => {
                    self.failures
                        .retain(|(failed, _)| !same_id(failed, &manifest.id));
                    self.offline.retain(|o| !same_id(&o.id, id));
                    api.log(
                        &manifest.id,
                        LogLevel::Info,
                        &format!("plugin loaded: {} ({})", manifest.id, manifest.display_name),
                    );
                    self.reports.push(PluginReport::Loaded {
                        id: manifest.id.clone(),
                    });
                    self.raise_login_if_in_stay(&manifest.id, api);
                    return;
                }
                Err(error) => errors.push(error),
            }
        }
        if let Some(mut at) = failed_at {
            id.clone_into(&mut at.id);
            self.set_offline(at);
        }
        if errors.is_empty() {
            errors.push(format!(
                "plugin '{id}' was not found in the configured plugin roots."
            ));
        }
        let text = errors.join(" | ");
        api.log(id, LogLevel::Warn, &format!("plugin failed: {id}: {text}"));
        self.record_failure(id, text);
    }

    fn record_failure(&mut self, id: &str, error: String) {
        self.failures.retain(|(failed, _)| !same_id(failed, id));
        self.failures.push((id.to_owned(), error.clone()));
        self.reports.push(PluginReport::Failed {
            id: id.to_owned(),
            error,
        });
    }

    /// Checks, instantiates and enables one candidate. On failure nothing of
    /// it stays registered.
    fn load_candidate(
        &mut self,
        candidate: Discovered,
        position: Option<usize>,
        api: &mut dyn SessionApi,
    ) -> Result<PluginManifest, String> {
        let manifest = candidate.manifest?;
        check_api_version(&manifest)?;
        let prepared = self.prepare(&candidate.directory, manifest)?;
        let manifest = prepared.manifest.clone();
        self.enable_prepared(prepared, candidate.directory, position, api)?;
        Ok(manifest)
    }

    /// Reads and checks a plugin's code, before anything of it runs. The
    /// module is read into memory, so nothing in the plugin's folder is held
    /// open.
    fn prepare(
        &self,
        directory: &std::path::Path,
        manifest: PluginManifest,
    ) -> Result<Prepared, String> {
        let module = self.engine.module(&directory.join(&manifest.entry))?;
        let checked = check_module(&module, &manifest)?;
        Ok(Prepared {
            manifest,
            module,
            checked,
        })
    }

    /// Instantiates and enables a prepared plugin at `position` in the load
    /// order (the end when `None`); it then hears every object already in
    /// the world and every service the plugins were told is on offer. On
    /// failure nothing of it stays registered.
    fn enable_prepared(
        &mut self,
        prepared: Prepared,
        directory: PathBuf,
        position: Option<usize>,
        api: &mut dyn SessionApi,
    ) -> Result<(), String> {
        let Prepared {
            manifest,
            module,
            checked,
        } = prepared;
        let instance = Instance::new(
            &self.engine,
            &module,
            &checked.imports,
            manifest.memory_mib,
            self.options.slow_log,
        )?;
        let index = position
            .unwrap_or(self.plugins.len())
            .min(self.plugins.len());
        // The plugin's icon.png, read as it starts so the windows it
        // registers carry it to the dock (OpenAC reads it as each window
        // mounts); a reload reads it again.
        if let Some(ui) = api.plugin_ui() {
            ui.set_file_icon(&manifest.id, crate::icon::load_icon(&directory));
            // Where the file icons of the windows it registers are read.
            ui.set_plugin_folder(&manifest.id, Some(directory.clone()));
        }
        self.plugins.insert(
            index,
            Loaded {
                manifest: manifest.clone(),
                directory,
                instance,
            },
        );
        let start = StartInfo {
            plugin_id: manifest.id.clone(),
            api_version: API_CURRENT.to_string(),
            client_version: self.options.host_version.to_string(),
        }
        .to_bytes_at(manifest.api_version);
        match self.invoke(index, api, |instance, handler| {
            instance.enable(&start, handler)
        }) {
            Ok(Ok(())) => {
                // A plugin enabled now hears every object already in the
                // world, once (OpenAC `RuntimeWorldEntityProjection.Subscribe`).
                let entities = api.entities();
                let ids: Vec<u32> = entities.iter().map(|e| e.id).collect();
                // Then every service the others were told is on offer; the
                // offers and withdrawals not yet told reach it with theirs.
                let services = self
                    .services
                    .plugin_services
                    .told()
                    .iter()
                    .cloned()
                    .map(Event::ServiceChanged)
                    .filter(|event| hears(&self.plugins[index].manifest, event));
                let replay: Vec<Event> = entities
                    .into_iter()
                    .map(Event::EntitySpawned)
                    .chain(services.collect::<Vec<_>>())
                    .collect();
                for event in replay {
                    let (entry, bytes) = encode(&event, self.version_of(index));
                    if let Err(fault) = self.invoke(index, api, |instance, handler| {
                        instance.event(&bytes, entry, handler)
                    }) {
                        self.remove(index, api);
                        return Err(fault);
                    }
                }
                self.replayed.push((manifest.id, ids));
                Ok(())
            }
            Ok(Err(refusal)) => {
                // As OpenAC: a plugin that refused to start is stopped, and
                // whatever it registered is released.
                let _ = self.invoke(index, api, Instance::disable);
                self.remove(index, api);
                Err(refusal)
            }
            Err(fault) => {
                self.remove(index, api);
                Err(fault)
            }
        }
    }

    /// A plugin started while the character is in the world hears the
    /// login it missed, so a plugin that sets up on login needs nothing
    /// special for a reload.
    fn raise_login_if_in_stay(&mut self, id: &str, api: &mut dyn SessionApi) {
        if !api.in_stay() {
            return;
        }
        if let Some(index) = self.index_of(id) {
            let (entry, bytes) = encode(&Event::LoginComplete, self.version_of(index));
            self.call(index, api, |instance, handler| {
                instance.event(&bytes, entry, handler)
            });
            self.settle(api);
        }
    }

    /// Remembers where a plugin that is not running lives.
    fn set_offline(&mut self, offline: Offline) {
        self.offline.retain(|o| !same_id(&o.id, &offline.id));
        self.offline.push(offline);
    }

    /// Runs one call into plugin `index`, carrying out its imports.
    fn invoke<T>(
        &mut self,
        index: usize,
        api: &mut dyn SessionApi,
        call: impl FnOnce(&mut Instance, &mut ImportHandler<'_>) -> Result<T, String>,
    ) -> Result<T, String> {
        let Self {
            plugins,
            services,
            options,
            reports,
            seconds,
            slow_calls,
            ..
        } = self;
        let (before, rest) = plugins.split_at_mut(index);
        let Some((caller, after)) = rest.split_first_mut() else {
            return Err("no such plugin".to_owned());
        };
        let Loaded {
            manifest,
            instance,
            directory,
        } = caller;
        let manifest: &PluginManifest = manifest;
        let directory: &std::path::Path = directory;
        let mut others = Others { before, after };
        let mut cx = Context {
            api,
            services,
            settings: &options.settings,
            storage_roots: &options.storage,
            lent: std::time::Duration::ZERO,
        };
        let answers = instance.answers_callbacks();
        let mut handler = |import: Import, request: &[u8]| {
            cx.lent = std::time::Duration::ZERO;
            let reply = handle_import(
                import,
                request,
                manifest,
                directory,
                answers,
                &mut cx,
                Some(&mut others),
            )?;
            Ok(Handled {
                reply,
                lent: cx.lent,
            })
        };
        let result = call(instance, &mut handler);
        // The slow calls: this one, and those of the plugins it asked
        // something. They keep running.
        for loaded in plugins.iter_mut() {
            for slow in loaded.instance.take_slow_calls() {
                slow_calls.report(
                    *seconds,
                    &loaded.manifest.id,
                    &loaded.manifest.display_name,
                    &slow,
                    cx.api,
                );
            }
        }
        // The calls over the slow log's threshold: this one, and those of
        // the plugins it asked something.
        if options.slow_log.is_some() {
            for loaded in plugins.iter_mut() {
                for message in loaded.instance.take_slow_notes() {
                    reports.push(PluginReport::SlowCall {
                        id: loaded.manifest.id.clone(),
                        message,
                    });
                }
            }
        }
        result
    }

    /// Unloads the plugins that faulted while answering another plugin's
    /// question, now that the asking call has returned.
    fn unload_faulted(&mut self, api: &mut dyn SessionApi) {
        for (id, reason) in std::mem::take(&mut self.services.faulted) {
            let Some(index) = self
                .plugins
                .iter()
                .position(|p| same_id(&p.manifest.id, &id))
            else {
                continue;
            };
            self.stopped_by_fault(index, api);
            let error = format!("plugin '{id}' stopped: {reason}");
            api.log(&id, LogLevel::Error, &error);
            self.record_failure(&id, error);
        }
    }

    /// [`Self::invoke`], unloading the plugin on a fault; true when it is
    /// still loaded.
    fn call(
        &mut self,
        index: usize,
        api: &mut dyn SessionApi,
        call: impl FnOnce(&mut Instance, &mut ImportHandler<'_>) -> Result<(), String>,
    ) -> bool {
        match self.invoke(index, api, call) {
            Ok(()) => true,
            Err(reason) => {
                let id = self.plugins[index].manifest.id.clone();
                self.stopped_by_fault(index, api);
                let error = format!("plugin '{id}' stopped: {reason}");
                api.log(&id, LogLevel::Error, &error);
                self.record_failure(&id, error);
                false
            }
        }
    }

    /// Drops plugin `index` and releases everything it registered, here
    /// and in the session.
    fn remove(&mut self, index: usize, api: &mut dyn SessionApi) -> Loaded {
        let loaded = self.plugins.remove(index);
        self.services.release(&loaded.manifest.id);
        api.released(&loaded.manifest.id);
        if let Some(ui) = api.plugin_ui() {
            ui.release(&loaded.manifest.id);
        }
        loaded
    }

    /// Unloads plugin `index` after a fault; it is tried again when its
    /// files change or the player reloads it.
    fn stopped_by_fault(&mut self, index: usize, api: &mut dyn SessionApi) {
        let loaded = self.remove(index, api);
        self.set_offline(Offline {
            id: loaded.manifest.id.clone(),
            directory: loaded.directory,
            manifest: Some(loaded.manifest),
        });
    }

    /// Stops plugin `index` and unloads it; a fault while stopping is
    /// logged, and its registrations are released regardless.
    fn disable_at(&mut self, index: usize, api: &mut dyn SessionApi) {
        let id = self.plugins[index].manifest.id.clone();
        if let Err(reason) = self.invoke(index, api, Instance::disable) {
            api.log(
                &id,
                LogLevel::Error,
                &format!("plugin disable failed: {id}: {reason}"),
            );
        }
        self.remove(index, api);
    }

    /// Stops every plugin, the last loaded first.
    pub fn shutdown(&mut self, api: &mut dyn SessionApi) {
        for index in (0..self.plugins.len()).rev() {
            self.disable_at(index, api);
        }
    }

    /// Asks for a plugin to be reloaded, or started again after it failed,
    /// at the end of the next pump: by its id, or every plugin this session
    /// was asked to run for [`ALL_PLUGINS`]. What came of it is written to
    /// chat.
    pub fn request_reload(&mut self, id_or_all: &str) {
        let id = id_or_all.trim();
        if !id.is_empty() {
            self.requested_reloads.push(id.to_owned());
        }
    }

    /// Whether a change to plugin `id`'s files has been seen and is waiting
    /// for them to go quiet.
    #[must_use]
    pub fn has_pending_file_change(&self, id: &str) -> bool {
        self.watch.has_pending(id)
    }

    /// The reloads due now: those asked for, and the plugins whose files
    /// changed and have been quiet since.
    fn reload_requested(&mut self, quiet: Vec<String>, api: &mut dyn SessionApi) {
        let mut due = quiet;
        due.append(&mut self.requested_reloads);
        if due.is_empty() {
            return;
        }
        let mut ids: Vec<String> = Vec::new();
        for request in due {
            let named: Vec<String> = if request.eq_ignore_ascii_case(ALL_PLUGINS) {
                self.plugins
                    .iter()
                    .map(|p| p.manifest.id.clone())
                    .chain(self.offline.iter().map(|o| o.id.clone()))
                    .collect()
            } else {
                vec![request]
            };
            for id in named {
                if !ids.iter().any(|known| same_id(known, &id)) {
                    ids.push(id);
                }
            }
        }
        for id in ids {
            self.reload(&id, api);
        }
        self.unload_faulted(api);
    }

    /// Replaces a running plugin with the copy now on disk, or starts one
    /// that is not running. The new copy is read and checked first, so an
    /// update this client cannot run leaves the running copy alone; then the
    /// old copy is stopped and everything it registered is released, and
    /// the new copy is enabled in its place.
    fn reload(&mut self, id: &str, api: &mut dyn SessionApi) {
        let index = self.index_of(id);
        let (directory, expected_id, name) = if let Some(index) = index {
            let loaded = &self.plugins[index];
            (
                loaded.directory.clone(),
                loaded.manifest.id.clone(),
                loaded.manifest.display_name.clone(),
            )
        } else if let Some(offline) = self.offline.iter().find(|o| same_id(&o.id, id)) {
            (
                offline.directory.clone(),
                offline.id.clone(),
                offline
                    .manifest
                    .as_ref()
                    .map_or_else(|| offline.id.clone(), |m| m.display_name.clone()),
            )
        } else {
            say(api, &format!("No plugin '{id}' is running in this client."));
            return;
        };
        let keeps = if index.is_some() {
            " The running copy stays."
        } else {
            ""
        };

        let next = match std::fs::read_to_string(directory.join(discovery::MANIFEST_FILE))
            .map_err(|error| error.to_string())
            .and_then(|text| PluginManifest::parse(&text).map_err(|error| error.0))
        {
            Ok(next) => next,
            Err(error) => {
                say(
                    api,
                    &format!(
                        "{name} was not reloaded: its {} could not be read ({error}).{keeps}",
                        discovery::MANIFEST_FILE
                    ),
                );
                return;
            }
        };
        if let Some(refusal) = self.refuse_replacement(&expected_id, &next) {
            say(
                api,
                &format!("{name} {} was not loaded: {refusal}.{keeps}", next.version),
            );
            return;
        }
        let prepared = match self.prepare(&directory, next) {
            Ok(prepared) => prepared,
            Err(error) => {
                say(
                    api,
                    &format!(
                        "{name} was not reloaded: the new copy could not be loaded ({error}).{keeps}"
                    ),
                );
                return;
            }
        };

        if let Some(index) = index {
            self.disable_at(index, api);
        }
        let position = index.unwrap_or(self.plugins.len());
        self.activate(prepared, directory, position, &expected_id, api);
    }

    /// Why a new copy of a plugin cannot replace the running one in this
    /// client, or `None` when it can.
    fn refuse_replacement(&self, expected_id: &str, next: &PluginManifest) -> Option<String> {
        if !same_id(expected_id, &next.id) {
            return Some(format!(
                "its new {} names a different plugin ('{}'); restart the client to load it",
                discovery::MANIFEST_FILE,
                next.id
            ));
        }
        if check_api_version(next).is_err() {
            return Some(format!(
                "it needs plugin API version {}, and this client supports {} to {}; it needs a \
                 newer client",
                next.api_version,
                ac_plugin_abi::API_MINIMUM,
                API_CURRENT
            ));
        }
        incompatibility(next, self.options.host_version)
            .map(|why| format!("it {why}; it needs a newer client or a restart"))
    }

    /// Enables the new copy at `position`; if it fails to start, the plugin
    /// is off until its files change again or the player reloads it.
    fn activate(
        &mut self,
        prepared: Prepared,
        directory: PathBuf,
        position: usize,
        requested_id: &str,
        api: &mut dyn SessionApi,
    ) {
        let manifest = prepared.manifest.clone();
        match self.enable_prepared(prepared, directory.clone(), Some(position), api) {
            Ok(()) => {
                self.offline.retain(|o| !same_id(&o.id, requested_id));
                self.failures
                    .retain(|(failed, _)| !same_id(failed, &manifest.id));
                self.watch.watch(&directory, &manifest.id);
                say(
                    api,
                    &format!("Reloaded {} {}.", manifest.display_name, manifest.version),
                );
                self.reports.push(PluginReport::Loaded {
                    id: manifest.id.clone(),
                });
                self.raise_login_if_in_stay(&manifest.id, api);
            }
            Err(reason) => {
                say(
                    api,
                    &format!(
                        "{} {} failed to start ({reason}). It is off until its files change \
                         again or /{MANAGER_VERB} reload {}.",
                        manifest.display_name, manifest.version, manifest.id
                    ),
                );
                self.set_offline(Offline {
                    id: requested_id.to_owned(),
                    directory,
                    manifest: Some(manifest.clone()),
                });
                self.record_failure(&manifest.id, reason);
            }
        }
    }

    /// A window attached to (or left) the session: plugins hear about it
    /// with the next events.
    pub fn set_window_attached(&mut self, attached: bool) {
        if self.window_attached != attached {
            self.window_attached = attached;
            self.events.push_back(Event::WindowAttached(attached));
        }
    }

    /// Something happened that plugins hear about with the next events (in
    /// the order queued).
    pub fn queue_event(&mut self, event: Event) {
        if !self.plugins.is_empty() {
            self.events.push_back(event);
        }
    }

    /// Something only plugin `owner` hears about, with the next events
    /// (dropped when it is not loaded then).
    pub fn queue_event_for(&mut self, owner: &str, event: Event) {
        if self.index_of(owner).is_some() {
            self.targeted.push_back((owner.to_owned(), event));
        }
    }

    /// A chat line arrived: plugins with the `chat` permission hear about
    /// it with the next events.
    pub fn queue_chat(&mut self, line: ChatLine) {
        if !self.plugins.is_empty() {
            self.events.push_back(Event::ChatReceived(line));
        }
    }

    /// How many rounds of ticks the pumps have raised so far (a round ticks
    /// every loaded plugin once, after the events queued before it).
    #[must_use]
    pub fn ticks_raised(&self) -> u64 {
        self.ticks_raised
    }

    /// Hands out the queued events, then the ticks due after
    /// `delta_seconds` more of session time in run `generation`.
    pub fn pump(&mut self, delta_seconds: f64, generation: Option<u64>, api: &mut dyn SessionApi) {
        self.deliver_ui(api);
        self.deliver_net(api);
        self.deliver(api);
        if delta_seconds.is_finite() && delta_seconds > 0.0 {
            self.seconds += delta_seconds;
        }
        let due = self.clock.feed(delta_seconds, generation);
        self.ticks_raised += u64::from(due);
        for _ in 0..due {
            for id in self.ids(false) {
                if let Some(index) = self.index_of(&id) {
                    self.call(index, api, Instance::tick);
                    self.settle(api);
                }
            }
        }
        self.unload_faulted(api);
        let quiet = self.watch.advance(delta_seconds);
        self.reload_requested(quiet, api);
    }

    /// Hands out the queued events now, without the tick (the session
    /// announcing something that cannot wait for its next pass, such as
    /// the character leaving the world before the session is torn down).
    pub fn deliver(&mut self, api: &mut dyn SessionApi) {
        loop {
            self.queue_service_changes();
            let Some(event) = self.events.pop_front() else {
                break;
            };
            let spawned = match &event {
                Event::EntitySpawned(entity) => Some(entity.id),
                Event::ServiceChanged(change) => {
                    self.services.plugin_services.told_of(change);
                    None
                }
                _ => None,
            };
            let entry = Entry::of_event(&event);
            let mut encodings = Encodings::new(&event);
            for id in self.listeners(&event) {
                // A spawn the plugin already heard replayed at its enable.
                if let Some(object) = spawned
                    && self
                        .replayed
                        .iter()
                        .any(|(p, ids)| same_id(p, &id) && ids.contains(&object))
                {
                    continue;
                }
                if let Some(index) = self.index_of(&id) {
                    let bytes = encodings.at(self.version_of(index));
                    self.call(index, api, |instance, handler| {
                        instance.event(bytes, entry, handler)
                    });
                    self.settle(api);
                }
            }
        }
        while let Some((owner, event)) = self.targeted.pop_front() {
            if let Some(index) = self.index_of(&owner)
                && hears(&self.plugins[index].manifest, &event)
            {
                let (entry, bytes) = encode(&event, self.version_of(index));
                self.call(index, api, |instance, handler| {
                    instance.event(&bytes, entry, handler)
                });
                self.settle(api);
            }
        }
        self.replayed.clear();
        self.unload_faulted(api);
    }

    /// The services offered and withdrawn since the last look join the
    /// events, in the order they happened: every plugin built for API 1.6
    /// or later hears each (a plugin's own too). Queued even with no plugin
    /// loaded, so what the plugins were told stays true.
    fn queue_service_changes(&mut self) {
        for change in self.services.plugin_services.take_changes() {
            self.events.push_back(Event::ServiceChanged(change));
        }
    }

    /// The loaded plugins' ids in load order (`chat`: only those allowed
    /// to hear chat), taken before a round of calls so a plugin unloaded
    /// during the round is passed over and none is skipped.
    fn ids(&self, chat: bool) -> Vec<String> {
        self.plugins
            .iter()
            .filter(|p| !chat || p.manifest.allows(Permission::Chat))
            .map(|p| p.manifest.id.clone())
            .collect()
    }

    /// [`Self::ids`] of the plugins that hear `event` ([`hears`]).
    fn listeners(&self, event: &Event) -> Vec<String> {
        self.plugins
            .iter()
            .filter(|p| hears(&p.manifest, event))
            .map(|p| p.manifest.id.clone())
            .collect()
    }

    /// The API version loaded plugin `index` was built for (its manifest's).
    fn version_of(&self, index: usize) -> ApiVersion {
        self.plugins[index].manifest.api_version
    }

    fn index_of(&self, id: &str) -> Option<usize> {
        self.plugins
            .iter()
            .position(|p| same_id(&p.manifest.id, id))
    }

    /// Right after a plugin call returns: the lines the plugins submitted
    /// are carried out, and the chat lines and status notices that appeared
    /// since (their own posts, what the submits wrote locally) reach every
    /// plugin allowed to hear chat before the next call. OpenAC hands such
    /// a line to the chat subscribers inside the call that wrote it; a wasm
    /// instance cannot be re-entered, so this is the nearest point. Lines
    /// written while hearing these wait for the next settle (or the
    /// session's next pass), so a plugin answering its own lines cannot
    /// loop within one.
    fn settle(&mut self, api: &mut dyn SessionApi) {
        if self.settling {
            return;
        }
        api.submit_now(&mut self.services.submits);
        let events = api.new_chat_events();
        if events.is_empty() {
            return;
        }
        self.settling = true;
        for event in events {
            let entry = Entry::of_event(&event);
            let mut encodings = Encodings::new(&event);
            for id in self.listeners(&event) {
                if let Some(index) = self.index_of(&id) {
                    let bytes = encodings.at(self.version_of(index));
                    self.call(index, api, |instance, handler| {
                        instance.event(bytes, entry, handler)
                    });
                }
            }
        }
        self.settling = false;
    }

    /// Whether any plugin intercepts typed lines.
    #[must_use]
    pub fn intercepts_input(&self) -> bool {
        !self.services.interceptors.is_empty()
    }

    /// Asks the interceptors, in registration order across every plugin,
    /// about a line the player sent; the first that does not pass decides
    /// (OpenAC `IPluginCommandBus.InterceptChatInput`). An interceptor
    /// whose plugin has no callback export, or answers something else,
    /// passes; one that faults is unloaded and passes.
    pub fn intercept(&mut self, typed: &str, api: &mut dyn SessionApi) -> InterceptDecision {
        let mut decision = InterceptDecision::Pass;
        for (owner, handle) in self.services.interceptors.ordered() {
            let Some(index) = self
                .plugins
                .iter()
                .position(|p| same_id(&p.manifest.id, &owner))
            else {
                continue;
            };
            let callback = Callback::ChatIntercept((handle, typed.to_owned()));
            let version = self.version_of(index);
            let (entry, bytes) = (Entry::of_callback(&callback), callback.to_bytes_at(version));
            let mut answer = None;
            self.call(index, api, |instance, handler| {
                answer = instance.callback(&bytes, entry, handler)?;
                Ok(())
            });
            if let Some(CallbackAnswer::Intercept(d)) =
                answer.and_then(|a| CallbackAnswer::from_bytes_at(&a, version).ok())
                && d != InterceptDecision::Pass
            {
                decision = d;
                break;
            }
        }
        self.unload_faulted(api);
        decision
    }

    /// Whether any plugin filters chat.
    #[must_use]
    pub fn filters_chat(&self) -> bool {
        !self.services.filters.is_empty()
    }

    /// Asks the chat filters, in registration order across every plugin,
    /// whether to drop a line (or status notice) before the transcript
    /// keeps it; the first that drops it decides (OpenAC
    /// `ChatSuppressionFilters.ShouldSuppress`). A filter whose plugin
    /// answers anything but yes or no keeps the line; one that faults is
    /// unloaded and keeps it ("a throwing filter suppresses nothing").
    pub fn filter_chat(&mut self, line: &ChatLine, api: &mut dyn SessionApi) -> bool {
        let mut suppress = false;
        for (owner, handle) in self.services.filters.ordered() {
            let Some(index) = self
                .plugins
                .iter()
                .position(|p| same_id(&p.manifest.id, &owner))
            else {
                continue;
            };
            let callback = Callback::ChatFilter((handle, line.clone()));
            let version = self.version_of(index);
            let (entry, bytes) = (Entry::of_callback(&callback), callback.to_bytes_at(version));
            let mut answer = None;
            self.call(index, api, |instance, handler| {
                answer = instance.callback(&bytes, entry, handler)?;
                Ok(())
            });
            if let Some(CallbackAnswer::Bool(true)) =
                answer.and_then(|a| CallbackAnswer::from_bytes_at(&a, version).ok())
            {
                suppress = true;
                break;
            }
        }
        self.unload_faulted(api);
        suppress
    }

    /// What the plugins' UI reported (refusals worth a log line) and what
    /// the player did with it (handlers, canvas pointer input), each to its
    /// own plugin only.
    fn deliver_ui(&mut self, api: &mut dyn SessionApi) {
        let Some(state) = api.plugin_ui() else {
            return;
        };
        let reports = ac_plugin_abi::ui_host::reports(state);
        let events = state.take_events();
        // A new plugin theme goes to every plugin, with the other events.
        if let Some(theme) = state.take_theme_change() {
            self.queue_event(Event::ThemeChanged(ac_plugin_abi::ui_host::theme_info(
                theme,
            )));
        }
        for (owner, line) in reports {
            api.log(&owner, LogLevel::Warn, &line);
        }
        for (owner, event) in events {
            let Some(index) = self
                .plugins
                .iter()
                .position(|p| same_id(&p.manifest.id, &owner))
            else {
                continue;
            };
            let event = match api.plugin_ui() {
                Some(state) => state.pointer_as_placed_now(event),
                None => event,
            };
            let event = ac_plugin_abi::ui_host::event(event);
            if !hears(&self.plugins[index].manifest, &event) {
                continue;
            }
            let (entry, bytes) = encode(&event, self.version_of(index));
            self.call(index, api, |instance, handler| {
                instance.event(&bytes, entry, handler)
            });
            self.settle(api);
        }
    }

    /// What the plugins' requests and sockets did, each to the plugin that
    /// made them.
    fn deliver_net(&mut self, api: &mut dyn SessionApi) {
        for (owner, event) in self.services.net.take_events() {
            let Some(index) = self
                .plugins
                .iter()
                .position(|p| same_id(&p.manifest.id, &owner))
            else {
                continue;
            };
            let (entry, bytes) = encode(&Event::Net(event), self.version_of(index));
            self.call(index, api, |instance, handler| {
                instance.event(&bytes, entry, handler)
            });
            self.settle(api);
        }
    }

    /// Every verb (lower case) a typed line can name to reach the plugins:
    /// their verbs and aliases, and the manager's `plugin`.
    #[must_use]
    pub fn claimed_verbs(&self) -> Vec<String> {
        let mut verbs = self.services.commands.names();
        verbs.push(MANAGER_VERB.to_owned());
        verbs
    }

    /// Whether a typed line is for the plugins: a plugin's verb, or the
    /// plugin manager's `/plugin`.
    #[must_use]
    pub fn claims_line(&self, raw: &str) -> bool {
        self.services.commands.is_plugin_line(raw) || is_manager_line(raw)
    }

    /// Carries out a typed line that names a plugin verb (the plugin gets
    /// the command now) or `/plugin`; false when it names neither.
    pub fn handle_line(&mut self, raw: &str, api: &mut dyn SessionApi) -> bool {
        if is_manager_line(raw) {
            let arguments = split_command_line(raw).map_or("", |(_, _, args)| args);
            self.manager(arguments, api);
            return true;
        }
        let Some((owner, call)) = self.services.commands.resolve(raw) else {
            return false;
        };
        let Some(index) = self
            .plugins
            .iter()
            .position(|p| same_id(&p.manifest.id, &owner))
        else {
            return false;
        };
        let (entry, bytes) = encode(&Event::Command(call), self.version_of(index));
        self.call(index, api, |instance, handler| {
            instance.event(&bytes, entry, handler)
        });
        self.settle(api);
        self.unload_faulted(api);
        true
    }

    /// `/plugin`, `/plugin list`, `/plugin reload <id>`.
    fn manager(&mut self, arguments: &str, api: &mut dyn SessionApi) {
        let (order, rest) = match arguments.split_once(char::is_whitespace) {
            Some((order, rest)) => (order, rest.trim()),
            None => (arguments, ""),
        };
        match order.to_ascii_lowercase().as_str() {
            "" | "list" => {
                if self.plugins.is_empty() && self.failures.is_empty() {
                    api.post_chat("No plugins are loaded.", 0);
                    return;
                }
                api.post_chat("Plugins:", 0);
                let lines: Vec<String> = self
                    .plugins
                    .iter()
                    .map(|p| {
                        format!(
                            "  {} {} ({}): loaded",
                            p.manifest.id, p.manifest.version, p.manifest.display_name
                        )
                    })
                    .chain(
                        self.failures
                            .iter()
                            .map(|(id, error)| format!("  {id}: failed: {error}")),
                    )
                    .collect();
                for line in lines {
                    api.post_chat(&line, 0);
                }
            }
            "allow" => {
                let mut words = rest.split_whitespace();
                let (Some(id), Some(host), None) = (words.next(), words.next(), words.next())
                else {
                    api.post_chat("Usage: /plugin allow <id> <host>", 0);
                    return;
                };
                if !self.plugins.iter().any(|p| same_id(&p.manifest.id, id)) {
                    api.post_chat(&format!("Plugin {id} is not loaded."), 0);
                    return;
                }
                self.services.approve(id, host);
                api.post_chat(&format!("Plugin {id} may reach {host} this session."), 0);
            }
            "reload" => {
                let mut words = rest.split_whitespace();
                match (words.next(), words.next()) {
                    (Some(id), None) => self.request_reload(id),
                    _ => say(
                        api,
                        &format!(
                            "Usage: /{MANAGER_VERB} reload <plugin id>, or /{MANAGER_VERB} \
                             reload {ALL_PLUGINS}"
                        ),
                    ),
                }
            }
            _ => api.post_chat(
                "Usage: /plugin [list] | /plugin reload <id> | /plugin reload all | /plugin \
                 allow <id> <host>",
                0,
            ),
        }
    }

    /// The folder plugin `id` was loaded from.
    #[must_use]
    pub fn directory_of(&self, id: &str) -> Option<&std::path::Path> {
        self.plugins
            .iter()
            .find(|p| same_id(&p.manifest.id, id))
            .map(|p| p.directory.as_path())
    }
}

/// An event's entry call (for reports) and its encoding as a plugin built
/// for API `version` reads it (a record field a later version added left
/// out).
fn encode(event: &Event, version: ApiVersion) -> (Entry, Vec<u8>) {
    (Entry::of_event(event), event.to_bytes_at(version))
}

/// One event's encodings for its listeners, made once per API version a
/// listener was built for.
struct Encodings<'a> {
    event: &'a Event,
    made: Vec<(ApiVersion, Vec<u8>)>,
}

impl<'a> Encodings<'a> {
    fn new(event: &'a Event) -> Self {
        Self {
            event,
            made: Vec::new(),
        }
    }

    /// The event as a plugin built for API `version` reads it.
    fn at(&mut self, version: ApiVersion) -> &[u8] {
        let at = if let Some(at) = self.made.iter().position(|(v, _)| *v == version) {
            at
        } else {
            self.made.push((version, self.event.to_bytes_at(version)));
            self.made.len() - 1
        };
        &self.made[at].1
    }
}

/// Whether the plugin of `manifest` is sent `event`: it was built for the
/// API version that added the event or a later one (an older SDK cannot
/// read an event kind it does not know, and stops the plugin), and a chat
/// line or status notice needs the `chat` permission. A canvas pointer
/// event of a kind a later version added (hover, leave) goes only to
/// plugins built for that version or later: an older plugin may read a kind
/// it does not know as a drag.
#[must_use]
pub fn hears(manifest: &PluginManifest, event: &Event) -> bool {
    let kind_known = match event {
        Event::Ui(UiEvent::Pointer(pointer)) => pointer.since().is_at_most(manifest.api_version),
        _ => true,
    };
    event.since().is_at_most(manifest.api_version)
        && kind_known
        && (!matches!(event, Event::ChatReceived(_) | Event::StatusTextReceived(_))
            || manifest.allows(Permission::Chat))
}

/// Writes to the player's chat and to the log: a reload is something the
/// player asked for or caused, so what came of it is said where they look.
fn say(api: &mut dyn SessionApi, text: &str) {
    api.log(MANAGER_ID, LogLevel::Info, text);
    api.post_chat(text, 0);
}

fn is_manager_line(raw: &str) -> bool {
    split_command_line(raw).is_some_and(|(_, verb, _)| verb.eq_ignore_ascii_case(MANAGER_VERB))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manifest(api_version: &str, chat: bool) -> PluginManifest {
        let permissions = if chat {
            r#", "permissionsVersion": 1, "permissions": [{ "name": "chat", "note": "n" }]"#
        } else {
            ""
        };
        PluginManifest::parse(&format!(
            r#"{{ "id": "x", "displayName": "X", "version": "0.1.0", "entry": "x.wasm", "apiVersion": "{api_version}"{permissions} }}"#
        ))
        .unwrap()
    }

    #[test]
    fn an_event_goes_only_to_plugins_built_for_its_api_version_or_later() {
        let status = Event::StatusTextReceived("busy".into());
        let chat = Event::ChatReceived(ChatLine::default());
        assert!(!hears(&manifest("1.0", true), &status));
        assert!(hears(&manifest("1.1", true), &status));
        // An API 1.0 event still reaches a 1.0 plugin.
        assert!(hears(&manifest("1.0", true), &chat));
        assert!(hears(&manifest("1.0", false), &Event::LoginComplete));
        // Like a chat line, a status notice needs the chat permission.
        assert!(!hears(&manifest("1.1", false), &status));
        assert!(!hears(&manifest("1.1", false), &chat));
        assert!(hears(&manifest("1.1", false), &Event::LoginComplete));
    }

    #[test]
    fn a_canvas_hover_or_leave_goes_only_to_plugins_built_for_api_1_6() {
        use ac_plugin_abi::ui::PointerEvent;
        let pointer = |kind| {
            Event::Ui(UiEvent::Pointer(PointerEvent {
                kind,
                ..PointerEvent::default()
            }))
        };
        for kind in [PointerEvent::HOVER, PointerEvent::LEAVE] {
            assert!(!hears(&manifest("1.5", false), &pointer(kind)));
            assert!(hears(&manifest("1.6", false), &pointer(kind)));
        }
        for kind in [
            PointerEvent::DOWN,
            PointerEvent::UP,
            PointerEvent::MOVE,
            PointerEvent::WHEEL,
            PointerEvent::CANCELLED,
        ] {
            assert!(hears(&manifest("1.0", false), &pointer(kind)));
        }
    }

    // An object change carries the world object; API 1.2 gave that record
    // a field, so a plugin built for API 1.1 is sent the event without it,
    // and a plugin of this version the event as it is. Each version's
    // bytes are made once.
    #[test]
    fn an_event_reaches_each_plugin_as_its_api_version_reads_it() {
        use ac_plugin_abi::world::{ObjectChange, WorldObject};
        let event = Event::ObjectChanged(ObjectChange {
            object_id: 7,
            current: Some(WorldObject {
                object_id: 7,
                hook_item_types: 0x80,
                ..WorldObject::default()
            }),
            ..ObjectChange::default()
        });
        let v1_1 = ApiVersion::new(1, 1);
        let mut encodings = Encodings::new(&event);
        assert_eq!(encodings.at(API_CURRENT), event.to_bytes().as_slice());
        let older = encodings.at(v1_1).to_vec();
        assert_eq!(older.len() + 4, event.to_bytes().len());
        assert_eq!(encodings.at(v1_1), older.as_slice());
        assert_eq!(encodings.made.len(), 2);
        let Ok(Event::ObjectChanged(read)) = Event::from_bytes_at(&older, v1_1) else {
            panic!("an API 1.1 object change");
        };
        assert_eq!(
            read.current.map(|o| (o.object_id, o.hook_item_types)),
            Some((7, 0))
        );
        assert_eq!(encode(&event, v1_1).1, older);
    }
}
