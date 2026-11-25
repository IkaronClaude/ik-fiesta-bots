import { useEffect, useState } from 'react';
import { CodeEditor } from '../../components/CodeEditor';
import { Field } from '../../components/ui';
import { useManifestLists } from '../../lib/app';
import {
  type BrainDef, type LayerDef, type MachineDef, type StateDef, type Transition, condKind, condText, isFnRef, renameState, stripUndefined, uniqueName,
} from '../../lib/brain';
import type { LintResult } from '../../api/types';
import { renameRefs } from '../../lib/subgraph';
import { ConditionForm } from './ConditionForm';

const numOrUndef = (s: string) => (s === '' ? undefined : Number(s));

/** enter / tick / exit / progress: a module function reference or an inline Lua body. */
function FnField({ label, value, onChange, fnRefs, expression, sensors }: { label: string; value?: string; onChange: (v?: string) => void; fnRefs: string[]; expression?: boolean; sensors?: string[] }) {
  const [mode, setMode] = useState<'none' | 'ref' | 'inline' | 'sensor'>(value == null || value === '' ? 'none' : sensors?.includes(value) ? 'sensor' : isFnRef(value) ? 'ref' : 'inline');
  useEffect(() => { setMode(value == null || value === '' ? 'none' : sensors?.includes(value) ? 'sensor' : isFnRef(value) ? 'ref' : 'inline'); }, [value, sensors]);
  return (
    <div className="fnfield">
      <div className="row">
        <span className="field-label">{label}</span>
        <select aria-label={label + ' kind'} value={mode} onChange={e => {
          const m = e.target.value as typeof mode; setMode(m);
          if (m === 'none') onChange(undefined);
          else if (m === 'ref') onChange(fnRefs[0] ?? 'module.fn');
          else if (m === 'sensor') onChange(sensors?.[0]);
          else onChange(expression ? '0' : '-- Lua\n');
        }}>
          <option value="none">none</option>
          {sensors && <option value="sensor">sensor</option>}
          <option value="ref">module function</option>
          <option value="inline">{expression ? 'Lua expression' : 'inline Lua'}</option>
        </select>
      </div>
      {mode === 'ref' && (
        <div className="row">
          <input aria-label={label + ' function'} list={'fnrefs'} value={value ?? ''} onChange={e => onChange(e.target.value)} placeholder="module.function" />
        </div>
      )}
      {mode === 'sensor' && sensors && (
        <select aria-label={label + ' sensor'} value={value} onChange={e => onChange(e.target.value)}>{sensors.map(s => <option key={s}>{s}</option>)}</select>
      )}
      {mode === 'inline' && <CodeEditor value={value ?? ''} onChange={v => onChange(v)} expression={expression} height={expression ? 56 : 120} compact ariaLabel={label + ' Lua'} />}
    </div>
  );
}

