// Sub-graph editing: group states into a sub-machine, ungroup, make a sub-machine a named reusable machine, make a
// local copy, find usages. Target forms (API_SPEC 5.2 + UI requests U30 / U31):
//   "s"        a state of the same machine
//   "G/s"      enter the sub-machine state G at its inner state s (instead of G's initial)          (U30)
//   "^/t"      leave this sub-machine for the state t of the machine that contains it              (U31)
//   "^"        pop to the parent (API_SPEC 5.2)
//   "m:s"      a state of another machine of the same layer (API_SPEC 5.4)
import { type BrainDef, type MachineDef, type Scope, type StateDef, type Transition, machineAt, stripUndefined, uniqueName, updateMachine } from './brain';

export const head = (to: string) => to.split('/')[0];
export const isUp = (to: string) => to === '^' || to.startsWith('^/');

/** The sub-machine of a state: inline, or the named machine it uses (`${expr}` uses are resolved by the runtime). */
export function subMachineOf(def: BrainDef, st: StateDef | undefined): { machine: MachineDef; named: string | null } | null {
  if (!st) return null;
  if (st.machine) return { machine: st.machine, named: null };
  if (st.use && def.machines?.[st.use]) return { machine: def.machines[st.use], named: st.use };
  return null;
}

/** Inner exits of a sub-machine to the containing machine: `^/t` transitions (one level). */
export function innerExits(m: MachineDef): { from: string; index: number; to: string; t: Transition }[] {
  const out: { from: string; index: number; to: string; t: Transition }[] = [];
  for (const [s, st] of Object.entries(m.states)) (st.transitions || []).forEach((t, index) => {
    if (t.to.startsWith('^/')) out.push({ from: s, index, to: t.to.slice(2), t });
    else if (t.to === '^') out.push({ from: s, index, to: '^', t });
  });
  return out;
}

export interface GroupPlan {
  selected: string[];
  /** inner states that receive transitions from outside the selection (or are the machine's initial) */
  entryCandidates: string[];
  incoming: { from: string; index: number; to: string }[];
  outgoing: { from: string; index: number; to: string }[];
}

export function planGroup(m: MachineDef, names: string[]): GroupPlan {
  const sel = new Set(names);
  const incoming: GroupPlan['incoming'] = [], outgoing: GroupPlan['outgoing'] = [];
  for (const [s, st] of Object.entries(m.states)) (st.transitions || []).forEach((t, index) => {
    const tgt = head(t.to);
    if (!sel.has(s) && sel.has(tgt)) incoming.push({ from: s, index, to: t.to });
    if (sel.has(s) && !sel.has(tgt) && !isUp(t.to) && !t.to.includes(':')) outgoing.push({ from: s, index, to: t.to });
  });
  const cands = new Set<string>(incoming.map(i => head(i.to)));
  if (sel.has(m.initial)) cands.add(m.initial);
  return { selected: names, entryCandidates: [...cands], incoming, outgoing };
}

/** Collapse `names` of the machine at `scope` into one state `group` whose inline machine holds them. */
export function groupStates(def: BrainDef, scope: Scope, names: string[], group: string, entry: string): BrainDef {
  return updateMachine(def, scope, m => {
    names = names.filter(n => n in m.states);
    const sel = new Set(names);
    const inner: Record<string, StateDef> = {};
    for (const n of names) {
      const st = m.states[n];
      inner[n] = stripUndefined({
        ...st,
        transitions: st.transitions?.map(t => {
          const tgt = head(t.to);
          if (sel.has(tgt) || isUp(t.to) || t.to.includes(':')) return t;      // stays inside / already leaves
          return { ...t, to: `^/${t.to}` };                                       // now crosses the boundary: exit
        }),
      });
    }
    const states: Record<string, StateDef> = {};
    let placed = false;
    for (const [k, st] of Object.entries(m.states)) {
      if (sel.has(k)) {
        if (!placed) { states[group] = { machine: { initial: entry, states: inner }, transitions: [] }; placed = true; }
        continue;
      }
      states[k] = stripUndefined({
        ...st,
        transitions: st.transitions?.map(t => {
          const tgt = head(t.to);
          if (!sel.has(tgt)) return t;
          return { ...t, to: tgt === entry && !t.to.includes('/') ? group : `${group}/${t.to}` };
        }),
        onError: st.onError && sel.has(st.onError) ? group : st.onError,
      });
    }
    return { ...m, initial: sel.has(m.initial) ? group : m.initial, states };
  });
}

