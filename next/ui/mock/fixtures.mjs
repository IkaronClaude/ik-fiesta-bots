// Fixture data for the mock host. Illustrative only: the brain, module and bot names follow the examples in
// docs/REDESIGN.md 5.6-5.7; every game value (ids, positions, maps) is synthetic test data, not game data.

export const LEVELLER_V1 = `brain: leveller
api: 3
description: Layered leveller (example from REDESIGN 5.6)
modules: [brain/leveller/survival, brain/leveller/errand, user/ikaron/heal]
blackboard: { wantKill: false }
machines:
  combat/melee:
    initial: approach
    states:
      approach:
        tick: "act.moveTo(ents.pos(bb.state.target or -1))"
        budget: 8000
        progress: "-ents.dist(bb.layer.target or -1)"
        transitions:
          - { to: swing, when: "combat.inBashReach(bb.layer.target or -1)" }
          - { to: "^", stuck: true, reason: "cannot reach target" }
      swing:
        claims: [Combat]
        enter: "act.bash(bb.layer.target or -1)"
        budget: 20000
        transitions:
          - { to: approach, on: MOVE_FAILED }
          - { to: "^", after: 30000 }
  combat/caster:
    initial: cast
    states:
      cast:
        claims: [Cast]
        tick: "local s = skills.best(skills.DAMAGE, bb.layer.target or -1) if s >= 0 then act.cast(s, bb.layer.target) end"
        budget: 15000
        transitions:
          - { to: "^", after: 30000 }
layers:
  - name: survival
    priority: 90
    rate: tick
    machine:
      initial: ok
      states:
        ok:
          terminal: true
          transitions:
            - { to: recover, value: self.hpPct, op: "<", than: 35, hold: 200, reason: "hp {self.hpPct}%" }
            - { to: flee, when: "threat.outmatched()", hold: 300 }
        recover:
          claims: [Cast]
          tick: survival.recoverTick
          budget: 15000
          progress: self.hpPct
          transitions:
            - { to: ok, value: self.hpPct, op: ">=", than: 70 }
            - { to: flee, stuck: true }
        flee:
          claims: [Movement, Combat, Cast]
          enter: survival.fleeEnter
          tick: survival.fleeTick
          budget: 20000
          progress: "-threat.incomingDps()"
          transitions:
            - { to: ok, all: [ { on: AGGRO_LOST }, { after: 2000 } ] }
            - { to: recover, stuck: true }
  - name: combat
    priority: 60
    rate: tick
    machine:
      initial: idle
      states:
        idle:
          terminal: true
          transitions:
            - { to: engage, on: AGGRO_GAINED }
            - { to: engage, when: "bb.bot.wantKill == true" }
        engage:
          use: combat/melee
          budget: 45000
          progress: "-ents.hp(bb.layer.target or -1)"
          transitions:
            - { to: idle, on: TARGET_DIED }
            - { to: idle, stuck: true, reason: "no damage for 45 s" }
  - name: errand
    priority: 30
    rate: 500
    machine:
      initial: choose
      states:
        choose:
          enter: errand.choose
          budget: 10000
          transitions:
            - { to: hunt, job: QUEST_PLAN, result: HUNT }
            - { to: town, value: self.bagFree, op: "<=", than: 2 }
            - { to: hunt, after: 8000, reason: "plan slow, hunt anyway" }
        hunt:
          tick: errand.huntTick
          budget: 600000
          progress: errand.questProgress
          transitions:
            - { to: town, value: self.bagFree, op: "<=", than: 0 }
            - { to: choose, stuck: true }
        town:
          enter: errand.townEnter
          budget: 300000
          transitions:
            - { to: choose, procedure: OPEN_SHOP, result: ok }
            - { to: choose, stuck: true }
`;

export const LEVELLER_V2 = LEVELLER_V1
  .replace('than: 35, hold: 200', 'than: 40, hold: 250')
  .replace('            - { to: hunt, after: 8000, reason: "plan slow, hunt anyway" }\n', '')
  .replace('description: Layered leveller (example from REDESIGN 5.6)', 'description: Layered leveller; flee earlier, no blind hunt');

