# ik-fiesta-bots

A bot-client framework for Fiesta Online. Runs **multiple synthetic clients in
parallel**, controlled over an HTTP API. Built to park a buffing priest in town,
party players on demand, and (later) help out in instances with configurable
class/level/gear loadouts.

Sibling of `ik-fiesta-api`, `OPTool-Reloaded`, `fiesta-proxy`, `fiesta-docker`.
See [`PROJECT_PLAN.md`](PROJECT_PLAN.md) for the design, decisions, and working log.

## Status

Early scaffolding. The protocol foundation (login → WM → zone, the `[1801]`
data-file-checksum anti-cheat) is proven in Python (`ik-fiesta-scripts`) and is
being ported to typed C# here.

## Layout

- `lib/FiestaLib-Reloaded/` — submodule: typed packet structs + cipher interface.
- `src/Fiesta.Bot/` — core library (transport, login chain, zone entry, session,
  behaviors, loadouts, account provisioning).
- `src/Fiesta.Bot.Host/` — ASP.NET minimal-API host + multi-bot manager + web UI.

## Build

```bash
git submodule update --init --recursive
dotnet build
```

## Control API

Run the host (`dotnet run --project src/Fiesta.Bot.Host`, needs the BYO XOR
table) and drive bots over HTTP. Swagger at `/`.

**Lifecycle**

| Method & path | Body | Does |
|---|---|---|
| `POST /api/bots` | `{host, username, password\|passwordMd5, slot?, create?, charName?, class?, ...}` | Spawn a bot; runs login→WM→zone in the background and stays in zone until stopped. |
| `GET /api/bots` | — | List bots with status. |
| `GET /api/bots/{id}` | — | One bot's status: phase, connection counters, `nearbyPlayers`, `lastChat`, recent log. |
| `POST /api/bots/{id}/stop` | — | Stop and remove a bot. |

**In-zone actions** (manual control — the same seam a future Lua/LLM controller
drives). All require the bot to be `InZone` (else `409`).

| Method & path | Body | Sends |
|---|---|---|
| `POST /api/bots/{id}/say` | `{text}` | Local chat (`ACT_CHAT_REQ`). |
| `POST /api/bots/{id}/cast` | `{skill, target}` | Cast a skill on a target handle (`BAT_SKILLBASH_OBJ_CAST_REQ`). |
| `POST /api/bots/{id}/use-item` | `{slot, invenType?}` | Use an inventory item by slot (`ITEM_USE_REQ`). |
| `POST /api/bots/{id}/gm` | `{command}` | Issue a GM command over chat (`&` prefix added if omitted). Needs the account to be GM (`nAuthID=9`). |

GM examples (Gamigo/NA2016 files): `levelup 46`, `makeitem SafeProtection01`,
`learnskill 1580`, `getmoney 1000000`. See `PROJECT_PLAN.md` for the full GM
reference and the Endure-buff IDs.

## BYO / no copyrighted data

Like `fiesta-docker`, this repo ships **no** copyrighted game data and **no**
cipher table. The XOR table is provided at runtime via `XOR_TABLE_HEX` or
`XOR_TABLE_PATH`. Data-file checksums for zone entry are computed against the
operator's own `ServerSource` tree.
