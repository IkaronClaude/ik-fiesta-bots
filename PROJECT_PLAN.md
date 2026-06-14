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
6. [done] Multi-bot manager + HTTP control API (spawn/list/stop + behaviors).
7. [in progress] Account provisioning via ik-fiesta-api master key (+ API GM-level addition).
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
- Submodule was added from a local checkout (URL repointed to GitHub), so
  `git submodule update --init` works for a real clone. (Earlier this note also
  claimed "no network in the build sandbox" — that was never true and was
  removed 2026-06-10: the dev/test environment reaches both the live game server
  at 62.171.171.24 and the internet, which is how every "live-verified" note in
  this file was produced.)

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

### Tutorial decline (SOLVED — in-session, NO reconnect)
A freshly-created char enters the newbie tutorial. After `CHAR_LOGIN_REQ`, the
server sends `CHAR_TUTORIAL_POPUP_REQ` (Char 272, 0x1110) **and**
`CHAR_LOGIN_ACK` (Char 3, 0x1003) in the same session. Reply to the popup with
`CHAR_TUTORIAL_POPUP_ACK { bIsSkip = 1 }` (Char 273) to decline. **No reconnect,
no CHAR_LOGINFAIL** — the earlier "decline only takes effect next login /
reconnect" theory was WRONG, disproved by `Z:/SkipTutorial.pcapng` and a live
test. The WM read loop handles `TUTORIAL_POPUP` and `CHAR_LOGIN_ACK` in either
arrival order; `CHAR_LOGINFAIL` is now a genuine error (it does not occur on the
happy path). New char must be created on a FREE slot (`FirstFreeSlot`) and
`CHAR_LOGIN_REQ` uses the **server-reported** slot from `CREATESUCC_ACK`.

Live flow (2026-06-10, Bot2433 Priest, Ikaron acct slot 2):
```
>> AVATAR_CREATE_REQ slot=2  << AVATAR_CREATESUCC_ACK slot=2 level=1
>> CHAR_LOGIN_REQ slot=2     << TUTORIAL_POPUP_REQ (0x1110)
>> TUTORIAL_POPUP_ACK skip=1 << CHAR_LOGIN_ACK zone=62.171.171.24:9016
>> MAP_LOGIN_REQ (0x1801)    << 0x1038  *** IN ZONE ***
```

**Opcode convention:** resolve every opcode via `PacketRegistry.GetOpcode<T>()`
(derived from each struct's `[FiestaOpcode(dept, cmd)]` — the 6-bit dept | 10-bit
cmd encoding). No hand-written hex consts. `FiestaPacket.Department/Command`
expose the split for logging.

## Zone entry — [1801] from scratch (SOLVED, live-verified 2026-06-09)

`PROTO_NC_MAP_LOGIN_REQ` (0x1801, sizeof 1590) = chardata
(PROTO_NC_CHAR_ZONE_CHARDATA_REQ: wldmanhandle = live WM handle + charid Name5)
+ checksum[49] (Name8 = 32 ASCII-hex chars each). Each checksum =
MD5(file[:0x24] + Encryption(file[0x24:])) over the client's reference .shn.

