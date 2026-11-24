using System.Text.Json;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.MemoryKit;

namespace Perga.Host.Memory;

public sealed class MemoryMcpBackend(
    IPergaKernel kernel, ITrackReadStore tracks, IMemoryTrackStore memoryTracks, IMemoryEngine engine, TimeProvider time)
    : IMcpToolBackend
{
    private const string List = "memory_list";
    private const string Read = "memory_read";
    private const string Recall = "memory_recall";
    private const string Put = "memory_put";
    private const string Delete = "memory_delete";
    private const string Propose = "memory_propose";
    private const int MaxBytes = 64 * 1024;

    private static readonly IReadOnlyList<McpToolWireDescriptor> Descriptors =
    [
        Describe(List, "List memory in the current Area and Global. Optionally select one scope.", """
            "properties":{"scope":{"enum":["area","global"]}}
            """, readOnly: true),
        Describe(Read, "Read Markdown and ETags for canonical area/<key> or global/<key> refs.", """
            "properties":{"refs":{"type":"array","items":{"type":"string"},"maxItems":64},"maxBytes":{"type":"integer","minimum":1,"maximum":65536}},
            "required":["refs"]
            """, readOnly: true),
        Describe(Recall, "Search memory by keywords. The current Area takes precedence over Global.", """
            "properties":{"query":{"type":"string","maxLength":4096},"maxItems":{"type":"integer","minimum":1,"maximum":64},"maxBytes":{"type":"integer","minimum":1,"maximum":65536}},
            "required":["query"]
            """, readOnly: true),
        Describe(Put, "Create or replace one Markdown entry in your bound scope. Provide the current ETag when replacing an entry.", """
            "properties":{"scope":{"enum":["area","global"]},"key":{"type":"string"},"title":{"type":"string"},"summary":{"type":"string"},
            "body":{"type":"string"},"pinned":{"type":"boolean"},"tags":{"type":"array","items":{"type":"string"}},
            "sources":{"type":"array","items":{"type":"string"}},"expectedEtag":{"type":"string"}},
            "required":["key","title","summary","body"]
            """, destructive: true),
        Describe(Delete, "Delete one entry in your bound scope. Provide the current ETag for an existing entry.", """
            "properties":{"scope":{"enum":["area","global"]},"key":{"type":"string"},"expectedEtag":{"type":"string"}},
            "required":["key"]
            """, destructive: true),
        Describe(Propose, "Send a durable proposal to the Memory Planner for your current Area or Global.", """
            "properties":{"scope":{"enum":["area","global"]},"summary":{"type":"string"},"details":{"type":"string"},"suggestedPinned":{"type":"boolean"}},
            "required":["scope","summary","details"]
            """)
    ];

    public string ServerName => MemoryTrackBinding.McpServerName;

    public async Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        var access = await ResolveAccessAsync(caller, ct).ConfigureAwait(false);
        return Descriptors.Where(tool => access.Allows(tool.Name)).ToArray();
    }

    public async Task<McpCallToolResult> CallToolAsync(McpSessionPrincipal caller, string toolName,
        JsonElement arguments, CancellationToken ct)
    {
        try
        {
            var descriptor = Descriptors.FirstOrDefault(tool => tool.Name == toolName)
                ?? throw KernelException.NotFound($"Unknown memory tool '{toolName}'.");
            var access = await ResolveAccessAsync(caller, ct).ConfigureAwait(false);
            if (!access.Allows(toolName))
                throw KernelException.Forbidden($"This session cannot use '{toolName}'.");

            var args = arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? McpToolResultFormatter.EmptyObject : arguments;
            CheckArguments(descriptor.InputSchema, args);
            var readOnly = descriptor.Annotations?.ReadOnlyHint == true;
            var generation = await kernel.GuardMcpToolCallAsync(caller, ServerName, toolName, args, ct, write: !readOnly)
                .ConfigureAwait(false);
            var readScope = new MemoryReadScope(access.AreaId);

            switch (toolName)
            {
                case List:
                {
                    var kind = OptionalString(args, "scope");
                    var entries = await engine.ListAsync(kind == "global" ? new() : readScope, ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(entries
                        .Where(entry => kind != "area" || entry.Ref.Kind == MemoryScopeKind.Area).Select(MemoryEntries.ToDto).ToArray());
                }
                case Read:
                {
                    var refs = Strings(args, "refs")!.Select(ParseRef).ToArray();
                    var result = await engine.ReadAsync(readScope, refs, Limit(args, "maxBytes", 32 * 1024, MaxBytes), ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new
                    {
                        entries = result.Entries.Select(MemoryEntries.ToDto).ToArray(),
                        notFound = result.NotFound.Select(reference => reference.ToString()).ToArray(),
                        result.OmittedCount
                    });
                }
                case Recall:
                {
                    var query = RequiredString(args, "query");
                    if (query.Length > 4096) throw KernelException.InvalidArgument("query must be at most 4096 characters.");
                    var result = await engine.RecallAsync(readScope, query,
                        Limit(args, "maxItems", 12, 64), Limit(args, "maxBytes", 16 * 1024, MaxBytes), ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new
                    {
                        hits = result.Hits.Select(hit => new { entry = hit.Entry.ToDto(), hit.Score }).ToArray(),
                        result.OmittedCount
                    });
                }
                case Put:
                case Delete:
                    return await kernel.Actors.GetOrActivate(caller.TrackId).ExecuteMemoryMutationAsync(caller, generation,
                        token => MutateAsync(toolName, access.Binding!, args, token), ct).ConfigureAwait(false);

                case Propose:
                {
                    var proposal = await kernel.DeliverMemoryProposalAsync(caller, generation, RequiredString(args, "scope"),
                        RequiredString(args, "summary"), RequiredString(args, "details"), Flag(args, "suggestedPinned"), ct).ConfigureAwait(false);
                    return McpToolResultFormatter.Success(new { mailId = proposal.Id, trackId = proposal.ToTrackId });
                }
                default:
                    throw KernelException.NotFound($"Unknown memory tool '{toolName}'.");
            }
        }
        catch (MemoryValidationException ex)
        {
            return Error(new { code = "memory_validation", error = ex.Message, ex.PinnedBudget });
        }
        catch (MemoryConflictException ex)
        {
            return Error(new { code = "memory_conflict", error = ex.Message, @ref = ex.Ref.ToString(), ex.CurrentEtag });
        }
        catch (KernelException ex)
        {
            return McpToolResultFormatter.FromException(ex);
        }
    }

    private async Task<McpCallToolResult> MutateAsync(string toolName, MemoryTrackBinding binding, JsonElement args, CancellationToken ct)
    {
        var kind = binding.AreaId is null ? "global" : "area";
        if (OptionalString(args, "scope") is { } requested && requested != kind)
            throw KernelException.Forbidden("This Memory Track may write only to its bound scope.");
        var scope = new MemoryWriteScope(kind == "global" ? MemoryScopeKind.Global : MemoryScopeKind.Area, binding.AreaId);
        var key = RequiredString(args, "key");
        var etag = OptionalString(args, "expectedEtag");
        if (toolName == Put)
        {
            var request = new MemoryPutRequest(key, RequiredString(args, "title"), RequiredString(args, "summary"),
                RequiredString(args, "body"), Flag(args, "pinned"), Strings(args, "tags"), Strings(args, "sources"), etag);
            var result = await engine.PutAsync(scope, request, ct).ConfigureAwait(false);
            Publish("put");
            return McpToolResultFormatter.Success(result.Meta.ToDto());
        }

        var deleted = await engine.DeleteAsync(scope, key, etag, ct).ConfigureAwait(false);
        Publish("delete");
        return McpToolResultFormatter.Success(deleted);

        void Publish(string action) => kernel.EventBus.PublishTransient(binding.AreaId, binding.TrackId, KernelEventKinds.MemoryUpdated,
            JsonSerializer.SerializeToElement(new { scope = kind, binding.AreaId, binding.TrackId, key, action }, PergaJsonDefaults.ApiOptions),
            time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task<Access> ResolveAccessAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        var track = await tracks.GetTrackAsync(caller.TrackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{caller.TrackId}' not found.");
        var binding = await memoryTracks.FindMemoryBindingByTrackIdAsync(track.Id, ct).ConfigureAwait(false);
        var areaId = binding is not null ? binding.AreaId :
            await memoryTracks.IsUserAreaAsync(track.AreaId, ct).ConfigureAwait(false) ? track.AreaId : null;
        return new(binding, areaId, caller.IsLead && caller.BoundTaskId is null && track.LeadSessionId == caller.SessionId);
    }

    private sealed record Access(MemoryTrackBinding? Binding, string? AreaId, bool IsLead)
    {
        public bool Allows(string tool) => tool switch
        {
            Put or Delete => IsLead && Binding is not null,
            Propose => IsLead && Binding is null,
            _ => true
        };
    }

    private static McpToolWireDescriptor Describe(string name, string description, string fields,
        bool readOnly = false, bool destructive = false) =>
        new(name, description, McpToolResultFormatter.ParseClone($$"""{"type":"object","additionalProperties":false,{{fields}}}"""),
            new McpToolAnnotations(ReadOnlyHint: readOnly, DestructiveHint: destructive));

    private static void CheckArguments(JsonElement schema, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Memory tool arguments must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var properties = schema.GetProperty("properties");
        foreach (var field in args.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !properties.TryGetProperty(field.Name, out var definition))
                throw KernelException.InvalidArgument($"Unexpected or duplicate memory argument '{field.Name}'.");
            if (definition.TryGetProperty("enum", out var values))
            {
                if (field.Value.ValueKind != JsonValueKind.String || !values.EnumerateArray().Any(value => value.ValueEquals(field.Value.GetString())))
                    throw KernelException.InvalidArgument($"Invalid value for '{field.Name}'.");
                continue;
            }

            var valid = definition.GetProperty("type").GetString() switch
            {
                "string" => field.Value.ValueKind == JsonValueKind.String,
                "boolean" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt32(out _),
                "array" => field.Value.ValueKind == JsonValueKind.Array &&
                    field.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                _ => false
            };
            if (!valid) throw KernelException.InvalidArgument($"Invalid value for '{field.Name}'.");
        }
        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
                if (!seen.Contains(field.GetString()!))
                    throw KernelException.InvalidArgument($"Missing required argument '{field.GetString()}'.");
        if (args.TryGetProperty("refs", out var refs) && refs.GetArrayLength() > 64)
            throw KernelException.InvalidArgument("At most 64 refs may be read at once.");
    }

    private static string? OptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static string RequiredString(JsonElement args, string name) =>
        OptionalString(args, name) ?? throw KernelException.InvalidArgument($"Missing required argument '{name}'.");

    private static bool Flag(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.GetBoolean();

    private static string[]? Strings(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : null;

    private static int Limit(JsonElement args, string name, int fallback, int max)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        var count = value.GetInt32();
        if (count < 1 || count > max)
            throw KernelException.InvalidArgument($"{name} must be between 1 and {max}.");
        return count;
    }

    private static MemoryRef ParseRef(string value) =>
        MemoryRef.TryParse(value, out var reference) ? reference
            : throw KernelException.InvalidArgument($"'{value}' is not a canonical memory ref.");

    private static McpCallToolResult Error<T>(T payload) =>
        McpToolResultFormatter.Error(JsonSerializer.Serialize(payload, PergaJsonDefaults.ApiOptions));
}
