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
| headless smoke driver | `node next/ui/mock/cdp.mjs <steps.mjs>` drives headless Edge/Chrome over CDP (screenshots, clicks, console) |
| dependencies | 6 runtime + 4 dev packages, exact versions in `package.json` / `package-lock.json`; `node_modules` ~150 MB (Monaco is most of it) |

## Pages and features vs REDESIGN 9 and the operator's asks

| REDESIGN 9 / ask | status |
|---|---|
| **Fleet** page: class/level/map, active state per layer with time in state **and date**, last transition + reason, ticks/s, p99, deaths/h, exp/h, version + channel, errors | done (live over `fleet` topic, polling fallback) |
| Fleet controls: spawn, start, stop, relog, pause/resume brain, apply version, edit config, delete | done (delete via in-button two-step confirm, no dialogs) |
| **wire 2016 / 2026 per bot** (operator 2026-11-25) | done: select in spawn and edit forms (options + default from `GET /host`), shown in the fleet table and the bot header; spec U5-U7 |
| **Bot** page header with live vitals; tabs debugger, sensors, actions, log, packets, inventory, quests, skills, metrics (+ versions, replay) | done; inventory / quests / skills need U21 on the real host |
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

## Open

- Run against the real host as its endpoints land; adjust normalisers (`src/api/client.ts`) where its JSON
  differs from section 13.
- The host must serve `next/ui/dist` (U1) and build it in the image (Node at build time only).
- Visual check in the operator's Chrome when the extension is connected.
- Large brains: graph layout is a simple layered BFS (no edge-crossing minimisation); positions persist once dragged.
- No undo history in the graph beyond Revert (Monaco has its own undo).
- Bundle is one ~4 MB chunk (Monaco); could be split if load time matters.
