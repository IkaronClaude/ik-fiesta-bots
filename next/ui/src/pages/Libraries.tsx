import { useEffect, useMemo, useState } from 'react';
import { api, ApiError } from '../api/client';
import type { LibraryVersion, LintResult } from '../api/types';
import { CodeEditor, DiffView } from '../components/CodeEditor';
import { ActionButton, Empty, Field, Modal, fmtDate, useAsync } from '../components/ui';
import { useApp } from '../lib/app';
import { fnRefs, parseDefinition } from '../lib/brain';
import { moduleExports } from '../lib/luaLint';
import { go, href } from '../lib/router';

export function BrainsPage() {
  const brains = useAsync(() => api.brains(), []);
  return (
    <div className="page">
      <div className="page-head"><h2>Brains</h2><div className="grow" /><a className="btn primary" href={href('/brains/new')}>New brain</a></div>
      {brains.error && <div className="banner err">{brains.error}</div>}
      {!brains.data?.length ? <Empty>{brains.loading ? 'loading' : 'No brains yet.'}</Empty> : (
        <table className="trace">
          <thead><tr><th>brain</th><th>description</th><th>latest</th><th>versions</th><th>channels</th><th /></tr></thead>
          <tbody>{brains.data.map(b => (
            <tr key={b.name}>
              <td><a className="strong" href={href(`/brains/${b.name}`)}>{b.name}</a></td><td className="small">{b.description}</td>
              <td className="mono">{b.latest}</td><td>{b.versions ?? '-'}</td>
              <td className="small">{Object.entries(b.channels || {}).map(([c, h]) => <div key={c}><span className="tag">{c}</span> <span className="mono">{h}</span></div>)}</td>
              <td><a className="btn sm" href={href(`/brains/${b.name}`)}>edit</a> <a className="btn sm ghost" href={href(`/versions/${b.name}`)}>versions</a></td>
            </tr>))}
          </tbody>
        </table>
      )}
    </div>
  );
}

export function LibrariesPage({ path }: { path?: string }) {
  const { libraries, refreshLibraries, toast } = useApp();
  const [creating, setCreating] = useState(false);
  const sel = path ? decodeURIComponent(path) : libraries[0]?.path;
  return (
    <div className="page libs">
      <aside className="libs-side">
        <div className="page-head"><h3>Libraries</h3><div className="grow" /><button className="btn sm primary" onClick={() => setCreating(true)}>New</button></div>
        {libraries.map(l => (
          <button key={l.path} className={'side-item' + (l.path === sel ? ' on' : '')} onClick={() => go(`/libraries/${encodeURIComponent(l.path)}`)}>
            <span className="mono small">{l.path}</span>{l.readOnly && <span className="tag">host</span>}
          </button>
        ))}
        {!libraries.length && <Empty>none</Empty>}
      </aside>
      <main className="libs-main">{sel ? <LibraryEditor key={sel} path={sel} /> : <Empty>Pick a library.</Empty>}</main>
      {creating && <NewLibrary onClose={() => setCreating(false)} onCreated={async p => { await refreshLibraries(); toast(`created ${p}`, 'ok'); go(`/libraries/${encodeURIComponent(p)}`); }} />}
    </div>
  );
}

function NewLibrary({ onClose, onCreated }: { onClose: () => void; onCreated: (p: string) => void }) {
  const [p, setP] = useState('user/');
  const ok = /^user\/[a-z0-9_-]+\/[a-z0-9_/-]+$/.test(p);
  return (
    <Modal title="New user library" onClose={onClose} footer={<ActionButton kind="primary" disabled={!ok} onClick={async () => {
      await api.saveLibrary(p, `-- ${p}\nlocal M = {}\n\n-- a user helper, e.g. a policy the operator tunes\nfunction M.example(x)\n  return x\nend\n\nreturn M\n`, 'new library');
      onClose(); onCreated(p);
    }}>Create</ActionButton>}>
      <Field label="path" hint="user/<owner>/<name>"><input autoFocus aria-label="library path" value={p} onChange={e => setP(e.target.value.toLowerCase())} /></Field>
    </Modal>
  );
}

