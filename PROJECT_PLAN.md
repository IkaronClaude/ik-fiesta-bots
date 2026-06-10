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

### TODO / roadmap (operator-requested)
- **Fix FiestaLib-Reloaded bugs upstream** (sibling repo, push allowed → bump the
  pinned submodule hash): the chat structs read `content` as
  `ReadBytes(itemLinkDataCount)` (should be `len`); `SHINE_ITEM_VAR_STRUCT.Read`
  does `ReadBytes(itemid)`. We work around these locally — fix at source instead.
- **Walk** (movement) — next goal. Client move packet is `ACT` cmd 25 (0x2019,
  16 bytes = from(x,y u32)+to(x,y u32)) seen in the zone captures.
- Then: **per-event log** → **/debug** (bot whispers all events to a player) →
  **track nearby dropped items** → **item pickup** → **pathfinding**.
- **2-bot chat-observe test** (one `/say`s, the other's ZoneView decodes
  `SOMEONECHAT`) needs a **second account** — only `testuser` creds are held.
- Cast packet (`SKILLBASH_OBJ_CAST_REQ` vs `SKILLENCHANT_REQ`) confirmed only once
  a priest has Endure learnt.
