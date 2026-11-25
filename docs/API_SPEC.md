# ik-fiesta-bots API specification (redesign)

The full API of the redesigned bot runtime: the Lua helper library, the brain definition format, the
user library contract, actions and results, events, the debug/telemetry model, and the host's HTTP and
WebSocket API for the UI. Architecture and rationale: `docs/REDESIGN.md`. Type and file layout:
`docs/SKELETON.md`. Engine choice: `docs/LUA_BENCH.md`.

API version: **3** (the brain format's `api:` field; version 1/2 are today's `bot.*` surface, kept as a
compatibility shim, section 11).

---

## 1. Conventions

### 1.1 Types

| Lua type in this spec | meaning |
|---|---|
| `handle` | integer zone handle of an entity; `-1` = none. Handles are per map and reused after a map change. |
| `id` | integer game id (mob, item, skill, quest, npc, map). **0 is a valid id**; absence is `-1`. |
| `slot` | integer inventory slot (flat, `0..`); `-1` = none |
| `x, y` | numbers, world units (1 tile = 6.25 u) |
| `ms` | integer milliseconds |
| `pct` | number 0..100 |
| `mask` | integer bitmask built from the module's constants (`ents.MOB + ents.HUNTABLE`) |
| `action` | integer action id (section 4); `-1` = rejected before queueing |
| `ticket` | integer job ticket (section 4.4) |
| `iter` | a Lua generic-for iterator (`for h in ents.near(400) do ... end`) |

Multiple return values are used instead of tables (`local x, y = self.pos()`).

### 1.2 Cost classes

| class | meaning |
|---|---|
| `O(1)` | field read or hash lookup, < 50 ns of C# work |
| `O(k)` | proportional to the result or to the entities in the grid cells touched (typically k < 50) |
| `O(n)` | proportional to a whole collection (bag, skill book, quest log); cached by component version unless stated |
| `job` | runs off the tick on the job pool; returns a ticket, the result arrives as an event |
| `cached` | first call per (inputs, component version) does the work, later calls are `O(1)` |

### 1.3 Allocation

| mark | meaning |
|---|---|
| `0` | no managed or Lua allocation |
| `lua-str` | pushes a Lua string; interned per value (map names, item names) so repeated calls do not allocate after the first |
| `iter` | returns a cached iterator function (no allocation per call; per-call state is the generic-for control value) |
| `small` | a short-lived small object (an error string); acceptable off the hot path |

No function returns a freshly built table. Functions that need to hand back many values use iterators or
`fill(tbl)` into a caller-owned table, which they clear first.

### 1.4 Threading and tick phase

- Every function below may only be called from the bot's own tick, during the **brain phase** (a state's
  `enter`/`tick`/`exit`, a transition condition, or a library function called from those). Calling from
  any other context (a coroutine resumed later, a `__gc`) raises `E_CONTEXT`.
- **Queries** read the `WorldState` as of the start of this tick (after inbound events were applied).
  They do not see the effect of actions issued earlier in the same tick; a query that would (e.g.
  `act.pending(kind)`) says so.
- **Actions** enqueue an intent and return an action id at once. The result is visible from the next tick
  (section 4).
- **Jobs** return a ticket; the result arrives as `ev.JOB_DONE` on a later tick.

### 1.5 Errors

- Unknown handle / id: the query returns its "none" value (`-1`, `false`, `nil`), never raises.
- Wrong argument type or count: the binding returns `nil` and sets the VM's pending error
  `E_ARG: <fn>: <detail>` (a C# callback never `lua_error`s: a longjmp across managed frames is undefined,
  see `docs/LUA_BENCH.md`). After the tick the runner records it (`DebugFrame.errors`), marks the tick
  errored and runs the state's `onError` transition (default: stay).
- Budget exceeded: state code runs inside a coroutine and the instruction-count hook yields it; the runner
  closes the coroutine and records `E_BUDGET`; same handling; three in a row pause the bot.
- Lua code may still call `error(...)` itself; that is caught by the runner's protected call as usual.
- An action that cannot be queued (link down, resource claimed by a higher layer, unknown skill) returns
  `-1`; `act.lastRejection()` gives the reason code.

---

## 2. Lua helper library (C#, generated bindings)

Global aliases: `canUse = skills.canUse`, `log = dbg.log`.
All functions are bound by the `[LuaFn]` source generator from `Fiesta.Bot.Api` (see SKELETON.md).

### 2.1 `self` (30) — our character

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `self.handle()` | handle | O(1) | 0 | |
| `self.name()` | string | O(1) | lua-str | interned once |
| `self.classId()` | id | O(1) | 0 | ClassName id |
| `self.classFamily()` | integer | O(1) | 0 | base class: 1 fighter, 6 cleric, 11 archer, 16 mage, 21 joker (from client data, not hardcoded) |
| `self.level()` | integer | O(1) | 0 | |
| `self.exp()` / `self.expNext()` | integer | O(1) | 0 | exp now / exp for next level |
| `self.hp()` / `self.maxHp()` / `self.hpPct()` | number | O(1) | 0 | |
| `self.sp()` / `self.maxSp()` / `self.spPct()` | number | O(1) | 0 | |
| `self.pos()` | x, y | O(1) | 0 | believed position (last server-confirmed + walk progress) |
| `self.map()` | string | O(1) | lua-str | MapInfo short name |
| `self.mapId()` | id | O(1) | 0 | |
| `self.isInstance()` | boolean | O(1) | 0 | |
| `self.facing()` | dx, dy | O(1) | 0 | unit vector |
| `self.moving()` / `self.casting()` / `self.mounted()` / `self.dead()` | boolean | O(1) | 0 | |
| `self.inCombat()` | boolean | O(1) | 0 | hit or hitting within 8 s |
| `self.money()` | integer | O(1) | 0 | seeded at zone entry, never unknown |
| `self.bagFree()` / `self.bagSize()` | integer | O(1) | 0 | |
| `self.runSpeed()` | number | O(1) | 0 | units/s from the last speed packet |
| `self.hasAbstate(id)` | boolean | O(1) | 0 | |
| `self.abstateLeftMs(id)` | ms | O(1) | 0 | `-1` if absent |
| `self.stat(statId)` | number | O(1) | 0 | displayed stat by `self.STAT_*` constant |

### 2.2 `ents` (26) — entities in view

Filter constants (mask): `MOB, HUNTABLE, NPC, PLAYER, GATE, DROP, AGGRO, PARTY, ALIVE, ALL`.

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `ents.near(r [, mask])` | iter of handle | O(k) | iter | nearest first; grid cells within r |
| `ents.count(r [, mask])` | integer | O(k) | 0 | |
| `ents.nearest([mask [, r]])` | handle | O(k) | 0 | default mask `HUNTABLE`, r unbounded (grid ring search) |
| `ents.fill(tbl, r [, mask])` | integer n | O(k) | 0 | writes handles into tbl[1..n], clears the rest |
| `ents.exists(h)` | boolean | O(1) | 0 | |
| `ents.kind(h)` | mask | O(1) | 0 | single kind bit |
| `ents.mobId(h)` | id | O(1) | 0 | `-1` for non-mobs and scenario clones |
| `ents.name(h)` | string | O(1) | lua-str | |
| `ents.level(h)` / `ents.grade(h)` | integer | O(1) | 0 | client MobInfo |
| `ents.hp(h)` / `ents.maxHp(h)` / `ents.hpPct(h)` | number | O(1) | 0 | `-1` when unknown (never seen a HP update) |
| `ents.pos(h)` | x, y | O(1) | 0 | extrapolated along the last move |
| `ents.dist(h)` | number | O(1) | 0 | straight line from us |
| `ents.routeDist(h)` | number | cached | 0 | walkable route length; `-1` = not computed yet (a job was queued) |
| `ents.facing(h)` | dx, dy | O(1) | 0 | `nil` if never moved in view |
| `ents.velocity(h)` | vx, vy | O(1) | 0 | units/s; 0,0 when standing |
| `ents.predictPos(h, ms)` | x, y | O(1) | 0 | position after ms along the current move leg |
| `ents.isDead(h)` | boolean | O(1) | 0 | corpses stay listed until removed |
| `ents.targetOf(h)` | handle | O(1) | 0 | from target broadcasts; `-1` unknown |
| `ents.isAggroOnUs(h)` | boolean | O(1) | 0 | hit us or chasing us (confident) |
| `ents.lastHitUsMs(h)` | ms | O(1) | 0 | `-1` never |
| `ents.body(h)` | number | O(1) | 0 | body size (MobInfo AbsoluteSize * 50 / 1000; players 50) |
| `ents.spawnPos(h)` | x, y | O(1) | 0 | first idle position observed |
| `ents.lastHitPos(h)` | x, y | O(1) | 0 | where the mob was last damaged (leash origin) |

