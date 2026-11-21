namespace Perga.Host.IntegrationTests.Fixtures;

// Use only when ambient variable names do not collide with parallel test collections.
internal sealed class AmbientEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new();

    public AmbientEnvironmentScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
