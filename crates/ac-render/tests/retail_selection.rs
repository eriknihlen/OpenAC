//! World-pick shapes read from the retail dats. Ignored by default;
//! `scripts/check.*` runs them when the dats are present.

use std::path::PathBuf;

use ac_dat::objects::{BspNode, GfxObj};
use ac_render::content::Content;
use ac_render::selection::selection_mesh;

fn dats() -> Content {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Content::open(&dir).expect("open the retail dats")
}

/// OpenAC `RetailSelectionAssetTests.DoorSelectionGeometry_UsesDrawingBspRootSphereAndDatPolygonOrder`:
/// the door model's pick shape is its drawing tree's root sphere and every
/// drawn polygon in file order, single-sided by the polygon's sides type.
#[test]
#[ignore = "needs the retail dat files"]
fn the_door_is_picked_against_its_drawing_root_sphere_and_its_polygons_in_file_order() {
    const DOOR: u32 = 0x0100_44B5;
    let d = dats();
    let gfx = d.dats.get::<GfxObj>(DOOR).unwrap().expect("the door model");
    let mesh = selection_mesh(&gfx).expect("a pick shape");
    let root = match gfx.drawing_bsp.as_ref().unwrap().root().unwrap() {
        BspNode::Split(s) => s.sphere,
        BspNode::Portal(p) => p.sphere,
        BspNode::Leaf(l) => l.sphere,
    };
    assert_eq!(
        mesh.sphere_center.to_array(),
        [root.origin.x, root.origin.y, root.origin.z]
    );
    assert_eq!(mesh.sphere_radius, root.radius);
    assert_eq!(mesh.polygons.len(), gfx.polygons.len());
    let (_, first) = &gfx.polygons[0];
    assert_eq!(mesh.polygons[0].single_sided, first.sides_type.0 == 0);
    assert_eq!(mesh.polygons[0].vertices.len(), first.vertex_ids.len());
}
