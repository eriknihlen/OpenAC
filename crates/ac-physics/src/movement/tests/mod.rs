//! Ported OpenAC movement tests. Each module names its source file; golden
//! values come from those tests (retail float literals quoted as written).
#![allow(clippy::excessive_precision)]

mod motion_fixtures;
mod support;

mod do_motion_family;
mod ground_lifecycle;
mod interp_tests;
mod jump_family;
mod mouse_look_tests;
mod move_to_harness;
mod move_to_tests;
mod movement_manager_tests;
mod normalization;
mod pending_motions;
mod player_tests;
mod remote_body_tests;
mod remote_chase_tests;
mod remote_driver_tests;
mod stub_world;
mod target_tests;
