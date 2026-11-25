# Redesigned runtime (`next/`): status

Branch `redesign-2026-11-core`. What is implemented against `docs/REDESIGN.md`, what was measured and proven live,
the bug log, and what is open. Build: `dotnet build next/Fiesta.Bot.Next.slnx`; tests:
`dotnet test next/tests/Fiesta.Bot.Next.Tests` (36 tests).

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
| 10 lifecycle | supervisor: login -> world -> zone, reconnect with 5-60 s backoff, clean logout (LOGOUTREADY, countdown, NORMALLOGOUT), relog, checkpoint on session end, resume on login, graceful drain on host stop. Health is computed in the bot summary (no tick for 2 s, tick p99 over budget, brain suspended, brain load errors, a layer flipping between two states more than 20 times a minute); the no-progress (10 min) alarm is open. |
| 11 Lua engine | native Lua 5.4 (KeraLua's library), sandbox (no load / loadfile / dofile / io / os), `require` only within the version's module set, coroutine per call with a yield-from-hook budget, no `lua_error` from managed code (pending E_ARG errors). Memory cap per VM and the MoonSharp compatibility engine are not implemented; `lib/compat_v2` is a minimal shim. |
| 12 testing | unit / simulation tests (36), deterministic replay (`SessionRecorder` / `SessionReplayer`, test proves identical intents), the macro benchmark (`bench`), the tick-path analyzer FBOT001 on Agent / Api / Brain (build error), FBOT002 for `[HotPath]` methods. |
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
- Gate travel: gate hops Rou -> RouCos02 (Forest of Tides) -> Rou, each a cross-server handoff (LINKOTHER,
  reconnect, MAP_LOGIN with the new WM handle), with the gate's menu answered.
- Shops: Pey's shop opened through the NPC menu (65 items listed), BUY_ACK 0x0201 (2 Floatation Stones, money
  52 -> 42, bag cell gained), SELL_ACK 0x0381 (money +1, lot 2 -> 1).
- Levelling: NextBotA level 1 -> 4 in about 15 minutes of the grinder brain (Rou south field and RouCos02).

Not yet proven live: the 2016 wire (no account on a 2016 server yet: the account provisioning call was refused by
the session's permission policy; the 2016 decoders are verified on a 2016 capture), revive after a death (no death
happened at the levels played), skill casts (the test character has no combat skill learned yet; casts are covered
by unit tests).

## Bug log (found in live runs)

