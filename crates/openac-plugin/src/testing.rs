//! A fake client for testing plugin logic natively with `cargo test`.
//!
//! [`FakeHost`] answers every import the way the client does (the same
//! encoded requests and replies the wasm build exchanges), records what the
//! plugin did, and drives the plugin's lifecycle. The host services (log,
//! settings, verbs, chat lines, the character, plugin services) are
//! answered from its fields; every other client function answers what a
//! test set with [`FakeHost::set_reply`], else its inert reply (nothing
//! found, unavailable):
//!
//! ```
//! use openac_plugin::testing::FakeHost;
//! use openac_plugin::{Event, Host, Plugin, StartInfo};
//!
//! #[derive(Default)]
//! struct Greeter;
//!
//! impl Plugin for Greeter {
//!     fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
//!         host.register_command("greet", "Greets")
//!     }
//!     fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
//!         if let Event::Command(call) = event {
//!             host.chat_post(&format!("hi {}", call.arguments));
//!         }
//!     }
//! }
//!
//! let mut host = FakeHost::new("example.greeter");
//! let mut plugin = Greeter::default();
//! host.enable(&mut plugin).unwrap();
//! assert!(host.type_line(&mut plugin, "/greet Horan"));
//! assert_eq!(host.posted[0].text, "hi Horan");
//! ```

use std::collections::{BTreeMap, BTreeSet, HashMap};

use ac_plugin_abi::callback::{Callback, CallbackAnswer};
use ac_plugin_abi::host::Clock;
use ac_plugin_abi::services::{
    MAX_DESCRIPTION, MAX_PAYLOAD, MAX_SERVICES, ServiceCall, ServiceError, ServiceInfo,
    ServiceRegistration, ServiceRequest, is_valid_name,
};
use ac_plugin_abi::verbs::{already_registered, normalize_verb, split_command_line};
use ac_plugin_abi::{
    API_CURRENT, Call, CharacterInfo, ChatPost, CommandCall, CommandDef, Event, Import, LogLevel,
    LogRequest, StartInfo, Wire,
};

use crate::{Host, Plugin, Transport};

/// A fake client: what the plugin sees, and what it did.
#[derive(Debug, Default)]
pub struct FakeHost {
    /// The plugin's id (in its start record).
    pub plugin_id: String,
    /// Whether a window is attached.
    pub window_attached: bool,
    /// The plugin's session settings.
    pub settings: BTreeMap<String, String>,
    /// The character in the world.
    pub character: Option<CharacterInfo>,
    /// What the clock reads.
    pub clock: Clock,
    /// Log lines, in order.
    pub logs: Vec<(LogLevel, String)>,
    /// Lines submitted to the chat box, in order.
    pub submitted: Vec<String>,
    /// Lines posted to the transcript, in order.
    pub posted: Vec<ChatPost>,
    /// Registered verbs.
    pub commands: Vec<CommandDef>,
    /// Every import called, in order.
    pub calls: Vec<Import>,
    /// Every request, encoded, in order (read with [`Self::requests`]).
    pub requests: Vec<(Import, Vec<u8>)>,
    /// The session was asked to close.
    pub close_requested: bool,
    replies: HashMap<Import, Vec<u8>>,
    /// The plugin's name as the player sees it (its UI's owner).
    pub display_name: String,
    /// The plugin's UI, kept exactly as the client keeps it: its windows,
    /// their bound values, canvases, images, labels and lines.
    pub ui: ac_plugin_ui::PluginUi,
    /// Spells the fake client knows, with their icons.
    pub spell_icons: BTreeMap<u32, u32>,
    /// Objects the fake client knows, with their icons.
    pub object_icons: BTreeMap<u32, u32>,
    /// The services the plugin offers, by handle.
    pub services: BTreeMap<u32, ServiceRegistration>,
    /// Other plugins' services ([`Self::set_service`]), by id in lower
    /// case: what they are, and what they answer.
    other_services: BTreeMap<String, (ServiceInfo, Result<Vec<u8>, ServiceError>)>,
    next_service: u32,
    /// The plugins whose status board changes the plugin subscribed to, by
    /// id in lower case. It hears none by itself: deliver
    /// [`Event::StatusChanged`](crate::Event::StatusChanged) to test them.
    pub status_subscriptions: BTreeSet<String>,
    /// Whether the plugin is answering a callback ([`Self::ask`]): it cannot
    /// call a service then, as in the client.
    answering: bool,
}

impl FakeHost {
    /// A host for the plugin `plugin_id`, no window, nothing set.
    #[must_use]
    pub fn new(plugin_id: &str) -> Self {
        Self {
            plugin_id: plugin_id.to_owned(),
            ..Self::default()
        }
    }

    /// From now on, client function `C` answers `reply`.
    pub fn set_reply<C: Call>(&mut self, reply: &C::Reply) {
        self.replies.insert(C::IMPORT, reply.to_bytes());
    }

    /// Every request the plugin made to client function `C`, in order.
    ///
    /// # Panics
    /// When a recorded request does not decode (it was encoded by the SDK,
    /// so it always does).
    #[must_use]
    pub fn requests<C: Call>(&self) -> Vec<C::Request> {
        self.requests
            .iter()
            .filter(|(import, _)| *import == C::IMPORT)
            .map(|(_, bytes)| C::Request::from_bytes(bytes).expect("a recorded request"))
            .collect()
    }

    /// Starts a plugin as the client does.
    ///
    /// # Errors
    /// The plugin's refusal.
    pub fn enable<P: Plugin>(&mut self, plugin: &mut P) -> Result<(), String> {
        let info = StartInfo {
            plugin_id: self.plugin_id.clone(),
            api_version: API_CURRENT.to_string(),
            client_version: "0.1.0".to_owned(),
        };
        let mut host = Host::with_transport(self);
        crate::__export::start(plugin, &mut host, &info.to_bytes())
    }

    /// Stops a plugin.
    pub fn disable<P: Plugin>(&mut self, plugin: &mut P) {
        plugin.disable(&mut Host::with_transport(self));
    }

