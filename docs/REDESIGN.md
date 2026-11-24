# ik-fiesta-bots redesign

The answer to `docs/REVIEW_2026-11.md`. Issue ids (P1, D1, C4, ...) refer to that document's index.

Priorities, in order: **performance** (no tick ever waits on the network; <= ~1 ms per bot per tick),
**correct game rules**, **a brain the operator can see, edit, version and switch from the UI**, and clean
boundaries so that each of those stays true.

---

## 0. Goals, budgets and non-goals

| goal | budget / acceptance |
|---|---|
| tick never blocks on I/O | zero `await`/`Wait` on the tick thread; enforced by an analyzer (section 12.5) |
| per-bot tick cost | p50 <= 0.3 ms, p99 <= 1.0 ms, hard cap 5 ms (Lua instruction budget aborts the tick and reports) |
| allocation | <= 2 KB per tick in steady state (target 0 in the C# path) |
| GC | no gen2 blocking collection during normal play; gen0 pause share < 0.5 % |
| tick rate | 20 Hz per bot by default (combat layers), lower for planning layers; 100 bots on 4 cores |
| decision latency | inbound packet -> brain sees it: <= 1 tick (50 ms) |
| stuck | every state has a failure exit; a bot that makes no progress for its state's budget leaves the state and the UI shows why |
| operability | spawn, stop, start, relog, pause, step, force-state, apply/rollback brain version: all from the UI |

Non-goals: rewriting the login chain, the cipher, the packet structs (FiestaLib-Reloaded) or the damage
formula code; changing the private status of the Lua (brains stay in the private store, never in this
repo); running server-only data at runtime.

---

## 1. Architecture

### 1.1 Layers

```
  +-----------------------------------------------------------------------------------+
  | Host: HTTP API, WebSocket push, UI (SPA), auth, metrics export          Fiesta.Bot.Host
  +-----------------------------------------------------------------------------------+
  | Fleet: BotSupervisor (lifecycle, respawn, checkpoint), BrainRepository,           |
  |        AssignmentLog, KnowledgeStore, Persistence                     Fiesta.Bot.Fleet
  +-----------------------------------------------------------------------------------+
  | Brain runtime: LuaEngine, BrainInstance (layers of HFSMs), Blackboard,            |
  |        DebugTap (state/transition/variable stream)                    Fiesta.Bot.Brain
  +-----------------------------------------------------------------------------------+
  | Query + action library exposed to Lua (source-generated bindings)     Fiesta.Bot.Api
  +-----------------------------------------------------------------------------------+
  | Agent core (tick-owned, single-threaded per bot):                                 |
  |   WorldState (+ SpatialGrid, versions)  ActionSystem (intents, ActionHandle,      |
  |   procedures)  MoveController  CombatController  GameRules         Fiesta.Bot.Agent
  +-----------------------------------------------------------------------------------+
  | Services (shared, thread-safe, read-mostly): ClientData, NavService (mesh, jobs), |
  |   KnowledgeStore (estimators), JobScheduler                           Fiesta.Bot.Services
  +-----------------------------------------------------------------------------------+
  | Protocol: frame codec, opcode table, typed decode into pooled struct events,      |
  |   encode of intents                                                   Fiesta.Bot.Protocol
  +-----------------------------------------------------------------------------------+
  | Transport: sockets, cipher, login/WM/zone links, handoff              Fiesta.Bot.Net
  +-----------------------------------------------------------------------------------+
```

Dependency rule: a layer only depends on layers below it. `Fiesta.Bot.Agent` has no reference to
sockets, HTTP or Lua; it is fed decoded events and emits encoded intents, which is what makes it
replayable (section 12).

### 1.2 What replaces the god classes

| today | becomes |
|---|---|
| `ZoneView` (3,868 lines) | `Protocol.Decoders/*` (one small decoder per packet family, pure functions `ReadOnlySpan<byte> -> struct`) + `Agent.WorldState` components (`SelfState`, `EntityTable`, `Inventory`, `SkillBook`, `QuestLog`, `PartyState`, `TradeState`, `MapState`) + `Agent.Learners/*` (aggro, reach, leash, damage fit) |
| `BotManager` (3,855) | `Fleet.BotSupervisor` (lifecycle), `Net.ZoneLink`/`Net.Handoff` (links), `Agent.Procedures/*` (shop, dialogue, gate hop, dismount, revive, trade), `Agent.MoveController`, `Agent.CombatController` |
| `BotApi` (2,498, 325 members) | `Api/*Module.cs`, one file per helper group (section 4), generated bindings |
| `BotEndpoints` (2,091) | `Host/Endpoints/{Fleet,Bot,Brain,Knowledge,Debug}Endpoints.cs`, DTOs only, no game logic |
| `watch.html` (1,743) | `ui/` SPA (section 9) |
| `BotScriptRunner` + `BehaviorGraphRunner` | `Brain.BrainInstance` (one runtime for scripts and graphs) |

---

## 2. Threading model

### 2.1 Threads

| thread / pool | owns | never does |
|---|---|---|
| **I/O** (async sockets, `System.IO.Pipelines`) | read, decrypt, frame, decode into `InboundEvent` structs, push to the bot's inbound ring; drain the bot's outbound ring, encode, encrypt, send | touch WorldState, call Lua |
| **Tick workers** (`K = cores - 1`, pinned) | a fixed partition of bots; runs each bot's tick to completion | await anything, block, allocate in steady state |
| **Jobs** (`JobScheduler`, bounded worker pool) | pathfinding, quest planning, route costing, big scans | mutate WorldState; results come back as events |
| **Host** (ASP.NET) | HTTP, WebSocket, UI | read live state (only published snapshots) |

One bot = one `BotAgent` object owned by exactly one tick worker. Nothing else writes its state.

### 2.2 Queues

- **Inbound**: per bot, a bounded single-producer/single-consumer ring (`InboundRing`, capacity 4,096)
  of `InboundEvent` (a 64-byte struct: opcode, kind, timestamp, and either inline fields or a pooled
  buffer slice for variable payloads). The I/O thread is the only producer, the tick worker the only
  consumer. Overflow is a CRIT metric and drops the oldest *movement* events first, never results.
- **Outbound**: per bot, an `OutboundRing` of `Intent` structs (opcode + up to 32 inline bytes or a
  pooled buffer). The tick enqueues; the connection's writer loop dequeues, encodes, sends. Enqueue is
  O(1) and never waits. If the link is down the intent is rejected immediately with `LinkDown` and the
  action's handle resolves to `Failed(LinkDown)` on the next tick.
- **Jobs**: `JobScheduler.Submit(in JobRequest) -> JobTicket`; completion posts a `JobCompleted` event
  into the bot's inbound ring (a second producer lane, `MpscLane`, so the SPSC property of the socket lane
  holds).
