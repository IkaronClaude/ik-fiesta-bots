import type * as T from './types';

const BASE = '/api/v3';
const TOKEN_KEY = 'botui.token';

export function getToken(): string {
  try { return localStorage.getItem(TOKEN_KEY) || ''; } catch { return ''; }
}
export function setToken(t: string) {
  try { if (t) localStorage.setItem(TOKEN_KEY, t); else localStorage.removeItem(TOKEN_KEY); } catch { /* private mode */ }
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public body: any) { super(message); }
  get lint(): T.LintResult[] { return Array.isArray(this.body?.lint) ? this.body.lint : Array.isArray(this.body) ? this.body : []; }
}

async function req<R>(method: string, path: string, body?: unknown): Promise<R> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const tok = getToken();
  if (tok) headers.Authorization = `Bearer ${tok}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (res.status === 204) return undefined as R;
  const text = await res.text();
  let json: any = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = text; }
  if (!res.ok) {
    const msg = (json && (json.title || json.detail)) || `${res.status} ${res.statusText}`;
    throw new ApiError(res.status, `${method} ${path}: ${msg}`, json);
  }
  return json as R;
}

const enc = encodeURIComponent;
const q = (o: Record<string, string | number | undefined | null>) => {
  const p = Object.entries(o).filter(([, v]) => v !== undefined && v !== null && v !== '').map(([k, v]) => `${k}=${enc(String(v))}`);
  return p.length ? '?' + p.join('&') : '';
};

// ---- normalisation: accept the spec's JSON and the skeleton records' serialisation ----
const RES_BITS = ['Movement', 'Combat', 'Cast', 'Inventory', 'Npc', 'Chat'];
function claimsOf(c: unknown): string[] {
  if (Array.isArray(c)) return c.map(String);
  if (typeof c === 'number') return RES_BITS.filter((_, i) => c & (1 << i));
  if (typeof c === 'string' && c) return c.split(/[,| ]+/).filter(x => x && x !== 'None');
  return [];
}
export function normDebugFrame(f: any): T.DebugFrame {
  if (!f) return f;
  const tickStats = f.tickStats || (typeof f.tick === 'object' ? f.tick : null) || { p50us: 0, p99us: 0, allocP95B: 0, steps: {} };
  let bb = f.blackboard ?? (f.blackboardJson ? safeJson(f.blackboardJson) : null);
  if (typeof bb === 'string') bb = safeJson(bb);
  bb = bb || {};
  return {
    ...f,
    tick: typeof f.tick === 'number' ? f.tick : Number(f.tickNo ?? 0),
    t: f.t || f.at,
    layers: (f.layers || []).map((l: any) => ({ ...l, claims: claimsOf(l.claims), conditions: (l.conditions || []).map((c: any) => ({
      ...c, kind: String(c.kind).toLowerCase(), holdMs: c.holdMs ?? c.holdElapsedMs ?? 0 })) })),
    lastTransitions: (f.lastTransitions || []).map((t: any) => ({ ...t, kind: String(t.kind).toLowerCase(), values: t.values || {} })),
    blackboard: { bot: bb.bot || {}, layer: bb.layer || {}, state: bb.state || {} },
    watches: f.watches || {},
    actions: (f.actions || []).map((a: any) => ({ ...a, state: String(a.state).toLowerCase() })),
    procedures: f.procedures || [],
    tickStats: { p50us: tickStats.p50us ?? tickStats.p50Us ?? 0, p99us: tickStats.p99us ?? tickStats.p99Us ?? 0,
      allocP95B: tickStats.allocP95B ?? tickStats.allocP95Bytes ?? 0, steps: tickStats.steps || tickStats.stepsUs || {} },
    errors: f.errors || [],
  };
}
function safeJson(s: string) { try { return JSON.parse(s); } catch { return null; } }

export function normSummary(b: any): T.BotSummary {
  return { ...b, layerStates: b.layerStates || {}, state: String(b.state ?? 'Offline') };
}

export const api = {
  host: () => req<T.HostInfo>('GET', '/host'),
  bots: async () => (await req<any[]>('GET', '/bots')).map(normSummary),
  bot: async (id: string) => normSummary(await req<any>('GET', `/bots/${enc(id)}`)) as T.BotDetail,
  spawn: (r: T.SpawnRequest) => req<T.BotSummary>('POST', '/bots', r),
  patchBot: (id: string, patch: Partial<T.SpawnRequest>) => req<T.BotSummary>('PATCH', `/bots/${enc(id)}`, patch),
  deleteBot: (id: string) => req<void>('DELETE', `/bots/${enc(id)}`),
  start: (id: string) => req('POST', `/bots/${enc(id)}/start`, {}),
  stop: (id: string) => req('POST', `/bots/${enc(id)}/stop`, { logout: true }),
  relog: (id: string) => req('POST', `/bots/${enc(id)}/relog`, {}),
  pause: (id: string) => req('POST', `/bots/${enc(id)}/pause`, {}),
  resume: (id: string) => req('POST', `/bots/${enc(id)}/resume`, {}),
  step: (id: string, ticks = 1) => req('POST', `/bots/${enc(id)}/step`, { ticks }),
  forceState: (id: string, layer: string, state: string) => req('POST', `/bots/${enc(id)}/force-state`, { layer, state }),
  setBlackboard: (id: string, scope: string, key: string, value: string, owner?: string) =>
    req('PUT', `/bots/${enc(id)}/blackboard`, { scope, key, value, owner }),
  operatorAction: (id: string, kind: string, args: Record<string, string>) => req('POST', `/bots/${enc(id)}/actions`, { kind, args }),
  packetlog: (id: string, enabled: boolean) => req('POST', `/bots/${enc(id)}/packetlog`, { enabled }),

  brains: () => req<T.BrainListItem[]>('GET', '/brains'),
  versions: (name: string) => req<T.BrainVersionInfo[]>('GET', `/brains/${enc(name)}/versions`),
  version: (name: string, hash: string) => req<T.BrainVersionFull>('GET', `/brains/${enc(name)}/versions/${enc(hash)}`),
  createVersion: (name: string, definition: string, modules: Record<string, string>, message: string) =>
    req<T.BrainVersionInfo>('POST', `/brains/${enc(name)}/versions`, { definition, modules, message }),
  lint: (definition: string, modules: Record<string, string>) => req<T.LintResult[]>('POST', '/brains/lint', { definition, modules }),
  diff: (name: string, from: string, to: string) => req<T.BrainDiff>('GET', `/brains/${enc(name)}/diff${q({ from, to })}`),
  setChannel: (name: string, channel: string, hash: string) => req('PUT', `/brains/${enc(name)}/channels/${enc(channel)}`, { hash }),
  brainAssignments: (name: string) => req<T.AssignmentRecord[]>('GET', `/brains/${enc(name)}/assignments`),
  brainMetrics: (name: string, versions: string[]) => req<T.VersionMetrics[]>('GET', `/brains/${enc(name)}/metrics${q({ versions: versions.join(',') })}`),
  applyBrain: (id: string, body: { name: string; hash?: string; channel?: string; keepState: boolean; reason?: string }) =>
    req<T.AssignmentRecord>('POST', `/bots/${enc(id)}/brain`, body),
  rollback: (id: string, to?: string) => req<T.AssignmentRecord>('POST', `/bots/${enc(id)}/brain/rollback`, to ? { to } : {}),
  assignments: (id: string) => req<T.AssignmentRecord[]>('GET', `/bots/${enc(id)}/assignments`),

  libraries: () => req<T.LibraryItem[]>('GET', '/libraries'),
  libraryVersions: (path: string) => req<T.LibraryVersion[]>('GET', `/libraries/${enc(path)}/versions`),
  libraryVersion: (path: string, hash: string) => req<T.LibraryVersion>('GET', `/libraries/${enc(path)}/versions/${enc(hash)}`),
  saveLibrary: (path: string, source: string, message: string) => req<T.LibraryVersion & { lint?: T.LintResult[] }>('POST', `/libraries/${enc(path)}/versions`, { source, message }),
  lintLibrary: (path: string, source: string) => req<T.LintResult[]>('POST', '/libraries/lint', { path, source }),
  manifest: () => req<T.ApiManifest>('GET', '/api-manifest'),

  debug: async (id: string) => normDebugFrame(await req<any>('GET', `/bots/${enc(id)}/debug`)),
  transitions: (id: string, since?: number, layer?: string) => req<T.TransitionRecord[]>('GET', `/bots/${enc(id)}/transitions${q({ since, layer })}`),
  ticks: (id: string, last = 1000) => req<T.TickSample[]>('GET', `/bots/${enc(id)}/tick${q({ last })}`),
  sensors: (id: string) => req<T.SensorFrame>('GET', `/bots/${enc(id)}/sensors`),
  log: (id: string, o: { level?: string; category?: string; from?: string; to?: string; max?: number; text?: string }) =>
    req<T.LogLine[]>('GET', `/bots/${enc(id)}/log${q(o)}`),
  logLevels: (id: string) => req<Record<string, { level: string; expiresAt: string | null }>>('GET', `/bots/${enc(id)}/log-levels`),
  setLogLevel: (id: string, category: string, level: string, expiryMinutes?: number) =>
    req<Record<string, { level: string; expiresAt: string | null }>>('PUT', `/bots/${enc(id)}/log-levels`, { category, level, expiryMinutes }),
  packetCapture: (id: string) => req<T.PacketCapture>('GET', `/bots/${enc(id)}/packet-capture`),
  setPacketCapture: (id: string, body: Partial<T.PacketCapture>) => req<T.PacketCapture>('POST', `/bots/${enc(id)}/packet-capture`, body),
  recordings: (id: string) => req<T.Recording[]>('GET', `/bots/${enc(id)}/recordings`),
  recordingFrames: async (id: string, rid: string, o: { from?: string; to?: string; every?: number } = {}) => {
    const r = await req<{ debug: any[]; sensors: T.SensorFrame[] }>('GET', `/bots/${enc(id)}/recordings/${enc(rid)}/frames${q(o)}`);
    return { debug: (r.debug || []).map(normDebugFrame), sensors: r.sensors || [] };
  },
  inventory: (id: string) => req<any>('GET', `/bots/${enc(id)}/inventory`),
  quests: (id: string) => req<any[]>('GET', `/bots/${enc(id)}/quests`),
  skills: (id: string) => req<any[]>('GET', `/bots/${enc(id)}/skills`),
  knowledgeMob: (mobId: number) => req<any>('GET', `/knowledge/mobs/${mobId}`),
};
