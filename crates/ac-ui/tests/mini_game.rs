//! The Game Center window. Ported from OpenAC
//! `tests/AcDream.App.Tests/UI/Layout/MiniGameUiControllerTests.cs` and
//! `MiniGameLayoutInstalledDatTests.cs` (the latter reads the retail dats
//! here, `real_dats`). OpenAC's fixture layout is built by hand: the board
//! list with the layout's piece images, the three buttons, and squares
//! shaped like the game's square template. The game is the session's
//! chess owner, driven here directly; the window's moves, offers and
//! resignations are the session commands it emits.

mod common;

use std::sync::Arc;

use ac_core::chess::{ChessPiece, ChessPieceType, ChessSquare, ChessTeam};
use ac_dat::objects::{DIRECT_STATE_ID, LayoutDesc, Property, PropertyValue};
use ac_proto::chess::{
    ChessMove, ChessMoveType, GameOver, JoinGameResponse, OpponentTurn, StartGame,
};
use ac_runtime::gameplay::{ChessCommand, ChessStage, ChessState, GameplayCommand};
use ac_runtime::session::SessionCommand;
use ac_ui::draw::TextureRef;
use ac_ui::element_info::{ElementInfo, StateInfo};
use ac_ui::host::{PanelController, UiHost};
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build, class};
use ac_ui::panels::mini_game::{
    self as mg, MiniGameController, MiniGameUpdate, read_piece_images, square_at,
};
use ac_ui::panels::{PanelOutput, panel_id};
use ac_ui::root::{ElementMessage, UiRoot};
use ac_ui::tree::ElementId;
use ac_ui::widgets::{DatElement, ListBox};
use common::items::panel_outputs;

const BOARD: u32 = 0x7A9B_4001;

// The first white and first black piece image the window's layout lists.
const WHITE_PIECE_SPRITES: u32 = 0x0600_4CFD;
const BLACK_PIECE_SPRITES: u32 = 0x0600_4D03;

#[derive(Debug, Default)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<LayoutDesc>> {
        None
    }
}

fn sq(name: &str) -> ChessSquare {
    let b = name.as_bytes();
    ChessSquare::new(
        i32::from(b[0]) - i32::from(b'a'),
        i32::from(b[1]) - i32::from(b'1'),
    )
}

fn element(id: u32, kind: u32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

/// The window's layout: the close button, the three buttons, and the board
/// list with the twelve piece images the game's layout gives it.
fn window_info(with_images: bool) -> ElementInfo {
    let mut board = element(mg::BOARD_ID, class::LIST_BOX, 256.0, 256.0);
    if with_images {
        let images = (0..12)
            .map(|i| Property {
                master_property_id: 0,
                value: PropertyValue::DataId(WHITE_PIECE_SPRITES + i),
            })
            .collect();
        let mut state = StateInfo::named(DIRECT_STATE_ID, "");
        state
            .properties
            .set_value(mg::PIECE_IMAGES_PROPERTY_ID, PropertyValue::Array(images));
        board.states.push((DIRECT_STATE_ID, state));
    }
    let mut root = element(mg::MINI_GAME_ROOT_ID, class::CONTAINER, 300.0, 362.0);
    for id in [
        mg::MINI_GAME_CLOSE_ID,
        mg::RESIGN_ID,
        mg::PASS_ID,
        mg::STALEMATE_ID,
    ] {
        root.children.push(element(id, class::BUTTON, 60.0, 20.0));
    }
    root.children.push(board);
    root
}

fn build_info(ui: &mut UiRoot, info: &ElementInfo) -> ImportedLayout {
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: mg::MINI_GAME_LAYOUT_ID,
        media_seconds: 0.0,
    };
    build(ui, info, &ctx)
}

/// A square shaped like the game's square template: a 32 by 32 image
/// holding a hidden selection frame.
fn create_square(ui: &mut UiRoot) -> Option<ImportedLayout> {
    let mut info = element(mg::SQUARE_TEMPLATE_ID, class::CONTAINER, 32.0, 32.0);
    let mut selection = element(mg::SELECTION_ID, class::CONTAINER, 32.0, 32.0);
    selection.invisible = true;
    info.children.push(selection);
    Some(build_info(ui, &info))
}

