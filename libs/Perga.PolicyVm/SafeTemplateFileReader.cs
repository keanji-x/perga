using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Perga.PolicyVm;

/// <summary>
/// Hardened, bounded regular-file and anchored-directory reader for template sources.
/// On POSIX systems, resolves and opens every path component relative to an anchored directory FD
/// with O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, preventing symlink-swap TOCTOU escapes and FIFO hangs.
/// </summary>
internal static partial class SafeTemplateFileReader
{
    private static bool IsPosix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static int PosixO_NONBLOCK => OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
    private static int PosixO_CLOEXEC => OperatingSystem.IsMacOS() ? 0x0100_0000 : 0x0008_0000;
    private static int PosixO_DIRECTORY => OperatingSystem.IsMacOS()
        ? 0x0010_0000
        : RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x4000 : 0x10000;
    private static int PosixO_NOFOLLOW => OperatingSystem.IsMacOS()
        ? 0x0100
        : RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x10000 : 0x20000;
    private static int PosixELOOP => OperatingSystem.IsMacOS() ? 62 : 40;
    private const int PosixENOENT = 2;
    private const int PosixENOTDIR = 20;

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int PosixOpen(string pathname, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int PosixOpenAt(int dirfd, string pathname, int flags, int mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int PosixClose(int fd);

    internal static string ReadBoundedRegularFile(
        string path,
        long maxFileBytes,
        ref long totalBytes,
        long maxTotalBytes,
        string displayName,
        bool rejectSymlink)
    {
        if (IsPosix)
        {
            var flags = PosixO_NONBLOCK | PosixO_CLOEXEC | (rejectSymlink ? PosixO_NOFOLLOW : 0);
            var fd = PosixOpen(path, flags, 0);
            if (fd < 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == PosixELOOP)
                {
                    throw new InvalidOperationException("A template source file cannot be a symbolic link.");
                }
                if (err == PosixENOENT)
                {
                    throw new FileNotFoundException($"Template file '{displayName}' was not found.", path);
                }

                throw new IOException($"Failed to open template file '{displayName}' (errno {err}).");
            }

            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            return ReadBoundedFromHandle(handle, maxFileBytes, ref totalBytes, maxTotalBytes, displayName);
        }

        var info = new FileInfo(path);
        if (rejectSymlink && (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidOperationException("A template source file cannot be a symbolic link.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBoundedFromStream(stream, maxFileBytes, ref totalBytes, maxTotalBytes, displayName);
    }

    internal static (bool IsDirectory, string? SingleFileContent, SortedDictionary<string, string>? DirectoryFiles, string? EntryFileName)
        ReadAnchoredWorkspaceTarget(
            string canonicalWorkspace,
            string[] relativeSegments,
            HashSet<string> allowedExtensions,
            int maxFiles,
            long maxSingleFileBytes,
            long maxTotalBytes)
    {
        if (!IsPosix)
        {
            return ReadWorkspaceTargetFallback(
                canonicalWorkspace, relativeSegments, allowedExtensions, maxFiles, maxSingleFileBytes, maxTotalBytes);
        }

        var rootFd = PosixOpen(canonicalWorkspace, PosixO_DIRECTORY | PosixO_CLOEXEC, 0);
        if (rootFd < 0)
        {
            throw new DirectoryNotFoundException($"Workspace directory '{canonicalWorkspace}' could not be opened.");
        }

        using var rootHandle = new SafeFileHandle((nint)rootFd, ownsHandle: true);
        SafeFileHandle currentDir = rootHandle;
        var ownedIntermediate = new List<SafeFileHandle>();
        var currentPath = canonicalWorkspace;
        try
        {
            for (var i = 0; i < relativeSegments.Length - 1; i++)
            {
                var seg = relativeSegments[i];
                var nextFd = PosixOpenAt((int)currentDir.DangerousGetHandle(), seg,
                    PosixO_NOFOLLOW | PosixO_DIRECTORY | PosixO_CLOEXEC, 0);
                if (nextFd < 0)
                {
                    ThrowForOpenAtError(Marshal.GetLastWin32Error(), seg);
                }

                var nextHandle = new SafeFileHandle((nint)nextFd, ownsHandle: true);
                ownedIntermediate.Add(nextHandle);
                currentDir = nextHandle;
                currentPath = Path.Combine(currentPath, seg);
            }

            var leaf = relativeSegments[^1];
            var leafPath = Path.Combine(currentPath, leaf);
            var leafDirFd = PosixOpenAt((int)currentDir.DangerousGetHandle(), leaf,
                PosixO_NOFOLLOW | PosixO_DIRECTORY | PosixO_CLOEXEC, 0);
            if (leafDirFd >= 0)
            {
                using var dirHandle = new SafeFileHandle((nint)leafDirFd, ownsHandle: true);
                var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
                long totalBytes = 0;
                WalkAnchoredDirectory(dirHandle, leafPath, string.Empty, files, ref totalBytes,
                    allowedExtensions, maxFiles, maxSingleFileBytes, maxTotalBytes, leaf);
                var entry = files.ContainsKey("template.perga.js") ? "template.perga.js" : "template.perga.json";
                return (true, null, files, entry);
            }

            var dirErr = Marshal.GetLastWin32Error();
            if (dirErr == PosixELOOP)
            {
                throw new InvalidOperationException("Template paths cannot contain symbolic links.");
            }
            if (dirErr != PosixENOTDIR)
            {
                ThrowForOpenAtError(dirErr, leaf);
            }

            if (!leaf.EndsWith(".perga.js", StringComparison.OrdinalIgnoreCase) &&
                !leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Template files must end in .perga.js or .json.");
            }

            var fileFd = PosixOpenAt((int)currentDir.DangerousGetHandle(), leaf,
                PosixO_NOFOLLOW | PosixO_NONBLOCK | PosixO_CLOEXEC, 0);
            if (fileFd < 0)
            {
                ThrowForOpenAtError(Marshal.GetLastWin32Error(), leaf);
            }

            using var fileHandle = new SafeFileHandle((nint)fileFd, ownsHandle: true);
            long singleTotal = 0;
            var content = ReadBoundedFromHandle(fileHandle, maxSingleFileBytes, ref singleTotal, maxSingleFileBytes, leaf);
            return (false, content, null, leaf);
        }
        finally
        {
            for (var i = ownedIntermediate.Count - 1; i >= 0; i--)
            {
                ownedIntermediate[i].Dispose();
            }
        }
    }

    private static void WalkAnchoredDirectory(
        SafeFileHandle dirHandle,
        string displayDirPath,
        string relativePrefix,
        SortedDictionary<string, string> files,
        ref long totalBytes,
        HashSet<string> allowedExtensions,
        int maxFiles,
        long maxSingleFileBytes,
        long maxTotalBytes,
        string displayRoot)
    {
        var dirFd = (int)dirHandle.DangerousGetHandle();
        string[] entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(displayDirPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrEmpty(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            return;
        }

        var subDirs = new List<(string Name, string RelPath, string DisplayPath)>();
        foreach (var name in entries)
        {
            var relPath = string.IsNullOrEmpty(relativePrefix) ? name : $"{relativePrefix}/{name}";
            var childDisplay = Path.Combine(displayDirPath, name);

            var subFd = PosixOpenAt(dirFd, name, PosixO_NOFOLLOW | PosixO_DIRECTORY | PosixO_CLOEXEC, 0);
            if (subFd >= 0)
            {
                PosixClose(subFd);
                subDirs.Add((name, relPath, childDisplay));
                continue;
            }

            var subErr = Marshal.GetLastWin32Error();
            if (subErr == PosixELOOP)
            {
                continue; // Skip symlinks inside package directories.
            }
            if (subErr != PosixENOTDIR)
            {
                continue;
            }

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
            {
                continue;
            }

            var normalized = TemplateBundle.NormalizeRelativePath(relPath);
            if (files.Count >= maxFiles)
            {
                throw new InvalidOperationException($"Template package '{displayRoot}' exceeds maximum file count ({maxFiles}).");
            }

            var fileFd = PosixOpenAt(dirFd, name, PosixO_NOFOLLOW | PosixO_NONBLOCK | PosixO_CLOEXEC, 0);
            if (fileFd < 0)
            {
                var fileErr = Marshal.GetLastWin32Error();
                if (fileErr == PosixELOOP || fileErr == PosixENOENT)
                {
                    continue;
                }

                throw new IOException($"Failed to open template package file '{normalized}' (errno {fileErr}).");
            }

            using var fileHandle = new SafeFileHandle((nint)fileFd, ownsHandle: true);
            files[normalized] = ReadBoundedFromHandle(fileHandle, maxSingleFileBytes, ref totalBytes, maxTotalBytes, normalized);
        }

        foreach (var (name, relPath, childDisplay) in subDirs)
        {
            var subFd = PosixOpenAt(dirFd, name, PosixO_NOFOLLOW | PosixO_DIRECTORY | PosixO_CLOEXEC, 0);
            if (subFd < 0)
            {
                continue;
            }

            using var subHandle = new SafeFileHandle((nint)subFd, ownsHandle: true);
            WalkAnchoredDirectory(subHandle, childDisplay, relPath, files, ref totalBytes,
                allowedExtensions, maxFiles, maxSingleFileBytes, maxTotalBytes, displayRoot);
        }
    }

    private static (bool IsDirectory, string? SingleFileContent, SortedDictionary<string, string>? DirectoryFiles, string? EntryFileName)
        ReadWorkspaceTargetFallback(
            string canonicalWorkspace,
            string[] relativeSegments,
            HashSet<string> allowedExtensions,
            int maxFiles,
            long maxSingleFileBytes,
            long maxTotalBytes)
    {
        var current = canonicalWorkspace;
        foreach (var part in relativeSegments)
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Template paths cannot contain symbolic links.");
            }
        }

        if (Directory.Exists(current))
        {
            var entry = File.Exists(Path.Combine(current, "template.perga.js"))
                ? "template.perga.js"
                : "template.perga.json";
            return (true, null, null, entry);
        }

        if (!current.EndsWith(".perga.js", StringComparison.OrdinalIgnoreCase) &&
            !current.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Template files must end in .perga.js or .json.");
        }

        long total = 0;
        var text = ReadBoundedRegularFile(current, maxSingleFileBytes, ref total, maxSingleFileBytes, Path.GetFileName(current), rejectSymlink: true);
        return (false, text, null, Path.GetFileName(current));
    }

    private static string ReadBoundedFromHandle(
        SafeFileHandle handle,
        long maxFileBytes,
        ref long totalBytes,
        long maxTotalBytes,
        string displayName)
    {
        var attrs = File.GetAttributes(handle);
        if ((attrs & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException($"Template file '{displayName}' must be a regular file.");
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        return ReadBoundedFromStream(stream, maxFileBytes, ref totalBytes, maxTotalBytes, displayName);
    }

    private static string ReadBoundedFromStream(
        FileStream stream,
        long maxFileBytes,
        ref long totalBytes,
        long maxTotalBytes,
        string displayName)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException($"Template file '{displayName}' must be a regular file.");
        }

        var remainingTotal = maxTotalBytes - totalBytes;
        if (remainingTotal < 0)
        {
            throw new InvalidOperationException($"Template package exceeds maximum total size ({maxTotalBytes} bytes).");
        }

        var readCap = (int)Math.Min(maxFileBytes, remainingTotal);
        var buffer = new byte[readCap + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read > maxFileBytes)
        {
            throw new InvalidOperationException($"Template file '{displayName}' exceeds maximum size ({maxFileBytes} bytes).");
        }

        totalBytes += read;
        if (totalBytes > maxTotalBytes)
        {
            throw new InvalidOperationException($"Template package exceeds maximum total size ({maxTotalBytes} bytes).");
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, read, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void ThrowForOpenAtError(int errno, string segment)
    {
        if (errno == PosixELOOP)
        {
            throw new InvalidOperationException("Template paths cannot contain symbolic links.");
        }

        if (errno == PosixENOENT)
        {
            throw new FileNotFoundException($"Template path component '{segment}' was not found.");
        }

        if (errno == PosixENOTDIR)
        {
            throw new InvalidOperationException($"Template path component '{segment}' is not a directory.");
        }

        throw new IOException($"Failed to open template path component '{segment}' (errno {errno}).");
    }
}
