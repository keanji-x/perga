using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Perga.VcsKit;

public static class WorkspaceFileReader
{
    public static string ResolveCanonicalPath(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        var current = Path.GetPathRoot(normalized)!;
        var parts = normalized[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current)
                        ? new FileInfo(current)
                        : null;

                if (info is not null && info.LinkTarget is not null)
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is not null)
                    {
                        current = Path.GetFullPath(resolved.FullName);
                    }
                }
            }
            catch (IOException)
            {
                throw new UnauthorizedAccessException("Workspace path could not be resolved.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException("Workspace path could not be resolved.");
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    public static string ResolveContainedPath(string root, string candidate)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new UnauthorizedAccessException("The workspace root must be a directory.");

        var canonicalPath = ResolveCanonicalPath(candidate);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(canonicalPath, root, StringComparison.Ordinal) &&
            !canonicalPath.StartsWith(prefix, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Resolved path escapes workspace root.");
        return canonicalPath;
    }

    public static FileStream Open(string canonicalPath)
    {
        var handle = OpenHandle(canonicalPath);
        try
        {
            var openedPath = GetOpenedPath(handle);
            // Validate the actual open file. Serving the path separately would resolve its links again.
            if (!string.Equals(openedPath, canonicalPath, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Workspace file changed before it could be opened.");

            var stream = new FileStream(handle, FileAccess.Read);
            if (!stream.CanSeek)
            {
                stream.Dispose();
                throw new UnauthorizedAccessException("Workspace path is not a readable file.");
            }

            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle? OpenDirectory(string canonicalPath)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        var handle = OpenHandle(canonicalPath);
        try
        {
            if (!string.Equals(GetOpenedPath(handle), canonicalPath, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Workspace directory changed before it could be opened.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string path)
    {
        if (OperatingSystem.IsWindows())
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Nonblocking open avoids waiting indefinitely if a workspace entry becomes a pipe.
        int flags;
        if (OperatingSystem.IsLinux())
            flags = 0x800 | 0x80000; // O_NONBLOCK | O_CLOEXEC
        else if (OperatingSystem.IsMacOS())
            flags = 0x4 | 0x1000000;
        else
            throw new UnauthorizedAccessException("Workspace file handles cannot be validated on this platform.");

        var fd = OpenUnix(path, flags);
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 20)
                throw new FileNotFoundException("Workspace file no longer exists.");
            throw new UnauthorizedAccessException("Workspace file could not be opened.");
        }

        return new SafeFileHandle(fd, ownsHandle: true);
    }

    private static string? GetOpenedPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
            return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget;
        if (OperatingSystem.IsMacOS())
        {
            var attributes = new MacAttributeList { BitmapCount = 5, Common = 0x08000000 }; // ATTR_CMN_FULLPATH
            var buffer = new byte[8192];
            if (GetMacFileAttributes(handle, ref attributes, buffer, (nuint)buffer.Length, 0) != 0)
                return null;

            // The length prefix is followed by an attrreference_t whose offset is relative to itself.
            var offset = (long)BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4)) + 4;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
            if (offset < 12 || count == 0 || offset + count > buffer.Length || buffer[offset + count - 1] != 0)
                return null;
            return Encoding.UTF8.GetString(buffer.AsSpan((int)offset, (int)count - 1));
        }

        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, name, name.Capacity, flags: 0);
        if (length == 0 || length >= name.Capacity)
            return null;

        var opened = name.ToString();
        const string uncPrefix = @"\\?\UNC\";
        const string pathPrefix = @"\\?\";
        if (opened.StartsWith(uncPrefix, StringComparison.Ordinal))
            return @"\\" + opened[uncPrefix.Length..];
        return opened.StartsWith(pathPrefix, StringComparison.Ordinal) ? opened[pathPrefix.Length..] : null;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MacAttributeList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint Common;
        public uint Volume;
        public uint Directory;
        public uint File;
        public uint Fork;
    }

    [DllImport("libc", EntryPoint = "fgetattrlist", SetLastError = true)]
    private static extern int GetMacFileAttributes(
        SafeFileHandle fd, ref MacAttributeList attributes, [Out] byte[] buffer, nuint length, uint options);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int capacity, int flags);
}