    /// Runs `f` with a host on this fake, as a plugin call would.
    pub fn with_host<R>(&mut self, f: impl FnOnce(&mut Host<'_>) -> R) -> R {
        f(&mut Host::with_transport(self))
    }

    /// One tick.
    pub fn tick<P: Plugin>(&mut self, plugin: &mut P) {
        plugin.tick(&mut Host::with_transport(self));
    }

    /// Delivers an event, through its encoding, as the client does.
    pub fn send<P: Plugin>(&mut self, plugin: &mut P, event: &Event) {
        let bytes = event.to_bytes();
        crate::__export::deliver(plugin, &mut Host::with_transport(self), &bytes);
    }

    /// Asks the plugin something it registered to answer, through the
    /// encoding, as the client does.
    ///
    /// # Panics
    /// When the plugin's answer does not decode (the SDK encodes it, so it
    /// always does).
    pub fn ask<P: Plugin>(&mut self, plugin: &mut P, callback: &Callback) -> CallbackAnswer {
        let bytes = callback.to_bytes();
        let was_answering = std::mem::replace(&mut self.answering, true);
        let answer = crate::__export::answer(plugin, &mut Host::with_transport(self), &bytes);
        self.answering = was_answering;
        CallbackAnswer::from_bytes(&answer).expect("an encoded answer")
    }

    /// Another plugin offers service `id` (`<plugin id>/<name>`) and answers
    /// every call of it with `reply`, as the client hands it back
    /// (an error for no reply). The plugin hears no event for it.
    pub fn set_service(&mut self, id: &str, reply: Result<Vec<u8>, ServiceError>) {
        let owner = id.split_once('/').map_or(id, |(owner, _)| owner);
        let info = ServiceInfo {
            id: id.to_owned(),
            owner: owner.to_owned(),
            description: String::new(),
        };
        self.other_services.insert(id.to_lowercase(), (info, reply));
    }

    /// Subscribes to plugin `owner`'s changes as the client does: false for
    /// a blank id, the plugin's own, or past the limit.
    fn status_subscribe(&mut self, owner: &str) -> bool {
        let owner = owner.trim().to_lowercase();
        if owner.is_empty() || owner == self.plugin_id.to_lowercase() {
            return false;
        }
        if !self.status_subscriptions.contains(&owner)
            && self.status_subscriptions.len() >= ac_plugin_abi::status::MAX_SUBSCRIPTIONS
        {
            return false;
        }
        self.status_subscriptions.insert(owner);
        true
    }

    /// Plugin `caller` calls the plugin's service `name` with `request`, as
    /// the client does: through the plugin's callback, its answer read
    /// back as the caller gets it.
    pub fn request_service<P: Plugin>(
        &mut self,
        plugin: &mut P,
        name: &str,
        caller: &str,
        request: &[u8],
    ) -> Result<Vec<u8>, ServiceError> {
        if request.len() > MAX_PAYLOAD {
            return Err(too_large());
        }
        let handle = self
            .services
            .iter()
            .find(|(_, s)| s.name == name)
            .map(|(handle, _)| *handle)
            .ok_or(ServiceError::NotFound)?;
        let callback = Callback::ServiceRequest(ServiceRequest {
            handle,
            caller_id: caller.to_owned(),
            payload: request.to_vec(),
        });
        match self.ask(plugin, &callback) {
            CallbackAnswer::Service(Ok(reply)) if reply.len() > MAX_PAYLOAD => Err(too_large()),
            CallbackAnswer::Service(Ok(reply)) => Ok(reply),
            CallbackAnswer::Service(Err(reason)) if reason.len() > MAX_PAYLOAD => Err(too_large()),
            CallbackAnswer::Service(Err(reason)) => Err(ServiceError::Refused(reason)),
            _ => Err(ServiceError::Refused(
                "the service gave no answer".to_owned(),
            )),
        }
    }

    /// The services on offer, the plugin's own and the others', by id.
    fn service_list(&self) -> Vec<ServiceInfo> {
        let own = self.services.values().map(|s| ServiceInfo {
            id: format!("{}/{}", self.plugin_id, s.name),
            owner: self.plugin_id.clone(),
            description: s.description.clone(),
        });
        let mut list: Vec<ServiceInfo> = own
            .chain(self.other_services.values().map(|(info, _)| info.clone()))
            .collect();
        list.sort_by_key(|info| info.id.to_lowercase());
        list
    }

    fn register_service(&mut self, offer: ServiceRegistration) -> Result<u32, ServiceError> {
        if !is_valid_name(&offer.name) {
            return Err(ServiceError::InvalidName);
        }
        if offer.description.len() > MAX_DESCRIPTION {
            return Err(ServiceError::DescriptionTooLarge(
                u32::try_from(MAX_DESCRIPTION).unwrap_or(u32::MAX),
            ));
        }
        if self.services.values().any(|s| s.name == offer.name) {
            return Err(ServiceError::AlreadyRegistered);
        }
        if self.services.len() >= MAX_SERVICES {
            return Err(ServiceError::TooMany(
                u32::try_from(MAX_SERVICES).unwrap_or(u32::MAX),
            ));
        }
        self.next_service += 1;
        self.services.insert(self.next_service, offer);
        Ok(self.next_service)
    }

    fn call_service(&self, call: &ServiceCall) -> Result<Vec<u8>, ServiceError> {
        if call.payload.len() > MAX_PAYLOAD {
            return Err(too_large());
        }
        if self.answering {
            return Err(ServiceError::Nested);
        }
        let id = call.service_id.trim().to_lowercase();
        let own = format!("{}/", self.plugin_id.to_lowercase());
        if let Some(name) = id.strip_prefix(&own)
            && self.services.values().any(|s| s.name == name)
        {
            return Err(ServiceError::SelfCall);
        }
        match self.other_services.get(&id) {
            Some((_, Ok(reply))) if reply.len() > MAX_PAYLOAD => Err(too_large()),
            Some((_, Err(ServiceError::Refused(reason)))) if reason.len() > MAX_PAYLOAD => {
                Err(too_large())
            }
            Some((_, reply)) => reply.clone(),
            None => Err(ServiceError::NotFound),
        }
    }

    /// The player typed `line`: when it names one of the plugin's verbs (or
    /// aliases), the plugin gets the command and this returns true.
    pub fn type_line<P: Plugin>(&mut self, plugin: &mut P, line: &str) -> bool {
        let Some((raw, verb, arguments)) = split_command_line(line) else {
            return false;
        };
        let Some(definition) = self.commands.iter().find(|d| {
            d.verb.eq_ignore_ascii_case(verb)
                || d.aliases.iter().any(|a| a.eq_ignore_ascii_case(verb))
        }) else {
            return false;
        };
        let call = CommandCall {
            verb: definition.verb.clone(),
            arguments: arguments.to_owned(),
            raw: raw.to_owned(),
        };
        self.send(plugin, &Event::Command(call));
        true
    }

    /// A UI import, carried out on [`Self::ui`] by the client's own code;
    /// what the UI refuses for the author's attention is logged as the
    /// client logs it.
    fn ui_call(&mut self, import: Import, bytes: &[u8]) -> Vec<u8> {
        use ac_plugin_abi::ui_host::{WorldQuery, handle, reports, world_query};
        let owner = ac_plugin_ui::UiOwner::new(self.plugin_id.as_str(), self.owner_name());
        let answer = match world_query(import, bytes) {
            Some(WorldQuery::SpellIcon(spell)) => self.spell_icons.get(&spell).copied(),
            Some(WorldQuery::ObjectIcon(object)) => self.object_icons.get(&object).copied(),
            None => None,
        };
        let reply = match handle(import, bytes, &owner, &mut self.ui, answer, API_CURRENT) {
            Ok(reply) => reply,
            Err(error) => panic!("{error}"),
        };
        for (_, line) in reports(&mut self.ui) {
            self.logs.push((LogLevel::Warn, line));
        }
        reply
    }

    fn owner_name(&self) -> String {
        if self.display_name.is_empty() {
            self.plugin_id.clone()
        } else {
            self.display_name.clone()
        }
    }

    /// What a window would report (a control used, a window opened or
    /// closed, pointer input, the client windows): the plugin hears its UI
    /// events at once, as the client hands them over before its next tick.
    /// A held pointer's move or release that carries the window's place is
    /// measured from where the plugin last put the canvas, as the client
    /// measures it (`ac_plugin_ui::PluginUi::pointer_as_placed_now`).
    pub fn ui_input<P: Plugin>(&mut self, plugin: &mut P, input: ac_plugin_ui::UiInput) {
        self.ui.input(input);
        for (owner, event) in self.ui.take_events() {
            if owner == self.plugin_id {
                let event = self.ui.pointer_as_placed_now(event);
                let event = ac_plugin_abi::ui_host::event(event);
                self.send(plugin, &event);
            }
        }
    }

    /// What a window reports of an in-markup canvas: an element hosts it,
    /// laid out at `size` (whole pixels), or (`None`) no element does. A new
    /// size is the canvas's, and the plugin hears
    /// [`Event::CanvasResized`](crate::Event::CanvasResized) at once.
    pub fn host_canvas<P: Plugin>(
        &mut self,
        plugin: &mut P,
        canvas: u32,
        size: Option<(i32, i32)>,
    ) {
        self.ui_input(plugin, ac_plugin_ui::UiInput::CanvasHosted { canvas, size });
    }

    /// The value last pushed for binding `name` of panel `panel`.
    #[must_use]
    pub fn panel_value(&self, panel: u32, name: &str) -> Option<crate::ui::UiValue> {
        use crate::ui::UiValue as W;
        use ac_plugin_ui::UiValue as V;
        let value = self.ui.panels().find(|p| p.id == panel)?.values.get(name)?;
        Some(match value.clone() {
            V::Bool(v) => W::Bool(v),
            V::Int(v) => W::Int(v),
            V::Uint(v) => W::Uint(v),
            V::Float(v) => W::Float(v),
            V::Text(v) => W::Text(v),
            V::TextList(v) => W::TextList(v),
            V::UintList(v) => W::UintList(v),
            V::IntList(v) => W::IntList(v),
            V::BoolList(v) => W::BoolList(v),
            V::OptionalBool(_) | V::FloatList(_) | V::Items(_) => return None,
        })
    }

    /// The value last pushed for typed binding `name` of panel `panel`
    /// (any kind but items).
    #[must_use]
    pub fn panel_typed_value(&self, panel: u32, name: &str) -> Option<crate::ui::TypedValue> {
        use crate::ui::TypedValue as T;
        use ac_plugin_ui::UiValue as V;
        let value = self.ui.panels().find(|p| p.id == panel)?.values.get(name)?;
        Some(match value.clone() {
            V::Bool(v) => T::Bool(v),
            V::Int(v) => T::Int(v),
            V::Uint(v) => T::Uint(v),
            V::Float(v) => T::Float(v),
            V::Text(v) => T::Text(v),
            V::TextList(v) => T::TextList(v),
            V::UintList(v) => T::UintList(v),
            V::IntList(v) => T::IntList(v),
            V::BoolList(v) => T::BoolList(v),
            V::OptionalBool(v) => T::OptionalBool(v),
            V::FloatList(v) => T::FloatList(v),
            V::Items(_) => return None,
        })
    }

    /// How many items panel `panel` holds for items binding `name`.
    #[must_use]
    pub fn panel_item_count(&self, panel: u32, name: &str) -> Option<usize> {
        match self.ui.panels().find(|p| p.id == panel)?.values.get(name)? {
            ac_plugin_ui::UiValue::Items(items) => Some(items.len()),
            _ => None,
        }
    }

    /// The player answered dialog `handle` with button `button`.
    pub fn answer_dialog<P: Plugin>(&mut self, plugin: &mut P, handle: u32, button: i32) {
        self.ui_input(
            plugin,
            ac_plugin_ui::UiInput::DialogAnswered { handle, button },
        );
    }

    /// The player clicked a control whose handler is `handler` (an
    /// `Action`) on panel `panel`.
    pub fn click<P: Plugin>(&mut self, plugin: &mut P, panel: u32, handler: &str) {
        self.act(plugin, panel, handler, crate::ui::ActionValue::None);
    }

    /// A control's handler fired with an argument (a row, a slider value,
    /// typed text).
    pub fn act<P: Plugin>(
        &mut self,
        plugin: &mut P,
        panel: u32,
        handler: &str,
        value: crate::ui::ActionValue,
    ) {
        use crate::ui::ActionValue as A;
        use ac_plugin_ui::ActionValue as B;
        use ac_plugin_ui::BindingKind as K;
        // The newer handler arguments travel in the plain ones; the
        // handler's declared kind says how to read them back.
        let typed = match self.ui.handler_kind(panel, handler) {
            Some(K::BoolAction) => value.as_bool().map(B::Bool),
            Some(K::UintAction) => value.as_uint().map(B::Uint),
            Some(K::IntBoolAction) => value.as_int_bool().map(|(v, f)| B::IntBool(v, f)),
            Some(K::TextBoolAction) => value.as_text_bool().map(|(v, f)| B::TextBool(v, f)),
            Some(K::FloatListAction) => value.as_float_list().map(B::FloatList),
            Some(K::IntListAction) => value.as_int_list().map(B::IntList),
            _ => None,
        };
        let value = match (typed, value) {
            (Some(typed), _) => typed,
            (None, A::None) => B::None,
            (None, A::Int(v)) => B::Int(v),
            (None, A::Float(v)) => B::Float(v),
            (None, A::Text(v)) => B::Text(v),
        };
        self.ui_input(
            plugin,
            ac_plugin_ui::UiInput::Action {
                panel,
                handler: handler.to_owned(),
                value,
            },
        );
    }

    /// A window attaches (`true`) or leaves, and the plugin hears it.
    pub fn attach_window<P: Plugin>(&mut self, plugin: &mut P, attached: bool) {
        self.window_attached = attached;
        self.ui.set_window_attached(attached);
        self.send(plugin, &Event::WindowAttached(attached));
    }

    fn register(&mut self, definition: CommandDef) -> Result<(), String> {
        let verb = normalize_verb(&definition.verb)?;
        let aliases = definition
            .aliases
            .iter()
            .map(|a| normalize_verb(a))
            .collect::<Result<Vec<_>, _>>()?;
        let mut names: Vec<String> = std::iter::once(verb.clone())
            .chain(aliases.iter().cloned())
            .map(|n| n.to_lowercase())
            .collect();
        names.sort_unstable();
        let count = names.len();
        names.dedup();
        if names.len() != count {
            return Err("Command aliases must be unique.".to_owned());
        }
        for existing in &self.commands {
            for name in std::iter::once(&existing.verb).chain(&existing.aliases) {
                if names.contains(&name.to_lowercase()) {
                    return Err(already_registered(name));
                }
            }
        }
        self.commands.push(CommandDef {
            verb,
            aliases,
            description: definition.description,
        });
        Ok(())
    }
}

fn too_large() -> ServiceError {
    ServiceError::TooLarge(u32::try_from(MAX_PAYLOAD).unwrap_or(u32::MAX))
}

fn reply<T: Wire>(value: &T) -> Vec<u8> {
    value.to_bytes()
}

fn request<T: Wire>(import: Import, bytes: &[u8]) -> T {
    match T::from_bytes(bytes) {
        Ok(value) => value,
        Err(error) => panic!(
            "the plugin's request to {} is malformed: {error}",
            import.name()
        ),
    }
}

impl Transport for FakeHost {
    fn call(&mut self, import: Import, bytes: &[u8]) -> Vec<u8> {
        self.calls.push(import);
        self.requests.push((import, bytes.to_vec()));
        if let Some(reply) = self.replies.get(&import) {
            return reply.clone();
        }
        match import {
            Import::HostLog => {
                let line: LogRequest = request(import, bytes);
                self.logs.push((line.level, line.message));
                Vec::new()
            }
            Import::HostWindowAttached => reply(&self.window_attached),
            Import::HostSetting => {
                let key: String = request(import, bytes);
                reply(&self.settings.get(&key).cloned())
            }
            Import::HostSettings => reply(
                &self
                    .settings
                    .iter()
                    .map(|(k, v)| (k.clone(), v.clone()))
                    .collect::<Vec<_>>(),
            ),
            Import::HostClock => reply(&self.clock),
            Import::HostRequestClose => {
                self.close_requested = true;
                reply(&true)
            }
            Import::CommandsRegister => {
                let definition: CommandDef = request(import, bytes);
                reply(&self.register(definition))
            }
            Import::CommandsUnregister => {
                let verb: String = request(import, bytes);
                let before = self.commands.len();
                self.commands
                    .retain(|d| !d.verb.eq_ignore_ascii_case(verb.trim()));
                reply(&(self.commands.len() != before))
            }
            Import::ChatSubmit => {
                let line: String = request(import, bytes);
                self.submitted.push(line);
                Vec::new()
            }
            Import::ChatPost => {
                let post: ChatPost = request(import, bytes);
                self.posted.push(post);
                Vec::new()
            }
            Import::CharacterInfo => reply(&self.character),
            Import::ServiceRegister => {
                let offer: ServiceRegistration = request(import, bytes);
                reply(&self.register_service(offer))
            }
            Import::ServiceUnregister => {
                let handle: u32 = request(import, bytes);
                reply(&self.services.remove(&handle).is_some())
            }
            Import::Services => reply(&self.service_list()),
            Import::StatusSubscribe => {
                let owner: String = request(import, bytes);
                reply(&self.status_subscribe(&owner))
            }
            Import::StatusUnsubscribe => {
                let owner: String = request(import, bytes);
                let owner = owner.trim().to_lowercase();
                reply(&self.status_subscriptions.remove(&owner))
            }
            Import::ServiceCall => {
                let call: ServiceCall = request(import, bytes);
                reply(&self.call_service(&call))
            }
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
            | Import::UiCanvasResize
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
            | Import::UiInputActionsListen => self.ui_call(import, bytes),
            other => other.default_reply(),
        }
    }
}

#[cfg(test)]
mod tests {
    use ac_plugin_abi::calls;

