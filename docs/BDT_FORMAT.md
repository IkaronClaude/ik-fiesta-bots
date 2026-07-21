# `.bdt` file format — REVERSE ENGINEERED (2026-07-21)

`BlockInfo/<Map>.bdt` is a **sparse region quadtree of WALKABLE cells** at **50-unit
(one-block) resolution**. It exists only for terrain/hill maps (76 of 158 maps); flat
maps (e.g. RouN) have **no `.bdt`**. All 76 files are exactly divisible by 9.

## Record layout — flat array of 9-byte nodes, NO header
`filesize / 9 == node count` exactly (RouVal02 = 238950 B → 26550 nodes).
Each node is one quadtree node, depth-first order (root first):

| bytes | field | type   | meaning |
|-------|-------|--------|---------|
| 0–1   | x1    | u16 LE | corner A x (world units) |
| 2–3   | x2    | u16 LE | corner B x |
| 4–5   | y1    | u16 LE | corner A y |
| 6–7   | y2    | u16 LE | corner B y |
| 8     | flag  | u8     | depth marker (see below) |

**Layout is all-X-then-all-Y** (x1,x2,y1,y2), NOT interleaved (x1,y1,x2,y2). The node's
axis-aligned box = `[min(x1,x2), max(x1,x2)] × [min(y1,y2), max(y1,y2)]`. (x1,x2 stored as
the two diagonal corners, so x1>x2 is common.)

## flag = quadtree depth (16 per level), leaf = 128
`depth = flag // 16`. Node box size = `12800 / 2^depth`:

| depth | flag band | node size (world) |
|-------|-----------|-------------------|
| 0 (root) | 15      | 12800 |
| 1     | 16–31     | 6400  |
| 2     | 32–47     | 3200  |
| 3     | 48–63     | 1600  |
| 4     | 64–79     | 800   |
| 5     | 80–95     | 400   |
| 6     | 96–111    | 200   |
| 7     | 112–127   | 100   |
| 8 (LEAF) | **128** | **50** |

**Only depth-8 leaves (flag==128, 50×50) are WALKABLE cells.** Larger nodes are internal
structure on the path from root to walkable leaves. Blocked regions are **absent** (pruned —
that's the compression). RouVal02 depth histogram: {0:1, 1:4, 2:16, 3:46, 4:145, 5:448,
6:1501, 7:5324, 8:19065} — a textbook pruned quadtree (powers of 4).

Coords are world units; world extent = 12800 = 2048 tiles × 6.25 = 256 blocks × 50
(`.ini` OneBlockWidth=50). `block = world // 50`, grid is 256×256 blocks.

## To read walkability
```
walkable(worldX, worldY):  is (worldX,worldY) inside any leaf (flag==128) box?
```
Equivalently: build a 256×256 bool grid, set True for each leaf's block, `walkable = grid[y//50][x//50]`.

## Relationship to `.shbd` (IMPORTANT — the open question)
`.bdt` walkable ≈ `.shbd` walkable but **coarser** (50u block vs 6.25u tile). At boundary
blocks they disagree both ways:
- **bdt-walkable / shbd-blocked** (bdt more permissive): a thin ring around every walkable
  boundary (a block counts walkable if it contains any walkable leaf).
- **shbd-walkable / bdt-blocked**: ~75–566 blocks on RouVal02 — **candidate wedge sites** IF
  the server enforces the `.bdt` grid and our bot pathfinds on the finer `.shbd`.

⚠️ **UNPROVEN which grid the server enforces.** The one known wedge tile (RouVal02 our-tile
(1329,1310) ≈ world (8306,8188), block (166,163)) is walkable in BOTH bdt and shbd, so it is
not explained by a binary walkable test on either grid alone. **Decide via the measuring
stick**: disable MOVEFAIL-poison, roam RouVal02, log MOVEFAIL world coords, then for each point
check `shbd-walkable?` and `bdt-walkable?` — whichever grid predicts the server's rejections is
the one to pathfind on. Analysis harness: `scratchpad/bdt*.py` (this session).