| # | symptom | cause | fix | commit |
|---|---|---|---|---|
| 1 | first ticks on a map took 64 ms | the nav mesh was built lazily on the tick thread by `nav.snap` / the move controller | map loading is a job; `Layer`/`Mesh` return null until ready; moves wait for the mesh | 774edef |
| 2 | a new brain version's `blackboard:` defaults missing after a hot swap | the swap restored the old bb.bot over the new defaults | defaults fill keys the restored bb.bot lacks (swap and resume); test `Hot_swap_keeps_bb_bot_and_states_and_seeds_new_defaults` | 774edef |
| 3 | 6 ms ticks once a second | the sensor frame scanned every mesh rectangle at each publish | sensor frames only while watched / requested; rectangles cached per 500 u cell | 774edef |
| 4 | re-issuing the same move each tick re-planned (a path job per tick) | `MoveController.Order` always started a new order | the same destination keeps the running order and action; test `Reissuing_the_same_move_keeps_the_running_order` | 774edef |
| 5 | gate hop cancelled after 20 s on the way to a far gate | the GATE action's 20 s budget did not include the walk | the gate hop's action budget = walk at run speed x2 + 20 s | 1694d3d |
| 6 | operator move did nothing while the brain was paused | the move / combat controllers were frozen with the brain | controllers keep running while paused; only the brain stops | 1694d3d |
| 8 | our HP flapped 27 % <-> 100 % and the survival layer thrashed | SOMEONESWING_DAMAGE was read with a u16 flag and a damage field; its layout is {flag u8, resthp u32 @5} with no damage amount | decoder fixed, damage on us derived from the HP drop; DOTDAMAGE decoded; test `Someone_swing_carries_rest_hp_not_damage` | 6070c0d |
| 9 | BASHSTART / SWING_START / CEASE_FIRE loop, 23 times in 10 s, no damage | swings started at the edge of the keep reach ((body + 120) x 1.25) were refused and the controller re-armed after every cease fire | start reach = body + weapon reach; a stream that landed nothing is re-armed once, then the bash stops and `combat.inBashReach` asks for a closer spot; tests `Refused_swing_starts_are_not_rearmed_forever`, `A_swing_stream_that_landed_is_rearmed` | 6070c0d |
| 10 | gathering nodes (HERB / WOOD / MUSHROOM1) counted as huntable mobs | huntable = any non player-side mob | huntable requires MobInfo MaxHP > 0 | 6070c0d |
| 12 | the operator saw "the bot left the world" flicker on the Game tab during normal play | (a) a viewer that subscribed before the first capture got an `off` before its `snap`; (b) captures taken between two zone servers counted as leaving the world; (c) two lifecycle loops could run for one bot (the autostart and an operator start raced in `BotRuntime.Start`), logging the same account in turn and kicking each other | `off` only when the supervisor says the bot is out of the world, once per outage, with a reason (`left-world` / `disconnected` / `host-stopping`); `Start` is serialised; test `Off_is_sent_only_when_the_bot_really_leaves_the_world` | (see git log: game-view off fix) |
| 13 | NextBotA / NextMageB cycled in outer Roumen without progress (operator report) | the plan pick (accepts ranked by exp per travel time, each with a danger-aware route search) ran over the quest layer's Lua budget; the blackboard still held the hand-in that had just been given up, so the layer went back to it every 30 s | safe route costs cached 30 s per (here, there, level); the brain clears its task before picking (a pick that dies leaves nothing to replay); health alarm "no progress" after 10 min without exp, level or quest change; test `A_pick_that_does_not_finish_never_replays_the_previous_task` (fails without the fix) | 732c75e |
| 14 | "level 7 bot running around killing slimes, no quest awareness" (operator) | the server counts only a quest's kill objectives; the bot read counter i as objective i, so a quest {talk, kill 5 slimes} put the five kills on the talk row and stayed 0/5 forever | `QuestDef.CounterOf`; test `Quest_counters_belong_to_the_kill_objectives_only` | 791bdc3 |
| 15 | ready quests were never handed in ("talk resolved without the quest changing") | quests with a choice of reward items need QUEST_REWARD_SELECT_ITEM_INDEX before the final page's ack; the server answered REWARD_NEED_SELECT_ITEM | the dialogue names its pick (the quest's reward-row index, from a capture); status 8 counts as ready; quests.giveUp; test `A_hand_in_with_a_reward_choice_names_the_pick_before_the_ack`. Live: NextMageB handed in Master Zach and Mischievous Monsters | 906b760 |
| 16 | a bot sat "in zone" without a session; relog / stop answered "offline" | an exception in the session teardown escaped the lifecycle loop, which died silently | teardown guarded, the loop marks the bot Failed if it ever dies | 747f663 |
| 11 | a successful gate hop through a cross-server handoff reported Failed (link down) | the procedure settled one step after the link-down that follows LINKOTHER in the same tick | procedures settle as soon as an event finishes them; test `Gate_hop_succeeds_through_a_cross_server_handoff` | 890d74f |
| 7 | the log ring returned nothing for small rings | the reader skipped a fixed 64-slot margin | margin capped at a quarter of the capacity; test `Repeats_collapse` | 774edef |

## Open

- 2016 live run (accounts needed), then the cluster soak the operator asked for.
- Bash start reach: swings landed at 127-128 u from a Slime (start reach 130) and some starts at 125-130 u were
  refused; the start / keep rule needs the acceptance probe of REVIEW 5.1 (distances logged at `combat` Info).
- Procedures: trade, tent, storage, multi-map travel planning; shop buy/sell not exercised live.
- Data still undecoded: displayed stats, skill empower, charged buffs (bag pages assumed 2), abstate durations on
  2026, party / trade packets; 2016 client QuestData format.
- Learners: chase distance, family assist, damage fit.
- The no-progress health alarm; MoonSharp compatibility
  engine; Lua VM memory cap; Postgres / git mirror for the stores; `.botrec` replay into UI frames.
