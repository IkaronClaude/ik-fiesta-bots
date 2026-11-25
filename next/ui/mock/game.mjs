// The mock's game view (API_SPEC 13.9): a per-bot GameState built from the toy world of sim.mjs plus skills with
// cooldowns, buffs with timers, a paged bag, equipment, soul stones and stats. Published on the `game` topic as one
// full snapshot then RFC 6902 deltas (maps keyed by id / slot / handle so a change touches one small path).
import { MAP } from './static.mjs';

const SKILL_IDS = [3001, 3003, 3002, 3004, 3005, 3006, 3007, 3008, 21001, 21002, 21003, 3012]; // learn order
const CD = id => [1500, 3000, 6000, 12000, 30000][(id % 1000) % 5];

export function initGame(sim) {
  const r = sim.r;
  const bag = {};
  for (let s = 0; s < 30; s++) if (r() < 0.6) bag[s] = { itemId: 500 + Math.floor(r() * 60), lot: 1 + Math.floor(r() * 20) };
  sim.game = {
    skills: Object.fromEntries(SKILL_IDS.map(id => [id, { id, cdEnd: 0, cdMs: CD(id) }])),
    buffs: {},
    bag: { size: 48, pageSize: 24, slots: bag },
    equip: { 0: { itemId: 501, upgrade: 7 }, 1: { itemId: 502, upgrade: 3 }, 2: { itemId: 508, upgrade: 0 }, 5: { itemId: 514, upgrade: 5 } },
    stones: { hp: { count: 18, max: 30, cdEnd: 0, cdMs: 4000 }, sp: { count: 9, max: 30, cdEnd: 0, cdMs: 6000 } },
    stats: { str: 82, end: 64, dex: 41, int: 12, spr: 30, dmgMin: 210, dmgMax: 268, mDmgMin: 40, mDmgMax: 52, def: 190, mDef: 88, aim: 140, evasion: 96, crit: 4.2 },
    freeStatPoints: 2,
    players: [0, 1, 2].map(i => ({ h: 20000 + i, k: 'player', name: ['Wanderer', 'Lumi', 'Kestrel'][i], x: sim.world.x + 300 * (i - 1), y: sim.world.y + 400, f: 0, lvl: 25 + i * 5, hpPct: 100 })),
    gate: { h: 30000, k: 'gate', id: 9010, x: 6400, y: 6900, name: 'gate to Mock Town' },
    target: -1,
  };
  for (let i = 0; i < 3; i++) addBuff(sim, 700 + Math.floor(r() * 12));
}

function addBuff(sim, id) {
  const dur = id % 4 === 0 ? null : 8000 + Math.floor(sim.r() * 50000); // null = permanent
  sim.game.buffs[id] = { id, level: 1 + (id % 3), endAt: dur ? sim.now + dur : null };
}

