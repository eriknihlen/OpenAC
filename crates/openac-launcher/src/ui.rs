//! Draws the launcher window from the window model (OpenAC's MainWindow and
//! SessionConsoleWindow). Every decision is the model's; this only lays out
//! what it says and calls its commands.

use egui::{Align, Color32, CornerRadius, Frame, Layout, Margin, RichText, Stroke, Ui, Vec2};

use ac_launcher_core::plugins::PluginReleaseChannel;
use ac_launcher_core::profiles::LauncherTextEditorKind;
use ac_launcher_core::window::accounts::{AccountGroup, AccountServerRow, RowLaunchMode};
use ac_launcher_core::window::install_dialog::PluginEnableChoice;
use ac_launcher_core::window::install_folder::InstallFolderRow;
use ac_launcher_core::window::plugins_panel::{
    BETA_CHANNEL_LABEL, PluginDiscoverRow, PluginInstalledRow, STABLE_CHANNEL_LABEL,
    permission_count_text,
};
use ac_launcher_core::window::{ClosePrompt, LauncherMainTab};

use crate::app::LauncherApp;
use crate::theme::{self, hex};

/// Draws one frame.
pub fn draw(app: &mut LauncherApp, root: &mut Ui) {
    let ctx = root.ctx().clone();
    if ctx.input(|input| input.key_pressed(egui::Key::Escape)) {
        app.window.close_active_modal();
    }
    header(app, root);
    bottom_bar(app, root);
    egui::CentralPanel::default()
        .frame(Frame::new().fill(theme::BACKGROUND))
        .show(root, |ui| {
            banners(app, ui);
            tabs(app, ui);
            ui.add_space(6.0);
            Frame::new()
                .inner_margin(Margin::symmetric(14, 4))
                .show(ui, |ui| match app.window.selected_tab {
                    LauncherMainTab::Accounts => accounts_tab(app, ui),
                    LauncherMainTab::Plugins => plugins_tab(app, ui),
                });
        });
    dialogs(app, &ctx);
    consoles(app, &ctx);
}

// --- Small widgets -----------------------------------------------------------------------

fn muted(text: impl Into<String>) -> RichText {
    RichText::new(text).color(theme::MUTED).size(12.0)
}

fn error_text(text: impl Into<String>) -> RichText {
    RichText::new(text).color(theme::ERROR)
}

/// A primary button: gold when it is ready, plain otherwise.
fn primary(ui: &mut Ui, text: &str, ready: bool, enabled: bool) -> bool {
    let label = RichText::new(text).strong();
    let button = if ready && enabled {
        egui::Button::new(label.color(theme::ON_GOLD)).fill(theme::GOLD)
    } else {
        egui::Button::new(label)
    };
    ui.add_enabled(enabled, button).clicked()
}

fn button(ui: &mut Ui, text: &str, enabled: bool) -> bool {
    ui.add_enabled(enabled, egui::Button::new(text)).clicked()
}

fn chip(ui: &mut Ui, text: &str, fill: Color32, color: Color32) {
    Frame::new()
        .fill(fill)
        .corner_radius(CornerRadius::same(8))
        .inner_margin(Margin::symmetric(7, 2))
        .show(ui, |ui| {
            ui.label(RichText::new(text).size(11.0).color(color));
        });
}

/// The expander's triangle: pointing down when open, right when closed.
fn expander_arrow(ui: &mut Ui, expanded: bool) {
    let (rect, _) = ui.allocate_exact_size(Vec2::splat(12.0), egui::Sense::hover());
    let c = rect.center();
    let points = if expanded {
        vec![
            egui::pos2(c.x - 4.0, c.y - 2.0),
            egui::pos2(c.x + 4.0, c.y - 2.0),
            egui::pos2(c.x, c.y + 3.0),
        ]
    } else {
        vec![
            egui::pos2(c.x - 2.0, c.y - 4.0),
            egui::pos2(c.x + 3.0, c.y),
            egui::pos2(c.x - 2.0, c.y + 4.0),
        ]
    };
    ui.painter().add(egui::Shape::convex_polygon(
        points,
        theme::SECONDARY,
        Stroke::NONE,
    ));
}

fn card_frame() -> Frame {
    Frame::new()
        .fill(theme::CARD)
        .stroke(Stroke::new(1.0, theme::BORDER))
        .corner_radius(CornerRadius::same(4))
        .inner_margin(Margin::same(16))
}

// --- Header, banners, tabs ---------------------------------------------------------------

fn header(app: &mut LauncherApp, root: &mut Ui) {
    egui::Panel::top("header")
        .frame(
            Frame::new()
                .fill(theme::HEADER)
                .inner_margin(Margin::symmetric(18, 10)),
        )
        .show(root, |ui| {
            ui.horizontal(|ui| {
                ui.vertical(|ui| {
                    ui.label(RichText::new("OpenAC").size(27.0).color(theme::GOLD));
                    ui.label(
                        RichText::new("Asheron's Call forever.")
                            .size(18.0)
                            .color(theme::PARCHMENT),
                    );
                });
                let window = &mut app.window;
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    if ui
                        .add_enabled(
                            window.can_interact() && !window.update_prompt.is_busy,
                            egui::Button::new("Check for updates").min_size(Vec2::new(0.0, 34.0)),
                        )
                        .on_hover_text(
                            "Checks the client, the launcher and your plugins now; the launcher \
                             also checks every 20 minutes",
                        )
                        .clicked()
                    {
                        window.check_for_updates();
                    }
                    ui.add_space(14.0);
                    if window.has_versions() {
                        egui::Grid::new("versions")
                            .num_columns(2)
                            .spacing([10.0, 1.0])
                            .show(ui, |ui| {
                                let caption = |text: &str| {
                                    RichText::new(text).size(12.0).color(hex("#9DB0BA"))
                                };
                                ui.label(caption("Client"));
                                ui.label(RichText::new(window.client_version_text()).size(12.0));
                                ui.end_row();
                                ui.label(caption("Launcher"));
                                ui.label(RichText::new(window.launcher_version_text()).size(12.0));
                                ui.end_row();
                                ui.label(caption("Plugins"));
                                let colour = if window.has_plugin_updates() {
                                    theme::GOLD
                                } else {
                                    hex("#8FD18F")
                                };
                                ui.label(
                                    RichText::new(window.plugins_update_text())
                                        .size(12.0)
                                        .color(colour),
                                );
                                ui.end_row();
                            });
                    }
                });
            });
        });
}

fn banners(app: &mut LauncherApp, ui: &mut Ui) {
    let window = &mut app.window;
    if window.show_installation_banner() {
        Frame::new()
            .fill(theme::BANNER)
            .inner_margin(Margin::symmetric(14, 8))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        ui.label(RichText::new(window.installation_banner_title()).strong());
                        ui.label(muted(window.installation_status()));
                    });
                    if !window.is_installation_checking() {
                        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                            if primary(ui, "Open setup", true, window.can_interact()) {
                                window.open_first_run();
                            }
                        });
                    }
                });
            });
    }
    if window.show_update_banner() {
        Frame::new()
            .fill(theme::BANNER)
            .inner_margin(Margin::symmetric(14, 8))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        ui.label(RichText::new("An OpenAC update is available").strong());
                        ui.label(muted(window.update_prompt.body()));
                    });
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        if primary(ui, "Review update", true, window.can_interact()) {
                            window.review_update();
                        }
                    });
                });
            });
    }
}

fn tabs(app: &mut LauncherApp, ui: &mut Ui) {
    Frame::new()
        .inner_margin(Margin {
            left: 14,
            right: 14,
            top: 10,
            bottom: 0,
        })
        .show(ui, |ui| {
            ui.horizontal(|ui| {
                for (tab, label) in [
                    (LauncherMainTab::Accounts, "Accounts"),
                    (LauncherMainTab::Plugins, "Plugins"),
                ] {
                    let selected = app.window.selected_tab == tab;
                    let text = RichText::new(label).size(15.0).color(if selected {
                        theme::GOLD
                    } else {
                        theme::SECONDARY
                    });
                    if ui.add(egui::Button::new(text).frame(selected)).clicked() && !selected {
                        match tab {
                            LauncherMainTab::Accounts => {
                                app.window.selected_tab = LauncherMainTab::Accounts;
                            }
                            LauncherMainTab::Plugins => app.window.select_plugins_tab(),
                        }
                    }
                }
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    let gear = egui::Button::new(RichText::new("⚙").size(16.0)).frame(false);
                    if ui
                        .add_enabled(app.window.can_interact(), gear)
                        .on_hover_text("Settings")
                        .clicked()
                    {
                        app.window.open_settings();
                    }
                });
            });
            let rect = ui.max_rect();
            ui.painter().hline(
                rect.x_range(),
                ui.min_rect().bottom() + 2.0,
                Stroke::new(1.0, theme::BORDER),
            );
        });
}

// --- Accounts tab ------------------------------------------------------------------------

/// OpenAC's account grid, `ColumnDefinitions="26,*,160,112,*,64,92"`
/// (`0.0` marks a `*` column, which share what is left equally).
const ACCOUNT_COLUMN_WIDTHS: [f32; 7] = [26.0, 0.0, 160.0, 112.0, 0.0, 64.0, 92.0];
/// OpenAC's `ColumnSpacing="6"`.
const ACCOUNT_COLUMN_SPACING: f32 = 6.0;
/// A row's padding inside its group (OpenAC `Padding="12,3,8,3"`).
const ROW_PADDING: Margin = Margin {
    left: 12,
    right: 8,
    top: 3,
    bottom: 3,
};
/// The group's border, inside which the rows sit.
const GROUP_BORDER: f32 = 1.0;
/// A row's height: the server's two lines, and the combo boxes and buttons.
const ROW_HEIGHT: f32 = 34.0;
/// The combo boxes' and buttons' height.
const CONTROL_HEIGHT: f32 = 26.0;

/// The x range of each account grid column. The header and every row are
/// laid out from one of these, made once from the list's width, so a column
/// starts at the same x in each (OpenAC repeats the same fixed grid in the
/// header and in each row). Nothing is wider than its column.
#[derive(Clone, Copy, Debug, PartialEq)]
struct AccountColumns([egui::Rangef; 7]);

impl AccountColumns {
    /// The columns of a row whose content spans `content` (inside the row's
    /// padding).
    fn new(content: egui::Rangef) -> Self {
        let gaps = ACCOUNT_COLUMN_SPACING * (ACCOUNT_COLUMN_WIDTHS.len() - 1) as f32;
        let fixed: f32 = ACCOUNT_COLUMN_WIDTHS.iter().sum::<f32>() + gaps;
        let stars = ACCOUNT_COLUMN_WIDTHS.iter().filter(|w| **w == 0.0).count() as f32;
        let star = ((content.span() - fixed) / stars).max(0.0);
        let mut left = content.min;
        let mut cells = [egui::Rangef::new(left, left); 7];
        for (cell, width) in cells.iter_mut().zip(ACCOUNT_COLUMN_WIDTHS) {
            let width = if width == 0.0 { star } else { width };
            *cell = egui::Rangef::new(left, left + width);
            left += width + ACCOUNT_COLUMN_SPACING;
        }
        Self(cells)
    }

    /// The columns of the rows in a list that spans `list`: inside the
    /// group's border and the row's padding.
    fn for_list(list: egui::Rangef) -> Self {
        Self::new(egui::Rangef::new(
            list.min + GROUP_BORDER + f32::from(ROW_PADDING.left),
            list.max - GROUP_BORDER - f32::from(ROW_PADDING.right),
        ))
    }

