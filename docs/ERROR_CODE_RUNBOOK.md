# ERROR CODE RUNBOOK — turning a server error code into the exact player-facing message

When the server refuses something it sends a bare number (`0x0FCA`, `0x0709`, `0x020B`, `0x0717`…).
**Those numbers are not named in any PDB** — not `Fiesta.pdb`, not `Zone.pdb`. They are immediates in
the server's code, so grepping either PDB for the constant finds nothing. Repeatedly guessing at them
is how this codebase carried a wrong meaning for `0x0FCA` for months (below).

**The client knows what they mean, because it shows the player a sentence.** That mapping is what we
read.

---

## How it works

The client converts a code into a message with a jump-table switch:

```asm
83 f8 18              cmp  eax, 0x18            ; N cases  (count = N+1)
0f 87 15 01 00 00     ja   default
ff 24 85 d0 a9 4a 00  jmp  [eax*4 + 0x4AA9D0]   ; <-- the jump table VA
...
68 21 09 38 17        push 0x17380921           ; <-- each case pushes an eTextID
```

So: `error code` → `code - base` → jump-table entry → `push <eTextID>` → the string in
`TextData/TextData2/TextData3.shn`.

⚠️ `eTextID` values are **hashed** (`0xD9132D2D`, not 1,2,3…), so you cannot compute them or look up a
code arithmetically. The switch is the only link.

---

## The tool

```bash
# decode a table you already know (see docs/jumptables/)
python tools/decode_error_table.py --table 0x4AA9D0 --base 0x0FC0 --count 25

# work backwards from a message you have SEEN in game
python tools/decode_error_table.py --find "out of casting range"
```

`--find` locates the string's `eTextID`, then every `push <that id>` in `Fiesta.bin` — those are the case
bodies. From a case body, scan **backwards** for `ff 24 85 <table VA little-endian>` (the
`jmp [eax*4+TABLE]`) and the `83 f8 <N>` (`cmp eax, N`) just above it. That gives you `--table` and
`--count`; `--base` is the code of index 0, which you get by lining up one known code with its index.

Inputs: `Z:/ClientProd2/Fiesta.bin` and `tools/text-ids.json` (4091 strings). Regenerate the latter with:

```csharp
// dotnet script — reads TextData/TextData2/TextData3.shn -> tools/text-ids.json
foreach (var f in new[]{"TextData","TextData2","TextData3"}) {
  var t = ShnTable.Load($"Z:/ClientProd2/ressystem/{f}.shn");
  foreach (var r in t.Rows) map[GetLong(r,"eTextID").ToString()] = GetStr(r,"acString");
}
```

---

## Known jump tables

Stored decoded in `docs/jumptables/`, named `jumptable_<PACKET>_errors.json`.

| file | packet | table VA | base | count | relevant when |
|---|---|---|---|---|---|
| `jumptable_NC_BAT_SKILLBASH_CAST_FAIL_ACK_errors.json` | `0x2434` cast failed | **`0x4AA9D0`** | **`0x0FC0`** | 25 | every skill cast the server refuses |

### `0x2434` — cast failures (decoded 2026-08-11)

| code | meaning |
|---|---|
| `0x0FC0` | Cannot use the skill while in nonbattle mode. |
| `0x0FC1` | Cannot use the skill right after logging in. |
| `0x0FC2` | Cannot use the skill when the target logged in just now. |
| `0x0FC3` | Cannot use the skill in this field. |
| `0x0FC4` | Casting another skill now. |
| `0x0FC5` | The skill has been reserved. |
| `0x0FC6` | Incorrect Skill. |
| `0x0FC7` | Cannot use the skill due to Silence State. |
| **`0x0FC8`** | **Cannot use the skill yet.** (cooldown) |
| `0x0FC9` | Not enough SP. |
| **`0x0FCA`** | **The target is out of casting range.** ⚠️ CATCH-ALL: also sent for position desyncs and invalid/stale target handles (operator 2026-08-13). See COMBAT_BIBLE.md. |
| `0x0FCB` | Cannot find the target. |
| `0x0FCC` | Cannot use the skill on Fear State. |
| `0x0FCD` | Skill has not been finished normally. |
| `0x0FCE` | Skill usage is prohibited in this area. |
| `0x0FCF` | Your spouse is not online, skill cannot be used. |
| `0x0FD0` | Target is in a Guild War, skill cannot be used. |
| `0x0FD1`, `0x0FD2`, `0x0FD3`, `0x0FD5`, `0x0FD6` | Failed to Cast the Skill. |
| `0x0FD4` | The effect cannot be used because upper level effect are used already. |
| **`0x0FD7`** | **Target user cannot be healed at this time.** |
| `0x0FD8` | You cannot use revival to user who is in state of Blessing of Teva. |

⛔ **`0x0FCA` IS RANGE, NOT MOVEMENT.** The codebase labelled it *"precondition unmet; suspect MOVING /
no committed STOP"* for months and that guess was repeated into the 2026-08-11 combat work as the stated
reason for suppressing a mid-swing face-step. It means **the target is too far to cast at**. The remedy
is a range check against `ActiveSkill.Range` before casting — not movement discipline.

`0x0FD7` confirmed the other diagnosis exactly: the Cleric was aiming `Heal` at a monster.

---

## Codes still unmapped

These have been SEEN on the wire but their switch has not been located yet. Do not guess at them — run
`--find` on the message you see in game, or find the switch for that packet's fail path:

| code | packet | observed when |
|---|---|---|
| `0x020B` | `NC_ITEM_BUY_ACK` | buying with insufficient money (empirical) |
| `0x0709` | item USE | using another class's skill book (empirical) |
| `0x0717` | item USE | using a crafting recipe without the job (empirical) |
| `0x0346` | loot pick | bag full (empirical) |
| `0x024A` | storage | item cannot be stored — timed/bound (empirical) |

"Empirical" = we know the situation that triggers it, not the client's own wording. Upgrading one of
these to a decoded string is a ~10 minute job with the tool above.
