//! Carrying out one import for a plugin: the plugin host's own services
//! (log, settings, verbs, chat lines, the registries plugins share) here,
//! everything about the game through [`SessionApi::call`].
//!
//! A call that asks another plugin something it registered (a loot
//! classifier, a service) calls that plugin's callback export directly: the other
//! plugin is idle (only the caller is inside a call), and the callback's
//! own imports are served like any other, except that they cannot ask a
//! third plugin. A plugin that faults while answering is unloaded once the
//! caller's call returns ([`Services::faulted`]). The answer's time is the
//! answering plugin's ([`Context::lent`]), not the caller's.

use std::time::{Duration, Instant};

use ac_plugin_abi::callback::{Callback, CallbackAnswer, LootQuestion};
use ac_plugin_abi::items::{
    ClassifierRegistration, ClassifyRequest, LootClassification, LootedNotice, RemovedNotice,
};
use ac_plugin_abi::services::{
    MAX_PAYLOAD, ServiceCall, ServiceError, ServiceRegistration, ServiceRequest,
};
use ac_plugin_abi::storage::{StorageKey, StoragePlace, StorageWrite};
use ac_plugin_abi::{
    ChatPost, CommandCall, CommandCompletion, CommandDef, DecodeError, Import, LogRequest, Wire,
};

use crate::api::{Caller, SessionApi};
use crate::classifiers::ClassifierRegistry;
use crate::commands::CommandRegistry;
use crate::instance::Handled;
use crate::interceptors::{FilterRegistry, InterceptorRegistry};
use crate::manifest::PluginManifest;
use crate::services::ServiceRegistry;
use crate::session::Loaded;
use crate::settings::PluginSessionSettings;
use crate::status::StatusBoard;
use crate::storage::{self as files, StorageRoots, StorageService};

/// What the plugin host keeps for the session's plugins.
#[derive(Debug, Default)]
pub(crate) struct Services {
    /// The chat verbs.
    pub(crate) commands: CommandRegistry,
    /// Lines submitted to the chat box, for the session to route after the
    /// call.
    pub(crate) submits: Vec<String>,
    /// The loot classifiers.
    pub(crate) classifiers: ClassifierRegistry,
    /// The chat input interceptors.
    pub(crate) interceptors: InterceptorRegistry,
    /// The chat filters.
    pub(crate) filters: FilterRegistry,
    /// Storage's locks.
    pub(crate) storage: StorageService,
    /// The session's status board.
    pub(crate) status: StatusBoard,
    /// The services plugins offer one another.
    pub(crate) plugin_services: ServiceRegistry,
    /// The plugins' network.
    pub(crate) net: ac_plugin_net::NetService,
    /// Hosts the player approved this session: (plugin id, host).
    pub(crate) approvals: Vec<(String, String)>,
    /// Hosts a plugin already asked the player about.
    pub(crate) asked: Vec<(String, String)>,
    /// Plugins that faulted while answering another plugin's question:
    /// unloaded once the caller's call returns.
    pub(crate) faulted: Vec<(String, String)>,
}

impl Services {
    /// The player lets plugin `id` reach `host` this session.
    pub(crate) fn approve(&mut self, id: &str, host: &str) {
        let host = host.to_lowercase();
        if !self
            .approvals
            .iter()
            .any(|(p, h)| p.eq_ignore_ascii_case(id) && *h == host)
        {
            self.approvals.push((id.to_lowercase(), host));
        }
    }

    /// The hosts plugin `manifest` may reach: its manifest's and the
    /// ones the player approved.
    fn allowed_hosts(&self, manifest: &PluginManifest) -> Vec<String> {
        manifest
            .permissions
            .iter()
            .filter(|p| p.permission == ac_plugin_abi::Permission::Network)
            .flat_map(|p| p.hosts.iter().map(|h| h.to_lowercase()))
            .chain(
                self.approvals
                    .iter()
                    .filter(|(p, _)| p.eq_ignore_ascii_case(&manifest.id))
                    .map(|(_, h)| h.clone()),
            )
            .collect()
    }
}

