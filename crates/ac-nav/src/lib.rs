//! Navigation: how the client walks, runs and jumps the character to a goal
//! on its own.
//!
//! The planning layer has no client state, so it runs in tests from shapes
//! built in code or from the installed game files:
//!
//! - [`geometry`] copies the fixed collision of a square region out of the
//!   physics world ([`geometry::capture`]).
//! - [`NavGrid::build`] samples it into 0.25 m columns of standing points
//!   joined by steps, with the walls kept for straight walks and sight.
//! - [`route::find`], [`route::find_onto`] and [`route::find_toward`]
//!   search the grid with A*, planning leaps ([`leaps`]) where no walk
//!   reaches.
//!
//! Units are metres, seconds and radians; z is up.

// `!(x > 0.0)` is written on purpose throughout: it is false for NaN as
// well as for zero and below, which is how every guard here rejects a bad
// number, and `x <= 0.0` would let a NaN through.
#![allow(clippy::neg_cmp_op_on_partial_ord)]

pub mod automation;
pub mod commands;
pub mod driver;
pub mod dungeon;
pub mod floorplan;
pub mod geometry;
pub mod grid;
pub mod indoor;
pub mod landscape;
pub mod leaps;
pub mod motor;
pub mod order;
pub mod route;
pub mod sources;
pub mod tiles;
pub mod view;
pub mod walk;

pub use geometry::{NavBody, NavCylinder, NavGeometry, NavSurfaces, NavTerrain, NavTriangle};
pub use grid::{NavGrid, NavGridBuildReport};
pub use leaps::{NavLeap, NavLeapAbility, NavLeapAim, NavLeapFinder, NavLeapPhysics, NavRouteLeap};
pub use route::{NavAvoidance, NavRoute, NavRouteOutcome, NavRouteRequest};
