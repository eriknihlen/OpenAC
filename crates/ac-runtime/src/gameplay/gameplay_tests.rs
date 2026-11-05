//! The gameplay owners wired to the session: what a command sends and what
//! a server message changes. Behaviour follows OpenAC's runtime owners as
//! named in each test.

use std::time::Duration;

use ac_proto::allegiance::build_update_request;

use super::*;
use crate::session::Clocks;

const CLOCKS: Clocks = Clocks {
    wall_unix_ms: 1_790_000_000_000,
    session: Duration::from_secs(30),
};

struct Fixture {
    world: World,
    outbox: Outbox,
    options: CharacterOptions,
    gameplay: Gameplay,
    body: Option<Movement>,
    teleporting: bool,
}

impl Fixture {
    fn new() -> Self {
        let mut world = World::new();
        world.set_player(PLAYER);
        Self {
            world,
            outbox: Outbox::new(),
            options: CharacterOptions::new(),
            gameplay: Gameplay::default(),
            body: None,
            teleporting: false,
        }
    }

    fn ctx(&mut self) -> (GameplayContext<'_>, &mut Gameplay) {
        (
            GameplayContext {
                world: &mut self.world,
                outbox: &mut self.outbox,
                options: &self.options,
                active: true,
                clocks: CLOCKS,
                body: self.body.as_mut(),
                teleporting: self.teleporting,
                opened_corpses: None,
            },
            &mut self.gameplay,
        )
    }

    fn tick(&mut self) {
        let (mut ctx, gameplay) = self.ctx();
        gameplay.tick(&mut ctx);
    }

    fn describe(&mut self, description: &PlayerDescription) {
        self.world.apply_player_description(description);
        let (mut ctx, gameplay) = self.ctx();
        gameplay.apply_player_description(description, &mut ctx);
    }

    fn sent(&mut self) -> Vec<Vec<u8>> {
        self.outbox.drain().map(|o| o.body).collect()
    }
}

const PLAYER: u32 = 0x5000_000B;

#[test]
fn the_character_description_asks_once_for_the_allegiance() {
    // OpenAC `RuntimeAllegianceLoginRequestTests`: the request goes out
    // when the character has arrived (its description is in), once a
    // session; a new session asks again.
    let mut f = Fixture::new();
    f.tick();
    assert!(f.sent().is_empty());
    let description = crate::world::tests::empty_description();
    f.describe(&description);
    f.describe(&description);
    assert_eq!(f.sent(), [build_update_request(1, true)]);
    f.gameplay.left_world(&mut f.world);
    f.describe(&description);
    assert_eq!(f.sent(), [build_update_request(2, true)]);
}

// -- Helpers for the wire ---------------------------------------------------------

use std::sync::Arc;

use ac_core::items::PublicWeenieFlags;
use ac_core::spells::{SpellMetadata, SpellTable};
use ac_proto::{
    CreateObject, ModelData, PhysicsTimestamps, Position, PrivateUpdatePropertyBool,
    PrivateUpdatePropertyInt, PublicWeenieDesc, Quaternion, Vector3,
};

const MONSTER: u32 = 0x8000_0101;
const FAR_MONSTER: u32 = 0x8000_0102;
const BOOK: u32 = 0x8000_4FCD;
const CELL: u32 = 0xA9B4_0031;

fn at(x: f32) -> Position {
    Position {
        cell_id: CELL,
        origin: Vector3 { x, y: 10.0, z: 0.0 },
        orientation: Quaternion::IDENTITY,
    }
}

fn object(guid: u32, name: &str, item_type: u32, flags: u32, x: f32) -> Inbound<'static> {
    Inbound::CreateObject(Box::new(CreateObject {
        guid,
        model: ModelData::default(),
        physics: crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                ..PhysicsTimestamps::default()
            },
            Some(at(x)),
        ),
        weenie: Some(PublicWeenieDesc {
            name: name.into(),
            item_type,
            object_description_flags: flags,
            ..PublicWeenieDesc::default()
        }),
    }))
}

fn event(event_type: GameEventType, payload: &[u8]) -> Vec<u8> {
    let mut body = Vec::new();
    for word in [ac_proto::Opcode::GameEvent.value(), PLAYER, 1, event_type.0] {
        body.extend_from_slice(&word.to_le_bytes());
    }
    body.extend_from_slice(payload);
    body
}

fn string16(text: &str) -> Vec<u8> {
    let mut w = ac_bits::Writer::new();
    w.string16l(text).unwrap();
    w.into_vec()
}

impl Fixture {
    fn apply(&mut self, message: &Inbound<'_>) {
        self.world.apply(message);
        let (mut ctx, gameplay) = self.ctx();
        gameplay.apply(message, &mut ctx);
    }

    fn apply_bytes(&mut self, bytes: &[u8]) {
        let message = Inbound::parse(bytes).unwrap();
        self.apply(&message);
    }

