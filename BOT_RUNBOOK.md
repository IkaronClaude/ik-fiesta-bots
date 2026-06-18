# BOT_RUNBOOK — operating + debugging the leveling bot

Operational cheat-sheet so a session doesn't re-derive how to run, observe, and debug the
bot. **Protocol/data facts live in `PROJECT_PLAN.md` and the agent memory; this is the
"how do I drive and inspect it" file.** Read this first when resuming bot work.

Current standing goal: level a fresh **Fighter "Bot1208"** (account `fighter1`/`fighter1`)
from 1→20 on the LIVE server, driven by `scripts/level_quest.lua`. Never log out mid-quest
(logout-at-progress glitches the quest via the FreeTDS varbinary bug — see memory
`fiesta-quest-persist-bug`). Zone handovers are safe; full relog re-grinds from 0.

## Run / rebuild loop (the host locks `Fiesta.Bot.dll` — must stop+kill before building)

```bash
# 1. stop the bot (clean logout) — skip if host already dead
curl -s -X POST http://127.0.0.1:5097/api/bots/Bot1208/stop

# 2. kill the host (PowerShell — it holds the dll lock)
#    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
#      Where CommandLine -like '*Fiesta.Bot.Host*' | Stop-Process -Force

# 3. build
dotnet build C:/Projects/ik-fiesta-bots/src/Fiesta.Bot.Host/Fiesta.Bot.Host.csproj -c Release

# 4. relaunch host (bash, background) — these env vars are REQUIRED
XOR_TABLE_PATH=C:/Projects/ik-fiesta-bots/xor-table.hex \
  ASPNETCORE_URLS=http://127.0.0.1:5097 \
  BLOCKINFO_DIR=Z:/ServerSource/9Data/Shine/BlockInfo \
  DOTNET_ENVIRONMENT=Development \
  nohup dotnet run --project src/Fiesta.Bot.Host -c Release --no-build >/tmp/host.log 2>&1 &
# poll http://127.0.0.1:5097/health until {"status":"ok"}

# 5. respawn the bot (returns to its logout spot)
curl -s -X POST http://127.0.0.1:5097/api/bots \
  -H 'Content-Type: application/json' \
  -d '{"Host":"62.171.171.24","LoginPort":9010,"Username":"fighter1","Password":"fighter1","Id":"Bot1208"}'
```

- Lua/C# split: **`.lua` is interpreted at runtime — re-applying the script needs NO rebuild.**
  Only C# changes (`src/`) require the full rebuild loop above.
- `XOR_TABLE_PATH` missing → every bot endpoint 503s. `BLOCKINFO_DIR` missing → `/walkto` 503s.
- VM wake-from-sleep kills all sockets; the tick loop then spins on a disposed connection
  (`ObjectDisposedException: SemaphoreSlim`). Recovery = stop → respawn → re-apply Lua.

## Apply / poll the Lua driver

PowerShell mangles multiline Lua — **always POST the script via python urllib**:

```bash
cd C:/Projects/ik-fiesta-bots && python -c "
import json,urllib.request
src=open('scripts/level_quest.lua',encoding='utf-8').read()
body=json.dumps({'source':src,'nameAs':'level_quest','tickMs':400}).encode()
req=urllib.request.Request('http://127.0.0.1:5097/api/bots/Bot1208/script',data=body,
    headers={'Content-Type':'application/json'},method='POST')
print(urllib.request.urlopen(req,timeout=15).read().decode())
"
```

Poll status (recentLog carries the `[lq]` script lines + `[ZoneView]` events):

```bash
curl -s http://127.0.0.1:5097/api/bots/Bot1208 | python -c "
import sys,json; s=json.load(sys.stdin)
print(s['level'],s['map'],s['position'],'hp',s['hp'],'/',s['maxHp'],'inCombat',s['inCombat'],'dead',s['dead'])
[print(l) for l in s['recentLog'][-12:] if 'appeared' not in l]
"
```

- `scripts/level_quest.lua` is **gitignored** (and purged from history — never commit it).
  It is the bulk quest leveler: accept-in-town → grind objectives by map → bulk hand-in,
  with glitch-detect/abandon, Schmitt-trigger combat, xpGrind fallback. Verbose `[lq]` logging.
