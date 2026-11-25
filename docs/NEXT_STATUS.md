# Redesigned runtime (`next/`): status

Branch `redesign-2026-11-core`. What is implemented against `docs/REDESIGN.md`, what was measured and proven live,
the bug log, and what is open. Build: `dotnet build next/Fiesta.Bot.Next.slnx`; tests:
`dotnet test next/tests/Fiesta.Bot.Next.Tests` (32 tests).

## Running it

```
XOR_TABLE_PATH=<BYO xor-table.hex> CLIENT_DATA_DIR=<client>/ressystem BOT_DATA_DIR=<private data dir> \
FIESTA_WIRE=2016|2026 ASPNETCORE_URLS=http://127.0.0.1:5098 dotnet next/src/Fiesta.Bot.Server/bin/Release/net10.0/Fiesta.Bot.Server.dll
```

- `CLIENT_ROOT` defaults to the ressystem's parent; walkability is read from the client's own
  `resmap/<kind>/<map>/<map>.shbd` (+ `.shab`), not from any server data.
- `BOT_DATA_DIR` holds the brain / library store, the fleet file, checkpoints, logs, packet captures and
  recordings. Brains are private and never stored in this repository.
- Other commands of the same binary: `login-test` (one login through a wire profile), `shn DIR TABLE[:rows]`
  (client table columns), `bench --bots N --ticks T` (the macro benchmark).

## Implementation vs the design

