// Sub-graph workflow UI: group / ungroup, make reusable (named machine), make a local copy, usages, import a machine
// from another brain. The operations themselves are pure functions in lib/subgraph.ts.
import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import { ActionButton, Field, Modal, useAsync } from '../../components/ui';
import { useApp } from '../../lib/app';
import { type BrainDef, type MachineDef, type Scope, machineAt, parseDefinition, uniqueName } from '../../lib/brain';
import { groupStates, makeLocalCopy, makeNamed, planGroup, subMachineOf, ungroup, usages } from '../../lib/subgraph';

const NAME_RE = /^[A-Za-z0-9_][A-Za-z0-9_/-]*$/;

/** Toolbar actions for the selected state when it is a sub-graph. */
export function SubgraphBar({ def, scope, state, onDef, onOpen, onNavigate }: {
  def: BrainDef; scope: Scope; state: string; onDef: (d: BrainDef) => void; onOpen: () => void; onNavigate: (s: Scope, state: string) => void;
}) {
  const { toast } = useApp();
  const m = machineAt(def, scope);
  const st = m?.states[state];
  const sub = subMachineOf(def, st);
  const [naming, setNaming] = useState(false);
  const [showUses, setShowUses] = useState(false);
  if (!st || (!sub && !st.use)) return null;
  const uses = st.use ? usages(def, st.use) : [];
  return (
    <span className="row subgraph-bar">
      {sub && <button className="btn sm" onClick={onOpen} title="edit the inside (or double-click the node)">open</button>}
      {sub && <button className="btn sm ghost" onClick={() => {
        const r = ungroup(def, scope, state);
        if (r.def === def) { toast(r.warnings.join('; ') || 'cannot ungroup', 'error'); return; }
        onDef(r.def); toast(`ungrouped '${state}'${r.warnings.length ? ': ' + r.warnings.join('; ') : ''}`, r.warnings.length ? 'info' : 'ok');
      }} title="inline the sub-graph's states back into this graph">ungroup</button>}
      {st.machine && <button className="btn sm ghost" onClick={() => setNaming(true)} title="move it to the brain's named machines so other graphs can use it">make reusable...</button>}
      {st.use && <>
        <button className="btn sm ghost" onClick={() => setShowUses(v => !v)} title="every state that uses this machine">used in {uses.length}</button>
        <button className="btn sm ghost" onClick={() => { onDef(makeLocalCopy(def, scope, state)); toast(`'${state}' now has its own copy of ${st.use}`, 'ok'); }} title="diverge: replace the shared machine with an inline copy">make local copy</button>
      </>}
      {showUses && st.use && (
        <div className="uses-pop">
          <div className="side-h">{st.use}: used in {uses.length} place(s); editing it edits all of them</div>
          {uses.map((u, i) => <button key={i} className="linkish block" onClick={() => { setShowUses(false); onNavigate(u.scope, u.state); }}>{u.label}</button>)}
        </div>
      )}
      {naming && <NameModal title="Make reusable machine" label="machine name" initial={uniqueName(Object.keys(def.machines || {}), state)}
        taken={Object.keys(def.machines || {})} onClose={() => setNaming(false)}
        onOk={n => { onDef(makeNamed(def, scope, state, n)); setNaming(false); toast(`'${state}' now uses the named machine '${n}'`, 'ok'); }} />}
    </span>
  );
}

export function NameModal({ title, label, initial, taken, onClose, onOk }: { title: string; label: string; initial: string; taken: string[]; onClose: () => void; onOk: (n: string) => void }) {
  const [n, setN] = useState(initial);
  const bad = !NAME_RE.test(n) ? 'letters, digits, _ - /' : taken.includes(n) ? 'already used' : null;
  return (
    <Modal title={title} onClose={onClose} footer={<button className="btn primary" disabled={!!bad} onClick={() => onOk(n)}>OK</button>}>
      <Field label={label} hint={bad ?? undefined}><input autoFocus aria-label={label} value={n} onChange={e => setN(e.target.value)} onKeyDown={e => e.key === 'Enter' && !bad && onOk(n)} /></Field>
    </Modal>
  );
}

