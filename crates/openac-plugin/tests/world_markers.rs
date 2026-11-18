//! A plugin's world markers through the SDK, against the fake host that
//! keeps them exactly as the client does.

use openac_plugin::testing::FakeHost;
use openac_plugin::ui::{
    Image, MapPoint, MarkerAnchor, ground_arc, ground_disc, ground_ring, world_icon,
};

const ART: Image = Image {
    handle: 7,
    width: 32,
    height: 32,
};

#[test]
fn a_plugin_hangs_icons_in_layers_it_owns() {
    let mut host = FakeHost::new("example.markers");
    let (first, second) = host.with_host(|h| {
        let first = h.create_marker_layer();
        let second = h.create_marker_layer();
        let mut framed = world_icon(MarkerAnchor::Object(0x8000_0001), ART);
        framed.border_rgba = Some(0xFF00_00FF);
        framed.hidden_by_world = true;
        let spot = MarkerAnchor::Position(
            MapPoint {
                east_west: 10.0,
                north_south: 20.0,
                elevation: 0.5,
            },
            true,
        );
        assert!(first.set_icons(h, &[framed]));
        assert!(second.set_icons(h, &[world_icon(spot, ART)]));
        (first, second)
    });
    let shown = host.ui.merged_icons();
    assert_eq!(shown.len(), 2);
    assert_eq!(shown[0].owner, "example.markers");
    assert_eq!(shown[0].icon.image, 7);
    assert_eq!(shown[0].icon.size, 24.0);
    assert_eq!(shown[0].icon.tint_rgba, 0xFFFF_FFFF);
    assert_eq!(shown[0].icon.border_rgba, Some(0xFF00_00FF));
    assert_eq!(shown[0].icon.max_range, 60.0);
    assert!(shown[0].icon.hidden_by_world);
    assert!(!shown[1].icon.hidden_by_world);
    assert!(shown[1].icon.anchor.is_valid());
    assert_ne!(
        shown[1].icon.anchor,
        ac_plugin_ui::MarkerAnchor::Object(0x8000_0001)
    );

    assert!(host.with_host(|h| first.remove(h)));
    assert_eq!(host.ui.merged_icons().len(), 1);
    assert!(!host.with_host(|h| first.set_icons(h, &[])));

    // Over the cap across its layers: refused, and the layer keeps its icon.
    let many = vec![world_icon(MarkerAnchor::Object(1), ART); 257];
    assert!(!host.with_host(|h| second.set_icons(h, &many)));
    assert_eq!(host.ui.merged_icons().len(), 1);

    // Leaving the world takes icons over objects down and keeps those at
    // fixed positions; the layer stays usable.
    let spot = MarkerAnchor::Position(
        MapPoint {
            east_west: 10.0,
            north_south: 20.0,
            elevation: 0.5,
        },
        true,
    );
    assert!(host.with_host(|h| {
        second.set_icons(
            h,
            &[
                world_icon(MarkerAnchor::Object(1), ART),
                world_icon(spot, ART),
            ],
        )
    }));
    host.ui.clear_object_markers();
    let left = host.ui.merged_icons();
    assert_eq!(left.len(), 1);
    assert!(matches!(
        left[0].icon.anchor,
        ac_plugin_ui::MarkerAnchor::Position { .. }
    ));
    assert!(
        host.with_host(|h| { second.set_icons(h, &[world_icon(MarkerAnchor::Object(1), ART)]) })
    );
    assert_eq!(host.ui.merged_icons().len(), 1);
}

