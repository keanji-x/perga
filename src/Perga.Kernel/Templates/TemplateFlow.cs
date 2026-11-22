using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class TemplateFlow
{
    private const string DetachedKey = "templateDetached";

    internal static TaskDto MarkDetached(TaskDto task)
    {
        var meta = task.Meta is { ValueKind: JsonValueKind.Object } previous
            ? JsonNode.Parse(previous.GetRawText())!.AsObject()
            : new JsonObject();
        meta[DetachedKey] = true;
        return task with { Meta = JsonSerializer.SerializeToElement(meta) };
    }

    internal static bool WasDetached(TaskDto task) =>
        task.Meta is { ValueKind: JsonValueKind.Object } meta &&
        meta.TryGetProperty(DetachedKey, out var detached) && detached.ValueKind == JsonValueKind.True;

    internal static string? GetSource(TaskDto task) =>
        task.Context is { ValueKind: JsonValueKind.Object } context &&
        context.TryGetProperty("flowSource", out var source) &&
        source.ValueKind == JsonValueKind.String ? source.GetString() : null;

    internal static void ValidateInputs(IPolicyVm vm, CompiledPolicy policy, JsonElement inputs)
    {
        try { vm.ValidateInputs(policy, inputs); }
        catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
    }

    internal static FlowNodeSpecDto[] ReadSeedNodes(IPolicyVm vm, CompiledPolicy policy, JsonElement inputs)
    {
        try { return vm.ResolveFlow(policy, inputs).Select(node => node with { Source = "template" }).ToArray(); }
        catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
    }

    internal static TrackFlowUpdate BuildSeedUpdate(
        TrackDto track, long expectedRev, IReadOnlyList<FlowNodeSpecDto> added, CompiledPolicy policy,
        IPolicyVm vm, IScheduleCronEvaluator? cron, TimeProvider timeProvider,
        IReadOnlyDictionary<string, AgentSessionDto>? sessions = null)
    {
        var now = track.UpdatedAtMs;
        return new(expectedRev, added.Select(node =>
                TrackFlowReconciler.BuildOrUpdateTaskForNode(track.Id, node, null, now,
                    FlowTaskRouting.ResolveAccess(node, policy, vm, track.TemplateInputs, sessions))).ToArray(),
            added.Select(node => TrackFlowReconciler.BuildScheduleForNode(track.Id, node, now, cron, timeProvider))
                .OfType<TrackScheduleDto>().Select(clock => ResolveClockText(clock, track, policy, vm)).ToArray());
    }

    internal static TrackScheduleDto ResolveClockText(TrackScheduleDto clock, TrackDto track, CompiledPolicy policy, IPolicyVm vm) =>
        clock with
        {
            Title = Interpolate(clock.Title, track.TemplateInputs, policy, vm) ?? clock.Title,
            Prompt = Interpolate(clock.Prompt, track.TemplateInputs, policy, vm) ?? clock.Prompt,
            UpdatedAtMs = track.UpdatedAtMs
        };

    internal static string? Interpolate(string? text, JsonElement inputs, CompiledPolicy? policy, IPolicyVm vm)
    {
        if (policy is not null && text?.Contains("${inputs.", StringComparison.Ordinal) == true)
        {
            try { return vm.InterpolateInputs(policy, text, inputs); }
            catch (InvalidOperationException) { }
        }

        return FlowConstants.InterpolateInputs(text, inputs);
    }

    internal static void RequireValid(IReadOnlyList<FlowValidationErrorDto> errors)
    {
        if (errors.Count != 0)
            throw KernelException.InvalidArgument(string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}")));
    }
}
