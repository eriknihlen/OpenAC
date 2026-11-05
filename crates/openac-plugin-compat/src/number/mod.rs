//! Numbers as .NET 10 prints, parses and rounds them in the invariant culture.
//!
//! - [`format_f64`], [`format_f32`], [`format_i32`], ... : `ToString(format,
//!   CultureInfo.InvariantCulture)`; [`to_string_f64`] is `ToString()`.
//! - [`parse_f64`], [`parse_i32`], ... : `TryParse(text, NumberStyles,
//!   CultureInfo.InvariantCulture)`.
//! - [`round`], [`round_digits`], [`convert_to_int32`]: `Math.Round` and
//!   `Convert.ToInt32(double)`.
//! - [`framework`]: the .NET Framework number text MosswartMassacre's backend
//!   expects.
//!
//! The C# plugins also format with the current culture in places (string
//! interpolation such as `$"{x:0.0}"`); this kit always uses the invariant
//! culture.

pub(crate) mod digits;
mod format;
pub mod framework;
mod parse;
mod round;

pub use format::{
    CustomFormat, FormatError, NumberFormat, format_f32, format_f64, format_i32, format_i64,
    format_u32, format_u64, to_string_f32, to_string_f64,
};
pub use parse::{NumberStyles, parse_f32, parse_f64, parse_i32, parse_i64, parse_u32, parse_u64};
pub use round::{MidpointRounding, convert_to_int32, round, round_digits, round_mode};