/// Whether plugin `manifest` may reach `host` now; when not, the player is
/// asked once (a line in the transcript naming the command that approves
/// it) and the refusal names it too.
fn reach(
    services: &mut Services,
    api: &mut dyn SessionApi,
    manifest: &PluginManifest,
    host: &str,
) -> Result<Vec<String>, String> {
    let allowed = services.allowed_hosts(manifest);
    if ac_plugin_net::host_allowed(host, &allowed) {
        return Ok(allowed);
    }
    let id = manifest.id.as_str();
    let ask = (id.to_lowercase(), host.to_lowercase());
    if !services.asked.contains(&ask) {
        services.asked.push(ask);
        api.post_chat(
            &format!(
                "Plugin {} ({id}) asks to reach {host}. Type /plugin allow {id} {host} to let it.",
                manifest.display_name
            ),
            0,
        );
    }
    Err(format!(
        "{host} is not one of the plugin's hosts; the player can approve it with /plugin allow {id} {host}"
    ))
}

impl Services {
    /// Everything plugin `id` registered goes.
    pub(crate) fn release(&mut self, id: &str) {
        self.commands.release(id);
        self.classifiers.release(id);
        self.interceptors.release(id);
        self.filters.release(id);
        self.storage.release(id);
        self.status.release(id);
        self.plugin_services.release(id);
        self.net.release(id);
    }

    /// Plugin `id` failed answering another plugin's question: it is
    /// unloaded once the asking call returns, and until then its services
    /// are gone and nothing asks it anything again. Noted once.
    fn fault(&mut self, id: &str, reason: String) {
        if self.has_faulted(id) {
            return;
        }
        self.plugin_services.release(id);
        self.faulted.push((id.to_owned(), reason));
    }

    /// Whether plugin `id` failed answering a question and waits to be
    /// unloaded.
    pub(crate) fn has_faulted(&self, id: &str) -> bool {
        self.faulted
            .iter()
            .any(|(faulted, _)| faulted.eq_ignore_ascii_case(id))
    }
}

/// The plugins other than the caller, which a call may ask something.
pub(crate) struct Others<'a> {
    pub(crate) before: &'a mut [Loaded],
    pub(crate) after: &'a mut [Loaded],
}

impl Others<'_> {
    fn find(&mut self, id: &str) -> Option<&mut Loaded> {
        self.before
            .iter_mut()
            .chain(self.after.iter_mut())
            .find(|p| p.manifest.id.eq_ignore_ascii_case(id))
    }
}

/// A request a plugin made that does not decode: the contract is broken.
fn malformed(import: Import) -> impl Fn(DecodeError) -> String {
    move |error| format!("its request to {} is malformed: {error}", import.name())
}

/// The session's settings and the host's services, for one call.
pub(crate) struct Context<'a> {
    pub(crate) api: &'a mut dyn SessionApi,
    pub(crate) services: &'a mut Services,
    pub(crate) settings: &'a PluginSessionSettings,
    pub(crate) storage_roots: &'a StorageRoots,
    /// How long other plugins took to answer the questions the import being
    /// carried out asked them: their time, left out of the caller's
    /// ([`crate::instance::Handled::lent`]).
    pub(crate) lent: Duration,
}