    /// Column `index` over the rows `y`.
    fn cell(&self, index: usize, y: egui::Rangef) -> egui::Rect {
        egui::Rect::from_x_y_ranges(self.0[index], y)
    }
}

/// Lays out one grid cell: a child of `ui` over `rect`, drawn only inside
/// it. Returns what the cell's content took (which may be wider than the
/// cell if something did not shrink; the clip hides that).
fn grid_cell(
    ui: &mut Ui,
    rect: egui::Rect,
    layout: Layout,
    add: impl FnOnce(&mut Ui),
) -> egui::Rect {
    let mut child = ui.new_child(egui::UiBuilder::new().max_rect(rect).layout(layout));
    // Room for a hovered button's outline, never into the next column.
    child.shrink_clip_rect(rect.expand2(Vec2::new(ACCOUNT_COLUMN_SPACING / 2.0, 3.0)));
    add(&mut child);
    child.min_rect()
}

/// A single line of text cut short with an ellipsis where it does not fit.
fn cut(ui: &mut Ui, text: impl Into<egui::WidgetText>) -> egui::Response {
    ui.add(egui::Label::new(text).truncate())
}

/// What the account list asks of the window.
#[derive(Clone, Debug, PartialEq)]
enum AccountAction {
    /// Show or hide an account's rows.
    Expand { account: String, expanded: bool },
    /// The group's Edit plugins (an account with one server).
    EditPlugins { account: String, server: String },
    /// Something in one row.
    Row {
        account: String,
        server: String,
        action: RowAction,
    },
}

/// What one row asks of the window.
#[derive(Clone, Debug, PartialEq)]
enum RowAction {
    Check(bool),
    Character(String),
    Mode(RowLaunchMode),
    Play,
    Stop,
    LogonCommands,
    AccountPlugins,
    CharacterPlugins,
    Console,
    OpenLogs,
    RemoveCharacter,
}

/// What a row may do, from the window.
#[derive(Clone, Copy, Debug)]
struct RowState {
    /// Its controls take input (a starting account keeps its selection).
    interact: bool,
    /// Its Stop is enabled.
    can_stop: bool,
    /// There is a logs folder to open.
    has_logs: bool,
}

/// Where the account list put things, for the layout tests.
#[derive(Debug, Default)]
struct AccountLayout {
    /// What each column header took.
    header: Vec<egui::Rect>,
    /// What each cell of each row took, in order.
    rows: Vec<[egui::Rect; 7]>,
    /// What each group's header line took.
    groups: Vec<egui::Rect>,
}

fn accounts_tab(app: &mut LauncherApp, ui: &mut Ui) {
    let window = &mut app.window;
    ui.horizontal(|ui| {
        if primary(
            ui,
            &window.play_checked_text(),
            window.has_playable_checked_rows(),
            window.can_launch_checked(),
        ) {
            window.launch_checked();
        }
        ui.label(muted(window.checked_selection_summary()));
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if button(ui, "Check servers", window.can_check_servers()) {
                window.check_servers();
            }
        });
    });
    if window.has_profile_filters() {
        ui.horizontal(|ui| {
            for chip in window.profile_filters.clone() {
                if ui.selectable_label(chip.is_selected, &chip.label).clicked() {
                    window.set_profile_filter(chip.profile.as_deref());
                }
            }
        });
    }
    ui.horizontal_wrapped(|ui| {
        let mut only_checked = window.show_only_checked_accounts();
        if ui
            .checkbox(&mut only_checked, "Show only checked accounts")
            .changed()
        {
            window.set_show_only_checked_accounts(only_checked);
        }
        ui.add_space(12.0);
        let mut relaunch = window.relaunch_checked_clients_automatically();
        if ui
            .checkbox(&mut relaunch, "Relaunch checked clients automatically")
            .changed()
        {
            window.set_relaunch_checked_clients_automatically(relaunch);
        }
        ui.label("Delay (seconds)");
        let mut delay = window.relaunch_delay_seconds_text().to_owned();
        if ui
            .add(egui::TextEdit::singleline(&mut delay).desired_width(65.0))
            .on_hover_text("Automatic relaunch delay in seconds")
            .changed()
        {
            window.set_relaunch_delay_seconds_text(&delay);
        }
    });
    ui.label(
        RichText::new(
            "When enabled, checked clients relaunch after a crash or Stop. Uncheck a client or \
             turn this off to keep it stopped. These controls last for this launcher session.",
        )
        .color(theme::MUTED)
        .size(11.0),
    );
    if !window.is_relaunch_delay_valid() {
        ui.label(
            RichText::new(
                "Enter a whole number from 5 to 3600 seconds. Automatic relaunch is paused while \
                 this value is invalid.",
            )
            .color(hex("#F17474"))
            .size(11.0),
        );
    }
    ui.add_space(4.0);
    let has_logs = window
        .install_folder
        .as_ref()
        .is_some_and(|panel| panel.folders.iter().any(|f| f.label == "Logs"));
    let interact = window.can_interact();
    let groups = window.accounts.clone();
    let states = |row: &AccountServerRow| {
        let (account, server) = (row.account_name.as_str(), row.server_name.as_str());
        RowState {
            // A starting account keeps its selection; a running one can be unchecked.
            interact: interact && !window.is_row_starting(account, server),
            can_stop: window.can_stop_row(account, server),
            has_logs,
        }
    };
    let actions = account_list(
        ui,
        &groups,
        window.has_accounts(),
        interact,
        &states,
        &mut AccountLayout::default(),
    );
    for action in actions {
        apply_account_action(window, action);
    }
}

/// Carries out what the account list asked.
fn apply_account_action(
    window: &mut ac_launcher_core::window::LauncherWindow,
    action: AccountAction,
) {
    let (account, server, action) = match action {
        AccountAction::Expand { account, expanded } => {
            window.set_group_expanded(&account, expanded);
            return;
        }
        AccountAction::EditPlugins { account, server } => {
            window.open_account_plugins(&account, &server);
            return;
        }
        AccountAction::Row {
            account,
            server,
            action,
        } => (account, server, action),
    };
    let (account, server) = (account.as_str(), server.as_str());
    match action {
        RowAction::Check(checked) => window.set_row_checked(account, server, checked),
        RowAction::Character(character) => window.set_row_character(account, server, &character),
        RowAction::Mode(mode) => window.set_row_launch_mode(account, server, mode),
        RowAction::Play => window.launch_row(account, server),
        RowAction::Stop => window.stop_row(account, server),
        RowAction::LogonCommands => window.open_logon_commands(),
        RowAction::AccountPlugins => window.open_account_plugins(account, server),
        RowAction::CharacterPlugins => window.open_character_plugins(account, server),
        RowAction::Console => window.open_console(account, server),
        RowAction::OpenLogs => {
            let logs = window
                .install_folder
                .as_ref()
                .and_then(|panel| panel.folders.iter().find(|f| f.label == "Logs"))
                .map(|row| row.path.clone());
            if let Some(logs) = logs {
                let _ = std::fs::create_dir_all(&logs);
                crate::shell::open_folder(&logs);
            }
        }
        RowAction::RemoveCharacter => window.request_remove_character(account, server),
    }
}

/// The column headers and the account groups below them (OpenAC's header
/// grid and its `AccountsScroll`, which never scrolls sideways).
fn account_list(
    ui: &mut Ui,
    groups: &[AccountGroup],
    has_accounts: bool,
    interact: bool,
    states: &dyn Fn(&AccountServerRow) -> RowState,
    layout: &mut AccountLayout,
) -> Vec<AccountAction> {
    let columns = AccountColumns::for_list(ui.available_rect_before_wrap().x_range());
    let (header, _) =
        ui.allocate_exact_size(Vec2::new(ui.available_width(), 16.0), egui::Sense::hover());
    for (column, text) in [
        (1, "Server · status · players"),
        (2, "Character"),
        (3, "Launch mode"),
        (4, "Session"),
    ] {
        let rect = columns.cell(column, header.y_range());
        layout.header.push(grid_cell(
            ui,
            rect,
            Layout::left_to_right(Align::Center),
            |ui| {
                cut(ui, muted(text));
            },
        ));
    }
    ui.add_space(5.0);
    let mut actions = Vec::new();
    egui::ScrollArea::vertical()
        .auto_shrink([false, false])
        .show(ui, |ui| {
            if !has_accounts {
                ui.add_space(16.0);
                ui.label(muted("Add your users and servers with the editors below."));
            }
            for group in groups.iter().filter(|group| group.is_visible) {
                Frame::new()
                    .fill(theme::GROUP)
                    .stroke(Stroke::new(GROUP_BORDER, theme::BORDER))
                    .corner_radius(CornerRadius::same(3))
                    .show(ui, |ui| {
                        ui.set_width(ui.available_width());
                        ui.spacing_mut().item_spacing.y = 0.0;
                        group_header(ui, group, interact, &mut actions, layout);
                        if group.is_expanded {
                            for row in group.servers.iter().filter(|row| row.is_visible) {
                                let cells =
                                    account_row(ui, &columns, row, states(row), &mut actions);
                                layout.rows.push(cells);
                            }
                        }
                    });
                ui.add_space(9.0);
            }
        });
    actions
}

/// An account's header line: the expander with the name, the profile tags
/// and subtitle, and for an account with one server its plugins with Edit
/// plugins on the right (OpenAC's `Auto,*,Auto` grid).
fn group_header(
    ui: &mut Ui,
    group: &AccountGroup,
    interact: bool,
    actions: &mut Vec<AccountAction>,
    layout: &mut AccountLayout,
) {
    let response = Frame::new()
        .fill(theme::CARD)
        .inner_margin(Margin::symmetric(10, 4))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.horizontal(|ui| {
                ui.set_min_height(24.0);
                ui.spacing_mut().item_spacing.x = 10.0;
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    if group.has_one_server() {
                        ui.spacing_mut().item_spacing.x = 8.0;
                        let edit = egui::Button::new(
                            RichText::new("Edit plugins").size(12.0).color(theme::GOLD),
                        )
                        .fill(Color32::TRANSPARENT)
                        .stroke(Stroke::new(1.0, hex("#5B4D30")))
                        .min_size(Vec2::new(0.0, 24.0));
                        if ui
                            .add_enabled(interact, edit)
                            .on_hover_text(format!("Edit plugins for {}", group.account_name))
                            .clicked()
                        {
                            actions.push(AccountAction::EditPlugins {
                                account: group.account_name.clone(),
                                server: group.server_name.clone(),
                            });
                        }
                        ui.allocate_ui(Vec2::new(260.0, 20.0), |ui| {
                            cut(ui, RichText::new(&group.plugins_summary).size(12.0))
                                .on_hover_text(&group.plugins_summary);
                        });
                        ui.label(muted("Plugins for this account:"));
                    }
                    ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                        let toggle = ui
                            .scope(|ui| {
                                ui.spacing_mut().item_spacing.x = 10.0;
                                expander_arrow(ui, group.is_expanded);
                                cut(ui, RichText::new(&group.account_name).strong().size(14.0));
                            })
                            .response;
                        let toggle = ui.interact(
                            toggle.rect,
                            egui::Id::new(("group", &group.account_name)),
                            egui::Sense::click(),
                        );
                        if toggle
                            .on_hover_text(format!("Show or hide {}", group.account_name))
                            .clicked()
                        {
                            actions.push(AccountAction::Expand {
                                account: group.account_name.clone(),
                                expanded: !group.is_expanded,
                            });
                        }
                        ui.spacing_mut().item_spacing.x = 4.0;
                        for tag in &group.profiles {
                            chip(ui, tag, theme::GOLD, theme::ON_GOLD);
                        }
                        ui.add_space(4.0);
                        cut(ui, muted(&group.subtitle));
                    });
                });
            });
        })
        .response;
    layout.groups.push(response.rect);
}

