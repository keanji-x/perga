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
    private sealed class TrackReport(TrackActor actor)
    {
        private ReportDocument _report = null!;

        public ReportDocument Document => _report;
        public void Hydrate(ReportDocument document) => _report = document;

        public Task<ReportReadResult> ReadReportAsync(string? callerSessionId = null, CancellationToken ct = default)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (callerSessionId is not null && !actor._sessions.ContainsKey(callerSessionId))
                {
                    throw KernelException.NotFound($"Session '{callerSessionId}' not found on track '{actor.TrackId}'.");
                }

                var rendered = actor._reportEngine.RenderMarkdown(_report, withMarkers: true);
                var knownRevs = _report.Blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
                var anchor = new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true);

                return new ReportReadResult(
                    DocRev: _report.DocRev,
                    Summary: _report.Summary,
                    RenderedMarkdown: rendered,
                    Blocks: _report.Blocks,
                    Tasks: actor._taskCoordinator.Snapshot().Select(actor.ProjectFlowTaskCore).ToArray(),
                    Anchor: anchor,
                    Diagnostics: ReportWidgetValidator.Diagnose(_report, actor._policy, actor._policyVm, actor._track.TemplateInputs),
                    DataRev: actor._policy is not null || actor._track.DataRev > 0 ? actor._track.DataRev : null);
            }, ct);
        }

        public async Task<ReportReadResult> CommitReportBySessionAsync(
            string callerSessionId,
            ReportCommitRequest request,
            CancellationToken ct = default,
            InteropPrincipal.Session? authorization = null)
        {
            Interlocked.Increment(ref actor._inFlightExternalOps);
            try
            {
                var (completed, approval) = await actor.EnqueueAsync<(ReportReadResult?, PolicyApproval?)>(async _ =>
                {
                    await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    actor.RequirePolicyAvailableCore();
                    actor.RequireMcpAuthorizationCore(authorization);

                    var (preview, editorRole, diagnostics) = PreviewReportCommitCore(callerSessionId, request);
                    if (actor._policy is not null)
                    {
                        var prevMd = actor._reportEngine.RenderMarkdown(_report, withMarkers: false);
                        var nextMd = actor._reportEngine.RenderMarkdown(preview.NextDocument, withMarkers: false);
                        var eval = actor._policyVm.Before(actor._policy, "report.commit", actor.TrackId, JsonHelpers.ToElement(new
                        {
                            editorRole, prevMarkdown = prevMd, nextMarkdown = nextMd
                        }), actor._track.TemplateInputs, actor._track.Data,
                            actor.BuildPolicySessionContextCore(actor.RequireSessionCore(callerSessionId)));

                        if (eval is GuardDecision.Deny denied)
                            throw KernelException.PolicyDenied(denied.Reason);
                        if (eval is GuardDecision.AskHuman ask)
                        {
                            if (authorization is null)
                                throw KernelException.Conflict("A policy approval requires an authorized runtime.");
                            var open = actor.RequireRuntimeBindingCore(authorization);
                            actor._scheduler.TryGetActive(callerSessionId, out var approvalTurn);
                            return (null, actor.ApplyGuardDecisionCore(ask, callerSessionId, open.RuntimeControl, ct, turn: approvalTurn)!);
                        }
                    }

                    return (await FinalizeReportCommitCoreAsync(preview, callerSessionId,
                        request.CommitMessage ?? $"Report commit ({editorRole})", diagnostics, ct, authorization).ConfigureAwait(false), null);
                }, CancellationToken.None).ConfigureAwait(false);

                if (completed is not null) return completed;
                try { await actor.RequirePolicyApprovalAsync(approval!, ct).ConfigureAwait(false); }
                finally { actor.ExpireApproval(approval!.Pending); }

                return await actor.EnqueueAsync(async _ =>
                {
                    actor.RequireMcpAuthorizationCore(authorization);
                    var (preview, editorRole, diagnostics) = PreviewReportCommitCore(callerSessionId, request);
                    return await FinalizeReportCommitCoreAsync(preview, callerSessionId,
                        request.CommitMessage ?? $"Report commit ({editorRole}, human-approved)", diagnostics, ct, authorization).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

        private (ReportCommitResult Preview, string EditorRole, IReadOnlyList<ReportDiagnosticDto> Diagnostics)
            PreviewReportCommitCore(string callerSessionId, ReportCommitRequest request)
        {
            var editorRole = actor.RequireSessionCore(callerSessionId).IsLead ? "lead" : "subagent";
            var preview = actor._reportEngine.ApplyCommit(_report, request.Anchor, request.Ops, request.Summary);
            ReportAuthoring.Validate(preview.NextDocument, _report);
            var diagnostics = ReportWidgetValidator.Diagnose(
                preview.NextDocument, actor._policy, actor._policyVm, actor._track.TemplateInputs, _report, rejectInvalidChanges: true);
            return (preview, editorRole, diagnostics);
        }

        private async Task<ReportReadResult> FinalizeReportCommitCoreAsync(
            ReportCommitResult commitResult,
            string? authorSessionId,
            string commitMessage,
            IReadOnlyList<ReportDiagnosticDto> diagnostics,
            CancellationToken ct,
            InteropPrincipal.Session? authorization)
        {
            var now = actor.NowMs();
            var compressedBlocks = actor._reportEngine.CompressZstd(commitResult.NextDocument.Blocks);
            var reportPayload = JsonHelpers.ToElement(new
            {
                docRev = commitResult.NextDocument.DocRev,
                summary = commitResult.NextDocument.Summary,
                changes = commitResult.Changes
            });
            var reportEvt = new EventEnvelopeDto(
                EventId: null,
                AreaId: actor._track.AreaId,
                TrackId: actor.TrackId,
                Kind: KernelEventKinds.ReportUpdated,
                Payload: reportPayload,
                CreatedAtMs: now);

            await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
            await actor._store.SaveReportAsync(
                actor.TrackId,
                commitResult.NextDocument,
                compressedBlocks,
                authorSessionId,
                commitMessage,
                reportEvt,
                ct,
                actor.CaptureMcpWriteFenceCore(authorization)).ConfigureAwait(false);

            _report = commitResult.NextDocument;
            var committedCt = actor._actorShutdownCts.Token;
            await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, reportEvt, committedCt).ConfigureAwait(false);

            var rendered = actor._reportEngine.RenderMarkdown(_report, withMarkers: true);
            var knownRevs = _report.Blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
            return new ReportReadResult(
                DocRev: _report.DocRev,
                Summary: _report.Summary,
                RenderedMarkdown: rendered,
                Blocks: _report.Blocks,
                Tasks: actor._taskCoordinator.Snapshot().Select(actor.ProjectFlowTaskCore).ToArray(),
                Anchor: new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true),
                Diagnostics: diagnostics,
                Changes: commitResult.Changes);
        }
    }
}
