// Stand-in for the host's BrainLinter (API_SPEC 5.4, 6, REDESIGN 11 "Linting"), good enough to exercise the UI:
// Lua syntax, unknown API names, writes to globals, structural checks of the definition.
import luaparse from 'luaparse';
import YAML from 'yaml';

const LUA_GLOBALS = new Set(['require', 'print', 'pairs', 'ipairs', 'next', 'type', 'tostring', 'tonumber', 'math', 'string',
  'table', 'select', 'error', 'pcall', 'assert', 'unpack', 'setmetatable', 'getmetatable', 'rawget', 'rawset', 'bb', 'nil']);

export function parseDefinition(text) {
  if (typeof text !== 'string') return text;
  const t = text.trim();
  if (t.startsWith('{')) return JSON.parse(t);
  return YAML.parse(text);
}

function luaErr(e) {
  return { line: e.line || 1, column: e.column || 0, message: String(e.message).replace(/^\[\d+:\d+\] /, '') };
}

/** Syntax + API names + global writes for one chunk. */
export function lintLua(source, manifest, ctx) {
  const out = [];
  let ast;
  try {
    ast = luaparse.parse(source, { luaVersion: '5.3', locations: true, scope: true });
  } catch (e) {
    const le = luaErr(e);
    out.push({ severity: 'error', code: 'LUA_SYNTAX', message: le.message, module: ctx.module ?? null, line: le.line,
      column: le.column, layer: ctx.layer ?? null, state: ctx.state ?? null });
    return out;
  }
  const modules = new Map();
  for (const f of manifest.functions) {
    if (f.module === '_G') continue;
    if (!modules.has(f.module)) modules.set(f.module, new Set());
    modules.get(f.module).add(f.name);
  }
  const consts = manifest.constants || {};
  const globalsFns = new Set(manifest.functions.filter(f => f.module === '_G').map(f => f.name));
  const seen = new Set();
  const walk = (n, parent) => {
    if (!n || typeof n !== 'object') return;
    if (Array.isArray(n)) { n.forEach(c => walk(c, parent)); return; }
    if (n.type === 'MemberExpression' && n.base?.type === 'Identifier' && n.base.isLocal === false && modules.has(n.base.name)) {
      const name = n.identifier?.name;
      const ok = modules.get(n.base.name).has(name) || (consts[n.base.name] && name in consts[n.base.name]);
      const key = n.base.name + '.' + name + ':' + n.loc.start.line;
      if (!ok && !seen.has(key)) {
        seen.add(key);
        out.push({ severity: 'error', code: 'API_UNKNOWN', message: `unknown API name ${n.base.name}.${name}`,
          module: ctx.module ?? null, line: n.loc.start.line, column: n.loc.start.column, layer: ctx.layer ?? null, state: ctx.state ?? null });
      }
    }
    if (n.type === 'AssignmentStatement') {
      for (const v of n.variables) {
        if (v.type === 'Identifier' && v.isLocal === false) {
          out.push({ severity: 'warning', code: 'GLOBAL_WRITE', message: `write to global '${v.name}' (use bb.* instead)`,
            module: ctx.module ?? null, line: v.loc.start.line, column: v.loc.start.column, layer: ctx.layer ?? null, state: ctx.state ?? null });
        }
      }
    }
    if (n.type === 'Identifier' && n.isLocal === false && parent?.type !== 'MemberExpression'
      && !(parent?.type === 'TableKeyString' && parent.key === n) && !modules.has(n.name)
      && !LUA_GLOBALS.has(n.name) && !globalsFns.has(n.name) && !(ctx.extraGlobals || []).includes(n.name)) {
      const key = 'g:' + n.name;
      if (!seen.has(key) && !(parent?.type === 'AssignmentStatement')) {
        seen.add(key);
        out.push({ severity: 'warning', code: 'GLOBAL_READ', message: `read of undefined global '${n.name}'`,
          module: ctx.module ?? null, line: n.loc.start.line, column: n.loc.start.column, layer: ctx.layer ?? null, state: ctx.state ?? null });
      }
    }
    for (const [k, v] of Object.entries(n)) {
      if (k === 'loc' || k === 'range') continue;
      if (v && typeof v === 'object') walk(v, n);
    }
  };
  walk(ast.body, null);
  return out;
}

