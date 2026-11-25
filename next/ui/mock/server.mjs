// Mock bot host: implements the HTTP + WebSocket API of docs/API_SPEC.md section 8 and 12.3 (plus the
// "UI requests" section) over in-memory fixture data and a fake brain runtime (sim.mjs).
// Dev/test tool for the UI only: node mock/server.mjs [--port 5199] [--static dist] [--token T]
import http from 'node:http';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync, statSync } from 'node:fs';
import { join, extname, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildManifest } from './manifest.mjs';
import { lintBrain, lintLua, parseDefinition } from './lint.mjs';
import { BotSim } from './sim.mjs';
import { acceptUpgrade, diff } from './ws.mjs';
import { unifiedDiff } from './textdiff.mjs';
import * as F from './fixtures.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => {
  if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
  return acc;
}, []));
const PORT = Number(args.port || process.env.MOCK_PORT || 5199);
const TOKEN = args.token || process.env.MOCK_TOKEN || null;
const STATIC = args.static ? resolve(here, '..', args.static === true ? 'dist' : args.static) : null;
const manifest = buildManifest(resolve(here, '../../../docs/API_SPEC.md'));

// ---------------------------------------------------------------- store
const hash12 = s => createHash('sha256').update(s).digest('hex').slice(0, 12);
const iso = (t = Date.now()) => new Date(t).toISOString();
const libraries = new Map(); // path -> [{hash, created, author, message, source}]
const brains = new Map();    // name -> {versions: [...], channels: {}}
const assignments = [];      // AssignmentRecord
const bots = new Map();      // id -> {rec, sim, state, logRing, logLevels, packetCapture, history}
const subs = new Set();      // websocket subscriptions

function addLibraryVersion(path, source, message, author = 'mock', at = Date.now()) {
  const list = libraries.get(path) || [];
  const hash = hash12(source);
  if (list.some(v => v.hash === hash)) return list.find(v => v.hash === hash);
  const v = { hash, path, created: iso(at), author, message: message || '', source };
  list.push(v); libraries.set(path, list);
  return v;
}
function latestLib(path) { const l = libraries.get(path); return l ? l[l.length - 1] : null; }

function resolveLibs(def, pinned = {}) {
  const libs = {};
  for (const ref of def.modules || []) {
    const [path, pin] = ref.split('@');
    if (path.startsWith('brain/')) continue;
    const list = libraries.get(path);
    if (!list) continue;
    const v = pin ? list.find(x => x.hash.startsWith(pin)) : (pinned[path] ? list.find(x => x.hash === pinned[path]) : list[list.length - 1]);
    if (v) libs[path] = { hash: v.hash, source: v.source };
  }
  return libs;
}

function createBrainVersion(name, definition, modules, message, author = 'operator', at = Date.now()) {
  let def;
  try { def = parseDefinition(definition); } catch (e) { return { status: 400, body: problem(400, 'definition does not parse', [{ severity: 'error', code: 'DEF_PARSE', message: e.message, line: 1 }]) }; }
  const libs = resolveLibs(def);
  const libSources = Object.fromEntries(Object.entries(libs).map(([p, v]) => [p, v.source]));
  const lint = lintBrain(definition, modules, manifest, libSources);
  if (def.brain !== name) lint.push({ severity: 'error', code: 'DEF_NAME', message: `brain: '${def.brain}' does not match '${name}'`, module: null, line: 1, layer: null, state: null });
  if (lint.some(l => l.severity === 'error')) return { status: 400, body: problem(400, 'lint errors', lint) };
  const canon = JSON.stringify({ d: definition, m: Object.keys(modules).sort().map(k => [k, modules[k]]), l: Object.keys(libs).sort().map(k => [k, libs[k].hash]), api: 3 });
  const hash = hash12(canon);
  const b = brains.get(name) || { versions: [], channels: {} };
  brains.set(name, b);
  let v = b.versions.find(x => x.hash === hash);
  if (!v) {
    v = { hash, brain: name, api: 3, created: iso(at), author, message: message || '', modules: [...Object.keys(modules), ...Object.keys(libs)].sort(),
      definition, sources: modules, libraries: Object.fromEntries(Object.entries(libs).map(([p, x]) => [p, x.hash])), lint };
    b.versions.push(v);
  }
  return { status: 201, body: info(v) };
}
const info = v => ({ hash: v.hash, brain: v.brain, api: v.api, created: v.created, author: v.author, message: v.message, modules: v.modules,
  libraries: v.libraries, warnings: (v.lint || []).filter(l => l.severity !== 'error').length });

function findVersion(name, hash) {
  const b = brains.get(name); if (!b) return null;
  return b.versions.find(v => v.hash === hash || v.hash.startsWith(hash)) || null;
}
function fullVersion(v) {
  const libs = {};
  for (const [p, h] of Object.entries(v.libraries)) libs[p] = { hash: h, source: libraries.get(p).find(x => x.hash === h).source };
  return { ...info(v), definition: v.definition, modules: v.sources, libraries: libs, lint: v.lint };
}

