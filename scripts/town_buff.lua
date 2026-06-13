-- town_buff.lua — a priest parked in town that buffs people on request.
--
-- Listens to local chat; when someone says the trigger word it casts the configured
-- buff skills on that speaker (per-speaker throttled). This is the chat-command +
-- low-latency-reaction pattern: no HTTP round-trip, the bot reacts the instant the
-- chat frame arrives.
local TRIGGER     = "buff"      -- substring to match (case-insensitive)
local BUFF_SKILLS = { 1580 }    -- learnt buff skill ids (e.g. 1580 = Endure [01])
local COOLDOWN_MS = 10000       -- don't re-buff the same person within this window

local lastBuffed = {}           -- handle -> last buff time (ms)

function on_start()
  log("town_buff ready — say '" .. TRIGGER .. "' nearby for buffs")
end

function on_chat(msg)
  if string.find(string.lower(msg.text), TRIGGER, 1, true) == nil then return end

  local last = lastBuffed[msg.handle] or 0
  if bot.now() - last < COOLDOWN_MS then return end
  lastBuffed[msg.handle] = bot.now()

  local who = msg.name or ("h" .. msg.handle)
  log("buff request from " .. who)
  for _, skill in ipairs(BUFF_SKILLS) do
    bot.cast(skill, msg.handle)
  end
end
