//! The Game Center window (layout 0x2100001E): the chess board, plus the
//! Resign, Pass and Stalemate buttons.
//!
//! Using a board in the world joins its game (the session's part) and
//! shows the window. The board is drawn from the side the local player
//! sits on (Black's view when not seated); clicking one of their pieces on
//! their turn selects it and clicking another square asks the session to
//! move it there. The game itself is the session's: the window draws the
//! copy it is fed and sends commands.

use ac_core::chess::{ChessPiece, ChessPieceType, ChessSquare, ChessTeam, SIZE};
use ac_dat::objects::PropertyValue;
use ac_runtime::gameplay::{ChessCommand, ChessStage, ChessView, GameplayCommand};
use ac_runtime::session::SessionCommand;

use crate::draw::TextureRef;
use crate::element_info::ElementInfo;
use crate::host::PanelController;
use crate::import::ImportedLayout;
use crate::panels::items::{bind_click, emit};
use crate::panels::{PanelOutput, panel_id};
use crate::root::{ElementMessage, UiRoot};
use crate::tree::{Container, Element, ElementId};
use crate::widgets::{DatElement, ListBox};

/// The mini-game panel's layout.
pub const MINI_GAME_LAYOUT_ID: u32 = 0x2100_001E;
/// Its root.
pub const MINI_GAME_ROOT_ID: u32 = 0x1000_016A;
/// Its close button.
pub const MINI_GAME_CLOSE_ID: u32 = 0x1000_016B;
/// The board (a list whose one row holds the squares).
pub const BOARD_ID: u32 = 0x1000_0174;
/// The Resign button.
pub const RESIGN_ID: u32 = 0x1000_0175;
/// The Pass button.
pub const PASS_ID: u32 = 0x1000_0176;
/// The Stalemate button.
pub const STALEMATE_ID: u32 = 0x1000_0177;
/// The square template every board square is built from.
pub const SQUARE_TEMPLATE_ID: u32 = 0x1000_0178;
/// The selection frame inside a square.
pub const SELECTION_ID: u32 = 0x1000_0179;

/// The board element's list of piece images: White's six, then Black's,
/// each running pawn, bishop, knight, rook, queen, king.
pub const PIECE_IMAGES_PROPERTY_ID: u32 = 0x1000_0021;
const PIECES_PER_SIDE: usize = 6;

/// What Resign asks first.
pub const RESIGN_CONFIRMATION: &str = "If a game is in progress, resigning will be recorded as your loss. Are you sure you want to resign?";

const SQUARE_SIZE: f32 = 32.0;
const SQUARE_COUNT: usize = 64;

/// What a frame's update asks of the window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MiniGameUpdate {
    /// A game ended by resigning or with no result: hide the window.
    pub hide: bool,
    /// Light (true) or ghost the indicator-bar button.
    pub indicator: Option<bool>,
}

/// The board square under window square `index` (left to right, top to
/// bottom as the window shows them). White sees its own back rank at the
/// bottom; anyone else sees the board turned round.
#[must_use]
pub fn square_at(index: usize, team: Option<ChessTeam>) -> ChessSquare {
    let index = i32::try_from(index).unwrap_or(0);
    let column = index % SIZE;
    let row = index / SIZE;
    if team == Some(ChessTeam::White) {
        ChessSquare::new(column, SIZE - 1 - row)
    } else {
        ChessSquare::new(SIZE - 1 - column, row)
    }
}

fn find_info(info: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    if info.id == id {
        return Some(info);
    }
    info.children.iter().find_map(|c| find_info(c, id))
}

/// The twelve piece images listed on the board element of the window's
/// layout, in the order [`MiniGameController::piece_sprite`] reads them.
/// An entry the layout does not give as an image is zero.
#[must_use]
pub fn read_piece_images(root_info: &ElementInfo) -> [u32; 2 * PIECES_PER_SIDE] {
    let mut images = [0; 2 * PIECES_PER_SIDE];
    let Some(PropertyValue::Array(list)) = find_info(root_info, BOARD_ID)
        .and_then(|board| board.effective_property(PIECE_IMAGES_PROPERTY_ID, None))
    else {
        return images;
    };
    for (image, entry) in images.iter_mut().zip(list) {
        if let PropertyValue::DataId(id) = entry.value {
            *image = id;
        }
    }
    images
}

