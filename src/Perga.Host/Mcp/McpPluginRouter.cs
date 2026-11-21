using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Perga.Host.Configuration;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Processes;

namespace Perga.Host.Mcp;

public sealed record McpPluginDefinition(
    string Id,
    string Transport, // "http" | "stdio"
    string? Url = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Cwd = null);

public sealed class McpPluginRouter : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IPergaKernel _kernel;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly ConcurrentDictionary<string, McpPluginDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IUpstreamMcpClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _clientInitLock = new(1, 1);
    private int _disposed;

    public McpPluginRouter(
        IPergaKernel kernel,
        PergaHostOptions options,
        HttpClient? httpClient = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = false
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            _disposeHttpClient = true;
        }

        if (!string.IsNullOrWhiteSpace(options.McpPluginsConfigPath) &&
            File.Exists(options.McpPluginsConfigPath))
        {
            LoadFromFile(options.McpPluginsConfigPath);
        }
    }

    public IReadOnlyCollection<string> RegisteredPluginIds => _definitions.Keys.ToArray();

    public void RegisterPlugin(McpPluginDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        _definitions[definition.Id] = definition;
    }

    public int LoadFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath, Encoding.UTF8);
        return LoadFromJson(json);
    }

    public int LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        if (McpToolResultFormatter.TryGetPropertyIgnoreCase(root, ["plugins", "mcpServers"], out var container))
        {
            if (container.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in container.EnumerateArray())
                {
                    if (TryParsePluginElement(null, item, out var def))
                    {
                        RegisterPlugin(def);
                        count++;
                    }
                }

                return count;
            }

            if (container.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in container.EnumerateObject())
                {
                    if (TryParsePluginElement(prop.Name, prop.Value, out var def))
                    {
                        RegisterPlugin(def);
                        count++;
                    }
                }

                return count;
            }
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                TryParsePluginElement(prop.Name, prop.Value, out var def))
            {
                RegisterPlugin(def);
                count++;
            }
        }

        return count;
    }

    public bool IsPluginRegistered(string pluginId) =>
        !string.IsNullOrWhiteSpace(pluginId) && _definitions.ContainsKey(pluginId);

    public async Task VerifyTrackAccessAsync(McpSessionPrincipal caller, string pluginId, CancellationToken ct)
    {
        if (!IsPluginRegistered(pluginId))
        {
            throw KernelException.NotFound($"MCP plugin '{pluginId}' is not registered.");
        }

        if (caller.EnabledPlugins.Contains(pluginId, StringComparer.Ordinal))
        {
            return;
        }

        // Also check live Track state in case EnabledPlugins was patched after token registration.
        var snap = await _kernel.GetTrackAggregateAsync(caller.TrackId, ct).ConfigureAwait(false);
        if (!snap.Track.EnabledPlugins.Contains(pluginId, StringComparer.Ordinal))
        {
            throw KernelException.Forbidden($"MCP plugin '{pluginId}' is not enabled on track '{caller.TrackId}'.");
        }
    }

    public IMcpToolBackend? TryCreateBackend(string pluginId)
    {
        if (!IsPluginRegistered(pluginId))
        {
            return null;
        }

        return new SinglePluginBackend(this, pluginId);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _clients.ToArray())
        {
            _clients.TryRemove(kvp.Key, out _);
            await kvp.Value.DisposeAsync().ConfigureAwait(false);
        }

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        _clientInitLock.Dispose();
    }

    private async Task<IUpstreamMcpClient> GetOrOpenClientAsync(string pluginId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_definitions.TryGetValue(pluginId, out var def))
        {
            throw KernelException.NotFound($"MCP plugin '{pluginId}' is not registered.");
        }

        if (_clients.TryGetValue(pluginId, out var existing) && existing.IsHealthy)
        {
            return existing;
        }

        await _clientInitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(pluginId, out existing))
            {
                if (existing.IsHealthy)
                {
                    return existing;
                }

                _clients.TryRemove(pluginId, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            IUpstreamMcpClient created;
            if (string.Equals(def.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(def.Url))
                {
                    throw KernelException.InvalidArgument($"HTTP MCP plugin '{pluginId}' is missing 'url'.");
                }

                var httpUpstream = new HttpMcpPluginClient(pluginId, new Uri(def.Url), def.Headers, _httpClient);
                await httpUpstream.EnsureInitializedAsync(ct).ConfigureAwait(false);
                created = httpUpstream;
            }
            else if (string.Equals(def.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(def.Command))
                {
                    throw KernelException.InvalidArgument($"Stdio MCP plugin '{pluginId}' is missing 'command'.");
                }

                var stdioUpstream = await StdioMcpPluginClient.StartAsync(def, Utf8NoBom, ct).ConfigureAwait(false);
                created = stdioUpstream;
            }
            else
            {
                throw KernelException.InvalidArgument(
                    $"Unsupported transport '{def.Transport}' on MCP plugin '{pluginId}'. Expected 'http' or 'stdio'.");
            }

            _clients[pluginId] = created;
            return created;
        }
        finally
        {
            _clientInitLock.Release();
        }
    }

    private static bool TryParsePluginElement(string? fallbackId, JsonElement elem, out McpPluginDefinition def)
    {
        def = default!;
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = McpToolResultFormatter.OptionalString(elem, ["id", "name"]) ?? fallbackId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var url = McpToolResultFormatter.OptionalString(elem, ["url", "endpoint"]);
        var command = McpToolResultFormatter.OptionalString(elem, ["command", "cmd"]);
        var transport = McpToolResultFormatter.OptionalString(elem, ["transport", "type"])
                        ?? (!string.IsNullOrWhiteSpace(url) ? "http" : !string.IsNullOrWhiteSpace(command) ? "stdio" : null);

        if (string.IsNullOrWhiteSpace(transport))
        {
            return false;
        }

        Dictionary<string, string>? headers = null;
        if (McpToolResultFormatter.TryGetPropertyIgnoreCase(elem, ["headers"], out var hdrsElem) &&
            hdrsElem.ValueKind == JsonValueKind.Object)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in hdrsElem.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } hv)
                {
                    headers[p.Name] = hv;
                }
            }
        }

        List<string>? args = null;
        if (McpToolResultFormatter.TryGetPropertyIgnoreCase(elem, ["args"], out var argsElem) &&
            argsElem.ValueKind == JsonValueKind.Array)
        {
            args = [];
            foreach (var a in argsElem.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { } av)
                {
                    args.Add(av);
                }
            }
        }

        Dictionary<string, string>? env = null;
        if (McpToolResultFormatter.TryGetPropertyIgnoreCase(elem, ["env"], out var envElem) &&
            envElem.ValueKind == JsonValueKind.Object)
        {
            env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in envElem.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } ev)
                {
                    env[p.Name] = ev;
                }
            }
        }

        var cwd = McpToolResultFormatter.OptionalString(elem, ["cwd", "workingDirectory"]);
        def = new McpPluginDefinition(id, transport, url, headers, command, args, env, cwd);
        return true;
    }

    private sealed class SinglePluginBackend : IMcpToolBackend
    {
        private readonly McpPluginRouter _router;
        private readonly string _pluginId;

        public SinglePluginBackend(McpPluginRouter router, string pluginId)
        {
            _router = router;
            _pluginId = pluginId;
        }

        public string ServerName => _pluginId;

        public async Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(
            McpSessionPrincipal caller,
            CancellationToken ct)
        {
            await _router.VerifyTrackAccessAsync(caller, _pluginId, ct).ConfigureAwait(false);
            var client = await _router.GetOrOpenClientAsync(_pluginId, ct).ConfigureAwait(false);
            return await client.ListToolsAsync(ct).ConfigureAwait(false);
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
                await _router._kernel.GuardMcpToolCallAsync(
                    caller.TrackId,
                    caller.SessionId,
                    _pluginId,
                    toolName,
                    normalizedArgs,
                    ct, caller.ExecutionOrigin).ConfigureAwait(false);

                var client = await _router.GetOrOpenClientAsync(_pluginId, ct).ConfigureAwait(false);
                var result = await client.CallToolAsync(toolName, normalizedArgs, ct).ConfigureAwait(false);
                await _router._kernel.Actors.GetOrActivate(caller.TrackId).AfterMcpToolCallAsync(_pluginId, toolName,
                    JsonSerializer.SerializeToElement(new { mcpServer = _pluginId, arguments = normalizedArgs, result }), ct).ConfigureAwait(false);
                return result;
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

    private interface IUpstreamMcpClient : IAsyncDisposable
    {
        bool IsHealthy { get; }
        Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(CancellationToken ct);
        Task<McpCallToolResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct);
    }

    private sealed class HttpMcpPluginClient : IUpstreamMcpClient
    {
        private readonly string _pluginId;
        private readonly Uri _url;
        private readonly IReadOnlyDictionary<string, string>? _headers;
        private readonly HttpClient _httpClient;
        private string? _mcpSessionId;
        private bool _initialized;
        private long _nextId;

        public HttpMcpPluginClient(
            string pluginId,
            Uri url,
            IReadOnlyDictionary<string, string>? headers,
            HttpClient httpClient)
        {
            _pluginId = pluginId;
            _url = url;
            _headers = headers;
            _httpClient = httpClient;
        }

        public bool IsHealthy => true;

        public async Task EnsureInitializedAsync(CancellationToken ct)
        {
            if (_initialized)
            {
                return;
            }

            var initParams = McpToolResultFormatter.ParseClone(
                """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"perga-host","version":"1.0.0"}}""");
            _ = await SendRpcRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
            await SendRpcNotificationAsync("notifications/initialized", ct).ConfigureAwait(false);
            _initialized = true;
        }

        public async Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(CancellationToken ct)
        {
            var res = await SendRpcRequestAsync("tools/list", McpToolResultFormatter.EmptyObject, ct)
                .ConfigureAwait(false);
            return ParseToolsListResult(res);
        }

        public async Task<McpCallToolResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
        {
            var callParams = JsonSerializer.SerializeToElement(
                new { name = toolName, arguments },
                PergaJsonDefaults.McpOptions);
            var res = await SendRpcRequestAsync("tools/call", callParams, ct).ConfigureAwait(false);
            return ParseCallToolResult(res);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<JsonElement> SendRpcRequestAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            };

            using var req = CreatePostMessage(payload);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
            {
                var sid = sessionValues.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sid))
                {
                    _mcpSessionId = sid;
                }
            }

            var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Upstream HTTP MCP plugin '{_pluginId}' returned HTTP {(int)resp.StatusCode}: {bodyText}");
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            var jsonLine = string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
                ? ExtractJsonDataFromSse(bodyText, id)
                : bodyText;

            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errElem) && errElem.ValueKind == JsonValueKind.Object)
            {
                var errMsg = McpToolResultFormatter.OptionalString(errElem, ["message"]) ?? errElem.GetRawText();
                throw new InvalidOperationException($"Upstream MCP plugin '{_pluginId}' error: {errMsg}");
            }

            if (root.TryGetProperty("result", out var resultElem))
            {
                return resultElem.Clone();
            }

            return McpToolResultFormatter.EmptyObject;
        }

        private async Task SendRpcNotificationAsync(string method, CancellationToken ct)
        {
            var payload = new { jsonrpc = "2.0", method };
            using var req = CreatePostMessage(payload);
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            _ = resp;
        }

        private HttpRequestMessage CreatePostMessage<T>(T payload)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, PergaJsonDefaults.McpOptions);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            var req = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = content
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            if (!string.IsNullOrWhiteSpace(_mcpSessionId))
            {
                req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
            }

            if (_headers is not null)
            {
                foreach (var (k, v) in _headers)
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
            }

            return req;
        }

        private static string ExtractJsonDataFromSse(string sseBody, long expectedId)
        {
            string? lastJson = null;
            var lines = sseBody.Split('\n');
            var dataBuilder = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var chunk = line["data:".Length..].TrimStart();
                    if (dataBuilder.Length > 0)
                    {
                        dataBuilder.Append('\n');
                    }

                    dataBuilder.Append(chunk);
                }
                else if (line.Length == 0 && dataBuilder.Length > 0)
                {
                    var candidate = dataBuilder.ToString();
                    dataBuilder.Clear();
                    lastJson = candidate;
                    if (MatchesRpcId(candidate, expectedId))
                    {
                        return candidate;
                    }
                }
            }

            if (dataBuilder.Length > 0)
            {
                lastJson = dataBuilder.ToString();
            }

            return lastJson ?? throw new InvalidOperationException("No JSON-RPC event data found in upstream SSE stream.");
        }

        private static bool MatchesRpcId(string json, long expectedId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("id", out var idElem) &&
                       idElem.ValueKind == JsonValueKind.Number &&
                       idElem.TryGetInt64(out var idVal) &&
                       idVal == expectedId;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class StdioMcpPluginClient : IUpstreamMcpClient
    {
        private readonly string _pluginId;
        private readonly Process _process;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly Task _stdoutLoop;
        private long _nextId;
        private int _disposed;

        private StdioMcpPluginClient(string pluginId, Process process)
        {
            _pluginId = pluginId;
            _process = process;
            _stdoutLoop = Task.Run(ReadStdoutLoopAsync);
        }

        public bool IsHealthy => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

        public static async Task<StdioMcpPluginClient> StartAsync(
            McpPluginDefinition def,
            Encoding utf8NoBom,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = def.Command!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = utf8NoBom,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };

            if (!string.IsNullOrWhiteSpace(def.Cwd) && Directory.Exists(def.Cwd))
            {
                psi.WorkingDirectory = Path.GetFullPath(def.Cwd);
            }

            if (def.Args is not null)
            {
                foreach (var arg in def.Args)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            ChildProcessEnvironment.Apply(psi, ChildProcessEnvironment.Capture());
            if (def.Env is not null)
            {
                foreach (var (k, v) in def.Env)
                {
                    psi.Environment[k] = v;
                }
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
            {
                proc.Dispose();
                throw new InvalidOperationException($"Failed to start stdio MCP plugin '{def.Id}' ('{def.Command}').");
            }

            // Drain stderr asynchronously so child never blocks.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch
                {
                }
            }, CancellationToken.None);

            var client = new StdioMcpPluginClient(def.Id, proc);
            try
            {
                var initParams = McpToolResultFormatter.ParseClone(
                    """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"perga-host","version":"1.0.0"}}""");
                _ = await client.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
                await client.SendNotificationAsync("notifications/initialized", ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(CancellationToken ct)
        {
            var res = await SendRequestAsync("tools/list", McpToolResultFormatter.EmptyObject, ct)
                .ConfigureAwait(false);
            return ParseToolsListResult(res);
        }

        public async Task<McpCallToolResult> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
        {
            var callParams = JsonSerializer.SerializeToElement(
                new { name = toolName, arguments },
                PergaJsonDefaults.McpOptions);
            var res = await SendRequestAsync("tools/call", callParams, ct).ConfigureAwait(false);
            return ParseCallToolResult(res);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _shutdownCts.Cancel();
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await _stdoutLoop.ConfigureAwait(false);
            }
            catch
            {
            }

            foreach (var kvp in _pending.ToArray())
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetCanceled();
                }
            }

            _process.Dispose();
            _writeLock.Dispose();
            _shutdownCts.Dispose();
        }

        private async Task<JsonElement> SendRequestAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                var line = JsonSerializer.Serialize(
                    new { jsonrpc = "2.0", id, method, @params = parameters },
                    PergaJsonDefaults.McpOptions);
                await WriteLineAsync(line, ct).ConfigureAwait(false);
                return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private Task SendNotificationAsync(string method, CancellationToken ct)
        {
            var line = JsonSerializer.Serialize(new { jsonrpc = "2.0", method }, PergaJsonDefaults.McpOptions);
            return WriteLineAsync(line, ct);
        }

        private async Task WriteLineAsync(string line, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadStdoutLoopAsync()
        {
            try
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(_shutdownCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object ||
                            !root.TryGetProperty("id", out var idElem) ||
                            idElem.ValueKind != JsonValueKind.Number ||
                            !idElem.TryGetInt64(out var id))
                        {
                            continue;
                        }

                        if (!_pending.TryRemove(id, out var tcs))
                        {
                            continue;
                        }

                        if (root.TryGetProperty("error", out var errElem) && errElem.ValueKind == JsonValueKind.Object)
                        {
                            var msg = McpToolResultFormatter.OptionalString(errElem, ["message"]) ?? errElem.GetRawText();
                            tcs.TrySetException(new InvalidOperationException($"Stdio MCP plugin '{_pluginId}' error: {msg}"));
                        }
                        else if (root.TryGetProperty("result", out var resElem))
                        {
                            tcs.TrySetResult(resElem.Clone());
                        }
                        else
                        {
                            tcs.TrySetResult(McpToolResultFormatter.EmptyObject);
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore non-JSON diagnostic stdout lines.
                    }
                }
            }
            catch
            {
            }
            finally
            {
                var exitError = new InvalidOperationException($"Stdio MCP plugin '{_pluginId}' exited unexpectedly.");
                foreach (var kvp in _pending.ToArray())
                {
                    if (_pending.TryRemove(kvp.Key, out var tcs))
                    {
                        tcs.TrySetException(exitError);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<McpToolWireDescriptor> ParseToolsListResult(JsonElement res)
    {
        var list = new List<McpToolWireDescriptor>();
        if (res.ValueKind == JsonValueKind.Object &&
            McpToolResultFormatter.TryGetPropertyIgnoreCase(res, ["tools"], out var toolsArr) &&
            toolsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in toolsArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = McpToolResultFormatter.OptionalString(item, ["name"]);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var desc = McpToolResultFormatter.OptionalString(item, ["description"]) ?? string.Empty;
                var schema = McpToolResultFormatter.TryGetPropertyIgnoreCase(item, ["inputSchema", "input_schema"], out var s)
                    ? s.Clone()
                    : McpToolResultFormatter.ParseClone("""{"type":"object","properties":{}}""");
                // Do not forward self-declared approval hints from untrusted third-party plugins.
                list.Add(new McpToolWireDescriptor(name, desc, schema));
            }
        }

        return list;
    }

    private static McpCallToolResult ParseCallToolResult(JsonElement res)
    {
        if (res.ValueKind != JsonValueKind.Object)
        {
            return McpToolResultFormatter.Success(res);
        }

        var isError = McpToolResultFormatter.TryGetPropertyIgnoreCase(res, ["isError", "is_error"], out var errFlag) &&
                      errFlag.ValueKind == JsonValueKind.True;

        if (McpToolResultFormatter.TryGetPropertyIgnoreCase(res, ["content"], out var contentElem) &&
            contentElem.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<McpContentBlock>();
            foreach (var c in contentElem.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.Object)
                {
                    var type = McpToolResultFormatter.OptionalString(c, ["type"]) ?? "text";
                    var text = McpToolResultFormatter.OptionalString(c, ["text"]) ?? c.GetRawText();
                    blocks.Add(new McpContentBlock(type, text));
                }
            }

            return new McpCallToolResult(blocks, isError);
        }

        return new McpCallToolResult([new McpContentBlock("text", res.GetRawText())], isError);
    }
}