    fn run(&mut self, command: GameplayCommand) {
        let (mut ctx, gameplay) = self.ctx();
        gameplay.execute(command, &mut ctx);
    }

    fn events(&mut self) -> Vec<GameplayEvent> {
        self.gameplay.drain_events().collect()
    }

    fn notices(&mut self) -> Vec<GameplayNotice> {
        self.gameplay.drain_notices().collect()
    }

    /// The player placed and described, a monster 5 m away and another 30 m
    /// away.
    fn in_the_field() -> Self {
        let mut f = Self::new();
        let creature = 0x10;
        f.apply(&object(
            PLAYER,
            "+Horan",
            creature,
            PublicWeenieFlags::PLAYER.bits(),
            0.0,
        ));
        let attackable = PublicWeenieFlags::ATTACKABLE.bits();
        f.apply(&object(MONSTER, "Drudge", creature, attackable, 5.0));
        f.apply(&object(
            FAR_MONSTER,
            "Drudge Slinker",
            creature,
            attackable,
            30.0,
        ));
        f.describe(&crate::world::tests::empty_description());
        f.sent();
        f
    }
}

// -- Magic through the owner ------------------------------------------------------

#[test]
fn a_cast_goes_out_counts_busy_and_its_use_done_is_reported() {
    // OpenAC `HeadlessGameplayOperations`: the cast gate reads the component
    // check over the player's own property (switched off here as the
    // server's `@requirecomps off` sends it), the cast counts busy, and the use-done
    // answers it.
    let mut f = Fixture::new();
    let spell = SpellMetadata {
        flags: ac_core::spells::SpellFlags::SELF_TARGETED,
        ..SpellMetadata::new(2, "Strength Self I", 1)
    };
    f.world
        .install_player_rules(Arc::new(SpellTable::create([spell]).unwrap()), None);
    f.apply(&object(PLAYER, "+Horan", 0x10, 0, 0.0));
    let mut description = crate::world::tests::empty_description();
    description.spells.insert(2, 0.0);
    f.describe(&description);
    f.sent();
    f.run(GameplayCommand::Cast {
        spell: "Strength Self I".into(),
        target: None,
    });
    assert!(f.sent().is_empty(), "components are required by default");
    assert_eq!(
        f.notices(),
        [GameplayNotice::Text(
            "You do not have all of this spell's components.".into(),
            LogTextType::Default
        )]
    );
    f.apply(&Inbound::PrivateUpdatePropertyBool(
        PrivateUpdatePropertyBool {
            sequence: 1,
            property: ac_core::properties::PropertyBool::SPELL_COMPONENTS_REQUIRED.0,
            value: false,
        },
    ));
    f.run(GameplayCommand::Cast {
        spell: "2".into(),
        target: None,
    });
    assert_eq!(
        f.sent(),
        [wire_magic::build_cast_targeted_spell(2, PLAYER, 2)]
    );
    assert_eq!(f.gameplay.take_busy(), 1);
    // The session hands a cast's use-done over (no item action was out).
    f.gameplay.complete_cast_use(0);
    assert!(
        f.events()
            .contains(&GameplayEvent::CastCompleted(SpellCastCompletion {
                revision: 1,
                spell_id: 2,
                target_id: PLAYER,
                weenie_error: 0,
            }))
    );
}

#[test]
fn an_unknown_spell_name_is_refused_as_host_feedback() {
    let mut f = Fixture::new();
    f.run(GameplayCommand::Cast {
        spell: "Nothing".into(),
        target: None,
    });
    assert_eq!(
        f.events(),
        [GameplayEvent::Refused(
            "No known spell is called \"Nothing\".".into()
        )]
    );
}

// -- Combat through the owner -----------------------------------------------------

#[test]
fn the_toggle_sends_the_default_mode_and_the_server_s_word_follows() {
    // OpenAC `WorldSessionCombatTests.SendChangeCombatMode_UsesSequenceAnd
    // RetailModeValue` and `CombatStateWiringTests`: an empty-handed toggle
    // asks for melee; the player's combat-mode property sets the mode.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::ToggleCombat);
    assert_eq!(
        f.sent(),
        [wire_combat::build_change_combat_mode(
            2,
            CombatMode::MELEE.bits()
        )]
    );
    assert_eq!(f.gameplay.combat_mode(), CombatMode::MELEE);
    assert!(
        f.events()
            .contains(&GameplayEvent::CombatMode(CombatMode::MELEE))
    );
    assert_eq!(f.gameplay.combat().server_mode(), None);
    f.apply(&Inbound::PrivateUpdatePropertyInt(
        PrivateUpdatePropertyInt {
            sequence: 1,
            property: COMBAT_MODE_PROPERTY,
            value: 1,
        },
    ));
    assert_eq!(f.gameplay.combat_mode(), CombatMode::NON_COMBAT);
    assert_eq!(
        f.gameplay.combat().server_mode(),
        Some(CombatMode::NON_COMBAT)
    );
    // Not a mode: ignored.
    f.apply(&Inbound::PrivateUpdatePropertyInt(
        PrivateUpdatePropertyInt {
            sequence: 2,
            property: COMBAT_MODE_PROPERTY,
            value: 3,
        },
    ));
    assert_eq!(f.gameplay.combat_mode(), CombatMode::NON_COMBAT);
}

