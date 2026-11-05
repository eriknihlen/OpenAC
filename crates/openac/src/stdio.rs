//! Lines to standard output and standard error that survive their reader
//! going away.
//!
//! A run's output is often piped into another program (a log stamper, a
//! `tee`). A Ctrl+C in the shell ends that reader too, and the next line
//! the client writes then meets a closed pipe. `println!` panics there,
//! which used to end a session's monitor and with it the process before the
//! sessions had logged off, leaving the characters in the world on the
//! server. A closed reader is not an error for a console: the line has
//! nowhere to go and is dropped, as OpenAC's console streams drop it, and
//! the sessions log off as asked. Any other write failure still panics, as
//! `println!` does.

use std::fmt;
use std::io::{self, Write};

/// `println!` that drops the line when standard output's reader is gone.
macro_rules! outln {
    () => {
        $crate::stdio::out_line(format_args!(""))
    };
    ($($arg:tt)*) => {
        $crate::stdio::out_line(format_args!($($arg)*))
    };
}

/// `eprintln!` that drops the line when standard error's reader is gone.
macro_rules! errln {
    () => {
        $crate::stdio::err_line(format_args!(""))
    };
    ($($arg:tt)*) => {
        $crate::stdio::err_line(format_args!($($arg)*))
    };
}

pub(crate) use {errln, outln};

/// One line to standard output.
pub(crate) fn out_line(args: fmt::Arguments<'_>) {
    write_line(&mut io::stdout().lock(), args, "stdout");
}

/// One line to standard error.
pub(crate) fn err_line(args: fmt::Arguments<'_>) {
    write_line(&mut io::stderr().lock(), args, "stderr");
}

fn write_line(out: &mut impl Write, args: fmt::Arguments<'_>, name: &str) {
    match line_to(out, args) {
        Ok(()) => {}
        // The reader has gone away: nobody is left to read the line.
        Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
        Err(error) => panic!("failed printing to {name}: {error}"),
    }
}

/// Writes `args` and a newline as one piece.
fn line_to(out: &mut impl Write, args: fmt::Arguments<'_>) -> io::Result<()> {
    let mut line = args.to_string();
    line.push('\n');
    out.write_all(line.as_bytes())?;
    out.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A writer whose reader has gone, or that fails otherwise.
    struct Failing(io::ErrorKind);

    impl Write for Failing {
        fn write(&mut self, _: &[u8]) -> io::Result<usize> {
            Err(io::Error::from(self.0))
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn a_line_to_a_closed_reader_is_dropped() {
        // OpenAC's console streams ignore a broken or closing pipe (.NET's
        // console stream returns success for ERROR_BROKEN_PIPE and
        // ERROR_NO_DATA, and for EPIPE on Unix); Rust's ErrorKind maps
        // all three to BrokenPipe.
        write_line(
            &mut Failing(io::ErrorKind::BrokenPipe),
            format_args!("lost"),
            "stdout",
        );
    }

    #[test]
    #[should_panic(expected = "failed printing to stdout")]
    fn any_other_failure_still_panics() {
        write_line(
            &mut Failing(io::ErrorKind::PermissionDenied),
            format_args!("x"),
            "stdout",
        );
    }

    #[test]
    fn a_line_ends_with_a_newline() {
        let mut out = Vec::new();
        write_line(&mut out, format_args!("[{}] hello", "a"), "stdout");
        assert_eq!(out, b"[a] hello\n");
    }

    #[test]
    fn windows_closed_pipe_errors_are_broken_pipe() {
        // ERROR_BROKEN_PIPE (109) and ERROR_NO_DATA (232, "the pipe is
        // being closed", the error the reported run hit).
        #[cfg(windows)]
        for code in [109, 232] {
            assert_eq!(
                io::Error::from_raw_os_error(code).kind(),
                io::ErrorKind::BrokenPipe
            );
        }
    }
}
