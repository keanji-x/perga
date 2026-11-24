using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;

namespace Perga.Kernel;

public sealed record MemoryPlannerTrackDto(string ScopeKey, string? AreaId, string TrackId, string LeadSessionId);

public partial interface IPergaKernel
{
    Task<MemoryPlannerTrackDto> GetOrEnsureMemoryTrackAsync(string? areaId, CancellationToken ct = default);
    Task<MailDto> DeliverMemoryProposalAsync(string fromTrackId, string? callerSessionId, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct = default);
    Task<MailDto> DeliverMemoryProposalAsync(McpSessionPrincipal caller, string generation, string scope,
        string summary, string details, bool suggestedPinned, CancellationToken ct);
}

public sealed partial class PergaKernelService
{
    private readonly IMemoryContextProvider _memoryContext;

    public async Task<MemoryPlannerTrackDto> GetOrEnsureMemoryTrackAsync(string? areaId, CancellationToken ct = default)
    {
        AreaDto? area = null;
        if (areaId is not null)
        {
            if (!await _store.IsUserAreaAsync(areaId, ct).ConfigureAwait(false))
                throw KernelException.NotFound($"Area '{areaId}' not found.");
            area = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
        }

        var scopeKey = MemoryTrackBinding.ScopeKeyFor(areaId);
        var trackId = await _store.GetMemoryTrackIdAsync(scopeKey, ct).ConfigureAwait(false);
        var track = trackId is null ? null : await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
        if (track is null)
        {
            var now = NowMs();
            var systemArea = new AreaDto(MemoryTrackBinding.SystemAreaId, "Memory", "gray", null, [], 0, now, now);
            var title = area is null ? "Global Memory" : $"{area.Name} Memory";
            var created = await CreateTrackCoreAsync(new(systemArea.Id, Title: title), ct, null,
                systemArea: systemArea, memoryScopeKey: scopeKey, memoryAreaId: areaId).ConfigureAwait(false);
            track = created.Track;
        }
        else if (track.TemplateId != MemoryTemplate.Id)
        {
            track = await Actors.GetOrActivate(track.Id).EnsureMemoryTemplateAsync(ct).ConfigureAwait(false);
        }

        return new(scopeKey, areaId, track.Id, track.LeadSessionId
            ?? throw KernelException.PreconditionFailed("The Memory Planner Track needs a Lead conversation."));
    }

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
        var target = await GetOrEnsureMemoryTrackAsync(areaId, ct).ConfigureAwait(false);
        return await SendMailCoreAsync(fromTrackId, lead, target.TrackId, $"Memory proposal: {summary}",
            $"Suggested scope: {target.ScopeKey}\nSuggested pinned: {suggestedPinned.ToString().ToLowerInvariant()}\n\n{details}",
            ct, null, authorization, requireLead: true).ConfigureAwait(false);
    }
}
