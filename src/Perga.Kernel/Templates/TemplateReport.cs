using System.Security.Cryptography;
using System.Text.Json;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Templates;

internal static class TemplateReport
{
    internal static ReportDocument Create(IReportDocEngine engine, IPolicyVm vm, CompiledPolicy? policy,
        JsonElement inputs, int rev = 0, string summary = "")
    {
        var markdown = "";
        if (policy is not null)
        {
            try { markdown = vm.InterpolateInputs(policy, policy.Manifest.InitialReportMarkdown, inputs); }
            catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
        }

        var document = engine.Parse(markdown, docRev: rev, summary: summary);
        // Batched edits can advance block revisions faster than the document revision.
        var reservedIds = rev > 1 && document.Blocks.Count > 0
            ? document.Blocks.Select(block => block.Id).ToHashSet(StringComparer.Ordinal)
            : null;
        var blocks = document.Blocks.Select(block =>
        {
            var id = block.Id;
            if (reservedIds is not null)
            {
                do
                {
                    id = $"b_{RandomNumberGenerator.GetHexString(12, lowercase: true)}";
                } while (!reservedIds.Add(id));
            }

            return block with { Id = id, Rev = 1 };
        }).ToList();
        document = document with { Blocks = blocks };
        ReportAuthoring.Validate(document);
        ReportWidgetValidator.Diagnose(document, policy, vm, inputs, rejectInvalidChanges: true);
        return document;
    }

    internal static bool IsBlank(ReportDocument document) =>
        document.Blocks.Count == 0 && string.IsNullOrWhiteSpace(document.LeadingComment);
}
