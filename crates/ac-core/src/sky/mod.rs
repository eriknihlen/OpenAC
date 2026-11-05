//! Sky, weather and the world environment: the keyframes that light the day,
//! the region's sky description, the choice of day group, the weather and
//! the server's fog overrides.

mod desc;
mod environment;
mod keyframe;
mod weather;

pub use desc::{
    DAYS_PER_YEAR, DatSkyKeyframeData, DayGroupData, DayGroupSource, LoadedSkyDesc, REGION_DAT_ID,
    SKY_OBJECT_POST_SCENE, SKY_OBJECT_WEATHER, SkyDatLookup, SkyDescSource, SkyObjectData,
    SkyObjectReplaceData, SkyObjectReplaceSource, SkyObjectSource, SkyTimeSource, color_to_vec3,
    load_sky_desc, select_day_group_index,
};
pub use environment::{
    DayGroupChange, DayGroupDefinition, EnvironSoundCue, EnvironmentDefinitionError,
    EnvironmentEffect, WorldEnvironment, WorldEnvironmentDefinition, WorldEnvironmentSnapshot,
};
pub use keyframe::{
    FogMode, NoKeyframes, SkyKeyframe, SkyStateProvider, retail_sun_vector, shortest_angle_lerp,
    sun_direction,
};
/// The seeded .NET generator lives in the plugin compatibility kit (one copy for the
/// client and the plugins).
pub use openac_plugin_compat::DotNetRandom;
pub use weather::{
    AtmosphereSnapshot, EnvironOverride, WeatherKind, WeatherSystem, map_day_group_name_to_kind,
};
