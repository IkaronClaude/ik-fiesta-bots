// Wire types of docs/API_SPEC.md sections 7, 8, 12 (+ the "UI requests" section). camelCase JSON.

export type Wire = string; // '2016' | '2026', list comes from GET /host

export interface HostInfo {
  name: string;
  mock?: boolean;
  apiVersion: number;
  wires: Wire[];
  defaultWire: Wire;
  defaultLogin?: { host: string; loginPort: number };
  classes?: { id: number; name: string }[];
  channels?: string[];
}

export interface Vitals {
  hp: number; maxHp: number; sp: number; maxSp: number; exp: number; expPrev?: number; expNext: number; money: number;
  bagFree: number; bagSize: number; x: number; y: number;
}

export interface BotSummary {
  id: string;
  state: string;
  character?: string;
  username?: string;
  host?: string;
  loginPort?: number;
  wire?: Wire;
  classFamily?: number;
  level: number;
  map: string | null;
  brain?: string | null;
  channel?: string | null;
  versionHash: string | null;
  layerStates: Record<string, string>;
  layerSince?: Record<string, string | null>;
  lastTransition?: { at: string; layer: string; from: string; to: string; reason: string } | null;
  tickP99Us: number;
  ticksPerSec?: number;
  expPerHour: number;
  deathsPerHour: number;
  lastError: string | null;
  health: string | null;
  paused?: boolean;
  autostart?: boolean;
  packetLog?: boolean;
  vitals?: Vitals;
  errors?: number;
}

export interface BotDetail extends BotSummary {
  record?: Record<string, unknown>;
  logLevels?: Record<string, { level: string; expiresAt: string | null }>;
  packetCapture?: PacketCapture;
}

export interface SpawnRequest {
  id: string; host: string; loginPort: number; username: string; password: string; character: string;
  create?: boolean; class?: number; brain?: string; channel?: string; autostart: boolean; wire?: Wire;
}

export interface ConditionTrace {
  state?: string; depth?: number; index?: number;
  to: string; kind: string; expr: string; value: number | null; result: boolean | null; holdMs: number; holdTarget?: number;
}

export interface LayerTrace {
  name: string; priority: number; path: string[]; enteredAt: string | null; enteredAtPath?: string[];
  timeInStateMs: number; progress: number | null; progressBestAt: string | null; budgetMs?: number | null;
  claims: string[]; conditions: ConditionTrace[];
}

export interface TransitionRecord {
  tick: number; at: string; layer: string; from: string; to: string; kind: string; reason: string;
  values: Record<string, unknown>; depth?: number; index?: number; leafFrom?: string;
}

export interface ActionTrace {
  id: number; kind: string; state: string; code: number; reason: string; sentAt: string; resolvedAt: string | null; layer: string;
}

export interface DebugFrame {
  bot: string; tick: number; t: string; version: string; paused?: boolean;
  layers: LayerTrace[];
  lastTransitions: TransitionRecord[];
  blackboard: { bot: Record<string, unknown>; layer: Record<string, Record<string, unknown>>; state: Record<string, Record<string, unknown>> };
  watches: Record<string, number>;
  actions: ActionTrace[];
  procedures: { kind: string; step: string; since: string }[];
  tickStats: { p50us: number; p99us: number; allocP95B: number; steps: Record<string, number> };
  errors: { tick: number; layer: string; state: string; error: string }[];
}

export interface SensorEntity {
  h: number; k: string; mobId?: number; name?: string; x: number; y: number; hpPct?: number; aggro?: boolean;
  detect?: number | null; facing?: [number, number] | null; leashOrigin?: [number, number] | null; leashLeft?: number | null;
}

export interface SensorFrame {
  bot: string; map: string; mapId: number; self: { x: number; y: number; facing: [number, number] };
  entities: SensorEntity[];
  reach: { bash: number; skills: { id: number; reach: number }[] };
  path: [number, number][];
  threat?: { cellSize: number; origin: [number, number]; w: number; h: number; values: string };
}

export interface TickSample {
  tick: number; totalUs: number; decodeUs: number; applyUs: number; actionsUs: number; moveUs: number; combatUs: number;
  brainUs: number; flushUs: number; allocBytes: number; events: number; intents: number; luaInstructions: number;
}

export interface LogLine { t: string; level: string; category?: string; text: string; repeat?: number }

export interface PacketCapture {
  enabled: boolean; opcodes?: number[]; depts?: number[]; minutes?: number; maxMb?: number; format?: string;
  files?: { name: string; sizeBytes: number; startedAt: string }[]; until?: string | null;
}

export interface LintResult {
  severity: string; code: string; message: string; module: string | null; line: number; column?: number;
  layer: string | null; state: string | null;
}

export interface BrainListItem { name: string; latest: string; channels: Record<string, string>; description?: string; versions?: number }

export interface BrainVersionInfo {
  hash: string; brain: string; api: number; created: string; author: string; message: string; modules: string[];
  libraries?: Record<string, string>; warnings?: number;
}

export interface BrainVersionFull extends BrainVersionInfo {
  definition: string;
  modules: any; // spec: module sources by path (object); info lists use string[]
  libraries?: any;
  lint?: LintResult[];
}

export interface BrainDiff {
  from: string; to: string; definitionDiff: string; modules: { path: string; unifiedDiff: string; status?: string }[];
}

export interface AssignmentRecord {
  bot: string; at: string; fromHash: string | null; toHash: string; brain?: string; by: string; reason: string; keptState: boolean;
}

export interface LibraryItem { path: string; latest: string; usedBy: string[]; readOnly?: boolean; versions?: number }
export interface LibraryVersion { hash: string; path?: string; created: string; author: string; message: string; source?: string }

export interface ManifestFunction {
  module: string; name: string; signature: string; params?: string[]; returns: string; cost: string; alloc: string; doc: string;
  resource?: string | null; aliasOf?: string;
}

export interface ApiManifest {
  apiVersion: number;
  functions: ManifestFunction[];
  constants: Record<string, Record<string, number>>;
  events?: { name: string; arg1?: string; arg2?: string; doc?: string }[];
  sensors?: { name: string; returns?: string; doc?: string }[];
  resources?: string[];
  procedures?: string[];
  jobs?: string[];
  actionKinds?: string[];
}

export interface VersionMetrics {
  hash: string;
  perClass: { classFamily: number; bots: number; hours: number; expPerHour: number; deathsPerHour: number; questsPerHour: number;
    stuckExitsPerHour: number; tickP99Us: number; stateShare: Record<string, number> }[];
}

export interface Recording { id: string; kind?: string; startedAt: string; endedAt: string; frames?: number; ticks?: number; sizeBytes?: number }
