//! One running plugin: its wasm instance, its memory and the calls into it.
//!
//! A plugin's call into the client is direct: the import stub records the
//! request and waits, the caller of the plugin, which owns the session
//! state, carries the request out and lets the plugin go on with the
//! reply's length. So an import reads and changes runtime state through
//! ordinary borrows, without the plugin's store having to hold any of it.
//! The wait is wasmtime's async support: a call into the plugin is a
//! future the caller polls itself; while an import waits for its reply the
//! future is pending (the plugin is parked on its own stack), and the next
//! poll after the reply is handed over resumes it. Nothing else ever makes
//! the future pending, and no executor is involved.
//!
//! A call may take as long as it needs ([`crate::watchdog`]): one over the slow-call time goes on and is
//! reported ([`SlowCall`]); one still running at its deadline is taken for
//! stuck and trapped. That, a panic, a bad memory access or the memory cap
//! is a fault: the caller unloads the plugin.

use std::fmt;
use std::future::Future;
use std::pin::{Pin, pin};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::task::{Context, Poll, Waker};
use std::time::{Duration, Instant};

use ac_plugin_abi::{Import, MODULE, exports};
use wasmtime::{
    AsContextMut, Caller, Extern, Linker, Memory, ResourceLimiter, Store, Trap, TypedFunc,
    WasmParams, WasmResults,
};

use crate::engine::{PluginEngine, PluginModule};
use crate::watchdog::{Entry, ImportTally, TimeLimits, duration_words, epochs};

/// The largest request a plugin may pass to one import, bytes.
const MAX_REQUEST: usize = 16 << 20;

/// What a plugin call needs from its caller: carry out one import.
pub(crate) type ImportHandler<'a> = dyn FnMut(Import, &[u8]) -> Result<Handled, String> + 'a;

/// One import carried out.
#[derive(Debug, Default)]
pub(crate) struct Handled {
    /// The encoded reply.
    pub(crate) reply: Vec<u8>,
    /// How long another plugin spent answering a question the import asked
    /// it (a loot classifier, a command's completions): that plugin's time,
    /// counted against its own call, so it is left out of this call's
    /// deadline and slow-call time.
    pub(crate) lent: Duration,
}

impl From<Vec<u8>> for Handled {
    fn from(reply: Vec<u8>) -> Self {
        Self {
            reply,
            lent: Duration::ZERO,
        }
    }
}

/// The request a waiting import made and the reply it waits for, shared by
/// the import stub (inside the plugin's call) and the caller driving it.
#[derive(Debug, Default)]
struct Exchange {
    pending: Option<(Import, Vec<u8>)>,
    reply: Option<Vec<u8>>,
    /// The call's deadline again, in epochs from now, when the import lent
    /// time to another plugin: the stub sets it before the plugin goes on.
    deadline: Option<u64>,
}

fn lock(exchange: &Mutex<Exchange>) -> MutexGuard<'_, Exchange> {
    // Nothing panics while holding the lock; a poisoned one is still whole.
    exchange.lock().unwrap_or_else(PoisonError::into_inner)
}

/// The import stub's wait: ready once the caller has handed the reply over.
struct Reply(Arc<Mutex<Exchange>>);

impl Future for Reply {
    type Output = Vec<u8>;

    fn poll(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<Vec<u8>> {
        lock(&self.0)
            .reply
            .take()
            .map_or(Poll::Pending, Poll::Ready)
    }
}

/// The memory cap: a grow past it is refused with [`OverCap`] (rather than
/// `memory.grow` failing quietly): the plugin's allocator would abort
/// anyway, and this way the report says why.
#[derive(Debug)]
struct MemoryCap {
    bytes: usize,
}

/// A grow past the plugin's memory cap.
#[derive(Debug)]
struct OverCap;

impl fmt::Display for OverCap {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("the plugin's memory cap")
    }
}

impl std::error::Error for OverCap {}

impl ResourceLimiter for MemoryCap {
    fn memory_growing(
        &mut self,
        _current: usize,
        desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        if desired > self.bytes {
            return Err(wasmtime::Error::new(OverCap));
        }
        Ok(true)
    }

    fn table_growing(
        &mut self,
        _current: usize,
        _desired: usize,
        _maximum: Option<usize>,
    ) -> wasmtime::Result<bool> {
        Ok(true)
    }
}

/// The plugin's store: what the import stubs need during a call.
#[derive(Debug)]
pub(crate) struct StoreData {
    cap: MemoryCap,
    exchange: Arc<Mutex<Exchange>>,
    reply: Vec<u8>,
    panic: Option<String>,
}