#[test]
fn with_a_body_bound_a_mode_change_waits_for_the_stance_and_a_teleport_refuses_it() {
    // OpenAC `RuntimeCombatModeReadiness` (bound by `GameRuntime`): the body
    // is not ready with no body to ask, so the change is parked; a teleport
    // under way refuses it with the retail line.
    let mut f = Fixture::in_the_field();
    f.body = Some(Movement::new(false));
    let _ = f.sent();
    f.run(GameplayCommand::ToggleCombat);
    f.tick();
    assert!(f.sent().is_empty(), "parked until the body is ready");
    assert_eq!(f.gameplay.combat_mode(), CombatMode::NON_COMBAT);
    f.teleporting = true;
    f.run(GameplayCommand::SetCombatMode(CombatMode::MELEE));
    assert!(f.sent().is_empty());
    assert!(f.notices().contains(&GameplayNotice::Text(
        "You can't change combat mode while teleporting.".into(),
        LogTextType::Default
    )));
    // OpenAC `RuntimeLocalPlayerMovementState.IsReadyForAttack`: no body,
    // no swing; the stance is the body's, not the server's last word.
    let body = Movement::new(false);
    for mode in [CombatMode::MELEE, CombatMode::MISSILE] {
        assert!(!ready_for_attack(Some(&body), &f.world, mode));
    }
    assert!(!is_dual_wield(Some(&body), &f.world));
}

fn death_of(guid: u32) -> Inbound<'static> {
    Inbound::UpdateMotion(Box::new(ac_proto::UpdateMotion {
        guid,
        instance_sequence: 1,
        movement_sequence: 2,
        server_control_sequence: 0,
        is_autonomous: false,
        movement: ac_proto::MovementData {
            movement_type: 0,
            motion_flags: 0,
            current_style: 0x003D,
            movement: ac_proto::Movement::Interpreted {
                state: ac_proto::InterpretedMotionState {
                    forward_command: Some(0x0011),
                    ..ac_proto::InterpretedMotionState::default()
                },
                sticky_object: None,
            },
        },
    }))
}

#[test]
fn selecting_a_monster_streams_its_health_and_its_death_lets_it_go() {
    // RuntimeSelectedObjectHealthQuery and RuntimeCreatureDeathState: the
    // selection asks for health; the death motion clears the selection and
    // closes the stream.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Select(ObjectRef::Name("drudge".into())));
    assert_eq!(f.gameplay.selection().selected(), Some(MONSTER));
    assert_eq!(f.sent(), [wire_combat::build_query_health(2, MONSTER)]);
    let mut health = Vec::new();
    health.extend_from_slice(&MONSTER.to_le_bytes());
    health.extend_from_slice(&0.5f32.to_le_bytes());
    f.apply_bytes(&event(GameEventType::UPDATE_HEALTH, &health));
    assert!(f.events().contains(&GameplayEvent::Health {
        guid: MONSTER,
        percent: 0.5,
    }));
    f.apply(&death_of(MONSTER));
    assert!(f.gameplay.deaths().is_dead(MONSTER));
    assert_eq!(f.gameplay.selection().selected(), None);
    assert_eq!(f.sent(), [wire_combat::build_query_health(3, 0)]);
    // RuntimeCreatureDeathStateTests.ASessionResetForgetsEveryDeath.
    f.gameplay.left_world(&mut f.world);
    assert!(!f.gameplay.deaths().is_dead(MONSTER));
}

// OpenAC `SelectionState.Reset`: leaving the world is a selection change
// (`SessionReset`) raised even with nothing selected, so whatever follows
// the selection lets the session's go.
#[test]
fn leaving_the_world_reports_the_selection_reset() {
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Select(ObjectRef::Guid(MONSTER)));
    let _ = f.events();
    f.gameplay.left_world(&mut f.world);
    assert_eq!(f.gameplay.selection().selected(), None);
    assert_eq!(
        f.events(),
        [GameplayEvent::Selection {
            selected: None,
            name: None
        }]
    );
    f.gameplay.left_world(&mut f.world);
    assert_eq!(
        f.events(),
        [GameplayEvent::Selection {
            selected: None,
            name: None
        }],
        "with nothing selected too"
    );
}

