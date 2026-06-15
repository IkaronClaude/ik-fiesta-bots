# Grind & Progression — knowledge doc

Findings from wiring **item pickup/drops**, **healing**, **guard filtering**, **soul-stones**
and **skill learning** for the bot, toward the autonomous "**quest/grind to level 20**" goal.
Live-verified against the live server (`62.171.171.24`) with test char **`testuser`/`test123`
slot 2 = `BotPriest`** (currently **level 77**, in **Uruga**). Read alongside `PROJECT_PLAN.md`.

> Convention reminder: the **bot/packets are the source of truth**, not the DB. Client data
> (`ressystem/*.shn`, BYO at `Z:/ClientProd2/ressystem`) is legitimate runtime data the client
> reads. Decode offsets are PDB-extracted (`lib/FiestaLib-Reloaded/docs/extracted/`).

---

## 1. Item pickup & ground drops  (DONE, wired)
Loot sequence for a mob kill, reconstructed from `KillAndPickupItems.pcapng`:

```
mob dies → NC_BRIEFINFO_DROPEDITEM_CMD (0x1C0A) broadcast (item on ground)
walk onto it → NC_ITEM_PICK_REQ (Item 0x300?) {itemhandle}
server → NC_ITEM_CELLCHANGE (bag slot gains item)  ← the authoritative success signal
       + NC_MAP_LOGOUT_CMD (0x1805) {handle}        ← ground item despawns (anyone's pick)
       + NC_ITEM_PICK_ACK (0x300A) {itemid,lot,error,itemhandle}
```

- **DROPEDITEM body** (15 B): `handle u16 @0 | itemid u16 @2 | location SHINE_XY_TYPE(x u32@4,y u32@8) | dropmobhandle u16 @12 | attr u8 @14`.
- **Success is judged by CELLCHANGE / the drop leaving view, NOT `PICK_ACK.error`** — a captured
  *successful* pick had `error=0x341` (not 0). The failure code (inventory full, etc.) is still
  unknown — needs a failing capture. `LootAsync` confirms by the drop handle leaving `Drops`.
- `MAP_LOGOUT_CMD {handle}` is the universal "entity left view" (also retires NPCs/players);
  `SOMEONEPICK` carries no handle so it can't retire a specific drop.
- Wired: `ZoneView.Drops/NearestDrop/LastPickResult` + events; `BotManager.PickupAsync/LootAsync`;
  lua `pickup/loot/drops/nearestDrop`; `GET /drops`, `POST /pickup`, `POST /loot`; snapshot `drops`.
- **Pickup blockers (operator):** inventory full; being in a **mini-house**; other states. Inventory
  supports up to **6 pages** (2 free, 4 premium-locked) — the **available page count** is expected in
  the `[1802]` login param block but is not yet decoded (no obvious page field in `CHAR_PARAMETER_DATA`;
  TODO — may be in the item-list packet).

## 2. Zone-login data burst is drained before ZoneView attaches  (IMPORTANT)
The post-`[1801]` login burst (sent **before** `MAP_LOGINCOMPLETE 0x1803`) is read by
`ZoneEntry`'s drain loop — **the in-zone session loop / `ZoneView` never sees those frames.**
So anything sent only at login must be **captured in `ZoneEntry` and seeded into `ZoneView`**:
- **Skill list** `NC_CHAR_CLIENT_SKILL_CMD` (**0x103D**, Char cmd 61) — fixed now (captured + `SeedSkills`).
- **Item/bag list** `NC_CHAR_CLIENT_ITEM_CMD` — **still drained**, so `Inventory` is **empty at login**
  and only fills from live `CELLCHANGE`/`EQUIPCHANGE` (e.g. after a buy). TODO: capture+seed like skills.

## 3. Skills (learned list + the heal)  (read DONE; learning higher ranks TODO)
- Skill list layout (`0x103D`): `restempow u8 | PartMark u8 | nMaxNum u16 | chrregnum u32 |
  number u16 | SKILLREADBLOCK[ number ]`; each block 12 B, **skillid = leading u16**.
