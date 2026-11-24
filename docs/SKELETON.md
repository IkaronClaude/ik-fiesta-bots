# Redesign skeleton

The compiling skeleton of the redesigned runtime (`docs/REDESIGN.md`), with the API of `docs/API_SPEC.md`.
Location: `next/` (own solution `next/Fiesta.Bot.Next.slnx`; the current `ik-fiesta-bots.slnx` and
`src/` are untouched). Bodies throw `NotImplementedException`; signatures and XML summaries are the spec.

Build: `dotnet build next/Fiesta.Bot.Next.slnx` (0 warnings, 0 errors on .NET SDK 10.0.203).

Naming during coexistence: the new host is `Fiesta.Bot.Server` (it replaces `Fiesta.Bot.Host` at the end of
the migration); the Roslyn generator and analyzers live in `Fiesta.Bot.CodeAnalysis`. Dependency order:
Net -> Protocol -> Services -> Agent -> Api -> Brain -> Fleet -> Server.

## Totals

| project | types | classes | interfaces | records | structs | enums | public members | enum values |
|---|---|---|---|---|---|---|---|---|
| Fiesta.Bot.Agent | 64 | 38 | 4 | 6 | 2 | 14 | 221 | 168 |
| Fiesta.Bot.Api | 32 | 23 | 0 | 3 | 0 | 6 | 259 | 51 |
| Fiesta.Bot.Brain | 42 | 14 | 1 | 21 | 0 | 6 | 147 | 32 |
| Fiesta.Bot.CodeAnalysis | 3 | 3 | 0 | 0 | 0 | 0 | 3 | 0 |
| Fiesta.Bot.Fleet | 19 | 5 | 4 | 8 | 0 | 2 | 86 | 16 |
| Fiesta.Bot.Net | 14 | 2 | 6 | 3 | 1 | 2 | 31 | 9 |
| Fiesta.Bot.Protocol | 16 | 10 | 3 | 0 | 2 | 1 | 30 | 43 |
| Fiesta.Bot.Server | 13 | 5 | 0 | 7 | 0 | 1 | 33 | 6 |
| Fiesta.Bot.Services | 23 | 5 | 5 | 9 | 0 | 4 | 87 | 22 |
| Fiesta.Bot.Next.Tests | 4 | 4 | 0 | 0 | 0 | 0 | 13 | 0 |

**10 projects, 230 types, 910 public members** (members counted as documented public declarations, record positional parameters and interface members; enum values separately).

## Fiesta.Bot.Agent

