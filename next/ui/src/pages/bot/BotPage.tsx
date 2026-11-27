import { useEffect, useMemo, useRef, useState } from 'react';
import { api, normDebugFrame } from '../../api/client';
import type { BotSummary, DebugFrame, LogLine, SensorFrame, TickSample } from '../../api/types';
import { useHostState, useTopic, useTopicEvents, useWsStatus } from '../../api/ws';
import { ActionButton, ConfirmButton, Empty, Field, StateBadge, Tabs, fmtDate, fmtNum, fmtTime, useAsync } from '../../components/ui';
import { useApp } from '../../lib/app';
import { go, href } from '../../lib/router';
import { BotConfigModal } from '../Fleet';
import { DebuggerTab, StepBar } from './Debugger';
import { SensorView } from './SensorView';
import { GameTab } from './game/GameTab';
import { ErrorBoundary } from '../../components/ErrorBoundary';
import { ConnOverlay } from '../../components/ConnOverlay';

type Tab = 'game' | 'debugger' | 'sensors' | 'actions' | 'log' | 'packets' | 'inventory' | 'quests' | 'skills' | 'metrics' | 'versions' | 'replay';
const TABS: { id: Tab; label: string }[] = [
  { id: 'game', label: 'Game' },
  { id: 'debugger', label: 'Brain debugger' }, { id: 'sensors', label: 'Sensors' }, { id: 'actions', label: 'Actions' }, { id: 'log', label: 'Log' },
  { id: 'packets', label: 'Packets' }, { id: 'inventory', label: 'Inventory' }, { id: 'quests', label: 'Quests' }, { id: 'skills', label: 'Skills' },
  { id: 'metrics', label: 'Metrics' }, { id: 'versions', label: 'Versions' }, { id: 'replay', label: 'Replay' },
];

export function BotPage({ id, tab }: { id: string; tab?: string }) {
  const t = (TABS.find(x => x.id === tab)?.id ?? 'game') as Tab;
  const fleet = useTopic<BotSummary[]>({ topic: 'fleet' });
  const polled = useAsync(() => api.bot(id), [id]);
  const b = fleet?.find(x => x.id === id) ?? polled.data;
  const ws = useWsStatus();
  const hostState = useHostState();
  const [edit, setEdit] = useState(false);
  const [replay, setReplay] = useState<{ frames: DebugFrame[]; sensors: SensorFrame[]; label: string } | null>(null);
  if (!b) return <div className="page"><Empty>{polled.error ? polled.error : 'Loading...'}</Empty></div>;
  const v = b.vitals;
  const offline = b.state === 'Offline';
  return (
    <div className="page bot-page">
      {/* Two fixed rows that never wrap and never appear / disappear: live numbers changing width (position,
          money) used to re-wrap this header and shift the whole page up and down. */}
      <div className="bot-head">
        <div className="bot-head-row">
          <h2>{b.id}</h2>
          <StateBadge state={b.paused ? 'Paused' : b.state} />
          <span className="badge wire" title="packet set">wire {b.wire ?? '?'}</span>
          <span className="nowrap">lvl <strong className="num-tab">{b.level || '-'}</strong></span>
          <span className="clip" title={v ? `${b.map ?? '-'} (${v.x}, ${v.y})` : b.map ?? ''}>{b.map ?? '-'} <span className="mono muted num-tab">{v ? `(${v.x}, ${v.y})` : ''}</span></span>
          <span className="small clip">{b.brain ? <a href={href(`/brains/${b.brain}${b.versionHash ? '@' + b.versionHash : ''}`)}>{b.brain}@{b.versionHash?.slice(0, 8)}</a> : 'no brain'}{b.channel && <span className="muted"> ({b.channel})</span>}</span>
          <div className="grow" />
          {offline ? <ActionButton small onClick={() => api.start(id)}>start</ActionButton> : <ActionButton small onClick={() => api.stop(id)}>stop</ActionButton>}
          <ActionButton small disabled={offline} onClick={() => api.relog(id)}>relog</ActionButton>
          <button className="btn sm ghost" onClick={() => setEdit(true)}>edit config</button>
        </div>
        <div className="bot-head-row vitals-row">
          <Meter label="HP" v={v?.hp ?? 0} max={v?.maxHp ?? 0} cls="hp" />
          <Meter label="SP" v={v?.sp ?? 0} max={v?.maxSp ?? 0} cls="sp" />
          {/* progress within the level, like the game (and the Game tab): (exp - level start) / (next - level start) */}
          <Meter label="EXP" v={(v?.exp ?? 0) - (v?.expPrev ?? 0)} max={(v?.expNext ?? 0) - (v?.expPrev ?? 0)} cls="xp" />
          <span title="money" className="num-tab nowrap">{v ? fmtNum(v.money) : '-'} cen</span>
          <span title={v ? `bag slots used / size (${v.bagFree} free)` : 'bag'} className="num-tab nowrap">bag {v ? `${v.bagSize - v.bagFree}/${v.bagSize}` : '-'}</span>
        </div>
      </div>
      <Tabs<Tab> tabs={TABS} value={t} onChange={x => go(`/bots/${encodeURIComponent(id)}/${x}`)} />
      <div className="bot-body">
        <ErrorBoundary resetKey={t}>
        {offline && !['game', 'versions', 'log', 'packets', 'replay'].includes(t) ? <Empty>The bot is offline. Start it to see live data.</Empty> : <>
          {t === 'game' && <GameTab bot={id} botState={b.state} />}
          {t === 'debugger' && (replay ? <DebuggerTab bot={id} replay={{ ...replay, onClose: () => setReplay(null) }} /> : <DebuggerTab bot={id} />)}
          {t === 'sensors' && <SensorView bot={id} />}
          {t === 'actions' && <ActionsTab bot={id} />}
          {t === 'log' && <LogTab bot={id} />}
          {t === 'packets' && <PacketsTab bot={id} />}
          {t === 'inventory' && <InventoryTab bot={id} />}
          {t === 'quests' && <QuestsTab bot={id} />}
          {t === 'skills' && <SkillsTab bot={id} />}
          {t === 'metrics' && <MetricsTab bot={id} />}
          {t === 'versions' && <VersionsTab bot={id} b={b} />}
          {t === 'replay' && <ReplayTab bot={id} onLoad={r => { setReplay(r); go(`/bots/${encodeURIComponent(id)}/debugger`); }} />}
        </>}
        </ErrorBoundary>
        {['debugger', 'sensors', 'actions'].includes(t) && !offline && <ConnOverlay down={hostState === 'restarting' || ws !== 'open' || !/InZone|Paused/i.test(b.state)} reason={hostState === 'restarting' ? 'host restarting / reconnecting' : ws !== 'open' ? 'connection to the host lost, reconnecting' : `bot is ${b.state}`} />}
      </div>
      {edit && <BotConfigModal bot={b} onClose={() => { setEdit(false); polled.reload(); }} />}
    </div>
  );
}

