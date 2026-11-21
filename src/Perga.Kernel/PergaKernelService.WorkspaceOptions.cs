using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<TrackWorkspaceOptionsDto> GetTrackWorkspaceOptionsAsync(
        string areaId, string? cwd, string? templateId, CancellationToken ct)
    {
        var area = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
        var env = _executionEnvironments.GetRequired(area.ExecutionTargetId);
        var templateDriver = string.IsNullOrWhiteSpace(templateId)
            ? null : Templates.GetRequiredPolicy(templateId).Manifest.WorkspaceDriver;
        var options = await WorkspaceChoicesAsync(env, WorkspaceSource(area, cwd), templateDriver, ct).ConfigureAwait(false);
        var recommended = options.FirstOrDefault(choice => choice.Option.Id == "managed" ||
            (choice.DriverId != DirectWorkspaceDriver.Id && choice.DriverId != OverlayWorkspaceDriver.Id) ||
            choice.DriverId == templateDriver);
        return new(recommended?.Option.Id, options.Select(choice => choice.Option).ToArray());
    }

    private static string? WorkspaceSource(AreaDto area, string? cwd, bool useExplicitEmpty = true)
    {
        if (useExplicitEmpty && cwd == string.Empty)
            return null;
        var source = string.IsNullOrWhiteSpace(cwd) ? area.DefaultCwd : cwd;
        return string.IsNullOrWhiteSpace(source) ? null : source;
    }

    private string PreferredWorkspaceDriver(string? explicitDriver, string? templateDriver) =>
        !string.IsNullOrWhiteSpace(explicitDriver) ? explicitDriver
            : !string.IsNullOrWhiteSpace(templateDriver) ? templateDriver : _options.DefaultWorkspaceDriver;

    private async Task<IReadOnlyList<WorkspaceChoice>> WorkspaceChoicesAsync(
        IExecutionEnvironment env, string? source, string? templateDriver, CancellationToken ct)
    {
        if (source is null)
        {
            var driver = PreferredWorkspaceDriver(null, templateDriver);
            if (!await env.WorkspaceDrivers.GetRequired(driver).IsAvailableAsync(null, ct).ConfigureAwait(false))
                throw KernelException.PreconditionFailed($"Workspace driver '{driver}' is unavailable on target '{env.Descriptor.Id}'.");
            return [new(new("managed", "Managed workspace", "Create a fresh workspace for this Track."), driver)];
        }

        if (!await env.Files.DirectoryExistsAsync(source, ct).ConfigureAwait(false))
            throw KernelException.InvalidArgument("The source folder is unavailable. Choose an existing folder.");

        var registered = env.WorkspaceDrivers.RegisteredDrivers;
        var choices = new List<WorkspaceChoice>();
        if (registered.Contains(VcsWorkspaceDriver.Id))
        {
            foreach (var backend in await env.Vcs.ListWorkspaceBackendsAsync(source, ct).ConfigureAwait(false))
            {
                var option = backend switch
                {
                    VcsBackendKind.JjColocated => new TrackWorkspaceOptionDto("jj", "Jujutsu workspace", "Create an isolated working copy with Jujutsu."),
                    VcsBackendKind.GitWorktree => new TrackWorkspaceOptionDto("git", "Git worktree", "Create an isolated working copy with Git."),
                    VcsBackendKind.PlainDirectory => new TrackWorkspaceOptionDto("copy", "Copy into a workspace", "Copy the source files into an isolated directory."),
                    _ => throw new InvalidOperationException($"Unsupported workspace backend '{backend}'.")
                };
                choices.Add(new(option, VcsWorkspaceDriver.Id, backend));
            }
        }

        if (registered.Contains(DirectWorkspaceDriver.Id))
            choices.Add(new(new("direct", "Use source folder", "Work directly in the selected directory."), DirectWorkspaceDriver.Id));
        if (registered.Contains(OverlayWorkspaceDriver.Id) &&
            await env.WorkspaceDrivers.GetRequired(OverlayWorkspaceDriver.Id).IsAvailableAsync(source, ct).ConfigureAwait(false))
            choices.Add(new(new(OverlayWorkspaceDriver.Id, "OverlayFS workspace",
                "Share a source snapshot and keep this Track's changes in a separate writable layer."), OverlayWorkspaceDriver.Id));
        if (choices.Count == 0)
            throw KernelException.InvalidArgument("No workspace options are available for this folder.");

        // Prefer isolation for attached folders unless the selected template has a preference.
        return choices.OrderByDescending(choice => choice.DriverId == templateDriver).ToArray();
    }

    private sealed record WorkspaceChoice(TrackWorkspaceOptionDto Option, string DriverId, VcsBackendKind? Backend = null);
}
