// "Game" tab: a compact HUD of what the game client shows for this bot (vitals, exp, money, stats, skill bar with
// cooldown sweeps, soul stones, buffs with time left, bag pages + equipment with icons and tooltips, minimap).
// State comes from the per-bot `game` topic (full snapshot, then RFC 6902 deltas applied with structural sharing),
// static data by id from cacheable endpoints. Cooldowns / buff timers are end times animated locally (one rAF loop
// that only touches the elements that are counting down).
import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { hub } from '../../../api/ws';
import { applyOps, fromSnapshot } from './gameModel';
import { Empty, fmtNum } from '../../../components/ui';
import { Minimap, entName } from './Minimap';
import { iconUrl, useStatic, useStaticVersion, type AbstateInfo, type ItemInfo, type MapInfo, type MobInfo, type SkillInfo } from './staticData';
import type { GameBuff, GameEnt, GameSkill, GameState, GameStone } from './types';

type Tip = { kind: 'item'; id: number; lot?: number; upgrade?: number } | { kind: 'skill'; id: number } | { kind: 'buff'; id: number; endAt: number | null }
  | { kind: 'stone'; which: string; s: GameStone } | { kind: 'ent'; h: string; e: GameEnt } | { kind: 'text'; text: string };

/** The `game` topic reassembled: snapshot, then op lists; a seq gap re-subscribes (= fresh snapshot). */
function useGame(bot: string): GameState | undefined {
  const [state, setState] = useState<GameState>();
  useEffect(() => {
    setState(undefined);
    const msg = { topic: 'game', bot };
    let seq = -1;
    return hub.subscribe(msg, (m: any) => {
      if (m.t === 'snap') { seq = m.seq; setState(fromSnapshot(m.data, m.now)); return; }
      if (seq < 0) return; // wait for the snapshot
      if (m.seq !== seq + 1) { seq = -1; hub.resubscribe(msg); return; }
      seq = m.seq;
      if (m.t === 'd') setState(s => (s ? applyOps(s, m.ops ?? [], m.now) : s));
      else if (m.t === 'off') setState(s => (s ? { ...s, offline: true, serverTime: m.now } : s));
    }, true);
  }, [bot]);
  return state;
}

