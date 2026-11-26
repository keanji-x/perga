using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed record MemoryPlannerTrackDto(string ScopeKey, string? AreaId, string TrackId, string LeadSessionId);
public sealed record MemoryDistillResultDto(string ScopeKey, string? AreaId, string MemoryTrackId, string LeadSessionId, string MailId);

public partial interface IPergaKernel
{
    Task<MemoryPlannerTrackDto> GetOrEnsureMemoryTrackAsync(string? areaId, CancellationToken ct = default);
    Task<MemoryDistillResultDto> DistillTrackToMemoryAsync(string trackId, string? scope = "auto", string? focus = null,
        string? requestKey = null, CancellationToken ct = default);
    Task<MailDto> DeliverMemoryProposalAsync(string fromTrackId, string? callerSessionId, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct = default);
    Task<MailDto> DeliverMemoryProposalAsync(McpSessionPrincipal caller, string generation, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct);
}

public sealed partial class PergaKernelService
{
    private const int MaxDistillFocusLength = 2_000;
    private readonly IMemoryContextProvider _memoryContext;

    public async Task<MemoryPlannerTrackDto> GetOrEnsureMemoryTrackAsync(string? areaId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        AreaDto? area = null;
        if (areaId is not null)
        {
            if (!await _store.IsUserAreaAsync(areaId, ct).ConfigureAwait(false))
                throw KernelException.NotFound($"Area '{areaId}' not found.");
            area = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
        }

        var scopeKey = MemoryTrackBinding.ScopeKeyFor(areaId);
        if (await FindMemoryTrackAsync(scopeKey, ct).ConfigureAwait(false) is { } track)
            return await EnsureExistingMemoryTrackAsync(scopeKey, areaId, track.Id, ct).ConfigureAwait(false);

        TaskCompletionSource<MemoryPlannerTrackDto>? owned = null;
        Task<MemoryPlannerTrackDto> flight;
        lock (_memoryEnsureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inFlightMemoryEnsures.TryGetValue(scopeKey, out var active))
            {
                owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                active = owned.Task;
                _ = active.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _inFlightMemoryEnsures.Add(scopeKey, active);
            }
            flight = active;
        }

        if (owned is not null)
            _ = Task.Run(() => RunMemoryEnsureFlightAsync(scopeKey, area, owned));
        return await flight.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<MemoryDistillResultDto> DistillTrackToMemoryAsync(string trackId, string? scope = "auto", string? focus = null,
        string? requestKey = null, CancellationToken ct = default)
    {
        var normalizedScope = scope ?? "auto";
        if (normalizedScope is not ("auto" or "area" or "global"))
            throw KernelException.InvalidArgument("Memory distillation scope must be 'auto', 'area', or 'global'.");
        var normalizedFocus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();
        if (normalizedFocus is { Length: > MaxDistillFocusLength })
            throw KernelException.InvalidArgument($"Memory distillation focus must be at most {MaxDistillFocusLength} characters.");

        var creationKey = CreateKey("memory-distill", "", requestKey,
            new { TrackId = trackId, Scope = normalizedScope, Focus = normalizedFocus });
        var source = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{trackId}' not found.");
        if (await _store.FindMemoryBindingByTrackIdAsync(trackId, ct).ConfigureAwait(false) is not null)
            throw KernelException.Forbidden("Memory Planner Tracks cannot be distilled into memory.");
        var hasUserArea = normalizedScope != "global" && await _store.IsUserAreaAsync(source.AreaId, ct).ConfigureAwait(false);
        if (normalizedScope == "area" && !hasUserArea)
            throw KernelException.InvalidArgument("Only Tracks inside a user Area can distill to Area memory.");

        var target = await GetOrEnsureMemoryTrackAsync(hasUserArea ? source.AreaId : null, ct).ConfigureAwait(false);
        var title = source.Title.Length <= 100 ? source.Title : source.Title[..100];
        var body = $"""
            Distill durable takeaways from Track "{source.Title}" (`{source.Id}`) into memory scope `{target.ScopeKey}`.
            Inspect the source with report_read using its Track ID. Use track_list with its Area ID (`{source.AreaId}`)
            and includeClosed: true if you need related Track context.
            Check existing entries with perga_memory list or recall, then curate the takeaways into Markdown with perga_memory put.
            Ask the human via user_ask if a takeaway conflicts with a pinned rule.
            """;
        if (normalizedFocus is not null)
            body += $"\n\nFocus: {normalizedFocus}";
        var mail = new MailDto($"mail_{Guid.NewGuid():N}", source.Id, target.TrackId,
            $"Distill track to memory: {title}", body, ReadAtMs: null, CreatedAtMs: NowMs());
        var stored = await Actors.GetOrActivate(target.TrackId)
            .ReceiveMailAsync(mail, ct, creationKey: creationKey)
            .ConfigureAwait(false);
        return new(target.ScopeKey, target.AreaId, target.TrackId, target.LeadSessionId, stored.Id);
    }

    private async Task<TrackDto?> FindMemoryTrackAsync(string scopeKey, CancellationToken ct)
    {
        var trackId = await _store.GetMemoryTrackIdAsync(scopeKey, ct).ConfigureAwait(false);
        return trackId is null ? null : await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
    }

