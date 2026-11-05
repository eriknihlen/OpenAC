//! `probe`: reads the runtime facts every plugin can read and logs them, so
//! a harness can run it in a session with a window and in one without and
//! compare the two transcripts (the parity harness). It declares
//! no permissions: it proves a plugin that needs none links and runs.
//!
//! It logs its facts when enabled, on `/probe`, on its 100th tick, and on
//! every window-attached event. `/probe census` (and `/probe`) also calls
//! every read the API offers without a permission and logs one line per
//! call, `probe census <import>: <summary>`, worded so the same session
//! gives the same lines with or without a window (only the
//! window-attached query differs, by design). `/probe events` logs the
//! events it heard since the last time.

use openac_plugin::calls;
use openac_plugin::items::{LootAction, LootClassification};
use openac_plugin::magic::SpellList;
use openac_plugin::{Callback, CallbackAnswer};
use openac_plugin::{Event, Host, Plugin, StartInfo};

#[derive(Default)]
pub struct Probe {
    ticks: u64,
    heard: Vec<String>,
    /// Every object change and entity spawn heard, by kind.
    counted: std::collections::BTreeMap<String, u32>,
    classifier: u32,
}

impl Probe {
    fn facts(host: &mut Host<'_>, why: &str) {
        let character = host.character().map_or_else(
            || "none".to_owned(),
            |c| format!("{} 0x{:08X} on {}", c.name, c.id, c.world),
        );
        let setting = host.setting("label").unwrap_or_else(|| "-".to_owned());
        let window = host.window_attached();
        host.info(&format!(
            "probe {why}: character={character} label={setting} window={window}"
        ));
    }

    /// The character and how many objects and owned items the plugin can
    /// read now.
    fn world_summary(host: &mut Host<'_>) -> String {
        let character = host
            .character()
            .map_or_else(|| "none".to_owned(), |c| c.name);
        let objects = host.call::<calls::WorldObjects>(&()).len();
        let owned = host.owned_items().len();
        format!("character={character} objects={objects} owned={owned}")
    }

    fn line(host: &mut Host<'_>, import: &str, summary: &str) {
        host.info(&format!("probe census {import}: {summary}"));
    }