// OpenAC v0.1.25 `RuntimeCreatureDeathStateTests`: a
// death leaves with the incarnation that died.
#[test]
fn deleting_a_corpse_forgets_its_death_but_a_stale_delete_does_not() {
    // DeletingACorpseForgetsItsDeathEvenWithoutAWindow.
    let mut f = Fixture::in_the_field();
    f.apply(&death_of(MONSTER));
    assert_eq!(f.gameplay.deaths().count(), 1);
    f.apply(&Inbound::DeleteObject(ac_proto::DeleteObject {
        guid: MONSTER,
        instance_sequence: 1,
    }));
    assert_eq!(f.gameplay.deaths().count(), 0);

    // AStaleDeleteDoesNotForgetTheNewIncarnationsDeath.
    let mut deaths = CreatureDeathState::default();
    let Inbound::UpdateMotion(motion) = death_of(MONSTER) else {
        unreachable!()
    };
    assert!(deaths.observe_motion(MONSTER, 2, Some(&motion.movement)));
    deaths.forget_incarnation(MONSTER, 1);
    assert!(deaths.is_dead_incarnation(MONSTER, 2));
    // VisibilityExpiryForgetsTheDepartedCreaturesDeath: the incarnation
    // the world forgot out of range takes its death along.
    f.apply(&death_of(MONSTER));
    f.gameplay.forget_expired(MONSTER, 1);
    assert!(!f.gameplay.deaths().is_dead(MONSTER));
}

#[test]
fn a_dead_targets_automatic_replacement_keeps_its_health_streaming() {
    // RuntimeSelectedObjectHealthQueryTests
    // .ADeadTargetsAutomaticReplacementKeepsItsHealthStreaming: the dead
    // target's clearing and its replacement are followed in the order they
    // happened, so the stream closed for the dead one is not closed again
    // after the replacement's is opened, and the server keeps sending the
    // replacement's health as it is damaged.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::SetCombatMode(CombatMode::MELEE));
    f.world.apply(&Inbound::PrivateUpdatePropertyDataId(
        ac_proto::PrivateUpdatePropertyDataId {
            sequence: 1,
            property: ac_core::properties::PropertyDataId::COMBAT_TABLE.0,
            value: 0x3000_0000,
        },
    ));
    f.options.set_bit(option_id::AUTO_TARGET, true);
    f.run(GameplayCommand::Select(ObjectRef::Guid(MONSTER)));
    let opened = f.sent();
    assert_eq!(
        opened.last(),
        Some(&wire_combat::build_query_health(3, MONSTER))
    );

    f.apply(&death_of(MONSTER));

    assert_eq!(f.gameplay.selection().selected(), Some(FAR_MONSTER));
    assert_eq!(
        f.sent(),
        [
            wire_combat::build_query_health(4, 0),
            wire_combat::build_query_health(5, FAR_MONSTER),
        ]
    );
    assert_eq!(f.gameplay.health_query().streaming_object_id(), FAR_MONSTER);
}

#[test]
fn an_attack_goes_to_the_nearest_monster_when_auto_target_is_on() {
    // RuntimeAttackTargetResolver: with the auto-target option an empty
    // selection takes the nearest living monster, which becomes the
    // selection; the dead one is passed over.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::SetCombatMode(CombatMode::MELEE));
    // The combat maneuver table makes melee ready.
    f.world.apply(&Inbound::PrivateUpdatePropertyDataId(
        ac_proto::PrivateUpdatePropertyDataId {
            sequence: 1,
            property: ac_core::properties::PropertyDataId::COMBAT_TABLE.0,
            value: 0x3000_0000,
        },
    ));
    f.options.set_bit(option_id::AUTO_TARGET, true);
    f.apply(&death_of(MONSTER));
    f.sent();
    f.run(GameplayCommand::SetAttackPower(0.0));
    f.run(GameplayCommand::Attack {
        height: AttackHeight::HIGH,
        target: None,
    });
    assert_eq!(f.gameplay.selection().selected(), Some(FAR_MONSTER));
    // The new selection's health is asked for as it is made, before the
    // swing goes out.
    assert_eq!(
        f.sent(),
        [
            wire_combat::build_query_health(3, FAR_MONSTER),
            wire_combat::build_targeted_melee_attack(4, FAR_MONSTER, AttackHeight::HIGH.0, 0.0),
        ]
    );
}

#[test]
fn a_reveal_clears_the_selection_it_was_asked_about_and_no_other() {
    // OpenAC `WorldGenerationQuiescence.CommitBegin`: a reveal that began
    // while a shown world object was selected clears the selection (the
    // client's own, `SelectionChangeSource.System`). The window asks by
    // object, so a selection made since stays.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Select(ObjectRef::Guid(MONSTER)));
    f.run(GameplayCommand::ClearRevealedSelection(FAR_MONSTER));
    assert_eq!(f.gameplay.selection().selected(), Some(MONSTER));
    f.run(GameplayCommand::ClearRevealedSelection(MONSTER));
    assert_eq!(f.gameplay.selection().selected(), None);
}

