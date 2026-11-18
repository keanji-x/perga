using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.TermClient;

namespace Perga.Kernel.Mcp;

public static class KernelMcpToolNames
{
    // Shared across all sessions (Lead, free Subagent, contract Subagent)
    public const string ReportRead = "perga_report_read";
    public const string ReportCommit = "perga_report_commit";
    public const string TaskLs = "perga_task_ls";
    public const string UserAsk = "perga_user_ask";
    public const string MailLs = "perga_mail_ls";
    public const string MailCat = "perga_mail_cat";
    public const string TrackStatus = "perga_track_status";
    public const string TerminalOpen = "perga_terminal_open";
    public const string TerminalRead = "perga_terminal_read";
    public const string TerminalInput = "perga_terminal_input";

    // Governance tools (BoundAttemptId == null ONLY)
    public const string TaskCancel = "perga_task_cancel";
    public const string TaskAccept = "perga_task_accept";
    public const string TaskReject = "perga_task_reject";
    public const string MailSend = "perga_mail_send";
    public const string TrackRename = "perga_track_rename";
    public const string TrackClose = "perga_track_close";

    // Contract completion tools (BoundAttemptId != null ONLY)
    public const string TaskDone = "perga_task_done";
    public const string TaskFail = "perga_task_fail";
    public const string SessionRead = "perga_session_read";
    public const string SessionSend = "perga_session_send";
    public const string SessionInterrupt = "perga_session_interrupt";
    public const string SessionUpdateSettings = "perga_session_update_settings";
    public const string TaskDispatch = "perga_task_dispatch";

    public static readonly IReadOnlyList<string> All =
    [
        ReportRead,
        ReportCommit,
        TaskLs,
        UserAsk,
        MailLs,
        MailCat,
        TrackStatus,
        TerminalOpen,
        TerminalRead,
        TerminalInput,
        TaskCancel,
        TaskAccept,
        TaskReject,
        MailSend,
        TrackRename,
        TrackClose,
        TaskDone,
        TaskFail,
        SessionRead,
        SessionSend,
        SessionInterrupt,
        SessionUpdateSettings,
        TaskDispatch
    ];
}

public sealed record McpToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema);

public static class KernelMcpCatalog
{
    private static readonly IReadOnlyList<McpToolDescriptor> AllDescriptors = BuildDescriptors();

    public static IReadOnlyList<McpToolDescriptor> GetAllTools() => AllDescriptors;

    public static IReadOnlyList<McpToolDescriptor> GetToolsForSession(string? boundAttemptId, bool isLead = false)
    {
        var isBound = !string.IsNullOrEmpty(boundAttemptId);
        return AllDescriptors
            .Where(d => isBound ? !IsGovernanceOnly(d.Name) : !IsCompletionOnly(d.Name))
            .Where(d => !IsWorkerOperatorOnly(d.Name) || (!isBound && isLead))
            .ToArray();
    }

    public static bool IsGovernanceOnly(string toolName) => toolName is
        KernelMcpToolNames.TaskCancel or
        KernelMcpToolNames.TaskAccept or
        KernelMcpToolNames.TaskReject or
        KernelMcpToolNames.MailSend or
        KernelMcpToolNames.TrackRename or
        KernelMcpToolNames.TrackClose;

    public static bool IsCompletionOnly(string toolName) => toolName is
        KernelMcpToolNames.TaskDone or
        KernelMcpToolNames.TaskFail;

    public static bool IsWorkerOperatorOnly(string toolName) => toolName is
        KernelMcpToolNames.SessionRead or KernelMcpToolNames.SessionSend or KernelMcpToolNames.SessionInterrupt or
        KernelMcpToolNames.SessionUpdateSettings or KernelMcpToolNames.TaskDispatch;

