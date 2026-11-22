using System.Text.Json;

namespace Perga.Contracts;

public sealed record AgentIdleActivityDto(long Since, long LastEventAt);
public sealed record AgentTurnOutcomeDto(string Kind, string? Failure = null, string? Reason = null,
    int? Status = null, string? Credential = null, string? ResetsAt = null, string? RateLimitType = null,
    JsonElement? StructuredOutput = null);
public sealed record AgentStopEvidenceDto(IReadOnlyList<string> PendingAbortIds, bool AbortedOnExit);
public sealed record AgentCompactionDto(string RequestId, bool? Started = null);
public sealed record AgentPendingPromptDto(string InputId, long SinceSeq, string? RequestId = null,
    AgentStopEvidenceDto? Stop = null);
public sealed record AgentRuntimeStatusDto(string Kind, string? Phase = null, string? ToolName = null,
    string? ToolCallId = null, bool? Writing = null, long? SinceSeq = null, string? RequestId = null,
    string? InputId = null, bool? Released = null, AgentTurnOutcomeDto? LastTurnOutcome = null,
    AgentIdleActivityDto? IdleActivity = null, AgentPendingPromptDto? PendingPrompt = null,
    AgentStopEvidenceDto? Stop = null, AgentCompactionDto? Compaction = null);

public sealed record AgentRuntimeTaskDto(string TaskId, string? TaskType = null, string? NativeType = null,
    string? Description = null, string? ToolCallId = null, string? ChildSessionId = null,
    bool? Background = null, bool? Ambient = null, string? Status = null, string? Error = null,
    string? Summary = null, string? OutputFile = null, long? Seq = null, long? Ts = null);
