# NavMesh path search: corridor A* + funnel (port guide from fiesta-godot, 2026-10-15)

fiesta-godot ported `src/Fiesta.Bot/Pathfinding/NavMeshPath.cs` to C++ (`fiesta-godot/extension/src/fiesta_walk.cpp`,
`FiestaWalkGrid::find_path`). The operator saw about 10 % of clicks walk ~130 degrees the wrong way for ~10 s before
turning back. Measuring the port showed why, and the fix below is live there. **The bots run the same algorithm, so they
have the same bug** (one candidate cause of bot deaths: walking away from the goal, or into a pack, for seconds).

## The measurement
`fiesta-godot/project/tests/path_quality.gd` takes random free point pairs (200-3000 units apart) and compares
`find_path`'s length against the exact 8-connected shortest path (a full-grid A*, test-only). It also checks every leg
with an exact supercover grid traversal. The old algorithm, 300 pairs per map:

| map      | paths over 1.15x the shortest | worst  |
|----------|-------------------------------|--------|
| Eld      | 113 / 300 (38 %)              | 3.48x  |
| RouVal01 | 64 / 300 (21 %)               | 2.29x  |

Typical failure (Eld): the start is 650 units from the goal and the first waypoint is 700 units *past* the goal.

## Two causes
1. **Region centres in the via-point list.** `Find` pushes `centre(A), near, far, centre(B), ...`. The greedy
   string-pull then reaches for the *furthest-indexed* point with line of sight. Greedy rectangles are often long thin
   strips, and a strip's centre can be hundreds of tiles beyond the goal yet still visible from the start, so the pull
   jumps there first. This is the wrong-way walk.
2. **Centre-to-centre Dijkstra costs** (`RegionRoute`). These misjudge long strips, so the corridor itself detours.

## The fix (what fiesta-godot does now)
1. **Corridor: A\* across regions.** Each region records the point where the way entered it:
   - Start region: its entry point is the start.
   - Each portal: cross it where the segment *entry -> goal* meets the portal line. If the goal is on the same side,
     use the goal's coordinate along the portal instead. Clamp the crossing into `[From + 0.5, Until + 0.5]`.
   - Cost = previous cost + |entry - crossing|; heuristic = |crossing - goal|.
   - This is still one label per region (an approximation), but it gets the region *sequence* right almost every time.
2. **Waypoints: the funnel algorithm** (Mononen's "simple stupid funnel") through the corridor's portals:
   - Portal ends are the tile centres inside it: `(At, From + 0.5)` / `(At, Until + 0.5)` for a vertical portal.
   - Left/right comes from the walking direction across the portal (+x if the next region is right of this one):
     an end is "right" when `cross(dir, end - mid) < 0`.
   - `tri(a, b, c) = (c.x - a.x) * (b.y - a.y) - (b.x - a.x) * (c.y - a.y)`, with the usual SSF update rules.
3. **Corner nudge (this is why the funnel now works).** A funnel corner lies *on* a portal line, which is a tile edge,
   so a leg can run along a wall's face. That is the ~90 % clipping the comment in `NavMeshPath.Find` reports for the
   earlier SSF attempt. For each corner whose two legs are not both clear (exact supercover check, honouring doors in
   the bots), try moving it by (+-0.5, 0), (0, +-0.5) and the four diagonals; keep the first one where both legs are
   clear.
4. Drop a final corner within one tile of the goal when the goal is in clear view (otherwise every path ends with a
   ~3-unit back-step and a second MOVE).
5. Remove region centres from the via points entirely.

The C++ is about 120 lines; see `find_path` in fiesta-godot `extension/src/fiesta_walk.cpp` (commits 56e087d and
f8248ea).

## Results (same 300 pairs per map)
| map      | over 1.15x | supercover-clipping legs | avg / max time   |
|----------|------------|--------------------------|------------------|
| Eld      | 3 / 300    | 0                        | 0.25 / 0.9 ms    |
| RouVal01 | 0 / 300    | 0                        | 0.5 / 2.4 ms     |
| Rou      | 1 / 300    | 1 (*)                    | 0.25 / 0.8 ms    |

(*) The final leg goes to the exact requested point inside a free tile, which is off its tile centre, and it shaves a
neighbour's corner. For the bots, consider ending at the goal tile's centre, or check that last leg too.

It is faster than before (the old version could take 5 ms) because the costly multi-pass crossing relaxation and the
furthest-visible string-pull scans are gone.

## Bot-specific things to keep when porting
- **Doors:** `Usable(...)` gating in the A* expansion stays as is. The corner nudge's clear-check must use the
  door-aware `Clear` (supercover over usable regions).
- **Clearance band / endpoint snapping / `Reattach`:** unchanged; apply the funnel between the snapped endpoints.
- **`Reachable`** can keep the cheap region route (its answer is the same).
- Re-run the bots' own validity audit (the 3,387-path supercover audit cited in `Clear`'s comment) before trusting it.
  The fiesta-godot numbers are from a margin-0 mesh without doors.
- The operator's verdict on validation: **"Best test is real usage against a real server."** Run the leveler and
  watch for wrong-way legs and deaths while travelling.