| REDESIGN section | status |
|---|---|
| 1 layers / projects | Net -> Protocol -> Services -> Agent -> Api -> Brain -> Fleet -> Server as in the skeleton; the Agent has no socket, HTTP or Lua reference. |
| 2.1-2.3 threading, queues, tick | I/O threads decode into a per-bot SPSC `InboundRing` (+ an MPSC lane for job results); the tick enqueues into an SPSC `OutboundRing` drained by the link's writer; operator commands through `ControlQueue`; K tick workers (`TickScheduler`, 50 ms) own a partition of agents; the tick runs control, apply, actions + procedures, move, combat, brain, publish, stats. Nothing on the tick awaits. Off-tick HTTP reads of world state go through tick-side queries (`ControlCommand.Query`). |
| 2.4-2.6 budget, allocation, measurement | `TickSample` per tick (per-step us, allocated bytes, publish step apart, events, Lua instructions); `TickStats` ring + per-minute log-bucket histograms; `/tick`, `/metrics`, debug frame `tickStats`. Instruction-count hook enforces a wall-clock budget per state call. |
| 3.1 actions / handles | `ActionSystem` with `ActionCatalog` matchers per kind (cast start / cast-fail code, bash first swing, target info, item use 0x0700, stones, pick 0x0341, buy 0x0201, sell 0x0381, mount ride on/off); local refusals with reasons; timeouts; link-down failure; resource claims (`L_PREEMPTED`). |
| 3.2 procedures | OpenShop, QuestDialogue (answers derived from the quest script's `IF RESULT` branches, `[MENU]` select, terminal ACCEPT / DONE), GateHop, Dismount, Revive. Trade, Tent, Storage, Travel (multi-map route) fail with "not wired". |
| 3.3 controllers | `MoveController` (path job, legs streamed at run speed, split at 800 u, spawn settle 1 s, MOVEFAIL desync vs blocked with the per-bot overlay, re-plan up to 4 refusals, same-destination re-orders kept). `CombatController` (target / battle mode once per zone session / STOP + BASHSTART, re-arm after moves and cease-fire, cast with face-step for skills with a usable arc, cast bar). |
| 3.4 GameRules | bash and skill reach, mob body, 3x3 target and two-sample segment rules, line of fire (`.shab`), leash left, `CanUse` (learned, cooldown + group, SP, HP, dead, mounted, casting, target, range, line of fire). |
| 4 helper library | 16 modules, 223 functions + `canUse` / `log` aliases, bound by the `[LuaFn]` source generator as `[UnmanagedCallersOnly]` function pointers; iterators over a per-call arena (no tables built); manifest generated. Functions whose data is not decoded / learned yet return their "none" value and say so in their doc (stats, empower, routeDist, assist, armour, equipUpgrade, party/trade/whisper actions). |
| 5 brain | YAML/JSON format (API_SPEC 5.2, `ui` key ignored), linter (exits, timeouts, targets, sensors, `use`, unknown API names, global writes, syntax), layered HFSM with all condition kinds (Lua, event with per-layer catch-up for rate-gated layers, action result via `bb.state`, procedure, job, after, every, value over C# sensors, stuck from progress / budget / timeout, always, all / any / not), hold, cooldown, minDwell, claims, `use` with `${expr}`, `select`, cross-machine and `^` targets, operator force-state, blackboard scopes, budget suspension after 3 overruns, debug frames (U8-U13). Event `filter:` is parsed but not evaluated yet. |
| 6 navigation | client `.shbd` (+ `.shab`) block layer, rectangle mesh, corridor A* + funnel + corner nudge + goal snap + partial path to the nearest reachable point (fiesta-godot port), per-bot overlay, path jobs; map loading is a job (never on the tick). Doors / `.sbi` are not modelled yet. |
| 7 versions | content-addressed brain versions (definition text + modules + pinned library hashes + API version), libraries with versions, channels, assignment log, hot swap at a tick boundary keeping bb.bot and states by name, rollback. Stored as files (no git mirror yet). |
| 8 persistence | file store under `BOT_DATA_DIR` (fleet, versions, channels, assignments, checkpoints, NPC role knowledge). No Postgres / SQLite yet. |
| 9 UI support | the host implements API_SPEC 8 and 13 (U1-U25, deviations in 13.8); the UI itself is on `redesign-2026-11-ui`. |
| 10 lifecycle | supervisor: login -> world -> zone, reconnect with 5-60 s backoff, clean logout (LOGOUTREADY, countdown, NORMALLOGOUT), relog, checkpoint on session end, resume on login, graceful drain on host stop. Health is computed in the bot summary (no tick for 2 s, tick p99 over budget, brain suspended); transition-cycle and no-progress alarms are open. |
| 11 Lua engine | native Lua 5.4 (KeraLua's library), sandbox (no load / loadfile / dofile / io / os), `require` only within the version's module set, coroutine per call with a yield-from-hook budget, no `lua_error` from managed code (pending E_ARG errors). Memory cap per VM and the MoonSharp compatibility engine are not implemented; `lib/compat_v2` is a minimal shim. |
| 12 testing | unit / simulation tests (32), deterministic replay (`SessionRecorder` / `SessionReplayer`, test proves identical intents), the macro benchmark (`bench`). The analyzers exist but are not wired into the build yet. |
| 16 logging | gated interpolated handler (0 B filtered), ring of structs read lock-free, repeat collapsing, rate limit, background writer with size rotation + gzip + retention, per-bot per-category levels with expiry (persisted), packet capture with filters / duration / size cap. |

## Measurements

Machine: the development box (Ryzen 9 5950X class), Release build.

| run | tick p50 | tick p99 | max | alloc / tick | GC |
|---|---|---|---|---|---|
| `bench --bots 100 --ticks 2000` (synthetic world: 50 mobs, 3 moves + 10 % swings per tick; 3-layer test brain with iterators, `canUse`, pull cost, moves, bash, watches) | 1.9 us | 28 us | 7.9 ms (one JIT outlier in 200,000) | mean 13 B, p95 0 B, max 1.7 KB | gen0 0, gen1 0, gen2 0 |
| live bot on the local 2026 stack (grinder brain, combat in Rou) | 46 us | 225-320 us | first ticks (JIT) up to 25 ms | p95 0 B (publish step 84 B mean at 1 Hz, ~47 KB per debug frame when watched) | - |

The debug frame (5 Hz while watched, 1 Hz otherwise) is built on the tick thread because it reads the Lua
blackboard; it is the only sizeable allocation and is measured apart (`publishAllocBytes`).

## Proven live (local 2026 stack, account r_nextbot1, character NextBotA)

- 2026 login chain end to end: version key, login, world list, challenge, OTP, world select, WM login, character
  list, **character creation** (NextBotA, Fighter), tutorial skip, char login, zone entry with the 53 checksums,
  MAP_LOGINCOMPLETE; reconnects after host restarts resume in place.
- Movement: path jobs (e.g. 26 waypoints / 7,799 u across Rou), legs streamed, arrival.
- Combat: target / bash / re-arm, kills (TargetDied), exp: level 1 -> 2 in about 5 minutes of grinding.
- Quests: walked to the Roumen chief and handed in "The Chief of Roumen" (quest 3) through the
  QuestDialogueProcedure (page answered, DONE).
- Brain: hot swap of a new version on a running bot (kept states), operator pause / force-state / actions,
  debug frames, packet capture, log levels.
- Gate travel: the gate hop walked toward the RouCos01 gate (the first run timed out, see the bug log).

Not yet proven live: the 2016 wire (no account on a 2016 server yet: the account provisioning call was refused by
the session's permission policy; the 2016 decoders are verified on a 2016 capture), shops (OpenShop), revive
after death, a completed map change through a gate.

## Bug log (found in live runs)

| # | symptom | cause | fix | commit |
|---|---|---|---|---|
| 1 | first ticks on a map took 64 ms | the nav mesh was built lazily on the tick thread by `nav.snap` / the move controller | map loading is a job; `Layer`/`Mesh` return null until ready; moves wait for the mesh | 774edef |
| 2 | a new brain version's `blackboard:` defaults missing after a hot swap | the swap restored the old bb.bot over the new defaults | defaults fill keys the restored bb.bot lacks (swap and resume); test `Hot_swap_keeps_bb_bot_and_states_and_seeds_new_defaults` | 774edef |
| 3 | 6 ms ticks once a second | the sensor frame scanned every mesh rectangle at each publish | sensor frames only while watched / requested; rectangles cached per 500 u cell | 774edef |
| 4 | re-issuing the same move each tick re-planned (a path job per tick) | `MoveController.Order` always started a new order | the same destination keeps the running order and action; test `Reissuing_the_same_move_keeps_the_running_order` | 774edef |
| 5 | gate hop cancelled after 20 s on the way to a far gate | the GATE action's 20 s budget did not include the walk | the gate hop's action budget = walk at run speed x2 + 20 s | (this commit) |
| 6 | operator move did nothing while the brain was paused | the move / combat controllers were frozen with the brain | controllers keep running while paused; only the brain stops | (this commit) |
| 7 | the log ring returned nothing for small rings | the reader skipped a fixed 64-slot margin | margin capped at a quarter of the capacity; test `Repeats_collapse` | 774edef |

## Open

- 2016 live run (accounts needed), then the cluster soak the operator asked for.
- Cease-fire loops: a few bash streams end with CEASE_FIRE at in-reach distances; diagnostics are logged
  (`combat` Info) - root cause still to be found.
- Procedures: trade, tent, storage, multi-map travel planning; shop buy/sell not exercised live.
- Data still undecoded: displayed stats, skill empower, charged buffs (bag pages assumed 2), abstate durations on
  2026, party / trade packets; 2016 client QuestData format.
- Learners: chase distance, family assist, damage fit.
- Health alarms for transition cycles / no progress; analyzers wired into the build; MoonSharp compatibility
  engine; Lua VM memory cap; Postgres / git mirror for the stores; `.botrec` replay into UI frames.
- Mount detection uses the mount item's InxName (no client column marks mounts): replace with the item class that
  RIDE_ON follows once observed.
