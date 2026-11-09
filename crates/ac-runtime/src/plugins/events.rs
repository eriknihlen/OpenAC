//! API group B: the events plugins hear, projected from the session's own
//! changes, and [`PluginServices`], the runtime state kept only for
//! plugins (revisions, what was last reported, pending activations).
//!
//! The runner calls [`objects_changed`] with the world's changes of each
//! pass, [`host_events`] with the pass's host events and [`follow`] once a
//! pass; each returns the plugin events in order (OpenAC `WorldEvents`
//! fired by `RuntimeAutomationSurface`'s observers).

use std::collections::HashMap;
use std::time::Instant;

use ac_core::items::{MoveOrigin, RemovalReason};
use ac_plugin::abi::Event;
use ac_plugin::abi::items::{EquipmentObservation, ItemUseCompletion};
use ac_plugin::abi::world::{
    ActivationCompletion, ActivationOutcome, Confirmation, ObjectChange, ObjectChangeKind,
    PortalTransition, SelectionChange,
};

use super::world::Ownership;
use crate::communication::ClientConfirmation;
use crate::gameplay::{GameplayEvent, SelectionSource};
use crate::local_time::LocalZone;
use crate::session::{HostEvent, SessionState};
use crate::world::transit::{PortalKind, PortalSnapshot};
use crate::world::{ItemChange, World, WorldChange};

/// Who changed the selection, as plugins read it
/// ([`ac_plugin::abi::world::selection_source`]).
fn selection_source_code(source: SelectionSource) -> u8 {
    use ac_plugin::abi::world::selection_source as code;
    match source {
        SelectionSource::System => code::SYSTEM,
        SelectionSource::Keyboard => code::KEYBOARD,
        SelectionSource::Mouse => code::MOUSE,
        SelectionSource::Host => code::PLUGIN,
    }
}

/// The runtime state kept for plugins, owned by the session state.
#[derive(Debug)]
pub struct PluginServices {
    started: Instant,
    zone: LocalZone,
    seed: u64,
    object_revision: i64,
    /// The last cell each object was reported in (a move is reported when
    /// it crosses into another).
    cells: HashMap<u32, u32>,
    /// The appraisal stamp each item was last reported with.
    appraisals: HashMap<u32, i32>,
    was_in_world: bool,
    /// The plugins were ticked after hearing this stay's arrival.
    ticked_in_stay: bool,
    open_container: u32,
    selected: Option<u32>,
    last_portal: Option<PortalSnapshot>,
    portal_revision: i64,
    pub(super) activations: Activations,
    /// Dungeon floorplans, built once per landblock.
    pub(super) floorplans: ac_nav::floorplan::DungeonFloorplanBuilder,
    /// Landblocks' indoor cells, read once each.
    pub(super) indoor_cells: ac_nav::indoor::IndoorCellCache,
    /// Landblocks' outdoor terrain, read once each.
    pub(super) landscape: ac_nav::landscape::LandscapeTerrainCache,
    /// The region's terrain colours, worked out once.
    pub(super) terrain_palette: Option<Vec<ac_plugin::abi::world::TerrainColor>>,
    /// What plugins reported and the client confirmed of enchantments.
    pub(super) enchantments: super::magic::EnchantmentTracker,
    /// The known-spell lists plugins read, and their revision.
    pub(super) known_spells: super::magic::KnownSpells,
    /// The creature a plugin-driven swing was armed at, 0 for none.
    pub(super) armed_attack_target: u32,
    /// The plugin holding combat control, if one does.
    pub(super) combat_control: Option<String>,
    /// The trade and vendor as last reported, and the plugin vendor lists.
    pub(super) trade: super::trade::TradeWatch,
    /// The navigation snapshot and walk report as last published, and
    /// the plugins' pauses.
    pub(super) navigation: super::navigation::NavigationWatch,
    /// The recalls plugins asked for and the places they taught.
    pub(super) recalls: super::social::RecallWatch,
    /// The chat lines plugins can read back, and links clicked.
    pub(crate) chat: super::chat::ChatWatch,
    /// The attached window reported itself minimized.
    pub(crate) window_minimized: bool,
    /// This client's part in the registry of clients on this computer.
    pub(crate) peers: super::peer_link::PeerLink,
    /// The last answered use reported.
    use_revision: u64,
    /// The character's experience as last reported.
    experience: Option<ac_plugin::abi::character::CharacterExperience>,
    /// Where the player's plugin hotkey overrides are kept, if anywhere.
    pub(crate) hotkey_file: Option<std::path::PathBuf>,
    /// Each plugin's CPU baseline for `host_process_stats`, from its
    /// previous call; dropped when it unloads.
    pub(super) cpu_meters: HashMap<String, ac_host::process_stats::CpuMeter>,
}