export function GameTab({ bot }: { bot: string }) {
  const state = useGame(bot);
  const offset = useRef(0); // serverTime - Date.now(), so local animation follows the host clock
  const samples = useRef<number[]>([]);
  useEffect(() => {
    if (!state) return;
    samples.current = [...samples.current.slice(-9), state.serverTime - Date.now()];
    offset.current = Math.max(...samples.current); // the least-delayed sample
  }, [state?.serverTime]); // eslint-disable-line react-hooks/exhaustive-deps
  const root = useRef<HTMLDivElement>(null);
  const [tip, setTip] = useState<{ t: Tip; x: number; y: number } | null>(null);

  // one animation loop for every countdown on the tab
  useEffect(() => {
    let raf = 0;
    const tick = () => {
      raf = requestAnimationFrame(tick);
      const el = root.current; if (!el) return;
      const now = Date.now() + offset.current;
      el.querySelectorAll<HTMLElement>('[data-end]').forEach(n => {
        const end = Number(n.dataset.end), dur = Number(n.dataset.dur || 0);
        const left = Math.max(0, end - now);
        if (dur) n.style.setProperty('--sweep', `${Math.min(1, left / dur) * 360}deg`);
        const label = n.querySelector<HTMLElement>('.cd-text');
        if (label) {
          const pretty = left <= 0 ? '' : left < 10000 ? (left / 1000).toFixed(1) : left < 60000 ? String(Math.ceil(left / 1000)) : `${Math.floor(left / 60000)}m`;
          if (label.textContent !== pretty) label.textContent = pretty;
        }
        n.classList.toggle('cooling', left > 0);
      });
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, []);

  // stable, so memoised tiles do not re-render on every game update
  const onTip = useCallback((t: Tip | null, ev?: { clientX: number; clientY: number }) => setTip(t && ev ? { t, x: ev.clientX, y: ev.clientY } : null), []);
  const onEnt = useCallback((h: { ent: GameEnt; h: string; x: number; y: number } | null) => setTip(h ? { t: { kind: 'ent', h: h.h, e: h.ent }, x: h.x, y: h.y } : null), []);
  if (!state) return <Empty>Waiting for the game view (topic <code>game</code>)...</Empty>;

  return (
    <div className={'game' + (state.offline ? ' offline' : '')} ref={root} onMouseLeave={() => setTip(null)}>
      {state.offline && <div className="banner">The bot left the world; showing its last state until it is back.</div>}
      <Hud s={state} />
      <div className="game-grid">
        <div className="game-left">
          <Minimap state={state} onHover={onEnt} />
          <TargetLine state={state} />
          <Buffs buffs={state.buffs} onTip={onTip} />
        </div>
        <div className="game-right">
          <SkillBar skills={state.skills} stones={stonesOf(state)} onTip={onTip} />
          <Bag state={state} onTip={onTip} />
          <Stats s={state.self} />
        </div>
      </div>
      {tip && <Tooltip tip={tip.t} x={tip.x} y={tip.y} offset={offset.current} />}
    </div>
  );
}

function stonesOf(s: GameState): GameState['stones'] {
  if (s.stones) return s.stones;
  const st: GameState['stones'] = {};
  if (s.self.hpStones != null) st.hp = { count: s.self.hpStones, cdEnd: 0, cdMs: 0 };
  if (s.self.spStones != null) st.sp = { count: s.self.spStones, cdEnd: 0, cdMs: 0 };
  return st;
}

const Bar = memo(function Bar({ label, v, max, cls }: { label: string; v: number; max: number; cls: string }) {
  const p = max > 0 ? Math.max(0, Math.min(100, (v / max) * 100)) : 0;
  return (
    <div className={'hud-bar ' + cls} title={`${label} ${fmtNum(v)} / ${fmtNum(max)}`}>
      <div className="hud-fill" style={{ width: `${p}%` }} />
      <span className="hud-l">{label}</span><span className="hud-v">{fmtNum(v)} / {fmtNum(max)}</span>
    </div>
  );
});

const Hud = memo(function Hud({ s: st }: { s: GameState }) {
  const s = st.self;
  const map = useStatic<MapInfo>('maps', s.mapId);
  const span = s.expNext - (s.expPrev ?? 0);
  const expPct = span > 0 ? ((s.exp - (s.expPrev ?? 0)) / span) * 100 : 0;
  const flags = [s.dead && 'DEAD', s.inCombat && 'combat', s.mounted && 'mounted', s.moving && 'moving'].filter(Boolean) as string[];
  return (
    <div className="hud">
      <div className="hud-id">
        <span className="hud-lv">Lv {s.level}</span>
        <span className="hud-name">{s.name}</span>
        {s.className && <span className="muted small">{s.className}</span>}
        <span className="muted small">{map?.name ?? s.map ?? `map ${s.mapId}`} <span className="mono">({Math.round(s.x)}, {Math.round(s.y)})</span></span>
        {flags.map(f => <span key={f} className={'tag ' + (f === 'DEAD' ? 'err' : f === 'combat' ? 'warn' : '')}>{f}</span>)}
        <span className="grow" />
        <span className="hud-money" title="money">{fmtNum(s.money)} <span className="muted small">cen</span></span>
      </div>
      <div className="hud-bars">
        <Bar label="HP" v={s.hp} max={s.maxHp} cls="hp" />
        <Bar label="SP" v={s.sp} max={s.maxSp} cls="sp" />
        {s.maxLp ? <Bar label="LP" v={s.lp ?? 0} max={s.maxLp} cls="lp" /> : null}
      </div>
      <div className="hud-exp" title={`exp ${fmtNum(s.exp)} / ${fmtNum(s.expNext)}`}>
        <div className="hud-fill" style={{ width: `${Math.max(0, Math.min(100, expPct))}%` }} />
        <span className="hud-l">EXP</span><span className="hud-v">{expPct.toFixed(2)}%</span>
      </div>
    </div>
  );
}, (a, b) => a.s.self === b.s.self);

function TargetLine({ state }: { state: GameState }) {
  useStaticVersion();
  const t = state.self.target >= 0 ? state.ents[String(state.self.target)] : undefined;
  const onUs = Object.values(state.ents).filter(e => e.aggro).length;
  return (
    <div className="target-line small">
      <span>target: {t ? <><strong>{entName(t)}</strong>{t.lvl ? ` Lv ${t.lvl}` : ''}{t.hpPct != null && <span className="tbar"><span style={{ width: `${t.hpPct}%` }} /></span>}{t.hpPct != null ? `${t.hpPct}%` : ''}</> : <span className="muted">none</span>}</span>
      <span className={onUs ? 'bad-text' : 'muted'}>{onUs} aggro on us</span>
    </div>
  );
}

const Icon = memo(function Icon({ url, fallback }: { url?: string | null; fallback: string }) {
  const [bad, setBad] = useState(false);
  const u = iconUrl(url);
  if (!u || bad) return <span className="ic-fallback">{fallback}</span>;
  return <img src={u} alt="" draggable={false} onError={() => setBad(true)} />;
});

const SkillTile = memo(function SkillTile({ s, onTip }: { s: GameSkill; onTip: (t: Tip | null, ev?: any) => void }) {
  const info = useStatic<SkillInfo>('skills', s.id);
  return (
    <div className="tile" data-end={s.cdEnd} data-dur={s.cdMs} onMouseMove={e => onTip({ kind: 'skill', id: s.id }, e)} onMouseLeave={() => onTip(null)}>
      <Icon url={info?.icon} fallback={info?.name?.slice(0, 6) ?? String(s.id)} />
      <div className="sweep" /><span className="cd-text" />
    </div>
  );
});

const StoneTile = memo(function StoneTile({ which, s, onTip }: { which: string; s: GameStone; onTip: (t: Tip | null, ev?: any) => void }) {
  return (
    <div className={'tile stone ' + which + (s.count ? '' : ' out')} data-end={s.cdEnd} data-dur={s.cdMs} onMouseMove={e => onTip({ kind: 'stone', which, s }, e)} onMouseLeave={() => onTip(null)}>
      <span className="ic-fallback">{which.toUpperCase()}</span><span className="cnt">{s.count}</span>
      <div className="sweep" /><span className="cd-text" />
    </div>
  );
});

function SkillBar({ skills, stones, onTip }: { skills: GameState['skills']; stones: GameState['stones']; onTip: (t: Tip | null, ev?: any) => void }) {
  const list = useMemo(() => Object.values(skills).sort((a, b) => a.id - b.id), [skills]);
  return (
    <div className="card game-card">
      <div className="side-h">Skills <span className="muted">{list.length}</span></div>
      <div className="tiles">
        {stones?.hp && <StoneTile which="hp" s={stones.hp} onTip={onTip} />}
        {stones?.sp && <StoneTile which="sp" s={stones.sp} onTip={onTip} />}
        {list.map(s => <SkillTile key={s.id} s={s} onTip={onTip} />)}
        {!list.length && <span className="muted small">no skills</span>}
      </div>
    </div>
  );
}

const BuffChip = memo(function BuffChip({ b, onTip }: { b: GameBuff; onTip: (t: Tip | null, ev?: any) => void }) {
  const info = useStatic<AbstateInfo>('abstates', b.id);
  return (
    <div className={'buff' + (info?.debuff ? ' debuff' : '')} data-end={b.endAt ?? undefined} onMouseMove={e => onTip({ kind: 'buff', id: b.id, endAt: b.endAt }, e)} onMouseLeave={() => onTip(null)}>
      <Icon url={info?.icon} fallback={String(b.id)} />
      <span className="cd-text" />
    </div>
  );
});

function Buffs({ buffs, onTip }: { buffs: GameState['buffs']; onTip: (t: Tip | null, ev?: any) => void }) {
  const list = Object.values(buffs);
  return (
    <div className="buffs">
      {list.map(b => <BuffChip key={b.id} b={b} onTip={onTip} />)}
      {!list.length && <span className="muted small">no buffs / debuffs</span>}
    </div>
  );
}

const Slot = memo(function Slot({ itemId, lot, upgrade, label, onTip }: { itemId?: number; lot?: number; upgrade?: number; label?: string; onTip: (t: Tip | null, ev?: any) => void }) {
  const info = useStatic<ItemInfo>('items', itemId);
  if (itemId == null) return <div className="slot slot-empty">{label && <span className="slot-l">{label}</span>}</div>;
  return (
    <div className={'slot g-' + (info?.grade ?? 'normal')} onMouseMove={e => onTip({ kind: 'item', id: itemId, lot, upgrade }, e)} onMouseLeave={() => onTip(null)}>
      <Icon url={info?.icon} fallback={info?.name?.slice(0, 8) ?? String(itemId)} />
      {lot && lot > 1 ? <span className="cnt">{lot}</span> : null}
      {upgrade ? <span className="upg">+{upgrade}</span> : null}
    </div>
  );
});

function Bag({ state, onTip }: { state: GameState; onTip: (t: Tip | null, ev?: any) => void }) {
  const size = state.bag.size, per = state.bag.pageSize || 24;
  const pages = Math.max(1, Math.ceil(size / per));
  const [page, setPage] = useState(0);
  const used = Object.keys(state.bag.slots).length;
  const equip = Object.entries(state.equip).sort((a, b) => Number(a[0]) - Number(b[0]));
  return (
    <div className="card game-card">
      <div className="side-h">Inventory <span className="muted">{used}/{size}</span>
        <span className="bag-tabs">{Array.from({ length: pages }, (_, i) => (
          <button key={i} className={'bag-tab' + (i === page ? ' on' : '')} onClick={() => setPage(i)}>{i + 1}<span className="muted"> {countPage(state, i, per)}</span></button>
        ))}</span>
      </div>
      <div className="bag-page">
        {Array.from({ length: Math.min(per, size - page * per) }, (_, i) => {
          const slot = page * per + i; const it = state.bag.slots[slot];
          return <Slot key={slot} itemId={it?.itemId} lot={it?.lot} onTip={onTip} />;
        })}
      </div>
      <div className="side-h">Equipped</div>
      <div className="equip-row">
        {equip.map(([k, e]) => <Slot key={k} itemId={e.itemId} upgrade={e.upgrade} onTip={onTip} />)}
        {!equip.length && <span className="muted small">nothing equipped</span>}
      </div>
    </div>
  );
}
function countPage(s: GameState, page: number, per: number) { let n = 0; for (const k of Object.keys(s.bag.slots)) { const i = Number(k); if (i >= page * per && i < (page + 1) * per) n++; } return n; }

const Stats = memo(function Stats({ s }: { s: GameState['self'] }) {
  const st = s.stats;
  if (!st) return null;
  const rows: [string, string][] = Object.entries(st).map(([k, v]) => [k, typeof v === 'number' ? (v < 0 ? '-' : fmtNum(v, 1)) : String(v)]); // -1 = not received yet
  return (
    <div className="card game-card">
      <div className="side-h">Stats {s.freeStatPoints ? <span className="warn-text">+{s.freeStatPoints} unspent</span> : null}</div>
      <div className="stat-grid">{rows.map(([k, v]) => <div key={k}><span className="muted">{k}</span><span className="mono">{v}</span></div>)}</div>
    </div>
  );
}, (a, b) => a.s.stats === b.s.stats && a.s.freeStatPoints === b.s.freeStatPoints);

function Tooltip({ tip, x, y, offset }: { tip: Tip; x: number; y: number; offset: number }) {
  useStaticVersion();
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ left: x + 14, top: y + 14 });
  useEffect(() => {
    const el = ref.current; if (!el) return;
    const w = el.offsetWidth, h = el.offsetHeight;
    setPos({ left: x + 14 + w > window.innerWidth ? x - w - 10 : x + 14, top: y + 14 + h > window.innerHeight ? y - h - 10 : y + 14 });
  }, [x, y, tip]);
  return <div ref={ref} className="gtip" style={pos}><TipBody tip={tip} offset={offset} /></div>;
}

