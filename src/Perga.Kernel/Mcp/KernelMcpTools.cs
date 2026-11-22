using System.Text.Json;
using System.Text.Json.Serialization;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.TermClient;

namespace Perga.Kernel.Mcp;

public static class KernelMcpToolNames
{
    public const string SystemUpgradeCheck = "system_upgrade_check";
    public const string SystemUpgradeApply = "system_upgrade_apply";
    public const string SystemUpgradeRollback = "system_upgrade_rollback";

    // Shared across all sessions (Lead, free Subagent, contract Subagent)
    public const string ReportRead = "report_read";
    public const string ReportCommit = "report_commit";
    public const string DataPatch = "data_patch";
    public const string TaskList = "task_list";
    public const string UserAsk = "user_ask";
    public const string MailList = "mail_list";
    public const string MailRead = "mail_read";
    public const string ProviderList = "provider_list";
    public const string TrackStatus = "track_status";
    public const string TerminalOpen = "terminal_open";
    public const string TerminalRead = "terminal_read";
    public const string TerminalInput = "terminal_input";

    // Governance tools (BoundTaskId == null ONLY)
    public const string FlowRead = "flow_read";
    public const string FlowCommit = "flow_commit";
    public const string TaskCancel = "task_cancel";
    public const string MailSend = "mail_send";
    public const string TrackRename = "track_rename";
    public const string TemplateList = "template_list";
    public const string TemplateRead = "template_read";
    public const string TemplateUpdateInputs = "template_update_inputs";
    public const string TrackSet = "track_set";
    public const string TrackClose = "track_close";

    // Contract completion tools (BoundTaskId != null ONLY)
    public const string TaskDone = "task_done";
    public const string TaskFail = "task_fail";
    public const string SessionRespond = "session_respond";
    public const string SessionRead = "session_read";
    public const string SessionSend = "session_send";
    public const string SessionWithdraw = "session_withdraw";
    public const string SessionList = "session_list";
    public const string SessionCreate = "session_create";
    public const string SessionUpdate = "session_update";
    public const string SessionControl = "session_control";

