// RFC 6902 apply (add / remove / replace / move / copy / test) with STRUCTURAL SHARING: only the containers on a
// patched path are copied, everything else keeps its identity. Memoised components (a bag slot, a skill tile,
// an entity) then re-render only when their own sub-object changed.
type Op = { op: string; path: string; value?: unknown; from?: string };

const unesc = (s: string) => s.replace(/~1/g, '/').replace(/~0/g, '~');
const parts = (p: string) => (p === '' ? [] : p.split('/').slice(1).map(unesc));

function get(doc: any, ps: string[]) { let c = doc; for (const p of ps) c = c?.[Array.isArray(c) ? Number(p) : p]; return c; }

/** Returns a new root with `value` set (or removed) at ps; copies only the containers along the path. */
function setIn(node: any, ps: string[], i: number, value: unknown, mode: 'add' | 'replace' | 'remove'): any {
  if (i === ps.length) return mode === 'remove' ? undefined : value;
  if (node === null || typeof node !== 'object') throw new Error('patch path missing: /' + ps.join('/'));
  const k = ps[i];
  const last = i === ps.length - 1;
  if (Array.isArray(node)) {
    const a = node.slice();
    const idx = k === '-' ? a.length : Number(k);
    if (last && mode === 'add') a.splice(idx, 0, value);
    else if (last && mode === 'remove') a.splice(idx, 1);
    else a[idx] = setIn(a[idx], ps, i + 1, value, mode);
    return a;
  }
  const o = { ...node };
  if (last && mode === 'remove') delete o[k];
  else o[k] = setIn(o[k], ps, i + 1, value, mode);
  return o;
}

export function applyPatch<T>(doc: T, patch: Op[]): T {
  let out: any = doc;
  for (const o of patch) {
    const ps = parts(o.path);
    switch (o.op) {
      case 'add': out = setIn(out, ps, 0, o.value, 'add'); break;
      case 'replace': out = setIn(out, ps, 0, o.value, 'replace'); break;
      case 'remove': out = setIn(out, ps, 0, undefined, 'remove'); break;
      case 'move': { const fp = parts(o.from!); const v = get(out, fp); out = setIn(out, fp, 0, undefined, 'remove'); out = setIn(out, ps, 0, v, 'add'); break; }
      case 'copy': out = setIn(out, ps, 0, structuredClone(get(out, parts(o.from!))), 'add'); break;
      case 'test': if (JSON.stringify(get(out, ps)) !== JSON.stringify(o.value)) throw new Error('patch test failed'); break;
      default: throw new Error('unknown patch op ' + o.op);
    }
  }
  return out;
}