/// A running plugin.
pub(crate) struct Instance {
    store: Store<StoreData>,
    exchange: Arc<Mutex<Exchange>>,
    memory: Memory,
    alloc: TypedFunc<i32, i32>,
    enable: TypedFunc<(i32, i32), i64>,
    disable: TypedFunc<(), ()>,
    tick: TypedFunc<(), ()>,
    event: TypedFunc<(i32, i32), ()>,
    callback: Option<TypedFunc<(i32, i32), i64>>,
    memory_mib: u32,
    /// How long a call may take.
    limits: TimeLimits,
    /// The wall time of the last finished call, less what other plugins
    /// took to answer it.
    last_time: Duration,
    /// The imports the call running (or the last one) made.
    tally: ImportTally,
    /// Calls over this long are noted ([`Self::take_slow_notes`]).
    slow_log: Option<Duration>,
    /// The calls over [`Self::slow_log`] since the last take.
    slow_notes: Vec<String>,
    /// The calls over the slow-call time since the last take.
    slow_calls: Vec<SlowCall>,
}

/// A call that took longer than the slow-call time and finished.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct SlowCall {
    /// The entry call (`tick`, `event chat line`, ...).
    pub entry: String,
    /// Its wall time, the client's work on its imports included (what
    /// other plugins took to answer it left out).
    pub time: Duration,
    /// Where it would have been stopped.
    pub deadline: Duration,
    /// The client functions it called.
    pub imports: String,
}

impl fmt::Debug for Instance {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Instance")
            .field("memory_mib", &self.memory_mib)
            .field("last_time", &self.last_time)
            .field("slow_log", &self.slow_log)
            .finish_non_exhaustive()
    }
}

fn memory_of(caller: &mut Caller<'_, StoreData>) -> wasmtime::Result<Memory> {
    caller
        .get_export(exports::MEMORY)
        .and_then(Extern::into_memory)
        .ok_or_else(|| wasmtime::format_err!("the plugin has no memory"))
}

fn read_request(
    caller: &mut Caller<'_, StoreData>,
    ptr: i32,
    len: i32,
) -> wasmtime::Result<Vec<u8>> {
    let (Ok(offset), Ok(len)) = (usize::try_from(ptr), usize::try_from(len)) else {
        wasmtime::bail!("a request with a negative pointer or length");
    };
    if len > MAX_REQUEST {
        wasmtime::bail!("a request larger than 16 MiB");
    }
    let memory = memory_of(caller)?;
    let mut bytes = vec![0u8; len];
    memory
        .read(&*caller, offset, &mut bytes)
        .map_err(|_| wasmtime::format_err!("a request outside the plugin's memory"))?;
    Ok(bytes)
}

fn link(linker: &mut Linker<StoreData>, import: Import) -> wasmtime::Result<()> {
    let name = import.name();
    match import {
        Import::AbiReplyRead => {
            linker.func_wrap(
                MODULE,
                name,
                |mut caller: Caller<'_, StoreData>, ptr: i32, len: i32| -> wasmtime::Result<i32> {
                    let reply = std::mem::take(&mut caller.data_mut().reply);
                    let (Ok(offset), Ok(len)) = (usize::try_from(ptr), usize::try_from(len)) else {
                        wasmtime::bail!("a reply buffer with a negative length");
                    };
                    let take = len.min(reply.len());
                    let memory = memory_of(&mut caller)?;
                    memory
                        .write(&mut caller, offset, &reply[..take])
                        .map_err(|_| {
                            wasmtime::format_err!("a reply buffer outside the plugin's memory")
                        })?;
                    Ok(0)
                },
            )?;
        }
        Import::AbiPanic => {
            linker.func_wrap(
                MODULE,
                name,
                |mut caller: Caller<'_, StoreData>, ptr: i32, len: i32| -> wasmtime::Result<i32> {
                    let bytes = read_request(&mut caller, ptr, len)?;
                    caller.data_mut().panic = Some(String::from_utf8_lossy(&bytes).into_owned());
                    Ok(0)
                },
            )?;
        }
        _ => {
            linker.func_wrap_async(
                MODULE,
                name,
                move |mut caller: Caller<'_, StoreData>,
                      (ptr, len): (i32, i32)|
                      -> Box<dyn Future<Output = wasmtime::Result<i32>> + Send + '_> {
                    Box::new(async move {
                        let request = read_request(&mut caller, ptr, len)?;
                        let exchange = Arc::clone(&caller.data().exchange);
                        lock(&exchange).pending = Some((import, request));
                        let reply = Reply(Arc::clone(&exchange)).await;
                        if let Some(deadline) = lock(&exchange).deadline.take() {
                            caller.as_context_mut().set_epoch_deadline(deadline);
                        }
                        let len = i32::try_from(reply.len())
                            .map_err(|_| wasmtime::format_err!("a reply larger than 2 GiB"))?;
                        caller.data_mut().reply = reply;
                        Ok(len)
                    })
                },
            )?;
        }
    }
    Ok(())
}

