using System.Text.Json;
using System.Threading.Channels;
using Perga.Contracts;

namespace Perga.Kernel.Events;

public static class KernelEventKinds
{
    // Persistent events (EventId != null)
    public const string AreaUpdated = "area.updated";
    public const string AreaDeleted = "area.deleted";
    public const string TrackSchedulesUpdated = "track.schedules.updated";
    public const string TrackUpdated = "track.updated";
    public const string TrackDeleted = "track.deleted";
    public const string ReportUpdated = "report.updated";
    public const string TaskUpdated = "task.updated";
    public const string SessionUpdated = "session.updated";
    public const string TurnCompleted = "turn.completed";
    public const string ApprovalDecided = "approval.decided";
    public const string ApprovalDelivery = "approval.delivery";
    public const string ApprovalsChanged = "approval.changed";
    public const string AskChanged = "ask.changed";
    public const string MailReceived = "mail.received";
    public const string TerminalChanged = "terminal.changed";

    // Transient streaming frames (EventId == null, never written to SQLite)
    public const string SessionStateChanged = "session.state_changed";
    public const string ReadReceiptUpdated = "read_receipt.updated";
    public const string SessionTextDelta = "session.text_delta";
    public const string SessionThinkingDelta = "session.thinking_delta";
    public const string SessionToolProgress = "session.tool_progress";

    private static readonly HashSet<string> TransientKinds = new(StringComparer.Ordinal)
    {
        SessionStateChanged,
        ReadReceiptUpdated,
        ApprovalsChanged,
        SessionTextDelta,
        SessionThinkingDelta,
        SessionToolProgress,
        "system.upgrade_status"
    };

    private static readonly HashSet<string> PersistentKinds = new(StringComparer.Ordinal)
    {
        AreaUpdated,
        AreaDeleted,
        TrackUpdated,
        TrackSchedulesUpdated,
        TrackDeleted,
        ReportUpdated,
        TaskUpdated,
        SessionUpdated,
        TurnCompleted,
        AskChanged,
        ApprovalDecided,
        ApprovalDelivery,
        MailReceived,
        TerminalChanged,
        "system.upgrade_result"
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
