using Perga.Contracts;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class TemplatePresentation
{
    internal static TrackDto Project(TrackDto track, CompiledPolicy? policy, IPolicyVm vm)
    {
        var view = policy is null ? null : vm.EvaluateHeaderView(policy,
            new PolicyHeaderViewContext(track.Id, track.TemplateInputs), track.TemplateState);
        return track with
        {
            TemplateTitle = policy?.Manifest.Title,
            TemplateSchedules = policy?.Manifest.Schedules.Select(s => new TemplateScheduleDto(s.Id, s.Cron, s.Timezone)).ToArray(),
            TemplateHeaderView = view is null ? null : new TemplateHeaderViewDto(
                view.Badges?.Select(b => new TemplateHeaderBadgeDto(b.Label, b.Tone)).ToArray(),
                view.Notice is { } n ? new TemplateHeaderNoticeDto(n.Text, n.Tone) : null,
                view.Metrics?.Select(m => new TemplateHeaderMetricDto(m.Label, m.Value, m.Delta, m.Tone, m.Sub, m.Spark)).ToArray(),
                view.Rules?.Select(r => new TemplateHeaderRuleDto(r.Label, r.Text)).ToArray())
        };
    }
}
