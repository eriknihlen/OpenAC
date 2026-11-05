//! The floating chat windows and the chat windows' opacity.
//!
//! Ported from OpenAC `tests/AcDream.App.Tests/UI/Layout/FloatingChatWindowControllerTests.cs`,
//! `FloatingChatLayoutConformanceTests.cs` (the real-dat module) and
//! `tests/AcDream.App.Tests/UI/RetailWindowOpacityControllerTests.cs`, on a
//! synthetic layout shaped like retail's 0x2100005B. OpenAC's command bus
//! is [`PanelOutput`] here.

mod common;

use std::sync::Arc;

use ac_core::chat::{ChatWindowId, ChatWindowState};
use ac_dat::objects::{DIRECT_STATE_ID, FontCharDesc, PropertyValue};
use ac_runtime::communication::ChatChannelKind;
use ac_runtime::session::SessionCommand;
use ac_ui::MouseButton;
use ac_ui::draw::{NoSprites, TextureRef};
use ac_ui::element_info::{ElementInfo, StateInfo};
use ac_ui::event::{Key, Modifiers};
use ac_ui::font::{Atlas, UiFont};
use ac_ui::host::UiHost;
use ac_ui::import::{BuildContext, ImportedLayout, LayoutSource, build};
use ac_ui::panels::PanelOutput;
use ac_ui::panels::floating_chat::{self, FloatingChatController};
use ac_ui::panels::transcript::TranscriptLine;
use ac_ui::root::{UiOutput, UiRoot};
use ac_ui::widgets::{Button, Field, Text};
use common::panels::text_of;
use common::*;

#[derive(Debug, Default)]
struct NoSource;

impl LayoutSource for NoSource {
    fn layout(&self, _id: u32) -> Option<Arc<ac_dat::objects::LayoutDesc>> {
        None
    }
}

fn info(id: u32, kind: u32, x: f32, y: f32, w: f32, h: f32) -> ElementInfo {
    ElementInfo {
        id,
        kind,
        x,
        y,
        width: w,
        height: h,
        ..ElementInfo::default()
    }
}

fn with(mut parent: ElementInfo, children: Vec<ElementInfo>) -> ElementInfo {
    parent.children = children;
    parent
}

fn editable(mut i: ElementInfo) -> ElementInfo {
    let mut s = StateInfo::named(DIRECT_STATE_ID, "");
    s.properties.set_value(0x16, PropertyValue::Bool(true));
    s.properties.set_value(0x27, PropertyValue::Bool(true));
    s.properties.set_value(0x20, PropertyValue::Bool(true));
    i.set_state(DIRECT_STATE_ID, s);
    i
}

fn build_layout(ui: &mut UiRoot, info: &ElementInfo) -> ImportedLayout {
    let ctx = BuildContext {
        source: &NoSource,
        default_font: None,
        source_layout_did: 0,
        media_seconds: 0.0,
    };
    build(ui, info, &ctx)
}

/// A 6-pixel monospace font, 10 pixels high.
fn font(id: u32) -> Arc<UiFont> {
    let glyphs = (0x20u16..0x7F).map(|c| FontCharDesc {
        unicode: c,
        offset_x: 0,
        offset_y: 0,
        width: 6,
        height: 10,
        horizontal_offset_before: 0,
        horizontal_offset_after: 0,
        vertical_offset_before: 0,
    });
    Arc::new(UiFont::new(
        id,
        Atlas {
            texture: TextureRef::Surface(0x0600_0001),
            width: 256,
            height: 256,
        },
        None,
        10.0,
        8.0,
        glyphs,
        0,
        0,
    ))
}

