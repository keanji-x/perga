using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    // Snapshots contain source modules rather than serialized functions. Every base is frozen.
    public static string Inherit(CompiledPolicy parent, string childSource) =>
        System.Text.Json.JsonSerializer.Serialize(new { format = "perga.snapshot.v1", sources = new[] { parent.JsSource, childSource } });

    private static JsValue LoadDefinition(Engine engine, string source)
    {
        engine.Execute(DefinitionHelpers);
        var index = 0;
        JsValue Load(string text, int depth = 0)
        {
            if (depth > MaxRecursionDepth) throw new InvalidOperationException("Template inheritance exceeds the maximum depth.");
            if (text.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("format", out var format) && format.GetString() == "perga.snapshot.v1")
                {
                    var sources = doc.RootElement.GetProperty("sources").EnumerateArray().ToArray();
                    if (sources.Length != 2) throw new InvalidOperationException("Invalid template snapshot.");
                    return engine.Invoke("__mergeTemplate", Load(sources[0].GetString()!, depth + 1), Load(sources[1].GetString()!, depth + 1));
                }
                text = "export default " + text + ";";
            }
            var name = $"template-{index++}.perga.js";
            engine.Modules.Add(name, text);
            var definition = engine.Modules.Import(name).Get("default");
            // Validate every source before merging: a child must not hide a legacy base hook.
            RejectLegacyHooks(definition);
            return definition;
        }
        return engine.Invoke("__adaptTemplate", Load(source));
    }

    private static void RejectLegacyHooks(JsValue definition)
    {
        if (definition is not ObjectInstance obj) return;

        Reject(obj, "guardToolCall", "guardToolCall");
        Reject(obj, "buildTurnContext", "buildTurnContext");
        if (obj.Get("report") is ObjectInstance report)
            Reject(report, "customValidate", "report.customValidate");
        if (obj.Get("tasks") is ObjectInstance tasks)
            Reject(tasks, "onSettled", "tasks.onSettled");

        static void Reject(ObjectInstance owner, string property, string path)
        {
            if (owner.HasProperty(property))
                throw new InvalidOperationException(
                    $"Removed legacy template hook '{path}' is not supported. Migrate to before/after/turnContext/headerView.");
        }
    }

    public PolicyEvalResult<GuardDecision> Before(CompiledPolicy policy, string action, string trackId,
        JsonElement data, JsonElement inputs, JsonElement state) => ExecuteSandboxed("Before", () =>
    {
        using var engine = CreateSandboxedEngine();
        var def = LoadDefaultTemplateObject(engine, policy.JsSource);
        var ctx = BuildTemplateContextObject(engine, def, trackId, inputs, state);
        var payload = ParseJsJson(engine, data.GetRawText());
        var value = engine.Invoke("__beforeTemplate", def, action, payload, ctx);
        var decision = ParseGuardDecision(engine, value, "before");
        return new PolicyEvalResult<GuardDecision>(ExtractContextState(engine, ctx), decision);
    });

    public PolicyEvalResult<string?> After(CompiledPolicy policy, string eventName, string trackId,
        JsonElement data, JsonElement inputs, JsonElement state) => ExecuteSandboxed("After", () =>
    {
        using var engine = CreateSandboxedEngine();
        var def = LoadDefaultTemplateObject(engine, policy.JsSource);
        var ctx = BuildTemplateContextObject(engine, def, trackId, inputs, state);
        var payload = ParseJsJson(engine, data.GetRawText());
        var result = def.Get("after") is Function hook
            ? engine.Call(hook, def, [engine.Invoke("__templateAction", eventName, payload), ctx]) : JsValue.Undefined;
        return new PolicyEvalResult<string?>(ExtractContextState(engine, ctx), ExtractOptionalStringResult(result, "after"));
    });

    public PolicyTaskRoute RouteTaskDispatch(CompiledPolicy policy, string? role, string access, string? provider, string? model, JsonElement inputs)
    {
        var selectedProvider = provider;
        var selectedModel = model;
        for (var i = 0; i < policy.Manifest.Routing.Count; i++)
        {
            var rule = policy.Manifest.Routing[i];
            if (rule.When.Role is { } r && r != role || rule.When.Access is { } a && a != access) continue;
            var target = ResolveRoutingTarget(policy, rule, inputs);
            if (rule.Enforce && provider is not null && target.Provider is not null && provider != target.Provider)
                return new(selectedProvider, selectedModel, $"Task routing requires provider {target.Provider}");
            if (rule.Enforce && model is not null && target.Model is not null && model != target.Model)
                return new(selectedProvider, selectedModel, $"Task routing requires model {target.Model}");
            selectedProvider ??= target.Provider;
            selectedModel ??= target.Model;
        }
        return new(selectedProvider, selectedModel);
    }

    public IReadOnlyList<PolicyTaskRoute> ResolveRoutingTargets(CompiledPolicy policy, JsonElement inputs)
        => policy.Manifest.Routing.Select(rule => ResolveRoutingTarget(policy, rule, inputs)).ToArray();

    private static PolicyTaskRoute ResolveRoutingTarget(CompiledPolicy policy, TaskRoutingRule rule, JsonElement inputs)
    {
        string? Interpolate(string? value) => value is null ? null : System.Text.RegularExpressions.Regex.Replace(value,
            @"\$\{inputs\.([A-Za-z0-9_]+)\}", match => {
                var key = match.Groups[1].Value;
                if (inputs.TryGetProperty(key, out var supplied)) return supplied.ToString();
                if (policy.Manifest.InputsSchema.TryGetProperty(key, out var schema) && schema.TryGetProperty("default", out var fallback)) return fallback.ToString();
                throw new InvalidOperationException($"Missing routing input: {key}");
            });
        return new PolicyTaskRoute(Interpolate(rule.Provider), Interpolate(rule.Model));
    }

    private static IReadOnlyList<TaskRoutingRule> ReadRoutingRules(Engine engine, ObjectInstance definition)
    {
        if (!TryGetOptionalObject(definition, "tasks", "template.tasks", out var tasks) || tasks.Get("routing").IsUndefined()) return [];
        var json = JsValueToJsonElement(engine, tasks.Get("routing"), "null");
        if (json.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("tasks.routing must be an array");
        var rules = new List<TaskRoutingRule>();
        foreach (var rule in json.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || rule.EnumerateObject().Any(p => p.Name is not ("when" or "provider" or "model" or "enforce")) ||
                !rule.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.Object ||
                when.EnumerateObject().Any(p => p.Name is not ("role" or "access")))
                throw new InvalidOperationException("Invalid tasks.routing rule");
            string? Read(JsonElement obj, string name)
            {
                if (!obj.TryGetProperty(name, out var value)) return null;
                if (value.ValueKind != JsonValueKind.String || name != "role" && string.IsNullOrWhiteSpace(value.GetString()))
                    throw new InvalidOperationException("Invalid tasks.routing " + name);
                return value.GetString();
            }
            var role = Read(when, "role");
            var access = Read(when, "access");
            if (access is not (null or "read_only" or "read_write")) throw new InvalidOperationException("Invalid tasks.routing access");
            var enforce = false;
            if (rule.TryGetProperty("enforce", out var e))
            {
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException("Invalid tasks.routing enforce");
                enforce = e.GetBoolean();
            }
            rules.Add(new(new(role, access), Read(rule, "provider"), Read(rule, "model"), enforce));
        }
        return rules;
    }

    private const string DefinitionHelpers = """
        globalThis.__templateAction = (type, data) => ({ ...data, type });
        globalThis.__mergeTemplate = (base, child) => {
          const keyed = (a,b,key) => [...new Map([...(a||[]),...(b||[])].map(x => [key(x),x])).values()];
          const routeKey = x => JSON.stringify([x.when?.role ?? null,x.when?.access ?? null]);
          const childRoutes = child.tasks?.routing || [];
          const overriddenRoutes = new Set(childRoutes.map(routeKey));
          return { ...base, ...child, extends: undefined,
            inputs: { ...base.inputs, ...child.inputs },
            plugins: [...new Set([...(base.plugins||[]),...(child.plugins||[])])],
            schedules: keyed(base.schedules, child.schedules, x => x.id),
            report: { ...base.report, ...child.report, sections: keyed(base.report?.sections, child.report?.sections, x => x.h1) },
            tasks: { ...base.tasks, ...child.tasks,
              routing: [...childRoutes, ...(base.tasks?.routing || []).filter(x => !overriddenRoutes.has(routeKey(x)))] }
          };
        };
        globalThis.__beforeTemplate = (def, type, data, ctx) => def.before?.(__templateAction(type,data),ctx) ?? Allow();
        globalThis.__adaptTemplate = def => {
          if (!def || typeof def !== 'object' || Array.isArray(def)) throw new TypeError('Invalid template definition');
          for (const key of ['before','after','initialState','turnContext'])
            if (def[key] != null && typeof def[key] !== 'function') throw new TypeError('template.' + key + ' must be a function');
          for (const key of ['report','tasks'])
            if (def[key] != null && (typeof def[key] !== 'object' || Array.isArray(def[key]))) throw new TypeError('template.' + key + ' must be an object');
          return def;
        };
        """;
}
