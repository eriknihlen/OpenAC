//! UI layouts: a tree of element descriptions, each with a default state
//! and named states that carry media (images, cursors, animation steps) and
//! typed properties.

use std::borrow::Cow;

use ac_bits::Reader;

use super::read::{self, bool8, compressed_string, hash_table_count, keyed, list};
use super::ui_property::{MasterProperty, Property};
use crate::database::DatFile;
use crate::error::{DatError, DecodeError};

type Result<T> = core::result::Result<T, DecodeError>;

/// Elements nest at most this deep; deeper input is refused rather than
/// recursed into. Retail layouts nest under ten levels.
const MAX_ELEMENT_DEPTH: usize = 64;

/// Which optional geometry fields an element stores (bits of the default
/// state's incorporation flags).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct IncorporationFlags(pub u32);

impl IncorporationFlags {
    /// The state passes itself to the children.
    pub const PASS_TO_CHILDREN: u32 = 0x01;
    /// X is stored.
    pub const X: u32 = 0x02;
    /// Y is stored.
    pub const Y: u32 = 0x04;
    /// Width is stored.
    pub const WIDTH: u32 = 0x08;
    /// Height is stored.
    pub const HEIGHT: u32 = 0x10;
    /// Z level is stored.
    pub const Z_LEVEL: u32 = 0x20;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// How an image is drawn.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DrawModeType(pub u32);

impl DrawModeType {
    /// Not set.
    pub const UNDEFINED: Self = Self(0);
    /// Opaque copy.
    pub const NORMAL: Self = Self(1);
    /// Overlay.
    pub const OVERLAY: Self = Self(2);
    /// Alpha blended.
    pub const ALPHABLEND: Self = Self(3);
}

/// One step of a state's media program.
#[derive(Debug, Clone, PartialEq)]
pub enum Media {
    /// Play a movie file.
    Movie {
        /// File name.
        file_name: String,
        /// Stretch to the full screen.
        stretch_to_full_screen: bool,
    },
    /// Use an alpha map.
    Alpha {
        /// Render surface id.
        file: u32,
    },
    /// Cycle image frames over a duration.
    Animation {
        /// Seconds for one cycle.
        duration: f32,
        /// How each frame draws.
        draw_mode: DrawModeType,
        /// Render surface ids.
        frames: Vec<u32>,
    },
    /// Use a cursor while the state is active.
    Cursor {
        /// Render surface id.
        file: u32,
        /// Hot spot x in pixels.
        x_hotspot: u32,
        /// Hot spot y in pixels.
        y_hotspot: u32,
    },
    /// Show an image.
    Image {
        /// Render surface id.
        file: u32,
        /// How it draws.
        draw_mode: DrawModeType,
    },
    /// Continue at another step.
    Jump {
        /// Step index.
        jump_item_index: u32,
        /// Chance of jumping, 0 to 1.
        probability: f32,
    },
    /// Send an element message.
    Message {
        /// Message id.
        id: u32,
        /// Chance of sending, 0 to 1.
        probability: f32,
    },
    /// Wait.
    Pause {
        /// Shortest wait in seconds.
        min_duration: f32,
        /// Longest wait in seconds.
        max_duration: f32,
    },
    /// Play a sound.
    Sound {
        /// Wave or sound table id.
        file: u32,
        /// Sound type.
        sound: u32,
    },
    /// Switch to another state.
    State {
        /// State id.
        state_id: u32,
        /// Chance of switching, 0 to 1.
        probability: f32,
    },
    /// Fade the element's alpha.
    Fade {
        /// Alpha at the start.
        start_alpha: f32,
        /// Alpha at the end.
        end_alpha: f32,
        /// Seconds.
        duration: f32,
    },
}

/// A media step with the type word stored in front of its fields.
#[derive(Debug, Clone, PartialEq)]
pub struct MediaDesc {
    /// The second type word (the first selects the step kind).
    pub declared_type: i32,
    /// The step.
    pub media: Media,
}

impl MediaDesc {
    /// The step kind number (1 movie .. 11 fade).
    #[must_use]
    pub fn media_type(&self) -> i32 {
        match self.media {
            Media::Movie { .. } => 1,
            Media::Alpha { .. } => 2,
            Media::Animation { .. } => 3,
            Media::Cursor { .. } => 4,
            Media::Image { .. } => 5,
            Media::Jump { .. } => 6,
            Media::Message { .. } => 7,
            Media::Pause { .. } => 8,
            Media::Sound { .. } => 9,
            Media::State { .. } => 10,
            Media::Fade { .. } => 11,
        }
    }

    fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        let kind = r.i32()?;
        let declared_type = r.i32()?;
        let media = match kind {
            1 => Media::Movie {
                file_name: compressed_string(r)?,
                stretch_to_full_screen: bool8(r)?,
            },
            2 => Media::Alpha { file: r.u32()? },
            3 => {
                let duration = r.f32()?;
                let draw_mode = DrawModeType(r.u32()?);
                let count = u64::from(r.u32()?);
                Media::Animation {
                    duration,
                    draw_mode,
                    frames: list(r, count, read::u32)?,
                }
            }
            4 => Media::Cursor {
                file: r.u32()?,
                x_hotspot: r.u32()?,
                y_hotspot: r.u32()?,
            },
            5 => Media::Image {
                file: r.u32()?,
                draw_mode: DrawModeType(r.u32()?),
            },
            6 => Media::Jump {
                jump_item_index: r.u32()?,
                probability: r.f32()?,
            },
            7 => Media::Message {
                id: r.u32()?,
                probability: r.f32()?,
            },
            8 => Media::Pause {
                min_duration: r.f32()?,
                max_duration: r.f32()?,
            },
            9 => Media::Sound {
                file: r.u32()?,
                sound: r.u32()?,
            },
            10 => Media::State {
                state_id: r.u32()?,
                probability: r.f32()?,
            },
            11 => Media::Fade {
                start_alpha: r.f32()?,
                end_alpha: r.f32()?,
                duration: r.f32()?,
            },
            other => return Err(DecodeError::UnsupportedMediaType(other)),
        };
        Ok(Self {
            declared_type,
            media,
        })
    }
}

/// One state of an element: its properties and media.
#[derive(Debug, Clone, PartialEq)]
pub struct StateDesc {
    /// State id.
    pub state_id: u32,
    /// Entering this state also sets it on the children.
    pub pass_to_children: bool,
    /// Which geometry fields follow (meaningful on an element's default
    /// state).
    pub incorporation_flags: IncorporationFlags,
    /// Properties keyed by property id, in file order.
    pub properties: Vec<(u32, Property)>,
    /// Media program, in order.
    pub media: Vec<MediaDesc>,
}

impl StateDesc {
    fn unpack(r: &mut Reader<'_>, master: &MasterProperty) -> Result<Self> {
        let state_id = r.u32()?;
        let pass_to_children = bool8(r)?;
        let incorporation_flags = IncorporationFlags(r.u32()?);
        let count = hash_table_count(r)?;
        let properties = keyed(r, "state properties", count, read::u32, |r| {
            Property::unpack(r, master)
        })?;
        let count = u64::from(r.compressed_u32()?);
        let media = list(r, count, MediaDesc::unpack)?;
        Ok(Self {
            state_id,
            pass_to_children,
            incorporation_flags,
            properties,
            media,
        })
    }

    /// The property stored under `id`.
    #[must_use]
    pub fn property(&self, id: u32) -> Option<&Property> {
        self.properties
            .iter()
            .find(|(k, _)| *k == id)
            .map(|(_, p)| p)
    }
}