/// Activations a plugin started and has not heard the end of.
#[derive(Debug, Default)]
pub(super) struct Activations {
    revision: i64,
    pending: Vec<u32>,
    pub(super) last: ActivationCompletion,
}

impl Activations {
    /// A plugin activated `object`.
    pub(super) fn started(&mut self, object: u32) {
        self.revision += 1;
        if !self.pending.contains(&object) {
            self.pending.push(object);
        }
    }

    /// Every pending activation ends with `outcome` (one revision for the
    /// lot, as OpenAC resolves them together).
    fn resolve(&mut self, outcome: ActivationOutcome, events: &mut Vec<Event>) {
        if self.pending.is_empty() {
            return;
        }
        self.revision += 1;
        for object_id in std::mem::take(&mut self.pending) {
            self.last = ActivationCompletion {
                revision: self.revision,
                object_id,
                outcome,
                weenie_error: 0,
            };
            events.push(Event::ActivationCompleted(self.last.clone()));
        }
    }
}

impl Default for PluginServices {
    fn default() -> Self {
        let started = Instant::now();
        let wall = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos());
        Self {
            started,
            zone: LocalZone::system(),
            // The low bits of the wall clock in nanoseconds: different for
            // every session, which is all a seed needs.
            #[allow(clippy::cast_possible_truncation)]
            seed: wall as u64 ^ 0x9E37_79B9_7F4A_7C15,
            object_revision: 0,
            cells: HashMap::new(),
            appraisals: HashMap::new(),
            was_in_world: false,
            ticked_in_stay: false,
            open_container: 0,
            selected: None,
            last_portal: None,
            portal_revision: 0,
            activations: Activations::default(),
            floorplans: ac_nav::floorplan::DungeonFloorplanBuilder::new(),
            indoor_cells: ac_nav::indoor::IndoorCellCache::new(),
            landscape: ac_nav::landscape::LandscapeTerrainCache::new(),
            terrain_palette: None,
            enchantments: super::magic::EnchantmentTracker::default(),
            known_spells: super::magic::KnownSpells::default(),
            armed_attack_target: 0,
            combat_control: None,
            use_revision: 0,
            experience: None,
            hotkey_file: None,
            cpu_meters: HashMap::new(),
            trade: super::trade::TradeWatch::default(),
            navigation: super::navigation::NavigationWatch::default(),
            recalls: super::social::RecallWatch::default(),
            chat: super::chat::ChatWatch::default(),
            window_minimized: false,
            peers: super::peer_link::PeerLink::default(),
        }
    }
}

impl PluginServices {
    /// How many seconds local time is ahead of UTC at `unix_ms`.
    pub(super) fn utc_offset_seconds(&self, unix_ms: i64) -> i32 {
        self.zone.utc_offset_seconds(unix_ms)
    }

    /// A fixed zone for tests: the machine's own changes its offset twice
    /// a year, and a test's fixed wall time then sits on the other side.
    #[cfg(test)]
    pub(super) fn set_zone(&mut self, zone: LocalZone) {
        self.zone = zone;
    }

    /// Milliseconds since the session began; never goes back.
    pub(super) fn monotonic_ms(&self) -> u64 {
        u64::try_from(self.started.elapsed().as_millis()).unwrap_or(u64::MAX)
    }

