using System.Text.Json;
using Jint;
using Jint.Native.Function;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    private static readonly JsonSerializerOptions HeaderViewJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TemplateHeaderView? EvaluateHeaderView(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement data) =>
        EvaluateHeaderViewWithDiagnostic(policy, ctx, data).View;

    // Presentation is read-only and must never prevent track reads, even for malformed hooks.
    public HeaderViewEvaluation EvaluateHeaderViewWithDiagnostic(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement data)
    {
        try
        {
            return EvaluateWithEffectiveDefinition<HeaderViewEvaluation>(policy, ctx.Inputs, "HeaderView",
                (engine, _, def, _, _) =>
                {
                    var hookVal = def.Get("headerView");
                    if (hookVal.IsUndefined() || hookVal.IsNull())
                        return new(null, null);
                    if (hookVal is not Function hook)
                        throw InvalidHeaderView("headerView", "expected a function");

                    var context = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, data);
                    var value = engine.Call(hook, def, [context]);
                    if (!value.IsObject() || value.IsArray())
                        throw InvalidHeaderView("headerView", "expected an object");

                    var json = JsValueToJsonElement(engine, value, defaultJson: "null");
                    ValidateHeaderObject(json, "headerView", (name, item, path) =>
                    {
                        switch (name)
                        {
                            case "badges":
                            case "metrics":
                            case "rules":
                                ValidateHeaderArray(item, path, (entry, entryPath) => ValidateHeaderEntry(entry, entryPath, name));
                                break;
                            case "notice":
                                ValidateHeaderEntry(item, path, name);
                                break;
                            default:
                                throw InvalidHeaderView(path, "unknown field");
                        }
                    });
                    return new(JsonSerializer.Deserialize<TemplateHeaderView>(json, HeaderViewJsonOptions), null);
                });
        }
        catch (Exception ex)
        {
            return new(null, ex.Message);
        }
    }

    private static void ValidateHeaderEntry(JsonElement item, string path, string kind)
    {
        ValidateHeaderObject(item, path, (name, value, fieldPath) =>
        {
            switch (name)
            {
                case "label" when kind != "notice":
                case "text" when kind is "notice" or "rules":
                case "value" or "delta" or "sub" when kind == "metrics":
                    RequireHeaderString(value, fieldPath);
                    break;
                case "tone" when kind is "badges" or "metrics":
                    if (value.ValueKind != JsonValueKind.String ||
                        value.GetString() is not ("neutral" or "accent" or "success" or "warn" or "error"))
                        throw InvalidHeaderView(fieldPath, "expected neutral, accent, success, warn or error");
                    break;
                case "tone" when kind == "notice":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("info" or "warn" or "error"))
                        throw InvalidHeaderView(fieldPath, "expected info, warn or error");
                    break;
                case "spark" when kind == "metrics":
                    ValidateHeaderArray(value, fieldPath, (point, pointPath) =>
                    {
                        if (point.ValueKind != JsonValueKind.Number || !point.TryGetDouble(out var number) || !double.IsFinite(number))
                            throw InvalidHeaderView(pointPath, "expected a finite number");
                    });
                    break;
                default:
                    throw InvalidHeaderView(fieldPath, "unknown field");
            }
        });

        RequireProperty(kind == "notice" ? "text" : "label");
        if (kind == "metrics") RequireProperty("value");
        if (kind == "rules") RequireProperty("text");

        void RequireProperty(string name)
        {
            if (!item.TryGetProperty(name, out _))
                throw InvalidHeaderView($"{path}.{name}", "required field is missing");
        }
    }

    private static void ValidateHeaderObject(JsonElement value, string path, Action<string, JsonElement, string> validateField)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidHeaderView(path, "expected an object");

        foreach (var field in value.EnumerateObject())
            validateField(field.Name, field.Value, $"{path}.{field.Name}");
    }

    private static void ValidateHeaderArray(JsonElement value, string path, Action<JsonElement, string> validateItem)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw InvalidHeaderView(path, "expected an array");

        var index = 0;
        foreach (var item in value.EnumerateArray())
            validateItem(item, $"{path}[{index++}]");
    }

    private static void RequireHeaderString(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidHeaderView(path, "expected a string");
    }

    private static InvalidOperationException InvalidHeaderView(string path, string message) =>
        new($"Invalid {path}: {message}.");
}