/// Carries out one import for the plugin `manifest`, which can answer
/// callbacks when `answers` (it exports `openac_callback`). `others` is
/// none inside a callback (a plugin answering a question cannot ask a
/// third).
pub(crate) fn handle_import(
    import: Import,
    request: &[u8],
    manifest: &PluginManifest,
    directory: &std::path::Path,
    answers: bool,
    cx: &mut Context<'_>,
    others: Option<&mut Others<'_>>,
) -> Result<Vec<u8>, String> {
    let id = manifest.id.as_str();
    // What the plugin sends is read, and what it is answered written, as
    // the API version it was built for knows them, so a record field a later
    // version adds never reaches a plugin built before it.
    let version = manifest.api_version;
    if let Some(permission) = import.permission()
        && !manifest.allows(permission)
    {
        return Err(format!(
            "called {} without the '{}' permission",
            import.name(),
            permission.name()
        ));
    }
    let bad = malformed(import);
    let caller = files::Caller {
        manifest,
        directory,
    };
    let services = &mut *cx.services;
    Ok(match import {
        Import::AbiReplyRead | Import::AbiPanic => {
            return Err(format!("{} reached the client", import.name()));
        }
        Import::HostLog => {
            let line = LogRequest::from_bytes_at(request, version).map_err(bad)?;
            cx.api.log(id, line.level, &line.message);
            Vec::new()
        }
        Import::HostWindowAttached => cx.api.window_attached().to_bytes_at(version),
        Import::HostSetting => {
            let key = String::from_bytes_at(request, version).map_err(bad)?;
            cx.settings
                .get(id, &key)
                .map(str::to_owned)
                .to_bytes_at(version)
        }
        Import::HostSettings => cx.settings.all(id).to_bytes_at(version),
        Import::CommandsRegister => {
            let definition = CommandDef::from_bytes_at(request, version).map_err(bad)?;
            services
                .commands
                .register(id, &definition)
                .to_bytes_at(version)
        }
        Import::CommandsUnregister => {
            let verb = String::from_bytes_at(request, version).map_err(bad)?;
            services.commands.unregister(id, &verb).to_bytes_at(version)
        }
        Import::CommandsDefinitions => services.commands.definitions().to_bytes_at(version),
        Import::CommandsHelp => services.commands.help().to_bytes_at(version),
        Import::CommandsComplete => {
            let mut call = CommandCall::from_bytes_at(request, version).map_err(bad)?;
            let Some((owner, verb)) = services
                .commands
                .owner_of(&call.verb)
                .map(|(o, v)| (o.to_owned(), v.to_owned()))
            else {
                return Ok(Vec::<CommandCompletion>::new().to_bytes_at(version));
            };
            call.verb = verb;
            match ask_plugin(id, &owner, others, cx, &Callback::CommandComplete(call)) {
                Some(CallbackAnswer::Completions(completions)) => completions,
                _ => Vec::new(),
            }
            .to_bytes_at(version)
        }
        Import::ChatSubmit => {
            services
                .submits
                .push(String::from_bytes_at(request, version).map_err(bad)?);
            Vec::new()
        }
        Import::ChatPost => {
            let post = ChatPost::from_bytes_at(request, version).map_err(bad)?;
            cx.api.post_chat(&post.text, post.log_text_type);
            Vec::new()
        }
        // O. The plugin UI, session state the session owns.
        Import::UiPanelRegister
        | Import::UiPanelRemove
        | Import::UiPanelSet
        | Import::UiPanelShow
        | Import::UiViewExists
        | Import::UiViewVisible
        | Import::UiControlExists
        | Import::UiControlLabel
        | Import::UiControlVisible
        | Import::UiPanelRegisterTyped
        | Import::UiPanelSetTyped
        | Import::UiDialogConfirm
        | Import::UiDialogWithdraw
        | Import::UiPanelShowModal
        | Import::UiClientWindow
        | Import::UiCanvasRegister
        | Import::UiCanvasPaint
        | Import::UiCanvasSet
        | Import::UiCanvasState
        | Import::UiCanvasAvailable
        | Import::UiCanvasRemove
        | Import::UiMeasureText
        | Import::UiFontBundled
        | Import::UiFontLoad
        | Import::UiFontMeasure
        | Import::UiFontRelease
        | Import::UiFontCount
        | Import::UiTheme
        | Import::UiImageClientArt
        | Import::UiImageSpellIcon
        | Import::UiImageObjectIcon
        | Import::UiImageRgba
        | Import::UiImageDecode
        | Import::UiImageRelease
        | Import::UiImageCount
        | Import::UiLabelsShow
        | Import::UiLinesCreate
        | Import::UiLinesSet
        | Import::UiLinesRemove
        | Import::UiTrianglesCreate
        | Import::UiTrianglesSet
        | Import::UiTrianglesRemove
        | Import::UiMarkersCreate
        | Import::UiMarkersSetIcons
        | Import::UiMarkersSetShapes
        | Import::UiMarkersRemove
        | Import::UiProjectileDebug
        | Import::UiHotkeyRegister
        | Import::UiHotkeyUnregister
        | Import::UiHotkeyRebind
        | Import::UiHotkeyState
        | Import::UiInputActionsListen => {
            use ac_plugin_abi::ui_host::{WorldQuery, handle, world_query};
            let owner = ac_plugin_ui::UiOwner::new(id, manifest.display_name.as_str());
            let api = &mut *cx.api;
            let answer = match world_query(import, request) {
                Some(WorldQuery::SpellIcon(spell)) => api.spell_icon(spell),
                Some(WorldQuery::ObjectIcon(object)) => api.object_icon(object),
                None => None,
            };
            let Some(state) = api.plugin_ui() else {
                return Err(format!(
                    "called {} but this session has no plugin UI",
                    import.name()
                ));
            };
            return handle(import, request, &owner, state, answer, version);
        }
        // Net: the permission is checked above; the host here.
        Import::NetHttpRequest => {
            let request =
                ac_plugin_abi::net::HttpRequest::from_bytes_at(request, version).map_err(bad)?;
            let Some(host) = ac_plugin_net::request_host(&request.url) else {
                return Ok(Err::<u32, String>(format!(
                    "'{}' is not an http(s) address",
                    request.url
                ))
                .to_bytes_at(version));
            };
            match reach(services, &mut *cx.api, manifest, &host) {
                Ok(allowed) => services
                    .net
                    .http_request(id, request, allowed)
                    .to_bytes_at(version),
                Err(refusal) => Err::<u32, String>(refusal).to_bytes_at(version),
            }
        }
        Import::NetTcpConnect | Import::NetTcpConnectProxied => {
            let connect =
                ac_plugin_abi::net::TcpConnect::from_bytes_at(request, version).map_err(bad)?;
            let via_proxy = import == Import::NetTcpConnectProxied;
            match reach(
                services,
                &mut *cx.api,
                manifest,
                &connect.host.to_lowercase(),
            ) {
                Ok(_) => services
                    .net
                    .tcp_connect(id, &connect, via_proxy)
                    .to_bytes_at(version),
                Err(refusal) => Err::<u32, String>(refusal).to_bytes_at(version),
            }
        }
        Import::NetTcpListen => {
            let port = u32::from_bytes_at(request, version).map_err(bad)?;
            services.net.tcp_listen(id, port).to_bytes_at(version)
        }
        Import::NetSend => {
            let (handle, bytes) = <(u32, Vec<u8>)>::from_bytes_at(request, version).map_err(bad)?;
            services.net.send(id, handle, &bytes).to_bytes_at(version)
        }
        Import::NetReceive => {
            let (handle, max) = <(u32, u32)>::from_bytes_at(request, version).map_err(bad)?;
            services.net.receive(id, handle, max).to_bytes_at(version)
        }
        Import::NetClose => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services.net.close(id, handle).to_bytes_at(version)
        }
        // M. The status board.
        Import::StatusPublish => {
            let (key, value) =
                <(String, Option<String>)>::from_bytes_at(request, version).map_err(bad)?;
            services
                .status
                .publish(id, &key, value)
                .to_bytes_at(version)
        }
        Import::StatusRead => {
            let (owner, key) = <(String, String)>::from_bytes_at(request, version).map_err(bad)?;
            services.status.read(&owner, &key).to_bytes_at(version)
        }
        Import::StatusCapture => {
            let owner = String::from_bytes_at(request, version).map_err(bad)?;
            services.status.capture(&owner).to_bytes_at(version)
        }
        // P. Plugin services.
        Import::ServiceRegister => {
            let registration = ServiceRegistration::from_bytes_at(request, version).map_err(bad)?;
            if answers {
                services.plugin_services.register(id, &registration)
            } else {
                Err(ServiceError::NoCallback)
            }
            .to_bytes_at(version)
        }
        Import::ServiceUnregister => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services
                .plugin_services
                .unregister(id, handle)
                .to_bytes_at(version)
        }
        Import::Services => services.plugin_services.available().to_bytes_at(version),
        Import::ServiceCall => {
            let call = ServiceCall::from_bytes_at(request, version).map_err(bad)?;
            call_service(id, call, others, cx).to_bytes_at(version)
        }
        // N. Storage.
        Import::StorageRead => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            files::read(cx.storage_roots, &caller, &key).to_bytes_at(version)
        }
        Import::StorageReadText => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            files::read(cx.storage_roots, &caller, &key)
                .map(|bytes| bytes.map(|b| files::decode_text(&b)))
                .to_bytes_at(version)
        }
        Import::StorageWrite => {
            let write = StorageWrite::from_bytes_at(request, version).map_err(bad)?;
            files::write(&mut services.storage, cx.storage_roots, &caller, &write)
                .to_bytes_at(version)
        }
        Import::StorageList => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            files::list(cx.storage_roots, &caller, &key).to_bytes_at(version)
        }
        Import::StorageDelete => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            files::delete(cx.storage_roots, &caller, &key).to_bytes_at(version)
        }
        Import::StorageEnsureFolder => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            files::ensure_folder(cx.storage_roots, &caller, &key).to_bytes_at(version)
        }
        Import::StorageDescribe => {
            let place = StoragePlace::from_bytes_at(request, version).map_err(bad)?;
            files::describe(cx.storage_roots, &caller, &place).to_bytes_at(version)
        }
        Import::StorageLock => {
            let key = StorageKey::from_bytes_at(request, version).map_err(bad)?;
            services
                .storage
                .lock(cx.storage_roots, &caller, &key)
                .to_bytes_at(version)
        }
        Import::StorageUnlock => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services.storage.unlock(id, handle).to_bytes_at(version)
        }
        Import::ResourcesListDataFiles => {
            let relative = String::from_bytes_at(request, version).map_err(bad)?;
            files::list_data_files(cx.storage_roots, &caller, &relative).to_bytes_at(version)
        }
        Import::ChatAddFilter => services.filters.register(id).to_bytes_at(version),
        Import::ChatRemoveFilter => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services.filters.unregister(id, handle).to_bytes_at(version)
        }
        Import::ChatAddInterceptor => services.interceptors.register(id).to_bytes_at(version),
        Import::ChatRemoveInterceptor => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services
                .interceptors
                .unregister(id, handle)
                .to_bytes_at(version)
        }
        Import::CharacterInfo => cx.api.character().to_bytes_at(version),
        // G. The loot classifiers plugins share.
        Import::LootClassifierRegister => {
            let registration =
                ClassifierRegistration::from_bytes_at(request, version).map_err(bad)?;
            services
                .classifiers
                .register(id, &registration)
                .to_bytes_at(version)
        }
        Import::LootClassifierUnregister => {
            let handle = u32::from_bytes_at(request, version).map_err(bad)?;
            services
                .classifiers
                .unregister(id, handle)
                .to_bytes_at(version)
        }
        Import::LootClassifiers => services.classifiers.available().to_bytes_at(version),
        Import::LootClassify => {
            let ask = ClassifyRequest::from_bytes_at(request, version).map_err(bad)?;
            let answer = ask_classifier(id, &ask.classifier_id, others, cx, |handle| {
                Callback::LootClassify(LootQuestion {
                    handle,
                    profile: ask.profile.clone(),
                    context: ask.context.clone(),
                })
            });
            let classification: Option<LootClassification> = match answer {
                Some(CallbackAnswer::Classification(c)) => c,
                _ => None,
            };
            classification.to_bytes_at(version)
        }
        Import::LootNeedsIdentification => {
            let ask = ClassifyRequest::from_bytes_at(request, version).map_err(bad)?;
            let answer = ask_classifier(id, &ask.classifier_id, others, cx, |handle| {
                Callback::LootNeedsIdentification(LootQuestion {
                    handle,
                    profile: ask.profile.clone(),
                    context: ask.context.clone(),
                })
            });
            matches!(answer, Some(CallbackAnswer::Bool(true))).to_bytes_at(version)
        }
        Import::LootNotifyLooted => {
            let notice = LootedNotice::from_bytes_at(request, version).map_err(bad)?;
            let item = notice.item.clone();
            ask_classifier(id, &notice.classifier_id, others, cx, |handle| {
                Callback::LootLooted((handle, item.clone()))
            })
            .is_some()
            .to_bytes_at(version)
        }
        Import::LootNotifyRemoved => {
            let notice = RemovedNotice::from_bytes_at(request, version).map_err(bad)?;
            ask_classifier(id, &notice.classifier_id, others, cx, |handle| {
                Callback::LootRemoved((handle, notice.object_id))
            })
            .is_some()
            .to_bytes_at(version)
        }
        // Everything about the game is the session's.
        game => {
            let caller = Caller {
                plugin_id: id,
                api_version: version,
            };
            return cx.api.call(caller, game, request);
        }
    })
}