- `ActionId` (record struct, 2 members, Actions.cs): Action identifier (index + generation into the action table).
- `ActionState` (enum, 7 values, Actions.cs): Lifecycle of an action (API_SPEC 4.1).
- `ActionKind` (enum, 36 values, Actions.cs): Action kinds (API_SPEC 4.2).
- `Resource` (enum, 8 values, Actions.cs): Resources a layer can claim (REDESIGN 5.1).
- `LocalRefusal` (enum, 11 values, Actions.cs): Local refusal codes (`act.L_*`).
- `ActionRecord` (struct, 6 members, Actions.cs): Stored state of one action.
- `IResultMatcher` (interface, 1 members, Actions.cs): Decides whether an inbound event resolves a pending action.
- `ActionSpec` (record, 4 members, Actions.cs): Static description of an action kind: matcher, timeout, resource.
- `ActionCatalog` (class, 2 members, Actions.cs): The table of every action kind; an action kind without a matcher cannot be registered.
- `ActionSystem` (class, 11 members, Actions.cs): Creates actions, tracks them, resolves them from inbound events, times them out. Tick thread only.
- `ProcedureKind` (enum, 9 values, Actions.cs): Procedure kinds (multi-packet interactions).
- `ProcedureStatus` (enum, 4 values, Actions.cs): Procedure outcome.
- `IProcedure` (interface, 3 members, Actions.cs): A resumable C# state machine stepped once per tick; it never waits.
- `ProcedureBase` (class, Actions.cs): Base for procedures: timeouts per step, status plumbing.
- `OpenShopProcedure` (class, 2 members, Actions.cs): END_OF_TRADE -> NPCCLICK -> menu -> shop list.
- `QuestDialogueProcedure` (class, 3 members, Actions.cs): Quest dialogue page by page ([NEXT], [MENU], [BUTTON], [SHOW_REWARD]).
- `GateHopProcedure` (class, 2 members, Actions.cs): Dismount -> walk into gate range -> click -> answer -> await the map link.
- `DismountProcedure` (class, Actions.cs): Use the mount item and wait for the ride-off event (no fixed sleeps).
- `ReviveProcedure` (class, Actions.cs): Revive request and the same-map / other-map revive paths.
- `TradeProcedure` (class, 1 members, Actions.cs): Propose/accept -> board items/money -> lock -> confirm.
- `TentProcedure` (class, Actions.cs): Find a clear spot (nav.clearSpot) -> walk -> pitch -> rest -> fold.
- `StorageProcedure` (class, 1 members, Actions.cs): Open the storage keeper and move items.
- `TravelProcedure` (class, 1 members, Actions.cs): Multi-map travel: a chain of GateHop procedures along a planned map route.
- `ProcedureRunner` (class, 4 members, Actions.cs): Runs the bot's active procedures (at most one per resource).
- `MoveFailKind` (enum, 2 values, Controllers.cs): Why the game refused a move.
- `MoveOrder` (record, 5 members, Controllers.cs): What the movement controller is asked to do.
- `MoveController` (class, 7 members, Controllers.cs): Owns MOVERUN/STOP: streams legs at client pace, keeps the believed position within the game's tolerance, classifies MOVEFAIL, tells the combat controller about moves.
- `CombatController` (class, 9 members, Controllers.cs): Owns battle mode, targeting, BASHSTART/STOP and the cast queue (no learned reach: GameRules).
- `CanUseReason` (enum, 15 values, Controllers.cs): Why <see cref="GameRules.CanUse"/> said no (the Lua `skills.R_*` codes).
- `WeaponClass` (enum, 6 values, Controllers.cs): Weapon class for reach.
- `GameRules` (class, 13 members, Controllers.cs): The game's rules in one tested place (REVIEW section 5; each confirmed by a live probe).
- `ILearner` (interface, 1 members, Controllers.cs): Learners turn observations into knowledge-store samples (server-only facts, client-legal).
- `AggroLearner` (class, Controllers.cs): Detect radius: lower bounds from "noticed us at d", upper bounds from "did not notice us in sight at d".
- `LeashLearner` (class, Controllers.cs): Chase distance from the last-hit point at the moment a mob turns back.
- `MobAttackLearner` (class, Controllers.cs): Mob reach (largest landed swing distance minus our body) and swing interval.
- `AssistLearner` (class, Controllers.cs): Family assist radius: distance of mobs that aggro within 1 s of our first hit on a family member.
- `DamageFitter` (class, Controllers.cs): Least-squares fit of a mob's weapon range and armour from hits taken and dealt (damage formula known).
- `EventKind` (enum, 34 values, Runtime.cs): Brain-visible event kinds (API_SPEC section 3; the Lua `ev.*` constants).
- `BrainEvent` (record struct, 4 members, Runtime.cs): One brain event.
- `BrainEventBus` (class, 5 members, Runtime.cs): Per-tick event bitset plus a small ring per kind (for ev.fired / ev.count / ev.last).
- `InboundRing` (class, 3 members, Runtime.cs): Bounded single-producer/single-consumer ring (I/O thread -> tick). Never blocks.
- `OutboundRing` (class, 3 members, Runtime.cs): Outbound ring (tick -> I/O writer).
- `ControlKind` (enum, 9 values, Runtime.cs): Operator commands drained at the start of a tick.
- `ControlCommand` (record, 5 members, Runtime.cs): One operator command.
- `ControlQueue` (class, 2 members, Runtime.cs): Thread-safe queue of operator commands (HTTP -> tick).
- `TickSample` (struct, 5 members, Runtime.cs): Per-tick timing and allocation, recorded every tick.
- `TickStats` (class, 4 members, Runtime.cs): HDR-style histograms of tick samples (1 s / 1 m / 10 m windows).
- `IBrainTicker` (interface, 1 members, Runtime.cs): Hook the brain runtime implements; the agent calls it in tick step 6.
- `AgentContext` (class, 15 members, Runtime.cs): Everything a tick step or helper module may touch for one bot (tick thread only).
- `BotAgent` (class, 6 members, Runtime.cs): One bot: owns its state; ticked by exactly one worker; never awaits.
- `AgentSnapshot` (record, 9 members, Runtime.cs): Immutable view published every 100 ms for HTTP/UI readers.
- `WorldComponent` (enum, 8 values, WorldState.cs): World components that carry a version counter (incremented on change).
- `EntityKind` (enum, 11 values, WorldState.cs): Entity kind bits (also the Lua `ents.*` filter values).
- `SelfState` (class, 19 members, WorldState.cs): Our character's state.
- `EntityTable` (class, 15 members, WorldState.cs): Entities in view as struct-of-arrays with a handle -> slot map (no per-entity objects).
- `SpatialGrid` (class, 6 members, WorldState.cs): Uniform-grid spatial index over the entity table (200 u cells).
- `Inventory` (class, 8 members, WorldState.cs): Bag, equipment and per-item indexes.
- `SkillBook` (class, 6 members, WorldState.cs): Learned skills, cooldowns (incl. groups) confirmed by the server, empower.
- `QuestLog` (class, 6 members, WorldState.cs): Quest log: active, done, progress.
- `PartyState` (class, 3 members, WorldState.cs): Party membership.
- `TradePhase` (enum, 8 values, WorldState.cs): Trade window state.
- `TradeState` (class, 3 members, WorldState.cs): Trade state.
- `MapState` (class, 4 members, WorldState.cs): Current map, doors, gates.
- `WorldState` (class, 11 members, WorldState.cs): Everything one bot knows, owned by its tick thread. Inbound events are applied at the start of the tick.

