//! Map contracts (OpenAC `PluginMapContracts`, `PluginMapSurface`,
//! `PluginMapResources`): a map's viewport and image in
//! the canonical map coordinates (east-west and north-south in 240 m map
//! units, north and east positive), the converter between them and an
//! image's pixels, markers, a route, and a stateful surface a plugin keeps.
//!
//! No client draws plugin maps (OpenAC's hosts all answer with the inert
//! registry); a plugin draws its map on a canvas, and these are the shared
//! vocabulary and arithmetic for it.

use super::MapPoint;

/// Why a map value was refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MapError {
    /// A width or height of zero or less.
    NotPositive,
    /// The surface was disposed.
    Disposed,
}

impl std::fmt::Display for MapError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::NotPositive => "map dimensions must be positive",
            Self::Disposed => "the map surface was disposed",
        })
    }
}

impl std::error::Error for MapError {}

/// A rectangle of the world in map units around a centre.
#[derive(Debug, Clone, PartialEq)]
pub struct MapViewport {
    center: MapPoint,
    width: f64,
    height: f64,
}

impl MapViewport {
    /// A viewport around `center`, `width` east-west by `height`
    /// north-south.
    ///
    /// # Errors
    /// [`MapError::NotPositive`].
    pub fn new(center: MapPoint, width: f64, height: f64) -> Result<Self, MapError> {
        if width <= 0.0 || height <= 0.0 {
            return Err(MapError::NotPositive);
        }
        Ok(Self {
            center,
            width,
            height,
        })
    }

    /// The point in the middle.
    #[must_use]
    pub fn center(&self) -> MapPoint {
        self.center.clone()
    }

    /// The east-west extent.
    #[must_use]
    pub fn width(&self) -> f64 {
        self.width
    }

    /// The north-south extent.
    #[must_use]
    pub fn height(&self) -> f64 {
        self.height
    }
}

/// Pixels in a map image, top-left origin.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MapPixel {
    /// From the left edge.
    pub x: f64,
    /// Down from the top edge.
    pub y: f64,
}

/// A converter for an image whose bounds are known in map units, north up
/// (OpenAC `LinearPluginMapCoordinateConverter`).
#[derive(Debug, Clone, PartialEq)]
pub struct LinearMapConverter {
    /// The world the whole image covers.
    pub world_bounds: MapViewport,
}

fn check_image(width: f64, height: f64) -> Result<(), MapError> {
    if width <= 0.0 || height <= 0.0 {
        Err(MapError::NotPositive)
    } else {
        Ok(())
    }
}

impl LinearMapConverter {
    /// Where `world` falls on an image of the given size.
    ///
    /// # Errors
    /// [`MapError::NotPositive`] for the image size.
    pub fn world_to_pixel(
        &self,
        world: MapPoint,
        image_width: f64,
        image_height: f64,
    ) -> Result<MapPixel, MapError> {
        check_image(image_width, image_height)?;
        let b = &self.world_bounds;
        Ok(MapPixel {
            x: (world.east_west - (b.center.east_west - b.width / 2.0)) / b.width * image_width,
            y: (b.center.north_south + b.height / 2.0 - world.north_south) / b.height
                * image_height,
        })
    }

    /// Which world point a pixel shows (no elevation).
    ///
    /// # Errors
    /// [`MapError::NotPositive`] for the image size.
    pub fn pixel_to_world(
        &self,
        pixel: MapPixel,
        image_width: f64,
        image_height: f64,
    ) -> Result<MapPoint, MapError> {
        check_image(image_width, image_height)?;
        let b = &self.world_bounds;
        Ok(MapPoint {
            east_west: b.center.east_west - b.width / 2.0 + pixel.x / image_width * b.width,
            north_south: b.center.north_south + b.height / 2.0 - pixel.y / image_height * b.height,
            elevation: 0.0,
        })
    }
}

/// A map image and how it relates to the world.
#[derive(Debug, Clone, PartialEq)]
pub struct MapImage {
    /// The image's id among the plugin's resources.
    pub id: String,
    /// Width, pixels.
    pub pixel_width: i32,
    /// Height, pixels.
    pub pixel_height: i32,
    /// Positions on the image and in the world.
    pub coordinates: LinearMapConverter,
}

/// A marker on a map.
#[derive(Debug, Clone, PartialEq)]
pub struct MapMarker {
    /// Unique on its map; input about it names it.
    pub id: String,
    /// Where it stands.
    pub position: MapPoint,
    /// Text beside it.
    pub label: Option<String>,
    /// An icon among the plugin's resources (`None`: the default marker).
    pub icon_id: Option<String>,
    /// Text when the pointer rests on it.
    pub tooltip: Option<String>,
    /// Drawn as the selected one.
    pub is_selected: bool,
}

impl MapMarker {
    /// A plain marker.
    #[must_use]
    pub fn new(id: &str, position: MapPoint) -> Self {
        Self {
            id: id.to_owned(),
            position,
            label: None,
            icon_id: None,
            tooltip: None,
            is_selected: false,
        }
    }
}