    use super::*;

    // Subscribing answers as the client does: ids ignore case, not its
    // own, not blank, at most the limit; unsubscribing says whether it was.
    #[test]
    fn status_subscriptions_answer_as_the_client_does() {
        let mut fake = FakeHost::new("test.Me");
        fake.with_host(|host| {
            assert!(host.status_subscribe("Openac.Vision"));
            assert!(host.status_subscribe("openac.vision"));
            assert!(!host.status_subscribe("TEST.ME"));
            assert!(!host.status_subscribe("  "));
        });
        assert_eq!(
            fake.status_subscriptions.iter().collect::<Vec<_>>(),
            ["openac.vision"]
        );
        fake.with_host(|host| {
            for n in 1..ac_plugin_abi::status::MAX_SUBSCRIPTIONS {
                assert!(host.status_subscribe(&format!("p{n}")));
            }
            assert!(!host.status_subscribe("one.more"));
            assert!(host.status_unsubscribe("OPENAC.VISION"));
            assert!(!host.status_unsubscribe("openac.vision"));
            assert!(host.status_subscribe("one.more"));
        });
    }

    #[derive(Default)]
    struct Recorder {
        ticks: u32,
        events: Vec<Event>,
    }

    impl Plugin for Recorder {
        fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
            host.info(&format!("{} on API {}", info.plugin_id, info.api_version));
            host.register_command_def(&CommandDef {
                verb: "rec".into(),
                aliases: vec!["r".into()],
                description: "Records".into(),
            })?;
            if host.setting("refuse").is_some() {
                return Err("told to refuse".into());
            }
            Ok(())
        }
        fn tick(&mut self, _host: &mut Host<'_>) {
            self.ticks += 1;
        }
        fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
            if let Event::Command(call) = &event {
                host.chat_submit(&format!("/e {}", call.arguments));
            }
            self.events.push(event);
        }
    }

    #[test]
    fn a_plugin_runs_against_the_fake_host_through_the_encoding() {
        let mut host = FakeHost::new("test.recorder");
        let mut plugin = Recorder::default();
        host.enable(&mut plugin).unwrap();
        assert_eq!(
            host.logs,
            vec![(LogLevel::Info, "test.recorder on API 1.12".to_owned())]
        );
        host.tick(&mut plugin);
        assert!(host.type_line(&mut plugin, " @R waves "));
        assert!(!host.type_line(&mut plugin, "/other"));
        assert_eq!(plugin.ticks, 1);
        assert_eq!(host.submitted, vec!["/e waves".to_owned()]);
        assert_eq!(
            plugin.events,
            vec![Event::Command(CommandCall {
                verb: "rec".into(),
                arguments: "waves".into(),
                raw: "@R waves".into(),
            })]
        );
        assert_eq!(
            host.requests::<calls::ChatSubmit>(),
            vec!["/e waves".to_owned()]
        );
    }

    #[test]
    fn a_refusal_and_a_taken_verb_come_back_as_errors() {
        let mut host = FakeHost::new("test.recorder");
        host.settings.insert("refuse".into(), "yes".into());
        assert_eq!(
            host.enable(&mut Recorder::default()),
            Err("told to refuse".to_owned())
        );
        // The verb from the refused start is still held by the fake (the
        // client releases it); a second registration is refused.
        assert_eq!(
            host.enable(&mut Recorder::default()),
            Err(already_registered("rec"))
        );
    }

    #[test]
    fn unset_calls_answer_inert_and_set_replies_win() {
        let mut fake = FakeHost::new("test.inert");
        {
            let mut host = Host::with_transport(&mut fake);
            assert!(!host.window_attached());
            assert_eq!(host.clock(), Clock::default());
        }
        fake.set_reply::<calls::HostWindowAttached>(&true);
        let mut host = Host::with_transport(&mut fake);
        assert!(host.window_attached());
    }

    // OpenAC `PluginApiInertDefaultTests.AFellowshipsTermsAndLevelsReadAsNoneByDefault`,
    // then the terms and a level a test sets coming back through the
    // encoding.
    #[test]
    fn a_fellowships_terms_and_levels_read_as_none_until_set() {
        use ac_plugin_abi::social::{FellowMember, FellowshipState};

        let mut fake = FakeHost::new("test.fellowship");
        {
            let mut host = Host::with_transport(&mut fake);
            let fellowship = host.fellowship();
            assert!(!fellowship.shares_experience);
            assert!(!fellowship.splits_experience_evenly);
            assert!(host.fellowship_roster().is_empty());
            assert_eq!(FellowMember::default().level, 0);
        }
        fake.set_reply::<calls::FellowshipState>(&FellowshipState {
            is_in_fellowship: true,
            shares_experience: true,
            ..FellowshipState::default()
        });
        fake.set_reply::<calls::FellowshipRoster>(&vec![FellowMember {
            object_id: 0x5000_0001,
            level: 42,
            ..FellowMember::default()
        }]);
        let mut host = Host::with_transport(&mut fake);
        let fellowship = host.fellowship();
        assert!(fellowship.shares_experience);
        assert!(!fellowship.splits_experience_evenly);
        assert_eq!(host.fellowship_roster()[0].level, 42);
    }

    // OpenAC `ItemAutomation.cs` (`ItemAutomationDefaultsTests`): a host
    // without the join answers a joining move Unavailable; then a result a
    // test sets coming back, and the request carrying the flag.
    #[test]
    fn a_joining_move_is_unavailable_until_set_and_carries_its_flag() {
        use ac_plugin_abi::items::{ItemResult, ItemStatus, MoveRequest};
        let mut fake = FakeHost::new("test.join");
        {
            let mut host = Host::with_transport(&mut fake);
            let result = host.move_item_joining_stack(7, 9, 0, 0, true);
            assert_eq!(result.status, ItemStatus::Unavailable);
        }
        fake.set_reply::<calls::ItemsMoveJoinStack>(&ItemResult::of(ItemStatus::Started));
        let mut host = Host::with_transport(&mut fake);
        let result = host.move_item_joining_stack(7, 9, 3, 2, true);
        assert_eq!(result.status, ItemStatus::Started);
        let sent = fake.requests::<calls::ItemsMoveJoinStack>();
        assert_eq!(
            sent.last(),
            Some(&(
                MoveRequest {
                    object_id: 7,
                    container_id: 9,
                    amount: 3,
                    placement: 2,
                },
                true
            ))
        );
    }

    // A put into the open container answers Unavailable from a host that
    // offers none; then a result a test sets coming back, and the request
    // carrying the item, the container, the amount and the slot.
    #[test]
    fn a_put_into_the_open_container_is_unavailable_until_set_and_carries_its_slot() {
        use ac_plugin_abi::items::{ItemResult, ItemStatus, MoveRequest};
        let mut fake = FakeHost::new("test.put");
        {
            let mut host = Host::with_transport(&mut fake);
            let result = host.put_in_open_container(7, 9, 0, 0);
            assert_eq!(result.status, ItemStatus::Unavailable);
        }
        fake.set_reply::<calls::LootPut>(&ItemResult::of(ItemStatus::Started));
        let mut host = Host::with_transport(&mut fake);
        let result = host.put_in_open_container(7, 9, 2, 3);
        assert_eq!(result.status, ItemStatus::Started);
        assert_eq!(
            fake.requests::<calls::LootPut>().last(),
            Some(&MoveRequest {
                object_id: 7,
                container_id: 9,
                amount: 2,
                placement: 3,
            })
        );
    }

    // The landscape reads answer empty on a host without game files; then
    // the replies a test sets come back, and the request carries the
    // landblocks asked for.
    #[test]
    fn the_landscape_reads_empty_until_set_and_carries_its_landblocks() {
        use ac_plugin_abi::world::{LandblockTerrain, TerrainColor, TerrainVertex};
        let mut fake = FakeHost::new("test.landscape");
        {
            let mut host = Host::with_transport(&mut fake);
            assert!(host.landscape_terrain(&[0xA9B4]).is_empty());
            assert!(host.terrain_palette().is_empty());
        }
        let holtburg = LandblockTerrain {
            landblock: 0xA9B4,
            vertices: vec![
                TerrainVertex {
                    terrain_type: 1,
                    road: 0,
                    height_meters: 66.0,
                };
                81
            ],
        };
        fake.set_reply::<calls::LandscapeTerrain>(&vec![holtburg.clone()]);
        fake.set_reply::<calls::LandscapeTerrainPalette>(&vec![TerrainColor {
            terrain_type: 1,
            rgb: 0x5A_5E30,
            map_rgb: 0x64_9664,
            name: Some("Grassland".into()),
        }]);
        let mut host = Host::with_transport(&mut fake);
        assert_eq!(host.landscape_terrain(&[0xA9B4, 0xFF00]), [holtburg]);
        let palette = host.terrain_palette();
        assert_eq!((palette[0].rgb, palette[0].map_rgb), (0x5A_5E30, 0x64_9664));
        assert_eq!(
            fake.requests::<calls::LandscapeTerrain>(),
            [vec![0xA9B4], vec![0xA9B4, 0xFF00]]
        );
    }

    // The process numbers read as zeros from a host that gives none; then
    // values a test sets coming back through the encoding.
    #[test]
    fn process_stats_read_zero_until_set() {
        use ac_plugin_abi::host::ProcessStats;
        let mut fake = FakeHost::new("test.process");
        {
            let mut host = Host::with_transport(&mut fake);
            assert_eq!(host.process_stats(), ProcessStats::default());
        }
        let set = ProcessStats {
            working_set_bytes: 512 * 1024 * 1024,
            cpu_percent: 3.5,
            handle_count: 900,
        };
        fake.set_reply::<calls::HostProcessStats>(&set);
        let mut host = Host::with_transport(&mut fake);
        assert_eq!(host.process_stats(), set);
    }

    // OpenAC `Automation.cs`: `IEquipmentAutomation.IsBusy` defaults to
    // false on a host that offers no equipment; then a busy a test sets
    // coming back through the encoding.
    #[test]
    fn equipment_reads_not_busy_until_set() {
        let mut fake = FakeHost::new("test.equipment");
        {
            let mut host = Host::with_transport(&mut fake);
            assert!(!host.equipment_busy());
        }
        fake.set_reply::<calls::EquipmentBusy>(&true);
        let mut host = Host::with_transport(&mut fake);
        assert!(host.equipment_busy());
    }

    // OpenAC `ItemAutomation.cs`: `IItemAutomation.ForgetStaleItem` answers
    // Unavailable by default and `PluginAppraisalState.CurrentObjectUnsuccessful`
    // reads false; then a completion and a refusal a test sets coming back
    // through the encoding.
    #[test]
    fn forgetting_a_stale_item_is_unavailable_and_no_refusal_reads_until_set() {
        use ac_plugin_abi::items::{AppraisalState, ItemResult, ItemStatus, LootState};

        let mut fake = FakeHost::new("test.stale");
        {
            let mut host = Host::with_transport(&mut fake);
            assert_eq!(
                host.forget_stale_item(0x8000_0001).status,
                ItemStatus::Unavailable
            );
            assert!(!host.loot_state().appraisal.current_object_unsuccessful);
        }
        fake.set_reply::<calls::ItemsForgetStale>(&ItemResult::of(ItemStatus::Completed));
        fake.set_reply::<calls::LootState>(&LootState {
            appraisal: AppraisalState {
                current_id: 0x8000_0001,
                current_object_unsuccessful: true,
                ..AppraisalState::default()
            },
            ..LootState::default()
        });
        let mut host = Host::with_transport(&mut fake);
        assert_eq!(
            host.forget_stale_item(0x8000_0001).status,
            ItemStatus::Completed
        );
        assert!(host.loot_state().appraisal.current_object_unsuccessful);
    }

    // OpenAC `ItemAutomation.cs`: `PluginInventoryItem.SharedCooldownId` and
    // `CooldownSeconds` read zero and `IsPetDevice` false on an item with no
    // cooldown; then an essence on the summoning cooldown a test sets coming
    // back through the encoding.
    #[test]
    fn an_owned_item_reads_no_shared_cooldown_until_set() {
        use ac_plugin_abi::items::InventoryItem;

        let plain = InventoryItem::default();
        assert_eq!(plain.shared_cooldown_id, 0);
        assert!(plain.cooldown_seconds.abs() < f64::EPSILON);
        assert!(!plain.is_pet_device());
        let mut fake = FakeHost::new("test.cooldown");
        {
            let mut host = Host::with_transport(&mut fake);
            assert!(host.owned_items().is_empty());
        }
        fake.set_reply::<calls::ItemsOwned>(&vec![InventoryItem {
            object_id: 0x7000_5101,
            shared_cooldown_id: InventoryItem::SUMMONING_COOLDOWN_ID,
            cooldown_seconds: 45.0,
            ..InventoryItem::default()
        }]);
        let mut host = Host::with_transport(&mut fake);
        let essence = &host.owned_items()[0];
        assert_eq!(essence.shared_cooldown_id, 213);
        assert!((essence.cooldown_seconds - 45.0).abs() < f64::EPSILON);
        assert!(essence.is_pet_device());
    }

    // API 1.7: a hostile target and a world object carry the server's last
    // position beside the client's own place, through the encoding.
    #[test]
    fn a_hostile_target_carries_the_servers_last_position() {
        use ac_plugin_abi::combat::CombatTarget;
        use ac_plugin_abi::world::{Position, WorldObject};

        let mut fake = FakeHost::new("test.targets");
        let server = Position::from_cell(0x0032_01D9, [87.35, -10.2, 18.01], 90.0);
        fake.set_reply::<calls::CombatHostileTargets>(&vec![CombatTarget {
            object_id: 0x8012_D57D,
            distance: 2.38,
            server_position: Some(server.clone()),
            ..CombatTarget::default()
        }]);
        fake.set_reply::<calls::WorldObject>(&Some(WorldObject {
            object_id: 0x8012_D57D,
            position: Some(Position::from_cell(0x0032_01DE, [92.0, -19.0, 18.0], 0.0)),
            server_position: Some(server.clone()),
            ..WorldObject::default()
        }));
        let mut host = Host::with_transport(&mut fake);
        let targets = host.hostile_targets(5.0);
        assert_eq!(targets[0].server_position.as_ref(), Some(&server));
        let object = host.object(0x8012_D57D).unwrap();
        assert_eq!(object.server_position, Some(server));
        assert_ne!(object.position, object.server_position);
    }

    // OpenAC `NavigationAutomation.cs`: `INavigationAutomation.CheckRoomAhead`
    // answers Unknown by default; then a clear answer a test sets coming
    // back through the encoding.
    #[test]
    fn room_ahead_reads_unknown_until_set() {
        use ac_plugin_abi::navigation::{RoomAhead, RoomAheadStatus};
        use ac_plugin_abi::world::Position;

        let mut fake = FakeHost::new("test.room");
        {
            let mut host = Host::with_transport(&mut fake);
            let room = host.check_room_ahead(3.0);
            assert_eq!(room.status, RoomAheadStatus::Unknown);
            assert!(!room.is_clear());
        }
        let spot = Position::from_cell(0xA9B4_0001, [96.0, 99.0, 0.1], 0.0);
        fake.set_reply::<calls::NavigationRoomAhead>(&RoomAhead {
            status: RoomAheadStatus::Clear,
            position: spot.clone(),
        });
        let mut host = Host::with_transport(&mut fake);
        let room = host.check_room_ahead(3.0);
        assert!(room.is_clear());
        assert_eq!(room.position, spot);
    }

    // The known-spell revision and ids (OpenAC hands
    // out the same lists until `SpellbookChanged`): no lists and revision
    // 0 by default; the cache asks the client for a list once per revision.
    #[test]
    fn known_spells_read_once_per_revision_through_the_cache() {
        use crate::KnownSpellCache;
        use ac_plugin_abi::magic::{SpellInfo, SpellList};

        let spell = |spell_id| SpellInfo {
            spell_id,
            ..SpellInfo::default()
        };
        let mut fake = FakeHost::new("test.spells");
        let mut cache = KnownSpellCache::new();
        {
            let mut host = Host::with_transport(&mut fake);
            assert_eq!(host.known_spells_revision(), 0);
            assert!(host.known_spell_ids(SpellList::SelfBuffs).is_empty());
            assert!(cache.list(&mut host, SpellList::SelfBuffs).is_empty());
        }
        assert_eq!(cache.revision(), Some(0));
        fake.set_reply::<calls::SpellsKnownIds>(&vec![10, 11]);
        fake.set_reply::<calls::SpellsKnown>(&vec![spell(10), spell(11)]);
        {
            let mut host = Host::with_transport(&mut fake);
            assert_eq!(host.known_spell_ids(SpellList::SelfBuffs), [10, 11]);
            // Same revision: the kept (empty) list.
            assert!(cache.list(&mut host, SpellList::SelfBuffs).is_empty());
        }
        fake.set_reply::<calls::SpellsKnownRevision>(&1);
        {
            let mut host = Host::with_transport(&mut fake);
            for _ in 0..3 {
                let ids: Vec<u32> = cache
                    .list(&mut host, SpellList::SelfBuffs)
                    .iter()
                    .map(|s| s.spell_id)
                    .collect();
                assert_eq!(ids, [10, 11]);
            }
            assert_eq!(cache.list(&mut host, SpellList::Combat).len(), 2);
        }
        // Self buffs read at revision 0 and once at 1; combat once.
        assert_eq!(
            fake.requests::<calls::SpellsKnown>(),
            [
                SpellList::SelfBuffs,
                SpellList::SelfBuffs,
                SpellList::Combat
            ]
        );
        cache.clear();
        assert_eq!(cache.revision(), None);
    }

    /// Records the pointer events it hears on its canvas.
    #[derive(Default)]
    struct PointerRecorder {
        canvas: Option<crate::ui::Canvas>,
        heard: Vec<(u8, f32, f32)>,
    }

    impl Plugin for PointerRecorder {
        fn enable(&mut self, host: &mut Host<'_>, _: &StartInfo) -> Result<(), String> {
            let canvas = host.register_canvas(&crate::ui::CanvasSpec {
                anchor: crate::ui::Anchor::TopRight,
                offset: (-20.0, 30.0),
                accepts_pointer_input: true,
                ..crate::ui::CanvasSpec::new("hud", 100, 50)
            })?;
            canvas.listen_pointer(host, true);
            self.canvas = Some(canvas);
            Ok(())
        }
        fn on_event(&mut self, _host: &mut Host<'_>, event: Event) {
            if let Event::Ui(crate::ui::UiEvent::Pointer(p)) = event {
                self.heard.push((p.kind, p.x, p.y));
            }
        }
    }

    /// A plugin's own test hears a held pointer as the client hands it
    /// over: a move that carries the window's place is measured from where
    /// the plugin last put the canvas; a move with no place is heard as
    /// sent.
    #[test]
    fn a_held_pointer_is_heard_as_the_client_measures_it() {
        use ac_plugin_ui::{CanvasPlace, PointerEvent, PointerKind, UiInput};
        let mut host = FakeHost::new("test.pointer");
        let mut plugin = PointerRecorder::default();
        host.enable(&mut plugin).unwrap();
        let canvas = plugin.canvas.unwrap();
        // The window has it at (680, 30) on an 800 x 600 layer.
        let place = Some(CanvasPlace {
            origin: (680.0, 30.0),
            layer: (800.0, 600.0),
        });
        let pointer = |kind, place| UiInput::Pointer {
            canvas: canvas.id(),
            event: PointerEvent {
                kind,
                x: 15.0,
                y: 25.0,
                button: 1,
                modifiers: 2,
                wheel_delta: 0,
            },
            place,
        };
        host.ui_input(&mut plugin, pointer(PointerKind::Down, place));
        // The plugin moves its canvas 10 right and 10 down; the window has
        // not caught up.
        host.with_host(|h| canvas.set_offset(h, -10.0, 40.0));
        host.ui_input(&mut plugin, pointer(PointerKind::Move, place));
        host.ui_input(&mut plugin, pointer(PointerKind::Move, None));
        host.ui_input(&mut plugin, pointer(PointerKind::Up, place));
        assert_eq!(
            plugin.heard,
            vec![
                (0, 15.0, 25.0),
                (2, 5.0, 15.0),
                (2, 15.0, 25.0),
                (1, 5.0, 15.0)
            ]
        );
    }

    /// A world map inside a window, and a dungeon map on the screen.
    #[derive(Default)]
    struct MapPlugin {
        map: Option<crate::ui::Canvas>,
        hud: Option<crate::ui::Canvas>,
        /// The sizes the map heard and the sizes it painted at.
        resized: Vec<(i32, i32)>,
        painted: Vec<(i32, i32)>,
    }

    impl Plugin for MapPlugin {
        fn enable(&mut self, host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
            self.map =
                Some(host.register_canvas(&crate::ui::CanvasSpec::in_markup("map", 64, 48))?);
            self.hud = Some(host.register_canvas(&crate::ui::CanvasSpec::new("hud", 100, 50))?);
            Ok(())
        }
        fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
            if let Event::CanvasResized(size) = event
                && let Some(map) = self.map
                && size.canvas == map.id()
            {
                self.resized.push((size.width, size.height));
                let mut at = (0, 0);
                map.paint(host, |p| at = (p.width(), p.height())).unwrap();
                self.painted.push(at);
            }
        }
    }

    /// A plugin's own test lays its in-markup canvas out as a window would:
    /// the plugin hears each new size and paints at it, and its state says
    /// so; a canvas on the screen resizes, and the in-markup one refuses.
    #[test]
    fn an_in_markup_canvas_hears_its_laid_out_size_and_paints_at_it() {
        let mut host = FakeHost::new("test.atlas");
        let mut plugin = MapPlugin::default();
        host.enable(&mut plugin).unwrap();
        host.attach_window(&mut plugin, true);
        let mut map = plugin.map.unwrap();
        assert!(map.in_markup());
        assert!(!host.with_host(|h| map.is_available(h)));
        host.host_canvas(&mut plugin, map.id(), Some((320, 240)));
        host.host_canvas(&mut plugin, map.id(), Some((320, 240)));
        host.host_canvas(&mut plugin, map.id(), Some((400, 240)));
        assert_eq!(plugin.resized, vec![(320, 240), (400, 240)]);
        assert_eq!(plugin.painted, plugin.resized);
        assert!(host.with_host(|h| map.is_available(h)));
        let state = host.with_host(|h| map.state(h)).unwrap();
        assert_eq!((state.width, state.height), (400, 240));
        assert_eq!((map.width(), map.height()), (64, 48), "as registered");
        assert!(!host.with_host(|h| map.resize(h, 10, 10)));
        host.host_canvas(&mut plugin, map.id(), None);
        assert!(!host.with_host(|h| map.is_available(h)));
        let mut hud = plugin.hud.unwrap();
        assert!(host.with_host(|h| hud.resize(h, 200, 80)));
        assert_eq!((hud.width(), hud.height()), (200, 80));
        let state = host.with_host(|h| hud.state(h)).unwrap();
        assert_eq!((state.width, state.height), (200, 80));
        assert!(!host.with_host(|h| hud.resize(h, 0, 80)));
        assert_eq!((hud.width(), hud.height()), (200, 80), "unchanged");
    }
}
