-- guild_buff_sm.lua — a support bot as a STATE MACHINE: idle -> buff -> idle.
--
-- The "buff guild" tree: park in town, wait for a guildmate to ask in chat, then
-- buff them. Demonstrates chat-driven transitions. (Reaching a distant member via
-- academy teleport isn't wired yet — the packet is still TBD; flagged below.)
--
-- Apply with:  POST /api/bots/{id}/statemachine  { "name": "guild_buff_sm" }
local TRIGGER = "buff"        -- substring to match in local chat (case-insensitive)
local BUFFS   = { 1580 }      -- learnt buff skill ids (1580 = Endure [01])
local requester = nil

statemachine({
  idle = {
    on_enter = function() log("idle: waiting for a '" .. TRIGGER .. "' request") end,
    on_chat = function(m)
      if string.find(string.lower(m.text), TRIGGER, 1, true) then
        requester = m.handle
        log("request from " .. (m.name or ("h" .. m.handle)))
      end
    end,
    next = function() if requester ~= nil then return "buff" end end,
  },

  buff = {
    on_enter = function()
      -- TODO: if the requester isn't nearby, academy-teleport to them (packet TBD).
      log("buff: casting on h=" .. tostring(requester))
      for _, s in ipairs(BUFFS) do bot.cast(s, requester) end
      requester = nil
    end,
    next = function() return "idle" end,   -- one-shot, straight back to idle
  },
}, "idle")