- **49-file order recovered** by computing the checksum over every .shn in
  ClientProd2/ressystem and matching a reference [1801] (48/49; idx 8=ItemInfo
  differed — the pcap's stale file, as warned). Full list in `DataFileChecksums.Files`.
- Computing all 49 fresh from ClientProd2 → server-matching values. Sent [1801],
  got **[1038] (Char cmd 56) = IN ZONE on the first try** (no [1804] DataFail).
- `[1804]` MAP_LOGINFAIL carries `nWrongDataFileIndex` (the failing file) — used
  for iteration; `[1038]` has no FiestaLib struct so we treat any non-[1804]
  post-[1801] frame as in-zone.
- BYO: operator points `--data-dir` at their client `ressystem` (must match the
  server's data). Code: `Zone/Encryption.cs`, `Zone/DataFileChecksums.cs`,
  `Zone/ZoneEntry.cs`. Spelled **Encryption** in our code (PDB symbol is the
  misspelled `Encription`).

## Full chain DONE
log in → create char (free slot) → CHAR_LOGIN → decline tutorial (in-session) →
[1801] → in zone — all typed, no capture replay, live-verified end to end
(Bot2433 Priest @ zone00, 9016, 2026-06-10). No reconnect anywhere.

## Session runtime (task 16, DONE — live-verified 2026-06-10)
`Session/BotSession.cs` + `Session/BotSessionState.cs`. One read loop per
connection that:
- pumps inbound S→C frames continuously;
- auto-answers the server keepalive: on `Misc HEARTBEAT_REQ` (0x0804) it replies
  a **bare** `HEARTBEAT_ACK` (0x0805, empty payload — matches the real client;
  the `_SEND` structs carry size+netcmd but the wire frame the client sends is
  opcode-only). Opcodes derived `(dept<<10)|cmd` from `ProtocolCommand.Misc` +
  `MiscOpcode`, no hex;
- updates `State` (uptime, inbound/heartbeat counts, last opcode, connected +
  disconnect reason) via Interlocked — safe to read from HTTP/status threads;
- fans every non-keepalive frame to `event PacketReceived` for the buff/party
  layers; exposes `SendAsync` (typed or raw) for outbound actions.
Owns the connection; `IAsyncDisposable`. A normal stop is `cancelled`; a kick
shows as `peer closed`.

LoginTestCli now runs a `BotSession` on BOTH the zone and WM links (the WM link
keeps getting heartbeats while in zone) for `--hold <sec>` (default 30).
Verified: BotFighter held in zone 40s, heartbeats answered on both, ended on
the hold timer (not a kick).

## Multi-bot manager + control API (task 17, DONE — live-verified 2026-06-10)
`Manager/BotManager.cs` owns N bots in parallel, keyed by id, in a
`ConcurrentDictionary`. `Spawn(BotSpawnOptions)` is non-blocking: it kicks the
**full chain** (the exact orchestration `LoginTestCli` proved — Login → WM with
optional in-band char-create + tutorial decline → [1801] zone entry → a
long-lived `BotSession` on BOTH the zone and WM links) onto a background task,
and returns a `BotHandle` immediately. A managed bot runs **until stopped** (no
hold timer); `StopAsync(id)` cancels its CTS, awaits wind-down (10s cap), and
removes it. The WM connection is disposed once via a tiny scope struct; the zone
connection is owned by its session's `DisposeAsync` (no double-dispose).
- `BotHandle` tracks lifecycle `Phase` (Pending→LoggingIn→SelectingChar→
  EnteringZone→InZone→Stopped/Failed), char name, error, and a 200-line ring-
  buffer log; `Snapshot()` is the serializable status view (pulls live counters
  off the in-zone `BotSession.State`). Phase/name/error are volatile, log is
  locked — safe to read from HTTP threads.
- `Host/BotEndpoints.cs` maps `/api/bots`: `POST` spawn (201 + snapshot),
  `GET` list, `GET /{id}` status (incl. recent log), `POST /{id}/stop`. Request
  DTO takes plaintext `password` (MD5'd here) or `passwordMd5`, opt-in char
  creation (`create`/`charName`/`class`/`gender`). Bad input → 400 ValidationProblem,
  dup id → 409. `Program.cs` loads the BYO XOR table at startup and registers the
  `BotManager` singleton (logs via `ILogger`); if the table is missing the host
  still starts and every bot endpoint returns **503 with the reason** (health
  reports `botsEnabled:false`).
- **Live-verified end to end via the HTTP API (2026-06-10).** `POST /api/bots`
  (testuser, against 62.171.171.24:9010) drove the whole chain: WORLDSELECT →
  WM (`handle=52500`, existing avatars `Anna`/`Anna2`) → CHAR_LOGIN slot 0 →
  zone 9016 → [1801]+49 checksums → `0x1038` *** IN ZONE *** on the first try.
  Sessions ran on BOTH links and answered their heartbeats (1 each); `POST
  /{id}/stop` cleanly cancelled both (uptime 41s, `disconnectReason=cancelled`)
  and removed the bot. Note `create:true` is opt-in-**only-if-missing**: the
  account already had avatars, so no new char was made — it entered `Anna`.
  Also locally verified: validation 400s, dup-id 409, and the no-XOR-table 503
  path (health `botsEnabled:false`).

## Account provisioning (task 18, PART A done — bots-side client built 2026-06-10)
`Accounts/ApiAccountProvisioner.cs` — POSTs `api/accounts` with `X-Api-Key` to
ik-fiesta-api, maps the 201 to `ProvisionedAccount` (UserNo + ready
`BotCredentials`, in-game pw MD5'd the same way the API hashes `sUserPW`). 409 →
`AccountExistsException`. Host: `Host/AccountEndpoints.cs` maps `POST /api/accounts`
(validation 400, 409, 502 on upstream error), registered from env
`FIESTA_API_BASE_URL` + `FIESTA_API_KEY` only when both are set — else 503
(health reports `provisioningEnabled`). Verified locally: build green, 503 +
validation paths. Optional by design (bots also take creds fed directly to spawn).

### Pending (all touch PROD — need operator input/decision):
- **Live-verify provisioning.** Needs the API base URL (`https://fiesta.ikaron.uk`,
  the `fiesta` traefik ingress — confirm the account path) + a **valid API key**
  (minted by an admin via `POST /api/apikeys`, stored hashed in `Account.tApiKey`;
  I don't have one). Creating an account is a prod write.
- **GM-level addition (Part B, sibling repo `ik-fiesta-api`).** The in-game GM
  field is **`Account.tUser.nAuthID`** (DB-confirmed: default **1** = normal,
  **9** = admin/GM marker; the API's admin JWT keys off `nAuthID==9`). `testuser`
  = nUserNo 100, nAuthID 1. The addition: master-key-gated way to set `nAuthID`
  on create (extra field on `CreateAccountRequest` + `UPDATE tUser` in
  `AccountService`, trusted-caller-only). Then build/push the API image + Argo
  sync. NOTE: whether `nAuthID` alone enables in-game GM *commands* (vs. just web
  admin) is unconfirmed — verify by setting it and trying a GM command in-zone
  (task-19 RE). Alternative now that we have DB access: grant GM via direct SQL
  `UPDATE tUser SET nAuthID=9` for testing without the API change.

**Cluster/DB access is now authorized** (kubectl + sqlcmd into `mssql-0`/`fiesta`
ns — see workspace `C:/Projects/CLAUDE.md`). This relaxes the old "don't touch
mssql/SA directly" guardrail to: reads/inspection free, **confirm before prod
mutations**. The API-routing for account *creation* remains the clean design.

**Next: finish task 18 (live verify + Part B, pending operator input), then 19
(loadout templates + GM gearing), 20 (web UI). Behaviors (buff-in-town first)
hang off the running `BotSession.PacketReceived` / `SendAsync`.**

## Perception + manual action layer (in progress 2026-06-10)

Direction (operator): **nail real-world packet interaction first via manual
endpoints**; design scripting/behaviour later (maybe Lua). The buff *behaviour* is
de-prioritised — it exists but is opt-in; the focus is hand-callable actions.

### Built this session
- **`Session/ZoneView.cs`** — a live perception model attached to every in-zone
  `BotSession`. Decodes Briefinfo `CHARACTER_CMD`(7)/`LOGINCHARACTER_CMD`(6) →
  nearby-player map (handle→name/class/level/coord), `BRIEFINFODELETE_CMD`(14) →
  remove, and `ACT_SOMEONECHAT_CMD` → `ChatReceived`. Events: `PlayerAppeared`,
  `PlayerLeft`, `ChatReceived`. This is the shared seam the future LLM/Lua
  controller consumes. Snapshot now carries `nearbyPlayers` + `lastChat`.
- **`Session/FiestaText.cs`** — EUC-KR (cp949) decode/encode for names + chat.
- **`Behaviors/ChatCodec.cs`** — hand-rolled chat codec. FiestaLib's generated
  chat structs read text as `content[itemLinkDataCount]` which is **wrong**: the
  real text length is `len` (itemLinkDataCount only counts trailing item-link
  blobs — itemlinks = items embedded in chat that others hover to inspect).
  Layouts confirmed from the extracted struct table:
  - `CHAT_REQ` (C→S, Act 1): `[itemLinkDataCount=0][len][text:len]`
  - `SOMEONECHAT` (S→C): `[itemLinkDataCount][handle:2][len][flag][font][balloon][text:len]`
- **`Behaviors/BuffInTownBehavior.cs` + `BuffConfig.cs`** — chat-triggered buff
  (opt-in, `Buff` spawn option). Kept but not the current focus.
- **Manual action endpoints** on the bot API (`Manager.ActAsync` seam):
  - `POST /api/bots/{id}/say {text}` — `ACT_CHAT_REQ`
  - `POST /api/bots/{id}/cast {skill,target}` — `BAT_SKILLBASH_OBJ_CAST_REQ`
  - `POST /api/bots/{id}/use-item {slot,invenType}` — `ITEM_USE_REQ` (Item 21)

### Live-verified (2026-06-10, testuser 'Anna' @ zone00 9016)
Spawn → in-zone on the new build, then `/say "hello from bot"` and `/cast skill
1903 target 0` were both **accepted by the live server with no disconnect**, and a
server **heartbeat arrived + was answered afterwards** (inbound 17→18,
heartbeats 0→1) — proving the C→S cipher stream stayed in sync across both sends,
i.e. both wire formats are correct. (Empty zone, so `nearbyPlayers:0`, no chat
observer.)

### Packet facts (for the action layer)
- **Buff/skill cast (single target):** `PROTO_NC_BAT_SKILLBASH_OBJ_CAST_REQ`
  {skill, target} (Bat 64). `_FLD_CAST_REQ`(65) = ground/AoE. `SKILLENCHANT_REQ`
  (Bat 9) **not ruled out** as a buff path (operator: "enchant" may = skill
  empower points — 4 types cd/dmg/duration/mana, up to 5 each — OR the buff
  cast). Try both live once a priest has a learnt buff.
- **Use item:** `PROTO_NC_ITEM_USE_REQ` {invenslot, invenType} (Item 21).
- **ItemInfo.shn** (via collab `fiesta shn`): `Name`(2), `DemandLv`(11),
  `UseClass`(31), `ItemUseSkill`(54). "Strong Endurance [01..04]" = IDs 5320–5323
  (`StrongEndure01..04`), but `UseClass=7`/no level gate → **likely a Fighter
  passive, NOT the lvl-47 Priest buff**. Still need to find the real lvl-47
  Priest buff scroll (filter `ItemUseSkill!=-` + Priest class + `DemandLv≈47`).

### GM commands (SOLVED — Gamigo/NA2016 files, `&` prefix, chat-routed)
This server = the **Gamigo NA2016** files (`Z:/ServerSource` has `GamigoZR`,
`GBO.reg`). GM commands are typed in chat (`ACT_CHAT_REQ`); the server processes
the `&`/`$` prefix when the account is GM (`nAuthID=9`). From FiestaHeroes docs
(doc.fiestaheroes.com/docs/GM_Commands):
- `&levelup (N=1)` — raise level by N
- `&makeitem InxName (-lLot) (-uUpgrade)` — spawn an item by **InxName** (not ID)
- `&learnskill ActiveSkill::ID` — learn a skill by ID
- `&getmoney Amount`
Exposed as **`POST /api/bots/{id}/gm {command}`** (prepends `&` if no prefix) —
plus `/say`, `/cast`, `/use-item`. The `/gm` endpoint reuses the chat send.

### Endure [01] buff (the lvl-47 Priest buff — IDs resolved)
Item and skill share `InxName = SafeProtection01`:
- **scroll:** ItemInfo ID **5480**, `Endure [01]`, `DemandLv 47`, `UseClass 9`
  (Cleric line) → `&makeitem SafeProtection01`
- **skill:** ActiveSkill ID **1580**, `Endure [01]` → `&learnskill 1580`, cast = `1580`
- NB "Strong Endurance [01..04]" (items 5320–5323, `UseClass 7`) are `DemandLv
  100` — a different/awakened skill, NOT this. The only `[01]` scroll at exactly
  lvl 47 is `Endure [01]`. (`fiesta shn ItemInfo.shn`/`ActiveSkill.shn`.)

### Full real-buff recipe (ready to run once testuser is GM)
1. **Grant GM:** `UPDATE tUser SET nAuthID=9 WHERE sUserID='testuser'` (authorized
   SQL; testuser is currently nAuthID=1 — confirm this prod write first).
2. Spawn a **Priest** bot (`create:true class:Priest`), in zone.
3. `/gm levelup 46` → level 47 (advancement/class tier for UseClass 9 may matter).
4. `/gm makeitem SafeProtection01` → Endure scroll in bag. (Or skip 4–5 and
   `/gm learnskill 1580` directly.)
5. `/use-item <slot>` → learns Endure (needs `/inventory` to find the slot).
6. `/cast 1580 <targetHandle>` → buff. Try `SKILLBASH_OBJ_CAST_REQ` first, then
   `SKILLENCHANT_REQ` if that's a no-op (cast packet still to be confirmed live).

### Action endpoints live-verified at transport level (2026-06-10)
Spawned a Priest (`BotPriest`, testuser slot 2, created in-band) → in zone, then
fired `/gm levelup 46`, `/gm learnskill 1580`, `/gm makeitem SafeProtection01`,
`/cast 1580`, `/use-item 40` in a burst. **All accepted; the session stayed in
zone ~73 min afterward** (ended only on a local power outage, not a desync) — so
none of the five packet types corrupt the cipher stream. The `&` auto-prefix
works. **NOT yet verified: the GM *effect*** (level/skill/item actually applied),
because `testuser` is still `nAuthID=1`. The `UPDATE tUser SET nAuthID=9` was
**denied by the auto-mode classifier** as an unconfirmed prod mutation — needs
explicit operator go-ahead (or run it via the `!` prompt) before the real
Endure-buff flow can be validated.

## BREAKTHROUGH (2026-06-10, all live-verified, operator-confirmed)

Two missing C→S packets explained *every* "in zone but inert" symptom. Both
recovered from real-client captures decoded with `fiesta-proxy/tools/session_client.py`.

### 1. Zone load was never finishing — `[1803] MAP_LOGINCOMPLETE`
We sent `[1801]`, got the chardata burst, and treated the first frame as "in
zone" — but never sent **`MAP_LOGINCOMPLETE` (0x1803, Map dept cmd 3)**, which the
client sends **after** the burst-ending **`MAP_LOGIN_ACK` (0x1802)**. Without it
the char sits in *loading limbo*: invisible to others, no nearby/chat broadcasts,
GM/chat/cast all silently ignored. Fix (`Zone/ZoneEntry.cs`): drain the post-[1801]
burst until `[1802]`, then send `[1803]`. Source: `Z:/ClientSourceZone.pcapng`.
After this: char visible, chat works, nearby players seen, GM commands take effect.

### 2. Skill cast needs target-first — not a lone cast
A bare cast got the caster **kicked** (`MAP LinkendClientCmd`). The real buff (from
`Z:/Buff.pcapng`) is a **3-packet sequence**:
1. `BAT TargettingReq` (0x2401, `{ushort target}`) — tab-target the handle. Server
   replies `BAT_TARGETINFO_CMD` (0x2402) with the target's HP/SP/level.
2. `ACT ChangemodeReq` (0x2008, payload `[0x02]`) — battle/cast stance.
3. `BAT_SKILLBASH_OBJ_CAST_REQ` (0x2440, `{skill,target}`) — the cast.
So **bash-obj WAS the right cast packet** (not enchant — enchant kicked); we just
never targeted first. `Manager/BotManager.CastAsync` now replays all three.
(Operator note: tab-target may be trimmable since target is in the bash packet;
ACT+bash might suffice — but the full sequence is verified working.)

### Endure buff — full flow proven end to end
GM-set BotPriest to char GM (`tCharacter.nAdminLevel=100`, value 100 = full admin),
then over the bot HTTP API: `/gm levelup 46` (→ lvl 47, DB-confirmed) → `/gm
learnskill 1580` (Endure [01], `SkillLearnsucCmd` confirmed) → `/cast {skill:1580,
target:<player handle>}` → **abstate applied to the target, no kick, operator saw
the Endure buff land.** `nAuthID` (account admin) is a misnomer — in-game GM is the
per-**character** `tCharacter.nAdminLevel`; GM commands ride normal chat (`&` prefix).
**Buff lasts 60 min regardless of the caster's login state** — so a buff survives
a bot disconnect/restart. (Design is still **persistent parked bots**: a priest
stays logged in in town for extended periods, buffing players on demand — not
cast-and-go.)

### Packet introspection
`logInbound` spawn flag logs every inbound frame on **both** zone+WM links
(opcode/dept/cmd/len + hex) via `BotSession` — invaluable; keep using it.

### Open / next
- **`/inventory`** — decode the inbound item list at zone-login so `/use-item` can
  target the right slot (use-item sent+accepted but slot targeting unverified).
- **Lingering-session kicks:** abruptly killing the host leaves the char "online"
  server-side → next login can `LinkendClientCmd`-kick. Add a clean logout, or
  always `/stop` (cancel) before shutdown.
- Behaviour/scripting layer (Lua?) on top of the manual action endpoints — later.

## Endpoint expansion + gear/buff demo (2026-06-11)

### Action endpoints now: /say /whisper /cast /use-item /equip /gm + GET /inventory /equipment
- **/equip** {slot} — `ITEM_EQUIP_REQ {slot}`; server derives the target equip slot
  from the item's `Equip` column. Live-verified: equipped the Life set
  (body/legs/feet) as Guardian, no kick.
- **/whisper** {to,text} — `ACT_WHISPER_REQ` (`[0][receiver Name5(20)][len][text]`,
  hand-coded like chat). Built; live-verify pending a recipient online.
- **GET /inventory** — bag slot→itemId, tracked in `ZoneView` from the login
  `CHAR_CLIENT_ITEM_CMD` + live `ITEM_CELLCHANGE_CMD`/`EQUIPCHANGE`. Live-verified
  (makeitem a scroll → shows at its slot).
- **GET /equipment** — worn gear (equip slot→itemId) from `EQUIPCHANGE` events.
  NB only tracks session equips; login-worn decode (by item box) is a TODO.

### Buff-boost via gear (live-verified)
Class is gated by `tCharacterShape.nClass` (loads at login). Set BotPriest to
Guardian (10) via SQL → equipped the **Life set** (LifeArmor 53016 / LifePants
53017 / LifeBoots 53018, `InxName` LifeArmor/LifePants/LifeBoots, DemandLv 75,
UseClass 10). Buffed maxHP: **1965 (no set) → 2630 (Life set)** from
`BAT_TARGETINFO` (the +HP bundles set base HP + amplified buff; isolating the
buff term needs the remove-buff command). The lvl-75 set boosts buff **power
(HP), not duration**.

### Class tree (corrected by operator)
Cleric(6) → HighCleric(7) → **lvl-60 Paladin** → lvl-80 Divine Paladin (visual
only, no JCQ). Guardian/HolyKnight are the **lvl-100** split. (ClassName.shn
acEngName labels 9/10 "HolyKnight/Guardian"; `nClass=10` works for equipping the
UseClass-10 Life set regardless of the label.)

### use-item is NOT working yet — needs a capture
`/use-item` sends `ITEM_USE_REQ {invenslot, invenType}`. With invenType=0 the
server ignores it; with ≠0 it replies `ITEM_USE_ACK { error=1794, useditem=0xFFFF }`
= "no item at that address" — so the inven addressing is wrong (the bag `Inven`
is a packed bitfield, not the plain slot I pass; equip works because EQUIP_REQ
only needs a slot). **Capture a real client using a skill scroll (e.g.
`Z:/UseItem.pcapng`) to nail the USE_REQ bytes.** Skill note: Endure [01]/[02]
(skills 1580/1581) are separate castable skills sharing a cooldown; some
unrelated skills also share cooldowns (e.g. Fighter Concuss/Devastate).

### Full.pcapng decoded → use-item + clean-logout SOLVED (2026-06-11)
Operator capture `Z:/Full.pcapng` (labeled in `Z:/Full.pcapng.txt`) covers
money/buy/use-scroll/sell/cast/walk/NPC-dialogue/quests/clean-logout. Findings:
- **use-item addressing fixed**: `ITEM_USE_REQ` invenType = **9** (normal item
  bag), not 0. With 9 the server finds the item (USE_ACK `useditem`=real id);
  with 0 it returned `useditem=0xFFFF`. `/use-item` now defaults invenType=9.
  (A remaining `USE_ACK error` on our test char is class-specific — the
  GM-frankenstein Guardian using a UseClass-9 scroll; fine on a legit char.)
- **Clean logout SOLVED** (fixes the relog kick): the client sends Char
  `LOGOUTREADY` (0x1071) + User quit (0x0C18) on **zone**, and the quit on **WM**.
  There's a **~10s combat-logout countdown** (cancelled if damaged) — so `StopAsync`
  sends the quit and then keeps the sessions running (answering heartbeats) until
  the **server** closes the links (don't cancel/close mid-countdown). Verified:
  stop → immediate relog now survives (no `LinkendClientCmd`).
- **Walk** = `ACT` cmd 25 (0x2019), 16B = from(x,y u32)→to(x,y u32) per step;
  `ACT` cmd 18 (0x2012) = single point. **Buy** = Item cmd 3 (0x3003). **Sell** =
  `ITEM_SELL_REQ` cmd 6 (0x3006) {slot, count u32}. **Quest accept differs**:
  remote (Shutian) = `Quest StartReq` (0x4414) after a `ReadReq` (0x4416) list
  browse; local (Robin, at NPC) = `Quest ScriptCmdAck` (0x4402) dialogue only.

### TODO / roadmap (operator-requested)
- **Fix FiestaLib-Reloaded bugs upstream** (sibling repo, push allowed → bump the
  pinned submodule hash): the chat structs read `content` as
  `ReadBytes(itemLinkDataCount)` (should be `len`); `SHINE_ITEM_VAR_STRUCT.Read`
  does `ReadBytes(itemid)`. We work around these locally — fix at source instead.
- **Walk** (movement) — next goal. Client move packet is `ACT` cmd 25 (0x2019,
  16 bytes = from(x,y u32)+to(x,y u32)) seen in the zone captures.
- Then: **per-event log** → **/debug** (bot whispers all events to a player) →
  **track nearby dropped items** → **item pickup** → **pathfinding** (started, below).

### Pathfinding — block grid decoded + A* (2026-06-11)
Walkability is in the server files: `Z:/ServerSource/9Data/Shine/BlockInfo/<Map>.shbd`
(BYO at runtime). **`.shbd` format (recovered):** 8-byte header = LE u32
`[bytesPerRow, height]`; then `height` rows × `bytesPerRow` bytes, **1 bit/tile**.
RouN = 2048×2048 tiles. Mapping (validated against the live spawn/regen points):
- tile = world ÷ 8 (`>>3`); **Y is flipped**: `tileY = (height-1) - (worldY>>3)`
- bit order **LSB-first**; **bit 0 = walkable, bit 1 = blocked**
- `blocked = (row[tx>>3] >> (tx&7)) & 1`
Built `Pathfinding/BlockGrid.cs` (loader + `IsWalkableWorld` + tile↔world) and
`Pathfinding/PathFinder.cs` (A*, 8-dir, no corner-cutting). Validated on RouN: a
43-waypoint path in 2ms, all waypoints walkable; out-of-region goal → no path.
`/walkto {fromX,fromY,toX,toY,map}` wires it end to end: loads
`BLOCKINFO_DIR/<map>.shbd` (cached), A* → `Simplify` (corner waypoints) →
`BotManager.WalkPath` (one MoverunCmd per segment, paced ~120 u/s on a background
task). **LIVE-VERIFIED (2026-06-11):** `/walkto (6900,8520)→(6445,8630)` on RouN
= 59 tiles → 11 waypoints; the char walked it and the DB position went
(6900,8520) → **(6444,8628)** = the target. No kick (move pace accepted).
**Polish left:** auto-track the bot's map (`sLoginZone`) + live position so
`/walkto {x,y}` needs no explicit from/map; tune speed; per-step arrival sync.
- **2-bot chat-observe test** (one `/say`s, the other's ZoneView decodes
  `SOMEONECHAT`) needs a **second account** — only `testuser` creds are held.
- Cast packet (`SKILLBASH_OBJ_CAST_REQ` vs `SKILLENCHANT_REQ`) confirmed only once
  a priest has Endure learnt.

## Navigation v2 — gates, follow, mounts (operator vision, 2026-06-11)
The next arc turns the verified walk+pathfind primitives into autonomous travel.
Operator's stated goals, in build order:
1. **walk-to-NPC / walk-to-gate** — resolve a named NPC (or a map's gate) to its
   world coord, then reuse the verified pathfind+walk. NPC coords are ground-truth
   in the server files (`9Data/Shine/World/NPC.txt`, cols
   `Name MapServer MapClient Coord-X Coord-Y Direct Party type…`). Gate/portal
   geometry is in `MapLinkPoint.shn` (16851 rows: `FromID,ToID,Weight,OneWay` — a
   node *graph*, IDs index a node table; not raw coords) and `TownPortal.shn`.
2. **Gate → switch maps (link-to)** — walk into a gate region to cross to the
   linked map. *Needs a capture:* does the client send an explicit map-move req or
   does walking into the link region trigger a server `MAPMOVE`? (Don't guess —
   this is the class of thing that's bitten us before: cast kick, use-item type.)
3. **Follow player** — in-client this is a CLIENT feature (likely **no new server
   packet**): the bot just tracks a target's live position and re-walks toward it.
   So follow = a control loop over the EXISTING move packet. Prereqs: (a) track the
   bot's **own** position; (b) track the **target's live** position (Briefinfo gives
   the initial coord; live movement needs the **other-player move broadcast** opcode
   — decode from `Full.pcapng` / a fresh capture). **Crucially: follow across map
   boundaries** — when the target vanishes (Briefinfo delete) right next to a gate,
   take that gate and re-acquire on the far side.
4. **Mounts** — cheat a time-limited mount (e.g. 7-day raccoon) via GM `&makeitem`,
   `use-item` it, **accept the time-limited-item confirmation** (a dialog that
   appears only on FIRST use — first use STARTS the timer), wait out the summon
   cast (1–10 s by mount), then **auto-mount when distance-to-goal > threshold**.
   *Needs a capture:* the time-limited-item accept packet + the mount summon/seated
   state opcode.

### Prereq that unblocks 1/3: self-position + live-position tracking
Every navigate-by-name/follow feature needs the bot to know **where it is** and
**where the target is**, live. Plan:
- **Own position:** seed from zone-entry coord (decode the spawn coord in the zone
  login/`[1802]` flow or carry it from WM char-select), then advance it from the
  moves WE issue (`WalkPath` already knows each waypoint). Exact, no guessing.
- **Target position:** `ZoneView` already decodes Briefinfo (initial coord). Add the
  **move-run broadcast** decode so a tracked handle's coord updates as they walk.
  This is the one live-capture dependency for follow.

### ⚠️ Data-source boundary — what the bot may read (operator-clarified 2026-06-12)
**This is a correctness/legitimacy rule, separate from the BYO "don't *commit* data"
ethos.** A bot is a synthetic *client*, so it may read **anything a real client can
see** — but nothing a client can't.

- **ALLOWED: client SHNs** (the `ressystem` tree, `Z:/ClientProd2|ClientSource/ressystem`).
  A client ships these and reads them, so the bot may too — item/skill/class/map tables,
  the `[1801]` checksums, etc. Reading client SHNs is *fine*, always.
- **OFF-LIMITS when we lack the server's source: server-only data.** All server
  `9Data/Shine/World/*.txt` / shine text tables **and all `*Server.shn`** (e.g.
  `NPC.txt`, mob/spawn tables, `MapInfo`-server views). These exist only on the server;
  a real client never has them, so a legitimate bot can't assume them either. Using them
  is only OK when the operator actually *has* that server's source (our dev convenience,
  via `serversource-data` — NOT something to bake into bot runtime behavior).
- **Server-authoritative state arrives over the wire, not from files:**
  - **NPC positions** are server-side until the server *sends* them — which it does, in
    the zone field-enter **briefinfo** broadcast. So `ZoneView` reading NPC/gate coords
    from packets (not `NPC.txt`) is the *correct* design, not a workaround. ✅ already done.
  - **Mob spawn groups are entirely server-side.** The client never receives the spawn-
    group definitions — only the **derived** output: the mob *positions* (briefinfo /
    `REGENMOB`) and **respawn packets** as they fire. To know spawn groups without server
    files we must **reconstruct them empirically** from those packets (observe where/when
    mobs appear over time → infer the groups). **NOT implementing this now** (irrelevant
    until something needs it) — captured here so the approach is on record. When it's
    needed it belongs in the AUTO-DISCOVERY `WorldModel` below.

### Client SHN reader — BYO client game-data (DONE, live-verified 2026-06-12)
The bot can now read **client-side SHN tables** (the ALLOWED side of the boundary above)
so feature code resolves game data instead of hard-coding it.
- **`FiestaLibReloaded.Shn` (new submodule project, dependency-free).** `ShnTable.Load(path)`
  → `{ Name, Columns (name/type/len/typeCode), Rows (col→value) }`. Decodes the 32-byte
  crypt header + `u32` length + body XOR'd with the symmetric Fiesta data cipher (same
  routine as the bot's `Zone/Encryption.Apply` and the [1801] checksum's
  `CDataReader::Encription`), then `[header][recordCount][defaultRecordLength][columnCount]`
  + column defs + `u16`-prefixed rows; strings EUC-KR (cp949). A read-only port of the
  proven `ik-fiesta-collab` SHN reader — put it in **FiestaLib** (not a cross-repo
  ProjectReference to collab) because the bot consumes FiestaLib as a **submodule**, so it
  deploys via git / the Dockerfile; a path-ref to collab would break the container build.
- **`Fiesta.Bot.GameData.ClientData`** — loads tables by name from the BYO `ressystem`
  dir (`CLIENT_DATA_DIR`, default `Z:/ClientProd2/ressystem` = same data the [1801]
  checksums use), cached. Typed `Skill(id)` → `SkillInfo(UsableDegree, IsMovingSkill,
  DlyTime, Range, SP)` from `ActiveSkill` — exactly the inputs the **deferred data-driven
  cast** needs (replaces the hard-coded "damage=face+stop, heal=neither" heuristic). Set
  on `BotManager.ClientData` by the host (alongside `GridProvider`).
- **Endpoints:** `GET /api/gamedata/{table}` (row count + columns — confirms a BYO file
  loads) and `GET /api/gamedata/skill/{id}` (the projected combat fields).
- **Live-verified** against `Z:/ClientProd2/ressystem`: ActiveSkill 2791 rows/96 cols
  (Wield01 1500 → UsableDegree 180, IsMovingSkill 1, DlyTime 6000, Range 0, SP 9; Heal10
  1549 → Range 600, DlyTime 3000; Bleed10 1649 → DlyTime 10000) — all matching the combat
  facts; ItemInfo 14999 rows/57 cols; ClassName 28 rows. 404 for a missing table.

### Future work — AUTO-DISCOVERY (botnet learns the server by playing it)
For the case where we DON'T have the server files (`9Data`, `.shbd`, `NPC.txt`).
The botnet bootstraps its own world model from gameplay alone:
- **World map graph** — walk map→map through link portals, recording each gate's
  source map+coord and the map it lands on → reconstruct the inter-map graph that
  `MapLinkPoint.shn` would have given us.
- **Per-map walkability** — accumulate walked tiles + server "you can't go there"
  rejections into a learned occupancy grid (a discovered `.shbd` substitute).
- **NPC/gate catalogue** — log Briefinfo/NPC-spawn broadcasts + their coords as bots
  roam → a discovered `NPC.txt`.
- **Combat model** — derive enemy damage/HP/range by getting hit and recording the
  damage broadcasts; build a bestiary empirically.
- The two paths converge on the SAME in-memory model (`WorldModel`): server-files
  path *seeds* it offline; auto-discovery *learns* it online. Build the consumer
  (nav/follow) against that model so it's source-agnostic.

### Tooling — ServerSource as a SQL-queryable sibling (2026-06-11, operator idea)
Set up `C:/Projects/serversource-data` as a **fiesta-collab project** over
`Z:/ServerSource` (server env, importPath `Z:/ServerSource/9Data`). `fiesta import`
pulls all ~1257 SHN/text tables to JSON; `fiesta query "<SQL>"` then answers data
questions directly (NPC coords, gate links, item slots, class tables) instead of
hand-decoding SHN/EUC-KR each time. **Local-only — NOT a git repo / never commit
(ground-truth game data, BYO ethos).** This replaces the ad-hoc `fiesta shn` +
PowerShell-CP949 decoding used so far.

### NPC + gate discovery from the zone packet (DONE, live-verified 2026-06-11)
Implements the "prefer zone packets" steer. On field enter the zone sends
`NC_BRIEFINFO_MOB` (0x1C09, a `[mobnum][record×N]` list; singles regen via
`REGENMOB` 0x1C08). Each **149-byte** record (verified against `Full.pcapng`):
`handle u16 | mode u8 | mobid u16 | x u32 | y u32 | dir u8 | flagstate u8 |
flag-blob[99] | sAnimation[32] | 3`. The typed FiestaLib struct skips the blob, so
`ZoneView` parses the record **by hand** to also read the blob.
- **`flagstate` = 0 → plain NPC/mob, 1 → a gate.** A gate's flag-blob *begins with
  the destination map name* (null-terminated ASCII).
- `ZoneView` now tracks `NearbyNpc(handle, mobid, mode, x, y, flag, linkMap)` and
  exposes `NearbyNpcs`; **`GET /api/bots/{id}/npcs`** lists them (`isGate`,`linkMap`).
- **LIVE-VERIFIED:** BotPriest in RouN saw 29 entities; every mobid+coord matched
  the ServerSource SQL oracle exactly (mobid 28=RouSmithJames@5645,8824;
  30=RouGaianMaria@5769,6787; …) and all **7 gates** decoded with destinations:
  GateRou1→RouCos02, Rou2→RouCos01, Rou4→RouCos03, +EventF/EventF01/Fbattle01/
  SD_Vale01. So gate location AND where each leads now come from the zone, no
  server files at runtime.
- `mode==2` for every town entity (gates are NPCs with flagstate=1), so **mode is
  not the NPC-vs-monster discriminator** — that needs a *field* capture (walk a bot
  through a gate into RouCos and re-dump `/npcs`; the bot can now produce that
  itself). Gate *transition* packet still un-decoded — next live step.

### Near-future: multi-map pathfinding via the link graph (operator note 2026-06-11)
The client already does cross-map autorun (run to a quest location / quest-reward
NPC across several maps). The graph behind it is **`MapLinkPoint.shn`** (16851 rows
`MLP_FromID, MLP_ToID, MLP_Weight, MLP_OneWay_Street` — a weighted *directed* graph;
the IDs index a node table whose per-node (map,coord) still needs resolving). Build
cross-map routing on top of what we have:
- **High level:** Dijkstra/A* over the map-to-map graph → an ordered list of gates
  to take. We can source that graph two ways that converge: (a) the server file
  `MapLinkPoint.shn`, or (b) **auto-discovered** — the gate `linkMap` destinations
  we now decode per map already give the edges (RouN→RouCos01/02/03/EventF/…), so
  roaming bots rebuild the same graph without server files.
- **Low level (per map):** the verified in-map A* over the `.shbd` block grid, with
  the target = the coord of the gate whose `linkMap` is the next hop (from `/npcs`).
- So: pick route → for each hop, walk-to-gate (in-map A*) → take gate (transition
  packet, TODO capture) → re-acquire on the far side → repeat. This is also exactly
  the machinery "follow player across a map boundary" needs.

### Self-position tracking (SOLVED + implemented, 2026-06-11)
The bot now knows where it is. The spawn coord is the last 8 bytes of the [1802]
zone-login ack: `PROTO_NC_CHAR_MAPLOGIN_ACK` (sizeof 242) = `charhandle(2) +
param(CHAR_PARAMETER_DATA) + logincoord(SHINE_XY: x u32, y u32)`. Parsing the tail is
robust to the big param struct between. **Verified against Portals.pcapng:** [1802]
tail = (6444,8628) = the first `MoverunCmd`'s from-coord exactly.
- `ZoneEntry.EnterAsync` now returns `ZoneEntryResult(Conn, SpawnX, SpawnY)`;
  `BotManager` seeds `BotHandle.Position` from it and **advances it on every move**
  (`WalkPath` per-waypoint, `WalkAsync` to target). Surfaced in the snapshot
  (`position`, `mounted`).
- **`/walkto` no longer needs `from`** — it defaults to the tracked position (still
  accepts an explicit from). Unblocks walk-to-NPC/gate by name (target coord from
  `/npcs`, from = tracked pos). TODO: re-seed position after an in-band map change /
  town-portal (the new map's spawn coord arrives the same way).

### Multi-zone capture + gate transition (Portals.pcapng, all conversations)
The capture spans 5 TCP conversations — **decode them ALL, not just zone00**:
login 9010 (×2), WM 9013, **zone00 9016 (RouN, seed 0x4A)**, **zone 9019 (Eld +
Collapsed Prison + instance area, seed 0x22)**. The RouN→Eld town-portal crossed
*zone servers* (9016→9019 = a new TCP conn), which is why a single `--port 9016`
filter hid the second half. One zone server hosts several maps, so Eld→Collapsed
Prison is an **in-band map change on the same 9019 connection** (no new TCP). Eld-zone
chat markers tell the story: "In Eld, going to a normal port" → "Now in Collapsed
Prison" → "Pressing No on a port" (a port with a confirm dialog) → "Going to an
instance gate" → note: **instances require a party and can offer an easy/hard pick**
→ "logout". TODO: decode the exact in-band gate-transition frames on 9019 (the
LOGOUT/new-map-data sequence) and the port confirm-dialog (Yes/No) packet.

### Decoder must trace per-account chains (prep for 2-client party captures)
Operator will run **2 clients side-by-side** to capture party interactions. Before
that, `pcap_decode.py` needs to **group the conversations into per-account chains**
(login→WM→zone(s)) and label which is which — both clients share the source IP, so
4-tuple alone won't disambiguate. Linking keys to thread through: the account/
username (login auth/worldselect), the OTP handed login→WM, the WM handle used at
zone [1801], and the **character name** in `MAP_LOGIN_REQ` (and chat). Plan: tag each
conversation with {account, charName, phase} and print a per-account chain header so
two players' streams don't blur together. **Do this before the party captures.**

### Mount + town-portal protocol (SOLVED from Portals.pcapng, 2026-06-11)
Operator capture `Z:/Portals.pcapng` (chat-marked each action). Both flows decoded:

**Mount (raccoon, `&makeitem Racoon01_3` = 7-day):**
- Use = `ITEM_USE_REQ {invenslot, invenType=9}` — the *same* packet as any item use
  (raccoon was bag slot 4). **The "first use starts the timer" confirmation is a
  CLIENT-SIDE dialog only — there is NO accept packet.** The bot just sends
  `ITEM_USE_REQ` and it works; we bypass the dialog entirely (great — nothing to
  handle).
- Server reply chain: `ITEM_USE_ACK` → `ACT_CREATECASTBAR` (summon cast, ~1–10 s by
  mount) → `BRIEFINFO_REGENMOVER` (0x1C1A, the mount entity) → `ACT_CANCELCASTBAR`
  → **`NC_MOVER_RIDE_ON_CMD` (0xCC02)** = now mounted. Using the item again toggles
  **`NC_MOVER_RIDE_OFF_CMD` (0xCC06)** / remount.
- Mover dept = 0x33 (0xCCxx): RIDE_ON 0xCC02 (self), SOMEONE_RIDE_ON 0xCC04 (others),
  RIDE_OFF 0xCC06, HUNGRY 0xCC0A, MOVESPEED 0xCC0D. `ZoneView` tracks self ride state
  off 0xCC02/0xCC06.

**Town multi-select portal (the inter-town gate, → Eld in the capture):**
1. `BAT_TARGETTING_REQ {npcHandle}` (0x2401) — target the portal NPC.
2. **`NC_ACT_NPCCLICK_CMD {npcHandle}` (0x200A)** — click it (server opens the menu).
3. **`NC_MAP_TOWNPORTAL_REQ {destIndex:1 byte}` (0x181A)** — select destination.
   `destIndex` = the **`TownPortal` table Index** within the applicable group; capture
   sent `02` = Eld (group 0: 0=RouN,1=RouVal01,2=Eld). We know the indices offline, so
   no need to wait for/parse the menu.
Then server sends `NC_MAP_LOGOUT_CMD` (0x1805) and the client re-enters on the new
map (same as a zone change). **TODO live-verify** (drive it from a bot near a portal)
and confirm the post-teleport re-entry handling. This is the cheapest cross-town edge.

### Near-future: teleports as route edges (operator note 2026-06-11)
The route planner shouldn't only walk + take gates — **teleports are cheap edges**
that skip large walks. Model them as extra edges in the multi-map graph and prefer
them when available:
Two distinct mechanics (operator-clarified — don't conflate them):
- **Town multi-select portal** — a physical interactable in each town (NOT a
  scroll). Stand next to it, click, pick a destination map from a menu. **Free,
  always available, level-gated.** Data is `TownPortal.shn` (table `TownPortal`:
  `Index, MinLevel, TP_GroupNo, MapName`):
  - Group 0 (lvl1): RouN, RouVal01, Eld · Group 1: EldGbl02, Urg, Urg_Alruin(lvl70)
    · Group 2 (lvl100): Adl, Bera. (TP_GroupNo = the tier/portal set; MinLevel gates
    which destinations are selectable.)
  - The portal NPC shows up in `/npcs`; interaction = walk-to-it (in-map A*) →
    click → select-destination. Needs a capture of the click→menu→select packets.
    This is the **cheapest cross-town edge** and needs no inventory — likely the
    default for hub-to-hub routing.
- **Town-portal scrolls** — common, purchasable *consumable items*, one per major
  town hub: **Roumen, Elderine, Uruga, Alberstol Ruins, Bera**, etc. (a few rarer
  ones TP to *enemy* maps e.g. Dark Passage II — lower priority). Each is a one-shot
  edge to that town's hub; usable anywhere (not just in town), so they shortcut the
  walk *out* of the field. **Prefer a scroll over a long walk** when in stock and on
  route.
  - **Stock management:** track scroll counts per destination in inventory (we
    already decode the bag in `ZoneView`); resolve scroll-item → destination-town
    from `ItemInfo` (TODO: identify the scroll item IDs). Find the vendor(s) that
    sell them (`NPCItemList` ↔ merchant NPCs) so a bot can restock; keep a
    configurable minimum stock.
  - **Dashboard warnings:** surface actionable status — "not enough money to
    restock", "only 2 ports left for Uruga", "no Roumen scroll" — so the operator
    sees why a bot chose to walk vs port.
- **GM cheat-teleport (we have GM):** as GM the bot can `&makeitem` scrolls for
  free *and* likely **cheat-TP to any map directly** (near-future — needs the GM
  warp command + a capture) and **TP to a player cross-map**. For a GM bot these
  become near-free edges that dominate the route graph.
- **Guild academy teleport** — the academy system lets one designated *academy
  master* TP to any academy member. Great fit for a support/priest bot: park a
  master in town, summon/reach members on demand. Needs the academy-teleport
  packet (capture) + guild/academy state. (Structs likely in FiestaLib
  `GuildAcademy.cs` / `GuildAcademyOpcode.cs`.)
- Design: each teleport is just a weighted edge the planner already understands —
  walk = grid-distance cost, scroll = small fixed cost if in stock (else ∞), GM
  warp = ~0. So "prefer TP to shorten walk" falls out of shortest-path; no special
  casing.

## Navigation v3 — cross-map transitions DECODED + foundation (2026-06-11)

### Map-transition packets (from Portals.pcapng, all-zone decode)
Using a gate is **position-OR-click triggered**; the client took the in-band gate by
**target + NPC-click** (NPCs with flagstate=1 are gates) — `NC_BAT_TARGETTING_REQ`
(0x2401) then `NC_ACT_NPCCLICK_CMD` (0x200A) on the gate handle. **No need to walk
onto the tile** — just be within the gate's range. The zone then replies with
`NC_MAP_LOGOUT_CMD` (0x1805, `[handle u16]`) followed by one of:

- **`NC_MAP_LINKSAME_CMD` (0x1809)** — in-band map change, *same zone process / same
  TCP connection*. Payload `[mapId u16][x u32][y u32]` (10 b). Bot just re-seeds
  position + switches block grid. (Capture: Eld(9)→EldPri01(13)→Eld(9).)
- **`NC_MAP_LINKOTHER_CMD` (0x180A)** — handoff to a *different zone server*. Payload
  `[mapId u16][x u32][y u32][ip char[16]][port u16][wmHandle u16]` (30 b). Bot must
  open a NEW connection to ip:port and re-send `MAP_LOGIN_REQ` using the carried
  **wmHandle** (the trailing u16) — confirmed: the 9019 reconnect's chardata was
  `[0x7B0C]["BotPriest"]` + the same 49 checksums, and 0x7B0C == LINKOTHER's tail.
  The WM link (9013) stays open across the handoff. (Capture: RouN→Eld = LINKOTHER
  to 62.171.171.24:9019, spawn (11802,10466).)

Multi-destination gates: server sends `NC_MAP_MULTY_LINK_CMD` (0x181F,
`npcHandle, npcPosition, limitRange, num, LinkMapName[5]`) when near; client picks
with `NC_MAP_MULTY_LINK_SELECT_REQ` (0x181F C→S, `LinkMapName` Name3=12 b).
Town portals are the separate `TOWNPORTAL_REQ`/`portalindex` path (already done).

**mapId = `MapInfo.ID`** (serversource SQL): 150=RouN (RegenXY 6445,8630 = our spawn),
9=Eld, 13=EldPri01. `MapInfo.MapName` = the `.shbd` shortname & gate `LinkMap`.
Parsers verified against the exact capture bytes (dotnet-script).

### Implemented this iteration (commit pending)
- `Navigation/MapHandoff.cs` — LINKSAME/LINKOTHER record + verified parsers.
- `Navigation/MapCatalog.cs` — id↔shortname, learning-first (pairs gate LinkMap +
  handoff mapId); optional `MAPINFO_PATH` CSV bootstrap (BYO).
- `Navigation/MapGraph.cs` — gate-edge graph, BFS `Route()`, auto-discovered by play.
- `ZoneView` — parses LINKSAME/LINKOTHER, tracks `CurrentMapId`, clears per-map
  entities, raises `MapChanged`.
- `BotHandle.CurrentMap` (seeded from `BotSpawnOptions.StartMap`="RouN", updated on
  transition) + `Map` in the snapshot.
- `BotManager` — `UseGateAsync` (target+click+optional dest select), `ObserveGates`,
  shared `Graph`/`Catalog`, `OnMapChanged` (re-seed + map-name update).
- Endpoints: `POST /{id}/use-gate`, `GET /{id}/gates` (folds view into graph),
  `GET /{id}/route?to=<map>` (read-only BFS plan).

### Cross-map travel — BOTH pieces DONE (live-verified 2026-06-12)
1. **Cross-server reconnect on LINKOTHER (DONE).** `RunBotAsync` is a re-enterable
   zone loop: the WM link stays open for the bot's whole life; a `ZoneView.MapChanged`
   with `IsCrossServer` trips a per-session `zoneCts`, the `RunAsync` await returns, and
   the loop reconnects via `ZoneEntry.EnterAsync(newEp, ho.WmHandle, charName)` to the
   carried ip:port. In-band LINKSAME never reaches the loop — it's handled live on the
   same connection by `OnMapChanged` (re-send `0x1803`). Field-to-field travel works.
2. **Autonomous `/travelto <map>` (DONE).** `BotManager.TravelTo` → `RunTravelAsync`
   background loop: `Graph.Route(from,to)` (BFS) → per hop `GateTo(dest)` (resolve the
   live in-view gate by `LinkMap`) → `ApproachAsync` (in-map A* to the gate coord, stop
   ~60u short) → `UseGateAsync` (auto-answers an instance confirm menu) → wait on a
   `BotHandle.MapChangeSeq` bump (transition-agnostic; survives the cross-server ZoneView
   swap) → for a cross-server hop wait until `Phase==InZone` again → `ObserveGates` the
   new map → next hop. Retries once onto the exact gate tile if a from-range click
   doesn't fire. Endpoints: `POST /{id}/travelto {to,unitsPerSec?}` (202 + plan, or 404
   no-route / 409 not-in-zone), `POST /{id}/stoptravel`.
   - **Map-name learning (the one subtlety):** the handoff carries only the map *id*.
     The travel loop sets `BotHandle.PendingDestMap` (the gate's `LinkMap`) *before*
     taking the gate; `OnMapChanged` then resolves `Catalog.NameFor(id) ?? PendingDestMap`
     and `Catalog.Learn(id, name)` — deterministically, *before* the cross-server
     reconnect re-reads `CurrentMap`. (First attempt corrected the name in the travel
     loop and lost a race with the reconnect → it stuck as `map#3`; moving the resolution
     into `OnMapChanged` fixed it.)
   - **Live runs (testuser/BotPriest):** in-band `EldPri01→Eld` (pathfound 126 waypoints
     to the exit gate, LINKSAME, re-sent `0x1803`, arrived); cross-server `Eld→RouCos03`
     and `RouCos03→Eld` (LINKOTHER reconnect 9019↔9016, re-entered zone, name resolved to
     the real short-name). No kicks.
   - **Also (operator ask):** `/walkto` now defaults `map` to the bot's `CurrentMap` (and
     `from` to its tracked position) — so a full-pathfinding walk needs only `{toX,toY}`.

### Deferred (next iterations)
- **Multi-destination gates in travel:** `TravelTo` clicks single-dest/menu gates
  (verified). A gate that sends `MULTY_LINK_CMD` needs the `destMap` select passed to
  `UseGateAsync` — wire it when one is on a route.
- **Teleport edges (town portal / scroll / GM warp)** as cheaper graph edges — the
  planner already treats every hop as an edge, so these slot in as weighted edges.

## Combat — instances, auto-attack, damage skills, self-heal (2026-06-11/12, live-verified)
Built combat for the lvl-70+ BotPriest in the **EldPri01** (Collapsed Prison)
instance. Decoded from `Z:/CombatExtensive.pcapng` (chat-annotated) +
`Z:/PartyFriendTarget.pcapng`; all kills/heals live-verified on `testuser`/BotPriest.

### Instance entry — the LINKSAME populate fix (the big one)
- The EldPri01 gate is a **walk-on trigger**: standing on it auto-opens a server
  menu **`0x3C01`** ("move to Collapsed Prison field?"). Answer with
  **SERVERMENU_ACK `0x3C02` `[00]`** (option 0 = Yes). `UseGateAsync` now answers an
  already-open menu (we spawn on the trigger when we logged out inside) or polls ~3s
  for one after target+click.
- The transition is **in-band LINKSAME `0x1809`** (same zone server 9019, server-driven
  warp `[mapId u16][x u32][y u32]`), NOT a reconnect. **The client MUST re-send
  MAP_LOGINCOMPLETE `0x1803` after the warp** or the new map never populates (no mob
  broadcasts — bot sits in limbo). Verified in `Z:/Portals.pcapng`: per transition the
  server sends `0x1805 LOGOUT_CMD` → `0x1809 LINKSAME_CMD`, client replies `0x1803`.
  `OnMapChanged` now fires `0x1803` on in-band changes. (The cross-server LINKOTHER
  path already re-logs in, so it sent `0x1803` implicitly — that's why town↔town
  worked but field-instance didn't.)
- A character that logs out inside an instance **logs back in directly inside it**
  (WM `loginmap` = "EldPri01"), so the map name resolves and the grid loads normally.

### The cast sequence + the FACING root cause
Every offensive cast that the server REJECTED came back as **`0x2434`
NC_BAT_SKILLBASH_CAST_FAIL_ACK** (2-byte reason, varies — not load-bearing). Heal
(self-target) always worked; enemy-target damage always failed. Root cause, after
ruling out weapon, range, stop, windup, and skill tier: **facing.** ActiveSkill has
**`UsableDegree`** (front arc, 180°) — the target must be in front of you. There is
**no rotate packet**; the client turns via **MOVERUN** (its from→to vector sets
facing). The accepted sequence (CombatExtensive.pcapng):
```
TARGET 0x2401 [handle] → CHANGEMODE 0x2008 [02] → MOVERUN 0x2019 (toward target = FACE)
  → STOP 0x2012 [x,y] → SKILLBASH_OBJ_CAST_REQ 0x2440 [skill u16][target u16]
```
Server success → `SKILLBASH_HIT_OBJ_START`/`SKILLBASH_CAST_SUC_ACK`/`HIT_DAMAGE`
(`0x244E` carries skill+target+cast-id) and `REALLYKILL 0x244A [mob,self]` on kill.
`CastAsync(stopFirst)`: damage casts send a **tiny MOVERUN toward the target** (≤16u,
capped so a ranged caster never closes into melee) + STOP before the cast to set
facing; heal passes `stopFirst:false` (self-cast needs no facing, castable while
moving). **Don't couple auto-attack to skills** — a mage casts without ever swinging.

### Auto-attack (melee swings)
Separate `/autoattack` (and `/stopattack`) endpoint: TARGET → CHANGEMODE → MOVERUN
into melee + STOP → **BASHSTART `0x242B`** (empty). Server then streams continuous
**`0x2447` SWING_START / `0x2448` SWING_DAMAGE** until the mob dies (`0x244A`) or
**BASHSTOP `0x2432`**. "Click once, many swings." Kills mobs on its own (used the
operator's hammer; ~150/swing). Optional — layer skills on top or not.

### Self-heal
`/heal` casts a Heal on **SelfHandle** (the first u16 of the `[1802]` MAP_LOGIN_ACK,
captured in `ZoneEntry`/`BotHandle`). Live: HP 2220→2495 (full) after a mob hit us.
Heal is IsMovingSkill + self-target → no facing/stop. (Casting on an enemy while a
heal "lands on me" in the real client is a client-side redirect — we self-target.)

### Skill data facts (ActiveSkill table in serversource-data; ItemInfo for scrolls)
- **`UsableDegree`** = facing arc (must face target). **`IsMovingSkill`** = castable
  while moving (all the combat skills tested = 1, so movement isn't the gate; facing
  is). **`DemandType`** = weapon-class requirement. **`Range`** (0 = melee; live melee
  range is generous, ~100–200u). **`DlyTime`** = cooldown ms (Wield 6s, PsychicChop
  10s, Heal 3s). **`SP`** = mana cost.
- **Display name ≠ InxName.** "Bash" = **Wield** series (1500–1514, 1500=Wield01 cheap,
  **any weapon**); "Bleed" = **PsychicChop** (1640–1649, **mace-only**); Heal =
  1540–1559 (1549=Heal10). A priest can't use BashStrike (240–250, Fighter) or
  Wield15's UseClass-12 tier — `&learnskill 1500` for the basic mace attack.
- **Skill scrolls** live in **ItemInfo**: `Name` contains `[01]` for first-in-series,
  `UseClass` = class (Heal[01] scroll = UseClass **8** = priest/cleric),
  `ItemUseSkill` links scroll→skill.
- **Equipped gear** = `tItem.nStorageType=8`, `nStorage` = equip slot (**12 = weapon**);
  `nStorageType=9` = bag, `12` = quickbar. `/equip {slot}` equips a bag slot →
  weapon slot. BotPriest (charNo 1006) shipped with the **hammer (942)** equipped;
  swapped to **mace (750)** to satisfy PsychicChop's mace requirement.

### New endpoints
`/autoattack`, `/stopattack` (bashstart/bashstop), plus `/heal` (self) and `/attack`
(skill on target/nearest, now faces first). `/use-gate` auto-answers the instance
confirm menu.

### Next / deferred
1. **DONE (2026-06-14) — data-driven cast from `ActiveSkill`** (integrated from the
   `ProjectsOpenCode` checkout). `CastAsync`/`CastGroundAsync` now take `bool? stopFirst`
   (default `null` = auto): a shared `ResolveFaceStop` reads the skill row and faces when
   **`UsableDegree > 0`** and/or STOPs when **`IsMovingSkill == false`**; an explicit
   `true`/`false` overrides. **Fallback added on integration** (their copy omitted it):
   no client data / unknown skill → face+STOP, the proven default, so a damage cast is
   never silently rejected. `HealSelfAsync` now passes `null` (heal is a moving-skill →
   no STOP via the data path). Still TODO on top of this seam: **`DlyTime`** cooldown
   enforcement, **`Range`** reject/approach, **`SP`** mana-gating (the SkillInfo fields
   are already projected — `ClientData.Skill`).
2. ZoneView doesn't parse the login item burst → `/inventory` `/equipment` read empty
   (cosmetic; equip-by-slot still works).

## Mage Frost Nova — location/AoE cast, cast-time, soul stones, error codes (2026-06-12, live-verified)
Demonstrated a mage casting **Frost Nova** (a cast-time, location-targeted AoE) end to
end. Several distinct mechanics fell out, all BotMage/`botmage1`-verified.

### Location-targeted (ground) cast — `/castground`
A skill that **takes a coordinate, not a target unit** (Frost Nova) uses
**`NC_BAT_SKILLBASH_FLD_CAST_REQ` (0x2441)** `{skill u16, locate SHINE_XY}` — the FLD
(field) analogue of the OBJ cast. `BotManager.CastGroundAsync` → `POST /{id}/castground
{skill,x,y}`: CHANGEMODE → face+stop toward the point (extracted the shared
`FaceAndStopAsync` helper) → FLD_CAST. **Cast-time skills are still the SKILLBASH family**
(NOT the separate SKILLCAST 24–30 opcodes — those have no generated struct and aren't used
here): the capture shows cast-time casts as `OBJ_CAST` + an abortable castbar
(`0x2444 CASTABORT`). Success chain: `0x2435 SKILLBASH_CAST_SUC_ACK` → (after `CastTime`)
`0x2457` HIT_FLD_START (carries num-targets + caster + skill) → `0x243C` HIT_DAMAGE per
target → `0x244A REALLYKILL` on kill. **Live: Frost Nova hit 2 mobs, killed one.**
- `ActiveSkill.CastTime` (ms) is the cast duration — Frost Nova **1950**; every priest
  skill that worked was `CastTime=0` (instant). `IsMovingSkill=0` for Frost Nova (must
  stand), `Range=300` (ranged), `TargetNumber=10` (AoE cap). The face+stop sequence the
  priest used is fine for cast-time too (the capture does MOVERUN→STOP→cast).

### "Use an SP/HP stone" — soul-stone recharge (its own packet, NOT an item)
The in-game "SP stone" is **`NC_SOULSTONE_SP_USE_REQ` (0x5009)**, empty payload — a
dedicated SOULSTONE-dept (20) packet, decoded from CombatExtensive.pcapng (chat "I will
use an SP stone" → 0x5009). It draws SP from the character's soul-stone **reserve** into
current SP; server replies `0x500A SP_USESUC_ACK` (or `0x5006 USEFAIL`). The **HP**
analogue is **`NC_SOULSTONE_HP_USE_REQ` (0x5007)** → `0x5008 HP_USESUC_ACK` (important for
combat survival). There are also `_BUY_REQ` (HP=0x5001/SP=0x5002) to recharge the reserve.
Endpoints `/{id}/soulstone-sp` + `/{id}/soulstone-hp`. **A fresh char already has a
charged reserve** (the use succeeded on a just-made BotMage — no buy/equip needed). The
reserve isn't in an obvious `World00_Character` column (server-internal).

### Cast-fail error codes (`0x2434 SKILLBASH_CAST_FAIL_ACK`, 2-byte reason) — learned empirically
The reason codes are **server-side** (not in FiestaLib enums), so they were captured by
triggering each failure. They live in an **`0x0Fxx`** range:
- **`0x0FC9` (bytes `C9 0F`) = NOT ENOUGH SP** — confirmed: a Frost Nova that failed with
  this succeeded after a soul-stone SP recharge, nothing else changed.
- **`0x0FCA` (bytes `CA 0F`) = OUT OF RANGE** — confirmed: same cast at 1050u (> Range
  300) failed with this; in range it succeeded. (This also retro-explains the priest's
  old `CA` fails = out-of-range.)
- `0x0FC4`/`0x0FC6` seen earlier (priest) = other reasons, not yet pinned (facing /
  cooldown / weapon — TODO trigger + record). **Next:** decode this reason in `ZoneView`
  and react (recharge SP on `0x0FC9`, approach on `0x0FCA`).

### Provisioning a GM mage via the API key (one-call GM account)
- **API key (minted this session):** `POST {base}/api/accounts` with `X-Api-Key` accepts
  **`IngameGmLevel:9`** (operator's account-API addition) → creates a `nAuthID=9` account
  in one call (it also set per-char `nAdminLevel=100`). Keys hash as SHA-256-hex in
  `Account.tApiKey` (irrecoverable from DB) → the minted plaintext is stored gitignored
  (CLAUDE.md) + memory, never committed. `botmage1` (nUserNo 105) created this way.
- **Class = Enchanter (`ClassName` nClass 18), level 60.** Frost Nova [01] = ActiveSkill
  **6440**; its **scroll** (ItemInfo **7340**) gates learning at `UseClass 22` / `DemandLv
  60` = the lvl-60 2nd mage promotion = **Enchanter**. Created a Mage, then SQL
  `nClass=18, nLevel=60, nAdminLevel=100`, relog, `&learnskill 6440`. **Stats/max-SP are
  automatic from level** (no stat columns to set) — but a raw SQL `nLevel` bump leaves
  **current** SP at the old value, hence the soul-stone recharge before a costly cast.

### Two parallel class enums (operator-RE'd, validated) — origin = future work
There are **two** class numberings (full table in memory `fiesta-useclass-enum`):
- **`ClassName.shn` ClassID** — the *visual* class on the character sheet (renames at lvl
  80); `tCharacterShape.nClass` uses it. Mage=16, WizMage=17, Enchanter=18, …, Chaser=22.
- **`ItemInfo`/scroll `UseClass`** — what actually **gates** equipping items / learning
  skills, a *different* enum: Mage=20, WizMage=21, **Enchanter=22**, Lvl80Mage=23,
  Warlock=24, Wizard=25; Joker line 27–32 (UseClass 26 = an SP-extender consumable slot,
  not a class); 33/34 = Sentinel/Savior special line (no lvl-80 tier). Validated against
  per-`UseClass` DemandLv bands. **FUTURE WORK (not now):** find *where the UseClass enum
  is defined* (server class table / a client mapping) rather than relying on the RE'd
  list — so item/skill class-gating is sourced, not inferred.

### New endpoints (this cycle)
`/castground` (location/AoE cast), `/soulstone-sp`, `/soulstone-hp`. `CastAsync`'s facing
logic was refactored into the shared `FaceAndStopAsync` (used by OBJ and FLD casts).

### TODO — real-time event/log stream (needed; flagged 2026-06-12)
We're flying blind during combat: the only feedback is polling `GET /{id}` for the log
ring-buffer **after** the fact. We need a **streaming endpoint — WebSocket or NDJSON/SSE**
— that pushes bot events live (inbound combat frames decoded: HIT_DAMAGE, our HP/SP
changes, SWING/cast results, REALLYKILL, deaths, chat, map changes). Then we can watch a
fight unfold in real time instead of reconstructing it from a stale log. Concretely: a
`GET /{id}/events` that upgrades to WS (or streams `application/x-ndjson`), fed from the
same `BotSession.PacketReceived` / `ZoneView` events the perception layer already raises.
- **Why it bit us:** Frost Nova "hit 2 mobs, killed 1" means the **other mob aggroed and
  was hitting BotMage** — the bot likely **died** while we couldn't see its HP dropping.
  This is exactly what the HP soul-stone (`/soulstone-hp`) + a live HP feed are for
  (auto-recharge HP when it drops). Combat needs live perception, not post-hoc polling.

### Cast-fail codes — more to learn (esp. cast-into-wall/blocked)
Beyond `0x0FC9` (not enough SP) and `0x0FCA` (out of range), expect a distinct code for
**casting a ground/AoE skill onto a blocked/wall tile** (off the walkable grid). TODO:
trigger it (cast Frost Nova at a `.shbd`-blocked tile within range) and record the
`0x0Fxx` value, alongside `0x0FC4`/`0x0FC6` (priest, unpinned).


## Cast-fail reactive handling (DONE, live-verified 2026-06-13)

Added reactive handling for `NC_BAT_SKILLBASH_CAST_FAIL_ACK` (0x2434):

1. **`ZoneView.cs`** — parses the 2-byte reason code from 0x2434, logs known codes
   (0x0FC9 = not enough SP, 0x0FCA = out of range) with a human label and unknown
   codes with the raw payload hex. Fires `CastFailed(ushort reason)` event.
   Added `CastFailReason` static class with `NotEnoughSp`/`OutOfRange` constants.

2. **`BotHandle.cs`** — added `LastCastSkill` and `LastCastTarget` volatile fields,
   updated by `CastAsync`/`CastGroundAsync` before sending, so the reactive layer
   knows what to retry.

3. **`BotManager.cs`** — subscribes to `ZoneView.CastFailed` in `RunBotAsync`:
   - **0x0FC9 (not enough SP):** fire-and-forget `UseSoulStoneSpAsync` — recharges
     current SP from the character's soul-stone reserve.
   - **0x0FCA (out of range):** walks one capped step toward the target's current
     position (if still in view), then retries the last cast (targeted or ground).
   - Unknown codes are already logged by ZoneView with hex payload — discoverable.

**Live-verified (2026-06-13):** Both the event infrastructure and the reactive
handler fire correctly on the live server:

- **0x0FCA detected + approach + retry cast sent.** Casting Wield01 (1500) on a
  mob ~1021u away (out of melee range) → `0x2434 [CA0F]` → `CastFailed` → walk
  step toward target with correct coords (uint underflow was the bug: `tp.X - pos.X`
  wraps when both are `uint` and tp < pos; fixed by doing `(double)tp.X - pos.X`
  before any other math) → retry `CastAsync`. Walk coords went from garbage
  `(1463,2561)->(1470419777,2744)` to correct `(1463,2561)->(1312,2760)`.

- **0x0FC6 logged as unknown.** Casting Wield01 on a friendly NPC (mobId 27,
  handle 17123) produced `0x0FC6` — logged as `"cast FAILED - unknown reason
  0x0FC6 (2b payload)"`. This is one of the previously unpinned codes; likely
  "invalid target" or "wrong weapon type". The unknown-code logging collects
  exactly the reverse-engineering data the operator needs.

- **0x0FC9 not triggered** (bot had sufficient SP for all casts). The code path
  is the simplest of the three — a single `SendAsync` — and shares the verified
  event infrastructure.

- **Edge case noted:** in walled dungeons (EldPri01) the straight-line approach
  hits a MOVEFAIL snapback, creating a cast-fail → approach → MOVEFAIL → retry
  loop. Mitigated by using proper pathfinding during approach (future work).

## MOVESPEED tracking — dynamic WalkPath pacing (DONE, live-verified 2026-06-13)

The bot now paces its movement packets against the server-authoritative movement
speed, not a hard-coded 120 u/s. This lets a mount (or speed abstate) double the
pace without MOVEFAILing.

### Protocol discovery (from captures)
Two opcodes carry movement speed. The character always **runs** by default
(runspeed); walkspeed (33) is a slow toggle.

| Opcode | Dept | Fields | When |
|---|---|---|---|
| `0x203E` NC_ACT_MOVESPEED_CMD | Act (8) | `walkspeed u16, runspeed u16` | Self-only, periodic (base speed) |
| `0xCC0D` NC_MOVER_MOVESPEED_CMD | Mover (51) | `nMoverHandle u16, nWalk u16, nRun u16` | Any mover, sent on speed change (mount/abstate) |

Speed encoding: raw runspeed × (120 / 127) ≈ u/s. Base human running: 127 → 120
u/s. Mounted running (Racoon01_3): 254 → 240 u/s (doubled).

### Implementation
- **`ZoneView.cs`** — parses both 0x203E (always self, no handle filter) and
  0xCC0D (filtered by self handle OR active mount handle). Conversion via
  `SpeedRawToUPerSec = 120.0 / 127.0`. Fires `WalkSpeedChanged(double)` event.
  Tracks `WalkSpeed` property (default 120.0).
- **Mount handle tracking** — 0xCC0D broadcasts the *mount's* handle, not the
  player's. RIDE_ON (0xCC02) payload is `[mountHandle u16]` — captured as
  `_mountHandle` and used alongside `SelfHandle` in the 0xCC0D filter.
- **Speed reset on dismount** — RIDE_OFF resets WalkSpeed to 120 u/s so a stale
  mount speed doesn't pace post-dismount movement.
- **`BotHandle.WalkSpeed`** — set by the ZoneView event subscriber; read by
  WalkPath each step for the pacing delay.
- **`WalkPath`** — delay calculation changed from `unitsPerSec` (param) to
  `handle.WalkSpeed ?? unitsPerSec`, so the pace live-tracks the current speed.

### Live-verified (2026-06-13, Eld, testuser/BotPriest)
```
17:21:58 [ZoneView] move speed: 120 -> 240 u/s (raw: walk=36 run=254, CC0D)
17:22:04 walk-path done (47 waypoints, 48 move steps)  ← mounted, 0 MOVEFAILs
17:22:44 [ZoneView] move speed: 240 -> 120 u/s (raw: walk=33 run=127, 203E)
17:23:34 walk-path done (51 waypoints, 50 move steps)  ← dismounted, 0 MOVEFAILs
```
**Zero MOVEFAILs** on both walks (48 vs 50 steps). Speed change correctly
detected from 0xCC0D (mount handle), reverted via 0x203E after dismount.

### Speed data (SHN reference)
- **Riding table**: Donkey RunSpeed=1300 FootSpeed=1100, Claude RunSpeed=3300
  FootSpeed=1100 — these use different encoding than the wire protocol.
- **SubAbState**: Type/SubType/ActionIndex/ActionArg system drives speed-altering
  abstates. Could be decoded if needed for prediction, but the wire broadcast
  (0xCC0D/0x203E) is authoritative and simpler.

## Scriptable behaviours (Lua) + state machines — THE NEXT BIG ARC (planned 2026-06-13)

### Vision (operator)
Keep the HTTP control surface, but make every bot **scriptable**. Upload a Lua
**behaviour script**, apply it to a bot, and the bot **loops it** — handling
low-latency reactions the HTTP round-trip can't (process every hit, listen to chat
for commands, "HP-stone when low", "skill when X"). Build/upload scripts on the fly;
a new upload replaces the running one. Then **compose** scripts into **state
machines** (behaviour trees) for bigger logic — an "Explore" tree, a "Fighter
level-in-group" tree, a "buff guild" tree (wait for "buff pls" in chat → academy
teleport → buff). **End goal: cross-tree transitions**, and likely a different
tree per class.

### Engine decision: MoonSharp (locked)
| Decision | Choice | Why |
|---|---|---|
| Lua engine | **MoonSharp 2.0.0** (pure-managed C#) | No native deps → works under Wine/Linux/Docker like the rest of the stack (NLua's native KeraLua bindings would break the container/Wine story). Sandboxes cleanly (drop `io`/`os`/`require`). |
| Concurrency | **One Lua VM per bot, single-threaded** | MoonSharp VMs aren't thread-safe and the game raises events from the session read loop. A dedicated per-bot script thread drains an event queue → no locking / re-entrancy. Standard game-scripting pattern. |
| Event source | **Stable per-bot event hub on `BotHandle`** | `ZoneView`/`ZoneSession` are *swapped* on a cross-server reconnect; a script must not lose its subscriptions. So events route through `BotHandle.Emit` (survives swaps). Doubles as the seam the deferred WS `/events` stream will use. |
| Script storage | **In-memory library** (`ScriptStore`) first | "Build + upload on the fly." Disk/Git persistence is an easy follow-up; not needed to prove the loop. |

### Architecture (layers — each builds on the last, same runtime)
```
Layer 0  manual HTTP actions on BotManager           [DONE — exists today]
Layer 1  one Lua script loop per bot                 [FIRST GOAL — this arc]
Layer 2  state machine = named scripts + transitions [next]
Layer 3  cross-tree transitions + per-class trees    [end goal]
```
A state (Layer 2) is *just a behaviour script* with `on_enter`/`tick`/`on_exit` +
a transition check; the SM engine swaps the active script on a transition. Layer 3
is the same engine with a transition table that can target states in **other**
trees. So Layers 2–3 reuse Layer 1's runtime wholesale — no new execution model.

### Script contract (Lua)
A script defines any subset of these globals; the runner calls the ones present:
```lua
function on_start()        end   -- once when applied
function tick()            end   -- looped every tickMs (default 250)
function on_chat(msg)      end   -- {handle, name, text}
function on_hit(ev)        end   -- {attacker, defender, damage, restHp}
function on_cast_fail(r)   end   -- reason code (0x0FC9 sp, 0x0FCA range, ...)
function on_hp(hp, max)    end   -- self HP changed
function on_player(p)      end   -- nearby player appeared {handle,name,class,level,x,y}
function on_map(map)       end   -- map changed
function on_stop()         end   -- once when stopped/replaced
```
Injected globals: `bot` (the action+perception facade) and `log(msg)`.
`bot` actions (sync wrappers over `BotManager`, fast packet sends):
`say/whisper/cast/castGround/attack/autoAttack/stopAttack/heal/useItem/equip/gm/
soulstoneHp/soulstoneSp/target/untarget/walkTo/walk/travelTo/stopTravel/follow/
stopFollow/useGate/townPortal/party*/friend*`.
`bot` state/perception getters: `hp/sp/maxHp/maxSp/hpPct/spPct/x/y/map/selfHandle/
mounted/walkSpeed/phase/inZone/now`, `nearestMob/nearbyMobs/nearbyPlayers/npcs/
gates/inventory/equipment/playerByName`.

### First-goal deliverable (THIS iteration — upload → apply → loop)
1. **Self HP/SP tracking (prereq for "HP-stone when low" + on_hit).** Was missing.
   - `[1802]` param block carries **MaxHp @ off 146, MaxSp @ off 150** (body =
     `charhandle(2)+CHAR_PARAMETER_DATA(232)+logincoord(8)`, sizeof 242). `ZoneEntry`
     parses them into `ZoneEntryResult`.
   - Current HP/SP arrive as typed CMDs: **`PROTO_NC_BAT_HPCHANGE_CMD` 0x240E
     `{hp u32, hpchangeorder u16}`** and **`PROTO_NC_BAT_SPCHANGE_CMD` 0x240F
     `{sp u32}`**. `ZoneView` tracks `Hp/Sp/MaxHp/MaxSp`, fires `HpChanged/SpChanged`.
   - Hit events for `on_hit`: `SWING_DAMAGE_CMD`/`DOTDAMAGE_CMD`/`SOMEONESWING_DAMAGE`
     carry `attacker,defender,damage,resthp` → `ZoneView.Damaged`.
   - Surfaced on `BotHandle` + `Snapshot` (`hp/sp/maxHp/maxSp`).
2. **Stable event hub:** `BotHandle.Emit(BotEvent)` + `event Events`; `BotManager`
   (where it already subscribes ZoneView events in `RunBotAsync`) also emits.
3. **`Scripting/` (in `Fiesta.Bot`):** `BotApi` (facade), `BotScriptRunner`
   (single-thread executor + event queue + tick), `ScriptStore` (library), wired
   into `BotManager.ApplyScript/StopScript/ScriptStatus` (and `StopAsync` tears the
   runner down). MoonSharp added to `Fiesta.Bot.csproj`.
4. **Endpoints (`ScriptEndpoints.cs`):**
   - `POST /api/scripts {name, source}` (compile-check; 400 w/ Lua error), `GET
     /api/scripts`, `GET /api/scripts/{name}`, `DELETE /api/scripts/{name}`.
   - `POST /api/bots/{id}/script {name|source}` — apply + loop (replaces running).
   - `POST /api/bots/{id}/script/stop`, `GET /api/bots/{id}/script` (debug: running,
     tickCount, lastError, last Lua state).
5. **Tracked sample scripts** (`scripts/`): `auto_grind.lua` (attack nearest;
   soulstone-HP when low), `town_buff.lua` (on_chat "buff pls" → cast buffs on sender).

### Verification (first goal)
`dotnet build`, then live (testuser/BotPriest or BotMage on 62.171.171.24): upload a
script → `POST /{id}/script` → watch `GET /{id}/script` + the bot log loop it; confirm
a new upload swaps cleanly and `/script/stop` halts it. HP/SP via `GET /{id}` snapshot.

### Live log stream + console + call-trace (DONE 2026-06-14, live-verified)
- **`log(msg)` AND Lua `print(...)`** both route to the bot log → host console → the live
  stream (`Script.Options.DebugPrint` catches `print`/`io.write`).
- **`GET /api/bots/{id}/logstream`** — NDJSON (`{"line":...}` per line), `?tail=N` backfills
  the last N then streams live off `BotHandle.LogLine`; bounded channel (drop-oldest) so a
  slow reader can't grow memory; ends on client disconnect. `curl -N` to watch a bot.
- **`trace:true`** on apply wraps `bot` in a metatable proxy that logs `call bot.<fn>(args)`
  before forwarding (return values pass through) — every action/getter is visible on the
  stream. Opt-in (noisy).
- Live-verified (testuser/Anna @ RouCos01): tailed `/logstream` while applying a traced
  script — saw `print()` output, `on_start`, and `call bot.map/hp/nearestMob(...)` lines
  stream in real time with the real return values (`tick 8 hp=91 nearest=675`).

### Layer 2 — state machines (DONE 2026-06-14)
A state machine is **pure Lua on the existing runtime** (no new execution model, exactly
as planned). The runner injects a `statemachine(states, initial)` harness: each state is a
table of callbacks; the harness wires the top-level `on_*`/`tick` to dispatch to the
**current** state and switch when its `next()` returns another state's name (running
`on_exit`/`on_enter`). A plain script that never calls it is unaffected.
- **Current state → C#** via `bot.__state(name)` (the harness reports each transition) →
  `BotScriptRunner.CurrentState` → `ScriptStatus.SmState`, so the debug endpoint shows it.
- **`POST /api/bots/{id}/statemachine`** (name or inline source; same `ApplyScript` runtime
  as `/script`); `GET /api/bots/{id}/script` now carries `smState`.
- **Cross-tree transitions / per-class trees fall out for free**: compose multiple state
  tables into one `states` table and a `next()` may target any state's name.
- Samples: `scripts/grind_sm.lua` (roam→fight→recover), `scripts/guild_buff_sm.lua`
  (idle→buff, chat-driven). Harness unit-tested (explore→fight→explore by perception).

## Merchant / enchant / craft protocol (decoded from captures 2026-06-14)

### Shop buy/sell (DONE — open-handshake live-verified, PurchaseSell.pcapng)
Opening a merchant's shop is a **menu handshake**, not a bare click (this was the missing
piece):
```
C→ 0x200A NPCCLICK_CMD {npchandle}      → click the merchant
S← 0x201C NPCMENUOPEN_REQ {mobid}       → server opens the NPC menu, waits
C→ 0x201D NPCMENUOPEN_ACK {ack=1}       → pick option 1 (shop)   ← REQUIRED
S← 0x3C03/06/09/0B SHOPOPEN*_CMD        → the sell list = [itemnum u16][npc u16][MENUITEM×n]
C→ 0x3003 ITEM_BUY_REQ {itemid u16, lot u32}   → buy by ITEM ID (server validates vs shop)
C→ 0x3006 ITEM_SELL_REQ {slot u8, lot u32}     → sell a bag slot
C→ 0x200B ENDOFTRADE_CMD                → close
```
- `BotManager.OpenShopAsync` (click → poll `ZoneView.NpcMenuOpen` → `NPCMENUOPEN_ACK`),
  `BuyAsync`, `SellAsync`. Endpoints `/shop-open`, `/shop`, `/buy`, `/sell`. Shop-open +
  list arrival **live-verified** at Blacksmith Hans (Uruga): 121 items.
- **CAVEAT — table shops (0x3C09/0x3C0B):** the `MENUITEM` list is a **tab/index reference**
  structure, NOT flat item ids. The current `/shop` decode (leading-u16-per-stride) only
  reads real ids for non-table shops; for table shops it's garbage. Buying works regardless
  (you send a real item id from `ItemInfo`). Resolving the real sell-list for table shops
  needs the shop-table cross-ref (NPCItemList / client shop tables) — deferred.

### Enchant gear (decoded, not yet wired — GearEnchantment.pcapng)
**`NC_ITEM_UPGRADE_REQ` (0x3017)**, 9 B: `equip u8` (gear's equip slot), `raw u8`,
`raw_left u8`, `raw_right u8`, `gift_money u32`, `raw_middle u8` — the `raw*` fields are
the **inventory slots of the Elrue stones** (`0xFF` = empty; capture used slots 7/6/10 on
equip slot 2). Elrue = the upgrade stone, InxName `El1..El5` (tier by level: El4=lvl80);
`Bless*`/`Lucky*` variants. `&makeitem El4 -l<n>` to cheat them.

### Craft Elrue / production (decoded, not yet wired — Production.pcapng)
Production is NPC + skill driven: target+click the production NPC → menu-ack →
**`NC_SKILL_PRODUCTFIELD_REQ` (0x4822)** {2 B, selects the recipe/field} →
**`NC_ACT_PRODUCE_CAST_REQ` (0x2035)** {2 B, the produce cast — repeated once per item}.
Materials bought via `ITEM_BUY_REQ`; consumed per craft.

### Enchant + soul-stone buy WIRED (2026-06-14; live-verify pending)
- **`/enchant`** = `NC_ITEM_UPGRADE_REQ` (0x3017) `{equip, raw, raw_left, raw_right, gift_money,
  raw_middle}` — `raw` = primary stone (Elrue/Lixir/Xir by + range), left/middle/right =
  safety/bonus (red=prevent-destroy, blue=prevent-(-1), gold=better-chance; 0xFF=none).
  `BotManager.EnchantAsync`, Lua `bot.enchant(equip, raw[, l, m, r, money])`.
- **`/soulstone-hp-buy` / `/soulstone-sp-buy`** = `NC_SOULSTONE_HP/SP_BUY_REQ` (0x5001/0x5002)
  `{number}` — buy charges into the reserve `bot.soulstoneHp/Sp()` (USE 0x5007/0x5009) draws
  from. Lua `bot.buyHpStone(n)/buySpStone(n)`. (FiestaLib PDB structs; the GearEnchantment
  capture narrates the healer stone-buy but its C→S near the healer was NPCClick/menu-driven
  with no visible 0x50xx/0x3003 — so 0x5001/0x5002 needs a live confirm; if it no-ops, the
  healer "stones" are regular items bought via `/buy`.)

### Soul-stone reserve tracking + money (for the restock SM)
- **Stone BUY needs to be near the healer** (operator-confirmed); a buy from elsewhere sends
  but no-ops. **Success = the BUY_ACK** (HP `0x5003` / SP `0x5004`, `{totalnumber u16}`) —
  `ZoneView` now reads it → `HpStones`/`SpStones` (current reserve) + logs; surfaced on the
  snapshot. A missing ack after a buy = it didn't take.
- **Max reserve** = `MaxHPStone`/`MaxSPStone` in the [1802] param block (param offsets
  160/164 → body 162/166, same place as MaxHp/MaxSp). TODO: parse in `ZoneEntry` + seed
  `ZoneView.SeedMaxStones` (ready — offsets known).
- **Initial CURRENT count**: operator says it's in the zone-login char-info — TODO find that
  packet (so "stones low" works before the first buy); also check USESUC (0x5008/0x500A) for a
  remaining-count to decrement on use, and react to USEFAIL (0x5006) = reserve empty.
- **Character money**: TODO — no clean money-update S→C found yet (DB `nMoney` exists but read
  from the wire per the bot-is-source-of-truth rule). Find the login money field + update CMD;
  needed for "enough money to restock" warnings. **Stone price**: operator says it's in its own
  SHN — TODO locate (not found by name in `ressystem`).

### TARGET state machine (operator vision): grind → restock → return
`grind → (HP/SP stones low OR death) → travel to nearest town's healer → buy stones → travel
back → grind`. Composes the primitives: grind_sm + travelTo(nearest town) + walk-to-healer
(resolve the healer NPC by name from MobInfo) + buyHp/SpStone + return. Prereqs still needed:
detect "stones low" (track reserve charges, or react to soul-stone **USEFAIL 0x5006**) and
"nearest town/healer" routing. Build incrementally on the Lua SM engine.

### Craft Elrue (decoded, NOT wired — Production.pcapng)
Production = NPC + skill: target+click production NPC → menu-ack → `NC_SKILL_PRODUCTFIELD_REQ`
(0x4822, {2 B select recipe/field}) → `NC_ACT_PRODUCE_CAST_REQ` (0x2035, {2 B}, repeated once
per item). Materials bought via `ITEM_BUY_REQ`. Also: **Karis** come from **dismantling +6
and-above items at the healer** (dismantle packet TODO — "Error when item can't be dismantled"
seen). Wire `/craft` (productfield + produce-cast) when needed.

### Deferred
- Layer 3 polish: first-class multi-tree composition + per-class tree presets.
- Self money/inventory exposure on the bot (for autonomous gear/restock decisions).
- ActAsync: return NotInZone (not 500) when the zone connection is gone (e.g. VM slept).
- WS/NDJSON `/events` **structured** stream (typed events, not just log lines) — reuses the
  same event hub the log stream and scripts already consume.
- Script persistence (disk/Git), hot-reload, per-script resource/time limits.
- Pin unknown cast-fail codes (0x0FC4/0x0FC6) so scripts can react to facing/cooldown.