    /// A fresh seed (SplitMix64 over the session's counter).
    pub(crate) fn next_seed(&mut self) -> u64 {
        self.seed = self.seed.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.seed;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    /// One object change, its state read with the pass's `ownership`.
    fn change(
        &mut self,
        world: &World,
        ownership: &Ownership<'_>,
        object_id: u32,
        kind: ObjectChangeKind,
    ) -> Event {
        self.object_revision += 1;
        let current = (kind != ObjectChangeKind::Released)
            .then(|| super::world::project_object_owned(world, object_id, ownership))
            .flatten();
        Event::ObjectChanged(ObjectChange {
            object_id,
            kind,
            revision: self.object_revision,
            changed_fields: kind.fields(),
            current,
        })
    }
}

/// An object entering the world with a place is an object appearing
/// (OpenAC `RuntimeWorldEntityProjection.OnEntity`, `Registered`).
fn spawned(world: &crate::world::World, out: &mut Vec<Event>, guid: u32) {
    if let Some(entity) = super::world::entity_snapshot(world, guid) {
        out.push(Event::EntitySpawned(entity));
    }
}

/// The object changes of one pass's world changes (OpenAC `OnEntity` and
/// `OnInventory`): a first sighting is a creation, a re-creation a release
/// then a creation, a cell crossing (or a move between containers) a move,
/// a description an update (an appraisal's an ident), and anything leaving
/// the table a release. An object is reported once per kind in a pass.
pub fn objects_changed(state: &mut SessionState, changes: &[WorldChange]) -> Vec<Event> {
    let SessionState {
        world,
        plugin_services: services,
        ..
    } = state;
    let world: &World = world;
    // The world does not change during the pass, so one ownership serves
    // every change in it: the contents lists are walked once at most.
    let ownership = Ownership::of(world.items(), world.player_guid().unwrap_or(0));
    let mut seen: Vec<(u32, ObjectChangeKind)> = Vec::new();
    let mut out = Vec::new();
    let mut emit = |services: &mut PluginServices, out: &mut Vec<Event>, id, kind| {
        if id == 0 || seen.contains(&(id, kind)) {
            return;
        }
        seen.push((id, kind));
        out.push(services.change(world, &ownership, id, kind));
    };
    for change in changes {
        match *change {
            WorldChange::Spawned(key) => {
                if let Some(guid) = world.guid_of_local_id(key.local_id) {
                    note_cell(services, world, guid);
                    emit(services, &mut out, guid, ObjectChangeKind::Created);
                    spawned(world, &mut out, guid);
                }
            }
            WorldChange::Respawned(key) => {
                if let Some(guid) = world.guid_of_local_id(key.local_id) {
                    note_cell(services, world, guid);
                    emit(services, &mut out, guid, ObjectChangeKind::Released);
                    emit(services, &mut out, guid, ObjectChangeKind::Created);
                    spawned(world, &mut out, guid);
                }
            }
            WorldChange::Refreshed(key) => {
                if let Some(guid) = world.guid_of_local_id(key.local_id) {
                    emit(services, &mut out, guid, ObjectChangeKind::Updated);
                }
            }
            WorldChange::Removed(key) => {
                if let Some(guid) = world.guid_of_local_id(key.local_id) {
                    services.cells.remove(&guid);
                    if world.item(guid).is_none() {
                        services.appraisals.remove(&guid);
                    }
                    emit(services, &mut out, guid, ObjectChangeKind::Released);
                }
            }
            WorldChange::Moved { key, .. } => {
                let Some(guid) = world.guid_of_local_id(key.local_id) else {
                    continue;
                };
                let cell = world
                    .object(guid)
                    .and_then(|o| o.position())
                    .map(|p| p.cell_id);
                let before = match cell {
                    Some(cell) => services.cells.insert(guid, cell),
                    None => continue,
                };
                if before.is_some_and(|b| Some(b) != cell) {
                    emit(services, &mut out, guid, ObjectChangeKind::Moved);
                }
            }
            WorldChange::Item(item) => match item {
                ItemChange::Added(guid) => {
                    note_appraisal(services, world, guid);
                    emit(services, &mut out, guid, ObjectChangeKind::Created);
                    // An item that enters the world already worn (OpenAC
                    // `PublishInitialEquipmentPlacement`).
                    if let Some(added) = world.item(guid)
                        && added.currently_equipped_location.bits() != 0
                        && ownership.owns(added)
                    {
                        out.push(Event::EquipmentPlacement(EquipmentObservation {
                            object_id: guid,
                            equipped_location: added.currently_equipped_location.bits(),
                            is_removal: false,
                            is_initial_placement: true,
                        }));
                    }
                }
                ItemChange::Updated(guid) => {
                    let stamp = world.item(guid).map_or(0, |i| i.last_appraisal_time_ms);
                    let before = services.appraisals.insert(guid, stamp).unwrap_or(0);
                    let kind = if stamp != 0 && stamp != before {
                        ObjectChangeKind::IdentReceived
                    } else {
                        ObjectChangeKind::Updated
                    };
                    emit(services, &mut out, guid, kind);
                }
                ItemChange::Moved {
                    guid,
                    current,
                    origin,
                    ..
                } => {
                    emit(services, &mut out, guid, ObjectChangeKind::Moved);
                    // A server-confirmed placement (OpenAC
                    // `OnEquipmentObjectMoved`).
                    if origin == MoveOrigin::AuthoritativeResponse {
                        let location = current.equip_location.bits();
                        out.push(Event::EquipmentPlacement(EquipmentObservation {
                            object_id: guid,
                            equipped_location: location,
                            is_removal: location == 0,
                            is_initial_placement: false,
                        }));
                    }
                }
                ItemChange::Removed { guid, reason } => {
                    services.appraisals.remove(&guid);
                    emit(services, &mut out, guid, ObjectChangeKind::Released);
                    // OpenAC `OnEquipmentObjectRemoved`: a logical delete.
                    if reason == RemovalReason::LogicalDelete {
                        out.push(Event::EquipmentPlacement(EquipmentObservation {
                            object_id: guid,
                            equipped_location: 0,
                            is_removal: true,
                            is_initial_placement: false,
                        }));
                    }
                }
                // A contents list or the whole table carries no object id
                // and is not reported (OpenAC skips "Cleared").
                ItemChange::ContentsChanged(_) | ItemChange::Cleared => {}
            },
            WorldChange::MotionChanged(_)
            | WorldChange::StateChanged(_)
            | WorldChange::VectorChanged(_)
            | WorldChange::AppearanceChanged(_)
            | WorldChange::Parented(_)
            | WorldChange::PickedUp(_)
            | WorldChange::PlayerChanged => {}
        }
    }
    out
}

fn note_cell(services: &mut PluginServices, world: &World, guid: u32) {
    if let Some(cell) = world
        .object(guid)
        .and_then(|o| o.position())
        .map(|p| p.cell_id)
    {
        services.cells.insert(guid, cell);
    }
}

fn note_appraisal(services: &mut PluginServices, world: &World, guid: u32) {
    let stamp = world.item(guid).map_or(0, |i| i.last_appraisal_time_ms);
    services.appraisals.insert(guid, stamp);
}

/// The plugin events one pass's host events carry: a death, the server's
/// question, a portal transition (and the activations it ends).
pub fn host_events(state: &mut SessionState, events: &[HostEvent]) -> Vec<Event> {
    let house = state.gameplay.house().position().copied();
    let services = &mut state.plugin_services;
    let mut out = Vec::new();
    for event in events {
        match event {
            HostEvent::Gameplay(gameplay) => {
                if let GameplayEvent::LocalPlayerDied(message) = gameplay.as_ref() {
                    out.push(Event::LocalPlayerDied(message.clone()));
                }
            }
            // A line the plugins heard right after the call that wrote it
            // is retained already.
            HostEvent::Chat(line) if !services.chat.heard_early(line.sequence) => {
                services.chat.retain(super::plugin_chat_line(line));
            }
            HostEvent::Confirmation(ClientConfirmation::Server {
                confirmation_type,
                context_id,
                message,
            }) => out.push(Event::ConfirmationRequested(Confirmation {
                context_id: *context_id,
                kind: i32::try_from(*confirmation_type).unwrap_or(i32::MAX),
                text: message.clone(),
            })),
            HostEvent::Portal(snapshot) => portal(services, snapshot, house.as_ref(), &mut out),
            _ => {}
        }
    }
    out
}

/// A portal snapshot that differs from the last one reported is a
/// transition; its end resolves every pending activation (OpenAC
/// `OnPortal`).
fn portal(
    services: &mut PluginServices,
    snapshot: &PortalSnapshot,
    house: Option<&ac_proto::Position>,
    out: &mut Vec<Event>,
) {
    if services.last_portal.as_ref() == Some(snapshot) {
        return;
    }
    services.last_portal = Some(*snapshot);
    let recall_request_revision = services.recalls.take_pending();
    services.portal_revision += 1;
    out.push(Event::PortalTransition(PortalTransition {
        revision: services.portal_revision,
        generation: i64::try_from(snapshot.generation).unwrap_or(i64::MAX),
        destination_cell: snapshot.readiness.destination_cell,
        is_ready: snapshot.readiness.is_ready(),
        is_materialized: snapshot.materialized,
        is_completed: snapshot.completed,
        is_cancelled: snapshot.cancelled,
        recall_request_revision,
        kind: match snapshot.kind {
            PortalKind::None => 0,
            PortalKind::Login => 1,
            PortalKind::Portal => 2,
        },
    }));
    if snapshot.completed || snapshot.cancelled {
        let outcome = if snapshot.completed {
            ActivationOutcome::Completed
        } else {
            ActivationOutcome::Interrupted
        };
        services.activations.resolve(outcome, out);
        // A completed transition a recall asked for teaches its place.
        if snapshot.completed && recall_request_revision > 0 {
            services
                .recalls
                .learn(snapshot.readiness.destination_cell, house);
        }
    }
}

/// Whether the plugins are in a stay in the world: they heard
/// `LoginComplete` and not yet `Logoff`. What they read of the character
/// and its things follows this, so a read agrees with the last event.
#[must_use]
pub fn in_stay(state: &SessionState) -> bool {
    state.plugin_services.was_in_world
}

/// The plugins were ticked after hearing the arrival: the runner notes it
/// after a pump in the stay that raised ticks (a pump hands out the events
/// queued before its ticks, so the arrival came first). Nothing outside a
/// stay.
pub fn note_ticked(state: &mut SessionState) {
    let services = &mut state.plugin_services;
    if services.was_in_world {
        services.ticked_in_stay = true;
    }
}

/// Whether the plugins heard this stay's arrival and have been ticked since
/// (what they do on their first tick in the world is done). The login
/// commands wait for it: OpenAC's plugins arrive when the enter-world
/// request goes out, a round trip before the server listens and the
/// commands run, and a plugin's first tick in the world readies what a
/// command then uses.
#[must_use]
pub fn ticked_in_stay(state: &SessionState) -> bool {
    state.plugin_services.ticked_in_stay
}

/// The character is leaving the world (OpenAC `AnnounceLeavingWorld`):
/// once per stay, the pending activations end interrupted, then `Logoff`.
/// The runner calls this before anything of the stay is torn down, so the
/// plugins hearing it can still read what they want to save.
pub fn leave(state: &mut SessionState) -> Vec<Event> {
    let services = &mut state.plugin_services;
    let mut out = Vec::new();
    if !services.was_in_world {
        return out;
    }
    services.was_in_world = false;
    services.ticked_in_stay = false;
    services
        .activations
        .resolve(ActivationOutcome::Interrupted, &mut out);
    out.push(Event::Logoff);
    // What the character that left had is not the next one's.
    if services.experience.take().is_some() {
        out.push(Event::ExperienceChanged(None));
    }
    out
}

/// The character's experience when it reads differently from what the
/// plugins last heard (OpenAC `RefreshExperience`): the description, an
/// update that changes a value, a level-up.
fn follow_experience(state: &mut SessionState, out: &mut Vec<Event>) {
    let now = super::character::experience(state);
    let services = &mut state.plugin_services;
    if services.experience != now {
        services.experience.clone_from(&now);
        out.push(Event::ExperienceChanged(now));
    }
}

/// What changed in the state since the last pass that plugins hear about:
/// the arrival in the world, the open outside container (a replacement
/// closes the old one first) and the selection.
///
/// The arrival (`LoginComplete`) is heard once the server listens to the
/// character (`listening`: the client has said its login is complete),
/// not when the client enters the world: what a plugin asks of the server
/// on arriving is otherwise dropped. The leaving is [`leave`].
pub fn follow(state: &mut SessionState, in_world: bool, listening: bool) -> Vec<Event> {
    let open = state.inventory.external_containers().current_container_id();
    let selected = state.gameplay.selection().selected();
    let selection_source = state.gameplay.selection().source();
    let completions = *state.inventory.completions();
    let services = &mut state.plugin_services;
    let mut out = Vec::new();
    // A use the client awaited was answered (OpenAC `OnUseCompleted`).
    if completions.use_revision != services.use_revision {
        services.use_revision = completions.use_revision;
        if completions.use_revision != 0 {
            out.push(Event::ItemUseCompleted(ItemUseCompletion {
                revision: i64::try_from(completions.use_revision).unwrap_or(i64::MAX),
                source_id: completions.use_source,
                target_id: completions.use_target,
                weenie_error: completions.use_error,
            }));
        }
    }
    if listening && !services.was_in_world {
        services.was_in_world = true;
        out.push(Event::LoginComplete);
    }
    if services.open_container != open {
        if services.open_container != 0 {
            out.push(Event::ContainerClosed(services.open_container));
        }
        if open != 0 {
            out.push(Event::ContainerOpened(open));
        }
        services.open_container = open;
    }
    // Compared once a pass: a selection that changed twice in the pass is
    // reported once, with who made the last change.
    if services.selected != selected {
        out.push(Event::SelectionChanged(SelectionChange {
            previous: services.selected,
            selected,
            source: selection_source_code(selection_source),
        }));
        services.selected = selected;
    }
    for link in std::mem::take(&mut state.plugin_services.chat.links) {
        out.push(Event::ChatLinkClicked(link));
    }
    follow_experience(state, &mut out);
    out.extend(super::trade::follow(state));
    out.extend(super::navigation::follow(state, in_world));
    out
}
