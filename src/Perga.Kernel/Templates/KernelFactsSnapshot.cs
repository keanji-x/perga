using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Templates;

internal static class KernelFactsSnapshot
{
    internal static JsonElement BuildKernelFactsSnapshot(
        string trackId,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyDictionary<string, AgentSessionDto> sessions,
        FlowProjectionDto flowProjection)
    {
        var tasksMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sessionsMap = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var session in sessions.Values)
        {
            sessionsMap[session.Id] = new
            {
                id = session.Id,
                isLead = session.IsLead,
                provider = session.Provider,
                model = session.Model,
                status = session.Status,
            };
        }

        foreach (var task in tasks)
        {
            var boundSession = task.SessionId is { } sid && sessions.TryGetValue(sid, out var session) ? session : null;
            var gateChecks = new List<object>();
            if (task.GateVerdict is { ValueKind: JsonValueKind.Object } gv &&
                gv.TryGetProperty("steps", out var stepsEl) &&
                stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in stepsEl.EnumerateArray())
                {
                    var name = step.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : "gate";
                    var exitCode = step.TryGetProperty("exitCode", out var ecEl) && ecEl.TryGetInt32(out var ec) ? ec : 0;
                    var passed = step.TryGetProperty("passed", out var pEl) && pEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? pEl.GetBoolean()
                        : exitCode == 0;
                    var durationMs = step.TryGetProperty("durationMs", out var dEl) && dEl.TryGetInt64(out var dm) ? dm : 0;
                    var cmd = step.TryGetProperty("cmd", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
                    var tail = step.TryGetProperty("outputTail", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() : null;
                    var stdout = step.TryGetProperty("stdout", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                    gateChecks.Add(new
                    {
                        label = name ?? "gate",
                        status = passed ? "pass" : "fail",
                        detail = durationMs > 0 ? $"{durationMs} ms" : null,
                        command = cmd,
                        output = string.IsNullOrWhiteSpace(tail) ? stdout : tail,
                    });
                }
            }

            var normalizedStatus = task.Status switch
            {
                TaskRunStatus.Done => "done",
                TaskRunStatus.Running or TaskRunStatus.Verifying => "running",
                TaskRunStatus.Failed or TaskRunStatus.Canceled => "blocked",
                _ => "pending"
            };

            tasksMap[task.TaskKey] = new
            {
                id = task.Id,
                taskKey = task.TaskKey,
                status = normalizedStatus,
                provider = boundSession?.Provider ?? task.Provider,
                model = boundSession?.Model,
                sessionId = boundSession?.Id,
                gates = gateChecks,
                updatedAtMs = task.UpdatedAtMs,
            };
        }

        return JsonHelpers.ToElement(new
        {
            trackId,
            tasks = tasksMap,
            sessions = sessionsMap,
            flow = flowProjection,
        });
    }
}