- Resolve a skill id → name via client **`ActiveSkill`** (`ClientData.SkillName`). `GET /skills`.
- **`BotPriest` learned (31):** Bash[01]=1500, Bash[15]=1514, **Heal[01]=1540, Heal[10]=1549**,
  Protect 1560, Endure 1580/1581, Trip 1600, Bleed 1649, Immune 1680, Restore 1700, Resist 1760,
  Invincible 1800, + event/utility (Mining 29001, Ride Mover 29206, …).
- **Heal skill `1549` (Heal[10]) WORKS** — manually healed 665→2220 (~1100/cast, SP cost 135).
- **Heal ranks** (skill id → scroll item id → `DemandLv` to learn; priest is lv77):
  | rank | skill id | scroll id | DemandLv |
  |---|---|---|---|
  | Heal[10] | 1549 | 5449 | 57  ← have |
  | Heal[11] | 1550 | 5450 | 63 |
  | Heal[12] | 1551 | 5451 | **69** ← "lvl-70ish" |
  | Heal[13] | 1552 | 5452 | 75 ← highest the lv77 priest qualifies for |
  | Heal[14] | 1553 | 5453 | 81 (too high) |
- **Learning a skill = buy the scroll item + `useItem` it** (`ItemUseSkill=UseSkill`, Type=1, Class=11).
  Scrolls have a `UseClass` gate (Heal[11-13]=UseClass 10) — the char's class must cover it.
- **Skill masters (operator):** **Cyburn (id 153) sells lv60+**, **Elde sells lv20–60**. So Heal[11-13]
  come from Cyburn.
- **Skill-shop opcode FOUND:** the skill master sends **`NC_MENU_SHOPOPENTABLE_SKILL_CMD` (Menu cmd
  10 = `0x3C0A`)**, ~1588 B — a "table" shop with the SAME `[itemnum u16][npc u16][MENUITEM × n]`
  shape as merchant shops. It was missing from `ZoneView.OpShopOpen` (which had only weapon/item
  cmds 3/6/9/11). **Fixed:** added the SKILL variants `0x3C04` (SHOPOPENSKILL) and `0x3C0A`
  (SHOPOPENTABLE_SKILL). (Menu shop cmds: 3 weapon, 4 skill, 5 soulstone, 6 item, 9/10/11 = table
  forms.) The stride-divide parse yields noisy ids for the table-skill element, but **buying is
  validated server-side by id**, so a correct scroll id buys fine regardless of parse noise.
- **Buying a scroll WORKS:** `BuyAsync(5451)` (Heal[12]) → arrived in bag slot 8 via CELLCHANGE
  (the priest has money — same as the stone buys).
- **OPEN PROBLEM — consuming the scroll to LEARN does not work via `useItem`:** `UseItemAsync`
  (NC_ITEM_USE_REQ, invenType 9) on the scroll did NOT consume it and did NOT learn Heal[12]
  (still only Heal[01]/[10] after relog). So a skill scroll is learned by a **different mechanism**
  than a normal item-use — TODO: **get a capture of the operator learning a skill from a scroll**
  (the C→S packet), or check for a skill-learn REQ (cf. `NC_SKILL_*`). Also note ZoneView only
  tracks skills from the login list — a **live skill-add** packet isn't handled yet (so a freshly
  learned skill won't show until relog).