    /// One line per read, each summarised without anything that changes
    /// from run to run (clock readings, seeds).
    fn census(host: &mut Host<'_>) {
        // A. Host services.
        let settings = host.settings();
        Self::line(host, "host_settings", &format!("{} keys", settings.len()));
        let clock = host.clock();
        Self::line(
            host,
            "host_clock",
            &format!(
                "utc>0={} offset%15={}",
                clock.utc_unix_ms > 0,
                clock.utc_offset_minutes % 15 == 0
            ),
        );
        let process = host.process_stats();
        Self::line(
            host,
            "host_process_stats",
            &format!(
                "working_set>0={} handles>0={} cpu>=0={}",
                process.working_set_bytes > 0,
                process.handle_count > 0,
                process.cpu_percent >= 0.0
            ),
        );
        // D. Character.
        let info = host.character();
        let summary = info.map_or_else(
            || "none".to_owned(),
            |c| {
                format!(
                    "{} level={} index={} slots={} health={}/{} titles={} vitae={:.2}",
                    c.name,
                    c.level,
                    c.character_index,
                    c.main_pack_free_slots,
                    c.health.0,
                    c.health.1,
                    c.titles.len(),
                    c.vitae_multiplier
                )
            },
        );
        Self::line(host, "character_info", &summary);
        let skills = host.call::<calls::CharacterSkills>(&());
        let trained = skills.iter().filter(|s| s.training as u8 >= 2).count();
        Self::line(
            host,
            "character_skills",
            &format!("{} skills, {trained} trained", skills.len()),
        );
        let attributes = host.call::<calls::CharacterAttributes>(&());
        let attributes: Vec<String> = attributes
            .iter()
            .map(|a| format!("{}={}", a.name, a.base))
            .collect();
        Self::line(host, "character_attributes", &attributes.join(" "));
        let vitals = host.call::<calls::CharacterVitals>(&());
        let vitals: Vec<String> = vitals
            .iter()
            .map(|v| format!("{}={}", v.name, v.base))
            .collect();
        Self::line(host, "character_vitals", &vitals.join(" "));
        let enchantments = host.call::<calls::CharacterEnchantments>(&false);
        Self::line(
            host,
            "character_enchantments",
            &format!("{} in effect", enchantments.len()),
        );
        let options = host.call::<calls::CharacterOptions>(&());
        let on = options.iter().filter(|(_, value)| *value).count();
        Self::line(
            host,
            "character_options",
            &format!("{} options, {on} on", options.len()),
        );
        // C. World reads.
        let objects = host.call::<calls::WorldObjects>(&());
        let owned = objects.iter().filter(|o| o.is_owned).count();
        Self::line(
            host,
            "world_objects",
            &format!("{} objects, {owned} owned", objects.len()),
        );
        let me = info_id(host);
        let me_object = host.call::<calls::WorldObject>(&me);
        Self::line(
            host,
            "world_object",
            &me_object.map_or_else(
                || "none".to_owned(),
                |o| format!("{} class={:?}", o.name, o.object_class),
            ),
        );
        let properties = host.call::<calls::WorldProperties>(&me);
        Self::line(
            host,
            "world_properties",
            &properties.map_or_else(|| "none".to_owned(), |p| format!("{} ints", p.ints.len())),
        );
        let level = host.call::<calls::WorldIntProperty>(&(me, 25));
        Self::line(host, "world_int_property", &format!("level={level:?}"));
        let open = host.call::<calls::WorldOpenContainer>(&());
        Self::line(host, "world_open_container", &format!("0x{open:08X}"));
        let activation = host.call::<calls::WorldLastActivation>(&());
        Self::line(
            host,
            "world_last_activation",
            &format!("revision={}", activation.revision),
        );
        let entities = host.call::<calls::WorldEntities>(&());
        Self::line(
            host,
            "world_entities",
            &format!("{} placed", entities.len()),
        );
        let scenery = host.scenery();
        let models = scenery.iter().filter(|s| s.source_id >> 24 == 0x01).count();
        Self::line(
            host,
            "world_scenery",
            &format!("{} pieces, {models} single models", scenery.len()),
        );
        let landblock = host.current_landblock();
        let cells = host.indoor_cells(landblock);
        let seeing = cells.iter().filter(|c| c.sees_outside).count();
        Self::line(
            host,
            "dungeon_indoor_cells",
            &format!(
                "0x{landblock:08X}: {} cells, {seeing} see outside, first {}",
                cells.len(),
                cells.first().map_or_else(
                    || "none".to_owned(),
                    |c| format!(
                        "0x{:08X} env=0x{:04X} yaw={:.0}",
                        c.cell_id,
                        c.environment_id,
                        c.yaw_degrees()
                    )
                ),
            ),
        );
        let contracts = host.call::<calls::WorldContracts>(&());
        let stages: Vec<String> = contracts
            .iter()
            .map(|c| {
                format!(
                    "{:#x} stage={} progress={} named={}",
                    c.contract_id,
                    c.stage,
                    c.progress,
                    !c.name.is_empty()
                )
            })
            .collect();
        Self::line(
            host,
            "world_contracts",
            &format!("{} tracked [{}]", contracts.len(), stages.join("; ")),
        );
        let available = host.projectiles_available();
        Self::line(host, "projectiles_available", &format!("{available}"));
        // A shot at the nearest hostile creature, if one is near.
        let nearest = host
            .hostile_targets(30.0)
            .into_iter()
            .min_by(|a, b| a.distance.total_cmp(&b.distance));
        let shot = nearest.map(|t| {
            host.evaluate_projectile_path(&openac_plugin::projectile::ProjectilePathQuery {
                target_id: t.object_id,
                kind: openac_plugin::projectile::ProjectilePathKind::Straight,
                target_height: openac_plugin::combat::AttackHeight::Medium,
                projectile_radius: 0.4,
                step_distance: 0.7,
                maximum_collision_checks: 500,
                diagnostics: false,
            })
            .status
        });
        Self::line(host, "projectiles_evaluate_path", &format!("{shot:?}"));
        let files = host.resource_list("");
        Self::line(
            host,
            "resources_list",
            &format!(
                "manifest={}",
                files
                    .as_ref()
                    .is_ok_and(|f| f.iter().any(|k| k == "plugin.json"))
            ),
        );
        let data = host.resource_list_data_files("routes");
        Self::line(
            host,
            "resources_list_data_files",
            &format!("{:?}", data.map(|d| d.len())),
        );
        let time = host.call::<calls::WorldTime>(&());
        Self::line(
            host,
            "world_time",
            &format!("available={} year={}", time.is_available, time.year),
        );
        let landblock = host.call::<calls::DungeonLandblock>(&());
        Self::line(host, "dungeon_landblock", &format!("0x{landblock:08X}"));
        let sealed = host.call::<calls::DungeonIsSealed>(&(landblock | 0x100));
        Self::line(host, "dungeon_is_sealed", &format!("{sealed}"));
        // E. Spells and magic.
        let buffs = host.known_spells(SpellList::SelfBuffs);
        let attacks = host.known_spells(SpellList::Attacks);
        let combat = host.known_spells(SpellList::Combat);
        Self::line(
            host,
            "spells_known",
            &format!(
                "{} self buffs, {} attacks, {} combat",
                buffs.len(),
                attacks.len(),
                combat.len()
            ),
        );
        let ids = host.known_spell_ids(SpellList::SelfBuffs);
        let same = ids.iter().copied().eq(buffs.iter().map(|s| s.spell_id));
        Self::line(
            host,
            "spells_known_ids",
            &format!("{} self buffs, same as spells_known={same}", ids.len()),
        );
        let revision = host.known_spells_revision();
        Self::line(
            host,
            "spells_known_revision",
            &format!("read={}", revision != 0),
        );
        // The whole table, page by page.
        let all = host.all_spells().len();
        Self::line(host, "spells_all", &format!("{all} spells"));
        let first = buffs.first().map_or(0, |s| s.spell_id);
        let found = host.spell(first).map(|s| s.name);
        Self::line(host, "spells_get", &format!("{found:?}"));
        let known = host.is_spell_known(first);
        Self::line(host, "spells_is_known", &format!("{known}"));
        let by_name = host.find_spell("Strength Self", true).map(|s| s.spell_id);
        Self::line(host, "spells_find", &format!("{by_name:?}"));
        let component = host.spell_component(1).map(|c| c.name);
        Self::line(host, "spells_component", &format!("{component:?}"));
        let cooldown = host.cooldown_remaining(1);
        Self::line(host, "spells_cooldown", &format!("{cooldown}"));
        let casting = host.is_casting();
        Self::line(host, "magic_is_casting", &format!("{casting}"));
        let gate = host.cast_gate(first, 0);
        Self::line(host, "magic_gate", &format!("{gate:?}"));
        let components = host.has_components(first);
        Self::line(host, "magic_has_components", &format!("{components}"));
        let last = host.last_cast();
        Self::line(
            host,
            "magic_last_completion",
            &format!("revision={}", last.revision),
        );
        let me = info_id(host);
        let tracked = host.tracked_enchantments(me);
        Self::line(
            host,
            "enchantments_capture",
            &format!("{} tracked", tracked.len()),
        );
        // F. Combat.
        let combat = host.combat();
        Self::line(
            host,
            "combat_snapshot",
            &format!(
                "mode={:?} height={:?} pending={}",
                combat.mode, combat.attack_height, combat.server_response_pending
            ),
        );
        let hostile = host.hostile_targets(30.0);
        let mut species: Vec<String> = hostile.iter().map(|t| t.species_name.clone()).collect();
        species.sort();
        species.dedup();
        Self::line(
            host,
            "combat_hostile_targets",
            &format!("{} within 30 m [{}]", hostile.len(), species.join(", ")),
        );
        // G. Items, equipment, loot.
        let owned = host.owned_items();
        let packs = owned.iter().filter(|i| i.items_capacity > 0).count();
        Self::line(
            host,
            "items_owned",
            &format!(
                "{} items, {packs} packs, {} with headers, {} plural names, {} worn coverage, \
                 {} on a shared cooldown, {} pet devices",
                owned.len(),
                owned.iter().filter(|i| i.header.is_some()).count(),
                owned.iter().filter(|i| !i.plural_name.is_empty()).count(),
                owned.iter().filter(|i| i.coverage_mask != 0).count(),
                owned.iter().filter(|i| i.shared_cooldown_id != 0).count(),
                owned.iter().filter(|i| i.is_pet_device()).count()
            ),
        );
        let state = host.item_state();
        Self::line(
            host,
            "items_state",
            &format!(
                "available={} busy={} vendor=0x{:08X}",
                state.is_available, state.is_busy, state.active_vendor_id
            ),
        );
        let equipment = host.owned_equipment();
        let worn = equipment
            .iter()
            .filter(|e| e.equipped_location != 0)
            .count();
        Self::line(
            host,
            "equipment_owned",
            &format!("{} wearable, {worn} worn", equipment.len()),
        );
        let wielding = host.equipment_busy();
        Self::line(host, "equipment_busy", &format!("busy={wielding}"));
        let loot = host.loot_state();
        Self::line(
            host,
            "loot_state",
            &format!(
                "open=0x{:08X} ready={} appraised=0x{:08X} unsuccessful={}",
                loot.current_container_id,
                loot.current_contents_ready,
                loot.appraisal.current_id,
                loot.appraisal.current_object_unsuccessful
            ),
        );
        let corpses = host.corpses(30.0);
        Self::line(
            host,
            "loot_corpses",
            &format!("{} within 30 m", corpses.len()),
        );
        let classifiers: Vec<String> = host.loot_classifiers().into_iter().map(|c| c.id).collect();
        Self::line(host, "loot_classifiers", &classifiers.join(" "));
        // H. Trade and vendor.
        let trade = host.trade();
        Self::line(
            host,
            "trade_state",
            &format!("open={} partner=0x{:08X}", trade.is_open, trade.partner_id),
        );
        let vendor = host.vendor();
        Self::line(
            host,
            "vendor_state",
            &format!(
                "open={} items={} busy={}",
                vendor.is_open,
                vendor.items.len(),
                vendor.is_busy
            ),
        );
        // I. Navigation.
        let nav = host.navigation();
        Self::line(
            host,
            "navigation_snapshot",
            &format!(
                "available={} portal={}",
                nav.is_available, nav.is_portal_space
            ),
        );
        let me = host.navigation_object(nav.local_object_id);
        Self::line(host, "navigation_object", &format!("self={}", me.is_some()));
        let objects = host.navigation_objects();
        Self::line(
            host,
            "navigation_objects",
            &format!("any={}", !objects.is_empty()),
        );
        let room = host.check_room_ahead(3.0);
        Self::line(
            host,
            "navigation_room_ahead",
            &format!(
                "status={:?} cell=0x{:08X}",
                room.status, room.position.cell_id
            ),
        );
        let report = host.go_to_report();
        Self::line(
            host,
            "navigation_go_to_report",
            &format!("state={:?} owner={:?}", report.state, report.owner),
        );
        let moves = host.move_report();
        Self::line(
            host,
            "navigation_move_report",
            &format!("travel={:?}", moves.travel.state),
        );
        // J. Social and session.
        let fellowship = host.fellowship();
        Self::line(
            host,
            "fellowship_state",
            &format!(
                "in={} members={} sharing={} even={}",
                fellowship.is_in_fellowship,
                fellowship.member_count,
                fellowship.shares_experience,
                fellowship.splits_experience_evenly
            ),
        );
        let roster = host.fellowship_roster();
        Self::line(
            host,
            "fellowship_roster",
            &format!(
                "{:?}",
                roster
                    .iter()
                    .map(|m| format!("{:08X}:{}", m.object_id, m.level))
                    .collect::<Vec<_>>()
            ),
        );
        let allegiance = host.allegiance();
        Self::line(
            host,
            "allegiance_state",
            &format!(
                "available={} known={} monarch={:?} patron={:?} vassals={}",
                allegiance.is_available,
                allegiance.is_known,
                allegiance.monarch.as_ref().map(|m| m.name.as_str()),
                allegiance.patron.as_ref().map(|m| m.name.as_str()),
                allegiance.vassals.len()
            ),
        );
        let login = host.login();
        Self::line(
            host,
            "login_state",
            &format!(
                "available={} characters={} can_logout={}",
                login.is_available,
                login.roster.len(),
                login.can_request_logout
            ),
        );
        let recalls = host.recalls();
        Self::line(
            host,
            "recall_state",
            &format!(
                "available={} last={:?}",
                recalls.is_available, recalls.last_request.status
            ),
        );
        let busy = host.busy_state();
        Self::line(
            host,
            "recovery_busy_state",
            &format!(
                "busy={} pending={}",
                busy.busy_count, busy.pending_inventory
            ),
        );
        // K, L, M, N: the chat entry, the window, the status board and
        // storage, each read the same way with or without a window.
        let typing = host.chat_input_active();
        Self::line(host, "chat_is_input_active", &format!("{typing}"));
        let minimized = host.window_minimized();
        Self::line(host, "client_is_minimized", &format!("{minimized}"));
        host.status_publish("census", Some("taken"));
        let status = host.status_read("openac.probe", "census");
        Self::line(host, "status_read", &format!("{status:?}"));
        let place = openac_plugin::storage::StoragePlace::global(
            openac_plugin::storage::StorageRoot::PluginData,
        );
        let written = host.storage_write_text(&place, "census.txt", "taken");
        let read = host.storage_read_text(&place, "census.txt");
        Self::line(
            host,
            "storage_read_text",
            &format!("written={} read={read:?}", written.is_ok()),
        );
    }
}