struct Harness {
    host: UiHost,
    c: MiniGameController,
    layout: ImportedLayout,
    chess: ChessState,
}

impl Harness {
    fn new() -> Self {
        Self::with_images(true)
    }

    fn with_images(with_images: bool) -> Self {
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let info = window_info(with_images);
        let layout = build_info(&mut host.root, &info);
        let r = host.root.root();
        host.root.add_child(r, layout.root);
        let c = MiniGameController::bind(
            &mut host.root,
            &layout,
            read_piece_images(&info),
            &mut create_square,
        );
        Self {
            host,
            c,
            layout,
            chess: ChessState::default(),
        }
    }

    fn update(&mut self) -> MiniGameUpdate {
        let view = self.chess.view();
        self.c.update(&mut self.host.root, Some(&view))
    }

    fn seat(&mut self, team: ChessTeam) {
        self.chess.begin(BOARD);
        self.chess.apply_join_response(&JoinGameResponse {
            board_guid: BOARD,
            team: team.wire(),
        });
        self.chess.apply_start_game(&StartGame {
            board_guid: BOARD,
            first_team: ChessTeam::White.wire(),
        });
        self.update();
    }

    fn click(&mut self, element: ElementId) -> Vec<PanelOutput> {
        assert!(self.c.on_element_message(
            &mut self.host.root,
            element,
            &ElementMessage::Clicked { x: 1, y: 1 }
        ));
        self.host.pump();
        panel_outputs(&mut self.host)
    }

    fn click_square(&mut self, index: usize) -> Vec<PanelOutput> {
        let square = self.c.squares()[index];
        self.click(square)
    }

    fn button(&self, id: u32) -> ElementId {
        self.layout.find(id).unwrap()
    }

    fn image(&self, index: usize) -> Option<Option<TextureRef>> {
        self.host
            .root
            .widget::<DatElement>(self.c.squares()[index])
            .unwrap()
            .runtime_image
    }

    fn highlighted(&self, index: usize) -> bool {
        let square = self.c.squares()[index];
        let selection = self.host.root.children(square)[0];
        self.host.root.element(selection).unwrap().visible
    }
}

fn piece(sprite: u32) -> Option<Option<TextureRef>> {
    Some(Some(TextureRef::Surface(sprite)))
}

fn chess_commands(outputs: &[PanelOutput]) -> Vec<ChessCommand> {
    outputs
        .iter()
        .filter_map(|o| match o {
            PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Chess(c))) => {
                Some(c.clone())
            }
            _ => None,
        })
        .collect()
}

#[test]
fn bind_lays_out_sixty_four_squares_and_hides_the_pass_button() {
    let h = Harness::new();
    let board = h.layout.find(mg::BOARD_ID).unwrap();
    assert_eq!(
        h.host
            .root
            .widget::<ListBox>(board)
            .unwrap()
            .item_count(&h.host.root),
        1
    );
    let squares = h.c.squares();
    assert_eq!(squares.len(), 64);
    let at = |i: usize| {
        let e = h.host.root.element(squares[i]).unwrap();
        (e.left, e.top)
    };
    assert_eq!(at(1), (32.0, 0.0));
    assert_eq!(at(8), (0.0, 32.0));
    assert!(
        squares
            .iter()
            .all(|&s| !h.host.root.element(s).unwrap().click_through)
    );
    assert!(!h.host.root.element(h.button(mg::PASS_ID)).unwrap().visible);
    assert!(
        h.host
            .root
            .element(h.button(mg::RESIGN_ID))
            .unwrap()
            .visible
    );
}

#[test]
fn white_sees_its_back_rank_along_the_bottom() {
    let mut h = Harness::new();
    h.seat(ChessTeam::White);
    assert_eq!(h.image(60), piece(WHITE_PIECE_SPRITES + 5)); // e1 king
    assert_eq!(h.image(0), piece(BLACK_PIECE_SPRITES + 3)); // a8 rook
    assert_eq!(h.image(36), Some(None)); // e4 empty
}

