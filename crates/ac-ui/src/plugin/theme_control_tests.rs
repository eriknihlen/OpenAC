//! Themed windows and controls draw the modern shapes and states.

use std::cell::RefCell;
use std::rc::Rc;
use std::sync::Arc;

use ac_plugin_ui::{DialogView, UiOwner};

use super::style::tests::{corners, has_color, same_rgb};
use super::style::{self};
use super::theme_tests::{
    CLASSIC_TEXTURE, Gallery, REGULAR_TEXTURE, SEMIBOLD_TEXTURE, find_named, test_font,
};
use super::title_bar::{self, TitleBar};
use super::*;
use crate::draw::{DrawCmd, DrawList, TextureRef};
use crate::event::{Key, Modifiers, MouseButton};

const P: Palette = Palette::MOSS;

fn window(attributes: &str, body: &str) -> Gallery {
    Gallery::of(
        &format!(
            r#"<panel x="100" y="100" w="200" h="120" theme="plugin" {attributes}>{body}</panel>"#
        ),
        PluginTheme::Moss,
    )
}

/// The atlases text was drawn from, with each string's lowest edge.
fn text_runs(list: &DrawList) -> Vec<(TextureRef, f32, f32)> {
    list.commands()
        .filter_map(|c| match c {
            DrawCmd::Glyphs { texture, quads, .. } => {
                let top = quads.iter().map(|q| q.y).fold(f32::MAX, f32::min);
                let left = quads.iter().map(|q| q.x).fold(f32::MAX, f32::min);
                Some((*texture, left, top))
            }
            _ => None,
        })
        .collect()
}