function Meter({ label, v, max, cls }: { label: string; v: number; max: number; cls: string }) {
  const p = max > 0 ? Math.max(0, Math.min(100, (v / max) * 100)) : 0;
  return <span className="meter" title={`${label} ${fmtNum(v)} / ${fmtNum(max)}`}><span className="meter-l">{label}</span><span className={'meter-bar ' + cls}><span style={{ width: `${p}%` }} /></span><span className="small mono">{p.toFixed(0)}%</span></span>;
}

function ActionsTab({ bot }: { bot: string }) {
  const f = useTopic<DebugFrame>({ topic: 'debug', bot }, normDebugFrame);
  const { manifest, toast } = useApp();
  const [kind, setKind] = useState('');
  const [args, setArgs] = useState('{}');
  const frame = f;
  return (
    <div className="split">
      <div>
        <h3>Pending and resolved actions</h3>
        {!frame ? <Empty>waiting for a frame</Empty> : (
          <table className="trace">
            <thead><tr><th>id</th><th>kind</th><th>layer</th><th>state</th><th>code</th><th>reason</th><th>sent</th><th>latency</th></tr></thead>
            <tbody>{frame.actions.map(a => <tr key={a.id}><td className="mono small">{a.id}</td><td className="mono">{a.kind}</td><td>{a.layer}</td><td><span className={'astate ' + String(a.state).toLowerCase()}>{String(a.state).toLowerCase()}</span></td>
              <td className="mono">{a.code || ''}</td><td>{a.reason}</td><td className="mono small">{fmtTime(a.sentAt)}</td><td className="mono small">{a.resolvedAt ? `${Date.parse(a.resolvedAt) - Date.parse(a.sentAt)} ms` : 'pending'}</td></tr>)}</tbody>
          </table>
        )}
        <h3>Procedures</h3>
        {frame?.procedures?.length ? <ul>{frame.procedures.map((p, i) => <li key={i}><span className="mono">{p.kind}</span> step <strong>{p.step}</strong> since {fmtDate(p.since)}</li>)}</ul> : <Empty>none running</Empty>}
      </div>
      <div className="card">
        <h3>Operator action</h3>
        <p className="hint">Runs in the operator layer (highest priority); it claims its resource until released.</p>
        <Field label="kind"><select aria-label="operator action kind" value={kind} onChange={e => setKind(e.target.value)}><option value="">choose...</option>{(manifest?.actionKinds ?? []).map(k => <option key={k}>{k}</option>)}</select></Field>
        <Field label="args (JSON object of strings)"><input aria-label="operator action args" value={args} onChange={e => setArgs(e.target.value)} /></Field>
        <ActionButton kind="primary" small disabled={!kind} onClick={async () => { const r: any = await api.operatorAction(bot, kind, JSON.parse(args || '{}')); toast(`action ${r?.id ?? ''} ${r?.state ?? 'queued'}`, 'ok'); }}>send</ActionButton>
      </div>
    </div>
  );
}

