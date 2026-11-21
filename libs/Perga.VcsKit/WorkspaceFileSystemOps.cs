namespace Perga.VcsKit;

/// <summary>
/// Abstraction for filesystem probes and directory lifecycle operations used by
/// <see cref="VcsService"/>, <see cref="DirectWorkspaceDriver"/>, and <see cref="VcsWorkspaceDriver"/>.
/// Defaults to local host filesystem operations; remote or cross-user targets supply an implementation
/// that executes inside the target environment.
/// </summary>
public interface IWorkspaceFileSystemOps
{
    bool IsLocal { get; }

    (string RootPath, bool HasJj, bool HasGit)? FindNearestVcsRoot(
        string startDirectory, bool searchAncestors = true, bool ignoreAmbientRoots = true)
    {
        var fs = this;
        var current = NormalizeDirectoryPath(startDirectory);
        var tempRoot = NormalizeDirectoryPath(fs.TempRootPath);

        while (!string.IsNullOrEmpty(current))
        {
            var parent = Path.GetDirectoryName(current);
            var isFilesystemRoot = string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal);

            // Never treat `/` or the system temp root (`/tmp`) itself as a repository root.
            if (!ignoreAmbientRoots || (!isFilesystemRoot && !string.Equals(current, tempRoot, PathComparison)))
            {
                var hasJj = IsValidJjMarker(fs, Path.Combine(current, ".jj"));
                var hasGit = IsValidGitMarker(fs, Path.Combine(current, ".git"));

                if (hasJj || hasGit)
                {
                    return (current, hasJj, hasGit);
                }
            }

            if (isFilesystemRoot || !searchAncestors)
            {
                break;
            }

            current = NormalizeDirectoryPath(parent!);
        }

        return null;
    }

    private static bool IsValidJjMarker(IWorkspaceFileSystemOps fs, string jjPath)
    {
        if (!fs.DirectoryExists(jjPath))
        {
            return false;
        }

        var repoEntry = Path.Combine(jjPath, "repo");
        return fs.DirectoryExists(repoEntry) || fs.FileExists(repoEntry);
    }

    private static bool IsValidGitMarker(IWorkspaceFileSystemOps fs, string gitPath)
    {
        if (fs.DirectoryExists(gitPath))
        {
            return fs.FileExists(Path.Combine(gitPath, "HEAD"));
        }

        if (fs.FileExists(gitPath))
        {
            var firstLine = fs.ReadFirstLine(gitPath);
            return firstLine is not null &&
                   firstLine.TrimStart().StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    string TempRootPath => IsLocal ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) : "/tmp";

    bool DirectoryExists(string path);
    bool FileExists(string path);
    string? ReadFirstLine(string path);
    byte[]? ReadContainedFileBytes(string workspaceRoot, string path, int maxBytes);
    void CreateDirectory(string path);
    void CopyDirectory(string sourceDir, string targetDir, CancellationToken ct);
    void DeleteDirectoryIfExists(string path);
    string? ResolveExecutable(string executableName, string? pathEnvironmentOverride = null);
}

public sealed class LocalWorkspaceFileSystemOps : IWorkspaceFileSystemOps
{
    public static LocalWorkspaceFileSystemOps Instance { get; } = new();

    public bool IsLocal => true;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadFirstLine(string path)
    {
        try
        {
            return File.ReadLines(path).FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? ReadContainedFileBytes(string workspaceRoot, string path, int maxBytes)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = WorkspaceFileReader.Open(WorkspaceFileReader.ResolveContainedPath(workspaceRoot, path));
            var limit = (int)Math.Min(fs.Length, Math.Max(1, maxBytes));
            var buf = new byte[limit];
            var read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return read == buf.Length ? buf : buf[..read];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CopyDirectory(string sourceDir, string targetDir, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");
        }

        var sourceEntries = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var sourceSubdirs = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);

        Directory.CreateDirectory(targetDir);

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        var normalizedTargetPrefix = normalizedTarget + Path.DirectorySeparatorChar;

        foreach (var dir in sourceSubdirs)
        {
            ct.ThrowIfCancellationRequested();
            var fullSubdir = Path.GetFullPath(dir);
            if (string.Equals(Path.TrimEndingDirectorySeparator(fullSubdir), normalizedTarget, pathComparison) ||
                fullSubdir.StartsWith(normalizedTargetPrefix, pathComparison))
            {
                continue;
            }

            var relDir = Path.GetRelativePath(sourceDir, fullSubdir);
            Directory.CreateDirectory(Path.Combine(targetDir, relDir));
        }

        foreach (var file in sourceEntries)
        {
            ct.ThrowIfCancellationRequested();
            var fullFile = Path.GetFullPath(file);
            if (fullFile.StartsWith(normalizedTargetPrefix, pathComparison))
            {
                continue;
            }

            var relFile = Path.GetRelativePath(sourceDir, fullFile);
            var destFile = Path.Combine(targetDir, relFile);
            var destParent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            File.Copy(fullFile, destFile, overwrite: true);
        }
    }

    public void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    public string? ResolveExecutable(string executableName, string? pathEnvironmentOverride = null)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        if (Path.IsPathRooted(executableName))
        {
            return File.Exists(executableName) ? executableName : null;
        }

        var pathVar = pathEnvironmentOverride ?? Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            foreach (var entry in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(entry.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var yielded = new HashSet<string>(StringComparer.Ordinal);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && yielded.Add(userProfile))
        {
            var candidate = Path.Combine(userProfile, ".local", "bin", executableName);
            if (File.Exists(candidate)) return candidate;
        }

        var homeEnv = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homeEnv) && yielded.Add(homeEnv))
        {
            var candidate = Path.Combine(homeEnv, ".local", "bin", executableName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
