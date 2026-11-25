// Brain definition model (API_SPEC 5.2) and editing helpers. The definition is edited as an object and
// serialised to YAML (the store's format; JSON is accepted by the host too).
import YAML from 'yaml';

export type Cond = {
  when?: string; on?: string; action?: string; result?: string; procedure?: string; job?: string; filter?: string;
  after?: number; every?: number; value?: string; op?: string; than?: number | string; stuck?: true; always?: true;
  all?: Cond[]; any?: Cond[]; not?: Cond;
};
export type Transition = Cond & { to: string; priority?: number; hold?: number; cooldown?: number; reason?: string };
export interface StateDef {
  enter?: string; tick?: string; exit?: string; use?: string; machine?: MachineDef; claims?: string[]; budget?: number;
  progress?: string; minDwell?: number; timeout?: number; terminal?: boolean; onError?: string; transitions?: Transition[];
  [k: string]: unknown;
}
export interface MachineDef { initial: string; select?: { by: string; cases: Record<string, string> }; states: Record<string, StateDef> }
export interface LayerDef { name: string; priority: number; rate?: 'tick' | number; budgetMs?: number; enabled?: boolean; machine: MachineDef; claims_when_active?: string[] }
export interface BrainDef {
  brain: string; api: number; description?: string; modules?: string[]; blackboard?: Record<string, unknown>;
  machines?: Record<string, MachineDef>; layers: LayerDef[];
  ui?: { layout?: Record<string, Record<string, [number, number]>> };
  [k: string]: unknown;
}

export const COND_KINDS = ['value', 'lua', 'event', 'action', 'after', 'every', 'stuck', 'procedure', 'job', 'always', 'all', 'any', 'not'] as const;
export type CondKind = typeof COND_KINDS[number] | 'none';
export const COND_LABEL: Record<string, string> = {
  value: 'value compare (sensor)', lua: 'Lua condition', event: 'event', action: 'action result', after: 'time in state',
  every: 'periodic', stuck: 'stuck (no progress)', procedure: 'procedure result', job: 'job result', always: 'always',
  all: 'all of', any: 'any of', not: 'not', none: '(none)',
};
const COND_KEYS = ['when', 'on', 'action', 'result', 'procedure', 'job', 'filter', 'after', 'every', 'value', 'op', 'than', 'stuck', 'always', 'all', 'any', 'not'] as const;

export function parseDefinition(text: string): BrainDef {
  const t = text.trim();
  const v = t.startsWith('{') ? JSON.parse(t) : YAML.parse(text);
  if (!v || typeof v !== 'object') throw new Error('definition is empty');
  return v as BrainDef;
}

export function serializeDefinition(def: BrainDef): string {
  const doc = new YAML.Document(stripUndefined(def));
  YAML.visit(doc, {
    Pair(_, pair: any) {
      const k = pair.key?.value;
      if ((k === 'transitions') && YAML.isSeq(pair.value)) {
        for (const it of pair.value.items) if (YAML.isMap(it) && !hasNested(it)) it.flow = true;
      }
      if ((k === 'claims' || k === 'modules' || k === 'claims_when_active') && YAML.isSeq(pair.value)) pair.value.flow = true;
      if (k === 'ui' && YAML.isMap(pair.value)) {
        YAML.visit(pair.value, { Seq(_k, s: any) { s.flow = true; } });
      }
    },
  });
  return doc.toString({ lineWidth: 0 });
}
function hasNested(m: any) { return m.items.some((p: any) => YAML.isSeq(p.value) && p.value.items.some((x: any) => YAML.isMap(x) && hasNestedDeep(x))); }
function hasNestedDeep(m: any): boolean { return m.items.some((p: any) => YAML.isSeq(p.value) || YAML.isMap(p.value)); }

export function stripUndefined<T>(v: T): T {
  if (Array.isArray(v)) return v.map(stripUndefined) as T;
  if (v && typeof v === 'object') {
    const o: any = {};
    for (const [k, x] of Object.entries(v)) if (x !== undefined && x !== '') o[k] = stripUndefined(x);
    return o;
  }
  return v;
}

export function condKind(c: Cond): CondKind {
  if (c.all) return 'all';
  if (c.any) return 'any';
  if (c.not) return 'not';
  if (c.value != null) return 'value';
  if (c.after != null) return 'after';
  if (c.every != null) return 'every';
  if (c.stuck) return 'stuck';
  if (c.on === 'action') return 'action';
  if (c.on != null) return 'event';
  if (c.procedure != null) return 'procedure';
  if (c.job != null) return 'job';
  if (c.when != null) return 'lua';
  if (c.always) return 'always';
  return 'none';
}