export function stepGame(sim) {
  const g = sim.game, w = sim.world, r = sim.r, now = sim.now;
  // keep the bot on the mock map
  const m = MAP.cover;
  w.x = Math.max(m.x0 + 600, Math.min(m.x1 - 600, w.x)); w.y = Math.max(m.y0 + 600, Math.min(m.y1 - 600, w.y));
  if (w.dest) { w.dest[0] = Math.max(m.x0 + 600, Math.min(m.x1 - 600, w.dest[0])); w.dest[1] = Math.max(m.y0 + 600, Math.min(m.y1 - 600, w.dest[1])); }
  // skills: fight = cast what is ready; also the odd cast out of combat
  const fighting = w.ents.some(e => e.aggro);
  if (r() < (fighting ? 0.08 : 0.01)) {
    const ready = Object.values(g.skills).filter(s => s.cdEnd <= now);
    const s = ready[Math.floor(r() * ready.length)];
    if (s) { g.skills[s.id] = { ...s, cdEnd: now + s.cdMs }; w.sp = Math.max(0, w.sp - 20); }
  }
  if (w.hp < w.maxHp * 0.6 && g.stones.hp.cdEnd <= now && g.stones.hp.count > 0) g.stones.hp = { ...g.stones.hp, count: g.stones.hp.count - 1, cdEnd: now + g.stones.hp.cdMs };
  if (w.sp < w.maxSp * 0.4 && g.stones.sp.cdEnd <= now && g.stones.sp.count > 0) g.stones.sp = { ...g.stones.sp, count: g.stones.sp.count - 1, cdEnd: now + g.stones.sp.cdMs };
  // buffs expire and come back
  for (const b of Object.values(g.buffs)) if (b.endAt && b.endAt <= now) delete g.buffs[b.id];
  if (Object.keys(g.buffs).length < 4 && r() < 0.01) addBuff(sim, 700 + Math.floor(r() * 12));
  // bag churn: loot and use
  if (r() < 0.005) { const free = [...Array(g.bag.size).keys()].find(s => !g.bag.slots[s]); if (free != null) g.bag.slots = { ...g.bag.slots, [free]: { itemId: 500 + Math.floor(r() * 60), lot: 1 } }; }
  if (r() < 0.004) { const used = Object.keys(g.bag.slots); const s = used[Math.floor(r() * used.length)]; if (s) { const it = g.bag.slots[s]; g.bag.slots = { ...g.bag.slots }; if (it.lot > 1) g.bag.slots[s] = { ...it, lot: it.lot - 1 }; else delete g.bag.slots[s]; } }
  // other players wander
  for (const p of g.players) if (r() < 0.05) { const a = r() * Math.PI * 2; p.x += Math.cos(a) * 60; p.y += Math.sin(a) * 60; p.f = Math.round(a * 180 / Math.PI); }
  if (Math.hypot(g.players[0].x - w.x, g.players[0].y - w.y) > 1500) g.players.forEach((p, i) => { p.x = w.x + 300 * (i - 1); p.y = w.y + 400; });
  g.target = w.ents.find(e => e.aggro)?.h ?? -1;
  w.exp = Math.min(w.expNext - 1, w.exp);
}

const deg = (dx, dy) => Math.round((Math.atan2(dy, dx) * 180) / Math.PI);

/** The wire snapshot of API_SPEC 14.1 (GameSnapshot). */
export function snapshot(sim) {
  const m = model(sim);
  return {
    self: m.self, stats: m.stats,
    abstates: Object.values(m.ab).map(b => [b.id, b.endsAt]),
    skills: Object.values(m.sk).map(k => [k.id, k.readyAt, k.durMs]),
    bag: { size: m.bagSize, pages: m.pages, cells: Object.entries(m.bag).map(([slot, c]) => [Number(slot), c.itemId, c.lot]) },
    equip: Object.entries(m.eq).map(([slot, id]) => [Number(slot), id]),
    target: m.target, path: m.path,
    ents: Object.entries(m.ents).map(([h, e]) => [Number(h), e.k, e.id, e.x, e.y, e.hp, e.flags, e.name]),
  };
}

/** Internal keyed model, used to diff successive publishes into ops. */
export function model(sim) {
  const g = sim.game, w = sim.world;
  const ents = {};
  for (const e of w.ents) {
    const npc = e.k === 'npc';
    const flags = (e.hpPct <= 0 ? 1 : 0) | (e.aggro ? 2 : 0) | (g.target === e.h ? 4 : 0) | (npc ? 0 : 8);
    ents[e.h] = { k: npc ? 'n' : 'm', id: npc ? 105 + (e.h % 3) : e.mobId, x: Math.round(e.x), y: Math.round(e.y), hp: Math.round(e.hpPct), flags, name: null };
  }
  for (const p of g.players) ents[p.h] = { k: 'p', id: 11, x: Math.round(p.x), y: Math.round(p.y), hp: p.hpPct, flags: 0, name: p.name };
  ents[g.gate.h] = { k: 'g', id: 110, x: g.gate.x, y: g.gate.y, hp: 100, flags: 0, name: 'Mock Town' };
  return {
    self: { handle: 1, name: sim.rec.character || sim.id, classId: sim.rec.classFamily ?? 1, level: sim.rec.level ?? 1, exp: w.exp, prevExp: 0, nextExp: w.expNext,
      hp: Math.round(w.hp), maxHp: w.maxHp, sp: Math.round(w.sp), maxSp: w.maxSp, money: w.money, fame: 0, statPoints: g.freeStatPoints,
      hpStones: g.stones.hp.count, spStones: g.stones.sp.count, dead: false, battle: w.ents.some(e => e.aggro), mounted: false,
      x: Math.round(w.x), y: Math.round(w.y), dir: deg(w.facing[0], w.facing[1]), mapId: MAP.id },
    stats: { str: g.stats.str, end: g.stats.end, dex: g.stats.dex, int: g.stats.int, spr: g.stats.spr, wcMin: g.stats.dmgMin, wcMax: g.stats.dmgMax,
      def: g.stats.def, aim: g.stats.aim, eva: g.stats.evasion, maMin: g.stats.mDmgMin, maMax: g.stats.mDmgMax, mdef: g.stats.mDef, maim: 0, meva: 0 },
    ab: Object.fromEntries(Object.values(g.buffs).map(b => [b.id, { id: b.id, endsAt: b.endAt ?? 0 }])),
    sk: Object.fromEntries(Object.values(g.skills).map(k => [k.id, { id: k.id, readyAt: k.cdEnd > sim.now ? k.cdEnd : 0, durMs: k.cdEnd > sim.now ? k.cdMs : 0 }])),
    bag: g.bag.slots, bagSize: g.bag.size, pages: Math.ceil(g.bag.size / g.bag.pageSize),
    eq: Object.fromEntries(Object.entries(g.equip).map(([k, e]) => [k, e.itemId])),
    target: g.target,
    path: w.dest ? [[Math.round(w.dest[0]), Math.round(w.dest[1])]] : [],
    ents,
  };
}