#[test]
fn the_camera_keeps_an_attackable_selection_in_view_in_targeted_combat_with_the_option() {
    // OpenAC CombatCameraTargetSource.GetTrackedTargetPoint: the
    // ViewCombatTarget option, melee or missile (SupportsTargetedAttack)
    // and an attackable creature selected (IsAttackableTarget).
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Select(ObjectRef::Guid(MONSTER)));
    assert_eq!(f.gameplay.selection().selected(), Some(MONSTER));
    assert_eq!(f.gameplay.combat_camera_target(&f.world, true), None);
    f.run(GameplayCommand::SetCombatMode(CombatMode::MELEE));
    assert_eq!(
        f.gameplay.combat_camera_target(&f.world, true),
        Some(MONSTER)
    );
    assert_eq!(f.gameplay.combat_camera_target(&f.world, false), None);
    f.run(GameplayCommand::SetCombatMode(CombatMode::MISSILE));
    assert_eq!(
        f.gameplay.combat_camera_target(&f.world, true),
        Some(MONSTER)
    );
    f.run(GameplayCommand::SetCombatMode(CombatMode::MAGIC));
    assert_eq!(f.gameplay.combat_camera_target(&f.world, true), None);
    // The player himself is never a target.
    f.run(GameplayCommand::SetCombatMode(CombatMode::MELEE));
    f.run(GameplayCommand::SelectSelf);
    assert_eq!(f.gameplay.combat_camera_target(&f.world, true), None);
}

#[test]
fn spellbook_commands_change_the_book_and_send_and_invalid_ones_do_neither() {
    // OpenAC RuntimeCharacterStateTests.SpellbookCommandsFollowRetailLocalAndOutboundOrder
    // and InvalidSpellbookCommandsDoNotPublishOrMutate.
    let mut f = Fixture::new();
    let spell = SpellMetadata::new(42, "Flame Bolt I", 1);
    f.world
        .install_player_rules(Arc::new(SpellTable::create([spell]).unwrap()), None);
    f.apply(&object(PLAYER, "+Horan", 0x10, 0, 0.0));
    let mut description = crate::world::tests::empty_description();
    description.spells.insert(42, 0.0);
    f.describe(&description);
    f.sent();
    let book = |f: &Fixture| f.world.spellbook().unwrap().clone();

    f.run(GameplayCommand::AddFavorite {
        tab: 0,
        position: 0,
        spell: "42".into(),
    });
    assert_eq!(book(&f).get_favorites(0), [42]);
    f.run(GameplayCommand::SetSpellbookFilter(0x3FFE));
    f.run(GameplayCommand::SetSpellbookFilter(0x3FFE));
    assert_eq!(book(&f).spellbook_filters(), 0x3FFE);
    f.run(GameplayCommand::SetDesiredComponent {
        component: 0x6800_0001,
        amount: 0,
    });
    assert_eq!(book(&f).desired_components().get(&0x6800_0001), Some(&0));
    f.run(GameplayCommand::ClearDesiredComponents);
    assert!(book(&f).desired_components().is_empty());
    assert_eq!(
        f.sent(),
        [
            wire_magic::build_add_spell_favorite(2, 42, 0, 0),
            wire_magic::build_spellbook_filter(3, 0x3FFE),
            wire_magic::build_set_desired_component_level(4, 0x6800_0001, 0),
            wire_magic::build_clear_desired_components(5),
        ],
        "the unchanged filter is not sent again"
    );

    f.run(GameplayCommand::AddFavorite {
        tab: 8,
        position: 0,
        spell: "42".into(),
    });
    f.run(GameplayCommand::SetDesiredComponent {
        component: 0,
        amount: 1,
    });
    f.run(GameplayCommand::SetDesiredComponent {
        component: 1,
        amount: 5001,
    });
    assert!(f.sent().is_empty());
    assert_eq!(book(&f).get_favorites(0), [42]);
    assert!(book(&f).desired_components().is_empty());

    // OpenAC `IRuntimeSpellbookCommands.ForgetSpell`: the request goes out;
    // the server's RemoveSpell takes it out of the book.
    f.run(GameplayCommand::ForgetSpell("Flame Bolt I".into()));
    assert_eq!(f.sent(), [wire_magic::build_remove_spell(6, 42)]);
}

// Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Gameplay/RuntimeAttackTargetResolverTests.cs.

fn resolve(f: &mut Fixture, allow_auto_target: bool) -> Option<u32> {
    let (mut ctx, gameplay) = f.ctx();
    let mut ops = ops!(gameplay, ctx);
    ops.resolve_attack_target(allow_auto_target)
}

