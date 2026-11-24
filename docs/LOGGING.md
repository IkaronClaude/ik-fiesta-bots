# Logging: measurement and quick wins (2026-11-24)

The operator: "for the bots repo we need to significantly curb logging, as it stands it produces too much
garbage and a ton of overhead as well". This document measures today's logging, lists who actually uses
which lines, and specifies quick wins that can ship on the current code. The target design for the
rewrite is in `docs/REDESIGN.md` section 16 and `docs/API_SPEC.md` section 12.

Sources: the live host's ring buffers (`GET /api/bots/{id}/log?level=verbose&max=100000`, read-only, six
bots, 21:06 UTC), the log files on the bot-host pod (`/app/bot-knowledge/roster/logs`, `ls`/`du` only),
the code at `7cbf379`, and a cost benchmark (`bench/Fiesta.Bot.LuaBench`, mode `logcost`, output
`bench/logcost.txt`).

---

## 1. How logging works today

| piece | where | what it does on every line |
|---|---|---|
| `BotHandle.Log(level, message)` | `src/Fiesta.Bot/Manager/BotHandle.cs:619-633` | **no level check**: formats a timestamp, runs `ResolveMobNames` (`:567-590`, a compiled regex over every line containing "mob", replacing each `mobNNN` with "Name (Id NNN)"), appends to a `List` ring under a lock, writes to the file, invokes `LogLine` subscribers |
| ring buffer | `BotHandle.cs:22-27`, trim `:598-617` | 100,000 lines **or 6,000,000 chars** (~14 MB per bot), trimmed with `RemoveRange` (array shift) |
| file | `src/Fiesta.Bot/Manager/BotLogFile.cs:39-56` | `StreamWriter` with **`AutoFlush = true`** (one write syscall per line), under a lock, on the caller's thread |
| rotation | `BotLogFile.cs:58-72` | every 10,000 lines, **synchronously renames up to 99 files** on the caller's thread; keeps 100 files per bot |
| callers | ~360 call sites (`BotManager.cs` ~133, `ZoneView.cs` ~123, `ZoneEntry.cs` 20, `LoginChain.cs` 18, scripting ~44) | the message is an interpolated string, **built before `Log` is called** at every level |
| Lua | `BotScriptRunner.cs:401-404` (`log`/`logi`/`logv` -> `Log`), `level_quest.lua` 89 `log`, 81 `logi`, 11 `logv`, 60 `dbg`, 281 `crutch` calls | Lua builds the string with `string.format` (MoonSharp allocates) before calling, at every level |
| packet log | `src/Fiesta.Bot/Net/PacketLog.cs:49-87` | per frame: `StringBuilder`, **one `ToString("X2")` string per payload byte**, synchronous `AutoFlush` write on the read loop and the send path. Off by default; no live bot had it on |

The callers run on the Lua tick thread, the session read loop, the NavPlanner worker and HTTP threads, so
all of the above (formatting, regex, lock, file write, rotation) happens on the hot threads.

## 2. Volume (live, per bot)

The ring is capped at 6 M chars, so each bot's window differs; rates are per bot-minute of its window.

| bot | window (min) | lines/min | KB/min | Note/min | Info/min | Verbose/min | avg bytes/line |
|---|---|---|---|---|---|---|---|
| NewArcher (stuck in a loop) | 204 | 219 | 27.7 | 204 | 11 | 3 | 130 |
| NewMage (stuck in a loop) | 209 | 215 | 27.5 | 204 | 11 | 0 | 131 |
| NewFighter | 71 | 178 | 67.6 | 51 | 66 | 61 | 390 |
| NewJoker | 97 | 115 | 54.5 | 37 | 40 | 38 | 487 |
| NewCleric | 123 | 70 | 46.3 | 31 | 18 | 21 | 674 |
| ArcherZero (producer) | 1,298 | 48 | 4.4 | 28 | 17 | 3 | 93 |
| **all** | 2,001 bot-min | **92** | **16.4** | | | | |

On disk: **2.1 GB in 791 files** for the fleet; 100 files per bot cover only 2.5 to 9 days (NewJoker
668 MB, NewCleric 499 MB, NewFighter 356 MB). NewArcher rotates every ~45 minutes.

