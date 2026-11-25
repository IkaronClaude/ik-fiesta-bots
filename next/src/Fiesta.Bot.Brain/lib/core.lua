-- lib/core: host-provided helpers (read-only, versioned with the API)
local C = {}

function C.clamp(v, lo, hi)
  if v < lo then return lo elseif v > hi then return hi end
  return v
end

function C.pctOf(a, b)
  if b <= 0 then return 0 end
  return a * 100 / b
end

-- true when an action id has resolved (any end state)
function C.resolved(a)
  local s = act.state(a)
  return s ~= "queued" and s ~= "sent"
end

-- the nearest live huntable mob within r that is not already claimed by another player (-1 none)
function C.nearestTarget(r)
  for h in ents.near(r, ents.HUNTABLE) do
    if not ents.isDead(h) then return h end
  end
  return -1
end

return C
