//! The map and house panel (layout 0x2100006E, slot 0x1000018C): a tab
//! panel with the map page (the in-game date and time, the world map with
//! the player's and the house's markers, the player's coordinates and the
//! 53 town hotspots) and the house page (the house's lines), and a close
//! button.
//!
//! The panel shows what the client hands each update ([`MapFacts`], the
//! house lines); the map refreshes every five seconds, the house page when
//! its lines change.

use ac_core::world::{CalendarDate, HourName};
use ac_dat::objects::PropertyValue;
use ac_runtime::gameplay::HouseLineColor;

use crate::element_info::ElementInfo;
use crate::geometry::AnchorEdges;
use crate::host::PanelController;
use crate::import::{BuildContext, ImportedLayout, build, import_infos_rooted};
use crate::panels::items::{bind_click, emit};
use crate::panels::{DatRowTemplates, PanelOutput, RowTemplates, panel_id};
use crate::radar::RadarCoordinates;
use crate::root::{ElementMessage, UiRoot};
use crate::text::Line;
use crate::tree::ElementId;
use crate::widgets::{Button, ListBox, TabPanel, Text};

/// The layout hosting the main panels.
pub const HOST_LAYOUT_ID: u32 = 0x2100_006E;
/// The panel's slot in it.
pub const SLOT_ELEMENT_ID: u32 = 0x1000_018C;
/// The map tab's button.
pub const MAP_BUTTON_ID: u32 = 0x1000_01F3;
/// The map page.
pub const MAP_PAGE_ID: u32 = 0x1000_01F6;
/// The house tab's button.
pub const HOUSE_BUTTON_ID: u32 = 0x1000_01F4;
/// The house page.
pub const HOUSE_PAGE_ID: u32 = 0x1000_01F7;
/// The close button.
pub const CLOSE_BUTTON_ID: u32 = 0x1000_01F5;

/// The date and time text.
pub const DATE_TIME_TEXT_ID: u32 = 0x1000_01EB;
/// The map.
pub const MAP_WIDGET_ID: u32 = 0x1000_01EC;
/// The player's marker.
pub const PLAYER_ICON_ID: u32 = 0x1000_01ED;
/// The house's marker.
pub const HOUSE_ICON_ID: u32 = 0x1000_01EE;
/// The coordinates text.
pub const COORDINATE_TEXT_ID: u32 = 0x1000_01EF;
/// The house page's list.
pub const HOUSE_TEXT_BOX_ID: u32 = 0x1000_01E6;

/// The map's marker area properties (left, right, top, bottom pixel).
const MARKER_AREA_X0: u32 = 0x1000_004E;
const MARKER_AREA_X1: u32 = 0x1000_004F;
const MARKER_AREA_Y0: u32 = 0x1000_0050;
const MARKER_AREA_Y1: u32 = 0x1000_0051;
/// The town hotspot template's element and layout properties.
const HOTSPOT_TEMPLATE_ELEMENT: u32 = 0x47;
const HOTSPOT_TEMPLATE_LAYOUT: u32 = 0x48;

/// How often the map refreshes, seconds.
pub const REFRESH_INTERVAL_SECONDS: f64 = 5.0;

/// A town hotspot: its rectangle on the map, pixels, and its name.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MapLocation {
    /// Left.
    pub x: i32,
    /// Top.
    pub y: i32,
    /// Width.
    pub width: i32,
    /// Height.
    pub height: i32,
    /// The town.
    pub name: &'static str,
}

const fn loc(x: i32, y: i32, width: i32, height: i32, name: &'static str) -> MapLocation {
    MapLocation {
        x,
        y,
        width,
        height,
        name,
    }
}