## Fiesta.Bot.Api

- `LuaModuleAttribute` (class, 1 members, Binding.cs): Marks a class as a Lua module; the source generator emits its bindings and manifest entries.
- `LuaFnAttribute` (class, 4 members, Binding.cs): Marks a method as a Lua function (name = method name with a lower-case first letter unless given).
- `LuaConstAttribute` (class, 1 members, Binding.cs): Marks a constant exposed in the module table.
- `Cost` (enum, 5 values, Binding.cs): Cost classes.
- `Alloc` (enum, 4 values, Binding.cs): Allocation marks.
- `IteratorKind` (enum, 7 values, Binding.cs): Iterator sources the generated bindings know how to walk without building tables.
- `LuaIter` (record struct, 3 members, Binding.cs): A lazy iterator handed to Lua: the binding pushes a cached iterator closure plus this state; each step asks the module for the next item after the control value.
- `ApiModule` (class, 1 members, Binding.cs): Base for modules: access to the bot's context for the current tick, and the calling layer.
- `ApiErrors` (class, 3 members, Binding.cs): Pending error state for one VM, checked by the runner after a state call.
- `ManifestFunction` (record, 7 members, Binding.cs): One function in the API manifest.
- `ApiManifest` (record, 7 members, Binding.cs): The generated API manifest (editor completion, linter, brain `api:` check).
- `ApiRegistry` (class, 1 members, Binding.cs): Creates every module for a bot and registers them into a Lua engine.
- `SkillTag` (enum, 17 values, Constants.cs): Skill tag bits (precomputed from ActiveSkill at load; the Lua `skills.*` constants).
- `ItemCategory` (enum, 9 values, Constants.cs): Bag categories (the Lua `items.*` constants).
- `NpcRole` (enum, 9 values, Constants.cs): NPC roles learned from menus (the Lua `npc.*` constants).
- `LuaConstants` (class, 9 members, Constants.cs): Constant tables pushed into each module table at VM creation.
- `SelfModule` (class, 30 members, Modules.cs): Lua module `self` (30 functions, API_SPEC 2).
- `EntsModule` (class, 26 members, Modules.cs): Lua module `ents` (26 functions, API_SPEC 2).
- `ThreatModule` (class, 13 members, Modules.cs): Lua module `threat` (13 functions, API_SPEC 2).
- `CombatModule` (class, 13 members, Modules.cs): Lua module `combat` (13 functions, API_SPEC 2).
- `SkillsModule` (class, 21 members, Modules.cs): Lua module `skills` (21 functions, API_SPEC 2).
- `ActModule` (class, 26 members, Modules.cs): Lua module `act` (26 functions, API_SPEC 2).
- `ItemsModule` (class, 13 members, Modules.cs): Lua module `items` (13 functions, API_SPEC 2).
- `QuestsModule` (class, 16 members, Modules.cs): Lua module `quests` (16 functions, API_SPEC 2).
- `NavModule` (class, 15 members, Modules.cs): Lua module `nav` (15 functions, API_SPEC 2).
- `NpcModule` (class, 11 members, Modules.cs): Lua module `npc` (11 functions, API_SPEC 2).
- `SocialModule` (class, 14 members, Modules.cs): Lua module `social` (14 functions, API_SPEC 2).
- `KnowModule` (class, 9 members, Modules.cs): Lua module `know` (9 functions, API_SPEC 2).
- `TimeModule` (class, 4 members, Modules.cs): Lua module `time` (4 functions, API_SPEC 2).
- `EvModule` (class, 4 members, Modules.cs): Lua module `ev` (4 functions, API_SPEC 2).
- `WorldModule` (class, 1 members, Modules.cs): Lua module `world` (1 functions, API_SPEC 2).
- `DbgModule` (class, 6 members, Modules.cs): Lua module `dbg` (6 functions, API_SPEC 2).

