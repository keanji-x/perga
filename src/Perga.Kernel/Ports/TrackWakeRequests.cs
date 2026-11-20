using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Perga.Contracts;

namespace Perga.Kernel.Ports;

public static class TrackWakeRequests
{
    public const string BootPrompt = "System boot reconciliation completed: previous in-flight session turns and task attempts were marked interrupted and temporary workspace leases were discarded. Review track status and decide next steps.";
    public const string MailKeyPrefix = "mail:";
    public const string TrackScheduleKeyPrefix = "track_schedule:";
    public const string SystemUpgradeMailIdPrefix = "system-upgrade:";
    public const string SystemUpgradeMailKeyPrefix = MailKeyPrefix + SystemUpgradeMailIdPrefix;

    public static string MailKey(string mailId) => MailKeyPrefix + mailId;
    public static string ScheduleKey(string scheduleId) => "schedule:" + scheduleId;
    public static string TrackScheduleKey(string scheduleId) =>
        TrackScheduleKeyPrefix + scheduleId;
    public static bool IsTrackScheduleWake(TrackWake wake) =>
        wake.Kind == TrackWakeKinds.Schedule &&
        wake.CoalesceKey?.StartsWith(TrackScheduleKeyPrefix, StringComparison.Ordinal) == true;
    public static string SystemUpgradeMailId(string jobId) => SystemUpgradeMailIdPrefix + jobId;
    public static bool IsSystemUpgradeMail(TrackWake wake) => TryGetSystemUpgradeMailId(wake, out _);

    // Upgrade notifications are mails keyed "mail:system-upgrade:<job>"; the mail id drops the "mail:" prefix.
    public static bool TryGetSystemUpgradeMailId(TrackWake wake, [NotNullWhen(true)] out string? mailId)
    {
        mailId = wake.CoalesceKey?.StartsWith(SystemUpgradeMailKeyPrefix, StringComparison.Ordinal) == true
            ? wake.CoalesceKey[MailKeyPrefix.Length..]
            : null;
        return mailId is not null;
    }

    public static TrackWakeRequest Prompt(string trackId, string kind, string? key, string prompt,
        string presentation = TrackWakePresentations.System) =>
        new(trackId, kind, key, JsonSerializer.SerializeToElement(new { prompt, presentation }));
    public static TrackWakeRequest Schedule(string trackId, string scheduleId) =>
        new(trackId, TrackWakeKinds.Schedule, ScheduleKey(scheduleId), JsonSerializer.SerializeToElement(new { scheduleId }));
    public static TrackWakeRequest DynamicSchedule(string trackId, string scheduleId, string prompt) =>
        new(trackId, TrackWakeKinds.Schedule, TrackScheduleKey(scheduleId),
            JsonSerializer.SerializeToElement(new { scheduleId, prompt, presentation = TrackWakePresentations.System }),
            TargetSessionId: null);
    public static TrackWakeRequest Mail(MailDto mail) => Prompt(mail.ToTrackId, TrackWakeKinds.Mail, MailKey(mail.Id),
        $"New cross-track mail '{mail.Id}' from track '{mail.FromTrackId}': {mail.Summary}\n\n{mail.Body}", TrackWakePresentations.SystemMail);
    public static TrackWakeRequest TaskSettled(TaskDto task, TaskAttemptDto attempt, string? prompt = null) => Prompt(task.TrackId,
        TrackWakeKinds.TaskSettled, $"task:{task.Id}:{attempt.AttemptId}",
        prompt ?? $"Task '{task.TaskKey}' (attempt '{attempt.AttemptId}') settled as {task.Status}. {task.StatusDetail}",
        task.Status == TaskRunStatus.Done ? TrackWakePresentations.SystemTaskCompleted : TrackWakePresentations.SystemTaskFailed);
    public static TrackWakeRequest Ask(AskDto ask) => Prompt(ask.TrackId, TrackWakeKinds.AskAnswered, "ask:" + ask.Id,
        $"User answered ask '{ask.Id}': {ask.Answer?.GetRawText()}");
    public static TrackWakeRequest Report(string trackId, int docRev, JsonElement changes)
    {
        var subject = "report";
        if (changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty("changes", out var entries) &&
            entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == 1 && entries[0].TryGetProperty("id", out var id))
            subject = $"report block '{id.GetString()}'";
        return Prompt(trackId, TrackWakeKinds.ReportEdited, "report_edited",
            $"Human updated {subject} (docRev={docRev}). Review the latest report changes.", TrackWakePresentations.SystemReportEdited);
    }
    public static TrackWakeRequest Boot(string trackId) => Prompt(trackId, TrackWakeKinds.Boot, "boot_reconcile", BootPrompt);
}