/// One account and server row, every cell in its grid column. Returns what
/// each cell took.
fn account_row(
    ui: &mut Ui,
    columns: &AccountColumns,
    row: &AccountServerRow,
    state: RowState,
    actions: &mut Vec<AccountAction>,
) -> [egui::Rect; 7] {
    let mut act = |action| {
        actions.push(AccountAction::Row {
            account: row.account_name.clone(),
            server: row.server_name.clone(),
            action,
        });
    };
    let (account, server) = (row.account_name.as_str(), row.server_name.as_str());
    let can_edit = !row.is_active() && state.interact;
    // A running play session switches between graphical and headless from
    // the mode box.
    let can_switch = can_edit || (row.can_switch_window() && state.interact);
    let mut cells = [egui::Rect::NOTHING; 7];
    Frame::new().inner_margin(ROW_PADDING).show(ui, |ui| {
        let top = ui.min_rect().top();
        ui.painter().hline(
            ui.max_rect().x_range(),
            top - f32::from(ROW_PADDING.top),
            Stroke::new(1.0, theme::ROW_LINE),
        );
        let (line, _) = ui.allocate_exact_size(
            Vec2::new(ui.available_width(), ROW_HEIGHT),
            egui::Sense::hover(),
        );
        let y = line.y_range();
        let centered = Layout::left_to_right(Align::Center);
        cells[0] = grid_cell(ui, columns.cell(0, y), centered, |ui| {
            let mut checked = row.is_checked;
            if ui
                .add(egui::Checkbox::without_text(&mut checked))
                .on_hover_text("Select account and server for batch launch and automatic relaunch")
                .changed()
            {
                act(RowAction::Check(checked));
            }
        });
        cells[1] = grid_cell(ui, columns.cell(1, y), Layout::top_down(Align::Min), |ui| {
            ui.spacing_mut().item_spacing = Vec2::new(6.0, 1.0);
            ui.add_space(2.0);
            ui.horizontal(|ui| {
                ui.spacing_mut().item_spacing.x = 0.0;
                let (dot, _) = ui.allocate_exact_size(Vec2::new(14.0, 10.0), egui::Sense::hover());
                ui.painter().circle_filled(
                    egui::pos2(dot.left() + 3.5, dot.center().y),
                    3.5,
                    hex(row.server_dot_color()),
                );
                cut(ui, RichText::new(server).strong()).on_hover_text(&row.endpoint);
            });
            ui.horizontal(|ui| {
                let latency = row.latency_text();
                let latency_width = if row.has_latency() {
                    let font = egui::FontId::proportional(11.0);
                    let text = ui
                        .painter()
                        .layout_no_wrap(latency.clone(), font, theme::MUTED)
                        .size()
                        .x;
                    14.0 + text + 2.0 * ui.spacing().item_spacing.x
                } else {
                    0.0
                };
                let status_width = (ui.available_width() - latency_width).max(0.0);
                ui.allocate_ui(Vec2::new(status_width, 14.0), |ui| {
                    cut(
                        ui,
                        RichText::new(&row.server_status_text)
                            .color(theme::MUTED)
                            .size(11.0),
                    )
                    .on_hover_text(
                        "Status checks the game endpoint. Player counts are reported by \
                         TreeStats, matched by server name.",
                    );
                });
                if row.has_latency() {
                    let (rect, response) =
                        ui.allocate_exact_size(Vec2::new(14.0, 10.0), egui::Sense::hover());
                    for (index, height) in [(1u8, 4.0f32), (2, 7.0), (3, 10.0)] {
                        let x = rect.left() + f32::from(index - 1) * 5.0;
                        let bar = egui::Rect::from_min_max(
                            egui::pos2(x, rect.bottom() - height),
                            egui::pos2(x + 3.0, rect.bottom()),
                        );
                        ui.painter()
                            .rect_filled(bar, 1.0, hex(row.ping_bar_color(index)));
                    }
                    response.on_hover_text(row.ping_tooltip());
                    ui.label(RichText::new(latency).color(theme::MUTED).size(11.0))
                        .on_hover_text(row.ping_tooltip());
                }
            });
        });
        cells[2] = grid_cell(ui, columns.cell(2, y), centered, |ui| {
            let width = ui.available_width();
            ui.add_enabled_ui(can_edit, |ui| {
                egui::ComboBox::from_id_salt(("character", account, server))
                    .width(width)
                    .truncate()
                    .selected_text(row.displayed_character())
                    .show_ui(ui, |ui| {
                        for choice in &row.character_choices {
                            if ui
                                .selectable_label(row.selected_character == *choice, choice)
                                .clicked()
                            {
                                act(RowAction::Character(choice.clone()));
                            }
                        }
                    })
                    .response
                    .on_hover_text(row.displayed_character());
            });
        });
        cells[3] = grid_cell(ui, columns.cell(3, y), centered, |ui| {
            let width = ui.available_width();
            ui.add_enabled_ui(can_switch, |ui| {
                egui::ComboBox::from_id_salt(("mode", account, server))
                    .width(width)
                    .truncate()
                    .selected_text(row.displayed_launch_mode().label())
                    .show_ui(ui, |ui| {
                        for mode in RowLaunchMode::ALL {
                            if ui
                                .selectable_label(row.displayed_launch_mode() == mode, mode.label())
                                .clicked()
                            {
                                act(RowAction::Mode(mode));
                            }
                        }
                    });
            });
        });
        // OpenAC sets the session text 2 px lower (`Margin="0,2,0,0"`) to sit level
        // with the combo boxes' larger text.
        let session = columns.cell(4, y).translate(Vec2::new(0.0, 2.0));
        cells[4] = grid_cell(ui, session, centered, |ui| match row.hold_text.as_deref() {
            // The server's hold counts down in place of the status (the window
            // redraws at least every 250 ms).
            Some(hold) => {
                cut(ui, RichText::new(hold).color(theme::GOLD).size(12.0)).on_hover_text(hold);
            }
            None => {
                cut(ui, muted(&row.status)).on_hover_text(&row.status);
            }
        });
        cells[5] = grid_cell(ui, columns.cell(5, y), centered, |ui| {
            let size = Vec2::new(ui.available_width(), CONTROL_HEIGHT);
            if row.is_active() {
                let stop = egui::Button::new("Stop").min_size(size);
                if ui.add_enabled(state.can_stop, stop).clicked() {
                    act(RowAction::Stop);
                }
            } else {
                let label = RichText::new("Play").strong();
                let play = if row.can_play() {
                    egui::Button::new(label.color(theme::ON_GOLD)).fill(theme::GOLD)
                } else {
                    egui::Button::new(label)
                };
                let response = ui.add_enabled(row.can_play(), play.min_size(size));
                let response = if row.can_play() {
                    response
                } else {
                    response.on_disabled_hover_text(&row.disabled_reason)
                };
                if response.clicked() {
                    act(RowAction::Play);
                }
            }
        });
        cells[6] = grid_cell(ui, columns.cell(6, y), centered, |ui| {
            let size = Vec2::new(ui.available_width(), CONTROL_HEIGHT);
            ui.add_enabled_ui(state.interact, |ui| {
                // OpenAC's "Options ▾": room for the arrow at the right, drawn below
                // (the font has no ▾).
                let arrow_room =
                    egui::Atom::custom(egui::Id::new("options-arrow"), Vec2::new(8.0, 6.0));
                let options = egui::Button::new("Options")
                    .right_text(arrow_room)
                    .min_size(size);
                let (response, _) =
                    egui::containers::menu::MenuButton::from_button(options).ui(ui, |ui| {
                        let mut item =
                            |ui: &mut Ui, text: &str, enabled: bool, action: RowAction| {
                                if ui.add_enabled(enabled, egui::Button::new(text)).clicked() {
                                    act(action);
                                    ui.close();
                                }
                            };
                        item(ui, "Logon commands…", true, RowAction::LogonCommands);
                        item(
                            ui,
                            "Plugins for this account…",
                            true,
                            RowAction::AccountPlugins,
                        );
                        item(
                            ui,
                            "Plugins for this character…",
                            row.character_name().is_some(),
                            RowAction::CharacterPlugins,
                        );
                        item(ui, "Console", row.can_open_console(), RowAction::Console);
                        ui.separator();
                        item(ui, "Open logs folder", state.has_logs, RowAction::OpenLogs);
                        let remove = row.character_name().is_some() && !row.is_active();
                        if ui
                            .add_enabled(
                                remove,
                                egui::Button::new(
                                    RichText::new("Remove character").color(hex("#E09A9A")),
                                ),
                            )
                            .clicked()
                        {
                            act(RowAction::RemoveCharacter);
                            ui.close();
                        }
                    });
                let arrow = egui::pos2(
                    response.rect.right() - ui.spacing().button_padding.x - 4.0,
                    response.rect.center().y,
                );
                let color = if response.enabled() {
                    theme::SECONDARY
                } else {
                    theme::MUTED
                };
                ui.painter().add(egui::Shape::convex_polygon(
                    vec![
                        egui::pos2(arrow.x - 4.0, arrow.y - 2.0),
                        egui::pos2(arrow.x + 4.0, arrow.y - 2.0),
                        egui::pos2(arrow.x, arrow.y + 3.0),
                    ],
                    color,
                    Stroke::NONE,
                ));
                response.on_hover_text(row.options_automation_name());
            });
        });
        if let Some(error) = row
            .launch_error
            .as_deref()
            .filter(|_| row.has_launch_error())
        {
            // OpenAC's second grid row, under the server column through Options.
            let x = egui::Rangef::new(columns.0[1].min, columns.0[6].max);
            let top = ui.cursor().top() + 3.0;
            let rect = egui::Rect::from_x_y_ranges(x, top..=f32::INFINITY);
            ui.scope_builder(egui::UiBuilder::new().max_rect(rect), |ui| {
                ui.add(egui::Label::new(error_text(error).size(12.0)).wrap());
                ui.add_space(3.0);
            });
        }
    });
    cells
}

// --- Plugins tab -------------------------------------------------------------------------