## Fiesta.Bot.Brain

- `BrainDefinition` (record, Model.cs): A brain definition (API_SPEC 5.2), parsed from brain.yaml / JSON.
- `LayerDef` (record, 6 members, Model.cs): One layer: an independent hierarchical machine with a priority and rate.
- `LayerRate` (record struct, 3 members, Model.cs): Layer tick rate: every tick, or every N ms.
- `MachineDef` (record, 4 members, Model.cs): A machine: initial state (or per-class/level selection) and states.
- `MachineSelect` (record, 3 members, Model.cs): Choose the initial state or machine by class family, level band or map.
- `SelectBy` (enum, 3 values, Model.cs): Selection key.
- `StateDef` (record, Model.cs): A state.
- `TransitionDef` (record, 6 members, Model.cs): A transition with exactly one condition (possibly composite).
- `ConditionKind` (enum, 13 values, Model.cs): Condition kinds (API_SPEC 5.3).
- `CompareOp` (enum, 6 values, Model.cs): Comparison operators for value conditions.
- `ConditionDef` (record, Model.cs): A condition. Fields used depend on <see cref="Kind"/>.
- `BrainLoader` (class, 3 members, Model.cs): Parses brain.yaml / JSON into the model and resolves `use` references.
- `LintSeverity` (enum, 3 values, Model.cs): Lint severity.
- `LintResult` (record, 7 members, Model.cs): One lint finding.
- `BrainLinter` (class, 1 members, Model.cs): Validates a brain and its modules (API_SPEC 5.4, 6).
- `LuaRef` (record struct, 2 members, Runtime.cs): A compiled Lua function reference inside one VM (registry index).
- `LuaCallStatus` (enum, 4 values, Runtime.cs): Outcome of one protected Lua call.
- `LuaCallResult` (record struct, 4 members, Runtime.cs): Result of a protected call: status, truthiness of the first result, error text.
- `ILuaEngine` (interface, 6 members, Runtime.cs): One Lua VM for one bot (native Lua 5.4 or the MoonSharp compatibility engine).
- `NativeLuaEngine` (class, 1 members, Runtime.cs): Native Lua 5.4.9 via KeraLua with function-pointer bindings (docs/LUA_BENCH.md).
- `MoonSharpCompatEngine` (class, 1 members, Runtime.cs): MoonSharp engine for the API-2 compatibility shim during migration.
- `ModuleResolver` (class, 2 members, Runtime.cs): Resolves `require` paths to the module versions pinned by a brain version.
- `BlackboardScope` (enum, 3 values, Runtime.cs): Blackboard scopes (Lua tables bb.bot / bb.layer / bb.state).
- `Blackboard` (class, 5 members, Runtime.cs): Owns the three blackboard tables in the VM; snapshots and restores them.
- `TransitionRecord` (record, 7 members, Runtime.cs): One recorded transition.
- `ConditionTrace` (record, 6 members, Runtime.cs): Evaluation of one outgoing transition in the last tick (debugger condition panel).
- `LayerTrace` (record, 6 members, Runtime.cs): Live state of one layer for the debugger.
- `DebugFrame` (record, 6 members, Runtime.cs): Everything the debugger shows for one bot at one moment (API_SPEC 7.1).
- `ActionTrace` (record, 8 members, Runtime.cs): Action row for the debugger.
- `ProcedureTrace` (record, 3 members, Runtime.cs): Procedure row for the debugger.
- `TickSummary` (record, 5 members, Runtime.cs): Tick summary for the debugger.
- `ErrorTrace` (record, 4 members, Runtime.cs): A recorded Lua or runtime error.
- `DebugTap` (class, 7 members, Runtime.cs): Collects transitions, condition traces, watches and errors; publishes DebugFrames (5 Hz + on transition).
- `StateRuntime` (class, 5 members, Runtime.cs): Runtime of one state (enter/tick/exit refs, progress tracking).
- `MachineRuntime` (class, 5 members, Runtime.cs): Runtime of a machine: active state, transitions, hold/cooldown timers.
- `LayerRuntime` (class, 5 members, Runtime.cs): Runtime of a layer: its machine, rate gating, claims.
- `ConditionEvaluator` (class, 3 members, Runtime.cs): Evaluates conditions: fixed kinds in C#, Lua leaves through the engine, composites with short-circuit.
- `SensorRegistry` (class, 4 members, Runtime.cs): Zero-argument numeric readings usable in `value:` conditions and `progress:` (self.hpPct, threat.onUs...).
- `BrainInstance` (class, 8 members, Runtime.cs): One bot's brain: engine, layers, blackboard, debug tap. Implements the agent's tick step 6.
- `CompiledBrain` (record, 5 members, Runtime.cs): A brain version compiled and validated (definition + module sources + manifest version).
- `BrainCheckpoint` (record, 4 members, Runtime.cs): Saved brain state for resume after relog/redeploy.
- `BrainCompiler` (class, 2 members, Runtime.cs): Compiles a brain version: parse, resolve modules, lint, hash.

