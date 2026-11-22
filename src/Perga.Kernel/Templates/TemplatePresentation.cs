using Perga.Contracts;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class TemplatePresentation
{
    internal static HeaderViewEvaluation Evaluate(TrackDto track, CompiledPolicy? policy, IPolicyVm vm) =>
        policy is null ? new(null, null) : vm.EvaluateHeaderViewWithDiagnostic(policy,
            new PolicyHeaderViewContext(track.Id, track.TemplateInputs), track.Data);

    internal static TrackDto Project(TrackDto track, CompiledPolicy? policy, IPolicyVm vm) =>
        Project(track, policy, Evaluate(track, policy, vm).View);

    internal static TrackDto Project(TrackDto track, CompiledPolicy? policy, TemplateHeaderView? view)
    {
        return track with
        {
            TemplateTitle = policy?.Manifest.Title,
            TemplateHeaderView = view is null ? null : new TemplateHeaderViewDto(
                view.Badges?.Select(b => new TemplateHeaderBadgeDto(b.Label, b.Tone)).ToArray(),
                view.Notice is { } n ? new TemplateHeaderNoticeDto(n.Text, n.Tone) : null,
                view.Metrics?.Select(m => new TemplateHeaderMetricDto(m.Label, m.Value, m.Delta, m.Tone, m.Sub, m.Spark)).ToArray(),
                view.Rules?.Select(r => new TemplateHeaderRuleDto(r.Label, r.Text)).ToArray())
        };
    }
}
