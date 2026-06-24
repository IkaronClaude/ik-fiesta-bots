namespace Fiesta.Bot;

/// <summary>Verbosity of a bot log line. Quieter = more important. The tail/snapshot
/// endpoints filter on this so a tailer can ask for headline-only or the full firehose.
/// Lives in the root namespace so every layer (Session/Manager/Host) can tag its logs.
/// <list type="bullet">
/// <item><see cref="Note"/> — headline events: quest accept/finish, level-up, death,
///   purchase, skill learned, errors. The "what happened" you read at a glance.</item>
/// <item><see cref="Info"/> — progress: each kill, quest objective credit, restock /
///   travel decisions, phase changes.</item>
/// <item><see cref="Verbose"/> — per-tick / per-frame spam: movement, skill casts,
///   auto-attacks, mob-appeared perception, the state dump. Only when chasing a bug.</item>
/// </list></summary>
public enum BotLogLevel { Note = 0, Info = 1, Verbose = 2 }
