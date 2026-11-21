using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm : IPolicyVm
{
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
            var loaded = LoadDefinition(engine, bundle);
            var rawDef = ValidateDefaultTemplateObject(loaded.Definition);

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
            EnsureOptionalFunction(def, "after", "template.after");
            EnsureOptionalFunction(def, "initialState", "template.initialState");
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
            int? maxProseChars = null;
            var sections = new List<SectionRule>();
            var humanReadOnly = false;
            var viewNames = new List<string>();
            if (TryGetOptionalObject(def, "report", "template.report", out var reportObj))
            {
                maxProseChars = ReadOptionalInt(reportObj, "maxProseChars", "template.report.maxProseChars");
                if (maxProseChars is <= 0)
                {
                    throw new InvalidOperationException("template.report.maxProseChars must be positive when specified.");
                }

                humanReadOnly = ReadOptionalBool(reportObj, "humanReadOnly", "template.report.humanReadOnly") ?? false;
                sections = ParseSectionRules(reportObj);
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
            var checkpoints = ParseCheckpoints(def);
            ValidateCrossFileReferencesAndInitialData(
                engine, def, defaultInputsJs, bundle, initialReportMarkdown, viewNames, tHelper, skipCrossFileChecks: !string.IsNullOrWhiteSpace(extendsId));

            // Tasks spec
            var requireGateForReadWrite = false;
            string? afterWorkerDoneCmd = null;
            WorkerDispatchDefaults? workerDispatch = null;
            if (TryGetOptionalObject(def, "tasks", "template.tasks", out var tasksObj))
            {
                requireGateForReadWrite = ReadOptionalBool(tasksObj, "requireGateForReadWrite", "template.tasks.requireGateForReadWrite") ?? false;
                afterWorkerDoneCmd = ReadOptionalString(tasksObj, "afterWorkerDoneCmd", "template.tasks.afterWorkerDoneCmd");
                if (TryGetOptionalObject(tasksObj, "dispatch", "template.tasks.dispatch", out var dispatch))
                {
                    var access = ReadOptionalString(dispatch, "access", "template.tasks.dispatch.access");
                    if (access is not (null or "read_only" or "read_write"))
                        throw new InvalidOperationException("template.tasks.dispatch.access must be read_only or read_write.");
                    workerDispatch = new WorkerDispatchDefaults(
                        ReadOptionalString(dispatch, "section", "template.tasks.dispatch.section"), access,
                        ReadOptionalString(dispatch, "gateInput", "template.tasks.dispatch.gateInput"));
                }
            }

            // Schedules spec
            var schedules = ParseSchedules(def);

            var manifest = new CompiledTemplateManifest(
                Id: id,
                Title: title,
                Description: description,
                DefaultPlugins: defaultPlugins,
                InputsSchema: inputsSchema,
                Sections: sections,
                MaxProseChars: maxProseChars,
                RequireGateForReadWrite: requireGateForReadWrite,
                WorkspaceDriver: workspaceDriver,
                WorkspaceLowerCacheDirs: lowerCacheDirs,
                WorkspaceCreateCmd: workspaceCreateCmd,
                WorkspaceCloseCmd: workspaceCloseCmd,
                AfterWorkerDoneCmd: afterWorkerDoneCmd,
                Schedules: schedules,
                Routing: ReadRoutingRules(engine, def),
                WorkerDispatch: workerDispatch,
                Extends: ReadOptionalString(rawDef, "extends", "template.extends"),
                HasHeaderView: !def.Get("headerView").IsUndefined(),
                InitialReportMarkdown: initialReportMarkdown,
                Checkpoints: checkpoints.Count > 0 ? checkpoints : null,
                ViewNames: viewNames.Count > 0 ? viewNames : null,
                HumanReadOnly: humanReadOnly);

            return new CompiledPolicy(id, jsModuleSource, manifest,
                loaded.LegacyBefore, loaded.LegacyAfter);
        }, CompileTimeoutMs);
    }

    public PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return ExecuteSandboxed("InitState", () =>
        {
            var bundle = TemplateBundle.Parse(policy.JsSource);
            using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
            var rawDef = LoadDefaultTemplateObject(engine, bundle);
            var jsInputs = BuildEffectiveJsInputs(engine, rawDef, inputs);
            var def = ResolveEffectiveDefinition(engine, rawDef, jsInputs);

            var initialStateFn = def.Get("initialState");
            if (initialStateFn.IsUndefined() || initialStateFn.IsNull())
            {
                var empty = ParseJsonObject("{}");
                return new PolicyEvalResult<JsonElement>(empty, empty.Clone());
            }

            if (initialStateFn is not Function fn)
            {
                throw new InvalidOperationException("template.initialState must be a function.");
            }

            var returned = engine.Call(fn, def, [jsInputs]);
            var stateElem = JsValueToJsonElement(engine, returned, defaultJson: "{}");
            return new PolicyEvalResult<JsonElement>(stateElem, stateElem.Clone());
        });
    }

    public PolicyEvalResult<PolicyTurnPreparation> PrepareTurn(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state)
    {
        var context = EvaluateTurnContext(policy, ctx, state);
        var before = Before(policy, "turn.before", ctx.TrackId, System.Text.Json.JsonSerializer.SerializeToElement(new {
            sessionId = ctx.SessionId, isLead = ctx.IsLead, boundTaskId = ctx.BoundTaskId,
            contextUsageRatio = ctx.ContextUsageRatio, context = context.Output
        }), ctx.Inputs, context.NextState);
        return new(before.NextState, new(context.Output, before.Output));
    }

    private PolicyEvalResult<string?> EvaluateTurnContext(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ctx);

        return ExecuteSandboxed("TurnContext", () =>
        {
            var bundle = TemplateBundle.Parse(policy.JsSource);
            using var engine = CreateSandboxedEngine(ExecutionTimeoutMs, bundle);
            var rawDef = LoadDefaultTemplateObject(engine, bundle);
            var jsInputs = BuildEffectiveJsInputs(engine, rawDef, ctx.Inputs);
            var def = ResolveEffectiveDefinition(engine, rawDef, jsInputs);

            var hookVal = def.Get("turnContext");
            if (hookVal.IsUndefined() || hookVal.IsNull())
            {
                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            if (hookVal is not Function hookFn)
            {
                throw new InvalidOperationException("template.turnContext must be a function.");
            }

            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var sessionObj = ParseJsJson(engine, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = ctx.SessionId,
                ["isLead"] = ctx.IsLead,
                ["boundTaskId"] = ctx.BoundTaskId,
                ["contextUsageRatio"] = ctx.ContextUsageRatio
            }));

            if (ctx.BoundTaskId is null && sessionObj is ObjectInstance sessionInst)
            {
                sessionInst.Delete("boundTaskId");
            }

            ((ObjectInstance)templateCtx).Set("session", sessionObj);
            var result = engine.Call(hookFn, def, [templateCtx]);
            var nextState = ExtractContextState(engine, templateCtx);
            var text = ExtractOptionalStringResult(result, "turnContext");
            return new PolicyEvalResult<string?>(nextState, text);
        });
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

    private static ObjectInstance LoadDefaultTemplateObject(Engine engine, string jsModuleSource) =>
        LoadDefaultTemplateObject(engine, TemplateBundle.Parse(jsModuleSource));

    private static ObjectInstance LoadDefaultTemplateObject(Engine engine, TemplateBundle bundle) =>
        ValidateDefaultTemplateObject(LoadDefinition(engine, bundle).Definition);

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
                $"Policy sandbox statement limit ({MaxStatementBudget}) exceeded during {phase}.",
                ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new InvalidOperationException(
                $"Policy sandbox memory limit ({MaxMemoryBytes} bytes) exceeded during {phase}.",
                ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw new InvalidOperationException(
                $"Policy sandbox recursion depth limit ({MaxRecursionDepth}) exceeded during {phase}.",
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Policy sandbox execution timeout ({timeoutMs}ms) exceeded during {phase}.",
                ex);
        }
        catch (JavaScriptException ex)
        {
            throw new InvalidOperationException(
                $"Policy JavaScript error during {phase}: {ex.Message}",
                ex);
        }
        catch (Acornima.ParseErrorException ex)
        {
            throw new InvalidOperationException(
                $"Policy JavaScript syntax error during {phase}: {ex.Message}",
                ex);
        }
    }

    private static ObjectInstance BuildTemplateContextObject(
        Engine engine,
        ObjectInstance def,
        string trackId,
        JsonElement inputs,
        JsonElement state)
    {
        var effectiveInputs = BuildEffectiveJsInputs(engine, def, inputs);
        var rawStateJson = state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : state.GetRawText();

        var jsState = ParseJsJson(engine, rawStateJson);
        if (jsState.IsUndefined() || jsState.IsNull() || jsState is not ObjectInstance || jsState.IsArray())
        {
            jsState = new JsObject(engine);
        }

        var ctxObj = new JsObject(engine);
        ctxObj.Set("trackId", trackId);
        ctxObj.Set("inputs", effectiveInputs);
        ctxObj.Set("state", jsState);
        return ctxObj;
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

    private static JsonElement ExtractContextState(Engine engine, ObjectInstance templateCtx)
    {
        var stateVal = templateCtx.Get("state");
        return JsValueToJsonElement(engine, stateVal, defaultJson: "{}");
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

    private static List<SectionRule> ParseSectionRules(ObjectInstance reportObj)
    {
        var sectionsVal = reportObj.Get("sections");
        if (sectionsVal.IsUndefined() || sectionsVal.IsNull())
        {
            return [];
        }

        if (!sectionsVal.IsArray())
        {
            throw new InvalidOperationException("template.report.sections must be an array.");
        }

        var list = new List<SectionRule>();
        var arr = sectionsVal.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var item = arr.Get(i);
            if (item is not ObjectInstance secObj || item.IsArray())
            {
                throw new InvalidOperationException($"template.report.sections[{i}] must be an object.");
            }

            var h1 = ReadRequiredNonEmptyString(secObj, "h1", $"template.report.sections[{i}].h1");
            var omitIfEmpty = ReadOptionalBool(secObj, "omitIfEmpty", $"template.report.sections[{i}].omitIfEmpty") ?? false;

            IReadOnlyList<string>? editableBy = null;
            var editableVal = secObj.Get("editableBy");
            if (!editableVal.IsUndefined() && !editableVal.IsNull())
            {
                editableBy = ReadStringArrayValue(editableVal, $"template.report.sections[{i}].editableBy");
            }

            list.Add(new SectionRule(h1, omitIfEmpty, editableBy));
        }

        return list;
    }

    private static List<ScheduleSpec> ParseSchedules(ObjectInstance def)
    {
        var schedulesVal = def.Get("schedules");
        if (schedulesVal.IsUndefined() || schedulesVal.IsNull())
        {
            return [];
        }

        if (!schedulesVal.IsArray())
        {
            throw new InvalidOperationException("template.schedules must be an array.");
        }

        var list = new List<ScheduleSpec>();
        var arr = schedulesVal.AsArray();
        for (uint i = 0; i < arr.Length; i++)
        {
            var item = arr.Get(i);
            if (item is not ObjectInstance schedObj || item.IsArray())
            {
                throw new InvalidOperationException($"template.schedules[{i}] must be an object.");
            }

            var id = ReadRequiredNonEmptyString(schedObj, "id", $"template.schedules[{i}].id");
            var cron = ReadRequiredNonEmptyString(schedObj, "cron", $"template.schedules[{i}].cron");
            var timezone = ReadOptionalString(schedObj, "timezone", $"template.schedules[{i}].timezone") ?? "UTC";

            if (schedObj.HasProperty("wakePrompt"))
                throw new InvalidOperationException("Removed schedules.wakePrompt; use prompt and after(schedule.fired).");
            var prompt = ReadRequiredNonEmptyString(schedObj, "prompt", $"template.schedules[{i}].prompt");
            var title = ReadOptionalString(schedObj, "title", $"template.schedules[{i}].title");
            if (id.Length > 128 || id.Any(char.IsWhiteSpace) || list.Any(s => s.Id == id))
                throw new InvalidOperationException($"Invalid or duplicate schedule id '{id}'.");
            var parts = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!Cronos.CronExpression.TryParse(cron, parts.Length == 6 ? Cronos.CronFormat.IncludeSeconds : Cronos.CronFormat.Standard, out _))
                throw new InvalidOperationException($"Invalid cron expression '{cron}'.");
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _))
                throw new InvalidOperationException($"Unknown schedule timezone '{timezone}'.");
            list.Add(new ScheduleSpec(id, cron, timezone, prompt, title));
        }

        return list;
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

    private static JsonElement NormalizeStateJson(JsonElement state) =>
        state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? ParseJsonObject("{}")
            : state.Clone();

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

    private static int? ReadOptionalInt(ObjectInstance obj, string prop, string path)
    {
        var val = obj.Get(prop);
        if (val.IsUndefined() || val.IsNull())
        {
            return null;
        }

        if (!val.IsNumber())
        {
            throw new InvalidOperationException($"{path} must be an integer number.");
        }

        var num = val.AsNumber();
        if (double.IsNaN(num) || double.IsInfinity(num) || Math.Floor(num) != num || num < int.MinValue || num > int.MaxValue)
        {
            throw new InvalidOperationException($"{path} must be a finite 32-bit integer.");
        }

        return (int)num;
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
