// Builds the API manifest (GET /api/v3/api-manifest) from docs/API_SPEC.md sections 2 and 3, so the mock
// always matches the spec. The real host emits the same shape from the [LuaFn] source generator.
import { readFileSync } from 'node:fs';

const MODULES = ['self', 'ents', 'threat', 'combat', 'skills', 'act', 'items', 'quests', 'nav', 'npc', 'social',
  'know', 'time', 'ev', 'world', 'dbg'];

function splitRow(line) {
  // split a markdown table row on pipes that are not inside backticks
  const cells = []; let cur = ''; let tick = false;
  for (const ch of line.trim().replace(/^\|/, '').replace(/\|$/, '')) {
    if (ch === '`') tick = !tick;
    if (ch === '|' && !tick) { cells.push(cur.trim()); cur = ''; } else cur += ch;
  }
  cells.push(cur.trim());
  return cells;
}

const FN_RE = /`([a-z]+)\.([a-zA-Z_][a-zA-Z0-9_]*)\(([^`]*?)\)`/g;

function costOf(s) {
  s = (s || '').toLowerCase();
  if (s.includes('job')) return 'job';
  if (s.includes('cached')) return 'cached';
  if (s.includes('o(n)')) return 'O(n)';
  if (s.includes('o(k)')) return 'O(k)';
  return 'O(1)';
}

export function buildManifest(specPath) {
  const text = readFileSync(specPath, 'utf8');
  const lines = text.split(/\r?\n/);
  const start = lines.findIndex(l => l.startsWith('## 2.'));
  const end = lines.findIndex(l => l.startsWith('## 3.'));
  const fns = new Map();
  const add = (module, name, args, returns, cost, alloc, doc, extra = {}) => {
    if (!MODULES.includes(module)) return;
    const key = module + '.' + name;
    if (fns.has(key)) return;
    const params = args ? args.replace(/[[\]]/g, '').split(',').map(a => a.trim()).filter(Boolean) : [];
    fns.set(key, { module, name, signature: `${key}(${args})`, params, returns: returns || '', cost: costOf(cost),
      alloc: alloc || '0', doc: doc || '', ...extra });
  };
  let header = null; let section = '';
  for (let i = start; i < end; i++) {
    const l = lines[i];
    if (l.startsWith('### ') || l.startsWith('#### ')) { section = l; header = null; continue; }
    if (l.startsWith('|') && !header) { header = splitRow(l).map(h => h.toLowerCase()); i++; continue; }
    if (!l.startsWith('|')) {
      header = null;
      const m = /^### 2\.\d+ `([a-z]+)`/.exec(section);
      if (m && (m[1] === 'social' || m[1] === 'know')) {
        const ret = m[1] === 'know' ? 'estimate, low, high, samples' : '';
        for (const mm of l.matchAll(FN_RE)) add(mm[1], mm[2], mm[3], ret, 'O(1)', '0', '');
        for (const mm of l.matchAll(/`([a-zA-Z]+)\(([^`)]*)\)`(?: \(([^)]*)\))?/g)) add(m[1], mm[1], mm[2], mm[3] || ret, 'O(1)', '0', '');
      }
      continue;
    }
    const cells = splitRow(l);
    const col = n => { const idx = header.indexOf(n); return idx >= 0 ? cells[idx] : ''; };
    for (const mm of cells[0].matchAll(FN_RE)) {
      const extra = header.includes('resource') ? { resource: col('resource') === '-' ? null : col('resource') } : {};
      add(mm[1], mm[2], mm[3], col('returns'), col('cost'), col('alloc'), (col('notes') || '').replace(/`/g, ''), extra);
    }
  }
  const globals = [];
  const canUse = fns.get('skills.canUse');
  const dbgLog = fns.get('dbg.log');
  if (canUse) globals.push({ ...canUse, module: '_G', name: 'canUse', signature: 'canUse(id, target)', aliasOf: 'skills.canUse' });
  if (dbgLog) globals.push({ ...dbgLog, module: '_G', name: 'log', signature: 'log(fmt, ...)', aliasOf: 'dbg.log' });

  const constants = {};
  const listAfter = (re) => { const m = re.exec(text); return m ? m[1].split(',').map(s => s.trim().replace(/\.$/, '')).filter(Boolean) : []; };
  const bits = (names) => Object.fromEntries(names.map((n, i) => [n, 2 ** i]));
  const seq = (names, from = 0) => Object.fromEntries(names.map((n, i) => [n, i + from]));
  constants.ents = bits(listAfter(/Filter constants \(mask\): `([^`]+)`/));
  constants.skills = bits(listAfter(/Tag constants \(mask, precomputed from ActiveSkill at load\): `([^`]+)`/));
  Object.assign(constants.skills, seq([...new Set([...text.matchAll(/`(R_[A-Z_]+)`/g)].map(m => m[1]))], 1));
  constants.npc = bits(listAfter(/Role constants \(learned from menus, client-legal\): `([^`]+)`/));
  constants.items = bits(listAfter(/masks `([^`]+)`/));
  constants.act = seq([...new Set([...text.matchAll(/`(L_[A-Z_]+)`/g)].map(m => m[1]))], 1000);
  const worldRest = listAfter(/`world\.SELF, ([^`]+)`/);
  constants.world = seq(['SELF', ...worldRest]);
  constants.dbg = { NOTE: 1, INFO: 2, DEBUG: 3, TRACE: 4 };

  const events = [];
  const evStart = lines.findIndex(l => l.startsWith('## 3. Events'));
  const evEnd = lines.findIndex((l, i) => i > evStart && l.startsWith('## 4.'));
  for (let i = evStart; i < evEnd; i++) {
    const l = lines[i];
    if (!l.startsWith('| `')) continue;
    const c = splitRow(l);
    for (const mm of c[0].matchAll(/`([A-Z_]+)`/g)) events.push({ name: mm[1], arg1: c[1], arg2: c[2], doc: c[3] });
  }
  constants.ev = seq(events.map(e => e.name), 1);

  const functions = [...fns.values(), ...globals];
  const sensors = functions
    .filter(f => f.module !== '_G' && f.params.length === 0 && /^(number|integer|pct|ms|boolean)/.test(f.returns))
    .map(f => ({ name: `${f.module}.${f.name}`, returns: f.returns, doc: f.doc }));
  return {
    apiVersion: 3,
    functions,
    constants,
    events,
    sensors,
    resources: ['Movement', 'Combat', 'Cast', 'Inventory', 'Npc', 'Chat'],
    procedures: ['OPEN_SHOP', 'TALK', 'GATE', 'DISMOUNT', 'REVIVE', 'TRADE', 'TENT', 'STORAGE', 'TRAVEL'],
    jobs: ['PATH', 'ROUTE_LENGTH', 'QUEST_PLAN', 'TRAVEL_PLAN', 'SAFE_SPOT', 'CUSTOM'],
    actionKinds: ['CAST', 'CAST_GROUND', 'BASH', 'MOVE', 'USE_ITEM', 'STONE_HP', 'STONE_SP', 'LOOT', 'BUY', 'SELL',
      'OPEN_SHOP', 'TALK', 'GATE', 'REVIVE', 'TRADE', 'SAY', 'WHISPER'],
  };
}

if (process.argv[1] && process.argv[1].endsWith('manifest.mjs')) {
  const m = buildManifest(process.argv[2]);
  const per = {};
  for (const f of m.functions) per[f.module] = (per[f.module] || 0) + 1;
  console.log(m.functions.length, JSON.stringify(per), 'events', m.events.length, 'sensors', m.sensors.length);
  console.log(JSON.stringify(m.constants).slice(0, 900));
}