export function condText(c: Cond): string {
  switch (condKind(c)) {
    case 'value': return `${c.value} ${c.op} ${c.than}`;
    case 'after': return `after ${fmtMs(c.after!)}`;
    case 'every': return `every ${fmtMs(c.every!)}`;
    case 'stuck': return 'stuck';
    case 'event': return `on ${c.on}${c.filter ? ` [${c.filter}]` : ''}`;
    case 'action': return `${c.action} ${c.result}`;
    case 'procedure': return `${c.procedure} ${c.result ?? ''}`.trim();
    case 'job': return `job ${c.job}${c.result ? ` = ${c.result}` : ''}`;
    case 'lua': return String(c.when);
    case 'always': return 'always';
    case 'all': return (c.all || []).map(condText).join(' AND ');
    case 'any': return (c.any || []).map(condText).join(' OR ');
    case 'not': return `NOT (${condText(c.not!)})`;
    default: return '(no condition)';
  }
}

export function fmtMs(ms: number): string {
  if (ms >= 60000 && ms % 60000 === 0) return `${ms / 60000} min`;
  if (ms >= 1000) return `${+(ms / 1000).toFixed(1)} s`;
  return `${ms} ms`;
}

/** Replace the condition part of a transition with a fresh condition of a kind. */
export function setCondKind(t: Transition, kind: CondKind, manifest?: { events?: { name: string }[]; sensors?: { name: string }[]; procedures?: string[]; jobs?: string[] }): Transition {
  const keep: any = {};
  for (const [k, v] of Object.entries(t)) if (!(COND_KEYS as readonly string[]).includes(k)) keep[k] = v;
  return { ...keep, ...emptyCond(kind, manifest) };
}
export function emptyCond(kind: CondKind, manifest?: { events?: { name: string }[]; sensors?: { name: string }[]; procedures?: string[]; jobs?: string[] }): Cond {
  switch (kind) {
    case 'value': return { value: manifest?.sensors?.[0]?.name ?? '', op: '<', than: 0 };
    case 'lua': return { when: 'false' };
    case 'event': return { on: manifest?.events?.[0]?.name ?? '' };
    case 'action': return { on: 'action', action: 'lastAction', result: 'refused' };
    case 'after': return { after: 5000 };
    case 'every': return { every: 5000 };
    case 'stuck': return { stuck: true };
    case 'procedure': return { procedure: manifest?.procedures?.[0] ?? '', result: 'ok' };
    case 'job': return { job: manifest?.jobs?.[0] ?? '', result: '' };
    case 'always': return { always: true };
    case 'all': return { all: [] };
    case 'any': return { any: [] };
    case 'not': return { not: { always: true } };
    default: return {};
  }
}

// ---------------------------------------------------------------- scopes (which machine the canvas shows)
export type Scope = { kind: 'layer'; layer: string; path: string[] } | { kind: 'machine'; name: string; path: string[] };

export function scopeKey(s: Scope): string {
  return (s.kind === 'layer' ? `layer:${s.layer}` : `machine:${s.name}`) + (s.path.length ? '/' + s.path.join('/') : '');
}

export function machineAt(def: BrainDef, s: Scope): MachineDef | null {
  let m: MachineDef | null | undefined = s.kind === 'layer' ? def.layers.find(l => l.name === s.layer)?.machine : def.machines?.[s.name];
  for (const p of s.path) {
    const st: StateDef | undefined = m?.states?.[p];
    m = st?.machine ?? null;
  }
  return m ?? null;
}

/** Immutable update of the machine a scope points at. */
export function updateMachine(def: BrainDef, s: Scope, fn: (m: MachineDef) => MachineDef): BrainDef {
  const rec = (m: MachineDef, path: string[]): MachineDef => {
    if (!path.length) return fn(m);
    const [h, ...rest] = path;
    const st = m.states[h];
    return { ...m, states: { ...m.states, [h]: { ...st, machine: rec(st.machine!, rest) } } };
  };
  if (s.kind === 'layer') return { ...def, layers: def.layers.map(l => (l.name === s.layer ? { ...l, machine: rec(l.machine, s.path) } : l)) };
  return { ...def, machines: { ...def.machines, [s.name]: rec(def.machines![s.name], s.path) } };
}

