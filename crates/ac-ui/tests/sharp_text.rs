//! Text drawn in the tree takes a font's sharper bake from what the window
//! told the root, at the pixel scale it gave the root.

mod common;

use std::sync::Arc;

use ac_ui::bundled_font::{Weight, bake};
use ac_ui::draw::{Color, DrawCmd, DrawList, NoSprites, RenderContext, TextureRef};
use ac_ui::font::{SharpFont, SharpFonts, UiFont};
use ac_ui::root::UiRoot;
use ac_ui::tree::{ElementId, Widget};

use common::{at, root};

const OWN: TextureRef = TextureRef::Runtime(1);
const SHARP: TextureRef = TextureRef::Runtime(2);

/// Draws one string in its font.
#[derive(Debug)]
struct Line(Arc<UiFont>);

impl Widget for Line {
    fn draw(&mut self, _ui: &mut UiRoot, _me: ElementId, rc: &mut RenderContext<'_>) {
        rc.string_dat(
            &self.0,
            "Buff Bot",
            0.0,
            0.0,
            Color::WHITE,
            false,
            Color::BLACK,
        );
    }
}

fn textures(list: &DrawList) -> Vec<TextureRef> {
    list.commands()
        .filter_map(|c| match c {
            DrawCmd::Glyphs { texture, .. } => Some(*texture),
            _ => None,
        })
        .collect()
}

/// The widest a glyph's image is drawn.
fn glyph_extent(list: &DrawList) -> f32 {
    list.commands()
        .flat_map(|c| match c {
            DrawCmd::Glyphs { quads, .. } => quads.iter().map(|q| q.x + q.w).collect(),
            _ => Vec::new(),
        })
        .fold(0.0, f32::max)
}

fn with_line() -> (UiRoot, Arc<SharpFonts>) {
    let font = bake(16.0, Weight::Regular).unwrap().create_font(OWN);
    let twin = bake(32.0, Weight::Regular).unwrap().create_font(SHARP);
    let fonts = Arc::new(SharpFonts::from([(
        font.id,
        SharpFont {
            font: Arc::new(twin),
            scale: 2.0,
        },
    )]));
    let mut ui = root(800.0, 600.0);
    let parent = ui.root();
    ui.add(parent, at(10.0, 10.0, 200.0, 20.0), Line(Arc::new(font)));
    (ui, fonts)
}

#[test]
fn text_takes_the_sharper_bake_at_the_scale_the_root_was_given() {
    let (mut ui, fonts) = with_line();
    let screen = (800.0, 600.0);
    let own = ui.draw(screen, &NoSprites);
    assert!(textures(&own).iter().all(|t| *t == OWN));

    ui.set_sharp_fonts(Some(fonts));
    assert_eq!(ui.draw(screen, &NoSprites), own, "no sharper at one a unit");

    ui.set_pixels_per_unit(2.0);
    let sharp = ui.draw(screen, &NoSprites);
    assert!(textures(&sharp).iter().all(|t| *t == SHARP));
    assert!(
        (glyph_extent(&sharp) - glyph_extent(&own)).abs() <= 1.0,
        "the line stays where it was: {} and {}",
        glyph_extent(&sharp),
        glyph_extent(&own)
    );

    ui.set_sharp_fonts(None);
    assert!(
        textures(&ui.draw(screen, &NoSprites))
            .iter()
            .all(|t| *t == OWN)
    );
}
