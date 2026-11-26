using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Perga.Host.Serialization;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.Auth;

public static class AuthCliCommand
{
    public static Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        var dbPath = new Option<string?>("--db-path") { Recursive = true, Description = "Path to the Perga SQLite database." };
        var dataDir = new Option<string?>("--data-dir") { Recursive = true, Description = "Perga data directory containing perga.db." };
        var allowMigrate = new Option<bool>("--allow-migrate") { Description = "Apply pending migrations before changing the owner." };
        var username = new Option<string>("--username") { Required = true, Description = "Owner username." };
        var displayName = new Option<string?>("--display-name") { Description = "Owner display name." };
        var passwordStdin = new Option<bool>("--password-stdin") { Description = "Read one password line from standard input." };
        var root = new RootCommand("Manage the Perga owner account and browser sessions.");
        var auth = new Command("auth", "Manage the Perga owner account and browser sessions.");
        root.Subcommands.Add(auth);
        auth.Options.Add(dbPath);
        auth.Options.Add(dataDir);

        Add("init-owner", "Create the installation owner.", async (store, values, now, token) =>
        {
            var name = Username(values.GetRequiredValue(username));
            var display = DisplayName(values.GetValue(displayName)) ?? name;
            var hash = await ReadPasswordHashAsync(values.GetValue(passwordStdin), input, error, token);
            await MutateAsync(store, values, token, owner => owner.CreateInitialOwnerAsync(name, display, hash, now, token),
                "An owner already exists or the username is already in use.");
        }, username, displayName, passwordStdin, allowMigrate);

        Add("passwd", "Change the owner password and revoke browser sessions.", async (store, values, now, token) =>
        {
            var hash = await ReadPasswordHashAsync(values.GetValue(passwordStdin), input, error, token);
            await MutateAsync(store, values, token, owner => owner.RotateOwnerPasswordAsync(hash, now, token));
        }, passwordStdin, allowMigrate);

        Add("rename-owner", "Rename the owner and revoke browser sessions.", (store, values, now, token) =>
        {
            var name = Username(values.GetRequiredValue(username));
            var display = DisplayName(values.GetValue(displayName));
            return MutateAsync(store, values, token, owner => owner.RenameOwnerAsync(name, display, now, token));
        }, username, displayName, allowMigrate);

        foreach (var disabled in new[] { true, false })
        {
            Add(disabled ? "disable-owner" : "enable-owner", disabled ? "Disable owner sign-ins." : "Enable owner sign-ins.",
                (store, values, now, token) => MutateAsync(store, values, token, owner => owner.SetOwnerDisabledAsync(disabled, now, token)),
                allowMigrate);
        }

        Add("revoke-sessions", "Revoke every owner browser session.", (store, values, now, token) =>
            MutateAsync(store, values, token, owner => owner.RevokeAllSessionsAsync(now, token)), allowMigrate);
        Add("status", "Read the owner and session status as JSON.", async (store, _, now, token) =>
        {
            var status = await store.GetStatusAsync(now, token);
            await output.WriteLineAsync(JsonSerializer.Serialize(status, PergaJsonDefaults.ApiOptions));
        });

        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);

        void Add(string name, string description, Func<SqliteAuthAdministration, ParseResult, long, CancellationToken, Task> action,
            params Option[] options)
        {
            var command = new Command(name, description);
            foreach (var option in options)
                command.Options.Add(option);
            auth.Subcommands.Add(command);
            command.SetAction(async (parsed, token) =>
            {
                try
                {
                    var explicitPath = parsed.GetValue(dbPath);
                    var explicitDirectory = parsed.GetValue(dataDir);
                    if (explicitPath is not null && explicitDirectory is not null)
                        throw new ArgumentException("Use either --db-path or --data-dir.");
                    var path = explicitPath ?? (explicitDirectory is null
                        ? Environment.GetEnvironmentVariable("PERGA_DB_PATH")
                        : Path.Combine(explicitDirectory, "perga.db"));
                    if (path is null && Environment.GetEnvironmentVariable("PERGA_DATA_DIR") is { Length: > 0 } directory)
                        path = Path.Combine(directory, "perga.db");
                    if (string.IsNullOrWhiteSpace(path))
                        throw new ArgumentException("Set --db-path, --data-dir, PERGA_DB_PATH or PERGA_DATA_DIR.");

                    path = Path.GetFullPath(path);
                    var store = new SqliteAuthAdministration(path);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await action(store, parsed, now, token);
                    return 0;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
                                             SqliteException or UnauthorizedAccessException)
                {
                    await error.WriteLineAsync(ex.Message);
                    return 1;
                }
            });
        }

        async Task MutateAsync(SqliteAuthAdministration store, ParseResult parsed, CancellationToken token,
            Func<IAuthOwnerWriter, Task<bool>> mutate, string missing = "No owner is configured. Run auth init-owner first.")
        {
            await new SqliteMigrator().EnsureMigratedAsync(store.DatabasePath, parsed.GetValue(allowMigrate), token);
            if (!await mutate(store))
                throw new InvalidOperationException(missing);
        }
    }

    private static string Username(string value) =>
        AuthCredentials.NormalizeUsername(value) ?? throw new ArgumentException("Invalid owner username.");

    private static string? DisplayName(string? value)
    {
        if (value is null)
            return null;
        value = value.Trim();
        if (value.Length is < 1 or > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Display name must contain 1–128 characters and no control characters.");
        return value;
    }

    private static async Task<string> ReadPasswordHashAsync(bool fromStdin, TextReader input, TextWriter error, CancellationToken ct)
    {
        string? password;
        if (fromStdin)
        {
            password = await input.ReadLineAsync(ct);
        }
        else
        {
            if (Console.IsInputRedirected)
                throw new ArgumentException("Use --password-stdin to read a password from redirected input.");
            password = Prompt("Password (12+ characters)", error, ct);
            if (password != Prompt("Confirm password", error, ct))
                throw new ArgumentException("Passwords do not match.");
        }

        if (!AuthCredentials.IsValidPassword(password, 12))
            throw new ArgumentException("Password must contain 12–1024 Unicode characters, no NUL and at most 4096 UTF-8 bytes.");

        return AuthPasswordHasher.HashPassword(password!);
    }

    private static string Prompt(string label, TextWriter error, CancellationToken ct)
    {
        error.Write(label + ": ");
        var password = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                error.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && password.Length != 0)
            {
                password.Length--;
                error.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                if (password.Length == 2048)
                    throw new ArgumentException("Password is too long.");
                password.Append(key.KeyChar);
                error.Write('*');
            }
        }
    }
}
