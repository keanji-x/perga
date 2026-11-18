using System.Text.Json;

namespace Perga.TermClient;

public enum TermWaitMode { None, Elapsed, Change, Text }
public enum TermActionKind { Text, Keys, Paste, Submit }

public sealed record TermEnsureRequest(
    string TerminalId,
    string Program,
    IReadOnlyList<string> Args,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    ushort Cols = 120,
    ushort Rows = 40);

public sealed record TermEnsureResult(string TerminalId, int Pid, bool Created);

public sealed record TerminalCursor(uint Column, uint Row, bool Visible);

public sealed record TerminalFrame(
    ushort Cols,
    ushort Rows,
    TerminalCursor Cursor,
    bool Alternate,
    int ScrollOffset,
    int HistoryRows,
    uint Modes,
    IReadOnlyList<string> Text);

public sealed record TermObserveRequest(
    string TerminalId,
    int ScrollOffset = 0,
    TermWaitMode WaitFor = TermWaitMode.None,
    int WaitMs = 0,
    int SettleMs = 150,
    IReadOnlyList<string>? WaitText = null,
    IReadOnlyList<string>? WaitTextAbsent = null);

public sealed record TermObserveResult(
    ulong Revision,
    TerminalFrame Frame,
    bool Exited,
    int? ExitCode);

public sealed record TermActionRequest(
    string TerminalId,
    TermActionKind Kind,
    JsonElement Payload);

public sealed record TermActionResult(ulong AckRevision);

public interface ITerminalBridgeClient : IAsyncDisposable
{
    Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct);
    Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct);
    Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct);
    Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct);
    Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default);
    Uri GetWebSocketRelayUri(string terminalId);
}
