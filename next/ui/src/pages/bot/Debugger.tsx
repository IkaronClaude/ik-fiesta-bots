import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../api/client';
import type { DebugFrame, LayerTrace, SensorFrame, TransitionRecord } from '../../api/types';
import { BrainGraph, type GraphSelection, type LiveInfo } from '../../components/BrainGraph';
import { ActionButton, Empty, Field, JsonValue, Sparkline, Tabs, fmtDate, fmtDuration, fmtNum, fmtTime, useAsync, useNow } from '../../components/ui';
import { type BrainDef, type MachineDef, type Scope, machineAt, parseDefinition } from '../../lib/brain';
import { useApp } from '../../lib/app';
import { href } from '../../lib/router';
import { useDebugStream } from './useDebugStream';
import { SensorView } from './SensorView';

const defCache = new Map<string, Promise<BrainDef>>();
export function useVersionDef(version: string | undefined) {
  return useAsync<BrainDef | null>(() => {
    if (!version || !version.includes('@')) return Promise.resolve(null);
    if (!defCache.has(version)) {
      const [name, hash] = version.split('@');
      defCache.set(version, api.version(name, hash).then(v => parseDefinition(v.definition)));
    }
    return defCache.get(version)!;
  }, [version]);
}

type Side = 'conditions' | 'trace' | 'variables' | 'actions' | 'errors' | 'tick';