const LEVELS = ['crit', 'note', 'info', 'debug', 'trace'];
const LEVEL_OF: Record<string, string> = { C: 'crit', N: 'note', I: 'info', D: 'debug', T: 'trace' };
const lvl = (l: string) => LEVEL_OF[l] ?? String(l).toLowerCase();

function LogTab({ bot }: { bot: string }) {
  const [level, setLevel] = useState('info');
  const [category, setCategory] = useState('');
  const [text, setText] = useState('');
  const [from, setFrom] = useState('');
  const [follow, setFollow] = useState(true);
  const [lines, setLines] = useState<LogLine[]>([]);
  const levels = useAsync(() => api.logLevels(bot), [bot]);
  const box = useRef<HTMLDivElement>(null);
  const { toast } = useApp();
  const load = async () => {
    try { setLines(await api.log(bot, { level, category: category || undefined, text: text || undefined, from: from ? new Date(from).toISOString() : undefined, max: 1000 })); }
    catch (e: any) { toast(e.message, 'error'); }
  };
  useEffect(() => { load(); }, [bot, level, category]); // eslint-disable-line react-hooks/exhaustive-deps
  useTopicEvents(follow ? { topic: 'log', bot, level } : null, (e: LogLine) => {
    if (category && e.category !== category) return;
    if (text && !e.text.toLowerCase().includes(text.toLowerCase())) return;
    setLines(ls => [...ls.slice(-1999), e]);
  });
  useEffect(() => { if (follow && box.current) box.current.scrollTop = box.current.scrollHeight; }, [lines, follow]);
  const cats = Object.keys(levels.data ?? {});
  return (
    <div className="split wide-left">
      <div>
        <div className="row wrap">
          <select aria-label="log level" value={level} onChange={e => setLevel(e.target.value)}>{LEVELS.map(l => <option key={l}>{l}</option>)}</select>
          <select aria-label="log category" value={category} onChange={e => setCategory(e.target.value)}><option value="">all categories</option>{cats.map(c => <option key={c}>{c}</option>)}</select>
          <input aria-label="log text filter" placeholder="text" value={text} onChange={e => setText(e.target.value)} onKeyDown={e => e.key === 'Enter' && load()} />
          <input aria-label="log from" type="datetime-local" value={from} onChange={e => setFrom(e.target.value)} />
          <button className="btn sm" onClick={load}>query</button>
          <label className="check small"><input type="checkbox" checked={follow} onChange={e => setFollow(e.target.checked)} /> follow live</label>
          <span className="muted small">{lines.length} lines</span>
        </div>
        <div className="logbox" ref={box}>
          {lines.map((l, i) => (
            <div key={i} className={'logline l-' + lvl(l.level)}>
              <span className="mono muted">{fmtDate(l.t)}</span> <span className="lvl">{lvl(l.level).toUpperCase().slice(0, 4)}</span> {l.category && <span className="cat">{l.category}</span>} {l.text}
              {l.repeat && l.repeat > 1 ? <span className="muted"> (x {l.repeat})</span> : null}
            </div>
          ))}
          {!lines.length && <div className="muted">no lines at this level</div>}
        </div>
      </div>
      <div className="card">
        <h3>Log levels</h3>
        <p className="hint">Per category, at runtime, persisted with the bot. An expiry reverts a forgotten debug level.</p>
        <table className="kv"><tbody>
          {Object.entries(levels.data ?? {}).map(([c, v]) => (
            <tr key={c}><td className="mono">{c}</td>
              <td><select aria-label={`level ${c}`} value={v.level.toLowerCase()} onChange={async e => { try { await api.setLogLevel(bot, c, e.target.value, e.target.value === 'debug' || e.target.value === 'trace' ? 15 : undefined); levels.reload(); } catch (er: any) { toast(er.message, 'error'); } }}>
                {LEVELS.map(l => <option key={l}>{l}</option>)}</select></td>
              <td className="small muted">{v.expiresAt ? `until ${fmtDate(v.expiresAt, false)}` : ''}</td></tr>
          ))}
        </tbody></table>
        <p className="hint">debug / trace are set for 15 min.</p>
      </div>
    </div>
  );
}

