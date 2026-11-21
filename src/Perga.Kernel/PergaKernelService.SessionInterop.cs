using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public partial interface IPergaKernel
{
    Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string trackId, SessionListFilter filter, InteropPrincipal caller, CancellationToken ct);
    Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionAsync(string trackId, CreateSessionRequest request, string requestKey, InteropPrincipal caller, CancellationToken ct);
    Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, SessionReadQuery query, InteropPrincipal caller, CancellationToken ct);
    Task<AgentSessionDto> UpdateSessionAsync(string sessionId, UpdateSessionRequest request, InteropPrincipal caller, CancellationToken ct);
    Task<SessionMessageReceipt> SendSessionMessageAsync(string sessionId, SendSessionMessageRequest request, InteropPrincipal caller, CancellationToken ct);
    Task WithdrawSessionMessageAsync(string sessionId, string messageId, string? expectedAttemptId, InteropPrincipal caller, CancellationToken ct);
    Task<AgentSessionDto> ControlSessionAsync(string sessionId, SessionControlRequest request, InteropPrincipal caller, CancellationToken ct);
    Task<SessionInteractionResult> RespondInteractionAsync(string sessionId, string interactionId, RespondInteractionRequest request, InteropPrincipal caller, CancellationToken ct);
}

public sealed partial class PergaKernelService
{
    public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string trackId, SessionListFilter filter, InteropPrincipal caller, CancellationToken ct) =>
        Actors.GetOrActivate(trackId).ListSessionsAsync(filter, caller, ct);
    public Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionAsync(string trackId, CreateSessionRequest request,
        string requestKey, InteropPrincipal caller, CancellationToken ct) => Actors.GetOrActivate(trackId).CreateSessionAsync(request, requestKey, caller, ct);
    public async Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, SessionReadQuery query, InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .GetSessionItemsAsync(sessionId, query, caller, ct).ConfigureAwait(false);
    public async Task<AgentSessionDto> UpdateSessionAsync(string sessionId, UpdateSessionRequest request, InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .UpdateSessionAsync(sessionId, request, caller, ct).ConfigureAwait(false);
    public async Task<SessionMessageReceipt> SendSessionMessageAsync(string sessionId, SendSessionMessageRequest request, InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .SendSessionMessageAsync(sessionId, request, caller, ct).ConfigureAwait(false);
    public async Task WithdrawSessionMessageAsync(string sessionId, string messageId, string? expectedAttemptId, InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .WithdrawSessionMessageAsync(sessionId, messageId, expectedAttemptId, caller, ct).ConfigureAwait(false);
    public async Task<AgentSessionDto> ControlSessionAsync(string sessionId, SessionControlRequest request, InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .ControlSessionAsync(sessionId, request, caller, ct).ConfigureAwait(false);
    public async Task<SessionInteractionResult> RespondInteractionAsync(string sessionId, string interactionId, RespondInteractionRequest request,
        InteropPrincipal caller, CancellationToken ct) =>
        await Actors.GetOrActivate(await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false))
            .RespondInteractionAsync(sessionId, interactionId, request, caller, ct).ConfigureAwait(false);
}
