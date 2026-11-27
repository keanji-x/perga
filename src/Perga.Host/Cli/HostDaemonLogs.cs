namespace Perga.Host.Cli;

internal static class HostDaemonLogs
{
    public static async Task<int> RunAsync(string path, TextWriter output, int count, bool follow, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            await output.WriteLineAsync($"No Perga log yet at {path}.").ConfigureAwait(false);
            return 0;
        }

        if (!follow)
        {
            await WriteTailAsync(path, output, count, 0, ct).ConfigureAwait(false);
            return 0;
        }

        var fileName = Path.GetFileName(path);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, fileName)
        {
            NotifyFilter = NotifyFilters.FileName
        };
        var replaced = 0;
        watcher.Created += (_, _) => Interlocked.Exchange(ref replaced, 1);
        watcher.Renamed += (_, change) =>
        {
            // A rename away leaves the old reader available until its replacement arrives.
            if (change.Name == fileName)
                Interlocked.Exchange(ref replaced, 1);
        };
        watcher.EnableRaisingEvents = true;

        var reader = Open(path);
        try
        {
            if (reader is not null)
                await WriteTailAsync(reader, output, count, ct).ConfigureAwait(false);

            while (true)
            {
                if (Interlocked.Exchange(ref replaced, 0) != 0 && reader is not null)
                {
                    await CopyLinesAsync(reader, output, ct).ConfigureAwait(false);
                    reader.Dispose();
                    reader = null;
                }
                reader ??= Open(path);
                if (reader is not null)
                {
                    if (reader.BaseStream.Length < reader.BaseStream.Position)
                    {
                        reader.DiscardBufferedData();
                        reader.BaseStream.Position = 0;
                    }
                    await CopyLinesAsync(reader, output, ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            reader?.Dispose();
        }
    }

    public static async Task WriteTailAsync(string path, TextWriter output, int count, long offset, CancellationToken ct)
    {
        using var reader = Open(path);
        if (reader is null)
            return;
        reader.BaseStream.Position = Math.Min(reader.BaseStream.Length, offset);
        await WriteTailAsync(reader, output, count, ct).ConfigureAwait(false);
    }

    private static StreamReader? Open(string path)
    {
        try
        {
            return new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task WriteTailAsync(StreamReader reader, TextWriter output, int count, CancellationToken ct)
    {
        var tail = new Queue<string>();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (count == 0)
                continue;
            if (tail.Count == count)
                tail.Dequeue();
            tail.Enqueue(line);
        }

        foreach (var line in tail)
            await output.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static async Task CopyLinesAsync(StreamReader reader, TextWriter output, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            await output.WriteLineAsync(line).ConfigureAwait(false);
    }
}
