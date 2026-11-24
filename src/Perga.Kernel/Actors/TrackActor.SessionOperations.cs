using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static SessionActivityDto ReadActivityCore(ActiveTurnState? active, string? turnId, long? after)
    {
        if (active is null) return new(null, 0, turnId is not null, [], "", false);
        var snapshot = active.Activity.Read(after, active.TurnDto.Id == turnId);
        return new(active.TurnDto.Id, snapshot.Sequence, snapshot.Reset, snapshot.Events, snapshot.Text, snapshot.TextTruncated);
    }

    public async Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, SessionReadQuery query,
        InteropPrincipal caller, CancellationToken ct)
    {
        await using var events = _eventHub.Subscribe(TrackId);
        Task<SessionItemsPageDto> Read() => ReadSessionItemsAsync(sessionId, query.AfterId, query.Limit, ct,
            query.Latest, query.BeforeId, activityTurnId: query.ActivityTurnId, afterSequence: query.AfterSequence, principal: caller);
        var page = await Read().ConfigureAwait(false);
        bool Waiting() => query.ActivityTurnId is { } turnId && query.AfterSequence is { } sequence
            ? page.ActiveTurn?.Id == turnId && page.Activity is { Reset: false } activity && activity.Sequence <= sequence
            : page.State is "running" or "stopping" or "compacting" or "unknown";
        if (query.WaitMs <= 0 || !Waiting()) return page;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Min(query.WaitMs, 10_000));
        while (Waiting())
        {
            EventEnvelopeDto evt;
            try { evt = await events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return await Read().ConfigureAwait(false); }
            var relevant = evt.Payload.TryGetProperty("sessionId", out var target) && target.GetString() == sessionId ||
                evt.Kind == KernelEventKinds.SessionUpdated && evt.Payload.TryGetProperty("id", out var id) && id.GetString() == sessionId ||
                evt.Kind == KernelEventKinds.AskChanged;
            if (relevant) return await Read().ConfigureAwait(false);
        }
        return page;
    }

    public async Task<SessionMessageReceipt> SendSessionMessageAsync(string sessionId, SendSessionMessageRequest request,
        InteropPrincipal caller, CancellationToken ct)
    {
        if (request.Mode is not ("default" or "queue" or "steer") ||
            (request.Content is null) == (request.MessageId is null) ||
            request.Mode == "steer" && request.TurnId is null || request.Mode != "steer" && request.TurnId is not null ||
            request.MessageId is not null && request.Mode != "steer")
            throw KernelException.InvalidArgument("Choose content or a queued messageId and a valid delivery mode.");
        if (request.Content is not null && string.IsNullOrWhiteSpace(request.Content))
            throw KernelException.InvalidArgument("Message content cannot be empty.");
        if (request.Content is not null) ValidateRequestKey(request.RequestKey!);
        var hash = RequestIdentity.Hash(new { version = 4, request.Content, request.Mode, request.TurnId, request.ExpectedEpoch });
        var author = caller is InteropPrincipal.Session identity ? identity.SessionId : InstallationAuthor;
        var startedAt = _timeProvider.GetTimestamp();
        var delivery = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireInteropCore(caller, session, write: true);
            if (request.ExpectedEpoch is { } expectedEpoch && session.Epoch != expectedEpoch)
                throw KernelException.Conflict("The session epoch changed.");

            var existing = request.MessageId is { } id
                ? await Messages.GetAsync(sessionId, id, ct).ConfigureAwait(false)
                    ?? throw KernelException.NotFound("Message not found in this session.")
                : await Messages.FindAsync(sessionId, author, request.RequestKey!, ct).ConfigureAwait(false);
            if (request.MessageId is null)
            {
                if (existing is not null)
                {
                    RequireMatchingHash(existing.RequestHash, hash);
                    if (existing.Kind == "input" && await _store.SessionInputs.FindByIdAsync(sessionId, existing.Id, ct).ConfigureAwait(false) is { } input)
                    {
                        var replay = await ReplayInputCoreAsync(input, ct).ConfigureAwait(false);
                        return Task.FromResult(new SessionMessageReceipt(replay.MessageId, sessionId, replay.TurnId, replay.DeliveryState, true));
                    }
                    if (existing.Delivery == "dispatching" && _scheduler.TryGetSteering(sessionId, out var pending) &&
                        pending.InputId == existing.Id && pending.Attempt is Task<SteeringResult> steering)
                        return CompleteSteeringReceiptAsync(steering, sessionId, existing.TargetTurnId!, true);
                    return Task.FromResult(MessageReceipt(existing, replay: true));
                }
            }
            else
            {
                var prior = await Messages.FindQueuedSteerAsync(existing!, request.TurnId!, ct).ConfigureAwait(false);
                if (prior is not null)
                {
                    if (prior == "dispatching" && _scheduler.TryGetSteering(sessionId, out var pending) &&
                        pending.InputId == existing!.Id && pending.Attempt is Task<SteeringResult> steering)
                        return CompleteSteeringReceiptAsync(steering, sessionId, request.TurnId!, true);
                    return Task.FromResult(new SessionMessageReceipt(existing!.Id, sessionId, request.TurnId!, prior, true));
                }
            }

            RequireSessionWritableCore(session);
            RequireSessionDispatchAvailableCore(sessionId);
            _scheduler.TryGetActive(sessionId, out var active);
            var turnId = request.TurnId;
            if (turnId is not null)
            {
                if (request.MessageId is not null)
                {
                    if (existing!.Kind != "queue" || existing.Delivery != "queued" || existing.TurnId is not null)
                        throw KernelException.Conflict("Only waiting messages can be steered.");
                }
                if (active?.TurnDto.Id != turnId || active.Interrupted)
                {
                    if (!await _store.HasSessionTurnAsync(sessionId, turnId, ct).ConfigureAwait(false))
                        throw KernelException.NotFound("The target turn was not found in this session.");
                    throw KernelException.Conflict("The target turn changed. Refresh before steering.");
                }
                _taskCoordinator.RequireDispatchWorkspace(session);
                if (request.Mode == "steer" && (!active.AgentSession.SupportsNativeSteer ||
                    request.MessageId is not null && !active.CanSteerQueuedMessages))
                    throw KernelException.PreconditionFailed("The runtime does not support steering this turn.");
                var originatingTurn = caller is InteropPrincipal.Session callerSession &&
                    _scheduler.TryGetActive(callerSession.SessionId, out var callerTurn) ? callerTurn : null;
                var message = existing ?? new SessionMessage($"inp_{Guid.NewGuid():N}", sessionId,
                    author, request.RequestKey!, hash, turnId, null, request.Content!, "dispatching", NowMs(), "steer");
                if (existing is null) await Messages.InsertAsync(message, ct).ConfigureAwait(false);
                else if (!await Messages.ClaimQueuedSteerAsync(message, turnId, ct).ConfigureAwait(false))
                    throw KernelException.Conflict("This message is already being sent or has been withdrawn.");
                _scheduler.TryGetSteering(sessionId, out var previous);
                var send = RunSteeringAsync(previous.Attempt, message, turnId, existing is not null, startedAt, caller, originatingTurn);
                _scheduler.SetSteering(sessionId, (message.Id, send));
                _ = TrackBackgroundJob(() => send);
                NotifyQueueChangedCore(sessionId);
                return CompleteSteeringReceiptAsync(send, sessionId, turnId, false);
            }

            var activeTask = _taskCoordinator.TryGetActiveTaskForSessionCore(sessionId)?.Active;
            var busy = _scheduler.IsOccupied(session);
            var resuming = activeTask?.Paused == true && !busy && request.Mode == "default";
            var occupied = busy || await _scheduler.HasQueuedMessageAsync(session, ct).ConfigureAwait(false);
            if (occupied && request.Mode != "queue" && !resuming)
                throw KernelException.Conflict("The session already has an active or unresolved turn.");
            if (occupied || request.Mode == "queue")
            {
                RequireWorkspaceReadyCore();
                _taskCoordinator.RequireDispatchWorkspace(session);
                var message = new SessionMessage($"inp_{Guid.NewGuid():N}", sessionId, author,
                    request.RequestKey!, hash, null, null, request.Content!, "queued", NowMs(), "queue");
                await Messages.InsertAsync(message, ct).ConfigureAwait(false);
                // Persist the resume input before opening the existing queue for dispatch.
                if (resuming) _taskCoordinator.SetPaused(activeTask!, false);
                await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                NotifyQueueChangedCore(sessionId);
                return Task.FromResult(MessageReceipt(message));
            }
            session = session with { Status = "queued", UpdatedAtMs = NowMs() };
            var evt = SessionUpdatedEvent(session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var accepted = await _store.SessionInputs.AcceptAsync(session,
                new(request.RequestKey!, hash, request.Content!, author), evt, ct).ConfigureAwait(false);
            if (resuming) _taskCoordinator.SetPaused(activeTask!, false);
            RegisterAcceptedInput(session, accepted);
            await PublishInputEventAsync(evt).ConfigureAwait(false);
            return Task.FromResult(new SessionMessageReceipt(accepted.Receipt.MessageId, sessionId,
                accepted.Receipt.TurnId, accepted.Receipt.DeliveryState));
        }, ct).ConfigureAwait(false);
        return await delivery.WaitAsync(ct).ConfigureAwait(false);
    }

    private static SessionMessageReceipt MessageReceipt(SessionMessage message, bool replay = false) =>
        new(message.Id, message.SessionId, message.DeliveredTurnId ?? message.TurnId ?? message.TargetTurnId, message.Delivery, replay);

    private static async Task<SessionMessageReceipt> CompleteSteeringReceiptAsync(Task<SteeringResult> send,
        string sessionId, string turnId, bool replay)
    {
        var result = await send.ConfigureAwait(false);
        return new(result.InputId, sessionId, turnId, result.Delivery switch
        { "accepted" => "delivered", "unconfirmed" => "unknown", _ => result.Delivery }, replay);
    }

    public Task WithdrawSessionMessageAsync(string sessionId, string messageId,
        InteropPrincipal caller, CancellationToken ct) => RemoveQueuedMessageAsync(sessionId, messageId, ct, principal: caller);

    public async Task<AgentSessionDto> ControlSessionAsync(string sessionId, SessionControlRequest request,
        InteropPrincipal caller, CancellationToken ct)
    {
        switch (request.Action)
        {
            case "stop":
                if (request.TurnId is null) throw KernelException.InvalidArgument("stop requires turnId.");
                var (stop, snapshot) = await EnqueueAsync(async _ =>
                {
                    await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    var session = RequireSessionCore(sessionId);
                    RequireSessionMutationCore(session, caller, request.ExpectedEpoch, destructive: true);
                    var activeTask = _taskCoordinator.TryGetActiveTaskForSessionCore(sessionId)?.Active;
                    if (_scheduler.TryGetActive(sessionId, out var active) && active.TurnDto.Id == request.TurnId)
                    {
                        var stopping = RequestTurnStop(active, pauseTask: activeTask);
                        return (stopping, session with { Status = "stopping", UpdatedAtMs = NowMs() });
                    }
                    if (_runtimes.Get(sessionId).CancelLaunch(request.TurnId))
                    {
                        if (activeTask is not null)
                        {
                            _taskCoordinator.SetPaused(activeTask, true);
                            RevokeSessionRuntime(sessionId);
                        }
                        var stopping = session with { Status = "stopping", UpdatedAtMs = NowMs() };
                        await PersistAndEmitSessionUpdatedCoreAsync(stopping, stopping.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                        return (Task.CompletedTask, stopping);
                    }
                    throw KernelException.Conflict("The observed turn is no longer current.");
                }, ct).ConfigureAwait(false);
                await stop.WaitAsync(ct).ConfigureAwait(false);
                // Stopping persistence may be waiting for SQLite; do not re-enter the mailbox for the response.
                return snapshot;
            case "reconnect":
            case "reset_context":
                return await RecoverSessionAsync(sessionId, request.Action, ct, caller, request.ExpectedEpoch).ConfigureAwait(false);
            case "compact":
                return await CompactSessionAsync(sessionId, ct, caller, request.ExpectedEpoch).ConfigureAwait(false);
            default:
                throw KernelException.InvalidArgument("Unknown session control action.");
        }
    }

    public async Task<SessionInteractionResult> RespondInteractionAsync(string sessionId, string interactionId,
        RespondInteractionRequest request, InteropPrincipal caller, CancellationToken ct)
    {
        if (request.Answers is { } answer)
        {
            await _interactions.AnswerAskAsync(interactionId, answer, ct, caller, sessionId).ConfigureAwait(false);
            return new(interactionId, "ask");
        }
        if (request.ExecutionId is null || request.OptionId is null)
            throw KernelException.InvalidArgument("Approval responses require executionId and optionId.");
        await DecideApprovalAsync(request.ExecutionId, interactionId, request.OptionId, ct, caller, sessionId).ConfigureAwait(false);
        return new(interactionId, "approval");
    }
    private IAgentSession? FindOpenSessionForSettingsCore(string sessionId, string provider)
    {
        var normalized = SessionLaunchValidator.NormalizeProvider(provider);
        return _sessions.TryGetValue(sessionId, out var session) && session.ArchivedAtMs is null && session.Status != "unknown" &&
            !_scheduler.IsRecovering(sessionId) && SessionLaunchValidator.NormalizeProvider(session.Provider) == normalized &&
            _runtimes.TryGetOpenForSettings(sessionId, out var handle) &&
            SessionLaunchValidator.NormalizeProvider(handle.LaunchSpec.Provider) == normalized
            ? handle.Agent : null;
    }

    private AgentSessionDto NewSubagentSessionCore(CreateSessionRequest request, IReadOnlyList<ProviderStatus> providers,
        bool requireAvailable, AgentSessionDto? parent = null)
    {
        var now = NowMs();
        var provider = SessionLaunchValidator.NormalizeProvider(request.Provider ?? parent?.Provider ?? GetDefaultProviderCore());
        var switchesParentProvider = parent is not null && provider != SessionLaunchValidator.NormalizeProvider(parent.Provider);
        var requestedModel = SessionLaunchValidator.NormalizeRequestedModel(request.Model);
        var model = requestedModel ?? (switchesParentProvider ? null : parent?.Model ?? GetDefaultModelCore(provider));
        var canInheritReasoningEffort = !switchesParentProvider &&
            (requestedModel is null || string.Equals(requestedModel, parent?.Model, StringComparison.Ordinal));
        var effort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : request.ReasoningEffort.Trim();
        var status = SessionLaunchValidator.ValidateProvider(providers, provider, request.Provider is not null, requireAvailable);
        SessionLaunchValidator.ValidateModelAndEffort(provider, status, requestedModel, model, effort);
        var permissionMode = request.PermissionMode is null
            ? ProviderPermissionModes.ResolveInheritedMode(parent?.Provider, parent?.PermissionMode, provider)
            : RuntimePermissionModes.Resolve(provider, request.PermissionMode,
                ResolvePermissionCatalogCore(provider, status, parent?.Id));

        return new AgentSessionDto(
            Id: $"ses_{Guid.NewGuid():N}",
            TrackId: TrackId,
            IsLead: false,
            Title: string.IsNullOrWhiteSpace(request.Title) ? "Subagent" : request.Title,
            Provider: provider,
            Model: model,
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ArchivedAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            ReasoningEffort: effort ?? (canInheritReasoningEffort ? parent?.ReasoningEffort : null),
            ParentSessionId: parent?.Id, SpawnDepth: parent is null ? 0 : parent.SpawnDepth + 1,
            PermissionMode: permissionMode, SystemPrompt: request.SystemPrompt, NotifyParent: request.NotifyParent ?? "none",
            SourceSessionId: request.SourceSessionId);
    }

    private Task<SessionItemsPageDto> ReadSessionItemsAsync(string sessionId, long afterId, int limit, CancellationToken ct,
        bool latest, long? beforeId, string? activityTurnId, long? afterSequence, InteropPrincipal principal)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireInteropCore(principal, session);
            var (items, hasMoreBefore) = await _store
                .ListSessionItemsPageAsync(sessionId, afterId, limit, ct, latest, beforeId)
                .ConfigureAwait(false);
            var activeTurn = _scheduler.TryGetActive(sessionId, out var running) ? running.TurnDto : null;
            var lastTurn = (await _store.ListSessionTurnsAsync(sessionId, ct).ConfigureAwait(false))
                .MaxBy(turn => turn.TurnIndex);
            var binding = ReadTaskBindingCore(session);
            var queue = await ReadMessageQueueCoreAsync(sessionId, ct).ConfigureAwait(false);
            var settingsSession = session.Provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase)
                ? FindOpenSessionForSettingsCore(sessionId, session.Provider) : null;
            return new SessionItemsPageDto(items.Select(item => WithMessageStatus(item, running)).ToArray(),
                activeTurn, session, hasMoreBefore,
                queue.Queue, running?.CanSteerQueuedMessages == true, queue.Handoffs,
                session.Status == "queued" ? "running" : session.Status,
                ReadSessionReadonlyReasonCore(session),
                session.ArchivedAtMs is not null && _track.ClosedAtMs is null && binding is null,
                binding?.Phase == "paused", lastTurn, ReadUnknownDetailCore(session), settingsSession?.AvailablePermissionModes,
                ReadActivityCore(running, activityTurnId, afterSequence), binding, ReadCapabilitiesCore(session),
                ReadInteractionsCore(session, principal ?? new InteropPrincipal.Human()),
                ReadRuntimeActivityCore(session)?.Status, ReadRuntimeActivityCore(session)?.Tasks.Values.ToArray());
        }, ct);
    }

    private async Task<AgentSessionDto> CompactSessionAsync(string sessionId, CancellationToken ct = default, InteropPrincipal? principal = null, int? expectedEpoch = null)
    {
        var tcs = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                var compacted = await CompactSessionInternalAsync(sessionId, _actorShutdownCts.Token, principal: principal, expectedEpoch: expectedEpoch).ConfigureAwait(false);
                tcs.TrySetResult(compacted);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // ------------------- Tasks, Governance & Settlement -------------------

    // Maintenance updates durable diagnostics outside the actor. Refresh only matching
    // task versions so a concurrent retry or declaration change keeps its own evidence.
    private async Task<IAgentSession> GetOrOpenAgentSessionCoreAsync(
        AgentSessionDto session, string? overrideSystemPrompt, CancellationToken ct)
    {
        try
        {
            if (_runtimes.TryGetOpen(session.Id, out var existing) && existing.Agent.NativeThreadId is { } nativeId &&
                string.Equals(existing.LaunchSpec.Provider, session.Provider, StringComparison.OrdinalIgnoreCase))
            {
                session = session with { NativeThreadId = session.NativeThreadId ?? nativeId };
                await EnqueueAsync(async _ =>
                {
                    var current = RequireSessionCore(session.Id);
                    if (current.NativeThreadId is null)
                    {
                        var identified = current with { NativeThreadId = nativeId, UpdatedAtMs = NowMs() };
                        await PersistAndEmitSessionUpdatedCoreAsync(identified, identified.UpdatedAtMs, ct).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
            }
            var forceReopen = _runtimes.TryGetOpen(session.Id, out var previous) && !HasRuntimeCredential(session.Id, previous);
            var handle = await _runtimes.Get(session.Id).EnsureOpenAsync(EffectiveAgentRuntime,
                SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode),
                session.Provider,
                bindingId => EnqueueAsync(_ => BuildAgentLaunchSpecCoreAsync(session, overrideSystemPrompt, bindingId, ct), ct),
                agent =>
                {
                    RequireConfirmedConfiguration(session, agent, agent.ConfigurationState);
                }, _options.SessionStartupTimeout, ct, forceReopen: forceReopen).ConfigureAwait(false);
            // Finish handing off the owned handle before startup cancellation can be observed.
            await EnqueueAsync(_ => RegisterOpenAgentSessionCoreAsync(session, handle.Agent, handle.LaunchSpec, CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);
            return handle.Agent;
        }
        catch (Exception error)
        {
            if (error is ProviderSessionNotFoundException || _runtimes.IsShutdownUnconfirmed(session.Id))
                await EnqueueAsync(async _ =>
                {
                    if (!await MarkShutdownUnconfirmedIfNeededAsync(session.Id, error is ProviderSessionNotFoundException).ConfigureAwait(false))
                        await MarkSessionUnknownCoreAsync(RequireSessionCore(session.Id), "session_lost", reconnectInvalidated: true).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            if (error is SessionConfigurationException) throw KernelException.PreconditionFailed(error.Message);
            throw;
        }
    }

    private static void RequireConfirmedConfiguration(AgentSessionDto session, IAgentSession agent, ConfirmedConfiguration? confirmed)
    {
        var requested = SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode);
        if (confirmed?.Selection != requested || confirmed != agent.ConfigurationState ||
            confirmed?.ContextUsageRevision is { } revision && revision != agent.ContextUsageRevision)
            throw KernelException.PreconditionFailed("The provider did not confirm the complete session configuration.");
    }

    private async Task<HarnessLaunchSpec> BuildAgentLaunchSpecCoreAsync(
        AgentSessionDto session, string? overrideSystemPrompt, string bindingId, CancellationToken ct)
    {
        _taskCoordinator.RequireDispatchWorkspace(session);
        var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        var activeTask = _taskCoordinator.TryGetActiveTaskForSessionCore(session.Id);

        var mcpBaseUri = await Env.ResolveMcpBaseUriAsync(_mcpBridge.ResolveBaseUri(), ct).ConfigureAwait(false);
        var current = RequireSessionCore(session.Id);
        RequireSessionWritableCore(current);
        if (_closingTrack || _deletingTrack || current.ConfigVersion != session.ConfigVersion || current.Epoch != session.Epoch)
            throw KernelException.Conflict("The session changed during provider startup.");
        ct.ThrowIfCancellationRequested();
        McpSessionRegistration mcpReg;
        try
        {
            mcpReg = _mcpBridge.RegisterSession(
                TrackId,
                session.Id,
                session.IsLead,
                activeTask?.Task.Id,
                activeTask?.Active.RunCount,
                _track.EnabledPlugins,
                runtimeBindingId: bindingId, baseUriOverride: mcpBaseUri);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // The mailbox operation can outlive its canceled waiter and launch cleanup.
            if (ct.IsCancellationRequested) _mcpBridge.RevokeSession(session.Id, bindingId);
        }

        var systemPrompt = overrideSystemPrompt ?? BuildDefaultSystemPromptCore(session);
        if (session.SystemPrompt is { } customPrompt) systemPrompt += "\n\n" + customPrompt;
        return new HarnessLaunchSpec(
            Provider: session.Provider,
            Model: session.Model,
            Cwd: mounted.MergedCwd,
            ResumeNativeThreadId: session.NativeThreadId,
            SystemPrompt: systemPrompt,
            McpServers: mcpReg.McpServers,
            ReasoningEffort: session.ReasoningEffort,
            StateKey: session.Id,
            PermissionMode: session.PermissionMode);
    }

    private async Task SwitchLeadSessionCoreAsync(string newLeadSessionId, long now, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(newLeadSessionId, out var target))
        {
            throw KernelException.NotFound($"Session '{newLeadSessionId}' does not exist on track '{TrackId}'.");
        }

        if (_taskCoordinator.TryGetActiveOwningTaskCore(newLeadSessionId) is { } owningTask)
        {
            throw KernelException.Forbidden(
                $"Contract subagent session '{newLeadSessionId}' bound to task '{owningTask.Id}' cannot become Lead.");
        }

        var oldLeadId = _track.LeadSessionId;
        if (oldLeadId is not null && _sessions.TryGetValue(oldLeadId, out var oldLead) && oldLead.IsLead)
        {
            var demoted = oldLead with { IsLead = false, UpdatedAtMs = now };
            _sessions[oldLeadId] = demoted;
            await PersistAndEmitSessionUpdatedCoreAsync(demoted, now, ct).ConfigureAwait(false);
        }

        var promoted = target with { IsLead = true, UpdatedAtMs = now };
        _sessions[newLeadSessionId] = promoted;
        _track = _track with { LeadSessionId = newLeadSessionId, UpdatedAtMs = now };
        _mcpBridge.UpdateLeadStatus(TrackId, newLeadSessionId);
        await PersistAndEmitSessionUpdatedCoreAsync(promoted, now, ct).ConfigureAwait(false);
    }

    private AgentSessionDto RequireSessionCore(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw KernelException.NotFound($"Session '{sessionId}' not found on track '{TrackId}'.");
        }

        return session;
    }

    private AgentSessionDto RequireGovernanceSessionCore(string callerSessionId, string toolName)
    {
        var session = RequireSessionCore(callerSessionId);
        if (_taskCoordinator.TryGetActiveOwningTaskCore(callerSessionId) is { } task)
        {
            throw KernelException.Forbidden(
                $"Governance tool '{toolName}' requires an unbound session, but session '{callerSessionId}' is bound to task '{task.Id}'.");
        }

        return session;
    }

    private (AgentSessionDto Session, TrackTasks.ActiveTaskRunState Active) RequireBoundTaskSessionCore(string callerSessionId, string toolName)
    {
        var session = RequireSessionCore(callerSessionId);
        if (_taskCoordinator.TryGetActiveTaskForSessionCore(callerSessionId) is not { } bound)
        {
            throw KernelException.Forbidden(
                $"Completion tool '{toolName}' requires an active bound task session, but session '{callerSessionId}' has no active task.");
        }

        return (session, bound.Active);
    }

    private async Task PersistAndEmitSessionUpdatedCoreAsync(AgentSessionDto session, long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.SessionUpdated,
            Payload: JsonHelpers.ToElement(session),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertSessionAsync(session, evt, ct).ConfigureAwait(false);
        _sessions[session.Id] = session;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private IReadOnlyList<AgentSessionDto> OrderedSessionsCore()
    {
        return _sessions.Values
            .OrderByDescending(s => s.IsLead)
            .ThenBy(s => s.CreatedAtMs)
            .ToArray();
    }

    private string GetDefaultProviderCore()
    {
        if (_track.LeadSessionId is { } leadId && _sessions.TryGetValue(leadId, out var lead))
        {
            return lead.Provider;
        }

        return _options.DefaultProvider;
    }

    private string? ResolveTaskWorkerModelCore(string provider) =>
        SessionLaunchValidator.NormalizeProvider(provider) == SessionLaunchValidator.NormalizeProvider(GetDefaultProviderCore())
            ? GetDefaultModelCore(provider) : null;

    private string? GetDefaultModelCore(string provider)
    {
        var normalized = SessionLaunchValidator.NormalizeProvider(provider);
        if (_track.LeadSessionId is { } leadId &&
            _sessions.TryGetValue(leadId, out var lead) &&
            SessionLaunchValidator.NormalizeProvider(lead.Provider) == normalized)
        {
            return lead.Model;
        }

        return SessionLaunchValidator.NormalizeProvider(_options.DefaultProvider) == normalized
            ? _options.DefaultModel
            : null;
    }

    private int NextTurnIndexCore(string sessionId)
    {
        _nextTurnIndexBySession.TryGetValue(sessionId, out var current);
        var next = current + 1;
        _nextTurnIndexBySession[sessionId] = next;
        return next;
    }

}
