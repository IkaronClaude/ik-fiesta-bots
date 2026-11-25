// Static game data for the mock's game view: names, descriptions and icons by id, served from cacheable GET
// endpoints (API_SPEC 13.9). Everything here is synthetic test data; icons are generated SVGs.

const hue = id => (id * 47) % 360;
const GRADES = ['normal', 'magic', 'rare', 'unique', 'legendary'];

export const MAP = { id: 0, short: 'MockField', name: 'Mock Field', size: 12800, cover: { x0: 0, y0: 0, x1: 12800, y1: 12800 } };

// Shapes follow API_SPEC 14.2 (the host's field names).
export function item(id) {
  if (id < 500 || id > 560) return null;
  const kinds = ['potion', 'weapon', 'armor', 'material', 'quest', 'scroll'];
  const type = kinds[id % kinds.length];
  return { id, inx: `MockItem${id}`, name: `${type} item #${id}`, type, class: id % 7, equip: type === 'weapon' || type === 'armor' ? id % 12 : -1,
    demandLv: (id % 30) + 1, maxLot: type === 'weapon' || type === 'armor' ? 1 : 99, buyPrice: 40 * id, sellPrice: 10 * id, icon: id % 13 !== 0 };
}

// Mock skill ids: 3001-3006 active (3001 / 3002 one family at ranks 1 and 2), 3007-3008 passive, 3012 "other"
// (in no skill table), 21001-21003 production. `category` and `rowKey` are the UI request U26 fields.
export function skill(id) {
  const prod = id >= 21001 && id <= 21003;
  if (!prod && (id < 3001 || id > 3012)) return null;
  const n = prod ? id - 21000 + 20 : id - 3000;
  const category = prod ? 'production' : id <= 3006 ? 'active' : id <= 3008 ? 'passive' : id === 3012 ? 'other' : 'active';
  const family = id === 3002 ? 3001 : id;
  const step = id === 3002 ? 2 : id === 3001 ? 1 : 1 + (n % 5);
  const name = prod ? `recipe #${id}` : `skill #${family}`;
  return { id, inx: `MockSkill${n}`, name: `${name} [${String(step).padStart(2, '0')}]`, category, rowKey: name, step, maxStep: 5, sp: 8 + n * 3, hp: 0,
    cooldownMs: [1500, 3000, 6000, 12000, 30000][n % 5], groupCooldownMs: 0, range: 80 + n * 20, castMs: n % 3 === 0 ? 1000 : 0,
    description: `Synthetic ${category} skill ${n}.`, icon: id !== 3012 };
}

export function abstate(id) {
  if (id < 700 || id > 712) return null;
  return { id, inx: `MockState${id}`, name: `${id % 3 === 0 ? 'debuff' : 'buff'} #${id}`, description: `Synthetic state ${id}.`, sort: id % 3 === 0 ? 'DEBUFF' : 'BUFF', icon: true };
}

export function mob(id) {
  if (id < 100 || id > 110) return null;
  const npc = id >= 105; // 105..109 NPCs, 110 the gate in this mock (NPCs and gates are MobInfo rows too)
  return { id, inx: `MockMob${id}`, name: id === 110 ? 'gate' : npc ? `npc #${id}` : `mob #${id}`, level: 20 + (id % 12), maxHp: 900 + id, npc, playerSide: npc };
}

export function npc(id) {
  if (id < 9000 || id > 9010) return null;
  return { id, name: `npc #${id}`, role: ['SHOP', 'SOULSTONE', 'QUEST', 'STORAGE'][id % 4] };
}

export function iconSvg(kind, id) {
  const h = hue(id + (kind === 'skill' ? 120 : kind === 'abstate' ? 240 : 0));
  const label = String(id).slice(-3);
  const shape = kind === 'skill'
    ? `<circle cx="16" cy="16" r="10" fill="hsl(${h},70%,60%)"/><path d="M16 6 L19 16 L16 26 L13 16Z" fill="hsl(${h},80%,85%)"/>`
    : kind === 'abstate'
      ? `<path d="M16 4 L28 16 L16 28 L4 16Z" fill="hsl(${h},65%,55%)"/>`
      : `<rect x="6" y="6" width="20" height="20" rx="4" fill="hsl(${h},55%,50%)"/><rect x="10" y="10" width="12" height="12" rx="2" fill="hsl(${h},60%,72%)"/>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">`
    + `<rect width="32" height="32" rx="3" fill="hsl(${h},35%,18%)"/>${shape}`
    + `<text x="30" y="30" font-size="8" text-anchor="end" fill="#fff" font-family="monospace">${label}</text></svg>`;
}

/** A synthetic "map art" image covering MAP.cover (y up in world = top of image is high y). */
export function mapSvg() {
  const parts = [];
  let s = 12345;
  const r = () => { s = (s * 1103515245 + 12345) >>> 0; return s / 4294967296; };
  for (let i = 0; i < 40; i++) {
    const x = r() * 512, y = r() * 512, rad = 20 + r() * 70;
    parts.push(`<circle cx="${x.toFixed(0)}" cy="${y.toFixed(0)}" r="${rad.toFixed(0)}" fill="hsl(${90 + r() * 60},${25 + r() * 20}%,${18 + r() * 10}%)" opacity=".8"/>`);
  }
  parts.push('<path d="M0 260 C120 220 200 320 300 280 S440 200 512 240" stroke="#3a5f7a" stroke-width="14" fill="none" opacity=".8"/>');
  parts.push('<path d="M250 0 L270 512" stroke="#6b5a3a" stroke-width="6" fill="none" opacity=".7"/>');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512"><rect width="512" height="512" fill="#24331f"/>${parts.join('')}</svg>`;
}
