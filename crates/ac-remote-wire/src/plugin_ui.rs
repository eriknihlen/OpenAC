//! The plugins' UI as a window copies it: panels with their parsed markup
//! and values, canvases and their paint lists, images, world markers,
//! hotkeys, dialogs, and what the window tells the session back.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Arc;

use ac_plugin_ui::flex::{FlexAlign, FlexDirection, FlexEdges, FlexJustify};
use ac_plugin_ui::fonts::{self, FontBake, FontSource, FontView, FontWeight, SharpBake};
use ac_plugin_ui::hotkeys::{BoundHotkey, Chord};
use ac_plugin_ui::markup::{
    Anchors, BoolSource, ColorSource, ColorValueKind, Column, ColumnAlign, ColumnHeader,
    ColumnKind, Common, ContextMenu, Element, FlexContainer, FlexItem, IconKind, IconSource,
    ItemsSource, ListBody, ListHeader, ListTree, Node, NumberKind, NumberSource, PanelSpec,
    PathSource, Rect, SortKeysSource, SortKind, TextSource, UintSource,
};
use ac_plugin_ui::number::{Dec, NumberSpec};
use ac_plugin_ui::{
    ActionValue, CanvasAnchor, CanvasDescriptor, CanvasPlace, CanvasView, ClientWindow,
    ClientWindowAction, Color8, DecodedImage, DialogView, FileImage, GroundShape, GroundShapeKind,
    ImageSource, ImageView, ItemRecord, MapPoint, MarkerAnchor, MarkupDocument, PaintCmd,
    PaintRect, PanelDescriptor, PanelView, PluginIcon, PointerEvent, PointerKind, ProjectileSample,
    Rgba, ThemeKind, UiChange, UiInput, UiOwner, UiValue, WindowIcon, WorldIcon, WorldIconEntry,
    WorldLabel, WorldLine, WorldTriangle,
};

use crate::codec::{
    DecodeContext, DecodeError, EncodeContext, EncodeError, Reader, Shape, Wire, Writer, record,
    tuple_record, union,
};

tuple_record! {
    Anchors(bits: u8)
}