#[test]
fn a_plugin_lays_shapes_on_the_ground_in_its_layers() {
    let mut host = FakeHost::new("example.markers");
    let target = MarkerAnchor::Object(0x8000_0001);
    let layer = host.with_host(|h| {
        let layer = h.create_marker_layer();
        let mut wedge = ground_arc(target.clone(), 8.0, -30.0, 60.0, true, 0xFF00_0060);
        wedge.faces_object = true;
        let shapes = [
            ground_ring(target.clone(), 2.0, 0.2, 0x00FF_0080),
            ground_disc(target.clone(), 5.0, 0x0000_FF40),
            ground_arc(target.clone(), 3.0, 0.0, 90.0, false, 0xFFFF_0080),
            wedge,
        ];
        assert!(layer.set_shapes(h, &shapes));
        layer
    });
    let shown = host.ui.merged_shapes();
    assert_eq!(shown.len(), 4);
    assert_eq!(shown[0].width, 0.2);
    assert_eq!(shown[0].sweep_degrees, 360.0);
    assert_eq!(shown[1].color_rgba, 0x0000_FF40);
    assert!(shown[1].filled);
    assert_eq!(shown[2].width, 0.15);
    assert!(shown[3].faces_object);
    assert_eq!(shown[3].width, 0.0);

    let many = vec![ground_disc(target, 1.0, 0xFFFF_FFFF); 257];
    assert!(!host.with_host(|h| layer.set_shapes(h, &many)));
    assert_eq!(host.ui.merged_shapes().len(), 4);
    assert!(host.with_host(|h| layer.remove(h)));
    assert!(host.ui.merged_shapes().is_empty());
}

/// A plugin paints a name plate with its own font and hangs it over an
/// object; the session keeps the paint, the box, the pivot and the scale
/// as given, and refuses a paint it could not draw.
#[test]
fn a_plugin_hangs_drawings_painted_in_its_own_font() {
    use openac_plugin::ui::{DrawingScale, FontWeight, rgba, world_drawing};
    let mut host = FakeHost::new("example.markers");
    let layer = host.with_host(|h| {
        let font = h.font_bundled(18.0, FontWeight::SemiBold);
        assert!(font.is_valid());
        let mut plate = world_drawing(MarkerAnchor::Object(0x8000_0001), 120.0, 24.0, |p| {
            p.fill_rect(0.0, 0.0, 120.0, 24.0, rgba(0, 0, 0, 160));
            p.text_in(
                font,
                "Drudge Skulker",
                (6.0, 3.0),
                rgba(255, 255, 255, 255),
                true,
            );
            p.image(ART, 100.0, 4.0, 16.0, 16.0, rgba(255, 255, 255, 255));
        });
        plate.height_offset = 0.3;
        plate.scale = DrawingScale::WithDistance(10.0, 40.0, 0.5);
        plate.hidden_by_world = true;
        let layer = h.create_marker_layer();
        assert!(layer.set_drawings(h, &[plate]));
        layer
    });
    let shown = host.ui.merged_drawings();
    assert_eq!(shown.len(), 1);
    assert_eq!(shown[0].owner, "example.markers");
    let drawing = &shown[0].drawing;
    assert_eq!(
        drawing.anchor,
        ac_plugin_ui::MarkerAnchor::Object(0x8000_0001)
    );
    assert_eq!(drawing.size, (120.0, 24.0));
    // Standing on its anchor by default.
    assert_eq!(drawing.pivot, (60.0, 24.0));
    assert_eq!(drawing.height_offset, 0.3);
    assert_eq!(drawing.max_range, 60.0);
    assert!(drawing.hidden_by_world);
    assert_eq!(
        drawing.scale,
        ac_plugin_ui::DrawingScale::WithDistance {
            near: 10.0,
            far: 40.0,
            smallest: 0.5,
        }
    );
    assert_eq!(drawing.paint.len(), 3);
    assert!(matches!(
        &drawing.paint[1],
        ac_plugin_ui::PaintCmd::Text { text, font, outline: true, .. }
            if text == "Drudge Skulker" && *font != 0
    ));

    // A clip left pushed: refused, and the layer keeps its drawing.
    let unbalanced = world_drawing(MarkerAnchor::Object(2), 10.0, 10.0, |p| {
        p.push_clip(0.0, 0.0, 5.0, 5.0);
    });
    assert!(!host.with_host(|h| layer.set_drawings(h, &[unbalanced])));
    assert_eq!(host.ui.merged_drawings().len(), 1);

    // Leaving the world takes it down with its object; the layer stays.
    host.ui.clear_object_markers();
    assert!(host.ui.merged_drawings().is_empty());
    assert!(host.with_host(|h| layer.remove(h)));
}