- Mount detection uses the mount item's InxName (no client column marks mounts): replace with the item class that
  RIDE_ON follows once observed.

## Watching the bots in the UI

The host serves the built UI (`UI_DIR`, SPA fallback) and the API + `/api/v3/ws` on one port; the long-lived
instance runs on http://127.0.0.1:5300 (loopback, no token). The Brain debugger page (`#/bots/<id>`, live
following), Fleet, Log and Inventory pages render against this host without a mismatch found so far.

## Game view (API_SPEC 14)

Implemented on the host: the `game` WebSocket topic (snapshot, then op-list deltas, `off` when the bot leaves the
world), the static data routes with ETag / `Cache-Control: public, max-age=86400` (304 on `If-None-Match`), item /
skill / abstate icons cut from the client's atlases (`<client root>/resmenu/Icon`), minimap art
(`<client root>/resmenu/minimap/<MapName>.dds`) with its placement from MapViewInfo.

How it is produced: `GameTap` (Agent) fills a preallocated `GameCapture` on the tick at most every 100 ms and only
while someone watches, and hands it over through a lock-free triple buffer; `GameStream` (Server) keeps the state
the viewers have and writes the differences on the WebSocket pump thread, once per bot for all viewers.

Measured (local 2026 stack, NextBotA in Roumen, 54 entities in view, 19 skills, 15 bag cells):
- snapshot 3.1 KB; deltas 77 - 236 B/s (0.8 - 2.2 messages/s) while roaming / walking in town (54 entities); nothing is sent while nothing changes,
  plus a keep-alive every 5 s;
- capture cost 18 us per fill with 100 entities (Release), 0 bytes allocated (test
  `Capture_does_not_allocate_on_the_tick`); at 10 Hz that is ~9 us per tick on average. Live, 60 s each:
  without a game viewer tick p50 50 us / p99 288 us / alloc p95 0 B; with one game viewer p50 52 us / p99 229 us /
  alloc p95 0 B (the difference is within noise).