## Fiesta.Bot.CodeAnalysis

- `LuaBindingGenerator` (class, 1 members, CodeAnalysis.cs): Emits a static [UnmanagedCallersOnly] lua_CFunction per [LuaFn] method (argument reads, result pushes, tuple -> multiple returns, LuaIter -> cached iterator closure, pending error instead of lua_error) plus the API manifest JSON.
- `TickBlockingAnalyzer` (class, 1 members, CodeAnalysis.cs): FBOT001: blocking or time-reading calls in tick-path assemblies (Task.Wait, .Result, GetAwaiter().GetResult(), Thread.Sleep, await, DateTime.UtcNow) in Fiesta.Bot.Agent / Api / Brain.
- `HotPathAllocationAnalyzer` (class, 1 members, CodeAnalysis.cs): FBOT002: per-tick allocation patterns (LINQ, capturing lambdas, params arrays, boxing) in methods marked [HotPath]; a warning, per the allocation guideline.

## Fiesta.Bot.Fleet

- `SupervisorState` (enum, 9 values, Fleet.cs): Lifecycle states of a supervised bot.
- `BotRecord` (record, 6 members, Fleet.cs): Persisted configuration of one bot.
- `BotSummary` (record, 7 members, Fleet.cs): Summary row for the fleet page.
- `IBotSupervisor` (interface, 11 members, Fleet.cs): Owns every bot's lifecycle: spawn, login, handoff, reconnect, checkpoint/resume, stop. The only writer of bot lifecycle state; operator commands reach the agent through its control queue.
- `BotSupervisor` (class, 1 members, Fleet.cs): Default supervisor.
- `TickScheduler` (class, 5 members, Fleet.cs): K pinned tick workers; each owns a fixed partition of agents and ticks them at their rate.
- `BrainVersionInfo` (record, 7 members, Fleet.cs): Information about one brain version.
- `ChannelInfo` (record, 3 members, Fleet.cs): A named pointer to a brain version.
- `AssignmentRecord` (record, 7 members, Fleet.cs): One apply of a brain version to a bot.
- `BrainDiff` (record, 5 members, Fleet.cs): Diff between two brain versions.
- `IBrainRepository` (interface, 10 members, Fleet.cs): Content-addressed store of brain and library versions, channels and assignments; mirrored to the private git repo.
- `IFleetStore` (interface, 3 members, Fleet.cs): Persistent fleet configuration (Postgres or SQLite).
- `ICheckpointStore` (interface, 2 members, Fleet.cs): Brain checkpoints for resume.
- `HealthKind` (enum, 7 values, Fleet.cs): Health problems the monitor raises (REDESIGN 10).
- `HealthAlert` (record, 6 members, Fleet.cs): One alert.
- `HealthMonitor` (class, 2 members, Fleet.cs): Watches agents and brains for the health rules; alerts go to the log (CRIT), the UI and metrics.
- `SessionRecorder` (class, 4 members, Fleet.cs): Records a bot's inbound events, control commands, clock and job results to a .botrec file.
- `SessionReplayer` (class, 2 members, Fleet.cs): Replays a .botrec through an agent with a recorded clock; reports intents and transitions for comparison.
- `ReplayResult` (record, 5 members, Fleet.cs): What a replay produced.