## 4. Guard / enemy filtering  (DONE — via client MobInfo, NOT the packet)
- The mob **briefinfo packet carries NO ally/enemy field**: `mode`, `flagstate`, and `nKQTeamType`
  (record offset 147) are **all uniformly `2`** in a normal field for guards, mobs, herbs and NPCs.
  (`nKQTeamType` is a King's-Quest battlefield team, not a guard flag.)
- The real discriminator is **client `MobInfo`**: **`IsPlayerSide`** and **`Type`**:
  - Town Guard (9908) `IsPlayerSide=2` (ally) ; Pinky(68)/Orc(84) `IsPlayerSide=0` (enemy).
  - `Type==9` = **gatherable resource node** (Herb/Wood/Mushroom) — not a fight target.
- Huntable enemy = `!IsNPC && IsPlayerSide==0 && Type!=9` → `ClientData.IsHuntableEnemy(mobId)`.
  Wired into `BotManager.NearestMob` (auto-attack never targets guards/NPCs/resources) and into
  `ZoneView.IsHuntableMob` (suppresses the angle-aggro heuristic for wandering guards). `GET /npcs`
  now returns `playerSide/type/huntable`.

## 5. Soul-stones (HP/SP reserve)  (buy DONE; current-count read TODO)
- **`MaxHPStone`/`MaxSPStone`** are in the `[1802]` `CHAR_PARAMETER_DATA` (param off 160/164 → body
  162/166). BotPriest max = **232 HP / 287 SP**.
- **Current reserve is NOT in the login packet** (the `PwrStone`/`GrdStone` 16-B sub-structs at body
  170/186 read **all zeros**). `ZoneView.HpStones/SpStones` are only populated by a **BUY_ACK**
  (`0x5003`/`0x5004` `totalnumber`). So after a relog the snapshot count resets to null even though
  the **server-side reserve persists**. TODO: find where the current count is sent (its own cmd?).
- **Buying requires opening the healer's menu first** (it didn't ack otherwise): click the healer
  (`OpenShopAsync` → NPCMENUOPEN_ACK) **then** `BuyHpStoneAsync`/`BuySpStoneAsync`. Verified at
  **Healer Poring (id 149)** in Uruga: bought to 209 HP / 106 SP, BUY_ACK reported the new total.
- USE draws from the reserve (`soulstoneHp/Sp`, 0x5007/0x5009). The stock `auto_grind.lua` relies on
  this — but a char that is **out of stones** can't, so it must heal with the **heal skill** instead.

## 6. Combat survivability findings  (priest)
- **An overlevelled char isn't auto-aggroed** (matches CLAUDE.md) — when idle, mobs don't attack and
  HP stays flat. You only get hit by what you engage.
- **The priest has very low melee/Bash DPS** — in ~25 s of healed combat vs the Uruga Pinky/Orc pack
  it **killed nothing** while the pack wore it down. A priest needs to fight **one mob at a time**,
  not stand in a pack, and probably can't fast-grind these mobs at all (wrong class for melee grind).
- **Script bug found & fixed:** re-issuing `autoAttack(nearestMob)` every tick as the pack mills
  around **resets the swing windup → ~0 damage**. Fix: **lock the target until it leaves view**
  (`scripts/heal_grind.lua`). Looting walks (blocks healing) so only loot when no enemy is engaged.
- For grinding, heal **with the heal skill** when out of stones; buy stones at a healer when you can.

## 7. Bugs / gaps still open
- **`RespawnAsync` (NC_CHAR_REVIVE_REQ 0x104E) does not revive** the dead bot (HP stays 0, no move).
  Workaround that DOES work: a **clean stop + relog** revives (server returns the char alive). The
  revive packet/flow needs fixing (payload? must ack DEADMENU differently?).
- **Inventory empty at login** (§2 — item list drained by `ZoneEntry`).
- **Inventory page count** not decoded (§1).
- **Skill-master shop** multi-tab/page not parsed (§3).
- **Current soul-stone count** not read from login (§5).

## Behaviour graph (design — operator-specified, building now)
A first-class **graph state machine** replacing the single-script SM:
- **State** = a node with its own script: `on_enter()` / `tick()` / `on_exit()` (+ event hooks).
  Each state is its own file (e.g. `mob_grind.lua`, `stay_alive.lua`).
