using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private const string MemoryClearedMessage = "Memory is now empty (previous entries were removed).";

    private readonly IMemoryContextProvider _memoryContext;
    private MemoryTrackBinding? _memoryBinding;
    private string? _memoryAreaId;
    private volatile MemoryInjection? _lastInjectedMemoryVersion;

    private sealed record MemoryInjection(string SessionId, int Epoch, string Version, int EntryCount);

    private static string MemoryPlannerPrompt(string? areaId) =>
        $"""
        You are the Memory Planner for {(areaId is null ? "Global" : $"Area `{areaId}`")} memory.
        Curate durable facts and preferences in focused Markdown entries. Start with perga_memory list, read, or recall.
        This Track has a fixed Memory template and a read-only report that projects live entries.
        Commit only to your bound scope with perga_memory put and delete, using the latest ETag for existing entries.
        Your isolated Track workspace is for drafts. Treat inbound proposals as material to review and distill.
        Use track_list (pass the Area ID or "*" for all Areas) and report_read to distill useful material from closed Tracks.
        Ask the human with user_ask when a proposal conflicts with a pinned rule. Prefer short, durable pinned entries.
        """;

    private async Task HydrateMemoryScopeCoreAsync(CancellationToken ct)
    {
        _memoryBinding = await _store.FindMemoryBindingByTrackIdAsync(TrackId, ct).ConfigureAwait(false);
        _memoryAreaId = _memoryBinding is not null ? _memoryBinding.AreaId
            : await _store.IsUserAreaAsync(_track.AreaId, ct).ConfigureAwait(false) ? _track.AreaId : null;
    }

    private async Task<TrackDto> ProjectMemoryDataCoreAsync(CancellationToken ct)
    {
        if (_memoryBinding is { } binding &&
            await _memoryContext.GetMemoryTemplateDataAsync(binding.AreaId, ct).ConfigureAwait(false) is { } data)
            return _track with { Data = data };
        return _track;
    }

    internal Task<TrackDto> EnsureMemoryTemplateAsync(CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_memoryBinding is null)
                throw KernelException.Forbidden("This Track is not bound to a Memory scope.");
            var policy = MemoryTemplate.Resolve(_templateCatalog);
            var alreadyBound = _track.TemplateId == MemoryTemplate.Id;
            if (alreadyBound && _templateSnapshotSource == policy.JsSource) return _track;

            var leadId = _track.LeadSessionId
                ?? throw KernelException.PreconditionFailed("The Memory Planner Track needs a Lead conversation.");
            var next = _track with
            {
                TemplateId = policy.TemplateId,
                TemplateInputs = JsonHelpers.EmptyObject,
                UpdatedAtMs = NowMs()
            };
            var report = alreadyBound ? null : PrepareTemplateReportSeedCore(next, policy, null, leadId, preserveExisting: true);
            return (await ApplyTemplateBindingCoreAsync(next, policy, false, ct, null, "preserve", [], report, null)
                .ConfigureAwait(false)).Track;
        }, ct);

    private void RequireMutableTemplateCore()
    {
        if (_memoryBinding is not null)
            throw KernelException.Forbidden("Memory Planner Tracks use a fixed template and live Markdown data. Use perga_memory put or delete to change entries.");
    }

    // Keep a committed mutation and Track teardown in the same actor's order.
    // The adapter owns file writes, and the Kernel fences them to the current Lead.
    public Task<T> ExecuteMemoryMutationAsync<T>(McpSessionPrincipal caller, string generation,
        Func<CancellationToken, Task<T>> mutate, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_memoryBinding is null || !caller.IsLead || caller.BoundTaskId is not null ||
                _track.LeadSessionId != caller.SessionId)
                throw KernelException.Forbidden("Only the current Memory Track Lead may change memory.");
            RequireGovernanceSessionCore(caller.SessionId, "memory");
            RequireRuntimeBindingCore(new(caller.TrackId, caller.SessionId, caller.IsLead, caller.BoundTaskId, 0,
                caller.Token, caller.RuntimeBindingId) { AuthorizedPolicyGeneration = generation });
            return await mutate(ct).ConfigureAwait(false);
        }, ct);

    private async Task<(string Prompt, MemoryInjection? Injection)> PrepareMemoryPromptAsync(
        string prompt, string? ownerContent, AgentSessionDto session, SessionTurnDto turn, CancellationToken ct)
    {
        MemoryInjection? pending = null;
        if (session.IsLead && _memoryBinding is null)
        {
            var snapshot = await _memoryContext.GetContextSnapshotAsync(_memoryAreaId, ct).ConfigureAwait(false);
            if (snapshot is { EntryCount: > 0 })
            {
                var candidate = new MemoryInjection(turn.SessionId, turn.Epoch, snapshot.Version, snapshot.EntryCount);
                if (candidate != _lastInjectedMemoryVersion)
                {
                    pending = candidate;
                    prompt = snapshot.Content + "\n" + prompt;
                }
            }
            else if (snapshot is { EntryCount: 0 } && await HasDeliveredMemoryAsync(session, turn.Epoch, ct).ConfigureAwait(false))
            {
                pending = new(turn.SessionId, turn.Epoch, "", 0);
                prompt = $"<memory_context>\n{MemoryClearedMessage}\n</memory_context>\n\n{prompt}";
            }

            if (ownerContent is not null)
            {
                var recalled = await _memoryContext.AutoRecallAsync(_memoryAreaId, ownerContent, ct).ConfigureAwait(false);
                if (recalled.Length > 0) prompt = recalled + "\n" + prompt;
            }
        }

        if (ownerContent is not null)
        {
            var explicitMemory = await _memoryContext.HydrateMentionsAsync(_memoryAreaId, ownerContent, ct).ConfigureAwait(false);
            if (explicitMemory.Length > 0) prompt = explicitMemory + "\n" + prompt;
        }
        return (prompt, pending);
    }

    private async Task<bool> HasDeliveredMemoryAsync(AgentSessionDto session, int epoch, CancellationToken ct)
    {
        if (_lastInjectedMemoryVersion is { } previous && previous.SessionId == session.Id && previous.Epoch == epoch)
            return previous.EntryCount > 0;

        if (string.IsNullOrWhiteSpace(session.NativeThreadId))
            return false;

        var mode = await _store.GetLatestMemoryContextModeAsync(session.Id, epoch, ct).ConfigureAwait(false);
        return mode is "loaded" or "refreshed";
    }

    private void ResetMemoryInjection(string sessionId)
    {
        if (_lastInjectedMemoryVersion?.SessionId == sessionId) _lastInjectedMemoryVersion = null;
    }

    private SessionItemDto MemoryInjectionNotice(SessionTurnDto turn, MemoryInjection injection, long now)
    {
        var mode = injection.EntryCount == 0 ? "cleared"
            : _lastInjectedMemoryVersion is { EntryCount: > 0 } previous &&
              previous.SessionId == injection.SessionId && previous.Epoch == injection.Epoch ? "refreshed" : "loaded";
        return new(0, turn.SessionId, turn.Id, "system", JsonHelpers.ToElement(new
        {
            v = 1,
            kind = "memory_context",
            mode,
            entryCount = injection.EntryCount,
            content = mode == "cleared" ? MemoryClearedMessage : $"Memory context {mode} ({injection.EntryCount} entries)."
        }), now);
    }
}