// seed
const t0 = Date.now() - 3 * 86400000;
addLibraryVersion('lib/core', F.LIB_CORE, 'host library', 'host', t0);
addLibraryVersion('user/ikaron/heal', F.HEAL_LUA_V1, 'heal policy', 'ikaron', t0 + 1000);
const mods = { 'brain/leveller/survival': F.SURVIVAL_LUA, 'brain/leveller/errand': F.ERRAND_LUA };
const lv1 = createBrainVersion('leveller', F.LEVELLER_V1, mods, 'initial layered leveller', 'ikaron', t0 + 2000).body;
addLibraryVersion('user/ikaron/heal', F.HEAL_LUA_V2, 'faster heals only', 'ikaron', t0 + 86400000);
const lv2 = createBrainVersion('leveller', F.LEVELLER_V2, mods, 'flee at 40%, drop blind hunt', 'ikaron', t0 + 86400000 + 1000).body;
const av1 = createBrainVersion('assist', F.ASSIST_V1, {}, 'party healer', 'ikaron', t0 + 5000).body;
for (const x of [lv1, lv2, av1]) if (!x.hash) { console.error('fixture brain failed lint', JSON.stringify(x, null, 1)); process.exit(1); }
brains.get('leveller').channels = { stable: lv1.hash, candidate: lv2.hash, 'operator-test': lv2.hash };
brains.get('assist').channels = { stable: av1.hash };

function versionFor(rec) {
  const b = brains.get(rec.brain); if (!b) return null;
  const h = rec.versionHash || (rec.channel && b.channels[rec.channel]) || b.versions.at(-1).hash;
  return findVersion(rec.brain, h);
}

function startSim(bot) {
  const v = versionFor(bot.rec);
  if (!v) { bot.sim = null; return; }
  bot.rec.versionHash = v.hash;
  bot.sim = new BotSim(bot.rec, parseDefinition(v.definition), `${v.brain}@${v.hash}`, (cat, lvl, text) => pushLog(bot, cat, lvl, text));
  bot.sim.onTransition = r => broadcast('transition', bot.rec.id, r);
}

function addBot(rec, state = 'InZone') {
  const bot = { rec: { packetLog: false, autostart: true, ...rec }, state, logRing: [], history: [], sensorHistory: [],
    logLevels: Object.fromEntries(['core', 'net', 'packet', 'nav', 'combat', 'quest', 'item', 'social', 'brain', 'script', 'prof', 'knowledge'].map(c => [c, { level: 'note', expiresAt: null }])),
    packetCapture: { enabled: false, opcodes: [], depts: [], minutes: 15, maxMb: 64, format: 'text', files: [] }, createdAt: Date.now() };
  bots.set(rec.id, bot);
  if (state !== 'Offline') startSim(bot);
  pushLog(bot, 'core', 'N', `bot ${rec.id} ${state === 'Offline' ? 'registered (offline)' : 'in zone'}`);
  return bot;
}
for (const b of F.BOTS) {
  const bot = addBot({ ...b }, b.offline ? 'Offline' : 'InZone');
  if (bot.rec.brain) assignments.push({ bot: b.id, at: iso(t0 + 3 * 3600000), fromHash: null, toHash: bot.rec.versionHash, brain: b.brain, by: 'ikaron', reason: 'initial', keptState: false });
}

const LEVEL_LETTER = { C: 0, N: 1, I: 2, D: 3, T: 4 };
const LEVEL_NAME = ['crit', 'note', 'info', 'debug', 'trace'];
function pushLog(bot, category, lvl, text) {
  const last = bot.logRing.at(-1);
  if (last && last.text === text && last.category === category && Date.now() - Date.parse(last.t) < 10000) { last.repeat++; return; }
  const e = { t: iso(), level: lvl, category, text, repeat: 1 };
  bot.logRing.push(e); if (bot.logRing.length > 5000) bot.logRing.shift();
  broadcast('log', bot.rec.id, e, sub => (sub.levelN ?? 1) >= LEVEL_LETTER[lvl]);
}

// ---------------------------------------------------------------- summaries
function summary(bot) {
  const r = bot.rec;
  const base = { id: r.id, state: bot.sim?.paused ? 'Paused' : bot.state, character: r.character, username: r.username, host: r.host, loginPort: r.loginPort,
    wire: r.wire, classFamily: r.classFamily, level: r.level, brain: r.brain, channel: r.channel, versionHash: r.versionHash ?? null,
    autostart: r.autostart, packetLog: r.packetLog, health: bot.state === 'Offline' ? null : 'ok', lastError: null, map: null,
    layerStates: {}, tickP99Us: 0, expPerHour: 0, deathsPerHour: 0 };
  if (!bot.sim || bot.state === 'Offline') return base;
  const s = bot.sim.summary();
  const health = s.errors > 3 ? 'errors' : s.tickP99Us > 1000 ? 'tick over budget' : 'ok';
  return { ...base, ...s, versionHash: r.versionHash, health, lastError: bot.sim.errors.at(-1)?.error ?? null };
}