function LibraryEditor({ path }: { path: string }) {
  const { libraries, refreshLibraries, toast } = useApp();
  const info = libraries.find(l => l.path === path);
  const versions = useAsync<LibraryVersion[]>(() => api.libraryVersions(path), [path, info?.latest]);
  const [hash, setHash] = useState<string | null>(null);
  const cur = hash ?? versions.data?.[0]?.hash;
  const ver = useAsync<LibraryVersion | null>(() => (cur ? api.libraryVersion(path, cur) : Promise.resolve(null)), [path, cur]);
  const [src, setSrc] = useState<string | null>(null);
  const [lint, setLint] = useState<LintResult[]>([]);
  const [msg, setMsg] = useState('');
  const [compare, setCompare] = useState<string | null>(null);
  const [usages, setUsages] = useState<{ brain: string; where: string; ref: string }[] | null>(null);
  const [offerUpdate, setOfferUpdate] = useState<string[] | null>(null);
  const base = ver.data?.source ?? '';
  const text = src ?? base;
  const readOnly = !!info?.readOnly || path.startsWith('lib/');
  const dirty = src != null && src !== base;
  const exports = useMemo(() => moduleExports(text), [text]);
  const cmp = useAsync<LibraryVersion | null>(() => (compare ? api.libraryVersion(path, compare) : Promise.resolve(null)), [path, compare]);

  useEffect(() => {
    if (readOnly) return;
    const t = window.setTimeout(() => api.lintLibrary(path, text).then(setLint).catch(() => setLint([])), 600);
    return () => window.clearTimeout(t);
  }, [text, path, readOnly]);

  const findUsages = async () => {
    const out: { brain: string; where: string; ref: string }[] = [];
    const short = path.split('/').pop()!;
    const names = new Set(exports.map(e => e.name));
    for (const b of info?.usedBy ?? []) {
      try {
        const v = await api.version(b, (await api.brains()).find(x => x.name === b)!.latest);
        const def = parseDefinition(v.definition);
        for (const r of fnRefs(def)) if (r.ref.startsWith(short + '.') || [...names].some(n => r.ref.includes(`.${n}(`))) out.push({ brain: b, where: r.where, ref: r.ref });
        const mods: Record<string, string> = Array.isArray(v.modules) ? {} : v.modules;
        for (const [mp, ms] of Object.entries(mods)) ms.split('\n').forEach((line, i) => {
          if (line.includes(`"${path}"`) || line.includes(`'${path}'`) || [...names].some(n => new RegExp(`\\.${n}\\s*\\(`).test(line))) out.push({ brain: b, where: `${mp}:${i + 1}`, ref: line.trim() });
        });
      } catch { /* skip */ }
    }
    setUsages(out);
  };

  const save = async () => {
    try {
      const r = await api.saveLibrary(path, text, msg);
      toast(`saved ${path}@${r.hash}`, 'ok'); setMsg(''); setSrc(null); setHash(null);
      await refreshLibraries(); versions.reload();
      if (info?.usedBy?.length) setOfferUpdate(info.usedBy);
    } catch (e: any) {
      if (e instanceof ApiError && e.lint.length) { setLint(e.lint); toast('not saved: lint errors', 'error'); } else toast(e.message, 'error');
    }
  };

  const errors = lint.filter(l => l.severity === 'error').length;
  return (
    <div className="lib-editor">
      <div className="editor-bar">
        <strong className="mono">{path}</strong>
        {readOnly && <span className="tag">host-provided, read-only</span>}
        {dirty && <span className="tag warn">modified</span>}
        <span className={'lint-sum ' + (errors ? 'err' : lint.length ? 'warn' : 'ok')}>{errors ? `${errors} errors` : lint.length ? `${lint.length} warnings` : 'lint ok'}</span>
        <div className="grow" />
        <select aria-label="library version" value={cur ?? ''} onChange={e => { setHash(e.target.value); setSrc(null); }}>
          {(versions.data || []).map(v => <option key={v.hash} value={v.hash}>{v.hash} {fmtDate(v.created, false)} {v.message}</option>)}
        </select>
        <select aria-label="compare with" value={compare ?? ''} onChange={e => setCompare(e.target.value || null)}>
          <option value="">compare with...</option>{(versions.data || []).filter(v => v.hash !== cur).map(v => <option key={v.hash} value={v.hash}>{v.hash} {v.message}</option>)}
        </select>
        <button className="btn ghost" onClick={findUsages}>Find usages</button>
        {!readOnly && <><input aria-label="library message" placeholder="message" value={msg} onChange={e => setMsg(e.target.value)} />
          <ActionButton kind="primary" disabled={!dirty || !msg.trim() || errors > 0} onClick={save}>Save version</ActionButton></>}
      </div>
      <div className="lib-body">
        <div className="lib-code">
          {compare && cmp.data ? <DiffView original={cmp.data.source ?? ''} modified={text} height="100%" />
            : <CodeEditor value={text} onChange={setSrc} readOnly={readOnly} height="100%" markers={lint} ariaLabel={path} />}
        </div>
        <aside className="lib-info">
          <div className="side-h">Exports</div>
          <ul className="exports">{exports.map(e => <li key={e.name}><span className="mono">{e.name}({e.params})</span>{e.doc && <div className="muted small">{e.doc}</div>}</li>)}</ul>
          <div className="side-h">Used by</div>
          <div>{info?.usedBy?.length ? info.usedBy.map(b => <a key={b} className="tag" href={href(`/brains/${b}`)}>{b}</a>) : <span className="muted small">no brain</span>}</div>
          {usages && <><div className="side-h">Usages ({usages.length})</div>
            <ul className="usages">{usages.map((u, i) => <li key={i}><a href={href(`/brains/${u.brain}`)}>{u.brain}</a> <span className="mono small">{u.where}</span><div className="mono small muted">{u.ref.slice(0, 90)}</div></li>)}</ul></>}
          <div className="side-h">Lint</div>
          <ul className="lint-list">{lint.map((l, i) => <li key={i} className={l.severity}>{l.line ? `${l.line}: ` : ''}{l.message}</li>)}{!lint.length && <li className="muted">none</li>}</ul>
          <div className="hint">Generic checks (<code>canUse</code>, <code>skills.whyNot</code>, ...) are C# helpers; libraries hold the subjective policy (e.g. <code>isGoodHealSkill</code>).</div>
        </aside>
      </div>
      {offerUpdate && <UpdateBrains path={path} brains={offerUpdate} onClose={() => setOfferUpdate(null)} />}
    </div>
  );
}

