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

## BYO / no copyrighted data

Like `fiesta-docker`, this repo ships **no** copyrighted game data and **no**
cipher table. The XOR table is provided at runtime via `XOR_TABLE_HEX` or
`XOR_TABLE_PATH`. Data-file checksums for zone entry are computed against the
operator's own `ServerSource` tree.