/// OpenAC's test tree: the transcript panel, the entry row, the title and
/// the close button under a 250 x 108 root.
fn floaty_info() -> ElementInfo {
    with(
        info(floating_chat::ROOT_ID, 3, 0.0, 0.0, 250.0, 108.0),
        vec![
            with(
                info(
                    floating_chat::TRANSCRIPT_PANEL_ID,
                    3,
                    0.0,
                    20.0,
                    250.0,
                    60.0,
                ),
                vec![
                    info(floating_chat::TRANSCRIPT_ID, 12, 5.0, 0.0, 224.0, 60.0),
                    info(floating_chat::TRACK_ID, 11, 229.0, 0.0, 16.0, 60.0),
                ],
            ),
            with(
                info(floating_chat::INPUT_ROW_ID, 3, 0.0, 80.0, 250.0, 18.0),
                vec![
                    editable(info(floating_chat::INPUT_ID, 12, 0.0, 0.0, 202.0, 18.0)),
                    info(floating_chat::SEND_ID, 1, 202.0, 0.0, 38.0, 18.0),
                ],
            ),
            info(floating_chat::TITLE_BAR_ID, 12, 0.0, 0.0, 240.0, 16.0),
            info(floating_chat::CLOSE_BUTTON_ID, 1, 225.0, 1.0, 14.0, 14.0),
        ],
    )
}

fn window(id: i32) -> ChatWindowId {
    ChatWindowId::new(id).unwrap()
}

fn name(id: i32) -> String {
    format!("chat-window-{id}")
}

/// A host with floating window `id` mounted and shown.
fn floaty_host(id: i32) -> (UiHost, ImportedLayout) {
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let layout = build_layout(&mut host.root, &floaty_info());
    let r = host.root.root();
    host.root.add_child(r, layout.root);
    host.root
        .register_window(&name(id), layout.root, None, 0)
        .unwrap();
    let c = FloatingChatController::bind(
        &mut host.root,
        &layout,
        window(id),
        &name(id),
        Some(font(0x4000_0001)),
    )
    .unwrap();
    host.attach_controller(&name(id), Box::new(c)).unwrap();
    (host, layout)
}

fn with_floaty<R>(
    host: &mut UiHost,
    id: i32,
    f: impl FnOnce(&mut FloatingChatController, &mut UiRoot) -> R,
) -> R {
    host.with_controller::<FloatingChatController, _>(&name(id), f)
        .unwrap()
}

fn shown(host: &mut UiHost, id: i32) -> Vec<String> {
    with_floaty(host, id, |c, ui| {
        c.refresh_transcript(ui);
        c.transcript_layout()
            .lines
            .iter()
            .map(|l| l.text.clone())
            .collect()
    })
}

fn session_commands(host: &mut UiHost) -> Vec<String> {
    host.take_outputs()
        .into_iter()
        .filter_map(|o| match o {
            UiOutput::Panel(PanelOutput::Session(c)) => Some(format!("{c:?}")),
            _ => None,
        })
        .collect()
}

// ── Bind ────────────────────────────────────────────────────────────────

#[test]
fn bind_succeeds_on_the_retail_shaped_tree_and_keeps_the_window_number() {
    let (mut host, _) = floaty_host(1);
    assert_eq!(with_floaty(&mut host, 1, |c, _| c.window_id()), window(1));
}

#[test]
#[should_panic(expected = "a floating chat window is 1..=4")]
fn the_main_window_is_not_a_floating_one() {
    let mut ui = root(800.0, 600.0);
    let layout = build_layout(&mut ui, &floaty_info());
    let _ = FloatingChatController::bind(&mut ui, &layout, ChatWindowId::MAIN, "x", None);
}

#[test]
fn bind_needs_the_transcript_panel() {
    let mut ui = root(800.0, 600.0);
    let layout = build_layout(
        &mut ui,
        &info(floating_chat::ROOT_ID, 3, 0.0, 0.0, 250.0, 108.0),
    );
    assert!(
        FloatingChatController::bind(&mut ui, &layout, window(1), "chat-window-1", None).is_none()
    );
}

#[test]
fn the_transcript_sits_in_the_transcript_panel_and_the_entry_in_the_row() {
    let (mut host, layout) = floaty_host(2);
    let (t, i) = with_floaty(&mut host, 2, |c, _| (c.transcript(), c.input()));
    assert_eq!(
        host.root.parent(t),
        layout.find(floating_chat::TRANSCRIPT_PANEL_ID)
    );
    assert_eq!(
        host.root.parent(i),
        layout.find(floating_chat::INPUT_ROW_ID)
    );
    let text = host.root.widget::<Text>(t).unwrap();
    assert!(text.selectable() && !text.one_line);
}

