// Static game data by id (names, descriptions, icons, map art), fetched once from the cacheable GET endpoints of
// API_SPEC 14.2 and kept in memory for the page's lifetime. Lookups coalesce: every id asked for in the same frame
// goes out as one batch request per kind (`?ids=`). Never re-requested per game-state update; the browser's HTTP
// cache (ETag / max-age) covers reloads.
import { useSyncExternalStore } from 'react';
import { getToken } from '../../../api/client';

export type StaticKind = 'items' | 'skills' | 'abstates' | 'mobs' | 'npcs' | 'maps' | 'classes';

export interface ItemInfo { id: number; name: string; kind?: string; grade?: string; levelReq?: number; maxLot?: number; sellPrice?: number; desc?: string; stats?: Record<string, string | number>; icon?: string | null }
export interface SkillInfo { id: number; name: string; rank?: number; spCost?: number; cooldownMs?: number; castMs?: number; range?: number; tags?: string; desc?: string; icon?: string | null }
export interface AbstateInfo { id: number; name: string; debuff?: boolean; desc?: string; icon?: string | null }
export interface MobInfo { id: number; name: string; level?: number; grade?: string; body?: number; maxHp?: number; npc?: boolean }
export interface NpcInfo { id: number; name: string; role?: string }
export interface MapInfo { id: number; short?: string; name: string; image?: string | null; cover?: { x0: number; y0: number; x1: number; y1: number } }

type Entry = { state: 'loading' | 'ok' | 'missing'; data?: any };
const cache = new Map<string, Entry>();
const listeners = new Set<() => void>();
let version = 0;
const queue = new Set<string>();
let scheduled = false;

function notify() { version++; listeners.forEach(l => l()); }

const BATCH: StaticKind[] = ['items', 'skills', 'abstates', 'mobs'];

/** Map the host's static records (API_SPEC 14.2) onto the view's shapes. */
function norm(kind: StaticKind, d: any): any {
  if (!d) return d;
  switch (kind) {
    case 'items': return { id: d.id, name: d.name ?? `item ${d.id}`, kind: d.kind ?? d.type, grade: d.grade, levelReq: d.levelReq ?? d.demandLv, maxLot: d.maxLot,
      sellPrice: d.sellPrice, desc: d.desc ?? d.description, stats: d.stats, icon: iconRoute('item', d) };
    case 'skills': return { id: d.id, name: d.name ?? `skill ${d.id}`, rank: d.rank ?? d.step, spCost: d.spCost ?? d.sp, cooldownMs: d.cooldownMs, castMs: d.castMs,
      range: d.range, tags: d.tags, desc: d.desc ?? d.description, icon: iconRoute('skill', d) };
    case 'abstates': return { id: d.id, name: d.name ?? `state ${d.id}`, debuff: d.debuff ?? /DEBUFF/i.test(String(d.sort ?? '')), desc: d.desc ?? d.description, icon: iconRoute('abstate', d) };
    case 'mobs': return { id: d.id, name: d.name ?? `mob ${d.id}`, level: d.level, grade: d.grade, maxHp: d.maxHp, npc: d.npc };
    case 'maps': {
      const cover = d.cover ?? (d.world ? { x0: 0, y0: 0, x1: d.world.w, y1: d.world.h } : null);
      const image = typeof d.image === 'string' ? d.image : d.minimap ? `/api/v3/static/maps/${d.id}/minimap.png` : null;
      return { id: d.id, short: d.short ?? d.mapName, name: d.name ?? d.mapName ?? `map ${d.id}`, image, cover };
    }
    default: return d;
  }
}
/** `icon` is a URL (string) or "the client ships art" (true) -> the icon route. */
function iconRoute(kind: string, d: any): string | null {
  if (typeof d.icon === 'string') return d.icon;
  return d.icon ? `/api/v3/icons/${kind}/${d.id}.png` : null;
}

function get(url: string) {
  const tok = getToken();
  return fetch(url, { headers: tok ? { Authorization: `Bearer ${tok}` } : {} }).then(r => (r.ok ? r.json() : null));
}

function flush() {
  scheduled = false;
  const keys = [...queue]; queue.clear();
  const byKind = new Map<StaticKind, string[]>();
  for (const key of keys) { const [k, id] = key.split(':'); byKind.set(k as StaticKind, [...(byKind.get(k as StaticKind) ?? []), id]); }
  for (const [kind, ids] of byKind) {
    const single = (id: string) => get(`/api/v3/static/${kind}/${id}`)
      .then(d => { cache.set(`${kind}:${id}`, d ? { state: 'ok', data: norm(kind, d) } : { state: 'missing' }); notify(); })
      .catch(() => { cache.set(`${kind}:${id}`, { state: 'missing' }); notify(); });
    if (BATCH.includes(kind) && ids.length > 1) {
      for (let i = 0; i < ids.length; i += 100) {
        const chunk = ids.slice(i, i + 100).sort((a, b) => Number(a) - Number(b));
        get(`/api/v3/static/${kind}?ids=${chunk.join(',')}`)
          .then(m => {
            if (!m) { chunk.forEach(single); return; } // host without the batch route: one by one
            for (const id of chunk) cache.set(`${kind}:${id}`, m[id] ? { state: 'ok', data: norm(kind, m[id]) } : { state: 'missing' });
            notify();
          })
          .catch(() => chunk.forEach(single));
      }
    } else ids.forEach(single);
  }
}

export function lookup<T>(kind: StaticKind, id: number | null | undefined): T | undefined {
  if (id == null || id < 0) return undefined;
  const key = `${kind}:${id}`;
  const e = cache.get(key);
  if (e) return e.data as T | undefined;
  cache.set(key, { state: 'loading' });
  queue.add(key);
  if (!scheduled) { scheduled = true; queueMicrotask(flush); }
  return undefined;
}

/** Re-render when any static entry arrives (cheap: entries arrive a handful of times per page load). */
export function useStaticVersion() {
  return useSyncExternalStore(l => { listeners.add(l); return () => { listeners.delete(l); }; }, () => version);
}

export function useStatic<T>(kind: StaticKind, id: number | null | undefined): T | undefined {
  useStaticVersion();
  return lookup<T>(kind, id);
}

/** Absolute icon URL with the token when the host requires one (an <img> cannot send headers). */
export function iconUrl(u: string | null | undefined): string | undefined {
  if (!u) return undefined;
  const tok = getToken();
  return tok ? `${u}${u.includes('?') ? '&' : '?'}token=${encodeURIComponent(tok)}` : u;
}

const images = new Map<string, HTMLImageElement | null>();
/** A decoded image for canvas drawing (map art); null while loading or when absent. */
export function image(url: string | null | undefined): HTMLImageElement | null {
  if (!url) return null;
  if (images.has(url)) { const im = images.get(url)!; return im && im.complete && im.naturalWidth ? im : null; }
  const im = new Image();
  im.onload = () => notify();
  im.onerror = () => images.set(url, null);
  im.src = iconUrl(url)!;
  images.set(url, im);
  return null;
}