### 2.3 `threat` (13)

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `threat.onUs()` | integer | O(1) | 0 | mobs aggroed on us |
| `threat.incomingDps()` | number | O(1) | 0 | expected damage/s from aggressors (learned hits / intervals) |
| `threat.sustainableHps()` | number | cached | 0 | HP/s we can recover (heals, stones, potions) |
| `threat.outmatched()` | boolean | O(1) | 0 | `incomingDps > sustainableHps * k` and time-to-die < time-to-kill |
| `threat.packAround(h, r)` | integer | O(k) | 0 | other hostiles within r of h |
| `threat.wouldWake(h)` | integer | O(k) | 0 | mobs whose aggro shape the walk to h crosses, plus family assists |
| `threat.pullCost(h)` | number | cached | 0 | the target-picker score: route length + wake/pack penalties |
| `threat.burstIfEngaged(h)` | number | O(k) | 0 | synchronised first-hit damage if we engage h |
| `threat.inAggroShape(h, x, y)` | boolean | O(1) | 0 | detect circle (r = learned DetectCha) offset 0.4 r along facing |
| `threat.detectRadius(h)` | number | O(1) | 0 | estimate; `-1` unknown |
| `threat.leashLeft(h)` | number | O(1) | 0 | distance until h gives up the chase, measured from its last-hit point |
| `threat.assistRadius(h)` | number | O(1) | 0 | learned family-assist radius; `-1` unknown |
| `threat.safeSpot(r)` | x, y or nil | cached | 0 | walkable, reachable point within r with no aggro shape covering it |

### 2.4 `combat` (13)

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `combat.bashReach(h)` | number | O(1) | 0 | `(body(h) + 120 or 600) * 1.25` |
| `combat.skillReach(skill, h)` | number | O(1) | 0 | `min(r * 1.25, r + 60)`, `r = Range + max(50, Range/10) + body(h)` |
| `combat.inBashReach(h)` | boolean | O(1) | 0 | |
| `combat.los(h)` | boolean | O(n) cached | 0 | attack-block line test (`.shab` maps); always true elsewhere |
| `combat.target()` | handle | O(1) | 0 | server-confirmed selection |
| `combat.battleMode()` | boolean | O(1) | 0 | |
| `combat.bashing()` / `combat.bashTarget()` | boolean / handle | O(1) | 0 | |
| `combat.lastSwingMs()` | ms | O(1) | 0 | since our last landed swing |
| `combat.dpsOut()` | number | O(1) | 0 | our damage/s over the current fight |
| `combat.timeToKill(h)` | number (s) | O(1) | 0 | hp / dpsOut against h's learned armour |
| `combat.timeToDie()` | number (s) | O(1) | 0 | hp / (incomingDps - regen) |
| `combat.kills()` | integer | O(1) | 0 | this session |

### 2.5 `skills` (21)

Tag constants (mask, precomputed from ActiveSkill at load): `DAMAGE, HEAL, BUFF, DEBUFF, STUN, AOE,
GROUND, SELF, PARTY, MOVING, DOT, SLOW, DISPEL, REVIVE, TOGGLE`.

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `skills.has(id)` | boolean | O(1) | 0 | learned |
| `skills.list([mask])` | iter of id | O(n) cached | iter | highest rank of each family only |
| `skills.best(mask, target)` | id | O(n) | 0 | highest expected value among `canUse` skills with the tags |
| `skills.canUse(id, target)` | boolean | O(1) | 0 | **generic, objective check** (section 2.5.1). Global alias `canUse`. |
| `skills.whyNot(id, target)` | code, string | O(1) | small | first failing check (`skills.R_*` code) and text |
| `skills.ready(id)` | boolean | O(1) | 0 | cooldown and cooldown group only |
| `skills.readyInMs(id)` | ms | O(1) | 0 | |
| `skills.cost(id)` | sp, hp | O(1) | 0 | after empower |
| `skills.castTimeMs(id)` / `skills.cooldownMs(id)` | ms | O(1) | 0 | after empower |
| `skills.range(id)` / `skills.area(id)` | number | O(1) | 0 | raw data values |
| `skills.isMoving(id)` | boolean | O(1) | 0 | castable while moving |
| `skills.hasTag(id, tag)` / `skills.tags(id)` | boolean / mask | O(1) | 0 | |
| `skills.family(id)` / `skills.rank(id)` | id / integer | O(1) | 0 | |
| `skills.highestRank(family)` | id | O(1) | 0 | learned highest rank, `-1` none |
| `skills.healAmount(id)` | number | O(1) | 0 | expected heal on us |
| `skills.damageEstimate(id, h)` | number | O(1) | 0 | damage formula against h's learned armour |
| `skills.empower(id)` | dmg, sp, dur, cd | O(1) | 0 | empower levels per track |

#### 2.5.1 `canUse(id, target)` checks, in order (reason codes)

