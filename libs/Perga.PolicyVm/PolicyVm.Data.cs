using Jint;
using System.Text.Json;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    public JsonElement InitData(CompiledPolicy policy, JsonElement inputs) => ExecuteSandboxed("InitData", () =>
    {
        var bundle = TemplateBundle.Parse(policy.JsSource);
        using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
        var tHelper = engine.GetValue("t");
        var rawDef = LoadDefaultTemplateObject(engine, bundle);
        var jsInputs = BuildEffectiveJsInputs(engine, rawDef, inputs);
        var def = ResolveEffectiveDefinition(engine, rawDef, jsInputs);
        if (TryGetOptionalObject(def, "data", "template.data", out var dataObj) &&
            !dataObj.Get("initial").IsUndefined() && !dataObj.Get("initial").IsNull())
            return InitializeAndValidateData(engine, def, dataObj, jsInputs, [], tHelper);
        return ParseJsonObject("{}");
    });

    // Validate preserved data without merging or changing system metadata.
    public IReadOnlyList<DataValidationError> ValidateData(CompiledPolicy policy, JsonElement inputs,
        JsonElement data, IReadOnlyList<string>? knownTaskKeys) => ExecuteSandboxed("ValidateData", () =>
    {
        var bundle = TemplateBundle.Parse(policy.JsSource);
        using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
        var tHelper = engine.GetValue("t");
        var rawDef = LoadDefaultTemplateObject(engine, bundle);
        var def = ResolveEffectiveDefinition(engine, rawDef, BuildEffectiveJsInputs(engine, rawDef, inputs));
        return (IReadOnlyList<DataValidationError>)TemplateDataValidator.ValidateRoot(
            ExtractTemplateSchemaNode(engine, def, tHelper), NormalizeStateJson(data), knownTaskKeys);
    });
}