- **Control**: operator commands (pause, step, force state, apply version, stop) go through a small
  `ControlQueue` drained at the start of the tick. No HTTP thread ever touches a bot directly.

### 2.3 The tick

```
BotAgent.Tick(now)                                   // now = tick-start monotonic ms, injected (IClock)
  1. drain ControlQueue                              // operator commands
  2. drain InboundRing -> WorldState.Apply(evt)      // pure C#, deterministic, raises BrainEvents
  3. ActionSystem.Advance(now)                       // match results to pending handles, time out stale ones
  4. MoveController.Advance(now)                     // stream MOVERUN legs at client pace, handle MOVEFAIL
  5. CombatController.Advance(now)                   // bash restart after moves, mode, cast queue
  6. Brain.Tick(now)                                 // layers -> transitions -> state ticks (Lua)
  7. flush intents to OutboundRing                   // already enqueued; nothing to wait for
  8. publish snapshot if due (every 100 ms)          // double-buffered, immutable for readers
  9. record TickStats                                // per-phase ticks, alloc, Lua instructions
```

Steps 2 to 5 are C# and bounded by the number of events; step 6 is bounded by the Lua budget.

### 2.4 How <= 1 ms is achieved

1. **No waiting.** Every action is an intent plus a handle (section 3). Multi-packet flows are procedures
   stepped once per tick.
2. **No marshalling.** Lua calls go through generated bindings that pass integers, numbers and booleans on
   the Lua stack. Lists are exposed as iterators or index accessors, never as freshly built tables
   (section 5.3). Measured target: <= 100 ns per crossing (vs ~10 KB of garbage per crossing today).
3. **Answers are precomputed in C#.** Spatial queries hit a uniform grid (`SpatialGrid`, 200 u cells,
   per-map, per-bot view), cost O(entities in cells touched). Scores that Lua used to compute by looping
   (threat, pack density, route cost) are C# helpers over cached data (section 4).
4. **Versions instead of memo lists.** Every WorldState component carries a `Version` counter that
   increments on change. C# helpers cache by `(component, version)`; Lua can do the same with
   `world.version("quests")`. There is no hand-kept list of what may be cached.
5. **Layers run at their own rate.** The combat layer runs every tick; goal/errand layers every 250 to
   1,000 ms; their expensive planning goes to jobs whose results arrive as events.
6. **Zero-allocation C# path.** Pooled buffers (`ArrayPool`), struct events, `Span`-based decoders, no
   LINQ, no closures, no `DateTime.UtcNow` (one clock read per tick).
7. **Budget enforcement.** The Lua engine's instruction-count hook fires every 10,000 instructions; a
   state tick over its budget (default 2 ms) is aborted with `BudgetExceeded`, the bot stays in its state,
   and the UI shows which function blew it. A bot over budget three ticks in a row is paused, not left to
   stall its worker's partition.

### 2.5 How it is measured

- `TickStats` per tick: wall time per step (2 to 9), events applied, intents emitted, Lua instructions,
  allocated bytes (`GC.GetAllocatedBytesForCurrentThread` delta). Recorded into an HDR histogram per bot
  per step (no 500 ms batch averaging), exported as Prometheus metrics and streamed to the UI.
- `GET /api/bots/{id}/tick` returns the last 1,000 ticks' step breakdown; the UI shows a flame strip.
- CI runs the benchmark suite (section 12.3); a regression beyond budget fails the build.

---

## 3. Actions: intents, handles, procedures

### 3.1 ActionHandle

```csharp
public readonly record struct ActionId(int Value);
public enum ActionState : byte { Queued, Sent, Acked, Refused, TimedOut, Cancelled, Failed }

public struct ActionRecord            // stored in ActionSystem's fixed-size table, indexed by ActionId
{
    public ActionKind Kind; public ActionState State; public ushort RefusalCode;
    public long SentAt, ResolvedAt; public int Arg0, Arg1; public ResultMatcher Matcher;
}
```