fn plugins_tab(app: &mut LauncherApp, ui: &mut Ui) {
    let now = std::time::SystemTime::now();
    {
        let window = &mut app.window;
        ui.horizontal(|ui| {
            if let Some(age) = window.plugins.list_age_text(now) {
                ui.label(muted(age));
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if button(ui, "Refresh list", window.can_check_plugins()) {
                    window.plugins_check_now();
                }
                if button(ui, "Add from URL", window.can_add_from_url()) {
                    window.add_from_url();
                }
                ui.add(
                    egui::TextEdit::singleline(&mut window.plugins.add_from_url_text)
                        .hint_text("https://github.com/owner/name")
                        .desired_width(280.0),
                );
            });
        });
        // No file picker here: the ZIP's path is typed, or the ZIP is dropped on
        // the window.
        ui.horizontal(|ui| {
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let path = window.plugins.local_zip_text.trim().to_owned();
                if ui
                    .add_enabled(
                        window.can_install_local_zip() && !path.is_empty(),
                        egui::Button::new("Install from ZIP"),
                    )
                    .on_hover_text("Install a plugin release ZIP from this computer")
                    .clicked()
                {
                    window.open_local_zip(std::path::Path::new(&path));
                }
                ui.add(
                    egui::TextEdit::singleline(&mut window.plugins.local_zip_text)
                        .hint_text("Path to a plugin ZIP, or drop one on the window")
                        .desired_width(280.0),
                );
            });
        });
        let dropped = ui.ctx().input(|input| input.raw.dropped_files.clone());
        if let Some(zip) = dropped
            .into_iter()
            .map(|file| file.path().to_path_buf())
            .find(|path| {
                path.extension()
                    .is_some_and(|extension| extension.eq_ignore_ascii_case("zip"))
            })
            && window.can_install_local_zip()
        {
            window.plugins.local_zip_text = zip.display().to_string();
            window.open_local_zip(&zip);
        }
        if let Some(error) = &window.plugins.error {
            ui.label(error_text(error));
        }
        if let Some(status) = &window.plugins.status_text {
            ui.label(muted(status));
        }
        ui.add_space(4.0);
    }
    let width = ui.available_width();
    app.window.plugins.set_panel_width(width);
    if app.window.plugins.is_side_by_side {
        let height = ui.available_height();
        ui.columns(2, |columns| {
            columns[0].set_min_height(height);
            discover_well(app, &mut columns[0]);
            installed_well(app, &mut columns[1]);
        });
    } else {
        let height = (ui.available_height() / 2.0 - 8.0).max(160.0);
        ui.allocate_ui(Vec2::new(width, height), |ui| installed_well(app, ui));
        ui.add_space(10.0);
        ui.allocate_ui(Vec2::new(width, height), |ui| discover_well(app, ui));
    }
}

fn well(ui: &mut Ui, add: impl FnOnce(&mut Ui)) {
    Frame::new()
        .fill(theme::GROUP)
        .stroke(Stroke::new(1.0, theme::BORDER))
        .corner_radius(CornerRadius::same(4))
        .inner_margin(Margin::same(10))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            add(ui);
        });
}

fn plugin_tile(
    app: &mut LauncherApp,
    ui: &mut Ui,
    id: &str,
    version: &str,
    installed: bool,
    initials: &str,
    icon: Option<&ac_launcher_core::window::icon::PluginIcon>,
) {
    let ctx = ui.ctx().clone();
    match icon {
        Some(icon) => {
            let texture = app.icon_texture(&ctx, id, version, installed, icon);
            ui.add(egui::Image::new((texture.id(), Vec2::splat(32.0))));
        }
        None => {
            let (rect, _) = ui.allocate_exact_size(Vec2::splat(32.0), egui::Sense::hover());
            ui.painter()
                .rect_filled(rect, 4.0, Color32::from_rgb(0x2A, 0x3A, 0x44));
            ui.painter().text(
                rect.center(),
                egui::Align2::CENTER_CENTER,
                initials,
                egui::FontId::proportional(13.0),
                theme::GOLD,
            );
        }
    }
}

fn installed_well(app: &mut LauncherApp, ui: &mut Ui) {
    well(ui, |ui| {
        ui.label(RichText::new("Installed").strong());
        ui.add(
            egui::TextEdit::singleline(&mut app.window.plugins.installed_filter)
                .hint_text("Search installed")
                .desired_width(f32::INFINITY),
        );
        egui::ScrollArea::vertical()
            .id_salt("installed")
            .auto_shrink([false, false])
            .show(ui, |ui| {
                let view = app.window.plugins.installed_view();
                if view.is_empty() {
                    ui.label(muted(app.window.plugins.installed_empty_text()));
                }
                for index in view {
                    let row = app.window.plugins.installed[index].clone();
                    installed_card(app, ui, &row);
                    ui.add_space(5.0);
                }
            });
    });
}

fn installed_card(app: &mut LauncherApp, ui: &mut Ui, row: &PluginInstalledRow) {
    let interact = app.window.can_interact() && !app.window.plugins.is_busy;
    card_frame().inner_margin(Margin::same(10)).show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.horizontal(|ui| {
            plugin_tile(
                app,
                ui,
                &row.info.id,
                &row.info.version,
                true,
                &row.initials,
                row.icon.as_ref(),
            );
            ui.vertical(|ui| {
                ui.label(RichText::new(&row.info.display_name).strong().size(15.0));
                ui.label(
                    RichText::new(row.summary())
                        .size(12.0)
                        .color(theme::SECONDARY),
                );
            });
            if row.show_beta_channel_label() {
                ui.with_layout(Layout::right_to_left(Align::Min), |ui| {
                    ui.label(
                        RichText::new("Beta Channel")
                            .size(11.0)
                            .color(theme::SECONDARY),
                    );
                });
            }
        });
        if let Some(note) = row.update_compatibility_note() {
            ui.label(RichText::new(note).size(12.0).color(theme::GOLD));
        }
        if row.info.conflict {
            ui.label(muted(
                "A newer client version bundles this plugin; it will replace this copy on update.",
            ));
        }
        if row.has_update_withheld_reason() {
            ui.label(muted(format!(
                "Update withheld: {}",
                row.update_withheld_reason.as_deref().unwrap_or_default()
            )));
        }
        ui.horizontal_wrapped(|ui| {
            ui.label(muted(format!("v{}", row.info.version)));
            if row.is_refused() {
                chip(ui, "Refused", theme::ERROR_BOX, theme::ERROR);
                ui.label(error_text(row.info.refusal.as_deref().unwrap_or_default()).size(12.0));
            } else if row.show_skipped() {
                chip(ui, "Skipped", theme::ERROR_BOX, theme::ERROR);
                ui.label(error_text(row.compatibility_text()).size(12.0));
            } else if row.show_compatibility_info_line() {
                ui.label(muted(row.compatibility_text()));
            }
        });
        ui.horizontal_wrapped(|ui| {
            if row.is_prerelease {
                chip(ui, "beta install", theme::PROGRESS_BOX, theme::TEXT);
            }
            if !row.permissions.is_empty() {
                chip(
                    ui,
                    &permission_count_text(row.permissions.len()),
                    theme::BUTTON,
                    theme::SECONDARY,
                );
            }
            if let Some(text) = row.update_chip_text() {
                chip(ui, &text, theme::BANNER, theme::GOLD);
            }
            if row.info.has_duplicate {
                chip(ui, "Duplicate", theme::ERROR_BOX, theme::ERROR);
            }
            if let Some(text) = row.blocked_text() {
                chip(ui, &text, theme::ERROR_BOX, theme::ERROR);
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if row.can_remove && button(ui, "Remove", interact) {
                    app.window.open_remove_dialog(&row.info.id);
                }
                if row.update.is_some() && primary(ui, "Update", true, interact) {
                    app.window.open_update_install(&row.info.id);
                }
                if row.show_beta_toggle {
                    let mut beta = row.is_beta_channel;
                    if ui
                        .add_enabled(interact, egui::Checkbox::new(&mut beta, "Beta updates"))
                        .changed()
                    {
                        app.window.toggle_plugin_beta(&row.info.id, beta);
                    }
                }
            });
        });
    });
}

fn discover_well(app: &mut LauncherApp, ui: &mut Ui) {
    well(ui, |ui| {
        ui.label(RichText::new("Discover").strong());
        ui.add(
            egui::TextEdit::singleline(&mut app.window.plugins.discover_filter)
                .hint_text("Search the plugin list")
                .desired_width(f32::INFINITY),
        );
        egui::ScrollArea::vertical()
            .id_salt("discover")
            .auto_shrink([false, false])
            .show(ui, |ui| {
                let panel = &app.window.plugins;
                if let Some(line) = &panel.discover_status_line {
                    ui.label(muted(line));
                }
                if panel.is_discover_checking {
                    ui.label(muted(panel.discover_checking_text()));
                }
                if panel.show_discover_empty_text() {
                    if panel.discover_filter.is_empty() {
                        ui.add_space(48.0);
                        ui.vertical_centered(|ui| {
                            ui.label(RichText::new("No Plugins Available").size(16.0).strong());
                            ui.label(
                                RichText::new(
                                    "Plugins are in active development, check back soon!",
                                )
                                .size(13.0)
                                .color(theme::MUTED),
                            );
                        });
                    } else {
                        ui.label(muted(panel.discover_empty_text()));
                    }
                }
                for index in app.window.plugins.discover_view() {
                    let row = app.window.plugins.discover[index].clone();
                    discover_card(app, ui, index, &row);
                    ui.add_space(5.0);
                }
            });
    });
}

fn discover_card(app: &mut LauncherApp, ui: &mut Ui, index: usize, row: &PluginDiscoverRow) {
    let interact = app.window.can_interact() && !app.window.plugins.is_busy;
    card_frame().inner_margin(Margin::same(10)).show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.horizontal(|ui| {
            let version = row.latest_version().unwrap_or_default().to_owned();
            plugin_tile(
                app,
                ui,
                &row.entry.id,
                &version,
                false,
                &row.initials,
                row.icon.as_ref(),
            );
            ui.vertical(|ui| {
                ui.label(RichText::new(&row.entry.name).strong().size(15.0));
                ui.label(
                    RichText::new(row.author_and_repo())
                        .size(12.0)
                        .color(theme::SECONDARY),
                );
            });
        });
        ui.label(RichText::new(&row.entry.description).size(13.0))
            .on_hover_text(&row.entry.description);
        ui.horizontal_wrapped(|ui| {
            if let Some(version) = row.latest_version() {
                ui.label(muted(format!("v{version}")));
            }
            if row.show_compatibility_warning() {
                ui.label(
                    RichText::new(row.compatibility_text())
                        .size(12.0)
                        .color(theme::GOLD),
                );
            } else if row.show_compatibility_info() {
                ui.label(muted(row.compatibility_text()));
            }
            if let Some(text) = row.channel_unavailable_text() {
                ui.label(RichText::new(text).size(12.0).color(theme::GOLD));
            }
            if !row.permissions().is_empty() {
                chip(
                    ui,
                    &permission_count_text(row.permissions().len()),
                    theme::BUTTON,
                    theme::SECONDARY,
                );
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if primary(
                    ui,
                    "Install",
                    true,
                    interact && row.can_install_selected_channel(),
                ) {
                    app.window.open_discover_install(&row.entry.id);
                }
                if row.show_channel_picker {
                    egui::ComboBox::from_id_salt(("channel", &row.entry.id))
                        .width(80.0)
                        .selected_text(row.selected_channel_label())
                        .show_ui(ui, |ui| {
                            for (label, channel) in [
                                (STABLE_CHANNEL_LABEL, PluginReleaseChannel::Stable),
                                (BETA_CHANNEL_LABEL, PluginReleaseChannel::Beta),
                            ] {
                                if ui
                                    .selectable_label(row.selected_channel == channel, label)
                                    .clicked()
                                {
                                    app.window.plugins.discover[index]
                                        .set_selected_channel(channel);
                                }
                            }
                        });
                }
            });
        });
    });
}

// --- Bottom bar ---------------------------------------------------------------------------