// ---------------------------------------------------------------- sim loop
let lastFleet = 0, lastDebug = 0, lastSensors = 0;
setInterval(() => {
  const now = Date.now();
  for (const bot of bots.values()) {
    const s = bot.sim; if (!s || bot.state === 'Offline') continue;
    if (!s.paused) s.step(50);
    else if (s.pendingSteps > 0) { s.step(50); s.pendingSteps--; }
  }
  if (now - lastDebug >= 200) {
    lastDebug = now;
    for (const bot of bots.values()) {
      if (!bot.sim || bot.state === 'Offline') continue;
      const f = bot.sim.debugFrame();
      bot.history.push(f); if (bot.history.length > 1500) bot.history.shift();
      const sf = bot.sim.sensorFrame();
      bot.sensorHistory.push(sf); if (bot.sensorHistory.length > 1500) bot.sensorHistory.shift();
      broadcast('debug', bot.rec.id, f);
    }
  }
  if (now - lastSensors >= 100) {
    lastSensors = now;
    for (const sub of subs) if (sub.topic === 'sensors' && now - (sub.lastSent || 0) >= 1000 / (sub.hz || 5)) {
      const bot = bots.get(sub.bot); if (!bot?.sim) continue;
      sub.lastSent = now; sendDelta(sub, bot.sim.sensorFrame());
    }
  }
  if (now - lastFleet >= 1000) { lastFleet = now; broadcast('fleet', null, [...bots.values()].map(summary)); }
}, 50);

// ---------------------------------------------------------------- websocket

function sendDelta(sub, data) {
  if (sub.conn.closed) { subs.delete(sub); return; }
  if (sub.prev === undefined || sub.noDelta) { sub.conn.send({ topic: sub.topic, bot: sub.bot ?? undefined, full: true, data }); }
  else {
    const patch = diff(sub.prev, data);
    if (patch.length) sub.conn.send({ topic: sub.topic, bot: sub.bot ?? undefined, full: false, patch });
  }
  sub.prev = JSON.parse(JSON.stringify(data));
}
function broadcast(topic, bot, data, filter) {
  for (const sub of subs) {
    if (sub.topic !== topic || (sub.bot && bot && sub.bot !== bot)) continue;
    if (filter && !filter(sub)) continue;
    if (topic === 'debug' || topic === 'fleet') sendDelta(sub, data);
    else sub.conn.send({ topic, bot: bot ?? undefined, data });
  }
}

// ---------------------------------------------------------------- http helpers
function problem(status, title, lint) { return { type: 'about:blank', title, status, ...(lint ? { lint } : {}) }; }
function send(res, status, body, headers = {}) {
  const isText = typeof body === 'string';
  res.writeHead(status, { 'content-type': isText ? 'text/plain; charset=utf-8' : status >= 400 ? 'application/problem+json' : 'application/json', ...headers });
  res.end(isText ? body : JSON.stringify(body));
}
async function readBody(req) {
  const chunks = []; for await (const c of req) chunks.push(c);
  if (!chunks.length) return {};
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { return null; }
}
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.json': 'application/json', '.png': 'image/png', '.woff2': 'font/woff2' };
function serveStatic(req, res, path) {
  if (!STATIC) return false;
  let p = join(STATIC, decodeURIComponent(path));
  if (!p.startsWith(STATIC)) return false;
  if (!existsSync(p) || statSync(p).isDirectory()) p = join(STATIC, 'index.html');
  if (!existsSync(p)) return false;
  res.writeHead(200, { 'content-type': MIME[extname(p)] || 'application/octet-stream' });
  res.end(readFileSync(p));
  return true;
}

// ---------------------------------------------------------------- routes
const routes = [];
const route = (method, pattern, fn) => routes.push({ method, re: new RegExp('^/api/v3' + pattern.replace(/\{(\w+)\*\}/g, '(?<$1>.+)').replace(/\{(\w+)\}/g, '(?<$1>[^/]+)') + '$'), fn });
const needBot = (p) => { const b = bots.get(decodeURIComponent(p.id)); if (!b) throw httpErr(404, `no bot ${p.id}`); return b; };
const needSim = (p) => { const b = needBot(p); if (!b.sim || b.state === 'Offline') throw httpErr(409, `bot ${p.id} is offline`); return b; };
const httpErr = (status, title) => Object.assign(new Error(title), { status });