function moduleExports(source) {
  // names assigned as function M.name / M.name = function in a returned table
  const names = new Set();
  for (const m of source.matchAll(/function\s+[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\s*\(/g)) names.add(m[1]);
  return names;
}

function isFnRef(s) { return /^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$/.test(s.trim()); }

/** Full lint of a brain definition + its modules (POST /brains/lint). */
export function lintBrain(definitionText, modules, manifest, libraries = {}) {
  const out = [];
  let def;
  try { def = parseDefinition(definitionText); } catch (e) {
    return [{ severity: 'error', code: 'DEF_PARSE', message: String(e.message), module: null, line: e.linePos?.[0]?.line || 1, layer: null, state: null }];
  }
  if (!def || typeof def !== 'object') return [{ severity: 'error', code: 'DEF_PARSE', message: 'definition is empty', module: null, line: 1, layer: null, state: null }];
  if (!def.brain || !/^[a-z][a-z0-9_-]*$/.test(def.brain)) out.push(e('DEF_SCHEMA', 'brain: name must match ^[a-z][a-z0-9_-]*$'));
  if (def.api !== 3) out.push(e('DEF_API', `api: ${def.api} (this host speaks api 3)`));
  if (!Array.isArray(def.layers) || def.layers.length === 0) out.push(e('DEF_SCHEMA', 'layers: at least one layer required'));
  const allSources = { ...libraries, ...modules };
  for (const [path, src] of Object.entries(modules || {})) out.push(...lintLua(src, manifest, { module: path }));
  // short module name -> exports (brain/leveller/survival -> survival)
  const exportsByShort = new Map();
  for (const ref of def.modules || []) {
    const path = ref.replace(/@[0-9a-f]+$/, '');
    const src = allSources[path];
    if (src == null) { out.push(e('MODULE_MISSING', `module ${path} is not in this version or the library store`)); continue; }
    exportsByShort.set(path.split('/').pop(), moduleExports(src));
  }
  const resources = new Set(manifest.resources);
  const events = new Set(manifest.events.map(x => x.name));
  const sensors = new Set(manifest.sensors.map(x => x.name));
  const machines = def.machines || {};
  const checkFn = (ref, layer, state, field) => {
    if (!ref) return;
    if (isFnRef(ref)) {
      const [mod, fn] = ref.trim().split('.');
      if (exportsByShort.has(mod)) {
        if (!exportsByShort.get(mod).has(fn)) out.push({ ...e('FN_MISSING', `${field}: ${ref} is not defined in module ${mod}`), layer, state });
        return;
      }
      if (manifest.functions.some(f => f.module === mod && f.name === fn)) return; // a helper reference
      out.push({ ...e('FN_MISSING', `${field}: ${ref}: no module '${mod}' in this brain`), layer, state });
      return;
    }
    for (const x of lintLua(ref, manifest, { layer, state, module: null, extraGlobals: [...exportsByShort.keys()] })) out.push({ ...x, message: `${field}: ${x.message}` });
  };
  const checkCond = (c, layer, state, where) => {
    if (!c || typeof c !== 'object') return;
    if (c.when != null) {
      const w = String(c.when);
      if (isFnRef(w)) checkFn(w, layer, state, where);
      else for (const x of lintLua('return ' + w, manifest, { layer, state, extraGlobals: [...exportsByShort.keys()] })) out.push({ ...x, message: `${where} when: ${x.message}` });
    }
    if (c.on != null && c.on !== 'action' && !events.has(c.on)) out.push({ ...e('EVENT_UNKNOWN', `${where}: unknown event ${c.on}`), layer, state });
    if (c.value != null && !sensors.has(c.value)) out.push({ ...w('SENSOR_UNKNOWN', `${where}: ${c.value} is not a registered sensor`), layer, state });
    if (c.value != null && (c.op == null || c.than == null)) out.push({ ...e('DEF_SCHEMA', `${where}: value condition needs op and than`), layer, state });
    for (const k of ['all', 'any']) if (Array.isArray(c[k])) c[k].forEach((cc, i) => checkCond(cc, layer, state, `${where}.${k}[${i}]`));
    if (c.not) checkCond(c.not, layer, state, `${where}.not`);
  };
  // sub-graphs (API_SPEC 5.2 + U30 / U31): "G/s" enters G's sub-machine at s, "^/t" leaves for the parent's t
  const subOf = st => (st?.machine ? st.machine : st?.use && machines[st.use] ? machines[st.use] : null);
  const exitsOf = m => Object.values(m?.states || {}).flatMap(st => (st.transitions || []).filter(t => t.to === '^' || String(t.to).startsWith('^/')));
  // entries into named machines through "G/s" at their use sites, and the use sites themselves
  const namedEntries = {};
  const collect = (m) => {
    for (const st of Object.values(m?.states || {})) {
      if (st.machine) collect(st.machine);
      for (const t of st.transitions || []) {
        const [h, inner] = String(t.to).split('/');
        const tgt = m.states?.[h];
        if (inner != null && h !== '^' && tgt?.use) (namedEntries[tgt.use] ||= []).push(inner);
      }
    }
  };
  for (const l of def.layers || []) collect(l.machine);
  for (const m of Object.values(machines)) collect(m);

  const lintMachine = (m, layer, label, isSub, parentStates = null, extraEntries = []) => {
    if (!m || typeof m !== 'object') { out.push({ ...e('DEF_SCHEMA', `${label}: machine missing`), layer }); return; }
    const states = m.states || {};
    const names = Object.keys(states);
    if (!names.length) out.push({ ...e('DEF_SCHEMA', `${label}: no states`), layer });
    if (!(m.initial in states)) out.push({ ...e('INITIAL_MISSING', `${label}: initial state '${m.initial}' does not exist`), layer });
    const reach = new Set([m.initial, ...extraEntries.filter(x => x in states)]);
    let changed = true;
    while (changed) {
      changed = false;
      for (const n of [...reach]) for (const t of states[n]?.transitions || []) {
        const h = String(t.to).split('/')[0];
        if (h in states && !reach.has(h)) { reach.add(h); changed = true; }
      }
    }
    for (const [sn, s] of Object.entries(states)) {
      if (!s || typeof s !== 'object') continue;
      const ts = s.transitions || [];
      const sub = subOf(s);
      const innerExits = sub ? exitsOf(sub) : [];
      if (!reach.has(sn)) out.push({ ...w('UNREACHABLE', `${label}: state '${sn}' is unreachable from '${m.initial}'`), layer, state: sn });
      const exits = ts.filter(t => t.to !== sn);
      if (sub && !exits.length && !innerExits.length && !s.terminal) out.push({ ...e('SUBGRAPH_NO_EXIT', `sub-graph '${sn}' has no way out: no transition of its own and no inner exit (^ / ^/state)`), layer, state: sn });
      else if (!sub && !exits.length && !s.terminal) out.push({ ...e('NO_EXIT', `state '${sn}' has no transition to another state and is not terminal`), layer, state: sn });
      const hasEscape = ts.some(t => t.stuck || t.after != null) || s.timeout != null || (sub && innerExits.some(t => t.stuck || t.after != null));
      if (!s.terminal && !hasEscape) {
        const item = (s.budget === 0 ? e : w)('NO_STUCK_EXIT', `state '${sn}' has no stuck/after/timeout exit`);
        out.push({ ...item, layer, state: sn });
      }
      for (const r of s.claims || []) if (!resources.has(r)) out.push({ ...e('RESOURCE_UNKNOWN', `claims: unknown resource ${r}`), layer, state: sn });
      for (const f of ['enter', 'tick', 'exit']) checkFn(s[f], layer, sn, f);
      if (s.progress && !sensors.has(s.progress) && !isFnRef(s.progress)) {
        for (const x of lintLua('return ' + s.progress, manifest, { layer, state: sn, extraGlobals: [...exportsByShort.keys()] })) out.push({ ...x, message: `progress: ${x.message}` });
      } else if (s.progress && isFnRef(s.progress) && !sensors.has(s.progress)) checkFn(s.progress, layer, sn, 'progress');
      if (s.use && !s.use.includes('${') && !(s.use in machines)) out.push({ ...e('MACHINE_UNKNOWN', `use: no machine named '${s.use}'`), layer, state: sn });
      if (s.use && machines[s.use]) for (const t of new Set(exitsOf(machines[s.use]).filter(x => x.to !== '^').map(x => String(x.to).slice(2)))) {
        if (!(t in states)) out.push({ ...e('EXIT_TARGET_UNKNOWN', `machine '${s.use}' exits to '${t}', which this graph does not have`), layer, state: sn });
      }
      if (s.onError && !(s.onError in states)) out.push({ ...e('TARGET_UNKNOWN', `onError: no state '${s.onError}'`), layer, state: sn });
      ts.forEach((t, i) => {
        const tgt = String(t.to ?? '');
        let ok;
        if (tgt === '^') ok = isSub;
        else if (tgt.startsWith('^/')) ok = isSub && (parentStates == null || parentStates.has(tgt.slice(2)));
        else if (tgt.includes(':')) ok = true;
        else if (tgt.includes('/')) { const [h, inner] = tgt.split('/'); const sm = subOf(states[h]); ok = !!sm && inner in (sm.states || {}); }
        else ok = tgt in states;
        if (!ok) out.push({ ...e('TARGET_UNKNOWN', `transition #${i + 1}: target '${tgt}' does not exist`), layer, state: sn });
        const kinds = ['when', 'on', 'procedure', 'job', 'after', 'every', 'value', 'stuck', 'always', 'all', 'any', 'not'].filter(k => t[k] != null);
        if (kinds.length === 0) out.push({ ...e('COND_MISSING', `transition #${i + 1} to '${tgt}' has no condition`), layer, state: sn });
        const primary = kinds.filter(k => !['all', 'any', 'not'].includes(k));
        if (primary.length > 1 && !(primary.length === 2 && primary.includes('on') && primary.includes('action'))) {
          out.push({ ...w('COND_MULTI', `transition #${i + 1}: several condition kinds (${primary.join(', ')}); use all: [...]`), layer, state: sn });
        }
        checkCond(t, layer, sn, `transition #${i + 1}`);
      });
      if (s.machine) {
        const entries = Object.values(states).flatMap(o => (o.transitions || []).map(t => String(t.to))).filter(x => x.startsWith(sn + '/')).map(x => x.split('/')[1]);
        lintMachine(s.machine, layer, `${label}/${sn}`, true, new Set(names), entries);
      }
    }
  };
  // recursion: a named machine that uses itself, directly or through other machines
  const usesOf = mm => { const r = []; const scan = x => { for (const st of Object.values(x?.states || {})) { if (st.use) r.push(st.use); if (st.machine) scan(st.machine); } }; scan(mm); return r; };
  for (const mn of Object.keys(machines)) {
    const seen = new Set(); const stack = [...usesOf(machines[mn])];
    while (stack.length) { const x = stack.pop(); if (x === mn) { out.push(e('MACHINE_RECURSION', `machine '${mn}' uses itself (directly or through other machines)`)); break; } if (seen.has(x) || !machines[x]) continue; seen.add(x); stack.push(...usesOf(machines[x])); }
  }
  for (const l of def.layers || []) {
    if (!l.name) out.push(e('DEF_SCHEMA', 'layer without a name'));
    if (typeof l.priority !== 'number' || l.priority < 0 || l.priority > 100) out.push({ ...e('DEF_SCHEMA', `layer ${l.name}: priority must be 0..100`), layer: l.name });
    if (l.rate != null && l.rate !== 'tick' && !(Number.isInteger(l.rate) && l.rate >= 50)) out.push({ ...e('DEF_SCHEMA', `layer ${l.name}: rate must be 'tick' or >= 50 ms`), layer: l.name });
    lintMachine(l.machine, l.name, l.name, false);
  }
  for (const [mn, m] of Object.entries(machines)) lintMachine(m, null, `machines.${mn}`, true, null, namedEntries[mn] || []);
  return out;

  function e(code, message) { return { severity: 'error', code, message, module: null, line: 0, layer: null, state: null }; }
  function w(code, message) { return { severity: 'warning', code, message, module: null, line: 0, layer: null, state: null }; }
}