#[test]
fn black_sees_the_board_turned_round() {
    let mut h = Harness::new();
    h.seat(ChessTeam::Black);
    assert_eq!(h.image(3), piece(WHITE_PIECE_SPRITES + 5)); // e1 king
    assert_eq!(h.image(59), piece(BLACK_PIECE_SPRITES + 5)); // e8 king
}

#[test]
fn not_seated_shows_blacks_view() {
    assert_eq!(square_at(0, None), ChessSquare::new(7, 0));
    assert_eq!(square_at(0, Some(ChessTeam::White)), sq("a8"));
}

#[test]
fn piece_sprites_are_read_from_the_layout_in_the_games_order() {
    let h = Harness::new();
    for (piece_type, offset) in [
        (ChessPieceType::Pawn, 0),
        (ChessPieceType::Bishop, 1),
        (ChessPieceType::Knight, 2),
        (ChessPieceType::Rook, 3),
        (ChessPieceType::Queen, 4),
        (ChessPieceType::King, 5),
    ] {
        assert_eq!(
            h.c.piece_sprite(ChessPiece::new(piece_type, ChessTeam::White)),
            WHITE_PIECE_SPRITES + offset
        );
        assert_eq!(
            h.c.piece_sprite(ChessPiece::new(piece_type, ChessTeam::Black)),
            BLACK_PIECE_SPRITES + offset
        );
    }
}

#[test]
fn a_layout_without_piece_images_draws_no_pieces() {
    assert!(
        read_piece_images(&element(mg::MINI_GAME_ROOT_ID, 0, 0.0, 0.0))
            .iter()
            .all(|&i| i == 0)
    );
    let mut h = Harness::with_images(false);
    assert_eq!(
        h.c.piece_sprite(ChessPiece::new(ChessPieceType::King, ChessTeam::White)),
        0
    );
    h.seat(ChessTeam::White);
    assert_eq!(h.image(60), Some(None));
}

#[test]
fn clicking_a_piece_then_a_square_moves_it_and_sends_the_move() {
    let mut h = Harness::new();
    h.seat(ChessTeam::White);

    assert!(chess_commands(&h.click_square(52)).is_empty()); // e2
    assert_eq!(h.c.selected(), Some(sq("e2")));
    assert!(h.highlighted(52));

    let sent = chess_commands(&h.click_square(36)); // e4
    assert_eq!(
        sent,
        [ChessCommand::Move {
            from: sq("e2"),
            to: sq("e4")
        }]
    );
    assert_eq!(h.c.selected(), None);
    assert!(!h.highlighted(52));

    // The session plays it and sends the board back.
    assert!(h.chess.try_move(sq("e2"), sq("e4")).is_accepted());
    h.update();
    assert_eq!(h.image(36), piece(WHITE_PIECE_SPRITES));
    assert_eq!(h.image(52), Some(None));
}

#[test]
fn clicking_the_selected_square_again_puts_the_piece_down() {
    let mut h = Harness::new();
    h.seat(ChessTeam::White);
    h.click_square(52);
    let sent = chess_commands(&h.click_square(52));
    assert_eq!(h.c.selected(), None);
    assert!(sent.is_empty());
}

#[test]
fn a_move_the_rules_refuse_drops_the_selection_and_the_session_sends_nothing() {
    // The window asks; the session's board refuses e2-e5 and nothing goes
    // to the server (the session's own tests pin the sending).
    let mut h = Harness::new();
    h.seat(ChessTeam::White);
    h.click_square(52); // e2
    h.click_square(28); // e5
    assert_eq!(h.c.selected(), None);
    assert_eq!(
        h.chess.try_move(sq("e2"), sq("e5")),
        ac_core::chess::ChessMoveResult::BAD_DIRECTION
    );
}

