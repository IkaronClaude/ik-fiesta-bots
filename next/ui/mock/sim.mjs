// A fake brain runtime for the mock host: runs any brain definition's layers / machines / transitions with the
// semantics of API_SPEC 5.3-5.4 over a toy world, so the debugger views (live graph, condition panel, trace,
// blackboard, watches, actions, sensors, tick stats) show realistic, definition-driven data.
// Nothing here is game data: the world is random numbers.

const OPS = { '<': (a, b) => a < b, '<=': (a, b) => a <= b, '>': (a, b) => a > b, '>=': (a, b) => a >= b,
  '==': (a, b) => a == b, '~=': (a, b) => a != b };

const REFUSALS = [[4042, 'out of range'], [4031, 'cooldown'], [4033, 'not enough SP'], [1001, 'preempted by survival'],
  [4051, 'target is dead']];

function rnd(seed) { let s = seed >>> 0; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; }

export class BotSim {
  constructor(rec, def, version, log) {
    this.rec = rec; this.id = rec.id; this.log = log;
    this.r = rnd([...rec.id].reduce((a, c) => a * 31 + c.charCodeAt(0), 7));
    this.tick = 1000 + Math.floor(this.r() * 100000);
    this.now = Date.now();
    this.paused = false; this.pendingSteps = 0;
    this.world = { hp: 900, maxHp: 1000, sp: 400, maxSp: 500, x: 5000 + this.r() * 2000, y: 5000 + this.r() * 2000,
      facing: [1, 0], bagFree: 24, bagSize: 36, money: 15000, exp: 1200, expNext: 50000, kills: 0, deaths: 0,
      dest: null, ents: [] };
    for (let i = 0; i < 12; i++) this.spawnEnt(i);
    this.transitions = []; this.actions = []; this.errors = []; this.ticks = []; this.watchHist = {};
    this.nextActionId = 9000 + Math.floor(this.r() * 500);
    this.procedures = [];
    this.firedEvents = new Set();
    this.load(def, version, false);
    this.startedAt = Date.now();
    this.expStart = this.world.exp;
  }

  spawnEnt(i) {
    const a = this.r() * Math.PI * 2, d = 200 + this.r() * 900;
    const ent = { h: 1000 + i + Math.floor(this.r() * 50) * 100, k: i % 6 === 5 ? 'npc' : 'mob', mobId: i % 6 === 5 ? -1 : 100 + (i % 4),
      x: this.world.x + Math.cos(a) * d, y: this.world.y + Math.sin(a) * d, hpPct: 100, aggro: false,
      detect: 180 + (i % 3) * 60, facing: [Math.cos(a + 2), Math.sin(a + 2)], leashOrigin: null, leashLeft: null };
    this.world.ents[i] = ent;
  }

  load(def, version, keepState) {
    const old = keepState ? this.layers : null;
    this.def = def; this.version = version;
    this.bb = keepState && this.bb ? { bot: this.bb.bot, layer: {}, state: {} } : { bot: { ...(def.blackboard || {}) }, layer: {}, state: {} };
    this.layers = (def.layers || []).filter(l => l.enabled !== false).sort((a, b) => b.priority - a.priority).map(l => ({
      def: l, name: l.name, priority: l.priority, rate: l.rate === 'tick' || l.rate == null ? 50 : l.rate, lastRun: 0,
      path: [], conditions: [], claims: [] }));
    for (const L of this.layers) {
      this.bb.layer[L.name] = {}; this.bb.state[L.name] = {};
      const prev = old?.find(o => o.name === L.name);
      const keepName = prev && prev.path[0] && L.def.machine?.states?.[prev.path[0].state] ? prev.path[0].state : null;
      this.enter(L, 0, L.def.machine, keepName || L.def.machine?.initial, keepName ? 'kept on hot reload' : 'initial');
    }
  }

