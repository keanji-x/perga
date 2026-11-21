using System.Text.Json;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

internal sealed class StorageTestHarness : IAsyncDisposable
{
    public string TempRoot { get; }
    public string DbPath { get; }
    public string DataDir { get; }
    public SqliteTrackStore Store { get; }

    private StorageTestHarness(string tempRoot, string dbPath, string dataDir, SqliteTrackStore store)
    {
        TempRoot = tempRoot;
        DbPath = dbPath;
        DataDir = dataDir;
        Store = store;
    }

    public static async Task<StorageTestHarness> CreateAsync(
        Func<SqliteStorageOptions, SqliteStorageOptions>? configure = null,
        CancellationToken ct = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"perga-sqlite-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var dbPath = Path.Combine(tempRoot, "perga.db");
        var dataDir = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(dataDir);

        var options = new SqliteStorageOptions
        {
            DbPath = dbPath,
            DataDir = dataDir
        };

        if (configure is not null)
        {
            options = configure(options);
        }

        var store = new SqliteTrackStore(options);
        await store.InitializeAsync(ct);

        return new StorageTestHarness(tempRoot, dbPath, dataDir, store);
    }

    public SqliteConnection OpenRawInspectConnection()
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    public static JsonElement Json(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }

    public static AreaDto CreateArea(string id = "area_1", string name = "Core", double sortOrder = 1.0, long nowMs = 1_000) =>
        new(
            Id: id,
            Name: name,
            Color: "#3b82f6",
            DefaultCwd: "/workspace/core",
            Folders: ["/workspace/core", "/workspace/shared"],
            SortOrder: sortOrder,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);

    public static (TrackDto Track, ReportDocument Report, AgentSessionDto LeadSession) CreateSeedTrack(
        string trackId = "trk_1",
        string areaId = "area_1",
        string leadSessionId = "sess_lead_1",
        long nowMs = 2_000,
        long? closedAtMs = null)
    {
        var track = new TrackDto(
            Id: trackId,
            AreaId: areaId,
            ParentTrackId: null,
            ParentAttemptId: null,
            Title: $"Track {trackId}",
            Cwd: $"/workspace/{trackId}",
            WorkspaceDriver: "direct",
            WorkspaceSpec: Json("""{"driverId":"direct","mergedCwd":"/workspace"}"""),
            LeadSessionId: leadSessionId,
            TemplateId: "dev",
            TemplateInputs: Json("""{"repo":"perga"}"""),
            TemplateState: Json("""{"stage":"init"}"""),
            EnabledPlugins: ["market", "research-archive"],
            ClosedAtMs: closedAtMs,
            PinnedAtMs: null,
            SortOrder: 10.0,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);

        var report = new ReportDocument(
            DocRev: 1,
            Summary: "Initial track report",
            LeadingComment: "<!-- header -->",
            Blocks:
            [
                new ReportBlock(
                    Id: "b_0001",
                    Rev: 1,
                    Kind: ReportBlockKind.Prose,
                    SectionH1: "Overview",
                    Markdown: "# Overview\nInitial goals.",
                    Payload: null)
            ]);

        var leadSession = new AgentSessionDto(
            Id: leadSessionId,
            TrackId: trackId,
            IsLead: true,
            Title: "Lead",
            Provider: "codex",
            Model: "gpt-5",
            NativeThreadId: "th_lead_1",
            Epoch: 1,
            Status: "idle",
            ArchivedAtMs: null,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);

        return (track, report, leadSession);
    }

    public static EventEnvelopeDto CreateEvent(
        string kind,
        string? trackId = "trk_1",
        string? areaId = "area_1",
        string payloadJson = """{"ok":true}""",
        long createdAtMs = 3_000) =>
        new(
            EventId: null,
            AreaId: areaId,
            TrackId: trackId,
            Kind: kind,
            Payload: Json(payloadJson),
            CreatedAtMs: createdAtMs);

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort temp directory cleanup
        }
    }
}
