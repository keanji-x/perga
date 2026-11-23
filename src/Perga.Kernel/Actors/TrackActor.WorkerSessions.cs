using System.Collections.Concurrent;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;
using System.Text;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal Task<WorkerCompletionOrigin> CaptureWorkerCompletionOriginAsync(McpSessionPrincipal caller,
        string generation, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (caller.BoundTaskId is not { } taskId || caller.TaskRunCount is not { } runCount)
                throw KernelException.Forbidden("Completion tool requires an active bound task session.");
            var open = RequireRuntimeBindingCore(new(caller.TrackId, caller.SessionId, caller.IsLead,
                taskId, 0, caller.Token, caller.RuntimeBindingId) { AuthorizedPolicyGeneration = generation });
            if (!_taskCoordinator.TryGetActiveRun(taskId, out var activeTask) || activeTask.SessionId != caller.SessionId)
                throw KernelException.Conflict("The task execution no longer accepts completion.");
            var origin = new WorkerCompletionOrigin(caller.Token, open.RuntimeBindingId, taskId, runCount)
                { AuthorizedPolicyGeneration = generation };
            CheckWorkerCompletionCore(activeTask, origin);
            return origin;
        }, ct);

    private void CheckWorkerCompletionCore(TrackTasks.ActiveTaskRunState activeTask, WorkerCompletionOrigin? origin)
    {
        var task = _taskCoordinator.RequireTaskCore(activeTask.TaskId);
        if (activeTask.Paused || activeTask.Settling || task.Status != TaskRunStatus.Running)
            throw KernelException.Conflict("The task no longer accepts completion.");
        if (origin is null) return;
        if (origin.TaskId != activeTask.TaskId || origin.RunCount != activeTask.RunCount ||
            activeTask.SessionId is not { } sessionId)
            throw KernelException.Conflict("The task execution no longer accepts completion.");
        RequireRuntimeBindingCore(new(TrackId, sessionId, false, origin.TaskId, 0, origin.Token, origin.RuntimeBindingId)
            { AuthorizedPolicyGeneration = origin.AuthorizedPolicyGeneration });
        if (_mcpBridge.ResolveToken(origin.Token) is not { } credential ||
            credential.BoundTaskId != origin.TaskId || credential.TaskRunCount != origin.RunCount)
            throw KernelException.Conflict("The runtime credential belongs to another task execution.");
    }

    private void PublishTurnActivity(ActiveTurnState active, string kind, JsonElement payload, long now)
    {
        var sequence = active.Activity.Append(kind, payload, now);
        var wire = payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        wire["sequence"] = JsonHelpers.ToElement(sequence);
        _eventHub.PublishTransient(_track.AreaId, TrackId, kind, JsonHelpers.ToElement(wire), now);
    }

    private sealed class TurnActivity
    {
        private const int Capacity = 128;
        private const int PayloadLimit = 4096;
        private const int TextLimit = 65536;
        private readonly Queue<WorkerActivityEvent> _recent = new();
        private readonly StringBuilder _text = new();
        private bool _textTruncated;
        private long _sequence;

        public long Append(string kind, JsonElement payload, long now)
        {
            var raw = payload.GetRawText();
            lock (_recent)
            {
                if (kind == KernelEventKinds.SessionTextDelta && payload.TryGetProperty("delta", out var delta))
                {
                    _text.Append(delta.GetString());
                    if (_text.Length > TextLimit)
                    {
                        _text.Remove(0, _text.Length - TextLimit);
                        _textTruncated = true;
                    }
                }
                if (raw.Length > PayloadLimit)
                    payload = JsonHelpers.ToElement(new { preview = raw[..PayloadLimit], truncated = true });
                if (_recent.Count == Capacity)
                    _recent.Dequeue();
                _recent.Enqueue(new WorkerActivityEvent(++_sequence, kind, payload, now));
                return _sequence;
            }
        }

        public (long Sequence, bool Reset, WorkerActivityEvent[] Events, string Text, bool TextTruncated) Read(long? after, bool sameTurn)
        {
            lock (_recent)
            {
                var reset = after is not null && (!sameTurn || after < _sequence - _recent.Count || after > _sequence);
                return (_sequence, reset, _recent.Where(item => reset || after is null || item.Sequence > after).ToArray(),
                    sameTurn && after is not null && !reset ? string.Empty : _text.ToString(), _textTruncated);
            }
        }
    }
    private string BuildDefaultSystemPromptCore(AgentSessionDto session)
    {
        var reportMd = _reportEngine.RenderMarkdown(_reports.Document, withMarkers: true);
        var templateDataSection = BuildActiveTemplateDataSectionCore();
        if (session.IsLead)
        {
            var templateScoping = _policy is null
                ? " Once you understand the scope of substantive work—before doing deep work or writing the Report—check `template_list` (and `template_read` for any plausible match) and apply a fitting template with `track_set` up front so its Report layout and `data` seed cleanly."
                : string.Empty;
            return $"""
                    You are the Lead Session for Perga Track `{TrackId}` (title: `{_track.Title}`).

                    ## Track Model
                    You own four durable surfaces on this Track:
                    - **Title**: keep it concise and accurate once the goal is clear (`track_rename`).
                    - **Report**: the Track's living document (`report_read` / `report_commit`)—where plans, research, decisions, and current progress live. Use **Conversation** as the working thread for quick answers, clarifying questions, coordination, and brief summaries of Report updates, not as the sole home for lasting deliverables.
                    - **Templates & Data**: Templates are optional and implicit (`template_list` / `template_read`, apply via `track_set` with a `templateId` or `.perga.js source`). A template composes four layers—Report widgets, schema-validated `data` (`data_patch`), starter Flow nodes, and worker routing—and is composable rather than all-or-nothing (you own any Flow or schedule nodes a template seeds and may edit or cancel them).{templateScoping} Keep structured facts in `data` and narrative in the Report without duplicating widget content. After `track_set`, read the seeded Report (`report_read`) and inspect `template_read(templateId: "__current__", format: "files")` when you need the template's `data` schema.
                    - **Flow**: declarative task and schedule orchestration at `{_flowFileStore.GetFlowFilePath(TrackId)}` (`flowRev`: {_track.FlowRev}).

                    ## Authority & Boundaries
                    - You are the single authority over Track template binding (`track_set`) and orchestration.
                    - Control only direct children (as Lead, you may also manage other Track sessions). Never stop, reset, or archive yourself. Answer delegated asks and approvals via `session_respond`; the human can always take over.

                    ## Current Report
                    {reportMd}{templateDataSection}
                    """;
        }

        var boundTask = _taskCoordinator.TryGetActiveOwningTaskCore(session.Id);
        var role = boundTask is not null
            ? $"Contract Subagent (BoundTaskId={boundTask.Id})"
            : "Collaborative Subagent (Unbound)";

        const string unboundGovernanceNote =
            "Use session_list/create/read/update/send/withdraw/control/respond to manage child conversations. Use session_create for collaborators and reuse requestKey for retries. Declare formal tasks in Flow and have Workers submit task_done with an outcome. Use provider_list to discover available providers, models, reasoning efforts, and permission modes. Only control your direct children; the Lead can also manage other sessions. Never stop, reset or archive yourself. Answer delegated asks and approvals with session_respond; the human can always take over.";
        var sessionNote = boundTask is null
            ? unboundGovernanceNote + " If blocked by missing permissions or sandbox restrictions (such as EPERM, blocked local ports/network, or denied actions), do not loop or silently skip checks: report the exact permission gap in your reply so your parent/Lead can adjust permissionMode via session_update (takes effect on the next turn) or run the restricted step; only call user_ask when pausing for explicit human/parent input (it does not auto-wake an idle parent)."
            : "Use session_list/read to observe conversations. Submit your task with task_done (optional outcome: ok|attention|blocked, summary) or task_fail; bound Workers cannot spawn or control sessions. If blocked by missing permissions or sandbox restrictions (such as EPERM, blocked local ports/network, or denied actions), do not loop or silently skip required checks: either finish the turn without calling task_done/task_fail and report the exact permission gap so the Lead can update permissionMode via session_update for the next turn while the task remains active, or call task_fail (which settles the task) so the Lead can run the restricted check or re-dispatch; only call user_ask when waiting for explicit human/parent input (it does not auto-wake an idle parent).";
        return $"You are operating in Perga Track '{_track.Title}' ({TrackId}) as {role}.\n\n{sessionNote}\n\n## Current Report\n{reportMd}{templateDataSection}";
    }

    private string BuildEpochSeedSystemPrompt(
        AgentSessionDto compactedSession,
        string latestReportMarkdown,
        string handoverSummary)
    {
        var boundTask = _taskCoordinator.TryGetActiveOwningTaskCore(compactedSession.Id);
        var role = compactedSession.IsLead
            ? "Lead Session"
            : boundTask is not null
                ? $"Contract Subagent (BoundTaskId={boundTask.Id})"
                : "Collaborative Subagent";
        var templateDataSection = BuildActiveTemplateDataSectionCore();

        return $"""
                You are resuming Perga Track '{_track.Title}' ({TrackId}) as {role} in Epoch {compactedSession.Epoch}.

                ## Latest Track Report
                {latestReportMarkdown}{templateDataSection}

                ## Epoch {compactedSession.Epoch - 1} Handover Summary
                {handoverSummary}
                """;
    }

    private string BuildActiveTemplateDataSectionCore()
    {
        if (_policy is null)
        {
            return string.Empty;
        }

        var viewNames = _policy.Manifest.ViewNames ?? [];
        var hasViews = viewNames.Count > 0;
        var hasState = _track.Data.ValueKind == JsonValueKind.Object &&
                       _track.Data.EnumerateObject().Any();
        if (!hasViews && !hasState)
        {
            return string.Empty;
        }

        const int maxChars = 16 * 1024;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"## Active Template Data ({_policy.TemplateId})");
        sb.AppendLine($"dataRev: {_track.DataRev} (pass expectedDataRev to data_patch to reject stale writes).");
        if (hasViews)
        {
            var views = string.Join("`, `", viewNames);
            sb.AppendLine($"Report `perga-block widget` blocks render views (`{views}`) live from structured template data and Track facts, and `{KernelMcpToolNames.ReportRead}` includes view projections when the report contains widget blocks.");
            sb.AppendLine($"Update structured facts with `{KernelMcpToolNames.DataPatch}`, keep narrative and layout in `{KernelMcpToolNames.ReportCommit}`, and avoid duplicating widget tables or lists in Markdown prose.");
        }

        if (hasState)
        {
            var rawJson = _track.Data.GetRawText();
            if (rawJson.Length > maxChars)
            {
                rawJson = rawJson[..maxChars] + "\n... (truncated)";
            }

            sb.AppendLine("```json");
            sb.AppendLine(rawJson);
            sb.Append("```");
        }

        return sb.ToString();
    }

    private static string BuildContractSubagentPrompt(TaskDto task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Execute contract task '{task.TaskKey}' (generation {task.Generation}, access={FormatAccessModeForPolicy(task.Access)}).");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(task.GoalOrCmd);

        if (!string.IsNullOrWhiteSpace(task.Acceptance))
        {
            sb.AppendLine();
            sb.AppendLine("## Acceptance Criteria");
            sb.AppendLine(task.Acceptance);
        }

        if (!string.IsNullOrWhiteSpace(task.SemanticNote))
        {
            sb.AppendLine();
            sb.AppendLine("## Previous Review Note");
            sb.AppendLine(task.SemanticNote);
        }

        sb.AppendLine();
        sb.AppendLine("When finished, call `task_done` with your structured result, or `task_fail` if blocked. If blocked by missing permissions or sandbox restrictions (e.g. `EPERM`, blocked local ports, or denied tool calls), do not loop or silently skip checks: either end the turn without calling `task_done`/`task_fail` and report the exact permission gap so the Lead can update `permissionMode` via `session_update` for the next turn while the task remains active, or call `task_fail` (which settles the task) so the Lead can run the restricted check or re-dispatch; call `user_ask` only when waiting for explicit human/parent input.");
        return sb.ToString();
    }

    private static string FormatAccessModeForPolicy(AccessMode mode) =>
        mode == AccessMode.ReadOnly ? "read_only" : "read_write";

}
