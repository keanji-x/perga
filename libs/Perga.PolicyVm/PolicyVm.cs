using System.Runtime.CompilerServices;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Perga.Contracts;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm : IPolicyVm
{
    private static readonly ConditionalWeakTable<CompiledPolicy, TemplateBundle> PolicyBundles = new();

    private const long MaxMemoryBytes = 16L * 1024 * 1024;
    private const int MaxStatementBudget = 10_000;
    private const int CompileTimeoutMs = 500;
    private const int ExecutionTimeoutMs = 200;
    private const int MaxRecursionDepth = 64;

    private const string GlobalPreludeScript = """
        globalThis.Allow = function Allow() {
          return { kind: "allow" };
        };
        globalThis.Deny = function Deny(reason) {
          if (typeof reason !== "string" || reason.trim().length === 0) {
            throw new TypeError("Deny(reason) requires a non-empty string reason.");
          }
          return { kind: "deny", reason };
        };
        globalThis.AskHuman = function AskHuman(title, labels) {
          if (typeof title !== "string" || title.trim().length === 0) {
            throw new TypeError("AskHuman(title, labels?) requires a non-empty string title.");
          }
          if (labels !== undefined && (labels === null || typeof labels !== "object" || Array.isArray(labels))) {
            throw new TypeError("AskHuman labels must be an object with optional approve and deny strings.");
          }
          const approveLabel = labels?.approve === undefined ? "Approve" : labels.approve;
          const denyLabel = labels?.deny === undefined ? "Deny" : labels.deny;
          if (typeof approveLabel !== "string" || approveLabel.trim().length === 0 ||
              typeof denyLabel !== "string" || denyLabel.trim().length === 0) {
            throw new TypeError("AskHuman labels must be non-empty strings.");
          }
          return { kind: "ask_human", title, approveLabel, denyLabel };
        };
        globalThis.defineTemplate = function defineTemplate(def) {
          if (def === null || typeof def !== "object" || Array.isArray(def)) {
            throw new TypeError("defineTemplate(def) requires a template definition object.");
          }
          return def;
        };
        """;

    public CompiledPolicy Compile(string jsModuleSource)
    {
        if (string.IsNullOrWhiteSpace(jsModuleSource))
        {
            throw new ArgumentException("Template JS module source cannot be empty.", nameof(jsModuleSource));
        }

        return ExecuteSandboxed("Compile", () =>
        {
            var bundle = TemplateBundle.Parse(jsModuleSource);
            using var engine = CreateSandboxedEngine(CompileTimeoutMs, bundle);
            var tHelper = engine.GetValue("t");
            var rawDef = ValidateDefaultTemplateObject(LoadDefinition(engine, bundle));

            var id = ReadRequiredNonEmptyString(rawDef, "id", "template.id");
            var title = ReadRequiredNonEmptyString(rawDef, "title", "template.title");
            var description = ReadOptionalString(rawDef, "description", "template.description");
            var defaultPlugins = ReadOptionalStringList(rawDef, "plugins", "template.plugins");
            var inputsSchema = ExtractInputsSchema(engine, rawDef);
            var extendsId = ReadOptionalString(rawDef, "extends", "template.extends");

            var defaultInputsJs = BuildEffectiveJsInputs(engine, rawDef, ParseJsonObject("{}"));
            var def = ResolveEffectiveDefinition(engine, rawDef, defaultInputsJs);

            EnsureOptionalFunction(def, "headerView", "template.headerView");
            EnsureOptionalFunction(def, "before", "template.before");
            EnsureOptionalFunction(def, "turnContext", "template.turnContext");

            // Workspace spec
            string? workspaceDriver = null;
            IReadOnlyList<string> lowerCacheDirs = [];
            string? workspaceCreateCmd = null;
            string? workspaceCloseCmd = null;
            if (TryGetOptionalObject(def, "workspace", "template.workspace", out var workspaceObj))
            {
                workspaceDriver = ReadOptionalString(workspaceObj, "driver", "template.workspace.driver");
                lowerCacheDirs = ReadOptionalStringList(workspaceObj, "lowerCacheDirs", "template.workspace.lowerCacheDirs");
                workspaceCreateCmd = ReadOptionalString(workspaceObj, "onCreateCmd", "template.workspace.onCreateCmd");
                workspaceCloseCmd = ReadOptionalString(workspaceObj, "onCloseCmd", "template.workspace.onCloseCmd");
            }

            // Report spec
            var viewNames = new List<string>();
            if (TryGetOptionalObject(def, "report", "template.report", out var reportObj))
            {
                if (TryGetOptionalObject(reportObj, "views", "template.report.views", out var viewsObj))
                {
                    foreach (var prop in viewsObj.GetOwnProperties())
                    {
                        var viewKey = prop.Key.AsString();
                        if (prop.Value.Value is not Function)
                        {
                            throw new InvalidOperationException($"template.report.views.{viewKey} must be a function.");
                        }

                        viewNames.Add(viewKey);
                    }
                }
            }

            var initialReportMarkdown = ResolveInitialReportMarkdown(def, bundle);
            ValidateCrossFileReferencesAndInitialData(
                engine, def, defaultInputsJs, bundle, initialReportMarkdown, viewNames, tHelper, skipCrossFileChecks: !string.IsNullOrWhiteSpace(extendsId));

            // Tasks spec
            var requireGateForReadWrite = false;
            string? afterWorkerDoneCmd = null;
            if (TryGetOptionalObject(def, "tasks", "template.tasks", out var tasksObj))
            {
                requireGateForReadWrite = ReadOptionalBool(tasksObj, "requireGateForReadWrite", "template.tasks.requireGateForReadWrite") ?? false;
                afterWorkerDoneCmd = ReadOptionalString(tasksObj, "afterWorkerDoneCmd", "template.tasks.afterWorkerDoneCmd");
            }

            var (flow, _) = ReadFlow(engine, def);

            var manifest = new CompiledTemplateManifest(
                Id: id,
                Title: title,
                Description: description,
                DefaultPlugins: defaultPlugins,
                InputsSchema: inputsSchema,
                RequireGateForReadWrite: requireGateForReadWrite,
                WorkspaceDriver: workspaceDriver,
                WorkspaceLowerCacheDirs: lowerCacheDirs,
                WorkspaceCreateCmd: workspaceCreateCmd,
                WorkspaceCloseCmd: workspaceCloseCmd,
                AfterWorkerDoneCmd: afterWorkerDoneCmd,
                Flow: flow,
                Routing: ReadRoutingRules(engine, def),
                Extends: ReadOptionalString(rawDef, "extends", "template.extends"),
                HasHeaderView: !def.Get("headerView").IsUndefined(),
                InitialReportMarkdown: initialReportMarkdown,
                ViewNames: viewNames.Count > 0 ? viewNames : null);

            var policy = new CompiledPolicy(id, jsModuleSource, manifest, bundle.WidgetAssetPaths());
            PolicyBundles.Add(policy, bundle);
            return policy;
        }, CompileTimeoutMs);
    }

    public PolicyTurnPreparation PrepareTurn(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var session = new PolicySessionContext(ctx.SessionId, ctx.IsLead, ctx.ContextUsageRatio, ctx.BoundTaskId);
        var sessionJson = SerializeSessionContext(session);
        var context = EvaluateWithEffectiveDefinition<string?>(policy, ctx.Inputs, "TurnContext", (engine, _, def, _, _) =>
        {
            if (def.Get("turnContext") is not Function hook)
                return null;
            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, data, sessionJson);
            return ExtractOptionalStringResult(engine.Call(hook, def, [templateCtx]), "turnContext");
        });
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            sessionId = ctx.SessionId, isLead = ctx.IsLead, boundTaskId = ctx.BoundTaskId,
            contextUsageRatio = ctx.ContextUsageRatio, context
        });
        var decision = EvaluateBefore(policy, "turn.before", ctx.TrackId, payload, ctx.Inputs, data, session);
        return new(context, decision);
    }

    private static JsonElement SerializeSessionContext(PolicySessionContext session)
    {
        var fields = new Dictionary<string, object?>
        {
            ["sessionId"] = session.SessionId,
            ["isLead"] = session.IsLead,
            ["contextUsageRatio"] = session.ContextUsageRatio
        };
        if (session.BoundTaskId is not null)
            fields["boundTaskId"] = session.BoundTaskId;
        return System.Text.Json.JsonSerializer.SerializeToElement(fields);
    }

    private static Engine CreateSandboxedEngine(int timeoutMs = ExecutionTimeoutMs, TemplateBundle? bundle = null)
    {
        var engine = new Engine(options =>
        {
            options.Strict();
            options.LimitMemory(MaxMemoryBytes);
            options.MaxStatements(MaxStatementBudget);
            options.TimeoutInterval(TimeSpan.FromMilliseconds(timeoutMs));
            options.LimitRecursion(MaxRecursionDepth);
            options.Interop.Enabled = false;
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            if (bundle is { HasMultiFileSources: true })
            {
                options.Modules.ModuleLoader = new BundleModuleLoader(bundle);
            }
        });

        // Remove any host-reflection or dynamic-code hatches if present, then inject the frozen global helpers.
        engine.Execute(GlobalPreludeScript);
        engine.Execute(SchemaPrelude.Script);
        engine.Constraints.Reset();
        return engine;
    }

    private static T EvaluateWithEffectiveDefinition<T>(
        CompiledPolicy policy,
        JsonElement inputs,
        string phase,
        Func<Engine, TemplateBundle, ObjectInstance, JsValue, JsValue, T> evaluate) =>
        ExecuteSandboxed(phase, () =>
        {
            // Use policy identity so record copies with modified source load their own bundle.
            var bundle = PolicyBundles.GetValue(policy, static compiled => TemplateBundle.Parse(compiled.JsSource));
            using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
            var schemaBuilder = engine.GetValue("t");
            var rawDef = ValidateDefaultTemplateObject(LoadDefinition(engine, bundle));
            var jsInputs = BuildEffectiveJsInputs(engine, rawDef, inputs);
            var def = ResolveEffectiveDefinition(engine, rawDef, jsInputs);
            return evaluate(engine, bundle, def, jsInputs, schemaBuilder);
        });

    private static ObjectInstance ValidateDefaultTemplateObject(JsValue defaultExport)
    {
        if (defaultExport.IsUndefined() || defaultExport.IsNull() || defaultExport is not ObjectInstance obj || defaultExport.IsArray())
        {
            throw new InvalidOperationException(
                "Template ES module must export a default object via `export default defineTemplate({ ... })`.");
        }

        return obj;
    }

    private static T ExecuteSandboxed<T>(string phase, Func<T> action, int timeoutMs = ExecutionTimeoutMs)
    {
        try
        {
            return action();
        }
        catch (StatementsCountOverflowException ex)
        {
            throw new InvalidOperationException(
                $"Template sandbox statement limit ({MaxStatementBudget}) exceeded during {phase}.",
                ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new InvalidOperationException(
                $"Template sandbox memory limit ({MaxMemoryBytes} bytes) exceeded during {phase}.",
                ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw new InvalidOperationException(
                $"Template sandbox recursion depth limit ({MaxRecursionDepth}) exceeded during {phase}.",
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Template sandbox execution timeout ({timeoutMs}ms) exceeded during {phase}.",
                ex);
        }
        catch (JavaScriptException ex)
        {
            throw new InvalidOperationException(
                $"Template JavaScript error during {phase}: {FormatJavaScriptError(ex)}",
                ex);
        }
        catch (Acornima.ParseErrorException ex)
        {
            throw new InvalidOperationException(
                $"Template JavaScript syntax error during {phase}: " +
                FormatErrorAt(ex.Message, ex.Error.SourceFile ?? "template", ex.LineNumber, ex.Column),
                ex);
        }
    }

    private static string FormatJavaScriptError(JavaScriptException exception)
    {
        var location = exception.Location;
        var source = location.SourceFile;
        var start = location.Start;
        var detail = FormatErrorAt(exception.Message, source ?? "template", start.Line, start.Column + 1);
        var frame = exception.JavaScriptStackTrace?.Split('\n', StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("at ", StringComparison.Ordinal) &&
                (source is null || line.Contains(source, StringComparison.Ordinal)));
        return frame is null ? detail : $"{detail}\n{frame}";
    }

    private static string FormatErrorAt(string message, string source, int line, int column)
    {
        var position = $"{source}:{line}:{column}";
        return line <= 0 || message.Contains(position, StringComparison.Ordinal) ? message : $"{message} ({position})";
    }

    private static ObjectInstance BuildTemplateContextObject(
        Engine engine,
        ObjectInstance def,
        string trackId,
        JsonElement inputs,
        JsonElement data,
        JsonElement? session = null)
    {
        var context = new JsObject(engine);
        context.Set("trackId", trackId);
        context.Set("inputs", BuildEffectiveJsInputs(engine, def, inputs));
        context.Set("data", ParseJsJson(engine, NormalizeObjectJson(data).GetRawText()));
        if (session is { } sessionJson)
            context.Set("session", ParseJsJson(engine, sessionJson.GetRawText()));
        return context;
    }

    private static JsValue BuildEffectiveJsInputs(Engine engine, ObjectInstance def, JsonElement inputs)
    {
        var rawInputsJson = inputs.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : inputs.GetRawText();

        var parsed = ParseJsJson(engine, rawInputsJson);
        var inputsObj = parsed is ObjectInstance obj && !parsed.IsArray()
            ? obj
            : new JsObject(engine);

        if (TryGetOptionalObject(def, "inputs", "template.inputs", out var schemaObj))
        {
            foreach (var key in schemaObj.GetOwnPropertyKeys())
            {
                var propName = key.ToString();
                if (!inputsObj.Get(propName).IsUndefined())
                {
                    continue;
                }

                var fieldSpec = schemaObj.Get(propName);
                if (fieldSpec is ObjectInstance fieldSpecObj)
                {
                    var defaultVal = fieldSpecObj.Get("default");
                    if (!defaultVal.IsUndefined())
                    {
                        inputsObj.Set(propName, defaultVal);
                    }
                }
            }
        }

        return inputsObj;
    }

    private static JsonElement ExtractInputsSchema(Engine engine, ObjectInstance def)
    {
        var inputsVal = def.Get("inputs");
        if (inputsVal.IsUndefined() || inputsVal.IsNull())
        {
            return ParseJsonObject("{}");
        }

        if (inputsVal is not ObjectInstance inputsObj || inputsVal.IsArray())
        {
            throw new InvalidOperationException("template.inputs must be an object mapping input names to schemas.");
        }

        foreach (var key in inputsObj.GetOwnPropertyKeys())
        {
            var fieldName = key.ToString();
            var specVal = inputsObj.Get(fieldName);
            if (specVal is not ObjectInstance specObj || specVal.IsArray())
            {
                throw new InvalidOperationException($"template.inputs.{fieldName} must be an object.");
            }

            var typeStr = ReadRequiredNonEmptyString(specObj, "type", $"template.inputs.{fieldName}.type");
            if (typeStr is not ("string" or "number" or "boolean" or "enum"))
            {
                throw new InvalidOperationException(
                    $"template.inputs.{fieldName}.type must be 'string', 'number', 'boolean', or 'enum' (got '{typeStr}').");
            }
        }

        var schema = JsValueToJsonElement(engine, inputsVal, defaultJson: "{}");
        foreach (var field in schema.EnumerateObject())
        {
            if (field.Value.GetProperty("type").GetString() == "enum" &&
                (!field.Value.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array ||
                 values.GetArrayLength() == 0 || values.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)))
                throw new InvalidOperationException($"template.inputs.{field.Name}.values must be a nonempty array of strings.");
        }
        return schema;
    }

    private static GuardDecision ParseGuardDecision(Engine engine, JsValue value, string hookName)
    {
        if (value is not ObjectInstance obj || value.IsArray())
        {
            throw new InvalidOperationException(
                $"{hookName} must return a GuardDecision object (use Allow(), Deny(reason), or AskHuman(title, labels?)).");
        }

        if (!obj.Get("patch").IsUndefined()) throw new InvalidOperationException("GuardDecision does not support patches.");
        var kind = ReadRequiredNonEmptyString(obj, "kind", $"{hookName}.result.kind");
        return kind switch
        {
            "allow" => new GuardDecision.Allow(),
            "deny" => new GuardDecision.Deny(
                ReadRequiredNonEmptyString(obj, "reason", $"{hookName}.result.reason")),
            "ask_human" => ParseAskHuman(obj, hookName),
            _ => throw new InvalidOperationException(
                $"Unknown GuardDecision kind '{kind}' returned from {hookName}.")
        };
    }

    private static GuardDecision.AskHuman ParseAskHuman(ObjectInstance obj, string hookName)
    {
        if (obj.HasProperty("options"))
            throw new InvalidOperationException("GuardDecision.AskHuman does not support options; use approveLabel and denyLabel.");

        return new GuardDecision.AskHuman(
            ReadAskHumanString(obj, "title", $"{hookName}.result.title"),
            obj.Get("approveLabel").IsUndefined() ? "Approve" : ReadAskHumanString(obj, "approveLabel", $"{hookName}.result.approveLabel"),
            obj.Get("denyLabel").IsUndefined() ? "Deny" : ReadAskHumanString(obj, "denyLabel", $"{hookName}.result.denyLabel"));
    }

    private static string ReadAskHumanString(ObjectInstance obj, string prop, string path)
    {
        var value = obj.Get(prop);
        // ECMAScript trim whitespace includes FEFF and excludes .NET's NEL (0085).
        if (!value.IsString() || value.AsString().All(c =>
            c is '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or '\u1680' or
                >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF'))
            throw new InvalidOperationException($"{path} must be a non-blank string.");
        return value.AsString();
    }

    private static string? ExtractOptionalStringResult(JsValue value, string hookName)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value.IsString())
        {
            return value.AsString();
        }

        throw new InvalidOperationException($"{hookName} must return a string or undefined/void.");
    }

    private static JsValue ParseJsJson(Engine engine, string json)
    {
        var parser = new JsonParser(engine);
        return parser.Parse(json);
    }

    private static JsonElement JsValueToJsonElement(Engine engine, JsValue value, string defaultJson)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return ParseJsonObject(defaultJson);
        }

        var serializer = new Jint.Native.Json.JsonSerializer(engine);
        var serialized = serializer.Serialize(value, JsValue.Undefined, JsValue.Undefined);
        if (!serialized.IsString())
        {
            return ParseJsonObject(defaultJson);
        }

        return ParseJsonObject(serialized.AsString());
    }

    private static JsonElement NormalizeObjectJson(JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? ParseJsonObject("{}")
            : value.Clone();

    private static JsonElement ParseJsonObject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static bool TryGetOptionalObject(ObjectInstance parent, string prop, string path, out ObjectInstance child)
    {
        var val = parent.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            child = null!;
            return false;
        }

        if (val is not ObjectInstance obj || val.IsArray())
        {
            throw new InvalidOperationException($"{path} must be an object.");
        }

        child = obj;
        return true;
    }

    private static void EnsureOptionalFunction(ObjectInstance parent, string prop, string path)
    {
        var val = parent.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            return;
        }

        if (val is not Function)
        {
            throw new InvalidOperationException($"{path} must be a function.");
        }
    }

    private static string ReadRequiredNonEmptyString(ObjectInstance obj, string prop, string path)
    {
        var val = obj.Get(prop);
        if (!val.IsString())
        {
            throw new InvalidOperationException($"{path} must be a non-empty string.");
        }

        var str = val.AsString();
        if (string.IsNullOrWhiteSpace(str))
        {
            throw new InvalidOperationException($"{path} cannot be blank.");
        }

        return str;
    }

    private static string? ReadOptionalString(ObjectInstance obj, string prop, string path)
    {
        var val = obj.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            return null;
        }

        if (!val.IsString())
        {
            throw new InvalidOperationException($"{path} must be a string.");
        }

        return val.AsString();
    }

    private static bool? ReadOptionalBool(ObjectInstance obj, string prop, string path)
    {
        var val = obj.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            return null;
        }

        if (!val.IsBoolean())
        {
            throw new InvalidOperationException($"{path} must be a boolean.");
        }

        return val.AsBoolean();
    }

    private static IReadOnlyList<string> ReadOptionalStringList(ObjectInstance obj, string prop, string path)
    {
        var val = obj.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            return [];
        }

        return ReadStringArrayValue(val, path);
    }

    private static List<string> ReadStringArrayValue(JsValue val, string path)
    {
        if (!val.IsArray())
        {
            throw new InvalidOperationException($"{path} must be an array of strings.");
        }

        var arr = val.AsArray();
        var result = new List<string>((int)arr.Length);
        for (uint i = 0; i < arr.Length; i++)
        {
            var item = arr.Get(i);
            if (!item.IsString())
            {
                throw new InvalidOperationException($"{path}[{i}] must be a string.");
            }

            result.Add(item.AsString());
        }

        return result;
    }

}
