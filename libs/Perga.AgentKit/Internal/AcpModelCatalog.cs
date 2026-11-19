namespace Perga.AgentKit.Internal;

internal sealed record AcpRawModel(string ModelId, string? Name, string? Description);

// One table owns the public grouped defaults and their exact advertised wire IDs.
internal sealed class AcpModelCatalog(
    IReadOnlyList<ProviderModelInfo> models,
    Dictionary<string, AcpModelCatalog.Selection> selections,
    AcpSessionOption? thoughtLevel)
{
    private static readonly string[] KnownEffortSuffixes = ["low", "medium", "high", "xhigh", "max", "ultra"];
    internal sealed record Selection(string DefaultId, Dictionary<string, string> Variants);
    internal AcpSessionOption? ThoughtLevel { get; } = thoughtLevel;
    internal IReadOnlyList<ProviderModelInfo> Models { get; } = models.Select(model =>
        model.SupportedReasoningEfforts.Count == 0 && thoughtLevel is not null
            ? model with
            {
                DefaultReasoningEffort = thoughtLevel.Current,
                SupportedReasoningEfforts = thoughtLevel.Values.Select(value => new ProviderModelEffort(value.Id, value.Name)).ToArray()
            }
            : model).ToArray();

    internal bool UsesThoughtLevel(string? model) => ThoughtLevel is not null &&
        (model is null || !selections.TryGetValue(model, out var selection) || selection.Variants.Count == 0);

    internal string Resolve(string model, string? effort)
    {
        if (!selections.TryGetValue(model, out var selection))
            return model; // Preserve explicit raw IDs and provider validation of unknown IDs.
        return !string.IsNullOrWhiteSpace(effort) && selection.Variants.TryGetValue(effort.Trim(), out var rawId)
            ? rawId : selection.DefaultId;
    }

    internal static AcpModelCatalog Build(
        IReadOnlyList<AcpRawModel> rawModels,
        string? currentModelId,
        AcpSessionOption? thoughtLevel = null)
    {
        if (rawModels.Count == 0)
        {
            return new([], new(StringComparer.Ordinal), thoughtLevel);
        }

        static (string BaseId, string? Effort) SplitEffortSuffix(string modelId)
        {
            foreach (var suffix in KnownEffortSuffixes)
            {
                var dashSuffix = "-" + suffix;
                if (modelId.EndsWith(dashSuffix, StringComparison.OrdinalIgnoreCase) &&
                    modelId.Length > dashSuffix.Length)
                {
                    return (modelId[..^dashSuffix.Length], suffix);
                }
            }

            return (modelId, null);
        }

        static string StripEffortFromDisplayName(string? name, string? effort)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var trimmed = name.Trim();
            if (effort is null)
            {
                return trimmed;
            }

            var parenSuffix = $" ({effort})";
            if (trimmed.EndsWith(parenSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^parenSuffix.Length].TrimEnd();
            }

            var dashSuffix = $"-{effort}";
            if (trimmed.EndsWith(dashSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^dashSuffix.Length].TrimEnd();
            }

            return trimmed;
        }

        var (currentBase, currentEffort) = string.IsNullOrWhiteSpace(currentModelId)
            ? (string.Empty, null)
            : SplitEffortSuffix(currentModelId);

        var order = new List<string>();
        var groups = new Dictionary<string, (string? DisplayName, string? Description, Dictionary<string, string> Variants, string? BareId, List<AcpRawModel> RawModels)>(StringComparer.Ordinal);

        foreach (var raw in rawModels)
        {
            var (baseId, effort) = SplitEffortSuffix(raw.ModelId);
            if (!groups.TryGetValue(baseId, out var entry))
            {
                entry = (null, null, new(StringComparer.OrdinalIgnoreCase), null, []);
                order.Add(baseId);
            }

            var cleanName = StripEffortFromDisplayName(raw.Name, effort);
            if (string.IsNullOrWhiteSpace(entry.DisplayName) && !string.IsNullOrWhiteSpace(cleanName))
            {
                entry.DisplayName = cleanName;
            }

            if (string.IsNullOrWhiteSpace(entry.Description) && !string.IsNullOrWhiteSpace(raw.Description))
            {
                entry.Description = raw.Description;
            }

            if (effort is not null)
            {
                entry.Variants.TryAdd(effort, raw.ModelId);
            }
            else
            {
                entry.BareId ??= raw.ModelId;
            }

            entry.RawModels.Add(raw);
            groups[baseId] = entry;
        }

        var advertisedIds = rawModels.Select(raw => raw.ModelId).ToHashSet(StringComparer.Ordinal);
        var catalog = new List<ProviderModelInfo>(order.Count);
        var selections = new Dictionary<string, Selection>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            var baseId = order[i];
            var entry = groups[baseId];
            var sortedEfforts = KnownEffortSuffixes
                .Where(e => entry.Variants.ContainsKey(e))
                .Select(e => new ProviderModelEffort(e, e))
                .ToArray();
            string? defaultEffort = null;
            if (entry.BareId is null)
            {
                if (baseId == currentBase && currentEffort is not null && entry.Variants.ContainsKey(currentEffort))
                    defaultEffort = currentEffort;
                else if (entry.Variants.ContainsKey("medium"))
                    defaultEffort = "medium";
                else
                    defaultEffort = sortedEfforts.FirstOrDefault()?.ReasoningEffort;
            }
            var defaultId = entry.BareId ?? entry.Variants[defaultEffort!];
            // A derived name may already be a wire ID belonging to another group.
            // Publish this group's raw variants instead, so every row stays selectable
            // without allowing an alias to change the meaning of an advertised ID.
            if (entry.BareId is null && advertisedIds.Contains(baseId))
            {
                foreach (var raw in entry.RawModels)
                {
                    var rawIsDefault = !string.IsNullOrWhiteSpace(currentModelId)
                        ? string.Equals(raw.ModelId, currentModelId, StringComparison.Ordinal)
                        : i == 0 && string.Equals(raw.ModelId, defaultId, StringComparison.Ordinal);
                    catalog.Add(new ProviderModelInfo(
                        Model: raw.ModelId,
                        DisplayName: string.IsNullOrWhiteSpace(raw.Name) ? raw.ModelId : raw.Name.Trim(),
                        Description: raw.Description ?? string.Empty,
                        IsDefault: rawIsDefault,
                        DefaultReasoningEffort: null,
                        SupportedReasoningEfforts: []));
                }
                continue;
            }

            selections.Add(baseId, new(defaultId, entry.Variants));
            var isDefault = !string.IsNullOrWhiteSpace(currentBase)
                ? string.Equals(baseId, currentBase, StringComparison.Ordinal)
                : i == 0;

            catalog.Add(new ProviderModelInfo(
                Model: baseId,
                DisplayName: string.IsNullOrWhiteSpace(entry.DisplayName) ? baseId : entry.DisplayName!,
                Description: entry.Description ?? string.Empty,
                IsDefault: isDefault,
                DefaultReasoningEffort: defaultEffort,
                SupportedReasoningEfforts: sortedEfforts));
        }

        return new(catalog, selections, thoughtLevel);
    }
}
