using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.TermClient;

namespace Perga.Kernel.Mcp;

public static class KernelMcpToolNames
{
    public const string SystemUpgradeCheck = "perga_system_upgrade_check";
    public const string SystemUpgradeApply = "perga_system_upgrade_apply";
    public const string SystemUpgradeRollback = "perga_system_upgrade_rollback";

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
    public const string ScheduleLs = "perga_schedule_ls";

    // Governance tools (BoundAttemptId == null ONLY)
    public const string TaskCancel = "perga_task_cancel";
    public const string TaskAccept = "perga_task_accept";
    public const string TaskReject = "perga_task_reject";
    public const string MailSend = "perga_mail_send";
    public const string TrackRename = "perga_track_rename";
    public const string TemplateLs = "perga_template_ls";
    public const string TemplateCat = "perga_template_cat";
    public const string TemplateUpdateInputs = "perga_template_update_inputs";
    public const string TrackSet = "perga_track_set";
    public const string TrackClose = "perga_track_close";
    public const string ScheduleSet = "perga_schedule_set";
    public const string ScheduleRm = "perga_schedule_rm";

    // Contract completion tools (BoundAttemptId != null ONLY)
    public const string TaskDone = "perga_task_done";
    public const string TaskFail = "perga_task_fail";
    public const string SessionRespond = "perga_session_respond";
    public const string SessionRead = "perga_session_read";
    public const string SessionSend = "perga_session_send";
    public const string SessionWithdraw = "perga_session_withdraw";
    public const string SessionList = "perga_session_list";
    public const string SessionCreate = "perga_session_create";
    public const string SessionUpdate = "perga_session_update";
    public const string SessionControl = "perga_session_control";

    public static readonly IReadOnlyList<string> All =
    [
        SystemUpgradeCheck, SystemUpgradeApply, SystemUpgradeRollback,
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
        ScheduleLs,
        TaskCancel,
        TaskAccept,
        TaskReject,
        MailSend,
        TrackRename,
        TrackClose,
        ScheduleSet,
        ScheduleRm,
        TemplateLs,
        TemplateCat,
        TemplateUpdateInputs,
        TrackSet,
        TaskDone,
        TaskFail,
        SessionRespond,
        SessionRead,
        SessionSend,
        SessionWithdraw,
        SessionList,
        SessionCreate,
        SessionUpdate,
        SessionControl
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
            .Where(d => !IsLeadOnly(d.Name) || (!isBound && isLead))
            .ToArray();
    }

    public static bool IsGovernanceOnly(string toolName) => toolName is
        KernelMcpToolNames.TaskCancel or KernelMcpToolNames.TaskAccept or KernelMcpToolNames.TaskReject or
        KernelMcpToolNames.SessionCreate or KernelMcpToolNames.SessionUpdate or KernelMcpToolNames.SessionSend or
        KernelMcpToolNames.SessionWithdraw or KernelMcpToolNames.SessionControl or KernelMcpToolNames.SessionRespond or
        KernelMcpToolNames.MailSend or
        KernelMcpToolNames.TrackRename or
        KernelMcpToolNames.TrackClose or
        KernelMcpToolNames.TemplateLs or
        KernelMcpToolNames.TemplateCat or
        KernelMcpToolNames.TemplateUpdateInputs or
        KernelMcpToolNames.TrackSet or
        KernelMcpToolNames.ScheduleSet or
        KernelMcpToolNames.ScheduleRm;

    public static bool IsCompletionOnly(string toolName) => toolName is
        KernelMcpToolNames.TaskDone or
        KernelMcpToolNames.TaskFail;

    public static bool IsLeadOnly(string toolName) => toolName is
        KernelMcpToolNames.SystemUpgradeCheck or KernelMcpToolNames.SystemUpgradeApply or KernelMcpToolNames.SystemUpgradeRollback;

