//! A plugin window built from markup and driven through the UI host: what
//! the session's values show, and what the player's presses report.
//! Behaviour from OpenAC `tests/AcDream.App.Tests/UI/MarkupDocumentTests.cs`,
//! `MarkupPanelClickTests.cs`, `MarkupListColumnsTests.cs` and
//! `Plugins/BufferedUiRegistryTests.cs`.

use std::cell::RefCell;
use std::rc::Rc;

use ac_plugin_ui::{
    ActionValue, BindingKind as K, BindingSchema, PanelDescriptor, PanelView, PluginUi, UiInput,
    UiOwner, UiValue,
};

use super::*;
use crate::draw::TextureRef;
use crate::event::MouseButton;
use crate::host::UiHost;

fn named(ui: &UiRoot, start: ElementId, name: &str) -> Option<ElementId> {
    if ui.element(start)?.name.as_deref() == Some(name) {
        return Some(start);
    }
    ui.children(start)
        .into_iter()
        .find_map(|c| named(ui, c, name))
}

struct NoIcons;

impl MarkupIcons for NoIcons {
    fn surface(&mut self, id: u32) -> Option<Icon> {
        Some((crate::draw::TextureRef::Surface(id), 16.0, 16.0))
    }
    fn spell(&mut self, _spell: u32) -> Option<Icon> {
        None
    }
    fn item(&mut self, _object: u32) -> Option<Icon> {
        None
    }
}

struct NoDockIcons;

impl dock::DockIcons for NoDockIcons {
    fn file(&mut self, _icon: &ac_plugin_ui::PluginIcon) -> Option<Icon> {
        None
    }
    fn surface(&mut self, _id: u32) -> Option<Icon> {
        None
    }
}

const MARKUP: &str = r#"<panel x="10" y="20" w="300" h="220" title="Demo">
  <label name="Status" x="8" y="24" text="{Status}"/>
  <button name="Go" x="8" y="44" w="80" h="20" text="Go" onclick="{Go}"/>
  <toggle x="8" y="70" w="120" h="18" text="Fast" checked="{Fast}" onclick="{ToggleFast}"/>
  <list x="8" y="94" w="200" h="60" rowheight="20" items="{Rows}" selected="{Row}" onchange="{PickRow}"/>
  <list x="8" y="160" w="200" h="40" rowheight="20" selected="{Row}">
    <column type="text" width="100" items="{Rows}"/>
    <column type="check" width="20" values="{Checks}" onchange="{Check}"/>
  </list>
</panel>"#;

fn session() -> (PluginUi, u32) {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("Demo")
        .with("Status", K::Text)
        .with("Go", K::Action)
        .with("Fast", K::Bool)
        .with("ToggleFast", K::Action)
        .with("Rows", K::TextList)
        .with("Row", K::Int)
        .with("PickRow", K::IntAction)
        .with("Checks", K::BoolList)
        .with("Check", K::IntAction);
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
        vec![
            ("Status".into(), UiValue::Text("idle".into())),
            ("Fast".into(), UiValue::Bool(true)),
            (
                "Rows".into(),
                UiValue::TextList(vec!["a".into(), "b".into(), "c".into()]),
            ),
            ("Row".into(), UiValue::Int(-1)),
            ("Checks".into(), UiValue::BoolList(vec![true, false])),
        ],
    )
    .unwrap();
    (ui, id)
}

fn mounted(view: &PanelView) -> (UiHost, PluginOutbox, ElementId) {
    let mut host = UiHost::new();
    host.root.set_size(1024.0, 768.0);
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut controller = build_panel(&mut host.root, view, None, Rc::clone(&outbox));
    let frame = controller.root;
    let root = host.root.root();
    host.root.add_child(root, frame);
    host.root
        .register_window(&controller.window_name, frame, Some(frame), 0)
        .unwrap();
    controller.sync(&mut host.root, view, 1, &mut NoIcons, PluginTheme::Classic);
    host.attach_controller(&view.window_name(), Box::new(controller))
        .unwrap();
    (host, outbox, frame)
}

fn click(host: &mut UiHost, x: i32, y: i32) {
    host.mouse_down(MouseButton::Left, x, y, 0);
    host.mouse_up(MouseButton::Left, x, y, 0);
}

#[test]
fn the_window_shows_the_session_values_and_reports_presses() {
    let (state, panel) = session();
    let view = state.panels().next().unwrap().clone();
    let (mut host, outbox, frame) = mounted(&view);
    assert!(host.root.is_window_visible("plugin:test.demo:main"));
    let status = named(&host.root, frame, "Status").expect("the status label");
    assert_eq!(
        host.root.widget::<MarkupLabel>(status).unwrap().text,
        "idle"
    );
    // The button at (8, 44) in a window at (10, 20).
    click(&mut host, 10 + 20, 20 + 50);
    // The toggle.
    click(&mut host, 10 + 20, 20 + 78);
    // The second row of the single-column list (rows 20 high from y 94).
    click(&mut host, 10 + 30, 20 + 94 + 25);
    // The check column, first row.
    click(&mut host, 10 + 8 + 105, 20 + 160 + 5);
    let inputs = outbox.borrow_mut().drain(..).collect::<Vec<_>>();
    assert_eq!(
        inputs,
        vec![
            UiInput::Action {
                panel,
                handler: "Go".into(),
                value: ActionValue::None
            },
            UiInput::Action {
                panel,
                handler: "ToggleFast".into(),
                value: ActionValue::None
            },
            UiInput::Action {
                panel,
                handler: "PickRow".into(),
                value: ActionValue::Int(1)
            },
            UiInput::Action {
                panel,
                handler: "Check".into(),
                value: ActionValue::Int(0)
            },
        ]
    );
}

#[test]
fn closing_the_window_is_reported_once_and_a_session_hide_is_not_echoed() {
    let (mut state, panel) = session();
    let view = state.panels().next().unwrap().clone();
    let (mut host, outbox, _) = mounted(&view);
    // The player minimizes it.
    host.root.hide_window("plugin:test.demo:main");
    host.pump();
    assert_eq!(
        outbox.borrow_mut().drain(..).collect::<Vec<_>>(),
        vec![UiInput::PanelShown {
            panel,
            shown: false
        }]
    );
    // The session agrees and hides it: no echo.
    state.input(UiInput::PanelShown {
        panel,
        shown: false,
    });
    let hidden = state.panels().next().unwrap().clone();
    host.with_controller::<PluginPanelController, _>("plugin:test.demo:main", |c, ui| {
        c.sync(ui, &hidden, 2, &mut NoIcons, PluginTheme::Classic);
    });
    assert!(!host.root.is_window_visible("plugin:test.demo:main"));
    assert!(outbox.borrow().is_empty());
    // The session shows it again (the plugin's ShowPanel): no echo either.
    assert!(state.show_panel("test.demo", "main", true));
    let shown = state.panels().next().unwrap().clone();
    host.with_controller::<PluginPanelController, _>("plugin:test.demo:main", |c, ui| {
        c.sync(ui, &shown, 3, &mut NoIcons, PluginTheme::Classic);
    });
    assert!(host.root.is_window_visible("plugin:test.demo:main"));
    assert!(outbox.borrow().is_empty());
}

#[test]
fn a_control_label_and_visibility_override_reach_the_widgets() {
    let (mut state, _) = session();
    assert!(state.set_control_label("test.demo", "main", "Go", "Stop"));
    assert!(state.set_control_visible("test.demo", "main", "Status", false));
    let view = state.panels().next().unwrap().clone();
    let (host, _, frame) = mounted(&view);
    let go = named(&host.root, frame, "Go").unwrap();
    assert_eq!(host.root.widget::<MarkupButton>(go).unwrap().text, "Stop");
    let status = named(&host.root, frame, "Status").unwrap();
    assert!(!host.root.element(status).unwrap().visible);
}

