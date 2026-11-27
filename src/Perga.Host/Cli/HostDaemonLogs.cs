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

        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName
        };
        var changed = 0;
        void OnChanged(object _, FileSystemEventArgs __) => Interlocked.Exchange(ref changed, 1);
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.Error += (_, _) => Interlocked.Exchange(ref changed, 1);
        watcher.EnableRaisingEvents = true;

        var reader = Open(path);
        var checkpoint = new byte[32];
        var sample = new byte[checkpoint.Length];
        var checkpointLength = 0;
        void SaveCheckpoint() => checkpointLength = ReadCheckpoint(reader!, checkpoint);
        try
        {
            if (reader is not null)
                await WriteTailAsync(reader, output, count, ct, SaveCheckpoint).ConfigureAwait(false);

            while (true)
            {
                if (Interlocked.Exchange(ref changed, 0) != 0 || reader is null)
                {
                    // Drain the old file before switching to a newly created log.
                    if (reader is not null)
                    {
                        await CopyLinesAsync(reader, output, ct).ConfigureAwait(false);
                        reader.Dispose();
                    }
                    reader = Open(path);
                    checkpointLength = 0;
                }
                else if (!MatchesCheckpoint(reader, checkpoint.AsSpan(0, checkpointLength), sample))
                {
                    reader.DiscardBufferedData();
                    reader.BaseStream.Position = 0;
                }

                if (reader is not null)
                    await CopyLinesAsync(reader, output, ct, SaveCheckpoint).ConfigureAwait(false);
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

    private static async Task WriteTailAsync(
        StreamReader reader, TextWriter output, int count, CancellationToken ct, Action? beforeOutput = null)
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

        beforeOutput?.Invoke();
        foreach (var line in tail)
            await output.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static async Task CopyLinesAsync(
        StreamReader reader, TextWriter output, CancellationToken ct, Action? onNewBuffer = null)
    {
        var position = reader.BaseStream.Position;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (onNewBuffer is not null && position != reader.BaseStream.Position)
            {
                position = reader.BaseStream.Position;
                onNewBuffer();
            }
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static bool MatchesCheckpoint(StreamReader reader, ReadOnlySpan<byte> checkpoint, Span<byte> sample) =>
        reader.BaseStream.Length >= reader.BaseStream.Position &&
        ReadCheckpoint(reader, sample[..checkpoint.Length]) == checkpoint.Length &&
        sample[..checkpoint.Length].SequenceEqual(checkpoint);

    // A log truncated and rewritten between polls can already be larger than the old offset.
    private static int ReadCheckpoint(StreamReader reader, Span<byte> sample)
    {
        var stream = (FileStream)reader.BaseStream;
        var size = (int)Math.Min(sample.Length, stream.Position);
        return RandomAccess.Read(stream.SafeFileHandle, sample[..size], stream.Position - size);
    }
}