fn bottom_bar(app: &mut LauncherApp, root: &mut Ui) {
    egui::Panel::bottom("bottom")
        .frame(
            Frame::new()
                .fill(theme::BOTTOM)
                .inner_margin(Margin::symmetric(14, 8)),
        )
        .show(root, |ui| {
            let window = &mut app.window;
            ui.horizontal(|ui| {
                let interact = window.can_interact();
                if window.selected_tab == LauncherMainTab::Accounts {
                    if button(ui, "Edit accounts", interact) {
                        window.open_text_editor(LauncherTextEditorKind::Accounts);
                    }
                    if ui
                        .add_enabled(interact, egui::Button::new("Add server"))
                        .on_hover_text("Pick a known server, or add your own")
                        .clicked()
                    {
                        window.open_add_server();
                    }
                    if button(ui, "Edit Servers", interact) {
                        window.open_text_editor(LauncherTextEditorKind::Servers);
                    }
                    if button(ui, "Logon commands", interact) {
                        window.open_text_editor(LauncherTextEditorKind::LogonCommands);
                    }
                }
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    if window.has_cancelable_operation()
                        && button(ui, "Cancel", window.can_cancel_operation())
                    {
                        window.cancel_operation();
                    }
                    if button(ui, "Log", interact) {
                        window.open_session_log();
                    }
                    if window.show_classic_migration()
                        && ui
                            .button("Import VTank & UB…")
                            .on_hover_text(
                                "Copy profiles from a classic VirindiTank and UtilityBelt install",
                            )
                            .clicked()
                    {
                        window.open_classic_migration();
                    }
                    let response = ui.button("Installation & updates");
                    egui::Popup::from_toggle_button_response(&response).show(|ui| {
                        ui.set_width(280.0);
                        ui.label(RichText::new("Installation & updates").strong());
                        ui.label(muted(window.installation_status()));
                        if button(ui, "First-run setup", window.can_interact()) {
                            window.open_first_run();
                        }
                        if button(
                            ui,
                            "Verify files",
                            window.can_interact() && !window.has_active_sessions(),
                        ) {
                            window.verify_content();
                        }
                        if button(
                            ui,
                            "Check for updates",
                            window.can_interact() && !window.update_prompt.is_busy,
                        ) {
                            window.check_for_updates();
                        }
                        ui.label(muted(window.update_prompt.status.clone()));
                    });
                    // The status takes what the buttons leave, cut short as OpenAC's
                    // (`TextTrimming="CharacterEllipsis"`).
                    ui.with_layout(Layout::left_to_right(Align::Center), |ui| {
                        cut(ui, muted(window.operation_status.clone()))
                            .on_hover_text(&window.operation_status);
                    });
                });
            });
            if let Some(error) = window.last_error.clone() {
                ui.label(error_text(error));
            }
        });
}

// --- Dialogs -----------------------------------------------------------------------------

/// A dialog `width` wide at most: as OpenAC's cards (`MaxWidth`, 24 px
/// margin), it narrows with the window.
fn modal(ctx: &egui::Context, id: &str, width: f32, add: impl FnOnce(&mut Ui)) {
    let width = width.min(ctx.content_rect().width() - 2.0 * 24.0 - 2.0 * 16.0);
    egui::Modal::new(egui::Id::new(id))
        .frame(card_frame())
        .show(ctx, |ui| {
            ui.set_width(width);
            add(ui);
        });
}

fn dialog_title(ui: &mut Ui, text: &str) {
    ui.label(RichText::new(text).size(22.0).strong());
}

fn dialogs(app: &mut LauncherApp, ctx: &egui::Context) {
    let window = &mut app.window;
    if window.text_editor.is_open {
        modal(ctx, "text-editor", 820.0, |ui| text_editor(window, ui));
    }
    if window.add_server.is_open {
        modal(ctx, "add-server", 900.0, |ui| add_server(window, ui));
    }
    if window
        .classic_migration
        .as_ref()
        .is_some_and(|dialog| dialog.is_open)
    {
        modal(ctx, "classic-migration", 900.0, |ui| {
            classic_migration(window, ui);
        });
    }
    if window.is_row_options_open {
        modal(ctx, "row-options", 640.0, |ui| row_options(window, ui));
    }
    if window.is_session_log_open {
        modal(ctx, "session-log", 900.0, |ui| session_log(window, ui));
    }
    if window.is_settings_open {
        modal(ctx, "settings", 440.0, |ui| settings(window, ui));
    }
    if window.first_run.is_open {
        modal(ctx, "first-run", 640.0, |ui| first_run(window, ui));
    }
    if window.update_prompt.is_open {
        modal(ctx, "update", 480.0, |ui| update_prompt(window, ui));
    }
    if window.plugins.install_dialog.is_open {
        modal(ctx, "install", 520.0, |ui| install_dialog(window, ui));
    }
    if window.plugins.is_remove_dialog_open {
        modal(ctx, "remove", 440.0, |ui| remove_dialog(window, ui));
    }
    if window.remove_character_target.is_some() {
        modal(ctx, "remove-character", 440.0, |ui| {
            dialog_title(ui, &window.remove_character_title());
            ui.label(muted(window.remove_character_message()));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let remove =
                    egui::Button::new(RichText::new("Remove").strong()).fill(theme::DANGER);
                if ui.add(remove).clicked() {
                    window.confirm_remove_character();
                }
                if ui.button("Cancel").clicked() {
                    window.close_desktop_dialogs();
                }
            });
        });
    }
    if let Some(prompt) = window.close_prompt {
        modal(ctx, "close-launcher", 440.0, |ui| {
            dialog_title(ui, ClosePrompt::TITLE);
            ui.label(muted(prompt.message()));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let close = egui::Button::new(RichText::new("Close").strong()).fill(theme::DANGER);
                if ui.add(close).clicked() {
                    window.confirm_close();
                }
                if ui.button("Cancel").clicked() {
                    window.cancel_close();
                }
            });
        });
    }
    if let Some(notice) = window.profile_migration_notice.clone() {
        modal(ctx, "migration-notice", 640.0, |ui| {
            dialog_title(ui, "Profiles updated");
            egui::ScrollArea::vertical()
                .max_height(360.0)
                .show(ui, |ui| {
                    ui.add(egui::Label::new(notice).selectable(true).wrap());
                });
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if primary(ui, "OK", true, true) {
                    window.dismiss_migration_notice();
                }
            });
        });
    }
}

type Window = ac_launcher_core::window::LauncherWindow;

/// OpenAC's Add a server dialog: your own server, then the known servers.
fn add_server(window: &mut Window, ui: &mut Ui) {
    dialog_title(ui, "Add a server");
    ui.label(muted("Pick a known server, or add your own."));
    ui.add_space(6.0);
    Frame::new()
        .fill(hex("#141E24"))
        .stroke(Stroke::new(1.0, hex("#35434B")))
        .corner_radius(CornerRadius::same(3))
        .inner_margin(Margin::symmetric(12, 10))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(RichText::new("Add your own server").strong().size(13.0));
            ui.horizontal(|ui| {
                ui.vertical(|ui| {
                    ui.label(muted("Name"));
                    ui.add(
                        egui::TextEdit::singleline(&mut window.add_server.name)
                            .hint_text("My local ACE")
                            .desired_width(220.0),
                    );
                });
                ui.vertical(|ui| {
                    ui.label(muted("Host"));
                    ui.add(
                        egui::TextEdit::singleline(&mut window.add_server.host)
                            .hint_text("127.0.0.1")
                            .desired_width(300.0),
                    );
                });
                ui.vertical(|ui| {
                    ui.label(muted("Port"));
                    ui.add(
                        egui::TextEdit::singleline(&mut window.add_server.port).desired_width(90.0),
                    );
                });
                ui.vertical(|ui| {
                    ui.add_space(16.0);
                    if primary(ui, "Add", true, true) {
                        window.add_own_server();
                    }
                });
            });
        });
    if window.add_server.has_error() {
        ui.label(error_text(
            window.add_server.error.clone().unwrap_or_default(),
        ));
    } else if let Some(notice) = window.add_server.notice.clone() {
        ui.label(RichText::new(notice).color(hex("#8FD18F")));
    }
    ui.add_space(6.0);
    ui.horizontal(|ui| {
        ui.label(RichText::new("Known servers").strong().size(13.0));
        ui.label(muted(window.add_server.list_status.clone()));
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            let selected = window.add_server.selected_type().to_owned();
            egui::ComboBox::from_id_salt("server-type")
                .selected_text(&selected)
                .show_ui(ui, |ui| {
                    for kind in window.add_server.types.clone() {
                        if ui.selectable_label(kind == selected, &kind).clicked() {
                            window.add_server.set_selected_type(&kind);
                        }
                    }
                });
            let mut search = window.add_server.search().to_owned();
            if ui
                .add(
                    egui::TextEdit::singleline(&mut search)
                        .hint_text("Search servers")
                        .desired_width(220.0),
                )
                .changed()
            {
                window.add_server.set_search(&search);
            }
        });
    });
    let rows: Vec<_> = window.add_server.servers().into_iter().cloned().collect();
    // The list takes what the window leaves, as OpenAC's stretches.
    let height = (ui.ctx().content_rect().height() - 420.0).clamp(160.0, 640.0);
    let mut add = None;
    Frame::new()
        .stroke(Stroke::new(1.0, theme::BORDER))
        .corner_radius(CornerRadius::same(3))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            Frame::new()
                .fill(theme::CARD)
                .inner_margin(Margin::symmetric(12, 7))
                .show(ui, |ui| {
                    ui.set_width(ui.available_width());
                    known_server_columns(ui, |ui, column| {
                        if let Some(caption) = ["Server", "Type", "Players", "About"].get(column) {
                            ui.label(muted(*caption));
                        }
                    });
                });
            egui::ScrollArea::vertical()
                .id_salt("known-servers")
                .max_height(height)
                .auto_shrink([false, true])
                .show(ui, |ui| {
                    for row in &rows {
                        let top = ui.cursor().top();
                        ui.painter().hline(
                            ui.max_rect().x_range(),
                            top,
                            Stroke::new(1.0, hex("#22303A")),
                        );
                        Frame::new()
                            .inner_margin(Margin::symmetric(12, 7))
                            .show(ui, |ui| {
                                ui.set_width(ui.available_width());
                                known_server_columns(ui, |ui, column| {
                                    if known_server_cell(ui, row, column) {
                                        add = Some(row.name().to_owned());
                                    }
                                });
                            });
                    }
                });
        });
    if let Some(name) = add {
        window.add_known_server(&name);
    }
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if ui.button("Close").clicked() {
            window.add_server.close();
        }
    });
}

/// OpenAC's Import VirindiTank & UtilityBelt dialog: the two classic
/// folders (typed; there is no folder picker), Replace files that differ,
/// the target, the outcome, one line per file, then Close, Preview and
/// Import.
fn classic_migration(window: &mut Window, ui: &mut Ui) {
    let mut run = None;
    if let Some(dialog) = window.classic_migration.as_mut() {
        dialog_title(ui, "Import VirindiTank & UtilityBelt");
        ui.add(
            egui::Label::new(muted(
                "Copies your classic profiles, metas, navs, loot and UtilityBelt profiles to \
                 where MossTank reads them. The original folders are only read.",
            ))
            .wrap(),
        );
        ui.add_space(6.0);
        for (label, value, hint) in [
            (
                "VirindiTank",
                &mut dialog.virindi_tank_path,
                r"C:\Games\VirindiPlugins\VirindiTank",
            ),
            (
                "UtilityBelt",
                &mut dialog.utility_belt_path,
                r"Documents\Decal Plugins\UtilityBelt",
            ),
        ] {
            ui.horizontal(|ui| {
                ui.add_sized([100.0, 18.0], egui::Label::new(label));
                ui.add(
                    egui::TextEdit::singleline(value)
                        .hint_text(hint)
                        .desired_width(ui.available_width()),
                );
            });
        }
        ui.checkbox(&mut dialog.overwrite, "Replace files that differ");
        ui.add(egui::Label::new(muted(format!("Into {}", dialog.target_path().display()))).wrap());
        if dialog.has_error() {
            ui.label(error_text(dialog.error.clone().unwrap_or_default()));
        } else if let Some(status) = &dialog.status {
            ui.add(egui::Label::new(RichText::new(status).color(hex("#8FD18F"))).wrap());
        }
        let height = (ui.ctx().content_rect().height() - 420.0).clamp(160.0, 640.0);
        well(ui, |ui| {
            egui::ScrollArea::both()
                .id_salt("classic-migration-lines")
                .max_height(height)
                .auto_shrink([false, true])
                .show(ui, |ui| {
                    for line in &dialog.lines {
                        ui.add(
                            egui::Label::new(RichText::new(line).monospace().size(12.0))
                                .selectable(true)
                                .extend(),
                        );
                    }
                });
        });
        let can_run = dialog.can_run();
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if primary(ui, "Import", true, can_run) {
                run = Some(true);
            }
            if button(ui, "Preview", can_run) {
                run = Some(false);
            }
            if button(ui, "Close", dialog.can_close()) {
                dialog.close();
            }
        });
    }
    if let Some(apply) = run {
        window.run_classic_migration(apply);
    }
}

