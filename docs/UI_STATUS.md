# UI status (branch `redesign-2026-11-ui`)

The web console of the redesign (REDESIGN section 9): fleet, bot pages with the visual brain debugger, the
node-based brain editor with Lua, libraries, versions. Contract with the runtime: `docs/API_SPEC.md` sections
7, 8, 12 and **13 (UI requests)**. Last updated 2026-11-25.

## Where and how

| what | where |
|---|---|
| SPA source | `next/ui/src` (TypeScript, React 18, React Flow 12 `@xyflow/react`, Monaco 0.52, `yaml`, `luaparse`; Vite 5 build, no React plugin) |
| build | `cd next/ui && npm ci && npm run build` -> `next/ui/dist` (static, self-contained, ~4 MB JS, no CDN, works offline) |
| served by | the host at `/` (request U1); until then the mock serves it |
| mock host | `node next/ui/mock/server.mjs --port 5199 --static dist [--token T]`: every endpoint of API_SPEC 8 / 12.3 / 13, WebSocket with RFC 6902 deltas, fixture brains / libraries / bots, and a fake brain runtime that executes any brain definition's layers, machines and transitions (sensors and events are random numbers, not game data) |
| dev loop | `npm run mock` + `npm run dev` (Vite on :5198, proxies `/api` and the WebSocket to `BOT_HOST`, default the mock) |
| manifest in the mock | generated at start from `docs/API_SPEC.md` sections 2 and 3 (223 functions + 2 aliases, 34 events, 36 sensors), so it follows the spec |
| mock tests | `npm test` (node:test): manifest = 223 spec functions + 2 aliases, fixture brains lint clean, linter catches syntax / unknown API / bad targets, JSON patch + unified diff, the fake runtime records transitions with depth and index |
| headless smoke driver | `node next/ui/mock/cdp.mjs <steps.mjs>` drives headless Edge/Chrome over CDP (screenshots, clicks, console) |
| dependencies | 6 runtime + 4 dev packages, exact versions in `package.json` / `package-lock.json`; `node_modules` ~150 MB (Monaco is most of it) |

## Game tab (operator request 2026-11-25)

The first and default tab of a bot's page: a compact HUD of what the game client shows, laid out for a ~950 px
wide window (two columns; one column below 820 px).