## Fiesta.Bot.Net

- `LinkKind` (enum, 3 values, Links.cs): Which server a link talks to.
- `LinkState` (enum, 6 values, Links.cs): Connection state of a link.
- `Endpoint` (record struct, 2 members, Links.cs): A server endpoint (host, port).
- `RawFrame` (struct, 4 members, Links.cs): One decrypted, framed packet as read from the wire. The payload is a pooled buffer slice owned by the receiver until <see cref="Release"/>.
- `IFrameSink` (interface, 2 members, Links.cs): Sink for frames read by a link's receive loop (runs on an I/O thread, must not block).
- `IOutboundSource` (interface, 2 members, Links.cs): Source of outbound frames the link's writer loop drains (the tick enqueues, the writer sends).
- `ILink` (interface, 2 members, Links.cs): A transport link: socket + cipher + framing, with a receive loop and a writer loop that never involve the tick thread.
- `IStreamCipher` (interface, 2 members, Links.cs): Stream cipher state for one direction (the asymmetric XOR table, BYO).
- `PipeLink` (class, 1 members, Links.cs): Socket implementation of <see cref="ILink"/> on System.IO.Pipelines.
- `FrameCodec` (class, 2 members, Links.cs): Frame codec: length prefix, opcode, payload (the existing FiestaPacket framing).
- `LoginResult` (record, 6 members, Links.cs): Result of the Login -> WorldManager -> Zone chain.
- `ILoginChain` (interface, 1 members, Links.cs): The typed login chain (port of the current Login/), run on I/O threads.
- `LoginRequest` (record, 6 members, Links.cs): Login parameters.
- `IHandoffCoordinator` (interface, 1 members, Links.cs): Moves a bot between zone links (LINKSAME / LINKOTHER / cross-server) without involving the tick.

## Fiesta.Bot.Protocol

