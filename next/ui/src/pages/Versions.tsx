import { useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import type { BrainVersionFull, VersionMetrics } from '../api/types';
import { DiffView } from '../components/CodeEditor';
import { Empty, fmtDate, fmtNum, useAsync } from '../components/ui';
import { useApp } from '../lib/app';
import { go, href, useQuery } from '../lib/router';
import { parseDefinition, serializeDefinition } from '../lib/brain';
import { ApplyModal } from './BrainEditor';

export function VersionsPage({ brain }: { brain: string }) {
  const q = useQuery();
  const versions = useAsync(() => api.versions(brain), [brain]);
  const brains = useAsync(() => api.brains(), [brain]);
  const assignments = useAsync(() => api.brainAssignments(brain), [brain]);
  const list = versions.data ?? [];
  const to = q.get('to') ?? list[0]?.hash;
  const from = q.get('from') ?? list[list.findIndex(v => v.hash === to) + 1]?.hash ?? list[1]?.hash;
  const channels = brains.data?.find(b => b.name === brain)?.channels ?? {};
  const { toast } = useApp();
  const [apply, setApply] = useState<string | null>(null);
  const setQ = (f?: string, t?: string) => go(`/versions/${brain}?${f ? `from=${f}&` : ''}${t ? `to=${t}` : ''}`);
  if (versions.error) return <div className="page"><Empty>{versions.error}</Empty></div>;
  return (
    <div className="page">
      <div className="page-head"><h2>Versions: <a href={href(`/brains/${brain}`)}>{brain}</a></h2></div>
      <div className="split wide-right">
        <div>
          <h3>Versions</h3>
          <table className="trace">
            <thead><tr><th>from</th><th>to</th><th>hash</th><th>created</th><th>author</th><th>message</th><th>channels</th><th /></tr></thead>
            <tbody>{list.map(v => (
              <tr key={v.hash} className={v.hash === to || v.hash === from ? 'sel' : ''}>
                <td><input type="radio" aria-label={`diff from ${v.hash}`} checked={v.hash === from} onChange={() => setQ(v.hash, to)} /></td>
                <td><input type="radio" aria-label={`diff to ${v.hash}`} checked={v.hash === to} onChange={() => setQ(from, v.hash)} /></td>
                <td className="mono"><a href={href(`/brains/${brain}@${v.hash}`)}>{v.hash}</a>{v.warnings ? <span className="tag warn" title="lint warnings">{v.warnings}w</span> : null}</td>
                <td className="small">{fmtDate(v.created)}</td><td>{v.author}</td><td className="small">{v.message}</td>
                <td>{Object.entries(channels).filter(([, h]) => h === v.hash).map(([c]) => <span key={c} className="tag ok">{c}</span>)}</td>
                <td className="actions">
                  <select aria-label={`move channel to ${v.hash}`} value="" onChange={async e => { const c = e.target.value; if (!c) return; try { await api.setChannel(brain, c, v.hash); toast(`${c} -> ${v.hash}`, 'ok'); brains.reload(); } catch (er: any) { toast(er.message, 'error'); } }}>
                    <option value="">channel...</option>{[...new Set([...Object.keys(channels), 'stable', 'candidate', 'operator-test'])].map(c => <option key={c} value={c}>{c}</option>)}
                  </select>
                  <button className="btn sm" onClick={() => setApply(v.hash)}>apply</button>
                </td>
              </tr>))}
            </tbody>
          </table>
          <Metrics brain={brain} versions={[from, to].filter(Boolean) as string[]} />
        </div>
        <div>
          <h3>Assignment timeline</h3>
          {assignments.error ? <Empty>{assignments.error}</Empty> : (
            <ol className="timeline-list">{(assignments.data || []).map((a, i) => (
              <li key={i}><span className="mono small">{fmtDate(a.at)}</span> <a href={href(`/bots/${encodeURIComponent(a.bot)}/versions`)}>{a.bot}</a>
                <span className="mono small"> {a.fromHash ?? '-'} &rarr; {a.toHash}</span><div className="small muted">{a.by}: {a.reason || '-'}{a.keptState ? ' · kept state' : ''}</div></li>
            ))}{!assignments.data?.length && <li className="muted">none</li>}</ol>
          )}
        </div>
      </div>
      {from && to && from !== to ? <VersionDiff brain={brain} from={from} to={to} /> : <Empty>Pick two versions to compare.</Empty>}
      {apply && <ApplyModal brain={brain} hash={apply} onClose={() => { setApply(null); assignments.reload(); brains.reload(); }} />}
    </div>
  );
}

function VersionDiff({ brain, from, to }: { brain: string; from: string; to: string }) {
  const diff = useAsync(() => api.diff(brain, from, to), [brain, from, to]);
  const a = useAsync<BrainVersionFull>(() => api.version(brain, from), [brain, from]);
  const b = useAsync<BrainVersionFull>(() => api.version(brain, to), [brain, to]);
  const [file, setFile] = useState('brain.yaml');
  const [normalize, setNormalize] = useState(true);
  const sources = (v?: BrainVersionFull) => {
    let defText = v?.definition ?? '';
    // hand-written and UI-saved YAML differ in layout: diff the parsed model re-serialised, without node positions
    if (normalize && v) { try { const d = parseDefinition(defText); delete d.ui; defText = serializeDefinition(d); } catch { /* keep raw */ } }
    const out: Record<string, string> = { 'brain.yaml': defText };
    if (v && !Array.isArray(v.modules)) Object.assign(out, v.modules);
    if (v?.libraries) for (const [p, x] of Object.entries<any>(v.libraries)) out[p] = typeof x === 'string' ? '' : x.source ?? '';
    return out;
  };
  const sa = useMemo(() => sources(a.data), [a.data, normalize]); // eslint-disable-line react-hooks/exhaustive-deps
  const sb = useMemo(() => sources(b.data), [b.data, normalize]); // eslint-disable-line react-hooks/exhaustive-deps
  const files = [...new Set([...Object.keys(sa), ...Object.keys(sb)])];
  const status = (f: string) => f === 'brain.yaml' ? (diff.data?.definitionDiff ? 'changed' : 'same') : diff.data?.modules.find(m => m.path === f)?.status ?? (sa[f] === sb[f] ? 'same' : 'changed');
  useEffect(() => { if (!files.includes(file)) setFile('brain.yaml'); }, [files.join('|')]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className="diff">
      <h3>Diff <span className="mono">{from}</span> &rarr; <span className="mono">{to}</span></h3>
      <div className="row wrap"><label className="check small"><input type="checkbox" checked={normalize} onChange={e => setNormalize(e.target.checked)} /> normalized brain.yaml</label>{files.map(f => <button key={f} className={'btn sm ' + (f === file ? 'primary' : 'ghost')} onClick={() => setFile(f)}>{f} <span className={'tag ' + (status(f) === 'same' ? '' : 'warn')}>{status(f)}</span></button>)}</div>
      {(a.data && b.data) ? <DiffView original={sa[file] ?? ''} modified={sb[file] ?? ''} language={file.endsWith('.yaml') ? 'yaml' : 'lua'} height={480} /> : <Empty>loading</Empty>}
    </div>
  );
}

function Metrics({ brain, versions }: { brain: string; versions: string[] }) {
  const m = useAsync<VersionMetrics[]>(() => (versions.length ? api.brainMetrics(brain, versions) : Promise.resolve([])), [brain, versions.join(',')]);
  if (m.error) return <div className="card"><h3>Compare versions</h3><Empty>{/404|no route/i.test(m.error) ? 'The host does not provide version metrics yet (UI requests: GET /brains/{name}/metrics).' : m.error}</Empty></div>;
  const rows = (m.data || []).flatMap(v => v.perClass.map(c => ({ hash: v.hash, ...c })));
  const classes = [...new Set(rows.map(r => r.classFamily))];
  const cols: [keyof typeof rows[number], string, number, boolean][] = [['expPerHour', 'exp/h', 0, true], ['deathsPerHour', 'deaths/h', 2, false], ['questsPerHour', 'quests/h', 1, true], ['stuckExitsPerHour', 'stuck exits/h', 2, false], ['tickP99Us', 'tick p99 us', 0, false]];
  return (
    <div className="card">
      <h3>Compare versions <span className="muted small">(REDESIGN 9.2: what "is the new brain better" is decided on)</span></h3>
      {classes.map(cf => {
        const rs = rows.filter(r => r.classFamily === cf);
        return (
          <div key={cf}>
            <div className="side-h">class family {cf}</div>
            <table className="trace">
              <thead><tr><th>version</th><th>bots</th><th>hours</th>{cols.map(c => <th key={c[0] as string}>{c[1]}</th>)}</tr></thead>
              <tbody>{rs.map(r => (
                <tr key={r.hash}><td className="mono">{r.hash}</td><td>{r.bots}</td><td>{r.hours}</td>
                  {cols.map(([k, , dg, higher]) => {
                    const vals = rs.map(x => x[k] as number); const best = higher ? Math.max(...vals) : Math.min(...vals);
                    return <td key={k as string} className={rs.length > 1 && r[k] === best ? 'best' : ''}>{fmtNum(r[k] as number, dg)}</td>;
                  })}</tr>
              ))}</tbody>
            </table>
            <div className="small">time per state:</div>
            {rs.map(r => <ShareBar key={r.hash} label={r.hash.slice(0, 8)} share={r.stateShare} />)}
          </div>
        );
      })}
      {!rows.length && <Empty>no data</Empty>}
    </div>
  );
}

function ShareBar({ label, share }: { label: string; share: Record<string, number> }) {
  const layers = [...new Set(Object.keys(share).map(k => k.split('/')[0]))];
  const colors = ['#4e9fd1', '#37b29a', '#c9a640', '#b46fd8', '#d46a6a', '#7f9f5a', '#c08a5a', '#6aa4a4'];
  return (
    <div className="share">
      <span className="mono small">{label}</span>
      {layers.map(l => (
        <div key={l} className="share-layer" title={l}>
          <span className="small muted">{l}</span>
          <div className="stepbar">{Object.entries(share).filter(([k]) => k.startsWith(l + '/')).map(([k, v], i) => <div key={k} title={`${k} ${(v * 100).toFixed(1)}%`} style={{ width: `${v * 100}%`, background: colors[i % colors.length] }} />)}</div>
        </div>
      ))}
    </div>
  );
}

