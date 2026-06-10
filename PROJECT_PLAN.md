# ik-fiesta-bots — project plan & working log

> Working tracker for the Fiesta bot-client. Free-form, refresh as things change.
> This file is the running log of **what we decided and why** so context survives
> across sessions. Sibling of `ik-fiesta-api`, `OPTool-Reloaded`, `fiesta-proxy`,
> `fiesta-docker`.

## Goal / vision

A bot-client framework that runs **multiple bots in parallel**, controlled over
an **HTTP/API** (same shape as OPTool's host). Concrete use cases, in order:

1. **Buffing priest in town** — a bot parked in a town zone that buffs players.
2. **On-demand party** — ask a bot to party you when you want to run instances.
3. **(future) Join to help** — bots that come along and actually fight in
   instances (e.g. DragonTomb) using a configured class/level/gear loadout.

The bot is a *synthetic game client*: it speaks the real client↔server (c2s)
protocol, logs all the way into a zone, and then drives gameplay packets
(buffs = skill casts, party invite/accept, movement, etc.).

## Decisions (locked)

| Decision | Choice | Why |
|---|---|---|
| **Stack** | **C# / .NET 10** | Reuse FiestaLib-Reloaded's typed structs for *every* gameplay packet (Party/Skill/Mover/Char/Avatar) + the cipher abstraction. Matches the whole ecosystem (OPTool, proxy, api). Port the proven Python login chain. |
| **Zone entry** | **Build [1801] MAP_LOGIN_REQ from scratch** | Construct chardata from typed structs + computed checksums, per-bot, no capture dependency. Verify against the live zone the OPTool way (REQ→ACK: `[1804]` until correct, `[1038]` when right). |
| **Account provisioning** | **ik-fiesta-api master key** (`X-Api-Key` → `POST /api/accounts`) | Decouples the bot from the DB schema; reuses the *verified* `usp_User_insert` path; respects the hard guardrail "don't touch mssql/SA directly without authorization". Needs a small API addition to grant in-game GM level on create. |
| **Gearing** | **In-game GM commands**, driven by JSON loadout templates | Bot account is created with GM/admin level, then the bot self-gears once in-zone by issuing GM commands (set level, give item) per its active loadout. |

## Architecture

```
ik-fiesta-bots/
  lib/FiestaLib-Reloaded/        submodule (pinned fad95db) — typed packet structs + cipher iface
  src/Fiesta.Bot/               core library
    Net/   XorStreamCipher, FiestaClientConnection (plaintext read / XOR write), handshake
    Login/ Login→WM→Zone chain (typed)
    Zone/  [1801] builder (chardata + 49 data-file checksums via Encription)
    Session/ in-zone keepalive, inbound dispatch, per-bot game state
    Behaviors/ buff-in-town, accept/request-party, gear-up-from-loadout
    Loadout/  JSON template schema (class/level/armor/weapon, per-instance presets)
    Accounts/ ik-fiesta-api client (create account w/ master key, grant GM)
  src/Fiesta.Bot.Host/          ASP.NET minimal-API host + BotManager (N bots in parallel) + web UI
  templates/                    tracked sample loadout JSON (operator instances gitignored)
```

## Protocol foundation (already proven, being ported from Python)

- **Proven login chain**: `ik-fiesta-scripts/forge_login_e2e.py` drives
  Login→WM→Zone, holds the session with keepalives, then find-user/kick via
  OPTool. e2e proven (in-zone "Anna", find-user, kick drops session).
- **Transport** (`fiesta-proxy/tools/_fiesta_proto.py`):
  - Length-prefix framing: 1 byte (1..254), or `0x00` + 2-byte LE for ≥255.
  - Body = opcode (LE u16) + payload.
  - **Asymmetric cipher**: C→S body is XOR'd with a 499-byte table, position
    per-direction wrapping mod 499, starting at the handshake **seed**. **S→C
    stays plaintext.** Server enables it by sending a 4-byte plaintext frame
    `[07 08 seedLo seedHi]` S→C. (This is why we write our own thin
    `FiestaClientConnection` instead of reusing `FiestaConnection`, which
    transforms both directions.)
- **[1801] anti-cheat (cracked)**: the `MAP_LOGIN_REQ` "chardata" region is
  **49 data-file checksums**, `checksum = MD5(file[:0x24] + Encription(file[0x24:]))`
  where `Encription` is lifted from `Zone.exe CDataReader::Encription @0x62A0B0`:
  ```python
  def encription(data):
      b=bytearray(data); n=len(b); key=n&0xFF
      for i in range(n-1,-1,-1):
          b[i]^=key; a=(i*11)&0xFF; a^=((i&0xf)+0x55)&0xFF; a^=key; a^=0xAA; key=a&0xFF
      return bytes(b)
  ```
  Files (idx 0..48): AbState, ActiveSkill, CharacterTitleData, ChargedEffect,
  ClassName, Gather, GradeItemOption, ItemDismantle, **ItemInfo(8)**, MapInfo, …
  (under `9Data/Shine/` and `9Data/Shine/View/`). Compute over the *target
  server's* data files; only operator-modified files differ. On the k8s server
  only `ItemInfo.shn` differs (its checksum `1937d4cd…`). The anti-cheat is
  per-login, so a real client must ship the SAME modified data files.
- **Login→WM→Zone chain semantics**: OTP is `validate_new[64]` @19 of
  `WORLDSELECT_ACK`, echoed @256 into `LOGINWORLD_REQ`. `[1003] CHAR_LOGIN_ACK`
  carries the zone ip/port. `[1801]` chardata.wldmanhandle @0 must be the live
  WM handle. Post-`[1801]` establishing sequence then periodic keepalive so the
  zone doesn't drop the session.

## Key constraints / guardrails

- **XOR table is purged from git history** (fiesta-proxy `project_fiesta_proxy_history_purged`).
  Do NOT commit the table. Load it at runtime from `XOR_TABLE_HEX` /
  `XOR_TABLE_PATH` (mirror `fiesta-proxy/.../Crypto/XorTableLoader.cs`). The repo
  ships no copyrighted/sensitive cipher data — BYO, like fiesta-docker.
- **Do NOT touch mssql/SA directly without authorization** — this is the main
  reason account provisioning goes through ik-fiesta-api, not a DB string.
- **Do NOT force-sync / `kubectl apply -f` the game manifests** (game tier is
  `replicas:0` by design; a guard hook blocks it). Deploy via image build/push +
  manifest tag bump + Argo auto-sync, if/when this gets deployed.
- ik-fiesta-api specifics: `POST /api/accounts` with `X-Api-Key` bypasses
  captcha + rate-limit and creates a game account (MD5 in-game pw via
  `usp_User_insert`). Admin endpoints (`/api/accounts/{id}/cash|inventory`)
  require an admin JWT (`nAuthID==9`) and deliver via the cash shop
  (`tChargeItem`) — separate from in-game GM level. The API does NOT yet set a
  game account's in-game GM level → small addition needed (master-key-gated).
- Test account: `testuser` / `test123` (password = raw MD5, no salt).

## Roadmap (task tracker mirrors these)

1. [in progress] Scaffold repo (sln, 2 projects, submodule, gitignore, this plan).
2. Client c2s transport: `XorStreamCipher` + `FiestaClientConnection` + handshake.
3. Login→WM→Zone chain in C# (typed).
4. Build `[1801]` from scratch (chardata + 49 checksums via Encription); verify
   live (`[1804]` until right, `[1038]` = in zone).
5. Bot session runtime: keepalive, inbound dispatch, per-bot state.
6. Multi-bot manager + HTTP control API (spawn/list/stop + behaviors).
7. Account provisioning via ik-fiesta-api master key (+ API GM-level addition).
8. Loadout templates (JSON) + GM-command gearing.
9. Web UI for templates + bot control.

Behaviors land in dependency order: **buff-in-town** first (just needs in-zone +
skill-cast + nearby-player tracking), then **party** (invite/accept), then
**instance assist** (movement + combat + loadout gearing).

## Open questions / notes

- In-game GM/admin level field + which GM command packets give items / set level
  (RE from the client or Zone.exe) — needed for gearing. Capture/inspect first
  (no blind guessing — build introspection, send only inspected bytes).
- Loadout schema: start minimal (class, level, list of {slot, itemId, +enchant})
  + named presets keyed by instance (e.g. "DragonTomb").
- Buff target selection: nearby players from zone broadcast packets; need to
  confirm which inbound opcodes carry nearby-player spawn/positions.
- No network in the build sandbox — submodule added from local checkout, URL
  repointed to GitHub. `git submodule update --init` will work for a real clone.

## Refinements (added mid-build)

- **ik-fiesta-api integration is OPTIONAL.** Bots accept login creds fed directly
  (the CLI already does `--user/--pass/--passmd5`). API master-key provisioning
  is one option, not a requirement.
- **GM-permission detection at runtime.** The bot must detect whether GM commands
  are allowed for its account; if NOT, it must not attempt to grab the "correct"
  gear. Future fallback: players trade items to the bot, which then equips them.
  So gearing has two modes: GM self-gear (admin accounts) vs player-traded gear.
- **k8s-ready.** Ship a Dockerfile (multi-stage, BYO XOR table at runtime via
  env — never baked). Local dev/testing stays `dotnet run` direct.

## Login handshake (SOLVED, live-verified 2026-06-09)

Login→WM chain works end-to-end, typed, no capture replay. Verified vs the live
k8s server (62.171.171.24). The real client C→S Login order (from the reference
pcap, decoded with fiesta-proxy `tools/session_client.py`):

1. `VERSION_CHECK_REQ` 0x0C65 — **sent first**, server version-gates and drops the
   socket if missing. 64-byte key = build version string `"10022024000000"` (the
   client leaves the tail as uninitialised stack, so only the prefix is checked).
2. `US_LOGIN_REQ` 0x0C5A — sUserName[260] + sPassword[36]=MD5hex + spawnapps Name5
   = **"Original"** (build tag; login is rejected without it).
3. `XTRAP_REQ` 0x0C04 (anti-cheat key) + `WORLD_STATUS_REQ` 0x0C1B (empty).
4. `WORLDSELECT_REQ` 0x0C0B worldno → `WORLDSELECT_ACK` 0x0C0C carries OTP
   (validate_new[64] @19) + WM ip/port.
Then WM: `LOGINWORLD_REQ` 0x0C0F (user + OTP echoed) → `LOGINWORLD_ACK` 0x0C14
(wm handle + avatars). `CHAR_LOGIN_REQ` 0x1001 (slot) → `CHAR_LOGIN_ACK` 0x1003
(zone ip/port). These build-identity constants live in `ClientProfile.ClientProd2`
(VersionKey/SpawnAppsTag/XtrapKey) — NOT session data, same spirit as [1801]
checksums.

### ⚠️ [1801] ItemInfo checksum — DO NOT trust the pcap
The reference pcap was captured with a **stale ItemInfo.shn** (its idx-8 checksum
fails on the current server — that's the known "DataFail file 8"). When building
[1801], compute the idx-8 (ItemInfo) checksum FRESH from
`Z:/ClientProd2/ressystem/ItemInfo.shn` (the BYO file matching the server), never
copy it from the capture. All 49 checksums = `MD5(file[:0x24] + Encription(file[0x24:]))`.

## Character creation (SOLVED, live-verified 2026-06-09)

First-class feature: the bot provisions its own character via the game protocol.
On the WM connection (char-select), `AVATAR_CREATE_REQ` 0x1401 (slotnum + name
Name5 + char_shape PROTO_AVATAR_SHAPE_INFO 4B) → `AVATAR_CREATESUCC_ACK` 0x1406
(new avatar) / `AVATAR_CREATEFAIL` 0x1404. Live-verified: created a level-1
Fighter "BotFighter" on the empty server.

### char_shape bitfield (4 bytes)
byte0 = race[0..2) + chrclass[2..7) + gender[7]; byte1 hairtype; byte2 haircolor;
byte3 faceshape.

### Class IDs (ground truth from ClientProd2 ClassName.shn via `fiesta shn`)
Level-1 creatable: **Fighter=1, Priest(Cleric)=6, Archer=11, Mage=16, Joker=21**.
Advancement at lvl 20 → 60 → 100 (lvl-100 is a branch, e.g. Mage→Wizard(20) or
Warlock(19); Joker line's lvl-100 = Spectre(24) or Reaper/Assassin(25)).
**Crusader (Sentinel=26)** is creatable at level 60, but only if the account
already has a level-60+ character. Full tree is in `ClassId` (CharacterSpec.cs).

### Next: tutorial popup → zone entry
A freshly-created char enters the newbie tutorial: CHAR_LOGIN_REQ 0x1001 is
answered with `CHAR_TUTORIAL_POPUP_REQ` 0x1110 (Char cmd 272), NOT the
zone-handoff CHAR_LOGIN_ACK 0x1003. So zone entry (task 15, the [1801] build)
needs tutorial handling/skip for new chars (existing chars get 0x1003 directly,
per forge_login_e2e). This is the seam between creation and the zone phase.
