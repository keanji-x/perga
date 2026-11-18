using Perga.Contracts;
using Perga.VcsKit;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<TrackWorkspaceOptionsDto> GetTrackWorkspaceOptionsAsync(
        string areaId, string? cwd, string? templateId, CancellationToken ct)
    {
        var area = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
        var templateDriver = string.IsNullOrWhiteSpace(templateId)
            ? null : Templates.GetRequiredPolicy(templateId).Manifest.WorkspaceDriver;
        var options = WorkspaceChoices(WorkspaceSource(area, cwd), templateDriver);
        var recommended = options.FirstOrDefault(choice => choice.Option.Id == "managed" ||
            choice.DriverId != DirectWorkspaceDriver.Id || templateDriver == DirectWorkspaceDriver.Id);
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

    private IReadOnlyList<WorkspaceChoice> WorkspaceChoices(string? source, string? templateDriver)
    {
        if (source is null)
        {
            var driver = PreferredWorkspaceDriver(null, templateDriver);
            _workspaceDrivers.GetRequired(driver);
            return [new(new("managed", "Managed workspace", "Create a fresh workspace for this Track."), driver)];
        }

        if (!Directory.Exists(source))
            throw KernelException.InvalidArgument("The source folder is unavailable. Choose an existing folder.");

        var registered = _workspaceDrivers.RegisteredDrivers;
        var choices = new List<WorkspaceChoice>();
        if (registered.Contains(VcsWorkspaceDriver.Id))
        {
            foreach (var backend in _vcsService.ListWorkspaceBackends(source))
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
        if (choices.Count == 0)
            throw KernelException.InvalidArgument("No workspace options are available for this folder.");

        // Prefer isolation for attached folders unless the selected template has a preference.
        return choices.OrderByDescending(choice => choice.DriverId == templateDriver).ToArray();
    }

    private sealed record WorkspaceChoice(TrackWorkspaceOptionDto Option, string DriverId, VcsBackendKind? Backend = null);
}