- UI requests U26-U29 (UI branch section 13.9): U26 `category` follows the client's skill window: produce range 20000..29998
  is never on the active page (client Skill::IsProduceSkill), SummonMate01 is left out; inside the range only the ids
  the Produce / Gather tables name are `production` (GUESS until the client's production page is confirmed), the rest
  `other` (test `Skill_categories_follow_the_client_skill_window` with NextBotA's 19 skills) and `rowKey` = the InxName family; a
  `passive` category is not derivable from a skill id (the passive and active id spaces overlap) - passives need
  their own list (open, not decoded yet). U27 `off` reasons are `left-world` / `disconnected` / `host-stopping`.
  U28 / U29 done (below).
- UI requests after the first build of the Game tab: names that the US client data ships as "?? ??" fall back to
  the InxName (the bytes in the file are literally question marks, not an encoding problem); skills with
  `icon:false` have no art in the client data (`IconFile` "-": event / item-use skills); `self.dir` is degrees
  0..359, 0 = east, counter-clockwise; `index.html` is served `no-cache`, `/assets/*` `immutable`.

New decodes for it: the character stats (CHAR_PARAMETER_DATA in MAP_LOGIN_ACK; live changes through
CHANGEPARAMCHANGE ids 0..15, the id = the stat's position, checked on a capture), fame and free stat points from
CHAR_BASE, soul stone capacity.

Skill ids check (coordinator's sanity question): NextBotA's 19 skill ids (29001 Mining, 29206 Ride Mover, 29349
Use Potion, the event ball skills, ...) are right: the CLIENT_SKILL frame (13:28:05, 238 B) reads restempow 1,
partmark 3, maxnum 19, chrregnum 0x0BCE, number 19, 19 x 12-byte records, and every id resolves to an ActiveSkill
row; a level-4 Fighter has not bought a class skill yet (the Mage got Magic Missile [01] at creation). With the
UI's id-range rule they would all be "production"; the host's data-driven rule makes only Mining production.

Open bug: once, right after a host restart, NextBotA came into the world with an EMPTY skill list (a relog then
received the 19-skill CLIENT_SKILL as usual; the frame arrives before MAP_LOGIN_ACK and nothing clears the skill
book). Packet capture is left on for the next restart to see whether the server sent the frame.

Open: CHANGEPARAMCHANGE ids 0x12..0x1B and 0x24 (values 0 in the capture) are not named yet; player entities
carry no class id yet (`id` -1 for players); drop entities carry the item id only where the drop decode has it.

## Leveller brain (port of the private Lua drivers)

The levelling / questing logic of the private Lua repo is rebuilt as the brain `leveler` (layers survival,
combat, quest; reusable sub-machines npcTalk and zoneWork using the `^/plan` exits of section 13.11). Like the
Lua repo it stays private: it lives in the gitignored `scripts/brains/` with its old-area -> new-module mapping
table (`scripts/brains/MAPPING.md`, every area marked ported / partial / open). What it needs from the runtime is
generic and committed: the world atlas (client NPC spots, spawn areas, gate graph + learned gates), multi-map
travel, quest availability / hand-in helpers, collect items linked to their dropping mob.

Why NextA spun in Roumen: the old test brain only travelled through a gate already in view to a configured field;
in the north of Roumen none is in view, so it alternated seek <-> roam. The leveller routes over the atlas.

Runtime pieces added while porting it (all committed, generic): danger-aware routing (maps whose easiest hostile
mob is above a level are routed around; `atlas.safeCost` / `easiestLevel`), gates that answer no menu (silent
refusals, e.g. a level requirement) are routed around and remembered per bot for 15 min, a fresh brain swap cancels
the previous version's pending actions, `quests.acceptable` is cached per level / map / quest-log version.

Not ported yet (open, see the private mapping table): stone / potion restock and service discovery (healer,
merchant, skill master, smith by shop kind), selling, skill learning, gear, storage, stat points, mount, kite / shed /
flee, death-loop escape, scenario / instance driver, the support / producer / trade / social brains.

Live (local 2026 stack): NextBotA accepted and handed in quests across Roumen (Baby Steps chain, the Grandpa Robin
chain, The Chief of Roumen ...), level 4 -> 6 in about 10 minutes, exp/h about 3400.

Open bug: after a host restart NextBotA twice logged in but never reached the world: MAP_LOGIN_REQ sent, no
MAP_LOGIN_ACK and no MAP_LOGINFAIL, the supervisor showing InZone with empty vitals; a relog a minute later worked.
Both times the previous session had just logged out (the restart's graceful drain), so the likely cause is the zone
still holding the character while its logout completes; not confirmed. The session now ends when MAP_LOGIN_REQ is
not answered within 20 s or is answered with MAP_LOGINFAIL (a missing answer is an error like any other, test
`A_zone_that_never_answers_the_map_login_ends_the_session`), so the supervisor logs in again with its backoff. The
server-side cause stays open.

### Leveller port, steps 1-2 (survival, upkeep)

- Survival: flee when outmatched or low with nothing to heal (run to a clear spot / away from the nearest
  aggressor); two deaths on a map within 5 min avoid it for 30 min; trips avoid hostile spawn areas above the
  level limit that lie on the walk (not the map's easiest mob); avoided / refusing maps are saved per bot.
- Upkeep layer: soul stones (HP, and SP for SP classes) at a healer, plain loot sold at a merchant, skill books
  from the skill master, free stat points; service NPCs are found by role, learned from the window kind an NPC
  opens (probing unprobed NPCs in towns). Live: NextMageB found a healer and bought 7 HP + 1 SP stones.
- A shop whose NPC answers the click with a quest page opens with the menu answer, as the client does (captured).
- Stat points (resolved): the server did answer: INCPOINT_REQ {End} got CHAR_STAT_INCPOINTFAIL 0x1061 {0x0AC2},
  which the bot did not decode, so it looked unanswered. INCPOINT {Int} is acked (0x105F) on the 2026 stack, the same
  layout the 2016 capture and the Godot client's 2026 stat window use. The refusal is decoded now and the brain moves
  on to the next stat of its class order. CHAR_BASE's statdistribute block was wrongly read as free points (fixed).
- Open: gear upgrades (ItemInfo stats not loaded yet), storage (procedure not wired), potions.