export function DebuggerTab({ bot, replay }: { bot: string; replay?: { frames: DebugFrame[]; sensors: SensorFrame[]; label: string; onClose: () => void } }) {
  const stream = useDebugStream(bot, !replay);
  const history = replay ? replay.frames : stream.history;
  const [cursor, setCursor] = useState<number | null>(replay ? 0 : null); // null = follow live
  const frame = cursor == null ? (replay ? history[history.length - 1] : stream.latest) : history[Math.min(cursor, history.length - 1)];
  const def = useVersionDef(frame?.version);
  const [layerName, setLayerName] = useState<string | null>(null);
  const [scopePath, setScopePath] = useState<string[] | null>(null); // null = follow the active path
  const [sel, setSel] = useState<GraphSelection>(null);
  const [side, setSide] = useState<Side>('conditions');
  const [traceLayer, setTraceLayer] = useState('');
  const now = useNow(250);
  const { toast } = useApp();

  const transitions: TransitionRecord[] = useMemo(() => {
    if (!replay) return stream.transitions;
    const m = new Map<string, TransitionRecord>();
    for (const f of history) for (const t of f.lastTransitions || []) m.set(`${t.tick}|${t.layer}|${t.to}|${t.index}`, t);
    return [...m.values()].sort((a, b) => b.tick - a.tick).slice(0, 200);
  }, [replay, history, stream.transitions]);

  useEffect(() => { if (!layerName && frame?.layers?.length) setLayerName(frame.layers[0].name); }, [frame, layerName]);
  // Esc / Backspace: up one sub-graph level (same as the editor); reads the current path through a ref so the hook
  // can sit before the early return
  const pathRef = useRef<string[]>([]);
  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      const el = e.target;
      if (el instanceof Element && el.closest('input, textarea, select, [contenteditable=true]')) return;
      if ((e.key === 'Escape' || e.key === 'Backspace') && pathRef.current.length > 0) { e.preventDefault(); setScopePath(pathRef.current.slice(0, -1)); }
    };
    window.addEventListener('keydown', k);
    return () => window.removeEventListener('keydown', k);
  }, []);
  if (!frame) return <Empty>{stream.error ? `No debug frame: ${stream.error}` : 'Waiting for the first debug frame...'}</Empty>;

  const layer = frame.layers.find(l => l.name === layerName) ?? frame.layers[0];
  const d = def.data;
  const path = scopePath ?? (layer ? layer.path.slice(0, -1) : []);
  // resolve the machine at the scope; the layer path may pass through `use` machines
  const resolved = d && layer ? resolveScope(d, layer.name, path) : null;
  const depth = path.length;
  pathRef.current = path;
  const frameNow = cursor == null && !replay && !frame.paused ? now : Date.parse(frame.t);
  const live: LiveInfo | undefined = layer ? {
    active: layer.path[depth] ?? null,
    activeSince: Date.parse(layer.enteredAtPath?.[depth] ?? layer.enteredAt ?? frame.t) + (cursor == null && !replay ? 0 : 0),
    conditions: layer.conditions.filter(c => (c.depth ?? 0) === depth || (c.depth == null && c.state === layer.path[depth])),
    flashes: transitions.filter(t => t.layer === layer.name && (t.depth ?? 0) === depth && frameNow - Date.parse(t.at) < 1600 && frameNow >= Date.parse(t.at)).map(t => ({ from: t.from, index: t.index ?? -1, at: Date.parse(t.at) })),
    claims: layer.claims,
    activeInner: layer.path.slice(depth + 1),
    now: frameNow,
    visited: Object.fromEntries(transitions.filter(t => t.layer === layer.name && frameNow - Date.parse(t.at) < 8000).map(t => [t.leafFrom ?? t.from, Date.parse(t.at)])),
  } : undefined;

  const jumpTo = (t: TransitionRecord) => {
    const i = history.findIndex(f => f.tick >= t.tick);
    if (i >= 0) setCursor(i);
    setLayerName(t.layer); setScopePath(null);
  };

  return (
    <div className="dbg">
      <div className="dbg-bar">
        {replay ? <span className="tag warn">replay: {replay.label}</span> : <span className={'tag ' + (stream.source === 'ws' ? 'ok' : 'warn')}>{stream.source === 'ws' ? 'live (ws)' : stream.source === 'poll' ? 'live (polling)' : 'no feed'}</span>}
        <span className="mono small">tick {frame.tick} · {fmtDate(frame.t)}</span>
        <span className="mono small muted" title="brain version">{frame.version}</span>
        {frame.paused && <span className="tag warn">brain paused</span>}
        <div className="grow" />
        {!replay && <>
          {frame.paused ? <ActionButton small onClick={() => api.resume(bot)}>resume</ActionButton> : <ActionButton small onClick={() => api.pause(bot)}>pause</ActionButton>}
          <ActionButton small disabled={!frame.paused} onClick={() => api.step(bot, 1)} title="run one tick while paused">step 1</ActionButton>
          <ActionButton small disabled={!frame.paused} onClick={() => api.step(bot, 20)}>step 20</ActionButton>
          <ForceState bot={bot} def={d ?? null} layers={frame.layers} onDone={() => toast('forced', 'ok')} />
        </>}
        {replay && <button className="btn sm" onClick={replay.onClose}>back to live</button>}
      </div>
      <div className="timeline">
        <input type="range" aria-label="timeline" min={0} max={Math.max(0, history.length - 1)} value={cursor ?? history.length - 1}
          onChange={e => { const v = Number(e.target.value); setCursor(!replay && v >= history.length - 1 ? null : v); }} />
        <span className="small mono">{history.length ? `${fmtTime(history[0].t)} .. ${fmtTime(history[history.length - 1].t)}` : ''}</span>
        {cursor != null && !replay && <button className="btn sm" onClick={() => setCursor(null)}>follow live</button>}
        <span className="muted small">{cursor == null ? 'following' : `frame ${cursor + 1}/${history.length}`}</span>
      </div>
      <div className="dbg-body">
        <aside className="dbg-layers">
          {frame.layers.map(l => <LayerCard key={l.name} l={l} on={l.name === layer?.name} frameT={frameNow} onClick={() => { setLayerName(l.name); setScopePath(null); setSel(null); }} />)}
          {frame.procedures.length > 0 && <div className="card"><div className="side-h">Procedures</div>
            {frame.procedures.map((p, i) => <div key={i} className="small"><span className="mono">{p.kind}</span> {p.step} <span className="muted">since {fmtDuration(frameNow - Date.parse(p.since))}</span></div>)}</div>}
          <div className="card small">
            <div className="side-h">Tick</div>
            <div>p50 {fmtNum(frame.tickStats.p50us)} us · p99 <span className={frame.tickStats.p99us > 1000 ? 'bad-text' : ''}>{fmtNum(frame.tickStats.p99us)} us</span></div>
            <StepBar steps={frame.tickStats.steps} />
          </div>
        </aside>
        <main className="dbg-graph">
          <div className="graph-bar">
            <div className="crumbs">
              <button className="linkish" onClick={() => setScopePath([])}>{layer?.name}</button>
              {path.map((p, i) => <span key={i}> / <button className="linkish" onClick={() => setScopePath(path.slice(0, i + 1))}>{p}</button></span>)}
            </div>
            <div className="grow" />
            <label className="check small"><input type="checkbox" checked={scopePath == null} onChange={e => setScopePath(e.target.checked ? null : path)} /> follow active state</label>
            {d && <a className="btn sm ghost" href={href(`/brains/${frame.version}`)}>open in editor</a>}
          </div>
          {def.error && <div className="banner err">definition: {def.error}</div>}
          {resolved ? (
            <BrainGraph def={d!} scope={resolved.scope} machine={resolved.machine} mode="live" selection={sel} onSelect={setSel} live={live} viewportKey={`live:${bot}:${layer?.name}:${path.join('/')}`}
              onOpenSub={s => setScopePath([...path, s])} />
          ) : <Empty>{d ? 'This state path is not in the definition.' : 'Loading the definition...'}</Empty>}
        </main>
        <aside className="dbg-side">
          <Tabs<Side> value={side} onChange={setSide} tabs={[
            { id: 'conditions', label: 'Conditions' }, { id: 'trace', label: `Trace (${transitions.length})` }, { id: 'variables', label: 'Variables' },
            { id: 'actions', label: 'Actions' }, { id: 'errors', label: `Errors${frame.errors.length ? ` (${frame.errors.length})` : ''}` }]} />
          <div className="dbg-side-body">
            {side === 'conditions' && layer && <Conditions layer={layer} frameT={frameNow} />}
            {side === 'trace' && (
              <>
                <div className="row"><select aria-label="trace layer" value={traceLayer} onChange={e => setTraceLayer(e.target.value)}><option value="">all layers</option>{frame.layers.map(l => <option key={l.name}>{l.name}</option>)}</select></div>
                <table className="trace">
                  <thead><tr><th>time</th><th>tick</th><th>layer</th><th>from &rarr; to</th><th>kind</th><th>reason / values</th></tr></thead>
                  <tbody>
                    {transitions.filter(t => !traceLayer || t.layer === traceLayer).map((t, i) => (
                      <tr key={i} onClick={() => jumpTo(t)} className="click" title="jump the timeline to this transition">
                        <td className="mono small" title={fmtDate(t.at)}>{fmtTime(t.at)}</td><td className="mono small">{t.tick}</td><td>{t.layer}</td>
                        <td className="mono small">{t.from} &rarr; {t.to}{t.index != null && t.index >= 0 ? <span className="muted"> #{t.index + 1}</span> : ''}</td>
                        <td><span className={'ekind k-' + t.kind}>{t.kind}</span></td>
                        <td className="small">{t.reason}{Object.keys(t.values || {}).length > 0 && <div className="muted mono">{Object.entries(t.values).map(([k, v]) => `${k}=${typeof v === 'number' ? +v.toFixed(2) : JSON.stringify(v)}`).join(' ')}</div>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </>
            )}
            {side === 'variables' && <Variables bot={bot} frame={frame} history={history} editable={!replay} />}
            {side === 'actions' && (
              <table className="trace">
                <thead><tr><th>id</th><th>kind</th><th>layer</th><th>state</th><th>code / reason</th><th>latency</th></tr></thead>
                <tbody>{frame.actions.map(a => (
                  <tr key={a.id}><td className="mono small">{a.id}</td><td className="mono small">{a.kind}</td><td>{a.layer}</td>
                    <td><span className={'astate ' + a.state}>{a.state}</span></td>
                    <td className="small">{a.code ? <span className="mono">{a.code}</span> : null} {a.reason}</td>
                    <td className="small mono">{a.resolvedAt ? fmtDuration(Date.parse(a.resolvedAt) - Date.parse(a.sentAt)) : `${fmtDuration(frameNow - Date.parse(a.sentAt))}...`}</td></tr>
                ))}</tbody>
              </table>
            )}
            {side === 'errors' && (frame.errors.length ? <ul className="errors">{frame.errors.map((e, i) => <li key={i}><span className="mono small">tick {e.tick} {e.layer}/{e.state}</span><div className="bad-text mono small">{e.error}</div></li>)}</ul> : <Empty>No errors.</Empty>)}
          </div>
        </aside>
      </div>
      <SensorStrip bot={bot} frame={replay ? replay.sensors[Math.min(cursor ?? 0, replay.sensors.length - 1)] : undefined} />
    </div>
  );
}

function SensorStrip({ bot, frame }: { bot: string; frame?: SensorFrame }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="sensor-strip">
      <button className="linkish" onClick={() => setOpen(o => !o)}>{open ? 'hide' : 'show'} sensor map</button>
      {open && <SensorView bot={bot} height={340} frame={frame} />}
    </div>
  );
}

function resolveScope(def: BrainDef, layerName: string, path: string[]): { scope: Scope; machine: MachineDef } | null {
  // walk the path; a `use` state switches to the named machine scope
  let scope: Scope = { kind: 'layer', layer: layerName, path: [] };
  let m = machineAt(def, scope);
  for (const p of path) {
    const st = m?.states[p];
    if (!st) return null;
    if (st.machine) { scope = { ...scope, path: [...scope.path, p] }; m = st.machine; }
    else if (st.use) {
      const name = def.machines?.[st.use] ? st.use : Object.keys(def.machines || {}).find(n => new RegExp('^' + st.use!.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\$\\\{[^}]+\\\}/g, '.+') + '$').test(n));
      if (!name) return null;
      scope = { kind: 'machine', name, path: [] }; m = def.machines![name];
    } else return null;
  }
  return m ? { scope, machine: m } : null;
}

function LayerCard({ l, on, onClick, frameT }: { l: LayerTrace; on: boolean; onClick: () => void; frameT: number }) {
  const since = l.enteredAt ? frameT - Date.parse(l.enteredAt) : l.timeInStateMs;
  const stall = l.progressBestAt ? frameT - Date.parse(l.progressBestAt) : null;
  const budget = l.budgetMs ?? null;
  return (
    <button className={'card layer-card' + (on ? ' on' : '')} onClick={onClick}>
      <div className="row"><strong>{l.name}</strong><span className="muted small">prio {l.priority}</span></div>
      <div className="mono">{l.path.join(' / ') || '-'}</div>
      <div className="small">in state {fmtDuration(since)} <span className="muted">(since {fmtDate(l.enteredAt)})</span></div>
      {stall != null && budget != null && (
        <div className="small" title="time since the progress expression last improved, against the state budget">
          progress {l.progress != null ? +Number(l.progress).toFixed(2) : '-'} · stall {fmtDuration(stall)} / {fmtDuration(budget)}
          <div className="bar"><div className={'fill ' + (stall / budget > 0.8 ? 'bad' : stall / budget > 0.5 ? 'warn' : '')} style={{ width: `${Math.min(100, (stall / budget) * 100)}%` }} /></div>
        </div>
      )}
      <div className="small">claims: {l.claims.length ? l.claims.map(c => <span key={c} className="tag">{c}</span>) : <span className="muted">none</span>}</div>
    </button>
  );
}

function Conditions({ layer, frameT }: { layer: LayerTrace; frameT: number }) {
  const byState = new Map<string, typeof layer.conditions>();
  for (const c of layer.conditions) { const k = `${c.depth ?? 0}|${c.state ?? layer.path.at(-1)}`; byState.set(k, [...(byState.get(k) || []), c]); }
  if (!layer.conditions.length) return <Empty>No outgoing transitions evaluated this tick.</Empty>;
  return (
    <div className="conds">
      {[...byState.entries()].map(([k, cs]) => (
        <div key={k} className="cond-group">
          <div className="side-h">{k.split('|')[1]} <span className="muted">depth {k.split('|')[0]}</span></div>
          {cs.map((c, i) => (
            <div key={i} className={'cond-row ' + (c.result === true ? 'true' : c.result === false ? 'false' : 'idle')}>
              <span className="cres">{c.result === true ? 'TRUE' : c.result === false ? 'false' : 'n/e'}</span>
              <span className="mono small">#{(c.index ?? i) + 1} &rarr; {c.to}</span>
              <span className={'ekind k-' + c.kind}>{c.kind}</span>
              <div className="mono small cexpr">{c.expr}{c.value != null && <> <span className="muted">=</span> <strong>{typeof c.value === 'number' ? +c.value.toFixed(2) : String(c.value)}</strong></>}</div>
              {!!c.holdTarget && <div className="small">hold {fmtDuration(c.holdMs)} / {fmtDuration(c.holdTarget)}<div className="bar"><div className="fill" style={{ width: `${Math.min(100, (c.holdMs / c.holdTarget) * 100)}%` }} /></div></div>}
            </div>
          ))}
        </div>
      ))}
      <div className="hint">n/e = not evaluated (an earlier transition fired, or the layer was not due). Frame time {new Date(frameT).toLocaleTimeString()}.</div>
    </div>
  );
}

function Variables({ bot, frame, history, editable }: { bot: string; frame: DebugFrame; history: DebugFrame[]; editable: boolean }) {
  const { toast } = useApp();
  const [edit, setEdit] = useState<{ scope: string; owner?: string; key: string; value: string } | null>(null);
  const series = (k: string) => history.slice(-120).map(f => f.watches?.[k]).filter((v): v is number => typeof v === 'number');
  const scopes: { scope: string; owner?: string; vals: Record<string, unknown> }[] = [
    { scope: 'bot', vals: frame.blackboard.bot },
    ...Object.entries(frame.blackboard.layer).map(([o, v]) => ({ scope: 'layer', owner: o, vals: v })),
    ...Object.entries(frame.blackboard.state).map(([o, v]) => ({ scope: 'state', owner: o, vals: v })),
  ];
  return (
    <div className="vars">
      <div className="side-h">Watches (dbg.watch)</div>
      <table className="kv">
        <tbody>{Object.entries(frame.watches).map(([k, v]) => <tr key={k}><td className="mono">{k}</td><td><JsonValue v={v} /></td><td><Sparkline values={series(k)} /></td></tr>)}</tbody>
      </table>
      {!Object.keys(frame.watches).length && <div className="muted small">none</div>}
      {scopes.map(s => (
        <div key={s.scope + (s.owner ?? '')}>
          <div className="side-h">bb.{s.scope}{s.owner ? ` [${s.owner}]` : ''}{editable && <button className="btn ghost sm" onClick={() => setEdit({ scope: s.scope, owner: s.owner, key: '', value: '' })}>+ set</button>}</div>
          <table className="kv"><tbody>
            {Object.entries(s.vals).map(([k, v]) => (
              <tr key={k}><td className="mono">{k}</td><td><JsonValue v={v} /></td>
                <td>{editable && <button className="btn ghost sm" onClick={() => setEdit({ scope: s.scope, owner: s.owner, key: k, value: JSON.stringify(v) })}>edit</button>}</td></tr>
            ))}
            {!Object.keys(s.vals).length && <tr><td className="muted small" colSpan={3}>empty</td></tr>}
          </tbody></table>
        </div>
      ))}
      {edit && (
        <div className="card">
          <div className="side-h">set bb.{edit.scope}{edit.owner ? ` [${edit.owner}]` : ''}</div>
          <Field label="key"><input aria-label="bb key" value={edit.key} onChange={e => setEdit({ ...edit, key: e.target.value })} /></Field>
          <Field label="value (JSON)"><input aria-label="bb value" value={edit.value} onChange={e => setEdit({ ...edit, value: e.target.value })} /></Field>
          <div className="row">
            <ActionButton kind="primary" small disabled={!edit.key} onClick={async () => { await api.setBlackboard(bot, edit.scope, edit.key, edit.value, edit.owner); toast(`bb.${edit.scope}.${edit.key} set`, 'ok'); setEdit(null); }}>set</ActionButton>
            <button className="btn sm ghost" onClick={() => setEdit(null)}>cancel</button>
          </div>
        </div>
      )}
    </div>
  );
}

function ForceState({ bot, def, layers, onDone }: { bot: string; def: BrainDef | null; layers: LayerTrace[]; onDone: () => void }) {
  const [layer, setLayer] = useState('');
  const [state, setState] = useState('');
  const states = def?.layers.find(l => l.name === layer)?.machine.states ?? {};
  return (
    <span className="row force">
      <select aria-label="force layer" value={layer} onChange={e => { setLayer(e.target.value); setState(''); }}><option value="">force state...</option>{layers.map(l => <option key={l.name}>{l.name}</option>)}</select>
      {layer && <select aria-label="force state" value={state} onChange={e => setState(e.target.value)}><option value="">state</option>{Object.keys(states).map(s => <option key={s}>{s}</option>)}</select>}
      {layer && state && <ActionButton small kind="primary" onClick={async () => { await api.forceState(bot, layer, state); setLayer(''); setState(''); onDone(); }}>force</ActionButton>}
    </span>
  );
}

export function StepBar({ steps }: { steps: Record<string, number> }) {
  const total = Object.values(steps).reduce((a, b) => a + (b || 0), 0) || 1;
  const colors = ['#4e9fd1', '#37b29a', '#c9a640', '#b46fd8', '#d46a6a', '#7f9f5a', '#888'];
  return (
    <div>
      <div className="stepbar">{Object.entries(steps).map(([k, v], i) => <div key={k} title={`${k} ${fmtNum(v, 1)} us`} style={{ width: `${(v / total) * 100}%`, background: colors[i % colors.length] }} />)}</div>
      <div className="steplegend">{Object.entries(steps).map(([k, v], i) => <span key={k}><i style={{ background: colors[i % colors.length] }} />{k} {fmtNum(v, 0)}</span>)}</div>
    </div>
  );
}