Every action (cast, bash, use item, buy, sell, click NPC, gate, revive, trade step) returns an
`ActionId` at once. `ActionSystem.Apply(InboundEvent)` resolves it from the matching result packet
(cast result / cast fail code, item use ack / fail, buy/sell ack, menu open, map link...). Each kind
declares its matcher and timeout in one table (`ActionCatalog`), which is also where "wire an action =
wire its result" becomes mechanical: an action kind without a matcher does not compile.

Lua sees:

```lua
local a = act.cast(skill, target)         -- returns an action id immediately
if act.state(a) == "refused" then log(act.reasonText(a)) end
-- or, in a transition: { on = "action", action = "lastCast", result = "refused" }
```

`CastAsync`'s "return Sent on a refusal" (`BotManager.cs:485, 501, 512`) disappears: a locally refused
cast resolves to `Refused(LocalReason)` with a reason enum, distinct from a server refusal code.

### 3.2 Procedures

A procedure is a small C# state machine for a multi-packet interaction, stepped by the tick:

| procedure | steps | replaces |
|---|---|---|
| `OpenShopProcedure` | END_OF_TRADE -> NPCCLICK -> menu ack / `[MENU]` handling -> shop list | `OpenShopAsync` re-click loop (23 s blocking) |
| `QuestDialogueProcedure` | page-by-page per QuestDialog tags (`[NEXT]`, `[MENU]`, `[BUTTON]`, `[SHOW_REWARD]`) | `DriveQuestDialogueAsync` |
| `GateHopProcedure` | dismount -> walk into gate range -> click -> answer menu -> await link | `UseGateAsync` + `EnsureDismountedAsync` (350 ms sleep) |
| `ReviveProcedure`, `TradeProcedure`, `TentProcedure`, `StorageProcedure` | as named | Lua crutches |

Each step has a timeout and a declared failure (`ProcedureResult.Failed(reason)`), surfaced to the brain
as an event and to the UI as a row in the action log.

### 3.3 MoveController and CombatController

`MoveController` owns all MOVERUN/STOP traffic. It accepts a `MoveOrder` (path handle or point), streams
legs at the client's pace, and encodes the movement rules from REVIEW 5.4/5.5:

- keeps our believed position within 200 u of the last server-confirmed position; on MOVEFAIL it adopts
  the carried position as truth, classifies the refusal (`Desync` if our `from` was > 200 u off,
  `BlockedSegment` otherwise) and only the second kind becomes a learned block, **in this bot's overlay**;
- validates each leg with `GameRules.MoveSegmentAccepted` (two consecutive fully blocked samples) rather
  than the exact supercover test;
- tells `CombatController` that a move happened, which re-arms BASHSTART and drops a casting skill unless
  it is a moving skill.

`CombatController` owns battle mode, targeting, BASHSTART/BASHSTOP and the cast queue, with reach checks
from `GameRules` (no learned melee scalar).

### 3.4 GameRules

A static, data-driven class with unit tests and a live acceptance probe per rule:

```csharp
public static class GameRules
{
    public const double PlayerBody = 50;
    public static double MobBody(in MobInfoRow m) => m.AbsoluteSize * 50.0 / 1000.0;
    public static double BashReach(double targetBody, WeaponType w)
        => (targetBody + (w is WeaponType.Bow or WeaponType.Crossbow ? 600 : 120)) * 1.25;
    public static double SkillReach(in ActiveSkillRow s, double targetBody)
    {
        double r = s.Range + Math.Max(50, s.Range * 0.10) + targetBody;
        return Math.Min(r * 1.25, r + 60);
    }
    public const double MoveFromTolerance = 200;
    public static bool MoveTargetAccepted(BlockLayer b, double x, double y);       // 3x3 rule
    public static bool MoveSegmentAccepted(BlockLayer b, Point from, Point to);    // two-sample rule
    // leash: a chasing mob gives up beyond FollowCha from the point where it was last damaged (learned per mob)
}
```

`BlockLayer` is the `.shbd` OR-ed with `.shab` where present, with door overlays.

---

## 4. The C# helper library (what Lua calls)

Lua stays glue and logic. Everything that loops over entities, items, quests or the map is a C# helper
backed by a structure that answers it in O(small).

### 4.1 Binding mechanism

```csharp
[LuaModule("mobs")]
public sealed partial class MobsModule
{
    [LuaFn] public int Count(double radius) => _q.CountWithin(_self.Pos, radius, EntityFilter.HuntableMob);
    [LuaFn] public int Nearest(EntityFilter f = EntityFilter.HuntableMob) => ...;   // handle or -1
    [LuaFn] public double Hp(int handle) => ...;
}
```

A Roslyn source generator emits, per `[LuaFn]`, a static `int Fn(IntPtr L)` that reads arguments with
`lua_tointegerx`/`lua_tonumberx` and pushes results, with no reflection and no boxing. It also emits an
**API manifest** (`api-manifest.json`: names, parameters, types, docs, version) used by the Lua editor
for completion and by the linter. The manifest version is part of every brain version (section 7), so a
brain declares which API it was written against.

