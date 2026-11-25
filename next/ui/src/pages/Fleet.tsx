import { useMemo, useState } from 'react';
import { api } from '../api/client';
import type { BotSummary, SpawnRequest } from '../api/types';
import { useTopic } from '../api/ws';
import { ActionButton, ConfirmButton, Empty, Field, Modal, StateBadge, fmtDate, fmtDuration, fmtNum, useAsync, useNow } from '../components/ui';
import { useApp } from '../lib/app';
import { href } from '../lib/router';
import { ApplyModal } from './BrainEditor';

export function FleetPage() {
  const live = useTopic<BotSummary[]>({ topic: 'fleet' });
  const polled = useAsync(() => api.bots(), []);
  const bots = live ?? polled.data ?? [];
  const now = useNow(1000);
  const [spawn, setSpawn] = useState(false);
  const [edit, setEdit] = useState<BotSummary | null>(null);
  const [apply, setApply] = useState<BotSummary | null>(null);
  const [filter, setFilter] = useState('');
  const layers = useMemo(() => [...new Set(bots.flatMap(b => Object.keys(b.layerStates || {})))], [bots]);
  const shown = bots.filter(b => !filter || [b.id, b.brain, b.map, b.state, b.wire].some(x => String(x ?? '').toLowerCase().includes(filter.toLowerCase())));
  return (
    <div className="page">
      <div className="page-head">
        <h2>Fleet</h2>
        <span className="muted">{bots.length} bots, {bots.filter(b => b.state !== 'Offline').length} online{live ? '' : ' (polled; live feed not connected)'}</span>
        <div className="grow" />
        <input aria-label="filter bots" placeholder="filter" value={filter} onChange={e => setFilter(e.target.value)} />
        <button className="btn primary" onClick={() => setSpawn(true)}>Spawn bot</button>
      </div>
      {polled.error && !live && <div className="banner err">{polled.error}</div>}
      {!bots.length ? <Empty>No bots yet. Spawn one.</Empty> : (
        <div className="table-wrap">
          <table className="fleet">
            <thead>
              <tr>
                <th>bot</th><th>state</th><th>wire</th><th>lvl</th><th>map</th>
                {layers.map(l => <th key={l}>{l}</th>)}
                <th>last transition</th><th title="tick p99">p99</th><th>t/s</th><th>exp/h</th><th>deaths/h</th><th>brain</th><th>health</th><th />
              </tr>
            </thead>
            <tbody>
              {shown.map(b => (
                <tr key={b.id} className={b.state === 'Offline' ? 'off' : ''}>
                  <td><a href={href(`/bots/${encodeURIComponent(b.id)}`)} className="strong">{b.id}</a>{b.character && b.character !== b.id && <div className="muted small">{b.character}</div>}</td>
                  <td><StateBadge state={b.paused ? 'Paused' : b.state} /></td>
                  <td><span className="badge wire">{b.wire ?? '?'}</span></td>
                  <td>{b.level || '-'}</td>
                  <td>{b.map ?? '-'}</td>
                  {layers.map(l => (
                    <td key={l} className="mono small">
                      {b.layerStates?.[l] ?? '-'}
                      {b.layerSince?.[l] && <div className="muted" title={`since ${fmtDate(b.layerSince[l])}`}>{fmtDuration(now - Date.parse(b.layerSince[l]!))} <span className="small">({fmtDate(b.layerSince[l], false)})</span></div>}
                    </td>
                  ))}
                  <td className="small">{b.lastTransition ? <><span className="mono">{b.lastTransition.layer}: {b.lastTransition.from} &rarr; {b.lastTransition.to}</span><div className="muted">{b.lastTransition.reason} · {fmtDate(b.lastTransition.at)}</div></> : '-'}</td>
                  <td className={b.tickP99Us > 1000 ? 'bad-text' : ''}>{b.tickP99Us ? `${fmtNum(b.tickP99Us)} us` : '-'}</td>
                  <td>{b.ticksPerSec ?? '-'}</td>
                  <td>{fmtNum(b.expPerHour)}</td>
                  <td>{fmtNum(b.deathsPerHour, 2)}</td>
                  <td className="small">{b.brain ? <a href={href(`/brains/${b.brain}${b.versionHash ? '@' + b.versionHash : ''}`)}>{b.brain}</a> : <span className="muted">none</span>}
                    {b.versionHash && <span className="mono muted">@{b.versionHash.slice(0, 8)}</span>}{b.channel && <div className="muted">{b.channel}</div>}</td>
                  <td className={b.health && b.health !== 'ok' ? 'warn-text small' : 'small'}>{b.health ?? '-'}{b.lastError && <div className="bad-text small" title={b.lastError}>{b.lastError.slice(0, 40)}</div>}</td>
                  <td className="actions">
                    {b.state === 'Offline'
                      ? <ActionButton small onClick={() => api.start(b.id)} ok={`${b.id} starting`}>start</ActionButton>
                      : <ActionButton small onClick={() => api.stop(b.id)} ok={`${b.id} stopping`}>stop</ActionButton>}
                    {b.state !== 'Offline' && <ActionButton small onClick={() => api.relog(b.id)} ok="relog requested">relog</ActionButton>}
                    {b.state !== 'Offline' && (b.paused || b.state === 'Paused'
                      ? <ActionButton small onClick={() => api.resume(b.id)}>resume</ActionButton>
                      : <ActionButton small onClick={() => api.pause(b.id)}>pause</ActionButton>)}
                    <button className="btn sm" onClick={() => setApply(b)}>apply version</button>
                    <button className="btn sm ghost" onClick={() => setEdit(b)}>edit</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {spawn && <BotConfigModal onClose={() => { setSpawn(false); polled.reload(); }} />}
      {edit && <BotConfigModal bot={edit} onClose={() => { setEdit(null); polled.reload(); }} />}
      {apply && <ApplyPicker bot={apply} onClose={() => setApply(null)} />}
    </div>
  );
}

function ApplyPicker({ bot, onClose }: { bot: BotSummary; onClose: () => void }) {
  const brains = useAsync(() => api.brains(), []);
  const [brain, setBrain] = useState(bot.brain ?? '');
  const versions = useAsync(() => (brain ? api.versions(brain) : Promise.resolve([])), [brain]);
  const [hash, setHash] = useState('');
  const [channel, setChannel] = useState('');
  const [keep, setKeep] = useState(true);
  const [step2, setStep2] = useState(false);
  const { toast } = useApp();
  if (step2 && hash) return <ApplyModal brain={brain} hash={hash} presetBot={bot.id} onClose={onClose} />;
  const channels = brains.data?.find(b => b.name === brain)?.channels ?? {};
  return (
    <Modal title={`Apply a brain to ${bot.id}`} onClose={onClose} footer={
      channel ? <ActionButton kind="primary" onClick={async () => { await api.applyBrain(bot.id, { name: brain, channel, keepState: keep }); toast(`${bot.id} follows ${brain}/${channel}`, 'ok'); onClose(); }}>Follow channel</ActionButton>
        : <button className="btn primary" disabled={!hash} onClick={() => setStep2(true)}>Next</button>}>
      <Field label="brain"><select aria-label="brain" value={brain} onChange={e => { setBrain(e.target.value); setHash(''); setChannel(''); }}>
        <option value="">choose...</option>{(brains.data || []).map(b => <option key={b.name}>{b.name}</option>)}</select></Field>
      {brain && <>
        <Field label="channel" hint="the bot follows the channel when it moves"><select aria-label="channel" value={channel} onChange={e => { setChannel(e.target.value); setHash(''); }}>
          <option value="">(pin a version instead)</option>{Object.entries(channels).map(([c, h]) => <option key={c} value={c}>{c} &rarr; {h}</option>)}</select></Field>
        {!channel && <Field label="version"><select aria-label="version" value={hash} onChange={e => setHash(e.target.value)}>
          <option value="">choose...</option>{(versions.data || []).map(v => <option key={v.hash} value={v.hash}>{v.hash} {fmtDate(v.created, false)} {v.message}</option>)}</select></Field>}
        {channel && <label className="check"><input type="checkbox" checked={keep} onChange={e => setKeep(e.target.checked)} /> keep state</label>}
      </>}
    </Modal>
  );
}

/** Spawn (no bot) or edit (bot) the bot / profile configuration, including the wire (packet set). */
export function BotConfigModal({ bot, onClose }: { bot?: BotSummary; onClose: () => void }) {
  const { host, toast } = useApp();
  const brains = useAsync(() => api.brains(), []);
  const wires = host?.wires?.length ? host.wires : ['2016', '2026'];
  const [f, setF] = useState<SpawnRequest>({
    id: bot?.id ?? '', host: bot?.host ?? host?.defaultLogin?.host ?? '', loginPort: bot?.loginPort ?? host?.defaultLogin?.loginPort ?? 0,
    username: bot?.username ?? '', password: '', character: bot?.character ?? '', create: false, autostart: bot?.autostart ?? true,
    brain: bot?.brain ?? '', channel: bot?.channel ?? '', wire: bot?.wire ?? host?.defaultWire ?? wires[0],
  });
  const set = (p: Partial<SpawnRequest>) => setF(x => ({ ...x, ...p }));
  const channels = brains.data?.find(b => b.name === f.brain)?.channels ?? {};
  const submit = async () => {
    if (bot) {
      const patch: Partial<SpawnRequest> = {};
      for (const k of ['host', 'loginPort', 'username', 'character', 'autostart', 'wire'] as const) if ((f as any)[k] !== (bot as any)[k]) (patch as any)[k] = f[k];
      if (f.password) patch.password = f.password;
      await api.patchBot(bot.id, patch);
      toast(`${bot.id} updated${patch.wire ? ` (wire ${patch.wire}: applies at the next login)` : ''}`, 'ok');
    } else {
      const body: SpawnRequest = { ...f, brain: f.brain || undefined, channel: f.channel || undefined, class: f.create ? f.class : undefined };
      await api.spawn(body);
      toast(`spawned ${f.id}`, 'ok');
    }
    onClose();
  };
  const valid = f.id && f.username && f.character && (bot || f.password) && f.host && f.loginPort;
  return (
    <Modal title={bot ? `Edit ${bot.id}` : 'Spawn bot'} onClose={onClose} footer={
      <>{bot && <ConfirmButton onConfirm={async () => { await api.deleteBot(bot.id); toast(`${bot.id} removed`, 'ok'); onClose(); }}>delete bot</ConfirmButton>}
        <div className="grow" /><ActionButton kind="primary" onClick={submit} disabled={!valid}>{bot ? 'Save' : 'Spawn'}</ActionButton></>}>
      <div className="grid2">
        <Field label="id"><input aria-label="bot id" value={f.id} disabled={!!bot} onChange={e => set({ id: e.target.value })} /></Field>
        <Field label="wire (packet set)" hint={`host default ${host?.defaultWire ?? '?'}${bot ? '; applies at the next login' : ''}`}>
          <select aria-label="wire" value={f.wire} onChange={e => set({ wire: e.target.value })}>{wires.map(w => <option key={w} value={w}>{w}</option>)}</select>
        </Field>
        <Field label="login host"><input aria-label="login host" value={f.host} onChange={e => set({ host: e.target.value })} /></Field>
        <Field label="login port"><input aria-label="login port" type="number" value={f.loginPort || ''} onChange={e => set({ loginPort: Number(e.target.value) })} /></Field>
        <Field label="username"><input aria-label="username" autoComplete="off" value={f.username} onChange={e => set({ username: e.target.value })} /></Field>
        <Field label="password" hint={bot ? 'leave empty to keep' : undefined}><input aria-label="password" type="password" autoComplete="new-password" value={f.password} onChange={e => set({ password: e.target.value })} /></Field>
        <Field label="character"><input aria-label="character" value={f.character} onChange={e => set({ character: e.target.value })} /></Field>
        <Field label="autostart"><label className="check"><input type="checkbox" checked={f.autostart} onChange={e => set({ autostart: e.target.checked })} /> log in on host start</label></Field>
      </div>
      {!bot && (
        <div className="grid2">
          <Field label="create character"><label className="check"><input type="checkbox" checked={!!f.create} onChange={e => set({ create: e.target.checked })} /> create if missing</label></Field>
          {f.create && <Field label="class"><select aria-label="class" value={f.class ?? ''} onChange={e => set({ class: Number(e.target.value) })}>
            <option value="">choose...</option>{(host?.classes ?? []).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>}
          <Field label="brain"><select aria-label="spawn brain" value={f.brain} onChange={e => set({ brain: e.target.value, channel: '' })}>
            <option value="">none</option>{(brains.data || []).map(b => <option key={b.name}>{b.name}</option>)}</select></Field>
          {f.brain && <Field label="channel"><select aria-label="spawn channel" value={f.channel} onChange={e => set({ channel: e.target.value })}>
            <option value="">latest version</option>{Object.keys(channels).map(c => <option key={c}>{c}</option>)}</select></Field>}
        </div>
      )}
    </Modal>
  );
}
