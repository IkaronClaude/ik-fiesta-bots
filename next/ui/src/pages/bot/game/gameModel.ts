// Reassembles the `game` topic (API_SPEC section 14: one snapshot, then small op lists) into the view model the
// Game tab renders. Updates are immutable with structural sharing: an op copies only the map it touches, so a
// memoised bag slot / skill tile / entity re-renders only when its own record changed.
import type { GameEnt, GameState } from './types';

type Row = [number, string, number, number, number, number, number, string | null, number?, number?, number?];

const KIND: Record<string, GameEnt['k']> = { m: 'mob', n: 'npc', p: 'player', g: 'gate', d: 'drop' };

function ent(r: Row | number[], at?: number): GameEnt {
  const [, k, id, x, y, hp, flags, name, tx, ty, spd] = r as Row;
  return { k: KIND[k] ?? k, id, x, y, hpPct: hp, flags, dead: !!(flags & 1), aggro: !!(flags & 2), ourTarget: !!(flags & 4), huntable: !!(flags & 8), name: name ?? null,
    tx: tx ?? x, ty: ty ?? y, spd: spd ?? 0, at };
}

function selfOf(s: any, prev?: GameState['self']): GameState['self'] {
  const o: any = { ...(prev ?? {}) };
  for (const [k, v] of Object.entries(s ?? {})) {
    switch (k) {
      case 'prevExp': o.expPrev = v; break;
      case 'nextExp': o.expNext = v; break;
      case 'dir': o.f = v; break;
      case 'battle': o.inCombat = v; break;
      case 'statPoints': o.freeStatPoints = v; break;
      default: o[k] = v;
    }
  }
  return o;
}

export function fromSnapshot(d: any, now: number): GameState {
  const ents: Record<string, GameEnt> = {};
  for (const r of d.ents ?? []) ents[String(r[0])] = ent(r, now);
  const slots: GameState['bag']['slots'] = {};
  for (const [slot, itemId, lot] of d.bag?.cells ?? []) if (itemId >= 0) slots[slot] = { itemId, lot };
  const size = d.bag?.size ?? 0, pages = d.bag?.pages || 1;
  return {
    v: 1, serverTime: now, offline: false,
    self: { ...selfOf(d.self), target: d.target ?? -1, stats: d.stats ?? undefined, at: now },
    path: d.path ?? [],
    skills: Object.fromEntries((d.skills ?? []).map(([id, readyAt, durMs]: number[]) => [id, { id, cdEnd: readyAt, cdMs: durMs }])),
    skillOrder: (d.skills ?? []).map((r: number[]) => r[0]),
    buffs: Object.fromEntries((d.abstates ?? []).map(([id, endsAt]: number[]) => [id, { id, endAt: endsAt || null }])),
    bag: { size, pageSize: Math.ceil(size / pages) || 24, slots },
    equip: Object.fromEntries((d.equip ?? []).map(([slot, itemId]: number[]) => [slot, { itemId }])),
    ents,
  };
}

/** Apply one delta message's ops. Returns the same object when nothing changed. */
export function applyOps(s: GameState, ops: any[], now: number): GameState {
  if (!ops.length) return s.serverTime === now ? s : { ...s, serverTime: now };
  let skillOrder = s.skillOrder;
  let self = s.self, skills = s.skills, buffs = s.buffs, bag = s.bag, equip = s.equip, ents = s.ents, path = s.path;
  const own = { skills: false, buffs: false, slots: false, equip: false, ents: false };
  const mut = <K extends keyof typeof own>(k: K) => {
    if (own[k]) return;
    own[k] = true;
    if (k === 'skills') skills = { ...skills };
    else if (k === 'buffs') buffs = { ...buffs };
    else if (k === 'slots') bag = { ...bag, slots: { ...bag.slots } };
    else if (k === 'equip') equip = { ...equip };
    else ents = { ...ents };
  };
  for (const op of ops) {
    switch (op[0]) {
      case 'self': self = { ...selfOf(op[1], self) }; if ('x' in op[1]) self.at = now; break;
      case 'stats': self = { ...self, stats: { ...(self.stats ?? {}), ...op[1] } }; break;
      case 'ab': mut('buffs'); buffs[op[1]] = { id: op[1], endAt: op[2] || null }; break;
      case 'ab-': mut('buffs'); delete buffs[op[1]]; break;
      case 'sk': mut('skills'); if (!skills[op[1]]) skillOrder = [...skillOrder, op[1]]; skills[op[1]] = { id: op[1], cdEnd: op[2], cdMs: op[3] }; break;
      case 'sk-': mut('skills'); delete skills[op[1]]; skillOrder = skillOrder.filter(x => x !== op[1]); break;
      case 'bag': mut('slots'); if (op[2] >= 0) bag.slots[op[1]] = { itemId: op[2], lot: op[3] }; else delete bag.slots[op[1]]; break;
      case 'bagSize': bag = { ...bag, size: op[1], pageSize: Math.ceil(op[1] / (op[2] || 1)) || 24 }; break;
      case 'eq': mut('equip'); if (op[2] >= 0) equip[op[1]] = { itemId: op[2] }; else delete equip[op[1]]; break;
      case 'tgt': self = { ...self, target: op[1] }; break;
      case 'path': path = op[1] ?? []; break;
      case 'map': self = { ...self, mapId: op[1] }; ents = {}; own.ents = true; path = []; break;
      case 'e+': mut('ents'); ents[String(op[1])] = ent(op.slice(1), now); break;
      case 'e-': mut('ents'); delete ents[String(op[1])]; break;
      case 'ep': { mut('ents'); const e = ents[String(op[1])]; if (e) ents[String(op[1])] = { ...e, x: op[2], y: op[3], tx: op[4] ?? op[2], ty: op[5] ?? op[3], spd: op[6] ?? 0, at: now }; break; }
      case 'eh': { mut('ents'); const e = ents[String(op[1])]; if (e) ents[String(op[1])] = { ...e, hpPct: op[2] }; break; }
      case 'ef': { mut('ents'); const e = ents[String(op[1])]; if (e) ents[String(op[1])] = { ...e, ...ent([0, '', 0, 0, 0, 0, op[2], null]), k: e.k, id: e.id, x: e.x, y: e.y, hpPct: e.hpPct, name: e.name }; break; }
      default: break; // unknown op: ignore (forward compatible)
    }
  }
  return { ...s, serverTime: now, self, skills, skillOrder, buffs, bag, equip, ents, path };
}