/// See the module documentation.
#[derive(Debug)]
pub struct MiniGameController {
    close: Option<ElementId>,
    resign: Option<ElementId>,
    pass: Option<ElementId>,
    stalemate: Option<ElementId>,
    squares: Vec<ElementId>,
    selections: Vec<Option<ElementId>>,
    piece_images: [u32; 2 * PIECES_PER_SIDE],
    view: Option<ChessView>,
    selected: Option<ChessSquare>,
    drawn_revision: Option<u64>,
    games_ended: Option<u64>,
    indicator_active: bool,
    confirming_resign: bool,
}

impl MiniGameController {
    /// Bind to the imported panel: the buttons, the 64 squares built by
    /// `create_square` (the layout's square template; building stops at the
    /// first that is not an image element) into the board list, and the
    /// piece images ([`read_piece_images`]). The Pass button is hidden: the
    /// game has one but keeps it out of sight.
    pub fn bind(
        ui: &mut UiRoot,
        layout: &ImportedLayout,
        piece_images: [u32; 2 * PIECES_PER_SIDE],
        create_square: &mut dyn FnMut(&mut UiRoot) -> Option<ImportedLayout>,
    ) -> Self {
        let button = |ui: &mut UiRoot, id| layout.find(id).filter(|b| bind_click(ui, *b));
        let mut c = Self {
            close: button(ui, MINI_GAME_CLOSE_ID),
            resign: button(ui, RESIGN_ID),
            pass: button(ui, PASS_ID),
            stalemate: button(ui, STALEMATE_ID),
            squares: Vec::with_capacity(SQUARE_COUNT),
            selections: Vec::with_capacity(SQUARE_COUNT),
            piece_images,
            view: None,
            selected: None,
            drawn_revision: None,
            games_ended: None,
            indicator_active: false,
            confirming_resign: false,
        };
        if let Some(pass) = c.pass {
            ui.set_visible(pass, false);
        }
        if let Some(board) = layout
            .find(BOARD_ID)
            .filter(|b| ui.widget::<ListBox>(*b).is_some())
        {
            c.build_squares(ui, board, create_square);
        }
        c
    }

    fn build_squares(
        &mut self,
        ui: &mut UiRoot,
        board: ElementId,
        create_square: &mut dyn FnMut(&mut UiRoot) -> Option<ImportedLayout>,
    ) {
        let side = SQUARE_SIZE * SIZE as f32;
        let grid = ui.insert(Element::at(0.0, 0.0, side, side), Container);
        for index in 0..SQUARE_COUNT {
            // A square that cannot be built leaves the board without any:
            // the grid is never added.
            let square = match create_square(ui) {
                Some(s) if ui.widget::<DatElement>(s.root).is_some() => Some(s),
                Some(other) => {
                    ui.destroy(other.root);
                    None
                }
                None => None,
            };
            let Some(square) = square else {
                ui.destroy(grid);
                self.squares.clear();
                self.selections.clear();
                return;
            };
            let id = square.root;
            if let Some(e) = ui.element_mut(id) {
                e.left = (index % 8) as f32 * SQUARE_SIZE;
                e.top = (index / 8) as f32 * SQUARE_SIZE;
            }
            bind_click(ui, id);
            if let Some(d) = ui.widget_mut::<DatElement>(id) {
                d.runtime_image = Some(None);
            }
            ui.add_child(grid, id);
            self.squares.push(id);
            self.selections.push(square.find(SELECTION_ID));
        }
        ui.with::<ListBox, _>(board, |l, ui| {
            l.add_prebuilt_row(ui, board, grid);
        });
    }

    /// The squares, numbered left to right and top to bottom as the window
    /// shows them.
    #[must_use]
    pub fn squares(&self) -> &[ElementId] {
        &self.squares
    }

    /// The square the local player has picked up a piece from.
    #[must_use]
    pub fn selected(&self) -> Option<ChessSquare> {
        self.selected
    }

