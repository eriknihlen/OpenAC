//! The pre-game screens against the retail dats: connecting, character
//! selection, the credits and the creation wizard, ported from OpenAC's
//! `ConnectionUiControllerTests`, `CharacterManagementUiControllerTests`,
//! `CharacterManagementLiveDatTests`, `CreditsUiControllerTests`,
//! `CreditsLiveDatTests`, `CharacterCreationUiControllerTests`,
//! `CharacterCreationLiveDatTests` and
//! `CharacterScreensFixedCanvasArbiterTests` (each test names its source).
//! The screens bind the authored layouts, so every test reads the dats.

use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use ac_runtime::gameplay::GameTables;
use ac_runtime::session::creation::{
    CharacterCreation, ChargenAttribute, CreationCommand, SeededRandom,
};
use ac_runtime::session::pregame::{ConnectionStatus, ConnectionView, PreGameEvent};
use ac_runtime::session::selection::{
    SelectionButtons, SelectionEntry, SelectionError, SelectionLifecycle, SelectionOperation,
    SelectionSnapshot,
};
use ac_runtime::session::{CharacterSelector, Generation, LifecycleCommand, SessionCommand};
use ac_ui::content::DatContent;
use ac_ui::draw::Color;
use ac_ui::event::{Key, Modifiers, kind};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::dialog_factory::DialogType as DialogKind;
use ac_ui::pregame::chargen::{self, Page};
use ac_ui::pregame::{
    PreGameAssets, PreGameScreens, PreGameState, characters, connection, credits,
};
use ac_ui::root::{ElementMessage, UiOutput, UiRoot};
use ac_ui::tree::ElementId;
use ac_ui::widgets::{Button, Field, ListBox, Meter, Text};

fn content() -> DatContent {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    DatContent::new(Arc::new(
        ac_dat::DatCollection::open(dir).expect("open the retail dats"),
    ))
    .expect("master property table")
}

struct Harness {
    content: DatContent,
    ui: UiRoot,
    screens: PreGameScreens,
    state: PreGameState,
    now: Instant,
}

impl Harness {
    fn new() -> Self {
        let content = content();
        let mut ui = UiRoot::new();
        ui.set_size(1280.0, 720.0);
        let (screens, notices) = PreGameScreens::mount(&mut ui, &content, "openAC-rust test", None);
        assert!(notices.is_empty(), "{notices:?}");
        Self {
            content,
            ui,
            screens,
            state: PreGameState::default(),
            now: Instant::now(),
        }
    }

    fn update(&mut self) -> Vec<PanelOutput> {
        let assets = PreGameAssets::of(&self.content);
        let mut out = self
            .screens
            .update(&mut self.ui, assets, &mut self.state, self.now);
        out.extend(self.route());
        out
    }

    fn route(&mut self) -> Vec<PanelOutput> {
        let mut out = Vec::new();
        loop {
            let batch = self.ui.drain_output();
            if batch.is_empty() {
                return out;
            }
            for o in batch {
                if let UiOutput::Element { element, message } = o {
                    let assets = PreGameAssets::of(&self.content);
                    if let Some(more) =
                        self.screens
                            .on_message(&mut self.ui, assets, element, &message, self.now)
                    {
                        out.extend(more);
                    }
                }
            }
        }
    }

    fn click(&mut self, e: ElementId) -> Vec<PanelOutput> {
        self.ui.fire_event(kind::CLICK, e, None);
        self.route()
    }

    fn message(&mut self, e: ElementId, m: ElementMessage) -> Vec<PanelOutput> {
        self.ui.send_message(e, m);
        self.route()
    }

    fn visible(&self, e: ElementId) -> bool {
        let mut at = Some(e);
        while let Some(id) = at {
            if !self.ui.element(id).is_some_and(|el| el.visible) {
                return false;
            }
            at = self.ui.parent(id);
        }
        true
    }

    fn dialog(&self) -> Option<(DialogKind, String)> {
        let view = self.screens.open_dialog()?;
        let text = self.ui.widget::<Text>(view.message()?)?;
        let message = text
            .content_lines()
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
            .join(" ");
        Some((view.kind(), message.trim_end().to_owned()))
    }

    fn answer(&mut self, yes: bool) -> Vec<PanelOutput> {
        let (accept, reject) = self
            .screens
            .open_dialog()
            .map(|v| v.buttons())
            .unwrap_or_default();
        self.click(if yes { accept } else { reject }.expect("a dialog button"))
    }
}

fn text_of(ui: &UiRoot, e: ElementId) -> String {
    ac_ui::pregame::rich_text::joined(ui, e)
}

fn sessions(out: &[PanelOutput]) -> Vec<SessionCommand> {
    out.iter()
        .filter_map(|o| match o {
            PanelOutput::Session(c) => Some(c.clone()),
            _ => None,
        })
        .collect()
}

fn lifecycle(out: &[PanelOutput]) -> Vec<LifecycleCommand> {
    sessions(out)
        .into_iter()
        .filter_map(|c| match c {
            SessionCommand::Lifecycle(l) => Some(l),
            _ => None,
        })
        .collect()
}

fn creation(out: &[PanelOutput]) -> Vec<CreationCommand> {
    lifecycle(out)
        .into_iter()
        .filter_map(|c| match c {
            LifecycleCommand::Create(c) => Some(c),
            _ => None,
        })
        .collect()
}

fn view(status: ConnectionStatus, started: Instant, direct_launch: bool) -> ConnectionView {
    ConnectionView::advance(None, status, None, started, direct_launch)
}

