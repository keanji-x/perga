using System.Collections.ObjectModel;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<ReportSnapshot> ReadReportSnapshotAsync(
        string trackId, string? sectionH1, IReadOnlySet<string>? blockIds, bool includeTemplateViews, CancellationToken ct = default)
    {
        var snapshot = await _store.LoadTrackAggregateAsync(trackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{trackId}' not found.");
        var report = snapshot.Report;
        if (sectionH1 is not null || blockIds is not null)
        {
            report = report with
            {
                LeadingComment = string.Empty,
                Blocks = ReportBlockSelection.FilterBlocks(report.Blocks, sectionH1, blockIds)
            };
        }

        var views = includeTemplateViews ? ProjectSnapshotViews(snapshot, report.Blocks) : null;
        return new ReportSnapshot(
            report.DocRev,
            report.Summary,
            _reportEngine.RenderMarkdown(report, withMarkers: true),
            report.Blocks,
            snapshot.Track.TemplateId is not null || snapshot.Track.DataRev > 0 ? snapshot.Track.DataRev : null,
            views);
    }

    private IReadOnlyDictionary<string, JsonElement>? ProjectSnapshotViews(
        TrackAggregateSnapshot snapshot, IReadOnlyList<ReportBlock> blocks)
    {
        var names = ReportBlockSelection.GetReferencedViews(blocks);
        if (names.Count == 0 || string.IsNullOrWhiteSpace(snapshot.TemplateSnapshotSource))
            return null;

        CompiledPolicy policy;
        try
        {
            policy = Templates.GetOrCompileSnapshot(snapshot.TemplateSnapshotSource);
        }
        catch (Exception ex)
        {
            // The report remains readable if the stored template cannot be loaded.
            System.Diagnostics.Trace.TraceWarning("Could not load report template for Track {0}: {1}", snapshot.Track.Id, ex.Message);
            return null;
        }

        var track = snapshot.Track;
        var sessions = snapshot.Sessions.ToDictionary(session => session.Id,
            session => session with { IsLead = session.Id == track.LeadSessionId }, StringComparer.Ordinal);
        var spec = TrackFlowParser.ParsePersisted(track.FlowSpecJson, track.FlowRev);
        // The committed spec and task facts are sufficient for snapshot views.
        var flow = TrackFlowProjector.BuildProjection(spec, filePath: null, snapshot.Tasks, sessions, [],
            ReadOnlyDictionary<string, ActiveWatchState>.Empty,
            text => TemplateFlow.Interpolate(text, track.TemplateInputs, policy, _policyVm));
        var facts = KernelFactsSnapshot.BuildKernelFactsSnapshot(track.Id, snapshot.Tasks, sessions, flow);
        var views = _policyVm.ProjectViews(policy, track.TemplateInputs, track.Data, facts, names).Views;
        return views.Count > 0 ? views : null;
    }
}