/// One element of a layout and its children.
#[derive(Debug, Clone, PartialEq)]
pub struct ElementDesc {
    /// The default (unnamed) state; its flags say which geometry follows.
    pub state_desc: StateDesc,
    /// Order among siblings.
    pub read_order: u32,
    /// Element id.
    pub element_id: u32,
    /// Element type (widget class).
    pub element_type: u32,
    /// Element this one inherits from (0 for none).
    pub base_element: u32,
    /// Layout holding the base element.
    pub base_layout_id: u32,
    /// State entered on creation.
    pub default_state: u32,
    /// Left edge in the parent, pixels (0 when not stored).
    pub x: i32,
    /// Top edge in the parent, pixels (0 when not stored).
    pub y: i32,
    /// Width in pixels (0 when not stored).
    pub width: i32,
    /// Height in pixels (0 when not stored).
    pub height: i32,
    /// Z level (0 when not stored).
    pub z_level: i32,
    /// Left edge mode when the parent resizes.
    pub left_edge: u32,
    /// Top edge mode.
    pub top_edge: u32,
    /// Right edge mode.
    pub right_edge: u32,
    /// Bottom edge mode.
    pub bottom_edge: u32,
    /// Named states keyed by state id, in file order.
    pub states: Vec<(u32, StateDesc)>,
    /// Children keyed by element id, in file order.
    pub children: Vec<(u32, ElementDesc)>,
}

impl ElementDesc {
    fn unpack(r: &mut Reader<'_>, master: &MasterProperty, depth: usize) -> Result<Self> {
        if depth > MAX_ELEMENT_DEPTH {
            return Err(DecodeError::TooDeep("layout elements"));
        }
        let state_desc = StateDesc::unpack(r, master)?;
        let read_order = r.u32()?;
        let element_id = r.u32()?;
        let element_type = r.u32()?;
        let base_element = r.u32()?;
        let base_layout_id = r.u32()?;
        let default_state = r.u32()?;
        let flags = state_desc.incorporation_flags;
        let optional = |r: &mut Reader<'_>, bit: u32| -> Result<i32> {
            Ok(if flags.contains(bit) { r.i32()? } else { 0 })
        };
        let x = optional(r, IncorporationFlags::X)?;
        let y = optional(r, IncorporationFlags::Y)?;
        let width = optional(r, IncorporationFlags::WIDTH)?;
        let height = optional(r, IncorporationFlags::HEIGHT)?;
        let z_level = optional(r, IncorporationFlags::Z_LEVEL)?;
        let left_edge = r.u32()?;
        let top_edge = r.u32()?;
        let right_edge = r.u32()?;
        let bottom_edge = r.u32()?;
        let count = hash_table_count(r)?;
        let states = keyed(r, "element states", count, read::u32, |r| {
            StateDesc::unpack(r, master)
        })?;
        let count = hash_table_count(r)?;
        let children = keyed(r, "element children", count, read::u32, |r| {
            ElementDesc::unpack(r, master, depth + 1)
        })?;
        Ok(Self {
            state_desc,
            read_order,
            element_id,
            element_type,
            base_element,
            base_layout_id,
            default_state,
            x,
            y,
            width,
            height,
            z_level,
            left_edge,
            top_edge,
            right_edge,
            bottom_edge,
            states,
            children,
        })
    }

    /// This element or a descendant with element id `id`, depth first in
    /// file order.
    #[must_use]
    pub fn find(&self, id: u32) -> Option<&ElementDesc> {
        if self.element_id == id {
            return Some(self);
        }
        self.children.iter().find_map(|(_, c)| c.find(id))
    }
}

/// A UI layout. File id `0x21......`, in the local (language) dat. Reading
/// one needs the [`MasterProperty`] table from the portal dat.
#[derive(Debug, Clone, PartialEq)]
pub struct LayoutDesc {
    /// File id.
    pub id: u32,
    /// Width the layout was authored at, pixels.
    pub width: u32,
    /// Height the layout was authored at, pixels.
    pub height: u32,
    /// Top-level elements keyed by element id, in file order.
    pub elements: Vec<(u32, ElementDesc)>,
}

impl LayoutDesc {
    /// Decode a layout from its file bytes.
    pub fn unpack(r: &mut Reader<'_>, master: &MasterProperty) -> Result<Self> {
        let id = r.u32()?;
        let width = r.u32()?;
        let height = r.u32()?;
        let count = hash_table_count(r)?;
        let elements = keyed(r, "layout elements", count, read::u32, |r| {
            ElementDesc::unpack(r, master, 0)
        })?;
        Ok(Self {
            id,
            width,
            height,
            elements,
        })
    }

