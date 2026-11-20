using System.Text.Json;

namespace Perga.Kernel.Ports;

// Values persisted in track_wakes.kind; 0014_track_wakes.sql constrains this set.
public static class TrackWakeKinds
{
    public const string Mail = "mail";
    public const string Schedule = "schedule";
    public const string TaskSettled = "task_settled";
    public const string AskAnswered = "ask_answered";
    public const string ReportEdited = "report_edited";
    public const string Boot = "boot";

    // Critical wakes survive runtime failures and boot recovery; the rest may be dropped.
    public static bool IsCritical(string kind) => kind is Mail or TaskSettled or AskAnswered;
}

// Values persisted in track_wakes.state; 0014_track_wakes.sql constrains this set.
public static class TrackWakeStates
{
    public const string Pending = "pending";
    public const string Delivering = "delivering";
    public const string Done = "done";
    public const string Dropped = "dropped";
}

// Values persisted in track_wakes.reason.
public static class TrackWakeReasons
{
    public const string Coalesced = "coalesced";
    public const string Canceled = "canceled";
    public const string Delivered = "delivered";
    public const string ConclusiveError = "conclusive_error";
    public const string Interrupted = "interrupted";
    public const string RuntimeFailed = "runtime_failed";
    public const string TrackClosed = "track_closed";
    public const string TemplateChanged = "template_changed";
    public const string BootSuperseded = "boot_superseded";
    public const string BootDropped = "boot_dropped";
    public const string BootRequeued = "boot_requeued";
    public const string MaintenanceExpired = "maintenance_expired";
    public const string UpgradeRetry = "upgrade_retry";
}

// Presentation hints carried in the wake payload and copied onto the user item.
public static class TrackWakePresentations
{
    public const string System = "system";
    public const string SystemMail = "system_mail";
    public const string SystemTaskCompleted = "system_task_completed";
    public const string SystemTaskFailed = "system_task_failed";
    public const string SystemReportEdited = "system_report_edited";
}

// The fields a wake payload may carry, parsed once from track_wakes.payload_json.
public sealed record TrackWakePayload(string? Prompt, string? Presentation, string? ScheduleId, string? SourceKind = null)
{
    public static TrackWakePayload Parse(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        return new(ReadString(root, "prompt"), ReadString(root, "presentation"), ReadString(root, "scheduleId"), ReadString(root, "sourceKind"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
