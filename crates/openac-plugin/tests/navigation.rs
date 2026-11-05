//! A plugin's path previews and reach searches through the SDK, against the
//! fake host: the handles it is given back and the answers it is sent.

use openac_plugin::calls;
use openac_plugin::navigation::{
    CellReach, NavigationPlan, NavigationPlanStatus, NavigationReach, ReachRequest,
};
use openac_plugin::testing::FakeHost;
use openac_plugin::world::Position;
use openac_plugin::{Event, Host, Plugin, StartInfo};

/// Asks for every reachable cell, then a preview to the nearest one it
/// hears of past its own.
#[derive(Default)]
struct Explorer {
    reach: u32,
    preview: u32,
    nearest: Option<u32>,
    plan_length: Option<f32>,
}

impl Explorer {
    fn ask(&mut self, host: &mut Host<'_>) {
        self.reach = host.reachable_cells(f32::INFINITY);
    }
}

impl Plugin for Explorer {
    fn enable(&mut self, _host: &mut Host<'_>, _info: &StartInfo) -> Result<(), String> {
        Ok(())
    }

    fn on_event(&mut self, host: &mut Host<'_>, event: Event) {
        match event {
            Event::NavigationReach(reach) if reach.handle == self.reach => {
                // Nearest first, the character's own cell among them.
                let Some(next) = reach.cells.iter().find(|c| c.route_meters > 1.0) else {
                    return;
                };
                self.nearest = Some(next.cell_id);
                self.preview = host.preview_path_to_place(next.place.clone(), 2.5);
            }
            Event::NavigationPlan(plan) if plan.handle == self.preview => {
                self.plan_length = Some(plan.length_meters);
            }
            _ => {}
        }
    }
}

fn place(cell_id: u32, east_west: f64) -> Position {
    Position {
        cell_id,
        east_west,
        north_south: -30.0,
        elevation: 0.1,
        ..Position::default()
    }
}

#[test]
fn a_plugin_hears_the_cells_it_can_reach_and_previews_the_nearest() {
    let mut host = FakeHost::new("example.explorer");
    let mut plugin = Explorer::default();
    host.enable(&mut plugin).unwrap();

    // Nothing to search from: the fake host's default answer is 0.
    host.with_host(|h| assert_eq!(h.reachable_cells(50.0), 0));

    host.set_reply::<calls::NavigationReachableCells>(&7);
    host.set_reply::<calls::NavigationPreviewPath>(&8);
    host.with_host(|h| plugin.ask(h));
    assert_eq!(plugin.reach, 7);
    let asked = host.requests::<calls::NavigationReachableCells>();
    assert_eq!(asked.len(), 2);
    assert!(asked[1].max_meters.is_infinite());
    assert_eq!(asked[0], ReachRequest { max_meters: 50.0 });

    host.send(
        &mut plugin,
        &Event::NavigationReach(NavigationReach {
            handle: 7,
            status: NavigationPlanStatus::Routed,
            cells: vec![
                CellReach {
                    cell_id: 0x0032_0100,
                    route_meters: 0.0,
                    place: place(0x0032_0100, 10.0),
                    by_drop: false,
                },
                CellReach {
                    cell_id: 0x0032_01C2,
                    route_meters: 284.0,
                    place: place(0x0032_01C2, 10.1),
                    by_drop: true,
                },
            ],
            reason: "2 cells reached".into(),
            grid_reused: true,
            took_ms: 9.0,
        }),
    );
    assert_eq!(plugin.nearest, Some(0x0032_01C2));
    assert_eq!(plugin.preview, 8);
    let previews = host.requests::<calls::NavigationPreviewPath>();
    assert_eq!(
        previews[0].place.as_ref().map(|p| p.cell_id),
        Some(0x0032_01C2)
    );

    host.send(
        &mut plugin,
        &Event::NavigationPlan(NavigationPlan {
            handle: 8,
            status: NavigationPlanStatus::Routed,
            length_meters: 290.0,
            grid_reused: true,
            took_ms: 12.0,
            ..NavigationPlan::default()
        }),
    );
    assert_eq!(plugin.plan_length, Some(290.0));
}
