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
    private sealed class TerminalLifecycle(TrackActor actor)
    {
        private readonly Dictionary<string, TerminalDto> _terminals = new(StringComparer.Ordinal);

        public TerminalDto[] Snapshot() => _terminals.Values.OrderBy(t => t.CreatedAtMs).ToArray();
        public void Hydrate(IEnumerable<TerminalDto> terminals)
        {
            _terminals.Clear();
            foreach (var terminal in terminals) _terminals[terminal.Id] = terminal;
        }
        public async Task TeardownCoreAsync(IExecutionEnvironment? env, CancellationToken ct)
        {
            foreach (var term in _terminals.Values.Where(t => t.Status == TerminalStatus.Running).ToArray())
            {
                try
                {
                    if (env is not null)
                        await env.Terminals.KillAsync(term.Id, graceMs: 1000, ct).ConfigureAwait(false);
                    _terminals.Remove(term.Id);
                }
                catch
                {
                    // Keep failed kills available for a retry if teardown cannot unmount.
                }
            }
        }

        public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(CancellationToken ct = default)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<TerminalDto>)_terminals.Values.OrderBy(t => t.CreatedAtMs).ToArray();
            }, ct);
        }

        public async Task<TerminalDto> OpenTerminalAsync(string? callerSessionId, OpenTerminalRequest req, CancellationToken ct = default)
        {
            await actor.WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
            return await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var mounted = await actor.EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);

                var effectiveCwd = mounted.MergedCwd;
                if (callerSessionId is not null)
                {
                    actor.RequireSessionCore(callerSessionId);
                }

                if (!string.IsNullOrWhiteSpace(req.Cwd))
                {
                    effectiveCwd = Path.IsPathRooted(req.Cwd)
                        ? req.Cwd
                        : Path.GetFullPath(Path.Combine(effectiveCwd, req.Cwd));
                }

                if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
                {
                    var existing = _terminals.Values.FirstOrDefault(t =>
                        string.Equals(t.IdempotencyKey, req.IdempotencyKey, StringComparison.Ordinal) &&
                        t.Status == TerminalStatus.Running);
                    if (existing is not null)
                    {
                        return existing;
                    }
                }

                var terminalId = $"trm_{Guid.NewGuid():N}";
                var program = string.IsNullOrWhiteSpace(req.Program) ? "/bin/bash" : req.Program!;
                var ensureReq = new TermEnsureRequest(
                    TerminalId: terminalId,
                    Program: program,
                    Args: req.Args ?? [],
                    Cwd: effectiveCwd,
                    Env: req.Env ?? new Dictionary<string, string>(),
                    Cols: req.Cols,
                    Rows: req.Rows);

                await actor.EffectiveTerminalClient.EnsureAsync(ensureReq, ct).ConfigureAwait(false);

                var now = actor.NowMs();
                var terminal = new TerminalDto(
                    Id: terminalId,
                    TrackId: actor.TrackId,
                    SessionId: callerSessionId,
                    IdempotencyKey: req.IdempotencyKey,
                    Title: string.IsNullOrWhiteSpace(req.Title) ? "Terminal" : req.Title!,
                    Program: program,
                    Cwd: effectiveCwd,
                    Status: TerminalStatus.Running,
                    ExitCode: null,
                    CreatedAtMs: now,
                    ExitedAtMs: null);

                _terminals[terminal.Id] = terminal;
                await PersistAndEmitTerminalChangedCoreAsync(terminal, now, ct).ConfigureAwait(false);
                return terminal;
            }, ct).ConfigureAwait(false);
        }

        public async Task<TermObserveResult> ObserveTerminalAsync(TermObserveRequest req, CancellationToken ct = default)
        {
            await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                await actor.EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
                if (!_terminals.ContainsKey(req.TerminalId))
                {
                    throw KernelException.NotFound($"Terminal '{req.TerminalId}' not found on track '{actor.TrackId}'.");
                }
            }, ct).ConfigureAwait(false);

            var observed = await actor.EffectiveTerminalClient.ObserveAsync(req, ct).ConfigureAwait(false);
            if (observed.Exited)
            {
                await actor.EnqueueAsync(async _ =>
                {
                    if (_terminals.TryGetValue(req.TerminalId, out var term) && term.Status != TerminalStatus.Exited)
                    {
                        var now = actor.NowMs();
                        var updated = term with
                        {
                            Status = TerminalStatus.Exited,
                            ExitCode = observed.ExitCode,
                            ExitedAtMs = now
                        };
                        _terminals[updated.Id] = updated;
                        await PersistAndEmitTerminalChangedCoreAsync(updated, now, ct).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
            }

            return observed;
        }

        public async Task<TermActionResult> SendTerminalInputAsync(TermActionRequest req, CancellationToken ct = default)
        {
            await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                await actor.EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
                if (!_terminals.ContainsKey(req.TerminalId))
                {
                    throw KernelException.NotFound($"Terminal '{req.TerminalId}' not found on track '{actor.TrackId}'.");
                }
            }, ct).ConfigureAwait(false);

            return await actor.EffectiveTerminalClient.SendActionAsync(req, ct).ConfigureAwait(false);
        }

        public async Task ResizeTerminalAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct = default)
        {
            await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (!_terminals.ContainsKey(terminalId))
                {
                    throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{actor.TrackId}'.");
                }
            }, ct).ConfigureAwait(false);

            await actor.EffectiveTerminalClient.ResizeAsync(terminalId, cols, rows, ct).ConfigureAwait(false);
        }

        public async Task<TerminalDto> CloseTerminalAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
        {
            await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (!_terminals.ContainsKey(terminalId))
                {
                    throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{actor.TrackId}'.");
                }
            }, ct).ConfigureAwait(false);

            await actor.EffectiveTerminalClient.KillAsync(terminalId, graceMs, ct).ConfigureAwait(false);

            return await actor.EnqueueAsync(async _ =>
            {
                if (!_terminals.Remove(terminalId, out var existing))
                {
                    throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{actor.TrackId}'.");
                }

                var now = actor.NowMs();
                var closed = existing with
                {
                    Status = TerminalStatus.Exited,
                    ExitCode = existing.ExitCode ?? 0,
                    ExitedAtMs = existing.ExitedAtMs ?? now
                };

                var payload = JsonHelpers.ToElement(new
                {
                    id = closed.Id,
                    terminalId = closed.Id,
                    trackId = closed.TrackId,
                    sessionId = closed.SessionId,
                    idempotencyKey = closed.IdempotencyKey,
                    title = closed.Title,
                    program = closed.Program,
                    cwd = closed.Cwd,
                    status = "exited",
                    exitCode = closed.ExitCode,
                    createdAtMs = closed.CreatedAtMs,
                    exitedAtMs = closed.ExitedAtMs,
                    deleted = true
                });
                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: actor._track.AreaId,
                    TrackId: actor.TrackId,
                    Kind: KernelEventKinds.TerminalChanged,
                    Payload: payload,
                    CreatedAtMs: now);

                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                await actor._store.DeleteTerminalAsync(actor.TrackId, terminalId, evt, ct).ConfigureAwait(false);
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, ct).ConfigureAwait(false);
                return closed;
            }, ct).ConfigureAwait(false);
        }

        public async Task PersistAndEmitTerminalChangedCoreAsync(TerminalDto terminal, long now, CancellationToken ct)
        {
            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: actor._track.AreaId,
                TrackId: actor.TrackId,
                Kind: KernelEventKinds.TerminalChanged,
                Payload: JsonHelpers.ToElement(terminal),
                CreatedAtMs: now);
            await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
            await actor._store.UpsertTerminalAsync(terminal, evt, ct).ConfigureAwait(false);
            await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, ct).ConfigureAwait(false);
        }

    }
}