    private async Task RunMemoryEnsureFlightAsync(string scopeKey, AreaDto? area,
        TaskCompletionSource<MemoryPlannerTrackDto> completion)
    {
        var work = EnsureMemoryTrackAsync(scopeKey, area);
        try
        {
            await ((Task)work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            lock (_memoryEnsureGate)
            {
                _inFlightMemoryEnsures.Remove(scopeKey);
                completion.TrySetFromTask(work);
            }
        }
    }

    private async Task<MemoryPlannerTrackDto> EnsureMemoryTrackAsync(string scopeKey, AreaDto? area)
    {
        var ct = _disposeCts.Token;
        ct.ThrowIfCancellationRequested();
        if (await FindMemoryTrackAsync(scopeKey, ct).ConfigureAwait(false) is { } track)
            return await EnsureExistingMemoryTrackAsync(scopeKey, area?.Id, track.Id, ct).ConfigureAwait(false);

        var now = NowMs();
        var systemArea = new AreaDto(MemoryTrackBinding.SystemAreaId, "Memory", "gray", null, [], 0, now, now);
        var title = area is null ? "Global Memory" : $"{area.Name} Memory";
        var provider = await ResolveDefaultProviderAsync(systemArea.ExecutionTargetId, ct).ConfigureAwait(false);
        var created = await CreateTrackCoreAsync(new(systemArea.Id, Title: title, Provider: provider), ct, null,
            systemArea: systemArea, memoryScopeKey: scopeKey, memoryAreaId: area?.Id).ConfigureAwait(false);
        return ToMemoryPlannerTrack(scopeKey, area?.Id, created.Track);
    }

    private async Task<MemoryPlannerTrackDto> EnsureExistingMemoryTrackAsync(string scopeKey, string? areaId,
        string trackId, CancellationToken ct)
    {
        Task<TrackDto> ensuring;
        lock (_memoryEnsureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ensuring = Actors.GetOrActivate(trackId).EnsureMemoryTemplateAsync(ct);
        }
        var track = await ensuring.ConfigureAwait(false);
        return ToMemoryPlannerTrack(scopeKey, areaId, track);
    }

    private static MemoryPlannerTrackDto ToMemoryPlannerTrack(string scopeKey, string? areaId, TrackDto track) =>
        new(scopeKey, areaId, track.Id, track.LeadSessionId
            ?? throw KernelException.PreconditionFailed("The Memory Planner Track needs a Lead conversation."));
    public Task<MailDto> DeliverMemoryProposalAsync(string fromTrackId, string? callerSessionId, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct = default) =>
        DeliverMemoryProposalCoreAsync(fromTrackId, callerSessionId, scope, summary, details, suggestedPinned, null, ct);

    public Task<MailDto> DeliverMemoryProposalAsync(McpSessionPrincipal caller, string generation, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct)
    {
        if (!caller.IsLead || caller.BoundTaskId is not null)
            throw KernelException.Forbidden("Only an unbound Track Lead may propose memory.");
        var authorization = new InteropPrincipal.Session(caller.TrackId, caller.SessionId, caller.IsLead,
            caller.BoundTaskId, 0, caller.Token, caller.RuntimeBindingId) { AuthorizedPolicyGeneration = generation };
        return DeliverMemoryProposalCoreAsync(caller.TrackId, caller.SessionId, scope, summary, details,
            suggestedPinned, authorization, ct);
    }

    private async Task<MailDto> DeliverMemoryProposalCoreAsync(string fromTrackId, string? callerSessionId, string scope,
        string summary, string details, bool suggestedPinned, InteropPrincipal.Session? authorization, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 140 || summary.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(details) || details.Length > 16 * 1024)
            throw KernelException.InvalidArgument("A memory proposal needs a one-line summary (up to 140 characters) and details (up to 16384 characters).");

        var source = await _store.GetTrackAsync(fromTrackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{fromTrackId}' not found.");
        if (source.LeadSessionId is not { } lead || callerSessionId is not null && lead != callerSessionId ||
            await _store.FindMemoryBindingByTrackIdAsync(fromTrackId, ct).ConfigureAwait(false) is not null)
            throw KernelException.Forbidden("Only a normal Track Lead may propose memory.");

        var areaId = scope switch
        {
            "global" => null,
            "area" => source.AreaId,
            _ => throw KernelException.InvalidArgument("Memory proposal scope must be 'area' or 'global'.")
        };
        if (areaId is not null && !await _store.IsUserAreaAsync(areaId, ct).ConfigureAwait(false))
            throw KernelException.InvalidArgument("Today does not belong to a user Area; propose to 'global' instead.");
        var target = await GetOrEnsureMemoryTrackAsync(areaId, ct).ConfigureAwait(false);
        return await SendMailCoreAsync(fromTrackId, lead, target.TrackId, $"Memory proposal: {summary}",
            $"Suggested scope: {target.ScopeKey}\nSuggested pinned: {suggestedPinned.ToString().ToLowerInvariant()}\n\n{details}",
            ct, null, authorization, requireLead: true).ConfigureAwait(false);
    }
}