/// The 53 town hotspots of the world map, in the game's order.
pub const MAP_LOCATIONS: [MapLocation; 53] = [
    loc(0xb2, 0x14, 0xb, 0xc, "Aerlinthe Island"),
    loc(0x12, 0x4a, 0x5, 0x5, "Ahurenga"),
    loc(0x8d, 0xa6, 0x7, 0x6, "Al-Arqas"),
    loc(0x81, 0x79, 0x7, 0x6, "Al-Jalima"),
    loc(0xbe, 0x58, 0x9, 0x8, "Arwic"),
    loc(0x13, 0xc9, 0x7, 0x6, "Ayan Baqur"),
    loc(0xc8, 0xbe, 0x7, 0x6, "Baishi"),
    loc(0xb8, 0x35, 0x5, 0x5, "Bandit Castle"),
    loc(0x22, 0x54, 0x5, 0x5, "Bluespire"),
    loc(0x2c, 0xeb, 0x5, 0x5, "Candeth Keep"),
    loc(0xb4, 0x61, 0x9, 0x8, "Cragstone"),
    loc(0x5b, 0x66, 0x5, 0x5, "Danby's Outpost"),
    loc(0xd3, 0x8a, 0x9, 0x8, "Dryreach"),
    loc(0xc7, 0x6a, 0x9, 0x8, "Eastham"),
    loc(0x38, 0xd, 0x5, 0x5, "Fiun Outpost"),
    loc(0x25, 0x7f, 0x9, 0x8, "Fort Tethana"),
    loc(0x9c, 0x5e, 0x9, 0x8, "Glenden Wood"),
    loc(0x2b, 0x4f, 0x5, 0x5, "Greenspire"),
    loc(0xe0, 0xb1, 0x7, 0x6, "Hebian-to"),
    loc(0xa4, 0x4d, 0x9, 0x8, "Holtburg"),
    loc(0xb6, 0xe6, 0x7, 0x6, "Kara"),
    loc(0x9b, 0xb9, 0x7, 0x6, "Khayyaban"),
    loc(0xe0, 0xda, 0x7, 0x6, "Kryst"),
    loc(0xd4, 0xc3, 0x7, 0x6, "Lin"),
    loc(0x9f, 0xe0, 0x5, 0x5, "Linvak Tukal"),
    loc(0xb9, 0x7e, 0x9, 0x8, "Lytelthorpe"),
    loc(0xeb, 0xdc, 0x7, 0x6, "MacNiall's Freehold"),
    loc(0xdf, 0xcb, 0x7, 0x6, "Mayoi"),
    loc(0x8d, 0x35, 0x5, 0x5, "Mt Esper-Crater Village"),
    loc(0xe0, 0xbf, 0x7, 0x6, "Nanto"),
    loc(0x8e, 0x2e, 0x5, 0x5, "Neydisa"),
    loc(0xf0, 0x80, 0x5, 0x5, "Oolutanga's Refuge"),
    loc(0x4a, 0x4f, 0x5, 0x5, "Plateau Village"),
    loc(0x94, 0xda, 0x7, 0x6, "Qalaba'r"),
    loc(0x1a, 0x53, 0x5, 0x5, "Redspire"),
    loc(0xc1, 0x72, 0x9, 0x8, "Rithwic"),
    loc(0x92, 0x85, 0x7, 0x6, "Samsur"),
    loc(0x32, 0x2a, 0x5, 0x5, "Sanamar"),
    loc(0xc3, 0xa3, 0x7, 0x6, "Sawato"),
    loc(0xd5, 0xab, 0x7, 0x6, "Shoushi"),
    loc(0x29, 0x19, 0x5, 0x5, "Silyun"),
    loc(0x6, 0xef, 0xf, 0x10, "Singularity Caul Island"),
    loc(0x64, 0x30, 0x5, 0x5, "Stonehold"),
    loc(0x20, 0x4c, 0x5, 0x5, "Timaru"),
    loc(0xef, 0xa3, 0x7, 0x6, "Tou-Tou"),
    loc(0x83, 0x94, 0x7, 0x6, "Tufa"),
    loc(0x70, 0xf4, 0x5, 0x5, "Ulgrim's Island"),
    loc(0x9f, 0xa0, 0x7, 0x6, "Uziz"),
    loc(0x3f, 0xcb, 0x7, 0x6, "Wai Jhou"),
    loc(0x90, 0xb5, 0x7, 0x6, "Xarabydun"),
    loc(0xaf, 0x91, 0x7, 0x6, "Yanshi"),
    loc(0x79, 0x9c, 0x7, 0x6, "Yaraq"),
    loc(0x7b, 0x70, 0x7, 0x6, "Zaikhal"),
];