/// One call's clock: when it started, its deadline, and the time it lent
/// other plugins ([`Handled::lent`]), which is not its own.
#[derive(Debug, Clone, Copy)]
struct CallClock {
    started: Instant,
    deadline: Duration,
    lent: Duration,
}

impl CallClock {
    fn start(deadline: Duration) -> Self {
        Self {
            started: Instant::now(),
            deadline,
            lent: Duration::ZERO,
        }
    }

    /// The call's own time so far: its wall time less what it lent.
    fn own(&self) -> Duration {
        self.started.elapsed().saturating_sub(self.lent)
    }
}

/// Drives one call into the plugin to its end: each time it waits on an
/// import, `handler` carries the import out and the call goes on with the
/// reply. Time the import lent another plugin moves the call's deadline
/// on by as much ([`CallClock`]). `Err` is the handler's own failure (the
/// call is abandoned); `Ok` holds what the engine returned.
fn drive<R>(
    call: impl Future<Output = wasmtime::Result<R>>,
    exchange: &Mutex<Exchange>,
    tally: &mut ImportTally,
    clock: &mut CallClock,
    handler: &mut ImportHandler<'_>,
) -> Result<wasmtime::Result<R>, String> {
    let mut call = pin!(call);
    let mut context = Context::from_waker(Waker::noop());
    loop {
        if let Poll::Ready(result) = call.as_mut().poll(&mut context) {
            return Ok(result);
        }
        let Some((import, request)) = lock(exchange).pending.take() else {
            return Err("a plugin call stopped without a request".to_owned());
        };
        let started = Instant::now();
        let handled = handler(import, &request)?;
        tally.record(import, started.elapsed().saturating_sub(handled.lent));
        let mut exchange = lock(exchange);
        exchange.reply = Some(handled.reply);
        if !handled.lent.is_zero() {
            clock.lent += handled.lent;
            exchange.deadline = Some(epochs(clock.deadline.saturating_sub(clock.own())));
        }
    }
}

/// The handler for instantiation, which runs before the plugin may call the
/// client (`openac_enable` is its first chance).
fn no_imports_yet(_: Import, _: &[u8]) -> Result<Handled, String> {
    Err("the plugin called the client while it was being instantiated".to_owned())
}

impl Instance {
    /// Instantiates `module` with exactly `imports` linked and its memory
    /// capped at `memory_mib`; every call longer than `slow_log` is noted.
    pub(crate) fn new(
        plugin_engine: &PluginEngine,
        module: &PluginModule,
        imports: &[Import],
        memory_mib: u32,
        slow_log: Option<Duration>,
    ) -> Result<Self, String> {
        plugin_engine.start_watchdog()?;
        let engine = plugin_engine.engine();
        let limits = plugin_engine.time_limits();
        let mut linker = Linker::<StoreData>::new(engine);
        for import in imports {
            link(&mut linker, *import).map_err(|error| error.to_string())?;
        }
        let exchange = Arc::new(Mutex::new(Exchange::default()));
        let bytes = usize::try_from(memory_mib).unwrap_or(usize::MAX) << 20;
        let mut store = Store::new(
            engine,
            StoreData {
                cap: MemoryCap { bytes },
                exchange: Arc::clone(&exchange),
                reply: Vec::new(),
                panic: None,
            },
        );
        store.limiter(|data| &mut data.cap);
        // A call that reaches its deadline traps (wasmtime's default, set
        // here so it does not depend on that); the deadline of each call is
        // set as it starts, this one for instantiation.
        store.epoch_deadline_trap();
        let start_deadline = limits.deadline(Entry::Start);
        store.set_epoch_deadline(epochs(start_deadline));
        let mut tally = ImportTally::default();
        let started = drive(
            linker.instantiate_async(&mut store, module.module()),
            &exchange,
            &mut tally,
            &mut CallClock::start(start_deadline),
            &mut no_imports_yet,
        )?;
        let instance = started.map_err(|error| {
            let panic = store.data_mut().panic.take();
            describe(
                &error,
                panic,
                memory_mib,
                start_deadline,
                Entry::Start,
                &tally,
            )
        })?;
        let memory = instance
            .get_memory(&mut store, exports::MEMORY)
            .ok_or_else(|| format!("the .wasm does not export its {}", exports::MEMORY))?;
        let func = |name: &str| format!("the .wasm does not export {name} as checked");
        Ok(Self {
            alloc: instance
                .get_typed_func(&mut store, exports::ALLOC)
                .map_err(|_| func(exports::ALLOC))?,
            enable: instance
                .get_typed_func(&mut store, exports::ENABLE)
                .map_err(|_| func(exports::ENABLE))?,
            disable: instance
                .get_typed_func(&mut store, exports::DISABLE)
                .map_err(|_| func(exports::DISABLE))?,
            tick: instance
                .get_typed_func(&mut store, exports::TICK)
                .map_err(|_| func(exports::TICK))?,
            event: instance
                .get_typed_func(&mut store, exports::EVENT)
                .map_err(|_| func(exports::EVENT))?,
            callback: instance.get_typed_func(&mut store, exports::CALLBACK).ok(),
            store,
            exchange,
            memory,
            memory_mib,
            limits,
            last_time: Duration::ZERO,
            tally,
            slow_log,
            slow_notes: Vec::new(),
            slow_calls: Vec::new(),
        })
    }

