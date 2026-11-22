using Perga.Kernel.Internal;
using Perga.ReportDoc;

namespace Perga.Kernel;

internal static class ReportAuthoring
{
    public static void Validate(ReportDocument document, ReportDocument? previousDocument = null)
    {
        var historical = previousDocument?.Blocks.Where(block => !IsAllowed(block.Kind))
            .ToDictionary(block => block.Id, StringComparer.Ordinal);
        foreach (var block in document.Blocks)
        {
            if (historical is not null && historical.TryGetValue(block.Id, out var previous))
            {
                if (previous.Kind == block.Kind && previous.Rev == block.Rev && previous.SectionH1 == block.SectionH1 &&
                    previous.Markdown == block.Markdown && JsonHelpers.JsonEquals(previous.Payload, block.Payload))
                    continue;
                Reject(previous.Kind);
            }
            if (!IsAllowed(block.Kind))
                Reject(block.Kind);
        }
    }

    private static bool IsAllowed(ReportBlockKind kind) =>
        kind is ReportBlockKind.Prose or ReportBlockKind.Widget;

    private static void Reject(ReportBlockKind kind) =>
        throw KernelException.InvalidArgument(kind == ReportBlockKind.Task
            ? "Author tasks in flow.json and submit them with flow_commit."
            : $"Replace the retired Report block ({kind}) with Markdown prose or a perga-block widget.");
}
