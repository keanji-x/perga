using System.Text;

namespace Perga.MemoryKit;

internal static class MemoryContext
{
    internal const int AreaPinnedBytes = 1228;
    internal const int GlobalPinnedBytes = 820;
    internal const int MaxPinnedBytes = AreaPinnedBytes + GlobalPinnedBytes;
    internal const int MaxTopicIndexBytes = 1024;

    internal static string RenderPinned(MemoryFile file) => RenderPinned(file.Meta, file.Body);

    internal static string RenderPinned(MemoryEntryMeta meta, string body)
    {
        var newline = body.Length > 0 && !body.EndsWith('\n') ? "\n" : "";
        return $"### {meta.Ref} — {meta.Title}\n{body}{newline}";
    }

    internal static MemoryContextSnapshot Render(IReadOnlyList<MemoryFile> files, int sourceEntryCount, MemoryContextBudget budget)
    {
        var pinnedLimit = Math.Clamp(budget.PinnedBytes, 0, MaxPinnedBytes);
        var indexLimit = Math.Clamp(budget.TopicIndexBytes, 0, MaxTopicIndexBytes);
        var pinned = files.Where(file => file.Meta.Pinned)
            .Select(file => new Line(file.Meta.Ref.Kind, RenderPinned(file))).ToArray();
        var globalPinned = pinned.Where(line => line.Kind == MemoryScopeKind.Global).ToArray();

        // Reserve the bytes Global can use within its quota. Area receives unused
        // Global bytes, and Global receives everything left after filling Area.
        var globalQuota = pinnedLimit * GlobalPinnedBytes / MaxPinnedBytes;
        var globalReserved = Fit(globalPinned, globalQuota, out var globalUnused);
        var area = Fit(pinned.Where(line => line.Kind == MemoryScopeKind.Area),
            pinnedLimit - (globalQuota - globalUnused), out var globalExtra);
        var global = globalExtra == 0 ? globalReserved : Fit(globalPinned, globalQuota - globalUnused + globalExtra, out _);

        var index = Fit(files.Where(file => !file.Meta.Pinned).Select(file => RenderIndex(file.Meta)), indexLimit, out _);
        var output = new StringBuilder("<memory_context>\nCurrent Area takes precedence over Global.\n");
        if (area.Count + global.Count > 0)
        {
            output.Append("## Pinned\n");
            foreach (var line in area.Concat(global))
                output.Append(line.Text);
        }
        if (index.Count > 0)
        {
            output.Append("## Topic Index\n");
            foreach (var line in index)
                output.Append(line.Text);
        }
        output.Append("</memory_context>\n");

        var content = output.ToString();
        var areaIndexCount = index.Count(line => line.Kind == MemoryScopeKind.Area);
        return new MemoryContextSnapshot(content, MemoryMarkdown.Hash(MemoryMarkdown.Utf8.GetBytes(content)),
            area.Count, global.Count, areaIndexCount, index.Count - areaIndexCount,
            sourceEntryCount, sourceEntryCount - files.Count);
    }

    private static Line RenderIndex(MemoryEntryMeta meta)
    {
        var tags = meta.Tags.Count > 0 ? $" [{string.Join(", ", meta.Tags)}]" : "";
        return new Line(meta.Ref.Kind, $"{meta.Ref} — {meta.Summary}{tags}\n");
    }

    private static List<Line> Fit(IEnumerable<Line> lines, int limit, out int remaining)
    {
        var selected = new List<Line>();
        remaining = limit;
        foreach (var line in lines)
        {
            var bytes = MemoryMarkdown.Utf8.GetByteCount(line.Text);
            if (bytes > remaining)
                continue;
            selected.Add(line);
            remaining -= bytes;
        }
        return selected;
    }

    private readonly record struct Line(MemoryScopeKind Kind, string Text);
}
