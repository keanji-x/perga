using System.Text.Json;
using System.Threading.Channels;
using Perga.Contracts;

namespace Perga.Kernel.Events;

public static class KernelEventKinds
{
    // Persistent events (EventId != null)
    public const string AreaUpdated = "area.updated";
    public const string AreaDeleted = "area.deleted";
    public const string TrackUpdated = "track.updated";
    public const string TrackDeleted = "track.deleted";
    public const string ReportUpdated = "report.updated";
    public const string TaskUpdated = "task.updated";
    public const string SessionUpdated = "session.updated";
    public const string TurnCompleted = "turn.completed";
    public const string AskChanged = "ask.changed";
    public const string MailReceived = "mail.received";
    public const string TerminalChanged = "terminal.changed";

    // Transient streaming frames (EventId == null, never written to SQLite)
    public const string SessionTextDelta = "session.text_delta";
    public const string SessionThinkingDelta = "session.thinking_delta";
    public const string SessionToolProgress = "session.tool_progress";

    private static readonly HashSet<string> TransientKinds = new(StringComparer.Ordinal)
    {
        SessionTextDelta,
        SessionThinkingDelta,
        SessionToolProgress
    };

    private static readonly HashSet<string> PersistentKinds = new(StringComparer.Ordinal)
    {
        AreaUpdated,
        AreaDeleted,
        TrackUpdated,
        TrackDeleted,
        ReportUpdated,
        TaskUpdated,
        SessionUpdated,
        TurnCompleted,
        AskChanged,
        MailReceived,
        TerminalChanged
    };

    public static bool IsTransient(string kind) => TransientKinds.Contains(kind);
    public static bool IsPersistent(string kind) => PersistentKinds.Contains(kind);
}

public interface IKernelEventSubscription : IAsyncDisposable, IDisposable
{
    string? TrackIdFilter { get; }
    ChannelReader<EventEnvelopeDto> Reader { get; }
    IAsyncEnumerable<EventEnvelopeDto> ReadAllAsync(CancellationToken ct = default);
}

public interface IKernelEventBus
{
    IKernelEventSubscription Subscribe(string? trackId = null);
    EventEnvelopeDto PublishPersistent(EventEnvelopeDto envelope);
    EventEnvelopeDto PublishTransient(string? areaId, string? trackId, string kind, JsonElement payload, long createdAtMs);
}