record! {
    UiOwner { id: String, display_name: String }
    PluginIcon { width: u32, height: u32, rgba: Arc<Vec<u8>> }
    WindowIcon { named: Option<PluginIcon>, light: Option<PluginIcon>, dark: Option<PluginIcon> }
    PanelDescriptor {
        window_id: String,
        title: String,
        icon_text: Option<String>,
        icon_surface_id: u32,
        start_visible: bool,
        show_in_side_panel: bool,
        icon_file: Option<String>,
    }
    Rect { x: f32, y: f32, w: f32, h: f32 }
    Rgba { r: f32, g: f32, b: f32, a: f32 }
    FlexEdges { top: f32, right: f32, bottom: f32, left: f32 }
    FlexContainer {
        direction: FlexDirection,
        gap: f32,
        padding: FlexEdges,
        justify: FlexJustify,
        align: FlexAlign,
        wrap: bool,
    }
    FlexItem {
        grow: f32,
        shrink: f32,
        basis: Option<f32>,
        width: Option<f32>,
        height: Option<f32>,
        min_width: Option<f32>,
        max_width: Option<f32>,
        min_height: Option<f32>,
        max_height: Option<f32>,
        align_self: Option<FlexAlign>,
    }
    NumberSpec { min: Dec, max: Dec, step: Dec, decimals: u32 }
    Column {
        width: f32,
        auto: bool,
        kind: ColumnKind,
        header: ColumnHeader,
        sort: SortKind,
        sort_keys: Option<SortKeysSource>,
        align: ColumnAlign,
    }
    ListHeader {
        show: bool,
        sort: Option<(String, String)>,
        onsort: Option<String>,
        resizable: bool,
        widths: Option<String>,
        onresize: Option<String>,
    }
    ListTree {
        column: usize,
        levels: String,
        keys: String,
        expanded: Option<String>,
        start_collapsed: bool,
        onexpand: Option<String>,
    }
    ContextMenu {
        items: ItemsSource,
        enabled: Option<String>,
        onselect: String,
        onopen: Option<String>,
    }
    Common {
        name: Option<String>,
        anchors: Anchors,
        visible: BoolSource,
        enabled: BoolSource,
        tooltip: Option<TextSource>,
        default_color: bool,
        default_background: bool,
        default_border: bool,
        context_menu: Option<ContextMenu>,
    }
    Node {
        element: Element,
        common: Common,
        rect: Rect,
        parent: Option<usize>,
        children: Vec<usize>,
        flex: Option<FlexItem>,
        repeat: Option<usize>,
    }
    PanelSpec {
        rect: Rect,
        resizable: bool,
        min_width: f32,
        min_height: f32,
        resize_x: bool,
        resize_y: bool,
        visible: Option<String>,
        title: Option<String>,
        themed: bool,
        layout: Option<FlexContainer>,
        title_bar: bool,
        scroll_x: bool,
        scroll_y: bool,
        authored_size: (Option<f32>, Option<f32>),
        authored_min: (Option<f32>, Option<f32>),
        geometry_revision: i32,
    }
    MarkupDocument { panel: PanelSpec, nodes: Vec<Node>, roots: Vec<usize>, warnings: Vec<String> }
    ItemRecord { values: BTreeMap<String, UiValue> }
    PanelView {
        id: u32,
        owner: UiOwner,
        descriptor: PanelDescriptor,
        document: Arc<MarkupDocument>,
        values: BTreeMap<String, UiValue>,
        label_overrides: BTreeMap<usize, String>,
        visible_overrides: BTreeMap<usize, bool>,
        requested_visible: bool,
        file_icon: Option<PluginIcon>,
        window_icon: Option<WindowIcon>,
    }
    Color8 { r: u8, g: u8, b: u8, a: u8 }
    PaintRect { x: f32, y: f32, w: f32, h: f32 }
    CanvasDescriptor {
        canvas_id: String,
        width: i32,
        height: i32,
        anchor: CanvasAnchor,
        offset: (f32, f32),
        start_visible: bool,
        accepts_pointer_input: bool,
        in_markup: bool,
    }
    CanvasView {
        id: u32,
        owner: UiOwner,
        descriptor: CanvasDescriptor,
        size: (i32, i32),
        hosted: bool,
        visible: bool,
        anchor: CanvasAnchor,
        offset: (f32, f32),
        listening: bool,
        paint: Arc<Vec<PaintCmd>>,
        release_count: u32,
    }
    DecodedImage { width: u32, height: u32, rgba: Vec<u8> }
    FileImage {
        path: String,
        version: String,
        levels: Vec<DecodedImage>,
        full_path: Option<PathBuf>,
    }
    ImageView { owner: String, handle: u32, width: u32, height: u32, source: ImageSource }
    MapPoint { east_west: f64, north_south: f64, elevation: f64 }
    WorldLabel {
        object_id: u32,
        text: String,
        color: [f32; 4],
        height_offset: f32,
        line: i32,
        max_range: f32,
        outline: bool,
    }
    WorldLine {
        start: MapPoint,
        end: MapPoint,
        color_rgb: u32,
        follow_terrain: bool,
        width_metres: f32,
    }
    WorldTriangle { corners: [MapPoint; 3], colors_rgba: [u32; 3] }
    WorldIcon {
        anchor: MarkerAnchor,
        image: u32,
        size: f32,
        tint_rgba: u32,
        border_rgba: Option<u32>,
        max_range: f32,
        hidden_by_world: bool,
    }
    WorldIconEntry { owner: String, icon: WorldIcon }
    GroundShape {
        anchor: MarkerAnchor,
        kind: GroundShapeKind,
        radius: f32,
        width: f32,
        start_degrees: f32,
        sweep_degrees: f32,
        filled: bool,
        faces_object: bool,
        color_rgba: u32,
    }
    ProjectileSample { position: MapPoint, is_clear: bool, radius: f32 }
    Chord { key: u32, ctrl: bool, alt: bool, shift: bool }
    BoundHotkey { owner: String, id: String, chord: Chord }
    DialogView {
        handle: u32,
        owner: UiOwner,
        title: String,
        message: String,
        buttons: Vec<String>,
        owner_panel: Option<u32>,
    }
    PointerEvent {
        kind: PointerKind,
        x: f32,
        y: f32,
        button: u8,
        modifiers: u8,
        wheel_delta: i32,
    }
    CanvasPlace { origin: (f32, f32), layer: (f32, f32) }
}

