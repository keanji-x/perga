using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Perga.AgentKit.Internal;

internal sealed class JsonRpcRemoteException : Exception
{
    public int Code { get; }
    public JsonElement? ErrorData { get; }

    public JsonRpcRemoteException(int code, string message, JsonElement? errorData = null)
        : base(message)
    {
        Code = code;
        ErrorData = errorData;
    }
}

internal sealed class JsonRpcStdioConnection : IAsyncDisposable
{
    private readonly StdioSubprocess _process;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private readonly Action<string, JsonElement> _onNotification;
    private readonly Action<JsonElement, string, JsonElement> _onServerRequest;
    private readonly Action<Exception?> _onDisconnected;
    private sealed record PendingRequest(TaskCompletionSource<JsonElement> Completion, Action<JsonElement>? ObserveResult);

    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private long _nextRequestId;
    private int _disposed;

    public JsonRpcStdioConnection(
        StdioSubprocess process,
        Action<string, JsonElement> onNotification,
        Action<JsonElement, string, JsonElement> onServerRequest,
        Action<Exception?> onDisconnected)
    {
        _process = process;
        _onNotification = onNotification;
        _onServerRequest = onServerRequest;
        _onDisconnected = onDisconnected;
        _pumpTask = Task.Run(PumpStdoutAsync);
    }

    public StdioSubprocess Process => _process;

    public static string NormalizeIdKey(JsonElement idElement) =>
        idElement.ValueKind switch
        {
            JsonValueKind.String => $"s:{idElement.GetString()}",
            JsonValueKind.Number => $"n:{idElement.GetRawText()}",
            _ => $"r:{idElement.GetRawText()}"
        };

