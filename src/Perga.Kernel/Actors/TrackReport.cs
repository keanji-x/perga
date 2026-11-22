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
                    Tasks: actor._taskCoordinator.Snapshot(),
                    Anchor: anchor,
                    Diagnostics: [],
                    DataRev: actor._policy is not null || actor._track.DataRev > 0 ? actor._track.DataRev : null);
            }, ct);
        }

        public async Task<ReportReadResult> CommitReportBySessionAsync(
            string callerSessionId,
            ReportCommitRequest request,
            CancellationToken ct = default,
            McpExecutionOrigin? origin = null,
            string? expectedPolicyGeneration = null)
        {
            var approvalTurn = actor._scheduler.TryGetActive(callerSessionId, out var callingTurn) ? callingTurn : null;
            Interlocked.Increment(ref actor._inFlightExternalOps);
            try
            {
                var generation = expectedPolicyGeneration;
                var (completed, approval) = await actor.EnqueueAsync<(ReportReadResult?, PolicyApproval?)>(async _ =>
                {
                    await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    actor.RequirePolicyAvailableCore();
                    if (generation is not null && generation != actor._policyGeneration)
                        throw KernelException.PolicyDenied("Active policy changed after authorization.");
                    generation = actor._policyGeneration;

                    var (preview, editorRole) = PreviewReportCommitCore(callerSessionId, request);
                    if (actor._policy is not null)
                    {
                        var prevMd = actor._reportEngine.RenderMarkdown(_report, withMarkers: false);
                        var nextMd = actor._reportEngine.RenderMarkdown(preview.NextDocument, withMarkers: false);
                        var eval = actor._policyVm.Before(actor._policy, "report.commit", actor.TrackId, JsonHelpers.ToElement(new
                        {
                            editorRole, prevMarkdown = prevMd, nextMarkdown = nextMd
                        }), actor._track.TemplateInputs, actor._track.Data);

                        if (eval is GuardDecision.Deny denied)
                            throw KernelException.PolicyDenied(denied.Reason);
                        if (eval is GuardDecision.AskHuman ask)
                        {
                            actor.RequireApprovalOrigin(callerSessionId, approvalTurn, origin);
                            return (null, actor.ApplyGuardDecisionCore(ask, callerSessionId, approvalTurn, ct)!);
                        }
                    }

                    return (await FinalizeReportCommitCoreAsync(preview, callerSessionId,
                        request.CommitMessage ?? $"Report commit ({editorRole})", ct).ConfigureAwait(false), null);
                }, CancellationToken.None).ConfigureAwait(false);

                if (completed is not null) return completed;
                try { await actor.RequirePolicyApprovalAsync(approval!, ct).ConfigureAwait(false); }
                finally { actor.ExpireApproval(approval!.Pending); }

                return await actor.EnqueueAsync(async _ =>
                {
                    if (actor._policyGeneration != generation)
                        throw KernelException.PolicyDenied("Active policy changed during report approval.");
                    var (preview, editorRole) = PreviewReportCommitCore(callerSessionId, request);
                    return await FinalizeReportCommitCoreAsync(preview, callerSessionId,
                        request.CommitMessage ?? $"Report commit ({editorRole}, human-approved)", ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

        private (ReportCommitResult Preview, string EditorRole)
            PreviewReportCommitCore(string callerSessionId, ReportCommitRequest request)
        {
            var editorRole = actor.RequireSessionCore(callerSessionId).IsLead ? "lead" : "subagent";
            var preview = actor._reportEngine.ApplyCommit(_report, request.Anchor, request.Ops, request.Summary);
            var historicalBlocks = _report.Blocks.Where(block => IsRetiredBlock(block.Kind))
                .ToDictionary(block => block.Id, StringComparer.Ordinal);
            foreach (var block in preview.NextDocument.Blocks)
            {
                if (historicalBlocks.TryGetValue(block.Id, out var previous))
                {
                    if (previous.Kind == block.Kind && previous.Rev == block.Rev && previous.SectionH1 == block.SectionH1 &&
                        previous.Markdown == block.Markdown && JsonHelpers.JsonEquals(previous.Payload, block.Payload))
                        continue;
                    RejectRetiredBlock(previous.Kind);
                }
                if (IsRetiredBlock(block.Kind))
                    RejectRetiredBlock(block.Kind);
            }

            return (preview, editorRole);
        }

        private static bool IsRetiredBlock(ReportBlockKind kind) =>
            kind is ReportBlockKind.Task or ReportBlockKind.Checkpoints;

        private static void RejectRetiredBlock(ReportBlockKind kind) =>
            throw KernelException.InvalidArgument(kind == ReportBlockKind.Task
                ? "Author tasks in flow.json and submit them with flow_commit."
                : "Use Markdown prose or a widget instead of authoring a checkpoints block.");

        private async Task<ReportReadResult> FinalizeReportCommitCoreAsync(
            ReportCommitResult commitResult,
            string? authorSessionId,
            string commitMessage,
            CancellationToken ct)
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
                ct).ConfigureAwait(false);

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
                Tasks: actor._taskCoordinator.Snapshot(),
                Anchor: new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true),
                Diagnostics: [],
                Changes: commitResult.Changes);
        }
    }
}
