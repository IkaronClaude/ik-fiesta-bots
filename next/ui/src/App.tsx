import { useState } from 'react';
import { useWsStatus } from './api/ws';
import { getToken, setToken } from './api/client';
import { Field } from './components/ui';
import { AppProvider, useApp } from './lib/app';
import { href, useRoute } from './lib/router';
import { BrainEditorPage } from './pages/BrainEditor';
import { FleetPage } from './pages/Fleet';
import { BrainsPage, LibrariesPage } from './pages/Libraries';
import { VersionsPage } from './pages/Versions';
import { BotPage } from './pages/bot/BotPage';

export function App() {
  return <AppProvider><Shell /></AppProvider>;
}

function Shell() {
  const r = useRoute();
  const { host, hostError, manifest, toasts, dismiss } = useApp();
  const ws = useWsStatus();
  const section = r[0] ?? 'fleet';
  let page;
  if (section === 'bots' && r[1]) page = <BotPage key={r[1]} id={decodeURIComponent(r[1])} tab={r[2]} />;
  else if (section === 'brains' && r[1]) { const [n, h] = decodeURIComponent(r[1]).split('@'); page = <BrainEditorPage key={r[1]} name={n} hash={h} />; }
  else if (section === 'brains') page = <BrainsPage />;
  else if (section === 'libraries') page = <LibrariesPage path={r[1]} />;
  else if (section === 'versions' && r[1]) page = <VersionsPage brain={decodeURIComponent(r[1])} />;
  else if (section === 'settings') page = <Settings />;
  else page = <FleetPage />;
  const nav = [['fleet', 'Fleet'], ['brains', 'Brains'], ['libraries', 'Libraries'], ['settings', 'Settings']];
  return (
    <div className="app">
      <header className="top">
        <span className="brand">bot console</span>
        <nav>{nav.map(([k, l]) => <a key={k} href={href('/' + k)} className={section === k || (k === 'brains' && section === 'versions') || (k === 'fleet' && section === 'bots') ? 'on' : ''}>{l}</a>)}</nav>
        <div className="grow" />
        {host?.mock && <span className="tag warn" title="the mock host from next/ui/mock">mock host</span>}
        {hostError && <span className="tag warn" title={hostError}>host info unavailable</span>}
        <span className="small muted" title="API manifest version">api {manifest?.apiVersion ?? '?'}</span>
        <span className={'dot ' + (ws === 'open' ? 'ok' : ws === 'connecting' ? 'warn' : 'off')} title={`live feed: ${ws}`} />
      </header>
      <div className="content">{page}</div>
      <div className="toasts" aria-live="polite">
        {toasts.map(t => <div key={t.id} className={'toast ' + t.kind} onClick={() => dismiss(t.id)}>{t.text}</div>)}
      </div>
    </div>
  );
}

function Settings() {
  const [tok, setTok] = useState(getToken());
  const { host, manifest, reloadAll, toast } = useApp();
  return (
    <div className="page narrow">
      <h2>Settings</h2>
      <div className="card">
        <Field label="API bearer token" hint="stored in this browser only; sent as Authorization: Bearer and as ?token= on the WebSocket">
          <input aria-label="token" type="password" value={tok} onChange={e => setTok(e.target.value)} />
        </Field>
        <button className="btn primary" onClick={() => { setToken(tok); reloadAll(); toast('token saved', 'ok'); }}>Save</button>
      </div>
      <div className="card">
        <h3>Host</h3>
        {host ? <table className="kv"><tbody>
          <tr><td>name</td><td>{host.name}</td></tr><tr><td>api</td><td>{host.apiVersion}</td></tr>
          <tr><td>wires</td><td>{host.wires.join(', ')} (default {host.defaultWire})</td></tr>
          <tr><td>default login</td><td>{host.defaultLogin ? `${host.defaultLogin.host}:${host.defaultLogin.loginPort}` : '-'}</td></tr>
        </tbody></table> : <div className="muted">GET /api/v3/host not available</div>}
        <h3>API manifest</h3>
        <div>{manifest ? `${manifest.functions.length} functions in ${new Set(manifest.functions.map(f => f.module)).size} modules, ${manifest.events?.length ?? 0} events, ${manifest.sensors?.length ?? 0} sensors` : 'not loaded'}</div>
      </div>
    </div>
  );
}