    public static string ExtractDisplayRequestId(JsonElement idElement) =>
        idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            _ => idElement.GetRawText()
        };

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct,
        Action<JsonElement>? observeResult = null)
    {
        var numericId = Interlocked.Increment(ref _nextRequestId);
        var key = $"n:{numericId.ToString(CultureInfo.InvariantCulture)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(key, new PendingRequest(tcs, observeResult)))
        {
            throw new InvalidOperationException($"Duplicate JSON-RPC request id '{key}'.");
        }

        if (_process.HasExited)
        {
            _pendingRequests.TryRemove(key, out _);
            throw CreateDisconnectedException();
        }

        using var reg = ct.Register(() =>
        {
            if (_pendingRequests.TryRemove(key, out var removed))
            {
                removed.Completion.TrySetCanceled(ct);
            }
        });

        var envelope = new Dictionary<string, object?>(4)
        {
            ["jsonrpc"] = "2.0",
            ["id"] = numericId,
            ["method"] = method,
            ["params"] = parameters ?? new Dictionary<string, object?>()
        };

        var json = JsonSerializer.Serialize(envelope);
        try
        {
            await _process.WriteLineAsync(json, ct).ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(key, out _);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        var envelope = new Dictionary<string, object?>(3)
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters ?? new Dictionary<string, object?>()
        };
        var json = JsonSerializer.Serialize(envelope);
        return _process.WriteLineAsync(json, ct);
    }

    public Task SendResultResponseAsync(JsonElement rawId, object? result, CancellationToken ct)
    {
        var envelope = new Dictionary<string, object?>(3)
        {
            ["jsonrpc"] = "2.0",
            ["id"] = rawId,
            ["result"] = result ?? new Dictionary<string, object?>()
        };
        var json = JsonSerializer.Serialize(envelope);
        return _process.WriteLineAsync(json, ct);
    }

    public Task SendErrorResponseAsync(JsonElement rawId, int code, string message, CancellationToken ct)
    {
        var envelope = new Dictionary<string, object?>(3)
        {
            ["jsonrpc"] = "2.0",
            ["id"] = rawId,
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        var json = JsonSerializer.Serialize(envelope);
        return _process.WriteLineAsync(json, ct);
    }

    internal bool ShutdownConfirmed => _process.ShutdownConfirmed;

    public async ValueTask DisposeAsync()
    {
        await _shutdownLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var firstAttempt = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (firstAttempt)
                await _pumpCts.CancelAsync().ConfigureAwait(false);
            try { await _process.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (firstAttempt)
                {
                    try { await _pumpTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                    catch { /* Pump failure cannot prevent shutdown. */ }
                    FailAllPending(new ObjectDisposedException(nameof(JsonRpcStdioConnection)));
                    _pumpCts.Dispose();
                }
            }
        }
        finally { _shutdownLock.Release(); }
    }

    private async Task PumpStdoutAsync()
    {
        Exception? disconnectError = null;
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                var line = await _process.ReadStdoutLineAsync(_pumpCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.IsEmpty || trimmed[0] != '{')
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    DispatchFrame(doc.RootElement);
                }
            }
        }
        catch (OperationCanceledException) when (_pumpCts.IsCancellationRequested)
        {
            // Normal disposal.
        }
        catch (Exception ex)
        {
            disconnectError = ex;
        }

        var failEx = disconnectError ?? CreateDisconnectedException();
        FailAllPending(failEx);
        if (Volatile.Read(ref _disposed) == 0)
        {
            _onDisconnected(failEx);
        }
    }

    private void DispatchFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = root.TryGetProperty("id", out var idProp) &&
                    idProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        var hasMethod = root.TryGetProperty("method", out var methodProp) &&
                        methodProp.ValueKind == JsonValueKind.String;

        if (hasId && !hasMethod)
        {
            var key = NormalizeIdKey(idProp);
            if (!_pendingRequests.TryRemove(key, out var pending))
            {
                return;
            }

            if (root.TryGetProperty("error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.Object)
            {
                var code = errProp.TryGetProperty("code", out var cProp) && cProp.TryGetInt32(out var parsedCode)
                    ? parsedCode
                    : -32000;
                var msg = errProp.TryGetProperty("message", out var mProp) && mProp.ValueKind == JsonValueKind.String
                    ? mProp.GetString() ?? "JSON-RPC error"
                    : errProp.GetRawText();
                JsonElement? data = errProp.TryGetProperty("data", out var dProp) ? dProp.Clone() : null;
                pending.Completion.TrySetException(new JsonRpcRemoteException(code, msg, data));
                return;
            }

            if (root.TryGetProperty("result", out var resProp))
            {
                try
                {
                    var result = resProp.Clone();
                    // Bind request identity before reading the following provider notification.
                    pending.ObserveResult?.Invoke(result);
                    pending.Completion.TrySetResult(result);
                }
                catch (Exception error)
                {
                    pending.Completion.TrySetException(error);
                }
            }
            else
            {
                pending.Completion.TrySetException(new InvalidOperationException("A JSON-RPC response must contain a result or error."));
            }

            return;
        }

        if (hasId && hasMethod)
        {
            var method = methodProp.GetString() ?? string.Empty;
            var clonedId = idProp.Clone();
            var clonedParams = root.TryGetProperty("params", out var pProp)
                ? pProp.Clone()
                : CreateEmptyJsonObject();
            _onServerRequest(clonedId, method, clonedParams);
            return;
        }

        if (!hasId && hasMethod)
        {
            var method = methodProp.GetString() ?? string.Empty;
            var clonedParams = root.TryGetProperty("params", out var pProp)
                ? pProp.Clone()
                : CreateEmptyJsonObject();
            _onNotification(method, clonedParams);
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var pending))
            {
                pending.Completion.TrySetException(ex);
            }
        }
    }

    private InvalidOperationException CreateDisconnectedException()
    {
        var exitCode = _process.TryGetExitCode();
        var stderr = _process.GetStderrSummary();
        var suffix = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $" Stderr: {stderr}";
        return new InvalidOperationException(
            $"JSON-RPC stdio stream closed unexpectedly (exitCode={exitCode?.ToString(CultureInfo.InvariantCulture) ?? "running"}).{suffix}");
    }

    internal static JsonElement CreateEmptyJsonObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