**The ring does not hold "about 5 hours"** (CLAUDE.md, BOT_RUNBOOK): the 6 M-char cap is hit first, so an
active leveller keeps **71 to 123 minutes**. The status dump (below) is what fills it.

**75 % of lines (72 % of bytes) repeat the same message template within 10 seconds.**

## 3. Top emitters

By share of logged bytes across the six bots:

| # | emitter | where | level | rate (fleet avg/min/bot) | bytes |
|---|---|---|---|---|---|
| 1 | `[lq] lvl.. (x,y) hp..% | <phase> | <quest board dump> | <view dump>`: **3.35 KB per line** (max 3.6 KB) | `level_quest.lua:2047-2054` (`dbg`, throttled to 2 s; `questDump` `:2027`, `viewDump` `:2016`) | Verbose | 2.0 | **38.2 %** |
| 2 | `[nav] walkTo (x,y) UNREACHABLE on Eld after #ms ...` | `src/Fiesta.Bot/Scripting/BotApi.cs:1964` | Note | 20.6 (stuck bots: ~100) | 17.7 % |
| 3 | `[tent] NPC h=# #u away (need > #u) - walking clear before pitching` | `level_quest.lua:3011` | Note | 20.6 (stuck bots: ~100) | 13.7 % |
| 4 | `[nav] route to (x,y) on map: #ms off-tick -> # waypoints` | `BotApi.cs:2005` | Info | 10.1 | 5.2 % |
| 5 | `walk-path aborted (cancelled / move blocked)` | `src/Fiesta.Bot/Manager/BotManager.cs:2829` | Note | 9.6 | 3.4 % |
| 6 | `[lqprof] alloc / TIME / activeKill memo` (3 lines every 15 s) | `level_quest.lua:5487-5496` | Info | 4.0 | 4.1 % |
| 7 | `[ZoneView] chat <h#>: ...` (mostly NPC ambient lines repeating) | `src/Fiesta.Bot/Session/ZoneView.cs:3753` | Note | 4.8 | 2.9 % |
| 8 | `CRUTCH[CRIT] SLOW TICKS` (every 30 s, fires on 100 ms ticks) | `src/Fiesta.Bot/Scripting/BotScriptRunner.cs:335` | Note | 1.5 | 1.7 % |
| 9 | `[prof] TICK ...` (each tick over 250 ms) | `BotScriptRunner.cs:208` | Note | 0.7 | 0.9 % |
| 10 | `[ZoneView] player appeared ...`, zone-entry dumps (`[Zone] item frame`, `quests active/done`) | `ZoneView.cs:3790`, `src/Fiesta.Bot/Zone/ZoneEntry.cs:190, 226` | Verbose/Note | ~1 | ~1 % |

Emitters 2 and 3 are the two stuck loops described in REVIEW 2.1: logging faithfully reports a loop, 20
to 100 times a minute, and nothing acts on it. 3,005 lines in the sample are `CRIT`, most of them
`SLOW TICKS`, which dilutes the "grep CRIT first" workflow.

## 4. Cost

### 4.1 Per line (benchmark, Ryzen 9 5950X, local SSD; `bench/logcost.txt`)

| case | ns/line | bytes allocated/line |
|---|---|---|
| today, short Note line (`[nav] route to ...`), ring only | 819 | 413 |
| today, a Verbose line nobody reads (no level check) | 840 | 328 |
| today, the 3.3 KB status line with mob tokens | **35,359** | **66,537** |
| today + file (AutoFlush per line), short line | 3,506 | 368 |
| today + file, 3.3 KB status line | 37,263 | 66,440 |
| **gated interpolated-string handler, filtered call** | **24** | **0** |
| gated handler, enabled Note line, ring only | 599 | 368 |
| today's packet-log dump of one 200-byte frame (formatting only) | 11,051 | 13,728 |

The file write was measured on a local NTFS SSD; the pod writes to a mounted volume, where a flushed
write and especially a 99-file rename chain cost more (not measured from here).

### 4.2 Per bot

