//! The .NET behaviour that converted plugins reproduce exactly.
//!
//! The C# plugins (MossTank, MosswartMassacre) print numbers, seed random
//! generators, decode text, write JSON and match regular expressions the way
//! .NET does, and their files, chat lines and server frames depend on the
//! exact bytes. This crate is the one Rust copy of those rules, usable from
//! a plugin built for `wasm32-unknown-unknown` and natively in tests:
//!
//! - [`number`]: `ToString(format)`, `TryParse`, `Math.Round` in the
//!   invariant culture, and the .NET Framework number text of
//!   MosswartMassacre's backend.
//! - [`random`]: `System.Random(seed)`, value for value.
//! - [`datetime`]: `DateTime`/`TimeSpan` text (`"o"`, `yyyy-MM-dd`, ...).
//! - [`text`]: `Encoding.UTF8.GetString` and `File.ReadAllText` decoding;
//!   UTF-16 lengths and indexes; `Ordinal`/`OrdinalIgnoreCase` comparison
//!   and search; `ToUpperInvariant`/`ToLowerInvariant`; the
//!   `InvariantCulture` string order.
//! - [`json`]: Newtonsoft.Json and System.Text.Json output, byte for byte, and
//!   a strict reader.
//! - [`regex`]: .NET regular expressions on `fancy-regex`, with a step limit
//!   in place of the match timeout.
//!
//! Every function names the .NET member it reproduces; the tests compare
//! against golden values a C# program printed on .NET 10.
//! MIT licensed, like the plugin SDK that re-exports it.

pub mod datetime;
pub mod json;
pub mod number;
pub mod random;
pub mod regex;
pub mod text;

pub use random::DotNetRandom;