route('GET', '/host', () => ({ name: 'mock host', mock: true, apiVersion: 3, wires: ['2016', '2026'], defaultWire: '2016',
  defaultLogin: { host: '127.0.0.1', loginPort: 9010 }, classes: [{ id: 1, name: 'class 1' }, { id: 6, name: 'class 6' }, { id: 11, name: 'class 11' }, { id: 16, name: 'class 16' }, { id: 21, name: 'class 21' }],
  channels: ['stable', 'candidate', 'operator-test'] }));
route('GET', '/bots', () => [...bots.values()].map(summary));
route('POST', '/bots', (p, body) => {
  if (!body?.id || !/^[A-Za-z0-9_-]{2,32}$/.test(body.id)) throw httpErr(400, 'id: 2-32 letters, digits, _ or -');
  if (bots.has(body.id)) throw httpErr(409, `bot ${body.id} exists`);
  if (!body.username || !body.character) throw httpErr(400, 'username and character are required');
  if (body.wire && !['2016', '2026'].includes(String(body.wire))) throw httpErr(400, 'wire must be 2016 or 2026');
  const rec = { id: body.id, username: body.username, character: body.character, host: body.host || '127.0.0.1', loginPort: body.loginPort || 9010,
    brain: body.brain || null, channel: body.channel || null, wire: String(body.wire || '2016'), classFamily: body.class ? Number(body.class) : 1, level: 1, autostart: body.autostart !== false };
  const bot = addBot(rec, body.autostart === false ? 'Offline' : 'InZone');
  if (rec.brain && bot.rec.versionHash) assignments.push({ bot: rec.id, at: iso(), fromHash: null, toHash: bot.rec.versionHash, brain: rec.brain, by: 'operator', reason: 'spawn', keptState: false });
  return [201, summary(bot)];
});
route('GET', '/bots/{id}', (p) => { const b = needBot(p); return { ...summary(b), record: b.rec, logLevels: b.logLevels, packetCapture: b.packetCapture }; });
route('PATCH', '/bots/{id}', (p, body) => {
  const b = needBot(p);
  for (const k of ['host', 'loginPort', 'username', 'character', 'autostart', 'wire']) if (body[k] !== undefined) b.rec[k] = body[k];
  if (body.wire && !['2016', '2026'].includes(String(body.wire))) throw httpErr(400, 'wire must be 2016 or 2026');
  pushLog(b, 'core', 'N', `config changed: ${Object.keys(body).join(', ')}${body.wire ? ` (wire ${body.wire}, applies on next login)` : ''}`);
  return summary(b);
});
route('DELETE', '/bots/{id}', (p) => { needBot(p); bots.delete(decodeURIComponent(p.id)); return [204, null]; });
route('POST', '/bots/{id}/start', (p) => { const b = needBot(p); if (b.state === 'Offline') { b.state = 'InZone'; startSim(b); pushLog(b, 'core', 'N', 'login -> zone'); } return summary(b); });
route('POST', '/bots/{id}/stop', (p) => { const b = needBot(p); b.state = 'Offline'; b.sim = null; pushLog(b, 'core', 'N', 'stopped (clean logout)'); return summary(b); });
route('POST', '/bots/{id}/relog', (p) => { const b = needSim(p); const keep = b.sim.bb.bot; startSim(b); b.sim.bb.bot = keep; pushLog(b, 'core', 'N', 'relog: checkpoint, logout, login, resume'); return summary(b); });
route('POST', '/bots/{id}/pause', (p) => { const b = needSim(p); b.sim.paused = true; pushLog(b, 'brain', 'N', 'brain paused by operator'); return summary(b); });
route('POST', '/bots/{id}/resume', (p) => { const b = needSim(p); b.sim.paused = false; pushLog(b, 'brain', 'N', 'brain resumed'); return summary(b); });
route('POST', '/bots/{id}/step', (p, body) => { const b = needSim(p); if (!b.sim.paused) throw httpErr(409, 'pause the brain before stepping'); b.sim.pendingSteps += Math.max(1, Math.min(200, body.ticks || 1)); return { queued: b.sim.pendingSteps }; });
route('POST', '/bots/{id}/force-state', (p, body) => {
  const b = needSim(p); const s = b.sim;
  const L = s.layers.find(l => l.name === body.layer); if (!L) throw httpErr(400, `no layer ${body.layer}`);
  const path = String(body.state).split('/');
  if (!L.def.machine.states[path[0]]) throw httpErr(400, `no state ${path[0]} in layer ${body.layer}`);
  const from = L.path[0]?.state;
  s.enter(L, 0, L.def.machine, path[0], 'operator');
  for (let i = 1; i < path.length; i++) { const m = L.path[i - 1] && s.machineOf(L.path[i - 1].machine.states[L.path[i - 1].state]); if (m?.states[path[i]]) s.enter(L, i, m, path[i], 'operator'); }
  const rec = { tick: s.tick, at: iso(s.now), layer: L.name, from, to: path.join('/'), depth: 0, index: -1, kind: 'operator', reason: 'operator', values: {} };
  s.transitions.push(rec); broadcast('transition', b.rec.id, rec);
  pushLog(b, 'brain', 'N', `operator forced ${L.name} -> ${body.state}`);
  return rec;
});
route('PUT', '/bots/{id}/blackboard', (p, body) => {
  const b = needSim(p); const s = b.sim;
  let v = body.value; if (typeof v === 'string') { try { v = JSON.parse(v); } catch { /* keep string */ } }
  const scope = body.scope;
  if (scope === 'bot') s.bb.bot[body.key] = v;
  else if (scope === 'layer' || scope === 'state') { if (!s.bb[scope][body.owner]) throw httpErr(400, `owner (layer) required for ${scope} scope`); s.bb[scope][body.owner][body.key] = v; }
  else throw httpErr(400, 'scope: bot, layer or state');
  pushLog(b, 'brain', 'N', `operator set bb.${scope}${body.owner ? '.' + body.owner : ''}.${body.key} = ${JSON.stringify(v)}`);
  return s.bb;
});
route('POST', '/bots/{id}/actions', (p, body) => {
  const b = needSim(p); const s = b.sim;
  if (!manifest.actionKinds.includes(body.kind)) throw httpErr(400, `unknown action kind ${body.kind}`);
  const a = { id: s.nextActionId++, kind: body.kind, state: 'sent', code: 0, reason: '', sentAt: iso(s.now), resolvedAt: null, layer: 'operator', _resolveAt: s.now + 400 };
  s.actions.push(a); pushLog(b, 'core', 'N', `operator action ${body.kind} ${JSON.stringify(body.args || {})}`);
  return { id: a.id, state: a.state };
});
route('POST', '/bots/{id}/packetlog', (p, body) => { const b = needBot(p); b.rec.packetLog = !!body.enabled; return { enabled: b.rec.packetLog }; });

