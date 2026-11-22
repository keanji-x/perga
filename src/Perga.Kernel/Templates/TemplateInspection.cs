using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Kernel.Internal;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class TemplateInspection
{
    internal static JsonElement Project(JsonElement metadata, string? source, string sourceField, string? format, string? file)
    {
        format ??= "summary";
        if (format is not ("summary" or "files" or "full"))
            throw KernelException.InvalidArgument("Template format must be 'summary', 'files', or 'full'.");
        if (file is not null && format != "files")
            throw KernelException.InvalidArgument("'file' requires format: 'files'.");

        var response = JsonNode.Parse(metadata.GetRawText())!.AsObject();
        if (format == "full")
        {
            if (source is not null) response[sourceField] = source;
            return JsonSerializer.SerializeToElement(response, JsonHelpers.SerializerOptions);
        }

        var files = ReadFiles(source);
        if (format == "summary")
        {
            response["files"] = JsonSerializer.SerializeToNode(files.Keys.Order(StringComparer.Ordinal));
            return JsonSerializer.SerializeToElement(response, JsonHelpers.SerializerOptions);
        }

        if (file is null)
        {
            response["files"] = JsonSerializer.SerializeToNode(files);
            return JsonSerializer.SerializeToElement(response, JsonHelpers.SerializerOptions);
        }

        if (!files.TryGetValue(file, out var content))
            throw KernelException.NotFound($"Template file '{file}' was not found.");

        // Keep single-file reads focused so large track data or manifests never truncate the requested file.
        var single = new JsonObject();
        var templateId = response["templateId"]?.GetValue<string>()
            ?? response["manifest"]?["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(templateId))
            single["templateId"] = templateId;
        single["files"] = new JsonObject { [file] = content };
        return JsonSerializer.SerializeToElement(single, JsonHelpers.SerializerOptions);
    }

    private static IReadOnlyDictionary<string, string> ReadFiles(string? source)
    {
        if (source is null) return new Dictionary<string, string>();
        try { return TemplateBundle.Parse(source).InspectFiles(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            // A retained, unavailable snapshot can still be retrieved in full.
            return new Dictionary<string, string>();
        }
    }
}