- To wait on a condition, use a `for i in $(seq …); do …; sleep N; done` loop — the Bash tool
  blocks a bare `sleep N; cmd`.

## Packet logging — the "stop guessing, read the wire" tool

```bash
# enable (default true). Survives zone handoffs. Returns the file path.
curl -s -X POST http://127.0.0.1:5097/api/bots/Bot1208/packetlog \
  -H 'Content-Type: application/json' -d '{"enabled":true}'
# disable
curl -s -X POST http://127.0.0.1:5097/api/bots/Bot1208/packetlog -d '{"enabled":false}'
```

- Writes to `<host-cwd>/packets-<id>.log` → currently
  `C:/Projects/ik-fiesta-bots/src/Fiesta.Bot.Host/packets-Bot1208.log`. `tail -f` it.
- Captures **both directions interleaved**, XOR-decoded to plaintext (C→S logged *before* the
  send cipher), each frame: `[ts] C->S|S->C 0xOPCODE d=<dept> c=<cmd> len=<n> <StructName>`
  then a hex/ASCII dump. Opcode = `(dept<<10)|cmd`. Name resolved via `PacketRegistry`.
- Grep by dept/opcode, e.g. quest = d=17: `grep "d=17 " packets-Bot1208.log`.
- Implementation: tap on `FiestaClientConnection.PacketTap` → `Net/PacketLog.cs`, toggled by
  `BotManager.SetPacketLog`, endpoint in `BotEndpoints.cs`.

## Inspecting game data (read the files — do NOT guess mechanics)

- **QuestData.shn** (bespoke binary, NOT standard SHN, EUC-KR) — the live quest defs. Parse
  raw with a dotnet-script (user preference) per the offsets in memory `questdata-shn-format`:
  ```bash
  # template: read q<ID> scripts/objectives/rewards
  dotnet script /tmp/q.csx   # see git history / memory for the offset map
  ```
  Key offsets: QuestID@4, **MinLevel@27 / MaxLevel@28** (corrected — was @17/@18, verified by zone:
  Forest-of-Mist 10–21, Burning Rock 79–91), StartNPC@30, **objectives@102 STRIDE 8** {mob u16, type
  @+2 (1=kill), count @+3, item @+4} (≤5 slots — a quest can have MULTIPLE objectives),
  prereq@58 (gated by count@56), rewards@516 (12×12, RawIndex matters for select), scriptLens@660
  (Start,Finish,Action u16), scripts@680 in order **Start, Action, Finish**.
  ⚠️ **These offsets are hand-RE'd and several may be WRONG** (MinLevel sat at @17 for ages on a
  coincidental match). Verify any field across many quests/zones before trusting it — see the caveat
  at the top of memory `questdata-shn-format`.
- Quest scripts map 1:1 to the wire: `SAY <dlgId> NPC|ME`, `ACCEPT`, `DONE`, `LINK`,
  `IF RESULT==1 GOTO`, `GET_PLAYER_EMPTY_INVENTORY`. Start=accept dialogue, **Action=in-progress
  dialogue** (served while the objective is unmet — if turn-in "loops" on the same SAY, the
  server thinks the quest is NOT complete), Finish=turn-in.
- **QuestDialog.shn** (standard SHN, English): `ID → Dialog`. Dump + grep:
  ```bash
  cd C:/Projects/ik-fiesta-collab && dotnet run -c Release --project src/Fiesta.Collab.Cli -- \
    shn "Z:/ClientProd2/ressystem/QuestDialog.shn" --head 25300 | grep -E "^\s*<ID>\b"
  ```
- **ServerSource tables (SQL):** `cd C:/Projects/ik-fiesta-collab && dotnet run -c Release
  --project src/Fiesta.Collab.Cli -- query --project C:/Projects/serversource-data "<SQL>"`
  (table = `<file>_<#Table>`, e.g. `NPC.txt`→`NPC_ShineNPC`). Ground-truth game data, local only.
- **MobCoordinate.shn** (client) = mob→map markers (the quest-log marker source). Note: a mob
  may NOT be at its marker (roamer/intermittent) — grind nearby field mobs to catch it.
