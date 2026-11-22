using System.Text.Json;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel;

internal static class ReportWidgetValidator
{
    internal static IReadOnlyList<ReportDiagnosticDto> Diagnose(
        ReportDocument document,
        CompiledPolicy? policy,
        IPolicyVm vm,
        JsonElement inputs,
        ReportDocument? previous = null,
        bool rejectInvalidChanges = false)
    {
        var priorBlocks = previous?.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
        IReadOnlyList<string>? declaredViews = null;
        var viewsResolved = false;
        var diagnostics = new List<ReportDiagnosticDto>();

        foreach (var block in document.Blocks.Where(block => block.Kind == ReportBlockKind.Widget))
        {
            var isUnchanged = priorBlocks?.TryGetValue(block.Id, out var prior) == true &&
                              prior.HasSameContentAs(block);
            if (!WidgetPayloadValidator.TryValidatePayload(block.Payload, out var use, out var view, out var error))
            {
                if (rejectInvalidChanges && !isUnchanged)
                    throw KernelException.InvalidArgument($"Widget '{block.Id}': {error}");
                diagnostics.Add(new("error", "invalid_widget_payload", error, block.Id));
                continue;
            }

            if (policy is not null && !viewsResolved)
            {
                declaredViews = ResolveEffectiveViews(policy, vm, inputs);
                viewsResolved = true;
            }

            foreach (var (code, message) in WidgetPayloadValidator.ValidateReferences(use, view, declaredViews, policy?.WidgetAssets))
            {
                if (rejectInvalidChanges && !isUnchanged)
                    throw KernelException.InvalidArgument($"Widget '{block.Id}': {message}");
                diagnostics.Add(new("warning", code, message, block.Id));
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> ResolveEffectiveViews(CompiledPolicy policy, IPolicyVm vm, JsonElement inputs)
    {
        try
        {
            return vm.ResolveViewNames(policy, inputs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return policy.Manifest.ViewNames ?? [];
        }
    }
}
