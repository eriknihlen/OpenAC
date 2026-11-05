//! World rules: game time and the calendar, landblock static placement,
//! and the floating world origin.

mod dereth_time;
mod landblock_statics;
mod world_origin;

pub use dereth_time::{
    CalendarDate, CalendarError, DAY_TICKS, DAYS_IN_A_MONTH, DEFAULT_ORIGIN_OFFSET_TICKS,
    DerethCalendar, HOUR_TICKS, HOURS_IN_A_DAY, HourName, MAX_TICKS, MONTH_TICKS, MONTHS_IN_A_YEAR,
    MonthName, YEAR_TICKS, ZERO_YEAR,
};
pub use landblock_statics::{
    STATIC_ID_MAX_COUNTER, StaticBuilding, StaticIdOverflow, StaticObject, StaticPlacement,
    allocate_static_entity_id, is_static_entity_id, place_landblock_statics, static_entity_id_base,
};
pub use world_origin::{LiveWorldOrigin, WorldFrameDisagreement};
