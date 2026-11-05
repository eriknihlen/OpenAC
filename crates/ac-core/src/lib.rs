//! Pure game logic: rules that decide what the game state becomes, with no
//! I/O, no clock and no presentation. The runtime owns the state; this
//! crate owns the rules.

mod vec3;
mod wire_enum;

pub mod chargen;
pub mod chat;
pub mod chess;
pub mod combat;
pub mod items;
pub mod journal;
pub mod lighting;
pub mod names;
pub mod physics;
pub mod player;
pub mod properties;
pub mod quests;
pub mod sky;
pub mod social;
pub mod spells;
pub mod terrain;
pub mod text;
pub mod world;
