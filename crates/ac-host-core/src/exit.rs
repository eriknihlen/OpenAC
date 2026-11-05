//! Process exit codes. Scripts and the launcher branch on these numbers.

/// How the process ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ExitCode {
    /// Everything worked.
    Success = 0,
    /// The command line does not fit the grammar.
    Usage = 2,
    /// The document or the directories are unusable.
    Configuration = 3,
    /// A password could not be had.
    Credential = 4,
    /// A session could not connect.
    Connection = 5,
    /// Anything else went wrong while running.
    Runtime = 6,
}

impl ExitCode {
    /// The number the process exits with.
    pub const fn code(self) -> u8 {
        self as u8
    }
}

impl From<ExitCode> for std::process::ExitCode {
    fn from(code: ExitCode) -> Self {
        Self::from(code.code())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Golden values: OpenAC `src/AcDream.Headless/HeadlessExitCode.cs`.
    #[test]
    fn numbers_are_pinned() {
        assert_eq!(ExitCode::Success.code(), 0);
        assert_eq!(ExitCode::Usage.code(), 2);
        assert_eq!(ExitCode::Configuration.code(), 3);
        assert_eq!(ExitCode::Credential.code(), 4);
        assert_eq!(ExitCode::Connection.code(), 5);
        assert_eq!(ExitCode::Runtime.code(), 6);
    }
}
