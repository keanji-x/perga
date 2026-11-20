using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore : ISessionInputStore, ISessionMessageStore
{
    private readonly Dictionary<(string SessionId, bool Worker, string Id), PersistedSessionInput> _inputs = new();
    private readonly Dictionary<(string SessionId, bool Worker, string Id), SessionMessage> _messages = new();
    private readonly Dictionary<(string Track, string Key), StoredSessionCreation> _sessionCreations = new();
    private readonly Dictionary<(string Id, string Turn), string> _queuedSteers = new();

    private static (string SessionId, bool Worker, string Id) Key(SessionMessage message) =>
        (message.SessionId, message.AttemptId is not null, message.Id);
    private static (string SessionId, bool Worker, string Id) Key(PersistedSessionInput input) =>
        (input.Receipt.SessionId, false, input.Receipt.InputId);

    public ISessionInputStore SessionInputs => this;
    public ISessionMessageStore SessionMessages => this;


    public Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(GetTurnsForSession(sessionId));

    public Task<PersistedSessionInput?> FindByKeyAsync(string sessionId, string key, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_messages.Values.Where(m => m.SessionId == sessionId && m.AttemptId is null && m.RequestKey == key && m.Kind == "input")
                .Select(m => _inputs.GetValueOrDefault(Key(m))).FirstOrDefault());
    }

    public Task<PersistedSessionInput?> FindByIdAsync(string sessionId, string inputId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_inputs.GetValueOrDefault((sessionId, false, inputId)) is { } input && input.Receipt.SessionId == sessionId ? input : null);
    }

    public Task<PersistedSessionInput?> FindUnresolvedAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_inputs.Values.LastOrDefault(i => i.Receipt.SessionId == sessionId &&
                i.Receipt.TurnStatus == "unknown" && i.Receipt.ResolvedAtMs is null));
    }

    public Task<StoredSessionCreation?> FindCreationAsync(SessionCreateIntent intent, CancellationToken ct)
    {
        lock (_gate)
        {
            var creation = _sessionCreations.GetValueOrDefault((intent.TrackId, intent.Key));
            return Task.FromResult(creation is null ? null : creation with
            {
                Session = creation.Session is { } session ? _sessions.GetValueOrDefault(session.Id) : null,
                FirstInput = creation.FirstInput is { } input ? _inputs.GetValueOrDefault(Key(input)) : null
            });
        }
    }

    private PersistedSessionInput BindInputLocked(AgentSessionDto session, SessionMessage message, bool followUp)
    {
        var index = (_turnsBySession.GetValueOrDefault(session.Id)?.Select(t => t.TurnIndex).DefaultIfEmpty().Max() ?? 0) + 1;
        var turnId = $"trn_{index:D6}_{Guid.NewGuid():N}";
        var input = new PersistedSessionInput(new(message.Id, turnId, session.Id, "accepted", "queued", null),
            message.RequestHash, message.Content, index, session.Epoch, session.Provider, session.Model, session.ReasoningEffort, followUp);
        _inputs.Add(Key(message), input);
        _messages[Key(message)] = message with { TurnId = turnId };
        var turn = new SessionTurnDto(turnId, session.Id, index, session.Epoch, "user", TestJson.FromObject(new { }), "queued", null, null, null, null);
        var recorded = RecordTurnAndItemsLocked(turn, [new(0, session.Id, turnId, "user",
            TestJson.FromObject(new { v = 1, content = message.Content, triggerKind = "user", inputId = message.Id }), session.UpdatedAtMs)], session);
        input = input with { UserItemId = recorded.Turn.UserItemId };
        _inputs[Key(message)] = input;
        return input;
    }

    public Task<PersistedSessionInput> AcceptAsync(AgentSessionDto session, SessionInputRequest request, EventEnvelopeDto sessionEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_sessions[session.Id].Status is "queued" or "running" or "unknown")
                throw KernelException.Conflict("The session already has an active or unresolved turn.");
            var input = BindInputLocked(session, new($"inp_{Guid.NewGuid():N}", session.Id, null, "owner:installation", request.Key,
                request.Hash, null, null, request.Content, "queued", session.UpdatedAtMs, "input"), false);
            AppendEventLocked(sessionEvent);
            return Task.FromResult(input);
        }
    }

    public Task<PersistedSessionInput?> PromoteQueuedBatchAsync(AgentSessionDto session, SessionMessage message, EventEnvelopeDto sessionEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_messages.GetValueOrDefault(Key(message)) is not { Delivery: "queued", TurnId: null } ||
                _sessions[session.Id].Status is "queued" or "running" or "unknown")
                return Task.FromResult<PersistedSessionInput?>(null);
            var batch = _messages.Values.Where(m => m.SessionId == session.Id && m.AttemptId is null && m.Delivery == "queued" && m.TurnId is null).ToArray();
            var input = BindInputLocked(session, message, true);
            foreach (var next in batch.Where(m => m.Id != message.Id))
            {
                _messages[Key(next)] = next with { TurnId = input.Receipt.TurnId };
                var itemIds = AppendSessionItemsLocked(session.Id, [new(0, session.Id, input.Receipt.TurnId, "user",
                    TestJson.FromObject(new { v = 1, content = next.Content, triggerKind = "user", inputId = next.Id }), session.UpdatedAtMs)]);
                _inputs[Key(next)] = input with { Receipt = input.Receipt with { InputId = next.Id }, Content = next.Content, UserItemId = itemIds[0] };
            }
            input = input with { Content = string.Join("\n\n", batch.Select(m => m.Content)) };
            AppendEventLocked(sessionEvent);
            return Task.FromResult<PersistedSessionInput?>(input);
        }
    }

    public Task<PersistedSessionInput?> CreateAsync(SessionCreateIntent intent, AgentSessionDto session, SessionInputRequest? firstInput, EventEnvelopeDto sessionEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            _sessions[session.Id] = session;
            var input = firstInput is null ? null : BindInputLocked(session, new($"inp_{Guid.NewGuid():N}", session.Id, null,
                "owner:installation", firstInput.Key, firstInput.Hash, null, null, firstInput.Content, "queued", session.UpdatedAtMs, "input"), false);
            _sessionCreations.Add((intent.TrackId, intent.Key), new(intent.Hash, session, input));
            AppendEventLocked(sessionEvent);
            return Task.FromResult(input);
        }
    }

    public Task<bool> ClaimAsync(PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            var stored = _inputs[Key(input)];
            if (stored.Receipt.DeliveryState != "accepted") return Task.FromResult(false);
            foreach (var member in _inputs.Values.Where(i => i.Receipt.TurnId == input.Receipt.TurnId).ToArray())
            {
                _inputs[Key(member)] = member with { Receipt = member.Receipt with { DeliveryState = "dispatching", TurnStatus = "running" } };
                _messages[Key(member)] = _messages[Key(member)] with { Delivery = "dispatching" };
            }
            var turns = _turnsBySession[session.Id];
            var idx = turns.FindIndex(t => t.Id == input.Receipt.TurnId);
            turns[idx] = turns[idx] with { Status = "running", StartedAtMs = session.UpdatedAtMs };
            _sessions[session.Id] = session;
            AppendEventLocked(sessionEvent);
            return Task.FromResult(true);
        }
    }

    public Task MarkDeliveredAsync(PersistedSessionInput input, string? nativeThreadId, string? providerTurnId, CancellationToken ct)
    {
        lock (_gate)
        {
            var stored = _inputs[Key(input)];
            foreach (var member in _inputs.Values.Where(i => i.Receipt.TurnId == input.Receipt.TurnId).ToArray())
            {
                _inputs[Key(member)] = member with { Receipt = member.Receipt with { DeliveryState = "delivered" } };
                _messages[Key(member)] = _messages[Key(member)] with { Delivery = "delivered", DeliveredTurnId = input.Receipt.TurnId };
            }
            if (nativeThreadId is not null) _sessions[input.Receipt.SessionId] = _sessions[input.Receipt.SessionId] with { NativeThreadId = nativeThreadId };
            return Task.CompletedTask;
        }
    }

    public Task SettleAsync(PersistedSessionInput input, string delivery, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session, IReadOnlyList<EventEnvelopeDto> events, CancellationToken ct)
    {
        lock (_gate)
        {
            var stored = _inputs[Key(input)];
            foreach (var member in _inputs.Values.Where(i => i.Receipt.TurnId == input.Receipt.TurnId).ToArray())
            {
                _inputs[Key(member)] = member with { Receipt = member.Receipt with { DeliveryState = delivery, TurnStatus = turn.Status } };
                _messages[Key(member)] = _messages[Key(member)] with { Delivery = delivery };
            }
            RecordTurnAndItemsLocked(turn, items, session);
            foreach (var evt in events) AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Task ResolveAsync(PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            var stored = _inputs[Key(input)];
            foreach (var member in _inputs.Values.Where(i => i.Receipt.TurnId == input.Receipt.TurnId).ToArray())
                _inputs[Key(member)] = member with { Receipt = member.Receipt with { ResolvedAtMs = session.UpdatedAtMs } };
            _sessions[session.Id] = session;
            AppendEventLocked(sessionEvent);
            return Task.CompletedTask;
        }
    }

    public Task<SessionMessage?> FindAsync(string sessionId, bool worker, string authorId, string requestKey, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_messages.Values.FirstOrDefault(m => m.SessionId == sessionId && (m.AttemptId is not null) == worker && m.AuthorId == authorId && m.RequestKey == requestKey));
    }

    public Task<SessionMessage?> GetAsync(string sessionId, bool worker, string messageId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_messages.GetValueOrDefault((sessionId, worker, messageId)) is { } m && m.SessionId == sessionId && (m.AttemptId is not null) == worker ? m : null);
    }

    public Task<SessionMessage?> NextAsync(string sessionId, string? attemptId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_messages.Values.FirstOrDefault(m => m.SessionId == sessionId && m.AttemptId == attemptId && m.Delivery == "queued" && m.TurnId is null));
    }

    public Task InsertAsync(SessionMessage message, CancellationToken ct)
    {
        lock (_gate) _messages.Add(Key(message), message);
        return Task.CompletedTask;
    }

    public Task<bool> TransitionAsync(SessionMessage message, string expected, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_messages.GetValueOrDefault(Key(message))?.Delivery != expected) return Task.FromResult(false);
            _messages[Key(message)] = message;
            return Task.FromResult(true);
        }
    }

    public Task<string?> FindQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_queuedSteers.GetValueOrDefault((message.Id, turnId)));
    }

    public Task<bool> ClaimQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_messages.GetValueOrDefault(Key(message)) is { Delivery: "queued", TurnId: null } && _queuedSteers.TryAdd((message.Id, turnId), "dispatching"));
    }

    public Task<string> SettleQueuedSteerAsync(SessionMessage message, string turnId, string outcome, SessionItemDto? item, CancellationToken ct)
    {
        lock (_gate)
        {
            _queuedSteers[(message.Id, turnId)] = outcome;
            if (outcome == "delivered")
            {
                _messages[Key(message)] = message with { TurnId = turnId, DeliveredTurnId = turnId, Delivery = outcome };
                if (item is not null) AppendSessionItemsLocked(message.SessionId, [item]);
            }
            return Task.FromResult(outcome);
        }
    }

    public async Task<bool> DeliverSteerAsync(SessionMessage message, SessionItemDto item, CancellationToken ct)
    {
        if (!await TransitionAsync(message, "dispatching", ct)) return false;
        await AppendSessionItemsAsync(message.SessionId, [item], ct);
        return true;
    }

    public Task<IReadOnlyList<SessionMessage>> ListQueueAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<SessionMessage>>(_messages.Values.Where(m => m.SessionId == sessionId && m.TurnId is null && m.Delivery is "queued" or "dispatching" or "unknown").ToArray());
    }

    public Task<IReadOnlyList<string>> ListHandoffsAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_messages.Values.Where(m => m.SessionId == sessionId && m.DeliveredTurnId is not null).Select(m => m.Id).ToArray());
    }

    public Task<IReadOnlyList<SessionMessage>> RecentWorkerAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<SessionMessage>>(_messages.Values.Where(m => m.SessionId == sessionId && m.AttemptId is not null).ToArray());
    }

    public Task<IReadOnlyList<(string TrackId, string SessionId)>> ListRecoverableQueuesAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<(string, string)>>(_messages.Values.Where(m => m.Delivery == "queued" && m.TurnId is null)
            .Select(m => (_sessions[m.SessionId].TrackId, m.SessionId)).Distinct().ToArray());
    }
}
