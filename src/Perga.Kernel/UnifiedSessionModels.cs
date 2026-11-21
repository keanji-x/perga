using System.Text.Json;
using System.Text.Json.Serialization;
using Perga.Contracts;

namespace Perga.Kernel;

[JsonConverter(typeof(OptionalStringConverter))]
public readonly record struct Optional<T>(T Value)
{
    public bool IsSet { get; } = true;
}

public sealed class OptionalStringConverter : JsonConverter<Optional<string?>>
{
    public override bool HandleNull => true;
    public override Optional<string?> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString());
    public override void Write(Utf8JsonWriter writer, Optional<string?> value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

public sealed record SessionListFilter(bool IncludeArchived = false, string? ParentSessionId = null);
public sealed record SessionReadQuery(long AfterId = 0, long? BeforeId = null, int Limit = 20,
    bool Latest = false, string? ActivityTurnId = null, long? AfterSequence = null, int WaitMs = 0);
public sealed record SessionTaskRequest(string? Access = null, string? Acceptance = null, string? Gate = null,
    IReadOnlyList<string>? DependsOn = null);
public sealed record UpdateSessionRequest(long? ExpectedConfigVersion = null, string? Title = null, bool? Archived = null,
    string? Provider = null, Optional<string?> Model = default, Optional<string?> ReasoningEffort = default,
    Optional<string?> PermissionMode = default, string? ExpectedAttemptId = null);
public sealed record SendSessionMessageRequest(string? Content = null, string? RequestKey = null, string Mode = "default",
    string? TurnId = null, string? MessageId = null, string? ExpectedAttemptId = null);
public sealed record SessionMessageReceipt(string MessageId, string SessionId, string? TurnId, string Delivery, bool Replay = false);
public sealed record SessionControlRequest(string Action, string? TurnId = null, int? ExpectedEpoch = null,
    string? ExpectedAttemptId = null);
public sealed record RespondInteractionRequest(string? ExecutionId = null, string? OptionId = null, JsonElement? Answers = null);
public sealed record SessionInteractionResult(string Id, string Kind);
public sealed record SessionActivityDto(string? TurnId, long Sequence, bool Reset, IReadOnlyList<WorkerActivityEvent> Events,
    string Text, bool Truncated);
public sealed record SessionSummaryDto(AgentSessionDto Session, string State, string? ReadonlyReason,
    bool WorkerPaused, SessionTurnDto? LastTurn, int PendingInteractionCount);

public sealed record SessionTaskBindingDto(TaskDto Task, string? AttemptId, string AttemptPhase, string? WaitingReason);
public sealed record SessionCapabilitiesDto(IReadOnlyList<Perga.AgentKit.WorkerModelOption> Models,
    string? DefaultModel, string? DefaultReasoningEffort, IReadOnlyList<Perga.AgentKit.PermissionModeOption> PermissionModes,
    bool SupportsNativeSteer, bool SupportsSessionResume, bool SupportsCompact);
public sealed record PendingInteractionDto(string Id, string Kind, string SessionId, string? TurnId,
    string? ExecutionId, string Title, IReadOnlyList<ApprovalChoiceDto>? Options,
    IReadOnlyList<AskQuestionDto>? Questions, bool CanRespond, long CreatedAtMs, string? ToolName = null, JsonElement? Arguments = null);