- **Lua side of the status dump**: `questDump` walks every active quest (NewJoker had 35) with ~5 to 6
  `bot.*` crossings each (`questName`, `questStatus`, `questObjProgress`, `invenCountOf`, ...) and builds
  the string with `string.format`/`table.concat`. Estimate from the crossing costs in `docs/LUA_BENCH.md`
  (MoonSharp userdata ~0.3 to 0.6 us and ~0.8 KB per crossing): **~150 to 200 crossings, ~100 us and
  ~150 KB of garbage every 2 s**, plus 35 us and 66 KB on the C# side, for a Verbose line no workflow reads.
- **Bytes**: at the measured volumes, logging allocates 0.4 to 1 KB per short line and 66 KB per status
  line. For an active leveller (178 lines/min incl. ~12 status lines) that is **~0.9 MB/min** of garbage
  from logging alone.
- **Retained heap**: the ring keeps up to ~14 MB per bot of strings alive for one to two hours, which
  promotes them to gen2: ~100 MB across the fleet that every full collection must mark (the host's last
  blocking gen2 took 527 ms).
- **Threads**: every enabled line takes the ring lock and a file write on the calling thread (tick thread,
  read loop, path worker); every 10,000th line also renames up to 99 files there.

## 5. Which lines are used

From `BOT_RUNBOOK.md`, the CLAUDE.md rules and the transcripts:

| workflow | what it reads |
|---|---|
| "PULL CRITICAL LOG LINES FIRST" | `level=note`, `grep CRITICAL|CRUTCH\[CRIT\]` |
| headline read, then drill down by time | `level=note&max=100000`, then `level=info&from=..&to=..` |
| poll a bot | `GET /api/bots/{id}`: `recentLog` (last 25 Note/Info lines), filtered for `appeared` |
| tick health | `[prof]`, `[lqprof]`, `SLOW TICKS` (now also in `/metrics`) |
| combat / nav / travel debugging | `[combat]`, `[damage]`, `[nav]`, `[travel]`, `[ZoneView]` events at Info, in a time window |
| wire debugging | the packet log, enabled for one bot during one investigation |

Not read by any recorded workflow: the 3.3 KB `[lq]` status line (Verbose), `player appeared`, NPC
ambient chat, the 10-second repeats of the same message, the zone-entry inventory/quest dumps (useful
once at login, at Info). The phase and state of a bot are read from `/phases` and the status endpoint,
not the log.

---

## 6. Targets

| measure | today | target (rewrite) | target (quick wins) |
|---|---|---|---|
| lines/min/bot at the default level | 92 avg, 219 in a loop | **<= 20** steady state; repeats collapse to one line per 10 s | <= 40 |
| bytes/min/bot | 16.4 KB avg, 67.6 KB active | <= 4 KB | <= 10 KB |
| cost of a filtered call | 840 ns, 328 B | **< 30 ns, 0 B** (measured 24 ns, 0 B) | same, for `BotHandle.Log` callers |
| file I/O and rotation on tick/read threads | per line | **none** (one background writer) | none |
| in-memory ring | up to ~14 MB/bot, 1-2 h | 16,384 structured entries, <= 4 MB/bot, hours at default level | <= 4 MB/bot |
| disk | 2.1 GB, 100 files/bot | <= 1 GB total, 3 days, gzip after rotation | <= 1 GB |
| logging share of tick time | unmeasured (status line alone ~135 us every 2 s) | < 1 % | |

---

## 7. Quick wins for the current code (ordered by payoff; not implemented)

1. **Gate the status dump in the Lua.** `level_quest.lua:2047-2054` (`dbg`): return before calling
   `questDump()`/`viewDump()` unless Verbose is enabled. Needs a host predicate: add
   `_lua.Globals["logEnabled"] = (Func<int, bool>)(l => _handle.LogEnabled((BotLogLevel)l))` next to
   `BotScriptRunner.cs:401-404`, and in `dbg` `if not logEnabled(2) then return end`. Also drop the
   board dump from the line (the quest board is on `/quests`). Payoff: **38 % of logged bytes**, ~66 KB +
   ~150 KB of garbage and ~135 us every 2 s per bot.
