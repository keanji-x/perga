using System.Net;
using Perga.Host.IntegrationTests.Fixtures;

namespace Perga.Host.IntegrationTests;

[Collection("Host process environment")]
public sealed class HostEnvironmentIsolationTests
{
    [Fact]
    public async Task BootDoesNotTouchStorageFromAmbientHostSettings()
    {
        var directory = Directory.CreateTempSubdirectory("perga-host-isolation-");
        try
        {
            var otherInstance = Path.Combine(directory.FullName, "inherited-instance");
            var otherData = Path.Combine(directory.FullName, "inherited-data");
            using var ambient = new AmbientEnvironmentScope(new Dictionary<string, string?>
            {
                ["PERGA_DB_PATH"] = Path.Combine(otherInstance, "perga.db"),
                ["PERGA_INSTALL_ROOT"] = otherInstance,
                ["Perga__InstallationRoot"] = null,
                ["Perga__DbPath"] = null,
                ["Perga__DataDir"] = otherData,
                ["Perga__WorkspacesRootDirectory"] = Path.Combine(otherData, "workspaces"),
                ["ASPNETCORE_Perga__DataDir"] = otherData,
                ["DOTNET_Perga__DataDir"] = otherData
            });

            var testData = Path.Combine(directory.FullName, "test-data");
            await using var factory = new IsolatedPergaHostFactory(testData);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/healthz");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(File.Exists(Path.Combine(testData, "perga.db")));
            Assert.False(Directory.Exists(otherData));
            Assert.False(Directory.Exists(otherInstance));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

[CollectionDefinition("Host process environment", DisableParallelization = true)]
public sealed class HostProcessEnvironmentCollection;
