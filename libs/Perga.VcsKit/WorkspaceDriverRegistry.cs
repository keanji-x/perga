namespace Perga.VcsKit;

public sealed class WorkspaceDriverRegistry : IWorkspaceDriverRegistry
{
    private readonly Dictionary<string, IWorkspaceDriver> _drivers;
    private readonly string[] _registeredDriverIds;

    public WorkspaceDriverRegistry(IEnumerable<IWorkspaceDriver> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        _drivers = new Dictionary<string, IWorkspaceDriver>(StringComparer.Ordinal);
        var ids = new List<string>();

        foreach (var driver in drivers)
        {
            ArgumentNullException.ThrowIfNull(driver);
            if (string.IsNullOrWhiteSpace(driver.DriverId))
            {
                throw new ArgumentException("Workspace driver must specify a non-empty DriverId.", nameof(drivers));
            }

            if (!_drivers.TryAdd(driver.DriverId, driver))
            {
                throw new ArgumentException(
                    $"Duplicate workspace driver registration for '{driver.DriverId}'.",
                    nameof(drivers));
            }

            ids.Add(driver.DriverId);
        }

        _registeredDriverIds = [.. ids];
    }

    public IReadOnlyList<string> RegisteredDrivers => _registeredDriverIds;

    public IWorkspaceDriver GetRequired(string driverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);

        if (_drivers.TryGetValue(driverId, out var driver))
        {
            return driver;
        }

        var available = _registeredDriverIds.Length == 0
            ? "<none>"
            : string.Join(", ", _registeredDriverIds);

        throw new KeyNotFoundException(
            $"Workspace driver '{driverId}' is not registered. Registered drivers: {available}.");
    }

    public static WorkspaceDriverRegistry CreateDefault(IVcsService vcs)
    {
        ArgumentNullException.ThrowIfNull(vcs);

        return new WorkspaceDriverRegistry(
        [
            new DirectWorkspaceDriver(),
            new VcsWorkspaceDriver(vcs),
        ]);
    }
}
