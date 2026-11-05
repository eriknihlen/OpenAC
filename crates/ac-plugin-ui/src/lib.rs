//! Plugin UI as data: plugin UI is runtime state that a window only draws.
//!
//! A plugin's panels (OpenAC's XML markup with `{Binding}`s), the values it
//! pushes for them, its canvases and their paint lists, its images, world
//! labels and lines live in one [`PluginUi`] the session owns. Every plugin
//! call is answered from it, the same with or without a window, so a plugin
//! cannot tell whether anyone is drawing. A window that attaches gets a
//! snapshot and then the changes, keeps a [`PluginUiView`] copy, draws it,
//! and reports what the player did ([`UiInput`]); the session turns that into
//! the plugins' events ([`UiEvent`]).
//!
//! Neither a plugin host nor a window: the session (`ac-runtime`) owns the
//! state, the plugin host (`ac-plugin`) translates the plugins' calls, and
//! the window (`ac-ui`, `ac-render`) draws the copy.

pub mod chrome;
pub mod color;
pub mod control_state;
pub mod file_images;
pub mod flex;
pub mod fonts;
pub mod hotkeys;
pub mod icon;
pub mod images;
pub mod list;
pub mod list_view;
pub mod markup;
pub mod mips;
pub mod model;
pub mod number;
pub mod row;
mod state;
pub mod theme;
pub mod value;
pub mod view;

pub use file_images::{FileImage, FileImageLoader, FileImageRead, FileImageResult};
pub use images::{DecodedImage, ImageBudget};
pub use markup::{MarkupDocument, MarkupError, parse};
pub use model::{
    ActionValue, CanvasAnchor, CanvasDescriptor, CanvasPlace, CanvasView, ClientWindow,
    ClientWindowAction, Color8, DialogView, GroundShape, GroundShapeKind, ImageSource, ImageView,
    MapPoint, MarkerAnchor, PaintCmd, PaintRect, PanelDescriptor, PanelView, PluginIcon,
    PointerEvent, PointerKind, ProjectileSample, UiChange, UiEvent, UiInput, UiOwner, WindowIcon,
    WorldIcon, WorldIconEntry, WorldLabel, WorldLine, WorldTriangle, canvas_origin, window_name,
};
pub use state::{
    DEFAULT_DIALOG_BUTTONS, MAXIMUM_CANVASES, MAXIMUM_DIALOG_BUTTONS, MAXIMUM_ICON_SIZE,
    MAXIMUM_ICONS, MAXIMUM_LABEL_TEXT, MAXIMUM_LABELS, MAXIMUM_LINES_PER_LAYER,
    MAXIMUM_PAINT_COMMANDS, MAXIMUM_PROJECTILE_SAMPLES, MAXIMUM_SHAPE_RADIUS, MAXIMUM_SHAPES,
    MAXIMUM_STEP_MS, MAXIMUM_TRIANGLES, MINIMUM_ICON_SIZE, MINIMUM_SHAPE_RADIUS, PluginUi,
    RELEASE_AFTER_MS, RELEASE_CHECK_INTERVAL_MS, TextMetrics, UiResources,
};
pub use theme::{ThemeKind, ThemePalette};
pub use value::{BindingKind, BindingSchema, ItemRecord, Rgba, UiValue};
pub use view::{PluginUiView, ViewDirty};
