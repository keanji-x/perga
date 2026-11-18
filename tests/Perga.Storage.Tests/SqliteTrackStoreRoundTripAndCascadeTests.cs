using System.Text.Json.Nodes;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;
using Perga.VcsKit;

namespace Perga.Storage.Tests;

public sealed class SqliteTrackStoreRoundTripAndCascadeTests
{
    [Fact]
    public async Task WorkspaceRecoveryRetriesRepeatableAllocationAndPreservesUncertaintyAboutInitialization()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var ct = CancellationToken.None;
        await harness.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), ct);

        async Task SeedAsync(string id, WorkspacePreparationPhase phase, bool repeatable)
        {
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack(id, leadSessionId: $"lead_{id}");
            var preparation = new WorkspacePreparation(
                new WorkspaceProvisionRequest(id, "/src", "/workspaces", []), null, phase, RepeatableProvision: repeatable);
            await harness.Store.CreateTrackAsync(new TrackCreation(track, report, null, lead, [],
                null, preparation, []), null, ct);
        }

        await SeedAsync("trk_repeatable", WorkspacePreparationPhase.Provisioning, repeatable: true);
        await SeedAsync("trk_legacy", WorkspacePreparationPhase.Provisioning, repeatable: false);
        await SeedAsync("trk_hook", WorkspacePreparationPhase.Initializing, repeatable: true);
        await SeedAsync("trk_mount", WorkspacePreparationPhase.Mounting, repeatable: true);

        var recovered = await harness.Store.RecoverWorkspacePreparationsAsync(ct);
        Assert.Equal(new[] { "trk_mount", "trk_repeatable" }, recovered.Order());
        Assert.Equal(WorkspacePreparationPhase.Pending,
            (await harness.Store.GetWorkspacePreparationAsync("trk_repeatable", ct))!.Phase);
        Assert.Equal(WorkspacePreparationPhase.Unknown,
            (await harness.Store.GetWorkspacePreparationAsync("trk_legacy", ct))!.Phase);
        Assert.Equal(WorkspacePreparationPhase.Unknown,
            (await harness.Store.GetWorkspacePreparationAsync("trk_hook", ct))!.Phase);
        Assert.Equal(WorkspacePreparationPhase.Provisioned,
            (await harness.Store.GetWorkspacePreparationAsync("trk_mount", ct))!.Phase);
    }

    [Fact]
    public async Task AreasAndTracks_FullCrudAggregateSnapshotAndCascadeColdBlobCleanup_RoundTripsExact()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        // 1. Areas CRUD & ordering
        var areaB = StorageTestHarness.CreateArea("area_b", "Backend", sortOrder: 20.0, nowMs: 1_000);
        var areaA = StorageTestHarness.CreateArea("area_a", "Architecture", sortOrder: 10.0, nowMs: 1_100);
        await store.UpsertAreaAsync(areaB, ct);
        await store.UpsertAreaAsync(areaA, ct);

        var updatedAreaA = areaA with { Name = "Architecture & Core", UpdatedAtMs = 1_500 };
        var persistedAreaA = await store.UpsertAreaAsync(updatedAreaA, ct);
        Assert.Equal("Architecture & Core", persistedAreaA.Name);
        Assert.Equal(1_100, persistedAreaA.CreatedAtMs);
        Assert.Equal(1_500, persistedAreaA.UpdatedAtMs);

        var areas = await store.ListAreasAsync(ct);
        Assert.Equal(["area_a", "area_b"], areas.Select(static a => a.Id).ToArray());
        Assert.NotNull(await store.GetAreaAsync("area_a", ct));
        Assert.Null(await store.GetAreaAsync("area_missing", ct));

        // 2. Create parent & child tracks
        var (parentTrack, initialReport, leadSession) = StorageTestHarness.CreateSeedTrack("trk_parent", "area_a", "sess_lead");
        await store.CreateTrackAsync(parentTrack, initialReport, "export default defineTemplate({ id: 'dev' });", leadSession, ct);

        var (childTrackSeed, childReport, childLead) = StorageTestHarness.CreateSeedTrack("trk_child", "area_b", "sess_child_lead");
        var childTrack = childTrackSeed with { ParentTrackId = "trk_parent", ParentAttemptId = "att_spawn_1" };
        await store.CreateTrackAsync(childTrack, childReport, null, childLead, ct);

        // 3. Subagent sessions, Open/Answered Asks, UpdateTrackAndEmitAsync (leadSessionId switch & close filter)
        var freeSubagent = new AgentSessionDto(
            Id: "sess_sub_free",
            TrackId: "trk_parent",
            IsLead: false,
            BoundTaskId: null,
            BoundAttemptId: null,
            Title: "Helper",
            Provider: "claude",
            Model: "claude-sonnet-4",
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ContextUsageRatio: 0.35,
            ArchivedAtMs: null,
            CreatedAtMs: 2_100,
            UpdatedAtMs: 2_100);
        await store.UpsertSessionAsync(freeSubagent, StorageTestHarness.CreateEvent("session.updated", "trk_parent", "area_a", createdAtMs: 2_100), ct);

        Assert.Equal("trk_parent", await store.FindTrackIdBySessionAsync("sess_sub_free", ct));
        Assert.Null(await store.FindTrackIdBySessionAsync("sess_nonexistent", ct));

        var openAsk = new AskDto(
            Id: "ask_open",
            TrackId: "trk_parent",
            Status: AskStatus.Open,
            Questions: [new AskQuestionDto("Approve deploy?", ["Yes", "No"], IsMultiSelect: false)],
            Answer: null,
            CreatedAtMs: 2_200,
            AnsweredAtMs: null);
        var answeredAsk = new AskDto(
            Id: "ask_done",
            TrackId: "trk_parent",
            Status: AskStatus.Answered,
            Questions: [new AskQuestionDto("Pick env", ["staging", "prod"], IsMultiSelect: true)],
            Answer: StorageTestHarness.Json("""{"selected":["staging"]}"""),
            CreatedAtMs: 2_250,
            AnsweredAtMs: 2_300);
        await store.UpsertAskAsync(openAsk, StorageTestHarness.CreateEvent("ask.changed", "trk_parent", "area_a", createdAtMs: 2_200), ct);
        await store.UpsertAskAsync(answeredAsk, StorageTestHarness.CreateEvent("ask.changed", "trk_parent", "area_a", createdAtMs: 2_300), ct);

        // Switch LeadSessionId to sess_sub_free via UpdateTrackAndEmitAsync
        var switchedTrack = parentTrack with
        {
            Title = "Renamed Parent Track",
            LeadSessionId = "sess_sub_free",
            PinnedAtMs = 2_400,
            UpdatedAtMs = 2_400
        };
        await store.UpdateTrackAndEmitAsync(
            switchedTrack,
            StorageTestHarness.CreateEvent("track.updated", "trk_parent", "area_a", createdAtMs: 2_400),
            ct);

        var snapshot = await store.LoadTrackAggregateAsync("trk_parent", ct);
        Assert.NotNull(snapshot);
        Assert.Equal("Renamed Parent Track", snapshot.Track.Title);
        Assert.Equal("sess_sub_free", snapshot.Track.LeadSessionId);
        Assert.Equal("export default defineTemplate({ id: 'dev' });", snapshot.TemplateSnapshotSource);
        Assert.Equal(2, snapshot.Sessions.Count);
        Assert.False(snapshot.Sessions.Single(static s => s.Id == "sess_lead").IsLead);
        Assert.True(snapshot.Sessions.Single(static s => s.Id == "sess_sub_free").IsLead);
        var loadedOpenAsk = Assert.Single(snapshot.OpenAsks);
        Assert.Equal("ask_open", loadedOpenAsk.Id);
        Assert.Equal(AskStatus.Answered, (await store.GetAskAsync("ask_done", ct))?.Status);

        // Verify ListTracksAsync includeClosed filtering
        var closedChild = childTrack with { ClosedAtMs = 2_500, UpdatedAtMs = 2_500 };
        await store.UpdateTrackAndEmitAsync(closedChild, evt: null, ct);

        Assert.Single(await store.ListTracksAsync(includeClosed: false, ct: ct));
        Assert.Equal(2, (await store.ListTracksAsync(includeClosed: true, ct: ct)).Count);
        Assert.Empty(await store.ListTracksAsync(areaId: "area_b", includeClosed: false, ct: ct));
        Assert.Single(await store.ListTracksAsync(areaId: "area_b", includeClosed: true, ct: ct));

        // 4. Create a >64KB cold blob on trk_parent, then DeleteAreaAsync("area_a") -> cascades trk_parent,
        // cleans blobs/trk_parent, and sets trk_child.parent_track_id = NULL (ON DELETE SET NULL)!
        var largeTurn = new SessionTurnDto(
            Id: "turn_big",
            SessionId: "sess_lead",
            TurnIndex: 1,
            Epoch: 1,
            TriggerKind: "user",
            Input: StorageTestHarness.Json("""{"prompt":"big"}"""),
            Status: "completed",
            Usage: null,
            ErrorText: null,
            StartedAtMs: 2_600,
            CompletedAtMs: 2_650);
        var bigPayload = StorageTestHarness.Json($$"""{"output":"{{new string('X', 80_000)}}"}""");
        await store.RecordTurnAndItemsAsync(
            largeTurn,
            [new SessionItemDto(0, "sess_lead", "turn_big", "tool_result", bigPayload, 2_650)],
            updatedSession: null,
            ct);

        var parentBlobDir = Path.Combine(harness.DataDir, "blobs", "trk_parent");
        Assert.True(Directory.Exists(parentBlobDir));

        await store.DeleteAreaAsync("area_a", ct);

        Assert.Null(await store.GetTrackAsync("trk_parent", ct));
        Assert.Null(await store.LoadTrackAggregateAsync("trk_parent", ct));
        Assert.False(Directory.Exists(parentBlobDir), "DeleteAreaAsync must clean cold blob directories of cascaded tracks.");

        var survivingChild = await store.GetTrackAsync("trk_child", ct);
        Assert.NotNull(survivingChild);
        Assert.Null(survivingChild.ParentTrackId);
    }

    [Fact]
    public async Task SaveReportTasksAttemptsTurnsMailsTerminalsAndEvents_SyncsAndDeduplicatesCleanly()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);
        var (trackA, reportA, leadA) = StorageTestHarness.CreateSeedTrack("trk_a", "area_1", "sess_a");
        var (trackB, reportB, leadB) = StorageTestHarness.CreateSeedTrack("trk_b", "area_1", "sess_b");
        await store.CreateTrackAsync(trackA, reportA, null, leadA, ct);
        await store.CreateTrackAsync(trackB, reportB, null, leadB, ct);

        // 1. SaveReportAndTasksAsync with 2 tasks + Zstd revision blocks
        var task1 = new TaskDto(
            Id: "task_1",
            TrackId: "trk_a",
            BlockId: "b_t1",
            TaskKey: "build",
            Generation: 1,
            Kind: "subagent",
            Provider: "codex",
            Access: AccessMode.ReadWrite,
            Ready: true,
            DependsOn: [],
            GoalOrCmd: "Build project",
            Context: StorageTestHarness.Json("""{"target":"all"}"""),
            Acceptance: "0 warnings",
            Gate: new GateSpecDto([new GateStepDto("build", "dotnet build")], TimeoutSecs: 600),
            Status: TaskRunStatus.Pending,
            StatusDetail: null,
            SemanticVerdict: SemanticVerdict.None,
            SemanticNote: null,
            LatestAttemptId: null,
            CreatedAtMs: 3_000,
            UpdatedAtMs: 3_000);

        var task2 = task1 with
        {
            Id = "task_2",
            BlockId = "b_t2",
            TaskKey = "obsolete-lint",
            Access = AccessMode.ReadOnly,
            DependsOn = ["build"]
        };

        var rev2Blocks = new List<ReportBlock>
        {
            new("b_0001", 2, ReportBlockKind.Prose, "Overview", "# Overview\nUpdated.", null),
            new("b_t1", 1, ReportBlockKind.Task, "Tasks", null, StorageTestHarness.Json("""{"key":"build"}""")),
            new("b_t2", 1, ReportBlockKind.Task, "Tasks", null, StorageTestHarness.Json("""{"key":"obsolete-lint"}"""))
        };
        var rev2Doc = new ReportDocument(2, "Added two tasks", "<!-- lead -->", rev2Blocks);
        var rev2Zstd = SqliteJsonUpcaster.CompressReportBlocksZstd(rev2Blocks);

        await store.SaveReportAndTasksAsync(
            trackId: "trk_a",
            report: rev2Doc,
            compressedRevisionBlocks: rev2Zstd,
            authorSessionId: "sess_a",
            commitMessage: "Add build and lint tasks",
            syncedTasks: [task1, task2],
            nextTemplateState: StorageTestHarness.Json("""{"stage":"planned"}"""),
            evt: StorageTestHarness.CreateEvent("report.updated", "trk_a", "area_1", createdAtMs: 3_100),
            ct: ct);

        var decompressedRev2 = await store.GetReportRevisionBlocksAsync("trk_a", 2, ct);
        Assert.Equal(3, decompressedRev2.Count);
        Assert.Equal(ReportBlockKind.Task, decompressedRev2[1].Kind);
        Assert.Empty(await store.GetReportRevisionBlocksAsync("trk_a", 999, ct));

        // 2. Attach an attempt to task_1, then re-run SaveReportAndTasksAsync removing task_2 while keeping task_1!
        // Existing task_attempts on task_1 MUST survive (in-place ON CONFLICT DO UPDATE, not INSERT OR REPLACE!).
        var attempt1 = new TaskAttemptDto(
            AttemptId: "att_1",
            TaskId: "task_1",
            TrackId: "trk_a",
            SessionId: "sess_a",
            Status: "done",
            WorkerResult: StorageTestHarness.Json("""{"summary":"compiled"}"""),
            Meta: StorageTestHarness.Json("""{"exitCode":0}"""),
            WorkspaceLease: StorageTestHarness.Json("""{"driverId":"direct","effectiveCwd":"/workspace/trk_a"}"""),
            GateVerdict: StorageTestHarness.Json("""{"passed":true}"""),
            StartedAtMs: 3_200,
            FinishedAtMs: 3_300);

        var task1Done = task1 with
        {
            Status = TaskRunStatus.Done,
            SemanticVerdict = SemanticVerdict.Accepted,
            SemanticNote = "Clean build",
            LatestAttemptId = "att_1",
            UpdatedAtMs = 3_300
        };
        await store.UpsertTaskAndAttemptAsync(
            task1Done,
            attempt1,
            StorageTestHarness.Json("""{"stage":"built"}"""),
            StorageTestHarness.CreateEvent("task.updated", "trk_a", "area_1", createdAtMs: 3_300),
            ct);

        var rev3Blocks = rev2Blocks.Take(2).ToList();
        var rev3Doc = new ReportDocument(3, "Removed obsolete-lint", "<!-- lead -->", rev3Blocks);
        await store.SaveReportAndTasksAsync(
            trackId: "trk_a",
            report: rev3Doc,
            compressedRevisionBlocks: SqliteJsonUpcaster.CompressReportBlocksZstd(rev3Blocks),
            authorSessionId: "sess_a",
            commitMessage: "Drop obsolete task",
            syncedTasks: [task1Done],
            nextTemplateState: StorageTestHarness.Json("""{"stage":"trimmed"}"""),
            evt: StorageTestHarness.CreateEvent("report.updated", "trk_a", "area_1", createdAtMs: 3_400),
            ct: ct);

        var aggA = await store.LoadTrackAggregateAsync("trk_a", ct);
        Assert.NotNull(aggA);
        Assert.Equal(3, aggA.Report.DocRev);
        Assert.Equal("trimmed", aggA.Track.TemplateState.GetProperty("stage").GetString());
        var remainingTask = Assert.Single(aggA.Tasks);
        Assert.Equal("task_1", remainingTask.Id);
        Assert.Equal(SemanticVerdict.Accepted, remainingTask.SemanticVerdict);

        var attemptsOnTask1 = await store.ListAttemptsForTaskAsync("task_1", ct);
        Assert.Single(attemptsOnTask1);
        Assert.Equal("att_1", attemptsOnTask1[0].AttemptId);

        // 3. RecordTurnAndItemsAsync updates the same turn and paginates its items
        var turn1Running = new SessionTurnDto(
            Id: "turn_1",
            SessionId: "sess_a",
            TurnIndex: 1,
            Epoch: 1,
            TriggerKind: "user",
            Input: StorageTestHarness.Json("""{"prompt":"Hello"}"""),
            Status: "running",
            Usage: null,
            ErrorText: null,
            StartedAtMs: 4_000,
            CompletedAtMs: null);

        await store.RecordTurnAndItemsAsync(
            turn1Running,
            [new SessionItemDto(0, "sess_a", "turn_1", "user", StorageTestHarness.Json("""{"text":"Hello"}"""), 4_000)],
            updatedSession: leadA with { Status = "running", UpdatedAtMs = 4_000 },
            ct);

        // Same turn.Id completing in-place appends items and updates turn status
        var turn1Completed = turn1Running with
        {
            Status = "completed",
            Usage = StorageTestHarness.Json("""{"inputTokens":120,"outputTokens":45}"""),
            CompletedAtMs = 4_200
        };
        await store.RecordTurnAndItemsAsync(
            turn1Completed,
            [
                new SessionItemDto(0, "sess_a", "turn_1", "thinking", StorageTestHarness.Json("""{"text":"plan"}"""), 4_100),
                new SessionItemDto(0, "sess_a", "turn_1", "assistant_text", StorageTestHarness.Json("""{"text":"Done!"}"""), 4_200)
            ],
            updatedSession: leadA with { Status = "idle", ContextUsageRatio = 0.25, UpdatedAtMs = 4_200 },
            ct);

        Assert.Single(await store.ListSessionTurnsAsync("sess_a", ct));

        var page1 = await store.ListSessionItemsAsync("sess_a", afterId: 0, limit: 2, ct: ct);
        Assert.Equal(2, page1.Count);
        var page2 = await store.ListSessionItemsAsync("sess_a", afterId: page1[^1].Id, limit: 10, ct: ct);
        Assert.Single(page2);
        Assert.Equal("assistant_text", page2[0].Kind);

        // 4. Mails, Terminals, & Events replay
        var mail1 = new MailDto("mail_1", "trk_a", "trk_b", "First ping", "Body 1", ReadAtMs: null, CreatedAtMs: 5_000);
        var mail2 = new MailDto("mail_2", "trk_a", "trk_b", "Second ping", "Body 2", ReadAtMs: null, CreatedAtMs: 5_100);
        await store.UpsertMailAsync(mail1, StorageTestHarness.CreateEvent("mail.received", "trk_b", "area_1", createdAtMs: 5_000), ct);
        await store.UpsertMailAsync(mail2, StorageTestHarness.CreateEvent("mail.received", "trk_b", "area_1", createdAtMs: 5_100), ct);

        var allMailsB = await store.ListMailsAsync("trk_b", unreadOnly: false, ct: ct);
        Assert.Equal(["mail_2", "mail_1"], allMailsB.Select(static m => m.Id).ToArray());

        await store.MarkMailReadAsync("mail_2", 5_200, ct);
        var unreadMailsB = await store.ListMailsAsync("trk_b", unreadOnly: true, ct: ct);
        Assert.Equal(["mail_1"], unreadMailsB.Select(static m => m.Id).ToArray());

        var term = new TerminalDto("term_1", "trk_a", "att_1", "idem_term", "Build Shell", "/bin/bash", "/workspace/trk_a", TerminalStatus.Running, null, 6_000, null);
        await store.UpsertTerminalAsync(term, StorageTestHarness.CreateEvent("terminal.changed", "trk_a", "area_1", createdAtMs: 6_000), ct);
        await store.UpsertTerminalAsync(term with { Status = TerminalStatus.Exited, ExitCode = 0, ExitedAtMs = 6_500 }, evt: null, ct);

        var loadedTerm = Assert.Single(await store.ListTerminalsAsync("trk_a", ct));
        Assert.Equal(TerminalStatus.Exited, loadedTerm.Status);
        Assert.Equal(0, loadedTerm.ExitCode);

        var appendedEvt = await store.AppendEventWithIdAsync(
            StorageTestHarness.CreateEvent("custom.ping", "trk_a", "area_1", createdAtMs: 7_000),
            ct);
        Assert.NotNull(appendedEvt.EventId);

        var trkAEvents = await store.ListEventsSinceAsync(0, trackId: "trk_a", limit: 100, ct: ct);
        Assert.True(trkAEvents.Count >= 4);
        Assert.All(trkAEvents, static e => Assert.Equal("trk_a", e.TrackId));
    }

    [Fact]
    public async Task MarkInFlightInterruptedOnBootAsync_MarksMixedCaseAttemptsAndTurnsResetsSessionsAndExtractsLeases()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);

        var (openTrack1, rep1, lead1) = StorageTestHarness.CreateSeedTrack("trk_open_1", "area_1", "sess_open_1");
        var (openTrack2, rep2, lead2) = StorageTestHarness.CreateSeedTrack("trk_open_2", "area_1", "sess_open_2");
        var (closedTrack, repClosed, leadClosed) = StorageTestHarness.CreateSeedTrack("trk_closed", "area_1", "sess_closed", closedAtMs: 9_000);

        await store.CreateTrackAsync(openTrack1, rep1, null, lead1 with { Status = "Running" }, ct);
        await store.CreateTrackAsync(openTrack2, rep2, null, lead2, ct);
        await store.CreateTrackAsync(closedTrack, repClosed, null, leadClosed with { Status = "running" }, ct);

        // In-flight Verifying attempt with WorkspaceLease on openTrack2
        var taskOpen2 = new TaskDto(
            "t_open2", "trk_open_2", "b_1", "test", 1, "subagent", "codex", AccessMode.ReadWrite,
            true, [], "Run tests", null, null, null, TaskRunStatus.Verifying, null, SemanticVerdict.None, null, "att_verifying", 5_000, 5_000);
        var attemptOpen2 = new TaskAttemptDto(
            "att_verifying", "t_open2", "trk_open_2", null, "Verifying", null,
            StorageTestHarness.Json("{}"),
            StorageTestHarness.Json("""{"driverId":"vcs-workspace","attemptId":"att_verifying","effectiveCwd":"/tmp/lease1"}"""),
            null, 5_000, null);
        await store.UpsertTaskAndAttemptAsync(taskOpen2, attemptOpen2, null, null, ct);

        var legacyTask = taskOpen2 with { Id = "t_legacy", TaskKey = "legacy", Status = TaskRunStatus.Running, LatestAttemptId = "att_legacy" };
        var legacyAttempt = attemptOpen2 with { AttemptId = "att_legacy", TaskId = legacyTask.Id, Status = "interrupted" };
        await store.UpsertTaskAndAttemptAsync(legacyTask, legacyAttempt, null, null, ct);

        var pendingTask = taskOpen2 with { Id = "t_reserved", TaskKey = "reserved", Status = TaskRunStatus.Running, LatestAttemptId = "att_reserved" };
        var reservation = attemptOpen2 with { AttemptId = "att_reserved", TaskId = pendingTask.Id, Status = "running", WorkspaceLease = null };
        await store.UpsertTaskAndAttemptAsync(pendingTask, reservation, null, null, ct);

        // In-flight Running turn on openTrack1 and closedTrack
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_open1", "sess_open_1", 1, 1, "user", StorageTestHarness.Json("{}"), "Running", null, null, 5_100, null),
            [],
            updatedSession: null,
            ct);
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_closed", "sess_closed", 1, 1, "user", StorageTestHarness.Json("{}"), "running", null, null, 5_200, null),
            [],
            updatedSession: null,
            ct);

        var leasesBeforeMark = await store.ListInFlightAttemptLeasesAsync(ct);
        var leaseBefore = Assert.Single(leasesBeforeMark);
        Assert.Equal("att_verifying", leaseBefore.AttemptId);
        Assert.Equal("/tmp/lease1", leaseBefore.WorkspaceLease.GetProperty("effectiveCwd").GetString());

        var affectedTracks = await store.MarkInFlightInterruptedOnBootAsync(ct);
        Assert.Equal(["trk_closed", "trk_open_1", "trk_open_2"], affectedTracks.ToArray());

        Assert.Empty(await store.ListInFlightAttemptLeasesAsync(ct));

        var recoveredTrack = (await store.LoadTrackAggregateAsync(openTrack2.Id, ct))!;
        Assert.Equal(TaskRunStatus.Failed, Assert.Single(recoveredTrack.Tasks, task => task.Id == legacyTask.Id).Status);
        Assert.Equal(TaskRunStatus.Pending, Assert.Single(recoveredTrack.Tasks, task => task.Id == pendingTask.Id).Status);

        var updatedAttempts = await store.ListAttemptsForTaskAsync("t_open2", ct);
        Assert.Equal("interrupted", updatedAttempts[0].Status);
        Assert.NotNull(updatedAttempts[0].FinishedAtMs);

        var updatedTurnsOpen1 = await store.ListSessionTurnsAsync("sess_open_1", ct);
        Assert.Equal("interrupted", updatedTurnsOpen1[0].Status);
        Assert.NotNull(updatedTurnsOpen1[0].CompletedAtMs);

        var updatedTurnsClosed = await store.ListSessionTurnsAsync("sess_closed", ct);
        Assert.Equal("interrupted", updatedTurnsClosed[0].Status);

        var aggOpen1 = await store.LoadTrackAggregateAsync("trk_open_1", ct);
        Assert.Equal("idle", aggOpen1!.Sessions[0].Status);
    }

    [Fact]
    public void SqliteJsonUpcaster_UpcastsLegacyV0JsonObjectsToV1()
    {
        var upcaster = new SqliteJsonUpcaster();
        upcaster.RegisterMigration(
            "tracks.template_state_json",
            fromVersion: 0,
            static obj =>
            {
                if (!obj.ContainsKey("retryBudget"))
                {
                    obj["retryBudget"] = 3;
                }

                return obj;
            });

        var upcasted = upcaster.UpcastObjectOrElement("""{"phase":"init"}""", "tracks.template_state_json");
        Assert.Equal(1, upcasted.GetProperty("v").GetInt32());
        Assert.Equal("init", upcasted.GetProperty("phase").GetString());
        Assert.Equal(3, upcasted.GetProperty("retryBudget").GetInt32());
    }

    [Fact]
    public async Task DeleteTrackAsync_CascadesAllTenChildTablesWithoutTouchingSiblingTracks()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);

        foreach (var suffix in new[] { "del", "keep" })
        {
            var trackId = $"trk_{suffix}";
            var sessId = $"sess_{suffix}";
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack(trackId, "area_1", sessId);
            await store.CreateTrackAsync(track, report, "export default {};", lead, ct);

            var task = new TaskDto(
                $"task_{suffix}", trackId, "b_1", "k1", 1, "subagent", "codex", AccessMode.ReadWrite,
                true, [], "Goal", null, null, null, TaskRunStatus.Running, null, SemanticVerdict.None, null, $"att_{suffix}", 1_000, 1_000);
            var attempt = new TaskAttemptDto(
                $"att_{suffix}", $"task_{suffix}", trackId, sessId, "running", null, StorageTestHarness.Json("{}"), null, null, 1_000, null);
            await store.UpsertTaskAndAttemptAsync(task, attempt, null, null, ct);

            var turn = new SessionTurnDto(
                $"turn_{suffix}", sessId, 1, 1, "user", StorageTestHarness.Json("{}"), "completed", null, null, 1_000, 1_100);
            await store.RecordTurnAndItemsAsync(
                turn,
                [new SessionItemDto(0, sessId, $"turn_{suffix}", "user", StorageTestHarness.Json("{}"), 1_000)],
                updatedSession: null,
                ct);

            await store.UpsertAskAsync(
                new AskDto($"ask_{suffix}", trackId, AskStatus.Open, [new AskQuestionDto("Q?", ["A"])], null, 1_000, null),
                StorageTestHarness.CreateEvent("ask.changed", trackId),
                ct);
            await store.UpsertMailAsync(
                new MailDto($"mail_{suffix}", "trk_keep", trackId, "Hi", "Body", null, 1_000),
                StorageTestHarness.CreateEvent("mail.received", trackId),
                ct);
            await store.UpsertTerminalAsync(
                new TerminalDto($"term_{suffix}", trackId, null, null, "Term", "/bin/sh", "/tmp", TerminalStatus.Running, null, 1_000, null),
                null,
                ct);
        }

        await store.DeleteTrackAsync("trk_del", ct);

        using var conn = harness.OpenRawInspectConnection();
        (string Table, string FilterSql)[] checks =
        [
            ("tracks", "id = 'trk_del'"),
            ("reports", "track_id = 'trk_del'"),
            ("report_revisions", "track_id = 'trk_del'"),
            ("tasks", "track_id = 'trk_del'"),
            ("task_attempts", "track_id = 'trk_del'"),
            ("agent_sessions", "track_id = 'trk_del'"),
            ("session_turns", "session_id = 'sess_del'"),
            ("session_items", "session_id = 'sess_del'"),
            ("asks", "track_id = 'trk_del'"),
            ("mails", "to_track_id = 'trk_del'"),
            ("terminals", "track_id = 'trk_del'")
        ];

        foreach (var (table, filter) in checks)
        {
            var count = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(1) FROM {table} WHERE {filter};");
            Assert.Equal(0L, count);
        }

        Assert.NotNull(await store.LoadTrackAggregateAsync("trk_keep", ct));
        Assert.Single(await store.ListMailsAsync("trk_keep", ct: ct));
        Assert.Single(await store.ListTerminalsAsync("trk_keep", ct));
    }
}
