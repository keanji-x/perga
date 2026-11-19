using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public async Task<WorkerDispatchReceipt> DispatchWorkerAsync(
        DispatchWorkerRequest request, string? requestKey, string? callerSessionId, CancellationToken ct, McpExecutionOrigin? origin = null)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            throw KernelException.InvalidArgument("Worker dispatch requires a non-empty goal.");
        var key = requestKey ?? Guid.NewGuid().ToString("N");
        ValidateRequestKey(key);
        var authorId = callerSessionId ?? "owner:installation";
        var taskKey = "worker-" + RequestIdentity.Hash(new { TrackId, authorId, key })[..24].ToLowerInvariant();
        request = request with { Goal = request.Goal.Trim() };
        var intent = new WorkerDispatchIntent(TrackId, authorId, key, RequestIdentity.Hash(request), taskKey);
        var committed = await CommitReportInternalAsync(callerSessionId is null ? EditActorKind.Human : null, callerSessionId,
            report => BuildWorkerDispatchReportCommitCore(report, request, taskKey), ct, intent, origin).ConfigureAwait(false);
        var task = committed.Tasks.Single(item => item.TaskKey == taskKey);
        return await EnqueueAsync(_ => Task.FromResult(new WorkerDispatchReceipt(task,
            task.LatestAttemptId is null ? null : _sessions.Values.FirstOrDefault(session => session.BoundAttemptId == task.LatestAttemptId)?.Id, key)), ct)
            .ConfigureAwait(false);
    }

    private async Task<ReportReadResult?> TryReplayWorkerDispatchCoreAsync(
        WorkerDispatchIntent intent, string? callerSessionId, CancellationToken ct)
    {
        RequireWorkerOperatorCore(callerSessionId);
        var replay = await _store.FindWorkerDispatchAsync(intent, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            RequireMatchingHash(replay.RequestHash, intent.RequestHash);
            if (!_tasks.Any(task => task.Id == replay.TaskId && task.TaskKey == intent.TaskKey))
                throw KernelException.Gone("The dispatched Worker task is no longer present.");
            if (_tasks.Any(task => task.Id == replay.TaskId && task.Status == TaskRunStatus.Pending))
                await ScheduleEligibleTasksCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            return new ReportReadResult(_report.DocRev, _report.Summary, _reportEngine.RenderMarkdown(_report, withMarkers: true),
                _report.Blocks, _tasks.ToArray(), WorkerDispatchReadAnchor(_report), []);
        }
        if (_track.ClosedAtMs is not null)
            throw KernelException.Conflict("Cannot dispatch a Worker on a closed Track.");
        if (_tasks.Any(task => task.TaskKey == intent.TaskKey))
            throw KernelException.Conflict("The generated Worker task key is already in use.");
        return null;
    }

    private ReportCommitRequest BuildWorkerDispatchReportCommitCore(ReportDocument report, DispatchWorkerRequest request, string taskKey)
    {
        var defaults = _policy?.Manifest.WorkerDispatch;
        var access = request.Access ?? defaults?.Access ?? "read_only";
        if (access is not ("read_only" or "read_write"))
            throw KernelException.InvalidArgument("Worker access must be read_only or read_write.");
        string? configuredGate = null;
        if (defaults?.GateInput is { } input)
        {
            if (_track.TemplateInputs.TryGetProperty(input, out var configured))
                configuredGate = configured.ValueKind == System.Text.Json.JsonValueKind.String ? configured.GetString() : null;
            else if (_policy!.Manifest.InputsSchema.TryGetProperty(input, out var schema) &&
                schema.TryGetProperty("default", out var defaultGate) && defaultGate.ValueKind == System.Text.Json.JsonValueKind.String)
                configuredGate = defaultGate.GetString();
        }
        if (configuredGate is not null && request.Gate is not null && request.Gate != configuredGate)
            throw KernelException.PolicyDenied("Dispatch uses the validation command already configured by the Track template.");
        var gate = access == "read_write" ? configuredGate ?? request.Gate : request.Gate;

        var payload = JsonHelpers.ToElement(new
        {
            task_key = taskKey, kind = "agent", ready = true, goal_or_cmd = request.Goal,
            provider = request.Provider, role = request.Role, access, acceptance = request.Acceptance,
            depends_on = request.DependsOn, gate,
            context = new { workerDispatch = true, initialModel = request.Model }
        });
        var position = report.Blocks.Count;
        var ops = new List<BlockOp>();
        if (defaults?.Section is { } section)
        {
            var sectionExists = false;
            for (var i = 0; i < report.Blocks.Count; i++)
            {
                if (!string.Equals(report.Blocks[i].SectionH1, section, StringComparison.Ordinal)) continue;
                position = i + 1;
                sectionExists = true;
            }
            // Empty reports have no section to inherit. Create it with the first dispatched task.
            if (!sectionExists)
            {
                ops.Add(new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, $"# {section}\n", null, position));
                position++;
            }
        }
        ops.Add(new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, payload, position));
        return new ReportCommitRequest(WorkerDispatchReadAnchor(report), ops, CommitMessage: $"Dispatch {taskKey}");
    }

    private static ReadAnchor WorkerDispatchReadAnchor(ReportDocument report) =>
        new(report.DocRev, report.Blocks.ToDictionary(block => block.Id, block => block.Rev, StringComparer.Ordinal), WholeDocumentRead: true);

    private string? ResolveTaskWorkerModelCore(string provider) =>
        string.Equals(provider, GetDefaultProviderCore(), StringComparison.Ordinal) ? GetDefaultModelCore(provider) : null;
}