/// "Date: Frostfell 27, 119 P.Y." then "Time: Dawnsong" (a half hour
/// "Morntide-and-Half").
#[must_use]
pub fn format_date_time(date: &CalendarDate) -> String {
    format!(
        "Date: {:?} {}, {} P.Y.\nTime: {}",
        date.month,
        date.day,
        date.year,
        hour_text(date.hour)
    )
}

/// An hour's name as the game shows it: "Dawnsong", a half hour
/// "Morntide-and-Half".
#[must_use]
pub fn hour_text(hour: HourName) -> String {
    let name = format!("{hour:?}");
    match name.strip_suffix("AndHalf") {
        Some(name) => format!("{name}-and-Half"),
        None => name,
    }
}

/// Where a marker of `icon_w` x `icon_h` goes on a map whose marker area
/// is x0..=x1, y0..=y1, for map coordinates (`x` east, `y` north).
#[must_use]
#[allow(clippy::too_many_arguments)]
pub fn marker_position(
    x0: i32,
    x1: i32,
    y0: i32,
    y1: i32,
    icon_w: i32,
    icon_h: i32,
    x: f64,
    y: f64,
) -> (f32, f32) {
    let half_w = icon_w / 2;
    let half_h = icon_h / 2;
    let extent_x = f64::from(x1 - x0 + 1);
    let extent_y = f64::from(y1 - y0 + 1);
    let x_offset = (extent_x * (x * 10.0 + 1024.0) * (-1.0 / 2048.0)) as i32;
    let y_offset = (extent_y * (2047.0 - (y * 10.0 + 1024.0)) * (-1.0 / 2048.0)) as i32;
    (
        (x0 - half_w - x_offset) as f32,
        (y0 - half_h - y_offset) as f32,
    )
}

/// What the map shows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct MapFacts {
    /// The in-game date and time, once known.
    pub calendar: Option<CalendarDate>,
    /// The cell the player stands in (0: unknown).
    pub player_cell: u32,
    /// The cell the player's house stands in.
    pub house_cell: Option<u32>,
}

fn integer(value: Option<&PropertyValue>) -> Option<u32> {
    match value? {
        PropertyValue::Integer(v) => u32::try_from(*v).ok(),
        PropertyValue::Enum(v) | PropertyValue::DataId(v) => Some(*v),
        _ => None,
    }
}

fn find_info(info: &ElementInfo, id: u32) -> Option<&ElementInfo> {
    if info.id == id {
        return Some(info);
    }
    info.children.iter().find_map(|c| find_info(c, id))
}

fn set_lines(ui: &mut UiRoot, id: Option<ElementId>, text: &str) {
    if let Some(t) = id.and_then(|id| ui.widget_mut::<Text>(id)) {
        let color = t.default_color;
        t.set_lines(
            text.split('\n')
                .filter(|_| !text.is_empty())
                .map(|l| Line::new(l, color))
                .collect(),
        );
    }
}

/// The map page.
#[derive(Debug)]
pub struct MapPageController {
    date_time: Option<ElementId>,
    map: ElementId,
    player_icon: Option<ElementId>,
    house_icon: Option<ElementId>,
    coordinates: Option<ElementId>,
    area: (i32, i32, i32, i32),
    markers: Vec<ElementId>,
    next_update_seconds: f64,
}

/// Builds what the map adds to its authored tree: the markers its
/// description holds but the build left out, and the town hotspots from
/// their template (whose highlight child is built on its own).
pub trait MapBuilder: RowTemplates {
    /// The description of element `element` of layout `layout`.
    fn template_info(&self, layout: u32, element: u32) -> Option<ElementInfo>;
    /// Build `info` with its subtree; it comes back detached.
    fn build_info(&mut self, ui: &mut UiRoot, info: &ElementInfo) -> Option<ElementId>;
}

