using Perga.Kernel.Ports;
using Perga.PolicyVm;
using Perga.VcsKit;

namespace Perga.Kernel.Templates;

internal static class WorkspaceTemplateSource
{
    internal static string Read(string workspace, string? targetId, string path)
    {
        if (!ExecutionTargetIds.IsLocal(targetId))
            throw KernelException.InvalidArgument("Template paths are supported only on local execution targets.");
        if (string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(workspace))
            throw KernelException.InvalidArgument("The track has no local workspace directory.");
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
            throw KernelException.InvalidArgument("Template paths cannot contain '..' components.");

        var workspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var canonicalWorkspace = WorkspaceFileReader.ResolveCanonicalPath(workspacePath);
        var fullPath = Path.GetFullPath(path, workspacePath);
        var relative = Path.GetRelativePath(workspacePath, fullPath);
        if (!IsDescendant(relative) && Path.IsPathFullyQualified(path))
            relative = Path.GetRelativePath(canonicalWorkspace, fullPath);
        if (!IsDescendant(relative))
            throw KernelException.InvalidArgument("Template path must be inside the track workspace.");

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static s => s is "." or ".."))
            throw KernelException.InvalidArgument("Template path must be inside the track workspace.");

        try
        {
            var candidate = Path.Combine([canonicalWorkspace, .. segments]);
            RejectSymlinkSegments(canonicalWorkspace, segments);
            var canonicalTarget = WorkspaceFileReader.ResolveContainedPath(canonicalWorkspace, candidate);
            if (!string.Equals(canonicalTarget, candidate, StringComparison.Ordinal))
                throw new InvalidOperationException("Template paths cannot contain symbolic links.");

            if (Directory.Exists(canonicalTarget))
            {
                var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
                long totalBytes = 0;
                WalkPackageDirectory(canonicalTarget, canonicalTarget, string.Empty, files, ref totalBytes, relative);
                var entry = files.ContainsKey("template.perga.js") ? "template.perga.js" : "template.perga.json";
                return TemplateBundle.PackFiles(files, entry, relative);
            }

            if (!File.Exists(canonicalTarget))
                throw new FileNotFoundException($"Template path '{path}' was not found.");

            var leaf = Path.GetFileName(canonicalTarget);
            if (!leaf.EndsWith(".perga.js", StringComparison.OrdinalIgnoreCase) &&
                !leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Template files must end in .perga.js or .json.");

            using var stream = WorkspaceFileReader.Open(canonicalTarget);
            long singleTotal = 0;
            return TemplateBundle.ReadBoundedStream(
                stream, TemplateBundle.MaxSingleFileBytes, ref singleTotal, TemplateBundle.MaxSingleFileBytes, leaf);
        }
        catch (KernelException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw KernelException.InvalidArgument(ex.Message);
        }
    }

    private static void WalkPackageDirectory(
        string canonicalRoot,
        string canonicalDir,
        string relativePrefix,
        SortedDictionary<string, string> files,
        ref long totalBytes,
        string displayRoot)
    {
        using var openedDir = WorkspaceFileReader.OpenDirectory(canonicalDir);
        var enumerationPath = openedDir is null ? canonicalDir : $"/proc/self/fd/{openedDir.DangerousGetHandle()}";

        foreach (var entryPath in Directory.EnumerateFiles(enumerationPath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entryPath);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!TemplateBundle.IsAllowedPackageExtension(ext))
                continue;

            var childPath = Path.Combine(canonicalDir, name);
            if (IsSymlink(new FileInfo(childPath)))
                continue;

            var relPath = string.IsNullOrEmpty(relativePrefix) ? name : $"{relativePrefix}/{name}";
            var normalized = TemplateBundle.NormalizeRelativePath(relPath);
            if (files.Count >= TemplateBundle.MaxBundleFiles)
                throw new InvalidOperationException($"Template package '{displayRoot}' exceeds maximum file count ({TemplateBundle.MaxBundleFiles}).");

            var canonicalFile = WorkspaceFileReader.ResolveContainedPath(canonicalRoot, childPath);
            if (!string.Equals(canonicalFile, childPath, StringComparison.Ordinal))
                continue;
            using var stream = WorkspaceFileReader.Open(canonicalFile);
            files[normalized] = TemplateBundle.ReadBoundedStream(
                stream, TemplateBundle.MaxSingleFileBytes, ref totalBytes, TemplateBundle.MaxTotalBundleBytes, normalized);
        }

        foreach (var entryPath in Directory.EnumerateDirectories(enumerationPath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entryPath);
            var childPath = Path.Combine(canonicalDir, name);
            if (IsSymlink(new DirectoryInfo(childPath)))
                continue;

            var canonicalSubDir = WorkspaceFileReader.ResolveContainedPath(canonicalRoot, childPath);
            if (!string.Equals(canonicalSubDir, childPath, StringComparison.Ordinal))
                continue;
            var relPath = string.IsNullOrEmpty(relativePrefix) ? name : $"{relativePrefix}/{name}";
            WalkPackageDirectory(canonicalRoot, canonicalSubDir, relPath, files, ref totalBytes, displayRoot);
        }
    }

    private static void RejectSymlinkSegments(string canonicalWorkspace, string[] segments)
    {
        var current = canonicalWorkspace;
        foreach (var part in segments)
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current)) && IsSymlink(new FileInfo(current)))
                throw new InvalidOperationException("Template paths cannot contain symbolic links.");
        }
    }

    private static bool IsSymlink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsDescendant(string relative) =>
        relative is not ("." or "..") && !Path.IsPathRooted(relative) &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
