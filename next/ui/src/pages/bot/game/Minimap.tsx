// Minimap (canvas): the map art when the host has it, the bot with its facing and remaining path, mobs (aggro on us /
// targeting us highlighted, our target ringed), players, NPCs and gates. Movement is drawn the way the game client
// draws it: each update carries a move segment (where it stood at the server time, where it walks to, its speed) and
// every animation frame places the dot along it, so the dot glides at the walking speed whatever the update cadence
// (it was a 220 ms slide toward each ~300 ms sample, then a stop: the walk juddered). A correction that lands near
// the extrapolated spot is blended out; a far one (teleport, map change) snaps. Drawn from refs: React never
// re-renders for movement.
import { useEffect, useRef, useState } from 'react';
import type { GameEnt, GameState } from './types';
import { image, imageFailed, lookup, type MapInfo, type MobInfo } from './staticData';

const ZOOMS = [800, 1600, 3200, 6400]; // world units across the view; last = whole map when art is known
const BLEND_MS = 200;   // a correction near the drawn spot fades in over this long
const SNAP_DIST = 150;  // further than this from the drawn spot: snap (teleport, link, respawn)

// a move segment in local time: at `t` (performance.now ms) at (x, y), walking to (tx, ty) at spd u/s; (bx, by) the
// offset of the drawn spot from the new segment at `bt`, faded out over BLEND_MS
type Track = { x: number; y: number; tx: number; ty: number; spd: number; t: number; bx: number; by: number; bt: number; rest?: [number, number][] };

function along(tr: Track, now: number) {
  let x = tr.x, y = tr.y;
  if (tr.spd > 0) {
    const dx = tr.tx - tr.x, dy = tr.ty - tr.y, d = Math.hypot(dx, dy);
    let go = (tr.spd * Math.max(0, now - tr.t)) / 1000;
    if (go <= d) { if (d > 0) { x += (dx / d) * go; y += (dy / d) * go; } }
    else {
      // past the leg's end: on along the remaining path (our own walk goes on to its next waypoint at once)
      x = tr.tx; y = tr.ty; go -= d;
      for (const [px, py] of tr.rest ?? []) {
        const sd = Math.hypot(px - x, py - y);
        if (sd >= go) { if (sd > 0) { x += ((px - x) / sd) * go; y += ((py - y) / sd) * go; } go = 0; break; }
        x = px; y = py; go -= sd;
      }
    }
  }
  const k = Math.max(0, 1 - (now - tr.bt) / BLEND_MS);
  return { x: x + tr.bx * k, y: y + tr.by * k };
}