    /// Starts the plugin with its encoded start record. `Ok(Err(..))` is
    /// the plugin's own refusal; `Err` is a fault.
    pub(crate) fn enable(
        &mut self,
        start: &[u8],
        handler: &mut ImportHandler<'_>,
    ) -> Result<Result<(), String>, String> {
        let (ptr, len) = self.hand_over(start, Entry::Start, handler)?;
        let enable = self.enable.clone();
        let result = self.run(enable, (ptr, len), Entry::Start, handler)?;
        if result == 0 {
            return Ok(Ok(()));
        }
        // The refusal's text: `ptr << 32 | len` in the plugin's memory.
        let message_ptr = usize::try_from(result >> 32).unwrap_or(usize::MAX);
        let message_len = usize::try_from(result & 0xFFFF_FFFF).unwrap_or(0);
        let mut message = vec![0u8; message_len.min(4096)];
        self.memory
            .read(&self.store, message_ptr, &mut message)
            .map_err(|_| "its refusal points outside its memory".to_owned())?;
        Ok(Err(String::from_utf8_lossy(&message).into_owned()))
    }

    /// The wall time of the last finished call into the plugin, the
    /// client's work on its imports included and what other plugins took
    /// to answer it left out.
    pub(crate) fn last_time(&self) -> Duration {
        self.last_time
    }