function PacketsTab({ bot }: { bot: string }) {
  const pc = useAsync(() => api.packetCapture(bot), [bot]);
  const [opcodes, setOpcodes] = useState('');
  const [minutes, setMinutes] = useState(15);
  const [maxMb, setMaxMb] = useState(64);
  const [format, setFormat] = useState('text');
  const { toast } = useApp();
  const parseList = (s: string) => s.split(/[ ,]+/).filter(Boolean).map(x => (x.startsWith('0x') ? parseInt(x, 16) : Number(x))).filter(n => !isNaN(n));
  return (
    <div className="split">
      <div className="card">
        <h3>Packet capture</h3>
        <p className="hint">Off by default; with an opcode filter, a duration (auto-off) and a size cap. The setting survives relogs.</p>
        <div>State: {pc.data?.enabled ? <span className="tag ok">capturing{pc.data.until ? ` until ${fmtDate(pc.data.until)}` : ''}</span> : <span className="tag">off</span>}</div>
        <div className="grid2">
          <Field label="opcodes" hint="hex or decimal, comma separated; empty = all"><input aria-label="opcodes" value={opcodes} onChange={e => setOpcodes(e.target.value)} placeholder="0x2001, 0x1C09" /></Field>
          <Field label="format"><select aria-label="capture format" value={format} onChange={e => setFormat(e.target.value)}><option>text</option><option>binary</option></select></Field>
          <Field label="minutes"><input aria-label="capture minutes" type="number" value={minutes} onChange={e => setMinutes(Number(e.target.value))} /></Field>
          <Field label="max MB"><input aria-label="capture max MB" type="number" value={maxMb} onChange={e => setMaxMb(Number(e.target.value))} /></Field>
        </div>
        <div className="row">
          <ActionButton kind="primary" small onClick={async () => { await api.setPacketCapture(bot, { enabled: true, opcodes: parseList(opcodes), minutes, maxMb, format }); pc.reload(); }} ok="capture on">start capture</ActionButton>
          <ActionButton small onClick={async () => { await api.setPacketCapture(bot, { enabled: false }); pc.reload(); }} ok="capture off">stop</ActionButton>
          <ActionButton small onClick={async () => { await api.packetlog(bot, true); toast('packet log enabled (persisted)', 'ok'); }}>packet log on</ActionButton>
          <ActionButton small onClick={async () => { await api.packetlog(bot, false); toast('packet log disabled', 'ok'); }}>off</ActionButton>
        </div>
      </div>
      <div className="card">
        <h3>Capture files</h3>
        {pc.data?.files?.length ? <table className="kv"><tbody>{pc.data.files.map(f => <tr key={f.name}><td className="mono small">{f.name}</td><td>{fmtNum(f.sizeBytes)} B</td><td className="small">{fmtDate(f.startedAt)}</td></tr>)}</tbody></table> : <Empty>none</Empty>}
      </div>
    </div>
  );
}

function NotProvided({ error }: { error: string }) {
  return <Empty>{/404|no route/i.test(error) ? 'This host does not provide this view yet (see docs/API_SPEC.md, UI requests).' : error}</Empty>;
}

function InventoryTab({ bot }: { bot: string }) {
  const d = useAsync(() => api.inventory(bot), [bot]);
  if (d.error) return <NotProvided error={d.error} />;
  if (!d.data) return <Empty>loading</Empty>;
  return (
    <div className="split">
      <div><h3>Bag ({d.data.slots?.length ?? 0} / {d.data.bagSize})</h3>
        <table className="trace"><thead><tr><th>slot</th><th>item</th><th>id</th><th>lot</th><th>category</th></tr></thead>
          <tbody>{(d.data.slots || []).map((s: any) => <tr key={s.slot}><td>{s.slot}</td><td>{s.name}</td><td className="mono">{s.itemId}</td><td>{s.lot}</td><td>{s.category}</td></tr>)}</tbody></table></div>
      <div><h3>Equipped</h3><table className="trace"><tbody>{(d.data.equipped || []).map((e: any) => <tr key={e.equipSlot}><td>{e.equipSlot}</td><td>{e.name}</td><td className="mono">{e.itemId}</td></tr>)}</tbody></table>
        <p>money {fmtNum(d.data.money)}</p></div>
    </div>
  );
}

