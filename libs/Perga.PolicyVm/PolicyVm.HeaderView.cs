using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    // Presentation is read-only and must never prevent track reads, even for malformed hooks.
    public TemplateHeaderView? EvaluateHeaderView(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement state)
    {
        try
        {
            var bundle = TemplateBundle.Parse(policy.JsSource);
            using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
            var rawDef = LoadDefaultTemplateObject(engine, bundle);
            var jsInputs = BuildEffectiveJsInputs(engine, rawDef, ctx.Inputs);
            var def = ResolveEffectiveDefinition(engine, rawDef, jsInputs);
            if (def.Get("headerView") is not Function hook)
                return null;
            var context = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var value = engine.Call(hook, def, [context]);
            if (!value.IsObject() || value.IsArray())
                return null;
            var json = JsValueToJsonElement(engine, value, defaultJson: "null");
            if (!ValidHeaderView(json))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize<TemplateHeaderView>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ValidHeaderView(JsonElement view) =>
        view.ValueKind == JsonValueKind.Object && view.EnumerateObject().All(property => property.Name switch
        {
            "badges" => ValidArray(property.Value, ValidBadge),
            "notice" => ValidNotice(property.Value),
            "metrics" => ValidArray(property.Value, ValidMetric),
            "rules" => ValidArray(property.Value, ValidRule),
            _ => false
        });

    private static bool ValidArray(JsonElement value, Func<JsonElement, bool> valid) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(valid);

    private static bool RequiredString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool ValidTone(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is "neutral" or "accent" or "success" or "warn" or "error";

    private static bool ValidBadge(JsonElement item) => RequiredString(item, "label") &&
        item.EnumerateObject().All(p => p.Name switch { "label" => true, "tone" => ValidTone(p.Value), _ => false });

    private static bool ValidNotice(JsonElement item) => RequiredString(item, "text") &&
        item.EnumerateObject().All(p => p.Name switch {
            "text" => true,
            "tone" => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "info" or "warn" or "error",
            _ => false
        });

    private static bool ValidMetric(JsonElement item) => RequiredString(item, "label") && RequiredString(item, "value") &&
        item.EnumerateObject().All(p => p.Name switch {
            "label" or "value" => true,
            "delta" or "sub" => p.Value.ValueKind == JsonValueKind.String,
            "tone" => ValidTone(p.Value),
            "spark" => ValidArray(p.Value, n => n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d) && double.IsFinite(d)),
            _ => false
        });

    private static bool ValidRule(JsonElement item) => RequiredString(item, "label") && RequiredString(item, "text") &&
        item.EnumerateObject().All(p => p.Name is "label" or "text");
}
