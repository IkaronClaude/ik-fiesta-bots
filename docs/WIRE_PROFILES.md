# Wire profiles (2016 and 2026 packet sets)

The redesigned runtime (`next/`) speaks both packet sets from one code base. A bot's packet set is a per-bot
setting (`wire: "2016" | "2026"` on the spawn request, the persisted bot record and `PATCH /api/v3/bots/{id}`;
default from the host's `FIESTA_WIRE`, else 2016). Everything that differs lives behind `WireProfile`
(`next/src/Fiesta.Bot.Protocol/Wire.cs`); the decoders, the world state, the actions and the brain are shared.

## What differs

| area | 2016 (`Wire2016Profile`) | 2026 US (`Wire2026Profile`) |
|---|---|---|
| login | `Login2016`: CLIENT_VERSION_CHECK_REQ 0x0C65 (version key), US_LOGIN_REQ 0x0C5A {user[260], md5[36], build tag[20]}, XTRAP_REQ, WORLD_STATUS_REQ, LOGIN_ACK 0x0C0A, WORLDSELECT 0x0C0B / ACK 0x0C0C {status, ip[16], port, validate[64]} | `Login2026`: version key 0x0C2E (ack 0x0C2F / mismatch 0x0C30), 349-byte login 0x0C01, acks 0x0C42 + 0x0C49 (refusal 0x0C07), world list 0x0C06 {count, 4 head bytes, {no, name[16], status}}, challenge 0x0C40 answered with 0x0C36 + 0x0C41, OTP 0x0C37, world select 0x0C0A / ACK 0x0C0B |
| world manager | LOGINWORLD_REQ 0x0C0F {user[256], validate[64]}, avatar list 0x0C14 {wm u16, n u8, n x 130 B} | WM login 0x0C0E (same body), avatar list 0x0C0F {wm u16, n u8, slots u8, ip[16], port u16, n x 161 B} |
| character create | race bits from the class line (the 2016 create fails on a wrong race) | race bits kept at 1, class + gender bits set (what the 2026 client sends) |
| shared | GAMETIME_REQ 0x080D after the list, CHAR_LOGIN 0x1001 {slot}, TUTORIAL_POPUP_REQ 0x1110 answered 0x1111 {1}, CHAR_LOGIN_ACK 0x1003 {zone ip[16], port}, AVATAR_CREATE 0x1401 / 0x1404 / 0x1406 | same |
| MAP_LOGIN_REQ checksums | 49 tables (`Wire2016Profile.Files`) | 53 tables: the 49 without MapLinkPoint / MapWayPoint, plus DeprecatedFiles and the five quest tables (`Wire2026Profile.Files`) |
| after MAP_LOGIN_ACK | MAP_LOGINCOMPLETE | world manager 0x0878 and 0x581B, MAP_LOGINCOMPLETE, then three ENDOFTRADE and the reward / charged item list requests (page 0), the 2026 client's start-up order |
| wider zone packets | canonical layouts | `Wire2026Shapes.ToLegacy` turns the 2026 shape into the canonical one after checking the exact width (briefinfo records +1 byte US, CHAR_CLIENT_BASE 362 B, quest lists 37 B per row, shop tables 6 B per item, item records by item class, skill hit 21 B per target, target info 41 B, the SKILLBASH start frames +4 B, charged buffs 22 B); a frame of any other width is counted in `Mismatches` and passed on, never misread |
| per-swing packets | SWING_DAMAGE 16 B, damage u16 | 25 B, damage u32; read width-aware by `CombatDecoder` without a copy |
| moved opcodes | NORMALLOGOUT 0x0C18, AVATAR_LIST_REQ 0x0C1F | 0x0C15, 0x0C1A (`WireProfile.Opcode(OutMsg)`) |

The cipher, the framing, the zone opcodes used for actions (move, stop, target, cast, bash, item use, buy / sell,
NPC click, menus, quest script answers) and the heartbeat are identical on both.

## Client data

The MAP_LOGIN_REQ checksums are computed from the client data the bot is configured with (`CLIENT_DATA_DIR`), so a
bot must use the client data of the client its packet set belongs to (the 2016 client's ressystem for 2016
servers, the played 2026 client's ressystem for 2026 servers). Walkability comes from the same client's
`resmap/<kind>/<map>/<map>.shbd` (and `.shab` where present).

Open: the 2016 client's QuestData is a bespoke binary layout, not a column table; the 2026 tables
(QuestData script + QuestEndNpc / QuestEndItem / QuestReward) are read. Quest objectives on 2016 client data are
not loaded yet (quest progress from the wire still works).

## Verification

- 2026: live on the local 2026 stack (login, character creation, zone entry, movement, combat, quest hand-in;
  `docs/NEXT_STATUS.md`).
- 2016: the zone frames of a 2016 capture (`Z:/Full.pcapng`, decoded by `fiesta-proxy/tools/pcap_decode.py`) are
  decoded through the 2016 profile in `WireTests.Decodes_a_2016_capture` (map login, character base, entities, bag,
  skills). The login bodies match the capture's frame sizes (version check 64 B, US_LOGIN 316 B, XTRAP 30 B). A live
  2016 run is pending (see NEXT_STATUS).
