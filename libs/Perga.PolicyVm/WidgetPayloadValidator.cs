using System.Text.Json;

namespace Perga.PolicyVm;

/// <summary>
/// Single authoritative validator for <c>perga-block widget</c> payloads and template references,
/// shared by compile-time validation (PolicyVm) and runtime report commits/reads (Kernel).
/// </summary>
public static class WidgetPayloadValidator
{
    public const double MinHeightPixels = 48;
    public const double MaxHeightPixels = 2400;
    public const int MaxTextRunes = 2048;

    public static bool TryValidatePayload(JsonElement? payload, out string use, out string view, out string error)
    {
        use = string.Empty;
        view = string.Empty;
        error = "Expected a JSON object with non-empty 'use' and 'view' strings.";
        if (payload is not { ValueKind: JsonValueKind.Object } obj ||
            !TryReadIdentifier(obj, "use", out use) ||
            !TryReadIdentifier(obj, "view", out view))
        {
            return false;
        }

        error = "Invalid 'use'. Use builtin:flow, builtin:list, builtin:table, or ./widgets/*.html.";
        if (use is not ("builtin:flow" or "builtin:list" or "builtin:table") && !IsHtmlAssetPath(use))
        {
            return false;
        }

        error = $"'height' must be a number between {MinHeightPixels} and {MaxHeightPixels} pixels.";
        if (obj.TryGetProperty("height", out var height) && height.ValueKind != JsonValueKind.Null &&
            (height.ValueKind != JsonValueKind.Number || !height.TryGetDouble(out var pixels) ||
             double.IsNaN(pixels) || double.IsInfinity(pixels) || pixels < MinHeightPixels || pixels > MaxHeightPixels))
        {
            return false;
        }

        error = "'chrome' must be 'seamless' or 'framed'.";
        if (obj.TryGetProperty("chrome", out var chrome) &&
            (chrome.ValueKind != JsonValueKind.String || chrome.GetString() is not ("seamless" or "framed")))
        {
            return false;
        }

        error = $"'title' must be a string of at most {MaxTextRunes} characters.";
        if (obj.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null &&
            (title.ValueKind != JsonValueKind.String || !FitsTextLimit(title.GetString()!)))
        {
            return false;
        }

        error = "Widget payload contains an unsupported field.";
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name is not ("use" or "view" or "height" or "chrome" or "title"))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<(string Code, string Message)> ValidateReferences(
        string use,
        string view,
        IReadOnlyCollection<string>? declaredViews,
        TemplateBundle? bundle)
    {
        if (string.Equals(use, "builtin:flow", StringComparison.Ordinal))
        {
            return string.Equals(view, "flow", StringComparison.Ordinal)
                ? []
                : [("unknown_widget_view", "builtin:flow requires view 'flow'.")];
        }

        var issues = new List<(string Code, string Message)>(2);
        if (declaredViews is null || !declaredViews.Contains(view, StringComparer.Ordinal))
        {
            issues.Add(("unknown_widget_view", $"View '{view}' is not declared in the active template."));
        }

        if (use.StartsWith("./widgets/", StringComparison.Ordinal) &&
            bundle?.TryGetFile(use, out _) != true)
        {
            issues.Add(("missing_widget_asset", $"Widget asset '{use}' is missing from the active template."));
        }

        return issues;
    }

    private static bool TryReadIdentifier(JsonElement obj, string key, out string value)
    {
        value = obj.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value) && FitsTextLimit(value);
    }

    private static bool FitsTextLimit(string value) => value.EnumerateRunes().Take(MaxTextRunes + 1).Count() <= MaxTextRunes;

    private static bool IsHtmlAssetPath(string use) =>
        use.StartsWith("./widgets/", StringComparison.Ordinal) &&
        use.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
        !use.Contains('\\') && !use.Contains(':') && !use.Contains('\0') &&
        use[2..].Split('/').All(static part => part is not ("" or "." or ".."));
}