#[test]
#[ignore = "needs the retail dat files"]
fn every_screen_mounts_from_the_interface_enumeration() {
    // OpenAC `CharacterManagementLiveDatTests.
    // EnumTable5_ResolvesAndImportsTheExactRetailScreenAndDialogs`,
    // `CreditsLiveDatTests.Category4_ResolvesAuthoredPictureAndTextRoots`,
    // `CharacterCreationLiveDatTests.EnumTable5_ResolvesTheMasterShellRootAndChildren`.
    let h = Harness::new();
    let dats = h.content.dats();
    use ac_ui::panels::options::chat_dat::resolve_data_id;
    assert_eq!(resolve_data_id(dats, characters::ROOT_ENUM, 5), 0x2100_0004);
    assert_eq!(resolve_data_id(dats, credits::ROOT_ENUM, 5), 0x2100_0003);
    assert_eq!(resolve_data_id(dats, connection::ROOT_ENUM, 5), 0x2100_0000);
    assert_eq!(
        resolve_data_id(dats, 2, 5),
        0x2100_003C,
        "the dialog catalog"
    );
    assert_ne!(resolve_data_id(dats, chargen::ROOT_ENUM, 5), 0);
    let c = h.screens.characters().expect("character selection");
    for (id, label) in [
        (characters::CREATE_ID, "Create Character"),
        (characters::ENTER_ID, "ENTER"),
        (characters::DELETE_ID, "DELETE"),
        (characters::RESTORE_ID, "RESTORE"),
        (characters::CREDITS_ID, "CREDITS"),
        (characters::EXIT_ID, "EXIT"),
    ] {
        let b = c.button(id).unwrap();
        assert_eq!(
            h.ui.widget::<Button>(b).and_then(|b| b.label.as_deref()),
            Some(label)
        );
    }
    assert!(h.screens.connection().is_some());
    assert!(h.screens.creation().is_some());
    assert!(h.screens.credits().is_some());
    assert!(!h.screens.is_active(&h.ui), "everything starts hidden");
    assert_eq!(h.ui.fixed_canvas(), None);
    assert!(h.content.take_errors().is_empty());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_connecting_screen_shows_the_meters_texts_and_errors() {
    // OpenAC `ConnectionUiControllerTests.
    // ConnectionAndCheckUseActualProgressAndAuthoredTextStates`,
    // `FastConnectionShowsCompletedMetersUntilMinimumPresentationTime`,
    // `RequiredUpdatesRemainVisibleWithExplanationAndCancel`,
    // `ReconnectClearsPriorErrorAndDoesNotKeepCompletedMeters`,
    // `InactiveAndDisposalReleaseCanvasAndCancelCallback`,
    // `BackdropCoversTheCanvasBehindAllAuthoredArtwork`.
    let mut h = Harness::new();
    let t = h.now;
    let c = h.screens.connection().unwrap();
    let root = c.root();
    let error = c.error_text();
    let cancel = c.cancel_button();
    let find = |ui: &UiRoot, id| ui.find_descendant(root, id).unwrap();
    let (meter, update_meter) = (
        find(&h.ui, connection::CONNECTION_METER_ID),
        find(&h.ui, connection::UPDATE_METER_ID),
    );
    let (text, update_text) = (
        find(&h.ui, connection::CONNECTION_TEXT_ID),
        find(&h.ui, connection::UPDATE_TEXT_ID),
    );
    let fills = |ui: &UiRoot| {
        (
            ui.widget::<Meter>(meter).and_then(|m| m.fill),
            ui.widget::<Meter>(update_meter).and_then(|m| m.fill),
        )
    };

    h.state.connection = Some(view(ConnectionStatus::Connecting, t, false));
    h.update();
    assert!(h.visible(root));
    assert_eq!(h.ui.fixed_canvas(), Some((800.0, 600.0)));
    assert_eq!(fills(&h.ui), (Some(0.0), Some(0.0)));
    assert_eq!(text_of(&h.ui, text), "Connecting...");
    let backdrop =
        h.ui.children(root)
            .into_iter()
            .find(|c| {
                h.ui.element(*c)
                    .is_some_and(|e| e.name.as_deref() == Some("ConnectionBackdrop"))
            })
            .expect("a backdrop");
    let e = h.ui.element(backdrop).unwrap();
    assert_eq!((e.left, e.top, e.width, e.height), (0.0, 0.0, 800.0, 600.0));
    assert_eq!(e.z_order, i32::MIN);

    h.state.connection = Some(view(ConnectionStatus::CheckingData, t, false));
    h.update();
    assert_eq!(fills(&h.ui), (Some(1.0), Some(0.0)));
    assert_eq!(text_of(&h.ui, text), "Connected!");
    assert_eq!(text_of(&h.ui, update_text), "Looking for data to patch...");

    // Ready before the minimum time: shown, both meters full.
    h.state.connection = Some(view(ConnectionStatus::Ready, t, false));
    h.now = t + Duration::from_millis(100);
    h.update();
    assert!(h.visible(root));
    assert_eq!(fills(&h.ui), (Some(1.0), Some(1.0)));
    // After it: gone, canvas released.
    h.now = t + Duration::from_secs(2);
    h.update();
    assert!(!h.visible(root));
    assert_eq!(h.ui.fixed_canvas(), None);

    // A refused update shows its explanation, wrapped, until cancelled.
    let explanation = "The server requires a game-data update, which this client cannot \
                       apply. Update the client's data files and connect again.";
    let started = t + Duration::from_secs(5);
    h.state.connection = Some(ConnectionView::advance(
        None,
        ConnectionStatus::Unsupported,
        Some(explanation.to_owned()),
        started,
        false,
    ));
    h.update();
    assert!(h.visible(root));
    assert!(h.visible(error));
    let lines = h.ui.widget::<Text>(error).unwrap().content_lines();
    assert!(lines.len() > 1, "wrapped");
    assert_eq!(
        lines
            .iter()
            .map(|l| l.text.clone())
            .collect::<Vec<_>>()
            .join(" "),
        explanation
    );
    assert_eq!(fills(&h.ui).1, Some(0.0));
    assert!(
        matches!(
            sessions(&h.click(cancel)).as_slice(),
            [SessionCommand::Shutdown]
        ),
        "cancel leaves"
    );
    // A new connection clears the error and the meters.
    h.state.connection = Some(view(
        ConnectionStatus::Connecting,
        t + Duration::from_secs(9),
        false,
    ));
    h.update();
    assert!(!h.visible(error));
    assert_eq!(text_of(&h.ui, error), "");
    assert_eq!(fills(&h.ui).0, Some(0.0));
    assert_eq!(text_of(&h.ui, update_text), "Update progress");
    // No connection at all: hidden, canvas released.
    h.state.connection = None;
    h.update();
    assert!(!h.visible(root));
    assert_eq!(h.ui.fixed_canvas(), None);
}

/// A roster as the session shows it: (id, name, seconds pending deletion).
fn selection(
    entries: &[(u32, &str, u32)],
    highlighted: u32,
    slots: i32,
    revision: u64,
) -> SelectionSnapshot {
    let entries: Vec<SelectionEntry> = entries
        .iter()
        .enumerate()
        .map(|(i, &(id, name, greyed))| SelectionEntry {
            active_index: i,
            character_id: id,
            name: name.to_owned(),
            seconds_greyed_out: greyed,
        })
        .collect();
    let chosen = entries.iter().find(|e| e.character_id == highlighted);
    let pending = chosen.is_some_and(SelectionEntry::is_pending_delete);
    SelectionSnapshot {
        generation: Generation(1),
        lifecycle: SelectionLifecycle::AwaitingSelection,
        revision,
        account_name: "testaccount".into(),
        slot_count: slots,
        world_name: "sawato".into(),
        server_population: None,
        highlighted_character_id: highlighted,
        highlighted_display_index: None,
        pending_delete_character_id: 0,
        last_restore_requested_character_id: 0,
        operation: SelectionOperation::None,
        error: None,
        buttons: SelectionButtons {
            can_enter: chosen.is_some_and(SelectionEntry::can_enter),
            can_delete: chosen.is_some() && !pending,
            can_restore: pending,
            delete_visible: !pending,
            restore_visible: pending,
            can_create: usize::try_from(slots).is_ok_and(|s| entries.len() < s),
        },
        entries,
    }
}

/// A session at character selection whose connection finished long ago.
fn at_selection(h: &mut Harness, snapshot: SelectionSnapshot, direct_launch: bool) {
    h.state.connection = Some(view(
        ConnectionStatus::Ready,
        h.now - Duration::from_secs(60),
        direct_launch,
    ));
    h.state.selection = Some(snapshot);
}

fn row_labels(h: &Harness) -> Vec<(String, f32, f32, bool, Color)> {
    h.screens
        .characters()
        .unwrap()
        .rows()
        .iter()
        .map(|&(row, _)| {
            let b = h.ui.widget::<Button>(row).unwrap();
            let e = h.ui.element(row).unwrap();
            (
                b.label.clone().unwrap_or_default(),
                e.height,
                e.top,
                b.selected(),
                b.label_color,
            )
        })
        .collect()
}

const RED: Color = Color::rgba(1.0, 0.0, 0.0, 1.0);

#[test]
#[ignore = "needs the retail dat files"]
fn the_selection_screen_lists_the_roster_and_mirrors_the_buttons() {
    // OpenAC `CharacterManagementUiControllerTests.
    // AuthoredChildContract_PreservesRuntimeOrderGreyTailHighlightAndButtonMatrix`,
    // `ActiveScreen_KeepsAuthoredRootExtent_AndSetsHostFixedCanvas`,
    // `WorldName_TicksFromSnapshot_IntoTheWorldTextElement`,
    // `CreateButton_GhostsWhenRosterReachesTheSlotCeiling_AndUnGhostsBelowIt`.
    let mut h = Harness::new();
    let roster = [
        (0x5000_0001, "Alpha", 0),
        (0x5000_0002, "Zulu", 0),
        (0x5000_0003, "Aaron", 3600),
    ];
    at_selection(&mut h, selection(&roster, 0x5000_0001, 5, 1), false);
    h.update();
    let c = h.screens.characters().unwrap();
    let (root, world) = (c.root(), c.world_text());
    let button = |id| c.button(id).unwrap();
    let (create, enter, delete, restore) = (
        button(characters::CREATE_ID),
        button(characters::ENTER_ID),
        button(characters::DELETE_ID),
        button(characters::RESTORE_ID),
    );
    assert!(h.visible(root));
    assert_eq!(
        h.ui.element(root).map(|e| (e.width, e.height)),
        Some((800.0, 600.0))
    );
    assert_eq!(h.ui.fixed_canvas(), Some((800.0, 600.0)));
    assert!(h.screens.is_active(&h.ui));
    assert_eq!(
        row_labels(&h),
        [
            ("Alpha".to_owned(), 64.0, 0.0, true, Color::WHITE),
            ("Zulu".to_owned(), 64.0, 64.0, false, Color::WHITE),
            ("Aaron".to_owned(), 64.0, 128.0, false, RED),
        ]
    );
    assert_eq!(text_of(&h.ui, world), "sawato");
    let enabled = |h: &Harness, e: ElementId| h.ui.element(e).is_some_and(|e| e.enabled());
    assert!(h.visible(create) && enabled(&h, create));
    assert!(enabled(&h, enter));
    assert!(h.visible(delete) && enabled(&h, delete));
    assert!(!h.visible(restore));

    // A row's click highlights it at once and asks the session.
    let aaron = h.screens.characters().unwrap().rows()[2].0;
    let out = h.click(aaron);
    assert_eq!(
        lifecycle(&out),
        [LifecycleCommand::Highlight(CharacterSelector::Id(
            0x5000_0003
        ))]
    );
    assert!(row_labels(&h)[2].3);
    // The session's answer moves the buttons.
    at_selection(&mut h, selection(&roster, 0x5000_0003, 5, 2), false);
    h.update();
    assert!(!enabled(&h, enter));
    assert!(!h.visible(delete));
    assert!(h.visible(restore) && enabled(&h, restore));

    // A full roster ghosts create; the world's name follows the snapshot.
    let mut full = selection(
        &[
            (1, "A", 0),
            (2, "B", 0),
            (3, "C", 0),
            (4, "D", 0),
            (5, "E", 0),
        ],
        1,
        5,
        3,
    );
    full.world_name = "Frostfell".into();
    at_selection(&mut h, full, false);
    h.update();
    assert!(h.visible(create) && !enabled(&h, create));
    assert_eq!(text_of(&h.ui, world), "Frostfell");
}

#[test]
#[ignore = "needs the retail dat files"]
fn rows_share_the_list_by_slot_and_shrink_for_a_large_roster() {
    // OpenAC `CharacterManagementUiControllerTests.
    // RowHeight_UsesAllowedSlotsAndClampsAtOneTenthForLargeRosters`.
    let mut h = Harness::new();
    let names: Vec<String> = (0..12).map(|i| format!("Character {i:02}")).collect();
    let roster: Vec<(u32, &str, u32)> = names
        .iter()
        .enumerate()
        .map(|(i, n)| (0x5000_0100 + i as u32, n.as_str(), 0))
        .collect();
    at_selection(&mut h, selection(&roster, 0x5000_0100, 5, 1), false);
    h.update();
    let rows = row_labels(&h);
    assert_eq!(rows.len(), 12);
    for (i, row) in rows.iter().enumerate() {
        assert_eq!((row.1, row.2), (32.0, i as f32 * 32.0));
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn delete_wants_the_word_typed_then_waits_for_the_server() {
    // OpenAC `CharacterManagementUiControllerTests.
    // DeleteConfirmation_IsCaseInsensitive_ThenWaitsThroughAckUntilFreshRoster`
    // and `CharacterManagementLiveDatTests` (the strings).
    let mut h = Harness::new();
    let roster = [(0x5000_0001, "Alpha", 0), (0x5000_0002, "Zulu", 0)];
    at_selection(&mut h, selection(&roster, 0x5000_0002, 5, 1), false);
    h.update();
    let delete = h
        .screens
        .characters()
        .unwrap()
        .button(characters::DELETE_ID)
        .unwrap();
    assert_eq!(
        lifecycle(&h.click(delete)),
        [LifecycleCommand::RequestDelete(None)]
    );
    // The session opens the confirmation for Zulu.
    let mut pending = selection(&roster, 0x5000_0002, 5, 2);
    pending.pending_delete_character_id = 0x5000_0002;
    at_selection(&mut h, pending.clone(), false);
    h.update();
    let (kind, text) = h.dialog().expect("the delete question");
    assert_eq!(kind, DialogKind::ConfirmationTextInput);
    assert!(text.contains("Zulu") && text.contains("'DELETE'"), "{text}");
    let input = h.screens.open_dialog().and_then(|v| v.input()).unwrap();
    h.update();
    assert_eq!(
        h.ui.keyboard_focus(),
        Some(input),
        "the entry takes the keys"
    );
    for c in "delete".chars() {
        h.ui.on_char(c);
    }
    assert_eq!(
        h.ui.widget::<Field>(input).map(Field::text).as_deref(),
        Some("delete")
    );
    assert_eq!(
        lifecycle(&h.answer(true)),
        [LifecycleCommand::ConfirmDelete]
    );
    // Sent: the wait shows through the acknowledgement.
    let mut sent = selection(&roster, 0x5000_0002, 5, 3);
    sent.operation = SelectionOperation::DeleteRequested;
    at_selection(&mut h, sent, false);
    h.update();
    assert_eq!(
        h.dialog(),
        Some((DialogKind::Wait, "Please Wait".to_owned()))
    );
    at_selection(&mut h, selection(&roster[..1], 0x5000_0001, 5, 4), false);
    h.update();
    assert_eq!(h.dialog(), None, "the fresh roster closes the wait");

    // Anything but the word cancels.
    let mut pending = selection(&roster, 0x5000_0002, 5, 5);
    pending.pending_delete_character_id = 0x5000_0002;
    at_selection(&mut h, pending, false);
    h.update();
    let input = h.screens.open_dialog().and_then(|v| v.input()).unwrap();
    h.update();
    for c in "nope".chars() {
        h.ui.on_char(c);
    }
    let _ = input;
    assert_eq!(lifecycle(&h.answer(true)), [LifecycleCommand::Cancel]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn entering_waits_until_the_session_answers_or_refuses() {
    // OpenAC `CharacterManagementUiControllerTests.
    // AuthoredRowDoubleActivation_EntersTheHighlightedCharacter` and
    // `RestoreSilenceExpires_EnterTransitions_AndErrorUsesMessageDialog`;
    // the window's requests are answered asynchronously.
    let mut h = Harness::new();
    let roster = [(0x5000_0001, "Alpha", 0)];
    at_selection(&mut h, selection(&roster, 0x5000_0001, 5, 1), false);
    h.update();
    let c = h.screens.characters().unwrap();
    let enter = c.button(characters::ENTER_ID).unwrap();
    let row = c.rows()[0].0;
    assert_eq!(lifecycle(&h.click(enter)), [LifecycleCommand::Enter(None)]);
    assert_eq!(
        h.dialog(),
        Some((DialogKind::Wait, "Entering World".to_owned()))
    );
    h.update();
    assert!(h.dialog().is_some(), "the unanswered enter keeps its wait");
    h.state.refusals.push("refused".into());
    h.update();
    assert_eq!(h.dialog(), None, "a refusal closes it");

    // A double click on a row enters too; the session's answer keeps the
    // wait up until the world.
    assert_eq!(
        lifecycle(&h.message(row, ElementMessage::DoubleClicked)),
        [LifecycleCommand::Enter(None)]
    );
    let mut entering = selection(&roster, 0x5000_0001, 5, 2);
    entering.lifecycle = SelectionLifecycle::EnteringWorld;
    at_selection(&mut h, entering, false);
    h.update();
    assert_eq!(
        h.dialog(),
        Some((DialogKind::Wait, "Entering World".to_owned()))
    );
    let mut in_world = selection(&roster, 0x5000_0001, 5, 3);
    in_world.lifecycle = SelectionLifecycle::InWorld;
    at_selection(&mut h, in_world, false);
    h.update();
    assert_eq!(h.dialog(), None);
    assert!(!h.screens.is_active(&h.ui));
    assert_eq!(h.ui.fixed_canvas(), None);
}

#[test]
#[ignore = "needs the retail dat files"]
fn exit_asks_first_and_an_error_is_a_message_that_cancels() {
    // OpenAC `CharacterManagementUiControllerTests.
    // ExitButton_OpenThenCancel_KeepsScreenActive_NoExitRequested`,
    // `ExitButton_OpenThenConfirm_ReachesGracefulShutdownSeam`,
    // `ExitButton_SecondClickWhileOpen_IsNoOp`,
    // `DirectCharacterEntryErrorRestoresSelectionAndItsErrorDialog`.
    let mut h = Harness::new();
    let roster = [(0x5000_0001, "Alpha", 0)];
    at_selection(&mut h, selection(&roster, 0x5000_0001, 5, 1), false);
    h.update();
    let exit = h
        .screens
        .characters()
        .unwrap()
        .button(characters::EXIT_ID)
        .unwrap();
    assert!(h.click(exit).is_empty());
    assert_eq!(
        h.dialog(),
        Some((
            DialogKind::Confirmation,
            "Are you sure you want to leave?".to_owned()
        ))
    );
    assert!(h.answer(false).is_empty());
    assert!(h.screens.is_active(&h.ui));
    h.click(exit);
    assert!(matches!(
        sessions(&h.answer(true)).as_slice(),
        [SessionCommand::Shutdown]
    ));

    let mut failed = selection(&roster, 0x5000_0001, 5, 2);
    failed.error = Some(SelectionError {
        raw_code: 0x0D,
        code: None,
        message: "That character is unavailable.".into(),
    });
    at_selection(&mut h, failed, true);
    h.update();
    assert_eq!(
        h.dialog(),
        Some((
            DialogKind::Message,
            "That character is unavailable.".to_owned()
        ))
    );
    assert!(
        h.screens.characters().unwrap().is_active(),
        "an error shows selection"
    );
    assert_eq!(lifecycle(&h.answer(true)), [LifecycleCommand::Cancel]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_direct_launch_hides_selection_until_the_character_logs_off() {
    // OpenAC `CharacterManagementUiControllerTests.
    // DirectCharacterLaunchSkipsSelectionAndEntryPresentationButShowsSelectionAfterLogout`.
    let mut h = Harness::new();
    let roster = [(0x5000_0001, "Alpha", 0)];
    let mut entering = selection(&roster, 0x5000_0001, 5, 1);
    entering.lifecycle = SelectionLifecycle::EnteringWorld;
    at_selection(&mut h, entering, true);
    h.update();
    assert!(!h.screens.is_active(&h.ui));
    assert_eq!(h.dialog(), None);
    let mut in_world = selection(&roster, 0x5000_0001, 5, 2);
    in_world.lifecycle = SelectionLifecycle::InWorld;
    at_selection(&mut h, in_world, true);
    h.update();
    // Logged off back to selection: it shows.
    let mut back = selection(&roster, 0x5000_0001, 5, 1);
    back.generation = Generation(2);
    at_selection(&mut h, back, false);
    h.update();
    assert!(h.screens.characters().unwrap().is_active());
    assert_eq!(h.ui.fixed_canvas(), Some((800.0, 600.0)));
    // A new connection enters the configured character again: hidden.
    let mut connecting = selection(&[], 0, 5, 1);
    connecting.generation = Generation(3);
    connecting.lifecycle = SelectionLifecycle::Connecting;
    at_selection(&mut h, connecting, true);
    h.update();
    let mut entering = selection(&roster, 0x5000_0001, 5, 2);
    entering.generation = Generation(3);
    entering.lifecycle = SelectionLifecycle::EnteringWorld;
    at_selection(&mut h, entering, true);
    h.update();
    assert!(!h.screens.characters().unwrap().is_active());
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_credits_scroll_over_selection_and_any_key_returns() {
    // OpenAC `CreditsLiveDatTests.Category4_ResolvesAuthoredPictureAndTextRoots`,
    // `CreditsUiControllerTests.Activate_UsesAuthoredCanvas_TextTiming_AndCyclicPictureStrip`,
    // `AnyKey_ShowsWaitForAFrame_ThenReturnsToCharacterManagement`,
    // `CharacterManagementUiControllerTests.
    // CreditsButton_QueuesTheCreditsMode_AndPresentationCanBeSuppressedForIt`.
    let mut h = Harness::new();
    {
        let dats = h.content.dats();
        use ac_ui::import::import_infos_rooted;
        let text = import_infos_rooted(&h.content, 0x2100_0003, credits::TEXT_ROOT_ID).unwrap();
        let picture =
            import_infos_rooted(&h.content, 0x2100_0003, credits::PICTURE_ROOT_ID).unwrap();
        assert_eq!(
            (picture.x, picture.y, picture.width, picture.height),
            (0.0, 0.0, 400.0, 600.0)
        );
        assert_eq!(
            (text.x, text.y, text.width, text.height),
            (400.0, 0.0, 400.0, 600.0)
        );
        let (area, table, seconds, pictures) =
            credits::authored_properties(&text, &picture).unwrap();
        assert_eq!(
            (area, table, seconds),
            (credits::TEXT_AREA_ID, 0x2300_0008, 20.0)
        );
        assert_eq!(
            pictures,
            (0..7).map(|i| 0x0600_5F14 + i).collect::<Vec<u32>>()
        );
        let strings = ac_ui::strings::StringResolver::new({
            let dats = Arc::clone(dats);
            move |id| dats.get(id).ok().flatten()
        });
        let count = (1..=4096)
            .take_while(|i| {
                strings
                    .resolve(
                        table,
                        ac_ui::strings::compute_hash(&format!("ID_Credits{i}")),
                        0,
                    )
                    .is_some()
            })
            .count();
        assert_eq!(count, 2345);
    }
    let roster = [(0x5000_0001, "Alpha", 0)];
    at_selection(&mut h, selection(&roster, 0x5000_0001, 5, 1), false);
    h.update();
    let button = h
        .screens
        .characters()
        .unwrap()
        .button(characters::CREDITS_ID)
        .unwrap();
    h.click(button);
    let c = h.screens.credits().unwrap();
    assert!(c.is_active());
    assert!(
        !h.screens.characters().unwrap().is_active(),
        "selection is covered"
    );
    assert_eq!(h.ui.fixed_canvas(), Some((800.0, 600.0)));
    let area = h.ui.element(c.text_area()).unwrap();
    assert_eq!(area.top, 600.0, "the text starts below the field");
    assert_eq!(c.pictures().len(), 1);
    let picture = h.ui.element(c.pictures()[0]).unwrap();
    assert_eq!(picture.top, 601.0);
    assert!(c.duration() > 20.0);

    // Six seconds on, the text and the first picture are on screen.
    let surface = c.surface();
    let text_area = c.text_area();
    let start = h.now;
    h.now = start + Duration::from_secs(6);
    h.update();
    let list = h.ui.draw((1280.0, 720.0), &h.content);
    let pictures: Vec<_> = list
        .commands()
        .filter(|cmd| {
            matches!(cmd, ac_ui::draw::DrawCmd::Sprite { texture: ac_ui::draw::TextureRef::Surface(id), .. } if (0x0600_5F14..=0x0600_5F1A).contains(id))
        })
        .collect();
    assert!(!pictures.is_empty(), "a picture draws");
    let glyphs = list
        .commands()
        .filter(|cmd| matches!(cmd, ac_ui::draw::DrawCmd::Glyphs { .. }))
        .count();
    assert!(glyphs > 0, "the text draws");
    // Frame after frame (the root lays its children out on each draw),
    // the strip keeps moving up with the text.
    let first = h.screens.credits().unwrap().pictures()[0];
    let picture_top = |h: &Harness| h.ui.element(first).unwrap().top;
    let before = picture_top(&h);
    for step in 1..=10 {
        h.now = start + Duration::from_secs(6) + Duration::from_millis(100 * step);
        h.update();
        let _ = h.ui.draw((1280.0, 720.0), &h.content);
    }
    assert!(picture_top(&h) < before, "{} {before}", picture_top(&h));
    assert!(picture_top(&h) < 600.0);
    let top = h.ui.element(text_area).unwrap().top;
    assert!(top < 600.0, "{top}");
    // A key shows the wait for a frame, then selection returns.
    assert_eq!(h.ui.keyboard_focus(), Some(surface));
    h.ui.on_key_down(Key::Space, Modifiers::NONE);
    h.route();
    assert_eq!(
        h.dialog(),
        Some((DialogKind::Wait, "Please wait ...".to_owned()))
    );
    h.update();
    assert!(h.screens.credits().unwrap().is_active());
    h.update();
    assert!(!h.screens.credits().unwrap().is_active());
    assert_eq!(h.dialog(), None);
    h.update();
    assert!(h.screens.characters().unwrap().is_active());
    assert_eq!(row_labels(&h).len(), 1);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_direct_launch_or_a_late_window_skips_the_progress_but_not_a_refused_update() {
    // OpenAC `ConnectionUiControllerTests.
    // DirectLaunchSkipsProgressButStillShowsUnsupportedUpdates`,
    // `SlowConnectionDoesNotAddAnotherDelayAfterReadiness`.
    let mut h = Harness::new();
    let t = h.now;
    let root = h.screens.connection().unwrap().root();
    h.state.connection = Some(view(ConnectionStatus::CheckingData, t, true));
    h.update();
    assert!(!h.visible(root));
    assert_eq!(h.ui.fixed_canvas(), None);
    h.state.connection = Some(ConnectionView::advance(
        None,
        ConnectionStatus::Unsupported,
        Some("Update required.".into()),
        t,
        true,
    ));
    h.update();
    assert!(h.visible(root));

    // A window that attaches long after the connection was ready.
    let mut h = Harness::new();
    let root = h.screens.connection().unwrap().root();
    h.state.connection = Some(view(
        ConnectionStatus::Ready,
        h.now - Duration::from_secs(5),
        false,
    ));
    h.update();
    assert!(!h.visible(root));
}

/// The wizard over the runtime's own creation owner: every request the
/// screen makes is carried out by the owner and its snapshot fed back.
struct Wizard {
    h: Harness,
    owner: CharacterCreation,
}

const ALUVIAN: u32 = 1;
const OLTHOI: u32 = 12;

impl Wizard {
    fn new() -> Self {
        let mut h = Harness::new();
        let tables = GameTables::load(h.content.dats()).expect("the game tables");
        let options = Arc::clone(tables.chargen());
        let mut owner =
            CharacterCreation::new(Arc::clone(&options), Box::new(SeededRandom::new(7)));
        owner.begin(Generation(1));
        at_selection(
            &mut h,
            selection(&[(0x5000_0001, "Alpha", 0)], 0x5000_0001, 5, 1),
            false,
        );
        h.state.options = Some(options);
        h.state.creation = Some(owner.snapshot());
        h.update();
        Self { h, owner }
    }

    /// Carry out what the screen asked; true when anything was asked.
    fn run(&mut self, out: &[PanelOutput]) -> Vec<CreationCommand> {
        let commands = creation(out);
        for c in &commands {
            match c {
                CreationCommand::Finish {
                    confirm_unspent_credits,
                } => {
                    let _ = self.owner.try_begin_finish(1, 5, *confirm_unspent_credits);
                }
                other => {
                    self.owner.apply_command(other);
                }
            }
        }
        self.h.state.creation = Some(self.owner.snapshot());
        commands
    }

    /// One frame, carrying out what it asks until quiet.
    fn settle(&mut self) -> Vec<CreationCommand> {
        let mut all = Vec::new();
        for _ in 0..4 {
            let out = self.h.update();
            let ran = self.run(&out);
            if ran.is_empty() {
                break;
            }
            all.extend(ran);
        }
        all
    }

    fn click(&mut self, e: ElementId) -> Vec<CreationCommand> {
        let out = self.h.click(e);
        let mut ran = self.run(&out);
        ran.extend(self.settle());
        ran
    }

    fn message(&mut self, e: ElementId, m: ElementMessage) -> Vec<CreationCommand> {
        let out = self.h.message(e, m);
        let mut ran = self.run(&out);
        ran.extend(self.settle());
        ran
    }

    /// The mouse pressed at `e`'s near end (its top left corner, element
    /// coordinates), dragged to its far end (bottom right) and released:
    /// the widget itself turns that into whatever messages it reports.
    fn drag_across(&mut self, e: ElementId) -> Vec<CreationCommand> {
        let (w, h) = self
            .h
            .ui
            .element(e)
            .map(|el| (el.width as i32, el.height as i32))
            .unwrap();
        for (k, x, y) in [
            (kind::MOUSE_DOWN, 0, 0),
            (kind::MOUSE_MOVE, w, h),
            (kind::MOUSE_UP, w, h),
        ] {
            let event = ac_ui::event::UiEvent {
                kind: k,
                data1: x,
                data2: y,
                target: Some(e),
                ..ac_ui::event::UiEvent::default()
            };
            self.h.ui.send_event(e, &event);
        }
        let out = self.h.route();
        let mut ran = self.run(&out);
        ran.extend(self.settle());
        ran
    }

    fn screen(&self) -> &chargen::CreationScreen {
        self.h.screens.creation().unwrap()
    }

    fn shell(&self, id: u32) -> ElementId {
        self.screen().button(id).unwrap()
    }

    fn open(&mut self) -> Vec<CreationCommand> {
        let create = self
            .h
            .screens
            .characters()
            .unwrap()
            .button(characters::CREATE_ID)
            .unwrap();
        self.click(create)
    }

    fn page_shown(&self) -> Vec<Page> {
        [
            Page::Heritage,
            Page::Profession,
            Page::Skills,
            Page::Appearance,
            Page::Town,
            Page::Summary,
        ]
        .into_iter()
        .filter(|p| self.h.visible(self.screen().page_root(*p)))
        .collect()
    }

    fn selected(&self, e: ElementId) -> bool {
        self.h.ui.widget::<Button>(e).is_some_and(Button::selected)
    }

    fn enabled(&self, e: ElementId) -> bool {
        self.h.ui.element(e).is_some_and(|e| e.enabled())
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn create_opens_the_wizard_on_a_rolled_character_of_the_other_gender() {
    // OpenAC `CharacterCreationUiControllerTests.
    // ActiveScreen_KeepsAuthoredRootExtent_AndDefaultsToTheHeritagePage`,
    // `Open_RollsACharacterThenFlipsTheGenderToTheOpposite`,
    // `Open_SetsFixedCanvas_ExitConfirmClosesAndNullsIt`,
    // `CharacterScreensFixedCanvasArbiterTests.
    // CreateButtonClick_OpensChargen_AndExitConfirmReturnsToManagement`.
    let mut w = Wizard::new();
    assert!(!w.screen().is_open());
    let ran = w.open();
    assert_eq!(ran[0], CreationCommand::RandomizeCharacter);
    let rolled = w.owner.snapshot();
    // The appearance page's face choice takes the rolled headgear off
    // before the gender turns over.
    assert!(matches!(
        ran[1..],
        [
            CreationCommand::SetAppearance(
                ac_runtime::session::creation::AppearanceSlot::HeadgearStyle,
                ac_runtime::session::creation::UNSET
            ),
            CreationCommand::SelectGender(g)
        ] if g == rolled.gender_key
    ));
    assert!(rolled.heritage_id != 0 && rolled.template != u32::MAX);
    let root = w.screen().root();
    assert!(w.h.visible(root));
    assert_eq!(
        w.h.ui.element(root).map(|e| (e.width, e.height)),
        Some((800.0, 600.0))
    );
    assert_eq!(w.page_shown(), [Page::Heritage]);
    assert!(w.selected(w.shell(chargen::TAB_IDS[0])));
    assert_eq!(w.h.ui.fixed_canvas(), Some((800.0, 600.0)));
    // Exit asks, and a yes closes the wizard: selection is still there.
    let exit = w.shell(chargen::EXIT_ID);
    w.click(exit);
    assert_eq!(w.h.dialog().map(|d| d.0), Some(DialogKind::Confirmation));
    let out = w.h.answer(true);
    assert!(creation(&out).is_empty());
    w.settle();
    assert!(!w.screen().is_open());
    assert!(w.h.screens.characters().unwrap().is_active());
    assert_eq!(
        w.h.ui.fixed_canvas(),
        Some((800.0, 600.0)),
        "selection keeps it"
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_shell_steps_through_the_pages() {
    // OpenAC `CharacterCreationUiControllerTests.
    // TabClick_SwitchesToTheClickedPage_FreeOfValidation`,
    // `Next_AdvancesOnePageAtATime_AndBackReturns`, `Next_AtSummary_IsANoOp`,
    // `Finish_GhostedExceptOnSummary`, `Random_IsDisabledOnSkillsPageOnly`,
    // `Back_AtHeritage_OpensExitConfirmation`, `Exit_Cancel_LeavesTheScreenOpen`.
    let mut w = Wizard::new();
    w.open();
    let (next, back, finish, random) = (
        w.shell(chargen::NEXT_ID),
        w.shell(chargen::BACK_ID),
        w.shell(chargen::FINISH_ID),
        w.shell(chargen::RANDOM_ID),
    );
    assert!(!w.enabled(finish));
    w.click(next);
    assert_eq!(w.page_shown(), [Page::Profession]);
    w.click(next);
    assert_eq!(w.page_shown(), [Page::Skills]);
    assert!(!w.enabled(random), "no random on the skills page");
    w.click(back);
    assert_eq!(w.page_shown(), [Page::Profession]);
    assert!(w.enabled(random));
    w.click(w.shell(chargen::TAB_IDS[4]));
    assert_eq!(w.page_shown(), [Page::Town]);
    assert!(w.selected(w.shell(chargen::TAB_IDS[4])));
    w.click(w.shell(chargen::TAB_IDS[5]));
    assert_eq!(w.page_shown(), [Page::Summary]);
    assert!(w.h.visible(finish) && w.enabled(finish));
    assert!(!w.h.visible(next));
    w.click(next);
    assert_eq!(w.page_shown(), [Page::Summary]);
    w.click(w.shell(chargen::TAB_IDS[0]));
    w.click(back);
    assert_eq!(w.h.dialog().map(|d| d.0), Some(DialogKind::Confirmation));
    w.h.answer(false);
    assert!(w.screen().is_open());
}

#[test]
#[ignore = "needs the retail dat files"]
fn an_olthoi_skips_profession_skills_and_town() {
    // OpenAC `CharacterCreationUiControllerTests.
    // OlthoiHeritage_HidesProfessionSkillsAndTownTabs`,
    // `HeritageButtonClick_RestoresHiddenTabsAtClickTime_ExceptLugian`.
    let mut w = Wizard::new();
    w.open();
    let olthoi = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == OLTHOI)
        .map(|b| b.0)
        .unwrap();
    assert_eq!(w.click(olthoi), [CreationCommand::SelectHeritage(OLTHOI)]);
    let tabs: Vec<ElementId> = chargen::TAB_IDS.iter().map(|&id| w.shell(id)).collect();
    for i in [1, 2, 4] {
        assert!(!w.h.visible(tabs[i]), "tab {i} hidden");
    }
    w.click(w.shell(chargen::NEXT_ID));
    assert_eq!(w.page_shown(), [Page::Appearance]);
    w.click(w.shell(chargen::NEXT_ID));
    assert_eq!(w.page_shown(), [Page::Summary]);
    w.click(w.shell(chargen::BACK_ID));
    assert_eq!(w.page_shown(), [Page::Appearance]);
    w.click(w.shell(chargen::BACK_ID));
    assert_eq!(w.page_shown(), [Page::Heritage]);
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    assert!(w.h.visible(tabs[1]) && w.h.visible(tabs[2]) && w.h.visible(tabs[4]));
}

/// The attribute sliders and the shade slider are value sliders: dragging
/// the real widget sets the value (GitHub issue #5, the sliders did
/// nothing). OpenAC wires them through `UiScrollbar.ScalarChanged`.
#[test]
#[ignore = "needs the retail dat files"]
fn dragging_the_attribute_and_shade_sliders_sets_their_values() {
    use ac_runtime::session::creation::ShadeSlot;
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[1]));
    let (_, slider, _) = w
        .screen()
        .profession_page()
        .slider(ChargenAttribute::Strength)
        .unwrap();
    // Dragged to the far end: the last value set is the top one.
    let ran = w.drag_across(slider.unwrap());
    assert_eq!(
        ran.last(),
        Some(&CreationCommand::SetAttribute(
            ChargenAttribute::Strength,
            100
        )),
        "{ran:?}"
    );
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert_eq!(w.page_shown(), [Page::Appearance]);
    let (_, shade) = w.screen().appearance_page().grad_and_shade();
    let ran = w.drag_across(shade.unwrap());
    assert!(
        matches!(ran.last(), Some(CreationCommand::SetShade(ShadeSlot::Hair, v)) if (*v - 1.0).abs() < 1e-6),
        "{ran:?}"
    );
}

/// The face choice takes the headgear off (so the hair shows) and keeps
/// it; the clothes choice puts it back. The page starts on the face
/// choice, so the opening roll's headgear is off from the start and stays
/// off in the request unless the clothes choice is opened (GitHub issue
/// #5, a hat covered the hair).
#[test]
#[ignore = "needs the retail dat files"]
fn the_face_choice_takes_the_headgear_off_and_the_clothes_choice_puts_it_back() {
    use ac_runtime::session::creation::{AppearanceSlot, UNSET};
    use ac_ui::pregame::chargen::appearance::{self, Part};
    let mut w = Wizard::new();
    let opening = w.open();
    let off = CreationCommand::SetAppearance(AppearanceSlot::HeadgearStyle, UNSET);
    let at = |c: &CreationCommand| opening.iter().position(|o| o == c);
    let gender = opening
        .iter()
        .position(|c| matches!(c, CreationCommand::SelectGender(_)))
        .expect("the opening roll turns the gender over");
    assert!(at(&off).is_some_and(|i| i < gender), "{opening:?}");
    assert_eq!(w.owner.snapshot().appearance.headgear_style, UNSET);
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    let root = w.screen().page_root(Page::Appearance);
    let find = |w: &Wizard, id| w.h.ui.find_descendant(root, id).unwrap();

    // Put a hat on in the clothes choice.
    w.click(find(&w, appearance::CLOTHES_ID));
    let headgear = w.screen().appearance_page().spin(Part::Headgear).unwrap();
    let up =
        w.h.ui
            .find_descendant(headgear, appearance::INCREMENT_ARROW_ID)
            .unwrap();
    // The spin's steps pass through "none": step until a hat is on.
    w.click(up);
    for _ in 0..3 {
        if w.owner.snapshot().appearance.headgear_style != UNSET {
            break;
        }
        w.click(up);
    }
    let hat = w.owner.snapshot().appearance.headgear_style;
    assert_ne!(hat, UNSET);

    assert_eq!(w.click(find(&w, appearance::FACE_ID)), [off]);
    assert_eq!(w.owner.snapshot().appearance.headgear_style, UNSET);
    assert_eq!(
        w.click(find(&w, appearance::CLOTHES_ID)),
        [CreationCommand::SetAppearance(
            AppearanceSlot::HeadgearStyle,
            hat
        )]
    );
    assert_eq!(w.owner.snapshot().appearance.headgear_style, hat);

    // Never opening the clothes: the request goes without headgear.
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    w.owner
        .apply_command(&CreationCommand::SetName("Hatless".into()));
    let request = w.owner.try_begin_finish(0, 5, true).unwrap();
    assert_eq!(request.request.appearance.headgear_style, UNSET);
}

/// The page outlives the wizard: opened again after leaving it on the
/// clothes with the shirt picked, it starts on the face with the hair
/// picked, so the swatches and the shade edit the hair, not the shirt.
/// OpenAC's `SelectChoice` sets the part with the choice.
#[test]
#[ignore = "needs the retail dat files"]
fn reopening_the_wizard_starts_on_the_face_with_the_hair_picked() {
    use ac_ui::pregame::chargen::appearance::{self, Choice, Part};
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    let root = w.screen().page_root(Page::Appearance);
    let clothes =
        w.h.ui
            .find_descendant(root, appearance::CLOTHES_ID)
            .unwrap();
    w.click(clothes);
    let shirt = w.screen().appearance_page().spin(Part::Shirt).unwrap();
    w.message(shirt, ElementMessage::Clicked { x: 10, y: 10 });
    let page = w.screen().appearance_page();
    assert_eq!((page.choice(), page.part()), (Choice::Clothes, Part::Shirt));

    w.click(w.shell(chargen::EXIT_ID));
    w.h.answer(true);
    w.settle();
    assert!(!w.screen().is_open());
    w.open();
    let page = w.screen().appearance_page();
    assert_eq!((page.choice(), page.part()), (Choice::Face, Part::Hair));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_heritage_profession_and_town_pages_drive_the_owner() {
    // OpenAC `CharacterCreationUiControllerTests.
    // HeritageButton_SelectsHeritage_WithNoGenderSideEffect`,
    // `ProfessionTemplateButton_SelectsTemplate`,
    // `ProfessionSlider_ScalarChange_SetsTheAttribute`,
    // `ProfessionValueField_DirectEntry_SetsTheAttribute`,
    // `ProfessionSlider_Refresh_DisplaysScalarAsValueOverOneHundred`,
    // `TownButton_SelectsTheLiteralStartAreaIndex`,
    // `TownButton_Refresh_SetsThePagesOwnRetailStateLiteral`,
    // `HeritageDescription_ComposesGreenHeaderAndWhiteBodySegments`.
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    assert_eq!(w.click(aluvian), [CreationCommand::SelectHeritage(ALUVIAN)]);
    assert!(w.selected(aluvian));
    let description =
        w.h.ui
            .find_descendant(w.screen().page_root(Page::Heritage), 0x1000_03C4)
            .unwrap();
    assert!(!text_of(&w.h.ui, description).is_empty());

    w.click(w.shell(chargen::TAB_IDS[1]));
    let bow = w.screen().profession_page().template_buttons()[1].0;
    assert_eq!(w.click(bow), [CreationCommand::SelectTemplate(1)]);
    assert!(w.selected(bow));
    let (lock, slider, value) = w
        .screen()
        .profession_page()
        .slider(ChargenAttribute::Strength)
        .unwrap();
    let (lock, slider, value) = (lock.unwrap(), slider.unwrap(), value.unwrap());
    assert_eq!(
        w.message(slider, ElementMessage::ScalarChanged(0.5)),
        [CreationCommand::SetAttribute(
            ChargenAttribute::Strength,
            50
        )]
    );
    assert_eq!(
        w.message(value, ElementMessage::Submitted("42".into())),
        [CreationCommand::SetAttribute(
            ChargenAttribute::Strength,
            42
        )]
    );
    let strength = w.owner.snapshot().attributes.strength;
    assert_eq!(
        w.h.ui.widget::<Field>(value).map(Field::text),
        Some(strength.to_string())
    );
    let position =
        w.h.ui
            .widget::<ac_ui::widgets::Scrollbar>(slider)
            .map(|s| s.scalar_position())
            .unwrap();
    assert!((position - strength as f32 / 100.0).abs() < 1e-6);
    assert_eq!(
        w.click(lock),
        [CreationCommand::SetAttributeLock(
            ChargenAttribute::Strength,
            true
        )]
    );
    // OpenAC `ProfessionLock_Click_ShowsLockedArtOnTheLockAndTheSlider_AndClickAgainUnlocks`,
    // `CharacterCreationLiveDatTests.ProfessionAttributeLock_AndItsSlider_ShowTheAuthoredLockedArt`:
    // the lock and its slider show their authored Locked art.
    const UNLOCKED: u32 = 0x1000_0018;
    const LOCKED: u32 = 0x1000_0019;
    let fill = |w: &Wizard| {
        w.h.ui
            .widget::<ac_ui::widgets::Scrollbar>(slider)
            .map(|s| s.thumb_fill_sprite)
    };
    assert_eq!(w.h.ui.retail_state(lock), Some(LOCKED));
    assert_eq!(fill(&w), Some(0x0600_4D3A));
    assert_eq!(
        w.click(lock),
        [CreationCommand::SetAttributeLock(
            ChargenAttribute::Strength,
            false
        )]
    );
    assert_eq!(w.h.ui.retail_state(lock), Some(UNLOCKED));
    assert_eq!(fill(&w), Some(0x0600_4D39));

    w.click(w.shell(chargen::TAB_IDS[4]));
    let yaraq = w
        .screen()
        .town_page()
        .buttons()
        .iter()
        .find(|b| b.1 == 2)
        .map(|b| b.0)
        .unwrap();
    assert_eq!(w.click(yaraq), [CreationCommand::SelectStartArea(2)]);
    let page = w.screen().page_root(Page::Town);
    assert_eq!(w.h.ui.retail_state(page), Some(0x1000_0036));
    assert!(w.selected(yaraq));
}

/// The profession page's help box: a bordered text two lines tall in the
/// authored layout, not one line.
const PROFESSION_HELP_ID: u32 = 0x1000_03EC;

#[test]
#[ignore = "needs the retail dat files"]
fn every_page_lays_its_authored_text_out_inside_its_box() {
    // The authored text of a text element that is not one line wraps to the
    // element's width between its margins. The profession page's help box
    // (754 by 42 pixels, margins 8, 8, 5 and 5, a 14-pixel font) holds its
    // sentence on two lines.
    let mut w = Wizard::new();
    w.open();
    let pages = [
        Page::Heritage,
        Page::Profession,
        Page::Skills,
        Page::Appearance,
        Page::Town,
        Page::Summary,
    ];
    for (i, page) in pages.into_iter().enumerate() {
        w.click(w.shell(chargen::TAB_IDS[i]));
        let mut stack = vec![w.screen().page_root(page)];
        while let Some(e) = stack.pop() {
            stack.extend(w.h.ui.children(e));
            let (width, dat) = {
                let el = w.h.ui.element(e).unwrap();
                (el.width, el.dat_element_id)
            };
            let Some(t) = w.h.ui.widget_mut::<Text>(e) else {
                continue;
            };
            if t.one_line {
                continue;
            }
            let room = width - t.margin_left - t.margin_right;
            let font = t.font.clone().expect("a dat font");
            for line in t.refresh_lines(width) {
                let fits = font.measure_width(&line.text) <= room;
                assert!(
                    fits || !line.text.contains(' '),
                    "{page:?} {dat:#X}: {:?} is wider than {room} px",
                    line.text
                );
            }
        }
    }

    w.click(w.shell(chargen::TAB_IDS[1]));
    let help =
        w.h.ui
            .find_descendant(w.screen().page_root(Page::Profession), PROFESSION_HELP_ID)
            .unwrap();
    let (width, height) = {
        let el = w.h.ui.element(help).unwrap();
        (el.width, el.height)
    };
    let t = w.h.ui.widget_mut::<Text>(help).unwrap();
    let (top, bottom) = (t.margin_top, t.margin_bottom);
    let line_height = t.font.as_ref().unwrap().line_height;
    let lines: Vec<String> = t
        .refresh_lines(width)
        .iter()
        .map(|l| l.text.clone())
        .collect();
    assert_eq!(lines.len(), 2, "{lines:?}");
    assert!(lines[0].starts_with("From this screen you can modify your profession"));
    assert!(lines[1].ends_with("by adjusting the slider bars."));
    assert!(lines.len() as f32 * line_height <= height - top - bottom);
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_skills_page_lists_groups_and_trains_through_the_arrows() {
    // OpenAC `CharacterCreationUiControllerTests.SkillsRow_ArrowClick_TrainsThenSpecializes`,
    // `SkillsPage_BucketHeaders_AlwaysBuildAllFour_InRetailOrder`,
    // `SkillsPage_ArrowClick_AlsoSelectsRow_InfoBoxShowsLevelBonusLine`,
    // `SkillsPage_AdvancingASkill_MovesItsRowIntoTheNewBucket`,
    // `CharacterCreationLiveDatTests.SkillsPage_RealRowTemplate_HasNameLevelCostTextAndArrowButtons`.
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    // The custom template trains nothing: every credit is free.
    w.click(w.shell(chargen::TAB_IDS[1]));
    let custom = w.screen().profession_page().template_buttons()[0].0;
    w.click(custom);
    w.click(w.shell(chargen::TAB_IDS[2]));
    let list =
        w.h.ui
            .find_descendant(w.screen().page_root(Page::Skills), 0x1000_03F7)
            .unwrap();
    let templates = w.h.ui.widget::<ListBox>(list).unwrap().templates.clone();
    assert_eq!(templates.len(), 2);
    assert_eq!(templates[1].element, 0x1000_02FF);
    let rows = w.screen().skills_page().rows();
    assert!(rows.len() > 20, "{}", rows.len());
    let items = w.h.ui.widget::<ListBox>(list).unwrap().item_count(&w.h.ui);
    assert_eq!(items, rows.len() + 4, "four group headers");
    // An untrained skill the credits cover: up trains it, again
    // specializes, down goes back.
    let snapshot = w.owner.snapshot();
    let options = w.h.state.options.clone().unwrap();
    let skill = rows
        .iter()
        .map(|r| r.1)
        .find(|&s| {
            let level = snapshot.skills[s as usize];
            let (t, p) = ac_ui::pregame::chargen::skills::costs(&options, ALUVIAN, s);
            matches!(
                level,
                ac_core::chargen::ChargenSkillAdvancementClass::Untrained
            ) && t > 0
                && p > t
                && p <= snapshot.remaining_skill_credits
        })
        .expect("a trainable skill");
    let (up, down) = w.screen().skills_page().arrows(skill).unwrap();
    assert_eq!(w.click(up.unwrap()), [CreationCommand::TrainSkill(skill)]);
    assert_eq!(w.screen().skills_page().selected(), Some(skill));
    let (title, info) = w.screen().skills_page().info();
    let name = ac_core::chargen::chargen_skill_name(&options, skill);
    assert!(text_of(&w.h.ui, title.unwrap()).starts_with(&format!("{name} (")));
    assert!(text_of(&w.h.ui, info.unwrap()).contains("Training Bonus  +5"));
    let (up, down2) = w.screen().skills_page().arrows(skill).unwrap();
    assert_eq!(
        w.click(up.unwrap()),
        [CreationCommand::SpecializeSkill(skill)]
    );
    let (_, down) = w
        .screen()
        .skills_page()
        .arrows(skill)
        .unwrap_or((None, down));
    let _ = down2;
    assert_eq!(w.click(down.unwrap()), [CreationCommand::TrainSkill(skill)]);
    assert_eq!(
        w.owner.skill_level(skill),
        ac_core::chargen::ChargenSkillAdvancementClass::Trained
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_appearance_page_spins_paints_and_shades() {
    // OpenAC `CharacterCreationUiControllerTests.AppearanceGenderButton_SelectsGender`,
    // `AppearanceSpin_IncrementArrowClick_CyclesStyleForwardFromUnset`,
    // `AppearanceSpin_BodyClick_SelectsPartWithoutChangingIndex`,
    // `AppearanceSkinSpin_Click_NeverCallsSetAppearanceIndex`,
    // `AppearanceSwatch_WithinColorCount_SetsColorForTheCurrentPart`,
    // `AppearanceSwatchOverlays_ExactlyOneVisible_TrackingTheCurrentPartsColorIndex`,
    // `AppearanceShadeScroll_ScalarChanged_SetsShadeForTheCurrentPart`,
    // `AppearanceZoomButtons_ClickPath_TogglesMutualExclusiveHighlightPair`,
    // `CharacterCreationLiveDatTests.AppearancePage_SpinArrowGeometryIsUniformAcrossAllNineSpins`.
    use ac_runtime::session::creation::{AppearanceSlot, ShadeSlot};
    use ac_ui::pregame::chargen::appearance::{self, Part};
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert_eq!(w.page_shown(), [Page::Appearance]);
    let page = w.screen().appearance_page().clone();
    let root = w.screen().page_root(Page::Appearance);
    let female = w.h.ui.find_descendant(root, appearance::FEMALE_ID).unwrap();
    assert_eq!(w.click(female), [CreationCommand::SelectGender(2)]);
    assert!(w.selected(female));
    let hair = page.spin(Part::Hair).unwrap();
    assert_eq!(w.h.ui.element(hair).map(|e| e.width), Some(200.0));
    let current = w.owner.snapshot().appearance.hair_style;
    let up =
        w.h.ui
            .find_descendant(hair, appearance::INCREMENT_ARROW_ID)
            .expect("the hair spin's step-forward arrow");
    let ran = w.click(up);
    assert!(
        matches!(ran.as_slice(), [CreationCommand::SetAppearance(AppearanceSlot::HairStyle, n)] if *n != current),
        "{ran:?}"
    );
    // The middle picks the part without a change; skin never sets a style.
    assert!(
        w.message(hair, ElementMessage::Clicked { x: 10, y: 10 })
            .is_empty()
    );
    let skin = page.spin(Part::Skin).unwrap();
    assert!(
        w.message(skin, ElementMessage::Clicked { x: 130, y: 10 })
            .is_empty()
    );
    assert_eq!(w.screen().appearance_page().part(), Part::Skin);
    w.message(hair, ElementMessage::Clicked { x: 10, y: 10 });
    assert_eq!(w.h.ui.retail_state(hair), Some(ac_ui::states::HIGHLIGHT));
    // A swatch sets the hair colour; its mark follows.
    let (swatches, overlays) = page.swatches();
    assert_eq!(
        w.click(swatches[1].unwrap()),
        [CreationCommand::SetAppearance(AppearanceSlot::HairColor, 1)]
    );
    let marked: Vec<usize> = overlays
        .iter()
        .enumerate()
        .filter(|(_, o)| o.is_some_and(|o| w.h.visible(o)))
        .map(|(i, _)| i)
        .collect();
    assert_eq!(marked, [1]);
    let (_, shade) = page.grad_and_shade();
    assert_eq!(
        w.message(shade.unwrap(), ElementMessage::ScalarChanged(0.25)),
        [CreationCommand::SetShade(ShadeSlot::Hair, 0.25)]
    );
    let zoom_in =
        w.h.ui
            .find_descendant(root, appearance::ZOOM_IN_ID)
            .unwrap();
    let zoom_out =
        w.h.ui
            .find_descendant(root, appearance::ZOOM_OUT_ID)
            .unwrap();
    w.click(zoom_in);
    assert_eq!(
        (w.h.ui.retail_state(zoom_in), w.h.ui.retail_state(zoom_out)),
        (Some(ac_ui::states::HIGHLIGHT), Some(ac_ui::states::NORMAL))
    );
    assert_eq!(
        w.h.screens.creation_mut().unwrap().take_preview_requests(),
        [appearance::PreviewRequest::ZoomIn]
    );
    let preview = w.screen().preview().expect("the preview's look");
    assert_eq!(preview.heritage, ALUVIAN);
    assert_eq!(preview.viewport, page.viewport().unwrap());
    assert!(!preview.summary, "the appearance page's preview");
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_gearknights_trim_and_gear_colours_stay_picked_as_they_change() {
    // Issue #37: a Gearknight's trim colour (the eyes spin) and gear colour
    // (the skin spin) went back to the body style (the hair) on the
    // change's refresh.
    use ac_runtime::session::creation::{AppearanceSlot, ShadeSlot};
    use ac_ui::pregame::chargen::appearance::Part;
    const GEARKNIGHT: u32 = 6;
    let mut w = Wizard::new();
    w.open();
    let gearknight = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == GEARKNIGHT)
        .map(|b| b.0)
        .unwrap();
    w.click(gearknight);
    w.click(w.shell(chargen::TAB_IDS[3]));
    let page = w.screen().appearance_page().clone();
    let trim = page.spin(Part::Eyes).unwrap();
    w.message(trim, ElementMessage::Clicked { x: 130, y: 10 });
    assert_eq!(w.screen().appearance_page().part(), Part::Eyes);
    let (swatches, _) = page.swatches();
    assert_eq!(
        w.click(swatches[1].unwrap()),
        [CreationCommand::SetAppearance(AppearanceSlot::EyeColor, 1)]
    );
    assert_eq!(w.screen().appearance_page().part(), Part::Eyes);
    assert_eq!(w.h.ui.retail_state(trim), Some(ac_ui::states::HIGHLIGHT));

    let gear = page.spin(Part::Skin).unwrap();
    w.message(gear, ElementMessage::Clicked { x: 130, y: 10 });
    let (_, shade) = page.grad_and_shade();
    assert_eq!(
        w.message(shade.unwrap(), ElementMessage::ScalarChanged(0.25)),
        [CreationCommand::SetShade(ShadeSlot::Skin, 0.25)]
    );
    assert_eq!(w.screen().appearance_page().part(), Part::Skin);

    // The gear colour moves up into the hidden nose's place (90 pixels
    // from the top), and back down below the mouth (180 pixels) for a
    // heritage with a nose and mouth.
    let top = |w: &Wizard, e| w.h.ui.element(e).map(|e| (e.left, e.top));
    let nose = page.spin(Part::Nose).unwrap();
    assert_eq!(top(&w, gear), Some((0.0, 90.0)));
    assert_eq!(top(&w, gear), top(&w, nose));
    w.click(w.shell(chargen::TAB_IDS[0]));
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert_eq!(top(&w, gear), Some((0.0, 180.0)));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_spin_arrows_are_buttons_of_their_own() {
    // OpenAC `CharacterCreationUiControllerTests.
    // AppearanceSpinArrows_AreTheirOwnButtons_SkinsAlwaysGhosted`,
    // `AppearanceEyesArrows_GhostedForOlthoi_EnabledAgainForAluvian`,
    // `CharacterCreationLiveDatTests.
    // AppearanceSpins_ArrowsAreTheirOwnButtons_ThatRollOverAndKeepTheirArtWhenTheSpinHighlights`.
    use ac_runtime::session::creation::AppearanceSlot;
    use ac_ui::pregame::chargen::appearance::{self, Part};
    let mut w = Wizard::new();
    w.open();
    let heritage = |w: &Wizard, id: u32| {
        w.screen()
            .heritage_page()
            .buttons()
            .iter()
            .find(|b| b.2 == id)
            .map(|b| b.0)
            .unwrap()
    };
    w.click(heritage(&w, ALUVIAN));
    w.click(w.shell(chargen::TAB_IDS[3]));
    let page = w.screen().appearance_page().clone();
    let arrows = |w: &Wizard, part: Part| {
        let spin = page.spin(part).unwrap();
        (
            w.h.ui
                .find_descendant(spin, appearance::DECREMENT_ARROW_ID)
                .expect("a step-back arrow"),
            w.h.ui
                .find_descendant(spin, appearance::INCREMENT_ARROW_ID)
                .expect("a step-forward arrow"),
        )
    };
    let enabled = |w: &Wizard, e: ElementId| w.h.ui.element(e).unwrap().enabled();
    let hair = page.spin(Part::Hair).unwrap();
    assert_eq!(
        w.h.ui.widget::<Button>(hair).unwrap().face_segment_count(),
        0
    );
    let (down, up) = arrows(&w, Part::Hair);
    assert!(w.h.ui.widget::<Button>(up).is_some());

    // The current part's spin is highlighted; its arrows keep their art.
    assert_eq!(w.h.ui.retail_state(hair), Some(ac_ui::states::HIGHLIGHT));
    for arrow in [down, up] {
        assert_eq!(
            w.h.ui.widget::<Button>(arrow).unwrap().active_state(),
            "Normal"
        );
    }

    // An arrow rolls over on its own and carries no hint; the spin does.
    let hover = ac_ui::event::UiEvent::new(0, up, kind::HOVER_ENTER);
    w.h.ui.send_event(up, &hover);
    assert_eq!(
        w.h.ui.widget::<Button>(up).unwrap().active_state(),
        "Normal_rollover"
    );
    assert!(w.h.ui.element(hair).unwrap().authored.tooltip_enabled);
    assert!(!w.h.ui.element(up).unwrap().authored.tooltip_enabled);

    // The arrows step the style.
    let current = w.owner.snapshot().appearance.hair_style;
    let ran = w.click(down);
    assert!(
        matches!(ran.as_slice(), [CreationCommand::SetAppearance(AppearanceSlot::HairStyle, n)] if *n != current),
        "{ran:?}"
    );

    // Skin has no styles: its arrows stay ghosted.
    let (skin_down, skin_up) = arrows(&w, Part::Skin);
    assert!(!enabled(&w, skin_down) && !enabled(&w, skin_up));
    let (eyes_down, eyes_up) = arrows(&w, Part::Eyes);
    assert!(enabled(&w, eyes_down) && enabled(&w, eyes_up));

    // The Olthoi have no eye styles: those arrows ghost, and come back.
    w.click(w.shell(chargen::TAB_IDS[0]));
    w.click(heritage(&w, OLTHOI));
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert!(!enabled(&w, eyes_down) && !enabled(&w, eyes_up));
    w.click(w.shell(chargen::TAB_IDS[0]));
    w.click(heritage(&w, ALUVIAN));
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert!(enabled(&w, eyes_down) && enabled(&w, eyes_up));
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_appearance_page_opens_zoomed_in_and_the_choices_zoom() {
    // OpenAC `CharacterCreationUiControllerTests.
    // AppearancePage_OpensOnTheFaceChoiceZoomedIn_SoZoomOutWorksAtOnce`,
    // `AppearanceChoice_ClothesZoomsOut_FaceZoomsBackIn`.
    use ac_ui::pregame::chargen::appearance::{self, PreviewRequest};
    use ac_ui::states::{HIGHLIGHT, NORMAL};
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    let root = w.screen().page_root(Page::Appearance);
    let find = |w: &Wizard, id| w.h.ui.find_descendant(root, id).unwrap();
    let (zoom_in, zoom_out) = (
        find(&w, appearance::ZOOM_IN_ID),
        find(&w, appearance::ZOOM_OUT_ID),
    );
    let lit = |w: &Wizard| (w.h.ui.retail_state(zoom_in), w.h.ui.retail_state(zoom_out));
    let requests = |w: &mut Wizard| w.h.screens.creation_mut().unwrap().take_preview_requests();

    assert_eq!(lit(&w), (Some(HIGHLIGHT), Some(NORMAL)));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomIn]);

    w.click(zoom_out);
    assert_eq!(lit(&w), (Some(NORMAL), Some(HIGHLIGHT)));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomOut]);
    // The button already lit only lights again.
    w.click(zoom_out);
    assert!(requests(&mut w).is_empty());

    w.click(find(&w, appearance::FACE_ID));
    assert_eq!(lit(&w), (Some(HIGHLIGHT), Some(NORMAL)));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomIn]);
    w.click(find(&w, appearance::CLOTHES_ID));
    assert_eq!(lit(&w), (Some(NORMAL), Some(HIGHLIGHT)));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomOut]);
}

#[test]
#[ignore = "needs the retail dat files"]
fn an_eyes_arrow_keeps_its_rollover_when_its_click_refreshes_the_page() {
    // OpenAC `CharacterCreationUiControllerTests.
    // AppearanceEyesArrow_KeepsItsRollover_WhenItsClickRefreshesThePage`.
    use ac_runtime::session::creation::AppearanceSlot;
    use ac_ui::pregame::chargen::appearance::{self, Part};
    let mut w = Wizard::new();
    w.open();
    let aluvian = w
        .screen()
        .heritage_page()
        .buttons()
        .iter()
        .find(|b| b.2 == ALUVIAN)
        .map(|b| b.0)
        .unwrap();
    w.click(aluvian);
    w.click(w.shell(chargen::TAB_IDS[3]));
    let eyes = w.screen().appearance_page().spin(Part::Eyes).unwrap();
    let up =
        w.h.ui
            .find_descendant(eyes, appearance::INCREMENT_ARROW_ID)
            .unwrap();
    let hover = ac_ui::event::UiEvent::new(0, up, kind::HOVER_ENTER);
    w.h.ui.send_event(up, &hover);
    let state = |w: &Wizard| {
        w.h.ui
            .widget::<Button>(up)
            .unwrap()
            .active_state()
            .to_owned()
    };
    assert_eq!(state(&w), "Normal_rollover");
    let ran = w.click(up);
    assert!(
        matches!(
            ran.as_slice(),
            [CreationCommand::SetAppearance(AppearanceSlot::EyesStrip, _)]
        ),
        "{ran:?}"
    );
    assert_eq!(state(&w), "Normal_rollover");
}

#[test]
#[ignore = "needs the retail dat files"]
fn heritages_without_clothes_zoom_back_in_where_the_page_updates() {
    // OpenAC `CharacterCreationUiControllerTests.
    // AppearancePage_OlthoiReZoomsIn_WhereThePageUpdates_GenderRandomAndShown`,
    // `AppearancePage_AluvianGenderPick_LeavesTheZoomAlone`.
    use ac_ui::pregame::chargen::appearance::{self, Part, PreviewRequest};
    use ac_ui::states::{HIGHLIGHT, NORMAL};
    let mut w = Wizard::new();
    w.open();
    let heritage = |w: &Wizard, id: u32| {
        w.screen()
            .heritage_page()
            .buttons()
            .iter()
            .find(|b| b.2 == id)
            .map(|b| b.0)
            .unwrap()
    };
    let root = w.screen().page_root(Page::Appearance);
    let find = |w: &Wizard, id| w.h.ui.find_descendant(root, id).unwrap();
    let (zoom_in, zoom_out, female) = (
        find(&w, appearance::ZOOM_IN_ID),
        find(&w, appearance::ZOOM_OUT_ID),
        find(&w, appearance::FEMALE_ID),
    );
    let lit = |w: &Wizard| (w.h.ui.retail_state(zoom_in), w.h.ui.retail_state(zoom_out));
    let requests = |w: &mut Wizard| w.h.screens.creation_mut().unwrap().take_preview_requests();

    // An Aluvian gender pick leaves the zoom alone.
    w.click(heritage(&w, ALUVIAN));
    w.click(w.shell(chargen::TAB_IDS[3]));
    w.click(zoom_out);
    requests(&mut w);
    w.click(female);
    assert!(requests(&mut w).is_empty());
    assert_eq!(lit(&w), (Some(NORMAL), Some(HIGHLIGHT)));

    // The page shown again for an Olthoi: back on the face, zoomed in.
    w.click(w.shell(chargen::TAB_IDS[0]));
    w.click(heritage(&w, OLTHOI));
    w.click(w.shell(chargen::TAB_IDS[3]));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomIn]);
    assert_eq!(lit(&w), (Some(HIGHLIGHT), Some(NORMAL)));

    // A gender pick.
    w.click(zoom_out);
    requests(&mut w);
    w.click(female);
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomIn]);
    assert_eq!(lit(&w), (Some(HIGHLIGHT), Some(NORMAL)));

    // Random on this page.
    w.click(zoom_out);
    requests(&mut w);
    w.click(w.shell(chargen::RANDOM_ID));
    assert_eq!(requests(&mut w), [PreviewRequest::ZoomIn]);

    // A plain change of the character is not one of those.
    w.click(zoom_out);
    requests(&mut w);
    let hair = w.screen().appearance_page().spin(Part::Hair).unwrap();
    let up =
        w.h.ui
            .find_descendant(hair, appearance::INCREMENT_ARROW_ID)
            .unwrap();
    w.click(up);
    assert!(requests(&mut w).is_empty());
    assert_eq!(lit(&w), (Some(NORMAL), Some(HIGHLIGHT)));
}

#[test]
#[ignore = "needs the retail dat files"]
fn finish_asks_for_a_name_then_for_the_points_left() {
    // OpenAC `CharacterCreationUiControllerTests.
    // Finish_EmptyName_ShowsNoNameWarningDialog_AndDoesNotSend`,
    // `Finish_UnspentCredits_ShowsCreditWarning_ConfirmResendsWithConfirmedFlag`,
    // `SummaryNameField_Submit_CommitsTheName`,
    // `SummaryNameField_TooLong_ShowsDialogAndRevertsToLastCommitted`,
    // `Summary_RebuildListbox_SkillRows_UseKeyValueTemplate_WithUnconditionalHeaders`,
    // `CreationFailed_NameInUse_ShowsTheRetailErrorDialog_AndAcknowledgesOnClose`.
    let mut w = Wizard::new();
    w.open();
    w.click(w.shell(chargen::TAB_IDS[5]));
    let finish = w.shell(chargen::FINISH_ID);
    let ran = w.click(finish);
    assert!(matches!(
        ran.as_slice(),
        [CreationCommand::Finish {
            confirm_unspent_credits: false
        }]
    ));
    assert!(w.owner.snapshot().last_local_refusal.no_name);
    let (kind, _) = w.h.dialog().expect("the no-name warning");
    assert_eq!(kind, DialogKind::Message);
    w.h.answer(true);
    assert_eq!(w.h.dialog(), None);

    let name = w.screen().summary_page().name_field().unwrap();
    let long = "A".repeat(33);
    assert!(w.message(name, ElementMessage::Submitted(long)).is_empty());
    assert_eq!(w.h.dialog().map(|d| d.0), Some(DialogKind::Message));
    w.h.answer(true);
    assert_eq!(
        w.message(name, ElementMessage::Submitted("Testing Horan".into())),
        [CreationCommand::SetName("Testing Horan".into())]
    );
    // Points left (lower an attribute) ask before sending.
    w.owner
        .apply_command(&CreationCommand::SetAttribute(ChargenAttribute::Focus, 10));
    w.h.state.creation = Some(w.owner.snapshot());
    w.settle();
    assert!(w.owner.snapshot().remaining_attribute_credits > 0);
    w.click(finish);
    assert!(
        w.owner
            .snapshot()
            .last_local_refusal
            .attribute_credits_unspent
    );
    assert_eq!(w.h.dialog().map(|d| d.0), Some(DialogKind::Confirmation));
    let out = w.h.answer(true);
    assert!(matches!(
        w.run(&out).as_slice(),
        [CreationCommand::Finish {
            confirm_unspent_credits: true
        }]
    ));
    assert!(w.owner.snapshot().verification_pending, "sent");
    w.settle();
    assert_eq!(w.h.dialog(), None);

    // The overview lists the choices.
    let list = w.screen().summary_page().list().unwrap();
    let rows = w.h.ui.widget::<ListBox>(list).unwrap().item_count(&w.h.ui);
    assert!(rows >= 4 + 1 + 10 + 2, "{rows}");

    // The server refused the name: the game's words, acknowledged on OK.
    let mut refused = w.owner.snapshot();
    refused.revision += 1;
    refused.last_rejection = Some(ac_runtime::session::creation::CreationRejection {
        raw_code: ac_proto::character::chargen_code::NAME_IN_USE,
        reason: "NameInUse".into(),
        attempted_name: "Testing Horan".into(),
    });
    w.h.state.creation = Some(refused);
    w.h.update();
    assert_eq!(w.h.dialog().map(|d| d.0), Some(DialogKind::Message));
    let out = w.h.answer(true);
    assert_eq!(creation(&out), [CreationCommand::AcknowledgeRejection]);
}

#[test]
fn the_window_keeps_the_latest_of_each_pre_game_message() {
    let mut state = PreGameState::default();
    let t = Instant::now();
    state.apply(PreGameEvent::Connection(view(
        ConnectionStatus::CheckingData,
        t,
        false,
    )));
    state.apply(PreGameEvent::Connection(view(
        ConnectionStatus::Ready,
        t,
        false,
    )));
    state.apply(PreGameEvent::Selection(Box::new(selection(&[], 0, 5, 7))));
    state.apply(PreGameEvent::Refused("no".into()));
    state.apply(PreGameEvent::Refused("again".into()));
    assert_eq!(
        state.connection.map(|c| c.status),
        Some(ConnectionStatus::Ready)
    );
    assert_eq!(state.selection.map(|s| s.revision), Some(7));
    assert_eq!(state.refusals, ["no", "again"]);
}