2. **A level threshold in `BotHandle.Log`, checked first.** `BotHandle.cs:619`: add
   `public BotLogLevel MinLevel` (default `Info`, settable by `POST /api/bots/{id}/loglevel`, persisted with
   the bot), and `if (level > MinLevel) return;` as the first statement; `LogEnabled(level)` for callers.
   In `ZoneView.cs:364` (`LogV`) and the Lua delegates (`BotScriptRunner.cs:403-404`), check before
   concatenating the prefix. Payoff: Verbose lines (21-61/min on active bots) stop costing anything but the
   caller's own interpolation.
3. **Make the interpolation itself free when filtered.** Add an overload
   `Log(BotLogLevel level, [InterpolatedStringHandlerArgument("level")] ref BotLogHandler h)` beside
   `BotHandle.cs:619` (a ref struct like the one in `bench/Fiesta.Bot.LuaBench/LogCost.cs`). Existing
   `handle.Log(BotLogLevel.X, $"...")` call sites bind to it without edits, because the compiler prefers
   the handler conversion for an interpolated string literal. Do the same for the `Action<BotLogLevel,
   string>` delegates in `ZoneView` by giving `ZoneView` a `BotHandle`-backed logger. Payoff: filtered calls
   at **24 ns and 0 B** instead of 840 ns and 328 B.
4. **Move file writing and rotation off the calling thread.** `BotLogFile.cs:39-72`: `Append` posts to a
   bounded `Channel<string>` (drop-oldest, count drops); one background task per host drains it, writes
   in batches, flushes every 1 s and immediately after a `CRIT` line; rotates by **size** (16 MB) with
   date-stamped names instead of renaming a 100-file chain; gzips rotated files; keeps 3 days or 150 MB
   per bot. Payoff: no syscalls or rename storms on tick/read threads; disk 2.1 GB -> < 1 GB.
5. **Collapse repeats.** In `BotHandle.Log`, keep `(lastMessage, firstAt, count)` per bot: an identical
   message within 10 s only increments the count; the next different line (or the 10 s timer, checked on
   the next call) emits `... (x N in 10 s)`. Payoff: 75 % of lines today; the stuck-loop lines
   (`BotApi.cs:1964`, `level_quest.lua:3011`) drop from ~100/min to 6/min. (The loops themselves are
   REVIEW D1 and need a real fix; this only stops them from flooding the log.)
6. **Resolve mob names at read time, not write time.** Remove `ResolveMobNames` from `BotHandle.cs:621`;
   apply it in the `/log` endpoint and the log stream when lines are served (`RecentLines`, `LogLine`
   subscribers). Payoff: the regex pass on every line containing "mob" (35 us / 66 KB on a status line)
   leaves the hot path.
7. **Shrink the ring** to 1,500,000 chars (`BotHandle.cs:23`) once 1-5 have landed: at the reduced volume it
   still holds several hours, and the gen2 heap drops by ~70 MB across the fleet. Correct the "about 5
   hours" claims in CLAUDE.md / BOT_RUNBOOK to whatever the new cap measures.
8. **Demote or dedupe chatty Note lines**: `BotManager.cs:2829` "walk-path aborted" -> Info;
   `ZoneView.cs:3753` NPC chat -> Info for NPC speakers (keep players' chat at Note), dedupe per speaker
   text; `ZoneEntry.cs:190, 226` zone-entry dumps -> Info; `ZoneView.cs:3790` `player appeared` stays
   Verbose; `level_quest.lua:5487-5496` `[lqprof]` -> Verbose (the numbers are in `/metrics` and `/profile`).
9. **Stop `SLOW TICKS` from being a CRIT at 100 ms.** `BotScriptRunner.cs:58` (`CritTickMs = 50`) and `:335` and the
   window line: report it as a metric and log `CRIT` only when the worst tick in the window exceeds
   1,000 ms or the slow-tick share exceeds 5 %; otherwise Note. Payoff: CRIT means "act now" again
   (3,005 CRIT lines in the sample, mostly this).
10. **Cheaper packet log, when it is on.** `PacketLog.cs:66-87`: format with
    `Convert.ToHexString`/`TryFormat` into a pooled `char[]` instead of `ToString("X2")` per byte; copy the
    raw frame into a pooled buffer and format on the background writer; add an opcode/department filter
    and an auto-off after N minutes to `POST /api/bots/{id}/packetlog`. Payoff: 13.7 KB and 11 us per
    200-byte frame -> a copy of the bytes on the I/O thread.