- **Transition** = a first-class edge `{name, from, to, check}`; `check()` returns true to fire.
- **Tick:** fire any pending *requested* transition; else `current.tick()`, then run every
  outgoing transition's `check()` in order — first truthy fires it (`from.on_exit()` →
  `to.on_enter()` → switch current).
- **Transition triggers:** a `check()` returns true (autonomous) OR an explicit request —
  `POST /{id}/state {name}` (operator flips it) or `bot.requestState("...")` from a state script.
- **Persistence:** the whole graph (states + transitions + scripts + initial) + the bot's
  current state saved as JSON on disk, reloaded on host start (also fixes scripts vanishing
  on restart).
- **Composition:** a shared helper (e.g. `survive()`) is importable by every state, so
  `mob_grind` runs the survival cycle first, then layers combat/aggro on top.
- **`stay_alive`**: heal-first → soul-stone when heal on cooldown → and **flee while healing**
  when incoming DPS outpaces heals+stones. Runs safely 24/7; flip to it anytime for "90% control".
- Solves the concurrency problem: survival never stops while the operator does manual things.

## Fighter-to-20 run (the milestone)
- **Deployment rule (operator):** the goal is **1 account per character** so multiple bots can
  be logged in at once (multi-login). Multiple chars on ONE account can NEVER be online
  simultaneously — so provision a fresh account per bot.
- **Provisioned:** account `fighter1` (userNo 107, GM level 1 = no GM) → char `Bot1208`
  (slot 0, Fighter, level 1, starter zone Roumen/9016). Fresh Fighter has **no combat skills**
  (only 29xxx event skills) — must buy Bash etc. from **Ruby**; gear from **Smith**; mount from
  **Pey** (feed it). Credentials in local memory (never committed).
- **Quests (required to 20):** wire accept → progress → turn-in. Low-level quests have a
  **remote-accept / remote-handin flag**; later ones need the start/end NPC. Some need a
  **special item drop** picked up → keep ≥1 free bag slot. **Bonus:** the client can show where
  mob clusters are per quest — find that packet to locate mobs WITHOUT server SHN tables.
  Plan: do the first 1-2 quests by hand (API), then wire into a Lua quest graph/state.

## Quest protocol (decoded from QuestsLowLevel.pcapng — non-remote flow)
Accept / progress / turn-in is an **NPC dialogue exchange**, not a one-shot accept:
- Click the quest NPC: `NC_ACT_NPCCLICK_CMD` (0x200A).
- Server drives the dialogue: `NC_QUEST_SCRIPT_CMD_REQ` (0x4401) `{ nQuestID u16, Command
  STRUCT_QSC(101b) }` — each step; the QSC command code is `STRUCT_QSC[0]` (seen 0x02 = prompt/
  accept, 0x06 = complete/reward).
- Client answers each step: `NC_QUEST_SCRIPT_CMD_ACK` (0x4402) `{ nQuestID u16, nQSC u8 (echo the
  command code), nResult u32 (the choice; 1 = proceed/accept) }`.
- Reward selection: `NC_QUEST_REWARD_SELECT_ITEM_INDEX_CMD` (0x4411) `{ nQuestID u16,
  nSelectedItemIndex u32 }` — pick the class-appropriate reward (e.g. quest 8 → index 5 = fighter
  boots).
- Quest state seeds at login: `QUEST_DOING (0x103A) / DONE (0x103B) / READ (0x10CE) / REPEAT
  (0x10D7)` — decode these for "what quests am I on / done".
- **"Location" button = client-side** (no packet) — the client maps quest→target-mob and shows
  the cluster from its own map/mob data. So locate quest mobs from CLIENT data, not server SHN.
- This capture is the **non-remote** flow (accept at NPC, hand in at NPC). Remote accept/handin
  is a separate (later) capture.
