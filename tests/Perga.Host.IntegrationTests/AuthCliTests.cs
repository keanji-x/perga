using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class AuthCliTests
{
    [Fact]
    public async Task StatusReadsMissingOldAndMigratedDatabasesWithoutCreatingOrCopyingFiles()
    {
        var directory = Directory.CreateTempSubdirectory("perga-auth-cli-");
        try
        {
            var path = Path.Combine(directory.FullName, "perga.db");
            var before = await RunAsync(path, ["status"]);
            Assert.Equal(0, before.Code);
            Assert.Equal(0, Status(before).SchemaVersion);
            Assert.False(Status(before).Configured);
            Assert.Empty(directory.EnumerateFiles());

            var migrator = new SqliteMigrator();
            await new SqliteMigrator(migrator.Migrations.Take(25).ToArray()).MigrateAsync(path);
            var old = await RunAsync(path, ["status"]);
            Assert.Equal(25, Status(old).SchemaVersion);
            Assert.False(Status(old).Configured);
            Assert.False(File.Exists(path + ".bak-v25"));

            var refused = await RunAsync(path, ["init-owner", "--username", "Owner", "--password-stdin"], "a sample password");
            Assert.Equal(1, refused.Code);
            Assert.Contains("--allow-migrate", refused.Error);
            Assert.Empty(refused.Output);
            Assert.Equal(25, Status(await RunAsync(path, ["status"])).SchemaVersion);

            var created = await RunAsync(path,
                ["init-owner", "--username", "Owner", "--display-name", "CLI Owner", "--password-stdin", "--allow-migrate"],
                "a sample password");
            Assert.Equal(0, created.Code);
            Assert.Equal("Owner account 'owner' created." + Environment.NewLine, created.Output);
            Assert.True(File.Exists(path + ".bak-v25"));
            var files = DatabaseFiles(directory);
            var status = Status(await RunAsync(path, ["status"]));
            Assert.Equal(SqliteMigrator.CurrentSchemaVersion, status.SchemaVersion);
            Assert.True(status.Configured);
            Assert.Equal("owner", status.Username);
            Assert.Equal("CLI Owner", status.DisplayName);
            Assert.Equal(files, DatabaseFiles(directory));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LiveOwnerCommandsRotateCredentialsAndRevokeSessions()
    {
        await using var host = new PergaHostTestCluster();
        var db = Path.Combine(host.DataDir, "perga.db");
        host.TimeProvider.SetUtcNow(DateTimeOffset.UtcNow);
        var initialized = await RunAsync(db, ["init-owner", "--username", "Owner", "--password-stdin"], "a sample password");
        Assert.True(initialized.Code == 0, initialized.Error);
        Assert.Equal(1, (await RunAsync(db, ["init-owner", "--username", "other", "--password-stdin"], "another sample password")).Code);
        var first = await Login("owner", "a sample password");
        Assert.Equal(1, Status(await RunAsync(db, ["status"])).ActiveSessions);

        var rename = await RunAsync(db, ["rename-owner", "--username", "New.Name", "--display-name", "New Name"]);
        Assert.True(rename.Code == 0, rename.Error);
        await CheckRevoked(first);
        Assert.Equal("new.name", Status(await RunAsync(db, ["status"])).Username);
        var second = await Login("new.name", "a sample password");

        Assert.Equal(0, (await RunAsync(db, ["disable-owner"])).Code);
        await CheckRevoked(second);
        Assert.True(Status(await RunAsync(db, ["status"])).Disabled);
        using (var disabled = await AuthHttpIntegrationTests.LoginAsync(host.Client, "new.name", "a sample password"))
            await AuthHttpIntegrationTests.AssertErrorAsync(disabled, 401, "invalid_credentials");

        Assert.Equal(0, (await RunAsync(db, ["enable-owner"])).Code);
        Assert.False(Status(await RunAsync(db, ["status"])).Disabled);
        var third = await Login("new.name", "a sample password");
        Assert.Equal(0, (await RunAsync(db, ["passwd", "--password-stdin"], "a replacement password")).Code);
        await CheckRevoked(third);
        using (var old = await AuthHttpIntegrationTests.LoginAsync(host.Client, "new.name", "a sample password"))
            await AuthHttpIntegrationTests.AssertErrorAsync(old, 401, "invalid_credentials");

        var fourth = await Login("new.name", "a replacement password");
        Assert.Equal(0, (await RunAsync(db, ["revoke-sessions"])).Code);
        await CheckRevoked(fourth);
        Assert.Equal(0, Status(await RunAsync(db, ["status"])).ActiveSessions);

        async Task<string> Login(string username, string password)
        {
            using var response = await AuthHttpIntegrationTests.LoginAsync(host.Client, username, password);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return BrowserAuthentication.ReadCookie(response);
        }

        async Task CheckRevoked(string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
            request.Headers.Add("Cookie", cookie);
            using var response = await host.Client.SendAsync(request);
            await AuthHttpIntegrationTests.AssertErrorAsync(response, 401, "unauthorized");
        }
    }

    [Fact]
    public async Task PasswordMustComeFromInputAndMeetCreationRules()
    {
        await using var host = new PergaHostTestCluster();
        var db = Path.Combine(host.DataDir, "perga.db");
        var passwordArgument = await RunAsync(db, ["init-owner", "--username", "owner", "--password", "some credential"]);
        Assert.NotEqual(0, passwordArgument.Code);

        var veryShort = await RunAsync(db, ["init-owner", "--username", "owner", "--password-stdin"], "😀😀😀😀😀😀");
        Assert.Equal(1, veryShort.Code);
        var whitespace = await RunAsync(db, ["init-owner", "--username", "owner", "--password-stdin"], new string(' ', 12));
        Assert.Equal(1, whitespace.Code);
        Assert.Null(await host.Services.GetRequiredService<IAuthStore>().GetOwnerAsync(default));

        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await AuthCliCommand.RunAsync(["auth", "--data-dir", host.DataDir, "status"], TextReader.Null, output, error);
        Assert.Equal(0, code);
        Assert.False(Status((code, output.ToString(), error.ToString())).Configured);
    }

    private static string[] DatabaseFiles(DirectoryInfo directory) =>
        directory.EnumerateFiles().Select(file => file.Name)
            .Where(name => name is not ("perga.db-wal" or "perga.db-shm")).Order().ToArray();

    private static async Task<(int Code, string Output, string Error)> RunAsync(string db, string[] args, string? password = null)
    {
        using var input = new StringReader(password is null ? "" : password + "\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await AuthCliCommand.RunAsync(["auth", .. args, "--db-path", db], input, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static SystemAuthStatusDto Status((int Code, string Output, string Error) result)
    {
        Assert.True(result.Code == 0, result.Error);
        return JsonSerializer.Deserialize<SystemAuthStatusDto>(result.Output, PergaJsonDefaults.ApiOptions)!;
    }
}
