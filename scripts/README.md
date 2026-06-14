# Behaviour scripts (Lua)

Upload a Lua script, apply it to a bot, and the bot **loops it** — handling
low-latency reactions the HTTP round-trip can't (process every hit, listen to chat,
"HP-stone when low", "skill when X"). A new apply **replaces** the running script.

Engine: **MoonSharp** (pure-managed Lua, sandboxed — no `io`/`os.execute`/`require`).
Each bot runs its script on its own dedicated thread (a single-threaded message pump),
so scripts never race the network layer.

## Workflow
```bash
# 1. upload to the library (compile-checked; 400 on a Lua syntax error)
curl -X POST :5097/api/scripts -H 'content-type: application/json' \
  -d "{\"name\":\"auto_grind\",\"source\":\"$(cat scripts/auto_grind.lua | jq -Rsa .)\"}"
# 2. apply to a bot and loop it
curl -X POST :5097/api/bots/b1/script -d '{"name":"auto_grind"}'
# 3. watch it
curl :5097/api/bots/b1/script            # state, ticks, events handled, last error
curl :5097/api/bots/b1                    # snapshot incl. hp/sp/script
curl -N :5097/api/bots/b1/logstream      # LIVE tail (NDJSON {"line":...}); ?tail=N backfills
# 4. stop / replace
curl -X POST :5097/api/bots/b1/script/stop
```
Apply also accepts inline `{"source":"..."}` for build-on-the-fly iteration, plus
`"tickMs":N` (loop interval, default 250) and `"trace":true` (log every `bot.*` call —
noisy, watch it on `/logstream`).

## Logging & watching it run
- `log(msg)` and Lua's built-in `print(...)` both go to the bot log → the host console
  → the live `/logstream`. So `curl -N :5097/api/bots/b1/logstream` tails everything the
  script (and the engine) emits, in real time.
- `trace:true` on apply logs `call bot.<fn>(args)` before every action/getter — tail it
  on `/logstream` to see exactly what the script is doing.

## Script contract
Define any subset of these globals — the runner calls the ones present:

| Function | When |
|---|---|
| `on_start()` | once when applied |
| `tick()` | every `tickMs` (default 250) |
| `on_chat(msg)` | nearby chat — `{handle, name, text}` |
| `on_hit(ev)` | combat hit — `{attacker, defender, damage, restHp, self}` |
| `on_cast_fail(reason)` | cast rejected (0x0FC9 SP, 0x0FCA range, …) |
| `on_hp(hp, max)` / `on_sp(sp, max)` | self HP/SP changed |
| `on_player(p)` / `on_player_left(handle)` | nearby player appeared / left |
| `on_map(map)` | map changed |
| `on_move_fail(x, y)` | server snapped us back |
| `on_stop()` | once when stopped/replaced |

## The `bot` global (+ `log(msg)`)
**Actions** (call with dot syntax): `say whisper cast castGround attack autoAttack
stopAttack heal useItem equip gm soulstoneHp soulstoneSp target untarget walk walkTo
travelTo stopTravel follow stopFollow useGate townPortal party* friend*`.

**State/perception**: `hp() sp() maxHp() maxSp() hpPct() spPct() x() y() map()
selfHandle() mounted() walkSpeed() phase() inZone() now()` and
`nearestMob() nearbyMobs() nearbyPlayers() gates() inventory() equipment()
playerByName(name)`.

See `auto_grind.lua` and `town_buff.lua` for working examples.

## State machines (compose behaviours)
A state machine is just a script that calls the built-in `statemachine(states, initial)`.
Each state is a table of callbacks; the engine runs the current state's `tick`/`on_*`
and switches when its `next()` returns another state's name (running `on_exit`/`on_enter`):

```lua
statemachine({
  roam   = { next = function() if bot.nearestMob() then return "fight" end end },
  fight  = { tick = function() bot.attack(1500, bot.nearestMob()) end,
             next = function() if not bot.nearestMob() then return "roam" end end },
}, "roam")
```
Apply it (same runtime as a plain script; the status shows the live state):
```bash
curl -X POST :5097/api/bots/b1/statemachine -d '{"name":"grind_sm"}'
curl :5097/api/bots/b1/script        # -> status.smState = "fight"
```
Per-state callbacks: `on_enter, tick, next, on_exit` plus any event handler
(`on_chat`, `on_hit`, `on_hp`, …) — events dispatch to the current state.
**Cross-tree transitions / per-class trees** fall out: compose several state tables into
one and a `next()` may return *any* state's name. Examples: `grind_sm.lua` (roam→fight→
recover), `guild_buff_sm.lua` (idle→buff, chat-driven).
