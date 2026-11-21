using System.Text.Json;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.Mcp;

public sealed class KernelMcpBackend : IMcpToolBackend
{
    private readonly IPergaKernel _kernel;

    public KernelMcpBackend(IPergaKernel kernel)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
    }

    public string ServerName => "perga";

    public async Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        var isLead = caller.BoundTaskId is null &&
            await _kernel.GetTrackLeadSessionIdAsync(caller.TrackId, ct).ConfigureAwait(false) == caller.SessionId;
        return KernelMcpCatalog.GetToolsForSession(caller.BoundTaskId, isLead)
            .Select(static t => new McpToolWireDescriptor(t.Name, t.Description, t.InputSchema, t.Annotations))
            .ToArray();
    }

    public async Task<McpCallToolResult> CallToolAsync(
        McpSessionPrincipal caller,
        string toolName,
        JsonElement arguments,
        CancellationToken ct)
    {
        var normalizedArgs = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? McpToolResultFormatter.EmptyObject
            : arguments;

        try
        {
            // InvokeKernelMcpToolAsync runs IPergaKernel.GuardMcpToolCallAsync("perga") first,
            // then enforces bound-task fences and executes the kernel tool!
            var result = await _kernel.InvokeKernelMcpToolAsync(caller, toolName, normalizedArgs, ct)
                .ConfigureAwait(false);
            var normalizedText = result.GetRawText()
                .Replace("\"access\":\"readOnly\"", "\"access\":\"read_only\"", StringComparison.Ordinal)
                .Replace("\"access\":\"readWrite\"", "\"access\":\"read_write\"", StringComparison.Ordinal);
            return McpToolResultFormatter.Success(normalizedText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (KernelException ex)
        {
            var status = ex.Code switch
            {
                KernelErrorCode.Forbidden or KernelErrorCode.PolicyDenied or KernelErrorCode.SystemUpgradeLeadRequired => 403,
                KernelErrorCode.NotFound => 404,
                KernelErrorCode.Gone or KernelErrorCode.IdempotencyKeyExhausted => 410,
                KernelErrorCode.PreconditionFailed => 412,
                KernelErrorCode.SessionRecoveryRequired => 409,
                KernelErrorCode.Conflict or KernelErrorCode.IdempotencyKeyReused or KernelErrorCode.WorkerTurnChanged => 409,
                _ => 400
            };
            return McpToolResultFormatter.Error(JsonSerializer.Serialize(new
            {
                code = JsonNamingPolicy.SnakeCaseLower.ConvertName(ex.Code.ToString()), status,
                error = ex.Message, nextStep = ex.NextStep
            }));
        }
        catch (Exception ex)
        {
            return McpToolResultFormatter.FromException(ex);
        }
    }
}
