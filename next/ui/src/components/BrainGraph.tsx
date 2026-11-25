// The state-machine canvas (React Flow): states as nodes, transitions as edges labelled with condition + priority.
// Edit mode: drag, connect (creates a transition), delete, select. Live mode: active state highlighted with time in
// state, outgoing conditions coloured by their last evaluation, edges flash when taken.
import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow, ReactFlowProvider, Background, Controls, MiniMap, Handle, Position, BaseEdge, EdgeLabelRenderer,
  useInternalNode, useNodesState, useReactFlow, useUpdateNodeInternals, MarkerType,
  type Node, type Edge, type NodeProps, type EdgeProps, type Connection, type NodeChange, type OnSelectionChangeParams,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  type BrainDef, type MachineDef, type Scope, type Transition, condKind, condText, layoutFor, setLayout, updateMachine, deleteState,
} from '../lib/brain';
import type { ConditionTrace, LintResult } from '../api/types';
import { head, innerExits, subMachineOf, usages } from '../lib/subgraph';
import { fmtDuration } from './ui';

export type GraphSelection = { kind: 'state'; state: string } | { kind: 'edge'; from: string; index: number } | null;

export interface LiveInfo {
  active: string | null;           // active state in this machine
  activeSince: number | null;      // epoch ms
  conditions: ConditionTrace[];    // conditions of the active state at this depth
  flashes: { from: string; index: number; at: number }[];
  claims: string[];
  visited?: Record<string, number>; // state -> last left (epoch ms) for the fading trail
  /** active path below the active state (its sub-machine's active inner states) */
  activeInner?: string[];
  now?: number;                     // frame time (epoch ms): live = wall clock, replay / paused = the frame's time
}

interface Props {
  def: BrainDef;
  scope: Scope;
  machine: MachineDef;
  mode: 'edit' | 'live';
  selection: GraphSelection;
  onSelect: (s: GraphSelection) => void;
  onChange?: (def: BrainDef) => void;
  onOpenSub?: (state: string) => void;
  /** several states selected (box / shift / ctrl) */
  onMultiSelect?: (states: string[]) => void;
  /** key under which this level's viewport is remembered (pan / zoom per drill level) */
  viewportKey?: string;
  live?: LiveInfo;
  lint?: LintResult[];
  height?: number | string;
}

type SNodeData = {
  name: string; initial: boolean; terminal: boolean; claims: string[]; fns: string[]; sub: string | null; budget?: number;
  active: boolean; activeSince: number | null; activeMs: number; live: boolean; usedIn?: number; activeInner?: string[]; exits?: number; issues: number; errors: number; trail: number; onOpenSub?: (s: string) => void;
  external?: boolean;
};

const StateNode = memo(function StateNode({ data, selected }: NodeProps<Node<SNodeData>>) {
  const d = data;
  if (d.external) {
    return (
      <div className="snode external" title={d.name === '^' ? 'pop to the parent machine' : 'state in another machine'}>
        <Handle type="target" position={Position.Left} className="h" />
        <div className="snode-name">{d.name === '^' ? '^ parent' : d.name}</div>
      </div>
    );
  }
  return (
    <div className={['snode', selected ? 'sel' : '', d.active ? 'active' : '', d.terminal ? 'terminal' : '', d.errors ? 'has-err' : d.issues ? 'has-warn' : ''].join(' ')}
      style={d.trail > 0 && !d.active ? { boxShadow: `0 0 0 2px rgba(55,178,154,${0.5 * d.trail})` } : undefined}>
      <Handle type="target" position={Position.Left} className="h" />
      <div className="snode-head">
        {d.initial && <span className="tag init" title="initial state">initial</span>}
        <span className="snode-name">{d.name}</span>
        {d.terminal && <span className="tag" title="terminal: no stuck exit needed">terminal</span>}
        {(d.errors > 0 || d.issues > 0) && <span className={'tag ' + (d.errors ? 'err' : 'warn')} title="lint findings">{d.errors || d.issues}</span>}
      </div>
      {d.fns.length > 0 && <div className="snode-fns">{d.fns.join(' · ')}</div>}
      <div className="snode-meta">
        {d.claims.length > 0 && <span title="claims">{d.claims.join(' ')}</span>}
        {d.budget != null && <span title="budget (ms without progress before stuck)">budget {fmtDuration(d.budget)}</span>}
      </div>
      {d.sub && <button className="snode-sub nodrag" onClick={e => { e.stopPropagation(); d.onOpenSub?.(d.name); }} title="open the sub-graph (or double-click the node)">sub-graph: {d.sub}{d.usedIn && d.usedIn > 1 ? ` · used in ${d.usedIn}` : ''} &rarr;</button>}
      {d.active && d.activeInner && d.activeInner.length > 0 && <div className="snode-inner">inside: {d.activeInner.join(' / ')}</div>}
      {d.active && <div className="snode-live">active {fmtDuration(d.activeMs)}</div>}
      <Handle type="source" position={Position.Right} className="h" />
    </div>
  );
});

