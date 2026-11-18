using System.Text;
using System.Text.Json;

namespace Perga.TermClient;

/// <summary>
/// Strongly-typed helpers for constructing <see cref="TermActionRequest"/> JSON payloads and
/// encoding/decoding <c>/ws/{terminal_id}</c> binary WebSocket frames.
/// </summary>
public static class TermActionPayload
{
    public static JsonElement Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SerializeToElement(new { text });
    }

    public static JsonElement Submit(string text = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        return SerializeToElement(new { text });
    }

    public static JsonElement Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SerializeToElement(new { text });
    }

    public static JsonElement Keys(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return SerializeToElement(new { keys });
    }

    public static JsonElement Keys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return SerializeToElement(new { keys = keys.ToArray() });
    }

    public static JsonElement Key(string key, int repeat = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return SerializeToElement(new { key, repeat });
    }

    private static JsonElement SerializeToElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// Binary WebSocket framing helpers for <c>GET /ws/{terminal_id}</c>.
/// <list type="bullet">
/// <item><description><c>0x01 + raw_pty_bytes</c>: bidirectional raw terminal I/O</description></item>
/// <item><description><c>0x02 + utf8_json</c>: control frames (<c>resize</c> / <c>exit</c>)</description></item>
/// </list>
/// </summary>
public static class TerminalWebSocketFrames
{
    public const byte PtyDataPrefix = 0x01;
    public const byte ControlJsonPrefix = 0x02;

    public static byte[] EncodePtyData(ReadOnlySpan<byte> rawBytes)
    {
        var frame = new byte[1 + rawBytes.Length];
        frame[0] = PtyDataPrefix;
        rawBytes.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static byte[] EncodePtyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = Encoding.UTF8.GetByteCount(text);
        var frame = new byte[1 + count];
        frame[0] = PtyDataPrefix;
        Encoding.UTF8.GetBytes(text, frame.AsSpan(1));
        return frame;
    }

    public static byte[] EncodeResizeControl(ushort cols, ushort rows)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "resize",
            cols,
            rows
        });
        var frame = new byte[1 + jsonBytes.Length];
        frame[0] = ControlJsonPrefix;
        jsonBytes.CopyTo(frame.AsSpan(1));
        return frame;
    }

    public static JsonDocument ParseControlJson(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 2 || frame.Span[0] != ControlJsonPrefix)
        {
            throw new ArgumentException("Frame is not a 0x02 JSON control frame.", nameof(frame));
        }

        return JsonDocument.Parse(frame[1..]);
    }
}
