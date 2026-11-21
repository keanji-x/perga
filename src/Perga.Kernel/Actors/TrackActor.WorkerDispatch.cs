using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public async Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionAsync(CreateSessionRequest request,
        string requestKey, InteropPrincipal caller, CancellationToken ct)
    {
        if (request.Task is null)
            return await CreateConversationAsync(request, requestKey, ct, caller).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw KernelException.InvalidArgument("A task requires a prompt.");
        ValidateRequestKey(requestKey);
        var authorId = caller is InteropPrincipal.Session identity ? identity.SessionId : InstallationAuthor;
        var taskKey = "worker-" + RequestIdentity.Hash(new { TrackId, authorId, key = requestKey })[..24].ToLowerInvariant();
        var candidateId = $"ses_{Guid.NewGuid():N}";
        var intent = new WorkerDispatchIntent(TrackId, authorId, requestKey, RequestIdentity.Hash(request), taskKey, Principal: caller);
        var committed = await CommitReportInternalAsync(caller is InteropPrincipal.Human ? EditActorKind.Human : null,
            caller is InteropPrincipal.Session parent ? parent.SessionId : null,
            report => BuildWorkerDispatchReportCommitCore(report, request, taskKey),
            ct, intent, caller is InteropPrincipal.Session origin ? origin.ExecutionOrigin : null,
            providers => PrepareWorkerSessionCore(request, providers, candidateId)).ConfigureAwait(false);
        var task = committed.Tasks.Single(t => t.TaskKey == taskKey);
        return await EnqueueAsync(async _ =>
        {
            var currentTask = RequireTaskCore(task.Id);
            var created = RequireSessionCore(currentTask.SessionId!);
            var replayed = created.Id != candidateId;
            SessionInputReceipt? first = null;
            if (currentTask.RunCount > 0 &&
                await Messages.GetAsync(created.Id, $"inp_{currentTask.Id}_{currentTask.RunCount}", ct).ConfigureAwait(false) is { } initial)
            {
                var turn = (await _store.ListSessionTurnsAsync(created.Id, ct).ConfigureAwait(false))
                    .SingleOrDefault(t => t.Id == initial.TargetTurnId);
                first = new(initial.Id, initial.TargetTurnId!, created.Id,
                    initial.Delivery == "dispatching" ? "accepted" : initial.Delivery, turn?.Status ?? initial.Delivery switch
                    { "unknown" => "unknown", "interrupted" => "interrupted", "rejected" => "failed", _ => "queued" }, null);
            }
            return (new SessionCreationReceipt(created, first, currentTask, requestKey), replayed);
        }, ct).ConfigureAwait(false);
    }

    private AgentSessionDto PrepareWorkerSessionCore(CreateSessionRequest request,
        IReadOnlyList<ProviderStatus> providers, string sessionId)
    {
        var requestedModel = SessionLaunchValidator.NormalizeRequestedModel(request.Model);
        var route = _policy is null ? new PolicyTaskRoute(request.Provider, requestedModel) :
            _policyVm.RouteTaskDispatch(_policy, request.Role,
                request.Task!.Access ?? _policy.Manifest.WorkerDispatch?.Access ?? "read_only",
                request.Provider, requestedModel, _track.TemplateInputs);
        if (route.Denial is { } denial) throw KernelException.PolicyDenied(denial);
        return NewSubagentSessionCore(request with
        {
            Provider = request.Provider ?? route.Provider,
            Model = requestedModel ?? route.Model,
            NotifyParent = request.NotifyParent ?? "task_settled"
        }, providers, requireAvailable: true, sessionId: sessionId);
    }

    private async Task<ReportReadResult?> TryReplayWorkerDispatchCoreAsync(
        WorkerDispatchIntent intent, CancellationToken ct)
    {
        var parent = RequireInteropCore(intent.Caller, write: true);
        SessionPolicy.RequireCreate(intent.Caller, parent, TrackId,
            callerIsTaskOwned: parent is not null && IsTaskOwnedSessionCore(parent.Id), _options.MaxSessionSpawnDepth);
        var replay = await _store.FindWorkerDispatchAsync(intent, ct).ConfigureAwait(false);
        if (replay is not null)
        {
            RequireMatchingHash(replay.RequestHash, intent.RequestHash);
            var task = _tasks.FirstOrDefault(task => task.Id == replay.TaskId && task.TaskKey == intent.TaskKey)
                ?? throw KernelException.Gone("The dispatched Worker task is no longer present.");
            if (task.Status == TaskRunStatus.Pending)
            {
                // The durable receipt is available while a pending delivery retries in the background.
                var shutdown = _actorShutdownCts.Token;
                _ = TrackBackgroundJob(() => TriggerSchedulerAsync(shutdown));
            }
            return new ReportReadResult(_report.DocRev, _report.Summary, _reportEngine.RenderMarkdown(_report, withMarkers: true),
                _report.Blocks, _tasks.ToArray(), WorkerDispatchReadAnchor(_report), []);
        }
        if (_track.ClosedAtMs is not null)
            throw KernelException.Conflict("Cannot dispatch a Worker on a closed Track.");
        if (_tasks.Any(task => task.TaskKey == intent.TaskKey))
            throw KernelException.Conflict("The generated Worker task key is already in use.");
        return null;
    }

    private ReportCommitRequest BuildWorkerDispatchReportCommitCore(ReportDocument report, CreateSessionRequest request, string taskKey)
    {
        var defaults = _policy?.Manifest.WorkerDispatch;
        var access = request.Task!.Access ?? defaults?.Access ?? "read_only";
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
        if (configuredGate is not null && request.Task!.Gate is not null && request.Task!.Gate != configuredGate)
            throw KernelException.PolicyDenied("Dispatch uses the validation command already configured by the Track template.");
        var gate = access == "read_write" ? configuredGate ?? request.Task!.Gate : request.Task!.Gate;

        var payload = JsonHelpers.ToElement(new
        {
            task_key = taskKey, kind = "agent", ready = true, goal_or_cmd = request.Prompt,
            provider = request.Provider, role = request.Role, access, acceptance = request.Task!.Acceptance,
            depends_on = request.Task!.DependsOn, gate,
            context = new { workerDispatch = true, initialModel = request.Model?.Trim() }
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
        SessionLaunchValidator.NormalizeProvider(provider) == SessionLaunchValidator.NormalizeProvider(GetDefaultProviderCore())
            ? GetDefaultModelCore(provider) : null;
}