#[test]
fn a_titled_themed_window_draws_a_shadow_a_header_and_a_separator() {
    let mut g = window(r#"title="Buff Bot""#, "");
    let v = corners(&g.draw());
    assert!(v.iter().any(|(x, _, _)| *x < 100.0), "a shadow outside it");
    assert!(has_color(&v, style::mix(P.background, P.text, 0.06)));
    assert!(
        v.iter()
            .any(|(_, y, c)| (*y - 124.0).abs() < 0.01 && same_rgb(*c, P.border))
    );
}

#[test]
fn a_themed_window_has_round_corners() {
    let mut g = window(r#"title="Buff Bot""#, "");
    let v = corners(&g.draw());
    assert!(
        !v.iter()
            .any(|(x, y, c)| *x == 100.0 && *y == 100.0 && same_rgb(*c, P.background)),
        "no square corner"
    );
}

#[test]
fn an_untitled_themed_window_has_no_header() {
    let mut g = window("", "");
    assert!(!has_color(
        &corners(&g.draw()),
        style::mix(P.background, P.text, 0.06)
    ));
}

#[test]
fn a_title_moves_into_the_header_in_the_title_weight_and_back() {
    let mut g = window(r#"title="Buff Bot""#, "");
    // 14-pixel lines centre 5 down a 24-pixel band; each glyph starts 1 lower.
    let themed = text_runs(&g.draw());
    assert_eq!(
        themed,
        [(TextureRef::Surface(SEMIBOLD_TEXTURE), 112.0, 106.0)]
    );
    g.set_theme(PluginTheme::Classic);
    // Classic's title is outlined, so a pass a pixel out comes first.
    let classic = text_runs(&g.draw());
    assert!(classic.contains(&(TextureRef::Surface(CLASSIC_TEXTURE), 108.0, 105.0)));
}

#[test]
fn a_title_bars_title_is_in_the_title_weight_and_its_close_button_a_ghost_x() {
    let mut g = window(r#"title="Buff Bot" titlebar="true""#, "");
    let list = g.draw();
    assert_eq!(text_runs(&list)[0].0, TextureRef::Surface(SEMIBOLD_TEXTURE));
    let ui = &g.host.root;
    let bar = find_named(ui, ui.root(), "PluginTitleBar").unwrap();
    let close = ui.widget::<TitleBar>(bar).unwrap().close;
    let (x, y) = ui.screen_position(close);
    let e = ui.element(close).unwrap();
    let (w, h) = (e.width, e.height);
    let v = corners(&list);
    let glyph = |c: Color| {
        v.iter().any(|(vx, vy, vc)| {
            *vx >= x && *vx <= x + w && *vy >= y && *vy <= y + h && same_rgb(*vc, c)
        })
    };
    assert!(glyph(P.muted), "the X at rest");
    assert!(!glyph(style::hover(&P, P.field)), "no face at rest");
    assert_eq!(e.top, title_bar::THEMED_CLOSE_TOP);

    let (cx, cy) = g.centre("PluginClose");
    g.host.mouse_move(cx, cy);
    let v = corners(&g.draw());
    assert!(has_color(&v, style::hover(&P, P.field)), "a face on hover");
}

#[test]
fn a_resizable_themed_window_has_a_grip_of_dots() {
    let mut g = window(r#"resizable="true""#, "");
    let v = corners(&g.draw());
    assert!(
        v.iter()
            .any(|(x, y, c)| { *x > 280.0 && *y > 210.0 && same_rgb(*c, P.muted) && c.a > 0.9 })
    );
}

#[test]
fn a_window_that_does_not_opt_in_stays_classic_under_a_theme() {
    let markup = r#"<panel x="100" y="100" w="200" h="120" title="Plain"></panel>"#;
    let mut plain = Gallery::of(markup, PluginTheme::Moss);
    let mut classic = Gallery::of(markup, PluginTheme::Classic);
    assert_eq!(plain.draw(), classic.draw());
}

fn control(body: &str) -> Gallery {
    Gallery::of(
        &format!(r#"<panel x="0" y="0" w="500" h="400" theme="plugin">{body}</panel>"#),
        PluginTheme::Moss,
    )
}

/// What the named element draws, in its parent's coordinates.
fn element_corners(g: &mut Gallery, name: &str) -> Vec<(f32, f32, Color)> {
    corners(&element_list(g, name))
}

fn element_list(g: &mut Gallery, name: &str) -> DrawList {
    g.tick();
    let ui = &mut g.host.root;
    let id = find_named(ui, ui.root(), name).unwrap();
    let mut list = DrawList::default();
    let mut rc =
        crate::draw::RenderContext::new(&mut list, (1024.0, 768.0), &crate::draw::NoSprites);
    ui.draw_tree(id, &mut rc);
    drop(rc);
    list
}

fn opaque(v: &[(f32, f32, Color)], color: Color) -> Vec<(f32, f32)> {
    v.iter()
        .filter(|(_, _, c)| c.a > 0.99 && same_rgb(*c, color))
        .map(|(x, y, _)| (*x, *y))
        .collect()
}

fn span(values: impl Iterator<Item = f32> + Clone) -> (f32, f32) {
    (
        values.clone().fold(f32::MAX, f32::min),
        values.fold(f32::MIN, f32::max),
    )
}

#[test]
fn a_themed_button_shows_hover_and_press() {
    let mut g =
        control(r#"<button name="b" x="10" y="10" w="80" h="24" text="Go" onclick="{Click}"/>"#);
    assert!(has_color(&element_corners(&mut g, "b"), P.field));
    let (x, y) = g.centre("b");
    g.host.mouse_move(x, y);
    assert!(has_color(
        &element_corners(&mut g, "b"),
        style::hover(&P, P.field)
    ));
    g.host.mouse_down(MouseButton::Left, x, y, 0);
    assert!(has_color(
        &element_corners(&mut g, "b"),
        style::pressed(P.field)
    ));
    g.host.mouse_up(MouseButton::Left, x, y, 0);
    g.host.mouse_move(450, 390);
    assert!(has_color(&element_corners(&mut g, "b"), P.field));
}

#[test]
fn a_disabled_themed_button_fades_its_caption_too() {
    let mut g = control(
        r#"<button name="on" x="10" y="10" w="80" h="24" text="Start" onclick="{Click}"/>
           <button name="off" x="100" y="10" w="80" h="24" text="Stop" enabled="{TabOff}" onclick="{Click}"/>"#,
    );
    let tint = |list: &DrawList| {
        list.commands()
            .find_map(|c| match c {
                DrawCmd::Glyphs { tint, .. } => Some(tint.a),
                _ => None,
            })
            .expect("a caption")
    };
    assert_eq!(tint(&element_list(&mut g, "on")), 1.0);
    assert!(tint(&element_list(&mut g, "off")) <= style::DISABLED_ALPHA + 1e-4);
}

#[test]
fn a_selected_themed_tab_is_a_pill_without_an_underline() {
    let mut g = control(
        r#"<tab name="a" x="10" y="10" w="80" h="24" text="Status" selected="{TabOn}" onclick="{Click}"/>
           <tab name="b" x="100" y="10" w="80" h="24" text="Spells" selected="{TabOff}" onclick="{Click}"/>"#,
    );
    let selected = element_corners(&mut g, "a");
    assert!(has_color(&selected, P.selected));
    assert!(!has_color(&selected, P.accent));
    assert!(!has_color(&element_corners(&mut g, "b"), P.selected));
    let tint = |list: &DrawList| {
        list.commands()
            .find_map(|c| match c {
                DrawCmd::Glyphs { tint, .. } => Some(*tint),
                _ => None,
            })
            .unwrap()
    };
    assert_eq!(tint(&element_list(&mut g, "a")), P.text);
    assert_eq!(tint(&element_list(&mut g, "b")), P.muted);
}

#[test]
fn a_themed_field_glows_when_focused() {
    let mut g = control(r#"<field name="f" x="10" y="10" w="160" h="24" text="{Text}"/>"#);
    let blurred = element_corners(&mut g, "f");
    assert!(has_color(&blurred, P.border));
    assert!(!has_color(&blurred, P.accent));
    let at = g.centre("f");
    g.click(MouseButton::Left, at);
    let focused = element_corners(&mut g, "f");
    assert!(has_color(&focused, P.accent));
}

#[test]
fn a_themed_menu_has_a_rounded_face_and_a_shadowed_popup() {
    let mut g = control(
        r#"<menu name="m" x="10" y="10" w="160" h="24" items="{Items}" selected="{Selected}" rows="3"/>"#,
    );
    let at = g.centre("m");
    g.host.mouse_move(at.0, at.1);
    assert!(has_color(
        &element_corners(&mut g, "m"),
        style::hover(&P, P.field)
    ));
    g.click(MouseButton::Left, at);
    let v = corners(&g.draw());
    assert!(
        v.iter()
            .any(|(_, y, c)| *y > 34.0 && c.a > 0.0 && same_rgb(*c, Color::BLACK)),
        "a shadow below the face"
    );
    assert!(has_color(&v, P.accent), "the open edge");
    assert!(has_color(&v, P.selected), "the chosen row");
}

#[test]
fn a_themed_menu_hovers_in_a_colour_distinct_from_selected_and_classic_gets_its_own_back() {
    let mut g = control(
        r#"<menu name="m" x="10" y="10" w="160" h="24" items="{Items}" selected="{Selected}" rows="3"/>"#,
    );
    let hover_color = |g: &Gallery| {
        let ui = &g.host.root;
        let id = find_named(ui, ui.root(), "m").unwrap();
        ui.widget::<Menu>(id).unwrap().plain_hover_color
    };
    assert_eq!(hover_color(&g), style::hover(&P, P.field));
    assert_ne!(hover_color(&g), P.selected);
    g.set_theme(PluginTheme::Classic);
    assert_eq!(hover_color(&g), Menu::default().plain_hover_color);
}

#[test]
fn a_themed_list_has_inset_rows_and_a_slim_thumb() {
    let mut g = control(
        r#"<list name="l" x="0" y="0" w="200" h="54" rowheight="18" items="{Items}" selected="{Index}" selectionband="true"/>"#,
    );
    let v = element_corners(&mut g, "l");
    let band = opaque(&v, P.selected);
    assert!(!band.is_empty());
    assert!(span(band.iter().map(|p| p.0)).0 >= style::ROW_INSET - 0.51);
    let thumb = opaque(&v, P.muted);
    let (left, right) = span(thumb.iter().map(|p| p.0));
    assert!(
        right - left <= style::SCROLL_THUMB_WIDTH + 1.01,
        "{left} {right}"
    );
}

#[test]
fn a_themed_list_bands_the_selected_row_of_its_columns_too() {
    let mut g = control(
        r#"<list name="l" x="0" y="0" w="200" h="54" selected="{Index}">
             <column type="text" width="*" items="{Items}"/></list>"#,
    );
    assert!(has_color(&element_corners(&mut g, "l"), P.selected));
}

#[test]
fn a_themed_list_header_sits_inside_the_rounded_container() {
    let mut g = control(
        r#"<list name="l" x="0" y="0" w="200" h="80" header="true" selected="{Index}" onchange="{Choose}">
             <column type="text" header="Name" items="{Items}"/></list>"#,
    );
    let v = element_corners(&mut g, "l");
    assert!(
        !v.iter()
            .any(|(x, y, c)| *x == 0.0 && *y == 0.0 && same_rgb(*c, P.field)),
        "no square band at the corner"
    );
    let line = super::list_ext::HEADER_HEIGHT - 1.0;
    let underline: Vec<f32> = v
        .iter()
        .filter(|(_, y, c)| (*y - line).abs() < 0.01 && same_rgb(*c, P.border))
        .map(|(x, _, _)| *x)
        .collect();
    assert_eq!(
        span(underline.iter().copied()),
        (style::ROW_INSET, 200.0 - style::ROW_INSET)
    );
}

#[test]
fn a_themed_slider_has_a_fill_and_a_round_thumb_where_it_is_grabbed() {
    let mut g = control(
        r#"<slider name="s" x="0" y="0" w="200" h="16" value="{Value}" onchange="{Changed}"/>"#,
    );
    let v = element_corners(&mut g, "s");
    assert!(has_color(&v, P.accent));
    let thumb = opaque(&v, P.text);
    let (left, right) = span(thumb.iter().map(|p| p.0));
    // The hit test's 16-pixel thumb, at 0.6 of the 184 it can travel.
    let centre = 184.0 * 0.6 + 8.0;
    assert!(
        ((left + right) / 2.0 - centre).abs() < 1.0,
        "{left} {right}"
    );
}

#[test]
fn a_themed_meter_is_rounded_in_its_own_colour() {
    let mut g = control(
        r##"<meter name="m" x="0" y="0" w="200" h="12" fill="{Fill}" color="#FFCC3333"/>"##,
    );
    let v = element_corners(&mut g, "m");
    assert!(has_color(&v, P.field));
    assert!(has_color(
        &v,
        Color::rgba(
            0xCC as f32 / 255.0,
            0x33 as f32 / 255.0,
            0x33 as f32 / 255.0,
            1.0
        )
    ));
    assert!(v.len() > 12, "rounded, not two rectangles");
}

#[test]
fn a_themed_log_sits_in_a_rounded_container_with_a_slim_thumb() {
    let mut g = control(r#"<log name="l" x="0" y="0" w="200" h="80" items="{Log}"/>"#);
    let v = element_corners(&mut g, "l");
    assert!(has_color(&v, P.field));
    assert!(v.len() > 12);
    let (left, right) = span(opaque(&v, P.muted).iter().map(|p| p.0));
    assert!(
        right - left <= style::SCROLL_THUMB_WIDTH + 1.01,
        "{left} {right}"
    );
}

#[test]
fn a_themed_scrolling_groups_bar_is_a_slim_thumb() {
    let mut g = control(
        r#"<group name="g" x="0" y="0" w="200" h="100" scroll="y"><label x="0" y="500" text="Far"/></group>"#,
    );
    let v = corners(&g.draw());
    let thumb = opaque(&v, P.muted);
    assert!(!thumb.is_empty());
    let (left, right) = span(thumb.iter().map(|p| p.0));
    assert!(
        right - left <= style::SCROLL_THUMB_WIDTH + 1.01,
        "{left} {right}"
    );
}

#[test]
fn switching_to_classic_clears_every_controls_palette() {
    let mut g = control(
        r#"<slider name="s" x="0" y="0" w="200" h="16" value="{Value}" onchange="{Changed}"/>
           <field name="f" x="0" y="20" w="200" h="24"/>
           <menu name="m" x="0" y="50" w="200" h="24" items="{Items}" selected="{Selected}"/>
           <meter name="e" x="0" y="80" w="200" h="12" fill="{Fill}"/>"#,
    );
    let set = |g: &Gallery| {
        let ui = &g.host.root;
        let id = |n| find_named(ui, ui.root(), n).unwrap();
        [
            ui.widget::<Scrollbar>(id("s")).unwrap().theme_palette,
            ui.widget::<MarkupField>(id("f"))
                .unwrap()
                .inner
                .theme_palette,
            ui.widget::<Menu>(id("m")).unwrap().theme_palette,
            ui.widget::<Meter>(id("e")).unwrap().theme_palette,
        ]
    };
    assert_eq!(set(&g), [Some(P); 4]);
    g.set_theme(PluginTheme::Classic);
    assert_eq!(set(&g), [None; 4]);
}

fn focus(g: &mut Gallery, name: &str) {
    let ui = &mut g.host.root;
    let id = find_named(ui, ui.root(), name).unwrap();
    ui.set_keyboard_focus(Some(id));
}

/// Whether a corner is exactly this colour, alpha included.
fn has_exact(v: &[(f32, f32, Color)], color: Color) -> bool {
    v.iter()
        .any(|(_, _, c)| same_rgb(*c, color) && (c.a - color.a).abs() < 0.01)
}

fn corner_at(v: &[(f32, f32, Color)], x: f32, y: f32, color: Color) -> bool {
    v.iter()
        .any(|(vx, vy, c)| (*vx - x).abs() < 0.01 && (*vy - y).abs() < 0.01 && same_rgb(*c, color))
}

#[test]
fn a_themed_toggle_is_a_switch_with_its_caption_after_it() {
    let mut g = control(
        r#"<toggle name="t" x="10" y="10" w="160" h="20" text="Auto" checked="{On}" onclick="{Click}"/>"#,
    );
    let list = element_list(&mut g, "t");
    let v = corners(&list);
    assert!(has_color(&v, P.text), "the knob, on");
    assert!(has_color(&v, P.accent), "the edge, on");
    let (_, left, _) = text_runs(&list)[0];
    assert_eq!(
        left,
        10.0 + super::content_size::THEMED_TOGGLE_CAPTION_X,
        "the caption starts after the switch and the gap"
    );
}

#[test]
fn a_themed_toggle_measures_room_for_its_switch() {
    let body = r#"<toggle name="t" text="Auto" checked="{On}" onclick="{Click}"/>"#;
    let markup = format!(r#"<panel x="0" y="0" theme="plugin" layout="row">{body}</panel>"#);
    let width = |g: &mut Gallery| {
        g.tick();
        g.draw();
        let ui = &g.host.root;
        let id = find_named(ui, ui.root(), "t").unwrap();
        ui.element(id).unwrap().width
    };
    let mut g = Gallery::of(&markup, PluginTheme::Moss);
    let themed = width(&mut g);
    g.set_theme(PluginTheme::Classic);
    let classic = width(&mut g);
    assert_eq!(
        themed - classic,
        super::content_size::THEMED_TOGGLE_CAPTION_X
            - super::content_size::CLASSIC_TOGGLE_CAPTION_X
    );
}

#[test]
fn a_themed_checkbox_is_a_rounded_tick_box_with_a_focus_ring() {
    let mut g = control(
        r#"<checkbox name="on" x="10" y="10" w="120" h="20" text="Loot" checked="{On}" onchange="{SetFlag}"/>
           <checkbox name="some" x="10" y="40" w="120" h="20" text="Some" checked="{Maybe}" onchange="{SetFlag}"/>"#,
    );
    let v = element_corners(&mut g, "on");
    assert!(has_color(&v, P.accent));
    // The box sits at (1, 4.5) inside the checkbox: no square corner there.
    assert!(!corner_at(&v, 11.0, 14.5, P.accent));
    assert!(
        has_color(&element_corners(&mut g, "some"), P.field),
        "the dash"
    );
    focus(&mut g, "on");
    let focused = element_corners(&mut g, "on");
    assert!(has_exact(&focused, style::focus_ring_color(&P)));
    assert!(!has_color(&focused, super::controls::FOCUS));
}

#[test]
fn a_themed_radio_button_is_round() {
    let mut g = control(
        r#"<radio name="r" x="10" y="10" w="120" h="40" items="A|B" selected="{Choice}" onchange="{Choose}"/>"#,
    );
    let v = element_corners(&mut g, "r");
    let accent: Vec<_> = v
        .iter()
        .filter(|(_, _, c)| same_rgb(*c, P.accent))
        .collect();
    assert!(!accent.is_empty());
    // Option 1 is the second 20-point cell; its glyph sits at (11, 30 + 4.5).
    let (cx, cy) = (11.0 + 5.5, 30.0 + 4.5 + 5.5);
    for (x, y, _) in accent {
        let distance = ((x - cx).powi(2) + (y - cy).powi(2)).sqrt();
        // Its radius, and half a pixel of soft edge.
        assert!(distance <= 6.01, "{x} {y}");
    }
}

#[test]
fn a_themed_segmented_radio_is_a_rounded_track_with_a_selected_pill() {
    let mut g = control(
        r#"<radio name="r" x="10" y="10" w="120" h="24" items="A|B" orientation="horizontal" style="segmented" selected="{Choice}" onchange="{Choose}"/>"#,
    );
    let v = element_corners(&mut g, "r");
    assert!(has_color(&v, P.selected));
    assert!(!corner_at(&v, 10.0, 10.0, P.field));
}

#[test]
fn a_themed_number_spin_has_chevrons_and_no_square_outline() {
    let mut g = control(
        r#"<number name="n" x="10" y="10" w="100" h="24" value="{Count}" onchange="{SetCount}"/>"#,
    );
    let v = element_corners(&mut g, "n");
    let spin = 110.0 - super::controls::SPIN_WIDTH;
    assert!(
        !v.iter()
            .any(|(x, _, c)| *x > spin + 0.5 && same_rgb(*c, P.border)),
        "the field stops at the spin buttons"
    );
    assert!(v.iter().any(|(x, _, c)| *x > spin && same_rgb(*c, P.muted)));
}

#[test]
fn a_themed_hotkey_and_colour_glow_when_focused() {
    let mut g = control(
        r#"<hotkey name="h" x="10" y="10" w="120" h="24" value="{Key}" onchange="{SetKey}"/>
           <color name="c" x="10" y="40" w="40" h="24" value="{Colour}" onchange="{SetColour}"/>"#,
    );
    assert!(!corner_at(
        &element_corners(&mut g, "h"),
        10.0,
        10.0,
        P.border
    ));
    focus(&mut g, "h");
    assert!(has_exact(
        &element_corners(&mut g, "h"),
        style::focus_glow(&P)
    ));
    focus(&mut g, "c");
    assert!(has_exact(
        &element_corners(&mut g, "c"),
        style::focus_glow(&P)
    ));
}

#[test]
fn a_disabled_themed_field_number_and_hotkey_fade_their_faces() {
    let mut g = control(
        r#"<field name="f" x="10" y="10" w="120" h="24" enabled="{TabOff}"/>
           <number name="n" x="10" y="40" w="100" h="24" value="{Count}" onchange="{SetCount}" enabled="{TabOff}"/>
           <hotkey name="h" x="10" y="70" w="120" h="24" value="{Key}" onchange="{SetKey}" enabled="{TabOff}"/>"#,
    );
    for name in ["f", "n", "h"] {
        let v = element_corners(&mut g, name);
        assert!(!v.is_empty());
        assert!(
            v.iter()
                .all(|(_, _, c)| c.a <= style::DISABLED_ALPHA + 1e-4),
            "{name}"
        );
    }
}

/// Whether some corner is off a whole pixel: drawn smooth, not stepped.
fn is_smooth(v: &[&(f32, f32, Color)]) -> bool {
    v.iter().any(|(x, _, _)| (x - x.round()).abs() > 0.01)
}

#[test]
fn a_themed_tree_arrow_is_a_smooth_chevron() {
    let mut g = control(
        r#"<list name="l" x="0" y="0" w="200" h="80" tree="true" levels="{Levels}" keys="{Keys}" selected="{Index}" onchange="{Choose}">
             <column type="text" items="{Items}"/></list>"#,
    );
    let v = element_corners(&mut g, "l");
    let arrow: Vec<_> = v
        .iter()
        .filter(|(x, y, c)| *x < 16.0 && *y < 18.0 && c.a > 0.05 && same_rgb(*c, P.text))
        .collect();
    assert!(!arrow.is_empty());
    assert!(is_smooth(&arrow));
}

#[test]
fn a_themed_expander_arrow_is_a_muted_chevron() {
    let mut g = control(r#"<expander name="x" x="8" y="8" w="200" h="100" text="Advanced"/>"#);
    let v = corners(&g.draw());
    let arrow: Vec<_> = v
        .iter()
        .filter(|(x, y, c)| {
            *x < 8.0 + super::fold::CAPTION_X && *y < 28.0 && c.a > 0.05 && same_rgb(*c, P.muted)
        })
        .collect();
    assert!(!arrow.is_empty());
    assert!(is_smooth(&arrow));
}

#[test]
fn a_themed_split_bar_is_a_slim_grip_pill() {
    let mut g =
        control(r#"<split name="s" x="10" y="10" w="305" h="200"><group/><group/></split>"#);
    g.tick();
    let ui = &mut g.host.root;
    let split = find_named(ui, ui.root(), "s").unwrap();
    let bar = ui.widget::<super::fold::MarkupSplit>(split).unwrap().bar;
    let mut list = DrawList::default();
    let mut rc =
        crate::draw::RenderContext::new(&mut list, (1024.0, 768.0), &crate::draw::NoSprites);
    ui.draw_tree(bar, &mut rc);
    drop(rc);
    let v = corners(&list);
    assert!(!has_color(&v, P.field), "no band");
    let grip: Vec<_> = v
        .iter()
        .filter(|(_, _, c)| c.a > 0.05 && same_rgb(*c, P.border))
        .collect();
    assert!(!grip.is_empty());
    let (top, bottom) = span(grip.iter().map(|p| p.1));
    // 24 long, and half a pixel of soft edge at each end.
    assert!(bottom - top <= 25.01, "{top} {bottom}");
}

fn is_shadow(c: &Color) -> bool {
    c.a > 0.0 && c.r == 0.0 && c.g == 0.0 && c.b == 0.0
}

#[test]
fn a_themed_context_menu_is_shadowed_and_hovers_in_the_hover_colour() {
    let mut g = control(
        r#"<button name="b" x="10" y="10" w="80" h="20" text="Go" onclick="{Click}"><contextmenu items="{Items}" onselect="{Choose}"/></button>"#,
    );
    let at = g.centre("b");
    g.click(MouseButton::Right, at);
    let list = g.draw();
    let overlay = DrawList {
        overlay: list.overlay.clone(),
        ..DrawList::default()
    };
    let v = corners(&overlay);
    assert!(v.iter().any(|(_, _, c)| is_shadow(c)));
    g.host.key_down(Key::Down, Modifiers::NONE);
    let list = g.draw();
    let keyed = corners(&DrawList {
        overlay: list.overlay,
        ..DrawList::default()
    });
    assert!(has_exact(&keyed, style::hover(&P, P.field)));
    assert!(!has_color(&keyed, P.selected));
}

#[test]
fn a_themed_colour_popup_is_shadowed() {
    let mut g = control(
        r#"<color name="c" x="10" y="40" w="40" h="24" value="{Colour}" onchange="{SetColour}"/>"#,
    );
    let at = g.centre("c");
    g.click(MouseButton::Left, at);
    let list = g.draw();
    let v = corners(&DrawList {
        overlay: list.overlay,
        ..DrawList::default()
    });
    assert!(v.iter().any(|(_, _, c)| is_shadow(c)));
}

#[test]
fn a_classic_colour_popup_has_no_shadow() {
    let mut g = control(
        r#"<color name="c" x="10" y="40" w="40" h="24" value="{Colour}" onchange="{SetColour}"/>"#,
    );
    g.set_theme(PluginTheme::Classic);
    let at = g.centre("c");
    g.click(MouseButton::Left, at);
    let list = g.draw();
    let v = corners(&DrawList {
        overlay: list.overlay,
        ..DrawList::default()
    });
    assert!(!v.is_empty());
    assert!(!v.iter().any(|(_, _, c)| is_shadow(c) && c.a < 0.9));
}

fn dialog_box(palette: Option<Palette>) -> (Gallery, ElementId) {
    let dialog = DialogView {
        handle: 7,
        owner: UiOwner::new("test.gallery", "Gallery"),
        owner_panel: None,
        title: "Delete".to_owned(),
        message: "Delete it?".to_owned(),
        buttons: vec!["OK".to_owned(), "Cancel".to_owned()],
    };
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let (mut widget, w, h) =
        dialog::PluginDialogBox::new(&dialog, Some(test_font(REGULAR_TEXTURE)), outbox);
    widget.palette = palette;
    widget.set_fonts(
        Some(test_font(REGULAR_TEXTURE)),
        Some(test_font(SEMIBOLD_TEXTURE)),
    );
    let mut g = Gallery::of(
        r#"<panel x="0" y="0" w="10" h="10"></panel>"#,
        PluginTheme::Classic,
    );
    let root = g.host.root.root();
    let id = g
        .host
        .root
        .add(root, Element::at(100.0, 100.0, w, h), widget);
    g.tick();
    (g, id)
}

fn tree_list(g: &mut Gallery, id: ElementId) -> DrawList {
    let mut list = DrawList::default();
    let mut rc =
        crate::draw::RenderContext::new(&mut list, (1024.0, 768.0), &crate::draw::NoSprites);
    g.host.root.draw_tree(id, &mut rc);
    drop(rc);
    list
}

#[test]
fn a_themed_dialog_is_a_rounded_window_with_its_heading_in_the_title_weight() {
    let (mut g, id) = dialog_box(Some(P));
    let list = tree_list(&mut g, id);
    let runs = text_runs(&list);
    let headings: Vec<_> = runs
        .iter()
        .filter(|(t, _, _)| *t == TextureRef::Surface(SEMIBOLD_TEXTURE))
        .collect();
    assert_eq!(headings.len(), 1);
    assert!(headings[0].2 < 100.0 + dialog::TITLE_HEIGHT);
    assert!(runs.iter().any(|(t, _, top)| {
        *t == TextureRef::Surface(REGULAR_TEXTURE) && *top >= 100.0 + dialog::TITLE_HEIGHT
    }));
    let v = corners(&list);
    assert!(!corner_at(&v, 100.0, 100.0, P.border), "no square corner");
    assert!(v.iter().any(|(x, _, _)| *x < 100.0), "a shadow");
}

#[test]
fn a_themed_dialogs_close_button_is_the_title_bars_ghost_x_and_classics_a_caption() {
    let (mut g, id) = dialog_box(Some(P));
    let list = tree_list(&mut g, id);
    let v = corners(&list);
    assert!(has_color(&v, P.muted), "the X");
    // Only the heading, the message and two button captions: no "x" caption.
    assert_eq!(text_runs(&list).len(), 4);
    let (mut g, id) = dialog_box(None);
    assert!(
        text_runs(&tree_list(&mut g, id)).len() > 4,
        "outlined, with an x"
    );
}

#[test]
fn a_themed_dialogs_buttons_show_hover() {
    let (mut g, id) = dialog_box(Some(P));
    let ui = &g.host.root;
    let e = ui.element(id).unwrap();
    let (x, y) = (
        (e.left + e.width / 2.0) as i32,
        (e.top + e.height - dialog::PAD - dialog::BUTTON_HEIGHT / 2.0) as i32,
    );
    assert!(!has_color(
        &corners(&tree_list(&mut g, id)),
        style::hover(&P, P.field)
    ));
    g.host.mouse_move(x - 40, y);
    g.tick();
    assert!(has_color(
        &corners(&tree_list(&mut g, id)),
        style::hover(&P, P.field)
    ));
}

#[test]
fn new_fonts_lay_a_dialog_out_again_and_the_same_fonts_do_not() {
    let dialog = DialogView {
        handle: 7,
        owner: UiOwner::new("test.gallery", "Gallery"),
        owner_panel: None,
        title: "A title long enough to set the width of the box itself, at any rate".to_owned(),
        message: String::new(),
        buttons: vec!["OK".to_owned()],
    };
    let outbox: PluginOutbox = Rc::new(RefCell::new(Vec::new()));
    let classic = test_font(CLASSIC_TEXTURE);
    let (mut b, w, _) = dialog::PluginDialogBox::new(&dialog, Some(classic.clone()), outbox);
    assert_eq!(
        b.set_fonts(Some(classic.clone()), Some(classic.clone())),
        None
    );
    let wide = Arc::new(UiFont::clone(&test_font(SEMIBOLD_TEXTURE)));
    let resized = b.set_fonts(Some(classic), Some(wide));
    assert_eq!(
        resized.map(|r| r.0),
        Some(w),
        "the same metrics, the same width"
    );
}

#[test]
fn a_themed_list_header_is_in_the_title_weight_and_classics_is_not() {
    let mut g = control(
        r#"<list name="l" x="0" y="0" w="200" h="80" header="true" selected="{Index}" onchange="{Choose}">
             <column type="text" header="Name" items="{Items}"/></list>"#,
    );
    let header = super::list_ext::HEADER_HEIGHT;
    let runs = text_runs(&element_list(&mut g, "l"));
    let semibold = TextureRef::Surface(SEMIBOLD_TEXTURE);
    assert!(
        runs.iter()
            .any(|(t, _, top)| *t == semibold && *top < header)
    );
    assert!(
        !runs
            .iter()
            .any(|(t, _, top)| *t == semibold && *top >= header)
    );
    assert!(
        runs.iter()
            .any(|(t, _, top)| *t == TextureRef::Surface(REGULAR_TEXTURE) && *top >= header)
    );
    g.set_theme(PluginTheme::Classic);
    let runs = text_runs(&element_list(&mut g, "l"));
    assert!(!runs.iter().any(|(t, _, _)| *t == semibold));
}

const EXPANDER: &str = r#"<expander name="x" x="8" y="8" w="200" h="100" text="Advanced"><label x="4" y="4" text="In"/></expander>"#;

#[test]
fn a_themed_expander_is_framed_round_its_content_with_a_band_and_a_divider() {
    let mut g = control(EXPANDER);
    let v = element_corners(&mut g, "x");
    let edge: Vec<_> = v
        .iter()
        .filter(|(_, _, c)| c.a > 0.05 && same_rgb(*c, P.border))
        .collect();
    assert!(
        span(edge.iter().map(|p| p.1)).1 > 8.0 + 90.0,
        "reaches round the content"
    );
    assert!(
        has_color(&v, style::mix(P.background, P.text, 0.08)),
        "the band"
    );
    let divider = 8.0 + ac_plugin_ui::markup::EXPANDER_HEADER_HEIGHT - 1.0;
    assert!(edge.iter().any(|(_, y, _)| *y == divider), "the divider");
}

#[test]
fn a_folded_themed_expanders_frame_is_just_the_header() {
    let mut g = control(EXPANDER);
    let at = g.centre("x");
    let header_y = 8 + 9;
    g.click(MouseButton::Left, (at.0, header_y));
    g.draw();
    let v = element_corners(&mut g, "x");
    let (_, bottom) = span(
        v.iter()
            .filter(|(_, _, c)| same_rgb(*c, P.border))
            .map(|p| p.1),
    );
    let header = ac_plugin_ui::markup::EXPANDER_HEADER_HEIGHT;
    assert!(bottom <= 8.0 + header + 0.51, "{bottom}");
}

#[test]
fn a_themed_expander_with_border_false_has_no_frame() {
    let mut g = control(
        r#"<expander name="x" x="8" y="8" w="200" h="100" text="Advanced" border="false"/>"#,
    );
    let v = element_corners(&mut g, "x");
    assert!(!has_color(&v, P.border));
    assert!(!has_color(&v, style::mix(P.background, P.text, 0.08)));
}

#[test]
fn a_classic_expander_is_framed_round_its_content_with_a_band_and_a_divider() {
    let mut g = control(EXPANDER);
    g.set_theme(PluginTheme::Classic);
    let v = element_corners(&mut g, "x");
    let edge: Vec<_> = v
        .iter()
        .filter(|(_, _, c)| *c == super::controls::CLASSIC_FRAME)
        .collect();
    assert!(
        span(edge.iter().map(|p| p.1)).1 >= 8.0 + 100.0,
        "reaches round the content"
    );
    let divider = 8.0 + ac_plugin_ui::markup::EXPANDER_HEADER_HEIGHT - 1.0;
    assert!(edge.iter().any(|(_, y, _)| *y == divider), "the divider");
    assert!(has_color(&v, super::controls::CLASSIC_BAND), "the band");
}

#[test]
fn a_folded_classic_expanders_frame_is_just_the_header() {
    let mut g = control(EXPANDER);
    g.set_theme(PluginTheme::Classic);
    let at = g.centre("x");
    let header_y = 8 + 9;
    g.click(MouseButton::Left, (at.0, header_y));
    g.draw();
    let v = element_corners(&mut g, "x");
    let (_, bottom) = span(v.iter().map(|p| p.1));
    assert!(
        bottom <= 8.0 + ac_plugin_ui::markup::EXPANDER_HEADER_HEIGHT,
        "{bottom}"
    );
    assert!(has_color(&v, super::controls::CLASSIC_FRAME));
}

#[test]
fn a_classic_expander_with_border_off_has_no_frame() {
    let mut g = control(&EXPANDER.replace("<expander ", r#"<expander border="false" "#));
    g.set_theme(PluginTheme::Classic);
    let v = element_corners(&mut g, "x");
    assert!(!has_color(&v, super::controls::CLASSIC_FRAME));
    assert!(!has_color(&v, super::controls::CLASSIC_BAND));
    assert!(v.iter().all(|(_, y, _)| *y < 8.0 + 20.0), "only its arrow");
}

#[test]
fn every_theme_draws_in_the_colours_plugins_read() {
    for kind in ac_plugin_ui::ThemeKind::ALL {
        let drawn = PluginTheme::from(kind).palette();
        let read = kind.palette();
        assert_eq!(drawn.is_some(), read.is_some(), "{kind:?}");
        let (Some(drawn), Some(read)) = (drawn, read) else {
            continue;
        };
        for (color, byte) in [
            (drawn.background, read.background),
            (drawn.field, read.field),
            (drawn.border, read.border),
            (drawn.text, read.text),
            (drawn.muted, read.muted),
            (drawn.accent, read.accent),
            (drawn.selected, read.selected),
        ] {
            assert_eq!(color, super::color(byte.to_rgba()), "{kind:?}");
        }
    }
}

#[test]
fn a_colour_pair_follows_a_light_theme_only_in_a_themed_window() {
    let dark = Color::rgba(
        0x11 as f32 / 255.0,
        0x22 as f32 / 255.0,
        0x33 as f32 / 255.0,
        1.0,
    );
    let light = Color::rgba(
        0x44 as f32 / 255.0,
        0x55 as f32 / 255.0,
        0x66 as f32 / 255.0,
        1.0,
    );
    for (attribute, theme, expected) in [
        ("theme=\"plugin\"", PluginTheme::Parchment, light),
        ("theme=\"plugin\"", PluginTheme::Moss, dark),
        ("theme=\"plugin\"", PluginTheme::Classic, dark),
        ("", PluginTheme::Parchment, dark),
    ] {
        let mut g = Gallery::of(
            &format!(
                r##"<panel x="100" y="100" w="200" h="120" {attribute}><group x="0" y="20" w="50" h="50" background="#FF112233|light:#FF445566"/></panel>"##
            ),
            theme,
        );
        let v = corners(&g.draw());
        assert!(has_color(&v, expected), "{attribute} {theme:?}");
    }
}

#[test]
fn a_palette_is_light_only_under_parchment() {
    for kind in ac_plugin_ui::ThemeKind::ALL {
        let palette = PluginTheme::from(kind).palette();
        assert_eq!(
            palette.is_some_and(|p| p.is_light()),
            kind == ac_plugin_ui::ThemeKind::Parchment,
            "{kind:?}"
        );
    }
}

/// What the window's controls sent since the last call.
fn sent(g: &Gallery) -> Vec<(String, ActionValue)> {
    g.outbox
        .borrow_mut()
        .drain(..)
        .filter_map(|i| match i {
            ac_plugin_ui::UiInput::Action { handler, value, .. } => Some((handler, value)),
            _ => None,
        })
        .collect()
}

/// Bound controls ask, and the plugin decides: a click sends what it asks
/// for, and the control shows only what the plugin stores. A plugin that hears
/// and stores nothing leaves them looking as if nothing happened.
#[test]
fn a_checkbox_and_a_colour_send_by_pointer_and_show_only_what_is_stored() {
    use ac_plugin_ui::UiValue;
    for theme in [PluginTheme::Moss, PluginTheme::Classic] {
        let mut g = window(
            r#"title="T""#,
            r#"<checkbox name="tick" x="10" y="30" w="50" h="20" text="Tick" checked="{On}" onchange="{SetFlag}"/>
               <checkbox name="empty" x="70" y="30" w="50" h="20" text="Empty" checked="{Flag}" onchange="{SetFlag}"/>
               <checkbox name="some" x="130" y="30" w="60" h="20" text="Some" checked="{Maybe}" onchange="{SetFlag}"/>
               <color name="swatch" x="10" y="60" w="28" h="24" value="{Colour}" onchange="{SetColour}"/>"#,
        );
        g.set_theme(theme);
        let state = |g: &Gallery, name: &str| {
            let ui = &g.host.root;
            let found = find_named(ui, ui.root(), name).unwrap();
            ui.widget::<controls::MarkupCheckbox>(found).unwrap().state
        };
        for (name, before, asked) in [
            ("tick", Some(true), false),
            ("empty", Some(false), true),
            ("some", None, true),
        ] {
            let at = g.centre(name);
            g.click(MouseButton::Left, at);
            g.tick();
            assert_eq!(
                sent(&g),
                [("SetFlag".to_owned(), ActionValue::Bool(asked))],
                "{theme:?} {name}"
            );
            assert_eq!(state(&g, name), before, "{theme:?} {name}: nothing stored");
        }
        g.set("Flag", UiValue::Bool(true));
        assert_eq!(state(&g, "empty"), Some(true), "{theme:?}: once stored");

        let at = g.centre("swatch");
        g.click(MouseButton::Left, at);
        g.tick();
        assert!(sent(&g).is_empty(), "{theme:?}: opening asks for nothing");
        // Into the square of the picker, which opens under the swatch.
        g.click(MouseButton::Left, (at.0 + 40, at.1 + 70));
        g.tick();
        let picked = sent(&g);
        let [(handler, ActionValue::Uint(colour))] = picked.as_slice() else {
            panic!("{theme:?}: {picked:?}");
        };
        assert_eq!(handler, "SetColour");
        assert_ne!(*colour, 0x8033_6699);
        // A click outside closes it with the pick kept: nothing goes back.
        g.click(MouseButton::Left, (1000, 700));
        g.tick();
        assert!(sent(&g).is_empty(), "{theme:?}");
        let ui = &g.host.root;
        let found = find_named(ui, ui.root(), "swatch").unwrap();
        assert!(!ui.widget::<color::MarkupColor>(found).unwrap().is_open());
    }
}