#[test]
fn a_named_selection_is_the_target_even_without_auto_target() {
    let mut f = Fixture::in_the_field();
    f.options.set_bit(option_id::AUTO_TARGET, false);
    f.gameplay.selection.select(
        MONSTER,
        SelectionSource::Keyboard,
        SelectionReason::Selected,
    );
    f.gameplay.selection.select(
        FAR_MONSTER,
        SelectionSource::Host,
        SelectionReason::Selected,
    );
    assert_eq!(resolve(&mut f, false), Some(FAR_MONSTER));
}

#[test]
fn without_the_option_an_empty_selection_stays_empty() {
    let mut f = Fixture::in_the_field();
    f.options.set_bit(option_id::AUTO_TARGET, false);
    assert_eq!(resolve(&mut f, true), None);
    assert_eq!(f.gameplay.selection().selected(), None);
}

#[test]
fn auto_target_fills_an_empty_selection_only_when_the_caller_allows_it() {
    let mut f = Fixture::in_the_field();
    f.options.set_bit(option_id::AUTO_TARGET, true);
    assert_eq!(resolve(&mut f, false), None);
    assert_eq!(resolve(&mut f, true), Some(MONSTER));
    assert_eq!(f.gameplay.selection().selected(), Some(MONSTER));
}

#[test]
fn a_shared_player_killer_status_is_attackable_when_selected_and_never_acquired() {
    let mut f = Fixture::new();
    f.options.set_bit(option_id::AUTO_TARGET, true);
    let pk_lite = PublicWeenieFlags::PLAYER.bits() | PublicWeenieFlags::PLAYER_KILLER_LITE.bits();
    f.apply(&object(PLAYER, "+Horan", 0x10, pk_lite, 0.0));
    f.apply(&object(OTHER_PLAYER, "+Acdream", 0x10, pk_lite, 1.0));
    f.gameplay.selection.select(
        OTHER_PLAYER,
        SelectionSource::Keyboard,
        SelectionReason::Selected,
    );
    assert_eq!(resolve(&mut f, true), Some(OTHER_PLAYER));
    f.gameplay
        .selection
        .clear(SelectionSource::Keyboard, SelectionReason::Cleared);
    assert_eq!(resolve(&mut f, true), None, "no monster to acquire");
}

#[test]
fn a_selection_that_has_left_play_is_refused() {
    let mut f = Fixture::in_the_field();
    let Inbound::CreateObject(mut hidden) = object(
        MONSTER,
        "Drudge",
        0x10,
        PublicWeenieFlags::ATTACKABLE.bits(),
        5.0,
    ) else {
        unreachable!()
    };
    hidden.physics.state |= PhysicsState::HIDDEN.bits();
    hidden.physics.timestamps.instance = 2;
    f.apply(&Inbound::CreateObject(hidden));
    f.gameplay.selection.select(
        MONSTER,
        SelectionSource::Keyboard,
        SelectionReason::Selected,
    );
    assert_eq!(resolve(&mut f, false), None);
}

const OTHER_PLAYER: u32 = 0x5000_0011;

#[test]
fn combat_notifications_become_combat_lines_and_a_death_message() {
    // GameEventWiringTests.WireAll_KillerNotification_AppendsCombatLine and
    // the victim path (`onLocalPlayerDeath`).
    let mut f = Fixture::in_the_field();
    f.apply_bytes(&event(
        GameEventType::KILLER_NOTIFICATION,
        &string16("You killed the drudge!"),
    ));
    f.apply_bytes(&event(
        GameEventType::VICTIM_NOTIFICATION,
        &string16("You were killed by a drudge!"),
    ));
    f.apply_bytes(&event(
        GameEventType::EVASION_DEFENDER_NOTIFICATION,
        &string16("Drudge"),
    ));
    assert_eq!(
        f.notices(),
        [
            GameplayNotice::CombatLine("You killed the drudge!".into(), CombatLineKind::Info),
            GameplayNotice::CombatLine(
                "You were killed by a drudge!".into(),
                CombatLineKind::Error
            ),
            GameplayNotice::Combat(CombatNotice::EvadedIncoming {
                attacker_name: "Drudge".into()
            }),
        ]
    );
    assert!(f.events().contains(&GameplayEvent::LocalPlayerDied(
        "You were killed by a drudge!".into()
    )));
}

// -- Book, fellowship, allegiance through the owner --------------------------------

#[test]
fn an_opened_book_whose_page_has_no_text_asks_for_it() {
    // LiveSessionEventRouter.SendBookPageRequest.
    let mut f = Fixture::in_the_field();
    let mut w = ac_bits::Writer::new();
    w.u32(BOOK);
    w.i32(8);
    w.i32(8);
    w.i32(1000);
    w.i32(1);
    w.u32(0x8000_0001);
    w.string16l("F.P.").unwrap();
    w.string16l("").unwrap();
    w.u32(0); // the text is left out
    w.string16l("A Guide").unwrap();
    w.u32(0);
    w.string16l("").unwrap();
    f.apply_bytes(&event(GameEventType::BOOK_DATA_RESPONSE, &w.into_vec()));
    assert_eq!(f.sent(), [build_book_page_data(2, BOOK, 0)]);
    assert!(f.gameplay.book().request_pending());
    f.run(GameplayCommand::Book(BookCommand::Close));
    assert!(!f.gameplay.book().is_open());
}