export function renameState(m: MachineDef, from: string, to: string): MachineDef {
  if (from === to || !to || m.states[to]) return m;
  const states: Record<string, StateDef> = {};
  for (const [k, v] of Object.entries(m.states)) {
    const nv = { ...v, transitions: v.transitions?.map(t => (t.to === from ? { ...t, to } : t)), onError: v.onError === from ? to : v.onError };
    states[k === from ? to : k] = stripUndefined(nv);
  }
  return { ...m, initial: m.initial === from ? to : m.initial, states };
}

export function deleteState(m: MachineDef, name: string): MachineDef {
  const states: Record<string, StateDef> = {};
  for (const [k, v] of Object.entries(m.states)) {
    if (k === name) continue;
    states[k] = { ...v, transitions: v.transitions?.filter(t => t.to.split('/')[0] !== name) };
  }
  const initial = m.initial === name ? Object.keys(states)[0] ?? '' : m.initial;
  return { ...m, initial, states };
}

export function uniqueName(existing: Iterable<string>, base: string): string {
  const set = new Set(existing);
  if (!set.has(base)) return base;
  for (let i = 2; ; i++) if (!set.has(`${base}${i}`)) return `${base}${i}`;
}

/** Layered auto layout: BFS depth from the initial state -> column, order -> row. */
export function autoLayout(m: MachineDef): Record<string, [number, number]> {
  const names = Object.keys(m.states);
  const depth = new Map<string, number>();
  const queue = m.initial in m.states ? [m.initial] : names.slice(0, 1);
  if (queue[0]) depth.set(queue[0], 0);
  while (queue.length) {
    const n = queue.shift()!;
    for (const t of m.states[n]?.transitions || []) {
      if (t.to in m.states && !depth.has(t.to)) { depth.set(t.to, depth.get(n)! + 1); queue.push(t.to); }
    }
  }
  let maxD = Math.max(0, ...depth.values());
  for (const n of names) if (!depth.has(n)) depth.set(n, ++maxD);
  const rows = new Map<number, number>();
  const out: Record<string, [number, number]> = {};
  for (const n of names) {
    const d = depth.get(n)!;
    const r = rows.get(d) ?? 0; rows.set(d, r + 1);
    out[n] = [40 + d * 420, 40 + r * 240 + (d % 2) * 60];
  }
  return out;
}

export function layoutFor(def: BrainDef, s: Scope, m: MachineDef): Record<string, [number, number]> {
  const saved = def.ui?.layout?.[scopeKey(s)] || {};
  const auto = autoLayout(m);
  const out: Record<string, [number, number]> = {};
  for (const n of Object.keys(m.states)) out[n] = saved[n] ?? auto[n];
  return out;
}

export function setLayout(def: BrainDef, s: Scope, pos: Record<string, [number, number]>): BrainDef {
  const layout = { ...(def.ui?.layout || {}), [scopeKey(s)]: Object.fromEntries(Object.entries(pos).map(([k, [x, y]]) => [k, [Math.round(x), Math.round(y)] as [number, number]])) };
  return { ...def, ui: { ...(def.ui || {}), layout } };
}

/** Every fnRef (module.fn) and inline Lua body used by the definition. */
export function fnRefs(def: BrainDef): { where: string; ref: string }[] {
  const out: { where: string; ref: string }[] = [];
  const walkM = (m: MachineDef, label: string) => {
    for (const [sn, s] of Object.entries(m.states || {})) {
      for (const f of ['enter', 'tick', 'exit', 'progress'] as const) if (typeof s[f] === 'string') out.push({ where: `${label}/${sn}.${f}`, ref: s[f] as string });
      (s.transitions || []).forEach((t, i) => { if (t.when) out.push({ where: `${label}/${sn} -> ${t.to} #${i + 1}`, ref: t.when }); });
      if (s.machine) walkM(s.machine, `${label}/${sn}`);
    }
  };
  for (const l of def.layers || []) if (l.machine) walkM(l.machine, l.name);
  for (const [n, m] of Object.entries(def.machines || {})) walkM(m, `machines.${n}`);
  return out;
}

export const isFnRef = (s: string) => /^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$/.test(s.trim());

export function newBrain(name: string): BrainDef {
  return {
    brain: name, api: 3, description: '', modules: [],
    layers: [{ name: 'main', priority: 50, rate: 'tick', machine: { initial: 'idle', states: { idle: { terminal: true, transitions: [] } } } }],
  };
}
