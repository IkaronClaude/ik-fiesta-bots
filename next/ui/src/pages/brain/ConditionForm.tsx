import { CodeEditor } from '../../components/CodeEditor';
import { useManifestLists } from '../../lib/app';
import { type Cond, type CondKind, COND_KINDS, COND_LABEL, condKind, emptyCond, isFnRef, stripUndefined } from '../../lib/brain';

const OPS = ['<', '<=', '>', '>=', '==', '~='];

/** Form for one condition (API_SPEC 5.3), recursive for all / any / not. Only the condition keys are edited. */
export function ConditionForm({ cond, onChange, fnRefs, depth = 0, budget }: {
  cond: Cond; onChange: (c: Cond) => void; fnRefs: string[]; depth?: number; budget?: number;
}) {
  const L = useManifestLists();
  const kind = condKind(cond);
  const set = (patch: Partial<Cond>) => onChange(stripUndefined({ ...cond, ...patch }) as Cond);
  const change = (k: CondKind) => {
    const keep: any = {};
    for (const [key, v] of Object.entries(cond)) if (['to', 'priority', 'hold', 'cooldown', 'reason'].includes(key)) keep[key] = v;
    onChange({ ...keep, ...emptyCond(k, { events: L.events.map(name => ({ name })), sensors: L.sensors.map(name => ({ name })), procedures: L.procedures, jobs: L.jobs }) });
  };
  const kinds = COND_KINDS.filter(k => depth < 3 || !['all', 'any', 'not'].includes(k));
  return (
    <div className={'cond depth' + depth}>
      <div className="row">
        <select aria-label="condition kind" value={kind} onChange={e => change(e.target.value as CondKind)}>
          {kind === 'none' && <option value="none">(choose a condition)</option>}
          {kinds.map(k => <option key={k} value={k}>{COND_LABEL[k]}</option>)}
        </select>
        <span className="hint">{costHint(kind)}</span>
      </div>
      {kind === 'value' && (
        <div className="row wrap">
          <select aria-label="sensor" value={cond.value} onChange={e => set({ value: e.target.value })} title={L.sensorDoc[cond.value || ''] || ''}>
            {!L.sensors.includes(cond.value || '') && <option value={cond.value}>{cond.value || '(sensor)'}</option>}
            {L.sensors.map(s => <option key={s} value={s}>{s}</option>)}
          </select>
          <select aria-label="operator" value={cond.op} onChange={e => set({ op: e.target.value })}>{OPS.map(o => <option key={o}>{o}</option>)}</select>
          <input aria-label="threshold" className="num-input" type="number" value={cond.than as any ?? ''} onChange={e => set({ than: e.target.value === '' ? undefined : Number(e.target.value) })} />
          {cond.value && L.sensorDoc[cond.value] && <div className="hint full">{L.sensorDoc[cond.value]}</div>}
        </div>
      )}
      {kind === 'lua' && (
        <div>
          <div className="hint">A function from the brain's modules (<code>module.fn</code>) or an inline expression; truthy = fire. Completion: Ctrl+Space.</div>
          <CodeEditor value={String(cond.when ?? '')} onChange={v => set({ when: v })} expression={!isFnRef(String(cond.when ?? ''))} height={64} compact ariaLabel="Lua condition" />
          {fnRefs.length > 0 && (
            <select aria-label="insert function" value="" onChange={e => e.target.value && set({ when: e.target.value })}>
              <option value="">use a module function...</option>
              {fnRefs.map(f => <option key={f} value={f}>{f}</option>)}
            </select>
          )}
        </div>
      )}
      {kind === 'event' && (
        <div className="row wrap">
          <select aria-label="event" value={cond.on} onChange={e => set({ on: e.target.value })}>
            {!L.events.includes(cond.on || '') && <option value={cond.on}>{cond.on || '(event)'}</option>}
            {L.events.map(ev => <option key={ev} value={ev}>{ev}</option>)}
          </select>
          <input aria-label="event filter" placeholder="filter (Lua expr, optional)" value={cond.filter ?? ''} onChange={e => set({ filter: e.target.value || undefined })} />
          {cond.on && L.eventDoc[cond.on] && <div className="hint full">{L.eventDoc[cond.on]}</div>}
        </div>
      )}
      {kind === 'action' && (
        <div className="row wrap">
          <input aria-label="action name" placeholder="action handle (bb.state.<name>)" value={cond.action ?? ''} onChange={e => set({ action: e.target.value })} />
          <select aria-label="action result" value={cond.result} onChange={e => set({ result: e.target.value })}>{['acked', 'refused', 'timedout', 'failed', 'cancelled'].map(r => <option key={r}>{r}</option>)}</select>
        </div>
      )}
      {(kind === 'after' || kind === 'every') && (
        <div className="row">
          <input aria-label={kind + ' ms'} className="num-input" type="number" min={0} step={100} value={(kind === 'after' ? cond.after : cond.every) ?? 0}
            onChange={e => set(kind === 'after' ? { after: Number(e.target.value) } : { every: Number(e.target.value) })} />
          <span className="hint">ms {kind === 'after' ? 'in this state' : '(re-enters the state)'}</span>
        </div>
      )}
      {kind === 'stuck' && <div className="hint">Fires when the state's <code>progress</code> has not improved for its budget{budget != null ? ` (${budget} ms)` : ' (default 60000 ms)'}.</div>}
      {kind === 'procedure' && (
        <div className="row">
          <select aria-label="procedure" value={cond.procedure} onChange={e => set({ procedure: e.target.value })}>
            {!L.procedures.includes(cond.procedure || '') && <option value={cond.procedure}>{cond.procedure || '(procedure)'}</option>}
            {L.procedures.map(p => <option key={p}>{p}</option>)}
          </select>
          <select aria-label="procedure result" value={cond.result} onChange={e => set({ result: e.target.value })}><option>ok</option><option>failed</option></select>
        </div>
      )}
      {kind === 'job' && (
        <div className="row">
          <select aria-label="job" value={cond.job} onChange={e => set({ job: e.target.value })}>
            {!L.jobs.includes(cond.job || '') && <option value={cond.job}>{cond.job || '(job)'}</option>}
            {L.jobs.map(p => <option key={p}>{p}</option>)}
          </select>
          <input aria-label="job result" placeholder="result value" value={cond.result ?? ''} onChange={e => set({ result: e.target.value })} />
        </div>
      )}
      {(kind === 'all' || kind === 'any') && (
        <div className="cond-list">
          {(cond[kind] || []).map((c, i) => (
            <div key={i} className="cond-item">
              <ConditionForm cond={c} depth={depth + 1} fnRefs={fnRefs} budget={budget} onChange={nc => set({ [kind]: (cond[kind] || []).map((x, j) => (j === i ? nc : x)) } as any)} />
              <button className="btn ghost sm" onClick={() => set({ [kind]: (cond[kind] || []).filter((_, j) => j !== i) } as any)}>remove</button>
            </div>
          ))}
          <button className="btn sm" onClick={() => set({ [kind]: [...(cond[kind] || []), { after: 1000 }] } as any)}>+ condition</button>
          <div className="hint">Fixed conditions are evaluated first, Lua leaves last (short-circuit).</div>
        </div>
      )}
      {kind === 'not' && <ConditionForm cond={cond.not || {}} depth={depth + 1} fnRefs={fnRefs} budget={budget} onChange={nc => set({ not: nc })} />}
    </div>
  );
}

function costHint(k: CondKind) {
  if (k === 'lua') return 'Lua call';
  if (k === 'none') return '';
  if (k === 'all' || k === 'any' || k === 'not') return 'composite';
  return 'C#, no Lua';
}
