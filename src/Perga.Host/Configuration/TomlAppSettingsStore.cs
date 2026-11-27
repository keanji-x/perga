using System.Text;
using Microsoft.Win32.SafeHandles;
using Perga.Kernel.Settings;

namespace Perga.Host.Configuration;

internal sealed class TomlAppSettingsStore(string dataDir, Action<SafeFileHandle>? flushDirectory = null) : IAppSettingsStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const UnixFileMode SharedPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly string _path = Path.Combine(dataDir, "settings.toml");
    private readonly Action<SafeFileHandle> _flushDirectory = flushDirectory ?? SettingsUnixFiles.FlushDirectory;
    private bool _pendingDirectorySync;

    public async Task<IReadOnlyDictionary<string, object>> ReadAsync(CancellationToken ct)
    {
        RetryPendingDirectorySync();
        try
        {
            await using var stream = OpenRead();
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(stream.SafeFileHandle) & SharedPermissions) != 0)
                throw new IOException("settings.toml must have owner-only permissions.");

            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, ct).ConfigureAwait(false);
            return SettingsTomlCodec.Decode(Utf8.GetString(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, object>();
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Expected valid UTF-8.", ex);
        }
    }

    private FileStream OpenRead()
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream(SettingsUnixFiles.OpenRegularFile(_path), FileAccess.Read);

        if ((File.GetAttributes(_path) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new IOException("settings.toml must be a regular file.");
        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: true);
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        RetryPendingDirectorySync();
        var bytes = Utf8.GetBytes(SettingsTomlCodec.Encode(values));
        var tempPath = Path.Combine(dataDir, $".settings.toml.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        ct.ThrowIfCancellationRequested();
        var stream = new FileStream(tempPath, options);
        try
        {
            await using (stream)
            {
                // Create with no group/other access and ensure the final mode even under a restrictive umask.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            using var parent = OperatingSystem.IsWindows() ? null : SettingsUnixFiles.OpenDirectory(dataDir);
            File.Move(tempPath, _path, overwrite: true);
            if (parent is not null)
            {
                _pendingDirectorySync = true;
                SyncParentDirectory(parent);
            }
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    private void RetryPendingDirectorySync()
    {
        if (!_pendingDirectorySync) return;
        using var parent = SettingsUnixFiles.OpenDirectory(dataDir);
        SyncParentDirectory(parent);
    }

    private void SyncParentDirectory(SafeFileHandle parent)
    {
        _flushDirectory(parent);
        _pendingDirectorySync = false;
    }
}
