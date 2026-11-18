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

    public Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(McpSessionPrincipal caller, CancellationToken ct)
    {
        _ = ct;
        var tools = KernelMcpCatalog.GetToolsForSession(caller.BoundAttemptId)
            .Select(static t => new McpToolWireDescriptor(t.Name, t.Description, t.InputSchema))
            .ToArray();
        return Task.FromResult<IReadOnlyList<McpToolWireDescriptor>>(tools);
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
            // then enforces BoundAttemptId fences and executes the kernel tool!
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
        catch (Exception ex)
        {
            return McpToolResultFormatter.FromException(ex);
        }
    }
}