export function Minimap({ state, onHover }: { state: GameState; onHover: (e: { ent: GameEnt; h: string; x: number; y: number } | null) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const stateRef = useRef(state);
  const tracks = useRef(new Map<string, Track>());
  const [zoom, setZoom] = useState(2); // 3200 u: the client minimap art is coarse, closer zooms only blur it
  const [labels, setLabels] = useState(true);
  const view = useRef({ zoom: 1, labels: true, whole: false });
  view.current.zoom = zoom; view.current.labels = labels;
  const proj = useRef<{ sx: (x: number) => number; sy: (y: number) => number; s: number } | null>(null);

  // the server clock in local time: the smallest (arrival - server time) seen is the offset with the least delay
  const clockOff = useRef(Number.POSITIVE_INFINITY);
  // record move segments
  useEffect(() => {
    const now = performance.now();
    const off = Date.now() - performance.now();   // performance.now -> unix ms
    if (state.serverTime) clockOff.current = Math.min(clockOff.current, Date.now() - state.serverTime);
    const local = (at?: number) => (at != null && Number.isFinite(clockOff.current) ? at + clockOff.current - off : now);
    const upd = (key: string, o: { x: number; y: number; tx?: number; ty?: number; spd?: number; at?: number }) => {
      const tr = tracks.current.get(key);
      const seg = { x: o.x, y: o.y, tx: o.tx ?? o.x, ty: o.ty ?? o.y, spd: o.spd ?? 0, t: Math.min(now, local(o.at)) };
      if (!tr) { tracks.current.set(key, { ...seg, bx: 0, by: 0, bt: 0 }); return; }
      if (tr.x === seg.x && tr.y === seg.y && tr.tx === seg.tx && tr.ty === seg.ty && tr.spd === seg.spd) return;
      const drawn = along(tr, now);
      Object.assign(tr, seg);
      const fresh = along({ ...tr, bx: 0, by: 0, bt: 0 }, now);
      const dx = drawn.x - fresh.x, dy = drawn.y - fresh.y;
      if (Math.hypot(dx, dy) > SNAP_DIST) { tr.bx = 0; tr.by = 0; tr.bt = 0; }
      else { tr.bx = dx; tr.by = dy; tr.bt = now; }
    };
    upd('self', state.self);
    const st = tracks.current.get('self');
    if (st) { const p = state.path ?? []; const i = p.findIndex(([px, py]) => px === st.tx && py === st.ty); st.rest = i >= 0 ? p.slice(i + 1) : []; }
    for (const [h, e] of Object.entries(state.ents)) upd(h, e);
    for (const k of [...tracks.current.keys()]) if (k !== 'self' && !(k in state.ents)) tracks.current.delete(k);
    stateRef.current = state;
  }, [state]);

  useEffect(() => {
    let raf = 0;
    const pos = (key: string, now: number) => {
      const tr = tracks.current.get(key); if (!tr) return null;
      return along(tr, now);
    };
    const draw = () => {
      raf = requestAnimationFrame(draw);
      const c = canvas.current; if (!c) return;
      const st = stateRef.current;
      const dpr = window.devicePixelRatio || 1;
      const w = c.clientWidth, h = c.clientHeight;
      if (!w || !h) return;
      if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); }
      const g = c.getContext('2d')!;
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      const now = performance.now();
      const me = pos('self', now) ?? { x: st.self.x, y: st.self.y };
      (window as any).__minimapSelf = { x: me.x, y: me.y, t: now };   // the drawn spot, for measuring smoothness
      const map = lookup<MapInfo>('maps', st.self.mapId);
      const cover = map?.cover;
      const whole = view.current.zoom === ZOOMS.length - 1 && cover;
      const span = whole ? Math.max(cover!.x1 - cover!.x0, cover!.y1 - cover!.y0) : ZOOMS[view.current.zoom];
      const s = Math.min(w, h) / span;
      const cx = whole ? (cover!.x0 + cover!.x1) / 2 : me.x, cy = whole ? (cover!.y0 + cover!.y1) / 2 : me.y;
      const sx = (x: number) => w / 2 + (x - cx) * s;
      const sy = (y: number) => h / 2 - (y - cy) * s; // world y up
      proj.current = { sx, sy, s };
      g.fillStyle = '#0a0f11'; g.fillRect(0, 0, w, h);
      // map art: the image covers `cover` in world units; image top = high world y
      const im = image(map?.image);
      if (im && cover) {
        const cw = cover.x1 - cover.x0, ch = cover.y1 - cover.y0;
        const wx0 = cx - w / 2 / s, wx1 = cx + w / 2 / s, wy0 = cy - h / 2 / s, wy1 = cy + h / 2 / s;
        const srcX = ((wx0 - cover.x0) / cw) * im.naturalWidth, srcW = ((wx1 - wx0) / cw) * im.naturalWidth;
        const srcY = (1 - (wy1 - cover.y0) / ch) * im.naturalHeight, srcH = ((wy1 - wy0) / ch) * im.naturalHeight;
        g.globalAlpha = 0.75;
        try { g.drawImage(im, srcX, srcY, srcW, srcH, 0, 0, w, h); } catch { /* not decoded yet */ }
        g.globalAlpha = 1;
      } else {
        // say why there is no art instead of leaving an empty box
        const label = map?.name ?? map?.short ?? `map ${st.self.mapId}`;
        const why = !map ? null
          : !map.image || imageFailed(map.image) ? `no minimap for ${label}`
          : !cover ? `minimap placement for ${label} not known yet`
          : null;
        if (why) {
          g.fillStyle = 'rgba(230,236,238,.45)'; g.font = '12px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'top';
          g.fillText(why, w / 2, 10);
        }
        g.strokeStyle = 'rgba(255,255,255,0.05)'; g.lineWidth = 1;
        const step = 200 * s; if (step > 6) {
          for (let x = ((sx(0) % step) + step) % step; x < w; x += step) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, h); g.stroke(); }
          for (let y = ((sy(0) % step) + step) % step; y < h; y += step) { g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke(); }
        }
      }
      // path ahead
      if (st.path?.length) {
        g.strokeStyle = '#4e9fd1'; g.lineWidth = 1.5; g.setLineDash([5, 4]); g.beginPath(); g.moveTo(sx(me.x), sy(me.y));
        for (const [x, y] of st.path) g.lineTo(sx(x), sy(y));
        g.stroke(); g.setLineDash([]);
        const [lx, ly] = st.path[st.path.length - 1];
        g.fillStyle = '#4e9fd1'; g.beginPath(); g.arc(sx(lx), sy(ly), 3, 0, Math.PI * 2); g.fill();
      }
      // entities: npcs/gates/players first, mobs on top, aggro last
      const ents = Object.entries(st.ents).sort((a, b) => rank(a[1]) - rank(b[1]));
      g.font = '10px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'bottom';
      for (const [hdl, e] of ents) {
        const p = pos(hdl, now) ?? e;
        const x = sx(p.x), y = sy(p.y);
        if (x < -20 || y < -20 || x > w + 20 || y > h + 20) continue;
        const onUs = !!e.aggro || e.tgt === st.self.handle;
        if (e.k === 'mob') {
          const dead = e.dead || e.hpPct === 0;
          g.fillStyle = dead ? '#555' : onUs ? '#ff5a5a' : e.huntable === false ? '#a08a50' : '#d6b34a';
          g.beginPath(); g.arc(x, y, onUs ? 4.5 : 3.5, 0, Math.PI * 2); g.fill();
          if (onUs && !dead) { g.strokeStyle = 'rgba(255,90,90,.9)'; g.lineWidth = 1.5; g.beginPath(); g.arc(x, y, 7.5, 0, Math.PI * 2); g.stroke(); }
        } else if (e.k === 'player') {
          g.fillStyle = '#7fb3ff'; g.beginPath(); g.arc(x, y, 3.5, 0, Math.PI * 2); g.fill();
        } else if (e.k === 'npc' || e.k === 'gate') {
          g.fillStyle = e.k === 'gate' ? '#bc8cff' : '#56d3ec';
          g.beginPath(); g.moveTo(x, y - 4.5); g.lineTo(x + 4.5, y); g.lineTo(x, y + 4.5); g.lineTo(x - 4.5, y); g.closePath(); g.fill();
        } else {
          g.fillStyle = '#e0c060'; g.fillRect(x - 2, y - 2, 4, 4);
        }
        if (Number(hdl) === st.self.target || e.ourTarget) { g.strokeStyle = '#fff'; g.lineWidth = 1.5; g.setLineDash([3, 2]); g.beginPath(); g.arc(x, y, 10, 0, Math.PI * 2); g.stroke(); g.setLineDash([]); }
        if (view.current.labels && (onUs || (s > 0.08 ? e.k !== 'mob' || s > 0.25 : e.k === 'gate' || e.k === 'player'))) {
          const name = entName(e);
          if (name) { g.fillStyle = 'rgba(0,0,0,.55)'; const tw = g.measureText(name).width; g.fillRect(x - tw / 2 - 2, y - 17, tw + 4, 11); g.fillStyle = e.k === 'mob' ? (onUs ? '#ffb0a0' : '#e8dcb0') : '#d8e6ee'; g.fillText(name, x, y - 6); }
        }
      }
      // self: arrow along facing (degrees, world frame, 0 = +x, counter-clockwise)
      const a = ((st.self.f ?? 0) * Math.PI) / 180;
      const mx = sx(me.x), my = sy(me.y);
      g.save(); g.translate(mx, my); g.rotate(-a);
      g.fillStyle = st.self.dead ? '#777' : '#37e0b4'; g.strokeStyle = '#06261e'; g.lineWidth = 1.2;
      g.beginPath(); g.moveTo(9, 0); g.lineTo(-6, 6); g.lineTo(-3, 0); g.lineTo(-6, -6); g.closePath(); g.fill(); g.stroke();
      g.restore();
      // scale bar
      const bar = niceLen(span / 5); const bw = bar * s;
      g.strokeStyle = 'rgba(230,236,238,.7)'; g.lineWidth = 1; g.beginPath(); g.moveTo(10, h - 12); g.lineTo(10 + bw, h - 12); g.stroke();
      g.fillStyle = 'rgba(230,236,238,.75)'; g.textAlign = 'left'; g.fillText(`${bar} u`, 12, h - 14);
    };
    raf = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(raf);
  }, []);

  const onMove = (ev: React.MouseEvent) => {
    const p = proj.current, c = canvas.current; if (!p || !c) return;
    const r = c.getBoundingClientRect();
    const mx = ev.clientX - r.left, my = ev.clientY - r.top;
    let best: [string, GameEnt] | null = null, bd = 9;
    for (const [h, e] of Object.entries(stateRef.current.ents)) {
      const at = tracks.current.get(h); const q = at ? along(at, performance.now()) : e; const x = p.sx(q.x), y = p.sy(q.y);
      const d = Math.hypot(x - mx, y - my); if (d < bd) { bd = d; best = [h, e]; }
    }
    onHover(best ? { h: best[0], ent: best[1], x: ev.clientX, y: ev.clientY } : null);
  };

  return (
    <div className="mm">
      <canvas ref={canvas} className="mm-canvas" aria-label="minimap" onMouseMove={onMove} onMouseLeave={() => onHover(null)} />
      <div className="mm-bar">
        {ZOOMS.map((z, i) => <button key={z} className={'btn sm ' + (i === zoom ? 'primary' : 'ghost')} onClick={() => setZoom(i)}>{i === ZOOMS.length - 1 ? 'map' : `${z}u`}</button>)}
        <label className="check small"><input type="checkbox" checked={labels} onChange={e => setLabels(e.target.checked)} /> names</label>
        <span className="mm-legend small"><i className="lg self" />bot <i className="lg aggro" />aggro on us <i className="lg mob" />mob <i className="lg pl" />player <i className="lg npc" />npc <i className="lg gate" />gate</span>
      </div>
    </div>
  );
}

function rank(e: GameEnt) { return e.k === 'mob' ? (e.aggro ? 3 : 2) : 1; }
function niceLen(x: number) { const p = 10 ** Math.floor(Math.log10(x)); const m = x / p; return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * p; }

export function entName(e: GameEnt): string {
  if (e.name) return e.name;
  // mobs, NPCs and gates are all MobInfo rows (API_SPEC 14.1); drops are items; players carry their name
  if (e.k === 'mob' || e.k === 'npc' || e.k === 'gate') return lookup<MobInfo>('mobs', e.id)?.name ?? (e.id != null ? `${e.k} ${e.id}` : e.k);
  if (e.k === 'drop') return lookup<{ name: string }>('items', e.id)?.name ?? 'drop';
  return '';
}
