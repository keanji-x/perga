using System.Security.Cryptography;
using System.Text;

namespace Perga.MemoryKit.Tests;

internal sealed class MemoryTestDirectory : IDisposable
{
    internal static readonly MemoryReadScope Mixed = new("work");
    internal static readonly MemoryReadScope GlobalOnly = new();
    internal static readonly MemoryWriteScope Area = new(MemoryScopeKind.Area, "work");
    internal static readonly MemoryWriteScope Global = new(MemoryScopeKind.Global);
    internal static readonly CancellationToken Ct = CancellationToken.None;

    internal MemoryTestDirectory(MemoryEngineOptions? options = null)
    {
        Sandbox = Directory.CreateTempSubdirectory("perga-memory-").FullName;
        Root = Path.Combine(Sandbox, "memory");
        Engine = new MemoryEngine(Root, options);
    }

    internal string Sandbox { get; }
    internal string Root { get; }
    internal string AreaPath => Path.Combine(Root, "areas", "work");
    internal string GlobalPath => Path.Combine(Root, "global");
    internal MemoryEngine Engine { get; }

    internal static MemoryPutRequest Request(string key, string body = "Body.") => new(key, "Title", "Summary", body);

    internal static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal string WriteHandwritten(MemoryWriteScope scope, string name, string content)
    {
        var directory = scope.Kind == MemoryScopeKind.Global ? GlobalPath : Path.Combine(Root, "areas", scope.AreaId!);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(Sandbox, recursive: true);
}