`R_NOT_LEARNED`, `R_COOLDOWN` (incl. group), `R_SP`, `R_HP` (HP cost would kill), `R_DEAD`,
`R_STATE` (stunned, silenced, feared, captivated, ethereal), `R_MOUNTED`, `R_CASTING`,
`R_MOVING` (we are moving and the skill is not a moving skill), `R_WEAPON` (weapon/shield requirement),
`R_TARGET` (target missing, dead, wrong kind for the skill's target type, not healable/attackable),
`R_RANGE` (`combat.skillReach`), `R_LOS` (attack-block line), `R_MAP` (map forbids skills/revival).
`canUse` never evaluates policy ("is this a good idea"); that belongs to user helpers such as
`isGoodHealSkill`.

### 2.6 `act` (26) — actions (see section 4)

| function | returns | resource | notes |
|---|---|---|---|
| `act.cast(skill, h)` | action | Cast | object cast; handles target/mode/face/stop sequencing |
| `act.castAt(skill, x, y)` | action | Cast | ground cast |
| `act.bash(h)` | action | Combat | auto-attack; re-armed by CombatController after moves |
| `act.stopBash()` | action | Combat | |
| `act.target(h)` / `act.untarget()` | action | Combat | |
| `act.moveTo(x, y)` | action | Movement | path job + MoveController; resolves on arrival / failure |
| `act.moveDirect(x, y)` | action | Movement | one straight leg, no path |
| `act.follow(h, dist)` | action | Movement | until cancelled |
| `act.stop()` | action | Movement | commit position |
| `act.mount()` / `act.dismount()` | action | Movement | |
| `act.use(slot)` / `act.useItem(itemId)` | action | Inventory | |
| `act.stoneHp()` / `act.stoneSp()` | action | Inventory | soul stones |
| `act.loot(h)` | action | Inventory | pick up a drop |
| `act.revive()` | action | - | ReviveProcedure |
| `act.say(text)` / `act.whisper(name, text)` | action | Chat | rate limited |
| `act.state(a)` | string | - | `queued, sent, acked, refused, timedout, cancelled, failed` |
| `act.result(a)` | code | - | refusal / error code, `0` = ok (server code, or a local `act.L_*`) |
| `act.reason(a)` | string | - | text for the code (error-code table) |
| `act.cancel(a)` | boolean | - | best effort; a sent packet cannot be unsent |
| `act.pending(kind)` | action | - | newest unresolved action of a kind, `-1` none |
| `act.lastRejection()` | code | - | why the last call returned `-1` |

### 2.7 `items` (13)

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `items.count(itemId)` | integer | O(1) | 0 | per-id index |
| `items.find(itemId)` | slot | O(1) | 0 | first slot |
| `items.slots([mask])` | iter of slot | O(n) | iter | masks `CONSUMABLE, EQUIP, QUEST, MATERIAL, JUNK, POTION, SCROLL, MOUNT` |
| `items.at(slot)` | itemId, lot | O(1) | 0 | `-1, 0` empty |
| `items.class(itemId)` / `items.levelReq(itemId)` | integer | O(1) | 0 | client ItemInfo |
| `items.sellPrice(itemId)` | integer | O(1) | 0 | |
| `items.isQuestItem(itemId)` | boolean | O(1) | 0 | |
| `items.bestPotion(deficit, kindMask)` | slot | O(n) cached | 0 | smallest potion that covers the deficit |
| `items.junkValue()` | integer | cached | 0 | what selling all junk would fetch |
| `items.equipUpgrade()` | slot | cached | 0 | best bag item that improves an equipment slot |
| `items.equipped(equipSlot)` | itemId | O(1) | 0 | |
| `items.fill(tbl [, mask])` | integer n | O(n) | 0 | slots into a caller-owned table |

### 2.8 `quests` (16)

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `quests.active()` | iter of id | O(n) | iter | |
| `quests.available()` | iter of id | O(n) cached | iter | acceptable now (level, prerequisites) |
| `quests.isActive(q)` / `quests.isDone(q)` / `quests.ready(q)` | boolean | O(1) | 0 | ready = all objectives met |
| `quests.progress(q, i)` | have, need | O(1) | 0 | objective i (1-based) |
| `quests.objectives(q)` | integer | O(1) | 0 | count |
| `quests.objective(q, i)` | kind, mobId, itemId, need | O(1) | 0 | kind: `quests.KILL / COLLECT / ...` |
| `quests.name(q)` | string | O(1) | lua-str | always use names in logs |
| `quests.levels(q)` | min, max | O(1) | 0 | |
| `quests.giver(q)` | npcId | O(1) | 0 | |
| `quests.repeatable(q)` / `quests.exp(q)` | boolean / integer | O(1) | 0 | |
| `quests.slotsFree()` | integer | O(1) | 0 | free quest-log slots (the log holds 40) |
| `quests.plan(policyId)` | ticket | job | 0 | scores candidates; result: `quests.planResult(t)` |
| `quests.planResult(t)` | questId, kind | O(1) | 0 | kind: `HUNT, HANDIN, ACCEPT, NONE` |

### 2.9 `nav` (15)

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `nav.path(x, y)` | ticket | job | 0 | corridor A* + funnel; goal snapped to walkable |
| `nav.pathState(t)` | string | O(1) | 0 | `pending, ready, failed` |
| `nav.pathLength(t)` | number | O(1) | 0 | |
| `nav.routeLength(x, y)` | number | cached | 0 | `-1` unreachable |
| `nav.reachable(x, y)` | boolean | O(n) cached | 0 | region connectivity with door state |
| `nav.walkable(x, y)` | boolean | O(1) | 0 | block layer (`.shbd` OR `.shab`) + overlay |
| `nav.snap(x, y)` | x, y | O(k) | 0 | nearest walkable tile (client rule) |
| `nav.clearSpot(minFromNpc, r)` | x, y or nil | O(k) | 0 | reachable walkable point away from NPCs (tent placement) |
| `nav.routeRisk(x, y)` | number | cached | 0 | aggro shapes crossed by the walked route |
| `nav.gateTo(mapName)` | handle | O(1) | 0 | gate on this map toward that map, `-1` none |
| `nav.travel(mapName)` | action | job | 0 | multi-map travel (GateHopProcedures) |
| `nav.traveling()` | boolean | O(1) | 0 | |
| `nav.kiteStep(h, gap)` | x, y | O(k) | 0 | next point keeping `gap` from h along walkable ground away from other aggro shapes |
| `nav.mapOf(mapId)` | string | O(1) | lua-str | |
| `nav.distance(x1, y1, x2, y2)` | number | O(1) | 0 | |

### 2.10 `npc` (11)

Role constants (learned from menus, client-legal): `SHOP, SOULSTONE, SKILL, SMITH, STORAGE, QUEST,
TELEPORT, GATE`.

| function | returns | cost | alloc | notes |
|---|---|---|---|---|
| `npc.find(roleMask)` | iter of handle | O(k) | iter | in view |
| `npc.nearest(roleMask)` | handle | O(k) | 0 | in view |
| `npc.known(roleMask, map)` | npcId, x, y | O(1) | 0 | from the knowledge store, may be off-map |
| `npc.role(h)` / `npc.npcId(h)` | mask / id | O(1) | 0 | |
| `npc.openShop(h)` | action | Npc | OpenShopProcedure; resolves when the shop list arrived |
| `npc.shopHas(itemId)` | boolean | O(1) | 0 | currently open shop |
| `npc.buy(itemId, lot)` / `npc.sell(slot, lot)` | action | Npc | |
| `npc.talk(h, questId)` | action | Npc | QuestDialogueProcedure (accept or hand in) |
| `npc.storage(h)` | action | Npc | StorageProcedure |

### 2.11 `social` (14)

`partyInvite(name)`, `partyAccept()`, `partyLeave()`, `partyMembers()` (iter of handle, iter),
`partySize()`, `tradePropose(h)`, `tradeState()` (string), `tradeOfferItem(slot, lot)`,
`tradeOfferMoney(cen)`, `tradeLock()`, `tradeConfirm()`, `tradeCancel()`, `chatLast()` (name, text;
lua-str), `whisperLast()` (name, text). Actions return action ids (resource `Chat` or `Npc` for trade).

### 2.12 `know` (9) — learned knowledge (shared host-wide)

Each estimator returns `estimate, low, high, samples`; `-1, -1, -1, 0` when unknown.

`know.detect(mobId)`, `know.leash(mobId)`, `know.reach(mobId)`, `know.interval(mobId)`,
`know.assist(familyId)`, `know.hit(mobId)` (avg, max, samples), `know.armour(mobId)` (ac, mr, conf),
`know.dungeonRatio(mapId)`, `know.npcRole(npcId)` (mask). All O(1), 0 alloc.

### 2.13 `time` (4), `ev` (4), `world` (1), `dbg` (6)

| function | returns | notes |
|---|---|---|
| `time.now()` | ms | tick-start clock (monotonic, replayable) |
| `time.since(t)` | ms | |
| `time.tick()` | integer | tick counter |
| `time.every(key, ms)` | boolean | true at most once per ms per integer key (per layer) |
| `ev.fired(kind)` | boolean | event of that kind applied this tick |
| `ev.count(kind, ms)` | integer | occurrences in the last ms (per-kind ring of 64) |
| `ev.last(kind)` | tick, arg1, arg2 | most recent, `-1` none |
| `ev.chatText()` | name, text | for the latest `CHAT`/`WHISPER` event (lua-str) |
| `world.version(component)` | integer | `world.SELF, ENTS, BAG, SKILLS, QUESTS, PARTY, MAP` |
| `dbg.log(fmt, ...)` / `dbg.logi(fmt, ...)` / `dbg.logv(fmt, ...)` | - | Note / Info / Debug in category `script.<layer>`; formatted in C# (`string.format` rules) only when enabled; budgeted (section 12.4) |
| `dbg.crit(fmt, ...)` | - | Crit line + DebugFrame flag (never dropped, collapsed) |
| `dbg.enabled(level)` | boolean | is that level on for this layer's script category (`dbg.NOTE/INFO/DEBUG/TRACE`); call before building expensive text |
| `dbg.watch(name, value)` | - | shown in the debugger with history |
| `dbg.mark(section)` | - | tick profiler section boundary |

Total: **223 functions** in 16 modules (self 30, ents 26, threat 13, combat 13, skills 21, act 26,
items 13, quests 16, nav 15, npc 11, social 14, know 9, time 4, ev 4, world 1, dbg 7).

---

## 3. Events

`ev.*` constants. Each event is applied in step 2 of the tick and is visible to conditions (`on:`) and
`ev.fired` in the same tick.

| kind | arg1 | arg2 | raised when |
|---|---|---|---|
| `DAMAGED` | attacker | damage | we took damage |
| `HIT` | target | damage | we dealt damage |
| `TARGET_DIED` | handle | mobId | our target died |
| `KILL` | handle | mobId | a mob we damaged died |
| `AGGRO_GAINED` | handle | - | a mob became aggro on us |
| `AGGRO_LOST` | handle | - | it stopped (leash, death, left view) |
| `ENTITY_APPEARED` / `ENTITY_LEFT` | handle | kind | |
| `HP_CHANGED` / `SP_CHANGED` | value | max | |
| `LEVEL_UP` | level | - | |
| `MAP_CHANGED` | mapId | previous | |
| `MOVE_FAILED` | kind (`DESYNC`/`BLOCKED`) | - | MOVEFAIL classified |
| `ARRIVED` | action | - | a move action finished |
| `CAST_RESULT` | skill | code | |
| `ACTION_RESOLVED` | action | state | any action left the pending state |
| `PROCEDURE_DONE` | procedure kind | ok (0/1) | |
| `JOB_DONE` | ticket | ok | |
| `QUEST_PROGRESS` | quest | objective | |
| `QUEST_DONE` | quest | - | |
| `QUEST_ACCEPTED` | quest | - | |
| `ITEM_GAINED` / `ITEM_LOST` | itemId | lot | |
| `BAG_FULL` | - | - | server signal or 0 free |
| `MONEY_CHANGED` | money | delta | |
| `CHAT` / `WHISPER` | fromHandle | - | text via `ev.chatText()` |
| `PARTY_INVITE` / `TRADE_ASKED` | fromHandle | - | |
| `DIED` / `REVIVED` | - | - | |
| `STUCK` | layer index | state index | raised by the runtime when a state's progress stalls |
| `BUDGET` | layer index | - | a state tick hit its budget |
| `OPERATOR` | command | - | an operator command arrived |

---

## 4. Actions and result handles

### 4.1 Lifecycle

```
queued --(flush to outbound ring)--> sent --(matcher)--> acked
   |                                   |--(refusal packet)--> refused(code)
   |                                   |--(timeout)---------> timedout
   +--(link down / claim lost)--> failed(local code)    any --(act.cancel)--> cancelled
```

### 4.2 Action catalog (C# `ActionCatalog`)

| kind | wire (C->S) | success matcher | refusal | timeout |
|---|---|---|---|---|
| `CAST` | target (if needed), mode (once), stop/face, SKILLBASH_OBJ_CAST | cast start / skill result for our cast index | cast-fail code (0x0FC0..0x0FDC) | 1.5 s + castTime |
| `CAST_GROUND` | SKILLBASH_FLD_CAST | as above | as above | as above |
| `BASH` | target, mode, BASHSTART | first swing or BASHSTART ack | silent stop: timeout | 1.5 s |
| `MOVE` | MOVERUN legs, STOP | arrival within 1 tile | MOVEFAIL (classified) | route length / speed + 3 s |
| `USE_ITEM` | ITEM_USE | use ack | USEFAIL code | 2 s |
| `STONE_HP/SP` | soul stone use | reserve/HP change | fail code | 2 s |
| `LOOT` | pickup | item gained / drop removed | fail code | 2 s |
| `BUY` / `SELL` | ITEM_BUY / ITEM_SELL | ack 0x0381 + cell change | other ack codes | 2 s |
| `OPEN_SHOP` | procedure | shop list | menu refusal | 5 s |
| `TALK` | procedure | dialogue end / quest state change | quest error code | 15 s |
| `GATE` | procedure | map link | menu refusal / no link | 20 s |
| `REVIVE` | procedure | revive packet | none (timeout -> relog path) | 30 s |
| `TRADE_*` | trade packets | trade state change | PROPOSENO codes | 10 s |
| `SAY` / `WHISPER` | chat | none (fire-and-forget, logged) | - | - |

Local refusal codes (`act.L_*`): `L_LINK_DOWN`, `L_PREEMPTED` (resource claimed by a higher layer),
`L_UNKNOWN_SKILL`, `L_NOT_READY`, `L_OUT_OF_RANGE`, `L_MOVING`, `L_MOUNTED`, `L_BUSY` (procedure running),
`L_QUEUE_FULL`.

### 4.3 Resources

`Movement, Combat, Cast, Inventory, Npc, Chat`. A layer's active state declares claims; an action from a
lower layer on a claimed resource fails with `L_PREEMPTED`. The operator layer can claim everything.

### 4.4 Jobs

`JobKind`: `PATH, ROUTE_LENGTH, QUEST_PLAN, TRAVEL_PLAN, SAFE_SPOT, CUSTOM`. A ticket resolves once; its
result stays readable for 10 s or until the next request of the same kind from the same layer.

---

## 5. Brain definition format

### 5.1 File layout (brain store, private)

```
brains/<brain>/brain.yaml            definition (this section)
brains/<brain>/<module>.lua          brain-private modules (namespace brain/<brain>/...)
lib/<name>.lua                       host-provided libraries (read-only, versioned with the API)
user/<owner>/<name>.lua              user libraries (shared across brains)
```

### 5.2 Schema (JSON Schema, draft 2020-12; YAML is parsed to the same model)

```json
{
  "$id": "https://ik-fiesta-bots/brain.schema.json",
  "type": "object",
  "required": ["brain", "api", "layers"],
  "properties": {
    "brain": { "type": "string", "pattern": "^[a-z][a-z0-9_-]*$" },
    "api": { "type": "integer", "const": 3 },
    "description": { "type": "string" },
    "modules": { "type": "array", "items": { "$ref": "#/$defs/moduleRef" } },
    "blackboard": { "type": "object", "description": "initial bot-scope values" },
    "machines": { "type": "object", "additionalProperties": { "$ref": "#/$defs/machine" },
                  "description": "named reusable machines, referenced by states with `use`" },
    "layers": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/layer" } }
  },
  "$defs": {
    "moduleRef": { "type": "string", "pattern": "^(lib|user/[a-z0-9_-]+|brain/[a-z0-9_-]+)/[a-z0-9_/-]+(@[0-9a-f]{8,})?$" },
    "resource": { "enum": ["Movement", "Combat", "Cast", "Inventory", "Npc", "Chat"] },
    "layer": {
      "type": "object", "required": ["name", "priority", "machine"],
      "properties": {
        "name": { "type": "string" },
        "priority": { "type": "integer", "minimum": 0, "maximum": 100 },
        "rate": { "oneOf": [ { "const": "tick" }, { "type": "integer", "minimum": 50 } ] },
        "budgetMs": { "type": "number", "default": 2 },
        "enabled": { "type": "boolean", "default": true },
        "machine": { "$ref": "#/$defs/machine" }
      }
    },
    "machine": {
      "type": "object", "required": ["initial", "states"],
      "properties": {
        "initial": { "type": "string" },
        "select": { "type": "object", "description": "per-class/level initial or machine choice",
                    "properties": { "by": { "enum": ["classFamily", "levelBand", "map"] },
                                    "cases": { "type": "object" } } },
        "states": { "type": "object", "additionalProperties": { "$ref": "#/$defs/state" } }
      }
    },
    "state": {
      "type": "object",
      "properties": {
        "enter": { "$ref": "#/$defs/fnRef" }, "tick": { "$ref": "#/$defs/fnRef" }, "exit": { "$ref": "#/$defs/fnRef" },
        "use": { "type": "string", "description": "sub-machine name (may contain ${expr})" },
        "machine": { "$ref": "#/$defs/machine" },
        "claims": { "type": "array", "items": { "$ref": "#/$defs/resource" } },
        "budget": { "type": "integer", "minimum": 0, "default": 60000, "description": "ms without progress before `stuck`" },
        "progress": { "type": "string", "description": "sensor path or Lua expression; larger = better" },
        "minDwell": { "type": "integer", "default": 0 },
        "timeout": { "type": "integer", "description": "hard cap in ms; raises `after` semantics with reason timeout" },
        "terminal": { "type": "boolean", "default": false },
        "onError": { "type": "string", "description": "state to enter on a Lua error (default: stay)" },
        "transitions": { "type": "array", "items": { "$ref": "#/$defs/transition" } }
      }
    },
    "fnRef": { "type": "string", "description": "module.function or an inline Lua body" },
    "transition": {
      "type": "object", "required": ["to"],
      "properties": {
        "to": { "type": "string", "description": "state name, `machine:state`, or `^` (pop to parent)" },
        "priority": { "type": "integer", "default": 0 },
        "hold": { "type": "integer", "default": 0 },
        "cooldown": { "type": "integer", "default": 0 },
        "reason": { "type": "string", "description": "template with {sensor} placeholders" },
        "when": { "type": "string" },
        "on": { "type": "string" }, "action": { "type": "string" }, "result": { "type": "string" },
        "procedure": { "type": "string" }, "job": { "type": "string" },
        "after": { "type": "integer" }, "every": { "type": "integer" },
        "value": { "type": "string" }, "op": { "enum": ["<", "<=", ">", ">=", "==", "~="] }, "than": {},
        "stuck": { "const": true }, "always": { "const": true },
        "all": { "type": "array", "items": { "$ref": "#/$defs/transition/properties" } },
        "any": { "type": "array" }, "not": { "type": "object" }
      }
    }
  }
}
```

### 5.3 Condition kinds (exactly one per transition, or a composite)

| key | semantics | cost |
|---|---|---|
| `when: "<fn or expr>"` | Lua; truthy = fire | Lua call |
| `on: <EVENT>` (+ `filter: "<expr>"` optional) | an event of that kind applied this tick | bit test |
| `on: action, action: <name>, result: acked/refused/timedout/failed` | a named action handle (`bb.state.<name>`) resolved so | O(1) |
| `procedure: <kind>, result: ok/failed` | a procedure finished | O(1) |
| `job: <kind>, result: <value>` | a job ticket resolved with that result | O(1) |
| `after: ms` | time in state >= ms | O(1) |
| `every: ms` | fires periodically (self-transition re-enters the state) | O(1) |
| `value: <sensor>, op, than` | compare a registered sensor (`self.hpPct`, `threat.onUs`, `self.bagFree`, any zero-arg numeric helper) | O(1) |
| `stuck: true` | progress did not improve for `budget` ms | O(1) |
| `always: true` | unconditional | - |
| `all` / `any` / `not` | composites; fixed leaves first, Lua leaves last (short-circuit) | - |

Hysteresis: `hold` = the condition must be continuously true for that long; `cooldown` = the transition
cannot fire again within that time after it fired; `minDwell` on the source state blocks all its
transitions except `stuck`/`timeout`/higher-layer preemption for that long.

### 5.4 Semantics

- Layers evaluate highest priority first; each layer evaluates the active state path outermost first; the
  first firing transition (by `priority`, then declaration order) wins for that layer.
- A transition exits the states from the leaf up to and including the source, then enters the target
  and its initial sub-states (`enter` called outermost first).
- A state with neither transitions to other states nor `terminal: true` is a lint error. A non-terminal
  state without a `stuck`, `after` or `timeout` exit is a lint warning (error when `budget` is 0).
- `${expr}` in `use` is evaluated on entry (`combat/${self.classFamily}` -> `combat/11`).
- Cross-machine targets (`machine:state`) are allowed within one layer (plan Layer 3).

### 5.5 Blackboard

Three Lua tables: `bb.bot` (persisted, survives version changes and relogs; checkpointed every 10 s),
`bb.layer` (per layer, reset when the layer's machine changes), `bb.state` (cleared on exit). Values:
numbers, strings, booleans, nested tables of those (<= 4 KB serialised per scope). The debugger reads
them at publish time (5 Hz).

---

## 6. User helper library contract

- A module is a Lua file that **returns a table of functions and constants**. Top-level code may only
  build that table and local constants; it may not call actions, read world state or write globals (the
  linter and the loader's sandbox enforce it: world/action modules are not visible during load).
- Namespaces: `lib/...` (host, read-only, versioned with the API), `user/<owner>/...` (shared),
  `brain/<brain>/...` (private). `require "user/ikaron/heal"` resolves within the brain version's pinned
  module set; there is no file system access.
- Module state: none at module level across ticks (use `bb.*`); a module may cache derived constants on
  first use.
- Versioning: each module version is content-addressed (`sha256` of source, short form 12 hex). A brain
  version pins module versions (`user/ikaron/heal@3f9c1a2b7d01`); unpinned references resolve to the
  latest at the time the brain version is created.
- Hot reload: applying a brain version (or a library edit propagated to a brain) compiles all modules
  first; on success the VM swaps the module tables at the next tick boundary; `bb.bot` is kept; active
  states that still exist by name are kept. On a compile error nothing changes and the error is returned.
- Lint on save: syntax, unknown `api` names (manifest), writes to globals, reads of undefined globals,
  forward-reference upvalues, unused locals, functions referenced by brain.yaml that do not exist.

---

## 7. Debug and telemetry model (consumed by the UI)

### 7.1 `DebugFrame` (published at 5 Hz per watched bot, and on every transition)

```json
{
  "bot": "NewArcher", "tick": 23233563, "t": "2026-11-24T21:09:35.020Z", "version": "leveller@9c2e41d0aa12",
  "layers": [
    { "name": "survival", "priority": 90, "path": ["ok"], "enteredAt": "2026-11-24T21:09:31.002Z",
      "timeInStateMs": 4018, "progress": 812, "progressBestAt": "2026-11-24T21:09:31.002Z", "claims": [],
      "conditions": [ { "to": "recover", "kind": "value", "expr": "self.hpPct < 35", "value": 40.1, "result": false,
                        "holdMs": 0 } ] }
  ],
  "lastTransitions": [ { "tick": 23233510, "layer": "errand", "from": "choose", "to": "hunt",
                         "kind": "job", "reason": "quest Poor Merchant (kill 5)", "values": { "ticket": 41 } } ],
  "blackboard": { "bot": {}, "layer": { "errand": {} }, "state": { "errand": {} } },
  "watches": { "target": 1734, "pullCost": 512.4 },
  "actions": [ { "id": 9912, "kind": "CAST", "state": "refused", "code": 4042, "reason": "out of range",
                 "sentAt": "...", "resolvedAt": "...", "layer": "combat" } ],
  "procedures": [ { "kind": "OPEN_SHOP", "step": "awaitShopList", "since": "..." } ],
  "tick": { "p50us": 210, "p99us": 690, "allocP95B": 0, "steps": { "decode": 12, "apply": 40, "actions": 8, "brain": 150, "flush": 3 } },
  "errors": [ { "tick": 23233401, "layer": "combat", "state": "engage", "error": "E_ARG: act.cast: skill must be integer" } ]
}
```

### 7.2 `SensorFrame` (2 to 10 Hz, only while a sensor view is open)

```json
{
  "bot": "NewArcher", "map": "Eld", "mapId": 9, "self": { "x": 11802, "y": 10466, "facing": [0.7, 0.7] },
  "entities": [ { "h": 1734, "k": "mob", "mobId": 343, "x": 11950, "y": 10300, "hpPct": 64, "aggro": true,
                  "detect": 264, "facing": [-1, 0], "leashOrigin": [11980, 10320], "leashLeft": 1210 } ],
  "reach": { "bash": 162.5, "skills": [ { "id": 4000, "reach": 495 } ] },
  "path": [[11802, 10466], [11900, 10380]],
  "threat": { "cellSize": 200, "origin": [10600, 9200], "w": 13, "h": 13, "values": "base64-u8" }
}
```

### 7.3 Tick telemetry

`TickSample { tick, totalUs, decodeUs, applyUs, actionsUs, moveUs, combatUs, brainUs, flushUs, allocBytes,
events, intents, luaInstructions }`, aggregated into HDR histograms (1 s, 1 m, 10 m windows) and exported
as Prometheus metrics `fiestabot_tick_us{bot,step,quantile}`, `fiestabot_tick_alloc_bytes{bot,quantile}`,
`fiestabot_gc_pause_ms_total`, `fiestabot_transitions_total{bot,layer,from,to}`, `fiestabot_stuck_total{bot,layer,state}`.

---

## 8. Host HTTP and WebSocket API

Base: `/api/v3`. Auth: `Authorization: Bearer <token>` (as today). JSON, camelCase. Errors: RFC 7807.

### 8.1 Fleet control

| method | path | body | returns |
|---|---|---|---|
| GET | `/bots` | - | `BotSummary[]` (state per layer, version, health, tick p99, exp/h, deaths/h) |
| POST | `/bots` | `SpawnRequest {id, host, loginPort, username, password, character, create?, class?, brain?, channel?, autostart}` | `BotSummary` |
| GET | `/bots/{id}` | - | `BotDetail` |
| DELETE | `/bots/{id}` | - | stop and forget |
| POST | `/bots/{id}/start` | - | logs in (if offline) and starts the brain |
| POST | `/bots/{id}/stop` | `{logout: true}` | brain stop + clean logout (flees first in combat) |
| POST | `/bots/{id}/relog` | - | checkpoint, logout, login, resume |
| POST | `/bots/{id}/pause` / `/resume` | - | brain paused (link kept, controllers safe-stop) |
| POST | `/bots/{id}/step` | `{ticks: 1}` | runs N ticks while paused |
| POST | `/bots/{id}/force-state` | `{layer, state}` | operator transition (reason "operator") |
| PUT | `/bots/{id}/blackboard` | `{scope, key, value}` | edit a value |
| POST | `/bots/{id}/actions` | `{kind, args}` | operator action (operator layer) |
| POST | `/bots/{id}/packetlog` | `{enabled}` | persisted setting |

### 8.2 Brains, libraries, versions

| method | path | body / query | returns |
|---|---|---|---|
| GET | `/brains` | - | `[ {name, latest, channels} ]` |
| GET | `/brains/{name}/versions` | - | `BrainVersionInfo[]` (hash, created, author, message, api, modules) |
| GET | `/brains/{name}/versions/{hash}` | - | full definition + module sources |
| POST | `/brains/{name}/versions` | `{definition, modules: {path: source}, message}` | `BrainVersionInfo` or 400 with lint results |
| POST | `/brains/lint` | `{definition, modules}` | `LintResult[]` |
| GET | `/brains/{name}/diff?from=&to=` | - | `{definitionDiff, modules: [{path, unifiedDiff}]}` |
| PUT | `/brains/{name}/channels/{channel}` | `{hash}` | channel moved |
| POST | `/bots/{id}/brain` | `{name, hash? , channel?, keepState: true}` | applied; `AssignmentRecord` |
| POST | `/bots/{id}/brain/rollback` | `{to?: hash}` | previous version re-applied |
| GET | `/bots/{id}/assignments` | - | `AssignmentRecord[]` (who, when, from, to, why) |
| GET | `/libraries` | - | `[ {path, latest, usedBy} ]` |
| GET/POST | `/libraries/{path}/versions` | `{source, message}` | as brains |
| GET | `/api-manifest` | - | the generated manifest (modules, functions, params, types, docs, constants) |

### 8.3 Observation

| method | path | returns |
|---|---|---|
| GET | `/bots/{id}/debug` | latest `DebugFrame` |
| GET | `/bots/{id}/transitions?since=&layer=` | transition history (persisted, downsampled) |
| GET | `/bots/{id}/tick?last=1000` | `TickSample[]` |
| GET | `/bots/{id}/sensors` | latest `SensorFrame` |
| GET | `/bots/{id}/log?level=&from=&to=&max=` | log lines (files, survive relogs; dates included) |
| GET | `/bots/{id}/recordings` / `/recordings/{rid}` | session recordings (`.botrec`) for replay |
| GET | `/knowledge/mobs/{mobId}` | estimators with samples |
| GET | `/metrics` | Prometheus text |

### 8.4 WebSocket `/ws`

Client -> server: `{"op":"sub","topic":"fleet"}`, `{"op":"sub","topic":"debug","bot":"NewArcher"}`,
`{"op":"sub","topic":"sensors","bot":"NewArcher","hz":5}`, `{"op":"sub","topic":"log","bot":"...","level":"info"}`,
`{"op":"unsub", ...}`.

Server -> client: `{"topic":"fleet","data":BotSummary[]}` (1 Hz), `{"topic":"debug","data":DebugFrame}`
(5 Hz + on transition), `{"topic":"transition","data":TransitionRecord}` (immediately),
`{"topic":"sensors","data":SensorFrame}`, `{"topic":"log","data":{"t":"...","level":"N","line":"..."}}`,
`{"topic":"alert","data":{"bot":"...","kind":"stuck|budget|crit|disconnect","text":"..."}}`.

Frames are deltas after the first full frame (`{"full":false,"patch":[...]}`, RFC 6902), so a watched
bot costs ~1 KB/s.

---

## 9. Operator layer commands

`PAUSE, RESUME, STEP(n), FORCE_STATE(layer, state), SET_BB(scope, key, value), ACTION(kind, args),
STOP, RELOG, APPLY_VERSION(hash, keepState)`. Commands are drained at the start of the tick
(`ControlQueue`); each produces an `OPERATOR` event and an assignment/transition record.

---

## 10. Persistence model (tables)

`bots(id, account, character, host, port, brain, channel, hash, autostart, packetlog)`,
`brain_versions(hash, brain, api, created, author, message, definition_json)`,
`module_versions(hash, path, source, created, author)`, `brain_modules(brain_hash, module_hash)`,
`channels(brain, channel, hash)`, `assignments(bot, at, from_hash, to_hash, by, reason, kept_state)`,
`checkpoints(bot, at, hash, layers_json, bb_json)`, `transitions(bot, tick, at, layer, from, to, kind, reason)`
(downsampled), `knowledge_mob(mob_id, kind, estimate, lo, hi, n, updated)`, `knowledge_npc(npc_id, role_mask, map_id, x, y)`.

---

## 11. Compatibility shim (API 2)

`lib/compat_v2.lua` defines a `bot` table whose functions call the API 3 modules: `bot.hp()` ->
`self.hp()`, `bot.nearbyMobs()` -> builds the old table from `ents.near` (allocates; for migration only),
`bot.cast(s, t)` -> `act.cast` and returns `true` when queued, etc. A single-state brain
(`compat: true`) runs an unmodified API-2 script: its `tick()` becomes the state tick, `on_*` handlers are
called from the matching events. Removed once the leveller is split up (REDESIGN Phase 6).

---

## 12. Logging API

Design and measurements: `docs/REDESIGN.md` section 16, `docs/LOGGING.md`.

### 12.1 Model

| type | fields |
|---|---|
| `LogLevel` | `Crit = 0, Note = 1, Info = 2, Debug = 3, Trace = 4` (enabled when `level <= threshold`) |
| `LogCategory` | `Core, Net, Packet, Nav, Combat, Quest, Item, Social, Brain, Script, Prof, Knowledge` |
| `LogEntry` (struct) | `long At` (ms), `LogLevel`, `LogCategory`, `ushort EventId` (0 = free text), `byte Layer`, `long A0..A3`, `double D0..D1`, `string? Text`, `int Repeat` (collapsed count) |
| `LogEventDef` | id, category, level, template (`"walkTo ({x},{y}) unreachable on {map}"`), argument names; generated from `[LogEvent]` methods |

### 12.2 C# API (`Fiesta.Bot.Services.Logging`)

```csharp
public sealed class BotLogger
{
    public bool IsEnabled(LogCategory c, LogLevel l);                         // array lookup, no allocation
    public void Log(LogCategory c, LogLevel l,
        [InterpolatedStringHandlerArgument("", "c", "l")] ref LogHandler message); // formats only if enabled
    public void Event(in LogEntry entry);                                      // structured (generated callers)
    public void SetLevel(LogCategory c, LogLevel l, TimeSpan? expiry = null);
}

static partial class NavLog                                                    // generated bodies
{
    [LogEvent(Id = 101, Category = LogCategory.Nav, Level = LogLevel.Note,
              Template = "walkTo ({x},{y}) unreachable on {map} after {ms} ms")]
    public static partial void Unreachable(BotLogger log, int x, int y, int map, int ms);
}
```

Rules: no `string.Format` or interpolation outside a `LogHandler`; no `ToString()` of collections in log
arguments (log a count and an id; the UI shows the collection); `Crit` only for "a human should act".

### 12.3 HTTP

| method | path | body / query | returns |
|---|---|---|---|
| GET | `/api/v3/bots/{id}/log` | `level, category, from, to (ISO date-time), max, text` | formatted lines with dates (ring, then files) |
| GET | `/api/v3/bots/{id}/log-levels` | - | `{category: {level, expiresAt}}` |
| PUT | `/api/v3/bots/{id}/log-levels` | `{category, level, expiryMinutes?}` | updated map (persisted) |
| POST | `/api/v3/bots/{id}/packet-capture` | `{enabled, opcodes?, depts?, minutes: 15, maxMb: 64, format: text or binary}` | capture state |
| GET | `/api/v3/bots/{id}/packet-capture` | - | state and file list |
| GET | `/api/v3/log/stats` | - | lines, bytes, drops, collapsed per bot and category (last 1/10/60 min) |

The WebSocket topic `log` (section 8.4) streams entries rendered as `{t, level, category, text, repeat}`,
at most 20 lines/s per subscription; Debug/Trace only while the subscriber asked for them.

### 12.4 Lua budget and gating

Per layer: 5 `dbg.*` lines per tick and 60 per minute; the excess is counted and reported once per minute
as `script lines dropped: N`. `dbg.log(fmt, ...)` evaluates its arguments in Lua (scalars) and formats in
C# only when enabled; text arguments over 512 bytes are truncated with a marker.

## 13. UI requests (from the UI branch, `redesign-2026-11-ui`)

Additions and clarifications the web UI (`next/ui`, `docs/UI_STATUS.md`) builds against. The mock host
(`next/ui/mock/server.mjs`) implements every item below as written; the UI degrades gracefully when the real
host lacks one (it shows "not provided yet" instead of failing). Items are numbered for reference.

### 13.1 Serving and transport

- **U1. Static UI.** The host serves the built UI (`next/ui/dist`, path configurable, e.g. `UI_DIR`) at `/`,
  with an SPA fallback to `index.html` for unknown non-`/api` paths. The bundle is self-contained (no CDN, no
  network fonts); building it needs Node only at build time (`npm ci && npm run build` in `next/ui`).
- **U2. WebSocket path and auth.** The hub lives at **`/api/v3/ws`** (section 8.4 says `/ws`, under the
  `/api/v3` base). Browsers cannot set `Authorization` on a WebSocket, so the bearer token is also accepted as
  the query parameter **`?token=<token>`** on the upgrade request.
- **U3. Envelope.** Server -> client messages are `{"topic", "bot"?, "full": true, "data"}` for a full frame and
  `{"topic", "bot"?, "full": false, "patch": [RFC 6902 ops]}` for a delta against the previous frame of the same
  subscription. `full` is used for the state topics (`fleet`, `debug`, `sensors`); event topics (`transition`,
  `log`, `alert`) are always `{"topic", "bot", "data"}`. After a (re)subscribe the first message of a state
  subscription is full. The UI also accepts messages without `full` as full frames.
- **U4. JSON naming.** camelCase; enums as strings (`"refused"`, `"InZone"`, `"value"`), read case-insensitively;
  resources (`claims`) as a string array, not the `Resource` flags integer; timestamps ISO 8601 UTC.

### 13.2 Host info and the wire setting (operator requirement 2026-11-25: 2016 and 2026 packet sets per bot)

- **U5. `GET /api/v3/host`** -> `HostInfo`:
  `{ name, apiVersion: 3, wires: ["2016","2026"], defaultWire: "2016", defaultLogin?: {host, loginPort},
  classes?: [{id, name}], channels?: ["stable","candidate","operator-test"], mock?: false }`.
  `defaultWire` is the host default (`FIESTA_WIRE`, else `2016`); `classes` comes from client data (ClassName)
  for the spawn form's "create character" picker; `defaultLogin` pre-fills the spawn form (the UI hardcodes no
  login endpoint).
- **U6. `wire`** (string, one of `HostInfo.wires`) is a field of `SpawnRequest` (optional, default
  `HostInfo.defaultWire`), of `BotRecord` / the `bots` table, and of `BotSummary` / `BotDetail`.
- **U7. `PATCH /api/v3/bots/{id}`**, body any of `{host, loginPort, username, password, character, autostart,
  wire}` -> `BotSummary`. Changes the persisted bot / profile configuration; wire, endpoint and credentials apply
  at the next login (the UI says so). 400 on an unknown wire.

### 13.3 BotSummary / BotDetail fields the fleet and bot pages show

`BotSummary` = section 8.1 plus `character, username, host, loginPort, wire, classFamily, brain, channel,
versionHash, autostart, packetLog, paused, ticksPerSec`,
`layerSince: {layer: ISO time the layer's top-level state was entered}`,
`lastTransition: {at, layer, from, to, reason} | null`,
`vitals: {hp, maxHp, sp, maxSp, exp, expNext, money, bagFree, bagSize, x, y}` (all from the agent snapshot).
`BotDetail` = `BotSummary` + `record` (persisted config without the password) + `logLevels` + `packetCapture`.

### 13.4 Debug frame (section 7.1) clarifications

- **U8.** The example uses `"tick"` twice (tick number and tick stats). The tick number stays `tick`; the stats
  object is **`tickStats`** `{p50us, p99us, allocP95B, steps: {step: us}}`.
- **U9.** `blackboard` is a JSON object `{bot: {}, layer: {<layer>: {}}, state: {<layer>: {}}}`, not a string.
- **U10.** `LayerTrace` adds `enteredAtPath` (ISO time per element of `path`, outermost first) and `budgetMs`
  (the active state's budget), so the UI shows time in state and stall vs budget for nested states.
- **U11.** `ConditionTrace` adds `state` (the state on the active path that owns the transition), `depth` (its
  index in `path`), `index` (0-based position of the transition in that state's `transitions`) and `holdTarget`
  (the transition's `hold`, ms). The live graph matches an evaluation to its edge by `(state, index)`.
- **U12.** `TransitionRecord` adds `depth`, `index` (as above; `-1` for operator transitions) and `leafFrom` (the
  leaf state active when it fired). `kind` is the condition kind in lower case (`value, lua, event, action,
  after, every, stuck, procedure, job, always, all, any, not`) or `operator`.
- **U13.** `DebugFrame` adds `paused` (brain paused by the operator).

### 13.5 API manifest (section 8.2 `GET /api-manifest`) shape for the editor

`{ apiVersion, functions: [{module, name, signature, params: [string], returns, cost, alloc, doc, resource?,
aliasOf?}], constants: {module: {NAME: value}}, events: [{name, arg1, arg2, doc}], sensors: [{name, returns,
doc}], resources: [...], procedures: [...], jobs: [...], actionKinds: [...] }`.
Global aliases are listed with module `_G` and `aliasOf` (`canUse` -> `skills.canUse`, `log` -> `dbg.log`).
`sensors` is the `SensorRegistry` content (what `value:` and `progress:` accept); `events` is the `ev.*` table of
section 3. The editor's pickers (event, sensor, resource, procedure, job, operator action kind) and its
completion / hover / signature help come only from this manifest; nothing is baked into the UI.

### 13.6 Brains and libraries

- **U14. Definition text.** `POST /brains/{name}/versions` and `POST /brains/lint` take `definition` as a string
  (YAML or JSON). `GET /brains/{name}/versions/{hash}` returns `definition` as the stored text,
  `modules: {path: source}` (brain-private modules), `libraries: {path: {hash, source}}` (pinned library
  versions) and `lint` (findings at creation; only warnings, since errors refuse the version).
  `BrainVersionInfo` adds `libraries: {path: hash}` and `warnings` (count). A refused create is 400 with the
  problem body carrying `lint: LintResult[]`.
- **U15. `ui` key.** A definition may carry a top-level `ui` object, `ui.layout["layer:<name>[/<state>...]" |
  "machine:<name>[/...]"] = {state: [x, y]}` (the editor's node positions). Runtime and linter ignore it; it is
  part of the stored text and so of the hash.
- **U16.** `LintResult` adds `column` (0-based, optional).
- **U17. `GET /brains/{name}/assignments`** -> `AssignmentRecord[]` (newest first) over all bots, for the
  brain-wide timeline. `AssignmentRecord` adds `brain`.
- **U18. `GET /brains/{name}/metrics?versions=h1,h2`** -> `[{hash, perClass: [{classFamily, bots, hours,
  expPerHour, deathsPerHour, questsPerHour, stuckExitsPerHour, tickP99Us, stateShare: {"<layer>/<state>":
  0..1}}]}]` (REDESIGN 9.2).
- **U19. Library paths in URLs** are one URL-encoded segment (`/libraries/user%2Fikaron%2Fheal/versions`).
  `GET /libraries/{path}/versions/{hash}` -> `{hash, path, created, author, message, source}`; `GET /libraries`
  items add `readOnly` (`lib/*`) and `versions` (count); `POST /libraries/lint {path, source}` ->
  `LintResult[]` (lint before saving).
- **U20. "Update brains that use it"** is done by the UI as a new version per brain with the library reference
  unpinned (section 6: unpinned references resolve to the latest at creation). No extra endpoint.

### 13.7 Observation

- **U21.** `GET /bots/{id}/inventory` -> `{bagSize, money, slots: [{slot, itemId, name, lot, category}],
  equipped: [{equipSlot, itemId, name}]}`; `GET /bots/{id}/quests` -> `[{id, name, state: active|ready|done,
  objectives: [{kind, mobId, itemId, have, need}]}]`; `GET /bots/{id}/skills` -> `[{id, name, rank, tags,
  readyInMs, spCost, canUse, whyNot}]` (`canUse` / `whyNot` with ourselves as target). Names from client data.
- **U22. Replay frames.** `GET /bots/{id}/recordings/{rid}/frames?from=&to=&every=` -> `{debug: DebugFrame[],
  sensors: SensorFrame[]}` produced by replaying the recording on the host (the UI cannot replay `.botrec`
  itself). A recording with `kind: "ring"` may expose the in-memory frame history of the last minutes.
- **U23.** `SensorFrame` entities add `name` (read-time name) and optional `assist` (learned family-assist
  radius); the frame may add `nav: {rects: [[x, y, w, h]]}` (nav mesh rectangles near us) for the overlay.
- **U24.** Log lines (HTTP and the `log` topic) are `{t, level, category, text, repeat}` with `level` one of
  `C, N, I, D, T` (full lower-case names also accepted); the `log` subscription's `level` accepts both;
  `PUT /log-levels` takes lower-case names.
- **U25.** `POST /bots/{id}/actions` returns `{id, state}`.

### 13.8 Host implementation status and deviations (core branch, 2026-11-25)

The host in `next/src/Fiesta.Bot.Server` implements U1-U25 with these notes:

- **U1** the UI is served from `UI_DIR`, default `next/ui/dist` beside the solution; without a build `/` says so.
- **U2/U3** `/api/v3/ws` (also `/ws`), `?token=` accepted when `BOT_API_TOKEN` is set. State topics send a full frame,
  then RFC 6902 patches (objects diffed recursively, arrays element-wise when the length is unchanged, replaced
  otherwise). `debug` frames are published at 5 Hz while a debug or sensors subscription for the bot exists, 1 Hz
  otherwise; condition traces (`layers[].conditions`) are recorded only while someone subscribes to `debug` /
  `sensors` for that bot (they cost an allocation per evaluation), so `GET /debug` without a subscription returns
  empty `conditions`.
- **U5** `classes` are the base classes found in client data (the first class of each line); `clientData` and
  `tickWorkers` are extra fields.
- **U18** metrics are computed from the bots running the version now (no history store yet): `questsPerHour`,
  `stuckExitsPerHour` are 0 and `stateShare` is empty for now.
- **U21** the inventory adds `hpStones` / `spStones`; quests add `status` (the raw quest log status byte).
- **U22** `kind: "ring"` (id `recent`, the last 600 debug frames) is served; replaying a `.botrec` into frames on
  the host answers 501 for now. `POST /bots/{id}/recordings {enabled}` starts / stops a `.botrec` recording (an
  addition).
- **U25** `POST /bots/{id}/actions` takes `kind` from the manifest's `actionKinds` (`MOVE, MOVE_DIRECT, STOP, CAST,
  BASH, STOP_BASH, TARGET, USE, SAY, OPEN_SHOP, TALK, GATE, REVIVE, BUY, SELL, LOOT, RAW`) and string/number `args`
  (`actionArgs` in the manifest lists them per kind), and returns `{id, state, rejected}`.
- The manifest adds `actionArgs`. Function entries carry no `resource` yet.
- Wire setting: see `docs/WIRE_PROFILES.md`.
- Additions: `GET /bots/{id}/tick` samples add `publishUs` / `publishAllocBytes` (the debug publishing step is
  measured apart from the tick's own allocation); `PUT /log-levels` without `expiryMinutes` persists the level
  with the bot.

## 14. Game view (the bot page's "Game" tab; operator request 2026-11-25)

A light mirror of what the game client shows for one bot: vitals, stats, skill bar with cooldown sweeps, buffs,
bag and equipment with the client's own icons, and a minimap with the bot, nearby entities and its path.

Design rule (operator): no big blobs. The server sends ONE snapshot when the view subscribes, then only small
deltas; the browser keeps the model and reassembles it. Everything static (names, icons, map art, skill
cooldown lengths) goes by id and is fetched once through cacheable GET endpoints, never repeated in the stream.

### 14.1 Stream: WebSocket topic `game`

Subscribe: `{"op":"sub","topic":"game","bot":"<id>"}`; unsubscribe: `{"op":"unsub","topic":"game","bot":"<id>"}`.
Re-subscribing is the resync (the server answers with a fresh snapshot).

Server -> client (this topic does not use the RFC 6902 envelope of U3; it has its own op list):

| message | meaning |
|---|---|
| `{"topic":"game","bot","t":"snap","seq":n,"now":ms,"data":GameSnapshot}` | full state; the first message after a subscribe, and again after a map change into a new session |
| `{"topic":"game","bot","t":"d","seq":n,"now":ms,"ops":[op...]}` | changes since `seq - 1`; empty `ops` = keep-alive (every 5 s, carries `now` for clock sync) |
| `{"topic":"game","bot","t":"off","seq":n,"now":ms,"reason":r}` | the view has no live state; sent once per outage, only when the bot is really out of the world: `reason` `"left-world"` = stopped / offline, `"disconnected"` = the connection dropped and the bot is logging in again, `"host-stopping"` = the host is restarting (the WebSocket closes next; reconnect and re-subscribe). A map change through a gate (a zone handoff) is NOT an outage: no `off`, the next `snap` / `map` op follows. A viewer that subscribes while the bot is in the world gets no `off` first, only the `snap` (within 100 ms). The view keeps its last state greyed; a `snap` follows when the bot is back |

`seq` increases by one per message of this bot's stream. A gap (a dropped message) means: re-subscribe.
`now` and every time in the stream are the host's clock in unix milliseconds; the browser keeps
`offset = Date.now() - now` and animates against it.

Rates: changes of self / stats / buffs / skills / bag / equipment / target / path are coalesced to at most 10
messages per second; entity positions go at most 4 times per second and only for entities that moved 4 units or
more since their last sent position; nothing is sent when nothing changed. The state is captured on the bot's
tick from the world model's change counters into preallocated buffers (no allocation on the tick); diffing and
JSON happen on the host's WebSocket pump thread, once per bot, shared by every subscriber.

Cooldowns: a skill's `readyAt` (unix ms) and the cooldown length `durMs` are sent once when the cooldown starts;
the browser draws the sweep `1 - (readyAt - nowServer) / durMs` until `readyAt`. Buff end times likewise
(`endsAt`, 0 = no end).

#### GameSnapshot

```jsonc
{
  "self": { "handle": 12009, "name": "NextBotA", "classId": 1, "level": 4, "exp": 139, "prevExp": 0, "nextExp": 200,
            "hp": 104, "maxHp": 114, "sp": 41, "maxSp": 41, "money": 43, "fame": 0, "statPoints": 0,
            "hpStones": 0, "spStones": 0, "dead": false, "battle": false, "mounted": false,
            "x": 7328, "y": 6321, "dir": 90, "mapId": 0 },
  "stats": { "str": 14, "end": 12, "dex": 9, "int": 3, "spr": 5, "wcMin": 20, "wcMax": 25, "def": 30, "aim": 12,
             "eva": 9, "maMin": 0, "maMax": 0, "mdef": 8, "maim": 0, "meva": 0 },     // -1 = not received yet
  "abstates": [[17, 1764073200000]],            // [wire abstate id, endsAt (0 = no end)]
  "skills":   [[6000, 0, 0], [1001, 1764073201234, 3000]],   // [skill id, readyAt (0 = ready), durMs]
  "bag":   { "size": 48, "pages": 2, "cells": [[0, 3097, 1], [14, 2522, 1]] },      // [slot, item id, lot]
  "equip": [[12, 250]],                         // [equip slot, item id]
  "target": 6993,                               // handle, -1 none
  "path": [[7400, 6300], [7600, 6250]],         // the remaining waypoints of the current walk, [] = standing
  "ents": [[6993, "m", 1101, 7450, 6290, 100, 2, null], [21214, "g", 0, 17607, 13887, 100, 0, "Rou"]]
}
```

`ents` rows: `[handle, kind, id, x, y, hpPct, flags, name]`. `kind`: `m` mob, `n` NPC, `p` player, `g` gate,
`d` drop. `id`: the MobInfo id for mobs / NPCs / gates, the item id for drops, the class id for players.
`flags` bits: 1 dead, 2 aggro (hit us or targets us), 4 our target, 8 huntable. `name`: players' names and the
gates' destination text; `null` otherwise (the browser takes the mob name from the static data).

#### Delta ops

Each op is an array whose first element is its code.

| op | meaning |
|---|---|
| `["self", {field: value, ...}]` | changed fields of `self` (same names as the snapshot) |
| `["stats", {stat: value, ...}]` | changed stats |
| `["ab", id, endsAt]` / `["ab-", id]` | buff / debuff on or refreshed / gone |
| `["sk", id, readyAt, durMs]` / `["sk-", id]` | skill learned or its cooldown changed / skill gone |
| `["bag", slot, itemId, lot]` | a bag cell changed (`itemId` -1 = empty) |
| `["bagSize", size, pages]` | bag size changed |
| `["eq", slot, itemId]` | an equipment slot changed (-1 = empty) |
| `["tgt", handle]` | target changed (-1 none) |
| `["path", [[x, y], ...]]` | new remaining path (`[]` = stopped) |
| `["map", mapId]` | map changed: drop all entities and the path (new `e+` ops follow) |
| `["e+", h, kind, id, x, y, hpPct, flags, name]` | an entity appeared (or its row changed kind / id) |
| `["e-", h]` | an entity left view |
| `["ep", h, x, y]` | an entity moved |
| `["eh", h, hpPct]` | an entity's HP percentage changed |
| `["ef", h, flags]` | an entity's flags changed |

Positions are world units rounded to integers. World Y grows upward (north on the minimap).
`self.dir` is the facing in whole degrees 0..359: 0 = east (+x), counter-clockwise, 90 = north (+y).

Skills in the stream are the ACTIVE skill list (CLIENT_SKILL). Passive skills arrive in a separate list that is not
decoded yet, and their ids overlap the active ids (PassiveSkill id 0 is not ActiveSkill id 0), so a skill id alone
can never mean "passive"; passives will get their own `passives` field in the snapshot when decoded (open).

Names: the US client data carries some untranslated names as question marks (`"?? ??"`, e.g. internal skills
like WorldLink_N); the static routes then return the InxName as `name`. Some skills (event / item-use skills)
have no icon in the client data (`IconFile` "-"), so `icon` is false and the tile shows text.

Before a host restart every connection also gets `{"topic":"host","data":{"state":"restarting"}}` (not tied to a
subscription). `POST /api/v3/host/shutdown` stops the host gracefully (viewers notified, bots logged out cleanly).

### 14.2 Static data (cacheable)

All responses carry `ETag` (from the client-data fingerprint and the id) and `Cache-Control: public,
max-age=86400`; `If-None-Match` answers 304. The browser fetches `GET /api/v3/static` once per page load and
appends `?v=<fingerprint>` to every static and icon URL, so a changed client data set or a host fix to these
routes (the fingerprint includes the host's output version) is picked up at once instead of after the max-age. Unknown ids answer 404. Names come from the bot host's client data
(the client the bots play), so they match what the game shows.

| route | response |
|---|---|
| `GET /api/v3/static` | `{fingerprint, items, skills, mobs, abstates, maps}` (counts; the fingerprint changes when the client data changes) |
| `GET /api/v3/static/items/{id}` | `{id, inx, name, type, class, equip, demandLv, maxLot, buyPrice, sellPrice, icon}` (`icon`: true when the client ships art) |
| `GET /api/v3/static/skills/{id}` | `{id, inx, name, category, rowKey, step, maxStep, sp, hp, cooldownMs, groupCooldownMs, range, castMs, description, icon}`; `category` `"production"` when the client's production tables (Produce recipes, Gather) name the id, else `"active"`; an id without an ActiveSkill row answers 404 (the UI files it under Other); `rowKey` = the InxName without its rank digits (one row per skill family, highest learned step) |
| `GET /api/v3/static/abstates/{id}` | `{id, inx, name, description, sort, icon}` (`id` = the wire abstate index; `sort` BUFF / DEBUFF / ...) |
| `GET /api/v3/static/mobs/{id}` | `{id, inx, name, level, maxHp, npc, playerSide}` |
| `GET /api/v3/static/classes/{id}` | `{id, name}` |
| `GET /api/v3/static/maps/{id}` | `{id, mapName, name, minimap, world: {w, h} or null, cover: {x0, y0, x1, y1} or null}` |
| `GET /api/v3/static/{items|skills|abstates|mobs}?ids=1,2,3` | `{"1": {...}, "2": {...}}` (batch; missing ids omitted) |
| `GET /api/v3/icons/{item|skill|abstate}/{id}.png` | the icon cut from the client's atlas (32x32 or the atlas' cell size), 404 when none |
| `GET /api/v3/static/maps/{id}/minimap.png` | the client's minimap art for the map, 404 when none |

Minimap placement: the image covers world `[cover.x0, cover.x1] x [cover.y0, cover.y1]`; the image's top edge is
`cover.y1` (world Y grows upward, image Y downward). `world` is the walkable grid's extent (0..w, 0..h); `cover` is
null when the client has no placement row for the map (then the image covers the whole `world`).
