using System.Text.Json;
using Perga.ReportDoc;

namespace Perga.Kernel;

internal static class ReportBlockSelection
{
    internal static IReadOnlyList<ReportBlock> FilterBlocks(
        IReadOnlyList<ReportBlock> blocks, string? sectionH1, IReadOnlySet<string>? blockIds)
    {
        IEnumerable<ReportBlock> selected = blocks;
        if (sectionH1 is not null)
            selected = selected.Where(block => string.Equals(block.SectionH1, sectionH1, StringComparison.OrdinalIgnoreCase));
        if (blockIds is not null)
            selected = selected.Where(block => blockIds.Contains(block.Id));
        return selected.ToArray();
    }

    internal static HashSet<string> GetReferencedViews(IReadOnlyList<ReportBlock> blocks)
    {
        var views = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (block.Kind != ReportBlockKind.Widget || !TryExtractWidgetView(block, out var name))
            {
                continue;
            }

            views.Add(name);
        }
        return views;
    }

    private static bool TryExtractWidgetView(ReportBlock block, out string viewName)
    {
        if (block.Payload is { ValueKind: JsonValueKind.Object } payload)
            return TryReadNonFlowView(payload, out viewName);

        if (!string.IsNullOrWhiteSpace(block.Markdown))
        {
            try
            {
                using var doc = JsonDocument.Parse(block.Markdown);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return TryReadNonFlowView(doc.RootElement, out viewName);
            }
            catch (JsonException)
            {
            }
        }

        viewName = string.Empty;
        return false;
    }

    private static bool TryReadNonFlowView(JsonElement payload, out string viewName)
    {
        if (payload.TryGetProperty("use", out var use) &&
            use.ValueKind == JsonValueKind.String &&
            use.GetString() != "builtin:flow" &&
            payload.TryGetProperty("view", out var view) &&
            view.ValueKind == JsonValueKind.String &&
            view.GetString() is { } name &&
            !string.IsNullOrWhiteSpace(name))
        {
            viewName = name;
            return true;
        }

        viewName = string.Empty;
        return false;
    }
}
