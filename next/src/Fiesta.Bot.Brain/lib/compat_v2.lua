-- lib/compat_v2: the old bot.* surface over the API 3 modules, for migrating API-2 scripts (allocates; migration only)
local B = {}
function B.hp() return self.hp() end
function B.maxHp() return self.maxHp() end
function B.level() return self.level() end
function B.pos() return self.pos() end
function B.map() return self.map() end
function B.cast(s, t) return act.cast(s, t) >= 0 end
function B.walkTo(x, y) return act.moveTo(x, y) >= 0 end
function B.nearbyMobs(r)
  local out = {}
  for h in ents.near(r or 1200, ents.MOB) do
    local x, y = ents.pos(h)
    out[#out + 1] = { handle = h, mobId = ents.mobId(h), x = x, y = y, hp = ents.hp(h), dist = ents.dist(h) }
  end
  return out
end
return B
