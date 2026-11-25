// Instant, client-side Lua checks while typing: syntax (luaparse) and unknown API names from the manifest.
// The host's linter (POST /brains/lint, /libraries/lint) stays the authority; this is the fast feedback loop.
import luaparse from 'luaparse';
import type { ApiManifest } from '../api/types';

export interface LuaIssue { severity: 'error' | 'warning'; message: string; line: number; column: number; endColumn?: number; code: string }

export function lintLuaSource(source: string, manifest: ApiManifest | null, opts: { expression?: boolean } = {}): LuaIssue[] {
  const src = opts.expression ? 'return ' + source : source;
  const shift = opts.expression ? 7 : 0;
  let ast: any;
  try {
    ast = luaparse.parse(src, { luaVersion: '5.3', locations: true, scope: true } as any);
  } catch (e: any) {
    const line = e.line || 1;
    return [{ severity: 'error', code: 'LUA_SYNTAX', message: String(e.message).replace(/^\[\d+:\d+\] /, ''), line,
      column: Math.max(0, (e.column || 0) - (line === 1 ? shift : 0)) }];
  }
  if (!manifest) return [];
  const mods = new Map<string, Set<string>>();
  for (const f of manifest.functions) {
    if (f.module === '_G') continue;
    if (!mods.has(f.module)) mods.set(f.module, new Set());
    mods.get(f.module)!.add(f.name);
  }
  const out: LuaIssue[] = [];
  const walk = (n: any) => {
    if (!n || typeof n !== 'object') return;
    if (Array.isArray(n)) { n.forEach(walk); return; }
    if (n.type === 'MemberExpression' && n.base?.type === 'Identifier' && n.base.isLocal === false && mods.has(n.base.name)) {
      const name = n.identifier?.name;
      const known = mods.get(n.base.name)!.has(name) || (manifest.constants?.[n.base.name] && name in manifest.constants[n.base.name]);
      if (!known) {
        const line = n.loc.start.line;
        const col = n.identifier.loc.start.column - (line === 1 ? shift : 0);
        out.push({ severity: 'error', code: 'API_UNKNOWN', message: `unknown API name ${n.base.name}.${name}`, line, column: col, endColumn: col + String(name).length });
      }
    }
    for (const [k, v] of Object.entries(n)) if (k !== 'loc' && k !== 'range' && v && typeof v === 'object') walk(v);
  };
  walk(ast.body);
  return out;
}

/** Function names a library module exports (`function M.name(...)`), with their parameter lists. */
export function moduleExports(source: string): { name: string; params: string; doc: string }[] {
  const out: { name: string; params: string; doc: string }[] = [];
  const lines = source.split('\n');
  lines.forEach((l, i) => {
    const m = /^\s*function\s+[A-Za-z_][A-Za-z0-9_]*\.([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)/.exec(l);
    if (!m) return;
    const doc: string[] = [];
    for (let j = i - 1; j >= 0 && /^\s*--/.test(lines[j]); j--) doc.unshift(lines[j].replace(/^\s*--\s?/, ''));
    out.push({ name: m[1], params: m[2].trim(), doc: doc.join('\n') });
  });
  return out;
}
