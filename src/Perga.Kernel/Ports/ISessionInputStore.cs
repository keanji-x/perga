using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record SessionInputRequest(string Key, string Hash, string Content);

public sealed record PersistedSessionInput(
    SessionInputReceipt Receipt, string RequestHash, string Content, int TurnIndex, int Epoch,
    string Provider, string? Model, string? ReasoningEffort, bool IsQueuedFollowUp = false, long? UserItemId = null);

public sealed record SessionCreateIntent(string TrackId, string Key, string Hash);

public sealed record StoredSessionCreation(
    string RequestHash, AgentSessionDto? Session, PersistedSessionInput? FirstInput);

public interface ISessionInputStore
{
    Task<PersistedSessionInput?> FindByKeyAsync(string sessionId, string key, CancellationToken ct);
    Task<PersistedSessionInput?> FindByIdAsync(string sessionId, string inputId, CancellationToken ct);
    Task<PersistedSessionInput?> FindUnresolvedAsync(string sessionId, CancellationToken ct);
    Task<StoredSessionCreation?> FindCreationAsync(SessionCreateIntent intent, CancellationToken ct);
    Task<PersistedSessionInput> AcceptAsync(
        AgentSessionDto session, SessionInputRequest request, EventEnvelopeDto sessionEvent, CancellationToken ct);
    Task<PersistedSessionInput?> PromoteQueuedBatchAsync(AgentSessionDto session, SessionMessage message,
        EventEnvelopeDto sessionEvent, CancellationToken ct);
    Task<PersistedSessionInput?> CreateAsync(
        SessionCreateIntent intent, AgentSessionDto session, SessionInputRequest? firstInput,
        EventEnvelopeDto sessionEvent, CancellationToken ct);
    Task<bool> ClaimAsync(PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct);
    Task MarkDeliveredAsync(PersistedSessionInput input, string? nativeThreadId, string? providerTurnId, CancellationToken ct);
    Task SettleAsync(
        PersistedSessionInput input, string delivery, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session, IReadOnlyList<EventEnvelopeDto> events, CancellationToken ct);
    Task ResolveAsync(
        PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct);
}
