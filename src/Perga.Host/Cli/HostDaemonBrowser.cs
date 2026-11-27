using System.Diagnostics;

namespace Perga.Host.Cli;

internal static class HostDaemonBrowser
{
    private static readonly string[] DesktopEnvironment =
    [
        "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "BROWSER",
        "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_CONFIG_DIRS", "XDG_DATA_HOME", "XDG_DATA_DIRS",
        "XDG_CACHE_HOME", "XDG_CURRENT_DESKTOP", "XDG_SESSION_DESKTOP", "XDG_SESSION_TYPE",
        "DESKTOP_SESSION", "DESKTOP_STARTUP_ID", "XDG_ACTIVATION_TOKEN"
    ];

    public static bool CanOpen(HostCliEnvironment env, bool noOpen, bool explicitRequest = false)
    {
        if (noOpen || env.Get("PERGA_NO_OPEN") is "1" ||
            string.Equals(env.Get("PERGA_NO_OPEN"), "true", StringComparison.OrdinalIgnoreCase) ||
            (!explicitRequest && env.IsOutputRedirected))
            return false;

        if (new[] { "SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY", "CI" }.Any(env.Variables.ContainsKey))
            return false;

        return OperatingSystem.IsMacOS() ||
               (OperatingSystem.IsLinux() && (env.Get("DISPLAY") is not null || env.Get("WAYLAND_DISPLAY") is not null));
    }

    public static string ResolveBrowserLaunchUrl(string baseUrl, string dataDir)
    {
        var root = baseUrl.TrimEnd('/') + "/";
        var file = new FileInfo(Path.Combine(dataDir, "runtime", "local-launch-token"));
        try
        {
            // A valid token file has a physical length of at least 64 bytes. Checking
            // metadata first also avoids opening special files such as Unix FIFOs.
            if (file.LinkTarget is null && file.Exists && file.Length >= 64 &&
                (file.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0)
            {
                var token = File.ReadAllText(file.FullName).Trim();
                if (token.Length == 64 && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    return root + "auth/local?token=" + token;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return root;
    }

    public static bool TryOpenBrowser(HostCliEnvironment env, string url, string dataDir, bool noOpen, bool explicitRequest = false)
    {
        if (!CanOpen(env, noOpen, explicitRequest))
            return false;

        try
        {
            var launcher = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var arg in new[] { "-c", "exec \"$@\" </dev/null >/dev/null 2>&1", "perga-open", launcher, ResolveBrowserLaunchUrl(url, dataDir) })
                start.ArgumentList.Add(arg);
            start.Environment.Clear();
            foreach (var key in DesktopEnvironment)
            {
                if (env.Variables.TryGetValue(key, out var value))
                    start.Environment[key] = value;
            }
            using var browser = Process.Start(start);
            return browser is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