/// OpenAC's profile editor dialog: the heading with Copy all and Paste all,
/// the syntax line, the text (or the server rows), the plain-text warning,
/// the errors, then Show passwords (or the logon summary) beside Cancel and
/// Save.
/// The known servers table's fixed columns as OpenAC's: Server, Type,
/// Players, then About takes the rest, then the Add column; 12 apart.
const KNOWN_SERVER_WIDTHS: [f32; 5] = [188.0, 60.0, 70.0, 0.0, 90.0];
const KNOWN_SERVER_SPACING: f32 = 12.0;

/// Lays out one row of the known servers table: `cell` draws column
/// `0..5` in a cell of that column's width, so every row lines up with the
/// heading whatever its text.
fn known_server_columns(ui: &mut Ui, mut cell: impl FnMut(&mut Ui, usize)) {
    let fixed: f32 = KNOWN_SERVER_WIDTHS.iter().sum::<f32>()
        + KNOWN_SERVER_SPACING * (KNOWN_SERVER_WIDTHS.len() - 1) as f32;
    let about = (ui.available_width() - fixed).max(80.0);
    ui.horizontal_top(|ui| {
        ui.spacing_mut().item_spacing.x = KNOWN_SERVER_SPACING;
        for (column, width) in KNOWN_SERVER_WIDTHS.into_iter().enumerate() {
            let width = if column == 3 { about } else { width };
            ui.allocate_ui_with_layout(Vec2::new(width, 0.0), Layout::top_down(Align::Min), |ui| {
                ui.set_width(width);
                cell(ui, column);
            });
        }
    });
}

/// One cell of a known server's row; true when its Add was pressed. Text
/// that does not fit is cut short with an ellipsis and shown whole on hover.
fn known_server_cell(
    ui: &mut Ui,
    row: &ac_launcher_core::window::add_server::KnownServerRow,
    column: usize,
) -> bool {
    // A single line centred against the two-line name and address.
    const LINE_OFFSET: f32 = 8.0;
    match column {
        0 => {
            ui.spacing_mut().item_spacing.y = 1.0;
            ui.add(egui::Label::new(RichText::new(row.name()).strong().size(13.0)).truncate());
            ui.add(
                egui::Label::new(RichText::new(row.endpoint()).size(11.0).color(theme::MUTED))
                    .truncate(),
            )
            .on_hover_text(row.endpoint());
        }
        1 => {
            ui.add_space(LINE_OFFSET);
            ui.add(egui::Label::new(muted(row.server_type())).truncate());
        }
        2 => {
            ui.add_space(LINE_OFFSET);
            ui.add(egui::Label::new(RichText::new(row.players()).size(13.0)).truncate());
        }
        3 => {
            let links = [("Website", row.website()), ("Discord", row.discord())];
            let has_links = links.iter().any(|(_, link)| link.is_some());
            if !has_links {
                ui.add_space(LINE_OFFSET);
            }
            ui.spacing_mut().item_spacing.y = 1.0;
            ui.add(egui::Label::new(muted(row.about())).truncate())
                .on_hover_text(row.about());
            if has_links {
                ui.horizontal(|ui| {
                    ui.spacing_mut().item_spacing.x = 10.0;
                    for (text, link) in links {
                        if let Some(link) = link {
                            ui.hyperlink_to(
                                RichText::new(text).size(11.0).color(theme::GOLD),
                                link.to_string(),
                            );
                        }
                    }
                });
            }
        }
        _ => {
            ui.add_space(4.0);
            if row.is_added {
                ui.with_layout(Layout::right_to_left(Align::Min), |ui| {
                    ui.label(RichText::new("Added ✓").size(12.0).color(hex("#8FD18F")));
                });
            } else {
                let label = RichText::new("Add").strong().color(theme::ON_GOLD);
                return ui
                    .add_sized(
                        [ui.available_width(), 24.0],
                        egui::Button::new(label).fill(theme::GOLD),
                    )
                    .clicked();
            }
        }
    }
    false
}

fn text_editor(window: &mut Window, ui: &mut Ui) {
    profile_editor_body(&mut window.text_editor, ui);
    let editor = &mut window.text_editor;
    let mut save = false;
    ui.horizontal(|ui| {
        if editor.is_accounts_editor() {
            ui.checkbox(&mut editor.show_passwords, "Show passwords")
                .on_hover_text("Passwords stay hidden, and the text read-only, until this is on");
            if editor.is_password_hidden() {
                // The box takes no typing while passwords are hidden; say so where it shows.
                ui.label(
                    RichText::new("The text is read-only while passwords are hidden.")
                        .size(12.0)
                        .color(theme::GOLD),
                );
            }
        } else if editor.is_logon_commands_editor() {
            ui.label(
                RichText::new(editor.summary())
                    .size(12.0)
                    .color(hex("#8FD18F")),
            );
        }
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            save = primary(ui, editor.save_text(), true, true);
            if ui.button("Cancel").clicked() {
                editor.close();
            }
        });
    });
    if save {
        window.save_text_editor();
    }
}

/// Everything in the profile editor above its bottom row. Returns the text
/// box, when the editor is a text editor.
fn profile_editor_body(
    editor: &mut ac_launcher_core::window::profile_editor::ProfileTextEditor,
    ui: &mut Ui,
) -> Option<egui::Response> {
    ui.horizontal(|ui| {
        let buttons = if editor.is_text_editor() { 190.0 } else { 0.0 };
        ui.vertical(|ui| {
            ui.set_width(ui.available_width() - buttons);
            dialog_title(ui, &editor.title);
            ui.add(egui::Label::new(muted(editor.help_text.clone())).wrap());
        });
        if editor.is_text_editor() {
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                // Copy all and Paste all go through the system clipboard.
                if ui
                    .button("Paste all")
                    .on_hover_text(
                        "Replaces the text with what is on the clipboard; nothing is saved until \
                         Save",
                    )
                    .clicked()
                    && let Some(text) = crate::shell::read_clipboard_text()
                {
                    editor.set_text(&text);
                }
                if ui.button("Copy all").clicked() {
                    ui.ctx().copy_text(editor.text().to_owned());
                }
            });
        }
    });
    ui.add_space(4.0);
    // The text area takes what the window leaves, as OpenAC's stretches.
    let height = (ui.ctx().content_rect().height() - 330.0).clamp(160.0, 640.0);
    let mut text_box = None;
    if editor.is_fields_editor() {
        let (first, second) = (editor.first_column_label(), editor.second_column_label());
        let column = (ui.available_width() - 100.0) / 2.0;
        // The headings sit over the left edge of their fields, as OpenAC's.
        ui.horizontal(|ui| {
            for heading in [first, second] {
                ui.allocate_ui_with_layout(
                    Vec2::new(column - 8.0, 16.0),
                    Layout::left_to_right(Align::Center),
                    |ui| {
                        ui.set_width(column - 8.0);
                        ui.label(heading);
                    },
                );
            }
        });
        let mut remove = None;
        egui::ScrollArea::vertical()
            .max_height(height)
            .show(ui, |ui| {
                for (index, row) in editor.rows.iter_mut().enumerate() {
                    let placeholder = row.placeholder();
                    ui.horizontal(|ui| {
                        ui.add(
                            egui::TextEdit::singleline(&mut row.name).desired_width(column - 8.0),
                        );
                        ui.add(
                            egui::TextEdit::singleline(&mut row.value)
                                .hint_text(placeholder)
                                .desired_width(column - 8.0),
                        );
                        if ui.button("Remove").clicked() {
                            remove = Some(index);
                        }
                    });
                }
            });
        if let Some(index) = remove {
            editor.remove_row(index);
        }
        if ui.button(editor.add_row_text()).clicked() {
            editor.add_row();
        }
    } else {
        syntax_line(editor, ui);
        let hidden = editor.is_password_hidden();
        let mut shown = editor.displayed_text();
        egui::ScrollArea::vertical()
            .id_salt("profile-text")
            .max_height(height)
            .show(ui, |ui| {
                // While passwords are hidden the box is read-only, so a
                // hidden password is never saved; its text is dimmed to show it.
                let mut edit = egui::TextEdit::multiline(&mut shown)
                    .id_salt("profile-text-box")
                    .code_editor()
                    .interactive(!hidden)
                    .desired_width(f32::INFINITY)
                    .min_size(Vec2::new(0.0, height));
                if hidden {
                    edit = edit.text_color(theme::MUTED);
                }
                text_box = Some(ui.add(edit));
            });
        if !hidden {
            editor.set_text(&shown);
        }
    }
    if editor.is_accounts_editor() {
        Frame::new()
            .fill(theme::BANNER)
            .stroke(Stroke::new(1.0, hex("#5B4D30")))
            .corner_radius(CornerRadius::same(3))
            .inner_margin(Margin::symmetric(10, 8))
            .show(ui, |ui| {
                ui.set_width(ui.available_width());
                ui.label(
                    RichText::new(
                        "⚠  Passwords are shown and stored in plain text on this computer. Don't \
                         paste this into a public chat.",
                    )
                    .size(12.0)
                    .color(hex("#E6DCC6")),
                );
            });
    }
    if let Some(error) = &editor.error {
        ui.add(egui::Label::new(error_text(error)).selectable(true).wrap());
    }
    if let Some(warning) = editor.removal_warning() {
        Frame::new()
            .fill(theme::BANNER)
            .stroke(Stroke::new(1.0, theme::GOLD))
            .corner_radius(CornerRadius::same(3))
            .inner_margin(Margin::symmetric(10, 8))
            .show(ui, |ui| {
                ui.set_width(ui.available_width());
                ui.add(
                    egui::Label::new(RichText::new(warning).size(12.0).color(hex("#E6DCC6")))
                        .selectable(true)
                        .wrap(),
                );
            });
    }
    text_box
}

