using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // State transitions run in the owning Actor mailbox; external phases explicitly re-enter it.
    private sealed class TrackInteractions(TrackActor actor)
    {
        private sealed record AskWaiter(TaskCompletionSource<JsonElement> Completion, ExecutionControl? RuntimeControl);
        private readonly ConcurrentDictionary<string, AskWaiter> _pendingAskWaiters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AskDto> _asks = new(StringComparer.Ordinal);

        public AskDto[] OpenAsks() => _asks.Values.Where(a => a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray();
        public void Hydrate(IEnumerable<AskDto> asks)
        {
            _asks.Clear();
            foreach (var ask in asks) _asks[ask.Id] = ask;
        }
        public void CancelWaitersCore(CancellationToken ct)
        {
            foreach (var kvp in _pendingAskWaiters.ToArray())
            {
                _pendingAskWaiters.TryRemove(kvp.Key, out _);
                kvp.Value.Completion.TrySetCanceled(ct);
            }
        }

        public bool HasRuntimeWaiter(ExecutionControl control) =>
            _pendingAskWaiters.Values.Any(waiter => ReferenceEquals(waiter.RuntimeControl, control));

        public void ExpireRuntimeWaiters(ExecutionControl control)
        {
            foreach (var entry in _pendingAskWaiters)
                if (ReferenceEquals(entry.Value.RuntimeControl, control) && _pendingAskWaiters.TryRemove(entry))
                    entry.Value.Completion.TrySetCanceled();
        }

        public async Task<JsonElement> AskUserAsync(
            string? callerSessionId,
            IReadOnlyList<AskQuestionDto> questions,
            CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        {
            if (questions.Count == 0)
            {
                throw KernelException.InvalidArgument("At least one question is required for user_ask.");
            }

            if (questions.Any(question => question.IsMultiSelect))
                throw KernelException.InvalidArgument("user_ask does not support isMultiSelect: true.");

            Interlocked.Increment(ref actor._inFlightExternalOps);
            try
            {
                var (id, waiter) = await actor.EnqueueAsync(async _ =>
                {
                    ct.ThrowIfCancellationRequested();
                    await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    if (callerSessionId is not null) actor.RequireSessionCore(callerSessionId);
                    var control = authorization is null ? null : actor.RequireRuntimeBindingCore(authorization).RuntimeControl;
                    return await CreateWakeAskCoreAsync(questions, callerSessionId, control, ct).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);

                try
                {
                    var answer = await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
                    if (authorization is not null)
                        await actor.EnqueueAsync(_ =>
                        {
                            actor.RequireRuntimeBindingCore(authorization);
                            return Task.CompletedTask;
                        }, ct).ConfigureAwait(false);
                    return answer;
                }
                catch (OperationCanceledException) when (authorization is not null && !ct.IsCancellationRequested)
                {
                    throw KernelException.Conflict("The originating runtime closed while waiting for an answer.");
                }
                finally
                {
                    _pendingAskWaiters.TryRemove(id, out _);
                }
            }
            finally
            {
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

        public async Task<AskDto> AnswerAskAsync(string askId, JsonElement answer, CancellationToken ct = default, InteropPrincipal? principal = null, string? sessionId = null)
        {
            var outcome = await actor.EnqueueAsync<(AskDto AnsweredAsk, bool ShouldWakeLead)>(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (!_asks.TryGetValue(askId, out var existingAsk))
                {
                    throw KernelException.NotFound($"Ask '{askId}' not found on track '{actor.TrackId}'.");
                }

                if (principal is not null)
                {
                    var responder = actor.RequireInteropCore(principal, write: true);
                    if (existingAsk.SessionId != sessionId) throw KernelException.NotFound("Ask does not belong to this session.");
                    if (principal is InteropPrincipal.Session &&
                        (responder!.Id != existingAsk.TargetSessionId && !responder.IsLead))
                        throw KernelException.Forbidden("This ask is delegated to another session.");
                }
                if (existingAsk.Status != AskStatus.Open)
                {
                    throw KernelException.Conflict($"Ask '{askId}' is already '{existingAsk.Status}'.");
                }

                if (existingAsk.Delivery != AskDelivery.Wake)
                    throw KernelException.Conflict("This approval request is no longer pending.");

                var now = actor.NowMs();
                var clonedAnswer = answer.Clone();
                var answered = existingAsk with
                {
                    Status = AskStatus.Answered,
                    Answer = clonedAnswer,
                    AnsweredAtMs = now
                };

                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: actor._track.AreaId,
                    TrackId: actor.TrackId,
                    Kind: KernelEventKinds.AskChanged,
                    Payload: JsonHelpers.ToElement(answered),
                    CreatedAtMs: now);
                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                await actor._store.UpsertAskAsync(answered, evt, ct).ConfigureAwait(false);

                // The transaction accepted this answer. Neither request cancellation nor
                // event catch-up failure may reopen it or strand its original waiter.
                _asks[askId] = answered;
                _pendingAskWaiters.TryRemove(askId, out var waiter);
                try
                {
                    await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    // Complete inside the actor: EnqueueAsync's caller can stop waiting
                    // on cancellation before this committed operation returns.
                    waiter?.Completion.TrySetResult(clonedAnswer);
                }

                var shouldWakeLead = actor._track.ClosedAtMs is null && !string.IsNullOrEmpty(actor._track.LeadSessionId);
                return (answered, shouldWakeLead);
            }, ct).ConfigureAwait(false);

            if (outcome.ShouldWakeLead)
            {
                if (outcome.AnsweredAsk.SessionId is { } targetId)
                    await actor.EnqueueAsync(_ => actor.PokeSessionCoreAsync(targetId), ct).ConfigureAwait(false);
                else await actor.PokePendingWakesAsync(ct).ConfigureAwait(false);
            }

            return outcome.AnsweredAsk;
        }

        public async Task<MailDto> SendCrossTrackMailAsync(
            string callerSessionId,
            string? toTrackId,
            string summary,
            string body,
            Func<MailDto, Action?, CancellationToken, Task> deliverToTargetTrack,
            CancellationToken ct, string? replyToMailId, InteropPrincipal.Session? authorization = null, bool requireLead = false)
        {
            var (mail, authorizeDelivery) = await actor.EnqueueAsync<(MailDto, Action?)>(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                actor.RequireGovernanceSessionCore(callerSessionId, "mail_send");
                if (requireLead && actor._track.LeadSessionId != callerSessionId)
                    throw KernelException.Forbidden("Only the current Track Lead may propose memory.");
                if ((toTrackId is null) == (replyToMailId is null) ||
                    string.IsNullOrWhiteSpace(toTrackId ?? replyToMailId))
                    throw KernelException.InvalidArgument("Specify exactly one of toTrackId and replyToMailId.");
                if (replyToMailId is not null)
                {
                    var parent = await actor._store.FindReceivedMailAsync(actor.TrackId, replyToMailId, ct).ConfigureAwait(false)
                        ?? throw KernelException.NotFound("Original mail was not found in this Track's inbox.");
                    toTrackId = parent.FromTrackId;
                }

                var open = authorization is null ? null : actor.RequireRuntimeBindingCore(authorization);
                var originatingTurn = authorization is not null && actor._scheduler.TryGetActive(authorization.SessionId, out var active)
                    ? active : null;
                var now = actor.NowMs();
                var mail = new MailDto(
                    Id: $"mail_{Guid.NewGuid():N}",
                    FromTrackId: actor.TrackId,
                    ToTrackId: toTrackId!,
                    Summary: summary,
                    Body: body,
                    ReadAtMs: null,
                    CreatedAtMs: now,
                    ReplyToMailId: replyToMailId);
                return (mail, open is null ? null : () =>
                {
                    if (requireLead && actor._mcpBridge.ResolveToken(authorization!.Token)?.IsLead != true)
                        throw KernelException.Forbidden("Only the current Track Lead may propose memory.");
                    actor.RequireRuntimeWriteFence(authorization!, open, originatingTurn: originatingTurn);
                });
            }, ct).ConfigureAwait(false);

            await deliverToTargetTrack(mail, authorizeDelivery, ct).ConfigureAwait(false);
            return mail;
        }

        public async Task ReceiveMailAsync(MailDto mail, CancellationToken ct, Action? authorizeDelivery)
        {
            var shouldWake = await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: actor._track.AreaId,
                    TrackId: actor.TrackId,
                    Kind: KernelEventKinds.MailReceived,
                    Payload: JsonHelpers.ToElement(mail),
                    CreatedAtMs: mail.CreatedAtMs);

                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                var requestWake = actor._track.ClosedAtMs is null && actor._track.LeadSessionId is not null;
                await actor._store.UpsertMailAsync(mail, evt, ct, requestWake, beforeWrite: authorizeDelivery).ConfigureAwait(false);
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, CancellationToken.None).ConfigureAwait(false);
                return requestWake;
            }, ct).ConfigureAwait(false);

            if (shouldWake)
                await actor.PokePendingWakesAsync(actor._actorShutdownCts.Token).ConfigureAwait(false);
        }

        public async Task<MailDto> ReceiveDistillMailAsync(MailDto mail, bool idempotent, CancellationToken ct)
        {
            var (stored, shouldWake) = await actor.EnqueueAsync(async _ =>
            {
                ct.ThrowIfCancellationRequested();
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var requestWake = actor._track.ClosedAtMs is null && actor._track.LeadSessionId is not null;
                if (idempotent)
                {
                    var existingMails = await actor._store.ListMailsAsync(actor.TrackId, ct: ct).ConfigureAwait(false);
                    var existing = existingMails.FirstOrDefault(m => string.Equals(m.Id, mail.Id, StringComparison.Ordinal));
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.FromTrackId, mail.FromTrackId, StringComparison.Ordinal) ||
                            !string.Equals(existing.Body, mail.Body, StringComparison.Ordinal))
                            throw KernelException.Conflict("Idempotency-Key was already used with a different distillation request.");
                        return (existing, requestWake);
                    }
                }

                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: actor._track.AreaId,
                    TrackId: actor.TrackId,
                    Kind: KernelEventKinds.MailReceived,
                    Payload: JsonHelpers.ToElement(mail),
                    CreatedAtMs: mail.CreatedAtMs);

                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                await actor._store.UpsertMailAsync(mail, evt, ct, requestWake,
                    beforeWrite: () => ct.ThrowIfCancellationRequested()).ConfigureAwait(false);
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, CancellationToken.None).ConfigureAwait(false);
                return (mail, requestWake);
            }, CancellationToken.None).ConfigureAwait(false);

            if (shouldWake)
                await actor.PokePendingWakesAsync(actor._actorShutdownCts.Token).ConfigureAwait(false);
            return stored;
        }

        public Task<IReadOnlyList<MailDto>> ListMailsAsync(bool unreadOnly = false, CancellationToken ct = default)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                return await actor._store.ListMailsAsync(actor.TrackId, unreadOnly, ct).ConfigureAwait(false);
            }, ct);
        }

        public Task<MailDto> ReadMailAsync(string mailId, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var mails = await actor._store.ListMailsAsync(actor.TrackId, unreadOnly: false, ct).ConfigureAwait(false);
                var target = mails.FirstOrDefault(m => string.Equals(m.Id, mailId, StringComparison.Ordinal))
                             ?? throw KernelException.NotFound($"Mail '{mailId}' not found on track '{actor.TrackId}'.");

                if (target.ReadAtMs is not null)
                {
                    return target;
                }

                actor.RequireMcpAuthorizationCore(authorization);
                var now = actor.NowMs();
                var readMail = target with { ReadAtMs = now };
                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: actor._track.AreaId,
                    TrackId: actor.TrackId,
                    Kind: KernelEventKinds.MailReceived,
                    Payload: JsonHelpers.ToElement(readMail),
                    CreatedAtMs: now);

                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                await actor._store.UpsertMailAsync(readMail, evt, ct, requestWake: false).ConfigureAwait(false);
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, ct).ConfigureAwait(false);
                return readMail;
            }, ct);
        }

        private async Task<(string Id, TaskCompletionSource<JsonElement> Waiter)> CreateWakeAskCoreAsync(
            IReadOnlyList<AskQuestionDto> questions,
            string? callerSessionId,
            ExecutionControl? runtimeControl,
            CancellationToken ct)
        {
            var now = actor.NowMs();
            var ask = new AskDto(
                Id: $"ask_{Guid.NewGuid():N}",
                TrackId: actor.TrackId,
                Status: AskStatus.Open,
                Questions: questions,
                Answer: null,
                CreatedAtMs: now,
                AnsweredAtMs: null,
                Delivery: AskDelivery.Wake,
                SessionId: callerSessionId,
                TurnId: callerSessionId is not null && actor._scheduler.TryGetActive(callerSessionId, out var owner) ? owner.TurnDto.Id : null,
                TargetSessionId: callerSessionId is not null && actor.RequireSessionCore(callerSessionId).ParentSessionId is { } parentId &&
                    actor._sessions.TryGetValue(parentId, out var parent) && parent.ArchivedAtMs is null ? parentId : null);

            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: actor._track.AreaId,
                TrackId: actor.TrackId,
                Kind: KernelEventKinds.AskChanged,
                Payload: JsonHelpers.ToElement(ask),
                CreatedAtMs: now);
            await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
            await actor._store.UpsertAskAsync(ask, evt, ct).ConfigureAwait(false);
            _asks[ask.Id] = ask;
            _pendingAskWaiters[ask.Id] = new(tcs, runtimeControl);
            try
            {
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return (ask.Id, tcs);
            }
            catch
            {
                _pendingAskWaiters.TryRemove(ask.Id, out _);
                throw;
            }
        }

    }
}
