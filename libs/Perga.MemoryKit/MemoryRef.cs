namespace Perga.MemoryKit;

public enum MemoryScopeKind
{
    Area,
    Global,
}

/// <summary>An address relative to the current read scope.</summary>
public readonly record struct MemoryRef
{
    public MemoryRef(MemoryScopeKind kind, string key)
    {
        if (kind is not (MemoryScopeKind.Area or MemoryScopeKind.Global))
            throw new MemoryValidationException("Invalid memory scope kind.");
        if (!IsValidKey(key))
            throw new MemoryValidationException("A memory key must match ^[a-z0-9][a-z0-9_-]{0,63}$.");

        Kind = kind;
        Key = key;
    }

    public MemoryScopeKind Kind { get; }
    public string Key { get; }

    public override string ToString() => Kind switch
    {
        MemoryScopeKind.Area when Key is not null => $"area/{Key}",
        MemoryScopeKind.Global when Key is not null => $"global/{Key}",
        _ => throw new InvalidOperationException("The memory reference is uninitialized."),
    };

    public static MemoryRef Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new FormatException("Expected a canonical area/<key> or global/<key> memory reference.");

    public static bool TryParse(string? value, out MemoryRef result)
    {
        result = default;
        if (value is null)
            return false;

        var slash = value.IndexOf('/');
        if (slash < 0)
            return false;

        MemoryScopeKind? kind = value.AsSpan(0, slash) switch
        {
            "area" => MemoryScopeKind.Area,
            "global" => MemoryScopeKind.Global,
            _ => null,
        };
        var key = value[(slash + 1)..];
        if (kind is null || !IsValidKey(key))
            return false;

        result = new MemoryRef(kind.Value, key);
        return true;
    }

    internal static bool IsValidKey(string? key) =>
        key is { Length: >= 1 and <= 64 }
        && (char.IsAsciiLetterLower(key[0]) || char.IsAsciiDigit(key[0]))
        && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');
}
