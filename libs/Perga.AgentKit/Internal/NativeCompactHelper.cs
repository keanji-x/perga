using System.Text;

namespace Perga.AgentKit.Internal;

internal static class NativeCompactHelper
{
    public static async Task<NativeCompactResult> DrainAsync(
        IAsyncEnumerable<UnifiedAgentEvent> events,
        string interruptedMessage,
        CancellationToken ct)
    {
        var summary = new StringBuilder();
        TokenUsage? usage = null;
        long? contextUsageRevision = 0;
        long? completedPromptId = null;

        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case UnifiedAgentEvent.TextDelta td:
                    summary.Append(td.Delta);
                    break;
                case UnifiedAgentEvent.TurnCompleted tc:
                    usage = tc.Usage;
                    contextUsageRevision = tc.ContextUsageRevision;
                    completedPromptId = tc.CompletedPromptId;
                    break;
                case UnifiedAgentEvent.TurnError te:
                    throw new InvalidOperationException(te.Message);
                case UnifiedAgentEvent.TurnInterrupted:
                    throw new OperationCanceledException(interruptedMessage, ct);
            }
        }

        var summaryText = summary.ToString().Trim();
        return new NativeCompactResult(usage, string.IsNullOrEmpty(summaryText) ? "Context compacted." : summaryText, contextUsageRevision, completedPromptId);
    }
}