fn info_id(host: &mut Host<'_>) -> u32 {
    host.character().map_or(0, |c| c.id)
}

impl Plugin for Probe {
    fn enable(&mut self, host: &mut Host<'_>, info: &StartInfo) -> Result<(), String> {
        host.register_command("probe", "Logs the facts a plugin reads")?;
        host.info(&format!(
            "probe start: id={} api={} client={}",
            info.plugin_id, info.api_version, info.client_version
        ));
        Self::facts(host, "enable");
        // A loot classifier other plugins can ask: it keeps everything.
        self.classifier = host.register_loot_classifier("rules", "Probe Rules")?;
        Ok(())
    }

    fn callback(&mut self, _host: &mut Host<'_>, callback: Callback) -> CallbackAnswer {
        match callback {
            Callback::LootClassify(question) if question.handle == self.classifier => {
                CallbackAnswer::Classification(Some(LootClassification {
                    matched: true,
                    action: LootAction::Keep,
                    rule_name: "probe".to_owned(),
                    priority: 0,
                    keep_count: 0,
                }))
            }
            _ => CallbackAnswer::Nothing,
        }
    }

    fn disable(&mut self, host: &mut Host<'_>) {
        host.info(&format!("probe stop: ticks={}", self.ticks));
    }

    fn tick(&mut self, host: &mut Host<'_>) {
        self.ticks += 1;
        if self.ticks == 100 {
            Self::facts(host, "tick100");
        }
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        match event {
            Event::Command(call) => match call.arguments.as_str() {
                "census" => Self::census(host),
                // The plugin verbs: the help list and the definitions.
                "help" => {
                    let help = host.command_help();
                    let verbs: Vec<String> = host
                        .command_definitions()
                        .into_iter()
                        .map(|d| format!("{}{:?}", d.verb, d.aliases))
                        .collect();
                    host.info(&format!("probe help: {help:?} {verbs:?}"));
                }
                // Another plugin's completions: `complete <verb> <text>`.
                line if line.starts_with("complete ") => {
                    let rest = &line["complete ".len()..];
                    let (verb, arguments) = rest.split_once(' ').unwrap_or((rest, ""));
                    let offered: Vec<String> = host
                        .complete_command(verb, arguments, &format!("/{rest}"))
                        .into_iter()
                        .map(|c| match c.description {
                            Some(d) => format!("{} ({d})", c.text),
                            None => c.text,
                        })
                        .collect();
                    host.info(&format!("probe complete {rest}: {offered:?}"));
                }
                // The whole spell table alone.
                "spells" => {
                    let count = host.all_spells().len();
                    Self::line(host, "spells_all", &format!("{count} spells"));
                }
                // What reading the known self buffs costs, for the call
                // time measurements: `known <n>` reads the full records n times,
                // `known-ids <n>` the ids only.
                line if line.starts_with("known ") || line.starts_with("known-ids ") => {
                    let (what, times) = line.split_once(' ').unwrap_or((line, "1"));
                    let times: u32 = times.trim().parse().unwrap_or(1);
                    let mut read = 0;
                    for _ in 0..times {
                        read += if what == "known" {
                            host.known_spells(SpellList::SelfBuffs).len()
                        } else {
                            host.known_spell_ids(SpellList::SelfBuffs).len()
                        };
                    }
                    host.info(&format!("probe {what}: {read}"));
                }
                "events" => {
                    let heard = std::mem::take(&mut self.heard);
                    host.info(&format!("probe events: {}", heard.join(", ")));
                    let counted = std::mem::take(&mut self.counted);
                    host.info(&format!("probe counted: {counted:?}"));
                }
                // The other clients on this computer.
                "peers" => {
                    let peers: Vec<String> = host
                        .peer_clients()
                        .iter()
                        .map(|p| {
                            format!(
                                "{} on {} health={}/{} tags={:?} remote={}",
                                p.name,
                                p.world_name,
                                p.current_health,
                                p.max_health,
                                p.tags,
                                p.is_remote
                            )
                        })
                        .collect();
                    host.info(&format!("probe peers: [{}]", peers.join("; ")));
                }
                // Tell the others a Strength Self I landed on this character.
                "cast" => {
                    let me = info_id(host);
                    let told = host.announce_cast_success(me, 2, 100, 1800.0);
                    host.info(&format!("probe cast announced: {told}"));
                }
                "casts" => {
                    let casts: Vec<String> = host
                        .peer_casts(0)
                        .iter()
                        .map(|c| format!("spell {} on 0x{:08X}", c.spell_id, c.target_id))
                        .collect();
                    host.info(&format!("probe casts: [{}]", casts.join("; ")));
                }
                // The relay half: what this client tells its neighbours,
                // and a copy of itself imported as a peer from elsewhere
                // under another character id (it reads back remote).
                "relay" => {
                    let connected = host.peers_connected();
                    let subscription = host.peer_subscribe(1 | 2 | 4);
                    let me = host.peer_self();
                    let own = host.own_peer_casts(0).len();
                    let imported = me.as_ref().is_some_and(|me| {
                        let mut copy = me.clone();
                        copy.player_id = 0x7FFF_FFF0;
                        copy.name = format!("{} (relayed)", me.name);
                        host.import_peer_client(&copy)
                    });
                    host.info(&format!(
                        "probe relay: connected={connected} subscription={subscription:?} self={} own_casts={own} imported={imported}",
                        me.map_or_else(|| "none".to_owned(), |m| m.name),
                    ));
                }
                other if other.starts_with("broadcast ") => {
                    let line = other.trim_start_matches("broadcast ").trim();
                    let sent = host.broadcast_command(line, &[], 0);
                    host.info(&format!("probe broadcast {line:?}: {sent}"));
                }
                _ => {
                    Self::facts(host, "command");
                    Self::census(host);
                }
            },
            Event::WindowAttached(attached) => {
                host.info(&format!("probe window-attached={attached}"));
            }
            Event::ChatReceived(_) => {}
            // Every pass while the character moves: too many to list.
            Event::NavigationSnapshotChanged(_) => {}
            // What the world reads at each end of a stay: at the arrival
            // the character's own object is there (the server listens), at
            // the leaving everything is still there to save.
            Event::LoginComplete => {
                self.heard.push("login".to_owned());
                let world = Self::world_summary(host);
                host.info(&format!("probe event: login complete: {world}"));
            }
            Event::Logoff => {
                self.heard.push("logoff".to_owned());
                let world = Self::world_summary(host);
                host.info(&format!("probe event: logoff: {world}"));
            }
            Event::ObjectChanged(change) => {
                *self
                    .counted
                    .entry(format!("{:?}", change.kind))
                    .or_default() += 1;
                if self.heard.len() < 64 {
                    self.heard.push(format!("{:?}", change.kind));
                }
            }
            other => {
                if matches!(other, Event::EntitySpawned(_)) {
                    *self.counted.entry("EntitySpawned".to_owned()).or_default() += 1;
                }
                if self.heard.len() < 64 {
                    let name = format!("{other:?}");
                    let name = name.split('(').next().unwrap_or("").to_owned();
                    self.heard.push(name);
                }
            }
        }
    }
}

openac_plugin::export_plugin!(Probe);