- **To wire:** drive NPCClick → answer each 0x4401 with a 0x4402 (echo nQSC, nResult=1) →
  0x4411 for the reward. Replicate the first 1-2 quests by hand on Bot1208, then a Lua quest graph.
- **Quest-giver discovery (operator):** an NPC with a quest shows an **orange `!` mark** — likely a
  per-NPC flag in the briefinfo (`NearbyNpc`). Some starter NPCs' quests don't even appear in the
  quest list, so this flag is the reliable way to find quest givers. TODO: pin the exact byte
  (flagstate / mode / a flag-blob bit) by diffing a known quest NPC vs a non-quest NPC live, then
  expose it on `NearbyNpc` (e.g. `HasQuest`) so the bot can scan for quest givers.
- Quest plumbing wired (compiles): `ZoneView.PendingQuest` + `QuestStep`; `BotManager.ClickNpcAsync
  / AnswerQuestAsync / ProceedQuestAsync / SelectQuestRewardAsync`; endpoints `/click-npc`,
  `/quest`, `/quest/answer`, `/quest/reward`; Lua `clickNpc/answerQuest/selectReward/pendingQuest`.

## Next steps (TODO — tracked)
- **CAPTURE empower points + skill empower points** ⟵ *operator reminder.* Get a chat-annotated
  capture of gaining/spending empower points and skill-empower points (the empower system), so
  the bot can read + use them. Not yet decoded.
- **Revive bug** — `RespawnAsync` (NC_CHAR_REVIVE_REQ 0x104E) doesn't revive; only a relog does.
- **Close the shop window after buying** (operator note) — an open shop can block future NPC
  dialogs. Wire a SHOPCLOSE after buy/sell (currently only the map-change/teleport closes it).
- **Confirm grind end-to-end**: survival + SP management now work at Burning Rock (Fire Nix lv80);
  still need a confirmed KILL → DROPEDITEM → loot to prove the pickup path live (priest DPS vs
  lv80 is slow — may need a higher-DPS target or more attack power).
- **Live skill-add tracking** — ZoneView only learns skills from the login list, so a freshly
  learned scroll (e.g. Heal[12]) doesn't show in `/skills` until relog. Decode the live skill-add.
- **Commit** the party-invite tracking + readable cast-fail logging + grind script once verified.

## Empower: stat points + skill empower (decoded from Empower.pcapng)
- **Stat points** — `NC_CHAR_STAT_INCPOINT_REQ` (0x105C) `{ stat: u8 }`, one send per point.
  Stat index: **0=STR, 1=END, 2=DEX, 3=INT, 4=SPR** (mapped from the annotated STR+2/DEX+1/
  END+3/INT+5/SPR+4 run). Free + applied counts are in the login packet (TODO: decode them).
- **Skill empower** — `NC_SKILL_EMPOWALLOC_REQ` (0x4811) `{ skill u16, plus SKILL_EMPOWER(u16),
  minus SKILL_EMPOWER(u16) }`. `plus` adds, `minus` removes (re-spec). **`SKILL_EMPOWER` = packed
  u16 nibbles: damage(bits 0-3), sp(4-7), keeptime(8-11), cooltime(12-15).** So empower a skill's
  damage by N → `plus = N`; sp by N → `plus = N<<4`; etc. (verified: 0x0001=dmg+1, 0x0020=sp+2,
  0x3000=cool+3, 0x0400=keep+4). **Each category caps at 5** (damage/sp/keeptime/cooltime ≤ 5).
- **Fighter stat builds (operator):** Glad (axe melee DD) ≈ 25 SPR / 33 DEX / rest STR; low-level
  order: STR→10, then SPR→25, then STR→30, then DEX→33, then rest STR. Tank = full END.
- **No-GM economy (operator):** buy skills from **Ruby**, weapons/armor from **Smith**, a mount from
  **Pey** (mounts need **feeding**). Play like a real player — no makeitem/sethp/linkto for the
  Fighter-to-20 run. **Quests are REQUIRED to reach 20** (the quest system still needs wiring).