union! {
    FlexDirection: "flex direction" {
        0 => Row,
        1 => Column,
    }
    FlexJustify: "flex justify" {
        0 => Start,
        1 => Center,
        2 => End,
        3 => SpaceBetween,
    }
    FlexAlign: "flex align" {
        0 => Start,
        1 => Center,
        2 => End,
        3 => Stretch,
    }
    TextSource: "text source" {
        0 => None,
        1 => Literal(text: String),
        2 => Bound(name: String),
    }
    BoolSource: "bool source" {
        0 => Unset,
        1 => Literal(value: bool),
        2 => Bound(name: String),
    }
    NumberSource: "number source" {
        0 => Missing,
        1 => Bound(name: String),
    }
    ColorSource: "colour source" {
        0 => Literal(color: Rgba),
        1 => Paired { dark: Rgba, light: Rgba },
        2 => Bound(name: String),
        3 => Theme { token: String, classic: Rgba },
    }
    UintSource: "uint source" {
        0 => Literal(value: u32),
        1 => Bound(name: String),
    }
    IconKind: "icon kind" {
        0 => Did,
        1 => Spell,
        2 => Item,
        3 => File,
    }
    PathSource: "path source" {
        0 => Literal(path: String),
        1 => Bound(name: String),
    }
    IconSource: "icon source" {
        0 => Id { kind: IconKind, id: UintSource },
        1 => File(path: PathSource),
    }
    ColumnAlign: "column align" {
        0 => Left,
        1 => Right,
    }
    ColumnHeader: "column header" {
        0 => None,
        1 => Text(text: TextSource),
        2 => SelectAll(onall: Option<String>),
    }
    SortKind: "sort kind" {
        0 => None,
        1 => Text,
        2 => Number,
        3 => Bool,
    }
    SortKeysSource: "sort keys" {
        0 => Numbers(name: String),
        1 => Texts(name: String),
    }
    ItemsSource: "items source" {
        0 => Literal(items: Vec<String>),
        1 => Bound(name: String),
    }
    NumberKind: "number kind" {
        0 => Int,
        1 => Float,
    }
    ColorValueKind: "colour value kind" {
        0 => Uint,
        1 => Int,
        2 => Text,
    }
    ColumnKind: "column kind" {
        0 => Text { items: String, colors: Option<String>, onclick: Option<String> },
        1 => Check { values: String, onchange: String, boxed: bool },
        2 => Icon { values: String, kind: IconKind, onclick: String },
        3 => Button { items: String, onclick: String, button_width: f32, button_height: f32 },
    }
    ListBody: "list body" {
        0 => Single { items: String, colors: Option<String>, icons: Option<(String, IconKind)> },
        1 => Columns(columns: Vec<Column>),
    }
    Element: "element" {
        0 => Group {
            background: Option<ColorSource>,
            border: Option<ColorSource>,
            layout: Option<FlexContainer>,
            scroll_x: bool,
            scroll_y: bool,
        },
        1 => Meter {
            fill: NumberSource,
            cur: NumberSource,
            max: NumberSource,
            color: ColorSource,
            sprites: [u32; 6],
        },
        2 => Label { text: TextSource, color: Option<ColorSource> },
        3 => Button {
            text: TextSource,
            onclick: Option<String>,
            color: Option<ColorSource>,
            background: Option<ColorSource>,
            border: Option<ColorSource>,
            icon: Option<IconSource>,
        },
        4 => Icon { icon: IconSource },
        5 => Tab { text: String, selected: String, onclick: Option<String>, icon: Option<IconSource> },
        6 => Toggle {
            text: TextSource,
            checked: String,
            onclick: Option<String>,
            color: Option<ColorSource>,
        },
        7 => Slider {
            min: f32,
            max: f32,
            value: NumberSource,
            onchange: Option<String>,
            retail_art: bool,
        },
        8 => Field {
            text: TextSource,
            onchange: Option<String>,
            onsubmit: Option<String>,
            onblur: Option<String>,
            onup: Option<String>,
            ondown: Option<String>,
            one_line: bool,
            editable: bool,
            background: ColorSource,
            color: ColorSource,
            default_background: bool,
            default_color: bool,
            max_length: i32,
            clear_on_submit: bool,
        },
        9 => Menu {
            items: String,
            selected: TextSource,
            onchange: Option<String>,
            searchable: bool,
            rows: i32,
            row_height: f32,
            open_upward: bool,
            retail_art: bool,
        },
        10 => Log { items: String, first_index: Option<String> },
        11 => List {
            selected: String,
            onchange: Option<String>,
            row_height: f32,
            selection_band: bool,
            multi: bool,
            body: ListBody,
            header: ListHeader,
            tree: Option<ListTree>,
        },
        12 => Checkbox {
            text: TextSource,
            checked: String,
            onchange: Option<String>,
            color: Option<ColorSource>,
        },
        13 => Number {
            value: String,
            onchange: String,
            kind: NumberKind,
            spec: NumberSpec,
            background: ColorSource,
            color: ColorSource,
            default_background: bool,
            default_color: bool,
        },
        14 => Radio {
            items: ItemsSource,
            selected: String,
            onchange: Option<String>,
            horizontal: bool,
            segmented: bool,
            color: Option<ColorSource>,
        },
        15 => Separator { color: Option<ColorSource> },
        16 => Expander {
            text: TextSource,
            expanded: Option<String>,
            onchange: Option<String>,
            open: bool,
            color: Option<ColorSource>,
            framed: bool,
        },
        17 => Split {
            horizontal: bool,
            min: f32,
            max: Option<f32>,
            default_position: f32,
            at_given: bool,
            position: Option<String>,
            onchange: Option<String>,
        },
        18 => Hotkey { value: String, onchange: String },
        19 => Color { value: String, kind: ColorValueKind, onchange: String, alpha: bool },
        20 => Canvas { canvas_id: String },
        21 => Repeat { items: String, key: Option<String> },
    }
    CanvasAnchor: "canvas anchor" {
        0 => TopLeft,
        1 => TopCenter,
        2 => TopRight,
        3 => CenterLeft,
        4 => Center,
        5 => CenterRight,
        6 => BottomLeft,
        7 => BottomCenter,
        8 => BottomRight,
    }
    PaintCmd: "paint command" {
        0 => Clear(color: Color8),
        1 => FillRect { rect: PaintRect, color: Color8 },
        2 => StrokeRect { rect: PaintRect, color: Color8, thickness: f32 },
        3 => Line { from: (f32, f32), to: (f32, f32), color: Color8, thickness: f32 },
        4 => Text { text: String, at: (f32, f32), color: Color8, outline: bool, font: u32 },
        5 => Image { image: u32, rect: PaintRect, tint: Color8 },
        6 => ImageTransformed {
            image: u32,
            rect: PaintRect,
            tint: Color8,
            rotation: f32,
            pivot: (f32, f32),
            scale_x: f32,
            scale_y: f32,
        },
        7 => PushClip(rect: PaintRect),
        8 => PopClip,
    }
    ImageSource: "image source" {
        0 => ClientArt(surface: u32),
        1 => SpellIcon { spell: u32, icon: u32 },
        2 => ObjectIcon(object: u32),
        3 => Rgba(pixels: Arc<Vec<u8>>),
        4 => File(image: Arc<FileImage>),
    }
    MarkerAnchor: "marker anchor" {
        0 => None,
        1 => Object(object: u32),
        2 => Position { point: MapPoint, is_outdoor: bool },
    }
    GroundShapeKind: "ground shape kind" {
        0 => None,
        1 => Ring,
        2 => Disc,
        3 => Arc,
    }
    ClientWindow: "client window" {
        0 => Inventory,
        1 => Character,
        2 => CharacterInformation,
        3 => Spellbook,
        4 => Map,
        5 => Options,
        6 => Social,
        7 => Journal,
        8 => PositiveEffects,
        9 => NegativeEffects,
        10 => LinkStatus,
        11 => Vitae,
        12 => Radar,
    }
    ClientWindowAction: "client window action" {
        0 => Toggle,
        1 => Show,
        2 => Hide,
    }
    UiChange: "plugin UI change" {
        0 => Clear,
        1 => PanelAdded(panel: Box<PanelView>),
        2 => PanelRemoved(panel: u32),
        3 => PanelValues { panel: u32, values: Vec<(String, UiValue)> },
        4 => PanelRequestedVisible { panel: u32, visible: bool },
        5 => ControlLabel { panel: u32, node: usize, label: String },
        6 => ControlVisible { panel: u32, node: usize, visible: bool },
        7 => CanvasAdded(canvas: Box<CanvasView>),
        8 => CanvasRemoved(canvas: u32),
        9 => CanvasPaint { canvas: u32, paint: Arc<Vec<PaintCmd>> },
        10 => CanvasPlacement { canvas: u32, visible: bool, anchor: CanvasAnchor, offset: (f32, f32) },
        11 => CanvasListening { canvas: u32, listening: bool },
        12 => CanvasReleasePointer(canvas: u32),
        13 => CanvasResized { canvas: u32, size: (i32, i32) },
        14 => ImageAdded(image: Box<ImageView>),
        15 => FileIcon { panel: u32, path: String, handle: u32 },
        16 => ImageRemoved { owner: String, handle: u32 },
        17 => Labels(labels: Arc<Vec<WorldLabel>>),
        18 => Lines(lines: Arc<Vec<WorldLine>>),
        19 => Triangles(triangles: Arc<Vec<WorldTriangle>>),
        20 => Icons(icons: Arc<Vec<WorldIconEntry>>),
        21 => Shapes(shapes: Arc<Vec<GroundShape>>),
        22 => ProjectileDebug(samples: Arc<Vec<ProjectileSample>>),
        23 => ClientWindow { window: ClientWindow, action: ClientWindowAction },
        24 => Hotkeys(hotkeys: Arc<Vec<BoundHotkey>>),
        25 => FontAdded(font: Box<FontView>),
        26 => FontRemoved { owner: String, handle: u32 },
        27 => DialogAdded(dialog: Box<DialogView>),
        28 => DialogRemoved(dialog: u32),
        29 => Modal { panel: u32, owner: Option<u32> },
    }
    PointerKind: "pointer kind" {
        0 => Down,
        1 => Up,
        2 => Move,
        3 => Wheel,
        4 => Cancelled,
        5 => Hover,
        6 => Leave,
    }
    ThemeKind: "theme" {
        0 => Classic,
        1 => Moss,
        2 => Brass,
        3 => LavenderDusk,
        4 => Ember,
        5 => Bloodmoon,
        6 => Glacier,
        7 => Parchment,
    }
    ActionValue: "action value" {
        0 => None,
        1 => Int(value: i32),
        2 => Float(value: f32),
        3 => Text(value: String),
        4 => Bool(value: bool),
        5 => Uint(value: u32),
        6 => IntBool(column: i32, descending: bool),
        7 => TextBool(key: String, state: bool),
        8 => FloatList(values: Vec<f32>),
        9 => IntList(values: Vec<i32>),
    }
    UiInput: "plugin UI input" {
        0 => Action { panel: u32, handler: String, value: ActionValue },
        1 => PanelShown { panel: u32, shown: bool },
        2 => Pointer { canvas: u32, event: PointerEvent, place: Option<CanvasPlace> },
        3 => ClientWindows { mounted: u16, visible: u16 },
        4 => ClientChords(chords: Vec<Chord>),
        5 => Hotkey { owner: String, id: String },
        6 => InputAction { action: String, activation: String },
        7 => Theme(theme: ThemeKind),
        8 => FileIconsShown { panel: u32, paths: Vec<String> },
        9 => DisplayDensity(density: f32),
        10 => CanvasHosted { canvas: u32, size: Option<(i32, i32)> },
        11 => DialogAnswered { handle: u32, button: i32 },
    }
}

