//! The UI's 3-D viewports: small private scenes the renderer draws into
//! textures the UI shows (`ac_ui::widgets::Viewport::texture`).
//!
//! Four exist, one texture each:
//!
//! - the **inventory figure** (paperdoll): the player as the world shows
//!   him (his look and what he wears), holding the heritage's pose, seen
//!   from an eye fixed per heritage;
//! - the **examine portrait**: a copy of an examined creature or character
//!   in its current pose, with what it holds, the camera fitted to its box;
//! - the **character-creation previews** of the appearance and summary
//!   pages: the look being built, idling (or resting while zoomed in), in
//!   front of the heritage's scenery, turned by the page's buttons.
//!
//! The scenes reuse the world renderer's models, textures and appearance
//! composition (`crate::renderer`): a figure is the parts the world would
//! draw, posed here and drawn by the world's mesh pipelines into a texture
//! with its own camera and light. This module holds the pure rules (the
//! cameras, the poses, the creation preview's controls, the copies'
//! placement) and the GPU targets.

pub mod camera;
pub mod chargen;
pub mod examine;
pub(crate) mod gpu;
pub mod pose;

use ac_core::chargen::ChargenAppearanceSelection;
use ac_ui::draw::TextureRef;

use crate::appearance::AppearanceKey;

/// The first runtime texture name the viewports use (one per slot, far
/// above the names the icon composer counts up from 1).
pub const VIEWPORT_TEXTURE_BASE: u32 = 0xFFFF_FF00;

/// A viewport the renderer draws.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ViewportSlot {
    /// The inventory figure.
    Paperdoll,
    /// The examine portrait.
    Examine,
    /// The creation wizard's appearance page.
    ChargenAppearance,
    /// The creation wizard's summary page.
    ChargenSummary,
}

impl ViewportSlot {
    /// Every slot.
    pub const ALL: [Self; 4] = [
        Self::Paperdoll,
        Self::Examine,
        Self::ChargenAppearance,
        Self::ChargenSummary,
    ];

    /// The slot's position in [`Self::ALL`].
    #[must_use]
    pub fn index(self) -> usize {
        match self {
            Self::Paperdoll => 0,
            Self::Examine => 1,
            Self::ChargenAppearance => 2,
            Self::ChargenSummary => 3,
        }
    }

    /// The runtime texture the UI names the slot's picture by.
    #[must_use]
    pub fn texture(self) -> TextureRef {
        TextureRef::Runtime(VIEWPORT_TEXTURE_BASE + self.index() as u32)
    }

    /// When nothing could be drawn this frame, the inventory figure keeps
    /// its last picture; the others show nothing.
    #[must_use]
    pub fn keeps_last_picture(self) -> bool {
        self == Self::Paperdoll
    }
}

/// The look a creation preview shows, composed from the wizard's choices.
#[derive(Debug, Clone, PartialEq)]
pub struct ChargenLook {
    /// Heritage.
    pub heritage: u32,
    /// Gender key.
    pub gender: i32,
    /// The choices (a change rebuilds the figure).
    pub selection: ChargenAppearanceSelection,
    /// The composed look as the renderer draws it.
    pub key: AppearanceKey,
    /// The heritage's scenery Setup behind the figure (0: none).
    pub backdrop_setup: u32,
}

/// What a viewport shows.
#[derive(Debug, Clone, PartialEq)]
pub enum ViewportSubject {
    /// The player, posed for his heritage (0 before it is known).
    Paperdoll {
        /// The player's heritage.
        heritage: u32,
    },
    /// The examined creature or character.
    Examine {
        /// Its object id.
        guid: u32,
    },
    /// A creation preview.
    Chargen(Box<ChargenLook>),
}

/// One viewport to draw this frame.
#[derive(Debug, Clone, PartialEq)]
pub struct ViewportRequest {
    /// Which.
    pub slot: ViewportSlot,
    /// The texture's size, pixels.
    pub size: (u32, u32),
    /// The viewport's width over its height (as laid out).
    pub aspect: f32,
    /// What it shows.
    pub subject: ViewportSubject,
}

/// What became of a requested viewport this frame.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ViewportOutcome {
    /// Drawn: show the slot's texture.
    Drawn,
    /// Not drawn: keep what the viewport shows.
    Keep,
    /// Not drawn: show nothing.
    Clear,
}

/// The outcome of a viewport that could not be drawn.
#[must_use]
pub fn undrawn(slot: ViewportSlot) -> ViewportOutcome {
    if slot.keeps_last_picture() {
        ViewportOutcome::Keep
    } else {
        ViewportOutcome::Clear
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_slot_has_its_own_texture_far_from_the_icons() {
        let names: Vec<TextureRef> = ViewportSlot::ALL.iter().map(|s| s.texture()).collect();
        for (i, name) in names.iter().enumerate() {
            assert!(!names[..i].contains(name));
            assert!(matches!(name, TextureRef::Runtime(id) if *id >= VIEWPORT_TEXTURE_BASE));
        }
        for (i, slot) in ViewportSlot::ALL.iter().enumerate() {
            assert_eq!(slot.index(), i);
        }
    }

    #[test]
    fn only_the_inventory_figure_keeps_its_last_picture() {
        assert_eq!(undrawn(ViewportSlot::Paperdoll), ViewportOutcome::Keep);
        for slot in [
            ViewportSlot::Examine,
            ViewportSlot::ChargenAppearance,
            ViewportSlot::ChargenSummary,
        ] {
            assert_eq!(undrawn(slot), ViewportOutcome::Clear);
        }
    }
}