/** Group the selected states into one sub-graph state: pick its name and entry, optionally make it reusable. */
export function GroupModal({ def, scope, machine, names: preset, onClose, onDone }: {
  def: BrainDef; scope: Scope; machine: MachineDef; names: string[]; onClose: () => void; onDone: (d: BrainDef, group: string) => void;
}) {
  // the canvas selection (box / shift / ctrl) pre-ticks the list; the list also works without a keyboard selection
  const [names, setNames] = useState<string[]>(preset.filter(n => n in machine.states));
  const plan = useMemo(() => planGroup(machine, names), [machine, names]);
  const entries = plan.entryCandidates.length ? plan.entryCandidates : names;
  const [entryPick, setEntry] = useState<string>('');
  const entry = entries.includes(entryPick) ? entryPick : entries[0];
  const [name, setName] = useState(uniqueName(Object.keys(machine.states), 'group'));
  const [named, setNamed] = useState(false);
  const [mname, setMname] = useState(uniqueName(Object.keys(def.machines || {}), 'group'));
  const taken = Object.keys(machine.states).filter(k => !names.includes(k));
  const bad = !NAME_RE.test(name) || name.includes('/') ? 'letters, digits, _ -' : taken.includes(name) ? 'a state of that name exists' : null;
  const badM = named && (!NAME_RE.test(mname) || (def.machines || {})[mname]) ? 'name taken or invalid' : null;
  const others = plan.incoming.filter(i => i.to.split('/')[0] !== entry);
  return (
    <Modal title={`Group ${names.length} states into a sub-graph`} onClose={onClose} wide footer={
      <ActionButton kind="primary" disabled={!!bad || !!badM || !names.length || !entry} onClick={() => {
        let d = groupStates(def, scope, names, name, entry);
        if (named) d = makeNamed(d, scope, name, mname);
        onDone(d, name);
      }}>Group</ActionButton>}>
      <Field label="states to group">
        <div className="row wrap">{Object.keys(machine.states).map(n => (
          <label key={n} className="check"><input type="checkbox" aria-label={`group ${n}`} checked={names.includes(n)} onChange={e => setNames(v => (e.target.checked ? [...v, n] : v.filter(x => x !== n)))} /> {n}</label>
        ))}</div>
      </Field>
      <div className="grid2">
        <Field label="sub-graph state name" hint={bad ?? undefined}><input aria-label="group name" autoFocus value={name} onChange={e => setName(e.target.value)} /></Field>
        <Field label="entry (initial inner state)" hint={plan.entryCandidates.length > 1 ? 'several states are entered from outside' : undefined}>
          <select aria-label="entry state" value={entry} onChange={e => setEntry(e.target.value)}>{entries.map(n => <option key={n}>{n}</option>)}</select>
        </Field>
      </div>
      <div className="small">
        <div><strong>{names.join(', ')}</strong> become the inside of <strong>{name}</strong>.</div>
        <div>{plan.incoming.length} incoming transition(s): target <code>{name}</code> (enters <code>{entry}</code>){others.length > 0 && <>; {others.length} enter another inner state and become <code>{name}/&lt;state&gt;</code></>}.</div>
        <div>{plan.outgoing.length} outgoing transition(s): become inner exits <code>^/&lt;state&gt;</code>, drawn as edges leaving <code>{name}</code>.</div>
      </div>
      <label className="check"><input type="checkbox" checked={named} onChange={e => setNamed(e.target.checked)} /> make it a named, reusable machine</label>
      {named && <Field label="machine name" hint={badM ?? 'usable from any layer of this brain; editing it edits every use'}><input aria-label="machine name" value={mname} onChange={e => setMname(e.target.value)} /></Field>}
    </Modal>
  );
}

/** Copy a named machine from another brain's latest version into this brain (a copy: the brains do not share it). */
export function ImportMachineModal({ def, onClose, onDone }: { def: BrainDef; onClose: () => void; onDone: (d: BrainDef) => void }) {
  const brains = useAsync(() => api.brains(), []);
  const [brain, setBrain] = useState('');
  const src = useAsync(async () => {
    if (!brain) return null;
    const b = (brains.data || []).find(x => x.name === brain);
    return b ? parseDefinition((await api.version(brain, b.latest)).definition) : null;
  }, [brain, brains.data]);
  const [pick, setPick] = useState('');
  const machines = Object.keys(src.data?.machines || {});
  return (
    <Modal title="Import a machine from another brain" onClose={onClose} footer={
      <button className="btn primary" disabled={!pick || !src.data} onClick={() => {
        const name = uniqueName(Object.keys(def.machines || {}), pick);
        onDone({ ...def, machines: { ...(def.machines || {}), [name]: structuredClone(src.data!.machines![pick]) } });
      }}>Import copy</button>}>
      <p className="hint">A machine lives in one brain definition; importing makes a copy (shared machines across brains need U33).</p>
      <Field label="brain"><select aria-label="source brain" value={brain} onChange={e => { setBrain(e.target.value); setPick(''); }}>
        <option value="">choose...</option>{(brains.data || []).filter(b => b.name !== def.brain).map(b => <option key={b.name}>{b.name}</option>)}</select></Field>
      {brain && <Field label="machine"><select aria-label="source machine" value={pick} onChange={e => setPick(e.target.value)}>
        <option value="">{machines.length ? 'choose...' : 'no named machines'}</option>{machines.map(m => <option key={m}>{m}</option>)}</select></Field>}
    </Modal>
  );
}