// brains
route('GET', '/brains', () => [...brains.entries()].map(([name, b]) => ({ name, latest: b.versions.at(-1).hash, channels: b.channels,
  description: parseDefinition(b.versions.at(-1).definition).description || '', versions: b.versions.length })));
route('GET', '/brains/{name}/versions', (p) => { const b = brains.get(p.name); if (!b) throw httpErr(404, `no brain ${p.name}`); return [...b.versions].reverse().map(info); });
route('GET', '/brains/{name}/versions/{hash}', (p) => { const v = findVersion(p.name, p.hash); if (!v) throw httpErr(404, 'no such version'); return fullVersion(v); });
route('POST', '/brains/{name}/versions', (p, body) => {
  if (!/^[a-z][a-z0-9_-]*$/.test(p.name)) throw httpErr(400, 'brain name must match ^[a-z][a-z0-9_-]*$');
  const r = createBrainVersion(p.name, body.definition, body.modules || {}, body.message, body.author || 'operator');
  return [r.status, r.body];
});
route('POST', '/brains/lint', (p, body) => {
  let def = {}; try { def = parseDefinition(body.definition); } catch { /* reported by lint */ }
  const libs = resolveLibs(def || {});
  return lintBrain(body.definition, body.modules || {}, manifest, Object.fromEntries(Object.entries(libs).map(([k, v]) => [k, v.source])));
});
route('GET', '/brains/{name}/diff', (p, body, q) => {
  const a = findVersion(p.name, q.get('from') || ''), b = findVersion(p.name, q.get('to') || '');
  if (!a || !b) throw httpErr(404, 'from/to version not found');
  const A = fullVersion(a), B = fullVersion(b);
  const srcs = v => ({ ...v.modules, ...Object.fromEntries(Object.entries(v.libraries).map(([k, x]) => [k, x.source])) });
  const sa = srcs(A), sb = srcs(B);
  const paths = [...new Set([...Object.keys(sa), ...Object.keys(sb)])].sort();
  return { from: a.hash, to: b.hash, definitionDiff: unifiedDiff(a.definition, b.definition, `brain.yaml@${a.hash}`, `brain.yaml@${b.hash}`),
    modules: paths.map(path => ({ path, unifiedDiff: unifiedDiff(sa[path] ?? '', sb[path] ?? '', `${path}@${a.hash}`, `${path}@${b.hash}`),
      status: !(path in sa) ? 'added' : !(path in sb) ? 'removed' : sa[path] === sb[path] ? 'same' : 'changed' })) };
});
route('PUT', '/brains/{name}/channels/{channel}', (p, body) => {
  const b = brains.get(p.name); if (!b) throw httpErr(404, 'no brain');
  if (!findVersion(p.name, body.hash)) throw httpErr(404, 'no such version');
  b.channels[p.channel] = findVersion(p.name, body.hash).hash;
  for (const bot of bots.values()) if (bot.rec.brain === p.name && bot.rec.channel === p.channel && !bot.pinned) applyVersion(bot, p.name, b.channels[p.channel], p.channel, true, 'operator', `channel ${p.channel} moved`);
  return { brain: p.name, channel: p.channel, hash: b.channels[p.channel] };
});
route('GET', '/brains/{name}/assignments', (p) => assignments.filter(a => a.brain === p.name).slice().reverse());
route('GET', '/brains/{name}/metrics', (p, body, q) => {
  const b = brains.get(p.name); if (!b) throw httpErr(404, 'no brain');
  const want = (q.get('versions') || '').split(',').filter(Boolean);
  const list = want.length ? b.versions.filter(v => want.some(w => v.hash.startsWith(w))) : b.versions;
  return list.map((v, i) => {
    const def = parseDefinition(v.definition); const r = mulberry(v.hash);
    const states = (def.layers || []).flatMap(l => Object.keys(l.machine?.states || {}).map(s => [`${l.name}/${s}`, r()]));
    const total = {}; for (const l of def.layers || []) total[l.name] = states.filter(s => s[0].startsWith(l.name + '/')).reduce((a, s) => a + s[1], 0);
    return { hash: v.hash, perClass: [6, 11].map(cf => ({ classFamily: cf, bots: 1 + Math.floor(r() * 3), hours: +(5 + r() * 40).toFixed(1),
      expPerHour: Math.round(150000 + r() * 90000 + i * 20000), deathsPerHour: +(r() * 0.8).toFixed(2), questsPerHour: +(2 + r() * 4).toFixed(1),
      stuckExitsPerHour: +(r() * 3).toFixed(2), tickP99Us: Math.round(500 + r() * 400),
      stateShare: Object.fromEntries(states.map(([k, x]) => [k, +(x / total[k.split('/')[0]]).toFixed(3)])) })) };
  });
});
function mulberry(s) { let a = parseInt(s.slice(0, 8), 16) >>> 0; return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

function applyVersion(bot, name, hash, channel, keepState, by, why) {
  const v = findVersion(name, hash); if (!v) throw httpErr(404, 'no such version');
  const rec = { bot: bot.rec.id, at: iso(), fromHash: bot.rec.versionHash ?? null, toHash: v.hash, brain: name, by, reason: why || '', keptState: !!keepState };
  bot.rec.brain = name; bot.rec.versionHash = v.hash; bot.rec.channel = channel ?? null;
  if (bot.sim && bot.sim.def.brain === name && keepState) bot.sim.load(parseDefinition(v.definition), `${name}@${v.hash}`, true);
  else if (bot.state !== 'Offline') startSim(bot);
  assignments.push(rec);
  pushLog(bot, 'brain', 'N', `applied ${name}@${v.hash}${channel ? ` (channel ${channel})` : ''}, keepState=${!!keepState}`);
  return rec;
}
route('POST', '/bots/{id}/brain', (p, body) => {
  const b = needBot(p);
  const br = brains.get(body.name); if (!br) throw httpErr(404, `no brain ${body.name}`);
  const hash = body.hash || (body.channel ? br.channels[body.channel] : br.versions.at(-1).hash);
  if (!hash) throw httpErr(400, `channel ${body.channel} is not set`);
  b.pinned = !!body.hash && !body.channel;
  return applyVersion(b, body.name, hash, body.channel ?? null, body.keepState !== false, 'operator', body.reason || (body.channel ? `channel ${body.channel}` : 'apply'));
});
route('POST', '/bots/{id}/brain/rollback', (p, body) => {
  const b = needBot(p);
  const mine = assignments.filter(a => a.bot === b.rec.id);
  const to = body.to || mine.at(-1)?.fromHash;
  if (!to) throw httpErr(409, 'nothing to roll back to');
  return applyVersion(b, b.rec.brain, to, null, true, 'operator', 'rollback');
});
route('GET', '/bots/{id}/assignments', (p) => { const b = needBot(p); return assignments.filter(a => a.bot === b.rec.id).slice().reverse(); });

// libraries
route('GET', '/libraries', () => [...libraries.entries()].map(([path, list]) => ({ path, latest: list.at(-1).hash, readOnly: path.startsWith('lib/'), versions: list.length,
  usedBy: [...brains.entries()].filter(([, b]) => (parseDefinition(b.versions.at(-1).definition).modules || []).some(m => m.split('@')[0] === path)).map(([n]) => n) })));
route('GET', '/libraries/{path*}/versions/{hash}', (p) => {
  const list = libraries.get(decodeURIComponent(p.path)); const v = list?.find(x => x.hash.startsWith(p.hash));
  if (!v) throw httpErr(404, 'no such library version'); return v;
});
route('GET', '/libraries/{path*}/versions', (p) => { const list = libraries.get(decodeURIComponent(p.path)); if (!list) throw httpErr(404, 'no such library'); return [...list].reverse().map(({ source, ...v }) => v); });
route('POST', '/libraries/{path*}/versions', (p, body) => {
  const path = decodeURIComponent(p.path);
  if (path.startsWith('lib/')) throw httpErr(403, 'lib/* is host-provided and read-only');
  if (!/^(user\/[a-z0-9_-]+|brain\/[a-z0-9_-]+)\/[a-z0-9_/-]+$/.test(path)) throw httpErr(400, 'path must be user/<owner>/<name> or brain/<brain>/<name>');
  const lint = lintLua(body.source || '', manifest, { module: path });
  if (lint.some(l => l.severity === 'error')) return [400, problem(400, 'lint errors', lint)];
  const v = addLibraryVersion(path, body.source, body.message, body.author || 'operator');
  return [201, { ...v, source: undefined, lint }];
});
route('POST', '/libraries/lint', (p, body) => lintLua(body.source || '', manifest, { module: body.path || null }));
route('GET', '/api-manifest', () => manifest);

// observation
route('GET', '/bots/{id}/debug', (p) => needSim(p).sim.debugFrame());
route('GET', '/bots/{id}/transitions', (p, b, q) => {
  const s = needSim(p).sim; const since = Number(q.get('since') || 0); const layer = q.get('layer');
  return s.transitions.filter(t => t.tick > since && (!layer || t.layer === layer)).slice(-200).reverse();
});
route('GET', '/bots/{id}/tick', (p, b, q) => needSim(p).sim.ticks.slice(-Number(q.get('last') || 1000)));
route('GET', '/bots/{id}/sensors', (p) => needSim(p).sim.sensorFrame());
route('GET', '/bots/{id}/log', (p, b, q) => {
  const bot = needBot(p);
  const lvl = { crit: 0, note: 1, info: 2, debug: 3, trace: 4 }[(q.get('level') || 'trace').toLowerCase()] ?? 4;
  const cat = q.get('category'); const text = (q.get('text') || '').toLowerCase();
  const from = q.get('from') ? Date.parse(q.get('from')) : 0, to = q.get('to') ? Date.parse(q.get('to')) : Infinity;
  const max = Number(q.get('max') || 500);
  return bot.logRing.filter(e => LEVEL_LETTER[e.level] <= lvl && (!cat || e.category === cat) && (!text || e.text.toLowerCase().includes(text))
    && Date.parse(e.t) >= from && Date.parse(e.t) <= to).slice(-max);
});
route('GET', '/bots/{id}/log-levels', (p) => needBot(p).logLevels);
route('PUT', '/bots/{id}/log-levels', (p, body) => {
  const b = needBot(p);
  if (!(body.category in b.logLevels)) throw httpErr(400, `unknown category ${body.category}`);
  if (!LEVEL_NAME.includes(String(body.level).toLowerCase())) throw httpErr(400, 'level: crit, note, info, debug, trace');
  b.logLevels[body.category] = { level: String(body.level).toLowerCase(), expiresAt: body.expiryMinutes ? iso(Date.now() + body.expiryMinutes * 60000) : null };
  return b.logLevels;
});
route('GET', '/bots/{id}/packet-capture', (p) => needBot(p).packetCapture);
route('POST', '/bots/{id}/packet-capture', (p, body) => {
  const b = needBot(p); Object.assign(b.packetCapture, body);
  if (body.enabled) b.packetCapture.files = [...b.packetCapture.files, { name: `packets-${b.rec.id}-${Date.now()}.${body.format === 'binary' ? 'bin' : 'log'}`, sizeBytes: 0, startedAt: iso() }];
  b.packetCapture.until = body.enabled ? iso(Date.now() + (body.minutes || 15) * 60000) : null;
  return b.packetCapture;
});
route('GET', '/log/stats', () => [...bots.values()].map(b => ({ bot: b.rec.id, lines1m: b.logRing.filter(e => Date.now() - Date.parse(e.t) < 60000).length, drops: 0, collapsed: b.logRing.reduce((a, e) => a + e.repeat - 1, 0) })));
route('GET', '/bots/{id}/recordings', (p) => {
  const b = needBot(p); if (!b.history.length) return [];
  return [{ id: 'recent', kind: 'ring', startedAt: b.history[0].t, endedAt: b.history.at(-1).t, frames: b.history.length, ticks: b.history.at(-1).tick - b.history[0].tick, sizeBytes: 0 }];
});
route('GET', '/bots/{id}/recordings/{rid}/frames', (p, body, q) => {
  const b = needBot(p);
  const from = q.get('from') ? Date.parse(q.get('from')) : 0, to = q.get('to') ? Date.parse(q.get('to')) : Infinity;
  const every = Math.max(1, Number(q.get('every') || 1));
  const idx = b.history.map((f, i) => [f, i]).filter(([f]) => Date.parse(f.t) >= from && Date.parse(f.t) <= to).filter((x, i) => i % every === 0);
  return { debug: idx.map(([f]) => f), sensors: idx.map(([, i]) => b.sensorHistory[i]).filter(Boolean) };
});
route('GET', '/bots/{id}/inventory', (p) => {
  needSim(p);
  return { bagSize: 36, slots: Array.from({ length: 12 }, (_, i) => ({ slot: i, itemId: 500 + i, name: `item #${500 + i}`, lot: 1 + (i % 5), category: ['CONSUMABLE', 'EQUIP', 'QUEST', 'MATERIAL', 'JUNK', 'POTION'][i % 6] })),
    equipped: [{ equipSlot: 0, itemId: 900, name: 'item #900' }], money: needSim(p).sim.world.money };
});
route('GET', '/bots/{id}/quests', (p) => { needSim(p); return [1, 2, 3].map(i => ({ id: 200 + i, name: `quest #${200 + i}`, state: i === 3 ? 'ready' : 'active',
  objectives: [{ kind: 'KILL', mobId: 100 + i, itemId: -1, have: i * 2, need: 10 }] })); });
route('GET', '/bots/{id}/skills', (p) => { needSim(p); return [1, 2, 3, 4].map(i => ({ id: 3000 + i, name: `skill #${3000 + i}`, rank: i, tags: ['DAMAGE', 'HEAL', 'BUFF', 'AOE'][i - 1],
  readyInMs: i === 2 ? 1800 : 0, spCost: 20 * i, canUse: i !== 2, whyNot: i === 2 ? 'R_COOLDOWN' : null })); });
route('GET', '/knowledge/mobs/{mobId}', (p) => ({ mobId: Number(p.mobId), detect: [240, 200, 280, 14], leash: [1500, 1300, 1700, 6], reach: [60, 50, 70, 30], interval: [1800, 1600, 2000, 40] }));
route('GET', '/metrics', () => '# mock host\nfiestabot_tick_us{bot="MockArcher",step="total",quantile="0.99"} 690\n');

// ---------------------------------------------------------------- server
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  if (!url.pathname.startsWith('/api/')) { if (serveStatic(req, res, url.pathname)) return; return send(res, 404, problem(404, 'not found')); }
  if (TOKEN && req.headers.authorization !== `Bearer ${TOKEN}`) return send(res, 401, problem(401, 'missing or wrong bearer token'));
  for (const r of routes) {
    if (r.method !== req.method) continue;
    const m = r.re.exec(url.pathname); if (!m) continue;
    try {
      const body = ['POST', 'PUT', 'PATCH'].includes(req.method) ? await readBody(req) : {};
      if (body === null) return send(res, 400, problem(400, 'body is not JSON'));
      const out = await r.fn(m.groups || {}, body, url.searchParams);
      if (Array.isArray(out) && out.length === 2 && typeof out[0] === 'number') return out[0] === 204 ? (res.writeHead(204), res.end()) : send(res, out[0], out[1]);
      return send(res, 200, out);
    } catch (e) { return send(res, e.status || 500, problem(e.status || 500, e.message)); }
  }
  send(res, 404, problem(404, `no route ${req.method} ${url.pathname}`));
});
server.on('upgrade', (req, socket) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname !== '/api/v3/ws') { socket.destroy(); return; }
  if (TOKEN && url.searchParams.get('token') !== TOKEN) { socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n'); socket.destroy(); return; }
  const mine = new Set();
  const conn = acceptUpgrade(req, socket, msg => {
    if (msg.op === 'sub') {
      const lv = msg.level ? ({ crit: 0, c: 0, note: 1, n: 1, info: 2, i: 2, debug: 3, d: 3, trace: 4, t: 4 })[String(msg.level).toLowerCase()] : 1;
      const sub = { conn, topic: msg.topic, bot: msg.bot || null, hz: msg.hz || 5, levelN: lv ?? 1 };
      subs.add(sub); mine.add(sub);
      if (msg.topic === 'fleet') sendDelta(sub, [...bots.values()].map(summary));
      if (msg.topic === 'debug') { const b = bots.get(msg.bot); if (b?.sim) sendDelta(sub, b.sim.debugFrame()); }
    } else if (msg.op === 'unsub') {
      for (const s of [...mine]) if (s.topic === msg.topic && (!msg.bot || s.bot === msg.bot)) { subs.delete(s); mine.delete(s); }
    }
  }, () => { for (const s of mine) subs.delete(s); });
});
server.listen(PORT, '127.0.0.1', () => console.log(`mock bot host on http://127.0.0.1:${PORT}  (api /api/v3, ws /api/v3/ws${STATIC ? ', static ' + STATIC : ''}${TOKEN ? ', token required' : ''})`));