/// The line over the text naming its syntax.
fn syntax_line(editor: &ac_launcher_core::window::profile_editor::ProfileTextEditor, ui: &mut Ui) {
    let parts: &[(&str, Color32, &str)] = if editor.is_accounts_editor() {
        &[
            ("#Server", theme::GOLD, " starts a server"),
            ("Name=…,Password=…", theme::GOLD, " one account per line"),
            (",Profiles=Main;Bots", theme::GOLD, " optional tags"),
        ]
    } else {
        &[
            ("#Server", theme::GOLD, " starts a server"),
            ("##Account", hex("#9FC7E8"), " starts an account on it"),
            (
                "/command",
                hex("#E6DCC6"),
                " every other line, run at login in this order",
            ),
            ("\\#…", hex("#E6DCC6"), " a command that starts with #"),
        ]
    };
    Frame::new()
        .fill(hex("#111B21"))
        .inner_margin(Margin::symmetric(10, 6))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.horizontal_wrapped(|ui| {
                for (code, colour, text) in parts {
                    ui.spacing_mut().item_spacing.x = 0.0;
                    ui.label(RichText::new(*code).monospace().size(12.0).color(*colour));
                    ui.label(muted(*text));
                    ui.add_space(18.0);
                }
            });
        });
}

fn row_options(window: &mut Window, ui: &mut Ui) {
    dialog_title(ui, &window.row_options_title());
    ui.label(muted(window.row_options_plugins_caption()));
    if window.show_character_plugin_choice() {
        ui.checkbox(
            &mut window.character_uses_account_plugins,
            "Use the account's plugins",
        );
    }
    let editable = window.can_edit_plugin_choices();
    egui::ScrollArea::vertical()
        .id_salt("row-options-plugins")
        .max_height(220.0)
        .show(ui, |ui| {
            ui.add_enabled_ui(editable, |ui| {
                for choice in &mut window.row_options_choices {
                    ui.checkbox(&mut choice.is_checked, &choice.display_name);
                }
            });
        });
    if let Some(error) = &window.last_error {
        ui.label(error_text(error));
    }
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if primary(ui, "Save", true, !window.is_busy()) {
            window.save_row_options();
        }
        if ui.button("Cancel").clicked() {
            window.close_desktop_dialogs();
        }
    });
}

fn session_log(window: &mut Window, ui: &mut Ui) {
    dialog_title(ui, "Sessions");
    egui::ScrollArea::vertical()
        .max_height(420.0)
        .show(ui, |ui| {
            if window.sessions.is_empty() {
                ui.label(muted("No sessions yet."));
            }
            for session in &window.sessions {
                ui.horizontal(|ui| {
                    ui.add_sized([160.0, 18.0], egui::Label::new(&session.account));
                    ui.add_sized([160.0, 18.0], egui::Label::new(&session.character));
                    ui.vertical(|ui| {
                        ui.label(format!("{} · {}", session.state, session.status));
                        if let Some(error) =
                            session.error.as_deref().filter(|_| session.has_error())
                        {
                            ui.label(error_text(error));
                        }
                        if let Some(notice) = &session.plugin_notice {
                            ui.label(muted(notice));
                        }
                    });
                });
                ui.separator();
            }
        });
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if ui.button("Close").clicked() {
            window.close_desktop_dialogs();
        }
    });
}

fn settings(window: &mut Window, ui: &mut Ui) {
    dialog_title(ui, "Launcher settings");
    let mut beta = window.plugins.show_beta_plugins;
    if ui.checkbox(&mut beta, "Show beta plugins").changed() {
        window.set_show_beta_plugins(beta);
    }
    Frame::new()
        .inner_margin(Margin {
            left: 28,
            right: 0,
            top: 0,
            bottom: 0,
        })
        .show(ui, |ui| {
            ui.add(
                egui::Label::new(muted(
                    "Show development and testing versions of plugins for download. Beta \
                     plugins are more likely to contain bugs and could create issues for your \
                     characters. Use with caution.",
                ))
                .wrap(),
            );
        });
    install_folder(window, ui);
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if ui.button("Close").clicked() {
            window.close_desktop_dialogs();
        }
    });
}

/// The settings' Install folder section: where everything lives, a row
/// per folder that opens in the system's file manager (or offers its path
/// to copy when it will not open), and Move... to a typed folder.
fn install_folder(window: &mut Window, ui: &mut Ui) {
    let can_move = window.can_move_install();
    let Some(panel) = window.install_folder.as_mut() else {
        return;
    };
    ui.add_space(10.0);
    ui.label(RichText::new("Install folder").strong());
    let mut rows: Vec<(Option<usize>, &mut InstallFolderRow)> = vec![(None, &mut panel.root)];
    rows.extend(
        panel
            .folders
            .iter_mut()
            .enumerate()
            .map(|(i, row)| (Some(i), row)),
    );
    for (_, row) in rows {
        ui.horizontal(|ui| {
            ui.label(format!("{}:", row.label));
            ui.label(muted(row.path.display().to_string()));
            let open = egui::Button::new("Open folder");
            if ui.add(open).clicked() {
                let opened = row.prepare_open() && crate::shell::open_folder(&row.path);
                row.open_failed = !opened;
            }
            if row.open_failed && ui.button("Copy path").clicked() {
                ui.ctx().copy_text(row.path.display().to_string());
            }
        });
        if row.open_failed {
            ui.label(error_text(InstallFolderRow::OPEN_FAILED_TEXT));
        }
    }
    if let Some(reason) = panel.move_unavailable_reason() {
        ui.label(muted(reason));
        return;
    }
    let move_clicked = ui
        .horizontal(|ui| {
            ui.add(
                egui::TextEdit::singleline(&mut panel.move_target)
                    .hint_text("New folder for the whole install")
                    .desired_width(320.0),
            );
            ui.add_enabled(can_move, egui::Button::new("Move…"))
                .clicked()
        })
        .inner;
    if move_clicked {
        window.move_install();
    }
    let Some(panel) = window.install_folder.as_ref() else {
        return;
    };
    if let Some(status) = &panel.move_status {
        ui.label(status.clone());
    }
    if let Some(error) = &panel.move_error {
        ui.label(error_text(error));
    }
}

fn first_run(window: &mut Window, ui: &mut Ui) {
    if window.first_run.is_completed {
        dialog_title(ui, window.first_run.completed_title());
        ui.label(window.first_run.completed_body());
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if primary(ui, "OK", true, true) {
                window.first_run.acknowledge_completion();
            }
        });
        return;
    }
    dialog_title(ui, window.first_run.title());
    ui.label(window.first_run.body());
    ui.add_space(6.0);
    ui.label(RichText::new("Retail DAT directory").strong());
    let mut directory = window.first_run.dat_directory.clone();
    if ui
        .add(
            egui::TextEdit::singleline(&mut directory)
                .hint_text("Path containing client_portal.dat")
                .desired_width(f32::INFINITY),
        )
        .changed()
    {
        window.set_first_run_directory(&directory);
    }
    ui.label(window.first_run.validation_status.clone());
    if !window.first_run.missing_files.is_empty() {
        ui.label(error_text(window.first_run.missing_files.clone()));
    }
    Frame::new()
        .fill(theme::PROGRESS_BOX)
        .corner_radius(CornerRadius::same(5))
        .inner_margin(Margin::same(10))
        .show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.label(window.first_run.status.clone());
        });
    if let Some(error) = &window.first_run.error {
        ui.label(error_text(error));
    }
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if primary(
            ui,
            window.first_run.start_action_text(),
            true,
            window.first_run.can_start(),
        ) {
            window.start_first_run();
        }
        if ui.button("Close").clicked() {
            window.first_run.close();
        }
        if ui.button("Validate").clicked() {
            window.validate_first_run();
        }
    });
}

fn update_prompt(window: &mut Window, ui: &mut Ui) {
    dialog_title(ui, window.update_prompt.title());
    ui.label(window.update_prompt.body());
    if window.has_active_sessions() {
        ui.label(
            RichText::new("Close your active sessions before installing the update.")
                .color(theme::GOLD),
        );
    }
    if window.update_prompt.is_busy {
        Frame::new()
            .fill(theme::PROGRESS_BOX)
            .inner_margin(Margin::same(10))
            .show(ui, |ui| {
                ui.label(window.update_prompt.status.clone());
                let progress = if window.update_prompt.is_progress_indeterminate {
                    egui::ProgressBar::new(0.0).animate(true)
                } else {
                    egui::ProgressBar::new((window.update_prompt.progress_percent / 100.0) as f32)
                };
                ui.add(progress);
            });
    } else {
        ui.label(muted(window.update_prompt.status.clone()));
    }
    if let Some(error) = &window.update_prompt.error {
        Frame::new()
            .fill(theme::ERROR_BOX)
            .inner_margin(Margin::same(10))
            .show(ui, |ui| {
                ui.label(error);
            });
    }
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        let can_update = !window.update_prompt.is_busy
            && window.is_lifecycle_idle()
            && window.update_prompt.has_something_to_update();
        if primary(ui, "Update", true, can_update) {
            window.install_update();
        }
        if button(ui, "Not now", !window.update_prompt.is_busy) {
            window.update_prompt.close();
        }
        if window.update_prompt.is_busy && ui.button("Cancel").clicked() {
            window.cancel_update();
        }
    });
}

fn install_dialog(window: &mut Window, ui: &mut Ui) {
    let dialog = &mut window.plugins.install_dialog;
    dialog_title(ui, dialog.display_name());
    if let Some(request) = &dialog.request {
        ui.label(muted(request.repo.clone()));
    }
    ui.label(dialog.warning_text());
    if dialog.is_loading_permissions {
        ui.label(muted(dialog.loading_permissions_text()));
    }
    if let Some(error) = &dialog.permissions_load_error {
        ui.label(error_text(error));
    }
    if !dialog.permissions.is_empty() {
        ui.label(RichText::new(dialog.permissions_heading()).strong());
        // As OpenAC's: the chips share the widest chip's column, and each
        // note wraps in the rest of the dialog's width.
        let chip_width = dialog
            .permissions
            .iter()
            .map(|chip_view| {
                ui.fonts_mut(|fonts| {
                    fonts
                        .layout_no_wrap(
                            chip_view.label.clone(),
                            egui::FontId::proportional(11.0),
                            theme::SECONDARY,
                        )
                        .size()
                        .x
                })
            })
            .fold(0.0f32, f32::max)
            + 2.0 * 7.0
            + 1.0;
        for chip_view in &dialog.permissions {
            ui.horizontal_top(|ui| {
                ui.allocate_ui_with_layout(
                    Vec2::new(chip_width, 0.0),
                    Layout::top_down(Align::Min),
                    |ui| {
                        ui.set_width(chip_width);
                        chip(
                            ui,
                            &chip_view.label,
                            theme::BUTTON,
                            if chip_view.is_new {
                                theme::GOLD
                            } else {
                                theme::SECONDARY
                            },
                        );
                    },
                );
                ui.add(egui::Label::new(muted(chip_view.note.clone())).wrap());
            });
        }
    }
    if let Some(text) = dialog.no_permissions_text() {
        ui.label(muted(text));
    }
    if dialog.show_enable_choice() {
        ui.label(RichText::new("Enable for").strong());
        ui.radio_value(
            &mut dialog.choice,
            PluginEnableChoice::None,
            "None (enable later)",
        );
        ui.radio_value(
            &mut dialog.choice,
            PluginEnableChoice::All,
            "All characters",
        );
        ui.radio_value(
            &mut dialog.choice,
            PluginEnableChoice::Choose,
            "Choose characters",
        );
        if dialog.choice == PluginEnableChoice::Choose {
            for character in &mut dialog.characters {
                ui.horizontal(|ui| {
                    ui.add_space(20.0);
                    ui.checkbox(&mut character.is_checked, &character.option.display_name);
                });
            }
        }
    }
    if dialog.show_keep_enabled_choice() {
        ui.label(RichText::new("New permissions").strong());
        ui.label(dialog.affected_characters_text());
        ui.checkbox(&mut dialog.keep_enabled, "Keep enabled");
    }
    if let Some(error) = &dialog.error {
        ui.label(error_text(error));
    }
    let (can_confirm, busy, confirm, busy_label) = (
        dialog.can_confirm(),
        dialog.is_busy,
        dialog.confirm_label(),
        dialog.busy_label(),
    );
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        if busy {
            ui.add(egui::Spinner::new());
            ui.label(busy_label);
        } else if primary(ui, confirm, true, can_confirm) {
            window.confirm_install();
        }
        if button(ui, "Cancel", !busy) {
            window.close_install_dialog();
        }
    });
}