| part | what it shows |
|---|---|
| HUD | level, name, map name + position, state flags (dead / combat / mounted / moving), money, HP / SP (LP when sent) bars with numbers, exp bar as progress within the level |
| minimap (canvas) | the client's minimap art placed by the map's `cover` rectangle, the bot as an arrow along its facing, the remaining path, mobs (aggro on us red with a ring, our target dashed ring, dead grey), players, NPCs and gates (diamonds) with name tags, scale bar, zoom 800 / 1600 / 3200 u / whole map, hover tooltip per entity |
| target line | our target's name, level and HP, and how many mobs are on us |
| buffs / debuffs | icons with time left (debuffs red-framed), tooltip with name and description |
| skill panel | tabs Active (default) / Passive / Production / Other like the client's skill window: one row per skill family (highest learned rank), in learn order; HP / SP soul stone counts first, then the skills with icons and a cooldown sweep + seconds left. The category (`production` from the client's Produce / Gather tables, else `active`) and the family key (`rowKey`) come from the host's static skill data (API_SPEC 14.2); a skill the host has no record for goes under Other; the host has no passive list yet, so Passive says "not decoded by the host yet" |
| inventory | bag pages as tabs (like the client's bag), 6 wide, icons, stack counts, tooltips (name, type, level, price); equipped items |
| stats | every stat the host sends (`-1` = not received yet shows `-`), unspent points |

Data path, per the operator's rule "no big blobs, deltas over the WebSocket, the browser reassembles":
- the `game` topic of API_SPEC **section 14** (defined by the runtime branch): one `snap`, then `d` op lists;
  `src/pages/bot/game/gameModel.ts` reassembles them with structural sharing (an op copies only the map it
  touches), a `seq` gap re-subscribes (fresh snapshot), `off` greys the view;
- static data (items, skills, abstates, mobs incl. NPCs / gates, maps) by id from `/api/v3/static/...`, every
  static, icon and map-art URL carrying `?v=<fingerprint>` (from `GET /api/v3/static`, fetched once) so new host
  data is picked up at once despite max-age: all ids
  asked for in one frame go out as one `?ids=` batch per kind, kept in memory for the page and in the HTTP cache
  (ETag / max-age); icons and map art are plain `<img>` / canvas images from their cacheable URLs;
- cooldowns and buff timers are end times on the host clock: one `requestAnimationFrame` loop updates only the
  elements that are counting down (CSS conic-gradient sweep through a custom property); no polling;
- the minimap draws from refs every animation frame and interpolates entity positions between updates, so
  movement never re-renders React; memoised tiles / slots re-render only when their own record changes.

Connection state: a grey overlay with a throbber above a centred, box-less "bot not connected" + the reason
(host restarting from the `host` notice, socket reconnecting, bot state, or the stream's `off` reason `left-world` /
`disconnected` / `host-stopping`, unknown reasons as sent) covers the Game, debugger, sensors and actions tabs. It is
absolutely positioned (never moves the layout), appears only when the down state lasts over 1.5 s and drops as
soon as a snapshot arrives; each off / on transition and seq gap is logged with `console.debug`. It replaces the
"bot left the world" line that used to be inserted above the HUD and flashed in and out (the operator's flicker).
The bot header is now two fixed rows that never wrap (live numbers changing width used to re-wrap it).
Checked on the real host (NextA: Active 14 / Production 1 rows, Passive "not decoded", all static URLs versioned) and
for 60 s on the Game tab and 30 s on the debugger: no element moved or resized, no overlay.

Measured on the mock: snapshot ~1.5 KB, deltas ~200 B on average at 4 Hz. The mock implements section 14
exactly (snapshot, ops, keep-alive, `off`, static routes with ETag / 304, icon and minimap routes).

## Pages and features vs REDESIGN 9 and the operator's asks

| REDESIGN 9 / ask | status |
|---|---|
| **Fleet** page: class/level/map, active state per layer with time in state **and date**, last transition + reason, ticks/s, p99, deaths/h, exp/h, version + channel, errors | done (live over `fleet` topic, polling fallback) |
| Fleet controls: spawn, start, stop, relog, pause/resume brain, apply version, edit config, delete | done (delete via in-button two-step confirm, no dialogs) |
| **wire 2016 / 2026 per bot** (operator 2026-11-25) | done: select in spawn and edit forms (options + default from `GET /host`), shown in the fleet table and the bot header; spec U5-U7 |
| **Bot** page header with live vitals; tabs game (default, above), debugger, sensors, actions, log, packets, inventory, quests, skills, metrics (+ versions, replay) | done; inventory / quests / skills need U21 on the real host |
| **Brain editor**: graph canvas per layer (states as nodes, transitions as edges labelled with condition kind, text and priority / order) | done (React Flow; drag, connect by dragging handle onto a state, Delete removes, auto layout, positions saved under `ui.layout`) |
| hierarchy: sub-machines (inline `machine` and named `use` machines), named machines list, breadcrumb drill-in, `^` pop-to-parent and `machine:state` targets | done |
| side panel: state `enter`/`tick`/`exit` as module function ref or inline Lua (Monaco), budget, progress (sensor / function / expression), minDwell, timeout, onError, claims, terminal, initial, rename, transition order | done |
| transition editor with condition kinds as forms: event picker, value picker from the sensor registry, Lua expression, action result, procedure, job, after / every, stuck, always, composites all / any / not (nested) + priority, hold, cooldown, reason template | done (pickers from the manifest) |
| validation results from the linter | done: host lint (debounced `POST /brains/lint`) listed and clickable (jumps to state / module line), markers on nodes and in Monaco; plus instant client-side Lua syntax + unknown-API checks |
| "save as new version", "apply to bot/channel" | done (save refuses with lint errors; apply to bots with keep-state + reason, or move a channel) |
| YAML source view | done (`brain.yaml` tab, two-way: graph follows every valid parse) |
| **Libraries**: module list with versions, Monaco with completion from the API manifest and the user's own libraries, "find usages" across brains, "update brains that use it" | done |
| user Lua helpers (e.g. `isGoodHealSkill`) editable in the UI | done (Libraries page; brain-private modules in the editor) |
| generic checks (`canUse` ...) from the C# library in completion / docs | done: completion, hover docs (signature, returns, cost, alloc, resource, alias), signature help, constants, `require` path completion, alias-aware completion of library exports (`local my = require "user/x"` -> `my.`) |
| **Versions**: per brain version list, definition diff + Lua diffs (Monaco diff editor, normalized YAML option), channels, assignment timeline, rollback, compare metrics (9.2: exp/h, deaths/h, quests/h, stuck exits/h, tick p99, share of time per state per class) | done; metrics need U18 on the real host; rollback lives on the bot's Versions tab |
| **Visual debugging** 9.3: live graph (active state per layer highlighted with time in state, edges flash when taken, recent-state trail, edges coloured by last evaluation with value and hold progress), claimed resources per layer | done |
| transition trace (last 200, reason, values, tick), click to jump the timeline | done (local frame history ~3 min, scrubber, "follow live") |
| condition panel: every outgoing transition of every state on the active path with last evaluation (true / false / not evaluated, value vs threshold, hold timer progress) | done |
| variables: blackboard scopes + `dbg.watch` values live with sparkline history, edit a blackboard value | done |
| sensors map: entities, aggro shapes (detect circle offset 0.4 r along facing), bash + skill reach rings, leash origins, path with waypoints, nav mesh overlay (U23), assist radii, threat heat map; pan / zoom / hover | done (nav / assist drawn when the frame carries them) |
| actions: pending + resolved handles with codes and latency; procedures with current step; operator action form | done |
| pause / step / force a state / edit a blackboard value | done |
| replay: a recording loaded into the same views and scrubbed | done against U22 (mock exposes its in-memory frame ring as a recording) |
| log tab: level / category / text / date filters, live follow, per-category runtime levels with 15 min expiry for debug/trace; packet capture settings + files; tick metrics flame strip | done |
| auth: bearer token setting (sent on HTTP and as `?token=` on the WebSocket) | done |

## Tested

Chrome via the Claude-in-Chrome extension: **not available** in this session (the extension reported "not
connected" three times), so the UI was driven in **headless Edge over CDP** (`mock/cdp.mjs`) against the mock
host, with screenshots checked by eye and the console read (no errors or exceptions; only Chromium's
"password field not in a form" note on the spawn dialog):

- fleet: 4 fixture bots live over the WebSocket, layer columns with time + date, wire badges, controls;
- spawn with wire 2026 + brain -> new row `UiTestBot ... 2026`; edit MockArcher's wire to 2026 -> toast
  "applies at the next login", badge updated, log line from the host;
- editor: select a state, edit budget (modified tag), connect `ok` -> `flee` by drag (new edge `ok#2`, transition
  panel opens), switch its condition to value compare `threat.onUs >= 3`, hold 500, reason template; break
  `survival.lua` (`local broken = = 3`) -> host lint `LUA_SYNTAX ...:19`, red squiggle, save disabled;
  completion after `skills.` lists the manifest functions with signatures; save a valid change as a new version
  (`leveller@eb985c448b06`), apply it to MockArcher (assignment recorded), compare page with metrics table,
  state-share bars, normalized YAML diff and per-module status;
- debugger: live graph with active state, conditions panel values, flashes; pause -> step 1 -> force
  `errand -> town` (operator transition in the trace), variables with sparklines, trace click jumps the
  timeline; replay of the recording ring in the same views; sensors map; log; metrics flame strip; versions tab.

Real host: the runtime branch (`redesign-2026-11-core`, last checked at `9c9ac84`) has no HTTP endpoints yet
(`Program.cs` builds an empty app), so nothing ran against it; its wire profile naming (`"2016"`/`"2026"`,
default `FIESTA_WIRE` else 2016) matches U5/U6. Point the dev server at it with
`BOT_HOST=http://127.0.0.1:<port> npm run dev` once endpoints exist.

### Game tab against the real host (127.0.0.1:5300, bot NextA on the local stack)

Checked in Chrome (own tab; the operator's tab untouched): the `game` topic delivers the snapshot and keep-alives,
the tab shows NextBotA (Lv 4, Roumen, HP 104/114, SP 41/41, money 43, 4 unspent points, 19 skills, 15/48 bag
with the client's item icons and tooltips, the equipped weapon, stats), the minimap shows the Roumen art with
the town NPCs and gates on it at the right places and the bot at the map's south edge. No console errors.
Not yet seen live: cooldown sweeps, buffs, aggro, path and entity movement (the bot stood idle in town); those
were checked on the mock.

## Open

- Run against the real host as its endpoints land; adjust normalisers (`src/api/client.ts`) where its JSON
  differs from section 13.
- The host must serve `next/ui/dist` (U1) and build it in the image (Node at build time only).
- Visual check in the operator's Chrome when the extension is connected.
- Large brains: graph layout is a simple layered BFS (no edge-crossing minimisation); positions persist once dragged.
- No undo history in the graph beyond Revert (Monaco has its own undo).
- Edge labels close to a node can still slip partly under it on dense graphs (labels render below nodes).
- Bundle is one ~4 MB chunk (Monaco); could be split if load time matters.
- Game tab, needs from the runtime: skill names come through as `?? ??` for many skills (text not decoded from
  the client data's encoding) and many skills have `icon: false`; the convention of `self.dir` (the UI draws it as
  degrees, 0 = +x / east, counter-clockwise, world y up) should be written into section 14; `index.html` is
  served without `Cache-Control`, so a browser can keep a stale bundle after a rebuild (serve it `no-cache`, and
  `/assets/*` as immutable).