## 8. Misc live facts
- **Zone port varies per map** — Uruga = **9022** (RouN zone 9016, WM 9013). Decode with
  `pcap_decode.py --port <N>`.
- **Uruga (`Urg`, map id 17)** NPCs/positions (handles are per-session; ids stable): Healer Poring
  149 @≈(5603,5399); Skill Master Cyburn 153 @≈(7909,8083); Item Merchant Vellon 147; Blacksmith
  Hans 146; Storage Keeper Curly 152. Pinky(68)/Orc(84) field ≈ (2600–3900, 3000–4700);
  Herb/Wood/Mushroom (Type 9) resource nodes scattered there too.
- `CHAR_PARAMETER_DATA` (in `[1802]`, body = +2 vs param off): MaxHp@146, MaxSp@150, MaxLp@154,
  MaxAp@158, **MaxHPStone@162, MaxSPStone@166**, PwrStone(16)@170, GrdStone(16)@186; `logincoord`
  (spawn x/y u32) = last 8 bytes.

## 9. Quest system — DECODED + driver wired (Fighter leveled 1→2 live)
- **QuestData.shn is a bespoke format** (NOT standard SHN, not encrypted, EUC-KR). Fully
  reverse-engineered + validated vs all 2304 live quests; see memory `questdata-shn-format.md`
  and ik-fiesta-collab's QuestDataProvider (decodes StartNPC/mobs/items/rewards/scripts).
  Fixed 680B record region then Start/Action/Finish scripts. The serversource `Quest_Header`
  tables are a DIFFERENT/older quest set — IDs do NOT match the wire; QuestData.shn IS the
  live source. **QuestDialog.shn** (standard SHN, English) maps dialog/title ids → text.
- **Wire protocol (proven live):** click NPC (`0x200A NPCCLICK {npchandle}`) → the quest
  script runs SERVER-side and pushes each page as `0x4401 NC_QUEST_SCRIPT_CMD_REQ`; client
  ACKs each with `0x4402 {questId u16, qsc u8, result u32}`, result=1 = proceed/accept. The
  `qsc` is the QSC command (Cmd@payload+2): **2 = SAY** (dialog id = u32 @payload+7), **6 =
  ACCEPT**, **0x0A = DONE/complete**. Reward grant on DONE: `0x240B [exp]` + `0x1033 [money]`
  (matched the decoded rewards exactly: q1 EXP7/Money52).
- **Bot wiring:** `ClientData.Quest(id)`/`QuestDialog(id)` (QuestData.cs parser); `ZoneView`
  `QuestStep` now carries `DialogId`; `BotManager.DriveQuestDialogueAsync(npcHandle)` clicks +
  ACKs every page to completion; Lua: `bot.quest(id)`, `bot.questDialog(id)`, `bot.doQuest(h)`,
  `bot.npcByMob(id)`, `bot.level()`; endpoints `/quest/do`, `/quest-info/{id}`. First-cut
  driver `scripts/quest_chain.lua`.
- **Proven loop (Bot1208, fighter1):** q1 "Baby Steps" (accept Remi 111 → turn in Julia 29),
  q2 "More Baby Steps" (instant accept+done at Julia, LINK→q3), q3 "The Chief of Roumen"
  (accept Julia → walk → turn in Town Chief 92). **Leveled 1→2.**
- **OPEN — autonomy gap:** quests are largely independent (PrevQuest@46 mostly 0, few LINKs),
  offered by various NPCs gated by level. Strict id-chaining breaks (q4 startNpc=27 isn't a
  town NPC in the chain). NEXT: decode the **orange-! available-quest flag** (NPC briefinfo) to
  know which NPC currently has a quest — the right "which quest next" signal. Then handle
  hunting-quest mob locations (client enemy-cluster packets or wander+combat-graph) + buy
  skills(Ruby)/gear(Smith)/mount(Pey).
