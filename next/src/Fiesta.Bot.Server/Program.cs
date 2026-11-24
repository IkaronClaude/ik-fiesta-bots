using Fiesta.Bot.Server;

// Host for the redesigned runtime (replaces Fiesta.Bot.Host after migration; API_SPEC section 8).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapFleetEndpoints();
app.MapBrainEndpoints();
app.MapObservationEndpoints();
app.MapWebSocketHub();
app.Run();

namespace Fiesta.Bot.Server
{
    /// <summary>Spawn request body.</summary>
    public sealed record SpawnRequest(string Id, string Host, int LoginPort, string Username, string Password, string Character,
        bool Create = false, string? Class = null, string? Brain = null, string? Channel = null, bool Autostart = true);

    /// <summary>Force-state body.</summary>
    public sealed record ForceStateRequest(string Layer, string State);

    /// <summary>Blackboard edit body.</summary>
    public sealed record BlackboardEdit(string Scope, string Key, string Value, string? Owner = null);

    /// <summary>Operator action body.</summary>
    public sealed record OperatorActionRequest(string Kind, IReadOnlyDictionary<string, string> Args);

    /// <summary>Create-version body.</summary>
    public sealed record CreateVersionRequest(string Definition, IReadOnlyDictionary<string, string> Modules, string Message);

    /// <summary>Apply-brain body.</summary>
    public sealed record ApplyBrainRequest(string Name, string? Hash, string? Channel, bool KeepState = true);

    /// <summary>Fleet control endpoints (API_SPEC 8.1).</summary>
    public static class FleetEndpoints
    {
        /// <summary>Map /api/v3/bots/*.</summary>
        public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder app) => app;
    }

    /// <summary>Brain/library/version endpoints (API_SPEC 8.2).</summary>
    public static class BrainEndpoints
    {
        /// <summary>Map /api/v3/brains/*, /libraries/*, /api-manifest, /bots/{id}/brain*.</summary>
        public static IEndpointRouteBuilder MapBrainEndpoints(this IEndpointRouteBuilder app) => app;
    }

    /// <summary>Observation endpoints (API_SPEC 8.3).</summary>
    public static class ObservationEndpoints
    {
        /// <summary>Map /api/v3/bots/{id}/debug, transitions, tick, sensors, log, recordings, knowledge, metrics.</summary>
        public static IEndpointRouteBuilder MapObservationEndpoints(this IEndpointRouteBuilder app) => app;
    }

    /// <summary>WebSocket topics (API_SPEC 8.4).</summary>
    public enum WsTopic : byte { Fleet, Debug, Transition, Sensors, Log, Alert }

    /// <summary>One subscription from a client.</summary>
    public sealed record WsSubscription(WsTopic Topic, string? Bot, int Hz, string? Level);

    /// <summary>The /ws hub: subscriptions, delta encoding (RFC 6902 patches), fan-out off the tick threads.</summary>
    public static class WebSocketHub
    {
        /// <summary>Map /ws.</summary>
        public static IEndpointRouteBuilder MapWebSocketHub(this IEndpointRouteBuilder app) => app;
    }

    /// <summary>Builds the sensor view frame for a bot (API_SPEC 7.2).</summary>
    public static class SensorFrameBuilder
    {
        /// <summary>Build a frame from an agent snapshot and its world (called on the publisher, never on the tick).</summary>
        public static object Build(Fiesta.Bot.Agent.BotAgent agent) => throw new NotImplementedException();
    }
}
