# Lua engine benchmark (Phase 0 spike), 2026-11-24

Question: keep MoonSharp, or move to native Lua 5.4, for a tick budget of <= 1 ms per bot with as little
GC as possible? Project: `bench/Fiesta.Bot.LuaBench` (run `dotnet run -c Release -- micro|fleet|bdn`).
Raw outputs: `bench/micro.txt`, `bench/fleet.txt`, `bench/bdn.txt`.

Machine: AMD Ryzen 9 5950X, .NET 10.0.7 x64, Workstation concurrent GC, Windows 11. Numbers on the
cluster node will be slower in absolute terms (it is shared with the zone servers); the ratios are what
matter.

## Engines compared

| engine | what it is |
|---|---|
| `moonsharp-userdata` | MoonSharp 2.0.0 with a registered userdata facade called as `bot.fn(...)`: **how `BotApi` is exposed today** |
| `moonsharp-callback` | MoonSharp with `CallbackFunction` globals: the best MoonSharp can do without reflection dispatch |
| `keralua` | native Lua 5.4.9 via KeraLua 1.4.11 used idiomatically: `LuaFunction` delegates, KeraLua's `PushString`/`ToString`/`SetField(string)` |
| `raw-fnptr` | the same `lua54` library with `[UnmanagedCallersOnly]` callbacks registered as function pointers, `LibraryImport` + `SuppressGCTransition` for the stack accessors, pre-encoded UTF-8 names. This is what the planned source generator would emit. |

NLua was not benchmarked: it is KeraLua plus a reflection-based object bridge (the same dispatch style as
MoonSharp userdata), which is exactly the layer we want to avoid.

## Workload

All engines run the same Lua (`Scenario.cs`) against the same C# world (50 mobs):
scalar calls, calls with an argument, an iterator-style mob walk (`for h in mob_next, nil, -1`), building
a 50-mob table of tables (today's `nearbyMobs` shape), string passing (`map()` + `log(...)`), pure-Lua
table access, and a representative **decision tick** (`decide`: ~110 crossings, iterator over 50 mobs,
8 `can_use` checks, a user helper, blackboard writes, one log line per 20 ticks) plus the same decision
written against a mob table (`decide_table`). Hot reload compiles and runs a 300-function, ~1,500-line
module.

## Results

### Per operation (BenchmarkDotNet, in-process, MemoryDiagnoser)

| engine | scalar crossing | decision tick (iterator) | decision tick (table per tick) |
|---|---|---|---|
| moonsharp-userdata (today) | 298.8 ns, 785 B | 43.5 us, 90,738 B | 46.9 us, 128,139 B |
| moonsharp-callback | 195.2 ns, 489 B | 27.3 us, 66,084 B | 46.7 us, 127,149 B |
| keralua | **30.2 ns, 0 B** | **5.3 us, 6 B** | 27.7 us, 6 B |
| raw-fnptr | **25.6 ns, 0 B** | **4.2 us, 0 B** | 16.9 us, 0 B |

### Per scenario (Stopwatch + `GC.GetAllocatedBytesForCurrentThread`, 300 reps after warm-up)

| scenario (unit) | moonsharp-userdata | moonsharp-callback | keralua | raw-fnptr |
|---|---|---|---|---|
| calls with an argument (crossing) | 590 ns, 929 B | 289 ns, 601 B | 53 ns, 0 B | 34 ns, 0 B |
| iterator mob walk (crossing) | 391 ns, 620 B | 193 ns, 435 B | 46 ns, 0 B | 27 ns, 0 B |
| build a 50-mob table (call) | 72.3 us, 109 KB | 33.8 us, 109 KB | 29.7 us, 0 B managed | 17.0 us, 0 B managed |
| `map()` + `log(str)` (pair) | 1,041 ns, 1,939 B | 556 ns, 1,339 B | 421 ns, 144 B | 197 ns, 0 B |
| pure-Lua table read/write (iteration) | 739 ns, 961 B | 584 ns, 961 B | 61 ns, 0 B | 65 ns, 0 B |
| hot reload, 1,500-line module | 10.7 ms, 5,021 KB | 8.2 ms, 5,021 KB | 1.24 ms, 34 KB | 1.10 ms, 34 KB |

(The first MoonSharp scalar row in `micro.txt`, 1,108 ns, includes residual warm-up; BenchmarkDotNet's
299 ns is the reliable figure.)

### Fleet: 100 bots x 2,000 ticks on one thread

| engine | fn | tick p50 | p99 | max | KB/tick | gen0 | gen1 | GC pause total | Lua heap (100 VMs) |
|---|---|---|---|---|---|---|---|---|---|
| moonsharp-userdata | decide | 44.0 us | 98.5 us | 1,504 us | 94.7 | 1,158 | 0 | 313 ms | managed |
| moonsharp-userdata | decide_table | 43.8 us | 135.1 us | 1,850 us | 127.9 | 1,565 | 1 | 512 ms | managed |
| moonsharp-callback | decide | 27.9 us | 61.9 us | 1,659 us | 68.7 | 841 | 0 | 219 ms | managed |
| keralua | decide | 6.0 us | 19.0 us | 242 us | 0.01 | 0 | 0 | 0 ms | 3.5 MB |
| keralua | decide_table | 26.3 us | 90.0 us | 362 us | 0.01 | 0 | 0 | 0 ms | 5.1 MB |
| raw-fnptr | decide | **4.7 us** | **8.9 us** | 104 us | **0.00** | **0** | **0** | **0 ms** | 3.5 MB |
| raw-fnptr | decide_table | 16.1 us | 71.0 us | 385 us | 0.00 | 0 | 0 | 0 ms | 5.1 MB |

## What this says

1. **The boundary cost is 10x to 12x lower with native Lua, and allocation goes from ~0.5 to 0.9 KB per
   crossing to zero.** Today's shape (userdata, ~1,200 crossings a tick before the memo shim) would cost
   ~360 us and ~940 KB per tick in MoonSharp; with the raw binding the same crossings cost ~31 us and
   nothing.
2. **MoonSharp allocates even in pure Lua** (961 B per table read/write iteration): its values are
   managed objects. Native Lua keeps its own heap per VM (3.5 MB for 100 bots here), collected
   incrementally inside each VM, never stopping other bots.
3. **The fleet's GC disappears.** 100 MoonSharp bots produced 1,158 to 1,565 gen0 collections and
   313 to 512 ms of process-wide pause in 200,000 ticks; both native variants produced none. The worst
   tick dropped from 1.5 to 1.9 ms to 0.1 to 0.4 ms.
4. **Building tables is expensive in any engine** (17 to 72 us per 50-mob table). The API rule "no
   function returns a fresh table; use iterators" is worth 4x on its own even in native Lua.
5. **Hot reload is cheap**: ~1.1 ms to compile a 1,500-line module natively (vs 8 to 11 ms and 5 MB of
   garbage in MoonSharp), so reloading at a tick boundary fits the budget.
6. **KeraLua's convenience helpers allocate** (`PushString`/`ToString` 144 B per string pair, delegate
   reverse-P/Invoke ~5 ns slower than function pointers). Using KeraLua for VM management and our own
   generated function-pointer bindings for the hot path gets both.