#[test]
fn a_theme_restyles_only_the_defaulted_colours_of_a_themed_window() {
    let mut state = PluginUi::new();
    let id = state
        .register_panel(
            &UiOwner::new("test.theme", "Theme"),
            PanelDescriptor::new("t", "Theme"),
            r##"<panel theme="plugin" w="200" h="100"><button name="A" text="a"/><button name="B" text="b" background="#FF102030"/></panel>"##,
            BindingSchema::new("T"),
        )
        .unwrap();
    let view = state.panels().find(|p| p.id == id).unwrap().clone();
    let mut host = UiHost::new();
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut c = build_panel(&mut host.root, &view, None, outbox);
    let root = host.root.root();
    host.root.add_child(root, c.root);
    host.root
        .register_window(&c.window_name, c.root, Some(c.root), 0)
        .unwrap();
    c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Moss);
    let a = named(&host.root, c.root, "A").unwrap();
    let b = named(&host.root, c.root, "B").unwrap();
    assert_eq!(
        host.root.widget::<MarkupButton>(a).unwrap().background,
        Palette::MOSS.field
    );
    assert_eq!(
        host.root.widget::<MarkupButton>(b).unwrap().background,
        color(ac_plugin_ui::Rgba::argb(0xFF10_2030))
    );
}

// OpenAC `MarkupDocumentTests.MenuRefreshesInitiallyEmptyChoicesBeforeOpening`:
// a menu whose choices were empty when the window was
// built opens on the choices the session holds when it is pressed (here
// every sync reads them), and a pick reports the entry and closes it.
#[test]
fn a_menu_opens_on_choices_that_arrived_after_it_was_built() {
    let markup = r#"<panel x="0" y="0" w="300" h="250">
  <menu name="Choices" x="10" y="10" w="200" h="24" items="{Choices}" selected="{Selected}" onchange="{Select}"/>
</panel>"#;
    let mut state = PluginUi::new();
    let schema = BindingSchema::new("DynamicChoiceBinding")
        .with("Choices", K::TextList)
        .with("Selected", K::Text)
        .with("Select", K::TextAction);
    let owner = UiOwner::new("test.choices", "Choices");
    let id = state
        .register_panel(&owner, PanelDescriptor::new("c", "Choices"), markup, schema)
        .unwrap();
    state
        .set_values(
            "test.choices",
            id,
            vec![
                ("Choices".into(), UiValue::TextList(Vec::new())),
                ("Selected".into(), UiValue::Text("All".into())),
            ],
        )
        .unwrap();
    let empty = state.panels().next().unwrap().clone();
    let (mut host, outbox, frame) = mounted(&empty);
    let menu = named(&host.root, frame, "Choices").unwrap();
    assert!(
        host.root
            .widget::<crate::widgets::Menu>(menu)
            .unwrap()
            .items
            .is_empty()
    );
    let choices = ["Custom", "All", "None", "B", "BPS", "BPSA", "BPSAC", "ALFC"];
    state
        .set_values(
            "test.choices",
            id,
            vec![(
                "Choices".into(),
                UiValue::TextList(choices.iter().map(|&c| c.to_owned()).collect()),
            )],
        )
        .unwrap();
    let filled = state.panels().next().unwrap().clone();
    host.with_controller::<PluginPanelController, _>(&filled.window_name(), |c, ui| {
        c.sync(ui, &filled, 2, &mut NoIcons, PluginTheme::Classic);
    });
    click(&mut host, 20, 20);
    let open = host.root.widget::<crate::widgets::Menu>(menu).unwrap();
    assert!(open.is_open());
    assert_eq!(open.items.len(), 8);
    // OpenAC `MarkupDocument`: every markup menu fits long entries.
    assert!(open.fits_long_labels);
    let row_height = open.row_height;
    let choice_y = 10 + 24 + crate::widgets::chrome::BORDER + (2.5 * row_height) as i32;
    click(&mut host, 25, choice_y);
    assert_eq!(
        outbox.borrow_mut().drain(..).collect::<Vec<_>>(),
        vec![UiInput::Action {
            panel: id,
            handler: "Select".into(),
            value: ActionValue::Text("None".into())
        }]
    );
    assert!(
        !host
            .root
            .widget::<crate::widgets::Menu>(menu)
            .unwrap()
            .is_open()
    );
}

/// OpenAC `PluginUiThemeTests.Build`: a hidden group of a plain label, a
/// label in a bound colour, one in a theme token with a Classic fallback,
/// and a searchable menu.
fn theme_window(opt_in: bool) -> (UiHost, PluginPanelController, PanelView) {
    let markup = format!(
        r##"<panel x="0" y="0" w="250" h="200"{}>
  <group x="0" y="0" w="200" h="150" visible="false">
    <label name="Plain" x="0" y="0" text="ExactVariableName"/>
    <label name="Status" x="0" y="20" text="Status" color="{{StateColor}}"/>
    <label name="Muted" x="0" y="40" text="Muted" color="theme:muted|#FF123456"/>
    <menu name="Pick" x="0" y="60" w="150" h="20" items="{{Items}}" selected="{{Selected}}" searchable="true"/>
  </group>
</panel>"##,
        if opt_in { r#" theme="plugin""# } else { "" }
    );
    let mut state = PluginUi::new();
    let schema = BindingSchema::new("Binding")
        .with("StateColor", K::Uint)
        .with("Items", K::TextList)
        .with("Selected", K::Text);
    let id = state
        .register_panel(
            &UiOwner::new("test.themes", "Themes"),
            PanelDescriptor::new("t", "Themes"),
            &markup,
            schema,
        )
        .unwrap();
    state
        .set_values(
            "test.themes",
            id,
            vec![
                ("StateColor".into(), UiValue::Uint(0xFF12_3456)),
                (
                    "Items".into(),
                    UiValue::TextList(vec!["A".into(), "B".into()]),
                ),
                ("Selected".into(), UiValue::Text("A".into())),
            ],
        )
        .unwrap();
    let view = state.panels().next().unwrap().clone();
    let mut host = UiHost::new();
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let c = build_panel(&mut host.root, &view, None, outbox);
    let root = host.root.root();
    host.root.add_child(root, c.root);
    host.root
        .register_window(&c.window_name, c.root, Some(c.root), 0)
        .unwrap();
    (host, c, view)
}

#[test]
fn an_opted_in_window_switches_and_restores_classic_including_hidden_groups() {
    // PluginUiThemeTests.OptedInWindowSwitchesAndRestoresClassicIncludingHiddenGroups.
    let (mut host, mut c, view) = theme_window(true);
    c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Classic);
    let plain = named(&host.root, c.root, "Plain").unwrap();
    let status = named(&host.root, c.root, "Status").unwrap();
    let muted = named(&host.root, c.root, "Muted").unwrap();
    let label = |host: &UiHost, id| host.root.widget::<MarkupLabel>(id).unwrap().clone();
    let original = label(&host, plain).color;
    let original_muted = label(&host, muted).color;
    let bound = label(&host, status).color;
    for (revision, theme) in [(2, PluginTheme::Moss), (3, PluginTheme::Brass)] {
        c.sync(&mut host.root, &view, revision, &mut NoIcons, theme);
        let palette = theme.palette().unwrap();
        assert_eq!(label(&host, plain).color, palette.text);
        assert!(!label(&host, plain).outline);
        assert_eq!(label(&host, status).color, bound, "a bound colour stays");
        assert_eq!(label(&host, muted).color, palette.muted);
        assert_eq!(label(&host, plain).text, "ExactVariableName");
    }
    c.sync(&mut host.root, &view, 4, &mut NoIcons, PluginTheme::Classic);
    assert_eq!(label(&host, plain).color, original);
    assert_eq!(label(&host, muted).color, original_muted);
    assert!(label(&host, plain).outline);
    let pick = named(&host.root, c.root, "Pick").unwrap();
    assert!(
        host.root
            .widget::<crate::widgets::Menu>(pick)
            .unwrap()
            .searchable
    );
}

#[test]
fn a_window_that_does_not_opt_in_keeps_classic_when_the_theme_changes() {
    // PluginUiThemeTests.NonOptedInWindowKeepsClassicWhenGlobalThemeChanges.
    let (mut host, mut c, view) = theme_window(false);
    c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Moss);
    let plain = named(&host.root, c.root, "Plain").unwrap();
    let w = host.root.widget::<MarkupLabel>(plain).unwrap();
    assert!(w.outline);
    assert_eq!(w.color, Color::WHITE);
}

