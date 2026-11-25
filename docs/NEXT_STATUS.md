# Redesigned runtime (`next/`): status

Branch `redesign-2026-11-core`. What is implemented against `docs/REDESIGN.md`, what was measured and proven live,
the bug log, and what is open. Build: `dotnet build next/Fiesta.Bot.Next.slnx`; tests:
`dotnet test next/tests/Fiesta.Bot.Next.Tests` (94 tests).

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
| 3.2 procedures | OpenShop, QuestDialogue (answers derived from the quest script's `IF RESULT` branches, `[MENU]` select, terminal ACCEPT / DONE), GateHop, Dismount, Revive, Storage (the keeper's window), Travel (multi-map route; waits while another layer holds the legs). Trade runs as step actions (no procedure). Tent fails with "not wired". |
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
  Error 0x0AC2 (2754): the 2026 client has no text for it (its error table falls back to "An unidentified error code
  has occurred."). Live evidence: with REMAINPOINT 6 the server refused every End request with 0x0AC2, and accepted
  Int at level 8; a 2016 capture shows Str accepted ten times in a row. The rule behind the End refusal is not
  identified; the brain learns refused stats per level instead of assuming one.
- Gear, storage, potions: see the next section.

### Leveller port, step 3 (2026-11-25: potions, storage, selling / gear live, trade, team aid, production)

Runtime (committed): storage window (MENU_OPENSTORAGE) and bag <-> storage moves (ITEM_RELOC_REQ, acked when the
source cell changes, refused with the RELOC_ACK code); ItemUseEffect per item (`items.isPotion / bestPotion / potions /
useEffect`); player trade (TradeDecoder for dept 19, TradeState, `social.trade*` step actions each resolved by its own
ack / fail, events tradeAsked / tradeStarted / tradeEnded); the host-wide team board (`team.*`); production (Produce
table, `produce.*`, PRODUCE_MAKE / CAST_FAIL / PRODUCTFIELD / SKILLEXP decoded); `npc.close`.

Brain (private): potions.lua (HP potions after the stones, SP stones then SP potions for SP users, potion buying at
any open shop that sells one), storage.lua + upkeep `storage` (keeper by role), aid.lua + layer `team` (asks for SP
potions / stone money; a teammate claims, walks over, trades), craft.lua + upkeep `craft` (known recipes whose
materials we hold, potions or when the bag is short), grind rounds no longer end every few seconds (`objectiveMet` was
true for grind tasks: the bot re-planned the grind spot every 10 s), trips refused because a higher layer holds the
legs are retried instead of given up (PREEMPTED).

Proven live (local 2026 stack):
- Selling and gear buying, triggered by the bag (5 free slots) and money: NextA 17:00:57 sold its loot at the Rou
  merchant (money 22 -> about 350), 17:01:11 opened the smith (`gear: buying 6 (+13)`, Chain Pants, 200 cen; money
  -> 151) and 17:01:13 equipped it (`gear: equipping 6 (score 21)`; equipment slot 19 = Chain Pants, Leather Pants
  back in the bag).
- Storage keeper discovered by role: NextM probed RouStoreRaina (npc 108) at 17:35:58, the keeper's quest page was
  answered with the menu and the storage window opened (`storage open at npc 21708: page 1/16, 0 items, 0 cen`).
- Team aid: NextM (Mage, 4 SP potions) asked for 6 SP potions; NextA (Fighter, 8 SP potions it never drinks) claimed
  the ask, walked over and traded at 17:51:46-47: PROPOSE -> the asker's PROPOSE_ASK answered yes -> START ->
  UPBOARD_ACK (SP Potion x18, bag 21) / OPPOSIT_UPBOARD (item 3505 x18) -> both BOARDLOCK -> OPPOSIT_DECIDE ->
  DECIDE -> TRADECOMPLETE in 0.9 s; NextM's SP potions 7 -> 25, NextA's 18 -> 0 (the game boards a whole bag slot,
  so the stack went, not the 6 asked).
- Before that, ten proposals came back PROPOSENO 0x06C8 while NextA still had the smith's window open from its gear
  visit; closing its own window before proposing fixed it (the asker's window was closed already). 0x06C8 is the
  game's generic "rejected" (the client shows "Trade was rejected by the other."); an open NPC window on either side
  is one cause.

Bugs found and fixed on the way:

| # | symptom | cause | fix | commit |
|---|---|---|---|---|
| 17 | quest trips gave up within a second ("GIVE-UP ... no answer from the NPC") while the bot fought | a trip start refused because a higher layer holds the legs returns -1; the brain read that as a failed trip | the brain marks such a start PREEMPTED and retries it | (brain) |
| 18 | travels failed with "could not reach: Cancelled" / "the gate to the next map never came into view" after fights on the way | a higher layer's move cancels the travel's own walk, which the travel counted as a failure | the travel waits while the legs are held and plans again; test `A_trip_whose_walk_another_layer_takes_over_waits_instead_of_failing` (fails without the fix) | 629fa88 |
| 19 | the storage window closed itself at once | the 2026 server sends MENU_OPENSTORAGE_FAIL {01 08} right after a successful window (and once more later) | it no longer closes the window; logged with the window state | b1d4fe6 |
| 21 | trade proposals refused PROPOSENO 0x06C8 | the proposer's own NPC window (the last shop) was still open | the giver closes its window (END_OF_TRADE) before proposing | (brain) |
| 22 | a trade confirm timed out although the trade completed | TRADECOMPLETE came instead of our DECIDE_ACK (the partner had decided first) | COMPLETE acks the confirm; test `A_confirm_is_acked_by_the_completion_when_it_comes_first` | 8014f13 |
| 23 | a brain calling `produce.*` passed the lint on a host without the module, then failed every tick | the linter's API module list missed `team` / `produce` | both added; test lints a missing function of every `[LuaModule]` | 0b74f9a |
| 20 | a grind round ended every 10 s and re-planned the same spot | `hunt.objectiveMet` was true for grind tasks (no quest) | grind rounds end on level-up or after 90 s | (brain) |

Open:
- Storage deposits not exercised live yet (they need a bag that selling cannot clear); the meaning of the 2026
  0x3C07 {01 08} is not identified; storage cell capacity per page is learned from refusals, not assumed.
- Production not exercised live (the bots know no recipe yet); the producer's commission / price-list selling of the
  old scripts is not ported.
- JCQ (level 20) and the instance / scenario driver: not started (the bots are level 8-10; nothing can be proven
  live before level 20). The JCQ chain's plain quests are covered by the leveller; the scenario part needs the
  instance driver (areas, waves, the clone fight).
- The aid asker waits 15 min for its helper; a helper that loses its claim (the ask was re-posted) only notices when
  it arrives.

### Step 4 (2026-11-25 evening): split, production fields, scenarios, battle zones, exp/h

- exp/h read 0 right after every host restart: the rate was this session's exp over this session's time, and the bots
  were questing in town (no kill yet) when it was read. It is now kept per bot over the last hour across sessions (it
  still starts over with the host). Both bots were earning: NextA level 8 -> 12 over the afternoon, NextM 1032 -> 1232
  exp in three minutes of grinding at 18:05.
- Stack split (ITEM_SPLIT_REQ 0x300D {from, to, lot} -> two CELLCHANGEs -> SPLIT_ACK 0x0441, from two captures) as an
  action; the aid giver splits the asked amount into a free cell and boards that cell. Not proven live yet (no ask has
  come up since).
- Production fields: reading a recipe book is refused with ITEM_USE 0x0717 until the recipe's production field is
  chosen. Live, 18:15: NextA chose field 29105 (Material Decomposition) and read its recipe (0x0700), chose 29104,
  and the third field (29101, Stone Production for Elrue [1]) was refused 0x0B14: a character works a limited number
  of fields (two here). The leveller now reads only recipes worth a field (potions it drinks) and keeps the materials
  of the recipes it knows out of the merchant's hands; other recipes go to storage. The two fields NextA chose before
  this rule are spent (the game has no way back that the bot knows of).
- Free / party battle zones: routes from Rou crossed Fbattle01 (Free Battle Zone 1, linked like a field in the client's
  gate tables; its gate in fact leads back to Rou), which is why bots were found logged in there after a restart.
  The zone announces itself with MAP_PARTYBATTLEZONE_ON 0x1818 right after the LINKOTHER (capture); such maps are
  avoided for a week once seen. Live 18:21:55: `Fbattle01 is a player-fight area (party battle): routed around from now on`.
- 0x3C07 {01 08}: the 2026 client shows "An error has occurred while using the Storage." for every 0x3C07 (its text
  table, id 0xDF446EB4). It came 46 ms after a successful storage window opened from a probe click (the keeper had
  answered with a quest page first) and again 17 s later. What the bot did to cause it is not identified yet; the
  capture of the next storage visit (MENU, ACT and ITEM departments are being captured) will show the sequence.
- Scenarios: AREAENTRY_REQ (the server asks the client to confirm a trigger area), DOORSTATE, BUILDDOOR,
  OBJTYPECHANGE, timers and messages decoded (layouts checked on the job-change capture) and logged; the map's areas
  come from its .aid file. The leveller's `instance` layer walks into an asked area's box and confirms it
  (AREAENTRY_ACK), repeating while the room stays quiet. Not proven live: no bot has entered an instance yet (the job
  change starts at level 20; NextA is 12, NextM 9).
- Team aid also covers HP potions (a teammate keeps its own stock and gives the surplus) and the asker pays a list
  price for items (half the client buy price, judge.TEAM_PRICE); money asks are gifts.
- A money aid came up naturally (18:20, NextM 62 cen for stones); NextA's walk to it failed in Fbattle01 (above), the
  ask is re-posted after its cooldown.

### Offline batch while the stack moves to the Linux builds

- Door-aware paths: the map's .sbi doors (count, 56-byte headers {name, tile box, bitmap size, offset}, two bitmaps per
  door; on client instance maps the state 1 bitmap equals the .shbd, and the job-change capture shows a cleared room's
  door switched 0 -> 1). BUILDDOOR names the door's block, its state picks the bitmap, and the tiles it blocks beyond
  the map's own go into the bot's nav overlay. The path search used the overlay only to snap start and goal; a
  crossing whose leg runs over an overlay tile is now not taken (this also keeps paths off tiles the server refused).
  Test: a path goes through a near gap when the door is open and around by a far gap when it is closed.
- Waves: the mobs that appear after an area confirm are the room's wave (`scenario.wave()`); the leveller's
  `instance` layer holds the room (combat fights in it) until every mob of the wave is gone, and follows the next room.
- Producer brain (private, `scripts/brains/producer`): production field, recipes bought and read as the type mastery
  allows, raws bought where a merchant sells them, the goal recipe or the cheapest allowed one, chat commissions
  ("need N x <product>") quoted at raw cost, made, split to the count and traded against the payment. Lint + a
  simulation test; not run live. `produce.typeMastery` assumes the mastery totals of one type are shared (GUESS, to be
  checked on the first live craft).
- The host was not restarted for these (live work is paused); the next restart deploys them.

### Back on the Linux stack (2026-11-25 evening)

Live: a money aid of 1428 cen NextA -> NextM at 20:04:45-46 (CENBOARDING_ACK 1428 on the giver, OPPOSIT_CENBOARDING
1428 on the asker, both locks, DECIDE, TRADECOMPLETE). NextM bought Fire Bolt [02] at its level-up visit; the Rou skill
master's list (123 items) has `Recipe: HP Potion (Tier 1)` (item 20100, 250 cen) - NextM could not afford it after the
book; the next visit buys it now that the aid paid for the stones.

| # | symptom | cause | fix | commit |
|---|---|---|---|---|
| 24 | NextA earned no exp for 1.5 h (exp 3527 / 4671 from 18:30 to 20:10) | it walked between Rou and Eld to the givers of Q_TYPE 3 quests; the server leaves the NPC click unanswered (captured: NPCCLICK at the NPC's spot, nothing back), each accept was given up for 10 min and the next such quest picked | the leveller learns per level which quest kinds (`quests.kind`) the givers never offer (3 silent givers, none offered) and no longer walks to them | 850c4a7 + brain |
| 25 | after a host restart or a published brain, skip lists, refused books, unstorable items, probed NPCs were forgotten | bb.bot is saved as JSON; integer keys came back as string keys, so `t[20000]` missed | integer keys restored as integers; test fails without the fix | 14c0622 |
| 26 | the host died: access violation in lua_rawgeti (debug frame on a tick worker) | a session teardown checkpointed and disposed the brain while a worker still ticked the agent (Remove only queued the removal) | `TickScheduler.RemoveAndWait`; test | 25af8d0 |
| 27 | NextM walked into Fbattle01 as a service NPC's map | player-fight maps were learned per bot | learned once in the shared atlas (persisted, `atlas-pvp-maps.txt`; Fbattle01 seeded from NextA's live finding) | 76b701c |

#### Bug 28 (resolved): quest givers that "do not answer the click"

What the wire shows (NextA, 20:10:05, Eld): the last walk leg ends on the giver's spot (17382,12362), END_OF_TRADE +
NPCCLICK {21624} are sent a second later, and nothing comes back in the ACT, MENU, QUEST, ITEM, TRADE or SCENARIO
departments. Same for givers 27 (item merchant Nina) and 112 (item merchant Kenton) on every try since 18:16, while
other givers on Eld answer the same click (quests 27 and 30 handed in / accepted at 17:56 and 18:09).

Evidence about the quests: all three are QuestData Q_TYPE 3, and 142 of the 239 Q_TYPE 3 quests are named as event
quests ("I Really Love To Share My Love... (Event Quest)" 20028, "[Special Event] The four or five horsemen 2" 65478,
"[Valentine Event Quest] Valentine Council Director, Kobal" 65487). The giver of 65487 (E_Kobal) is never in view at
its spot: the event NPC is not spawned. So the quests are event quests and their events are off - real behaviour for
the quests. Not explained: a merchant with a menu answering a click with nothing at all (the bots' merchant clicks in
Rou are answered with NPCMENUOPEN). Not checked yet: the same click from the real client on Nina (needs the operator).

Workaround (marked in the brain's plan module): a quest kind whose givers stayed silent 3 times at a level, with none
offered, is not walked to at that level. To do: the official-client click on Nina; then either the bot's click is
wrong (fix it, drop the workaround) or the server answers nothing for that NPC (state it, keep only the event check).

Fire Bolt [02] refused 0x070B: not a refusal of the book; it was a second copy. The first read was acked 0x0700 and
followed by SKILL_LEARNSUC {6041}, which the bot did not decode, so the skill master's book was bought and read again
(0x070B: the skill is learned already). Fixed: 3fccd9c.

### The job change, planned end to end (prepared before a bot reaches 20)

Chain (client quest table; test `The_promotion_quests_start_a_scenario_and_their_chain_is_found`): 60015 (giver 98,
from level 20) -> 60016 (98) -> 60017 (143) -> Road to Promotion 60006 / 60011 / 60012 for the fighter / archer / mage
lines (giver 143, start script `ACCEPT`, `SCENARIO 8`; while active the giver's DOING script runs `SCENARIO 8` again,
which is how a failed run is re-entered; end script `DONE`, `SCENARIO 9`).

What the leveller does with it:
1. Accepts the chain first once its level allows (`quests.scenarioChain`: a scenario quest for exactly our class and
   the quests before it), whatever the other quests pay (operator: the job change is never deferred - gear and
   skills past 20 wait behind it).
2. An active scenario quest that is not done, outside its instance: back to its giver to be sent in (task `enter`).
   Its collect objective (an item issued inside) is not hunted in the field.
3. Inside the instance the quest layer waits; the `instance` layer plays the rooms: an asked area is confirmed every
   500 ms until the room's wave appears (the server judges the confirm by our server-side position; a single confirm
   sent while it still has us short of the trigger is lost - the old bots' "second room never spawns"), the wave is
   held and fought (no flee inside an instance), then after 5.5 s (the next trigger is armed late) the bot walks to
   the nearest trigger area it has not been asked about or walked to, until the server asks again.
4. The scripted copy of us (a character, no mob id) is turned into a mob in place on OBJTYPECHANGE 5 and back on 4
   (it flees at ~75-80% HP: that is "beaten").
5. Doors: closed doors are kept out of paths (.sbi bitmaps by state).
6. Casts at the target our swings land on skip the face step inside an instance (all casts were refused 0x0FCA in
   these rooms when the face step's move was refused).
7. CHAR_PROMOTE_ACK changes the class; the no-progress alarm counts rooms (an instance gives no exp).
8. Potions are drunk when the stones are out (the mage's win needed potions; the old notes list potions, moving while
   kiting, no cast with a walk in flight, a refused cast holding the skill, facing, waiting for potions before
   entering, and a ready hand-in outranking errands - the last two are covered by the plan's order).
Test: `The_job_change_instance_is_played_room_by_room_as_the_capture_shows` (frames from the job change capture).
Open: kiting for ranged classes in the copy fight, buying potions before entering, the clone's damage race.

### Death spiral and purse guard (the old bots' level-25 failure) - first version, superseded

The first guard lowered the fight level per death. Operator correction: easier mobs waste stones for low rewards and
CAUSE the spiral. Replaced by the cause-based guard and the efficiency planner below. The runtime ledger stays:
`Economy` (one-minute buckets for an hour: exp, money earned / spent, stone spend, deaths), `econ.rates(minutes)`.

Bug 28 resolution (21:09, NextA at Nina, with the QUEST department in the capture): the click IS answered. Nina sends a
quest page (QUEST_SCRIPT_CMD_REQ for quest 20017, a [MENU] page); our dialogue picks 20028 from it
(QUEST_SELECT_START_REQ {27, 20028}) and the server answers QUEST_SELECT_START_ACK {27, 20028, 0x0B47}: it will not
start that quest (an event quest whose event is off). "Nothing comes back" was a capture filter without the QUEST
department, and the bot ignored the answer: QUEST_SELECT_START_ACK was decoded with the NPC id in place of the code.
Fixed: the ack's code is decoded, the dialogue fails with it ("not offered by the NPC: ... 0x0B47"), and the leveller
leaves such a quest for 6 h. The quest-kind workaround is removed. Test with the captured bytes.

### Death guard redesigned (operator correction: easier mobs waste stones and ARE the spiral)

The first guard lowered the fight level after deaths; removed. Deaths are now diagnosed and the cause fixed at the
right level (caution.lua, layer `guard`):
- several mobs on us -> pull one at a time (no pull that wakes others) and rest fully between fights;
- one mob type killed us twice in 30 min -> that type is left alone unless it attacks;
- nothing left to heal -> stones bought before the next fight; out of SP -> SP kept up from 50 %;
- every death: the gear / skill visits are due again (under-powered for the level is the root cause);
- out of a fight HP comes back by itself (survival `rest`: no new pull, no stones below 70 % HP); stones and potions
  only in a fight;
- economy (ledger): a death rate >= 3/h or a draining purse raises ECON ALARM with exp / money / deaths / stone spend
  per hour; the remedy is earning efficiently (sell sooner, stones to the minimum, quests that complete), never
  easier mobs.
Test: `A_scripted_death_spiral_is_reversed_without_lowering_the_fight_level`.

### Efficiency: the planner's objective (operator guidance 2026-11-25)

Principle: the most progress for the least expense. The planner (plan.lua + value.lua, private) scores every
candidate - hand in, enter an instance, accept, hunt a quest mob, grind - as VALUE PER SECOND and takes the best:
- value (in exp): quest reward exp + money (QuestReward type 1, 1 cen = 1 exp, `EXP_PER_MONEY`) + item rewards
  (`ITEM_EXP` each, a choice counts once) + the kills the quest needs (net exp per kill); the job change chain gets a
  bonus that puts it first;
- cost (in seconds): travel (straight line on the map, the safe gate route across maps) + a trip overhead + kills x
  (kill time + rest per kill); a kill's net exp subtracts its stones (stones per kill x stone price) and its death
  risk (deaths / kills x `DEATH_COST_EXP`);
- stacking: an accept whose mobs an active quest is already hunting costs no extra kill time, so quests over the same
  mobs are taken together; hand-ins are worth their full reward over a short walk, so they win when due;
- measured, not guessed: the runtime ledger learns per mob type the kills, mean kill time (first hit by us to its
  death), stones used in its fights, exp per kill (EXPGAIN names the mob) and deaths by it (the mob that hit us last);
  exp with no mob is counted as quest exp. Lua: `econ.mob(id)`, `econ.questExp()`, `quests.reward(q)`. The judge's
  defaults (15 s, 10 exp, 0.2 stones per kill) stand in only until a type has been fought.
- no expense that buys no progress: no stones between fights (survival `rest`), event quests the NPC refuses are left
  for 6 h, grind fallback only when it scores best.
Every pick is logged: `plan: <kind> quest <id> <name>: <score> exp/s (value <v> over <s> s)`, and kept in
`bb.bot.plan`. The bot summary (`/api/v3/bots`) carries `econ` {expH, moneyH, deathsH, stonesH, questExp} over the
last 30 minutes. Tests: `The_planner_values_a_mob_by_what_the_bot_measured_against_it`,
`The_ledger_learns_what_a_mob_type_costs_and_pays`.
QuestReward type 1 is read as money (cen) from the values (52 cen for the first quests, 10000 for Road to
Promotion); to be confirmed on a live hand-in.

#### Live round 21:25-22:00 (planner deployed)

- Production cast PROVEN live: NextM made Elrue [1] from its dusts, 21:36:24-21:37:04, every cast acked
  PRODUCE_MAKE item 8000 0x0B01. It had been blocked by a bug: produce.hasField only knew fields chosen this session,
  so NextM (who knows Stone Production, skill 29101, from the login skill list) asked to choose it again every
  minute and was refused 0x0B13. A known field skill now counts as held; a refused field parks the recipe for the level.
- REST IS THE TENT. Standing still regenerates nothing on this server (NextA 208/455 HP for 45 s, no HP packet; the
  first `rest` state stood for 2 minutes at a time and froze the bot). Wired the client's home button:
  act.pitchTent() (stop + PITCHTENT_REQ 0x2027 -> PITCHTENT_ACK 0x0A81 or refused 0x0A82 mode / 0x0A83 too near an
  NPC / 0x0A84 map / 0x0A85 no skin), act.foldTent() 0x202A, self.inTent(), the server folding it (0x203D). The
  survival `rest` state pitches it, learns the NPC clearance from 0x0A83 (walks clear first), marks maps / no skin
  from 0x0A84 / 0x0A85 and folds on leaving (attacked, rested, gave up). Live: Rou refused 0x0A83 at 174 from an NPC.
- Planner cost against the 8 ms quest budget: the first picks ran 10-90 ms. Causes found with dbg.us() lap logs
  ("plan cost: <phase> took <ms>") and fixed: one route search per map pair on a cold cache (now one search per source
  map), the grind spot scan (11-12 ms per miss; band candidates now computed once), one full route search per target
  with the danger test (now one search for every map, walk danger cached per mob level). A pick now measures
  accepts 3-5 ms, the rest < 1 ms; the first pick after a restart still runs over once (cold caches).
- Stale upkeep flag: a brain swap in the middle of a sell left bb.bot.upkeepBusy set, so the quest layer re-planned
  every 10 s and never left `plan` (NextA, 21:40-21:51). The upkeep idle state now clears it on entry, and a pick
  while the upkeep has the legs waits without scoring.
- Two bots ending sessions at once collided writing knowledge-npcs.json (IOException): saved under a lock.
- Tent rest PROVEN live: NextA pitched on RouCos03 at 22:01:32 (HP 58 %) and folded at 22:01:56 at 96 % (24 s).
- Storage deposit PROVEN live: NextA at the keeper in Rou, 21:58:04-06, put 7 spare / other-class gear pieces and a
  recipe of a field it does not work into cells 0-7 (RELOC_ACK 0x0241 moved; the items left the bag). Open question:
  every gear piece was logged as moved from bag slot 22, one after another; the bag model and the server agreed (each
  move acked on slot 22's cell change), so the server seems to refill the slot - not understood yet.
- The economy ledger is saved per bot (econ-<bot>.json) at session end and restored at the next session, so rates and
  per-mob costs survive reconnects and host restarts.
- Tick cost on this machine: brain ticks with 0 Lua instructions still took 6-13 ms at times and trivial phases
  2.5 ms, with the host CPU at ~76 % (the stack's containers): part of the remaining budget overruns are the machine,
  not the planner. Steady picks measure 4-6 ms (accepts is the largest part).

#### Live round 22:10-22:50 (stack moved to native Linux builds)

- An indoor map is NOT an instance: the instance flag was MapInfo's InSide column (indoor maps). Echo Cave, an open
  field the quests send us to, read as an instance; after a reconnect both bots sat in the quest layer's "inside an
  instance: wait" for 10 minutes (22:23-22:33), and caves were never grind spots. The job change's dungeon has the
  same columns as Echo Cave (InSide 1, KingdomMap 0), so instance = KingdomMap set, or a scenario running for us
  on the map (any scenario packet). Test: the dungeon is an instance only once its scenario starts.
- A pick is now spread over several ticks (one phase or 5 acceptable quests per tick): right after a reconnect the
  cold caches made one pick exceed the 8 ms budget three times in a row and the runtime suspended NextM's brain
  (22:37). The planner logs `plan cost: <phase> took <ms>` above 3 ms.
- Runtime: a session's brain is built before the agent's first tick (context time 0), so every initial state's
  'after' / 'every' / timeout fired on the first tick. Timers of states entered while building now count from the
  first tick.
- Sell loop: NextM went to a merchant every minute and sold nothing (22:10-22:12): "need to sell" counted the
  materials of our recipes, which the sale keeps. Need and sale now share one test; storage uses it too.
- The economy ledger survived the stack restarts (restored at each reconnect, 22:22 / 22:36).
- 22:47: the zone04 maps (Teva, Cypian, Valtara ...) were removed from bb.bot.closedMaps on both bots; the stack
  runs all five zones as native Linux builds.

#### Efficiency per bot (live, local stack)

| time (UTC) | bot | lv | exp/h | quest exp | money/h | stones/h | deaths/h | chosen activity (score) |
|---|---|---|---|---|---|---|---|---|
| 21:58 (5 min after a restart) | NextA | 13 | 7881 | 500 | 7256 | 0 | 0 | hunt q25 Clam Chowder (7.4 exp/s, 665 over 89 s) |
| 21:58 (5 min after a restart) | NextM | 12 | 0 | 0 | 0 | 0 | 0 | hunt q30 Karl's Mysterious Sword (8.6 exp/s, 673 over 78 s) |

The ledger lives in memory: a host restart starts the hour over (the rates read 0 until the bot earns again).
| 22:52 (after two stack restarts) | NextA | 13 | 2940 | 517 | 6780 | 0 | 0 | accept q65479 (event, 0.9 exp/s) - unmeasured mobs undervalued; fixed: they now take the measured average |
| 22:52 (after two stack restarts) | NextM | 12 | 7585 | 250 | 4004 | 0 | 0 | hunt q416 Despicable Little Ones (4.0 exp/s) |
