using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class TemplateSchedules
{
    internal static void ValidateInputs(IPolicyVm vm, CompiledPolicy policy, JsonElement inputs)
    {
        try { vm.ValidateInputs(policy, inputs); }
        catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
    }

    internal static IReadOnlyList<TrackScheduleDto> Materialize(CompiledPolicy? policy, JsonElement inputs,
        string trackId, long now, IPolicyVm vm, IScheduleCronEvaluator? cron)
    {
        if (policy is null || policy.Manifest.Schedules.Count == 0) return [];
        if (cron is null) throw KernelException.PreconditionFailed("Cron evaluator is unavailable.");
        return policy.Manifest.Schedules.Select(spec =>
        {
            if (!cron.TryValidate(spec.Cron, spec.Timezone, out var timezone, out var error))
                throw KernelException.InvalidArgument(error ?? "Invalid template schedule.");
            var next = cron.GetNextOccurrenceMs(spec.Cron, timezone, now)
                ?? throw KernelException.InvalidArgument("Template cron has no future occurrence.");
            string prompt;
            try { prompt = vm.InterpolateInputs(policy, spec.Prompt, inputs); }
            catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
            return new TrackScheduleDto(spec.Id, trackId, TrackScheduleSources.Template, TrackScheduleKinds.Cron,
                spec.Title ?? spec.Id, prompt, spec.Cron, timezone, null, next, null, null, 0,
                TrackScheduleStates.Active, now, now);
        }).ToArray();
    }
}
