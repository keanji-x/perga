using Perga.Contracts;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    private static readonly Regex WidgetFenceRegex = new(
        @"```perga-block\s+widget\s*\r?\n(?<body>[\s\S]*?)\r?\n```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<TaskRoutingRule> ResolveRoutingRules(CompiledPolicy policy, JsonElement inputs)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return EvaluateWithEffectiveDefinition(policy, inputs, "ResolveRoutingRules", (engine, _, def, _, _) =>
        {

            return ReadRoutingRules(engine, def);
        });
    }

    public DataPatchValidationResult ValidateAndPatchData(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement patch,
        IReadOnlyList<string>? knownTaskKeys,
        string nowIso,
        string source)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (patch.ValueKind != JsonValueKind.Object)
        {
            return new DataPatchValidationResult(
                false,
                NormalizeStateJson(currentData),
                [new DataValidationError("$", "Patch payload must be a JSON object.")]);
        }

        return EvaluateWithEffectiveDefinition(policy, inputs, "ValidateAndPatchData", (engine, _, def, _, tHelper) =>
        {
            var normalizedCurrent = NormalizeStateJson(currentData);
            var (ok, mergedJson, safetyErrors) = TemplateDataValidator.MergeAndStamp(
                normalizedCurrent,
                patch,
                nowIso,
                source);
            if (!ok)
            {
                return new DataPatchValidationResult(false, normalizedCurrent, safetyErrors);
            }

            var schemaNode = ExtractTemplateSchemaNode(engine, def, tHelper);
            var errors = TemplateDataValidator.ValidateRoot(schemaNode, mergedJson, knownTaskKeys);
            if (errors.Count > 0)
            {
                return new DataPatchValidationResult(false, normalizedCurrent, errors);
            }

            return new DataPatchValidationResult(true, mergedJson, []);
        });
    }

    public TemplateViewsProjection ProjectViews(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement kernelSnapshot)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return EvaluateWithEffectiveDefinition(policy, inputs, "ProjectViews", (engine, bundle, def, _, _) =>
        {
            var views = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var diagnostics = new List<TemplateViewDiagnostic>();
            var widgets = ExtractBundledHtmlWidgets(bundle);

            if (TryGetOptionalObject(def, "report", "template.report", out var reportObj) &&
                TryGetOptionalObject(reportObj, "views", "template.report.views", out var viewsObj))
            {
                var dataJs = ParseJsJson(engine, NormalizeStateJson(currentData).GetRawText());
                var kernelJs = ParseJsJson(engine, NormalizeStateJson(kernelSnapshot).GetRawText());

                foreach (var prop in viewsObj.GetOwnProperties())
                {
                    var viewName = prop.Key.AsString();
                    var fnVal = prop.Value.Value;
                    if (fnVal is not Function fn)
                    {
                        diagnostics.Add(new TemplateViewDiagnostic(viewName, $"report.views.{viewName} is not a function."));
                        continue;
                    }

                    try
                    {
                        var projected = engine.Call(fn, viewsObj, [dataJs, kernelJs]);
                        views[viewName] = JsValueToJsonElement(engine, projected, "{}");
                    }
                    catch (Exception ex) when (ex is not TimeoutException
                        and not MemoryLimitExceededException
                        and not StatementsCountOverflowException
                        and not RecursionDepthOverflowException)
                    {
                        diagnostics.Add(new TemplateViewDiagnostic(viewName, ex.Message));
                    }
                }
            }

            return new TemplateViewsProjection(
                TemplateId: policy.TemplateId,
                Views: views,
                Widgets: widgets,
                Diagnostics: diagnostics);
        });
    }

    public IReadOnlyList<FlowNodeSpecDto> ResolveFlow(CompiledPolicy policy, JsonElement inputs) =>
        EvaluateWithEffectiveDefinition(policy, inputs, "ResolveFlow",
            (engine, _, def, _, _) => ReadFlow(engine, def).Nodes);

    private static (JsonElement Json, IReadOnlyList<FlowNodeSpecDto> Nodes) ReadFlow(Engine engine, ObjectInstance definition)
    {
        var value = definition.Get("flow");
        if (!value.IsUndefined() && !value.IsNull() && !value.IsArray())
            throw new InvalidOperationException("template.flow must be an array of Flow nodes.");
        var flow = JsValueToJsonElement(engine, value, "[]");
        var (spec, errors) = TrackFlowParser.Parse($$"""{"rev":0,"nodes":{{flow.GetRawText()}}}""");
        if (spec is null)
            throw new InvalidOperationException($"Invalid template flow: {string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"))}");
        return (flow, spec.Nodes);
    }

    private static ObjectInstance ResolveEffectiveDefinition(Engine engine, ObjectInstance rawDef, JsValue jsInputs)
    {
        var setupVal = rawDef.Get("setup");
        if (setupVal.IsUndefined() || setupVal.IsNull())
        {
            return rawDef;
        }

        if (setupVal is not Function setupFn)
        {
            throw new InvalidOperationException("template.setup must be a function receiving (inputs).");
        }

        var setupResult = engine.Call(setupFn, rawDef, [jsInputs]);
        if (setupResult is not ObjectInstance resolved || setupResult.IsArray())
        {
            throw new InvalidOperationException("template.setup(inputs) must return a configuration object.");
        }

        RejectLegacyHooks(resolved);

        var mergedVal = engine.Invoke("__mergeTemplateStatic", rawDef, resolved);
        if (mergedVal is ObjectInstance mergedObj)
        {
            mergedObj.Delete("setup");
        }

        var adapted = engine.Invoke("__adaptTemplate", mergedVal);
        return adapted as ObjectInstance ?? (mergedVal as ObjectInstance ?? rawDef);
    }

    private static string ResolveInitialReportMarkdown(ObjectInstance def, TemplateBundle bundle)
    {
        if (TryGetOptionalObject(def, "report", "template.report", out var reportObj))
        {
            var templatePath = ReadOptionalString(reportObj, "template", "template.report.template");
            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                if (!bundle.TryGetFile(templatePath, out var mdContent))
                {
                    throw new InvalidOperationException(
                        $"template.report.template '{templatePath}' was not found in the template package.");
                }

                return mdContent;
            }

            var reportInitial = ReadOptionalString(reportObj, "initialMarkdown", "template.report.initialMarkdown");
            if (!string.IsNullOrWhiteSpace(reportInitial))
            {
                return reportInitial;
            }
        }

        return ReadOptionalString(def, "initialMarkdown", "template.initialMarkdown") ?? string.Empty;
    }

    private static List<string> CollectViewNames(ObjectInstance def)
    {
        var names = new List<string>();
        if (TryGetOptionalObject(def, "report", "template.report", out var reportObj) &&
            TryGetOptionalObject(reportObj, "views", "template.report.views", out var viewsObj))
        {
            foreach (var prop in viewsObj.GetOwnProperties())
            {
                var name = prop.Key.AsString();
                if (prop.Value.Value is not Function)
                {
                    throw new InvalidOperationException($"template.report.views.{name} must be a function.");
                }

                names.Add(name);
            }
        }

        return names;
    }

    private static TemplateSchemaNode? ExtractTemplateSchemaNode(Engine engine, ObjectInstance def, JsValue tHelper)
    {
        if (!TryGetOptionalObject(def, "data", "template.data", out var dataObj))
        {
            return null;
        }

        var schemaVal = dataObj.Get("schema");
        if (schemaVal.IsUndefined() || schemaVal.IsNull())
        {
            return null;
        }

        JsValue schemaObj;
        if (schemaVal is Function schemaFn)
        {
            schemaObj = engine.Call(schemaFn, dataObj, [tHelper]);
        }
        else if (schemaVal is ObjectInstance)
        {
            schemaObj = schemaVal;
        }
        else
        {
            throw new InvalidOperationException("template.data.schema must be a function (t) => ({ ... }) or a schema object.");
        }

        return TemplateSchemaExtractor.ExtractSchemaNode(schemaObj);
    }

    private static JsonElement InitializeAndValidateData(
        Engine engine,
        ObjectInstance def,
        ObjectInstance dataObj,
        JsValue jsInputs,
        IReadOnlyList<string>? knownTaskKeys,
        JsValue tHelper)
    {
        var initialVal = dataObj.Get("initial");
        JsValue rawInitialJs;
        if (initialVal is Function initFn)
        {
            rawInitialJs = engine.Call(initFn, dataObj, [jsInputs]);
        }
        else if (initialVal is ObjectInstance && !initialVal.IsArray() && !initialVal.IsNull())
        {
            rawInitialJs = initialVal;
        }
        else
        {
            throw new InvalidOperationException("template.data.initial must be a function or a plain object.");
        }

        var rawInitialJson = TemplateSchemaExtractor.ExtractDataElement(rawInitialJs);
        var (ok, stampedJson, safetyErrs) = TemplateDataValidator.MergeAndStamp(
            ParseJsonObject("{}"),
            rawInitialJson,
            DateTimeOffset.UtcNow.ToString("O"),
            "template:initial", stampUnchanged: true);
        if (!ok)
        {
            var safetyDetail = string.Join("; ", safetyErrs.Select(e => $"{(string.IsNullOrEmpty(e.Path) ? "$" : e.Path)}: {e.Message}"));
            throw new InvalidOperationException($"template.data.initial failed safety validation: {safetyDetail}");
        }

        var schemaNode = ExtractTemplateSchemaNode(engine, def, tHelper);
        var errors = TemplateDataValidator.ValidateRoot(schemaNode, stampedJson, knownTaskKeys);
        if (errors.Count > 0)
        {
            var detail = string.Join("; ", errors.Select(e => $"{(string.IsNullOrEmpty(e.Path) ? "$" : e.Path)}: {e.Message}"));
            throw new InvalidOperationException($"template.data.initial failed schema validation: {detail}");
        }

        return stampedJson;
    }

    private static void ValidateCrossFileReferencesAndInitialData(
        Engine engine,
        ObjectInstance def,
        JsValue jsInputs,
        TemplateBundle bundle,
        string initialReportMarkdown,
        IReadOnlyList<string> viewNames,
        JsValue tHelper,
        bool skipCrossFileChecks = false)
    {
        // 1. Verify every `perga-block widget` in report.md references a declared view & existing widget asset.
        if (!skipCrossFileChecks)
        {
            var viewSet = new HashSet<string>(viewNames, StringComparer.Ordinal);
            foreach (Match match in WidgetFenceRegex.Matches(initialReportMarkdown))
            {
                var body = match.Groups["body"].Value.Trim();
                JsonElement payload;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    payload = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Invalid JSON in perga-block widget inside report template: {ex.Message}", ex);
                }

                if (payload.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("perga-block widget payload must be a JSON object.");
                }

                var use = payload.TryGetProperty("use", out var useEl) && useEl.ValueKind == JsonValueKind.String
                    ? useEl.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(use))
                {
                    throw new InvalidOperationException("perga-block widget requires a non-empty 'use' field.");
                }

                if (!use.StartsWith("builtin:", StringComparison.Ordinal))
                {
                    if (!bundle.TryGetFile(use, out _))
                    {
                        throw new InvalidOperationException(
                            $"perga-block widget references '{use}', which was not found in the template package.");
                    }
                }

                var viewName = payload.TryGetProperty("view", out var viewEl) && viewEl.ValueKind == JsonValueKind.String
                    ? viewEl.GetString() : null;
                if (viewName is not null)
                {
                    var isKernelFlowBuiltin = string.Equals(use, "builtin:flow", StringComparison.Ordinal) &&
                                              string.Equals(viewName, "flow", StringComparison.Ordinal);
                    if (!string.IsNullOrWhiteSpace(viewName) && !isKernelFlowBuiltin && !viewSet.Contains(viewName))
                    {
                        throw new InvalidOperationException(
                            $"perga-block widget references view '{viewName}', which is not declared in report.views.");
                    }
                }
            }
        }

        // 2. Validate data.initial() against data.schema(t), and trial-run every declared view against initial data + empty flow.
        JsValue initialDataJs = ParseJsJson(engine, "{}");
        if (TryGetOptionalObject(def, "data", "template.data", out var dataObj))
        {
            var initialVal = dataObj.Get("initial");
            if (!initialVal.IsUndefined() && !initialVal.IsNull())
            {
                var validatedJson = InitializeAndValidateData(
                    engine,
                    def,
                    dataObj,
                    jsInputs,
                    knownTaskKeys: null,
                    tHelper);
                initialDataJs = ParseJsJson(engine, validatedJson.GetRawText());
            }
            else
            {
                // Still validate schema shape at compile time even when data.initial is omitted.
                _ = ExtractTemplateSchemaNode(engine, def, tHelper);
            }
        }

        if (viewNames.Count > 0 &&
            TryGetOptionalObject(def, "report", "template.report", out var reportObj) &&
            TryGetOptionalObject(reportObj, "views", "template.report.views", out var viewsObj))
        {
            var emptyKernel = ParseJsJson(engine, "{\"tasks\":{},\"sessions\":{},\"flow\":{\"rev\":0,\"nodes\":[],\"archived\":[]}}");
            foreach (var name in viewNames)
            {
                var fn = (Function)viewsObj.Get(name);
                engine.Call(fn, viewsObj, [initialDataJs, emptyKernel]);
            }
        }
    }

    private static Dictionary<string, string> ExtractBundledHtmlWidgets(TemplateBundle bundle)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in bundle.Files)
        {
            if (kv.Key.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            {
                result[kv.Key] = kv.Value;
                result["./" + kv.Key] = kv.Value;
            }
        }

        return result;
    }
}
