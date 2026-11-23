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
        var boundTask = _taskCoordinator.TryGetActiveOwningTaskCore(session.Id);
        var role = session.IsLead
            ? "Lead Session (Primary Track Representative)"
            : boundTask is not null
                ? $"Contract Subagent (BoundTaskId={boundTask.Id})"
                : "Collaborative Subagent (Unbound)";

        var templateNote = session.IsLead
            ? "\n\nTemplates are optional and implicit. If useful, discover them with template_list, inspect a template or the authoring contract with template_read, inspect the active snapshot with template_read(__current__), apply or preview with track_set (templateId or JSON/.perga.js source, inputs, dryRun), detach with clear, and update inputs with template_update_inputs (preserves state)."
            : string.Empty;
        var flowGuide = session.IsLead
            ? $"\n\n## Track Flow Orchestration\n- The Host manages `{_flowFileStore.GetFlowFilePath(TrackId)}` (current `flowRev`: {_track.FlowRev}).\n- Use `when` (`after` / `cron` / `at`) and `do` (`run` / `wake` / `watch`) to declare nodes. If you can access the Host file, edit it and use `flow_commit(dryRun: true)` before `flow_commit()`.\n- From SSH or when the Host file is unavailable, call `flow_read(format: \"spec\")` for the current spec and revision, then pass the edited JSON text as `content` and the read revision as `expectedFlowRev` to `flow_commit`. Validate the same arguments with `dryRun: true` first.\n- To stop an active or recurring node, set `\"canceled\": true` and commit.\n- Background GC archives terminal one-shot nodes with no remaining dependents and preserves uncommitted drafts."
            : string.Empty;
        const string unboundGovernanceNote =
            "Use session_list/create/read/update/send/withdraw/control/respond to manage child conversations. Use session_create for collaborators and reuse requestKey for retries. Declare formal tasks in Flow and have Workers submit task_done with an outcome. Use provider_list to discover available providers, models, reasoning efforts, and permission modes. Only control your direct children; the Lead can also manage other sessions. Never stop, reset or archive yourself. Answer delegated asks and approvals with session_respond; the human can always take over.";
        var sessionNote = boundTask is null
            ? session.IsLead
                ? unboundGovernanceNote
                : unboundGovernanceNote + " If blocked by missing permissions or sandbox restrictions (such as EPERM, blocked local ports/network, or denied actions), do not loop or silently skip checks: report the exact permission gap in your reply so your parent/Lead can adjust permissionMode via session_update (takes effect on the next turn) or run the restricted step; only call user_ask when pausing for explicit human/parent input (it does not auto-wake an idle parent)."
            : "Use session_list/read to observe conversations. Submit your task with task_done (optional outcome: ok|attention|blocked, summary) or task_fail; bound Workers cannot spawn or control sessions. If blocked by missing permissions or sandbox restrictions (such as EPERM, blocked local ports/network, or denied actions), do not loop or silently skip required checks: either finish the turn without calling task_done/task_fail and report the exact permission gap so the Lead can update permissionMode via session_update for the next turn while the task remains active, or call task_fail (which settles the task) so the Lead can run the restricted check or re-dispatch; only call user_ask when waiting for explicit human/parent input (it does not auto-wake an idle parent).";
        var templateDataSection = BuildActiveTemplateDataSectionCore();
        return $"You are operating in Perga Track '{_track.Title}' ({TrackId}) as {role}.{templateNote}{flowGuide}\n\n{sessionNote}\n\n## Current Report\n{reportMd}{templateDataSection}";
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

        var hasViews = _policy.Manifest.ViewNames is { Count: > 0 };
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
            sb.AppendLine("Report `perga-block widget` blocks render live from this structured template data (and Track kernel facts). Call `data_patch` to update structured fields, or `report_read` to inspect the latest projected views.");
        }

        if (hasState)
        {
            var rawJson = _track.Data.GetRawText();
            if (rawJson.Length > maxChars)
            {
                rawJson = rawJson[..maxChars] + "\n... (truncated; call perga_report_read for latest projected views)";
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
