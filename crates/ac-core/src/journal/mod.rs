//! The per-character journal: pages the player writes (label, title,
//! notes, a countdown timer, a recorded map location) and the tagged text
//! file they are saved in. Entirely client-side: no wire, no server, no
//! dat. Reading and writing the file itself is the runtime's job.

mod file;
mod page;

pub use file::{JournalReadResult, MALFORMED_FILE_MESSAGE, file_name_for, read, write};
pub use page::{JournalPage, MAX_LABEL_LENGTH, MAX_NOTES_LENGTH, MAX_TITLE_LENGTH};
