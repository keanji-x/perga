using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Perga.VcsKit;

public static partial class WorkspaceFileReader
{
    public enum EntryKind { RegularFile, Directory, Other }

    public static string ResolveExistingPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = File.GetAttributes(path);
            return ResolveCanonicalPath(path);
        }

        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw UnixPathError(path);
        try { return Marshal.PtrToStringUTF8(resolved)!; }
        finally { Free(resolved); }
    }

    public static EntryKind GetEntryKind(string path)
    {
        if (OperatingSystem.IsLinux())
            return GetLinuxEntryKind(path);

        if (OperatingSystem.IsMacOS())
        {
            var attributes = new MacAttributeList { BitmapCount = 5, Common = 0x8 }; // ATTR_CMN_OBJTYPE
            var result = new uint[2];
            if (GetMacPathAttributes(path, ref attributes, result, 8, 0) != 0)
                throw UnixPathError(path);
            return GetMacEntryKind(result);
        }

        if (!OperatingSystem.IsWindows())
            throw new UnauthorizedAccessException("File types cannot be validated on this platform.");

        var windowsAttributes = File.GetAttributes(path);
        if (windowsAttributes.HasFlag(FileAttributes.Directory))
            return EntryKind.Directory;
        return windowsAttributes.HasFlag(FileAttributes.Device) ? EntryKind.Other : EntryKind.RegularFile;
    }

    public static FileStream OpenRegularFile(string canonicalPath)
    {
        var handle = OpenHandle(canonicalPath);
        try
        {
            if (!IsRegularHandle(handle, canonicalPath))
                throw new ArgumentException("Path is not a regular file.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static EntryKind GetLinuxEntryKind(string path, int fd = -100, int flags = 0) // AT_FDCWD
    {
        const uint statxType = 1;
        if (Statx(fd, path, flags, statxType, out var info) != 0)
            throw UnixPathError(path);
        return (info.Mask & statxType) == 0 ? EntryKind.Other : ModeKind(info.Mode);
    }

    private static EntryKind ModeKind(ushort mode) => (mode & 0xf000) switch
    {
        0x8000 => EntryKind.RegularFile,
        0x4000 => EntryKind.Directory,
        _ => EntryKind.Other
    };

    private static EntryKind GetMacEntryKind(uint[] result) => (result[0] == 8 ? result[1] : 0) switch
    {
        1 => EntryKind.RegularFile, // VREG
        2 => EntryKind.Directory, // VDIR
        _ => EntryKind.Other
    };

    private static bool IsRegularHandle(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsLinux())
            return GetLinuxEntryKind("", handle.DangerousGetHandle().ToInt32(), 0x1000) == EntryKind.RegularFile; // AT_EMPTY_PATH
        if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? MacFStatInode64(handle, out var info)
                : MacFStat(handle, out info);
            if (result != 0)
                throw UnixPathError(path);
            return ModeKind(info.Mode) == EntryKind.RegularFile;
        }

        return OperatingSystem.IsWindows() && GetFileType(handle) == 1; // FILE_TYPE_DISK
    }

    private static Exception UnixPathError(string path) => Marshal.GetLastPInvokeError() switch
    {
        2 or 20 => new FileNotFoundException($"Path '{path}' not found."),
        1 or 13 => new UnauthorizedAccessException($"Access denied to path '{path}'."),
        _ => new ArgumentException($"Path '{path}' could not be resolved.")
    };

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr address);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx info);

    [DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetMacPathAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref MacAttributeList attributes, [Out] uint[] buffer, nuint length, int flags);

    // Darwin's 64-bit inode stat layout has the same mode offset on x64 and arm64.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStat
    {
        [FieldOffset(4)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacFStatInode64(SafeFileHandle fd, out MacStat info);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int MacFStat(SafeFileHandle fd, out MacStat info);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);
}