#[test]
fn opponents_pieces_and_off_turn_clicks_do_nothing() {
    let mut h = Harness::new();
    h.seat(ChessTeam::Black);

    h.click_square(51); // e7 from Black's side, but it is White's turn
    assert_eq!(h.c.selected(), None);

    h.chess.apply_opponent_turn(&OpponentTurn {
        board_guid: BOARD,
        team: 0,
        chess_move: ChessMove {
            move_type: ChessMoveType::FROM_TO,
            player_guid: 0x5000_0002,
            from_x: 4,
            from_y: 1,
            to_x: 4,
            to_y: 3,
            piece_guid: 0,
        },
    });
    h.update();
    h.click_square(11); // e2, a White pawn
    assert_eq!(h.c.selected(), None);
    h.click_square(51); // e7
    assert_eq!(h.c.selected(), Some(sq("e7")));
}

#[test]
fn resign_asks_first_then_resigns_and_closes_the_window() {
    let mut h = Harness::new();
    h.seat(ChessTeam::White);
    let resign = h.button(mg::RESIGN_ID);

    let asked = h.click(resign);
    let [
        PanelOutput::ConfirmEither {
            message,
            on_yes,
            on_no,
        },
    ] = &asked[..]
    else {
        panic!("{asked:?}");
    };
    assert_eq!(message, mg::RESIGN_CONFIRMATION);
    assert!(matches!(**on_yes, PanelOutput::ChessResignAnswered(true)));
    assert!(matches!(**on_no, PanelOutput::ChessResignAnswered(false)));
    // A second click while the question is open asks nothing more.
    assert!(h.click(resign).is_empty());
    assert!(!h.c.resign_answered(false));

    h.click(resign);
    assert!(h.c.resign_answered(true));
    // The window sends the resignation; the session ends the game.
    h.chess.resign();
    assert_eq!(h.chess.stage(), ChessStage::NotPlaying);
    assert!(h.update().hide);
}

#[test]
fn stalemate_sends_the_offer_toggle() {
    let mut h = Harness::new();
    h.seat(ChessTeam::White);
    let stalemate = h.button(mg::STALEMATE_ID);
    let first = chess_commands(&h.click(stalemate));
    let second = chess_commands(&h.click(stalemate));
    assert_eq!(first, [ChessCommand::ToggleStalemate]);
    assert_eq!(second, [ChessCommand::ToggleStalemate]);
    // The session turns the toggles into the offer, then the retraction.
    assert_eq!(h.chess.try_toggle_stalemate(), Some(true));
    assert_eq!(h.chess.try_toggle_stalemate(), Some(false));
}

#[test]
fn indicator_lights_while_a_game_is_on() {
    let mut h = Harness::new();
    let lit = {
        h.chess.begin(BOARD);
        h.chess.apply_join_response(&JoinGameResponse {
            board_guid: BOARD,
            team: 0,
        });
        h.update()
    };
    h.chess.apply_game_over(&GameOver {
        board_guid: BOARD,
        winner: 0,
    });
    let out = h.update();
    assert_eq!(lit.indicator, Some(true));
    assert_eq!(out.indicator, Some(false));
    assert!(!out.hide);
}

