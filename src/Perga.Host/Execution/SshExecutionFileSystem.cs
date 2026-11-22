using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Execution;

public sealed class SshExecutionFileSystem : IExecutionFileSystem
{
    private const int BinarySniffBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SshTargetConfig _config;

    public SshExecutionFileSystem(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default)
    {
        const string pyScript = """
            import errno, json, os, sys
            raw = sys.argv[1] if len(sys.argv) > 1 else ""
            home = os.path.expanduser("~")
            if not raw or not raw.strip():
                target = home
            else:
                trimmed = raw.strip()
                if trimmed == "~":
                    target = home
                elif trimmed.startswith("~/"):
                    target = os.path.join(home, trimmed[2:])
                else:
                    target = trimmed
            full = os.path.abspath(target)
            if os.path.isfile(full) and not os.path.isdir(full):
                print(json.dumps({"error": "invalid_argument", "message": f"Path '{full}' is a file, not a directory."}))
                sys.exit(0)
            if not os.path.exists(full):
                print(json.dumps({"error": "not_found", "message": f"Directory '{full}' not found."}))
                sys.exit(0)
            try:
                names = os.listdir(full)
            except PermissionError:
                print(json.dumps({"error": "forbidden", "message": f"Access denied reading directory '{full}'."}))
                sys.exit(0)
            except NotADirectoryError:
                print(json.dumps({"error": "invalid_argument", "message": f"Path '{full}' is a file, not a directory."}))
                sys.exit(0)
            entries = []
            for name in names:
                child = os.path.join(full, name)
                try:
                    is_link = os.path.islink(child)
                    is_dir = os.path.isdir(child)
                    entries.append({"name": name, "isDir": is_dir, "isSymlink": is_link})
                except OSError:
                    continue
            entries.sort(key=lambda e: (not e["isDir"], e["name"].lower(), e["name"]))
            parent = os.path.dirname(full)
            if parent == full:
                parent = None
            print(json.dumps({"path": full, "parent": parent, "entries": entries}))
            """;

        var remoteScript = BuildPythonCommand(pyScript, path ?? string.Empty);

        var (exitCode, stdout, stderr) = await SshCommandBuilder
            .RunTextAsync(_config, remoteScript, stdin: null, ct)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw KernelException.PreconditionFailed(
                $"Failed to list directory on target '{_config.TargetId}': {stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        ThrowIfRemoteError(root);

        var resolvedPath = root.GetProperty("path").GetString() ?? "/";
        var parent = root.TryGetProperty("parent", out var parentElem) && parentElem.ValueKind == JsonValueKind.String
            ? parentElem.GetString()
            : null;
        var entries = new List<HostDirEntryDto>();
        if (root.TryGetProperty("entries", out var entriesElem) && entriesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in entriesElem.EnumerateArray())
            {
                entries.Add(new HostDirEntryDto(
                    item.GetProperty("name").GetString() ?? string.Empty,
                    item.GetProperty("isDir").GetBoolean(),
                    item.GetProperty("isSymlink").GetBoolean()));
            }
        }

        return new HostListDirResultDto(resolvedPath, parent, entries);
    }

    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var (code, _, _) = await SshCommandBuilder
            .RunTextAsync(_config, $"[ -d {SshCommandBuilder.QuotePosix(path)} ]", stdin: null, ct)
            .ConfigureAwait(false);
        return code == 0;
    }

    public async Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        const string pyScript = "import os, sys; print(os.path.realpath(sys.argv[1]))";
        var remoteScript = BuildPythonCommand(pyScript, path);
        var (code, stdout, stderr) = await SshCommandBuilder
            .RunTextAsync(_config, remoteScript, stdin: null, ct)
            .ConfigureAwait(false);
        if (code != 0)
        {
            throw KernelException.PreconditionFailed(
                $"Failed to resolve path on target '{_config.TargetId}': {stderr.Trim()}");
        }
        return stdout.Trim();
    }

    public async Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
        string workspaceRoot,
        string? relativePath,
        int maxBytes,
        CancellationToken ct = default)
    {
        var limit = Math.Max(1, maxBytes);
        var pyScript = WorkspacePathScript + "\n" + """
            import base64
            max_bytes = max(1, int(sys.argv[3]))
            if os.path.isdir(real_cand):
                try:
                    names = sorted(os.listdir(real_cand))
                except PermissionError:
                    print(json.dumps({"error": "forbidden", "message": f"Access denied reading directory '{rel}'."}))
                    sys.exit(0)
                dirs = []
                files = []
                for name in names:
                    child_cand = os.path.join(candidate, name)
                    child_real = os.path.join(real_cand, name)
                    child_rel = os.path.relpath(child_cand, root_full).replace("\\", "/")
                    try:
                        if os.path.isdir(child_real):
                            dirs.append({"name": name, "relativePath": child_rel, "isDirectory": True, "sizeBytes": 0})
                        elif os.path.isfile(child_real):
                            st = os.stat(child_real)
                            files.append({"name": name, "relativePath": child_rel, "isDirectory": False, "sizeBytes": st.st_size})
                    except OSError:
                        continue
                print(json.dumps({"relativePath": rel, "isDirectory": True, "entries": dirs + files}))
                sys.exit(0)

            try:
                fd = os.open(real_cand, os.O_RDONLY | os.O_NONBLOCK)
                with os.fdopen(fd, "rb") as f:
                    info = os.fstat(f.fileno())
                    if not stat.S_ISREG(info.st_mode):
                        print(json.dumps({"error": "invalid_argument", "message": "Workspace path is not a regular file."}))
                        sys.exit(0)
                    total_size = info.st_size
                    data = f.read(max_bytes)
            except PermissionError:
                print(json.dumps({"error": "forbidden", "message": f"Access denied reading file '{rel}'."}))
                sys.exit(0)
            except FileNotFoundError:
                print(json.dumps({"error": "not_found", "message": f"Workspace path '{rel}' not found."}))
                sys.exit(0)

            print(json.dumps({
                "relativePath": rel,
                "isDirectory": False,
                "totalSize": total_size,
                "dataB64": base64.b64encode(data).decode("ascii")
            }))
            """;

        var remoteScript = BuildPythonCommand(pyScript, workspaceRoot, relativePath ?? string.Empty,
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (exitCode, stdout, stderr) = await SshCommandBuilder
            .RunTextAsync(_config, remoteScript, stdin: null, ct)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw KernelException.PreconditionFailed(
                $"Failed to read workspace path on target '{_config.TargetId}': {stderr.Trim()}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        ThrowIfRemoteError(root);

        var relPath = root.GetProperty("relativePath").GetString() ?? string.Empty;
        if (root.GetProperty("isDirectory").GetBoolean())
        {
            var entries = JsonSerializer.Deserialize<List<WorkspaceFileEntryDto>>(
                root.GetProperty("entries").GetRawText(),
                JsonOptions) ?? [];
            return new WorkspaceFileResultDto(
                relPath,
                IsDirectory: true,
                Content: null,
                Truncated: false,
                Entries: entries,
                IsBinary: false);
        }

        var totalSize = root.GetProperty("totalSize").GetInt64();
        var dataB64 = root.GetProperty("dataB64").GetString() ?? string.Empty;
        var bytes = Convert.FromBase64String(dataB64);

        var sniffLen = Math.Min(bytes.Length, BinarySniffBytes);
        if (bytes.AsSpan(0, sniffLen).IndexOf((byte)0) >= 0)
        {
            return new WorkspaceFileResultDto(
                relPath,
                IsDirectory: false,
                Content: null,
                Truncated: false,
                Entries: null,
                IsBinary: true);
        }

        var truncated = totalSize > bytes.Length;
        var validLen = truncated ? LocalExecutionFileSystem.TrimTrailingIncompleteUtf8(bytes) : bytes.Length;
        var text = Encoding.UTF8.GetString(bytes, 0, validLen);
        return new WorkspaceFileResultDto(
            relPath,
            IsDirectory: false,
            Content: text,
            Truncated: truncated,
            Entries: null,
            IsBinary: false);
    }

    public async Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
        string workspaceRoot,
        string relativePath,
        CancellationToken ct = default)
    {
        var pyScript = WorkspacePathScript + "\n" + """
            import shutil
            if not os.path.isfile(real_cand):
                print(json.dumps({"error": "not_found", "message": f"Workspace file '{rel}' not found."}))
                sys.exit(0)
            try:
                f = os.fdopen(os.open(real_cand, os.O_RDONLY | os.O_NONBLOCK), "rb")
            except PermissionError:
                print(json.dumps({"error": "forbidden", "message": f"Access denied reading file '{rel}'."}))
                sys.exit(0)
            with f:
                if not stat.S_ISREG(os.fstat(f.fileno()).st_mode):
                    print(json.dumps({"error": "invalid_argument", "message": "Workspace path is not a regular file."}))
                    sys.exit(0)
                print(json.dumps({"path": real_cand}), flush=True)
                shutil.copyfileobj(f, sys.stdout.buffer, 65536)
            """;
        var script = BuildPythonCommand(pyScript, workspaceRoot, relativePath);
        ct.ThrowIfCancellationRequested();
        var process = new Process
        {
            StartInfo = SshCommandBuilder.CreateSshStartInfo(
                _config, SshCommandBuilder.BuildSshExecArguments(_config, script), redirectStdin: false)
        };
        try
        {
            if (!process.Start())
            {
                throw new IOException($"Failed to start raw file transfer on target '{_config.TargetId}'.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        var stream = new SshFileStream(process, ct);
        try
        {
            // Read only the JSON header; a StreamReader could buffer file bytes past it.
            using var header = new MemoryStream();
            var singleByte = new byte[1];
            while (await stream.ReadAsync(singleByte.AsMemory(), ct).ConfigureAwait(false) != 0)
            {
                if (singleByte[0] == (byte)'\n') break;
                header.WriteByte(singleByte[0]);
            }
            using var doc = JsonDocument.Parse(header.ToArray());
            ThrowIfRemoteError(doc.RootElement);
            return (doc.RootElement.GetProperty("path").GetString()!, stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private const string WorkspacePathScript = """
        import json, os, stat, sys
        root_full = os.path.abspath(sys.argv[1])
        raw_path = sys.argv[2]

        root_with_sep = root_full if root_full.endswith(os.sep) else root_full + os.sep
        if not raw_path or not raw_path.strip():
            candidate = root_full
        else:
            trimmed = raw_path.strip()
            if os.path.isabs(trimmed):
                rooted = os.path.abspath(trimmed)
                if rooted == root_full or rooted.startswith(root_with_sep):
                    candidate = rooted
                else:
                    candidate = os.path.abspath(os.path.join(root_full, trimmed.lstrip("/\\")))
            else:
                candidate = os.path.abspath(os.path.join(root_full, trimmed))

        if candidate != root_full and not candidate.startswith(root_with_sep):
            print(json.dumps({"error": "forbidden", "message": "Path escapes workspace root."}))
            sys.exit(0)

        rel = "" if candidate == root_full else os.path.relpath(candidate, root_full).replace("\\", "/")

        if not os.path.exists(candidate):
            print(json.dumps({"error": "not_found", "message": f"Workspace path '{rel}' not found."}))
            sys.exit(0)

        real_root = os.path.realpath(root_full)
        real_root_sep = real_root if real_root.endswith(os.sep) else real_root + os.sep
        real_cand = os.path.realpath(candidate)
        if real_cand != real_root and not real_cand.startswith(real_root_sep):
            print(json.dumps({"error": "forbidden", "message": "Path escapes workspace root."}))
            sys.exit(0)
        """;

    private static string BuildPythonCommand(string script, params string[] arguments) =>
        "command -v python3 >/dev/null 2>&1 || { echo 'python3 is required for remote filesystem access.' >&2; exit 127; }\n" +
        "exec python3 -c " + SshCommandBuilder.QuotePosix(script) + " " +
        string.Join(" ", arguments.Select(SshCommandBuilder.QuotePosix));

    private sealed class SshFileStream : Stream
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private readonly CancellationTokenRegistration _cancellation;
        private bool _disposed;

        public SshFileStream(Process process, CancellationToken ct)
        {
            _process = process;
            _stdout = process.StandardOutput.BaseStream;
            _stderr = process.StandardError.ReadToEndAsync();
            _cancellation = ct.Register(() => SshCommandBuilder.TryKill(process));
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0 && buffer.Length > 0)
            {
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var error = await _stderr.ConfigureAwait(false);
                if (_process.ExitCode != 0)
                {
                    throw new IOException($"Remote file transfer failed: {error.Trim()}");
                }
            }
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Dispose();
            SshCommandBuilder.TryKill(_process);
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                await _stderr.ConfigureAwait(false);
            }
            finally
            {
                await _stdout.DisposeAsync().ConfigureAwait(false);
                _process.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }

    private static void ThrowIfRemoteError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errElem) || errElem.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var code = errElem.GetString();
        var message = root.TryGetProperty("message", out var msgElem) ? msgElem.GetString() ?? "Remote filesystem error." : "Remote filesystem error.";
        throw code switch
        {
            "not_found" => KernelException.NotFound(message),
            "forbidden" => KernelException.Forbidden(message),
            "invalid_argument" => KernelException.InvalidArgument(message),
            _ => KernelException.PreconditionFailed(message)
        };
    }
}