/// What the pointer did over a map.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MapInputKind {
    /// A button went down.
    PointerDown,
    /// A button came up.
    PointerUp,
    /// Down and up in one place.
    Click,
    /// The wheel turned.
    Wheel,
    /// Moved with a button held.
    Drag,
}

/// Input over a map.
#[derive(Debug, Clone, PartialEq)]
pub struct MapInput {
    /// What happened.
    pub kind: MapInputKind,
    /// Where on the image.
    pub position: MapPixel,
    /// Wheel turn (wheel events only).
    pub wheel_delta: f64,
    /// The marker under the pointer.
    pub marker_id: Option<String>,
}

/// A stateful map a plugin keeps (OpenAC `PluginMapSurface`): its
/// viewport, background, markers and route, copied on set; nothing can be
/// set after it is disposed.
#[derive(Debug, Clone, PartialEq)]
pub struct MapSurface {
    /// The part of the world shown; set it to pan or zoom.
    pub viewport: MapViewport,
    background: Option<MapImage>,
    markers: Vec<MapMarker>,
    route: Vec<MapPoint>,
    disposed: bool,
}

impl MapSurface {
    /// A surface showing `viewport`.
    #[must_use]
    pub fn new(viewport: MapViewport) -> Self {
        Self {
            viewport,
            background: None,
            markers: Vec::new(),
            route: Vec::new(),
            disposed: false,
        }
    }

    /// The image under the markers.
    #[must_use]
    pub fn background(&self) -> Option<&MapImage> {
        self.background.as_ref()
    }

    /// The markers.
    #[must_use]
    pub fn markers(&self) -> &[MapMarker] {
        &self.markers
    }

    /// The route, in walking order.
    #[must_use]
    pub fn route(&self) -> &[MapPoint] {
        &self.route
    }

    fn live(&self) -> Result<(), MapError> {
        if self.disposed {
            Err(MapError::Disposed)
        } else {
            Ok(())
        }
    }

    /// Sets the background.
    ///
    /// # Errors
    /// [`MapError::Disposed`].
    pub fn set_background(&mut self, image: MapImage) -> Result<(), MapError> {
        self.live()?;
        self.background = Some(image);
        Ok(())
    }

    /// Replaces the markers (copied).
    ///
    /// # Errors
    /// [`MapError::Disposed`].
    pub fn set_markers(&mut self, markers: &[MapMarker]) -> Result<(), MapError> {
        self.live()?;
        self.markers = markers.to_vec();
        Ok(())
    }

    /// Replaces the route (copied).
    ///
    /// # Errors
    /// [`MapError::Disposed`].
    pub fn set_route(&mut self, points: &[MapPoint]) -> Result<(), MapError> {
        self.live()?;
        self.route = points.to_vec();
        Ok(())
    }

    /// Drops the background, markers and route; setting anything after
    /// this fails.
    pub fn dispose(&mut self) {
        self.disposed = true;
        self.background = None;
        self.markers.clear();
        self.route.clear();
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Plugin.Tests/PluginMapRenderingContractTests.cs`.

    use super::*;

    fn point(east_west: f64, north_south: f64) -> MapPoint {
        MapPoint {
            east_west,
            north_south,
            elevation: 0.0,
        }
    }

    #[test]
    fn coordinate_conversion_round_trips_and_uses_top_left_pixels() {
        let bounds = MapViewport::new(point(100.0, 200.0), 20.0, 40.0).unwrap();
        let converter = LinearMapConverter {
            world_bounds: bounds,
        };
        assert_eq!(
            converter.world_to_pixel(point(110.0, 220.0), 1000.0, 800.0),
            Ok(MapPixel { x: 1000.0, y: 0.0 })
        );
        let world = converter
            .pixel_to_world(MapPixel { x: 1000.0, y: 0.0 }, 1000.0, 800.0)
            .unwrap();
        assert!((world.east_west - 110.0).abs() < 1e-10);
        assert!((world.north_south - 220.0).abs() < 1e-10);
    }

    #[test]
    fn invalid_map_dimensions_are_rejected() {
        assert_eq!(
            MapViewport::new(MapPoint::default(), 0.0, 1.0),
            Err(MapError::NotPositive)
        );
        let converter = LinearMapConverter {
            world_bounds: MapViewport::new(MapPoint::default(), 1.0, 1.0).unwrap(),
        };
        assert_eq!(
            converter.world_to_pixel(MapPoint::default(), 0.0, 1.0),
            Err(MapError::NotPositive)
        );
    }

    #[test]
    fn a_surface_copies_its_state_and_refuses_use_after_dispose() {
        let mut surface =
            MapSurface::new(MapViewport::new(MapPoint::default(), 10.0, 10.0).unwrap());
        let mut markers = vec![MapMarker::new("a", MapPoint::default())];
        surface.set_markers(&markers).unwrap();
        markers[0] = MapMarker::new("b", MapPoint::default());
        assert_eq!(surface.markers()[0].id, "a");
        surface.dispose();
        assert_eq!(surface.set_route(&[]), Err(MapError::Disposed));
        assert!(surface.markers().is_empty() && surface.background().is_none());
    }
}
