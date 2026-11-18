using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Perga.Host.Http;

internal sealed class WorkerApprovalAuthorization
{
    internal const string Header = "X-Perga-Worker-Approval-Key";

    private readonly byte[]? _digest;

    public WorkerApprovalAuthorization(string? verifier)
    {
        if (verifier is { Length: 64 } && verifier.All(char.IsAsciiHexDigit))
            _digest = Convert.FromHexString(verifier);
    }

    public bool IsConfigured => _digest is not null;

    public bool Verify(HttpContext context)
    {
        if (!context.Request.IsHttps)
            return false;
        if (_digest is null || !context.Request.Headers.TryGetValue(Header, out var values) || values.Count != 1 ||
            values[0] is not { Length: 64 } key || !key.All(char.IsAsciiHexDigit))
            return false;

        return CryptographicOperations.FixedTimeEquals(_digest, SHA256.HashData(Encoding.ASCII.GetBytes(key)));
    }
}
