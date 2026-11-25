import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api/client';
import type { BotSummary, BrainVersionFull, BrainVersionInfo, LintResult } from '../api/types';
import { BrainGraph, type GraphSelection } from '../components/BrainGraph';
import { CodeEditor } from '../components/CodeEditor';
import { ActionButton, Empty, Field, Modal, fmtDate, useAsync } from '../components/ui';
import { useApp } from '../lib/app';
import {
  type BrainDef, type MachineDef, type Scope, autoLayout, machineAt, newBrain, parseDefinition, scopeKey, serializeDefinition, setLayout, uniqueName, updateMachine,
} from '../lib/brain';
import { moduleExports } from '../lib/luaLint';
import { setBrainModules } from '../lib/monaco';
import { go, href } from '../lib/router';
import { LayerPanel, StatePanel, TransitionPanel, addState } from './brain/Inspector';

type Tab = 'graph' | 'yaml' | `mod:${string}`;

const MODULE_TEMPLATE = (path: string) => `-- ${path}\nlocal M = {}\n\nfunction M.tick()\nend\n\nreturn M\n`;

export function BrainEditorPage({ name, hash }: { name: string; hash?: string }) {
  const isNew = name === 'new';
  const versions = useAsync<BrainVersionInfo[]>(() => (isNew ? Promise.resolve([]) : api.versions(name)), [name]);
  const target = hash || versions.data?.[0]?.hash;
  const full = useAsync<BrainVersionFull | null>(() => (isNew || !target ? Promise.resolve(null) : api.version(name, target)), [name, target]);
  if (isNew) return <NewBrain />;
  if (versions.error) return <div className="page"><Empty>Could not load brain {name}: {versions.error}</Empty></div>;
  if (full.error) return <div className="page"><Empty>Could not load version: {full.error}</Empty></div>;
  if (!full.data) return <div className="page"><Empty>Loading {name}...</Empty></div>;
  return <Editor key={full.data.hash} version={full.data} versions={versions.data || []} onSaved={h => { versions.reload(); go(`/brains/${name}@${h}`); }} />;
}

function NewBrain() {
  const [n, setN] = useState('');
  const [def, setDef] = useState<BrainDef | null>(null);
  if (def) return <Editor version={null} initialDef={def} versions={[]} onSaved={h => go(`/brains/${def.brain}@${h}`)} />;
  return (
    <Modal title="New brain" onClose={() => go('/brains')} footer={<button className="btn primary" disabled={!/^[a-z][a-z0-9_-]*$/.test(n)} onClick={() => setDef(newBrain(n))}>Create</button>}>
      <Field label="name" hint="lower case, ^[a-z][a-z0-9_-]*$"><input autoFocus aria-label="new brain name" value={n} onChange={e => setN(e.target.value.toLowerCase())} /></Field>
    </Modal>
  );
}