#[test]
fn the_title_reads_chat_n_and_send_is_captioned() {
    let (host, layout) = floaty_host(3);
    let title = layout.find(floating_chat::TITLE_BAR_ID).unwrap();
    assert_eq!(text_of(&host.root, title), "Chat 3");
    let send = layout.find(floating_chat::SEND_ID).unwrap();
    assert_eq!(
        host.root.widget::<Button>(send).unwrap().label.as_deref(),
        Some("Send")
    );
}

// ── Sending ─────────────────────────────────────────────────────────────

#[test]
fn a_typed_line_always_goes_out_on_say() {
    let (mut host, _) = floaty_host(4);
    let i = with_floaty(&mut host, 4, |c, _| c.input());
    host.root.set_keyboard_focus(Some(i));
    for ch in "hi everyone".chars() {
        host.char_typed(ch);
    }
    host.key_down(Key::Enter, Modifiers::NONE);
    let expected = SessionCommand::ChatInput {
        text: "hi everyone".into(),
        channel: ChatChannelKind::Say,
        tell_target: None,
        tell_target_guid: 0,
    };
    assert_eq!(session_commands(&mut host), vec![format!("{expected:?}")]);
    assert_eq!(host.root.widget::<Field>(i).unwrap().text(), "");
}

#[test]
fn the_send_button_submits_the_entry() {
    let (mut host, layout) = floaty_host(1);
    let i = with_floaty(&mut host, 1, |c, _| c.input());
    host.root.widget_mut::<Field>(i).unwrap().set_text("/who");
    let send = layout.find(floating_chat::SEND_ID).unwrap();
    let (x, y) = host.root.screen_position(send);
    host.mouse_move(x as i32 + 3, y as i32 + 3);
    host.mouse_down(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    host.mouse_up(MouseButton::Left, x as i32 + 3, y as i32 + 3, 0);
    host.pump();
    let out = session_commands(&mut host);
    assert_eq!(out.len(), 1, "{out:?}");
    assert!(out[0].contains("\"/who\""), "{out:?}");
}

#[test]
fn the_close_button_hides_the_window() {
    let (mut host, layout) = floaty_host(2);
    host.root.show_window(&name(2));
    let close = layout.find(floating_chat::CLOSE_BUTTON_ID).unwrap();
    let (x, y) = host.root.screen_position(close);
    host.mouse_move(x as i32 + 2, y as i32 + 2);
    host.mouse_down(MouseButton::Left, x as i32 + 2, y as i32 + 2, 0);
    host.mouse_up(MouseButton::Left, x as i32 + 2, y as i32 + 2, 0);
    host.pump();
    assert!(!host.root.is_window_visible(&name(2)));
}

// ── The window's own filter ─────────────────────────────────────────────

#[test]
fn the_transcript_shows_only_what_this_windows_filter_accepts() {
    // Window 1's default: speech, tell, direct speech sent, emote.
    let (mut host, _) = floaty_host(1);
    with_floaty(&mut host, 1, |c, _| {
        c.push_line(TranscriptLine::plain(1, 0x02, "visible speech"));
        c.push_line(TranscriptLine::plain(2, 0x0A, "hidden social"));
    });
    assert_eq!(shown(&mut host, 1), ["visible speech"]);
}

#[test]
fn a_floating_window_draws_no_link_colour_and_no_grey_timestamp() {
    // OpenAC `FloatingChatWindowController.GetTranscriptLines`
    // builds its lines with no runs and no tag ranges: a line keeps its one
    // colour and nothing in it is a link.
    use ac_core::chat::{ChatSpanRole, ChatTextSpan, tag_markup};
    let (mut host, _) = floaty_host(1);
    let mut spans = vec![ChatTextSpan {
        text: "[12:00] ".into(),
        tag: None,
        role: ChatSpanRole::Timestamp,
    }];
    spans.extend(tag_markup::parse(
        r"<Tell:IIDString:1:Bob>Bob<\Tell> says, at 1N 2E",
    ));
    let line = TranscriptLine {
        spans: Some(spans),
        ..TranscriptLine::plain(1, 0x02, "[12:00] Bob says, at 1N 2E")
    };
    with_floaty(&mut host, 1, |c, _| c.push_line(line));
    assert_eq!(shown(&mut host, 1), ["[12:00] Bob says, at 1N 2E"]);
    let (runs, tags) = with_floaty(&mut host, 1, |c, _| {
        let layout = c.transcript_layout();
        (layout.runs.clone(), layout.tags.clone())
    });
    assert!(runs.iter().all(Option::is_none), "{runs:?}");
    assert!(tags.iter().all(Option::is_none), "{tags:?}");
}

#[test]
fn another_window_accepts_another_set_of_classes() {
    // Window 3's default: fellowship only.
    let (mut host, _) = floaty_host(3);
    with_floaty(&mut host, 3, |c, _| {
        c.push_line(TranscriptLine::plain(1, 0x02, "speech line"));
        c.push_line(TranscriptLine::plain(2, 0x13, "fellowship line"));
    });
    assert_eq!(shown(&mut host, 3), ["fellowship line"]);
}

#[test]
fn a_filter_change_shows_on_the_next_rebuild() {
    let (mut host, _) = floaty_host(2);
    with_floaty(&mut host, 2, |c, _| {
        c.push_line(TranscriptLine::plain(1, 0x02, "speech line"));
    });
    assert!(shown(&mut host, 2).is_empty());
    let mut filters = ChatWindowState::new();
    filters.set_filter(window(2), filters.filter(window(2)) | (1 << 0x02));
    with_floaty(&mut host, 2, |c, _| c.set_filters(filters));
    assert_eq!(shown(&mut host, 2), ["speech line"]);
}

#[test]
fn an_unchanged_filter_and_content_reuse_the_layout() {
    let (mut host, _) = floaty_host(1);
    with_floaty(&mut host, 1, |c, _| {
        c.push_line(TranscriptLine::plain(1, 0x02, "speech line"));
    });
    host.tick(0.016, 16, None);
    host.draw((800.0, 600.0), &NoSprites);
    host.tick(0.016, 32, None);
    host.draw((800.0, 600.0), &NoSprites);
    assert_eq!(
        with_floaty(&mut host, 1, |c, _| c.transcript_layout_builds),
        1
    );
}

// ── The chat font ───────────────────────────────────────────────────────

#[test]
fn the_chat_font_goes_to_the_transcript_and_not_the_entry() {
    let (mut host, _) = floaty_host(1);
    let (t, i) = with_floaty(&mut host, 1, |c, _| (c.transcript(), c.input()));
    let new_font = font(0x4000_0002);
    with_floaty(&mut host, 1, |c, ui| {
        c.apply_chat_font(ui, new_font.clone())
    });
    assert!(Arc::ptr_eq(
        host.root.widget::<Text>(t).unwrap().font.as_ref().unwrap(),
        &new_font
    ));
    assert_eq!(
        host.root
            .widget::<Field>(i)
            .unwrap()
            .font
            .as_ref()
            .unwrap()
            .id,
        0x4000_0001
    );
}

#[test]
fn a_new_chat_font_re_wraps_the_transcript() {
    let (mut host, _) = floaty_host(1);
    with_floaty(&mut host, 1, |c, ui| {
        c.push_line(TranscriptLine::plain(1, 0x02, "speech line"));
        c.refresh_transcript(ui);
    });
    let before = with_floaty(&mut host, 1, |c, _| c.transcript_layout_builds);
    with_floaty(&mut host, 1, |c, ui| {
        c.apply_chat_font(ui, font(0x4000_0001));
    });
    assert_eq!(
        with_floaty(&mut host, 1, |c, _| c.transcript_layout_builds),
        before + 1
    );
}

// ── Real dats (FloatingChatLayoutConformanceTests) ──────────────────────

mod real_dats {
    use super::*;
    use ac_ui::content::DatContent;
    use ac_ui::font::DEFAULT_FONT_ID;
    use ac_ui::import::{import, import_infos};
    use ac_ui::widgets::{ResizeGrip, Scrollbar};
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

    fn find(info: &ElementInfo, id: u32) -> Option<&ElementInfo> {
        if info.id == id {
            return Some(info);
        }
        info.children.iter().find_map(|c| find(c, id))
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_floaty_layout_has_its_elements_and_none_of_the_main_windows() {
        let c = content();
        let root = import_infos(&c, floating_chat::LAYOUT_ID).expect("layout 0x2100005B");
        for id in [
            floating_chat::TRANSCRIPT_ID,
            floating_chat::INPUT_ID,
            floating_chat::TRACK_ID,
            floating_chat::SEND_ID,
            floating_chat::TITLE_BAR_ID,
            floating_chat::CLOSE_BUTTON_ID,
            0x1000_0529,
        ] {
            assert!(find(&root, id).is_some(), "{id:#010x}");
        }
        // No talk-focus menu, maximize button or window indicators.
        for id in [0x1000_0014, 0x1000_046F, 0x1000_0522] {
            assert!(find(&root, id).is_none(), "{id:#010x}");
        }
        let kind = |id| find(&root, id).unwrap().kind;
        assert_eq!(kind(floating_chat::INPUT_ID), 12);
        assert_eq!(
            find(&root, floating_chat::INPUT_ID)
                .unwrap()
                .effective_bool(0x16, None),
            Some(true)
        );
        assert_eq!(kind(floating_chat::TITLE_BAR_ID), 12);
        assert_ne!(
            find(&root, floating_chat::TITLE_BAR_ID)
                .unwrap()
                .effective_bool(0x16, None),
            Some(true)
        );
        assert_eq!(kind(floating_chat::SEND_ID), 1);
        assert_eq!(kind(floating_chat::CLOSE_BUTTON_ID), 1);
        assert_eq!(kind(floating_chat::TRACK_ID), 11);
        assert_eq!(kind(0x1000_0529), 2, "the title bar's move handle");
        for grip in [
            0x1000_04FC,
            0x1000_000F,
            0x1000_04FE,
            0x1000_04D2,
            0x1000_0501,
            0x1000_04D4,
            0x1000_0503,
            0x1000_04D3,
        ] {
            assert_eq!(kind(grip), 9, "{grip:#010x} is a resize grip");
        }
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_floaty_window_builds_binds_and_its_grips_have_art() {
        let c = content();
        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(
            &mut host.root,
            &c,
            floating_chat::LAYOUT_ID,
            c.font(DEFAULT_FONT_ID),
        )
        .expect("layout 0x2100005B");
        let ui = &mut host.root;
        assert!(
            ui.widget::<Field>(layout.find(floating_chat::INPUT_ID).unwrap())
                .is_some()
        );
        assert!(
            ui.widget::<Text>(layout.find(floating_chat::TITLE_BAR_ID).unwrap())
                .is_some()
        );
        assert!(
            ui.widget::<Button>(layout.find(floating_chat::SEND_ID).unwrap())
                .is_some()
        );
        assert!(
            ui.widget::<Button>(layout.find(floating_chat::CLOSE_BUTTON_ID).unwrap())
                .is_some()
        );
        assert!(
            ui.widget::<Scrollbar>(layout.find(floating_chat::TRACK_ID).unwrap())
                .is_some()
        );
        for grip in [
            0x1000_04FC,
            0x1000_000F,
            0x1000_04FE,
            0x1000_04D2,
            0x1000_0501,
            0x1000_04D4,
            0x1000_0503,
            0x1000_04D3,
        ] {
            let g = layout.find(grip).unwrap();
            assert_ne!(
                ui.widget::<ResizeGrip>(g).unwrap().sprite,
                0,
                "{grip:#010x}"
            );
        }
        let ctl = FloatingChatController::bind(
            ui,
            &layout,
            window(1),
            "chat-window-1",
            c.font(DEFAULT_FONT_ID),
        );
        assert!(ctl.is_some());
        assert!(c.take_errors().is_empty());
    }

    /// OpenAC `ChatLayoutConformanceTests`: the main chat window's authored
    /// size, limits, reflow, grips and move strip.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_main_chat_window_conforms_to_its_retail_layout() {
        use ac_ui::panels::chat;
        use ac_ui::widgets::resize_grip::Border;
        let c = content();
        let infos = import_infos(&c, chat::LAYOUT_ID).expect("layout 0x2100006F");
        assert_eq!((infos.width, infos.height), (410.0, 100.0));
        let window = find(&infos, chat::ROOT_ID).unwrap();
        let int = |p| window.effective_integer(p, None);
        assert_eq!(
            (int(0x3E), int(0x3C)),
            (Some(100), Some(2000)),
            "height limits"
        );
        assert_eq!(
            (int(0x3F), int(0x3D)),
            (Some(300), Some(2000)),
            "width limits"
        );

        let mut host = UiHost::new();
        host.root.set_size(1024.0, 768.0);
        let layout = import(&mut host.root, &c, chat::LAYOUT_ID, c.font(DEFAULT_FONT_ID))
            .expect("layout 0x2100006F");
        let ui = &mut host.root;
        for (id, border) in [
            (0x1000_069B, Border::UpperLeft),
            (0x1000_069D, Border::UpperRight),
            (0x1000_069F, Border::LowerLeft),
            (0x1000_06A1, Border::LowerRight),
        ] {
            let grip = ui.widget::<ResizeGrip>(layout.find(id).unwrap()).unwrap();
            assert_eq!(grip.border, border, "{id:#010x}");
        }
        for id in [
            0x1000_069B,
            0x1000_069D,
            0x1000_069E,
            0x1000_069F,
            0x1000_06A0,
            0x1000_06A1,
            0x1000_06A2,
        ] {
            let grip = ui.widget::<ResizeGrip>(layout.find(id).unwrap()).unwrap();
            assert_ne!(grip.sprite, 0, "{id:#010x}");
        }
        let strip = layout.find(0x1000_069C).unwrap();
        assert!(ui.widget::<ResizeGrip>(strip).is_none());
        assert!(ui.element(strip).unwrap().window_move_handle);

        // Both panels keep their 5-pixel margins on a wider window.
        let ctl = ac_ui::panels::chat::ChatWindowController::bind(
            ui,
            &layout,
            c.font(DEFAULT_FONT_ID),
            None,
        )
        .unwrap();
        let root = ctl.root();
        assert_eq!(ui.element(root).unwrap().width, 410.0);
        ui.element_mut(root).unwrap().width = 615.0;
        let (w, h) = (615.0, ui.element(root).unwrap().height);
        for id in [chat::TRANSCRIPT_PANEL_ID, chat::INPUT_BAR_ID] {
            let e = layout.find(id).unwrap();
            ui.apply_anchor(e, w, h);
            assert_eq!(ui.element(e).unwrap().width, 605.0, "{id:#010x}");
        }
        // The talk-focus button keeps its authored width.
        let menu = layout.find(chat::MENU_ID).unwrap();
        let input_left = ui.element(ctl.input()).unwrap().left;
        let bar = ui.parent(menu).unwrap();
        let (bw, bh) = ui.element(bar).map(|e| (e.width, e.height)).unwrap();
        ui.apply_anchor(menu, bw, bh);
        assert_eq!(ui.element(menu).unwrap().width, 46.0);
        assert_eq!(ui.element(ctl.input()).unwrap().left, input_left);
    }
}

#[test]
fn the_transcript_hides_the_tags_in_a_line_and_draws_no_links() {
    // OpenAC chat tag fix
    // FloatingChatWindowControllerTests.Transcript_HidesTheTagsInALineAndDrawsNoLinks.
    let (mut host, _) = floaty_host(1);
    let mut log = ac_core::chat::ChatLog::new();
    log.on_system_message(0, r"<Tell:IIDString:1:Bob>Bob<\Tell> has logged on.", 0x02);
    log.on_local_speech(
        0,
        "You",
        r"ask <Tell:IIDString:7:Ann>Ann<\Tell>",
        0,
        false,
        0x02,
    );
    for entry in log.entries() {
        let line = ac_runtime::communication::ChatLine::from_entry(entry, false, 0);
        with_floaty(&mut host, 1, |c, _| c.push_chat_line(&line));
    }

    assert_eq!(
        shown(&mut host, 1),
        ["Bob has logged on.", "You say, \"ask Ann\""]
    );
    let (runs, tags) = with_floaty(&mut host, 1, |c, _| {
        let layout = c.transcript_layout();
        (layout.runs.clone(), layout.tags.clone())
    });
    assert!(runs.iter().all(Option::is_none), "{runs:?}");
    assert!(tags.iter().all(Option::is_none), "{tags:?}");
}
