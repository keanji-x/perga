using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;

namespace Perga.PolicyVm;

public sealed class PolicyVm : IPolicyVm
{
    private const string EntryModuleSpecifier = "template.perga.js";
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
        globalThis.AskHuman = function AskHuman(title, options) {
          if (typeof title !== "string" || title.trim().length === 0) {
            throw new TypeError("AskHuman(title, options?) requires a non-empty string title.");
          }
          const normalized = options === undefined || options === null
            ? []
            : Array.isArray(options)
              ? options.map((item) => String(item))
              : (() => { throw new TypeError("AskHuman options must be an array of strings."); })();
          return { kind: "ask_human", title, options: normalized };
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
            using var engine = CreateSandboxedEngine(CompileTimeoutMs);
            var def = LoadDefaultTemplateObject(engine, jsModuleSource);

            var id = ReadRequiredNonEmptyString(def, "id", "template.id");
            var title = ReadRequiredNonEmptyString(def, "title", "template.title");
            var description = ReadOptionalString(def, "description", "template.description");
            var defaultPlugins = ReadOptionalStringList(def, "plugins", "template.plugins");
            var inputsSchema = ExtractInputsSchema(engine, def);
            var initialMarkdown = ReadOptionalString(def, "initialMarkdown", "template.initialMarkdown") ?? string.Empty;

            EnsureOptionalFunction(def, "initialState", "template.initialState");
            EnsureOptionalFunction(def, "buildTurnContext", "template.buildTurnContext");
            EnsureOptionalFunction(def, "guardToolCall", "template.guardToolCall");

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
            if (TryGetOptionalObject(def, "report", "template.report", out var reportObj))
            {
                maxProseChars = ReadOptionalInt(reportObj, "maxProseChars", "template.report.maxProseChars");
                if (maxProseChars is <= 0)
                {
                    throw new InvalidOperationException("template.report.maxProseChars must be positive when specified.");
                }

                EnsureOptionalFunction(reportObj, "customValidate", "template.report.customValidate");
                sections = ParseSectionRules(reportObj);
            }

            // Tasks spec
            var requireGateForReadWrite = false;
            string? afterWorkerDoneCmd = null;
            WorkerDispatchDefaults? workerDispatch = null;
            if (TryGetOptionalObject(def, "tasks", "template.tasks", out var tasksObj))
            {
                requireGateForReadWrite = ReadOptionalBool(tasksObj, "requireGateForReadWrite", "template.tasks.requireGateForReadWrite") ?? false;
                afterWorkerDoneCmd = ReadOptionalString(tasksObj, "afterWorkerDoneCmd", "template.tasks.afterWorkerDoneCmd");
                EnsureOptionalFunction(tasksObj, "onSettled", "template.tasks.onSettled");
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
                InitialReportMarkdown: initialMarkdown,
                WorkerDispatch: workerDispatch);

            return new CompiledPolicy(id, jsModuleSource, manifest);
        }, CompileTimeoutMs);
    }

    public PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return ExecuteSandboxed("InitState", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);
            var jsInputs = BuildEffectiveJsInputs(engine, def, inputs);

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

    public PolicyEvalResult<string?> BuildTurnContext(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ctx);

        return ExecuteSandboxed("BuildTurnContext", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);

            var hookVal = def.Get("buildTurnContext");
            if (hookVal.IsUndefined() || hookVal.IsNull())
            {
                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            if (hookVal is not Function hookFn)
            {
                throw new InvalidOperationException("template.buildTurnContext must be a function.");
            }

            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var sessionObj = ParseJsJson(engine, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = ctx.SessionId,
                ["isLead"] = ctx.IsLead,
                ["boundAttemptId"] = ctx.BoundAttemptId,
                ["contextUsageRatio"] = ctx.ContextUsageRatio
            }));

            if (ctx.BoundAttemptId is null && sessionObj is ObjectInstance sessionInst)
            {
                sessionInst.Delete("boundAttemptId");
            }

            var result = engine.Call(hookFn, def, [templateCtx, sessionObj]);
            var nextState = ExtractContextState(engine, templateCtx);
            var text = ExtractOptionalStringResult(result, "buildTurnContext");
            return new PolicyEvalResult<string?>(nextState, text);
        });
    }

    public PolicyEvalResult<GuardDecision> GuardReportCommit(
        CompiledPolicy policy,
        PolicyReportCommitContext ctx,
        JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ctx);

        var preservedState = NormalizeStateJson(state);

        // 1. Static MaxProseChars limit
        if (policy.Manifest.MaxProseChars is { } maxProse && ctx.NextProseChars > maxProse)
        {
            return new PolicyEvalResult<GuardDecision>(
                preservedState,
                new GuardDecision.Deny(
                    $"Report prose length ({ctx.NextProseChars} chars) exceeds template limit ({maxProse} chars)."));
        }

        // 2. Static SectionRule.EditableBy enforcement (case-insensitive)
        if (policy.Manifest.Sections.Count > 0 && ctx.TouchedSections.Count > 0)
        {
            foreach (var touched in ctx.TouchedSections)
            {
                var normalizedTouched = NormalizeHeading(touched);
                var matchingRule = policy.Manifest.Sections.FirstOrDefault(r =>
                    string.Equals(NormalizeHeading(r.H1), normalizedTouched, StringComparison.OrdinalIgnoreCase));

                if (matchingRule?.EditableBy is { } allowedRoles)
                {
                    var isAllowed = allowedRoles.Any(role =>
                        string.Equals(role, ctx.EditorRole, StringComparison.OrdinalIgnoreCase));
                    if (!isAllowed)
                    {
                        var allowedText = allowedRoles.Count > 0 ? string.Join(", ", allowedRoles) : "none";
                        return new PolicyEvalResult<GuardDecision>(
                            preservedState,
                            new GuardDecision.Deny(
                                $"Section '{matchingRule.H1}' cannot be edited by role '{ctx.EditorRole}' (editableBy: {allowedText})."));
                    }
                }
            }
        }

        // 3. Dynamic JS report.customValidate(ctx, info)
        return ExecuteSandboxed("GuardReportCommit", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);

            if (!TryGetOptionalObject(def, "report", "template.report", out var reportObj))
            {
                return new PolicyEvalResult<GuardDecision>(preservedState, new GuardDecision.Allow());
            }

            var customValidateVal = reportObj.Get("customValidate");
            if (customValidateVal.IsUndefined() || customValidateVal.IsNull())
            {
                return new PolicyEvalResult<GuardDecision>(preservedState, new GuardDecision.Allow());
            }

            if (customValidateVal is not Function customValidateFn)
            {
                throw new InvalidOperationException("template.report.customValidate must be a function.");
            }

            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var infoObj = ParseJsJson(engine, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["editorRole"] = ctx.EditorRole,
                ["prevMarkdown"] = ctx.PrevMarkdown,
                ["nextMarkdown"] = ctx.NextMarkdown,
                ["touchedSections"] = ctx.TouchedSections,
                ["nextProseChars"] = ctx.NextProseChars
            }));

            var decisionVal = engine.Call(customValidateFn, reportObj, [templateCtx, infoObj]);
            var decision = ParseGuardDecision(decisionVal, "report.customValidate");
            var nextState = ExtractContextState(engine, templateCtx);
            return new PolicyEvalResult<GuardDecision>(nextState, decision);
        });
    }

    public PolicyEvalResult<GuardDecision> GuardToolCall(
        CompiledPolicy policy,
        PolicyToolCallContext ctx,
        JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ctx);

        return ExecuteSandboxed("GuardToolCall", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);

            var hookVal = def.Get("guardToolCall");
            if (hookVal.IsUndefined() || hookVal.IsNull())
            {
                return new PolicyEvalResult<GuardDecision>(NormalizeStateJson(state), new GuardDecision.Allow());
            }

            if (hookVal is not Function hookFn)
            {
                throw new InvalidOperationException("template.guardToolCall must be a function.");
            }

            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var argsRawJson = ctx.Arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : ctx.Arguments.GetRawText();

            var callObj = new JsObject(engine);
            callObj.Set("sessionId", ctx.SessionId);
            callObj.Set("isLead", ctx.IsLead);
            if (ctx.BoundAttemptId is not null)
            {
                callObj.Set("boundAttemptId", ctx.BoundAttemptId);
            }

            callObj.Set("mcpServer", ctx.McpServer);
            callObj.Set("toolName", ctx.ToolName);
            callObj.Set("arguments", ParseJsJson(engine, argsRawJson));

            var decisionVal = engine.Call(hookFn, def, [templateCtx, callObj]);
            var decision = ParseGuardDecision(decisionVal, "guardToolCall");
            var nextState = ExtractContextState(engine, templateCtx);
            return new PolicyEvalResult<GuardDecision>(nextState, decision);
        });
    }

    public PolicyEvalResult<string?> OnTaskSettled(
        CompiledPolicy policy,
        PolicyTaskSettledContext ctx,
        JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ctx);

        return ExecuteSandboxed("OnTaskSettled", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);

            if (!TryGetOptionalObject(def, "tasks", "template.tasks", out var tasksObj))
            {
                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            var onSettledVal = tasksObj.Get("onSettled");
            if (onSettledVal.IsUndefined() || onSettledVal.IsNull())
            {
                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            if (onSettledVal is not Function onSettledFn)
            {
                throw new InvalidOperationException("template.tasks.onSettled must be a function.");
            }

            var templateCtx = BuildTemplateContextObject(engine, def, ctx.TrackId, ctx.Inputs, state);
            var metaJson = ctx.AttemptMeta.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : ctx.AttemptMeta.GetRawText();

            var eventObj = new JsObject(engine);
            eventObj.Set("taskKey", ctx.TaskKey);
            eventObj.Set("accessMode", ctx.AccessMode);
            eventObj.Set("attemptStatus", ctx.AttemptStatus);
            if (ctx.WorkerResult is { ValueKind: not JsonValueKind.Undefined } wr)
            {
                eventObj.Set("workerResult", ParseJsJson(engine, wr.GetRawText()));
            }

            eventObj.Set("meta", ParseJsJson(engine, metaJson));
            if (ctx.GateVerdict is { ValueKind: not JsonValueKind.Undefined } gv)
            {
                eventObj.Set("gateVerdict", ParseJsJson(engine, gv.GetRawText()));
            }

            var result = engine.Call(onSettledFn, tasksObj, [templateCtx, eventObj]);
            var nextState = ExtractContextState(engine, templateCtx);
            var wakeNote = ExtractOptionalStringResult(result, "tasks.onSettled");
            return new PolicyEvalResult<string?>(nextState, wakeNote);
        });
    }

    public PolicyEvalResult<string?> BuildScheduleWakePrompt(
        CompiledPolicy policy,
        string scheduleId,
        JsonElement inputs,
        JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);

        return ExecuteSandboxed("BuildScheduleWakePrompt", () =>
        {
            using var engine = CreateSandboxedEngine();
            var def = LoadDefaultTemplateObject(engine, policy.JsSource);

            var schedulesVal = def.Get("schedules");
            if (schedulesVal.IsUndefined() || schedulesVal.IsNull())
            {
                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            if (!schedulesVal.IsArray())
            {
                throw new InvalidOperationException("template.schedules must be an array.");
            }

            var arr = schedulesVal.AsArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                var item = arr.Get(i);
                if (item is not ObjectInstance schedObj)
                {
                    continue;
                }

                var idVal = schedObj.Get("id");
                if (!idVal.IsString() || !string.Equals(idVal.AsString(), scheduleId, StringComparison.Ordinal))
                {
                    continue;
                }

                var wakePromptVal = schedObj.Get("wakePrompt");
                if (wakePromptVal.IsString())
                {
                    return new PolicyEvalResult<string?>(NormalizeStateJson(state), wakePromptVal.AsString());
                }

                if (wakePromptVal is Function wakeFn)
                {
                    var templateCtx = BuildTemplateContextObject(engine, def, string.Empty, inputs, state);
                    var outputVal = engine.Call(wakeFn, schedObj, [templateCtx]);
                    var nextState = ExtractContextState(engine, templateCtx);
                    var prompt = ExtractOptionalStringResult(outputVal, $"schedules[{scheduleId}].wakePrompt");
                    return new PolicyEvalResult<string?>(nextState, prompt);
                }

                return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
            }

            return new PolicyEvalResult<string?>(NormalizeStateJson(state), null);
        });
    }

    private static Engine CreateSandboxedEngine(int timeoutMs = ExecutionTimeoutMs)
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
        });

        // Remove any host-reflection or dynamic-code hatches if present, then inject the 4 frozen global helpers.
        engine.Execute(GlobalPreludeScript);
        engine.Constraints.Reset();
        return engine;
    }

    private static ObjectInstance LoadDefaultTemplateObject(Engine engine, string jsModuleSource)
    {
        engine.Modules.Add(EntryModuleSpecifier, jsModuleSource);
        var moduleNs = engine.Modules.Import(EntryModuleSpecifier);
        var defaultExport = moduleNs.Get("default");
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

        return JsValueToJsonElement(engine, inputsVal, defaultJson: "{}");
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

            var wakeVal = schedObj.Get("wakePrompt");
            string? staticWakePrompt;
            if (wakeVal.IsString())
            {
                staticWakePrompt = wakeVal.AsString();
            }
            else if (wakeVal is Function)
            {
                staticWakePrompt = null;
            }
            else
            {
                throw new InvalidOperationException(
                    $"template.schedules[{i}].wakePrompt must be a string or a function.");
            }

            list.Add(new ScheduleSpec(id, cron, timezone, staticWakePrompt));
        }

        return list;
    }

    private static GuardDecision ParseGuardDecision(JsValue value, string hookName)
    {
        if (value is not ObjectInstance obj || value.IsArray())
        {
            throw new InvalidOperationException(
                $"{hookName} must return a GuardDecision object (use Allow(), Deny(reason), or AskHuman(title, options?)).");
        }

        var kind = ReadRequiredNonEmptyString(obj, "kind", $"{hookName}.result.kind");
        return kind switch
        {
            "allow" => new GuardDecision.Allow(),
            "deny" => new GuardDecision.Deny(
                ReadRequiredNonEmptyString(obj, "reason", $"{hookName}.result.reason")),
            "ask_human" => new GuardDecision.AskHuman(
                ReadRequiredNonEmptyString(obj, "title", $"{hookName}.result.title"),
                ReadOptionalStringList(obj, "options", $"{hookName}.result.options")),
            _ => throw new InvalidOperationException(
                $"Unknown GuardDecision kind '{kind}' returned from {hookName}.")
        };
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

    private static string NormalizeHeading(string h1)
    {
        var trimmed = (h1 ?? string.Empty).Trim();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed.TrimStart('#').Trim();
        }

        return trimmed;
    }
}
