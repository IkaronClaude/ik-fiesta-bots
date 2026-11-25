// node --test mock/test/ : checks that the mock follows the spec it is generated from.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildManifest } from '../manifest.mjs';
import { lintBrain, lintLua, parseDefinition } from '../lint.mjs';
import { diff } from '../ws.mjs';
import { unifiedDiff } from '../textdiff.mjs';
import { BotSim } from '../sim.mjs';
import * as F from '../fixtures.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const manifest = buildManifest(resolve(here, '../../../../docs/API_SPEC.md'));

test('manifest has the 223 spec functions plus the two global aliases', () => {
  assert.equal(manifest.functions.filter(f => f.module !== '_G').length, 223);
  assert.deepEqual(manifest.functions.filter(f => f.module === '_G').map(f => f.name).sort(), ['canUse', 'log']);
  assert.ok(manifest.events.some(e => e.name === 'AGGRO_GAINED'));
  assert.ok(manifest.sensors.some(s => s.name === 'self.hpPct'));
});

test('fixture brains lint without errors', () => {
  const libs = { 'user/ikaron/heal': F.HEAL_LUA_V1 };
  const mods = { 'brain/leveller/survival': F.SURVIVAL_LUA, 'brain/leveller/errand': F.ERRAND_LUA };
  for (const [def, m] of [[F.LEVELLER_V1, mods], [F.LEVELLER_V2, mods], [F.ASSIST_V1, {}]]) {
    assert.deepEqual(lintBrain(def, m, manifest, libs).filter(l => l.severity === 'error'), []);
  }
});

test('lint finds syntax errors, unknown API names and bad targets', () => {
  assert.equal(lintLua('local x = = 1', manifest, {})[0].code, 'LUA_SYNTAX');
  assert.equal(lintLua('return self.notAThing()', manifest, {})[0].code, 'API_UNKNOWN');
  const bad = F.ASSIST_V1.replace('- { to: follow, after: 10000 }', '- { to: nowhere, after: 10000 }');
  assert.ok(lintBrain(bad, {}, manifest, { 'user/ikaron/heal': F.HEAL_LUA_V1 }).some(l => l.code === 'TARGET_UNKNOWN'));
});

test('json patch diff covers add / remove / replace', () => {
  const p = diff({ a: 1, b: { c: [1, 2] }, d: 'x' }, { a: 2, b: { c: [1, 3] }, e: true });
  assert.deepEqual(p, [{ op: 'remove', path: '/d' }, { op: 'replace', path: '/a', value: 2 }, { op: 'replace', path: '/b/c/1', value: 3 }, { op: 'add', path: '/e', value: true }]);
});

test('unified diff', () => {
  assert.equal(unifiedDiff('a\nb', 'a\nb'), '');
  assert.match(unifiedDiff('a\nb\nc', 'a\nB\nc'), /-b\n\+B/);
});

test('sim runs a brain and records transitions with depth and index', () => {
  const sim = new BotSim({ id: 'T', classFamily: 11 }, parseDefinition(F.LEVELLER_V1), 'leveller@test', () => {});
  for (let i = 0; i < 4000; i++) sim.step(50);
  const f = sim.debugFrame();
  assert.equal(f.layers.length, 3);
  assert.ok(f.layers.every(l => l.path.length >= 1));
  assert.ok(sim.transitions.length > 0);
  assert.ok(sim.transitions.every(t => typeof t.index === 'number' && typeof t.depth === 'number'));
});

test('game view: snapshot shape and small deltas (API_SPEC 14.1)', async () => {
  const { initGame, stepGame, snapshot, model, diffOps } = await import('../game.mjs');
  const sim = new BotSim({ id: 'G', classFamily: 11, level: 30 }, parseDefinition(F.LEVELLER_V1), 'leveller@test', () => {});
  initGame(sim);
  const snap = snapshot(sim);
  for (const k of ['self', 'stats', 'abstates', 'skills', 'bag', 'equip', 'target', 'path', 'ents']) assert.ok(k in snap, k);
  assert.ok(snap.ents.every(r => r.length === 8 && 'mnpgd'.includes(r[1])));
  let prev = model(sim);
  for (let i = 0; i < 40; i++) { sim.step(50); stepGame(sim); }
  const ops = diffOps(prev, model(sim));
  assert.ok(ops.every(o => typeof o[0] === 'string'));
  assert.ok(ops.filter(o => o[0] === 'ep').every(o => o.length === 4));
  prev = model(sim);
  assert.deepEqual(diffOps(prev, model(sim)), []);
});