  machineOf(state) {
    if (!state) return null;
    if (state.machine) return state.machine;
    if (state.use) {
      const name = state.use.replace(/\$\{([^}]+)\}/g, (_, e) => e === 'self.classFamily' ? String(this.rec.classFamily ?? 0) : '0');
      return this.def.machines?.[name] || this.def.machines?.[state.use] || null;
    }
    return null;
  }

  enter(L, depth, machine, stateName, why) {
    L.path.length = depth;
    if (!machine || !machine.states?.[stateName]) return;
    const st = machine.states[stateName];
    L.path.push({ machine, state: stateName, enteredAt: this.now, hold: {}, lastFired: {}, progressBest: null, progressBestAt: this.now });
    if (depth === 0) this.bb.state[L.name] = {};
    const sub = this.machineOf(st);
    if (sub) this.enter(L, depth + 1, sub, sub.initial, why);
    L.claims = [...new Set(L.path.flatMap(f => f.machine.states[f.state]?.claims || []))];
  }

  // ---- sensors / conditions ----
  sensor(name) {
    const w = this.world;
    switch (name) {
      case 'self.hpPct': return +(w.hp * 100 / w.maxHp).toFixed(1);
      case 'self.hp': return Math.round(w.hp);
      case 'self.maxHp': return w.maxHp;
      case 'self.spPct': return +(w.sp * 100 / w.maxSp).toFixed(1);
      case 'self.sp': return Math.round(w.sp);
      case 'self.bagFree': return w.bagFree;
      case 'self.money': return w.money;
      case 'self.level': return this.rec.level;
      case 'threat.onUs': return w.ents.filter(e => e.aggro).length;
      case 'threat.incomingDps': return w.ents.filter(e => e.aggro).length * 31.5;
      case 'combat.kills': return w.kills;
      case 'quests.slotsFree': return 12;
      default: {
        // a stable, slowly varying pseudo value per sensor name
        const k = [...name].reduce((a, c) => a + c.charCodeAt(0), 0);
        return +(50 + 40 * Math.sin(this.now / 7000 + k)).toFixed(1);
      }
    }
  }

  evalLua(expr) {
    const m = /^\s*([a-z]+\.[a-zA-Z]+)\(\)\s*(<=|>=|==|~=|<|>)\s*(-?[\d.]+)\s*$/.exec(expr);
    if (m) { const v = this.sensor(m[1]); return { result: OPS[m[2]](v, +m[3]), value: v }; }
    if (/outmatched/.test(expr)) return { result: this.world.hp < this.world.maxHp * 0.15 && this.world.ents.filter(e => e.aggro).length >= 3, value: null };
    if (/wantKill/.test(expr)) return { result: !!this.bb.bot.wantKill, value: null };
    return { result: this.r() < 0.004, value: null };
  }

  cond(c, frame, st) {
    const tIn = this.now - frame.enteredAt;
    if (c.all) { const parts = c.all.map(x => this.cond(x, frame, st)); return { kind: 'all', expr: parts.map(p => p.expr).join(' and '), result: parts.every(p => p.result), value: null }; }
    if (c.any) { const parts = c.any.map(x => this.cond(x, frame, st)); return { kind: 'any', expr: parts.map(p => p.expr).join(' or '), result: parts.some(p => p.result), value: null }; }
    if (c.not) { const p = this.cond(c.not, frame, st); return { kind: 'not', expr: 'not (' + p.expr + ')', result: !p.result, value: p.value }; }
    if (c.value != null) { const v = this.sensor(c.value); return { kind: 'value', expr: `${c.value} ${c.op} ${c.than}`, value: v, result: !!OPS[c.op]?.(v, Number(c.than)) }; }
    if (c.after != null) return { kind: 'after', expr: `after ${c.after} ms`, value: tIn, result: tIn >= c.after };
    if (c.every != null) return { kind: 'every', expr: `every ${c.every} ms`, value: tIn, result: tIn >= c.every };
    if (c.stuck) {
      const budget = st.budget ?? 60000;
      const since = this.now - frame.progressBestAt;
      return { kind: 'stuck', expr: `no progress for ${budget} ms`, value: since, result: since >= budget };
    }
    if (c.on === 'action') return { kind: 'action', expr: `action ${c.action} ${c.result}`, value: null, result: this.r() < 0.003 };
    if (c.on != null) return { kind: 'event', expr: `on ${c.on}`, value: null, result: this.firedEvents.has(c.on) };
    if (c.procedure != null) return { kind: 'procedure', expr: `procedure ${c.procedure} ${c.result ?? ''}`.trim(), value: null, result: tIn > 3000 && this.r() < 0.02 };
    if (c.job != null) return { kind: 'job', expr: `job ${c.job} = ${c.result ?? ''}`.trim(), value: null, result: tIn > 1500 && this.r() < 0.03 };
    if (c.when != null) { const r = this.evalLua(String(c.when)); return { kind: 'lua', expr: String(c.when), ...r }; }
    if (c.always) return { kind: 'always', expr: 'always', value: null, result: true };
    return { kind: 'none', expr: '(no condition)', value: null, result: false };
  }

  // ---- world ----
  stepWorld(dt) {
    const w = this.world;
    this.firedEvents.clear();
    const leafNames = this.layers.map(L => L.path.at(-1)?.state || '');
    const fighting = leafNames.some(n => /engage|swing|cast|approach|fight|hunt/.test(n));
    const healing = leafNames.some(n => /recover|heal/.test(n));
    const fleeing = leafNames.some(n => /flee/.test(n));
    if (!fighting && !fleeing && this.r() < 0.01) {
      const e = w.ents.find(x => x.k === 'mob' && !x.aggro);
      if (e) { e.aggro = true; e.leashOrigin = [e.x, e.y]; this.fire('AGGRO_GAINED', e.h); }
    }
    const aggro = w.ents.filter(e => e.aggro);
    for (const e of aggro) {
      // close in
      const dx = w.x - e.x, dy = w.y - e.y, d = Math.hypot(dx, dy) || 1;
      if (d > 60) { e.x += dx / d * 120 * dt; e.y += dy / d * 120 * dt; }
      e.facing = [dx / d, dy / d];
      e.leashLeft = Math.max(0, 1500 - Math.hypot(e.x - e.leashOrigin[0], e.y - e.leashOrigin[1]));
      if (d < 90 && this.r() < 0.06) { const dmg = 20 + this.r() * 40; w.hp = Math.max(1, w.hp - dmg); this.fire('DAMAGED', e.h); }
      if (fighting && this.r() < 0.05) { e.hpPct = Math.max(0, e.hpPct - 8 - this.r() * 10); this.fire('HIT', e.h); }
      if (e.hpPct <= 0) { this.fire('TARGET_DIED', e.h); this.fire('KILL', e.h); w.kills++; w.exp += 320; w.bagFree = Math.max(0, w.bagFree - (this.r() < 0.3 ? 1 : 0)); this.spawnEnt(w.ents.indexOf(e)); }
    }
    if (fleeing) for (const e of aggro) if (this.r() < 0.02) { e.aggro = false; this.fire('AGGRO_LOST', e.h); }
    w.hp = Math.min(w.maxHp, w.hp + (healing ? 90 : aggro.length ? 2 : 12) * dt);
    w.sp = Math.min(w.maxSp, w.sp + 5 * dt - (healing ? 3 * dt : 0));
    if (leafNames.some(n => /town/.test(n)) && this.r() < 0.05) w.bagFree = Math.min(w.bagSize, w.bagFree + 4);
    // drift toward a destination
    if (!w.dest || Math.hypot(w.dest[0] - w.x, w.dest[1] - w.y) < 20) w.dest = [w.x + (this.r() - 0.5) * 1200, w.y + (this.r() - 0.5) * 1200];
    const moving = leafNames.some(n => /flee|approach|hunt|town|follow/.test(n));
    if (moving) {
      const dx = w.dest[0] - w.x, dy = w.dest[1] - w.y, d = Math.hypot(dx, dy) || 1;
      w.x += dx / d * 160 * dt; w.y += dy / d * 160 * dt; w.facing = [dx / d, dy / d];
    }
    for (const e of w.ents) if (!e.aggro && e.k === 'mob' && this.r() < 0.02) { e.x += (this.r() - 0.5) * 40; e.y += (this.r() - 0.5) * 40; }
    // keep the toy world around us: idle mobs far behind respawn nearby
    w.ents.forEach((e, i) => { if (!e.aggro && Math.hypot(e.x - w.x, e.y - w.y) > 1800) this.spawnEnt(i); });
    if (this.r() < 0.0015) this.fire('MOVE_FAILED', 1);
  }

  fire(kind, arg) { this.firedEvents.add(kind); }

  // ---- brain tick ----
  step(dtMs) {
    // real time while running (the host loop can lag); exact 50 ms steps while paused and stepping
    this.now = this.paused ? this.now + dtMs : Math.max(this.now + 1, Date.now()); this.tick++;
    const t0 = process.hrtime.bigint();
    this.stepWorld(dtMs / 1000);
    for (const L of this.layers) {
      if (this.now - L.lastRun < L.rate) continue;
      L.lastRun = this.now;
      L.conditions = [];
      let fired = false;
      for (let i = 0; i < L.path.length && !fired; i++) {
        const fr = L.path[i];
        const st = fr.machine.states[fr.state] || {};
        // progress tracking
        if (st.progress) {
          const pv = st.progress.startsWith('-') ? -this.sensor(st.progress.slice(1).replace(/\(.*\)$/, '')) : this.sensor(st.progress.replace(/\(.*\)$/, ''));
          const val = this.r() < 0.97 ? pv : (fr.progressBest ?? pv);
          if (fr.progressBest == null || val > fr.progressBest) { fr.progressBest = val; fr.progressBestAt = this.now; }
        } else if (this.r() < 0.02) { fr.progressBestAt = this.now; }
        const ts = (st.transitions || []).map((t, idx) => ({ t, idx })).sort((a, b) => (b.t.priority || 0) - (a.t.priority || 0) || a.idx - b.idx);
        const dwellBlocked = st.minDwell && this.now - fr.enteredAt < st.minDwell;
        for (const { t, idx } of ts) {
          if (fired) { L.conditions.push({ state: fr.state, depth: i, index: idx, to: t.to, kind: kindOf(t), expr: exprOf(t), value: null, result: null, holdMs: 0 }); continue; }
          const c = this.cond(t, fr, st);
          let ok = c.result;
          if (dwellBlocked && !t.stuck) ok = false;
          if (ok && t.cooldown && fr.lastFired[idx] && this.now - fr.lastFired[idx] < t.cooldown) ok = false;
          let holdMs = 0;
          if (t.hold) {
            if (ok) { fr.hold[idx] ??= this.now; holdMs = this.now - fr.hold[idx]; ok = holdMs >= t.hold; } else delete fr.hold[idx];
          }
          L.conditions.push({ state: fr.state, depth: i, index: idx, to: t.to, kind: c.kind, expr: c.expr, value: c.value, result: c.result, holdMs, holdTarget: t.hold || 0 });
          if (ok) {
            fired = true;
            this.take(L, i, fr, t, idx, c);
          }
        }
      }
      if (!fired) this.tickLeaf(L);
    }
    // watches
    const watches = this.watches();
    for (const [k, v] of Object.entries(watches)) { (this.watchHist[k] ||= []).push(v); if (this.watchHist[k].length > 120) this.watchHist[k].shift(); }
    // resolve actions
    for (const a of this.actions) {
      if (a.state === 'sent' && this.now >= a._resolveAt) {
        const x = this.r();
        if (x < 0.75) a.state = 'acked';
        else if (x < 0.95) { a.state = 'refused'; const [c, rs] = REFUSALS[Math.floor(this.r() * REFUSALS.length)]; a.code = c; a.reason = rs; this.log('combat', 'N', `${a.kind} refused: ${rs} (${c})`); }
        else { a.state = 'timedout'; a.reason = 'no result in time'; }
        a.resolvedAt = new Date(this.now).toISOString();
      }
    }
    this.actions = this.actions.slice(-40);
    const us = Number(process.hrtime.bigint() - t0) / 1000;
    const brain = us * 0.6;
    this.ticks.push({ tick: this.tick, totalUs: +(150 + this.r() * 300 + (this.r() < 0.02 ? 600 : 0)).toFixed(1), decodeUs: +(5 + this.r() * 15).toFixed(1),
      applyUs: +(20 + this.r() * 40).toFixed(1), actionsUs: +(3 + this.r() * 8).toFixed(1), moveUs: +(5 + this.r() * 10).toFixed(1),
      combatUs: +(5 + this.r() * 10).toFixed(1), brainUs: +(80 + this.r() * 180 + brain).toFixed(1), flushUs: +(1 + this.r() * 4).toFixed(1),
      allocBytes: this.r() < 0.05 ? Math.floor(this.r() * 400) : 0, events: this.firedEvents.size, intents: 0, luaInstructions: Math.floor(500 + this.r() * 4000) });
    if (this.ticks.length > 1000) this.ticks.shift();
  }

  take(L, depth, fr, t, idx, c) {
    const from = L.path.at(-1).state;
    const fromOuter = fr.state;
    fr.lastFired[idx] = this.now;
    let target = String(t.to);
    let machine = fr.machine;
    let d = depth;
    if (target === '^') { d = Math.max(0, depth - 1); machine = L.path[d].machine; target = machine.initial; }
    else if (target.includes(':')) { const [mn, sn] = target.split(':'); machine = this.def.machines?.[mn] || machine; target = sn; }
    const reason = (t.reason || defaultReason(t, c)).replace(/\{([^}]+)\}/g, (_, s) => String(this.sensor(s)));
    const values = {};
    if (c.value != null) values[t.value || c.kind] = c.value;
    this.enter(L, d, machine, target, reason);
    const rec = { tick: this.tick, at: new Date(this.now).toISOString(), layer: L.name, from: fromOuter, leafFrom: from, to: target,
      depth: d, index: idx, kind: c.kind, reason, values };
    this.transitions.push(rec);
    if (this.transitions.length > 500) this.transitions.shift();
    this.onTransition?.(rec);
    this.log('brain', 'I', `${L.name}: ${fromOuter} -> ${target} (${c.kind}: ${reason})`);
  }

  tickLeaf(L) {
    const fr = L.path.at(-1); if (!fr) return;
    const st = fr.machine.states[fr.state] || {};
    if (!st.tick && !st.enter) return;
    if (this.r() < 0.002) {
      const err = 'E_ARG: act.cast: skill must be integer';
      this.errors.push({ tick: this.tick, layer: L.name, state: fr.state, error: err });
      if (this.errors.length > 50) this.errors.shift();
      this.log('script', 'C', `${L.name}/${fr.state}: ${err}`);
    }
    if (this.r() > 0.04) return;
    const claims = st.claims || L.claims;
    const kind = claims.includes('Cast') ? 'CAST' : claims.includes('Combat') ? 'BASH' : claims.includes('Movement') ? 'MOVE' : claims.includes('Npc') ? 'OPEN_SHOP' : (/town/.test(fr.state) ? 'OPEN_SHOP' : 'MOVE');
    const a = { id: this.nextActionId++, kind, state: 'sent', code: 0, reason: '', sentAt: new Date(this.now).toISOString(), resolvedAt: null,
      layer: L.name, _resolveAt: this.now + 150 + this.r() * 900 };
    this.actions.push(a);
    if (kind === 'OPEN_SHOP' && !this.procedures.length) {
      this.procedures.push({ kind: 'OPEN_SHOP', step: 'awaitShopList', since: new Date(this.now).toISOString(), _until: this.now + 4000 });
    }
    this.procedures = this.procedures.filter(p => p._until > this.now);
    if (fr.state === 'hunt' || fr.state === 'engage') this.bb.layer[L.name].target = this.world.ents.find(e => e.aggro)?.h ?? -1;
    if (fr.state === 'choose') this.bb.state[L.name].plan = 40 + (this.tick % 7);
  }

  watches() {
    const w = this.world;
    return { hpPct: +(w.hp * 100 / w.maxHp).toFixed(1), onUs: w.ents.filter(e => e.aggro).length, bagFree: w.bagFree,
      pullCost: +(300 + 200 * Math.sin(this.now / 5000)).toFixed(1) };
  }

  // ---- published views ----
  debugFrame() {
    const layers = this.layers.map(L => {
      const fr = L.path.at(-1);
      return {
        name: L.name, priority: L.priority, path: L.path.map(f => f.state), enteredAt: fr ? new Date(fr.enteredAt).toISOString() : null,
        timeInStateMs: fr ? this.now - fr.enteredAt : 0, enteredAtPath: L.path.map(f => new Date(f.enteredAt).toISOString()),
        progress: fr?.progressBest ?? null, progressBestAt: fr ? new Date(fr.progressBestAt).toISOString() : null,
        budgetMs: fr ? (fr.machine.states[fr.state]?.budget ?? 60000) : null,
        claims: L.claims, conditions: L.conditions,
      };
    });
    const ticks = this.ticks.slice(-200).map(t => t.totalUs).sort((a, b) => a - b);
    const q = p => ticks.length ? ticks[Math.min(ticks.length - 1, Math.floor(p * ticks.length))] : 0;
    const last = this.ticks.at(-1) || {};
    return {
      bot: this.id, tick: this.tick, t: new Date(this.now).toISOString(), version: this.version, paused: this.paused,
      layers,
      lastTransitions: this.transitions.slice(-20).reverse(),
      blackboard: JSON.parse(JSON.stringify(this.bb)),
      watches: this.watches(),
      actions: this.actions.map(({ _resolveAt, ...a }) => a).slice(-20).reverse(),
      procedures: this.procedures.map(({ _until, ...p }) => p),
      tickStats: { p50us: q(0.5), p99us: q(0.99), allocP95B: 0,
        steps: { decode: last.decodeUs, apply: last.applyUs, actions: last.actionsUs, move: last.moveUs, combat: last.combatUs, brain: last.brainUs, flush: last.flushUs } },
      errors: this.errors.slice(-10).reverse(),
    };
  }

  sensorFrame() {
    const w = this.world;
    const cell = 200, gw = 13, gh = 13, ox = Math.floor(w.x / cell) * cell - 6 * cell, oy = Math.floor(w.y / cell) * cell - 6 * cell;
    const vals = new Uint8Array(gw * gh);
    for (let j = 0; j < gh; j++) for (let i = 0; i < gw; i++) {
      const cx = ox + (i + 0.5) * cell, cy = oy + (j + 0.5) * cell;
      let v = 0; for (const e of w.ents) if (e.k === 'mob') v += Math.max(0, 255 - Math.hypot(e.x - cx, e.y - cy) / 2) * (e.aggro ? 1 : 0.4);
      vals[j * gw + i] = Math.min(255, v);
    }
    return {
      bot: this.id, map: 'MockField', mapId: 0, self: { x: w.x, y: w.y, facing: w.facing },
      entities: w.ents.map(e => ({ h: e.h, k: e.k, mobId: e.mobId, name: e.k === 'npc' ? `npc ${e.h}` : `mob #${e.mobId}`, x: e.x, y: e.y, hpPct: e.hpPct,
        aggro: e.aggro, detect: e.k === 'mob' ? e.detect : null, facing: e.facing, leashOrigin: e.leashOrigin, leashLeft: e.leashLeft })),
      reach: { bash: 162.5, skills: [{ id: 1, reach: 420 }] },
      path: w.dest ? [[w.x, w.y], [(w.x + w.dest[0]) / 2 + 40, (w.y + w.dest[1]) / 2 - 40], w.dest] : [],
      threat: { cellSize: cell, origin: [ox, oy], w: gw, h: gh, values: Buffer.from(vals).toString('base64') },
    };
  }

  summary(rec) {
    const hours = Math.max(1 / 60, (Date.now() - this.startedAt) / 3600000);
    const ticks = this.ticks.slice(-200).map(t => t.totalUs).sort((a, b) => a - b);
    const layerStates = {}; const layerSince = {}; const layerPaths = {};
    for (const L of this.layers) {
      const fr = L.path.at(-1);
      layerStates[L.name] = L.path.map(f => f.state).join('/');
      layerSince[L.name] = fr ? new Date(L.path[0].enteredAt).toISOString() : null;
      layerPaths[L.name] = L.path.map(f => f.state);
    }
    const lt = this.transitions.at(-1);
    return {
      layerStates, layerSince, layerPaths, versionHash: this.version.split('@')[1] || this.version, version: this.version,
      tickP99Us: ticks.length ? ticks[Math.floor(ticks.length * 0.99)] : 0, ticksPerSec: this.paused ? 0 : 20,
      expPerHour: Math.round((this.world.exp - this.expStart) / hours), deathsPerHour: +(this.world.deaths / hours).toFixed(2),
      lastTransition: lt ? { at: lt.at, layer: lt.layer, from: lt.from, to: lt.to, reason: lt.reason } : null,
      map: 'MockField', paused: this.paused,
      vitals: { hp: Math.round(this.world.hp), maxHp: this.world.maxHp, sp: Math.round(this.world.sp), maxSp: this.world.maxSp,
        exp: this.world.exp, expNext: this.world.expNext, money: this.world.money, bagFree: this.world.bagFree, bagSize: this.world.bagSize,
        x: Math.round(this.world.x), y: Math.round(this.world.y) },
      errors: this.errors.length,
    };
  }
}

export function kindOf(t) {
  for (const k of ['all', 'any', 'not']) if (t[k]) return k;
  if (t.value != null) return 'value';
  if (t.after != null) return 'after';
  if (t.every != null) return 'every';
  if (t.stuck) return 'stuck';
  if (t.on === 'action') return 'action';
  if (t.on != null) return 'event';
  if (t.procedure != null) return 'procedure';
  if (t.job != null) return 'job';
  if (t.when != null) return 'lua';
  if (t.always) return 'always';
  return 'none';
}

export function exprOf(t) {
  switch (kindOf(t)) {
    case 'value': return `${t.value} ${t.op} ${t.than}`;
    case 'after': return `after ${t.after} ms`;
    case 'every': return `every ${t.every} ms`;
    case 'stuck': return 'stuck';
    case 'event': return `on ${t.on}`;
    case 'action': return `action ${t.action} ${t.result}`;
    case 'procedure': return `procedure ${t.procedure} ${t.result ?? ''}`;
    case 'job': return `job ${t.job} = ${t.result ?? ''}`;
    case 'lua': return String(t.when);
    default: return kindOf(t);
  }
}

function defaultReason(t, c) {
  return c.value != null && c.kind === 'value' ? `${c.expr} (${c.value})` : c.expr;
}