/// A binding value. Item records hold binding values in turn: the shape
/// spells their layout out with a marker where the value recurs, and
/// reading them goes a level deeper each time ([`Reader::nested`]). One
/// list of variants gives the shape, the encoder and the decoder, so the
/// types read are the types declared.
macro_rules! ui_value {
    ($( $tag:literal => $variant:ident($ty:ty) ),+ $(,)?) => {
        /// The tag of item records.
        const UI_VALUE_ITEMS: u8 = 11;

        impl Wire for UiValue {
            const SHAPE: u64 = Shape::new("union")
                $( .tag($tag).name(stringify!($variant)).of(<$ty as Wire>::SHAPE) )+
                .tag(UI_VALUE_ITEMS as u64)
                .name("Items")
                // Arc<Vec<ItemRecord>>: a list of records whose one field,
                // `values`, maps names to values of this union.
                .of(Shape::new("list")
                    .of(Shape::new("record")
                        .name("values")
                        .of(Shape::new("map")
                            .of(<String as Wire>::SHAPE)
                            .name("this union")
                            .finish())
                        .finish())
                    .finish())
                .finish();
            fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
                match self {
                    $(
                        UiValue::$variant(value) => {
                            w.put_raw(&[$tag]);
                            <$ty as Wire>::encode(value, w, cx)
                        }
                    )+
                    UiValue::Items(items) => {
                        w.put_raw(&[UI_VALUE_ITEMS]);
                        <Arc<Vec<ItemRecord>> as Wire>::encode(items, w, cx)
                    }
                }
            }
            fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
                Ok(match r.byte()? {
                    $( $tag => UiValue::$variant(<$ty as Wire>::decode(r, cx)?), )+
                    UI_VALUE_ITEMS => UiValue::Items(
                        r.nested(|r| <Arc<Vec<ItemRecord>> as Wire>::decode(r, cx))?,
                    ),
                    tag => {
                        return Err(DecodeError::UnknownTag {
                            what: "binding value",
                            tag: u32::from(tag),
                        });
                    }
                })
            }
        }
    };
}

