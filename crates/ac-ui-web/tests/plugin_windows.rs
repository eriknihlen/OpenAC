//! Plugin windows in the module, driven as a page drives them over the
//! installed dats, with a plugin window made by hand (no recording): what
//! the module sends the session for the player's clicks, keys and
//! presses, and what it tells the session about the window itself.
//!
//! Needs the dats in `Documents\Asheron's Call` under the user's profile.

mod harness;

use ac_clock::Instant;
use ac_plugin_ui::{BindingKind as K, BindingSchema, PanelDescriptor, PluginUi, UiOwner, UiValue};
use ac_remote_wire::{EncodeContext, PresentationEvent, SessionCommand};
use ac_runtime::movement::{MovementCommand, MovementKeys};
use ac_runtime::session::interface::InterfaceEvent;
use ac_ui_web::host::{Modifiers, Panels, Session};
use harness::Page;

const WIDTH: u32 = 1024;
const HEIGHT: u32 = 768;
const GENERATION: u64 = 5;

const MARKUP: &str = r#"<panel x="300" y="200" w="240" h="120" title="Demo">
  <toggle name="Lines" x="8" y="24" w="120" h="20" text="Show lines" checked="{Lines}" onclick="{ToggleLines}"/>
  <button name="Go" x="8" y="50" w="80" h="20" text="Go" onclick="{Go}"/>
</panel>"#;

/// A second window, opened after the first and over its right part.
const OTHER: &str = r#"<panel x="380" y="160" w="240" h="120" title="Other">
  <button name="Ping" x="8" y="50" w="80" h="20" text="Ping" onclick="{Ping}"/>
</panel>"#;

/// The session's side: one plugin window, shown.
fn plugin_ui() -> PluginUi {
    let mut ui = PluginUi::new();
    ui.set_window_attached(true);
    let schema = BindingSchema::new("Demo")
        .with("Lines", K::Bool)
        .with("ToggleLines", K::Action)
        .with("Go", K::Action);
    let id = ui
        .register_panel(
            &UiOwner::new("test.demo", "Demo"),
            PanelDescriptor::new("main", "Demo"),
            MARKUP,
            schema,
        )
        .unwrap();
    ui.set_values(
        "test.demo",
        id,
        vec![("Lines".into(), UiValue::Bool(false))],
    )
    .unwrap();
    ui.show_panel("test.demo", "main", true);
    ui
}

/// The session's side with a second plugin's window, opened later.
fn two_windows() -> PluginUi {
    let mut ui = plugin_ui();
    ui.register_panel(
        &UiOwner::new("test.other", "Other"),
        PanelDescriptor::new("main", "Other"),
        OTHER,
        BindingSchema::new("Other").with("Ping", K::Action),
    )
    .unwrap();
    ui.show_panel("test.other", "main", true);
    ui
}

fn session(controlling: bool) -> Session {
    // Permissions: control.
    Session::from_words(GENERATION, u32::from(controlling), 1)
}

/// A page built over the dats with the plugin window streamed in.
fn page_with_plugin_window(controlling: bool) -> Page {
    page_with(&plugin_ui(), controlling)
}

/// A page built over the dats with `state`'s plugin windows streamed in.
fn page_with(state: &PluginUi, controlling: bool) -> Page {
    let mut page = Page::open();
    page.preload();
    page.panels().init(WIDTH, HEIGHT, 1.0, 0.0);
    page.panels().focus(true);
    page.panels().session(session(controlling));
    let event = PresentationEvent::Interface(InterfaceEvent::PluginUi(state.snapshot()));
    let frame =
        ac_remote_wire::encode_batch(&[event], &EncodeContext::new(Instant::now())).unwrap();
    let now = page.now;
    page.panels().stream(&frame, now).unwrap();
    page.settle(400);
    for _ in 0..3 {
        page.frame();
    }
    assert!(
        page.panels()
            .ui()
            .is_some_and(|ui| ui.plugin_windows().contains(&MAIN.to_owned())),
        "the plugin window is mounted"
    );
    page
}

const MAIN: &str = "plugin:test.demo:main";

/// The page's left button at `at`: down, then up, with what each returned.
fn click(page: &mut Page, at: (f32, f32), frame_between: bool) -> (bool, bool) {
    let none = Modifiers::default();
    let now = page.now;
    let down = page.panels().pointer_button(0, true, at, none, now);
    if frame_between {
        page.frame_after(80.0);
    }
    let now = page.now;
    let up = page.panels().pointer_button(0, false, at, none, now);
    (down, up)
}

/// The commands sent since the last look, heartbeats left out.
fn sent(page: &mut Page) -> Vec<String> {
    let heartbeat = format!(
        "{:?}",
        SessionCommand::Movement(MovementCommand::Keys(MovementKeys::default()))
    );
    page.sent_stamped()
        .into_iter()
        .map(|(_, command)| format!("{command:?}"))
        .filter(|c| *c != heartbeat)
        .collect()
}

/// The z-order of plugin window `name`'s frame.
fn z_order(page: &mut Page, name: &str) -> i32 {
    let ui = page.panels().ui().expect("built");
    let root = &ui.host().root;
    let frame = root.window(name).expect("mounted").frame;
    root.element(frame).expect("its frame").z_order
}