    /// Read layout `id` from `dat` (the local dat); `None` when absent. The
    /// id is set to the file id.
    pub fn read(
        dat: &DatFile,
        master: &MasterProperty,
        id: u32,
    ) -> core::result::Result<Option<Self>, DatError> {
        let Some(bytes) = dat.get_bytes(id)? else {
            return Ok(None);
        };
        let mut layout = Self::unpack(&mut Reader::new(&bytes), master)
            .map_err(|source| DatError::Decode { id, source })?;
        layout.id = id;
        Ok(Some(layout))
    }

    /// The element with id `id` anywhere in the tree, depth first in file
    /// order.
    #[must_use]
    pub fn find(&self, id: u32) -> Option<&ElementDesc> {
        self.elements.iter().find_map(|(_, e)| e.find(id))
    }
}

/// The id of the unnamed default state of an element in the importer's
/// state tables (never a real state id).
pub const DIRECT_STATE_ID: u32 = u32::MAX;

/// Named UI state ids.
pub const UI_STATE_NAMES: &[(u32, &str)] = &[
    (0x0000_0000, "Undef"),
    (0x0000_0001, "Normal"),
    (0x0000_0002, "Normal_rollover"),
    (0x0000_0003, "Normal_pressed"),
    (0x0000_0004, "Normal_focussed"),
    (0x0000_0005, "Normal_activated"),
    (0x0000_0006, "Highlight"),
    (0x0000_0007, "Highlight_rollover"),
    (0x0000_0008, "Highlight_pressed"),
    (0x0000_0009, "Drag_rollover_accept"),
    (0x0000_000A, "Drag_rollover_reject"),
    (0x0000_000B, "Closed"),
    (0x0000_000C, "Open"),
    (0x0000_000D, "Ghosted"),
    (0x0000_000E, "Unencumbered"),
    (0x0000_000F, "Encumbered"),
    (0x0000_0010, "Heavily_encumbered"),
    (0x0000_0011, "Connection_good"),
    (0x0000_0012, "Connection_uncertain"),
    (0x0000_0013, "Connection_bad"),
    (0x0000_0014, "Connection_disconnected"),
    (0x0000_0015, "Csm_highlight"),
    (0x0000_0016, "Csm_normal"),
    (0x0000_0017, "Csm_ghosted"),
    (0x0000_0018, "Dialog_pending_true"),
    (0x0000_0019, "Dialog_pending_false"),
    (0x1000_0001, "Talkfocus_highlight"),
    (0x1000_0003, "MeleeCombat"),
    (0x1000_0004, "MissileCombat"),
    (0x1000_0006, "HideDetail"),
    (0x1000_0007, "ShowDetail"),
    (0x1000_0008, "Abuse_PageOne"),
    (0x1000_0009, "Abuse_PageTwo"),
    (0x1000_000A, "Abuse_PageThree"),
    (0x1000_000B, "ObjectSelected"),
    (0x1000_000C, "StackedItemSelected"),
    (0x1000_000D, "StackedItem"),
    (0x1000_000E, "UrgentAssistance_PageOne"),
    (0x1000_000F, "UrgentAssistance_PageTwo"),
    (0x1000_0010, "UrgentAssistance_PageThree"),
    (0x1000_0011, "StatManagement_Footer_Default"),
    (0x1000_0012, "StatManagement_Footer_Text"),
    (0x1000_0013, "StatManagement_Footer_Meter"),
    (0x1000_0014, "Buffed"),
    (0x1000_0016, "Unselected"),
    (0x1000_0017, "Selected"),
    (0x1000_0018, "Unlocked"),
    (0x1000_0019, "Locked"),
    (0x1000_001A, "Inactive"),
    (0x1000_001B, "Active"),
    (0x1000_001C, "ItemSlot_Empty"),
    (0x1000_001D, "ItemSlot_Filled"),
    (0x1000_0021, "Aluvian"),
    (0x1000_0022, "Gharundim"),
    (0x1000_0023, "Sho"),
    (0x1000_0024, "Viamont"),
    (0x1000_0025, "Heritage"),
    (0x1000_0026, "Profession"),
    (0x1000_0027, "Skills"),
    (0x1000_0028, "Appearance"),
    (0x1000_0029, "Town"),
    (0x1000_002A, "Summary"),
    (0x1000_002B, "Custom"),
    (0x1000_002C, "Bow_hunter"),
    (0x1000_002D, "Life_caster"),
    (0x1000_002E, "War_mage"),
    (0x1000_002F, "Wayfarer"),
    (0x1000_0030, "Soldier"),
    (0x1000_0031, "Swashbuckler"),
    (0x1000_0032, "Create_normal"),
    (0x1000_0033, "Create_admin"),
    (0x1000_0034, "Holtburg"),
    (0x1000_0035, "Sanamar"),
    (0x1000_0036, "Yaraq"),
    (0x1000_0037, "Shoushi"),
    (0x1000_0038, "Frame1"),
    (0x1000_0039, "Frame2"),
    (0x1000_003A, "Frame3"),
    (0x1000_003B, "Inprogress"),
    (0x1000_003C, "Done"),
    (0x1000_003E, "IntroVideo"),
    (0x1000_003F, "ItemSlot_DragOver_Normal"),
    (0x1000_0040, "ItemSlot_DragOver_Accept"),
    (0x1000_0041, "ItemSlot_DragOver_Reject"),
    (0x1000_0042, "JumpMode"),
    (0x1000_0043, "MeleeMode"),
    (0x1000_0044, "MissileMode"),
    (0x1000_0045, "DDDMode"),
    (0x1000_0046, "ItemSlot_DragOver_DropIn"),
    (0x1000_0047, "Maximized"),
    (0x1000_0048, "Minimized"),
    (0x1000_0050, "Uninscribed"),
    (0x1000_0053, "Create_envoy"),
    (0x1000_0054, "Online"),
    (0x1000_0055, "Offline"),
    (0x1000_0056, "IsCharacter"),
    (0x1000_0057, "IsAccount"),
    (0x1000_0058, "Shadow"),
    (0x1000_0059, "Penumbraen"),
    (0x1000_005A, "Gearknight"),
    (0x1000_005B, "Undead"),
    (0x1000_005C, "Empyrean"),
    (0x1000_005D, "Olthoi"),
    (0x1000_005E, "Olthoiacid"),
    (0x1000_005F, "Auntumerok"),
    (0x1000_0060, "Lugian"),
    (0x1000_0063, "LockedUI"),
    (0x1000_0064, "UnlockedUI"),
];