/// Asks the plugin owning classifier `scoped_id` the question `make`
/// builds from its handle; `None` when no plugin answers (no such
/// classifier, the caller's own (a plugin asks its own code directly), no
/// callback export, inside another callback, or a fault, which unloads the
/// owner after the call).
fn ask_classifier(
    caller: &str,
    scoped_id: &str,
    others: Option<&mut Others<'_>>,
    cx: &mut Context<'_>,
    make: impl FnOnce(u32) -> Callback,
) -> Option<CallbackAnswer> {
    let (owner, handle) = cx.services.classifiers.find(scoped_id)?;
    ask_plugin(caller, &owner, others, cx, &make(handle))
}

/// Asks plugin `owner` `callback`; `None` when it does not answer (it is
/// the caller, has no callback export, the call is inside another
/// callback, or it faults, which unloads it after the call).
fn ask_plugin(
    caller: &str,
    owner: &str,
    others: Option<&mut Others<'_>>,
    cx: &mut Context<'_>,
    callback: &Callback,
) -> Option<CallbackAnswer> {
    if owner.eq_ignore_ascii_case(caller) {
        return None;
    }
    if cx.services.has_faulted(owner) {
        return None;
    }
    match put(others?.find(owner)?, cx, callback) {
        Asked::Answered(answer) => answer,
        Asked::Faulted { .. } => None,
    }
}

