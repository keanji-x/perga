using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Perga.Host.Configuration;

internal static class SettingsUnixFiles
{
    private const int Interrupted = 4; // EINTR

    internal static SafeFileHandle OpenRegularFile(string path)
    {
        // O_NOFOLLOW rejects links at open, and O_NONBLOCK avoids hanging on a FIFO.
        var handle = OpenReadOnly(path, OperatingSystem.IsMacOS() ? 0x104 : 0x20800);
        if (IsRegularFile(handle)) return handle;
        handle.Dispose();
        throw new IOException("settings.toml must be a regular file.");
    }

    internal static SafeFileHandle OpenDirectory(string path) =>
        OpenReadOnly(path, OperatingSystem.IsMacOS() ? 0x100000 : 0x10000); // O_DIRECTORY

    internal static void FlushDirectory(SafeFileHandle directory)
    {
        while (FSync(directory) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
                throw new IOException($"Could not flush the settings directory (errno {error}).");
        }
    }

    private static SafeFileHandle OpenReadOnly(string path, int flags)
    {
        // O_RDONLY is zero. O_CLOEXEC keeps the file descriptor out of child processes.
        flags |= OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
        while (true)
        {
            var fd = Open(path, flags);
            if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
            var error = Marshal.GetLastPInvokeError();
            if (error == 2) throw new FileNotFoundException("Settings path does not exist.", path);
            if (error != Interrupted) throw new IOException($"Could not open the settings path (errno {error}).");
        }
    }

    private static bool IsRegularFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsMacOS())
        {
            var attributes = new MacAttributeList { BitmapCount = 5, Common = 0x8 }; // ATTR_CMN_OBJTYPE
            return FGetAttrList(handle, ref attributes, out var result, 8, 0) == 0 &&
                result.Length == 8 && result.Type == 1; // VREG
        }

        const uint statxType = 1;
        const int atEmptyPath = 0x1000;
        return Statx(handle, "", atEmptyPath, statxType, out var stat) == 0 &&
            (stat.Mask & statxType) != 0 && (stat.Mode & 0xf000) == 0x8000; // S_IFMT, S_IFREG
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)] // Linux statx has a fixed layout across architectures.
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct MacAttributeList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint Common;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacObjectType
    {
        public uint Length;
        public uint Type;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(SafeFileHandle fd);

    [DllImport("libc", EntryPoint = "statx")]
    private static extern int Statx(SafeFileHandle fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx stat);

    [DllImport("libc", EntryPoint = "fgetattrlist")]
    private static extern int FGetAttrList(SafeFileHandle fd, ref MacAttributeList attributes,
        out MacObjectType result, nuint length, nuint options);
}