type TEdgeData = { t: Transition; index: number; label: string; prio: number; parallel: number; parallelIdx: number; self: boolean;
  evalResult: boolean | null | undefined; evalText?: string; flashAt: number; live: boolean; kind: string };

function nodeRect(n: ReturnType<typeof useInternalNode>) {
  const p = n?.internals.positionAbsolute ?? { x: 0, y: 0 };
  return { x: p.x, y: p.y, w: n?.measured.width ?? 180, h: n?.measured.height ?? 60 };
}
function borderPoint(r: { x: number; y: number; w: number; h: number }, tx: number, ty: number) {
  const cx = r.x + r.w / 2, cy = r.y + r.h / 2, dx = tx - cx, dy = ty - cy;
  if (!dx && !dy) return { x: cx, y: cy };
  const sx = (r.w / 2) / Math.abs(dx || 1e-9), sy = (r.h / 2) / Math.abs(dy || 1e-9);
  const s = Math.min(sx, sy);
  return { x: cx + dx * s, y: cy + dy * s };
}

const TransitionEdge = memo(function TransitionEdge({ id, source, target, data, selected, markerEnd }: EdgeProps<Edge<TEdgeData>>) {
  const sn = useInternalNode(source), tn = useInternalNode(target);
  const d = data!;
  const [flash, setFlash] = useState(false);
  useEffect(() => {
    if (!d.flashAt || Date.now() - d.flashAt > 1500) return;
    setFlash(true); const t = window.setTimeout(() => setFlash(false), 1200); return () => window.clearTimeout(t);
  }, [d.flashAt]);
  if (!sn || !tn) return null;
  const a = nodeRect(sn), b = nodeRect(tn);
  let path: string, lx: number, ly: number;
  if (d.self) {
    const x = a.x + a.w - 30, y = a.y;
    const r = 34 + d.parallelIdx * 16;
    path = `M ${x} ${y} C ${x + r} ${y - r * 1.6}, ${x + r * 2} ${y + r * 0.4}, ${a.x + a.w} ${y + 18}`;
    lx = x + r * 1.3; ly = y - r * 0.9;
  } else {
    const ac = { x: a.x + a.w / 2, y: a.y + a.h / 2 }, bc = { x: b.x + b.w / 2, y: b.y + b.h / 2 };
    const dx = bc.x - ac.x, dy = bc.y - ac.y, len = Math.hypot(dx, dy) || 1;
    // normal of the canonical (sorted) direction, so A->B and B->A fan out to opposite sides
    const sgn = source < target ? 1 : -1;
    const nx = (-dy / len) * sgn, ny = (dx / len) * sgn;
    const off = (d.parallelIdx - (d.parallel - 1) / 2) * 70;
    const mx = (ac.x + bc.x) / 2 + nx * off, my = (ac.y + bc.y) / 2 + ny * off;
    const p1 = borderPoint(a, mx, my), p2 = borderPoint(b, mx, my);
    path = `M ${p1.x} ${p1.y} Q ${mx} ${my} ${p2.x} ${p2.y}`;
    // label nearer the source end so opposite-direction edges do not stack their labels
    const t = d.parallel > 1 ? 0.38 : 0.5, u = 1 - t;
    lx = u * u * p1.x + 2 * u * t * mx + t * t * p2.x; ly = u * u * p1.y + 2 * u * t * my + t * t * p2.y;
  }
  const cls = ['tedge', selected ? 'sel' : '', flash ? 'flash' : '', d.live ? (d.evalResult === true ? 'true' : d.evalResult === false ? 'false' : 'idle') : ''].join(' ');
  return (
    <>
      <BaseEdge id={id} path={path} markerEnd={markerEnd} className={cls} interactionWidth={18} />
      <EdgeLabelRenderer>
        <div className={'elabel nodrag nopan ' + cls} style={{ transform: `translate(-50%, -50%) translate(${lx}px, ${ly}px)` }} title={d.evalText || d.label}>
          <span className="eprio" title="priority, then declaration order">{d.prio !== 0 ? `p${d.prio}` : ''}#{d.index + 1}</span>
          <span className={'ekind k-' + d.kind}>{d.kind}</span>
          <span className="etext">{d.label.length > (d.evalText ? 30 : 46) ? d.label.slice(0, d.evalText ? 28 : 44) + '...' : d.label}</span>
          {d.t.hold && !d.evalText ? <span className="ehold">hold {fmtDuration(d.t.hold)}</span> : null}
          {d.evalText && <span className="eval">{d.evalText}</span>}
        </div>
      </EdgeLabelRenderer>
    </>
  );
});