function Editor({ version, versions, onSaved, initialDef }: { version: BrainVersionFull | null; versions: BrainVersionInfo[]; onSaved: (hash: string) => void; initialDef?: BrainDef }) {
  const { toast, librarySources, libraries } = useApp();
  const baseText = version?.definition ?? '';
  const baseDef = useMemo(() => initialDef ?? parseDefinition(baseText), [initialDef, baseText]);
  const baseModules: Record<string, string> = useMemo(() => (version && !Array.isArray(version.modules) ? version.modules : {}), [version]);
  const [def, setDef] = useState<BrainDef>(baseDef);
  const [modules, setModules] = useState<Record<string, string>>(baseModules);
  const [tab, setTab] = useState<Tab>('graph');
  const [scope, setScope] = useState<Scope>({ kind: 'layer', layer: baseDef.layers[0]?.name ?? '', path: [] });
  const [sel, setSel] = useState<GraphSelection>(null);
  const [lint, setLint] = useState<LintResult[]>(version?.lint ?? []);
  const [linting, setLinting] = useState(false);
  const [yamlText, setYamlText] = useState('');
  const [yamlError, setYamlError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [applying, setApplying] = useState(false);
  const [revealLine, setRevealLine] = useState<{ path: string; line: number } | null>(null);
  const [inspector, setInspector] = useState<'scope' | 'brain'>('scope');

  const text = useMemo(() => serializeDefinition(def), [def]);
  const dirty = !version || text !== serializeDefinition(baseDef) || JSON.stringify(modules) !== JSON.stringify(baseModules);

  // brain-private modules + libraries resolve for completion and fnRefs
  const brainPrefix = `brain/${def.brain}/`;
  useEffect(() => { setBrainModules((def.modules || []).map(m => m.split('@')[0])); }, [def.modules]);
  const fnRefs = useMemo(() => {
    const out: string[] = [];
    for (const ref of def.modules || []) {
      const p = ref.split('@')[0];
      const src = p.startsWith('brain/') ? modules[p] : librarySources[p];
      if (!src) continue;
      for (const e of moduleExports(src)) out.push(`${p.split('/').pop()}.${e.name}`);
    }
    return out;
  }, [def.modules, modules, librarySources]);

  // host lint, debounced
  const lintSeq = useRef(0);
  useEffect(() => {
    const seq = ++lintSeq.current;
    const t = window.setTimeout(async () => {
      setLinting(true);
      try { const r = await api.lint(text, modules); if (seq === lintSeq.current) setLint(r); }
      catch (e: any) { if (seq === lintSeq.current) setLint([{ severity: 'error', code: 'LINT_CALL', message: e.message, module: null, line: 0, layer: null, state: null }]); }
      finally { if (seq === lintSeq.current) setLinting(false); }
    }, 700);
    return () => window.clearTimeout(t);
  }, [text, modules]);

  const machine = machineAt(def, scope);
  const isSub = scope.path.length > 0 || scope.kind === 'machine';
  const layer = scope.kind === 'layer' ? def.layers.find(l => l.name === scope.layer) : undefined;
  const scopeLint = lint.filter(l => scope.kind === 'layer' ? l.layer === scope.layer : l.layer == null);
  const errors = lint.filter(l => l.severity === 'error').length, warnings = lint.length - errors;

  const onMachine = useCallback((m: MachineDef) => setDef(d => updateMachine(d, scope, () => m)), [scope]);
  const openSub = (state: string) => {
    const st = machine?.states[state]; if (!st) return;
    if (st.machine) { setScope({ ...scope, path: [...scope.path, state] }); setSel(null); }
    else if (st.use && def.machines?.[st.use]) { setScope({ kind: 'machine', name: st.use, path: [] }); setSel(null); }
    else if (st.use) toast(`'${st.use}' is resolved at runtime (contains \${expr}) or is not defined under machines`, 'info');
  };

  const save = async (message: string) => {
    try {
      const info = await api.createVersion(def.brain, text, modules, message);
      toast(`saved ${def.brain}@${info.hash}`, 'ok');
      setSaving(false);
      onSaved(info.hash);
    } catch (e: any) {
      if (e instanceof ApiError && e.lint.length) { setLint(e.lint); toast(`not saved: ${e.lint.filter(l => l.severity === 'error').length} lint error(s)`, 'error'); }
      else toast(e.message, 'error');
    }
  };

  const enterYaml = () => { setYamlText(text); setYamlError(null); setTab('yaml'); };
  const onYaml = (v: string) => {
    setYamlText(v);
    try { const d = parseDefinition(v); if (!Array.isArray(d.layers)) throw new Error('layers: must be a list'); setDef(d); setYamlError(null); }
    catch (e: any) { setYamlError(e.message); }
  };

  const modulePaths = (def.modules || []).map(m => m.split('@')[0]);
  const addModule = () => {
    const short = uniqueName(modulePaths.filter(p => p.startsWith(brainPrefix)).map(p => p.slice(brainPrefix.length)), 'module');
    const path = brainPrefix + short;
    setModules(m => ({ ...m, [path]: MODULE_TEMPLATE(path) }));
    setDef(d => ({ ...d, modules: [...(d.modules || []), path] }));
    setTab(`mod:${path}`);
  };
  const jumpToLint = (l: LintResult) => {
    if (l.module) { setTab(`mod:${l.module}`); setRevealLine({ path: l.module, line: l.line || 1 }); return; }
    if (l.layer) { setTab('graph'); setScope({ kind: 'layer', layer: l.layer, path: [] }); if (l.state) setSel({ kind: 'state', state: l.state }); }
  };

  const tabs: { id: Tab; label: string }[] = [{ id: 'graph', label: 'Graph' }, { id: 'yaml', label: 'brain.yaml' },
    ...modulePaths.map(p => ({ id: `mod:${p}` as Tab, label: p.split('/').pop()! + (p.startsWith('brain/') ? '' : ' (lib)') }))];

  return (
    <div className="editor">
      <div className="editor-bar">
        <a href={href('/brains')} className="muted">Brains</a><span className="muted">/</span>
        <strong>{def.brain}</strong>
        {version ? <span className="mono muted" title={fmtDate(version.created)}>@{version.hash}</span> : <span className="tag">unsaved</span>}
        {dirty && <span className="tag warn">modified</span>}
        <span className={'lint-sum ' + (errors ? 'err' : warnings ? 'warn' : 'ok')} title="host lint">{linting ? 'linting...' : errors ? `${errors} error${errors > 1 ? 's' : ''}` : 'lint ok'}{warnings ? `, ${warnings} warning${warnings > 1 ? 's' : ''}` : ''}</span>
        <div className="grow" />
        {versions.length > 0 && (
          <select aria-label="version" value={version?.hash} onChange={e => go(`/brains/${def.brain}@${e.target.value}`)}>
            {versions.map(v => <option key={v.hash} value={v.hash}>{v.hash} {fmtDate(v.created, false)} {v.message}</option>)}
          </select>
        )}
        {version && <a className="btn ghost" href={href(`/versions/${def.brain}?to=${version.hash}`)}>Compare</a>}
        <button className="btn" disabled={!dirty} onClick={() => { setDef(baseDef); setModules(baseModules); }}>Revert</button>
        <button className="btn primary" disabled={!dirty || errors > 0} title={errors ? 'fix lint errors first' : ''} onClick={() => setSaving(true)}>Save as new version</button>
        <button className="btn" disabled={!version || dirty} title={dirty ? 'save first' : ''} onClick={() => setApplying(true)}>Apply...</button>
      </div>
      <div className="editor-body">
        <aside className="editor-side">
          <button className={'side-item' + (inspector === 'brain' ? ' on' : '')} onClick={() => setInspector('brain')}>Brain settings</button>
          <div className="side-h">Layers <span className="muted">by priority</span></div>
          {[...def.layers].sort((a, b) => b.priority - a.priority).map(l => (
            <button key={l.name} className={'side-item' + (scope.kind === 'layer' && scope.layer === l.name && tab === 'graph' ? ' on' : '')}
              onClick={() => { setScope({ kind: 'layer', layer: l.name, path: [] }); setSel(null); setTab('graph'); setInspector('scope'); }}>
              <span>{l.name}</span><span className="muted">{l.priority}{l.enabled === false ? ' off' : ''}</span>
              {lint.some(x => x.layer === l.name && x.severity === 'error') && <span className="dot err" />}
            </button>
          ))}
          <button className="btn sm ghost" onClick={() => {
            const n = uniqueName(def.layers.map(l => l.name), 'layer');
            setDef(d => ({ ...d, layers: [...d.layers, { name: n, priority: 10, rate: 'tick', machine: { initial: 'idle', states: { idle: { terminal: true, transitions: [] } } } }] }));
            setScope({ kind: 'layer', layer: n, path: [] }); setTab('graph');
          }}>+ layer</button>
          <div className="side-h">Machines <span className="muted">reusable (use)</span></div>
          {Object.keys(def.machines || {}).map(n => (
            <button key={n} className={'side-item' + (scope.kind === 'machine' && scope.name === n && tab === 'graph' ? ' on' : '')}
              onClick={() => { setScope({ kind: 'machine', name: n, path: [] }); setSel(null); setTab('graph'); setInspector('scope'); }}>{n}</button>
          ))}
          <button className="btn sm ghost" onClick={() => {
            const n = uniqueName(Object.keys(def.machines || {}), 'machine');
            setDef(d => ({ ...d, machines: { ...(d.machines || {}), [n]: { initial: 'start', states: { start: { transitions: [{ to: '^', after: 1000 }] } } } } }));
            setScope({ kind: 'machine', name: n, path: [] }); setTab('graph');
          }}>+ machine</button>
          <div className="side-h">Modules</div>
          {modulePaths.map(p => (
            <button key={p} className={'side-item' + (tab === `mod:${p}` ? ' on' : '')} onClick={() => setTab(`mod:${p}`)} title={p}>
              <span className="mono small">{p}</span>{lint.some(x => x.module === p && x.severity === 'error') && <span className="dot err" />}
            </button>
          ))}
          <div className="row wrap">
            <button className="btn sm ghost" onClick={addModule}>+ module</button>
            <select aria-label="add library" value="" onChange={e => { const p = e.target.value; if (p) setDef(d => ({ ...d, modules: [...(d.modules || []), p] })); }}>
              <option value="">+ library...</option>
              {libraries.filter(l => !modulePaths.includes(l.path)).map(l => <option key={l.path} value={l.path}>{l.path}</option>)}
            </select>
          </div>
          <div className="side-h">Lint {linting && <span className="spin" />}</div>
          <ul className="lint-list">
            {lint.length === 0 && <li className="muted">no findings</li>}
            {lint.map((l, i) => (
              <li key={i} className={l.severity}><button className="linkish" onClick={() => jumpToLint(l)}>
                <span className="lint-code">{l.code}</span> {l.message}
                <span className="muted small"> {[l.module, l.layer, l.state].filter(Boolean).join(' / ')}{l.line ? `:${l.line}` : ''}</span>
              </button></li>
            ))}
          </ul>
        </aside>
        <main className="editor-main">
          <div className="tabs">
            {tabs.map(t => <button key={t.id} className={'tab' + (tab === t.id ? ' on' : '')} onClick={() => (t.id === 'yaml' ? enterYaml() : setTab(t.id))}>{t.label}</button>)}
          </div>
          {tab === 'graph' && (
            <div className="graph-wrap">
              <div className="graph-bar">
                <Breadcrumb scope={scope} onScope={s => { setScope(s); setSel(null); }} />
                <div className="grow" />
                {machine && <button className="btn sm" onClick={() => { const [m, n] = addState(machine); onMachine(m); setSel({ kind: 'state', state: n }); }}>+ state</button>}
                {machine && <button className="btn sm ghost" onClick={() => setDef(d => setLayout(d, scope, autoLayout(machine)))}>auto layout</button>}
              </div>
              {machine ? (
                <BrainGraph def={def} scope={scope} machine={machine} mode="edit" selection={sel} onSelect={s => { setSel(s); setInspector('scope'); }} onChange={setDef}
                  onOpenSub={openSub} lint={scopeLint} />
              ) : <Empty>No machine here.</Empty>}
            </div>
          )}
          {tab === 'yaml' && (
            <div className="yaml-wrap">
              {yamlError ? <div className="banner err">YAML: {yamlError} (the graph keeps the last valid definition)</div> : <div className="banner">Editing the definition text directly; the graph follows on every valid parse. Node positions live under <code>ui.layout</code> (ignored by the runtime).</div>}
              <CodeEditor value={yamlText} onChange={onYaml} language="yaml" height="calc(100% - 34px)" ariaLabel="brain.yaml" />
            </div>
          )}
          {tab.startsWith('mod:') && (() => {
            const p = tab.slice(4);
            const editable = p.startsWith(brainPrefix);
            const src = editable ? modules[p] ?? '' : librarySources[p] ?? '';
            return (
              <div className="yaml-wrap">
                <div className="banner">
                  {editable ? <>Brain-private module, versioned with this brain.</> : <>Library <code>{p}</code> (read-only here). <a href={href(`/libraries/${encodeURIComponent(p)}`)}>Edit in Libraries</a></>}
                  {editable && <button className="btn ghost sm danger-text" onClick={() => { setDef(d => ({ ...d, modules: (d.modules || []).filter(m => m.split('@')[0] !== p) })); setModules(m => { const n = { ...m }; delete n[p]; return n; }); setTab('graph'); }}>remove module</button>}
                  {!editable && <button className="btn ghost sm" onClick={() => { setDef(d => ({ ...d, modules: (d.modules || []).filter(m => m.split('@')[0] !== p) })); setTab('graph'); }}>remove from brain</button>}
                </div>
                <CodeEditor value={src} readOnly={!editable} onChange={v => setModules(m => ({ ...m, [p]: v }))} height="calc(100% - 34px)"
                  markers={lint.filter(l => l.module === p)} ariaLabel={p} revealLine={revealLine?.path === p ? revealLine.line : undefined} />
              </div>
            );
          })()}
        </main>
        <aside className="editor-inspector">
          <datalist id="fnrefs">{fnRefs.map(f => <option key={f} value={f} />)}</datalist>
          <datalist id="machine-names">{Object.keys(def.machines || {}).map(f => <option key={f} value={f} />)}</datalist>
          {inspector === 'brain' ? <BrainSettings def={def} onChange={setDef} /> : machine && tab === 'graph' ? (
            sel?.kind === 'state' && machine.states[sel.state] ? (
              <StatePanel def={def} machine={machine} name={sel.state} onMachine={onMachine} onRename={n => setSel({ kind: 'state', state: n })} fnRefs={fnRefs}
                machineNames={Object.keys(def.machines || {})} lint={scopeLint} onSelectEdge={i => setSel({ kind: 'edge', from: sel.state, index: i })} onOpenSub={() => openSub(sel.state)} isSub={isSub} />
            ) : sel?.kind === 'edge' ? (
              <TransitionPanel machine={machine} from={sel.from} index={sel.index} onMachine={onMachine} fnRefs={fnRefs} onSelectState={s => setSel({ kind: 'state', state: s })} isSub={isSub} onDeleted={() => setSel({ kind: 'state', state: sel.from })} />
            ) : layer && scope.path.length === 0 ? (
              <LayerPanel layer={layer} canDelete={def.layers.length > 1}
                onChange={l => { setDef(d => ({ ...d, layers: d.layers.map(x => (x.name === layer.name ? l : x)) })); if (l.name !== layer.name) setScope({ kind: 'layer', layer: l.name, path: [] }); }}
                onDelete={() => { const rest = def.layers.filter(x => x.name !== layer.name); setDef(d => ({ ...d, layers: rest })); setScope({ kind: 'layer', layer: rest[0].name, path: [] }); }} />
            ) : (
              <div className="panel"><h4>{scopeKey(scope)}</h4><div className="hint">Select a state or a transition.</div>
                {scope.kind === 'machine' && scope.path.length === 0 && (
                  <button className="btn ghost sm danger-text" onClick={() => { const n = scope.name; setDef(d => { const ms = { ...(d.machines || {}) }; delete ms[n]; return { ...d, machines: ms }; }); setScope({ kind: 'layer', layer: def.layers[0].name, path: [] }); }}>delete machine</button>
                )}
              </div>
            )
          ) : <div className="panel"><div className="hint">Completion lists the C# helpers from the API manifest (Ctrl+Space), the brain's modules and the libraries; hover a name for its docs.</div></div>}
        </aside>
      </div>
      {saving && <SaveModal onClose={() => setSaving(false)} onSave={save} warnings={warnings} />}
      {applying && version && <ApplyModal brain={def.brain} hash={version.hash} onClose={() => setApplying(false)} />}
    </div>
  );
}

function Breadcrumb({ scope, onScope }: { scope: Scope; onScope: (s: Scope) => void }) {
  const root = scope.kind === 'layer' ? `layer ${scope.layer}` : `machine ${scope.name}`;
  return (
    <div className="crumbs">
      <button className="linkish" onClick={() => onScope({ ...scope, path: [] })}>{root}</button>
      {scope.path.map((p, i) => <span key={i}> / <button className="linkish" onClick={() => onScope({ ...scope, path: scope.path.slice(0, i + 1) })}>{p}</button></span>)}
    </div>
  );
}

function BrainSettings({ def, onChange }: { def: BrainDef; onChange: (d: BrainDef) => void }) {
  const [bbText, setBbText] = useState(JSON.stringify(def.blackboard ?? {}, null, 2));
  const [err, setErr] = useState<string | null>(null);
  return (
    <div className="panel">
      <h4>Brain</h4>
      <Field label="name"><input value={def.brain} disabled aria-label="brain name" /></Field>
      <Field label="api"><input value={def.api} disabled aria-label="api version" /></Field>
      <Field label="description"><textarea aria-label="description" rows={3} value={def.description ?? ''} onChange={e => onChange({ ...def, description: e.target.value || undefined })} /></Field>
      <Field label="initial bot blackboard (JSON)" hint={err ?? 'bb.bot values on first start; kept across versions and relogs'}>
        <CodeEditor value={bbText} language="plaintext" height={120} compact onChange={v => {
          setBbText(v);
          try { const o = JSON.parse(v || '{}'); if (typeof o !== 'object' || Array.isArray(o)) throw new Error('must be an object'); onChange({ ...def, blackboard: Object.keys(o).length ? o : undefined }); setErr(null); }
          catch (e: any) { setErr('JSON: ' + e.message); }
        }} />
      </Field>
    </div>
  );
}

function SaveModal({ onClose, onSave, warnings }: { onClose: () => void; onSave: (m: string) => Promise<void>; warnings: number }) {
  const [msg, setMsg] = useState('');
  return (
    <Modal title="Save as new version" onClose={onClose} footer={<ActionButton kind="primary" onClick={() => onSave(msg)} disabled={!msg.trim()}>Save</ActionButton>}>
      <p className="hint">Versions are immutable and content-addressed (definition + modules + pinned libraries + API version).</p>
      {warnings > 0 && <p className="warn-text">{warnings} lint warning(s) remain.</p>}
      <Field label="message"><input autoFocus aria-label="version message" value={msg} onChange={e => setMsg(e.target.value)} placeholder="what changed and why" /></Field>
    </Modal>
  );
}

export function ApplyModal({ brain, hash, onClose, presetBot }: { brain: string; hash: string; onClose: () => void; presetBot?: string }) {
  const { toast, host } = useApp();
  const bots = useAsync<BotSummary[]>(() => api.bots(), []);
  const brainInfo = useAsync(() => api.brains(), []);
  const channels = brainInfo.data?.find(b => b.name === brain)?.channels ?? {};
  const channelNames = [...new Set([...(host?.channels ?? []), ...Object.keys(channels)])];
  const [mode, setMode] = useState<'bots' | 'channel'>('bots');
  const [picked, setPicked] = useState<string[]>(presetBot ? [presetBot] : []);
  const [channel, setChannel] = useState(channelNames[0] ?? 'candidate');
  const [keep, setKeep] = useState(true);
  const [reason, setReason] = useState('');
  const run = async () => {
    if (mode === 'channel') {
      await api.setChannel(brain, channel, hash);
      toast(`channel ${brain}/${channel} -> ${hash}`, 'ok');
    } else {
      for (const id of picked) await api.applyBrain(id, { name: brain, hash, keepState: keep, reason: reason || undefined });
      toast(`applied ${brain}@${hash} to ${picked.join(', ')}`, 'ok');
    }
    onClose();
  };
  return (
    <Modal title={`Apply ${brain}@${hash}`} onClose={onClose} footer={<ActionButton kind="primary" onClick={run} disabled={mode === 'bots' && !picked.length}>Apply</ActionButton>}>
      <div className="row">
        <label className="check"><input type="radio" checked={mode === 'bots'} onChange={() => setMode('bots')} /> to bots (pinned to this version)</label>
        <label className="check"><input type="radio" checked={mode === 'channel'} onChange={() => setMode('channel')} /> move a channel</label>
      </div>
      {mode === 'bots' ? (
        <>
          <div className="pick-list">
            {(bots.data || []).map(b => (
              <label key={b.id} className="check">
                <input type="checkbox" checked={picked.includes(b.id)} onChange={e => setPicked(p => (e.target.checked ? [...p, b.id] : p.filter(x => x !== b.id)))} />
                {b.id} <span className="muted">{b.brain ?? 'no brain'}{b.versionHash ? '@' + b.versionHash : ''} {b.channel ? `(${b.channel})` : ''} {b.state}</span>
              </label>
            ))}
          </div>
          <label className="check"><input type="checkbox" checked={keep} onChange={e => setKeep(e.target.checked)} /> keep state (hot reload: layers keep active states that still exist; bb.bot always kept)</label>
          <Field label="why"><input aria-label="reason" value={reason} onChange={e => setReason(e.target.value)} placeholder="recorded in the assignment log" /></Field>
        </>
      ) : (
        <>
          <Field label="channel"><input aria-label="channel" list="chan-list" value={channel} onChange={e => setChannel(e.target.value)} /></Field>
          <datalist id="chan-list">{channelNames.map(c => <option key={c} value={c} />)}</datalist>
          <p className="hint">Currently: {channels[channel] ? <span className="mono">{channels[channel]}</span> : 'unset'}. Bots following the channel switch at their next tick boundary.</p>
        </>
      )}
    </Modal>
  );
}