    /// The calls over the slow log's threshold since the last take, each
    /// as a line: the entry call, its time and its imports.
    pub(crate) fn take_slow_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.slow_notes)
    }

    /// The calls over the slow-call time since the last take.
    pub(crate) fn take_slow_calls(&mut self) -> Vec<SlowCall> {
        std::mem::take(&mut self.slow_calls)
    }

    /// Stops the plugin.
    pub(crate) fn disable(&mut self, handler: &mut ImportHandler<'_>) -> Result<(), String> {
        let disable = self.disable.clone();
        self.run(disable, (), Entry::Stop, handler)
    }

    /// One tick.
    pub(crate) fn tick(&mut self, handler: &mut ImportHandler<'_>) -> Result<(), String> {
        let tick = self.tick.clone();
        self.run(tick, (), Entry::Tick, handler)
    }

    /// One encoded event; `entry` names it in reports.
    pub(crate) fn event(
        &mut self,
        event: &[u8],
        entry: Entry,
        handler: &mut ImportHandler<'_>,
    ) -> Result<(), String> {
        let (ptr, len) = self.hand_over(event, entry, handler)?;
        let func = self.event.clone();
        self.run(func, (ptr, len), entry, handler)
    }

    /// One encoded callback; the plugin's encoded answer. `Ok(None)` when
    /// the plugin exports no callback (it registered none). `entry` names
    /// it in reports.
    pub(crate) fn callback(
        &mut self,
        callback: &[u8],
        entry: Entry,
        handler: &mut ImportHandler<'_>,
    ) -> Result<Option<Vec<u8>>, String> {
        let Some(func) = self.callback.clone() else {
            return Ok(None);
        };
        let (ptr, len) = self.hand_over(callback, entry, handler)?;
        let result = self.run(func, (ptr, len), entry, handler)?;
        // The answer: `ptr << 32 | len` in the plugin's memory.
        let answer_ptr = usize::try_from(result >> 32).unwrap_or(usize::MAX);
        let answer_len = usize::try_from(result & 0xFFFF_FFFF).unwrap_or(0);
        if answer_len > MAX_REQUEST {
            return Err("its callback answer is larger than 16 MiB".to_owned());
        }
        let mut answer = vec![0u8; answer_len];
        self.memory
            .read(&self.store, answer_ptr, &mut answer)
            .map_err(|_| "its callback answer points outside its memory".to_owned())?;
        Ok(Some(answer))
    }

    /// Copies `bytes` into a buffer the plugin allocates; the next call
    /// (`entry`) takes ownership of it.
    fn hand_over(
        &mut self,
        bytes: &[u8],
        entry: Entry,
        handler: &mut ImportHandler<'_>,
    ) -> Result<(i32, i32), String> {
        let len =
            i32::try_from(bytes.len()).map_err(|_| "a record larger than 2 GiB".to_owned())?;
        let alloc = self.alloc.clone();
        let ptr = self.run(alloc, len, entry, handler)?;
        let offset =
            usize::try_from(ptr).map_err(|_| "openac_alloc returned a negative pointer")?;
        self.memory
            .write(&mut self.store, offset, bytes)
            .map_err(|_| "openac_alloc returned a buffer outside its memory".to_owned())?;
        Ok((ptr, len))
    }

    /// Runs one export (for entry call `entry`) to its end, carrying out
    /// each import it makes. Its time is its own: what another plugin took
    /// to answer it is that plugin's ([`Handled::lent`]).
    fn run<P: WasmParams + Sync, R: WasmResults + Sync>(
        &mut self,
        func: TypedFunc<P, R>,
        params: P,
        entry: Entry,
        handler: &mut ImportHandler<'_>,
    ) -> Result<R, String> {
        let deadline = self.limits.deadline(entry);
        self.store.set_epoch_deadline(epochs(deadline));
        self.store.data_mut().panic = None;
        *lock(&self.exchange) = Exchange::default();
        self.tally.clear();
        let mut clock = CallClock::start(deadline);
        let result = drive(
            func.call_async(&mut self.store, params),
            &self.exchange,
            &mut self.tally,
            &mut clock,
            handler,
        )?;
        let time = clock.own();
        match result {
            Ok(result) => {
                self.last_time = time;
                if time > self.limits.slow_call {
                    self.slow_calls.push(SlowCall {
                        entry: entry.to_string(),
                        time,
                        deadline,
                        imports: self.tally.to_string(),
                    });
                }
                if self.slow_log.is_some_and(|threshold| time > threshold) {
                    self.slow_notes.push(format!(
                        "{entry} took {:.1} ms ({}); imports: {}",
                        time.as_secs_f64() * 1000.0,
                        self.tally.client_share(),
                        self.tally
                    ));
                }
                Ok(result)
            }
            Err(error) => {
                let panic = self.store.data_mut().panic.take();
                Err(describe(
                    &error,
                    panic,
                    self.memory_mib,
                    deadline,
                    entry,
                    &self.tally,
                ))
            }
        }
    }
}

/// A call stopped at its deadline in words: the deadline, the entry call
/// and the last imports it made, so the report says where to look.
fn stuck(deadline: Duration, entry: Entry, imports: &ImportTally) -> String {
    format!(
        "ran for more than {} in one call (it looks stuck) in {entry}; last imports: {imports}",
        duration_words(deadline)
    )
}

/// A fault in words: the panic message when the plugin left one, else what
/// the engine stopped it for.
fn describe(
    error: &wasmtime::Error,
    panic: Option<String>,
    memory_mib: u32,
    deadline: Duration,
    entry: Entry,
    imports: &ImportTally,
) -> String {
    if let Some(message) = panic {
        return format!("panicked: {message}");
    }
    if error.downcast_ref::<OverCap>().is_some() {
        return format!("ran out of memory (its limit is {memory_mib} MiB)");
    }
    match error.downcast_ref::<Trap>() {
        Some(Trap::Interrupt) => stuck(deadline, entry, imports),
        Some(trap) => format!("trapped: {trap}"),
        None => error.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::watchdog::{CALL_DEADLINE, START_DEADLINE};

    /// The fault of a call stopped at the real deadlines, in the client's
    /// wording.
    #[test]
    fn a_stuck_call_reads_in_time_units() {
        let mut tally = ImportTally::default();
        tally.record(Import::HostClock, Duration::ZERO);
        assert_eq!(
            describe(
                &wasmtime::Error::new(Trap::Interrupt),
                None,
                16,
                CALL_DEADLINE,
                Entry::Tick,
                &tally
            ),
            "ran for more than 10 s in one call (it looks stuck) in tick; last imports: \
             host_clock \u{d7}1"
        );
        assert_eq!(
            stuck(START_DEADLINE, Entry::Start, &ImportTally::default()),
            "ran for more than 30 s in one call (it looks stuck) in start; last imports: none"
        );
    }
}