/// With control, a click on a plugin window's toggle (down and up, with
/// or without a pointer move first and with or without a frame between)
/// sends the toggle's action and nothing else, with the frame that follows
/// the release; the toggle shows the value the session sends back.
#[test]
#[ignore = "reads the installed dats"]
fn a_click_on_a_plugin_toggle_sends_its_action_with_the_next_frame() {
    let mut page = page_with_plugin_window(true);
    sent(&mut page);
    let toggle = (320.0, 234.0);
    let expected = "PluginUi(Action { panel: 1, handler: \"ToggleLines\", value: None })";
    for (moved_first, frame_between) in [(true, true), (false, true), (false, false)] {
        if moved_first {
            let now = page.now;
            assert!(page.panels().pointer_move(toggle.0, toggle.1, now));
        } else {
            // Somewhere else first, so the press is not where the pointer is.
            let now = page.now;
            page.panels().pointer_move(10.0, 300.0, now);
        }
        page.frame();
        sent(&mut page);
        assert_eq!(click(&mut page, toggle, frame_between), (true, true));
        if frame_between {
            assert!(sent(&mut page).is_empty(), "nothing before the release");
        }
        page.frame();
        assert_eq!(sent(&mut page), [expected], "{moved_first} {frame_between}");
        // Clicks far enough apart in time are no double click.
        page.frame_after(600.0);
        std::thread::sleep(std::time::Duration::from_millis(600));
    }
}

/// With control, a press and release over empty space send nothing: the
/// module makes no world click (the 3-D view is the page's), and a press
/// on a plugin window sends only what the window's control asks.
#[test]
#[ignore = "reads the installed dats"]
fn a_press_over_empty_space_sends_nothing() {
    let mut page = page_with_plugin_window(true);
    sent(&mut page);
    let empty = (700.0, 380.0);
    let now = page.now;
    assert!(!page.panels().pointer_move(empty.0, empty.1, now));
    assert_eq!(click(&mut page, empty, true), (false, false));
    let none = Modifiers::default();
    let now = page.now;
    assert!(!page.panels().pointer_button(2, true, empty, none, now));
    assert!(!page.panels().pointer_button(2, false, empty, none, now));
    page.frame();
    page.frame();
    assert!(sent(&mut page).is_empty());
}

/// A press anywhere on a plugin window raises it above another plugin
/// window, as in the native window (the order is the window's own, not
/// the session's: nothing is sent).
#[test]
#[ignore = "reads the installed dats"]
fn a_press_on_a_plugin_windows_title_raises_it() {
    let other = "plugin:test.other:main";
    let mut page = page_with(&two_windows(), true);
    sent(&mut page);
    assert!(
        z_order(&mut page, other) > z_order(&mut page, MAIN),
        "opened later"
    );
    // The first window's title, left of the second window.
    assert_eq!(click(&mut page, (320.0, 208.0), true), (true, true));
    page.frame();
    assert!(z_order(&mut page, MAIN) > z_order(&mut page, other));
    assert!(sent(&mut page).is_empty());
    // Where the two overlap, a click now reaches the first window's toggle.
    assert_eq!(click(&mut page, (400.0, 234.0), true), (true, true));
    page.frame();
    assert_eq!(
        sent(&mut page),
        ["PluginUi(Action { panel: 1, handler: \"ToggleLines\", value: None })"]
    );
}

/// The "–" of a window on the plugin dock hides the window at once and
/// tells the session, with the next frame.
#[test]
#[ignore = "reads the installed dats"]
fn the_minus_button_hides_the_window_and_tells_the_session() {
    let mut page = page_with_plugin_window(true);
    sent(&mut page);
    let minus = {
        let ui = page.panels().ui().expect("built");
        let root = &ui.host().root;
        let frame = root.window(MAIN).expect("mounted").frame;
        let (x, y) = root.screen_position(frame);
        let width = root.element(frame).expect("its frame").width;
        // The button sits 23 units in from the right edge, 3 down, 18 × 17.
        (x + width - 23.0 + 9.0, y + 3.0 + 8.0)
    };
    assert_eq!(click(&mut page, minus, true), (true, true));
    let shown = |page: &mut Page| {
        page.panels()
            .ui()
            .is_some_and(|ui| ui.host().root.is_window_visible(MAIN))
    };
    assert!(!shown(&mut page), "hidden at once");
    page.frame();
    assert_eq!(
        sent(&mut page),
        ["PluginUi(PanelShown { panel: 1, shown: false })"]
    );
}

/// The plugin UI commands sent since the last look, by kind (the
/// heartbeats left out).
fn plugin_reports(page: &mut Page) -> Vec<String> {
    page.sent_stamped()
        .into_iter()
        .filter_map(|(generation, command)| {
            assert_eq!(generation, GENERATION);
            match command {
                SessionCommand::PluginUi(input) => {
                    let text = format!("{input:?}");
                    Some(text[..text.find([' ', '(']).unwrap_or(text.len())].to_owned())
                }
                _ => None,
            }
        })
        .collect()
}

/// What the window tells the session about itself (its display density,
/// the client windows it has and shows, its key map's chords) is said when
/// the session can hear it: a page that only watched while the panels were
/// built says it all when it takes control, as one that had control from
/// the start said it then.
#[test]
#[ignore = "reads the installed dats"]
fn a_page_that_takes_control_later_tells_the_session_what_the_window_reports() {
    let mut from_start = page_with_plugin_window(true);
    let mut reported = plugin_reports(&mut from_start);
    reported.sort();
    for kind in ["ClientChords", "ClientWindows", "DisplayDensity"] {
        assert!(reported.iter().any(|r| r == kind), "{kind}: {reported:?}");
    }

    let mut later = page_with_plugin_window(false);
    assert!(
        later.sent_stamped().is_empty(),
        "nothing goes while watching"
    );
    later.panels().session(session(true));
    later.frame();
    let mut after_control = plugin_reports(&mut later);
    after_control.sort();
    assert_eq!(after_control, reported);
    later.frame();
    assert!(plugin_reports(&mut later).is_empty(), "said once");
}