## Recommendation

**Move to native Lua 5.4.9.** Use the **KeraLua** NuGet package for the native libraries
(`runtimes/win-x64/native/lua54.dll`, `runtimes/linux-x64/native/liblua54.so`) and for state creation,
loading and debugging conveniences; emit the `[LuaFn]` bindings as **`[UnmanagedCallersOnly]` function
pointers over `LibraryImport` declarations** (the `raw-fnptr` variant) from the source generator. Keep
MoonSharp behind `ILuaEngine` only until the compatibility shim is no longer needed (REDESIGN Phase 6).

Why not KeraLua's API alone: its string helpers and delegate callbacks are fine off the hot path, but the
generated bindings are where every tick's crossings happen, and there the raw form is 20 to 40 % faster
and allocation-free. Why not NLua: reflection bridging is the cost we are removing.

## Risks and packaging

- **Native dependency.** The bot host image is `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian, glibc);
  `liblua54.so` needs glibc >= 2.14 and `libm`/`libdl` only, so it loads as is. A framework-dependent
  publish (`/p:UseAppHost=false`, as the Dockerfile does) places it under
  `runtimes/linux-x64/native/` and the runtime resolves it through `deps.json`; a RID-specific publish
  (`-r linux-x64`) copies it next to the app. **Still to verify in the container** (remaining Phase 0
  item: run `bench micro` inside the image). An Alpine/musl image would need a musl build of Lua.
- **Never `lua_error` (longjmp) out of a managed callback.** A C `longjmp` across managed frames is
  undefined behaviour in .NET. Consequences for the design (folded into `docs/API_SPEC.md` 1.5):
  argument errors in a binding set a per-VM pending-error flag and return `nil`; the runner reports
  `E_ARG` and runs the state's `onError` after the tick. The instruction-budget hook must not raise either:
  state ticks run inside a coroutine, and the count hook **yields** (Lua 5.4 allows `lua_yield` from a
  count hook without a longjmp); the runner then closes the coroutine and records `E_BUDGET`.
- **Dialect.** Lua 5.4 has an integer subtype: `string.format("%d", 1.5)` raises where MoonSharp
  truncated; `math.floor` returns integers; `bit32` and global `unpack` do not exist. `level_quest.lua`
  uses none of `bit32`, `json`, global `unpack`, `loadstring`, `setfenv` (checked), so the main risk is
  `%d` with non-integers. The compatibility phase runs the leveller under the replay harness to catch them.
- **Sandbox.** Open only `base` (minus `load`/`dofile`/`loadfile`), `string`, `table`, `math`,
  `coroutine`, `utf8`; no `io`, `os` except a `time` shim; a custom allocator caps each VM (e.g. 16 MB).
- **Debugging.** Native Lua errors carry `chunk:line`; the brain loader names chunks by module path and
  version so the UI can jump to the line.