    /// The image the board's layout gives `piece`, or zero when it gives
    /// none.
    #[must_use]
    pub fn piece_sprite(&self, piece: ChessPiece) -> u32 {
        let side = if piece.team == ChessTeam::White {
            0
        } else {
            PIECES_PER_SIDE
        };
        let kind = match piece.piece_type {
            ChessPieceType::Pawn => 0,
            ChessPieceType::Bishop => 1,
            ChessPieceType::Knight => 2,
            ChessPieceType::Rook => 3,
            ChessPieceType::Queen => 4,
            ChessPieceType::King => 5,
        };
        self.piece_images[side + kind]
    }

    /// One frame: the game as the session last sent it. Says whether a
    /// game that ended by resigning or with no result hides the window,
    /// and whether the indicator-bar button changes.
    pub fn update(&mut self, ui: &mut UiRoot, view: Option<&ChessView>) -> MiniGameUpdate {
        let mut out = MiniGameUpdate::default();
        let Some(view) = view else {
            return out;
        };
        match self.games_ended {
            Some(ended) if ended != view.games_ended => out.hide = true,
            _ => {}
        }
        self.games_ended = Some(view.games_ended);
        let active = view.board_guid != 0;
        if active != self.indicator_active {
            self.indicator_active = active;
            out.indicator = Some(active);
        }
        if self.drawn_revision == Some(view.revision) {
            return out;
        }
        if view.stage != ChessStage::MyTurn {
            self.selected = None;
        }
        self.view = Some(view.clone());
        self.redraw(ui);
        out
    }

    /// A click on window square `index`.
    pub fn click_square(&mut self, ui: &mut UiRoot, index: usize) {
        let Some(view) = &self.view else {
            return;
        };
        if index >= self.squares.len() || view.stage != ChessStage::MyTurn {
            return;
        }
        let Some(team) = view.team else {
            return;
        };
        let square = square_at(index, Some(team));
        if let Some(from) = self.selected.take() {
            if from != square {
                emit(ui, chess_command(ChessCommand::Move { from, to: square }));
            }
        } else if view.board.get(square).is_some_and(|p| p.team == team) {
            self.selected = Some(square);
        }
        self.redraw(ui);
    }

    /// The resignation question was answered. True means resign (the
    /// window sends [`ChessCommand::Resign`]).
    pub fn resign_answered(&mut self, confirmed: bool) -> bool {
        self.confirming_resign = false;
        confirmed
    }

    fn redraw(&mut self, ui: &mut UiRoot) {
        let Some(view) = &self.view else {
            return;
        };
        self.drawn_revision = Some(view.revision);
        for (index, &element) in self.squares.iter().enumerate() {
            let square = square_at(index, view.team);
            let image = view
                .board
                .get(square)
                .map(|p| self.piece_sprite(p))
                .filter(|&i| i != 0);
            if let Some(d) = ui.widget_mut::<DatElement>(element) {
                d.runtime_image = Some(image.map(TextureRef::Surface));
            }
            if let Some(Some(selection)) = self.selections.get(index) {
                ui.set_visible(*selection, self.selected == Some(square));
            }
        }
    }
}

fn chess_command(command: ChessCommand) -> PanelOutput {
    PanelOutput::Session(SessionCommand::Gameplay(GameplayCommand::Chess(command)))
}

impl PanelController for MiniGameController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if !matches!(message, ElementMessage::Clicked { .. }) {
            return false;
        }
        if Some(element) == self.close {
            emit(ui, PanelOutput::ClosePanel(panel_id::MINI_GAME));
        } else if Some(element) == self.resign {
            if !self.confirming_resign {
                self.confirming_resign = true;
                emit(
                    ui,
                    PanelOutput::ConfirmEither {
                        message: RESIGN_CONFIRMATION.to_owned(),
                        on_yes: Box::new(PanelOutput::ChessResignAnswered(true)),
                        on_no: Box::new(PanelOutput::ChessResignAnswered(false)),
                    },
                );
            }
        } else if Some(element) == self.stalemate {
            emit(ui, chess_command(ChessCommand::ToggleStalemate));
        } else if Some(element) == self.pass {
            emit(ui, chess_command(ChessCommand::Pass));
        } else if let Some(index) = self.squares.iter().position(|&s| s == element) {
            self.click_square(ui, index);
        } else {
            return false;
        }
        true
    }
}