impl MapBuilder for DatRowTemplates<'_> {
    fn template_info(&self, layout: u32, element: u32) -> Option<ElementInfo> {
        import_infos_rooted(self.source, layout, element)
    }

    fn build_info(&mut self, ui: &mut UiRoot, info: &ElementInfo) -> Option<ElementId> {
        let ctx = BuildContext {
            source: self.source,
            default_font: self.font.clone(),
            source_layout_did: 0,
            media_seconds: ui.media_seconds(),
        };
        Some(build(ui, info, &ctx).root)
    }
}

impl MapPageController {
    /// Bind to the map page `page` described by `page_info`: the markers
    /// the map swallows and the town hotspots are built with `builder`.
    /// `None` without the map.
    pub fn bind(
        ui: &mut UiRoot,
        page: ElementId,
        page_info: &ElementInfo,
        builder: &mut dyn MapBuilder,
    ) -> Option<Self> {
        let map = ui.find_descendant(page, MAP_WIDGET_ID)?;
        let map_info = find_info(page_info, MAP_WIDGET_ID);
        let area = map_info.map_or((0, 0, 0, 0), |i| {
            let v = |id| i.effective_integer(id, None).unwrap_or(0);
            (
                v(MARKER_AREA_X0),
                v(MARKER_AREA_X1),
                v(MARKER_AREA_Y0),
                v(MARKER_AREA_Y1),
            )
        });
        let mut icon = |ui: &mut UiRoot, id| -> Option<ElementId> {
            let found = match ui.find_descendant(map, id) {
                Some(e) => Some(e),
                None => {
                    let info = map_info.and_then(|i| find_info(i, id))?;
                    let built = builder.build_info(ui, info)?;
                    ui.add_child(map, built);
                    Some(built)
                }
            };
            if let Some(e) = found {
                ui.set_anchors(e, AnchorEdges::NONE);
                ui.set_visible(e, false);
            }
            found
        };
        let player_icon = icon(ui, PLAYER_ICON_ID);
        let house_icon = icon(ui, HOUSE_ICON_ID);
        let text = |ui: &UiRoot, id| {
            ui.find_descendant(page, id)
                .filter(|t| ui.widget::<Text>(*t).is_some())
        };
        let mut c = Self {
            date_time: text(ui, DATE_TIME_TEXT_ID),
            map,
            player_icon,
            house_icon,
            coordinates: text(ui, COORDINATE_TEXT_ID),
            area,
            markers: Vec::new(),
            next_update_seconds: 0.0,
        };
        if let Some(info) = map_info {
            c.build_town_markers(ui, info, builder);
        }
        Some(c)
    }

    fn build_town_markers(
        &mut self,
        ui: &mut UiRoot,
        map_info: &ElementInfo,
        builder: &mut dyn MapBuilder,
    ) {
        let element = integer(map_info.effective_property(HOTSPOT_TEMPLATE_ELEMENT, None));
        let layout = integer(map_info.effective_property(HOTSPOT_TEMPLATE_LAYOUT, None));
        let (Some(element), Some(layout)) = (element, layout) else {
            return;
        };
        if layout == 0 {
            return;
        }
        let template = builder.template_info(layout, element);
        for town in &MAP_LOCATIONS {
            let Some(marker) = builder.build_row(ui, layout, element) else {
                continue;
            };
            // The rollover highlight: the template's children, built on
            // their own (the hotspot's build leaves them out).
            if let Some(info) = &template {
                for child in &info.children {
                    if ui.find_descendant(marker, child.id).is_none()
                        && let Some(highlight) = builder.build_info(ui, child)
                    {
                        ui.add_child(marker, highlight);
                    }
                }
            }
            if let Some(e) = ui.element_mut(marker) {
                e.left = town.x as f32;
                e.top = town.y as f32;
                e.width = town.width as f32;
                e.height = town.height as f32;
            }
            if let Some(b) = ui.widget_mut::<Button>(marker) {
                b.tooltip_text = Some(town.name.to_owned());
            }
            ui.add_child(self.map, marker);
            ui.set_retail_state(marker, crate::states::NORMAL);
            self.markers.push(marker);
        }
    }