Return conventions: integers for handles and ids, `-1` (or `nil` where Lua idiom fits) for "none" because
0 is a valid id; numbers for distances/percentages; booleans for predicates; lists only as iterators
(`for h in mobs.near(400) do ... end`, the iterator closure is cached per call site) or as
`fill(tbl)` into a caller-owned reusable table.

### 4.2 Modules

| module | examples | data structure behind it |
|---|---|---|
| `self` | `hp() hpPct() sp() level() pos() map() moving() casting() mounted() dead() money() bagFree()` | `SelfState` fields, O(1) |
| `mobs` / `ents` | `near(r) nearest(filter) count(r) hp(h) pos(h) level(h) mobId(h) facing(h) speed(h) isAggro(h) targetOf(h) dist(h) routeDist(h)` | `EntityTable` (struct-of-arrays, handle -> slot map) + `SpatialGrid` (200 u cells) |
| `threat` | `onUs() incomingDps() packAround(h, r) burstIfEngaged(h) safeSpot(r) pullCost(h) wouldWake(h)` | aggro shapes from `KnowledgeStore`, per-tick cached threat map over the grid |
| `combat` | `bashReach(h) skillReach(skill, h) inReach(skill, h) canBash(h) los(h)` | `GameRules`, `BlockLayer` (with `.shab`) |
| `skills` | `has(id) ready(id) readyIn(id) cost(id) castTime(id) cooldown(id) isMoving(id) best(tag, h) family(id) rank(id) list(tag)` | `SkillBook` + client `ActiveSkill` rows, cooldown groups; tags precomputed at load (`heal`, `aoe`, `stun`, `buff`, `damage`, `selfOnly`) |
| `act` | `cast(skill, h) castAt(skill, x, y) bash(h) stopBash() use(slot) moveTo(x, y) stop() target(h) state(a) reason(a) cancel(a)` | `ActionSystem`, `MoveController`, `CombatController` |
| `items` | `count(itemId) slots(filter) potions() bestPotion(deficit) junk() sellValue() equipUpgrade()` | `Inventory` with per-item-class indexes |
| `quests` | `active() ready(q) progress(q, i) objectives(q) candidates() best(policy) name(q)` | `QuestLog` + client QuestData; candidate scoring as a job, result cached by `(quests.version, level, map)` |
| `nav` | `path(x, y) -> ticket` `pathState(t)` `routeLength(x, y)` `reachable(x, y)` `snap(x, y)` `nearestWalkable(x, y)` `gateTo(map)` `travel(map) -> action` | `NavService` (section 6) |
| `npc` | `find(role) nearestOfRole(role) shop(npc) openShop(npc) -> action` | learned NPC roles from menus (client-legal) |
| `social` | `party.* trade.* chat.* whisper.*` | `PartyState`, `TradeState` |
| `know` | `detect(mobId) leash(mobId) mobReach(mobId) mobInterval(mobId) assist(family) confidence(...)` | `KnowledgeStore` estimators |
| `time` | `now() since(t) every(key, ms)` | tick clock |
| `ev` | `fired(kind) last(kind) count(kind, ms)` | per-tick event bitset + small per-kind ring |
| `bb` | `get(k) set(k, v)` scoped bot / layer / state | Blackboard (section 5.4) |
| `dbg` | `log(...) logi(...) watch(name, value) mark(section)` | DebugTap (section 9.3) |

### 4.3 What moves from Lua into C#

