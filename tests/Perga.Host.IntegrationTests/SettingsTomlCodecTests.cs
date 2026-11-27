using System.Text.Json;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Settings;

namespace Perga.Host.IntegrationTests;

public sealed class SettingsTomlCodecTests
{
    [Fact]
    public void DecodesSupportedScalarsEscapesAndCommentsAndWritesDeterministically()
    {
        const string text = """
            # settings
            [ sessions ] # table
            idle_timeout_minutes = +4_5 # number
            offset = -9
            enabled = true
            disabled = false
            [planners]
            default_provider = 'acp:antigravity'
            note = "# is text; \" \\ \b \t \n \f \r \u00E9 \U0001F642 \u0000"
            """;

        var values = SettingsTomlCodec.Decode(text.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(45L, values["sessions.idle_timeout_minutes"]);
        Assert.Equal(-9L, values["sessions.offset"]);
        Assert.Equal(true, values["sessions.enabled"]);
        Assert.Equal(false, values["sessions.disabled"]);
        Assert.Equal("acp:antigravity", values["planners.default_provider"]);
        Assert.Equal("# is text; \" \\ \b \t \n \f \r é 🙂 \0", values["planners.note"]);

        var encoded = SettingsTomlCodec.Encode(values);
        Assert.StartsWith("[planners]\ndefault_provider = \"acp:antigravity\"\n", encoded);
        Assert.Contains("\n[sessions]\ndisabled = false\nenabled = true\nidle_timeout_minutes = 45\noffset = -9\n", encoded);

        var roundTrip = SettingsTomlCodec.Decode(encoded);
        Assert.Equal(values.OrderBy(pair => pair.Key), roundTrip.OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("[sessions]\nidle = 15\nidle = 20", "Duplicate key")]
    [InlineData("[sessions]\n[sessions]", "Duplicate table")]
    [InlineData("[sessions", "closing table bracket")]
    [InlineData("[sessions]\nidle = missing", "signed decimal integer")]
    [InlineData("[sessions]\nidle = 01", "signed decimal integer")]
    [InlineData("[sessions]\nidle = 1__5", "signed decimal integer")]
    [InlineData("[sessions]\nidle = 9223372036854775808", "signed decimal integer")]
    [InlineData("[sessions]\nidle = 15.0", "signed decimal integer")]
    [InlineData("[sessions]\nidle = # missing", "signed decimal integer")]
    [InlineData("[sessions]\nidle = \"bad\\q\"", "Unknown string escape")]
    [InlineData("[sessions]\nidle = \"\\uD800\"", "Invalid Unicode escape")]
    [InlineData("[sessions]\nidle = \"\\u123\0\"", "Invalid Unicode escape")]
    [InlineData("[sessions]\nidle = \"\\U0000123\0\"", "Invalid Unicode escape")]
    [InlineData("[sessions]\nidle = \"\\U00110000\"", "Invalid Unicode escape")]
    [InlineData("[sessions]\nidle = \"unterminated", "Unterminated string")]
    [InlineData("[sessions]\nidle = \"a\" \"b\"", "Unexpected text")]
    [InlineData("[sessions]\nidle = \"a\u007f\"", "Control character in string")]
    [InlineData("[sessions] # control\u0001", "Control character in comment")]
    [InlineData("[sessions]\nidle = 15\r", "signed decimal integer")]
    [InlineData("idle = 15", "Expected a table")]
    public void RejectsMalformedSettings(string text, string diagnostic)
    {
        var error = Assert.Throws<FormatException>(() => SettingsTomlCodec.Decode(text));
        Assert.StartsWith("Line ", error.Message);
        Assert.Contains(diagnostic, error.Message);
    }

    [Fact]
    public async Task ReplacesFileAtomicallyAndCleansUpAfterFailedMove()
    {
        var dir = Directory.CreateTempSubdirectory("perga-settings-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.toml");
            var store = new TomlAppSettingsStore(dir.FullName);
            Assert.Empty(await store.ReadAsync(default));
            Assert.False(File.Exists(path));

            await store.WriteAsync(new Dictionary<string, object> { ["sessions.idle_timeout_minutes"] = 15L }, default);
            using (var oldFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var oldReader = new StreamReader(oldFile))
            {
                await store.WriteAsync(new Dictionary<string, object> { ["sessions.idle_timeout_minutes"] = 45L }, default);
                Assert.Contains("idle_timeout_minutes = 15", await oldReader.ReadToEndAsync());
                Assert.Equal(45L, (await store.ReadAsync(default))["sessions.idle_timeout_minutes"]);
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                File.Delete(path);
                var operatorPath = Path.Combine(dir.FullName, "perga.toml");
                await File.WriteAllTextAsync(operatorPath, "[instance]\nname = 'operator'");
                File.CreateSymbolicLink(path, operatorPath);
                await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(default));
                Assert.Equal("[instance]\nname = 'operator'", await File.ReadAllTextAsync(operatorPath));
            }

            File.Delete(path);
            Directory.CreateDirectory(path);
            await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(default));
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.WriteAsync(new Dictionary<string, object> { ["sessions.idle_timeout_minutes"] = 30L }, default));
            Assert.True(Directory.Exists(path));
            Assert.Empty(dir.EnumerateFiles(".settings.toml.*.tmp"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilesReplacementOnlyAfterDirectorySyncSucceeds()
    {
        if (OperatingSystem.IsWindows()) return;

        const string key = "sessions.idle_timeout_minutes";
        var dir = Directory.CreateTempSubdirectory("perga-settings-sync-");
        try
        {
            var syncError = new IOException("Could not flush the settings directory (errno 5)."); // EIO
            var failSync = true;
            var syncAttempts = 0;
            var store = new TomlAppSettingsStore(dir.FullName, parent =>
            {
                syncAttempts++;
                if (failSync) throw syncError;
                SettingsUnixFiles.FlushDirectory(parent);
            });
            using var settings = new AppSettingsService(new(new KernelOptions(), ["codex"]), store);
            var initial = await settings.ReloadAsync();
            var patch = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(45) };

            Assert.Same(syncError, await Assert.ThrowsAsync<IOException>(() => settings.PatchAsync(new(initial.Contract.Rev, patch))));
            Assert.Contains("idle_timeout_minutes = 45", await File.ReadAllTextAsync(Path.Combine(dir.FullName, "settings.toml")));
            Assert.Same(initial, settings.Current);

            var readError = await Assert.ThrowsAsync<AppSettingsLoadException>(() => settings.ReloadAsync());
            Assert.Same(syncError, readError.InnerException);
            Assert.Same(initial, settings.Current);
            var overwrite = new Dictionary<string, object> { [key] = 30L };
            Assert.Same(syncError, await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(overwrite, default)));
            Assert.Equal(3, syncAttempts);

            failSync = false;
            var recovered = await settings.ReloadAsync();
            Assert.Equal(4, syncAttempts);
            Assert.Equal(initial.Contract.Rev + 1, recovered.Contract.Rev);
            Assert.Equal(45, recovered.Contract.Values[key].GetInt32());
            Assert.Same(recovered, settings.Current);
            Assert.Same(recovered, await settings.ReloadAsync());
            Assert.Equal(4, syncAttempts);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RejectsSettingsFileWithOtherWritePermission()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("perga-settings-mode-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.toml");
            var store = new TomlAppSettingsStore(dir.FullName);
            await store.WriteAsync(new Dictionary<string, object> { ["sessions.idle_timeout_minutes"] = 30L }, default);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);

            var error = await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(default));
            Assert.Contains("owner", error.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RejectsInvalidUtf8WithAFormatDiagnostic()
    {
        var dir = Directory.CreateTempSubdirectory("perga-settings-utf8-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.toml");
            await File.WriteAllBytesAsync(path, [0xff, 0xfe]);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var error = await Assert.ThrowsAsync<FormatException>(() => new TomlAppSettingsStore(dir.FullName).ReadAsync(default));
            Assert.Contains("UTF-8", error.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
