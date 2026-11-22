using System.Text.Json;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Templates;

internal static class TemplateReport
{
    internal static ReportDocument Create(IReportDocEngine engine, IPolicyVm vm, CompiledPolicy? policy,
        JsonElement inputs, int rev = 0)
    {
        var markdown = "";
        if (policy is not null)
        {
            try { markdown = vm.InterpolateInputs(policy, policy.Manifest.InitialReportMarkdown, inputs); }
            catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
        }

        var document = engine.Parse(markdown, docRev: rev, summary: "");
        ReportAuthoring.Validate(document);
        ReportWidgetValidator.Diagnose(document, policy, vm, inputs, rejectInvalidChanges: true);
        return document;
    }

    internal static bool IsBlank(ReportDocument document) =>
        document.DocRev == 0 && document.Blocks.Count == 0 && string.IsNullOrWhiteSpace(document.Summary);
}