/// A book of `max` pages, both written by the player with their text.
fn open_written_book(f: &mut Fixture, max: i32) {
    let mut w = ac_bits::Writer::new();
    w.u32(BOOK);
    w.i32(max);
    w.i32(max);
    w.i32(1000);
    w.i32(2);
    for text in ["one", "two"] {
        w.u32(PLAYER);
        w.string16l("Tester").unwrap();
        w.string16l("").unwrap();
        w.u32(1); // the text is included
        w.string16l(text).unwrap();
    }
    w.string16l("A Diary").unwrap();
    w.u32(0);
    w.string16l("").unwrap();
    f.apply_bytes(&event(GameEventType::BOOK_DATA_RESPONSE, &w.into_vec()));
}

#[test]
fn the_readers_turn_saves_the_page_left_and_its_close_saves_the_open_one() {
    // OpenAC BookPanelControllerTests.TurningAwayFromAnEditedPageSavesIt and
    // ClosingThePanelSavesTheOpenPageFirst, through the owner.
    let mut f = Fixture::in_the_field();
    open_written_book(&mut f, 4);
    f.sent();
    assert_eq!(f.gameplay.book().current_page(), 0);
    f.run(GameplayCommand::Book(BookCommand::TurnWriting {
        page: 1,
        typed: Some("rewritten".into()),
    }));
    assert_eq!(
        f.sent(),
        [build_book_modify_page(2, BOOK, 0, "rewritten").unwrap()]
    );
    assert_eq!(f.gameplay.book().current_page(), 1);
    assert_eq!(f.gameplay.book().page(0).unwrap().page_text, "rewritten");
    f.run(GameplayCommand::Book(BookCommand::CloseWriting(Some(
        "final words".into(),
    ))));
    assert_eq!(
        f.sent(),
        [build_book_modify_page(3, BOOK, 1, "final words").unwrap()]
    );
    assert!(!f.gameplay.book().is_open());
}

#[test]
fn a_leader_quitting_hands_the_lead_on_first() {
    // DirectGameRuntimeCommandAdapter.Quit: the hand-off goes before the quit.
    let mut f = Fixture::in_the_field();
    let member = |guid| ac_proto::fellowship::FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level: 10,
        max_health: 1,
        max_stamina: 1,
        max_mana: 1,
        current_health: 1,
        current_stamina: 1,
        current_mana: 1,
        share_loot: 0,
        name: "Fellow".into(),
    };
    let full = ac_proto::fellowship::FellowshipFullUpdate {
        members: vec![member(PLAYER), member(MONSTER)],
        name: "F".into(),
        leader_guid: PLAYER,
        share_xp: false,
        even_xp_split: false,
        open_fellow: false,
        locked: false,
        departed: Vec::new(),
    };
    f.gameplay.fellowship.apply_full_update(&full);
    f.run(GameplayCommand::Fellowship(FellowshipCommand::Quit {
        disband: false,
    }));
    assert_eq!(
        f.sent(),
        [
            wire_fellowship::build_assign_new_leader(2, MONSTER),
            wire_fellowship::build_quit(3, false),
        ]
    );
    f.run(GameplayCommand::Fellowship(FellowshipCommand::Quit {
        disband: true,
    }));
    assert_eq!(f.sent(), [wire_fellowship::build_quit(4, true)]);
}

#[test]
fn the_panel_and_the_vitals_request_share_one_subscription() {
    // OpenAC `RuntimeFellowshipState`: the panel-open flag and the vitals
    // request are apart and ORed; only a change of the OR goes out.
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Fellowship(
        FellowshipCommand::SetPanelOpen(true),
    ));
    f.run(GameplayCommand::Fellowship(
        FellowshipCommand::RequestVitals(true),
    ));
    f.run(GameplayCommand::Fellowship(
        FellowshipCommand::SetPanelOpen(false),
    ));
    assert_eq!(
        f.sent(),
        [wire_fellowship::build_update_request(2, true)],
        "the console still asks"
    );
    f.run(GameplayCommand::Fellowship(
        FellowshipCommand::RequestVitals(false),
    ));
    assert_eq!(f.sent(), [wire_fellowship::build_update_request(3, false)]);
}

