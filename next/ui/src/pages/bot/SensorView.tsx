// Sensor map (REDESIGN 9.3 "Sensors"): entities, aggro shapes (detect circle offset 0.4 r along facing), our bash /
// skill reach rings, leash origins, the current path with waypoints, nav mesh overlay (when sent), assist radii and
// the threat heat map. Pan: drag; zoom: wheel. Everything drawn comes from the SensorFrame.
import { useEffect, useRef, useState } from 'react';
import type { SensorEntity, SensorFrame } from '../../api/types';
import { useTopic } from '../../api/ws';
import { api } from '../../api/client';

interface Layers { heat: boolean; detect: boolean; reach: boolean; leash: boolean; path: boolean; nav: boolean; labels: boolean }

export function SensorView({ bot, height = 520, frame: fixed }: { bot: string; height?: number; frame?: SensorFrame }) {
  const ws = useTopic<SensorFrame>(fixed ? null : { topic: 'sensors', bot, hz: 5 });
  const [polled, setPolled] = useState<SensorFrame | undefined>();
  const last = useRef(0);
  useEffect(() => { if (ws) last.current = Date.now(); }, [ws]);
  useEffect(() => {
    if (fixed) return;
    const t = window.setInterval(async () => { if (Date.now() - last.current > 2000) { try { setPolled(await api.sensors(bot)); } catch { /* offline */ } } }, 500);
    return () => window.clearInterval(t);
  }, [bot, fixed]);
  const frame = fixed ?? (ws && Date.now() - last.current < 2500 ? ws : polled ?? ws);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [view, setView] = useState({ scale: 0.35, ox: 0, oy: 0 }); // world units -> px, offset from self in px
  const [layers, setLayers] = useState<Layers>({ heat: true, detect: true, reach: true, leash: true, path: true, nav: true, labels: true });
  const [hover, setHover] = useState<SensorEntity | null>(null);
  const drag = useRef<{ x: number; y: number; ox: number; oy: number } | null>(null);

  useEffect(() => {
    const c = canvas.current; if (!c || !frame) return;
    const dpr = window.devicePixelRatio || 1;
    const w = c.clientWidth, h = c.clientHeight;
    if (c.width !== w * dpr || c.height !== h * dpr) { c.width = w * dpr; c.height = h * dpr; }
    const g = c.getContext('2d')!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const s = view.scale;
    const sx = (x: number) => w / 2 + view.ox + (x - frame.self.x) * s;
    const sy = (y: number) => h / 2 + view.oy - (y - frame.self.y) * s; // world y up
    // heat
    if (layers.heat && frame.threat) {
      const t = frame.threat; const bytes = atob(t.values);
      for (let j = 0; j < t.h; j++) for (let i = 0; i < t.w; i++) {
        const v = bytes.charCodeAt(j * t.w + i); if (!v) continue;
        g.fillStyle = `rgba(214,92,92,${(v / 255) * 0.35})`;
        const x0 = sx(t.origin[0] + i * t.cellSize), y0 = sy(t.origin[1] + (j + 1) * t.cellSize);
        g.fillRect(x0, y0, t.cellSize * s + 0.5, t.cellSize * s + 0.5);
      }
    }
    // grid
    g.strokeStyle = 'rgba(255,255,255,0.04)'; g.lineWidth = 1;
    const step = 200 * s;
    for (let x = ((w / 2 + view.ox - frame.self.x * s) % step + step) % step; x < w; x += step) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, h); g.stroke(); }
    for (let y = ((h / 2 + view.oy + frame.self.y * s) % step + step) % step; y < h; y += step) { g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke(); }
    // nav mesh overlay (optional field)
    const nav = (frame as any).nav as { rects?: [number, number, number, number][] } | undefined;
    if (layers.nav && nav?.rects) {
      g.strokeStyle = 'rgba(78,159,209,0.25)';
      for (const [x, y, rw, rh] of nav.rects) g.strokeRect(sx(x), sy(y + rh), rw * s, rh * s);
    }
    // path
    if (layers.path && frame.path?.length > 1) {
      g.strokeStyle = '#4e9fd1'; g.setLineDash([6, 4]); g.lineWidth = 1.5; g.beginPath();
      frame.path.forEach(([x, y], i) => (i ? g.lineTo(sx(x), sy(y)) : g.moveTo(sx(x), sy(y)))); g.stroke(); g.setLineDash([]);
      g.fillStyle = '#4e9fd1'; for (const [x, y] of frame.path.slice(1)) { g.beginPath(); g.arc(sx(x), sy(y), 3, 0, Math.PI * 2); g.fill(); }
    }
    // entities
    for (const e of frame.entities) {
      const x = sx(e.x), y = sy(e.y);
      if (layers.detect && e.detect && e.detect > 0) {
        const f = e.facing ?? [0, 0];
        const cx = sx(e.x + f[0] * 0.4 * e.detect), cy = sy(e.y + f[1] * 0.4 * e.detect);
        g.strokeStyle = e.aggro ? 'rgba(214,92,92,0.7)' : 'rgba(201,166,64,0.35)'; g.lineWidth = 1;
        g.beginPath(); g.arc(cx, cy, e.detect * s, 0, Math.PI * 2); g.stroke();
      }
      const assist = (e as any).assist as number | undefined;
      if (layers.detect && assist) { g.strokeStyle = 'rgba(180,111,216,0.35)'; g.setLineDash([3, 3]); g.beginPath(); g.arc(x, y, assist * s, 0, Math.PI * 2); g.stroke(); g.setLineDash([]); }
      if (layers.leash && e.leashOrigin) {
        const lx = sx(e.leashOrigin[0]), ly = sy(e.leashOrigin[1]);
        g.strokeStyle = 'rgba(201,166,64,0.6)'; g.setLineDash([2, 3]); g.beginPath(); g.moveTo(lx, ly); g.lineTo(x, y); g.stroke(); g.setLineDash([]);
        g.fillStyle = 'rgba(201,166,64,0.9)'; g.fillRect(lx - 3, ly - 3, 6, 6);
      }
      g.fillStyle = e.k === 'npc' ? '#4e9fd1' : e.k === 'player' ? '#b46fd8' : e.k === 'drop' ? '#c9a640' : e.aggro ? '#d65c5c' : '#c9a640';
      g.beginPath();
      if (e.k === 'npc') g.rect(x - 5, y - 5, 10, 10); else g.arc(x, y, 5, 0, Math.PI * 2);
      g.fill();
      if (e.hpPct != null && e.k === 'mob') { g.fillStyle = '#222'; g.fillRect(x - 10, y + 8, 20, 3); g.fillStyle = '#37b29a'; g.fillRect(x - 10, y + 8, 20 * (e.hpPct / 100), 3); }
      if (layers.labels) { g.fillStyle = 'rgba(230,236,238,0.75)'; g.font = '10px system-ui'; g.fillText(e.name ?? `${e.k} ${e.h}`, x + 8, y - 6); }
      if (hover?.h === e.h) { g.strokeStyle = '#fff'; g.beginPath(); g.arc(x, y, 9, 0, Math.PI * 2); g.stroke(); }
    }
    // self + reach
    const mx = sx(frame.self.x), my = sy(frame.self.y);
    if (layers.reach) {
      g.strokeStyle = 'rgba(55,178,154,0.8)'; g.beginPath(); g.arc(mx, my, frame.reach.bash * s, 0, Math.PI * 2); g.stroke();
      g.strokeStyle = 'rgba(55,178,154,0.35)';
      for (const k of frame.reach.skills) { g.beginPath(); g.arc(mx, my, k.reach * s, 0, Math.PI * 2); g.stroke(); }
    }
    g.fillStyle = '#37b29a'; g.beginPath(); g.arc(mx, my, 7, 0, Math.PI * 2); g.fill();
    const f = frame.self.facing; g.strokeStyle = '#37b29a'; g.lineWidth = 2; g.beginPath(); g.moveTo(mx, my); g.lineTo(mx + f[0] * 18, my - f[1] * 18); g.stroke();
    g.fillStyle = 'rgba(230,236,238,0.6)'; g.font = '11px system-ui';
    g.fillText(`${frame.map} (${Math.round(frame.self.x)}, ${Math.round(frame.self.y)})  scale ${view.scale.toFixed(2)} px/u`, 8, h - 8);
  }, [frame, view, layers, hover]);

  const toWorld = (px: number, py: number) => {
    const c = canvas.current!; const r = c.getBoundingClientRect();
    return { x: frame!.self.x + (px - r.left - r.width / 2 - view.ox) / view.scale, y: frame!.self.y - (py - r.top - r.height / 2 - view.oy) / view.scale };
  };

  if (!frame) return <div className="empty">No sensor frame yet.</div>;
  return (
    <div className="sensor">
      <div className="row wrap small">
        {(Object.keys(layers) as (keyof Layers)[]).map(k => <label key={k} className="check"><input type="checkbox" checked={layers[k]} onChange={e => setLayers({ ...layers, [k]: e.target.checked })} /> {k}</label>)}
        <button className="btn sm ghost" onClick={() => setView({ scale: 0.35, ox: 0, oy: 0 })}>center</button>
        <span className="muted">{frame.entities.length} entities, {frame.entities.filter(e => e.aggro).length} aggro</span>
      </div>
      <div className="sensor-canvas-wrap" style={{ height }}>
        <canvas ref={canvas} className="sensor-canvas" aria-label="sensor map"
          onWheel={e => { const k = e.deltaY < 0 ? 1.15 : 1 / 1.15; setView(v => ({ ...v, scale: Math.max(0.03, Math.min(4, v.scale * k)) })); }}
          onMouseDown={e => { drag.current = { x: e.clientX, y: e.clientY, ox: view.ox, oy: view.oy }; }}
          onMouseUp={() => { drag.current = null; }} onMouseLeave={() => { drag.current = null; setHover(null); }}
          onMouseMove={e => {
            if (drag.current) { const d = drag.current; setView(v => ({ ...v, ox: d.ox + e.clientX - d.x, oy: d.oy + e.clientY - d.y })); return; }
            const w = toWorld(e.clientX, e.clientY);
            let best: SensorEntity | null = null, bd = 12 / view.scale;
            for (const en of frame.entities) { const dd = Math.hypot(en.x - w.x, en.y - w.y); if (dd < bd) { bd = dd; best = en; } }
            setHover(best);
          }} />
        {hover && (
          <div className="sensor-tip">
            <div><strong>{hover.name ?? hover.k}</strong> <span className="mono">h={hover.h}</span>{hover.mobId != null && hover.mobId >= 0 && <span className="mono"> mob {hover.mobId}</span>}</div>
            <div className="small mono">({Math.round(hover.x)}, {Math.round(hover.y)}) hp {hover.hpPct ?? '-'}% {hover.aggro ? 'AGGRO' : ''}</div>
            {hover.detect != null && <div className="small">detect {Math.round(hover.detect)} u</div>}
            {hover.leashLeft != null && <div className="small">leash left {Math.round(hover.leashLeft)} u</div>}
          </div>
        )}
      </div>
    </div>
  );
}