- **PDB ground truth** (opcodes + struct layouts):
  `lib/FiestaLib-Reloaded/docs/extracted/merged/{all-enums.json,all-structs.json}` and the
  typed structs under `…/Structs/*.cs`. (Some union structs e.g. `STRUCT_QSC` are only
  partially decoded — read the raw bytes from the packet log for those.)

## Packet captures (pcapng) on Z:

Decode (ALWAYS pass the XOR table or C→S = garbage): `cd C:/Projects/fiesta-proxy/tools &&
XOR_TABLE_PATH=C:/Projects/ik-fiesta-bots/xor-table.hex python pcap_decode.py Z:/<cap>.pcapng
--port 9016 [--opcode 0xXXXX]`. Streams: login 9010, WM 9013, zone00 9016. Most are chat-annotated
(`--opcode 0x2001`, read the `|...|` ASCII = the legend for the action packets). **Port 6443 = a
freelens/k8s probe, NOT the game — ignore it.**

- `Z:/Full.pcapng` (+ `Z:/Full.pcapng.txt` labeled index) — zone-enter, scroll, cast, walk,
  buy/sell, quests, clean-logout.
- `Z:/SellAndInventoryManagement.pcapng` (chat-annotated) — **the SELL ground truth.** Open shop:
  NPCCLICK 0x200A → NPCMENUOPEN_REQ 0x201C → NPCMENUOPEN_ACK 0x201D **{ack=1}** → server sends the
  shop-open (here `0x3C05 SHOPOPENSOULSTONE` — a soul-stone merchant; item merchants send
  0x3C0B/0x3C06). **Soul-stone shops DO accept item sells.** SELL = `NC_ITEM_SELL_REQ 0x3006
  {slot u8, lot u32}` → `NC_ITEM_SELL_ACK 0x3005` = 2-byte code **0x0381 = success**, 0x0383 =
  rejected (= shop not actually open). **`lot` is a COUNT: lot=1 sells 1 (rightclick); lot=<full
  stack> sells the whole stack in ONE req (ctrl-rightclick) — both ack 0x0381.** ONE open → MANY
  sells (no re-click). 2nd inventory page = **flat slot byte 24+** (`slot=0x18`); items are moved
  between pages with `NC_ITEM_RELOC_REQ 0x300B` and split with shift-click. (slot = inven & 0xFF
  across pages, so the bot's box-9 slot map already addresses it.)
- `Z:/Buff.pcapng` — self-buff cast · `Z:/ClientSourceZone.pcapng` — zone load incl. [1803] ·
  `Z:/AggroAndHerbs.pcapng`, `Z:/CombatExtensive.pcapng`, `Z:/Death.pcapng`, `Z:/Empower.pcapng`,
  `Z:/ClientSource*.pcapng` — combat/aggro, death/revive, skill-empower, client-source login/zone.

## Key source files

- `scripts/level_quest.lua` — the leveler (gitignored).
- `src/Fiesta.Bot/Scripting/BotApi.cs` — the `bot.*` Lua API (eligibleQuests, mobLocation,
  bestRewardIndex, questProgress, autoAttack, walkTo, travelTo, soulstoneHp, buyHpStone, …).
- `src/Fiesta.Bot/Session/ZoneView.cs` — inbound packet handlers + tracked state (HP via
  `0x240E HPCHANGE`, kills via REALLYKILL attacker==self, quest progress via `0x440D`,
  Dead via DEADMENU `0x104D` / revive `0x104F`/`0x1050`).
- `src/Fiesta.Bot/ClientData/QuestData.cs` — QuestData.shn parser (objectives, rewards, scripts).
- `src/Fiesta.Bot/Manager/BotManager.cs` — actions (REQ) + login/zone/handoff loop.
- `src/Fiesta.Bot/Net/{FiestaClientConnection,PacketLog}.cs` — transport + packet tap.

## Live server / accounts

- `62.171.171.24` — login 9010, WM 9013, zone00 9016. Sandbox reaches it + the internet.
- Test acct `testuser`/`test123` (nUserNo 100). Leveling acct `fighter1`/`fighter1` (Bot1208).
- In-game pw = raw MD5 (no salt). SQL Server access pattern: see workspace `C:/Projects/CLAUDE.md`.
