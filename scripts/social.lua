-- social — auto-accept incoming party invites and friend requests. Runs as its own loop
-- (independent of the gameplay/grind script) so an operator can friend the bot and party
-- with it to watch, without the bot needing manual steering. Both are WM-link actions and
-- don't touch movement/combat.
function on_enter() log("social: auto-accepting party invites + friend requests") end

function tick()
  local inv = bot.pendingInvite()
  if inv ~= "" then
    if bot.partyAccept() then log("social: accepted party invite from " .. inv) end
  end
  local fr = bot.pendingFriend()
  if fr ~= "" then
    if bot.friendAccept() then log("social: accepted friend request from " .. fr) end
  end
end
