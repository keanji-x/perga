using System.Security.Cryptography;
using System.Text.Json;

namespace Perga.Kernel.Internal;

internal static class RequestIdentity
{
    public static string Hash<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
}
