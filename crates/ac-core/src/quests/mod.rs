//! Contracts (the quest tracker): the authored contract catalog and the
//! tracker's progress column.

mod contract;
mod progress_text;

pub use contract::{ContractCatalog, ContractEntry};
pub use progress_text::build_progress_text;