ui_value! {
    0 => Bool(bool),
    1 => Int(i32),
    2 => Uint(u32),
    3 => Float(f32),
    4 => Text(String),
    5 => TextList(Vec<String>),
    6 => UintList(Vec<u32>),
    7 => IntList(Vec<i32>),
    8 => BoolList(Vec<bool>),
    9 => OptionalBool(Option<bool>),
    10 => FloatList(Vec<f32>),
}
/// An exact decimal: its whole number and scale, checked on the way in.
impl Wire for Dec {
    const SHAPE: u64 = Shape::new("decimal")
        .of(i128::SHAPE)
        .of(u32::SHAPE)
        .finish();
    const MIN_SIZE: usize = 20;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        self.mantissa().encode(w, cx)?;
        self.scale().encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let mantissa = i128::decode(r, cx)?;
        let scale = u32::decode(r, cx)?;
        Dec::from_raw(mantissa, scale).ok_or(DecodeError::OutOfRange("decimal"))
    }
}

/// A baked font as it crosses: what it is baked from, its size and the
/// characters it has (as runs of code points), so the far side bakes the
/// same again.
struct BakeOnWire {
    source: FontSource,
    pixel_size: f32,
    characters: Vec<(u32, u32)>,
}

/// A sharper bake: its scale and size; it is baked from what the font is.
struct SharpOnWire {
    scale: f32,
    pixel_size: f32,
}