/// Calls service `call` of another plugin for plugin `caller`: the owner
/// answers through its callback export, under its own deadline.
fn call_service(
    caller: &str,
    call: ServiceCall,
    others: Option<&mut Others<'_>>,
    cx: &mut Context<'_>,
) -> Result<Vec<u8>, ServiceError> {
    let too_large = || ServiceError::TooLarge(u32::try_from(MAX_PAYLOAD).unwrap_or(u32::MAX));
    if call.payload.len() > MAX_PAYLOAD {
        return Err(too_large());
    }
    let others = others.ok_or(ServiceError::Nested)?;
    let (owner, handle) = cx
        .services
        .plugin_services
        .find(&call.service_id)
        .ok_or(ServiceError::NotFound)?;
    if owner.eq_ignore_ascii_case(caller) {
        return Err(ServiceError::SelfCall);
    }
    let target = others.find(&owner).ok_or(ServiceError::NotFound)?;
    let request = Callback::ServiceRequest(ServiceRequest {
        handle,
        caller_id: caller.to_owned(),
        payload: call.payload,
    });
    match put(target, cx, &request) {
        Asked::Answered(Some(CallbackAnswer::Service(Ok(reply)))) => {
            if reply.len() > MAX_PAYLOAD {
                Err(too_large())
            } else {
                Ok(reply)
            }
        }
        Asked::Answered(Some(CallbackAnswer::Service(Err(reason)))) => {
            if reason.len() > MAX_PAYLOAD {
                Err(too_large())
            } else {
                Err(ServiceError::Refused(reason))
            }
        }
        Asked::Answered(_) => Err(ServiceError::Refused(
            "the service gave no answer".to_owned(),
        )),
        Asked::Faulted { stuck: true } => Err(ServiceError::TimedOut),
        Asked::Faulted { stuck: false } => Err(ServiceError::Faulted),
    }
}

