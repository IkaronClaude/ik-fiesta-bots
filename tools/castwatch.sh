#!/bin/bash
# Wait for a combat window, then count the cast sequence opcodes IN ONE SNAPSHOT of the packet log.
# Counting across two greps at different times is what produced a bogus "87 casts, 0 targetting":
# the log rotates on every zone session, so the two numbers came from different file contents.
POD=$(kubectl get pods -n fiesta -l app=fiesta-bot-host -o jsonpath='{.items[0].metadata.name}')
OUT=/c/Users/Claude/AppData/Local/Temp/claude/C--Projects/d86c6093-3002-4a03-af24-39c8907acf61/scratchpad
for i in $(seq 1 60); do
  MSYS_NO_PATHCONV=1 kubectl cp -n fiesta "$POD:/app/packets-FighterFresh.log" "$OUT/snap.log" >/dev/null 2>&1
  casts=$(grep -ac 0x2440 "$OUT/snap.log" 2>/dev/null || echo 0)
  if [ "$casts" -ge 10 ]; then break; fi
  sleep 20
done
echo "=== single snapshot: $(head -1 "$OUT/snap.log" | cut -c1-46) ==="
echo "lines: $(wc -l < "$OUT/snap.log")"
for op in 0x2401 0x2440 0x244E 0x2434 0x2008 0x242B 0x200A; do
  printf "  %-8s %s\n" "$op" "$(grep -ac "$op" "$OUT/snap.log" 2>/dev/null || echo 0)"
done
echo "=== cast sequences (target/cast/start/fail, in order) ==="
grep -aE "0x2401|0x2440|0x244E|0x2434" "$OUT/snap.log" | tail -30 | cut -c1-96
