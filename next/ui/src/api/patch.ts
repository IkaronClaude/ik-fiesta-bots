// RFC 6902 apply (add / remove / replace / move / copy / test) on a structural copy of the target.
type Op = { op: string; path: string; value?: unknown; from?: string };

const unesc = (s: string) => s.replace(/~1/g, '/').replace(/~0/g, '~');
const parts = (p: string) => (p === '' ? [] : p.split('/').slice(1).map(unesc));

function clone<T>(v: T): T { return v === null || typeof v !== 'object' ? v : (structuredClone(v) as T); }

function get(doc: any, ps: string[]) { let c = doc; for (const p of ps) c = c?.[Array.isArray(c) ? Number(p) : p]; return c; }

function set(root: any, ps: string[], value: unknown, mode: 'add' | 'replace' | 'remove'): any {
  if (!ps.length) return mode === 'remove' ? undefined : value;
  const parent = get(root, ps.slice(0, -1));
  const k = ps[ps.length - 1];
  if (parent === null || typeof parent !== 'object') throw new Error('patch path missing: /' + ps.join('/'));
  if (Array.isArray(parent)) {
    const i = k === '-' ? parent.length : Number(k);
    if (mode === 'add') parent.splice(i, 0, value);
    else if (mode === 'replace') parent[i] = value;
    else parent.splice(i, 1);
  } else if (mode === 'remove') delete parent[k];
  else parent[k] = value;
  return root;
}

export function applyPatch<T>(doc: T, patch: Op[]): T {
  let out: any = clone(doc);
  for (const o of patch) {
    const ps = parts(o.path);
    switch (o.op) {
      case 'add': out = set(out, ps, clone(o.value), 'add'); break;
      case 'replace': out = set(out, ps, clone(o.value), 'replace'); break;
      case 'remove': out = set(out, ps, undefined, 'remove'); break;
      case 'move': { const fp = parts(o.from!); const v = get(out, fp); out = set(out, fp, undefined, 'remove'); out = set(out, ps, v, 'add'); break; }
      case 'copy': out = set(out, ps, clone(get(out, parts(o.from!))), 'add'); break;
      case 'test': if (JSON.stringify(get(out, ps)) !== JSON.stringify(o.value)) throw new Error('patch test failed'); break;
      default: throw new Error('unknown patch op ' + o.op);
    }
  }
  return out;
}