const changed = (a, b) => JSON.stringify(a) !== JSON.stringify(b);

/** Ops (API_SPEC 14.1 delta ops) from model a to model b. Entity moves under 4 units are not sent. */
export function diffOps(a, b) {
  const ops = [];
  const fields = (x, y) => { const o = {}; for (const k of Object.keys(y)) if (changed(x[k], y[k])) o[k] = y[k]; return o; };
  const fs = fields(a.self, b.self); if (Object.keys(fs).length) ops.push(['self', fs]);
  const st = fields(a.stats, b.stats); if (Object.keys(st).length) ops.push(['stats', st]);
  for (const id of Object.keys(b.ab)) if (!a.ab[id] || a.ab[id].endsAt !== b.ab[id].endsAt) ops.push(['ab', Number(id), b.ab[id].endsAt]);
  for (const id of Object.keys(a.ab)) if (!b.ab[id]) ops.push(['ab-', Number(id)]);
  for (const id of Object.keys(b.sk)) if (!a.sk[id] || a.sk[id].readyAt !== b.sk[id].readyAt) ops.push(['sk', Number(id), b.sk[id].readyAt, b.sk[id].durMs]);
  for (const id of Object.keys(a.sk)) if (!b.sk[id]) ops.push(['sk-', Number(id)]);
  if (a.bagSize !== b.bagSize) ops.push(['bagSize', b.bagSize, b.pages]);
  for (const slot of new Set([...Object.keys(a.bag), ...Object.keys(b.bag)])) {
    const x = a.bag[slot], y = b.bag[slot];
    if (changed(x, y)) ops.push(['bag', Number(slot), y ? y.itemId : -1, y ? y.lot : 0]);
  }
  for (const slot of new Set([...Object.keys(a.eq), ...Object.keys(b.eq)])) if (a.eq[slot] !== b.eq[slot]) ops.push(['eq', Number(slot), b.eq[slot] ?? -1]);
  if (a.target !== b.target) ops.push(['tgt', b.target]);
  if (changed(a.path, b.path)) ops.push(['path', b.path]);
  for (const h of Object.keys(b.ents)) {
    const x = a.ents[h], y = b.ents[h];
    if (!x || x.k !== y.k || x.id !== y.id) { ops.push(['e+', Number(h), y.k, y.id, y.x, y.y, y.hp, y.flags, y.name]); continue; }
    if (Math.hypot(x.x - y.x, x.y - y.y) >= 4) ops.push(['ep', Number(h), y.x, y.y]); else { y.x = x.x; y.y = x.y; } // keep the last SENT position
    if (x.hp !== y.hp) ops.push(['eh', Number(h), y.hp]);
    if (x.flags !== y.flags) ops.push(['ef', Number(h), y.flags]);
  }
  for (const h of Object.keys(a.ents)) if (!b.ents[h]) ops.push(['e-', Number(h)]);
  return ops;
}