// ── The bundled font under the modern themes (as in OpenAC) ─────────────

fn same(a: Option<&Arc<UiFont>>, b: &Arc<UiFont>) -> bool {
    a.is_some_and(|a| Arc::ptr_eq(a, b))
}

fn font_window(
    markup: &str,
    classic: &Arc<UiFont>,
    modern: &Arc<UiFont>,
) -> (UiHost, PluginPanelController, PanelView) {
    let mut state = PluginUi::new();
    let schema = BindingSchema::new("Binding")
        .with("Items", K::TextList)
        .with("Selected", K::Text)
        .with("Row", K::Int)
        .with("On", K::Bool);
    let id = state
        .register_panel(
            &UiOwner::new("test.fonts", "Fonts"),
            PanelDescriptor::new("f", "Fonts"),
            markup,
            schema,
        )
        .unwrap();
    state
        .set_values(
            "test.fonts",
            id,
            vec![
                ("Items".into(), UiValue::TextList(vec!["A".into()])),
                ("Selected".into(), UiValue::Text("A".into())),
                ("Row".into(), UiValue::Int(-1)),
            ],
        )
        .unwrap();
    let view = state.panels().next().unwrap().clone();
    let mut host = UiHost::new();
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let c = build_panel(&mut host.root, &view, Some(Arc::clone(classic)), outbox)
        .with_modern_fonts(Some(ModernFonts {
            regular: Arc::clone(modern),
            semibold: Arc::clone(modern),
        }));
    let root = host.root.root();
    host.root.add_child(root, c.root);
    host.root
        .register_window(&c.window_name, c.root, Some(c.root), 0)
        .unwrap();
    (host, c, view)
}

/// Two fonts as OpenAC's test makes them: the bundled font at 16 and at
/// 12 pixels.
fn bundled_fonts() -> (Arc<UiFont>, Arc<UiFont>) {
    use crate::bundled_font::{Weight, bake};
    let modern = bake(16.0, Weight::Regular)
        .unwrap()
        .create_font(TextureRef::Runtime(1));
    let classic = bake(12.0, Weight::Regular)
        .unwrap()
        .create_font(TextureRef::Runtime(2));
    (Arc::new(modern), Arc::new(classic))
}

#[test]
fn theme_font_switch_restores_classic_font() {
    // BundledUiFontTests.ThemeFontSwitchRestoresClassicFont.
    let (modern, classic) = bundled_fonts();
    let (mut host, mut c, view) = font_window(
        r#"<panel theme="plugin" x="0" y="0" w="200" h="100" title="Title">
  <label name="L" x="1" y="20" text="BuffProfile_Banes" />
  <field name="F" x="1" y="40" w="150" h="24" />
</panel>"#,
        &classic,
        &modern,
    );
    c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Classic);
    let label = named(&host.root, c.root, "L").unwrap();
    let field = named(&host.root, c.root, "F").unwrap();
    let title = c.title.unwrap();
    let font_of = |host: &UiHost, id| host.root.widget::<MarkupLabel>(id).unwrap().font.clone();
    let field_font = |host: &UiHost| {
        host.root
            .widget::<MarkupField>(field)
            .unwrap()
            .inner
            .font
            .clone()
    };
    assert!(same(font_of(&host, label).as_ref(), &classic));
    c.sync(&mut host.root, &view, 2, &mut NoIcons, PluginTheme::Moss);
    assert!(same(font_of(&host, label).as_ref(), &modern));
    assert!(same(font_of(&host, title).as_ref(), &modern));
    assert!(same(field_font(&host).as_ref(), &modern));
    c.sync(&mut host.root, &view, 3, &mut NoIcons, PluginTheme::Classic);
    assert!(same(font_of(&host, label).as_ref(), &classic));
    assert!(same(field_font(&host).as_ref(), &classic));
}

/// On a high-density display a themed window's text draws from the sharper
/// bake and nothing moves; a Classic window's text draws exactly as it does
/// without one.
#[test]
fn only_a_themed_windows_text_takes_the_sharper_bake_and_nothing_moves() {
    use crate::bundled_font::{Weight, bake};
    use crate::draw::{DrawCmd, DrawList, NoSprites};
    use crate::font::{SharpFont, SharpFonts};

    const MARKUP: &str = r#"<panel theme="plugin" x="10" y="10" w="200" h="100" title="Title">
  <label name="L" x="1" y="20" text="Buff Bot" />
  <button name="B" x="1" y="40" w="80" h="24" text="Go" />
</panel>"#;
    const SHARP: TextureRef = TextureRef::Runtime(9);
    let (modern, classic) = bundled_fonts();
    let twin = bake(32.0, Weight::Regular).unwrap().create_font(SHARP);
    let fonts = Arc::new(SharpFonts::from([(
        modern.id,
        SharpFont {
            font: Arc::new(twin),
            scale: 2.0,
        },
    )]));
    let textures = |list: &DrawList| -> Vec<TextureRef> {
        list.commands()
            .filter_map(|c| match c {
                DrawCmd::Glyphs { texture, .. } => Some(*texture),
                _ => None,
            })
            .collect()
    };
    let screen = (640.0, 480.0);

    let (mut host, mut c, view) = font_window(MARKUP, &classic, &modern);
    c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Moss);
    let places = |host: &UiHost, c: &PluginPanelController| {
        ["L", "B"].map(|n| {
            let id = named(&host.root, c.root, n).unwrap();
            let e = host.root.element(id).unwrap();
            (e.left, e.top, e.width, e.height)
        })
    };
    host.root.draw(screen, &NoSprites);
    let before = places(&host, &c);
    let standard = host.root.draw(screen, &NoSprites);
    assert!(!textures(&standard).is_empty());
    assert!(!textures(&standard).contains(&SHARP));

    host.root.set_sharp_fonts(Some(Arc::clone(&fonts)));
    // Told of the bake, a standard display still draws the font's own glyphs.
    assert_eq!(host.root.draw(screen, &NoSprites), standard);
    host.root.set_pixels_per_unit(2.0);
    let dense = host.root.draw(screen, &NoSprites);
    assert!(textures(&dense).iter().all(|t| *t == SHARP));
    assert_eq!(places(&host, &c), before);

    // Under Classic the window keeps its own font, which has no sharper bake.
    c.sync(&mut host.root, &view, 2, &mut NoIcons, PluginTheme::Classic);
    let with_bake = host.root.draw(screen, &NoSprites);
    host.root.set_sharp_fonts(None);
    let without = host.root.draw(screen, &NoSprites);
    assert_eq!(with_bake, without);
    assert!(!textures(&with_bake).contains(&SHARP));
}

