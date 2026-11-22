using System.Text.Json;
using Jint;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    public JsonElement InitData(CompiledPolicy policy, JsonElement inputs) => EvaluateWithEffectiveDefinition(policy, inputs, "InitData", (engine, _, def, jsInputs, tHelper) =>
    {
        if (TryGetOptionalObject(def, "data", "template.data", out var dataObj))
            return InitializeAndValidateData(engine, def, dataObj, jsInputs, [], tHelper);
        return ParseJsonObject("{}");
    });

    // Validate preserved data without merging or changing system metadata.
    public IReadOnlyList<DataValidationError> ValidateData(CompiledPolicy policy, JsonElement inputs,
        JsonElement data, IReadOnlyList<string>? knownTaskKeys) => EvaluateWithEffectiveDefinition(policy, inputs, "ValidateData", (engine, _, def, _, tHelper) =>
    {
        return (IReadOnlyList<DataValidationError>)TemplateDataValidator.ValidateRoot(
            ExtractTemplateSchemaNode(engine, def, tHelper), NormalizeObjectJson(data), knownTaskKeys);
    });
}