/// What came of asking a plugin a question.
enum Asked {
    /// Its answer; none from a plugin without a callback export, or one
    /// that does not decode.
    Answered(Option<CallbackAnswer>),
    /// It faulted (`stuck`: it ran past its deadline); it is unloaded once
    /// the caller's call returns.
    Faulted { stuck: bool },
}

/// Asks plugin `target` `callback` through its callback export, as a call
/// of its own: its time is its own ([`Context::lent`]).
fn put(target: &mut Loaded, cx: &mut Context<'_>, callback: &Callback) -> Asked {
    // The question as the plugin it goes to reads it: a record field a later
    // API version added is left out for a plugin built before it.
    let version = target.manifest.api_version;
    let bytes = callback.to_bytes_at(version);
    let Loaded {
        manifest,
        instance,
        directory,
    } = target;
    let manifest: &PluginManifest = manifest;
    let directory: &std::path::Path = directory;
    let answers = instance.answers_callbacks();
    let started = Instant::now();
    let result = {
        let mut handler = |import: Import, request: &[u8]| {
            handle_import(import, request, manifest, directory, answers, cx, None)
                .map(Handled::from)
        };
        instance.callback(
            &bytes,
            crate::watchdog::Entry::of_callback(callback),
            &mut handler,
        )
    };
    cx.lent += started.elapsed();
    match result {
        Ok(answer) => {
            Asked::Answered(answer.and_then(|a| CallbackAnswer::from_bytes_at(&a, version).ok()))
        }
        Err(fault) => {
            cx.services.fault(&manifest.id, fault);
            Asked::Faulted {
                stuck: instance.was_stuck(),
            }
        }
    }
}