/** Inline the sub-machine of `group` back into its machine. Returns warnings for what could not be kept 1:1. */
export function ungroup(def: BrainDef, scope: Scope, group: string): { def: BrainDef; warnings: string[] } {
  const warnings: string[] = [];
  const m = machineAt(def, scope);
  const g = m?.states[group];
  const sub = subMachineOf(def, g);
  if (!m || !g || !sub) return { def, warnings: ['not a sub-graph'] };
  if (g.enter || g.tick || g.exit) return { def, warnings: [`'${group}' has its own enter / tick / exit functions: move them into the inner states first`] };
  if (g.claims?.length) warnings.push(`the sub-graph's claims (${g.claims.join(', ')}) are dropped`);
  const taken = new Set(Object.keys(m.states).filter(k => k !== group));
  const rename: Record<string, string> = {};
  for (const n of Object.keys(sub.machine.states)) { const nn = taken.has(n) ? uniqueName([...taken], `${n}`) : n; rename[n] = nn; taken.add(nn); }
  const outer = g.transitions || [];
  const states: Record<string, StateDef> = {};
  for (const [k, st] of Object.entries(m.states)) {
    if (k === group) {
      for (const [n, ist] of Object.entries(sub.machine.states)) {
        const own = (ist.transitions || []).map(t => {
          if (t.to.startsWith('^/')) return { ...t, to: t.to.slice(2) };
          if (t.to === '^') { warnings.push(`'${n}' pops to the parent ('^'): now targets '${m.initial === group ? rename[sub.machine.initial] : m.initial}'`); return { ...t, to: m.initial === group ? rename[sub.machine.initial] : m.initial }; }
          const h = head(t.to);
          return rename[h] ? { ...t, to: [rename[h], ...t.to.split('/').slice(1)].join('/') } : t;
        });
        // the group's own transitions were evaluated first while inside it: keep them first on every inner state
        states[rename[n]] = stripUndefined({ ...ist, transitions: [...outer, ...own] });
      }
      continue;
    }
    states[k] = stripUndefined({
      ...st,
      transitions: st.transitions?.map(t => {
        if (head(t.to) !== group) return t;
        const rest = t.to.split('/').slice(1);
        return { ...t, to: rest.length ? [rename[rest[0]] ?? rest[0], ...rest.slice(1)].join('/') : rename[sub.machine.initial] };
      }),
      onError: st.onError === group ? rename[sub.machine.initial] : st.onError,
    });
  }
  if (outer.length && Object.keys(sub.machine.states).length > 1) warnings.push(`the sub-graph's ${outer.length} transition(s) are copied onto each inner state`);
  return { def: updateMachine(def, scope, mm => ({ ...mm, initial: mm.initial === group ? rename[sub.machine.initial] : mm.initial, states })), warnings };
}

/** Move an inline sub-machine into the brain's named machines and make the state use it. */
export function makeNamed(def: BrainDef, scope: Scope, state: string, name: string): BrainDef {
  const m = machineAt(def, scope); const st = m?.states[state];
  if (!st?.machine || def.machines?.[name]) return def;
  const next = { ...def, machines: { ...(def.machines || {}), [name]: st.machine } };
  return updateMachine(next, scope, mm => ({ ...mm, states: { ...mm.states, [state]: stripUndefined({ ...mm.states[state], machine: undefined, use: name }) } }));
}

/** Replace a `use` with an inline copy of the named machine (diverge from the shared one). */
export function makeLocalCopy(def: BrainDef, scope: Scope, state: string): BrainDef {
  const m = machineAt(def, scope); const st = m?.states[state];
  if (!st?.use || !def.machines?.[st.use]) return def;
  const copy = structuredClone(def.machines[st.use]);
  return updateMachine(def, scope, mm => ({ ...mm, states: { ...mm.states, [state]: stripUndefined({ ...mm.states[state], use: undefined, machine: copy }) } }));
}

/** Place a named machine as a new state in the machine at `scope`. */
export function placeMachine(def: BrainDef, scope: Scope, name: string): [BrainDef, string] {
  const m = machineAt(def, scope)!;
  const state = uniqueName(Object.keys(m.states), name.split('/').pop()!.replace(/[^A-Za-z0-9_-]/g, '_') || 'sub');
  return [updateMachine(def, scope, mm => ({ ...mm, initial: mm.initial || state, states: { ...mm.states, [state]: { use: name, transitions: [] } } })), state];
}

export interface Usage { scope: Scope; state: string; label: string }

/** Every state that uses the named machine (layers, named machines and their inline sub-machines). */
export function usages(def: BrainDef, name: string): Usage[] {
  const out: Usage[] = [];
  const walk = (m: MachineDef, scope: Scope, label: string) => {
    for (const [s, st] of Object.entries(m.states || {})) {
      if (st.use === name) out.push({ scope, state: s, label: `${label} / ${s}` });
      if (st.machine) walk(st.machine, { ...scope, path: [...scope.path, s] }, `${label} / ${s}`);
    }
  };
  for (const l of def.layers || []) walk(l.machine, { kind: 'layer', layer: l.name, path: [] }, `layer ${l.name}`);
  for (const [n, mm] of Object.entries(def.machines || {})) walk(mm, { kind: 'machine', name: n, path: [] }, `machine ${n}`);
  return out;
}

/** Named machines reachable from `name` through `use` (for recursion checks). */
export function usesMachine(def: BrainDef, from: string, target: string, seen = new Set<string>()): boolean {
  if (seen.has(from)) return false;
  seen.add(from);
  const m = def.machines?.[from]; if (!m) return false;
  const scan = (mm: MachineDef): boolean => Object.values(mm.states || {}).some(st =>
    (st.use && (st.use === target || usesMachine(def, st.use, target, seen))) || (st.machine && scan(st.machine)));
  return scan(m);
}

/** Renaming a state also fixes `s/inner` targets and inner `^/s` exits. */
export function renameRefs(m: MachineDef, from: string, to: string): MachineDef {
  const fix = (t: Transition): Transition => {
    const parts = t.to.split('/');
    return parts[0] === from ? { ...t, to: [to, ...parts.slice(1)].join('/') } : t;
  };
  const fixInner = (mm: MachineDef): MachineDef => ({ ...mm, states: Object.fromEntries(Object.entries(mm.states).map(([k, st]) => [k, stripUndefined({
    ...st, transitions: st.transitions?.map(t => (t.to === `^/${from}` ? { ...t, to: `^/${to}` } : t)), machine: st.machine ? st.machine : undefined })])) });
  return { ...m, states: Object.fromEntries(Object.entries(m.states).map(([k, st]) => [k, stripUndefined({
    ...st, transitions: st.transitions?.map(fix), machine: st.machine ? fixInner(st.machine) : undefined })])) };
}