export function StatePanel({ def, machine, name, onMachine, onRename, fnRefs, machineNames, lint, onSelectEdge, onOpenSub, isSub }: {
  def: BrainDef; machine: MachineDef; name: string; onMachine: (m: MachineDef) => void; onRename: (n: string) => void; fnRefs: string[]; machineNames: string[];
  lint: LintResult[]; onSelectEdge: (i: number) => void; onOpenSub: () => void; isSub: boolean;
}) {
  const L = useManifestLists();
  const st = machine.states[name];
  const [nameDraft, setNameDraft] = useState(name);
  useEffect(() => setNameDraft(name), [name]);
  if (!st) return null;
  const set = (patch: Partial<StateDef>) => onMachine({ ...machine, states: { ...machine.states, [name]: stripUndefined({ ...st, ...patch }) as StateDef } });
  const ts = st.transitions || [];
  const move = (i: number, d: number) => { const a = [...ts]; const [x] = a.splice(i, 1); a.splice(i + d, 0, x); set({ transitions: a }); };
  const subKind = st.use != null ? 'use' : st.machine ? 'inline' : 'none';
  const myLint = lint.filter(l => l.state === name);
  return (
    <div className="panel">
      <h4>State</h4>
      <Field label="name">
        <div className="row">
          <input aria-label="state name" value={nameDraft} onChange={e => setNameDraft(e.target.value.replace(/[^A-Za-z0-9_-]/g, ''))}
            onBlur={() => { if (nameDraft && nameDraft !== name && !machine.states[nameDraft]) { const r = renameState(machine, name, nameDraft); onMachine(r === machine ? machine : renameRefs(r, name, nameDraft)); onRename(nameDraft); } else setNameDraft(name); }} />
          {machine.initial === name ? <span className="tag init">initial</span> : <button className="btn sm" onClick={() => onMachine({ ...machine, initial: name })}>make initial</button>}
        </div>
      </Field>
      {myLint.length > 0 && <ul className="lint-inline">{myLint.map((l, i) => <li key={i} className={l.severity}>{l.message}</li>)}</ul>}
      <div className="row wrap">
        <label className="check"><input type="checkbox" checked={!!st.terminal} onChange={e => set({ terminal: e.target.checked || undefined })} /> terminal</label>
      </div>
      <FnField label="enter" value={st.enter} onChange={v => set({ enter: v })} fnRefs={fnRefs} />
      <FnField label="tick" value={st.tick} onChange={v => set({ tick: v })} fnRefs={fnRefs} />
      <FnField label="exit" value={st.exit} onChange={v => set({ exit: v })} fnRefs={fnRefs} />
      <div className="grid2">
        <Field label="budget (ms)" hint="no progress this long = stuck"><input aria-label="budget" type="number" min={0} step={1000} value={st.budget ?? ''} placeholder="60000" onChange={e => set({ budget: numOrUndef(e.target.value) })} /></Field>
        <Field label="minDwell (ms)"><input aria-label="minDwell" type="number" min={0} step={100} value={st.minDwell ?? ''} placeholder="0" onChange={e => set({ minDwell: numOrUndef(e.target.value) })} /></Field>
        <Field label="timeout (ms)" hint="hard cap"><input aria-label="timeout" type="number" min={0} step={1000} value={st.timeout ?? ''} onChange={e => set({ timeout: numOrUndef(e.target.value) })} /></Field>
        <Field label="onError">
          <select aria-label="onError" value={st.onError ?? ''} onChange={e => set({ onError: e.target.value || undefined })}>
            <option value="">stay</option>{Object.keys(machine.states).map(s => <option key={s}>{s}</option>)}
          </select>
        </Field>
      </div>
      <FnField label="progress" value={st.progress} onChange={v => set({ progress: v })} fnRefs={fnRefs} expression sensors={L.sensors} />
      <Field label="claims">
        <div className="row wrap">
          {L.resources.map(r => (
            <label key={r} className="check"><input type="checkbox" checked={(st.claims || []).includes(r)}
              onChange={e => set({ claims: e.target.checked ? [...(st.claims || []), r] : (st.claims || []).filter(x => x !== r) })} /> {r}</label>
          ))}
          {!L.resources.length && <span className="hint">resources come from the API manifest</span>}
        </div>
      </Field>
      <Field label="sub-machine" hint="hierarchical: a state may contain a machine (inline) or reference a named one (use, may contain ${expr})">
        <div className="row wrap">
          <select aria-label="sub-machine kind" value={subKind} onChange={e => {
            const k = e.target.value;
            if (k === 'none') set({ use: undefined, machine: undefined });
            else if (k === 'use') set({ machine: undefined, use: machineNames[0] ?? '' });
            else set({ use: undefined, machine: { initial: 'start', states: { start: { terminal: true, transitions: [] } } } });
          }}>
            <option value="none">none</option><option value="use">use a named machine</option><option value="inline">inline machine</option>
          </select>
          {subKind === 'use' && <input aria-label="use machine" list="machine-names" value={st.use ?? ''} onChange={e => set({ use: e.target.value })} />}
          {subKind !== 'none' && <button className="btn sm" onClick={onOpenSub} disabled={subKind === 'use' && !(st.use && def.machines?.[st.use])}>open &rarr;</button>}
        </div>
      </Field>
      <h4>Transitions <span className="muted">(evaluated by priority, then order)</span></h4>
      <ol className="tlist">
        {ts.map((t, i) => (
          <li key={i}>
            <button className="linkish" onClick={() => onSelectEdge(i)}>
              <span className="mono">&rarr; {t.to}</span> <span className={'ekind k-' + condKind(t)}>{condKind(t)}</span> <span className="muted">{condText(t)}</span>
            </button>
            <span className="tbtns">
              <button className="btn ghost sm" disabled={i === 0} onClick={() => move(i, -1)} aria-label="move up">up</button>
              <button className="btn ghost sm" disabled={i === ts.length - 1} onClick={() => move(i, 1)} aria-label="move down">down</button>
            </span>
          </li>
        ))}
      </ol>
      <button className="btn sm" onClick={() => { set({ transitions: [...ts, { to: Object.keys(machine.states).find(s => s !== name) ?? name, after: 5000 }] }); onSelectEdge(ts.length); }}>+ transition</button>
      {isSub && <div className="hint">In a sub-machine, a transition to <code>^</code> pops to the parent.</div>}
    </div>
  );
}