    private static IReadOnlyList<McpToolDescriptor> BuildDescriptors()
    {
        static JsonElement Schema(string json) => JsonHelpers.ParseClone(json);

        return
        [
            new(KernelMcpToolNames.ReportRead, "Read the current track report document, block revisions, and read anchor.",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.ReportCommit, "Atomically apply block operations to the track report using a ReadAnchor.",
                Schema("""{"type":"object","required":["anchor","ops"],"properties":{"anchor":{"type":"object"},"ops":{"type":"array"},"summary":{"type":"string"},"commitMessage":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskLs, "List all tasks declared in the track.",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.UserAsk, "Suspend and ask the human user one or more structured multiple-choice questions.",
                Schema("""{"type":"object","required":["questions"],"properties":{"questions":{"type":"array"}}}""")),
            new(KernelMcpToolNames.MailLs, "List cross-track mails delivered to this track.",
                Schema("""{"type":"object","properties":{"unreadOnly":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.MailCat, "Read a specific mail by ID and mark it as read.",
                Schema("""{"type":"object","required":["mailId"],"properties":{"mailId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TrackStatus, "Inspect track status, active sessions, tasks, open asks, terminals, and unread mail count.",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.TerminalOpen, "Open or reuse an idempotent PTY terminal in the workspace.",
                Schema("""{"type":"object","properties":{"title":{"type":"string"},"program":{"type":"string"},"args":{"type":"array","items":{"type":"string"}},"cwd":{"type":"string"},"idempotencyKey":{"type":"string"},"cols":{"type":"integer"},"rows":{"type":"integer"}}}""")),
            new(KernelMcpToolNames.TerminalRead, "Observe terminal screen frame and optional wait condition.",
                Schema("""{"type":"object","required":["terminalId"],"properties":{"terminalId":{"type":"string"},"scrollOffset":{"type":"integer"},"waitFor":{"type":"string"},"waitMs":{"type":"integer"},"settleMs":{"type":"integer"}}}""")),
            new(KernelMcpToolNames.TerminalInput, "Send text, keys, paste, or submit action to a terminal.",
                Schema("""{"type":"object","required":["terminalId","kind","payload"],"properties":{"terminalId":{"type":"string"},"kind":{"type":"string"},"payload":{}}}""")),
            new(KernelMcpToolNames.TaskCancel, "Cancel a task and discard any active attempt lease (governance only).",
                Schema("""{"type":"object","required":["taskKey"],"properties":{"taskKey":{"type":"string"},"reason":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskAccept, "Record semantic acceptance verdict on a task (governance only).",
                Schema("""{"type":"object","required":["taskKey"],"properties":{"taskKey":{"type":"string"},"note":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskReject, "Reject a task with review feedback and optionally re-queue (governance only).",
                Schema("""{"type":"object","required":["taskKey","note"],"properties":{"taskKey":{"type":"string"},"note":{"type":"string"},"reQueue":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.MailSend, "Wake the recipient Lead by specifying exactly one of toTrackId or replyToMailId (a message in your inbox). Governance only.",
                Schema("""{"type":"object","required":["summary","body"],"properties":{"replyToMailId":{"type":"string"},"toTrackId":{"type":"string"},"summary":{"type":"string"},"body":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TrackRename, "Rename the current track (governance only).",
                Schema("""{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TrackClose, "Close the current track and cancel active attempts (governance only).",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.TaskDone, "Submit completed work for the bound task attempt and trigger gate verification (contract subagent only).",
                Schema("""{"type":"object","properties":{"result":{}}}""")),
            new(KernelMcpToolNames.TaskFail, "Report failure for the bound task attempt and discard lease (contract subagent only).",
                Schema("""{"type":"object","required":["reason"],"properties":{"reason":{"type":"string"},"result":{}}}""")),
            new(KernelMcpToolNames.SessionRead, "Read recent Worker messages, current turn progress, and open approvals. Find the session through perga_track_status.",
                Schema("""{"type":"object","required":["sessionId"],"properties":{"sessionId":{"type":"string"},"beforeId":{"type":"integer"},"limit":{"type":"integer","minimum":1,"maximum":100},"activityTurnId":{"type":"string"},"afterSequence":{"type":"integer","minimum":0},"waitMs":{"type":"integer","minimum":0,"maximum":10000}}}""")),
            new(KernelMcpToolNames.SessionSend, "Send to an idle or interrupted Worker, or steer its exact current turn. Set allowQueue to queue on providers without native steer. Reuse requestKey to retry.",
                Schema("""{"type":"object","required":["sessionId","attemptId","content","requestKey"],"properties":{"sessionId":{"type":"string"},"attemptId":{"type":"string"},"turnId":{"type":"string"},"content":{"type":"string"},"requestKey":{"type":"string"},"allowQueue":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.SessionInterrupt, "Interrupt the specified Worker turn and keep its Attempt available for continuation.",
                Schema("""{"type":"object","required":["sessionId","attemptId","turnId"],"properties":{"sessionId":{"type":"string"},"attemptId":{"type":"string"},"turnId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.SessionUpdateSettings, "Set a Worker's configuration for the next turn. Read its latest settings and capability first.",
                Schema("""{"type":"object","required":["sessionId","attemptId","version","model","reasoningEffort","approvalMode"],"properties":{"sessionId":{"type":"string"},"attemptId":{"type":"string"},"version":{"type":"integer","minimum":0},"model":{"type":["string","null"]},"reasoningEffort":{"type":["string","null"]},"approvalMode":{"enum":["ask","deny"]}}}""")),
            new(KernelMcpToolNames.TaskDispatch, "Dispatch a Worker on the current Track. Only the goal is required. Reuse an optional requestKey to retry safely.",
                Schema("""{"type":"object","required":["goal"],"properties":{"goal":{"type":"string"},"requestKey":{"type":"string"},"access":{"enum":["read_only","read_write"]},"acceptance":{"type":"string"},"dependsOn":{"type":"array","items":{"type":"string"}},"gate":{"type":"string"},"provider":{"type":"string"},"model":{"type":"string"}}}"""))
        ];
    }
}

public static class KernelMcpDispatcher
{
    public static async Task<JsonElement> ExecuteGuardedAsync(
        IPergaKernel kernel,
        McpSessionPrincipal caller,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        var args = arguments.ValueKind == JsonValueKind.Undefined ? JsonHelpers.EmptyObject : arguments;
        var origin = KernelMcpCatalog.IsCompletionOnly(toolName) && caller.BoundAttemptId is not null
            ? await kernel.Actors.GetOrActivate(caller.TrackId).CaptureWorkerCompletionOriginAsync(caller, ct).ConfigureAwait(false)
            : null;
        await kernel.GuardMcpToolCallAsync(caller.TrackId, caller.SessionId, "perga", toolName, args, ct)
            .ConfigureAwait(false);

        switch (toolName)
        {
            case KernelMcpToolNames.ReportRead:
            {
                var res = await kernel.ReadReportAsync(caller.TrackId, caller.SessionId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.ReportCommit:
            {
                var commitReq = ParseReportCommitArgs(args);
                var res = await kernel.CommitReportAsync(caller.TrackId, caller.SessionId, commitReq, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskLs:
            {
                var tasks = await kernel.ListTasksAsync(caller.TrackId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(tasks);
            }

            case KernelMcpToolNames.TaskDispatch:
            {
                var request = JsonHelpers.Deserialize<DispatchWorkerRequest>(args)
                    ?? throw KernelException.InvalidArgument("Worker dispatch requires a goal.");
                var receipt = await kernel.DispatchWorkerAsync(caller.TrackId, request, OptionalStringArg(args, ["requestKey"]), caller, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(receipt);
            }

            case KernelMcpToolNames.UserAsk:
            {
                if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["questions"], out var qElem) ||
                    qElem.ValueKind != JsonValueKind.Array)
                {
                    throw KernelException.InvalidArgument("Missing 'questions' array.");
                }

                var questions = JsonHelpers.Deserialize<List<AskQuestionDto>>(qElem) ?? [];
                return await kernel.AskUserAsync(caller.TrackId, caller.SessionId, questions, ct).ConfigureAwait(false);
            }

            case KernelMcpToolNames.MailLs:
            {
                var unreadOnly = JsonHelpers.TryGetPropertyIgnoreCase(args, ["unreadOnly", "unread_only"], out var u) &&
                                 u.ValueKind == JsonValueKind.True;
                var mails = await kernel.ListMailsAsync(caller.TrackId, unreadOnly, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(mails);
            }

            case KernelMcpToolNames.MailCat:
            {
                var mailId = RequireStringArg(args, ["mailId", "mail_id", "id"]);
                var mail = await kernel.ReadMailAsync(caller.TrackId, mailId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(mail);
            }

            case KernelMcpToolNames.TrackStatus:
            {
                var status = await kernel.GetTrackStatusAsync(caller.TrackId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(status);
            }

            case KernelMcpToolNames.SessionRead:
            {
                var sessionId = RequireStringArg(args, ["sessionId"]);
                var limit = (int)Math.Clamp(OptionalInt64Arg(args, "limit") ?? 20, 1, 100);
                var page = await kernel.GetSessionItemsAsync(sessionId, limit: limit, ct: ct, latest: true,
                    beforeId: OptionalInt64Arg(args, "beforeId"), caller: caller,
                    activityTurnId: OptionalStringArg(args, ["activityTurnId"]), afterSequence: OptionalInt64Arg(args, "afterSequence"),
                    waitMs: (int)Math.Clamp(OptionalInt64Arg(args, "waitMs") ?? 0, 0, 10_000))
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(page);
            }

            case KernelMcpToolNames.SessionSend:
            {
                var sessionId = RequireStringArg(args, ["sessionId"]);
                var request = new WorkerMessageRequest(RequireStringArg(args, ["attemptId"]), RequireStringArg(args, ["content"]),
                    RequireStringArg(args, ["requestKey"]), OptionalStringArg(args, ["turnId"]),
                    JsonHelpers.TryGetPropertyIgnoreCase(args, ["allowQueue"], out var allowQueue) && allowQueue.ValueKind == JsonValueKind.True);
                return JsonHelpers.ToElement(await kernel.SendWorkerMessageAsync(sessionId, request, caller, ct).ConfigureAwait(false));
            }

            case KernelMcpToolNames.SessionInterrupt:
                await kernel.InterruptWorkerAsync(RequireStringArg(args, ["sessionId"]), RequireStringArg(args, ["attemptId"]),
                    RequireStringArg(args, ["turnId"]), caller, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { ok = true });

            case KernelMcpToolNames.SessionUpdateSettings:
            {
                var sessionId = RequireStringArg(args, ["sessionId"]);
                if (OptionalInt64Arg(args, "version") is not (>= 0 and <= int.MaxValue) ||
                    !JsonHelpers.TryGetPropertyIgnoreCase(args, ["model"], out _) ||
                    !JsonHelpers.TryGetPropertyIgnoreCase(args, ["reasoningEffort"], out _))
                    throw KernelException.InvalidArgument("version, model and reasoningEffort are required.");
                var request = JsonHelpers.Deserialize<UpdateWorkerSettingsRequest>(args)!;
                return JsonHelpers.ToElement(await kernel.UpdateWorkerSettingsAsync(sessionId, request, caller, ct).ConfigureAwait(false));
            }

            case KernelMcpToolNames.TerminalOpen:
            {
                var openReq = JsonHelpers.Deserialize<OpenTerminalRequest>(args) ?? new OpenTerminalRequest();
                var term = await kernel.OpenTerminalAsync(caller.TrackId, caller.SessionId, openReq, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(term);
            }

            case KernelMcpToolNames.TerminalRead:
            {
                var observeReq = ParseTermObserveArgs(args);
                var res = await kernel.ObserveTerminalAsync(caller.TrackId, observeReq, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TerminalInput:
            {
                var termId = RequireStringArg(args, ["terminalId", "terminal_id"]);
                var kindStr = RequireStringArg(args, ["kind"]);
                if (!Enum.TryParse<TermActionKind>(kindStr, ignoreCase: true, out var kind))
                {
                    throw KernelException.InvalidArgument($"Invalid terminal action kind '{kindStr}'.");
                }

                var payload = JsonHelpers.TryGetPropertyIgnoreCase(args, ["payload"], out var p)
                    ? p.Clone()
                    : JsonHelpers.EmptyObject;
                var res = await kernel.SendTerminalInputAsync(
                    caller.TrackId,
                    new TermActionRequest(termId, kind, payload),
                    ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskCancel:
            {
                var taskKey = RequireStringArg(args, ["taskKey", "task_key", "taskId"]);
                var reason = OptionalStringArg(args, ["reason"]);
                var res = await kernel.CancelTaskAsync(caller.TrackId, caller.SessionId, taskKey, reason, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskAccept:
            {
                var taskKey = RequireStringArg(args, ["taskKey", "task_key", "taskId"]);
                var note = OptionalStringArg(args, ["note"]);
                var res = await kernel.AcceptTaskAsync(caller.TrackId, caller.SessionId, taskKey, note, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskReject:
            {
                var taskKey = RequireStringArg(args, ["taskKey", "task_key", "taskId"]);
                var note = RequireStringArg(args, ["note", "reason"]);
                var reQueue = JsonHelpers.TryGetPropertyIgnoreCase(args, ["reQueue", "re_queue"], out var rq) &&
                              rq.ValueKind == JsonValueKind.True;
                var res = await kernel.RejectTaskAsync(caller.TrackId, caller.SessionId, taskKey, note, reQueue, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.MailSend:
            {
                var toTrackId = MailAddressArg(args, ["toTrackId", "to_track_id"]);
                var replyToMailId = MailAddressArg(args, ["replyToMailId", "reply_to_mail_id"]);
                var summary = RequireStringArg(args, ["summary"]);
                var body = RequireStringArg(args, ["body"]);
                var res = await kernel.SendMailAsync(caller.TrackId, caller.SessionId, toTrackId, summary, body, ct, replyToMailId)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TrackRename:
            {
                var title = RequireStringArg(args, ["title"]);
                var res = await kernel.RenameTrackAsync(caller.TrackId, caller.SessionId, title, ct)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TrackClose:
            {
                var res = await kernel.CloseTrackAsync(caller.TrackId, caller.SessionId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskDone:
            {
                JsonElement? workerResult = JsonHelpers.TryGetPropertyIgnoreCase(args, ["result", "workerResult", "worker_result"], out var wr)
                    ? wr.Clone()
                    : null;
                var res = await kernel.CompleteAttemptDoneAsync(caller.TrackId, caller.SessionId, workerResult, ct, origin)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskFail:
            {
                var reason = RequireStringArg(args, ["reason", "error"]);
                JsonElement? workerResult = JsonHelpers.TryGetPropertyIgnoreCase(args, ["result", "workerResult", "worker_result"], out var wr)
                    ? wr.Clone()
                    : null;
                var res = await kernel.CompleteAttemptFailAsync(caller.TrackId, caller.SessionId, reason, workerResult, ct, origin)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            default:
                throw KernelException.NotFound($"Unknown kernel MCP tool '{toolName}'.");
        }
    }

    private static long? OptionalInt64Arg(JsonElement args, string name) =>
        JsonHelpers.TryGetPropertyIgnoreCase(args, [name], out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed) ? parsed : null;

    private static ReportCommitRequest ParseReportCommitArgs(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["anchor"], out var anchorElem) ||
            anchorElem.ValueKind != JsonValueKind.Object)
        {
            throw KernelException.InvalidArgument("Missing 'anchor' object.");
        }

        var anchor = JsonHelpers.Deserialize<ReadAnchor>(anchorElem)
                     ?? throw KernelException.InvalidArgument("Invalid 'anchor' object.");

        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["ops"], out var opsElem) ||
            opsElem.ValueKind != JsonValueKind.Array)
        {
            throw KernelException.InvalidArgument("Missing 'ops' array.");
        }

        var ops = new List<BlockOp>();
        foreach (var opElem in opsElem.EnumerateArray())
        {
            ops.Add(ParseBlockOp(opElem));
        }

        var summary = OptionalStringArg(args, ["summary"]);
        var commitMsg = OptionalStringArg(args, ["commitMessage", "commit_message", "message"]);
        return new ReportCommitRequest(anchor, ops, summary, commitMsg);
    }

    private static BlockOp ParseBlockOp(JsonElement opElem)
    {
        var type = RequireStringArg(opElem, ["op", "type", "kind"]);
        var normalized = type.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

        switch (normalized)
        {
            case "replacesection" or "replacesectionop":
                return new BlockOp.ReplaceSectionOp(
                    RequireStringArg(opElem, ["sectionH1", "section_h1", "h1"]),
                    RequireStringArg(opElem, ["markdown"]));

            case "upsert" or "upsertblock" or "upsertblockop":
            {
                var blockKindStr = OptionalStringArg(opElem, ["blockKind", "block_kind"]) ?? "Prose";
                if (!Enum.TryParse<ReportBlockKind>(blockKindStr, ignoreCase: true, out var blockKind) &&
                    JsonHelpers.TryGetPropertyIgnoreCase(opElem, ["kind"], out var kElem) &&
                    kElem.ValueKind == JsonValueKind.String &&
                    !string.Equals(kElem.GetString(), type, StringComparison.OrdinalIgnoreCase))
                {
                    Enum.TryParse(kElem.GetString(), ignoreCase: true, out blockKind);
                }

                int? position = null;
                if (JsonHelpers.TryGetPropertyIgnoreCase(opElem, ["position"], out var posElem) &&
                    posElem.ValueKind == JsonValueKind.Number &&
                    posElem.TryGetInt32(out var posVal))
                {
                    position = posVal;
                }

                JsonElement? payload = null;
                if (JsonHelpers.TryGetPropertyIgnoreCase(opElem, ["payload"], out var payElem) &&
                    payElem.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    payload = payElem.Clone();
                }

                return new BlockOp.UpsertBlockOp(
                    OptionalStringArg(opElem, ["id", "blockId", "block_id"]),
                    blockKind,
                    OptionalStringArg(opElem, ["markdown"]),
                    payload,
                    position);
            }

            case "delete" or "deleteblock" or "deleteblockop":
                return new BlockOp.DeleteBlockOp(RequireStringArg(opElem, ["id", "blockId", "block_id"]));

            case "move" or "moveblock" or "moveblockop":
            {
                if (!JsonHelpers.TryGetPropertyIgnoreCase(opElem, ["toIndex", "to_index"], out var idxElem) ||
                    idxElem.ValueKind != JsonValueKind.Number ||
                    !idxElem.TryGetInt32(out var toIndex))
                {
                    throw KernelException.InvalidArgument("MoveBlockOp requires integer 'toIndex'.");
                }

                return new BlockOp.MoveBlockOp(RequireStringArg(opElem, ["id", "blockId", "block_id"]), toIndex);
            }

            default:
                throw KernelException.InvalidArgument($"Unsupported BlockOp type '{type}'.");
        }
    }

    private static TermObserveRequest ParseTermObserveArgs(JsonElement args)
    {
        var termId = RequireStringArg(args, ["terminalId", "terminal_id"]);
        var scrollOffset = JsonHelpers.TryGetPropertyIgnoreCase(args, ["scrollOffset", "scroll_offset"], out var so) &&
                           so.ValueKind == JsonValueKind.Number && so.TryGetInt32(out var soVal)
            ? soVal
            : 0;
        var waitFor = TermWaitMode.None;
        if (OptionalStringArg(args, ["waitFor", "wait_for"]) is { } wf &&
            Enum.TryParse<TermWaitMode>(wf, ignoreCase: true, out var parsedWf))
        {
            waitFor = parsedWf;
        }

        var waitMs = JsonHelpers.TryGetPropertyIgnoreCase(args, ["waitMs", "wait_ms"], out var wm) &&
                     wm.ValueKind == JsonValueKind.Number && wm.TryGetInt32(out var wmVal)
            ? wmVal
            : 0;
        var settleMs = JsonHelpers.TryGetPropertyIgnoreCase(args, ["settleMs", "settle_ms"], out var sm) &&
                       sm.ValueKind == JsonValueKind.Number && sm.TryGetInt32(out var smVal)
            ? smVal
            : 150;

        return new TermObserveRequest(termId, scrollOffset, waitFor, waitMs, settleMs);
    }

    private static string? MailAddressArg(JsonElement args, ReadOnlySpan<string> keys)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, keys, out var address))
            return null;
        if (address.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(address.GetString()))
            throw KernelException.InvalidArgument($"'{keys[0]}' must be a non-empty mail address.");
        return address.GetString();
    }

    private static string RequireStringArg(JsonElement obj, ReadOnlySpan<string> keys)
    {
        var val = OptionalStringArg(obj, keys);
        if (string.IsNullOrEmpty(val))
        {
            throw KernelException.InvalidArgument($"Required argument '{keys[0]}' was missing or empty.");
        }

        return val;
    }

    private static string? OptionalStringArg(JsonElement obj, ReadOnlySpan<string> keys)
    {
        if (JsonHelpers.TryGetPropertyIgnoreCase(obj, keys, out var elem) && elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }

        return null;
    }
}