/** "update brains that use it": re-save each brain with the library re-resolved to its latest version. */
function UpdateBrains({ path, brains, onClose }: { path: string; brains: string[]; onClose: () => void }) {
  const { toast } = useApp();
  const [picked, setPicked] = useState<string[]>(brains);
  return (
    <Modal title={`Update brains that use ${path}`} onClose={onClose} footer={<ActionButton kind="primary" disabled={!picked.length} onClick={async () => {
      for (const b of picked) {
        const latest = (await api.brains()).find(x => x.name === b)!.latest;
        const v = await api.version(b, latest);
        const def = v.definition;
        const mods: Record<string, string> = Array.isArray(v.modules) ? {} : v.modules;
        // unpinned references re-resolve to the latest library version when the brain version is created
        const unpinned = def.replace(new RegExp(`(${path.replace(/[/-]/g, m => '\\' + m)})@[0-9a-f]+`, 'g'), '$1');
        const r = await api.createVersion(b, unpinned, mods, `update ${path}`);
        toast(`${b}@${r.hash}`, 'ok');
      }
      onClose();
    }}>Create new versions</ActionButton>}>
      <p className="hint">Each brain gets a new version that pins the new library version. Bots keep their current version until you apply it.</p>
      {brains.map(b => <label key={b} className="check"><input type="checkbox" checked={picked.includes(b)} onChange={e => setPicked(p => (e.target.checked ? [...p, b] : p.filter(x => x !== b)))} /> {b}</label>)}
    </Modal>
  );
}
