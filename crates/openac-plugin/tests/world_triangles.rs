//! A plugin's world triangles through the SDK, against the fake host that
//! keeps them exactly as the client does.

use openac_plugin::testing::FakeHost;
use openac_plugin::ui::{MapPoint, triangle};

fn at(east_west: f64, north_south: f64) -> MapPoint {
    MapPoint {
        east_west,
        north_south,
        elevation: 0.5,
    }
}

#[test]
fn a_plugin_fills_triangles_in_layers_it_owns() {
    let mut host = FakeHost::new("example.shading");
    let (shading, other) = host.with_host(|h| {
        let shading = h.create_triangle_layer();
        let other = h.create_triangle_layer();
        let red = triangle(
            at(10.0, 20.0),
            at(10.01, 20.0),
            at(10.0, 20.01),
            0xDC28_285A,
        );
        let mut blend = red.clone();
        blend.color_b = 0x00FF_00FF;
        assert!(shading.set_triangles(h, &[red.clone(), blend]));
        assert!(other.set_triangles(h, &[red]));
        (shading, other)
    });
    let shown = host.ui.merged_triangles();
    assert_eq!(shown.len(), 3);
    assert_eq!(shown[0].colors_rgba, [0xDC28_285A; 3]);
    assert_eq!(
        shown[1].colors_rgba,
        [0xDC28_285A, 0x00FF_00FF, 0xDC28_285A]
    );
    assert_eq!(shown[0].corners[1].east_west, 10.01);

    assert!(host.with_host(|h| shading.remove(h)));
    assert_eq!(host.ui.merged_triangles().len(), 1);
    assert!(!host.with_host(|h| shading.set_triangles(h, &[])));
    assert!(host.with_host(|h| other.set_triangles(h, &[])));
    assert!(host.ui.merged_triangles().is_empty());
}
