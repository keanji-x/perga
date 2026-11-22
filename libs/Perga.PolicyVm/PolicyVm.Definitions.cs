using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    public static string WithToolNameVersion(string source, bool legacy) =>
        ReadToolNameVersion(source) is not null
            ? source
            : JsonSerializer.Serialize(new { format = TemplateBundle.VersionedSnapshotFormat, toolNameVersion = legacy ? 1 : 2, source });

    public static (string Source, bool Legacy)? ReadToolNameVersion(string source)
    {
        if (!source.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
            return null;
        using var document = JsonDocument.Parse(source);
        return ReadToolNameVersion(document.RootElement);
    }

    private static (string Source, bool Legacy)? ReadToolNameVersion(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
            format.GetString() != TemplateBundle.VersionedSnapshotFormat)
            return null;
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(source.GetString()) || TemplateBundle.ReadSnapshotToolNameVersion(root) is not { } legacy)
            throw new InvalidOperationException("Invalid template snapshot tool-name version or source.");
        return (source.GetString()!, legacy);
    }

    // Snapshots contain source modules rather than serialized functions. Every base is frozen.
    public static string Inherit(CompiledPolicy parent, string childSource) =>
        System.Text.Json.JsonSerializer.Serialize(new { format = TemplateBundle.InheritedSnapshotFormat, sources = new[] { parent.JsSource, childSource } });

    private readonly record struct LoadedDefinition(JsValue Definition, bool LegacyBefore, bool LegacyAfter);

    private static LoadedDefinition LoadDefinition(Engine engine, TemplateBundle bundle)
    {
        engine.Execute(DefinitionHelpers);
        var index = 0;
        var snapBundleIndex = 0;
        var loaded = Load(bundle, legacyToolNames: false);
        return loaded with { Definition = engine.Invoke("__adaptTemplate", loaded.Definition) };

        LoadedDefinition Load(TemplateBundle source, bool legacyToolNames)
        {
            if (source.Snapshot is TemplateBundle.SnapshotDefinition.Versioned versioned)
            {
                return Load(versioned.Source, versioned.Legacy ??
                    throw new InvalidOperationException("Invalid template snapshot tool-name version or source."));
            }

            if (source.Snapshot is TemplateBundle.SnapshotDefinition.Inherited inherited)
            {
                var parent = Load(inherited.Parent, legacyToolNames);
                var child = Load(inherited.Child, legacyToolNames);
                var merged = engine.Invoke("__mergeTemplate", parent.Definition, child.Definition);
                return new(merged.Get("definition"),
                    merged.Get("beforeFromChild").AsBoolean() ? child.LegacyBefore : parent.LegacyBefore,
                    merged.Get("afterFromChild").AsBoolean() ? child.LegacyAfter : parent.LegacyAfter);
            }

            string entry;
            if (source.IsMultiFile)
            {
                entry = bundle.IsMultiFile ? source.EntryPath : $"__snap_{snapBundleIndex++}/{source.EntryPath}";
            }
            else
            {
                var text = source.EntrySource;
                if (text.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.TryGetProperty("format", out var format))
                        _ = format.GetString();
                    text = "export default " + text + ";";
                }
                entry = $"template-{index++}.perga.js";
                engine.Modules.Add(entry, text);
            }

            var definition = engine.Modules.Import(entry).Get("default");
            // Validate each source before merging so a child cannot hide an invalid base.
            RejectLegacyHooks(definition);
            if (definition is ObjectInstance sourceDefinition)
            {
                if (sourceDefinition.HasProperty("toolNameVersion"))
                {
                    var version = sourceDefinition.Get("toolNameVersion");
                    if (!version.IsNumber() || version.AsNumber() is not (1 or 2))
                        throw new InvalidOperationException("template.toolNameVersion must be the integer 1 or 2.");
                    legacyToolNames = version.AsNumber() == 1;
                }
                ReadFlow(engine, sourceDefinition);
            }
            return new(definition, legacyToolNames, legacyToolNames);
        }
    }

    private static void RejectLegacyHooks(JsValue definition)
    {
        if (definition is not ObjectInstance obj) return;

        Reject(obj, "initialMarkdown", "initialMarkdown");
        if (!obj.Get("headerView").IsUndefined() && obj.Get("headerView") is not Function)
            throw new InvalidOperationException("template.headerView must be a function.");
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
        JsonElement data, JsonElement inputs, JsonElement state) => EvaluateWithEffectiveDefinition(policy, inputs, "Before", (engine, _, def, _, _) =>
    {
        var ctx = BuildTemplateContextObject(engine, def, trackId, inputs, state);
        var payload = ParseJsJson(engine, data.GetRawText());
        var value = engine.Invoke("__beforeTemplate", def, action, payload, ctx);
        var decision = ParseGuardDecision(engine, value, "before");
        return new PolicyEvalResult<GuardDecision>(ExtractContextState(engine, ctx), decision);
    });

    public PolicyEvalResult<string?> After(CompiledPolicy policy, string eventName, string trackId,
        JsonElement data, JsonElement inputs, JsonElement state) => EvaluateWithEffectiveDefinition(policy, inputs, "After", (engine, _, def, _, _) =>
    {
        var ctx = BuildTemplateContextObject(engine, def, trackId, inputs, state);
        var payload = ParseJsJson(engine, data.GetRawText());
        var result = def.Get("after") is Function hook
            ? engine.Call(hook, def, [engine.Invoke("__templateAction", eventName, payload), ctx]) : JsValue.Undefined;
        return new PolicyEvalResult<string?>(ExtractContextState(engine, ctx), ExtractOptionalStringResult(result, "after"));
    });

    public PolicyTaskRoute RouteTaskDispatch(CompiledPolicy policy, string? role, string access, string? provider, string? model, JsonElement inputs)
    {
        var rules = policy.JsSource.Contains("setup", StringComparison.Ordinal)
            ? ResolveRoutingRules(policy, inputs)
            : policy.Manifest.Routing;
        var selectedProvider = provider;
        var selectedModel = model;
        string? selectedAccess = null;
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.When.Role is { } r && r != role || rule.When.Access is { } a && a != access) continue;
            var target = ResolveRoutingTarget(policy, rule, inputs);
            if (rule.Enforce && rule.TargetAccess is { } ta && access != ta)
                return new(selectedProvider, selectedModel, $"Task routing requires access {ta}");
            if (rule.Enforce && provider is not null && target.Provider is not null && provider != target.Provider)
                return new(selectedProvider, selectedModel, $"Task routing requires provider {target.Provider}");
            if (rule.Enforce && model is not null && target.Model is not null && model != target.Model)
                return new(selectedProvider, selectedModel, $"Task routing requires model {target.Model}");
            selectedProvider ??= target.Provider;
            selectedModel ??= target.Model;
            selectedAccess ??= rule.TargetAccess;
        }
        return new(selectedProvider, selectedModel, null, selectedAccess);
    }

    public IReadOnlyList<PolicyTaskRoute> ResolveRoutingTargets(CompiledPolicy policy, JsonElement inputs)
    {
        var rules = policy.JsSource.Contains("setup", StringComparison.Ordinal)
            ? ResolveRoutingRules(policy, inputs)
            : policy.Manifest.Routing;
        return rules.Select(rule => ResolveRoutingTarget(policy, rule, inputs)).ToArray();
    }

    private PolicyTaskRoute ResolveRoutingTarget(CompiledPolicy policy, TaskRoutingRule rule, JsonElement inputs)
    {
        return new PolicyTaskRoute(rule.Provider is null ? null : InterpolateInputs(policy, rule.Provider, inputs),
            rule.Model is null ? null : InterpolateInputs(policy, rule.Model, inputs), null, rule.TargetAccess);
    }

    private static IReadOnlyList<TaskRoutingRule> ReadRoutingRules(Engine engine, ObjectInstance definition)
    {
        if (!TryGetOptionalObject(definition, "tasks", "template.tasks", out var tasks) || tasks.Get("routing").IsUndefined()) return [];
        var json = JsValueToJsonElement(engine, tasks.Get("routing"), "null");
        if (json.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("tasks.routing must be an array");
        var rules = new List<TaskRoutingRule>();
        foreach (var rule in json.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || rule.EnumerateObject().Any(p => p.Name is not ("when" or "provider" or "model" or "access" or "enforce")) ||
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
            var targetAccess = Read(rule, "access");
            if (targetAccess is not (null or "read_only" or "read_write")) throw new InvalidOperationException("Invalid tasks.routing access");
            var enforce = false;
            if (rule.TryGetProperty("enforce", out var e))
            {
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException("Invalid tasks.routing enforce");
                enforce = e.GetBoolean();
            }
            rules.Add(new(new(role, access), Read(rule, "provider"), Read(rule, "model"), enforce, targetAccess));
        }
        return rules;
    }

    private const string DefinitionHelpers = """
        globalThis.__templateAction = (type, data) => ({ ...data, type });
        globalThis.__mergeTemplateStatic = (base, child) => {
          const keyed = (a,b,key) => [...new Map([...(a||[]),...(b||[])].map(x => [key(x),x])).values()];
          const routeKey = x => JSON.stringify([x.when?.role ?? null,x.when?.access ?? null]);
          const childRoutes = child.tasks?.routing || [];
          const overriddenRoutes = new Set(childRoutes.map(routeKey));
          const baseFields = { ...base };
          const childFields = { ...child };
          const mergeFlow = (inherited, own) => {
            for (const flow of [inherited, own]) {
              if (flow === undefined || flow === null) continue;
              if (!Array.isArray(flow)) throw new TypeError('template.flow must be an array of Flow nodes.');
              const ids = new Set();
              for (const node of flow) {
                if (!node || typeof node.id !== 'string' || ids.has(node.id))
                  throw new TypeError('template.flow requires unique node IDs.');
                ids.add(node.id);
              }
            }
            return keyed(inherited, own, x => x.id);
          };
          const merged = { ...baseFields, ...childFields, extends: undefined,
            inputs: { ...base.inputs, ...child.inputs },
            plugins: [...new Set([...(base.plugins||[]),...(child.plugins||[])])],
            flow: mergeFlow(base.flow, child.flow),
            report: { ...base.report, ...child.report, sections: keyed(base.report?.sections, child.report?.sections, x => x.h1) },
            tasks: { ...base.tasks, ...child.tasks,
              routing: [...childRoutes, ...(base.tasks?.routing || []).filter(x => !overriddenRoutes.has(routeKey(x)))] }
          };
          if (base.data || child.data) {
            merged.data = { ...base.data, ...child.data };
          }
          return merged;
        };
        globalThis.__mergeTemplate = (base, child) => {
          const baseSetup = base.setup;
          const childSetup = child.setup;
          const childFields = { ...child };
          const definition = __mergeTemplateStatic(base, child);
          if (typeof baseSetup === 'function' || typeof childSetup === 'function') {
            definition.setup = (inputs) => {
              const baseResolved = typeof baseSetup === 'function' ? baseSetup.call(base, inputs) : undefined;
              if (baseResolved !== undefined && (!baseResolved || typeof baseResolved !== 'object' || Array.isArray(baseResolved))) {
                throw new TypeError('template.setup(inputs) must return a configuration object.');
              }
              const childResolved = typeof childSetup === 'function' ? childSetup.call(child, inputs) : undefined;
              if (childResolved !== undefined && (!childResolved || typeof childResolved !== 'object' || Array.isArray(childResolved))) {
                throw new TypeError('template.setup(inputs) must return a configuration object.');
              }
              const effBase = baseResolved ? __mergeTemplateStatic(base, baseResolved) : base;
              const effChild = childResolved ? __mergeTemplateStatic(child, childResolved) : child;
              const combined = __mergeTemplateStatic(effBase, effChild);
              delete combined.setup;
              return combined;
            };
          }
          return { definition,
            beforeFromChild: Object.hasOwn(childFields, "before"),
            afterFromChild: Object.hasOwn(childFields, "after")
          };
        };
        globalThis.__beforeTemplate = (def, type, data, ctx) => def.before?.(__templateAction(type,data),ctx) ?? Allow();
        globalThis.__adaptTemplate = def => {
          if (!def || typeof def !== 'object' || Array.isArray(def)) throw new TypeError('Invalid template definition');
          for (const key of ['before','after','initialState','turnContext','headerView'])
            if (def[key] != null && typeof def[key] !== 'function') throw new TypeError('template.' + key + ' must be a function');
          if (def.headerView !== undefined && typeof def.headerView !== 'function') throw new TypeError('template.headerView must be a function');
          for (const key of ['report','tasks'])
            if (def[key] != null && (typeof def[key] !== 'object' || Array.isArray(def[key]))) throw new TypeError('template.' + key + ' must be an object');
          return def;
        };
        """;
}