- `InboundKind` (enum, 43 values, Protocol.cs): Kinds of decoded inbound events applied to the world state.
- `InboundEvent` (struct, 8 members, Protocol.cs): One decoded inbound event (64 bytes, stored in rings). Variable data lives in a pooled buffer.
- `Intent` (struct, 3 members, Protocol.cs): One outbound request produced by the tick (opcode + inline payload or pooled bytes).
- `IPacketDecoder` (interface, 1 members, Protocol.cs): Decodes the frames of one packet family into <see cref="InboundEvent"/>s.
- `IEventSink` (interface, 1 members, Protocol.cs): Receiver for decoded events (the bot's inbound ring).
- `DecoderTable` (class, 3 members, Protocol.cs): Opcode -> decoder table, built once at startup.
- `IntentEncoder` (class, 13 members, Protocol.cs): Encodes typed requests into intents without allocation (pooled buffers).
- `EntityDecoder` (class, Protocol.cs): Packet family decoders (each a small pure class; split out of today's ZoneView).
- `CombatDecoder` (class, Protocol.cs): Combat: damage, cast start/result/fail, bash stop.
- `SelfDecoder` (class, Protocol.cs): Self: HP/SP, stats, level, exp, money, speed, abstates, mode.
- `MovementDecoder` (class, Protocol.cs): Movement: moves, stops, MOVEFAIL, speed.
- `ItemDecoder` (class, Protocol.cs): Items: cell changes, use results, buy/sell acks, stone reserves.
- `QuestMenuDecoder` (class, Protocol.cs): Quests, dialogue pages, NPC menus, shop lists.
- `SocialDecoder` (class, Protocol.cs): Party, trade, chat, whisper.
- `WorldDecoder` (class, Protocol.cs): Map links, revive, dead menu, doors, tent.
- `IPacketLog` (interface, 1 members, Protocol.cs): Writes every frame, both directions, to a rolling per-bot file (survives relogs).

## Fiesta.Bot.Server

- `SpawnRequest` (record, 7 members, Program.cs): Spawn request body.
- `ForceStateRequest` (record, 2 members, Program.cs): Force-state body.
- `BlackboardEdit` (record, 4 members, Program.cs): Blackboard edit body.
- `OperatorActionRequest` (record, 3 members, Program.cs): Operator action body.
- `CreateVersionRequest` (record, 4 members, Program.cs): Create-version body.
- `ApplyBrainRequest` (record, 4 members, Program.cs): Apply-brain body.
- `FleetEndpoints` (class, 1 members, Program.cs): Fleet control endpoints (API_SPEC 8.1).
- `BrainEndpoints` (class, 1 members, Program.cs): Brain/library/version endpoints (API_SPEC 8.2).
- `ObservationEndpoints` (class, 1 members, Program.cs): Observation endpoints (API_SPEC 8.3).
- `WsTopic` (enum, 6 values, Program.cs): WebSocket topics (API_SPEC 8.4).
- `WsSubscription` (record, 4 members, Program.cs): One subscription from a client.
- `WebSocketHub` (class, 1 members, Program.cs): The /ws hub: subscriptions, delta encoding (RFC 6902 patches), fan-out off the tick threads.
- `SensorFrameBuilder` (class, 1 members, Program.cs): Builds the sensor view frame for a bot (API_SPEC 7.2).

## Fiesta.Bot.Services

- `IClock` (interface, Services.cs): Monotonic clock; the tick reads it once (replayable).
- `SystemClock` (class, Services.cs): Real clock.
- `MobInfoRow` (record, 8 members, Services.cs): Client MobInfo row (the fields the runtime uses).
- `ActiveSkillRow` (record, 8 members, Services.cs): Client ActiveSkill row (the fields the runtime uses).
- `ItemInfoRow` (record, 8 members, Services.cs): Client ItemInfo row.
- `MapInfoRow` (record, 4 members, Services.cs): Client MapInfo row.
- `QuestDef` (record, 7 members, Services.cs): A quest definition parsed from QuestData.
- `QuestObjectiveDef` (record, 4 members, Services.cs): One quest objective.
- `QuestObjectiveKind` (enum, 5 values, Services.cs): Objective kinds.
- `IClientData` (interface, 7 members, Services.cs): Read-only client data (ressystem SHN), loaded once and shared.
- `BlockLayer` (class, 7 members, Services.cs): Walkability for one map: .shbd OR .shab, with door overlays; immutable once built.
- `NavOverlay` (class, 4 members, Services.cs): Per-bot changes on top of the shared block layer: learned blocks with TTL, observed doors.
- `NavMesh` (class, 3 members, Services.cs): Rectangle/portal split of a block layer (kept from today's NavMesh).
- `PathStatus` (enum, 3 values, Services.cs): Outcome of a path search.
- `PathResult` (record, 5 members, Services.cs): A finished path (pooled waypoint array, world units).
- `PathSearch` (class, 3 members, Services.cs): Corridor A* over regions + funnel + corner nudge + goal snapping (ported from fiesta-godot).
- `INavService` (interface, 4 members, Services.cs): Shared navigation per map plus the job entry points.
- `JobKind` (enum, 6 values, Services.cs): Job kinds that run off the tick.
- `JobTicket` (record struct, 1 members, Services.cs): Handle to a job.
- `IJobScheduler` (interface, 3 members, Services.cs): Bounded worker pool for off-tick work; completions are posted to the owning bot's inbound lane.
- `Estimate` (record, 4 members, Services.cs): An interval estimate learned from observations.
- `MobFact` (enum, 8 values, Services.cs): What a mob estimator tracks.
- `IKnowledgeStore` (interface, 7 members, Services.cs): Host-wide learned knowledge (server-only facts estimated from the wire), persisted and shared.

## Fiesta.Bot.Next.Tests

- `GameRulesTests` (class, 4 members, PlannedTests.cs): GameRules against client data and the live probes' recorded answers (REVIEW section 5).
- `BrainTests` (class, 5 members, PlannedTests.cs): Brain format, linter and runtime semantics (API_SPEC 5).
- `ReplayTests` (class, 2 members, PlannedTests.cs): Deterministic replay of recorded sessions, including the 15-day tent loop fixture.
- `NavTests` (class, 2 members, PlannedTests.cs): Navigation port (corridor A*, funnel, nudge, goal snap).
