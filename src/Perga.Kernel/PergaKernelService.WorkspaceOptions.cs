using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<TrackWorkspaceOptionsDto> GetTrackWorkspaceOptionsAsync(
        string areaId, string? cwd, string? templateId, CancellationToken ct, string? targetId = null)
    {
        var area = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
        var effectiveTargetId = string.IsNullOrWhiteSpace(targetId)
            ? area.ExecutionTargetId
            : ExecutionTargetIds.Normalize(targetId);
        var env = _executionEnvironments.GetRequired(effectiveTargetId);
        var templateDriver = string.IsNullOrWhiteSpace(templateId)
            ? null : Templates.GetRequiredPolicy(templateId).Manifest.WorkspaceDriver;
        var source = string.Equals(effectiveTargetId, area.ExecutionTargetId, StringComparison.Ordinal)
            ? WorkspaceSource(area, cwd)
            : (string.IsNullOrWhiteSpace(cwd) ? null : cwd);
        var resolved = await ResolveWorkspaceChoicesAsync(env, source, templateDriver, ct).ConfigureAwait(false);
        return new(resolved.Recommended?.Option.Id, resolved.Choices.Select(choice => choice.Option).ToArray());
    }

    private static string? NormalizeWorkspaceDriver(string? driver) =>
        string.IsNullOrWhiteSpace(driver) ? null : driver.Trim();

    private static string? WorkspaceSource(AreaDto area, string? cwd, bool useExplicitEmpty = true)
    {
        if (useExplicitEmpty && cwd == string.Empty)
            return null;
        var source = string.IsNullOrWhiteSpace(cwd) ? area.DefaultCwd : cwd;
        return string.IsNullOrWhiteSpace(source) ? null : source;
    }

    private string PreferredWorkspaceDriver(string? explicitDriver, string? templateDriver) =>
        NormalizeWorkspaceDriver(explicitDriver)
            ?? NormalizeWorkspaceDriver(templateDriver)
            ?? _options.DefaultWorkspaceDriver;

    private async Task<ResolvedWorkspaceChoices> ResolveWorkspaceChoicesAsync(
        IExecutionEnvironment env, string? source, string? templateDriver, CancellationToken ct)
    {
        var normalizedTemplateDriver = NormalizeWorkspaceDriver(templateDriver);
        if (source is null)
        {
            var driver = PreferredWorkspaceDriver(null, normalizedTemplateDriver);
            if (!await env.WorkspaceDrivers.GetRequired(driver).IsAvailableAsync(null, ct).ConfigureAwait(false))
                throw KernelException.PreconditionFailed($"Workspace driver '{driver}' is unavailable on target '{env.Descriptor.Id}'.");
            var managed = new WorkspaceChoice(new("managed", "Managed workspace", "Create a fresh workspace for this Track."), driver);
            return new([managed], managed);
        }

        if (!await env.Files.DirectoryExistsAsync(source, ct).ConfigureAwait(false))
            throw KernelException.InvalidArgument("The source folder is unavailable. Choose an existing folder.");

        var registered = env.WorkspaceDrivers.RegisteredDrivers;
        var backends = await env.Vcs.ListWorkspaceBackendsAsync(source, ct).ConfigureAwait(false);
        var isPlainDirectory = backends.Contains(VcsBackendKind.PlainDirectory);
        var choices = new List<WorkspaceChoice>();
        WorkspaceChoice? plainCopyChoice = null;
        if (registered.Contains(VcsWorkspaceDriver.Id))
        {
            foreach (var backend in backends)
            {
                var option = backend switch
                {
                    VcsBackendKind.JjColocated => new TrackWorkspaceOptionDto("jj", "Jujutsu workspace", "Create an isolated working copy with Jujutsu."),
                    VcsBackendKind.GitWorktree => new TrackWorkspaceOptionDto("git", "Git worktree", "Create an isolated working copy with Git."),
                    VcsBackendKind.PlainDirectory => new TrackWorkspaceOptionDto("copy", "Copy into a workspace", "Copy the source files into an isolated directory."),
                    _ => throw new InvalidOperationException($"Unsupported workspace backend '{backend}'.")
                };
                var choice = new WorkspaceChoice(option, VcsWorkspaceDriver.Id, backend);
                if (backend == VcsBackendKind.PlainDirectory)
                    plainCopyChoice = choice;
                else
                    choices.Add(choice);
            }
        }

        if (registered.Contains(DirectWorkspaceDriver.Id))
            choices.Add(new(new("direct", "Use source folder", "Work directly in the selected directory."), DirectWorkspaceDriver.Id));
        if (plainCopyChoice is not null)
            choices.Add(plainCopyChoice);
        if (registered.Contains(OverlayWorkspaceDriver.Id) &&
            await env.WorkspaceDrivers.GetRequired(OverlayWorkspaceDriver.Id).IsAvailableAsync(source, ct).ConfigureAwait(false))
            choices.Add(new(new(OverlayWorkspaceDriver.Id, "OverlayFS workspace",
                "Share a source snapshot and keep this Track's changes in a separate writable layer."), OverlayWorkspaceDriver.Id));
        if (choices.Count == 0)
            throw KernelException.InvalidArgument("No workspace options are available for this folder.");

        // Prefer template-specified drivers first; otherwise keep isolation first for VCS repos
        // and direct-folder first for plain directories (with copy as an explicit opt-in).
        var ordered = choices.OrderByDescending(choice => choice.DriverId == normalizedTemplateDriver).ToArray();
        var recommended = ordered.FirstOrDefault(choice => choice.DriverId == normalizedTemplateDriver)
            ?? (isPlainDirectory
                ? ordered.FirstOrDefault(choice => choice.DriverId == DirectWorkspaceDriver.Id)
                : ordered.FirstOrDefault(choice => choice.Backend is VcsBackendKind.JjColocated or VcsBackendKind.GitWorktree));
        return new(ordered, recommended);
    }

    private sealed record ResolvedWorkspaceChoices(IReadOnlyList<WorkspaceChoice> Choices, WorkspaceChoice? Recommended);

    private sealed record WorkspaceChoice(
        TrackWorkspaceOptionDto Option,
        string DriverId,
        VcsBackendKind? Backend = null);
}
