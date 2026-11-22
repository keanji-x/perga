using Perga.Kernel.Ports;
using Perga.PolicyVm;

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
        var canonicalWorkspace = CanonicalizeWorkspace(workspacePath);
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
            return TemplateBundle.ReadAnchoredWorkspaceSource(canonicalWorkspace, segments);
        }
        catch (KernelException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw KernelException.InvalidArgument(ex.Message);
        }
    }

    private static bool IsDescendant(string relative) =>
        relative is not ("." or "..") && !Path.IsPathRooted(relative) &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string CanonicalizeWorkspace(string workspace)
    {
        var root = Path.GetPathRoot(workspace)!;
        var current = root;
        foreach (var part in workspace[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return current;
    }
}