Measured hot spots and the helper that replaces each: pack/threat scoring loops in the target picker ->
`threat.pullCost(h)`; potion choice per slot -> `items.bestPotion(deficit)`; quest board scoring every
full pass -> `quests.best(policy)` as a job; corridor risk -> `nav.routeRisk(x, y)`; tent placement ->
`nav.clearSpot(minFromNpc, r)` (a snapped, reachable spot, the fix for D1's 15-day loop); kite step
geometry -> `move.kiteStep(h, gap)`.

---

## 5. The brain

### 5.1 Concepts

- **Brain** = a versioned bundle: a definition (layers, machines, states, transitions) + Lua modules.
- **Layer** = an independent hierarchical state machine with a priority and a tick rate. All layers tick;
  a higher-priority layer can **claim resources** (`Movement`, `Combat`, `Cast`, `Npc`, `Inventory`,
  `Chat`). An action whose resource is claimed by a higher layer is rejected locally with
  `Preempted(layer)`, visible in the debugger.
- **Machine** = states + transitions; a state may contain a sub-machine (hierarchical).
- **State** = `enter`, `tick`, `exit` Lua functions (any may be omitted), a `budget` (max time in
  state without progress), a `progress` expression, and transitions.
- **Transition** = `to`, `priority`, a condition, optional `hold` (condition must stay true for N ms:
  hysteresis), optional `cooldown`, optional `reason` text template.

### 5.2 Conditions

| kind | form | evaluated |
|---|---|---|
| Lua function | `when = "lowHp"` (a function in the brain's modules) or inline `when = "self.hpPct() < 35"` | Lua, compiled once to a function |
| event | `on = "damaged"`, `on = "mapChanged"`, `on = "action", action = "lastCast", result = "refused"` | C# bitset test, no Lua call |
| timer | `after = 30000` (time in state), `every = 5000` | C# |
| value compare | `value = "self.hpPct", op = "<", than = 35` | C# against a registered sensor, no Lua call |
| procedure | `procedure = "openShop", result = "failed"` | C# |
| stuck | `stuck = true` (fires when the state's `progress` has not improved for `budget`) | C# |
| always | `always = true` (default fallthrough) | - |
| composite | `all = [...]`, `any = [...]`, `not = {...}` | C# over the above, Lua only for Lua leaves |

Fixed conditions cost nothing in Lua. Each evaluation result is recorded for the debugger (section 9.3).

### 5.3 Execution semantics (per tick)

```
for layer in layers by priority (high first), if layer due by its rate:
    m = layer.active machine path (root -> leaf)
    for state on path from root to leaf:                     // outer states first
        t = first transition (by priority) whose condition holds (with hold/cooldown)
        if t: exit states below+including state, enter t.to (and its initial sub-states), record, break
    if no transition fired: tick leaf state (Lua, within budget)
claims: a layer's active state lists the resources it claims; claims are recomputed after transitions
```

Rules that fix D1/D2 by construction:

- **Every state must have a way out.** The linter rejects a state without a `stuck`/`after`
  transition unless it is marked `terminal`. Default `budget` 60 s.
- **Commitment.** `minDwell` per state (default 0) and `hold` per transition stop two states from
  flapping (the xpgrind/farm thrash).
- **Reasons.** Every transition records `(from, to, condition kind, reason text, tick, values read)`.

### 5.4 Blackboard

Variables live in the blackboard, not in Lua globals: `bot` scope (persists across brain versions and
relogs, checkpointed), `layer` scope, `state` scope (cleared on exit). Values are numbers, strings,
booleans or small tables. The debugger shows them live; the checkpoint makes a respawn resume where it
was (section 10).

### 5.5 The retained layer model

The project plan's layers (Layer 0 manual actions, Layer 1 one Lua loop, Layer 2 state machines,
Layer 3 cross-tree transitions and per-class trees) carry over:

| plan layer | in the redesign |
|---|---|
| Layer 0: manual HTTP actions | the `Operator` layer: highest priority, empty unless the operator issues commands; any manual action claims its resource and pauses lower layers' use of it until released |
| Layer 1: a Lua loop | a single-state machine (`tick` only); old scripts run unchanged in a compatibility layer (section 11) |
| Layer 2: state machines | machines with transitions as above |
| Layer 3: cross-tree + per-class trees | brains compose machines by reference (`use = "combat/archer"`), with `select` by class or level; a transition may target a state in another machine of the same layer |

On top of these, **concurrent layers** (Operator, Survival, Combat, Errand/Goal, Background) are how
"don't stand in a fire while shopping" is expressed without a priority cascade.

### 5.6 Example brain definition

Stored as YAML in the private brain store (JSON accepted). Names are illustrative.

```yaml
brain: leveller
api: 3                         # API manifest version it was written for
modules: [lib/core, lib/combat_tags, user/ikaron]   # Lua modules, versioned with the brain
layers:
  - name: survival
    priority: 90
    rate: tick
    claims_when_active: [Movement, Combat, Cast]
    machine:
      initial: ok
      states:
        ok:
          transitions:
            - { to: recover, value: self.hpPct, op: "<", than: 35, hold: 200, reason: "hp {self.hpPct}%" }
            - { to: flee, when: "outmatched()", hold: 300 }
        recover:
          claims: [Cast]
          tick: survival.recoverTick       # heal skill > potion > stone, all via helpers
          budget: 15000
          progress: self.hpPct
          transitions:
            - { to: ok, value: self.hpPct, op: ">=", than: 70 }
            - { to: flee, stuck: true }
        flee:
          claims: [Movement, Combat, Cast]
          enter: survival.fleeEnter        # picks nav.safeSpot via threat helpers
          tick: survival.fleeTick
          budget: 20000
          progress: "-threat.incomingDps()"
          transitions:
            - { to: ok, all: [ { on: noAggro }, { after: 2000 } ] }
            - { to: recover, stuck: true }

  - name: combat
    priority: 60
    rate: tick
    machine:
      initial: idle
      states:
        idle:
          transitions:
            - { to: engage, on: aggressorAppeared }
            - { to: engage, when: "bb.get('wantKill') ~= nil" }
        engage:
          use: combat/${self.classFamily}     # per-class sub-machine (Layer 3)
          budget: 45000
          progress: "-mobs.hp(bb.get('target'))"
          transitions:
            - { to: idle, on: targetDied }
            - { to: idle, stuck: true, reason: "no damage for 45 s" }

  - name: errand
    priority: 30
    rate: 500
    machine:
      initial: choose
      states:
        choose:
          enter: errand.choose             # submits quests.best as a job
          transitions:
            - { to: hunt,  on: job, job: questPick, result: "hunt" }
            - { to: town,  value: self.bagFree, op: "<=", than: 2 }
        hunt:   { use: errand/hunt,  budget: 600000, progress: quests.progressSum,
                  transitions: [ { to: choose, stuck: true }, { to: town, value: self.bagFree, op: "<=", than: 0 } ] }
        town:   { use: errand/town,  budget: 300000, progress: errand.townProgress,
                  transitions: [ { to: choose, on: procedureDone, procedure: townRun }, { to: choose, stuck: true } ] }
```

### 5.7 State functions and user helpers

```lua
-- user/ikaron.lua  (a user library: shared helpers any state may call)
local M = {}

function M.isHealSkill(skill)
  return skills.hasTag(skill, "heal") and not skills.hasTag(skill, "partyOnly")
end

function M.bestHeal()
  for s in skills.list("heal") do                 -- iterator, no table built
    if skills.ready(s) and M.isHealSkill(s) then return s end
  end
  return -1
end

return M
```

```lua
-- survival.lua (module for the survival layer)
local my = require "user/ikaron"
local S = {}

function S.recoverTick()
  local s = my.bestHeal()
  if s >= 0 and canUse(s) and my.isHealSkill(s) then   -- canUse = core helper: ready, SP, not moving
    act.cast(s, self.handle())
    return
  end
  local p = items.bestPotion(self.maxHp() - self.hp())
  if p >= 0 then act.use(p) end
end

return S
```

`canUse(skill) and isHealSkill(skill)` works as written: `canUse` comes from `lib/core` (a default
library shipped with the host, versioned with the API), `isHealSkill` from the user's library.

---

## 6. Navigation

- **NavService** per map: an immutable `NavMesh` (rectangles + portals, built from `.shbd` OR `.shab`,
  door regions as today) shared by all bots, plus a **per-bot `NavOverlay`** (learned blocks with TTL,
  door states observed by that bot). Learned blocks are promoted to the shared map only after two bots
  confirm the same cell with `BlockedSegment` refusals.
- **Path search** ported from fiesta-godot (`docs/NAVMESH_PATH_IMPROVEMENT.md`): corridor A* with
  portal-crossing costs and a goal heuristic, funnel through portals, corner nudge with an exact leg
  check against `GameRules.MoveSegmentAccepted`, final corner drop, region centres removed. Door gating
  in the expansion stays.
- **Goal snapping** as the official client does it: if the target tile is blocked, ring-search the
  nearest walkable tile (closest to the start), then path to it. `nav.snap` exposes it to Lua.
- **Jobs.** `nav.path(x, y)` returns a ticket; the search runs on the job pool; the result is a pooled
  waypoint array delivered as an event. Typical cost 0.25 to 2.4 ms per path (godot measurements), off
  the tick. `routeLength` stays synchronous but is cached per `(map, from cell, to cell)`.
- **Validation.** The existing 3,387-path supercover audit becomes a test that also runs the
  acceptance-rule validator; the godot `path_quality` metric (share of paths over 1.15x the shortest)
  becomes a CI benchmark with budget 2 %.

---

## 7. Brain and script versioning

- **Content-addressed versions.** A `BrainVersion` is the hash of the definition plus every module's
  source plus the API manifest version. Versions are immutable.
- **Store.** `BrainRepository` keeps versions in the database and mirrors them to the private Lua git
  repo (`ik-fiesta-bots-lua`) as commits, so the git history and the runtime history agree. Nothing is
  written to this public repo.
- **Channels.** Named pointers (`stable`, `candidate`, `operator-test`) to versions; a bot is assigned a
  version or a channel.
- **Assignment log.** Every apply records `(bot, version, previous version, who, when, why, checkpoint
  kept?)`; the UI shows a timeline and one-click rollback.
- **Hot reload.** Applying a version to a running bot swaps at a tick boundary: the new definition is
  validated and compiled first; layers whose machine is unchanged keep their active state; states that
  still exist by name keep it; otherwise the layer restarts at its initial state. The `bot` blackboard is
  always kept.
- **User libraries** are versioned the same way, namespaced (`lib/*` host-provided, `user/<name>/*`
  user-provided, `brain/<brain>/*` private to one brain). A brain pins library versions; editing a
  library creates a new version and the editor offers "update brains that use it".
- **A/B.** Two channels on two bots of the same class, compared on the metrics in section 9.2.

---

## 8. Persistence

| data | store | notes |
|---|---|---|
| fleet config (accounts, characters, assigned version, autostart) | Postgres (cluster `pg-rw`) or SQLite locally, via one `IFleetStore` | replaces roster JSON |
| brain versions, libraries, channels, assignment log | same database + mirror to private git | |
| blackboard checkpoints | same database, every 10 s and on stop | respawn resumes |
| knowledge (estimators per mob id, NPC roles, shop stock, map rosters) | same database | replaces `NpcKnowledge` files |
| logs | per-bot rolling files + an in-memory ring; survive relog | today a relog empties the ring |
| packet logs | per-bot rolling files, setting persisted with the bot | today lost on respawn |
| tick stats, transitions | ring in memory, downsampled to the database | |

---

## 9. UI

A single-page app in `ui/` (TypeScript, Vite, React, React Flow for graphs, Monaco for Lua), served by
the host. State arrives over one WebSocket per open view with compact deltas at 5 to 10 Hz.

### 9.1 Pages

- **Fleet**: every bot as a row: class/level/map, active state per layer with time in state (with
  date), last transition and reason, ticks/s and p99, deaths/h, exp/h, version and channel, errors.
  Controls: spawn, start, stop, relog, pause brain, apply version.
- **Bot**: header with live vitals; tabs for brain debugger, sensors, actions, log, packets, inventory,
  quests, skills, metrics.
- **Brain editor**: graph canvas per layer (states as nodes, transitions as edges labelled with their
  condition and priority), side panel to edit a state's functions in Monaco, transition editor with the
  condition kinds as forms (event picker, value picker from the sensor registry, Lua expression),
  validation results from the linter, "save as new version", "apply to bot/channel".
- **Libraries**: module list with versions, Monaco editor with completion from the API manifest and the
  user's own libraries, "find usages" across brains.
- **Versions**: per brain, the version list with diffs (definition diff + Lua diffs), channels,
  assignment timeline, rollback.

### 9.2 Comparing versions

Per version and per class: exp/h, deaths/h, quest completions/h, share of time per state, stuck exits/h,
tick p99. These are what "is the new brain better" is decided on.

### 9.3 Visual debugging

- **Live graph**: the active state of every layer highlighted, with time in state; edges flash when
  taken; claimed resources shown per layer.
- **Transition trace**: the last 200 transitions with reason, values read and the tick; click to jump the
  timeline.
- **Condition panel**: for the active state, every outgoing transition with its last evaluation (true /
  false / not evaluated, value vs threshold, hold timer progress).
- **Variables**: blackboard scopes and `dbg.watch` values, live, with sparkline history.
- **Sensors**: the nearby-mob map (existing canvas, kept): entities, aggro shapes (detect circle offset
  by facing), our bash and skill reach rings, leash origins, the current path with waypoints, the nav
  mesh overlay, family-assist radii, threat heat map.
- **Actions**: pending and resolved action handles with result codes and latency; procedures with their
  current step.
- **Pause / step**: pause a bot's brain (C# keeps the link alive, combat controller safe-stops), step one
  tick, force a state, edit a blackboard value.
- **Replay**: any recorded session (section 12.1) can be loaded into the same views and scrubbed.

---

## 10. Lifecycle and supervision

- `BotSupervisor` owns spawn, login, handoff, reconnect, respawn and checkpoint/resume, as an explicit
  state machine visible in the UI (`Offline -> Login -> World -> Zone -> Handoff -> ...`).
- **Graceful restarts.** A host deploy drains: every bot checkpoints its blackboard and active states,
  logs out cleanly (fleeing first when in combat), and the new host resumes from the checkpoint with the
  same brain version. Bot logs and packet logs are files and survive.
- **Health.** A bot is unhealthy when: no tick for 2 s, tick p99 over budget for 30 s, a layer in the
  same state over its budget, no exp and no position change for 10 min outside town, or repeated
  transition cycles (A->B->A more than 20 times in 60 s). Each is a CRIT with the state path, not a guess
  at a cause.

---

## 11. Lua engine

- **Decision: native Lua 5.4 through KeraLua** (the raw C API binding), behind an `ILuaEngine` interface,
  with MoonSharp kept as a second implementation for the compatibility layer during migration. The
  project plan chose MoonSharp for "no native dependencies under Wine/Docker"; the host now runs as a
  Linux container on the cluster, where KeraLua ships `linux-x64` native libraries. The choice is gated
  by the Phase 0 spike: if the KeraLua build does not meet the crossing budget (<= 100 ns, 0 bytes) in
  the container, the bindings stay on MoonSharp with the same generated, table-free API.
- One `lua_State` per bot, created from a shared precompiled chunk cache (modules compiled once per
  version). Sandbox: no `io`, `os.execute`, `require` outside the brain's module map, `load` disabled.
  Memory cap per VM (custom allocator); instruction-count hook for the budget.
- **Compatibility layer.** The current `bot.*` API is reimplemented as a Lua shim over the new modules
  (`bot.nearbyMobs()` becomes a table built from `mobs.near`), so `level_quest.lua` can run as a
  single-state brain on the new runtime in Phase 2 and be split up incrementally.
- **Linting.** On save: syntax, unknown API names (from the manifest), globals written outside the
  blackboard, forward-reference/upvalue-nil cases (`ScriptForwardRefTests` generalised), states without
  exits, unreachable states, unknown resources in claims.

---

## 12. Testing

### 12.1 Deterministic replay

`BotAgent` is a pure function of `(inbound events, control commands, clock, job results)`. A recorder
writes those to a session file (`.botrec`: header with versions, then length-prefixed events). Replay
feeds them back with the recorded clock and asserts the same intents and transitions. Uses:
regression tests from real failures (the 15-day tent loop becomes a fixture), the UI's scrubbable
replay, and benchmarking real workloads.

### 12.2 Simulation

An `ISessionSource` adapter runs agents against the combat simulator (`ik-fiesta-emu-zone`,
`CombatScriptBench`) instead of a socket. Brains are benched per class on fixed seeds for kills/min,
deaths, low-water HP and damage taken; the bench is how kiting and pulling changes are accepted.

### 12.3 Performance tests

- BenchmarkDotNet micro-benchmarks: decode of the 20 most frequent packets, `WorldState.Apply`, spatial
  queries, each `[LuaFn]` crossing, path search on five maps.
- Macro benchmark: 100 agents replaying recorded sessions on 4 worker threads; asserts tick p99 <= 1 ms,
  allocation <= 2 KB/tick, no gen2.
- Budgets live in `perf-budgets.json`; CI fails on regression.

### 12.4 Rule tests

`GameRules` unit tests from client data, plus a live **acceptance probe** suite (a brain per rule, run on
a test character on request from the UI) for REVIEW 5.1 to 5.9. Learned estimators are checked offline
against the server tables (REVIEW section 6) by a test that reports bias and coverage.

### 12.5 Analyzers

A Roslyn analyzer bans `Task.Wait`, `.Result`, `GetAwaiter().GetResult()`, `Thread.Sleep`, `await` and
`DateTime.UtcNow` in `Fiesta.Bot.Agent`, `Fiesta.Bot.Api` and `Fiesta.Bot.Brain`.

---

## 13. Migration plan

Effort in engineer-days (one focused engineer; parallelisable where noted). Each phase ends with the
bots running live.

| phase | content | keeps / rewrites | effort | risk |
|---|---|---|---|---|
| **0. Spikes and guards** (1 week) | KeraLua vs MoonSharp crossing benchmark in the container; replay recorder on the current host (inbound frames + clock) so later phases have fixtures; tick-step stats without batch averaging; fix the phase visit date display | keeps everything | 4-6 | low |
| **1. Immediate correctness** (1 week, can ship on the current code) | port the godot path search + goal snapping; load `.shab`; `GameRules` for bash/skill reach replacing the learned scalar; leash from last-hit point; MOVEFAIL desync vs blocked classification and per-bot overlays; `Refused` instead of `Sent` for local refusals | rewrites `NavMeshPath.Find`, parts of `ZoneView` and `CastAsync` | 6-8 | medium: live behaviour changes; A/B on two bots |
| **2. Agent core** (3 weeks) | `Protocol` decoders split out of `ZoneView`; `WorldState` components with versions; `ActionSystem` + catalog; procedures for shop, dialogue, gate, dismount, revive, trade, tent, storage; `MoveController`, `CombatController`; tick workers + inbound/outbound rings; `BotApi` reimplemented on top (sync calls become intents; the Lua's `bool` contract kept by resolving "sent") | rewrites `ZoneView`, `BotManager` action code; keeps Net, login, cipher, FiestaLib, damage formula, ClientData | 15-20 | high: biggest surface; mitigated by replay fixtures from Phase 0 and running old and new decoders side by side, diffing WorldState |
| **3. Lua engine + helper library** (2 weeks) | generated bindings, modules from section 4, compatibility shim, level_quest running unchanged on the new runtime | rewrites `BotScriptRunner`, `BotApi` surface | 8-12 | medium: Lua semantics differences between MoonSharp and Lua 5.4 (integer division, `#` on tables, `select('#')`); covered by running the leveller in replay |
| **4. Brain runtime** (2 weeks) | layers, HFSM, conditions, claims, blackboard, budgets, stuck exits, linter, versions, channels, assignment log, hot reload, checkpoints | replaces `BehaviorGraphRunner`, `GraphStore`, `ScriptStore` | 10-12 | medium |
| **5. UI** (3 weeks, parallel with 4 once APIs exist) | fleet page with controls, bot page, debugger, sensors (port of existing canvases), brain editor, Monaco + manifest, versions/diff | replaces `watch.html` | 12-15 | low-medium |
| **6. Split up the leveller** (3-4 weeks, incremental) | move survival, combat per class, errand (quests, town, storage, tent, travel) out of `level_quest.lua` into layered machines one capability at a time, each A/B'd on one bot against the monolith on the bench and live | rewrites the Lua, private repo | 15-20 | medium: behaviour regressions; each step reversible by channel |
| **7. Knowledge estimators** (1-2 weeks) | interval estimators, shared store, offline validation report, family assist, mob reach/interval, damage fit | rewrites learners | 6-8 | low |
| **8. Hardening** (1 week) | perf CI budgets, analyzers, health rules, graceful deploy drain | | 4-6 | low |

Total ~80 to 105 engineer-days. Order rationale: Phase 1 fixes the S1 correctness items now, on the
current code, because they do not depend on the new architecture. Phase 0's recorder makes Phase 2
safe. The leveller is split up last, by the people who use the debugger to do it.

What is kept as is: `Fiesta.Bot/Net` (cipher, connection framing), `Login/` chain, `Zone/ZoneEntry` and
the checksum logic, FiestaLib-Reloaded, `ClientData` parsers, `Combat/DamageCalculator`, `NavMesh`
region build (rectangles, portals, doors), minimap and icon rendering, the knowledge already learned
(migrated into the new store), the operator-facing log conventions (`CRIT` first).

---

## 14. Decisions

1. **Tick-owned state, I/O off the tick, intents with handles.** The only way to guarantee "never waits
   on the network" is that the tick has nothing to wait on.
2. **C# computes, Lua decides.** Any loop over game objects is a helper; Lua code is conditions, choices
   and sequencing.
3. **Layers of hierarchical state machines with declared exits**, rather than behaviour trees: the
   operator asked for nodes with transitions and per-transition conditions, and explicit exits are what
   make stuck states impossible to write silently.
4. **Fixed conditions are first-class** so that most transitions never enter Lua.
5. **Versions are content-addressed and every apply is logged**, so "what was running when it died" is
   answerable.
6. **Game rules live in one tested place** (`GameRules`) and are confirmed by live probes, not learned
   when they are exact.
7. **Server-only facts are learned with explicit estimators** and validated offline, never read at
   runtime.

## 15. Open questions for the operator

- Database: cluster Postgres (`pg-rw`) for the fleet store, or SQLite on the bot pod's volume?
- Is a Lua 5.4 switch acceptable if the spike shows it, given MoonSharp's dialect quirks the leveller
  already depends on?
- UI auth: keep the bearer token, or move to the same login as the other web tools?
- Which bots may run candidate brains (A/B) on the live server?