export const SURVIVAL_LUA = `-- brain/leveller/survival: the survival layer's state functions
local my = require "user/ikaron/heal"
local S = {}

function S.recoverTick()
  local me = self.handle()
  local s = my.bestHeal(me)
  if s >= 0 and canUse(s, me) and my.isGoodHealSkill(s) then   -- canUse: C#; isGoodHealSkill: user library
    act.cast(s, me)
    return
  end
  local p = items.bestPotion(self.maxHp() - self.hp(), items.POTION)
  if p >= 0 then act.use(p) end
end

function S.fleeEnter()
  local x, y = threat.safeSpot(600)
  if x then bb.state.dest = { x = x, y = y } end
end

function S.fleeTick()
  local d = bb.state.dest
  if d and not self.moving() then act.moveTo(d.x, d.y) end
end

return S
`;

export const ERRAND_LUA = `-- brain/leveller/errand: quest choice, hunting and town runs
local E = {}

function E.choose()
  bb.state.plan = quests.plan(1)
end

function E.huntTick()
  local h = ents.nearest(ents.HUNTABLE, 800)
  if h >= 0 and not combat.bashing() then
    bb.bot.wantKill = true
    bb.layer.target = h
  end
end

function E.questProgress()
  local sum = 0
  for q in quests.active() do
    for i = 1, quests.objectives(q) do
      local have = quests.progress(q, i)
      sum = sum + have
    end
  end
  return sum
end

function E.townEnter()
  local h = npc.nearest(npc.SHOP)
  if h >= 0 then bb.state.shop = npc.openShop(h) end
end

return E
`;

export const HEAL_LUA_V1 = `-- user/ikaron/heal: shared heal policy (subjective, tuned in the editor)
local M = {}

-- canUse() (C#) answers "is it possible"; this answers "is it a good idea".
function M.isGoodHealSkill(skill)
  if not skills.hasTag(skill, skills.HEAL) or skills.hasTag(skill, skills.PARTY) then return false end
  local missing = self.maxHp() - self.hp()
  -- don't burn a big, slow heal on a scratch, and keep SP for damage when nothing is on us
  if skills.healAmount(skill) > missing * 2 and threat.onUs() == 0 then return false end
  return skills.castTimeMs(skill) < 1500 or threat.onUs() == 0
end

function M.bestHeal(target)
  for s in skills.list(skills.HEAL) do
    if canUse(s, target) and M.isGoodHealSkill(s) then return s end
  end
  return -1
end

return M
`;

export const HEAL_LUA_V2 = HEAL_LUA_V1.replace('castTimeMs(skill) < 1500', 'castTimeMs(skill) < 1200');

export const ASSIST_V1 = `brain: assist
api: 3
description: Follow a leader and heal the party
modules: [user/ikaron/heal]
layers:
  - name: support
    priority: 50
    rate: 250
    machine:
      initial: follow
      states:
        follow:
          tick: "local h = social.partyMembers()() if h then act.follow(h, 150) end"
          budget: 120000
          transitions:
            - { to: heal, when: "self.hpPct() < 60" }
            - { to: follow, stuck: true }
        heal:
          claims: [Cast]
          tick: "local my = require 'user/ikaron/heal' local s = my.bestHeal(self.handle()) if s >= 0 then act.cast(s, self.handle()) end"
          budget: 10000
          transitions:
            - { to: follow, value: self.hpPct, op: ">=", than: 85 }
            - { to: follow, after: 10000 }
`;

export const LIB_CORE = `-- lib/core: host-provided helpers (read-only, versioned with the API)
local C = {}

function C.clamp(v, lo, hi)
  if v < lo then return lo elseif v > hi then return hi end
  return v
end

function C.pctOf(a, b)
  if b <= 0 then return 0 end
  return a * 100 / b
end

return C
`;

export const BOTS = [
  { id: 'MockArcher', username: 'mock_archer', character: 'MockArcher', host: '127.0.0.1', loginPort: 9010,
    brain: 'leveller', channel: 'stable', wire: '2016', classFamily: 11, level: 31 },
  { id: 'MockCleric', username: 'mock_cleric', character: 'MockCleric', host: '127.0.0.1', loginPort: 9010,
    brain: 'leveller', channel: 'candidate', wire: '2026', classFamily: 6, level: 28 },
  { id: 'MockHealer', username: 'mock_healer', character: 'MockHealer', host: '127.0.0.1', loginPort: 9010,
    brain: 'assist', channel: 'stable', wire: '2016', classFamily: 6, level: 22 },
  { id: 'MockOffline', username: 'mock_off', character: 'MockOffline', host: '127.0.0.1', loginPort: 9010,
    brain: null, channel: null, wire: '2026', classFamily: 1, level: 12, offline: true },
];
