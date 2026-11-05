//! The LLVM x86-64 `-O0` miscompile behind the wasmi crash, without wasmi.
//!
//! A pointer travels as the bits of an `f64` (wasmi 2.0 does this on x86-64
//! to keep its instance pointer in a float register) and is read back
//! through. At `-C opt-level=0` LLVM's fast instruction selector emits the
//! load with the XMM register that holds the `f64` as its address base:
//! `movl (%xmm0), %eax` in `--emit asm`; the machine verifier
//! (`-C llvm-args=-verify-machineinstrs`) reports "Illegal virtual register
//! for instruction" on `MOV32rm %7:fr64, ...`. The object encoder writes the
//! XMM register as the general register with the same number (`[rax]`), so
//! the read goes to whatever `rax` holds.
//!
//! Build and run (debug assertions off, so the read is a plain `load` with
//! no alignment check in between):
//!   rustc -C opt-level=0 -C debug-assertions=off repro_llvm.rs && ./repro_llvm
//! Expected: `ok: 0x12345678`. Seen (rustc 1.95.0, LLVM 22.1.2,
//! x86_64-pc-windows-msvc, 2026-10-13): exit 0xC0000005, access violation.
//! Correct with `-C opt-level=1` and with `-C llvm-args=-fast-isel=false`.
//! `--target x86_64-unknown-linux-gnu --emit asm` shows the same instruction.
//!
//! The openAC-rust test `crates/ac-plugin/tests/opt_level_guard.rs` compiles
//! this file to tell whether the toolchain still has the bug.

use std::hint::black_box;

#[derive(Clone, Copy)]
struct Inst {
    bits: f64,
}

#[inline(never)]
fn read_through(inst: &Option<Inst>, other: usize) -> u32 {
    let Some(inst) = inst else { return 0 };
    // `transmute` lowers to a plain `bitcast double to i64`, `as` to `inttoptr`.
    #[allow(clippy::transmute_float_to_int, unnecessary_transmutes)]
    let addr: usize = unsafe { std::mem::transmute::<f64, usize>(inst.bits) };
    let ptr = addr as *const u32;
    // A call in between: the fast register allocator spills the f64 and
    // reloads it into an XMM register for the load.
    black_box(other);
    unsafe { *ptr }
}

fn main() {
    let value: u32 = 0x1234_5678;
    let addr = &raw const value as usize;
    #[allow(clippy::transmute_int_to_float, unnecessary_transmutes)]
    let inst = Some(Inst {
        bits: unsafe { std::mem::transmute::<usize, f64>(addr) },
    });
    let got = read_through(black_box(&inst), 6);
    assert_eq!(got, value);
    println!("ok: {got:#x}");
}