    /// The town hotspots, in the table's order.
    #[must_use]
    pub fn markers(&self) -> &[ElementId] {
        &self.markers
    }

    /// The map.
    #[must_use]
    pub fn map(&self) -> ElementId {
        self.map
    }

    /// Count `dt` seconds down; every five seconds (and on the first
    /// update) show `facts`.
    pub fn update(&mut self, ui: &mut UiRoot, facts: &MapFacts, dt: f64) {
        self.next_update_seconds -= dt;
        if self.next_update_seconds > 0.0 {
            return;
        }
        self.next_update_seconds = REFRESH_INTERVAL_SECONDS;
        self.refresh(ui, facts);
    }

    /// Show `facts` now.
    pub fn refresh(&mut self, ui: &mut UiRoot, facts: &MapFacts) {
        if self.date_time.is_some() {
            let text = facts
                .calendar
                .map(|c| format_date_time(&c))
                .unwrap_or_default();
            set_lines(ui, self.date_time, &text);
        }
        if let (Some(_), Some(icon)) = (self.coordinates, self.player_icon) {
            match RadarCoordinates::from_cell(facts.player_cell) {
                Some(c) => {
                    set_lines(ui, self.coordinates, &c.combined_text());
                    self.place(ui, icon, c.x, c.y);
                }
                None => {
                    set_lines(ui, self.coordinates, "");
                    ui.set_visible(icon, false);
                }
            }
        }
        if let Some(icon) = self.house_icon {
            match facts.house_cell.and_then(RadarCoordinates::from_cell) {
                Some(c) => self.place(ui, icon, c.x, c.y),
                None => ui.set_visible(icon, false),
            }
        }
    }

    fn place(&self, ui: &mut UiRoot, icon: ElementId, x: f64, y: f64) {
        let (w, h) = ui
            .element(icon)
            .map_or((0, 0), |e| (e.width as i32, e.height as i32));
        let (x0, x1, y0, y1) = self.area;
        let (left, top) = marker_position(x0, x1, y0, y1, w, h, x, y);
        if let Some(e) = ui.element_mut(icon) {
            e.left = left;
            e.top = top;
        }
        ui.set_visible(icon, true);
    }
}

/// The house page.
#[derive(Debug)]
pub struct HousePageController {
    list: ElementId,
    last: Option<Vec<(String, HouseLineColor)>>,
}

impl HousePageController {
    /// Bind to the house page (`None` without its list).
    pub fn bind(ui: &mut UiRoot, page: ElementId) -> Option<Self> {
        let list = ui
            .find_descendant(page, HOUSE_TEXT_BOX_ID)
            .filter(|l| ui.widget::<ListBox>(*l).is_some())?;
        Some(Self { list, last: None })
    }

    /// The list.
    #[must_use]
    pub fn list(&self) -> ElementId {
        self.list
    }

    /// Show `lines` when they changed: one row per line from the list's
    /// template, its colour the template's palette entry for the line's
    /// tint (normal 0, rent paid 1, rent due 2).
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        lines: &[(String, HouseLineColor)],
        rows: &mut dyn RowTemplates,
    ) {
        if self.last.as_deref() == Some(lines) {
            return;
        }
        self.last = Some(lines.to_vec());
        let list = self.list;
        let template = ui
            .widget::<ListBox>(list)
            .and_then(|b| b.templates.first().copied());
        ui.with::<ListBox, _>(list, |b, ui| b.flush(ui));
        let Some(t) = template else {
            return;
        };
        for (text, tint) in lines {
            let Some(row) = rows.build_row(ui, t.layout, t.element) else {
                continue;
            };
            ui.with::<ListBox, _>(list, |b, ui| b.add_prebuilt_row(ui, list, row));
            if let Some(w) = ui.widget_mut::<Text>(row) {
                let index = match tint {
                    HouseLineColor::Normal => 0,
                    HouseLineColor::RentPaid => 1,
                    HouseLineColor::RentNotPaid => 2,
                };
                let color = w
                    .font_color_palette
                    .get(index)
                    .copied()
                    .unwrap_or(w.default_color);
                w.set_lines(vec![Line::new(text.clone(), color)]);
            }
        }
    }
}

