//! A development aid: the remote-window stream and commands as JSON, over
//! standard input and output (see `ac_remote_wire::devtool`).
//!
//! Input is a sequence of records, `[kind: u8][length: u32 LE][payload]`:
//! kind 1 is one stream frame as the relay delivered it, kind 2 one command
//! request as UTF-8 JSON. Every record is answered with exactly one line of
//! JSON on standard output, in order. The program ends when its input does.

use std::io::{self, BufWriter, Read, Write};
use std::time::Instant;

use ac_remote_wire::devtool::{command_from_json, command_reply, error_reply, summarize};
use ac_remote_wire::{DecodeContext, EncodeContext, decode_batch, encode_command};

/// One stream frame from the relay.
const FRAME: u8 = 1;
/// One command request.
const COMMAND: u8 = 2;
/// The largest record taken: above the largest frame a relay delivers.
const MAX_RECORD: usize = 8 * 1024 * 1024;

fn answer(kind: u8, payload: &[u8], encode: &EncodeContext, decode: &DecodeContext) -> String {
    match kind {
        FRAME => match decode_batch(payload, decode) {
            Ok(events) => summarize(&events),
            Err(error) => error_reply(None, &error.to_string()),
        },
        COMMAND => {
            let Ok(text) = std::str::from_utf8(payload) else {
                return error_reply(None, "the command is not UTF-8");
            };
            match command_from_json(text) {
                Ok((id, command)) => match encode_command(&command, encode) {
                    Ok(bytes) => command_reply(id, &bytes),
                    Err(error) => error_reply(Some(id), &error.to_string()),
                },
                Err((id, why)) => error_reply(id, &why),
            }
        }
        other => error_reply(None, &format!("unknown record kind {other}")),
    }
}

fn main() -> io::Result<()> {
    let epoch = Instant::now();
    let encode = EncodeContext::new(epoch);
    // No spell table and no skill formulas: the summary reads neither.
    let decode = DecodeContext::new(epoch, None, None);
    let mut input = io::stdin().lock();
    let mut output = BufWriter::new(io::stdout().lock());
    loop {
        let mut header = [0u8; 5];
        match input.read_exact(&mut header) {
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
            Err(error) => return Err(error),
        }
        let length = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
        if length > MAX_RECORD {
            // The stream cannot be followed past a record this long.
            let line = error_reply(None, &format!("a record of {length} bytes is too long"));
            writeln!(output, "{line}")?;
            output.flush()?;
            return Ok(());
        }
        let mut payload = vec![0u8; length];
        input.read_exact(&mut payload)?;
        let line = answer(header[0], &payload, &encode, &decode);
        writeln!(output, "{line}")?;
        output.flush()?;
    }
}