function QuestsTab({ bot }: { bot: string }) {
  const d = useAsync(() => api.quests(bot), [bot]);
  if (d.error) return <NotProvided error={d.error} />;
  return (
    <table className="trace"><thead><tr><th>quest</th><th>id</th><th>state</th><th>objectives</th></tr></thead>
      <tbody>{(d.data || []).map((q: any) => <tr key={q.id}><td>{q.name}</td><td className="mono">{q.id}</td><td>{q.state}</td>
        <td>{(q.objectives || []).map((o: any, i: number) => <div key={i} className="small">{o.kind} {o.mobId >= 0 ? `mob ${o.mobId}` : ''}{o.itemId >= 0 ? ` item ${o.itemId}` : ''}: {o.have}/{o.need}</div>)}</td></tr>)}</tbody></table>
  );
}

function SkillsTab({ bot }: { bot: string }) {
  const d = useAsync(() => api.skills(bot), [bot]);
  if (d.error) return <NotProvided error={d.error} />;
  return (
    <table className="trace"><thead><tr><th>skill</th><th>id</th><th>rank</th><th>tags</th><th>ready in</th><th>SP</th><th>canUse (self)</th></tr></thead>
      <tbody>{(d.data || []).map((s: any) => <tr key={s.id}><td>{s.name}</td><td className="mono">{s.id}</td><td>{s.rank}</td><td>{s.tags}</td><td>{s.readyInMs ? `${s.readyInMs} ms` : 'ready'}</td><td>{s.spCost}</td>
        <td>{s.canUse ? <span className="true">yes</span> : <span className="false">no: {s.whyNot}</span>}</td></tr>)}</tbody></table>
  );
}

function MetricsTab({ bot }: { bot: string }) {
  const [gen, setGen] = useState(0);
  const d = useAsync<TickSample[]>(() => api.ticks(bot, 1000), [bot, gen]);
  useEffect(() => { const t = window.setInterval(() => setGen(g => g + 1), 3000); return () => window.clearInterval(t); }, []);
  const samples = d.data ?? [];
  const stats = useMemo(() => {
    const tot = samples.map(s => s.totalUs).sort((a, b) => a - b);
    const q = (p: number) => tot.length ? tot[Math.min(tot.length - 1, Math.floor(p * tot.length))] : 0;
    const avg = (k: keyof TickSample) => samples.length ? samples.reduce((a, s) => a + (s[k] as number), 0) / samples.length : 0;
    return { p50: q(0.5), p99: q(0.99), max: tot.at(-1) ?? 0, alloc: Math.max(0, ...samples.map(s => s.allocBytes)),
      steps: { decode: avg('decodeUs'), apply: avg('applyUs'), actions: avg('actionsUs'), move: avg('moveUs'), combat: avg('combatUs'), brain: avg('brainUs'), flush: avg('flushUs') } };
  }, [samples]);
  if (d.error) return <NotProvided error={d.error} />;
  return (
    <div>
      <div className="row wrap">
        <span>last {samples.length} ticks</span><span>p50 <strong>{fmtNum(stats.p50)} us</strong></span>
        <span>p99 <strong className={stats.p99 > 1000 ? 'bad-text' : ''}>{fmtNum(stats.p99)} us</strong></span><span>max {fmtNum(stats.max)} us</span>
        <span>max alloc {fmtNum(stats.alloc)} B/tick</span><span className="muted">budget p99 1000 us, hard cap 5 ms</span>
      </div>
      <h3>Flame strip (per tick, stacked by step)</h3>
      <FlameStrip samples={samples} />
      <h3>Average step breakdown</h3>
      <StepBar steps={stats.steps} />
    </div>
  );
}