fn remove_dialog(window: &mut Window, ui: &mut Ui) {
    let name = window
        .plugins
        .remove_target
        .as_ref()
        .map(|target| target.display_name.clone())
        .unwrap_or_default();
    dialog_title(ui, &name);
    ui.label(muted(
        "This removes the plugin's code. Its saved files are kept unless you tick the box \
         below; Vtank profiles are always kept.",
    ));
    ui.checkbox(
        &mut window.plugins.remove_delete_storage,
        "Also delete its saved files",
    );
    if let Some(error) = &window.plugins.error {
        ui.label(error_text(error));
    }
    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
        let remove = egui::Button::new(RichText::new("Remove").strong()).fill(theme::DANGER);
        if ui.add_enabled(!window.plugins.is_busy, remove).clicked() {
            window.confirm_remove();
        }
        if button(ui, "Cancel", !window.plugins.is_busy) {
            window.plugins.is_remove_dialog_open = false;
        }
    });
}

// --- Consoles ----------------------------------------------------------------------------

/// A windowless session's console. OpenAC opens a window of its own; here
/// it floats inside the launcher.
fn consoles(app: &mut LauncherApp, ctx: &egui::Context) {
    let window = &mut app.window;
    let mut send = None;
    for (index, console) in window.consoles.iter_mut().enumerate() {
        let mut open = console.is_open;
        egui::Window::new(console.title.clone())
            .id(egui::Id::new(("console", &console.session_id)))
            .open(&mut open)
            .default_size([720.0, 420.0])
            .show(ctx, |ui| {
                egui::ScrollArea::vertical()
                    .stick_to_bottom(true)
                    .auto_shrink([false, false])
                    .max_height(ui.available_height() - 36.0)
                    .show(ui, |ui| {
                        for runs in console.lines() {
                            ui.horizontal_wrapped(|ui| {
                                ui.spacing_mut().item_spacing.x = 0.0;
                                for run in runs {
                                    let color = if run.dim {
                                        Color32::from_rgb(0x7A, 0x88, 0x90)
                                    } else {
                                        run.color.map_or(theme::TEXT, theme::rgb)
                                    };
                                    ui.label(RichText::new(run.text).monospace().color(color));
                                }
                            });
                        }
                    });
                ui.horizontal(|ui| {
                    let response = ui.add(
                        egui::TextEdit::singleline(&mut console.input)
                            .desired_width(ui.available_width() - 70.0),
                    );
                    let entered = response.lost_focus()
                        && ui.input(|input| input.key_pressed(egui::Key::Enter));
                    if (ui
                        .add_enabled(console.can_send(), egui::Button::new("Send"))
                        .clicked()
                        || entered)
                        && console.can_send()
                    {
                        send = Some(index);
                    }
                });
                if !console.notice.is_empty() {
                    ui.label(error_text(console.notice.clone()));
                }
            });
        console.is_open = open;
    }
    if let Some(index) = send {
        window.send_console_line(index);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_launcher_core::window::profile_editor::ProfileTextEditor;

    /// Runs frames of the profile editor body in a headless egui context,
    /// feeding each frame its events.
    struct Harness {
        ctx: egui::Context,
        time: f64,
    }

    impl Harness {
        fn new() -> Self {
            Self {
                ctx: egui::Context::default(),
                time: 0.0,
            }
        }

        fn frame(
            &mut self,
            editor: &mut ProfileTextEditor,
            events: Vec<egui::Event>,
        ) -> Option<egui::Rect> {
            self.time += 0.1;
            let raw = egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    Vec2::new(1120.0, 740.0),
                )),
                time: Some(self.time),
                focused: true,
                events,
                ..Default::default()
            };
            let mut rect = None;
            let mut output = self.ctx.run_ui(raw, |root| {
                let ctx = root.ctx().clone();
                egui::CentralPanel::default().show(root, |ui| ui.label("behind"));
                // The same modal the launcher draws the dialog in.
                modal(&ctx, "text-editor", 820.0, |ui| {
                    rect = profile_editor_body(editor, ui).map(|response| response.rect);
                });
            });
            // No renderer here: the font atlas upload is dropped on purpose.
            output.textures_delta.clear();
            rect
        }

        /// Clicks into the text box and types `text`.
        fn type_into_text_box(&mut self, editor: &mut ProfileTextEditor, text: &str) {
            let rect = self.frame(editor, Vec::new()).expect("a text box");
            let at = rect.center();
            let button = |pressed| egui::Event::PointerButton {
                pos: at,
                button: egui::PointerButton::Primary,
                pressed,
                modifiers: egui::Modifiers::NONE,
            };
            self.frame(editor, vec![egui::Event::PointerMoved(at), button(true)]);
            self.frame(editor, vec![button(false)]);
            self.frame(editor, vec![egui::Event::Text(text.to_owned())]);
            self.frame(editor, Vec::new());
        }
    }

    /// An account with one server row whose character is `character`.
    fn account(name: &str, character: &str, status: &str) -> AccountGroup {
        let mut row = AccountServerRow::new(name, "Coldeve");
        row.character_choices.push(character.to_owned());
        row.selected_character = character.to_owned();
        row.status = status.to_owned();
        row.is_server_online = Some(true);
        row.server_status_text = "579 players".into();
        row.latency_milliseconds = Some(223.0);
        row.is_checked = true;
        let mut group = AccountGroup::new(name);
        group.server_name = "Coldeve".into();
        group.subtitle = "Coldeve · 1 character".into();
        group.plugins_summary = "MossTank · Mosswart Massacre".into();
        group.servers.push(row);
        group
    }

    /// Lays the account list out in a window `width` wide, as the launcher
    /// does (the tab's 14 px margins), and returns where things went and the
    /// list's right edge.
    fn account_layout(groups: &[AccountGroup], width: f32) -> (AccountLayout, f32) {
        let ctx = egui::Context::default();
        crate::theme::apply(&ctx);
        let mut result = (AccountLayout::default(), 0.0);
        // A few passes, as a window draws them, so sizes have settled.
        for pass in 0..3 {
            let raw = egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    Vec2::new(width, 740.0),
                )),
                time: Some(f64::from(pass) * 0.1),
                ..Default::default()
            };
            let mut output = ctx.run_ui(raw, |root| {
                egui::CentralPanel::default()
                    .frame(Frame::new())
                    .show(root, |ui| {
                        Frame::new()
                            .inner_margin(Margin::symmetric(14, 4))
                            .show(ui, |ui| {
                                let right = ui.max_rect().right();
                                let states = |_: &AccountServerRow| RowState {
                                    interact: true,
                                    can_stop: true,
                                    has_logs: true,
                                };
                                let mut layout = AccountLayout::default();
                                account_list(ui, groups, true, true, &states, &mut layout);
                                result = (layout, right);
                            });
                    });
            });
            output.textures_delta.clear();
        }
        result
    }

    // A report (about 1100 px wide, five accounts): the
    // columns did not line up from row to row and the right of the rows was
    // cut off. OpenAC repeats one grid, `26,*,160,112,*,64,92` 6 apart
    // (MainWindow.axaml), in the header and every row, and its text is cut
    // short with an ellipsis.
    #[test]
    fn account_rows_share_the_header_columns_and_fit_the_window() {
        let groups = [
            account("scout1", "Fisher of Lakes", "In world."),
            account("scout2", "Wanderer of the North Hills", "In world."),
            account("scout3", "Somebody Middling Long", "In world."),
            account(
                "an account with a very long name indeed",
                "Somebody with the longest character name the server allows",
                "Waiting for the server to answer the login request",
            ),
            account("scout5", "Al", "Ready"),
        ];
        // OpenAC's default width, the reported one, and the minimum (MinWidth 760).
        for width in [1120.0, 1100.0, 760.0] {
            let (layout, right) = account_layout(&groups, width);
            assert_eq!(layout.rows.len(), groups.len(), "{width}");
            assert_eq!(layout.header.len(), 4, "{width}");
            let first = layout.rows[0];
            for (index, row) in layout.rows.iter().enumerate() {
                for column in 0..7 {
                    assert_eq!(
                        row[column].left(),
                        first[column].left(),
                        "width {width}: row {index} column {column} starts elsewhere"
                    );
                }
                for column in 0..6 {
                    assert!(
                        row[column].right() <= row[column + 1].left(),
                        "width {width}: row {index} column {column} runs into the next: {row:?}"
                    );
                }
                assert!(
                    row[6].right() <= right,
                    "width {width}: row {index} ends at {} past the list's {right}",
                    row[6].right()
                );
            }
            for (header, column) in layout.header.iter().zip(1..) {
                assert_eq!(
                    header.left(),
                    first[column].left(),
                    "width {width}: header {column} is not over its column"
                );
            }
            for group in &layout.groups {
                assert!(
                    group.right() <= right,
                    "width {width}: a group header ends at {} past {right}",
                    group.right()
                );
            }
            // The fixed columns keep OpenAC's widths.
            assert_eq!(first[2].width(), 160.0, "{width}");
            assert_eq!(first[5].width(), 64.0, "{width}");
            assert_eq!(first[6].width(), 92.0, "{width}");
        }
    }

    #[test]
    fn account_columns_are_openacs_grid() {
        let columns = AccountColumns::new(egui::Rangef::new(0.0, 700.0));
        // 26 + 160 + 112 + 64 + 92 + 6 gaps of 6 = 490; two stars of 105.
        let widths: Vec<f32> = columns.0.iter().map(|c| c.span()).collect();
        assert_eq!(widths, [26.0, 105.0, 160.0, 112.0, 105.0, 64.0, 92.0]);
        assert_eq!(columns.0[1].min, 32.0);
        assert_eq!(columns.0[6].max, 700.0);
    }

    fn accounts_editor() -> ProfileTextEditor {
        let mut editor = ProfileTextEditor::default();
        assert!(editor.is_accounts_editor());
        editor.set_text("#Local\nName=fakeuser,Password=fakepass\n");
        editor
    }

    // A reported bug: nothing in Edit accounts took typing.
    // OpenAC keeps the text read-only while passwords are hidden
    // (ProfileTextEditorViewModel.IsPasswordHidden); with them shown, what
    // is typed reaches the editor's text.
    #[test]
    fn typing_in_the_accounts_text_reaches_the_editor_once_passwords_are_shown() {
        let mut harness = Harness::new();
        let mut editor = accounts_editor();
        let before = editor.text().to_owned();

        harness.type_into_text_box(&mut editor, "X");
        assert_eq!(
            editor.text(),
            before,
            "read-only while passwords are hidden"
        );

        editor.show_passwords = true;
        harness.type_into_text_box(&mut editor, "X");
        assert_ne!(editor.text(), before);
        assert!(editor.text().contains('X'), "{:?}", editor.text());
        assert!(editor.text().contains("Password=fakepass"));
    }
}
