// GameState of the `game` WebSocket topic (API_SPEC 13.9). Maps are keyed by id / slot / handle so a delta
// touches one small path.
export interface GameSelf {
  handle: number; name: string; classId?: number; classFamily?: number; className?: string; level: number;
  exp: number; expNext: number; expPrev?: number;
  hp: number; maxHp: number; sp: number; maxSp: number; lp?: number | null; maxLp?: number | null;
  money: number; mapId: number; map?: string; x: number; y: number; f: number;
  moving?: boolean; mounted?: boolean; dead?: boolean; inCombat?: boolean; runSpeed?: number; target: number;
  stats?: Record<string, number>; freeStatPoints?: number; fame?: number; hpStones?: number; spStones?: number;
}
export interface GameEnt {
  k: 'mob' | 'npc' | 'player' | 'gate' | 'drop' | string; id?: number | null; name?: string | null;
  x: number; y: number; f?: number; hpPct?: number | null; lvl?: number | null; aggro?: boolean; tgt?: number | null; dead?: boolean;
  flags?: number; ourTarget?: boolean; huntable?: boolean;
}
export interface GameSkill { id: number; cdEnd: number; cdMs: number }
export interface GameStone { count: number; max?: number; cdEnd: number; cdMs: number }
export interface GameBuff { id: number; level?: number; endAt: number | null }
export interface GameState {
  v: number;
  serverTime: number;
  offline?: boolean;
  offReason?: string;
  self: GameSelf;
  path: [number, number][];
  skills: Record<string, GameSkill>;
  /** skill ids in learn order (snapshot order, new ones appended) */
  skillOrder: number[];
  stones?: { hp?: GameStone; sp?: GameStone };
  buffs: Record<string, GameBuff>;
  bag: { size: number; pageSize?: number; slots: Record<string, { itemId: number; lot: number }> };
  equip: Record<string, { itemId: number; upgrade?: number }>;
  ents: Record<string, GameEnt>;
}