    private static IReadOnlyList<McpToolDescriptor> BuildDescriptors()
    {
        static JsonElement Schema(string json) => JsonHelpers.ParseClone(json);

        return
        [
            new(KernelMcpToolNames.SystemUpgradeCheck, "Check an installation-owner configured upgrade source (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sourceId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.SystemUpgradeApply, "Schedule a detached upgrade from a configured source ID and return immediately (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"required":["sourceId"],"properties":{"sourceId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.SystemUpgradeRollback, "Schedule detached rollback to the previous release (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"properties":{}}""")),
            new(KernelMcpToolNames.ReportRead, "Read the current track report document, block revisions, and read anchor. Supports optional format ('full'|'blocks'|'markdown'|'outline'), sectionH1/blockIds scoping, and includeContract for perga-block authoring specs.",
                Schema(KernelMcpReportSupport.ReportReadSchemaJson)),
            new(KernelMcpToolNames.ReportCommit, "Atomically apply block operations (replace_section, upsert, delete, move) to the track report using a ReadAnchor. Returns a compact commit receipt by default; set includeDocument=true for full renderedMarkdown and blocks.",
                Schema(KernelMcpReportSupport.ReportCommitSchemaJson)),
            new(KernelMcpToolNames.TaskLs, "List all tasks declared in the track.",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.UserAsk, "Suspend and ask the human user one or more structured multiple-choice questions.",
                Schema(KernelMcpReportSupport.UserAskSchemaJson)),
            new(KernelMcpToolNames.MailLs, "List cross-track mails delivered to this track.",
                Schema("""{"type":"object","properties":{"unreadOnly":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.MailCat, "Read a specific mail by ID and mark it as read.",
                Schema("""{"type":"object","required":["mailId"],"properties":{"mailId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TrackStatus, "Inspect track status, active sessions, tasks, open asks, terminals, and unread mail count.",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.TerminalOpen, "Open or reuse an idempotent PTY terminal in the workspace.",
                Schema("""{"type":"object","properties":{"title":{"type":"string"},"program":{"type":"string"},"args":{"type":"array","items":{"type":"string"}},"cwd":{"type":"string"},"idempotencyKey":{"type":"string"},"cols":{"type":"integer"},"rows":{"type":"integer"}}}""")),
            new(KernelMcpToolNames.TerminalRead, "Observe terminal screen frame and optional wait condition.",
                Schema(KernelMcpReportSupport.TerminalReadSchemaJson)),
            new(KernelMcpToolNames.TerminalInput, "Send text, keys, paste, or submit action to a terminal.",
                Schema(KernelMcpReportSupport.TerminalInputSchemaJson)),
            new(KernelMcpToolNames.ScheduleLs, "List durable schedules and reminders configured on this track.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"includeTerminal":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.ScheduleSet, "Create or update a persistent one-shot reminder or recurring cron schedule that wakes this track (governance only).",
                Schema("""{"type":"object","additionalProperties":false,"required":["title","prompt"],"properties":{"id":{"type":"string"},"title":{"type":"string"},"prompt":{"type":"string"},"runAt":{"type":"string"},"runAtMs":{"type":"integer"},"cron":{"type":"string"},"timezone":{"type":"string"},"maxOccurrences":{"type":"integer","minimum":1}}}""")),
            new(KernelMcpToolNames.ScheduleRm, "Cancel an active schedule on this track and drop any pending wake for it (governance only).",
                Schema("""{"type":"object","additionalProperties":false,"required":["scheduleId"],"properties":{"scheduleId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskCancel, "Cancel a task and discard any active attempt lease (task parent or Track Lead).",
                Schema("""{"type":"object","required":["taskKey"],"properties":{"taskKey":{"type":"string"},"reason":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskAccept, "Record semantic acceptance verdict on a task (task parent or Track Lead).",
                Schema("""{"type":"object","required":["taskKey"],"properties":{"taskKey":{"type":"string"},"note":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TaskReject, "Reject a task with review feedback and optionally re-queue (task parent or Track Lead).",
                Schema("""{"type":"object","required":["taskKey","note"],"properties":{"taskKey":{"type":"string"},"note":{"type":"string"},"reQueue":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.MailSend, "Wake the recipient Lead by specifying exactly one of toTrackId or replyToMailId (a message in your inbox). Governance only.",
                Schema("""{"type":"object","required":["summary","body"],"properties":{"replyToMailId":{"type":"string"},"toTrackId":{"type":"string"},"summary":{"type":"string"},"body":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TrackRename, "Rename the current track (governance only).",
                Schema("""{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TemplateLs, "List concise template summaries (governance only).",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.TemplateCat, "Inspect a template manifest and source; use __current__ for the active snapshot, inputs, state and headerView; omit templateId or use __contract__ for the authoring contract (governance only).",
                Schema("""{"type":"object","properties":{"templateId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.TemplateUpdateInputs, "Merge inputs into the active template, preserving state (governance only).",
                Schema("""{"type":"object","required":["inputs"],"properties":{"inputs":{"type":"object"}}}""")),
            new(KernelMcpToolNames.TrackSet, "Apply a templateId or JSON/.perga.js source with inputs. Use dryRun to preview or clear to detach while keeping the report (governance only).",
                Schema("""{"type":"object","properties":{"templateId":{"type":"string","minLength":1},"source":{"type":"string","minLength":1},"inputs":{"type":"object"},"dryRun":{"type":"boolean"},"clear":{"type":"boolean"}}}""")),
            new(KernelMcpToolNames.TrackClose, "Close the current track and cancel active attempts (governance only).",
                Schema("""{"type":"object","properties":{}}""")),
            new(KernelMcpToolNames.TaskDone, "Submit completed work for the bound task attempt and trigger gate verification (contract subagent only).",
                Schema("""{"type":"object","properties":{"result":{}}}""")),
            new(KernelMcpToolNames.TaskFail, "Report failure for the bound task attempt and discard lease (contract subagent only).",
                Schema("""{"type":"object","required":["reason"],"properties":{"reason":{"type":"string"},"result":{}}}""")),
            new(KernelMcpToolNames.SessionList, "List conversations in this Track.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"includeArchived":{"type":"boolean"},"parentSessionId":{"type":"string"}}}""")),
            new(KernelMcpToolNames.SessionCreate, "Create a child conversation with an optional task contract. Reuse requestKey to retry safely.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"title":{"type":"string"},"provider":{"type":"string"},"model":{"type":"string"},"reasoningEffort":{"type":"string"},"permissionMode":{"type":"string"},"role":{"type":"string"},"systemPrompt":{"type":"string"},"prompt":{"type":"string"},"requestKey":{"type":"string"},"notifyParent":{"enum":["none","turn_end","task_settled"]},"task":{"type":"object","additionalProperties":false,"properties":{"access":{"enum":["read_only","read_write"]},"acceptance":{"type":"string"},"gate":{"type":"string"},"dependsOn":{"type":"array","items":{"type":"string"}}}}}}""")),
            new(KernelMcpToolNames.SessionRead, "Read conversation state, history, progress and pending interactions.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"beforeId":{"type":"integer"},"afterId":{"type":"integer"},"limit":{"type":"integer","minimum":1,"maximum":100},"activityTurnId":{"type":"string"},"afterSequence":{"type":"integer","minimum":0},"waitMs":{"type":"integer","minimum":0,"maximum":10000}},"required":["sessionId"]}""")),
            new(KernelMcpToolNames.SessionUpdate, "Update a child conversation. Omit fields to preserve them; null restores defaults. Configuration changes require expectedConfigVersion.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"expectedConfigVersion":{"type":"integer"},"title":{"type":"string"},"archived":{"type":"boolean"},"provider":{"type":"string"},"model":{"type":["string","null"]},"reasoningEffort":{"type":["string","null"]},"permissionMode":{"type":["string","null"]}},"required":["sessionId"]}""")),
            new(KernelMcpToolNames.SessionSend, "Send content with requestKey, or steer a queued messageId into turnId. Default requires idle; queue waits; steer targets a running turn.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"expectedAttemptId":{"type":"string"},"content":{"type":"string"},"requestKey":{"type":"string"},"messageId":{"type":"string"},"turnId":{"type":"string"},"mode":{"enum":["default","queue","steer"]}},"required":["sessionId"]}""")),
            new(KernelMcpToolNames.SessionWithdraw, "Withdraw a queued or unresolved conversation message.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"expectedAttemptId":{"type":"string"},"messageId":{"type":"string"}},"required":["sessionId","messageId"]}""")),
            new(KernelMcpToolNames.SessionControl, "Stop a specific turn, reconnect an unknown session, reset context, or compact an idle conversation.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"expectedAttemptId":{"type":"string"},"action":{"enum":["stop","reconnect","reset_context","compact"]},"turnId":{"type":"string"},"expectedEpoch":{"type":"integer"}},"required":["sessionId","action"]}""")),
            new(KernelMcpToolNames.SessionRespond, "Answer a pending ask with answers, or an approval with executionId and optionId.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sessionId":{"type":"string"},"interactionId":{"type":"string"},"executionId":{"type":"string"},"optionId":{"type":"string"},"answers":{}},"required":["sessionId","interactionId"]}"""))
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
        if (toolName is KernelMcpToolNames.SystemUpgradeCheck or KernelMcpToolNames.SystemUpgradeApply or KernelMcpToolNames.SystemUpgradeRollback)
        {
            return await kernel.InvokeSystemUpgradeAsync(caller,
                toolName == KernelMcpToolNames.SystemUpgradeCheck ? "check" : toolName == KernelMcpToolNames.SystemUpgradeApply ? "apply" : "rollback", args, ct).ConfigureAwait(false);
        }
        var origin = KernelMcpCatalog.IsCompletionOnly(toolName) && caller.BoundAttemptId is not null
            ? await kernel.Actors.GetOrActivate(caller.TrackId).CaptureWorkerCompletionOriginAsync(caller, ct).ConfigureAwait(false)
            : null;
        var expectedPolicyGeneration = await kernel.GuardMcpToolCallAsync(caller.TrackId, caller.SessionId, "perga", toolName, args, ct, caller.ExecutionOrigin)
            .ConfigureAwait(false);

        var result = await ExecuteAuthorizedAsync(kernel, caller, toolName, args, expectedPolicyGeneration, origin, ct).ConfigureAwait(false);
        if (!(toolName == KernelMcpToolNames.TrackSet && args.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True))
            await kernel.Actors.GetOrActivate(caller.TrackId).AfterPolicyEventAsync("tool." + toolName,
                JsonHelpers.ToElement(new { arguments = args, result }), ct).ConfigureAwait(false);
        return result;
    }

    private static async Task<JsonElement> ExecuteAuthorizedAsync(IPergaKernel kernel, McpSessionPrincipal caller,
        string toolName, JsonElement args, string expectedPolicyGeneration, WorkerCompletionOrigin? origin, CancellationToken ct)
    {
        switch (toolName)
        {
            case KernelMcpToolNames.ReportRead:
            {
                var res = await kernel.ReadReportAsync(caller.TrackId, caller.SessionId, ct).ConfigureAwait(false);
                return KernelMcpReportSupport.FormatReportReadResponse(res, args);
            }

            case KernelMcpToolNames.ReportCommit:
            {
                var commitReq = ParseReportCommitArgs(args);
                var includeDocument = KernelMcpReportSupport.ParseIncludeDocumentArg(args);
                var res = await kernel.CommitReportAsync(caller.TrackId, caller.SessionId, commitReq, ct, caller.ExecutionOrigin)
                    .ConfigureAwait(false);
                return KernelMcpReportSupport.FormatReportCommitResponse(res, includeDocument);
            }

            case KernelMcpToolNames.TaskLs:
            {
                var tasks = await kernel.ListTasksAsync(caller.TrackId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(tasks);
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

            case KernelMcpToolNames.SessionList:
                return JsonHelpers.ToElement(await kernel.ListSessionsAsync(caller.TrackId,
                    ParseSessionArgs<SessionListFilter>(args, ["includeArchived", "parentSessionId"]), Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionCreate:
            {
                var request = ParseSessionArgs<CreateSessionRequest>(args,
                    ["title", "provider", "model", "reasoningEffort", "permissionMode", "role", "systemPrompt", "prompt", "notifyParent", "task", "requestKey"]);
                if (args.TryGetProperty("task", out var task) && task.ValueKind != JsonValueKind.Null)
                    RejectUnknownArgs(task, ["access", "acceptance", "gate", "dependsOn"]);
                var created = await kernel.CreateSessionAsync(caller.TrackId, request,
                    OptionalTypedStringArg(args, ["requestKey"]) ?? Guid.NewGuid().ToString("N"), Principal(caller), ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(created.Receipt);
            }

            case KernelMcpToolNames.SessionRead:
            {
                RejectUnknownArgs(args, ["sessionId", "beforeId", "afterId", "limit", "activityTurnId", "afterSequence", "waitMs"]);
                var query = new SessionReadQuery(IntegerArg(args, "afterId", 0, long.MaxValue) ?? 0,
                    IntegerArg(args, "beforeId", 0, long.MaxValue), (int)(IntegerArg(args, "limit", 1, 100) ?? 20),
                    !args.TryGetProperty("afterId", out _), OptionalTypedStringArg(args, ["activityTurnId"]),
                    IntegerArg(args, "afterSequence", 0, long.MaxValue), (int)(IntegerArg(args, "waitMs", 0, 10000) ?? 0));
                return JsonHelpers.ToElement(await kernel.GetSessionItemsAsync(RequireStringArg(args, ["sessionId"]),
                    query, Principal(caller), ct).ConfigureAwait(false));
            }

            case KernelMcpToolNames.SessionUpdate:
                return JsonHelpers.ToElement(await kernel.UpdateSessionAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<UpdateSessionRequest>(args, ["sessionId", "expectedConfigVersion", "title", "archived", "provider", "model", "reasoningEffort", "permissionMode"]),
                    Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionSend:
                return JsonHelpers.ToElement(await kernel.SendSessionMessageAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<SendSessionMessageRequest>(args, ["sessionId", "content", "requestKey", "messageId", "turnId", "mode", "expectedAttemptId"]),
                    Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionWithdraw:
                RejectUnknownArgs(args, ["sessionId", "messageId", "expectedAttemptId"]);
                await kernel.WithdrawSessionMessageAsync(RequireStringArg(args, ["sessionId"]), RequireStringArg(args, ["messageId"]),
                    OptionalTypedStringArg(args, ["expectedAttemptId"]), Principal(caller), ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { ok = true });

            case KernelMcpToolNames.SessionControl:
                return JsonHelpers.ToElement(await kernel.ControlSessionAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<SessionControlRequest>(args, ["sessionId", "action", "turnId", "expectedEpoch", "expectedAttemptId"]),
                    Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionRespond:
                return JsonHelpers.ToElement(await kernel.RespondInteractionAsync(RequireStringArg(args, ["sessionId"]),
                    RequireStringArg(args, ["interactionId"]), ParseSessionArgs<RespondInteractionRequest>(args,
                        ["sessionId", "interactionId", "executionId", "optionId", "answers"]), Principal(caller), ct).ConfigureAwait(false));

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

            case KernelMcpToolNames.TemplateLs:
                return JsonHelpers.ToElement(kernel.ListTemplates().Select(m => new { id = m.Id, title = m.Title,
                    description = m.Description, inputKeys = m.InputsSchema.EnumerateObject().Select(p => p.Name).ToArray(),
                    plugins = m.DefaultPlugins, hasSchedules = m.Schedules.Count > 0, hasHeaderView = m.HasHeaderView }));

            case KernelMcpToolNames.TemplateCat:
                return TemplateStringArg(args, "templateId") == "__current__"
                    ? await kernel.InspectCurrentTemplateAsync(caller.TrackId, caller.SessionId, ct).ConfigureAwait(false)
                    : kernel.InspectTemplate(TemplateStringArg(args, "templateId"));

            case KernelMcpToolNames.TemplateUpdateInputs:
                return JsonHelpers.ToElement(await kernel.UpdateTemplateInputsAsync(caller.TrackId, caller.SessionId,
                    args.GetProperty("inputs"), expectedPolicyGeneration, ct).ConfigureAwait(false));

            case KernelMcpToolNames.TrackSet:
            {
                var templateId = TemplateStringArg(args, "templateId");
                var source = TemplateStringArg(args, "source");
                JsonElement? inputs = JsonHelpers.TryGetPropertyIgnoreCase(args, ["inputs"], out var value) ? value.Clone() : null;
                var result = await kernel.SetTrackTemplateWithOptionsAsync(caller.TrackId, caller.SessionId,
                    templateId, source, expectedPolicyGeneration, inputs,
                    args.TryGetProperty("dryRun", out var dry) && dry.GetBoolean(),
                    args.TryGetProperty("clear", out var clear) && clear.GetBoolean(), ct).ConfigureAwait(false);
                var response = System.Text.Json.Nodes.JsonNode.Parse(JsonHelpers.ToElement(result.Track).GetRawText())!.AsObject();
                response["dryRun"] = result.DryRun;
                response["warnings"] = System.Text.Json.Nodes.JsonNode.Parse(JsonHelpers.ToElement(result.Warnings).GetRawText());
                return JsonHelpers.ParseClone(response.ToJsonString());
            }

            case KernelMcpToolNames.TrackClose:
            {
                var res = await kernel.CloseTrackAsync(caller.TrackId, caller.SessionId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.ScheduleLs:
            {
                RejectUnknownArgs(args, ["includeTerminal"]);
                var includeTerminal = false;
                if (JsonHelpers.TryGetPropertyIgnoreCase(args, ["includeTerminal"], out var inc) &&
                    inc.ValueKind != JsonValueKind.Null)
                {
                    if (inc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw KernelException.InvalidArgument("'includeTerminal' must be a boolean.");
                    includeTerminal = inc.GetBoolean();
                }

                var schedules = await kernel.ListTrackSchedulesAsync(caller.TrackId, includeTerminal, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { schedules });
            }

            case KernelMcpToolNames.ScheduleSet:
            {
                var req = ParseScheduleSetArgs(args);
                var schedule = await kernel.SetTrackScheduleAsync(caller.TrackId, req, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(schedule);
            }

            case KernelMcpToolNames.ScheduleRm:
            {
                RejectUnknownArgs(args, ["scheduleId"]);
                var scheduleId = RequireStringArg(args, ["scheduleId"]);
                var canceled = await kernel.CancelTrackScheduleAsync(caller.TrackId, scheduleId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { scheduleId, canceled });
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

    private static InteropPrincipal Principal(McpSessionPrincipal caller) => new InteropPrincipal.Session(
        caller.TrackId, caller.SessionId, caller.IsLead, caller.BoundAttemptId, 0, caller.Token, caller.ExecutionOrigin);

    private static T ParseSessionArgs<T>(JsonElement args, string[] keys)
    {
        RejectUnknownArgs(args, keys);
        try { return JsonHelpers.Deserialize<T>(args) ?? throw KernelException.InvalidArgument("Tool arguments are required."); }
        catch (JsonException error) { throw KernelException.InvalidArgument(error.Message); }
    }

    private static long? IntegerArg(JsonElement args, string name, long min, long max)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < min || number > max)
            throw KernelException.InvalidArgument($"'{name}' must be an integer between {min} and {max}.");
        return number;
    }

    private static void RejectUnknownArgs(JsonElement args, ReadOnlySpan<string> allowedKeys)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Tool arguments must be a JSON object.");
        foreach (var prop in args.EnumerateObject())
        {
            var matched = false;
            foreach (var allowed in allowedKeys)
            {
                if (string.Equals(prop.Name, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                throw KernelException.InvalidArgument(
                    $"Unknown argument '{prop.Name}'. Allowed arguments: {string.Join(", ", allowedKeys.ToArray())}.");
        }
    }

    private static string? OptionalTypedStringArg(JsonElement args, ReadOnlySpan<string> keys)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, keys, out var elem) || elem.ValueKind == JsonValueKind.Null)
            return null;
        if (elem.ValueKind != JsonValueKind.String)
            throw KernelException.InvalidArgument($"'{keys[0]}' must be a string.");
        return elem.GetString();
    }

    private static SetTrackScheduleRequest ParseScheduleSetArgs(JsonElement args)
    {
        RejectUnknownArgs(args, ["id", "title", "prompt", "runAt", "runAtMs", "cron", "timezone", "maxOccurrences"]);
        var id = OptionalTypedStringArg(args, ["id"]);
        var title = RequireStringArg(args, ["title"]);
        var prompt = RequireStringArg(args, ["prompt"]);
        var cron = OptionalTypedStringArg(args, ["cron"]);
        var timezone = OptionalTypedStringArg(args, ["timezone"]);
        var runAt = OptionalTypedStringArg(args, ["runAt"]);

        long? runAtMs = null;
        if (JsonHelpers.TryGetPropertyIgnoreCase(args, ["runAtMs"], out var msElem) &&
            msElem.ValueKind != JsonValueKind.Null)
        {
            if (msElem.ValueKind != JsonValueKind.Number || !msElem.TryGetInt64(out var parsedMs))
                throw KernelException.InvalidArgument("'runAtMs' must be an integer Unix millisecond timestamp.");
            runAtMs = parsedMs;
        }

        int? maxOccurrences = null;
        if (JsonHelpers.TryGetPropertyIgnoreCase(args, ["maxOccurrences"], out var maxElem) &&
            maxElem.ValueKind != JsonValueKind.Null)
        {
            if (maxElem.ValueKind != JsonValueKind.Number || !maxElem.TryGetInt32(out var parsedMax) || parsedMax < 1)
                throw KernelException.InvalidArgument("'maxOccurrences' must be an integer >= 1.");
            maxOccurrences = parsedMax;
        }

        return new SetTrackScheduleRequest(
            Title: title,
            Prompt: prompt,
            Id: id,
            CronExpr: cron,
            RunAtMs: runAtMs,
            Timezone: timezone,
            MaxOccurrences: maxOccurrences,
            SourceKind: TrackScheduleSources.Agent,
            RunAt: runAt);
    }

    private static ReportCommitRequest ParseReportCommitArgs(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["anchor"], out var anchorElem) ||
            anchorElem.ValueKind != JsonValueKind.Object)
        {
            throw KernelException.InvalidArgument(
                "Missing 'anchor' object. Pass the ReadAnchor returned by perga_report_read, e.g. {\"docRev\":0,\"knownBlockRevs\":{\"b_1234\":1},\"wholeDocumentRead\":true}.");
        }

        var anchor = JsonHelpers.Deserialize<ReadAnchor>(anchorElem)
                     ?? throw KernelException.InvalidArgument(
                         "Invalid 'anchor' object. Expected {\"docRev\":0,\"knownBlockRevs\":{\"b_1234\":1},\"wholeDocumentRead\":true}.");

        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["ops"], out var opsElem) ||
            opsElem.ValueKind != JsonValueKind.Array)
        {
            throw KernelException.InvalidArgument(
                "Missing 'ops' array. Example: [{\"op\":\"replace_section\",\"sectionH1\":\"Overview\",\"markdown\":\"...\"}].");
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
                throw KernelException.InvalidArgument(
                    $"Unsupported BlockOp type '{type}'. Supported ops: replace_section ({{op,sectionH1,markdown}}), upsert ({{op,id?,blockKind?,markdown?,payload?,position?}}), delete ({{op,id}}), move ({{op,id,toIndex}}).");
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

    private static string? TemplateStringArg(JsonElement args, string key)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, [key], out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw KernelException.InvalidArgument($"'{key}' must be a non-empty string.");
        return value.GetString();
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