/// One font a plugin holds, as it crosses.
struct FontViewOnWire {
    owner: String,
    handle: u32,
    font: BakeOnWire,
    sharp: Option<SharpOnWire>,
}

union! {
    FontWeight: "font weight" {
        0 => Regular,
        1 => SemiBold,
    }
    FontSource: "font source" {
        0 => Bundled(weight: FontWeight),
        1 => Own(file: Arc<Vec<u8>>),
    }
}

record! {
    BakeOnWire { source: FontSource, pixel_size: f32, characters: Vec<(u32, u32)> }
    SharpOnWire { scale: f32, pixel_size: f32 }
    FontViewOnWire { owner: String, handle: u32, font: BakeOnWire, sharp: Option<SharpOnWire> }
}

/// `characters` as runs of consecutive code points.
fn runs(characters: &[char]) -> Vec<(u32, u32)> {
    let mut runs: Vec<(u32, u32)> = Vec::new();
    for &c in characters {
        let c = u32::from(c);
        match runs.last_mut() {
            Some((_, last)) if last.checked_add(1) == Some(c) => *last = c,
            _ => runs.push((c, c)),
        }
    }
    runs
}

/// Bakes `source` at `pixel_size` with the characters `ranges` names, as
/// the session bakes plugin fonts. What the stream asks for is checked
/// against the session's own bounds first; the baker refuses the rest.
fn bake_again(
    source: &FontSource,
    pixel_size: f32,
    ranges: &[(u32, u32)],
) -> Result<FontBake, DecodeError> {
    if !(fonts::MINIMUM_PIXEL_SIZE..=fonts::MAXIMUM_SHARP_PIXEL_SIZE).contains(&pixel_size) {
        return Err(DecodeError::OutOfRange("font size"));
    }
    let baked = match source {
        FontSource::Bundled(weight) => fonts::bake_bundled(*weight, pixel_size, ranges),
        FontSource::Own(file) => {
            if file.len() > fonts::MAXIMUM_FILE_BYTES {
                return Err(DecodeError::OutOfRange("font file"));
            }
            fonts::bake((**file).clone(), pixel_size, ranges, fonts::MAXIMUM_GLYPHS)
        }
    };
    baked.map_err(DecodeError::Font)
}