function FlameStrip({ samples }: { samples: TickSample[] }) {
  const c = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const el = c.current; if (!el) return;
    const dpr = window.devicePixelRatio || 1; const w = el.clientWidth, h = el.clientHeight;
    el.width = w * dpr; el.height = h * dpr;
    const g = el.getContext('2d')!; g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, w, h);
    const max = Math.max(1000, ...samples.map(s => s.totalUs));
    const keys: (keyof TickSample)[] = ['decodeUs', 'applyUs', 'actionsUs', 'moveUs', 'combatUs', 'brainUs', 'flushUs'];
    const colors = ['#4e9fd1', '#37b29a', '#c9a640', '#b46fd8', '#d46a6a', '#7f9f5a', '#888'];
    const bw = w / Math.max(1, samples.length);
    samples.forEach((s, i) => {
      let y = h;
      keys.forEach((k, j) => { const v = (s[k] as number) / max * h; g.fillStyle = colors[j]; g.fillRect(i * bw, y - v, Math.max(1, bw), v); y -= v; });
      const rest = (s.totalUs - keys.reduce((a, k) => a + (s[k] as number), 0)) / max * h;
      if (rest > 0) { g.fillStyle = '#333'; g.fillRect(i * bw, y - rest, Math.max(1, bw), rest); }
    });
    const by = h - (1000 / max) * h; g.strokeStyle = '#d65c5c'; g.setLineDash([4, 4]); g.beginPath(); g.moveTo(0, by); g.lineTo(w, by); g.stroke(); g.setLineDash([]);
    g.fillStyle = '#d65c5c'; g.font = '10px system-ui'; g.fillText('1 ms budget', 4, by - 3);
  }, [samples]);
  return <canvas ref={c} className="flame" aria-label="tick flame strip" />;
}

function VersionsTab({ bot, b }: { bot: string; b: BotSummary }) {
  const a = useAsync(() => api.assignments(bot), [bot]);
  const { toast } = useApp();
  return (
    <div>
      <div className="row">
        <span>current: {b.brain ? <a href={href(`/brains/${b.brain}@${b.versionHash}`)} className="mono">{b.brain}@{b.versionHash}</a> : 'none'}{b.channel && ` (channel ${b.channel})`}</span>
        <div className="grow" />
        <ConfirmButton kind="primary" small onConfirm={async () => { const r = await api.rollback(bot); toast(`rolled back to ${r.toHash}`, 'ok'); a.reload(); }}>roll back to previous</ConfirmButton>
      </div>
      <h3>Assignment timeline</h3>
      {a.error ? <NotProvided error={a.error} /> : (
        <ol className="timeline-list">
          {(a.data || []).map((r, i) => (
            <li key={i}>
              <span className="mono small">{fmtDate(r.at)}</span> <span className="mono">{r.fromHash ?? '-'} &rarr; {r.toHash}</span> {r.brain && <span className="muted">{r.brain}</span>}
              <div className="small">by {r.by}: {r.reason || '-'} · keep state {r.keptState ? 'yes' : 'no'}
                {r.fromHash && <> · <ConfirmButton small kind="ghost" onConfirm={async () => { await api.rollback(bot, r.fromHash!); toast(`re-applied ${r.fromHash}`, 'ok'); a.reload(); }}>roll back to {r.fromHash.slice(0, 8)}</ConfirmButton></>}
                {r.brain && r.fromHash && <> · <a href={href(`/versions/${r.brain}?from=${r.fromHash}&to=${r.toHash}`)}>diff</a></>}</div>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function ReplayTab({ bot, onLoad }: { bot: string; onLoad: (r: { frames: DebugFrame[]; sensors: SensorFrame[]; label: string }) => void }) {
  const recs = useAsync(() => api.recordings(bot), [bot]);
  const { toast } = useApp();
  if (recs.error) return <NotProvided error={recs.error} />;
  return (
    <div>
      <p className="hint">A recorded session is loaded into the same debugger views and scrubbed with the timeline.</p>
      {!recs.data?.length ? <Empty>No recordings.</Empty> : (
        <table className="trace"><thead><tr><th>recording</th><th>from</th><th>to</th><th>frames</th><th /></tr></thead>
          <tbody>{recs.data.map(r => <tr key={r.id}><td className="mono">{r.id}</td><td>{fmtDate(r.startedAt)}</td><td>{fmtDate(r.endedAt)}</td><td>{r.frames ?? r.ticks ?? '-'}</td>
            <td><ActionButton small onClick={async () => { const f = await api.recordingFrames(bot, r.id); if (!f.debug.length) { toast('recording has no frames', 'error'); return; } onLoad({ frames: f.debug, sensors: f.sensors, label: `${r.id} ${fmtDate(r.startedAt)}` }); }}>load into debugger</ActionButton></td></tr>)}</tbody></table>
      )}
    </div>
  );
}