#[test]
fn allegiance_requests_go_out_by_name() {
    let mut f = Fixture::in_the_field();
    f.run(GameplayCommand::Allegiance(AllegianceCommand::Info(
        "Acdream".into(),
    )));
    f.run(GameplayCommand::Allegiance(AllegianceCommand::Subscribe(
        false,
    )));
    assert_eq!(
        f.sent(),
        [
            wire_allegiance::build_info_request(2, "Acdream").unwrap(),
            wire_allegiance::build_update_request(3, false),
        ]
    );
}

#[test]
fn the_title_table_and_notices_reach_the_titles_and_set_title_goes_out() {
    // OpenAC LiveSessionEventRouter (CharacterTitle → Titles.ReplaceTable,
    // UpdateTitle → ApplyUpdateTitle), DirectGameRuntimeCommandAdapter.SetTitle
    // (WorldSession.SendSetTitle, SocialActions title body) and
    // CharacterTitleEventsTests for the payloads.
    let words = |w: &[u32]| w.iter().flat_map(|x| x.to_le_bytes()).collect::<Vec<u8>>();
    let mut f = Fixture::new();
    f.apply_bytes(&event(
        GameEventType::CHARACTER_TITLE,
        &words(&[1, 13, 3, 1, 5, 13]),
    ));
    assert_eq!(
        f.events(),
        [GameplayEvent::Titles(TitlesView {
            display_title_id: 13,
            earned: vec![1, 5, 13]
        })]
    );
    f.apply_bytes(&event(GameEventType::UPDATE_TITLE, &words(&[7, 1])));
    assert_eq!(f.gameplay.titles().display_title_id(), 7);
    assert!(f.gameplay.titles().has_earned_title(7));
    assert_eq!(f.events().len(), 1);
    // A notice repeating what is held says nothing.
    f.apply_bytes(&event(GameEventType::UPDATE_TITLE, &words(&[7, 1])));
    assert!(f.events().is_empty());
    f.run(GameplayCommand::SetTitle(5));
    assert_eq!(f.sent(), [ac_proto::social::build_set_display_title(1, 5)]);
    // Nothing moves until the server says so.
    assert_eq!(f.gameplay.titles().display_title_id(), 7);
    f.run(GameplayCommand::ShowTitles);
    assert_eq!(
        f.events(),
        [GameplayEvent::Titles(TitlesView {
            display_title_id: 7,
            earned: vec![1, 5, 13, 7]
        })]
    );
    f.gameplay.left_world(&mut f.world);
    assert_eq!(f.gameplay.titles().count(), 0);
}

#[test]
fn the_fellow_keys_step_through_the_fellowship_and_wrap() {
    // OpenAC `SelectionInteractionController.SelectFellow`: the members in
    // the fellowship's order; next from none is the first, previous from
    // none the last; both wrap.
    use ac_proto::fellowship::{FellowMember, FellowshipFullUpdate};
    const A: u32 = 0x5000_00A1;
    const B: u32 = 0x5000_00A2;
    let member = |guid| FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level: 10,
        max_health: 100,
        max_stamina: 100,
        max_mana: 100,
        current_health: 100,
        current_stamina: 100,
        current_mana: 100,
        share_loot: 0,
        name: "Fellow".into(),
    };
    let mut f = Fixture::new();
    f.run(GameplayCommand::CycleFellow { previous: false });
    assert_eq!(f.gameplay.selection().selected(), None);
    f.gameplay
        .fellowship
        .apply_full_update(&FellowshipFullUpdate {
            members: vec![member(PLAYER), member(A), member(B)],
            name: "The Fellows".into(),
            leader_guid: PLAYER,
            share_xp: true,
            even_xp_split: false,
            open_fellow: true,
            locked: false,
            departed: Vec::new(),
        });
    let mut step = |previous| {
        f.run(GameplayCommand::CycleFellow { previous });
        f.gameplay.selection().selected()
    };
    assert_eq!(step(false), Some(PLAYER));
    assert_eq!(step(false), Some(A));
    assert_eq!(step(false), Some(B));
    assert_eq!(step(false), Some(PLAYER));
    assert_eq!(step(true), Some(B));
}

#[test]
fn a_guest_list_reply_prints_the_list_in_green_as_one_text() {
    // OpenAC GameEventWiringTests.AGuestListReply_PrintsTheListInGreenAsOneText.
    let mut f = Fixture::new();
    let mut w = ac_bits::Writer::new();
    w.u32(0x1000_0002);
    w.u32(0x3);
    w.u32(0x5000_0001);
    w.u16(1);
    w.u16(64);
    w.u32(0x5000_0002);
    w.u32(1);
    w.string16l("Alice").unwrap();
    w.u32(0);
    f.apply_bytes(&event(GameEventType::UPDATE_HAR, &w.into_vec()));
    assert_eq!(
        f.notices(),
        [GameplayNotice::Text(
            "Guests:\n  Alice *\nAllegiance Permissions: guest access\n[ open house ]\n".into(),
            LogTextType::Default
        )]
    );
}