/// The map and house panel's controller.
#[derive(Debug)]
pub struct MapHousePanelController {
    tab_panel: ElementId,
    close: Option<ElementId>,
    map: Option<MapPageController>,
    house: Option<HousePageController>,
}

impl MapHousePanelController {
    /// Bind to the imported slot described by `root_info` (`None` unless
    /// its root is a tab panel).
    pub fn bind(
        ui: &mut UiRoot,
        root_info: &ElementInfo,
        layout: &ImportedLayout,
        builder: &mut dyn MapBuilder,
    ) -> Option<Self> {
        let tab_panel = layout.root;
        ui.widget::<TabPanel>(tab_panel)?;
        let close = layout.find(CLOSE_BUTTON_ID).filter(|b| bind_click(ui, *b));
        let map = ui
            .find_descendant(tab_panel, MAP_PAGE_ID)
            .zip(find_info(root_info, MAP_PAGE_ID))
            .and_then(|(page, info)| MapPageController::bind(ui, page, info, builder));
        let house = ui
            .find_descendant(tab_panel, HOUSE_PAGE_ID)
            .and_then(|page| HousePageController::bind(ui, page));
        Some(Self {
            tab_panel,
            close,
            map,
            house,
        })
    }

    /// The tab panel.
    #[must_use]
    pub fn tab_panel(&self) -> ElementId {
        self.tab_panel
    }

    /// The map page.
    #[must_use]
    pub fn map(&self) -> Option<&MapPageController> {
        self.map.as_ref()
    }

    /// The house page.
    #[must_use]
    pub fn house(&self) -> Option<&HousePageController> {
        self.house.as_ref()
    }

    /// Start the tabs: the authored default (the map) shows.
    pub fn activate_tabs(&mut self, ui: &mut UiRoot) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.activate(ui, t));
    }

    /// The map page shows.
    #[must_use]
    pub fn is_showing_map(&self, ui: &UiRoot) -> bool {
        ui.widget::<TabPanel>(self.tab_panel)
            .is_some_and(|t| t.active_page() == MAP_PAGE_ID)
    }

    /// The house page shows.
    #[must_use]
    pub fn is_showing_house(&self, ui: &UiRoot) -> bool {
        ui.widget::<TabPanel>(self.tab_panel)
            .is_some_and(|t| t.active_page() == HOUSE_PAGE_ID)
    }

    /// Show the map page.
    pub fn show_map(&mut self, ui: &mut UiRoot) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.switch_to(ui, t, MAP_PAGE_ID));
    }

    /// Show the house page.
    pub fn show_house(&mut self, ui: &mut UiRoot) {
        let t = self.tab_panel;
        ui.with::<TabPanel, _>(t, |w, ui| w.switch_to(ui, t, HOUSE_PAGE_ID));
    }

    /// Show the map's facts (every five seconds) and the house's lines.
    pub fn update(
        &mut self,
        ui: &mut UiRoot,
        map: &MapFacts,
        dt: f64,
        house_lines: &[(String, HouseLineColor)],
        rows: &mut dyn RowTemplates,
    ) {
        if let Some(m) = self.map.as_mut() {
            m.update(ui, map, dt);
        }
        if let Some(h) = self.house.as_mut() {
            h.update(ui, house_lines, rows);
        }
    }
}

impl PanelController for MapHousePanelController {
    fn on_element_message(
        &mut self,
        ui: &mut UiRoot,
        element: ElementId,
        message: &ElementMessage,
    ) -> bool {
        if Some(element) == self.close {
            if matches!(message, ElementMessage::Clicked { .. }) {
                emit(ui, PanelOutput::ClosePanel(panel_id::MAP_HOUSE));
            }
            return true;
        }
        false
    }
}