/// The name of UI state `id`: its `UIStateId` name, or the decimal number
/// for an id the enumeration does not name (as .NET prints such a value).
#[must_use]
pub fn ui_state_name(id: u32) -> Cow<'static, str> {
    match UI_STATE_NAMES.iter().find(|(k, _)| *k == id) {
        Some((_, name)) => Cow::Borrowed(name),
        None => Cow::Owned(id.to_string()),
    }
}

/// The state id named `name` in [`UI_STATE_NAMES`].
#[must_use]
pub fn ui_state_id(name: &str) -> Option<u32> {
    UI_STATE_NAMES
        .iter()
        .find(|(_, n)| *n == name)
        .map(|(k, _)| *k)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::objects::ui_property::{BasePropertyType, PropertyValue, test_master};
    use ac_bits::Writer;

    fn state(w: &mut Writer, id: u32, pass: bool, flags: u32) {
        w.u32(id);
        w.u8(u8::from(pass));
        w.u32(flags);
    }

    /// An element with an image and a bool property in its default state,
    /// a named Highlight state with a cursor, and one child.
    fn layout_bytes() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0x2100_0006);
        w.u32(800);
        w.u32(600);
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.u32(0x1000_0001);
        // Default state: x, y, w, h stored; one property, one image.
        state(&mut w, 0, false, 0x1E);
        w.u8(0);
        w.compressed_u32(1).unwrap();
        w.u32(0x3B);
        w.u32(0x3B);
        w.u8(1);
        w.compressed_u32(1).unwrap();
        w.i32(5);
        w.i32(5);
        w.u32(0x0600_1234);
        w.u32(1);
        w.u32(3); // read order
        w.u32(0x1000_0001);
        w.u32(12);
        w.u32(0);
        w.u32(0);
        w.u32(1); // default state Normal
        w.u32(10);
        w.u32(20);
        w.u32(300);
        w.u32(40);
        for e in [1u32, 1, 2, 2] {
            w.u32(e);
        }
        // One named state with a cursor and a state jump.
        w.u8(0);
        w.compressed_u32(1).unwrap();
        w.u32(6);
        state(&mut w, 6, true, 0);
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.compressed_u32(2).unwrap();
        w.i32(4);
        w.i32(4);
        w.u32(0x0600_0001);
        w.u32(3);
        w.u32(4);
        w.i32(10);
        w.i32(10);
        w.u32(1);
        w.f32(1.0);
        // One child with only a z level stored.
        w.u8(0);
        w.compressed_u32(1).unwrap();
        w.u32(0x1000_0002);
        state(&mut w, 0, false, 0x20);
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.compressed_u32(0).unwrap();
        w.u32(0);
        w.u32(0x1000_0002);
        w.u32(3);
        w.u32(0x1000_0099);
        w.u32(0x2100_0001);
        w.u32(0);
        w.u32(7);
        for e in [0u32, 0, 0, 0] {
            w.u32(e);
        }
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.into_vec()
    }

    #[test]
    fn unpacks_elements_states_media_and_children() {
        let master = test_master(&[(0x3B, BasePropertyType::BOOL)]);
        let bytes = layout_bytes();
        let r = &mut Reader::new(&bytes);
        let l = LayoutDesc::unpack(r, &master).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!((l.id, l.width, l.height), (0x2100_0006, 800, 600));
        let e = &l.elements[0].1;
        assert_eq!(
            (e.x, e.y, e.width, e.height, e.z_level),
            (10, 20, 300, 40, 0)
        );
        assert_eq!((e.read_order, e.element_type, e.default_state), (3, 12, 1));
        assert_eq!(
            (e.left_edge, e.top_edge, e.right_edge, e.bottom_edge),
            (1, 1, 2, 2)
        );
        assert_eq!(
            e.state_desc.property(0x3B).unwrap().value,
            PropertyValue::Bool(true)
        );
        assert_eq!(
            e.state_desc.media[0].media,
            Media::Image {
                file: 0x0600_1234,
                draw_mode: DrawModeType::NORMAL
            }
        );
        let (id, s) = &e.states[0];
        assert_eq!(*id, 6);
        assert!(s.pass_to_children);
        assert_eq!(
            s.media[0].media,
            Media::Cursor {
                file: 0x0600_0001,
                x_hotspot: 3,
                y_hotspot: 4
            }
        );
        assert_eq!(s.media[1].media_type(), 10);
        let c = &e.children[0].1;
        assert_eq!((c.x, c.width, c.z_level), (0, 0, 7));
        assert_eq!(
            (c.base_element, c.base_layout_id),
            (0x1000_0099, 0x2100_0001)
        );
        assert_eq!(l.find(0x1000_0002).unwrap().element_type, 3);
        assert!(l.find(0x1000_0003).is_none());
        for len in 0..bytes.len() {
            assert!(LayoutDesc::unpack(&mut Reader::new(&bytes[..len]), &master).is_err());
        }
    }

    #[test]
    fn unknown_media_kind_is_refused() {
        let master = test_master(&[]);
        let mut w = Writer::new();
        state(&mut w, 0, false, 0);
        w.u8(0);
        w.compressed_u32(0).unwrap();
        w.compressed_u32(1).unwrap();
        w.i32(12);
        w.i32(12);
        assert_eq!(
            StateDesc::unpack(&mut Reader::new(w.as_slice()), &master),
            Err(DecodeError::UnsupportedMediaType(12))
        );
    }

    #[test]
    fn state_names_follow_the_enumeration_or_print_the_number() {
        assert_eq!(ui_state_name(1), "Normal");
        assert_eq!(ui_state_name(0x1000_0064), "UnlockedUI");
        assert_eq!(ui_state_name(0x1000_0099), "268435609");
        assert_eq!(ui_state_id("HideDetail"), Some(0x1000_0006));
        assert_eq!(ui_state_id("hidedetail"), None);
    }
}