#[test]
fn close_closes_the_panel() {
    let mut h = Harness::new();
    let close = h.button(mg::MINI_GAME_CLOSE_ID);
    assert!(matches!(
        &h.click(close)[..],
        [PanelOutput::ClosePanel(panel_id::MINI_GAME)]
    ));
}

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{import_infos_rooted, import_rooted};
    use ac_ui::widgets::Button;
    use std::path::PathBuf;

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

    /// OpenAC `MiniGameLayoutInstalledDatTests.Layout2100001E_BuildsTheBoardFromItsSquareTemplate`.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn layout_2100001e_builds_the_board_from_its_square_template() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let font = c.font(DEFAULT_FONT_ID);
        let info = import_infos_rooted(&c, mg::MINI_GAME_LAYOUT_ID, mg::MINI_GAME_ROOT_ID).unwrap();
        let layout = import_rooted(
            &mut host.root,
            &c,
            mg::MINI_GAME_LAYOUT_ID,
            mg::MINI_GAME_ROOT_ID,
            font.clone(),
        )
        .unwrap();
        let label = |id| {
            host.root
                .widget::<Button>(layout.find(id).unwrap())
                .and_then(|b| b.label.clone())
        };
        assert_eq!(label(mg::RESIGN_ID).as_deref(), Some("Resign"));
        assert_eq!(label(mg::PASS_ID).as_deref(), Some("Pass"));
        assert_eq!(label(mg::STALEMATE_ID).as_deref(), Some("Stalemate"));
        let board = layout.find(mg::BOARD_ID).unwrap();
        let templates = host
            .root
            .widget::<ListBox>(board)
            .unwrap()
            .templates
            .clone();
        assert_eq!(
            (templates[0].layout, templates[0].element),
            (mg::MINI_GAME_LAYOUT_ID, mg::SQUARE_TEMPLATE_ID)
        );

        let square = import_rooted(
            &mut host.root,
            &c,
            mg::MINI_GAME_LAYOUT_ID,
            mg::SQUARE_TEMPLATE_ID,
            font.clone(),
        )
        .unwrap();
        assert!(host.root.widget::<DatElement>(square.root).is_some());
        let root = host.root.element(square.root).unwrap();
        assert_eq!((root.width, root.height), (32.0, 32.0));
        let selection = square.find(mg::SELECTION_ID).unwrap();
        assert!(!host.root.element(selection).unwrap().visible);

        let mut create = |ui: &mut UiRoot| {
            import_rooted(
                ui,
                &c,
                mg::MINI_GAME_LAYOUT_ID,
                mg::SQUARE_TEMPLATE_ID,
                font.clone(),
            )
        };
        let controller = MiniGameController::bind(
            &mut host.root,
            &layout,
            read_piece_images(&info),
            &mut create,
        );
        assert_eq!(controller.squares().len(), 64);
        assert_eq!(
            host.root
                .widget::<ListBox>(board)
                .unwrap()
                .item_count(&host.root),
            1
        );

        // White's pawn, bishop, knight, rook, queen and king, then Black's.
        let expected: Vec<u32> = (0..12).map(|i| 0x0600_4CFD + i).collect();
        assert_eq!(read_piece_images(&info).to_vec(), expected);
        assert_eq!(
            controller.piece_sprite(ChessPiece::new(ChessPieceType::King, ChessTeam::Black)),
            0x0600_4D08
        );
    }
}

/// OpenAC `APieceImageTooWideToBeAnId_DrawsNoPieceInsteadOfFailing`. Here
/// an image id is a 32-bit data id by type, so a value too wide to be one
/// can only arrive as another kind of entry (a 64-bit one): it counts as
/// missing and draws no piece, and the entries after it are still read.
#[test]
fn a_piece_image_too_wide_to_be_an_id_draws_no_piece_instead_of_failing() {
    let mut info = window_info(true);
    let board = info
        .children
        .iter_mut()
        .find(|c| c.id == mg::BOARD_ID)
        .unwrap();
    let state = &mut board.states[0].1;
    let Some(PropertyValue::Array(list)) = state
        .properties
        .get(mg::PIECE_IMAGES_PROPERTY_ID)
        .map(|p| p.value.clone())
    else {
        panic!("the fixture lists the piece images");
    };
    let mut list = list;
    list[0].value = PropertyValue::Bitfield64(1 << 40);
    state
        .properties
        .set_value(mg::PIECE_IMAGES_PROPERTY_ID, PropertyValue::Array(list));

    let images = read_piece_images(&info);

    assert_eq!(images[0], 0);
    assert_eq!(images[1], WHITE_PIECE_SPRITES + 1);
}

/// OpenAC `MiniGameUiController.BuildSquares`: a square that fails to
/// build stops the board, and no grid is added at all.
#[test]
fn a_square_that_fails_to_build_leaves_the_board_empty() {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let info = window_info(true);
    let layout = build_info(&mut host.root, &info);
    let mut built = 0;
    let mut create = |ui: &mut UiRoot| {
        built += 1;
        if built == 10 { None } else { create_square(ui) }
    };
    let c = MiniGameController::bind(
        &mut host.root,
        &layout,
        read_piece_images(&info),
        &mut create,
    );
    assert!(c.squares().is_empty());
    let board = layout.find(mg::BOARD_ID).unwrap();
    assert_eq!(
        host.root
            .widget::<ListBox>(board)
            .unwrap()
            .item_count(&host.root),
        0
    );
}
