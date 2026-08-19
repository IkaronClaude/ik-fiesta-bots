"""Rebuild every cast from OUR packet log and test the target-desync theory directly:
does the handle we TARGET (0x2401) match the target field inside the CAST (0x2440),
and does a mismatch predict the 0x2434 failure?"""
import re, sys

path = r'C:\Users\Claude\AppData\Local\Temp\claude\C--Projects\d86c6093-3002-4a03-af24-39c8907acf61\scratchpad\snap.log'
lines = open(path, encoding='utf-8', errors='replace').read().splitlines()

hdr = re.compile(r'^\[(\d\d:\d\d:\d\d\.\d+)\]\s+(C->S|S->C)\s+(0x[0-9A-F]{4})')
hexline = re.compile(r'^\s+0000\s+((?:[0-9A-Fa-f]{2} )+)')

events = []
i = 0
while i < len(lines):
    m = hdr.match(lines[i])
    if m:
        ts, dirn, op = m.groups()
        payload = b''
        for j in range(i + 1, min(i + 4, len(lines))):
            hm = hexline.match(lines[j])
            if hm:
                payload = bytes(int(b, 16) for b in hm.group(1).split())
                break
        events.append((ts, dirn, op, payload))
    i += 1

def u16(p, off):
    return p[off] | (p[off + 1] << 8) if len(p) >= off + 2 else None

lastTarget = None      # (ts, handle)
pending = None         # the cast awaiting its verdict
print(f"{'cast time':<14} {'skill':>6} {'castTgt':>8} {'lastTargetted':>14} {'match':>6} {'gap_ms':>7}  verdict")
rows = []
for ts, dirn, op, p in events:
    if dirn == 'C->S' and op == '0x2401':
        lastTarget = (ts, u16(p, 0))
    elif dirn == 'C->S' and op == '0x2440':
        skill, tgt = u16(p, 0), u16(p, 2)
        lt_ts, lt_h = lastTarget if lastTarget else (None, None)
        gap = ''
        if lt_ts:
            def ms(t):
                h, m2, s = t.split(':'); return ((int(h) * 60 + int(m2)) * 60 + float(s)) * 1000
            gap = f"{ms(ts) - ms(lt_ts):.0f}"
        pending = dict(ts=ts, skill=skill, tgt=tgt, lt=lt_h, gap=gap, verdict='(none)')
        rows.append(pending)
    elif dirn == 'S->C' and op in ('0x244E', '0x2434') and pending is not None:
        if op == '0x244E':
            pending['verdict'] = 'OK'
        else:
            code = u16(p, 0)
            pending['verdict'] = f'FAIL 0x{code:04X}' if code is not None else 'FAIL'
        pending = None

for r in rows:
    match = 'YES' if r['lt'] is not None and r['lt'] == r['tgt'] else 'NO'
    lt = f"0x{r['lt']:04X}" if r['lt'] is not None else '-'
    print(f"{r['ts']:<14} {r['skill']:>6} 0x{r['tgt']:04X}   {lt:>14} {match:>6} {r['gap']:>7}  {r['verdict']}")

ok_match = sum(1 for r in rows if r['verdict'] == 'OK' and r['lt'] == r['tgt'])
ok_mis   = sum(1 for r in rows if r['verdict'] == 'OK' and r['lt'] != r['tgt'])
f_match  = sum(1 for r in rows if r['verdict'].startswith('FAIL') and r['lt'] == r['tgt'])
f_mis    = sum(1 for r in rows if r['verdict'].startswith('FAIL') and r['lt'] != r['tgt'])
print(f"\nTARGET MATCHES CAST?   success: match={ok_match} mismatch={ok_mis}   failure: match={f_match} mismatch={f_mis}")
print("If the desync theory holds, failures should cluster in 'mismatch' and successes in 'match'.")