/// A plugin's font goes as what it is baked from and is baked again on
/// arrival: a window tells fonts apart by the identity of their bake, so
/// the copy is a new font that draws and measures the same.
impl Wire for FontView {
    const SHAPE: u64 = <FontViewOnWire as Wire>::SHAPE;
    fn encode(&self, w: &mut Writer, cx: &EncodeContext) -> Result<(), EncodeError> {
        let FontView {
            owner,
            handle,
            font,
            sharp,
        } = self;
        FontViewOnWire {
            owner: owner.clone(),
            handle: *handle,
            font: BakeOnWire {
                source: font.source(),
                pixel_size: font.pixel_size,
                characters: runs(&font.characters()),
            },
            sharp: sharp.as_ref().map(|SharpBake { scale, font }| SharpOnWire {
                scale: *scale,
                pixel_size: font.pixel_size,
            }),
        }
        .encode(w, cx)
    }
    fn decode(r: &mut Reader<'_>, cx: &DecodeContext) -> Result<Self, DecodeError> {
        let FontViewOnWire {
            owner,
            handle,
            font:
                BakeOnWire {
                    source,
                    pixel_size,
                    characters,
                },
            sharp,
        } = FontViewOnWire::decode(r, cx)?;
        let font = Arc::new(bake_again(&source, pixel_size, &characters)?);
        let sharp = match sharp {
            None => None,
            Some(SharpOnWire { scale, pixel_size }) => Some(SharpBake {
                scale,
                font: Arc::new(bake_again(&source, pixel_size, &characters)?),
            }),
        };
        Ok(FontView {
            owner,
            handle,
            font,
            sharp,
        })
    }
}