function TipBody({ tip, offset }: { tip: Tip; offset: number }) {
  const item = useStatic<ItemInfo>('items', tip.kind === 'item' ? tip.id : null);
  const skill = useStatic<SkillInfo>('skills', tip.kind === 'skill' ? tip.id : null);
  const ab = useStatic<AbstateInfo>('abstates', tip.kind === 'buff' ? tip.id : null);
  const mob = useStatic<MobInfo>('mobs', tip.kind === 'ent' && ['mob', 'npc', 'gate'].includes(tip.e.k) ? tip.e.id : null);
  const now = Date.now() + offset;
  switch (tip.kind) {
    case 'item': return item ? (
      <>
        <div className={'tt-name g-' + (item.grade ?? 'normal')}>{item.name}{tip.upgrade ? ` +${tip.upgrade}` : ''}</div>
        <div className="muted">{[item.kind, item.grade, item.levelReq ? `Lv ${item.levelReq}` : null].filter(Boolean).join(' · ')} <span className="mono">#{item.id}</span></div>
        {item.stats && Object.entries(item.stats).map(([k, v]) => <div key={k}>{k} <strong>{v}</strong></div>)}
        {item.desc && <div className="tt-desc">{item.desc}</div>}
        <div className="muted">{tip.lot && tip.lot > 1 ? `x${tip.lot} · ` : ''}{item.sellPrice != null ? `sells for ${fmtNum(item.sellPrice)}` : ''}</div>
      </>) : <div>item {tip.id}</div>;
    case 'skill': return skill ? (
      <>
        <div className="tt-name">{skill.name}{skill.rank ? ` (rank ${skill.rank})` : ''}</div>
        <div className="muted">{[skill.tags, skill.spCost != null ? `SP ${skill.spCost}` : null, skill.cooldownMs ? `cd ${(skill.cooldownMs / 1000).toFixed(1)} s` : null, skill.castMs ? `cast ${(skill.castMs / 1000).toFixed(1)} s` : null, skill.range ? `range ${skill.range}` : null].filter(Boolean).join(' · ')}</div>
        {skill.desc && <div className="tt-desc">{skill.desc}</div>}
      </>) : <div>skill {tip.id}</div>;
    case 'buff': return (
      <>
        <div className={'tt-name ' + (ab?.debuff ? 'bad-text' : '')}>{ab?.name ?? `state ${tip.id}`}</div>
        <div className="muted">{tip.endAt ? `${Math.max(0, Math.ceil((tip.endAt - now) / 1000))} s left` : 'no time limit'}</div>
        {ab?.desc && <div className="tt-desc">{ab.desc}</div>}
      </>);
    case 'stone': return <><div className="tt-name">{tip.which.toUpperCase()} soul stone</div><div>{tip.s.count}{tip.s.max ? ` / ${tip.s.max}` : ''} left · cd {(tip.s.cdMs / 1000).toFixed(1)} s</div></>;
    case 'ent': return (
      <>
        <div className="tt-name">{entName(tip.e)}{tip.e.lvl ? ` Lv ${tip.e.lvl}` : mob?.level ? ` Lv ${mob.level}` : ''}</div>
        <div className="muted">{tip.e.k}{tip.e.id != null ? ` #${tip.e.id}` : ''} · h {tip.h}{mob?.grade ? ` · ${mob.grade}` : ''}{mob?.maxHp ? ` · max HP ${fmtNum(mob.maxHp)}` : ''}</div>
        {tip.e.hpPct != null && <div>HP {tip.e.hpPct}%</div>}
        {tip.e.aggro && <div className="bad-text">aggro on us</div>}{tip.e.ourTarget && <div>our target</div>}{tip.e.dead && <div className="muted">dead</div>}
      </>);
    default: return <div>{tip.text}</div>;
  }
}