    public static readonly IReadOnlyList<string> All =
    [
        SystemUpgradeCheck, SystemUpgradeApply, SystemUpgradeRollback,
        ReportRead,
        ReportCommit,
        DataPatch,
        TaskList,
        UserAsk,
        MailList,
        MailRead,
        TrackStatus,
        ProviderList,
        TerminalOpen,
        TerminalRead,
        TerminalInput,
        FlowRead,
        FlowCommit,
        TaskCancel,
        MailSend,
        TrackRename,
        TrackClose,
        TemplateList,
        TemplateRead,
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

public sealed record McpToolAnnotations(
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyName("readOnlyHint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReadOnlyHint = null,
    [property: JsonPropertyName("destructiveHint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DestructiveHint = null,
    [property: JsonPropertyName("idempotentHint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IdempotentHint = null,
    [property: JsonPropertyName("openWorldHint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OpenWorldHint = null);

public sealed record McpToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    McpToolAnnotations? Annotations = null);

public static class KernelMcpCatalog
{
    private static readonly IReadOnlyList<McpToolDescriptor> AllDescriptors = BuildDescriptors();

    public static IReadOnlyList<McpToolDescriptor> GetAllTools() => AllDescriptors;

    public static IReadOnlyList<McpToolDescriptor> GetToolsForSession(string? boundTaskId, bool isLead = false)
    {
        var isBound = !string.IsNullOrEmpty(boundTaskId);
        return AllDescriptors
            .Where(d => isBound ? !IsGovernanceOnly(d.Name) : !IsCompletionOnly(d.Name))
            .Where(d => !IsLeadOnly(d.Name) || (!isBound && isLead))
            .ToArray();
    }

    public static bool IsGovernanceOnly(string toolName) => toolName is
        KernelMcpToolNames.FlowRead or KernelMcpToolNames.FlowCommit or
        KernelMcpToolNames.TaskCancel or
        KernelMcpToolNames.SessionCreate or KernelMcpToolNames.SessionUpdate or KernelMcpToolNames.SessionSend or
        KernelMcpToolNames.SessionWithdraw or KernelMcpToolNames.SessionControl or KernelMcpToolNames.SessionRespond or
        KernelMcpToolNames.MailSend or
        KernelMcpToolNames.TrackRename or
        KernelMcpToolNames.TrackClose or
        KernelMcpToolNames.TemplateList or
        KernelMcpToolNames.TemplateRead or
        KernelMcpToolNames.TemplateUpdateInputs or
        KernelMcpToolNames.TrackSet;

    public static bool IsCompletionOnly(string toolName) => toolName is
        KernelMcpToolNames.TaskDone or
        KernelMcpToolNames.TaskFail;

    public static bool IsLeadOnly(string toolName) => toolName is
        KernelMcpToolNames.FlowRead or KernelMcpToolNames.FlowCommit or
        KernelMcpToolNames.TrackSet or KernelMcpToolNames.TemplateUpdateInputs or
        KernelMcpToolNames.SystemUpgradeCheck or KernelMcpToolNames.SystemUpgradeApply or KernelMcpToolNames.SystemUpgradeRollback;

    private static IReadOnlyList<McpToolDescriptor> BuildDescriptors()
    {
        static JsonElement Schema(string json) => JsonHelpers.ParseClone(json);

        var readOnlyLocal = new McpToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false);
        var readOnlyOpenWorld = new McpToolAnnotations(ReadOnlyHint: true, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: true);
        var localWrite = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: false, OpenWorldHint: false);
        var localIdempotentWrite = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: false, IdempotentHint: true, OpenWorldHint: false);
        var localDestructive = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: false);
        var openWorldDestructive = new McpToolAnnotations(ReadOnlyHint: false, DestructiveHint: true, IdempotentHint: false, OpenWorldHint: true);

        return
        [
            new(KernelMcpToolNames.SystemUpgradeCheck, "Check an installation-owner configured upgrade source (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"sourceId":{"type":"string"}}}"""), readOnlyOpenWorld),
            new(KernelMcpToolNames.SystemUpgradeApply, "Schedule a detached upgrade from a configured source ID and return immediately (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"required":["sourceId"],"properties":{"sourceId":{"type":"string"}}}"""), openWorldDestructive),
            new(KernelMcpToolNames.SystemUpgradeRollback, "Schedule detached rollback to the previous release (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""), localDestructive),
            new(KernelMcpToolNames.ReportRead, "Read the current track report document, block revisions, and read anchor. Supports optional format ('full'|'blocks'|'markdown'|'outline'), sectionH1/blockIds scoping, and includeContract for perga-block authoring specs.",
                Schema(KernelMcpReportSupport.ReportReadSchemaJson), readOnlyLocal),
            new(KernelMcpToolNames.ReportCommit, "Atomically apply block operations (replace_section, upsert, delete, move) to the track report using a ReadAnchor. Returns a compact commit receipt by default; set includeDocument=true for full renderedMarkdown and blocks.",
                Schema(KernelMcpReportSupport.ReportCommitSchemaJson), localWrite),
            new(KernelMcpToolNames.DataPatch, "Patch the track's structured template data (validated Fail-Closed against template data.schema and stamped with _meta.updatedAt/source).",
                Schema("""{"type":"object","required":["patch"],"properties":{"patch":{"type":"object","description":"Partial JSON object; array fields accept {$upsert: [{id|code, ...}], $delete: [key]}."},"expectedDataRev":{"type":"integer","minimum":0}}}"""), localWrite),
            new(KernelMcpToolNames.TaskList, "List all tasks declared in the track.",
                Schema("""{"type":"object","properties":{}}"""), readOnlyLocal),
            new(KernelMcpToolNames.UserAsk, "Suspend and ask the human user one or more structured multiple-choice questions.",
                Schema(KernelMcpReportSupport.UserAskSchemaJson), localWrite),
            new(KernelMcpToolNames.MailList, "List cross-track mails delivered to this track.",
                Schema("""{"type":"object","properties":{"unreadOnly":{"type":"boolean"}}}"""), readOnlyLocal),
            new(KernelMcpToolNames.MailRead, "Read a specific mail by ID and mark it as read.",
                Schema("""{"type":"object","required":["mailId"],"properties":{"mailId":{"type":"string"}}}"""), localIdempotentWrite),
            new(KernelMcpToolNames.TrackStatus, "Inspect track status, active sessions, tasks, open asks, terminals, and unread mail count.",
                Schema("""{"type":"object","properties":{}}"""), readOnlyLocal),
            new(KernelMcpToolNames.ProviderList, "List providers, models, supported reasoning efforts, and permission modes for this Track. " +
                "If permissionCatalogSource is dynamic and permissionModes is empty, the Track has no observed modes for that provider yet. Omit permissionMode to use its workspace default.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "provider":{"type":"string","description":"Filter to one provider, including if unavailable. Accepts canonical IDs and aliases such as agy or opencode."},
                  "refresh":{"type":"boolean","description":"Bypass the 60-second probe cache. Defaults to false."},
                  "includeUnavailable":{"type":"boolean","description":"Include unavailable providers and their diagnostic reasons. Defaults to false."}
                }}
                """), readOnlyLocal),
            new(KernelMcpToolNames.TerminalOpen, "Open or reuse an idempotent PTY terminal in the workspace.",
                Schema("""{"type":"object","properties":{"title":{"type":"string"},"program":{"type":"string"},"args":{"type":"array","items":{"type":"string"}},"cwd":{"type":"string"},"idempotencyKey":{"type":"string"},"cols":{"type":"integer"},"rows":{"type":"integer"}}}"""), openWorldDestructive),
            new(KernelMcpToolNames.TerminalRead, "Observe terminal screen frame and optional wait condition.",
                Schema(KernelMcpReportSupport.TerminalReadSchemaJson), readOnlyLocal),
            new(KernelMcpToolNames.TerminalInput, "Send text, keys, paste, or submit action to a terminal.",
                Schema(KernelMcpReportSupport.TerminalInputSchemaJson), openWorldDestructive),
            new(KernelMcpToolNames.FlowRead, "Read the Track's committed flow spec, current revision, Host file path, and live projection (Lead only).",
                Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""), readOnlyLocal),
            new(KernelMcpToolNames.FlowCommit,
                "Validate and commit a flow JSON draft supplied as content or read from the Host's flow.json (Lead only).\n" +
                "Cheatsheet:\n" +
                "- Root: { rev, nodes: [{ id, title?, source?, when?, do, canceled? }] }\n" +
                "- when (omit = run once immediately): { after: [\"id\" | { node, on?: ok|attention|blocked|any }], on? } | { cron, tz?, maxOccurrences? } | { at: ISO-8601 }\n" +
                "- do.kind='run': { kind:'run', prompt, session?:'new'|'ses_...', role?, access?, provider?, model?, gate?, acceptance? } OR { kind:'run', cmd }\n" +
                "- do.kind='wake': { kind:'wake', session:'lead'|'ses_...', prompt }\n" +
                "- do.kind='watch' (0-token): { kind:'watch', prChecks, repo?, headSha?, interval?:'60s', timeout?:'60m' } OR { kind:'watch', cmd, interval?, timeout? } (exit 0=ok, 75=wait, other=attention)\n" +
                "- Reusing an existing ses_... preserves NativeThreadId & context.\n" +
                "- Never silently delete active/running/cron nodes — set canceled:true explicitly; terminal orphan one-shot nodes are archived by background GC.\n" +
                "- Pass dryRun:true first to inspect plan diff & diagnostics with zero side effects.",
                Schema("""{"type":"object","additionalProperties":false,"properties":{"dryRun":{"type":"boolean","description":"Validate the draft and return the plan diff. Defaults to false."},"expectedFlowRev":{"type":"integer","minimum":0,"description":"Expected committed revision. Defaults to the draft rev."},"content":{"type":["string","null"],"description":"Flow JSON text. Omit to read the Host's flow.json."}}}"""), localWrite),
            new(KernelMcpToolNames.TaskCancel, "Cancel a task and stop any active execution (task parent or Track Lead).",
                Schema("""{"type":"object","required":["taskKey"],"properties":{"taskKey":{"type":"string"},"reason":{"type":"string"}}}"""), localWrite),
            new(KernelMcpToolNames.MailSend, "Wake the recipient Lead by specifying exactly one of toTrackId or replyToMailId (a message in your inbox). Governance only.",
                Schema("""{"type":"object","required":["summary","body"],"properties":{"replyToMailId":{"type":"string"},"toTrackId":{"type":"string"},"summary":{"type":"string"},"body":{"type":"string"}}}"""), localWrite),
            new(KernelMcpToolNames.TrackRename, "Rename the current track (governance only).",
                Schema("""{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}"""), localIdempotentWrite),
            new(KernelMcpToolNames.TemplateList, "List concise template summaries (governance only).",
                Schema("""{"type":"object","properties":{}}"""), readOnlyLocal),
            new(KernelMcpToolNames.TemplateRead, "Inspect a template manifest and source; use __current__ for the active snapshot, inputs, state, and headerView; omit templateId or use __contract__ for the authoring contract (governance only).",
                Schema("""{"type":"object","properties":{"templateId":{"type":"string"}}}"""), readOnlyLocal),
            new(KernelMcpToolNames.TemplateUpdateInputs, "Merge inputs into the active template, preserving state (Track Lead only).",
                Schema("""{"type":"object","required":["inputs"],"properties":{"inputs":{"type":"object"}}}"""), localIdempotentWrite),
            new(KernelMcpToolNames.TrackSet, "Apply a templateId, local template package path, or JSON/.perga.js source with inputs. Use dryRun to preview or clear to detach while keeping the report (Track Lead only).",
                Schema("""{"type":"object","properties":{"templateId":{"type":"string","minLength":1},"source":{"type":"string","minLength":1},"inputs":{"type":"object"},"dryRun":{"type":"boolean"},"clear":{"type":"boolean"},"resetData":{"type":"boolean"}}}"""), localWrite),
            new(KernelMcpToolNames.TrackClose, "Close the current track and cancel active tasks (governance only).",
                Schema("""{"type":"object","properties":{}}"""), localDestructive),
            new(KernelMcpToolNames.TaskDone, "Submit completed work for the bound task with optional neutral outcome (ok|attention|blocked) and summary, and trigger gate verification (contract subagent only).",
                Schema("""{"type":"object","properties":{"outcome":{"enum":["ok","attention","blocked"],"description":"Domain-neutral business outcome. Defaults to 'ok'."},"summary":{"type":"string","description":"Concise settlement summary."},"result":{}}}"""), localWrite),
            new(KernelMcpToolNames.TaskFail, "Report failure for the bound task (contract subagent only).",
                Schema("""{"type":"object","required":["reason"],"properties":{"reason":{"type":"string"},"result":{}}}"""), localWrite),
            new(KernelMcpToolNames.SessionList, "List conversations in this Track.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "includeArchived":{"type":"boolean","description":"Set true to include archived conversations."},
                  "parentSessionId":{"type":"string","description":"Filter to direct children of this conversation."}
                }}
                """), readOnlyLocal),
            new(KernelMcpToolNames.SessionCreate,
                "Create a lightweight multi-turn collaborator sharing the Track workspace. Avoid concurrent writers. " +
                "Declare formal tasks with flow_commit. Call provider_list if unsure of provider, model, reasoningEffort, or permissionMode. Reuse requestKey to retry safely.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "title":{"type":"string","description":"Human-readable conversation title."},
                  "provider":{"type":"string","description":"Provider ID from provider_list. Omit to use the parent provider."},
                  "model":{"type":"string","description":"Model ID for the target provider. Omit to use inherited or default settings."},
                  "reasoningEffort":{"type":"string","description":"Reasoning effort supported by the selected model. Omit to use inherited or default settings."},
                  "permissionMode":{"type":"string","description":"Use a native permission mode ID from provider_list. If permissionCatalogSource is dynamic and permissionModes is empty, omit this field to use the provider's workspace default or an applicable inherited mode."},
                  "systemPrompt":{"type":"string","description":"Additional system instructions for this conversation."},
                  "prompt":{"type":"string","description":"Initial instruction. Omit to create an idle child."},
                  "requestKey":{"type":"string","description":"Idempotency key scoped to the calling session and Track. Reuse with the same arguments."},
                  "notifyParent":{"enum":["none","turn_end"],"description":"Parent wakeup policy. Notify after a turn. Defaults to none."}
                }}
                """), localWrite),
            new(KernelMcpToolNames.SessionRead, "Read conversation state, history, progress and pending interactions.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation to read."},
                  "beforeId":{"type":"integer","description":"Read history items older than this item ID."},
                  "afterId":{"type":"integer","description":"Read history items newer than this item ID, or omit to page backward from the latest items."},
                  "limit":{"type":"integer","minimum":1,"maximum":100,"description":"Maximum history items to return, defaulting to 20."},
                  "activityTurnId":{"type":"string","description":"ID of the active turn whose activity you last observed."},
                  "afterSequence":{"type":"integer","minimum":0,"description":"Last activity sequence observed for activityTurnId, used to request incremental updates."},
                  "waitMs":{"type":"integer","minimum":0,"maximum":10000,"description":"Wait up to this many milliseconds for a relevant update when the current state permits waiting."}
                },"required":["sessionId"]}
                """), readOnlyLocal),
            new(KernelMcpToolNames.SessionUpdate,
                "Update a child conversation. Configuration changes require expectedConfigVersion. Use provider_list to discover supported values.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation to update."},
                  "expectedConfigVersion":{"type":"integer","description":"Current configuration version from session read or list. Required for provider, model, effort, or permission changes."},
                  "title":{"type":"string","description":"New title. Omit to keep the current title."},
                  "archived":{"type":"boolean","description":"Set true to archive or false to reopen. Omit to preserve the current state."},
                  "provider":{"type":"string","description":"Provider ID for an idle session. Omit to keep the current provider. Changing providers resets omitted model, reasoningEffort, and permissionMode."},
                  "model":{"type":["string","null"],"description":"Omit to keep the current model on the same provider, use null for the provider default, or pass a model ID."},
                  "reasoningEffort":{"type":["string","null"],"description":"Omit to keep the current effort on the same provider, use null for the model default, or pass a supported effort."},
                  "permissionMode":{"type":["string","null"],"description":"Omit to keep the current mode on the same provider, use null for the provider default, or pass a native mode ID."}
                },"required":["sessionId"]}
                """), localIdempotentWrite),
            new(KernelMcpToolNames.SessionSend, "Send content with requestKey, or steer a queued messageId into turnId. Default requires idle; queue waits; steer targets a running turn.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation that should receive the message."},
                  "expectedEpoch":{"type":"integer","description":"Require this conversation epoch."},
                  "content":{"type":"string","description":"Text for a new message, supplied with requestKey in place of messageId."},
                  "requestKey":{"type":"string","description":"Idempotency key required with new content and reused for identical retries."},
                  "messageId":{"type":"string","description":"ID of a queued message to steer into the active turn."},
                  "turnId":{"type":"string","description":"ID of the active turn targeted by steer mode."},
                  "mode":{"enum":["default","queue","steer"],"description":"Choose default for idle delivery, queue to wait for dispatch, or steer to target the active turn."}
                },"required":["sessionId"]}
                """), localWrite),
            new(KernelMcpToolNames.SessionWithdraw, "Withdraw a queued or unresolved conversation message.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation that owns the message."},
                  "messageId":{"type":"string","description":"ID of the queued or unresolved message to withdraw."}
                },"required":["sessionId","messageId"]}
                """), localIdempotentWrite),
            new(KernelMcpToolNames.SessionControl, "Stop a specific turn, reconnect an unknown session, reset context, or compact an idle conversation.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation to control."},
                  "action":{"enum":["stop","reconnect","reset_context","compact"],"description":"Lifecycle operation to perform on the target conversation."},
                  "turnId":{"type":"string","description":"ID of the observed turn, required for stop."},
                  "expectedEpoch":{"type":"integer","description":"Require this conversation epoch when reconnecting or resetting context."}
                },"required":["sessionId","action"]}
                """), localWrite),
            new(KernelMcpToolNames.SessionRespond, "Answer a pending ask with answers, or an approval with executionId and optionId.",
                Schema("""
                {"type":"object","additionalProperties":false,"properties":{
                  "sessionId":{"type":"string","description":"ID of the conversation with the pending interaction."},
                  "interactionId":{"type":"string","description":"ID of the pending ask or approval."},
                  "executionId":{"type":"string","description":"Execution ID from the pending approval, required for approval responses."},
                  "optionId":{"type":"string","description":"ID of the option selected for the pending approval."},
                  "answers":{"description":"Answers to the pending ask in the format specified by that interaction."}
                },"required":["sessionId","interactionId"]}
                """), localWrite)
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
        if (!KernelMcpToolNames.All.Contains(toolName))
            throw KernelException.NotFound($"Unknown kernel MCP tool '{toolName}'.");
        if (toolName is KernelMcpToolNames.SystemUpgradeCheck or KernelMcpToolNames.SystemUpgradeApply or KernelMcpToolNames.SystemUpgradeRollback)
        {
            return await kernel.InvokeSystemUpgradeAsync(caller,
                toolName == KernelMcpToolNames.SystemUpgradeCheck ? "check" : toolName == KernelMcpToolNames.SystemUpgradeApply ? "apply" : "rollback", args, ct).ConfigureAwait(false);
        }
        var origin = KernelMcpCatalog.IsCompletionOnly(toolName) && caller.BoundTaskId is not null
            ? await kernel.Actors.GetOrActivate(caller.TrackId).CaptureWorkerCompletionOriginAsync(caller, ct).ConfigureAwait(false)
            : null;
        var expectedPolicyGeneration = await kernel.GuardMcpToolCallAsync(caller.TrackId, caller.SessionId, "perga", toolName, args, ct, caller.ExecutionOrigin)
            .ConfigureAwait(false);

        var result = await ExecuteAuthorizedAsync(kernel, caller, toolName, args, expectedPolicyGeneration, origin, ct).ConfigureAwait(false);
        if (toolName is not (KernelMcpToolNames.ProviderList or KernelMcpToolNames.FlowRead) &&
            !(toolName is (KernelMcpToolNames.TrackSet or KernelMcpToolNames.FlowCommit) && args.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True))
            await kernel.Actors.GetOrActivate(caller.TrackId).AfterMcpToolCallAsync("perga", toolName,
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
                Perga.PolicyVm.TemplateViewsProjection? templateViews = null;
                if (res.Blocks.Any(b => b.Kind == ReportBlockKind.Widget))
                {
                    templateViews = await kernel.GetTrackViewsAsync(caller.TrackId, ct).ConfigureAwait(false);
                }

                return KernelMcpReportSupport.FormatReportReadResponse(res, args, templateViews);
            }

            case KernelMcpToolNames.ReportCommit:
            {
                var commitReq = ParseReportCommitArgs(args);
                var includeDocument = KernelMcpReportSupport.ParseIncludeDocumentArg(args);
                var res = await kernel.CommitReportAsync(caller.TrackId, caller.SessionId, commitReq, ct, caller.ExecutionOrigin, expectedPolicyGeneration)
                    .ConfigureAwait(false);
                return KernelMcpReportSupport.FormatReportCommitResponse(res, includeDocument);
            }

            case KernelMcpToolNames.DataPatch:
            {
                if (!args.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.Object)
                {
                    throw KernelException.InvalidArgument("data_patch requires a JSON object 'patch' property.");
                }

                long? expectedDataRev = null;
                if (args.TryGetProperty("expectedDataRev", out var rev))
                {
                    if (rev.ValueKind != JsonValueKind.Number || !rev.TryGetInt64(out var value) || value < 0)
                        throw KernelException.InvalidArgument("expectedDataRev must be a non-negative integer.");
                    expectedDataRev = value;
                }
                var nextData = await kernel.PatchTrackDataAsync(
                    caller.TrackId,
                    patchEl,
                    source: $"session:{caller.SessionId}",
                    callerSessionId: caller.SessionId,
                    ct: ct, expectedDataRev: expectedDataRev).ConfigureAwait(false);
                return JsonHelpers.ToElement(nextData);
            }

            case KernelMcpToolNames.TaskList:
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

            case KernelMcpToolNames.MailList:
            {
                var unreadOnly = JsonHelpers.TryGetPropertyIgnoreCase(args, ["unreadOnly", "unread_only"], out var u) &&
                                 u.ValueKind == JsonValueKind.True;
                var mails = await kernel.ListMailsAsync(caller.TrackId, unreadOnly, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(mails);
            }

            case KernelMcpToolNames.MailRead:
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

            case KernelMcpToolNames.ProviderList:
            {
                var query = ParseSessionArgs<ProviderListArgs>(args, toolName);
                if (query.Provider is null && JsonHelpers.TryGetPropertyIgnoreCase(args, ["provider"], out _))
                    throw KernelException.InvalidArgument("'provider' must be a string.");
                return JsonHelpers.ToElement(await kernel.ListProvidersForTrackAsync(caller.TrackId, query.Provider,
                    query.Refresh, query.IncludeUnavailable, ct).ConfigureAwait(false));
            }

            case KernelMcpToolNames.SessionList:
                return JsonHelpers.ToElement(await kernel.ListSessionsAsync(caller.TrackId,
                    ParseSessionArgs<SessionListFilter>(args, toolName), Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionCreate:
            {
                var request = ParseSessionArgs<CreateSessionRequest>(args, toolName);
                var created = await kernel.CreateSessionAsync(caller.TrackId, request,
                    OptionalTypedStringArg(args, ["requestKey"]) ?? Guid.NewGuid().ToString("N"),
                    Principal(caller) with { AuthorizedPolicyGeneration = expectedPolicyGeneration }, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(created.Receipt);
            }

            case KernelMcpToolNames.SessionRead:
            {
                RejectUnknownArgs(args, ToolSchema(toolName));
                var query = new SessionReadQuery(IntegerArg(args, "afterId", 0, long.MaxValue) ?? 0,
                    IntegerArg(args, "beforeId", 0, long.MaxValue), (int)(IntegerArg(args, "limit", 1, 100) ?? 20),
                    !args.TryGetProperty("afterId", out _), OptionalTypedStringArg(args, ["activityTurnId"]),
                    IntegerArg(args, "afterSequence", 0, long.MaxValue), (int)(IntegerArg(args, "waitMs", 0, 10000) ?? 0));
                return JsonHelpers.ToElement(await kernel.GetSessionItemsAsync(RequireStringArg(args, ["sessionId"]),
                    query, Principal(caller), ct).ConfigureAwait(false));
            }

            case KernelMcpToolNames.SessionUpdate:
                return JsonHelpers.ToElement(await kernel.UpdateSessionAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<UpdateSessionRequest>(args, toolName),
                    Principal(caller) with { AuthorizedPolicyGeneration = expectedPolicyGeneration }, ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionSend:
                return JsonHelpers.ToElement(await kernel.SendSessionMessageAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<SendSessionMessageRequest>(args, toolName),
                    Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionWithdraw:
                RejectUnknownArgs(args, ToolSchema(toolName));
                await kernel.WithdrawSessionMessageAsync(RequireStringArg(args, ["sessionId"]), RequireStringArg(args, ["messageId"]),
                    Principal(caller), ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { ok = true });

            case KernelMcpToolNames.SessionControl:
                return JsonHelpers.ToElement(await kernel.ControlSessionAsync(RequireStringArg(args, ["sessionId"]),
                    ParseSessionArgs<SessionControlRequest>(args, toolName),
                    Principal(caller), ct).ConfigureAwait(false));

            case KernelMcpToolNames.SessionRespond:
                return JsonHelpers.ToElement(await kernel.RespondInteractionAsync(RequireStringArg(args, ["sessionId"]),
                    RequireStringArg(args, ["interactionId"]), ParseSessionArgs<RespondInteractionRequest>(args, toolName), Principal(caller), ct).ConfigureAwait(false));

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

            case KernelMcpToolNames.FlowRead:
            {
                RejectUnknownArgs(args, ToolSchema(toolName));
                var snapshot = await kernel.GetTrackFlowAsync(caller.TrackId, ct).ConfigureAwait(false);
                return JsonHelpers.ToElement(new { snapshot.FlowRev, FlowPath = snapshot.FilePath, snapshot.Spec, snapshot.Projection });
            }

            case KernelMcpToolNames.FlowCommit:
            {
                RejectUnknownArgs(args, ToolSchema(toolName));
                var dryRun = false;
                if (args.TryGetProperty("dryRun", out var dryEl) && dryEl.ValueKind != JsonValueKind.Null)
                {
                    if (dryEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw KernelException.InvalidArgument("'dryRun' must be a boolean.");
                    dryRun = dryEl.GetBoolean();
                }

                var expectedFlowRev = IntegerArg(args, "expectedFlowRev", 0, long.MaxValue);
                var content = OptionalTypedStringArg(args, ["content"]);
                var res = await kernel.CommitTrackFlowAsync(caller.TrackId, dryRun, expectedFlowRev,
                    Principal(caller) with { AuthorizedPolicyGeneration = expectedPolicyGeneration }, ct, content)
                    .ConfigureAwait(false);
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

            case KernelMcpToolNames.TemplateList:
                return JsonHelpers.ToElement(kernel.ListTemplates().Select(m => new { id = m.Id, title = m.Title,
                    description = m.Description, inputKeys = m.InputsSchema.EnumerateObject().Select(p => p.Name).ToArray(),
                    plugins = m.DefaultPlugins, hasFlow = m.Flow.GetArrayLength() > 0, hasHeaderView = m.HasHeaderView }));

            case KernelMcpToolNames.TemplateRead:
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
                    args.TryGetProperty("clear", out var clear) && clear.GetBoolean(), ct,
                    args.TryGetProperty("resetData", out var reset) && reset.GetBoolean()).ConfigureAwait(false);
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

            case KernelMcpToolNames.TaskDone:
            {
                JsonElement? workerResult = JsonHelpers.TryGetPropertyIgnoreCase(args, ["result", "workerResult", "worker_result"], out var wr)
                    ? wr.Clone()
                    : null;
                var outcome = FlowOutcome.Ok;
                if (JsonHelpers.TryGetPropertyIgnoreCase(args, ["outcome"], out var outcomeEl) &&
                    outcomeEl.ValueKind != JsonValueKind.Null)
                {
                    if (outcomeEl.ValueKind != JsonValueKind.String ||
                        !Perga.Kernel.Flow.FlowConstants.TryParseOutcome(outcomeEl.GetString(), out outcome))
                    {
                        throw KernelException.InvalidArgument("'outcome' must be one of: ok, attention, blocked.");
                    }
                }

                var summary = OptionalTypedStringArg(args, ["summary"]);
                var res = await kernel.CompleteTaskDoneAsync(caller.TrackId, caller.SessionId, workerResult, ct, origin, outcome, summary)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            case KernelMcpToolNames.TaskFail:
            {
                var reason = RequireStringArg(args, ["reason", "error"]);
                JsonElement? workerResult = JsonHelpers.TryGetPropertyIgnoreCase(args, ["result", "workerResult", "worker_result"], out var wr)
                    ? wr.Clone()
                    : null;
                var res = await kernel.CompleteTaskFailAsync(caller.TrackId, caller.SessionId, reason, workerResult, ct, origin)
                    .ConfigureAwait(false);
                return JsonHelpers.ToElement(res);
            }

            default:
                throw KernelException.NotFound($"Unknown kernel MCP tool '{toolName}'.");
        }
    }

    private static InteropPrincipal.Session Principal(McpSessionPrincipal caller) => new(
        caller.TrackId, caller.SessionId, caller.IsLead, caller.BoundTaskId, 0, caller.Token, caller.ExecutionOrigin);

    private sealed record ProviderListArgs(string? Provider = null, bool Refresh = false, bool IncludeUnavailable = false);

    private static T ParseSessionArgs<T>(JsonElement args, string toolName)
    {
        RejectUnknownArgs(args, ToolSchema(toolName));
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

    private static JsonElement ToolSchema(string toolName) =>
        KernelMcpCatalog.GetAllTools().Single(tool => tool.Name == toolName).InputSchema;

    private static void RejectUnknownArgs(JsonElement args, JsonElement schema)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Tool arguments must be a JSON object.");
        var properties = schema.GetProperty("properties").EnumerateObject().ToArray();
        foreach (var prop in args.EnumerateObject())
        {
            var allowed = properties.FirstOrDefault(p => string.Equals(prop.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (allowed.Value.ValueKind == JsonValueKind.Undefined)
                throw KernelException.InvalidArgument(
                    $"Unknown argument '{prop.Name}'. Allowed arguments: {string.Join(", ", properties.Select(p => p.Name))}.");
            if (prop.Value.ValueKind != JsonValueKind.Null &&
                allowed.Value.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                RejectUnknownArgs(prop.Value, allowed.Value);
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

    private static ReportCommitRequest ParseReportCommitArgs(JsonElement args)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(args, ["anchor"], out var anchorElem) ||
            anchorElem.ValueKind != JsonValueKind.Object)
        {
            throw KernelException.InvalidArgument(
                "Missing 'anchor' object. Pass the ReadAnchor returned by report_read, e.g. {\"docRev\":0,\"knownBlockRevs\":{\"b_1234\":1},\"wholeDocumentRead\":true}.");
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
