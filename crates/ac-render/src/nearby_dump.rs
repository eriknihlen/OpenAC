//! The "dump nearby" developer command (Ctrl+F3): where the observer
//! stands, and the drawn objects and collision objects within 15 m of it,
//! nearest first, as lines for the window's log.

use glam::Vec3;

/// The horizontal reach of the dump, metres.
pub const NEARBY_DISTANCE: f32 = 15.0;
/// At most this many of each kind are listed.
const LISTED: usize = 12;

/// A drawn object, in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct NearbyEntity {
    /// Its id.
    pub id: u32,
    /// Its model (GfxObj or Setup).
    pub source: u32,
    /// Where it stands.
    pub position: Vec3,
    /// Its scale.
    pub scale: f32,
}

/// A collision object, in the drawing frame.
#[derive(Debug, Clone, PartialEq)]
pub struct NearbyShadow {
    /// Its owner's id.
    pub id: u32,
    /// Its collision shape's name.
    pub kind: String,
    /// Radius, metres.
    pub radius: f32,
    /// Cylinder height, metres.
    pub height: f32,
    /// Where it stands.
    pub position: Vec3,
}

/// The dump's lines. `observer` is in the drawing frame, whose origin is
/// the south-west corner of landblock `center` (x, y).
#[must_use]
pub fn nearby_dump(
    observer: Vec3,
    center: (i32, i32),
    entities: &[NearbyEntity],
    shadows: &[NearbyShadow],
    total_shadows: usize,
) -> Vec<String> {
    let p = observer;
    let lx = center.0 + (p.x / 192.0).floor() as i32;
    let ly = center.1 + (p.y / 192.0).floor() as i32;
    let landblock = ((lx as u32) << 24) | ((ly as u32 & 0xFF) << 16) | 0xFFFF;
    let mut lines = vec![format!(
        "=== F3 DEBUG DUMP ===\n  player pos=({:.2},{:.2},{:.2})\n  landblock=0x{landblock:08X} local=({:.2},{:.2})\n  total shadow objects: {total_shadows}",
        p.x,
        p.y,
        p.z,
        p.x - (lx - center.0) as f32 * 192.0,
        p.y - (ly - center.1) as f32 * 192.0,
    )];
    let horizontal_sq = |q: Vec3| {
        let (dx, dy) = (q.x - p.x, q.y - p.y);
        dx * dx + dy * dy
    };
    let mut visible: Vec<&NearbyEntity> = entities
        .iter()
        .filter(|e| horizontal_sq(e.position) < NEARBY_DISTANCE * NEARBY_DISTANCE)
        .collect();
    visible.sort_by(|a, b| {
        (a.position - p)
            .length()
            .total_cmp(&(b.position - p).length())
    });
    lines.push(format!("  VISIBLE entities within 15m: {}", visible.len()));
    for e in visible.iter().take(LISTED) {
        let q = e.position;
        lines.push(format!(
            "    VIS  id=0x{:08X} src=0x{:08X} pos=({:.2},{:.2},{:.2}) dist={:.2} scale={:.2}",
            e.id,
            e.source,
            q.x,
            q.y,
            q.z,
            (q - p).length(),
            e.scale
        ));
    }
    let mut near: Vec<(&NearbyShadow, f32)> = shadows
        .iter()
        .map(|s| (s, horizontal_sq(s.position).sqrt()))
        .filter(|(_, d)| *d < NEARBY_DISTANCE)
        .collect();
    near.sort_by(|a, b| a.1.total_cmp(&b.1));
    lines.push(format!("  SHADOW objects within 15m: {}", near.len()));
    for (s, d) in near.iter().take(LISTED) {
        let q = s.position;
        lines.push(format!(
            "    SHAD id=0x{:08X} {} r={:.2} h={:.2} pos=({:.2},{:.2},{:.2}) dist={d:.2}",
            s.id, s.kind, s.radius, s.height, q.x, q.y, q.z
        ));
    }
    lines
}

#[cfg(test)]
mod tests {
    //! OpenAC `tests/AcDream.App.Tests/Diagnostics/RuntimeDiagnosticCommandControllerTests.cs`
    //! :: `NearbyDump_PreservesLandblockCoordinatesRangeAndDistanceOrder`.

    use super::*;

    fn entity(id: u32, position: Vec3) -> NearbyEntity {
        NearbyEntity {
            id,
            source: 0x0100_0001,
            position,
            scale: 1.0,
        }
    }

    #[test]
    fn the_dump_keeps_the_landblock_the_range_and_the_distance_order() {
        let entities = [
            entity(2, Vec3::new(203.0, -1.0, 3.0)),
            entity(1, Vec3::new(201.0, -1.0, 3.0)),
            entity(3, Vec3::new(216.0, -1.0, 3.0)),
        ];
        let shadows = [NearbyShadow {
            id: 5,
            kind: "Sphere".into(),
            radius: 0.5,
            height: 0.0,
            position: Vec3::new(202.0, -1.0, 3.0),
        }];
        let lines = nearby_dump(
            Vec3::new(200.0, -1.0, 3.0),
            (10, 20),
            &entities,
            &shadows,
            4,
        );
        assert!(
            lines[0].contains("landblock=0x0B13FFFF local=(8.00,191.00)"),
            "{}",
            lines[0]
        );
        assert!(lines[0].contains("total shadow objects: 4"));
        assert_eq!(lines[1], "  VISIBLE entities within 15m: 2");
        assert!(lines[2].contains("id=0x00000001"));
        assert!(lines[3].contains("id=0x00000002"));
        assert_eq!(lines[4], "  SHADOW objects within 15m: 1");
        assert!(lines[5].contains("id=0x00000005"));
    }
}
