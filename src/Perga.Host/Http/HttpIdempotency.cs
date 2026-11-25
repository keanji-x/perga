using Microsoft.AspNetCore.Http;
using Perga.Kernel;

namespace Perga.Host.Http;

internal static class HttpIdempotency
{
    internal static string? OptionalKey(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var values))
            return null;
        if (values.Count != 1)
            throw KernelException.InvalidArgument("Exactly one Idempotency-Key is allowed.");
        return values[0] ?? string.Empty;
    }
}