const nodeTypes = { state: StateNode };
const edgeTypes = { transition: TransitionEdge };

function GraphInner(p: Props) {
  const { def, scope, machine, mode, selection, onSelect, onChange, live, lint } = p;
  const rf = useReactFlow();
  const positions = useMemo(() => layoutFor(def, scope, machine), [def, scope, machine]);
  const stateNames = Object.keys(machine.states);

  const lintBy = useMemo(() => {
    const m: Record<string, { e: number; w: number }> = {};
    for (const l of lint || []) if (l.state) { const x = (m[l.state] ||= { e: 0, w: 0 }); if (l.severity === 'error') x.e++; else x.w++; }
    return m;
  }, [lint]);

  const externalTargets = useMemo(() => {
    const s = new Set<string>();
    for (const st of Object.values(machine.states)) for (const t of st.transitions || []) if (!(head(t.to) in machine.states)) s.add(t.to);
    return [...s];
  }, [machine]);

  const buildNodes = useCallback((): Node<SNodeData>[] => {
    const now = Date.now();
    const ns: Node<SNodeData>[] = stateNames.map(n => {
      const st = machine.states[n];
      const fns = (['enter', 'tick', 'exit'] as const).filter(f => st[f]).map(f => `${f}`);
      const left = live?.visited?.[n];
      const sub = subMachineOf(def, st);
      return {
        id: n, type: 'state', position: { x: positions[n][0], y: positions[n][1] }, selected: selection?.kind === 'state' && selection.state === n,
        data: { name: n, initial: machine.initial === n, terminal: !!st.terminal, claims: st.claims || [], fns, sub: st.use ?? (st.machine ? 'inline' : null),
          usedIn: st.use ? usages(def, st.use).length : undefined, activeInner: live?.active === n ? live.activeInner : undefined, exits: sub ? innerExits(sub.machine).length : undefined,
          budget: st.budget, active: live?.active === n, activeSince: live?.active === n ? live.activeSince : null, activeMs: live?.active === n && live.activeSince ? (live.now ?? now) - live.activeSince : 0, live: mode === 'live',
          issues: lintBy[n]?.w ?? 0, errors: lintBy[n]?.e ?? 0, trail: left ? Math.max(0, 1 - ((live?.now ?? now) - left) / 8000) : 0, onOpenSub: p.onOpenSub },
      };
    });
    const maxX = Math.max(0, ...Object.values(positions).map(v => v[0]));
    externalTargets.forEach((t, i) => ns.push({ id: `ext:${t}`, type: 'state', position: { x: maxX + 320, y: 40 + i * 110 }, draggable: false, selectable: false,
      data: { name: t.startsWith('^/') ? `exit to ${t.slice(2)}` : t, initial: false, terminal: false, claims: [], fns: [], sub: null, active: false, activeSince: null, activeMs: 0, live: false, issues: 0, errors: 0, trail: 0, external: true } }));
    return ns;
  }, [stateNames.join('|'), machine, positions, selection, live, mode, lintBy, externalTargets, p.onOpenSub, def]); // eslint-disable-line react-hooks/exhaustive-deps

  const [nodes, setNodes, onNodesChangeBase] = useNodesState<Node<SNodeData>>(buildNodes());
  // keep React Flow's measurements when rebuilding (a node without `measured` is hidden until re-measured,
  // and the live view rebuilds several times a second)
  // ...but only within one graph level: a node of another level with the same name must be measured afresh, or
  // React Flow keeps stale handle bounds and draws none of its edges
  const levelRef = useRef('');
  const level = scopeKeyOf(scope);
  useEffect(() => {
    const fresh = levelRef.current !== level;
    levelRef.current = level;
    setNodes(prev => {
      if (fresh) return buildNodes();
      const byId = new Map(prev.map(n => [n.id, n]));
      return buildNodes().map(n => { const p = byId.get(n.id); return p ? { ...p, ...n } : n; });
    });
  }, [buildNodes, setNodes, level]);
  // when the set of states changes (group / ungroup / add / delete), re-measure every node's handles: a state that
  // comes back under an earlier name otherwise keeps stale handle bounds and its edges are not drawn
  const updateNodeInternals = useUpdateNodeInternals();
  const idsKey = stateNames.join('|') + '#' + externalTargets.join('|');
  useEffect(() => {
    const ids = [...stateNames, ...externalTargets.map(t => `ext:${t}`)];
    const r = requestAnimationFrame(() => updateNodeInternals(ids));
    return () => cancelAnimationFrame(r);
  }, [idsKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const edges: Edge<TEdgeData>[] = useMemo(() => {
    const pairCount = new Map<string, number>(); const pairIdx = new Map<string, number>();
    const key = (a: string, b: string) => [a, b].sort().join('->');
    for (const [from, st] of Object.entries(machine.states)) for (const t of st.transitions || []) {
      const to = head(t.to) in machine.states ? head(t.to) : `ext:${t.to}`;
      pairCount.set(key(from, to), (pairCount.get(key(from, to)) ?? 0) + 1);
    }
    const out: Edge<TEdgeData>[] = [];
    for (const [from, st] of Object.entries(machine.states)) {
      (st.transitions || []).forEach((t, index) => {
        const to = head(t.to) in machine.states ? head(t.to) : `ext:${t.to}`;
        const k = key(from, to); const pi = pairIdx.get(k) ?? 0; pairIdx.set(k, pi + 1);
        const ct = live && live.active === from ? live.conditions.find(c => c.index === index || (c.index == null && c.to === t.to)) : undefined;
        const flash = live?.flashes.filter(f => f.from === from && f.index === index).map(f => f.at).sort().at(-1) ?? 0;
        let evalText: string | undefined;
        if (ct) {
          evalText = ct.result == null ? 'not evaluated' : `${ct.result ? 'true' : 'false'}${ct.value != null ? ` (${typeof ct.value === 'number' ? +ct.value.toFixed(2) : ct.value})` : ''}`;
          if (ct.holdTarget) evalText += ` hold ${fmtDuration(ct.holdMs)}/${fmtDuration(ct.holdTarget)}`;
        }
        out.push({
          id: `${from}#${index}`, source: from, target: to, type: 'transition', selected: selection?.kind === 'edge' && selection.from === from && selection.index === index,
          markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16 },
          data: { t, index, label: (t.to.includes('/') && !t.to.startsWith('^') ? `-> ${t.to.split('/').slice(1).join('/')}: ` : '') + condText(t), prio: t.priority ?? 0, parallel: pairCount.get(k)!, parallelIdx: pi, self: from === to, kind: condKind(t),
            evalResult: ct ? ct.result : undefined, evalText, flashAt: flash, live: mode === 'live' && live?.active === from },
        });
      });
    }
    // a sub-graph's inner exits (`^/t`) drawn as edges leaving the sub-graph node (read-only here; edit them inside)
    for (const [g, st] of Object.entries(machine.states)) {
      const sub = subMachineOf(def, st); if (!sub) continue;
      innerExits(sub.machine).forEach(x => {
        if (x.to === '^' || !(x.to in machine.states)) return;
        out.push({ id: `exit:${g}:${x.from}:${x.index}`, source: g, target: x.to, type: 'transition', deletable: false, selectable: false,
          markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14 }, className: 'exit-edge',
          data: { t: x.t, index: x.index, label: `from ${x.from}: ${condText(x.t)}`, prio: x.t.priority ?? 0, parallel: 1, parallelIdx: 0, self: g === x.to, kind: 'exit',
            evalResult: undefined, flashAt: 0, live: false } });
      });
    }
    return out;
  }, [machine, live, selection, mode, def]);

  const onNodesChange = useCallback((changes: NodeChange<Node<SNodeData>>[]) => {
    onNodesChangeBase(changes.filter(c => c.type !== 'remove'));
  }, [onNodesChangeBase]);

  const onNodeDragStop = useCallback(() => {
    if (mode !== 'edit' || !onChange) return;
    const pos: Record<string, [number, number]> = {};
    for (const n of rf.getNodes()) if (!n.id.startsWith('ext:')) pos[n.id] = [n.position.x, n.position.y];
    onChange(setLayout(def, scope, pos));
  }, [mode, onChange, rf, def, scope]);

  const onConnect = useCallback((c: Connection) => {
    if (mode !== 'edit' || !onChange || !c.source || !c.target || c.target.startsWith('ext:')) return;
    const from = c.source, to = c.target;
    const next = updateMachine(def, scope, m => ({ ...m, states: { ...m.states, [from]: { ...m.states[from], transitions: [...(m.states[from].transitions || []), { to, when: 'false' }] } } }));
    onChange(next);
    onSelect({ kind: 'edge', from, index: (machine.states[from].transitions || []).length });
  }, [mode, onChange, def, scope, machine, onSelect]);

  // dropping a connection anywhere on a state node (not only on its handle) also creates the transition
  const onConnectEnd = useCallback((ev: MouseEvent | TouchEvent, st: any) => {
    if (mode !== 'edit' || !st?.fromNode || st.isValid) return;
    const pt = 'changedTouches' in ev ? ev.changedTouches[0] : ev;
    const el = document.elementFromPoint(pt.clientX, pt.clientY)?.closest('.react-flow__node') as HTMLElement | null;
    const to = el?.dataset.id;
    if (to && !to.startsWith('ext:')) onConnect({ source: st.fromNode.id, target: to, sourceHandle: null, targetHandle: null });
  }, [mode, onConnect]);

  const onDelete = useCallback(({ nodes: dn, edges: de }: { nodes: Node[]; edges: Edge[] }) => {
    if (mode !== 'edit' || !onChange) return;
    let next = def;
    // edges first, highest index first so indices stay valid
    const byState = new Map<string, number[]>();
    for (const e of de.filter(x => !x.id.startsWith('exit:'))) { const [from, i] = e.id.split('#'); byState.set(from, [...(byState.get(from) || []), Number(i)]); }
    for (const [from, idxs] of byState) {
      next = updateMachine(next, scope, m => ({ ...m, states: { ...m.states, [from]: { ...m.states[from], transitions: (m.states[from].transitions || []).filter((_, i) => !idxs.includes(i)) } } }));
    }
    for (const n of dn) if (!n.id.startsWith('ext:')) next = updateMachine(next, scope, m => deleteState(m, n.id));
    if (next !== def) { onChange(next); onSelect(null); }
  }, [mode, onChange, def, scope, onSelect]);

  const onSelectionChange = useCallback((s: OnSelectionChangeParams) => {
    const states = s.nodes.filter(n => !n.id.startsWith('ext:')).map(n => n.id);
    p.onMultiSelect?.(states.length > 1 ? states : []);
    if (states.length > 1) return;
    if (s.nodes.length === 1 && !s.nodes[0].id.startsWith('ext:')) { if (!(selection?.kind === 'state' && selection.state === s.nodes[0].id)) onSelect({ kind: 'state', state: s.nodes[0].id }); }
    else if (s.edges.length === 1) { const [from, i] = s.edges[0].id.split('#'); if (!(selection?.kind === 'edge' && selection.from === from && selection.index === Number(i))) onSelect({ kind: 'edge', from, index: Number(i) }); }
  }, [onSelect, selection, p.onMultiSelect]); // eslint-disable-line react-hooks/exhaustive-deps

  // each drill level keeps its own pan / zoom; a level seen for the first time is fitted
  const vpKey = p.viewportKey ?? scopeKeyOf(scope);
  useEffect(() => {
    const t = window.setTimeout(() => {
      const saved = VIEWPORTS.get(vpKey);
      if (saved) rf.setViewport(saved); else rf.fitView({ padding: 0.2, maxZoom: 1.1 });
    }, 80);
    return () => window.clearTimeout(t);
  }, [vpKey, rf]);

  return (
    <div className="graph" style={{ height: p.height ?? '100%' }}>
      <ReactFlow
        nodes={nodes} edges={edges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
        onNodesChange={onNodesChange} onNodeDragStop={onNodeDragStop} onConnect={onConnect} onConnectEnd={onConnectEnd} onDelete={onDelete}
        onSelectionChange={onSelectionChange} onPaneClick={() => onSelect(null)} onMoveEnd={(_, vp) => VIEWPORTS.set(vpKey, vp)}
        onNodeDoubleClick={(_, n) => { if (!n.id.startsWith('ext:') && subMachineOf(def, machine.states[n.id])) p.onOpenSub?.(n.id); }}
        selectionKeyCode="Shift" multiSelectionKeyCode={['Control', 'Meta', 'Shift']} zoomOnDoubleClick={false}
        nodesDraggable nodesConnectable={mode === 'edit'} elementsSelectable deleteKeyCode={mode === 'edit' ? ['Delete'] : null}
        fitView fitViewOptions={{ padding: 0.2, maxZoom: 1.1 }} minZoom={0.2} maxZoom={2} proOptions={{ hideAttribution: true }} colorMode="dark"
      >
        <Background gap={24} size={1} />
        <Controls showInteractive={false} />
        <MiniMap pannable zoomable nodeColor={n => ((n.data as SNodeData).active ? '#37b29a' : (n.data as SNodeData).external ? '#444' : '#2a363b')} />
      </ReactFlow>
    </div>
  );
}

const VIEWPORTS = new Map<string, { x: number; y: number; zoom: number }>();
const scopeKeyOf = (s: Scope) => (s.kind === 'layer' ? `layer:${s.layer}` : `machine:${s.name}`) + '/' + s.path.join('/');

export function BrainGraph(p: Props) {
  // one React Flow instance per graph level: React would otherwise reuse a node's DOM element for a same-named
  // state of another level, no resize fires, React Flow never re-measures its handles and draws none of its edges
  return <ReactFlowProvider key={scopeKeyOf(p.scope)}><GraphInner {...p} /></ReactFlowProvider>;
}