export function TransitionPanel({ machine, from, index, onMachine, fnRefs, onSelectState, isSub, onDeleted }: {
  machine: MachineDef; from: string; index: number; onMachine: (m: MachineDef) => void; fnRefs: string[]; onSelectState: (s: string) => void; isSub: boolean; onDeleted: () => void;
}) {
  const st = machine.states[from];
  const t = st?.transitions?.[index];
  if (!t) return <div className="panel"><div className="empty">transition not found</div></div>;
  const setT = (nt: Transition) => onMachine({ ...machine, states: { ...machine.states, [from]: { ...st, transitions: st.transitions!.map((x, i) => (i === index ? stripUndefined(nt) as Transition : x)) } } });
  const targets = [...Object.keys(machine.states), ...(isSub ? ['^'] : [])];
  return (
    <div className="panel">
      <h4>Transition #{index + 1}</h4>
      <div className="row"><button className="linkish mono" onClick={() => onSelectState(from)}>{from}</button><span>&rarr;</span>
        <select aria-label="target state" value={targets.includes(t.to) ? t.to : '__other'} onChange={e => setT({ ...t, to: e.target.value === '__other' ? 'machine:state' : e.target.value })}>
          {targets.map(s => <option key={s} value={s}>{s === '^' ? '^ (parent)' : s}</option>)}
          <option value="__other">other machine (machine:state)...</option>
        </select>
        {!targets.includes(t.to) && <input aria-label="cross-machine target" value={t.to} onChange={e => setT({ ...t, to: e.target.value })} />}
      </div>
      <div className="grid3">
        <Field label="priority"><input aria-label="priority" type="number" value={t.priority ?? 0} onChange={e => setT({ ...t, priority: Number(e.target.value) || undefined })} /></Field>
        <Field label="hold (ms)" hint="must stay true"><input aria-label="hold" type="number" min={0} step={50} value={t.hold ?? ''} onChange={e => setT({ ...t, hold: numOrUndef(e.target.value) })} /></Field>
        <Field label="cooldown (ms)"><input aria-label="cooldown" type="number" min={0} step={100} value={t.cooldown ?? ''} onChange={e => setT({ ...t, cooldown: numOrUndef(e.target.value) })} /></Field>
      </div>
      <Field label="reason" hint="template; {sensor} placeholders are filled when it fires">
        <input aria-label="reason" value={t.reason ?? ''} placeholder="e.g. hp {self.hpPct}%" onChange={e => setT({ ...t, reason: e.target.value || undefined })} />
      </Field>
      <h4>Condition</h4>
      <ConditionForm cond={t} onChange={c => setT({ ...(c as Transition), to: t.to })} fnRefs={fnRefs} budget={st.budget} />
      <div className="row end"><button className="btn ghost danger-text sm" onClick={() => { onMachine({ ...machine, states: { ...machine.states, [from]: { ...st, transitions: st.transitions!.filter((_, i) => i !== index) } } }); onDeleted(); }}>delete transition</button></div>
    </div>
  );
}

export function LayerPanel({ layer, onChange, onDelete, canDelete }: { layer: LayerDef; onChange: (l: LayerDef) => void; onDelete: () => void; canDelete: boolean }) {
  const [nameDraft, setNameDraft] = useState(layer.name);
  useEffect(() => setNameDraft(layer.name), [layer.name]);
  return (
    <div className="panel">
      <h4>Layer</h4>
      <Field label="name"><input aria-label="layer name" value={nameDraft} onChange={e => setNameDraft(e.target.value)} onBlur={() => nameDraft && onChange({ ...layer, name: nameDraft })} /></Field>
      <div className="grid2">
        <Field label="priority" hint="0..100, higher runs first and wins resources"><input aria-label="layer priority" type="number" min={0} max={100} value={layer.priority} onChange={e => onChange({ ...layer, priority: Number(e.target.value) })} /></Field>
        <Field label="rate" hint="'tick' or ms (>= 50)">
          <input aria-label="layer rate" value={String(layer.rate ?? 'tick')} onChange={e => onChange({ ...layer, rate: e.target.value === 'tick' || e.target.value === '' ? 'tick' : (Number(e.target.value) || (e.target.value as any)) })} />
        </Field>
        <Field label="budgetMs" hint="Lua budget per tick"><input aria-label="layer budget" type="number" step={0.5} value={layer.budgetMs ?? ''} placeholder="2" onChange={e => onChange(stripUndefined({ ...layer, budgetMs: numOrUndef(e.target.value) }))} /></Field>
        <Field label="enabled"><label className="check"><input type="checkbox" checked={layer.enabled !== false} onChange={e => onChange({ ...layer, enabled: e.target.checked ? undefined : false } as LayerDef)} /> enabled</label></Field>
      </div>
      <div className="hint">Select a state or a transition on the canvas to edit it. Drag from a state's right handle to another state to add a transition; Delete removes the selection.</div>
      {canDelete && <div className="row end"><button className="btn ghost danger-text sm" onClick={onDelete}>delete layer</button></div>}
    </div>
  );
}

export function addState(m: MachineDef, base = 'state'): [MachineDef, string] {
  const n = uniqueName(Object.keys(m.states), base);
  return [{ ...m, states: { ...m.states, [n]: { budget: 60000, transitions: [] } }, initial: m.initial || n }, n];
}