/// OpenAC `MarkupDocument.Build`: the title label takes `controls.ini`'s
/// `[title] color` (white without it), and `PluginMarkupTheme` gives a
/// themed window's title the theme's text colour under a modern theme and
/// that colour back under Classic; a window that does not opt in keeps it.
#[test]
fn the_title_takes_the_controls_ini_colour_where_no_theme_colours_it() {
    use crate::controls_ini::ControlsIni;
    let (modern, classic) = bundled_fonts();
    let sheet = ControlsIni::parse("[title]\ncolor=#FF4F657D\n");
    let colour = sheet.color("title", "color").unwrap();
    let title_colour = |host: &UiHost, c: &PluginPanelController| {
        host.root
            .widget::<MarkupLabel>(c.title.unwrap())
            .unwrap()
            .color
    };
    for themed in [true, false] {
        let theme = if themed { r#" theme="plugin""# } else { "" };
        let (mut host, c, view) = font_window(
            &format!(r#"<panel{theme} x="0" y="0" w="200" h="100" title="Title"></panel>"#),
            &classic,
            &modern,
        );
        assert_eq!(title_colour(&host, &c), Color::WHITE);
        let mut c = c.with_title_color(&mut host.root, colour);
        assert_eq!(title_colour(&host, &c), colour);
        c.sync(&mut host.root, &view, 1, &mut NoIcons, PluginTheme::Classic);
        assert_eq!(title_colour(&host, &c), colour);
        c.sync(&mut host.root, &view, 2, &mut NoIcons, PluginTheme::Moss);
        let moss = PluginTheme::Moss.palette().unwrap().text;
        let expected = if themed { moss } else { colour };
        assert_eq!(title_colour(&host, &c), expected, "themed {themed}");
        c.sync(&mut host.root, &view, 3, &mut NoIcons, PluginTheme::Classic);
        assert_eq!(title_colour(&host, &c), colour);
    }
}

/// OpenAC `PluginMarkupTheme`'s font bindings: under a modern theme the
/// buttons, tabs, toggles, menus (and their captions), logs and lists of a
/// themed window draw in the bundled font, a meter and the dock's
/// minimize button keep theirs; a window that does not opt in keeps its
/// font under every theme.
#[test]
fn every_text_control_of_a_themed_window_takes_the_bundled_font() {
    let (modern, classic) = bundled_fonts();
    let controls = r#"
  <button name="B" x="0" y="0" w="60" h="20" text="b" />
  <tab name="T" x="0" y="20" w="60" h="20" text="t" selected="{On}" />
  <toggle name="G" x="0" y="40" w="60" h="20" text="g" checked="{On}" />
  <menu name="M" x="0" y="60" w="100" h="20" items="{Items}" selected="{Selected}" />
  <log name="O" x="0" y="80" w="100" h="40" items="{Items}" />
  <list name="I" x="0" y="120" w="100" h="40" items="{Items}" selected="{Row}" />
  <meter name="E" x="0" y="160" w="100" h="10" fill="0.5" />"#;
    for (opt_in, theme) in [
        (true, PluginTheme::Moss),
        (true, PluginTheme::Brass),
        (false, PluginTheme::Moss),
    ] {
        let markup = format!(
            r#"<panel x="0" y="0" w="250" h="200"{}>{controls}</panel>"#,
            if opt_in { r#" theme="plugin""# } else { "" }
        );
        let (mut host, mut c, view) = font_window(&markup, &classic, &modern);
        c.sync(&mut host.root, &view, 1, &mut NoIcons, theme);
        let expect = if opt_in { &modern } else { &classic };
        let [b, t, g, m, o, i, e] =
            ["B", "T", "G", "M", "O", "I", "E"].map(|n| named(&host.root, c.root, n).unwrap());
        let ui = &host.root;
        assert!(same(
            ui.widget::<MarkupButton>(b).unwrap().font.as_ref(),
            expect
        ));
        assert!(same(
            ui.widget::<MarkupButton>(t).unwrap().font.as_ref(),
            expect
        ));
        assert!(same(
            ui.widget::<MarkupToggle>(g).unwrap().font.as_ref(),
            expect
        ));
        let menu = ui.widget::<Menu>(m).unwrap();
        assert!(same(menu.font.as_ref(), expect));
        if opt_in {
            assert!(same(menu.button_font.as_ref(), &modern));
        } else {
            assert!(menu.button_font.is_none());
        }
        assert!(same(
            ui.widget::<MarkupLog>(o).unwrap().font.as_ref(),
            expect
        ));
        assert!(same(
            ui.widget::<MarkupList>(i).unwrap().font.as_ref(),
            expect
        ));
        assert!(same(ui.widget::<Meter>(e).unwrap().font.as_ref(), &classic));
        c.sync(&mut host.root, &view, 2, &mut NoIcons, PluginTheme::Classic);
        let ui = &host.root;
        let menu = ui.widget::<Menu>(m).unwrap();
        assert!(same(menu.font.as_ref(), &classic));
        assert!(menu.button_font.is_none());
        assert!(same(
            ui.widget::<MarkupLog>(o).unwrap().font.as_ref(),
            &classic
        ));
    }
}

#[test]
fn a_canvas_holds_the_pointer_from_press_to_release_and_reports_a_lost_press() {
    // OpenAC PluginCanvasPointerTests: down, held moves (outside too), up;
    // the plugin's release ends the press with no cancel; a canvas hidden
    // mid-press reports it cancelled.
    use crate::overlay::OverlayHost;
    use ac_plugin_ui::{CanvasAnchor, CanvasDescriptor, PluginUiView, PointerKind};
    let mut state = PluginUi::new();
    let owner = UiOwner::new("test.canvas", "Canvas");
    let id = state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "map".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::TopLeft,
                offset: (200.0, 100.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    assert!(state.listen_canvas("test.canvas", id, true));
    let mut view = PluginUiView::new();
    view.apply_all(state.snapshot());
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let mut band = OverlayHost::mount(&mut host.root);
    let mut layer = canvas::CanvasLayer::mount(&mut host.root, &mut band);
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut no_image = |_: &str, _: u32| None;
    layer.sync(
        &mut host.root,
        &band,
        &view,
        &mut no_image,
        &mut |_| unreachable!("no fonts"),
        None,
        &outbox,
    );
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    host.mouse_move(400, 300);
    host.mouse_up(MouseButton::Left, 400, 300, 0);
    let kinds: Vec<(PointerKind, f32, f32)> = outbox
        .borrow_mut()
        .drain(..)
        .filter_map(|i| match i {
            UiInput::Pointer { canvas, event, .. } if canvas == id => {
                Some((event.kind, event.x, event.y))
            }
            _ => None,
        })
        .collect();
    assert_eq!(
        kinds,
        vec![
            (PointerKind::Down, 10.0, 10.0),
            (PointerKind::Move, 200.0, 200.0),
            (PointerKind::Up, 200.0, 200.0)
        ]
    );
    // The plugin releases mid-press: no cancel.
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    assert!(state.release_canvas_pointer("test.canvas", id));
    view.apply_all(state.take_changes());
    layer.sync(
        &mut host.root,
        &band,
        &view,
        &mut no_image,
        &mut |_| unreachable!("no fonts"),
        None,
        &outbox,
    );
    host.mouse_up(MouseButton::Left, 210, 110, 0);
    let kinds: Vec<PointerKind> = outbox
        .borrow_mut()
        .drain(..)
        .filter_map(|i| match i {
            UiInput::Pointer { event, .. } => Some(event.kind),
            _ => None,
        })
        .collect();
    assert_eq!(kinds, vec![PointerKind::Down]);
    // Hidden mid-press: cancelled.
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    assert!(state.place_canvas("test.canvas", id, Some(false), None, None));
    view.apply_all(state.take_changes());
    layer.sync(
        &mut host.root,
        &band,
        &view,
        &mut no_image,
        &mut |_| unreachable!("no fonts"),
        None,
        &outbox,
    );
    host.pump();
    let kinds: Vec<PointerKind> = outbox
        .borrow_mut()
        .drain(..)
        .filter_map(|i| match i {
            UiInput::Pointer { event, .. } => Some(event.kind),
            _ => None,
        })
        .collect();
    assert_eq!(kinds, vec![PointerKind::Down, PointerKind::Cancelled]);
}

#[test]
fn a_canvas_hears_the_pointer_hover_with_no_button_held_and_one_leave() {
    // A buttonless move over a listening canvas is a hover; a move with
    // the button held stays a move; the pointer going off it, the canvas
    // hiding, and a press after the canvas moved from under the pointer
    // each end the hover with one leave.
    use crate::overlay::OverlayHost;
    use ac_plugin_ui::{CanvasAnchor, CanvasDescriptor, PluginUiView, PointerKind};
    let mut state = PluginUi::new();
    let owner = UiOwner::new("test.canvas", "Canvas");
    let id = state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "map".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::TopLeft,
                offset: (200.0, 100.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    assert!(state.listen_canvas("test.canvas", id, true));
    let mut view = PluginUiView::new();
    view.apply_all(state.snapshot());
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let mut band = OverlayHost::mount(&mut host.root);
    let mut layer = canvas::CanvasLayer::mount(&mut host.root, &mut band);
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut no_image = |_: &str, _: u32| None;
    let mut sync = |host: &mut UiHost, view: &PluginUiView| {
        layer.sync(
            &mut host.root,
            &band,
            view,
            &mut no_image,
            &mut |_| unreachable!("no fonts"),
            None,
            &outbox,
        );
    };
    sync(&mut host, &view);
    let heard = |outbox: &PluginOutbox| -> Vec<(PointerKind, f32, f32, u8)> {
        outbox
            .borrow_mut()
            .drain(..)
            .filter_map(|i| match i {
                UiInput::Pointer { canvas, event, .. } if canvas == id => {
                    Some((event.kind, event.x, event.y, event.button))
                }
                _ => None,
            })
            .collect()
    };

    host.mouse_move(210, 110);
    host.mouse_move(220, 115);
    host.mouse_down(MouseButton::Left, 220, 115, 0);
    host.mouse_move(230, 120);
    host.mouse_up(MouseButton::Left, 230, 120, 0);
    host.mouse_move(240, 120);
    host.mouse_move(400, 300);
    host.mouse_move(500, 300);
    assert_eq!(
        heard(&outbox),
        vec![
            (PointerKind::Hover, 10.0, 10.0, 0),
            (PointerKind::Hover, 20.0, 15.0, 0),
            (PointerKind::Down, 20.0, 15.0, 1),
            (PointerKind::Move, 30.0, 20.0, 1),
            (PointerKind::Up, 30.0, 20.0, 1),
            (PointerKind::Hover, 40.0, 20.0, 0),
            (PointerKind::Leave, 200.0, 200.0, 0),
        ]
    );

    // Hidden under the pointer: one leave.
    host.mouse_move(210, 110);
    assert!(state.place_canvas("test.canvas", id, Some(false), None, None));
    view.apply_all(state.take_changes());
    sync(&mut host, &view);
    host.pump();
    let kinds: Vec<PointerKind> = heard(&outbox).into_iter().map(|h| h.0).collect();
    assert_eq!(kinds, vec![PointerKind::Hover, PointerKind::Leave]);

    // Shown again and hovered, then moved from under a pointer that does
    // not move: the press there ends the hover.
    assert!(state.place_canvas("test.canvas", id, Some(true), None, None));
    view.apply_all(state.take_changes());
    sync(&mut host, &view);
    host.mouse_move(211, 111);
    assert!(state.place_canvas("test.canvas", id, None, None, Some((500.0, 400.0))));
    view.apply_all(state.take_changes());
    sync(&mut host, &view);
    host.mouse_down(MouseButton::Left, 211, 111, 0);
    host.mouse_up(MouseButton::Left, 211, 111, 0);
    let kinds: Vec<PointerKind> = heard(&outbox).into_iter().map(|h| h.0).collect();
    assert_eq!(kinds, vec![PointerKind::Hover, PointerKind::Leave]);
}

#[test]
fn a_canvas_is_told_which_modifier_keys_each_pointer_event_came_with() {
    use crate::event::Modifiers;
    use crate::overlay::OverlayHost;
    use ac_plugin_ui::{CanvasAnchor, CanvasDescriptor, PluginUiView, PointerKind};
    let mut state = PluginUi::new();
    let owner = UiOwner::new("test.canvas", "Canvas");
    let id = state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "hud".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::TopLeft,
                offset: (200.0, 100.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    assert!(state.listen_canvas("test.canvas", id, true));
    let mut view = PluginUiView::new();
    view.apply_all(state.snapshot());
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let mut band = OverlayHost::mount(&mut host.root);
    let mut layer = canvas::CanvasLayer::mount(&mut host.root, &mut band);
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut no_image = |_: &str, _: u32| None;
    layer.sync(
        &mut host.root,
        &band,
        &view,
        &mut no_image,
        &mut |_| unreachable!("no fonts"),
        None,
        &outbox,
    );

    // A Ctrl+drag, ending with Ctrl let go before the button comes up.
    host.set_modifiers(Modifiers::CTRL);
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    host.mouse_move(250, 130);
    host.set_modifiers(Modifiers::NONE);
    host.mouse_up(MouseButton::Left, 250, 130, 0);
    // The wheel with Shift and Alt (the move there is a hover), and a plain
    // click.
    host.set_modifiers(Modifiers::SHIFT | Modifiers::ALT);
    host.mouse_move(210, 110);
    host.root.on_scroll(1);
    host.set_modifiers(Modifiers::NONE);
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    host.mouse_up(MouseButton::Left, 210, 110, 0);
    host.pump();

    let events: Vec<(PointerKind, u8)> = outbox
        .borrow_mut()
        .drain(..)
        .filter_map(|i| match i {
            UiInput::Pointer { event, .. } => Some((event.kind, event.modifiers)),
            _ => None,
        })
        .collect();
    assert_eq!(
        events,
        vec![
            (PointerKind::Down, 2),
            (PointerKind::Move, 2),
            (PointerKind::Up, 0),
            (PointerKind::Hover, 5),
            (PointerKind::Wheel, 5),
            (PointerKind::Down, 0),
            (PointerKind::Up, 0),
        ]
    );
}

#[test]
fn a_canvas_dragged_through_the_session_follows_the_pointer_without_overshooting() {
    // A plugin moves its canvas by where the pointer is from the grabbed
    // point, as a HUD does. The session and the window each see the other's
    // news a frame late, which is what a live drag measures.
    use crate::overlay::OverlayHost;
    use ac_plugin_ui::{CanvasAnchor, CanvasDescriptor, PluginUiView, PointerKind, UiChange};
    use std::collections::VecDeque;
    let mut state = PluginUi::new();
    let owner = UiOwner::new("test.canvas", "Canvas");
    let id = state
        .register_canvas(
            &owner,
            CanvasDescriptor {
                canvas_id: "hud".into(),
                width: 100,
                height: 50,
                anchor: CanvasAnchor::TopLeft,
                offset: (200.0, 100.0),
                start_visible: true,
                accepts_pointer_input: true,
            },
        )
        .unwrap();
    assert!(state.listen_canvas("test.canvas", id, true));
    let mut view = PluginUiView::new();
    view.apply_all(state.snapshot());
    state.take_changes();
    let mut host = UiHost::new();
    host.root.set_size(800.0, 600.0);
    let mut band = OverlayHost::mount(&mut host.root);
    let mut layer = canvas::CanvasLayer::mount(&mut host.root, &mut band);
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let mut no_image = |_: &str, _: u32| None;
    layer.sync(
        &mut host.root,
        &band,
        &view,
        &mut no_image,
        &mut |_| unreachable!("no fonts"),
        None,
        &outbox,
    );

    let mut to_session: VecDeque<Vec<UiInput>> = VecDeque::new();
    let mut to_window: VecDeque<Vec<UiChange>> = VecDeque::new();
    let mut offset = (200.0_f32, 100.0_f32);
    let mut grab = None;
    let mut worst = 0.0_f32;
    host.mouse_down(MouseButton::Left, 210, 110, 0);
    for frame in 0..120 {
        // Moving for a second, then held still for the rest.
        let step = frame.min(60);
        let pointer = (210 + step * 4, 110 + step * 2);
        host.mouse_move(pointer.0, pointer.1);
        to_session.push_back(outbox.borrow_mut().drain(..).collect());
        if to_session.len() > 1 {
            for input in to_session.pop_front().unwrap() {
                state.input(input);
            }
        }
        for (_, event) in state.take_events() {
            let event = state.pointer_as_placed_now(event);
            let ac_plugin_ui::UiEvent::Pointer { event, .. } = event else {
                continue;
            };
            match (event.kind, grab) {
                (PointerKind::Down, _) => grab = Some((event.x, event.y)),
                (PointerKind::Move, Some((gx, gy))) => {
                    offset = (offset.0 + event.x - gx, offset.1 + event.y - gy);
                    assert!(state.place_canvas("test.canvas", id, None, None, Some(offset)));
                }
                _ => {}
            }
        }
        to_window.push_back(state.take_changes());
        if to_window.len() > 1 {
            view.apply_all(to_window.pop_front().unwrap());
            layer.sync(
                &mut host.root,
                &band,
                &view,
                &mut no_image,
                &mut |_| unreachable!("no fonts"),
                None,
                &outbox,
            );
        }
        // The canvas keeps the grabbed point under the pointer, at most late.
        let target = (pointer.0 as f32 - 10.0, pointer.1 as f32 - 10.0);
        worst = worst.max(offset.0 - target.0).max(offset.1 - target.1);
    }
    assert_eq!(worst, 0.0, "the canvas ran ahead of the pointer");
    assert_eq!(offset, (440.0, 220.0));
}

// ── The dock under the plugin themes (as in OpenAC) ──────────────────────

#[test]
fn a_right_click_on_a_dock_slot_asks_for_appearance_without_toggling_the_plugin() {
    // PluginSidePanelThemeTests.RightClickOnEntryOpensAppearanceWithoutTogglingPlugin.
    use super::dock::{self, PluginDock};
    for theme in [PluginTheme::Classic, PluginTheme::Moss, PluginTheme::Brass] {
        let (state, _) = session();
        let mut host = UiHost::new();
        host.root.set_size(800.0, 600.0);
        let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
        let mut dock_controller = PluginDock::build(&mut host.root, None, None, Rc::clone(&outbox));
        let frame = dock_controller.root;
        let top = host.root.root();
        host.root.add_child(top, frame);
        host.root
            .register_window(dock::WINDOW, frame, Some(frame), 0)
            .unwrap();
        let panels: Vec<PanelView> = state.panels().cloned().collect();
        dock_controller.set_theme(&mut host.root, theme);
        dock_controller.sync(&mut host.root, &panels, &mut NoDockIcons);
        let button = dock_controller.slot(panels[0].id).unwrap();
        host.attach_controller(dock::WINDOW, Box::new(dock_controller))
            .unwrap();
        let (x, y) = host.root.screen_position(button);
        let e = host.root.element(button).unwrap();
        let (x, y) = ((x + e.width / 2.0) as i32, (y + e.height / 2.0) as i32);
        host.mouse_down(MouseButton::Right, x, y, 0);
        host.mouse_up(MouseButton::Right, x, y, 0);
        host.pump();
        let asked = host
            .with_controller::<PluginDock, _>(dock::WINDOW, |c, _| c.take_appearance_request())
            .unwrap();
        assert!(asked, "{theme:?}");
        assert!(outbox.borrow().is_empty(), "the plugin is not toggled");
    }
}

// ── Keyboard focus in markup panels (as in OpenAC) ──────────────────────

const KEYBOARD_MARKUP: &str = r#"<panel x="0" y="0" w="300" h="120">
  <field x="8" y="8" w="100" h="20"/>
  <button x="8" y="36" w="80" h="20" text="Go" onclick="{Activate}"/>
  <button x="8" y="64" w="80" h="20" text="Hidden" visible="{ShowHidden}"/>
  <tab x="120" y="36" w="80" h="20" text="Tab" selected="{Selected}" onclick="{Activate}"/>
  <toggle x="120" y="64" w="80" h="20" text="Toggle" checked="{Selected}" onclick="{Activate}"/>
</panel>"#;

fn keyboard_panel() -> (UiHost, PluginOutbox, ElementId) {
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("KeyboardMarkupBinding")
        .with("Activate", K::Action)
        .with("ShowHidden", K::Bool)
        .with("Selected", K::Bool);
    let id = ui
        .register_panel(
            &UiOwner::new("test.keys", "Keys"),
            PanelDescriptor::new("main", "Keys"),
            KEYBOARD_MARKUP,
            schema,
        )
        .unwrap();
    ui.set_values(
        "test.keys",
        id,
        vec![
            ("ShowHidden".into(), UiValue::Bool(false)),
            ("Selected".into(), UiValue::Bool(false)),
        ],
    )
    .unwrap();
    let view = ui.panels().next().unwrap().clone();
    mounted(&view)
}

fn activations(outbox: &PluginOutbox) -> usize {
    outbox
        .borrow()
        .iter()
        .filter(|i| matches!(i, UiInput::Action { handler, .. } if handler == "Activate"))
        .count()
}

#[test]
fn tab_moves_through_the_visible_controls_and_enter_activates_a_button() {
    // RetailWindowManagerTests.MarkupTabMovesThroughVisibleControlsAndEnterActivatesButton.
    use crate::event::Key;
    let (mut host, outbox, frame) = keyboard_panel();
    let (x, y) = host.root.screen_position(frame);
    click(&mut host, x as i32 + 20, y as i32 + 14);
    let field = host.root.keyboard_focus().expect("the field has focus");
    press(&mut host, Key::Tab);
    let button = host.root.keyboard_focus().unwrap();
    assert_ne!(button, field);
    assert!(
        host.root
            .widget::<MarkupButton>(button)
            .unwrap()
            .keyboard_focused
    );
    press(&mut host, Key::Enter);
    assert_eq!(activations(&outbox), 1);
    // The hidden button is skipped: the tab, the toggle, then back round.
    press(&mut host, Key::Tab);
    assert!(
        !host
            .root
            .widget::<MarkupButton>(button)
            .unwrap()
            .keyboard_focused
    );
    press(&mut host, Key::Tab);
    let toggle = host.root.keyboard_focus().unwrap();
    assert!(host.root.widget::<MarkupToggle>(toggle).is_some());
    press(&mut host, Key::Space);
    assert_eq!(activations(&outbox), 2);
    press(&mut host, Key::Tab);
    assert_eq!(host.root.keyboard_focus(), Some(field));
    // Shift+Tab goes back.
    host.key_down(Key::Tab, crate::event::Modifiers::SHIFT);
    assert_eq!(host.root.keyboard_focus(), Some(toggle));
}

#[test]
fn clicking_a_markup_control_leaves_the_keyboard_to_the_game() {
    // MarkupKeyboardCaptureTests.ClickingMarkupActionKeepsGameBindingsAndAutomationAvailable:
    // a click on a button, tab or toggle acts without taking keyboard focus,
    // so Space after it is the game's, not a second activation.
    use crate::event::Key;
    for (dx, dy) in [(12, 40), (124, 40), (124, 68)] {
        let (mut host, outbox, frame) = keyboard_panel();
        let (x, y) = host.root.screen_position(frame);
        click(&mut host, x as i32 + dx, y as i32 + dy);
        assert_eq!(activations(&outbox), 1, "({dx}, {dy})");
        assert!(host.root.keyboard_focus().is_none());
        assert!(!host.root.wants_keyboard());
        host.take_outputs();
        press(&mut host, Key::Space);
        assert_eq!(activations(&outbox), 1);
        assert!(host.take_outputs().iter().any(|o| matches!(
            o,
            crate::root::UiOutput::WorldKey {
                key: Key::Space,
                ..
            }
        )));
    }
}

// ── Field onblur (OpenAC v0.1.25) ──────────

/// OpenAC `MarkupDocumentTests` `BlurPanel`: a field reporting `onblur`,
/// a plain field, a button; `submit` adds `onsubmit`, `clear` adds
/// `clearonsubmit`.
fn blur_panel(submit: bool, clear: bool) -> (UiHost, PluginOutbox, u32, ElementId) {
    let field = format!(
        r#"<field x="4" y="4" w="120" h="20" onblur="{{Blur}}"{}{}/>"#,
        if submit {
            r#" onsubmit="{Submit}""#
        } else {
            ""
        },
        if clear {
            r#" clearonsubmit="true""#
        } else {
            ""
        },
    );
    let markup = format!(
        r#"<panel x="0" y="0" w="240" h="120">
  {field}
  <field x="4" y="32" w="120" h="20"/>
  <button x="4" y="60" w="60" h="20" text="Go" onclick="{{Press}}"/>
</panel>"#
    );
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("BlurBinding")
        .with("Submit", K::TextAction)
        .with("Blur", K::TextAction)
        .with("Press", K::Action);
    let panel = ui
        .register_panel(
            &UiOwner::new("test.blur", "Blur"),
            PanelDescriptor::new("main", "Blur"),
            &markup,
            schema,
        )
        .unwrap();
    let view = ui.panels().next().unwrap().clone();
    let (mut host, outbox, frame) = mounted(&view);
    let (x, y) = host.root.screen_position(frame);
    let at = |dx: i32, dy: i32| (x as i32 + dx, y as i32 + dy);
    let (fx, fy) = at(20, 10);
    click(&mut host, fx, fy);
    assert!(host.root.keyboard_focus().is_some());
    (host, outbox, panel, frame)
}

fn typed(host: &mut UiHost, text: &str) {
    for c in text.chars() {
        host.char_typed(c);
    }
}

fn press(host: &mut UiHost, key: crate::event::Key) {
    host.key_down(key, crate::event::Modifiers::NONE);
    host.key_up(key, crate::event::Modifiers::NONE);
}

/// The texts each handler received, in order.
fn handed(outbox: &PluginOutbox, name: &str) -> Vec<String> {
    outbox
        .borrow()
        .iter()
        .filter_map(|i| match i {
            UiInput::Action {
                handler,
                value: ActionValue::Text(t),
                ..
            } if handler == name => Some(t.clone()),
            _ => None,
        })
        .collect()
}

#[test]
fn a_field_onblur_receives_the_typed_text_when_focus_moves_away() {
    // FieldOnBlurReceivesTheTypedTextWhenFocusMovesAway.
    use crate::event::Key;
    for how in [
        "empty space",
        "other field",
        "button",
        "escape",
        "tab",
        "hide",
    ] {
        let (mut host, outbox, _, frame) = blur_panel(false, false);
        typed(&mut host, "abc");
        assert!(handed(&outbox, "Blur").is_empty(), "{how}");
        let (x, y) = host.root.screen_position(frame);
        let (x, y) = (x as i32, y as i32);
        match how {
            "empty space" => click(&mut host, x + 200, y + 100),
            "other field" => click(&mut host, x + 20, y + 40),
            "button" => click(&mut host, x + 20, y + 68),
            "escape" => press(&mut host, Key::Escape),
            "tab" => press(&mut host, Key::Tab),
            _ => {
                host.root.hide_window("plugin:test.blur:main");
                host.pump();
            }
        }
        assert_eq!(handed(&outbox, "Blur"), ["abc"], "{how}");
    }
}

#[test]
fn enter_submits_once_and_does_not_also_blur() {
    // EnterSubmitsOnceAndDoesNotAlsoBlur.
    use crate::event::Key;
    let (mut host, outbox, _, frame) = blur_panel(true, false);
    typed(&mut host, "abc");
    press(&mut host, Key::Enter);
    assert!(host.root.keyboard_focus().is_none());
    assert_eq!(handed(&outbox, "Submit"), ["abc"]);
    assert!(handed(&outbox, "Blur").is_empty());
    // Editing again and leaving reports the new text.
    let (x, y) = host.root.screen_position(frame);
    let (x, y) = (x as i32, y as i32);
    click(&mut host, x + 20, y + 10);
    press(&mut host, Key::End);
    typed(&mut host, "d");
    click(&mut host, x + 200, y + 100);
    assert_eq!(handed(&outbox, "Submit"), ["abc"]);
    assert_eq!(handed(&outbox, "Blur"), ["abcd"]);
}

#[test]
fn enter_on_a_clearing_field_does_not_blur_with_the_cleared_text() {
    // EnterOnAClearingFieldDoesNotBlurWithTheClearedText.
    use crate::event::Key;
    let (mut host, outbox, _, frame) = blur_panel(true, true);
    typed(&mut host, "abc");
    press(&mut host, Key::Enter);
    assert_eq!(handed(&outbox, "Submit"), ["abc"]);
    assert!(handed(&outbox, "Blur").is_empty());
    let (x, y) = host.root.screen_position(frame);
    let (x, y) = (x as i32, y as i32);
    click(&mut host, x + 20, y + 10);
    typed(&mut host, "xyz");
    click(&mut host, x + 200, y + 100);
    assert_eq!(handed(&outbox, "Blur"), ["xyz"]);
}

#[test]
fn a_field_with_only_onblur_applies_the_text_when_enter_leaves_it() {
    // FieldWithOnlyOnBlurAppliesTheTextWhenEnterLeavesIt.
    let (mut host, outbox, _, _) = blur_panel(false, false);
    typed(&mut host, "abc");
    press(&mut host, crate::event::Key::Enter);
    assert!(host.root.keyboard_focus().is_none());
    assert_eq!(handed(&outbox, "Blur"), ["abc"]);
}

#[test]
fn an_onblur_naming_no_text_action_is_refused() {
    // FieldOnBlurThatDoesNotResolveThrows.
    let mut ui = PluginUi::new();
    let refused = ui.register_panel(
        &UiOwner::new("test.blur", "Blur"),
        PanelDescriptor::new("main", "Blur"),
        r#"<panel x="0" y="0" w="240" h="120"><field x="4" y="4" w="120" h="20" onblur="{Missing}"/></panel>"#,
        BindingSchema::new("BlurBinding").with("Blur", K::TextAction),
    );
    let message = format!("{:?}", refused.unwrap_err());
    assert!(message.contains("onblur"), "{message}");
}

#[test]
fn a_tab_icon_draws_full_when_selected_dimmed_when_not_and_a_button_icon_is_never_dimmed() {
    // MarkupIconTests (OpenAC v0.1.25):
    // TabIcon_DrawsFullWhenSelected_AndDimmedWhenNot, ButtonIcon_IsNotDimmed.
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("TabIconBinding")
        .with("On", K::Bool)
        .with("Off", K::Bool);
    let panel = ui
        .register_panel(
            &UiOwner::new("test.tabs", "Tabs"),
            PanelDescriptor::new("main", "Tabs"),
            r#"<panel x="0" y="0" w="240" h="120">
  <tab x="4" y="4" w="60" h="20" text="A" selected="{On}" icon="0x06000001"/>
  <tab x="70" y="4" w="60" h="20" text="B" selected="{Off}" icon="0x06000002"/>
  <button x="4" y="40" w="60" h="20" text="Go" icon="7735"/>
</panel>"#,
            schema,
        )
        .unwrap();
    ui.set_values(
        "test.tabs",
        panel,
        vec![
            ("On".into(), UiValue::Bool(true)),
            ("Off".into(), UiValue::Bool(false)),
        ],
    )
    .unwrap();
    let view = ui.panels().next().unwrap().clone();
    let (mut host, _, _) = mounted(&view);
    let list = host.draw((1024.0, 768.0), &crate::draw::NoSprites);
    let tints = |id: u32| -> Vec<f32> {
        list.main
            .iter()
            .filter_map(|c| match c {
                crate::draw::DrawCmd::Sprite { texture, tint, .. }
                    if *texture == crate::draw::TextureRef::Surface(id) =>
                {
                    Some(tint.a)
                }
                _ => None,
            })
            .collect()
    };
    let (selected, unselected, button) =
        (tints(0x0600_0001), tints(0x0600_0002), tints(0x0600_1E37));
    assert!(!selected.is_empty() && selected.iter().all(|&a| (a - 1.0).abs() < 1e-3));
    assert!(
        !unselected.is_empty()
            && unselected
                .iter()
                .all(|&a| (a - widgets::INACTIVE_TAB_ICON_ALPHA).abs() < 1e-3)
    );
    assert!(!button.is_empty() && button.iter().all(|&a| (a - 1.0).abs() < 1e-3));
}

// ── The scrolling transcript (OpenAC `UiMarkupLogTests`) ─────────────────

/// A `<log>` window over the session's lines, as OpenAC's `Transcript`
/// fixture builds it; `tick` hands the session's values to the window and
/// draws it, which is when the log lays its rows out (OpenAC's `Tick`).
struct LogWindow {
    state: PluginUi,
    id: u32,
    host: UiHost,
    log: ElementId,
    revision: u64,
    lines: Vec<String>,
    first: i32,
}

impl LogWindow {
    fn new() -> Self {
        let markup = r#"<panel x="0" y="0" w="300" h="100">
  <log name="Log" x="0" y="0" w="280" h="88" items="{Lines}" firstindex="{First}"/>
</panel>"#;
        let mut state = PluginUi::new();
        let schema = BindingSchema::new("Transcript")
            .with("Lines", K::TextList)
            .with("First", K::Int);
        let id = state
            .register_panel(
                &UiOwner::new("test.log", "Log"),
                PanelDescriptor::new("log", "Log"),
                markup,
                schema,
            )
            .unwrap();
        let lines: Vec<String> = (0..30).map(|i| format!("Action {i}")).collect();
        state
            .set_values(
                "test.log",
                id,
                vec![
                    ("Lines".into(), UiValue::TextList(lines.clone())),
                    ("First".into(), UiValue::Int(0)),
                ],
            )
            .unwrap();
        let view = state.panels().next().unwrap().clone();
        let (host, _outbox, frame) = mounted(&view);
        let log = named(&host.root, frame, "Log").unwrap();
        let mut window = Self {
            state,
            id,
            host,
            log,
            revision: 1,
            lines,
            first: 0,
        };
        window.tick();
        window
    }

    fn tick(&mut self) {
        self.state
            .set_values(
                "test.log",
                self.id,
                vec![
                    ("Lines".into(), UiValue::TextList(self.lines.clone())),
                    ("First".into(), UiValue::Int(self.first)),
                ],
            )
            .unwrap();
        let view = self.state.panels().next().unwrap().clone();
        self.revision += 1;
        let revision = self.revision;
        self.host
            .with_controller::<PluginPanelController, _>(&view.window_name(), |c, ui| {
                c.sync(ui, &view, revision, &mut NoIcons, PluginTheme::Classic);
            });
        let _ = self.host.draw((1024.0, 768.0), &crate::draw::NoSprites);
    }

    fn log(&self) -> &MarkupLog {
        self.host.root.widget::<MarkupLog>(self.log).unwrap()
    }

    fn log_mut(&mut self) -> &mut MarkupLog {
        self.host.root.widget_mut::<MarkupLog>(self.log).unwrap()
    }
}

// UiMarkupLogTests.AppendFollowsOnlyAtBottomAndWheelScrollResumesAtEnd.
#[test]
fn a_log_follows_new_entries_only_at_the_bottom_and_resumes_at_the_end() {
    let mut w = LogWindow::new();
    assert!(w.log().scroll().at_end());
    w.lines.push("Next action".into());
    w.tick();
    assert!(w.log().scroll().at_end());
    w.host.mouse_move(20, 20);
    w.host.scroll(1);
    assert!(!w.log().following_end());
    let anchor = w.log().first_visible();
    let offset = w.log().scroll().scroll_y();
    w.lines.push("Another action".into());
    w.tick();
    assert_eq!(w.log().scroll().scroll_y(), offset);
    assert_eq!(w.log().first_visible(), anchor);
    w.log_mut().set_scroll_y(i32::MAX);
    w.lines.push("At bottom again".into());
    w.tick();
    assert!(w.log().scroll().at_end());
    assert!(w.log().following_end());
}

// UiMarkupLogTests.ScrollbarKeepsItsLayoutAndPausesFollowingThroughRootInput:
// a press on the bar (17 pixels in from the right edge) stops following,
// and a new entry leaves the reader where they were.
#[test]
fn a_press_on_the_log_scrollbar_pauses_following() {
    let mut w = LogWindow::new();
    let bar_left = 280 - 17;
    w.host.mouse_down(MouseButton::Left, bar_left + 5, 5, 0);
    w.host.mouse_up(MouseButton::Left, bar_left + 5, 5, 0);
    assert!(!w.log().following_end());
    let position = w.log().scroll().scroll_y();
    w.lines.push("Arrived during reading".into());
    w.tick();
    assert_eq!(w.log().scroll().scroll_y(), position);
}

// UiMarkupLogTests.TrimmingOldEntriesPreservesVisibleEntryAndDoesNotResumeFollowing.
#[test]
fn trimming_old_entries_keeps_the_visible_entry_and_does_not_resume_following() {
    let mut w = LogWindow::new();
    let line = w.log().scroll().line_height;
    w.log_mut().set_scroll_y(10 * line + 3);
    let visible = w.log().first_visible();
    w.lines.drain(..5);
    w.lines.push("New".into());
    w.first += 5;
    w.tick();
    assert_eq!(w.log().first_visible(), visible);
    assert!(!w.log().following_end());
    assert_eq!(w.log().scroll().scroll_y() % line, 3);
}

// UiMarkupLogTests.RemovedAnchorClampsToOldestWithoutFollowingNewEntries.
#[test]
fn a_removed_anchor_clamps_to_the_oldest_entry_without_following() {
    let mut w = LogWindow::new();
    w.log_mut().set_scroll_y(0);
    w.lines = vec!["Remaining".into()];
    w.first = 100;
    w.tick();
    assert!(!w.log().following_end());
    w.lines = (0..30).map(|i| format!("New {i}")).collect();
    w.tick();
    assert_eq!(w.log().scroll().scroll_y(), 0);
    assert!(!w.log().following_end());
}

// UiMarkupLogTests.ResizeRewrapsWhilePreservingReadingEntry.
#[test]
fn a_resized_log_rewraps_and_keeps_the_entry_being_read() {
    let mut w = LogWindow::new();
    w.lines = (0..30)
        .map(|i| {
            format!("Long action {i} with a target name that needs wrapping on narrow windows")
        })
        .collect();
    w.tick();
    let line = w.log().scroll().line_height;
    w.log_mut().set_scroll_y(10 * line);
    let entry = w.log().first_visible().map(|(e, _)| e);
    let element = w.host.root.element_mut(w.log).unwrap();
    element.width = 160.0;
    element.height = 70.0;
    w.tick();
    assert_eq!(w.log().first_visible().map(|(e, _)| e), entry);
    assert!(!w.log().following_end());
}

// ── A list column of buttons (OpenAC PR #223,
// `ListButtonColumnTests`; the widget's own cases are in
// `list_button_tests.rs`) ─────────────────────────────────────────────────

#[test]
fn a_list_button_reports_its_row_to_the_plugin_and_selects_nothing() {
    // ButtonColumn_BindsItsCaptionsAndOnclick,
    // PressAndReleaseOnAButton_FiresOnclickWithTheRow_WithoutSelecting,
    // EmptyCaption_IsNoButton_AndAClickOnItDoesNothing, through a plugin
    // window: names in x 0..100, buttons in 100..180, rows 18 high.
    let mut ui = PluginUi::new();
    let schema = BindingSchema::new("FoundBinding")
        .with("Names", K::TextList)
        .with("Captions", K::TextList)
        .with("Selected", K::Int)
        .with("Pick", K::IntAction)
        .with("Act", K::IntAction);
    let panel = ui
        .register_panel(
            &UiOwner::new("test.found", "Found"),
            PanelDescriptor::new("main", "Found"),
            r#"<panel x="0" y="0" w="200" h="100">
  <list x="0" y="0" w="180" h="60" rowheight="18" selected="{Selected}" onchange="{Pick}">
    <column type="text" width="100" items="{Names}"/>
    <column type="button" width="60" items="{Captions}" onclick="{Act}"/>
  </list>
</panel>"#,
            schema,
        )
        .unwrap();
    ui.set_values(
        "test.found",
        panel,
        vec![
            (
                "Names".into(),
                UiValue::TextList(vec!["Drudge".into(), "Rat".into(), "Mosswart".into()]),
            ),
            (
                "Captions".into(),
                UiValue::TextList(vec!["Select".into(), String::new(), "Select".into()]),
            ),
            ("Selected".into(), UiValue::Int(-1)),
        ],
    )
    .unwrap();
    let view = ui.panels().next().unwrap().clone();
    let (mut host, outbox, _) = mounted(&view);
    host.draw((1024.0, 768.0), &crate::draw::NoSprites);
    let list_at = |row: i32| 18 * row + 9;
    // The third row's button, then the second row's empty caption.
    host.mouse_move(140, list_at(2));
    click(&mut host, 140, list_at(2));
    host.mouse_move(140, list_at(1));
    click(&mut host, 140, list_at(1));
    assert_eq!(
        outbox.borrow_mut().drain(..).collect::<Vec<_>>(),
        vec![UiInput::Action {
            panel,
            handler: "Act".into(),
            value: ActionValue::Int(2)
        }]
    );
}
