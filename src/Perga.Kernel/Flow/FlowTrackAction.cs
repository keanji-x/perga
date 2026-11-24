using Perga.Contracts;

namespace Perga.Kernel.Flow;

internal static class FlowTrackAction
{
    internal static FlowDoSpecDto Interpolate(FlowNodeSpecDto node, Func<string?, string?> resolve)
    {
        var action = node.Do;
        var title = resolve(action.Title);
        return action with
        {
            Title = string.IsNullOrWhiteSpace(title) ? resolve(node.Title) : title,
            Prompt = resolve(action.Prompt),
            Area = resolve(action.Area),
            Template = resolve(action.Template),
            Cwd = resolve(action.Cwd),
            WorkspaceDriver = resolve(action.WorkspaceDriver),
            ExecutionTargetId = resolve(action.ExecutionTargetId),
            Provider = resolve(action.Provider),
            Model = resolve(action.Model),
            ReasoningEffort = resolve(action.ReasoningEffort),
            PermissionMode = resolve(action.PermissionMode)
        };
    }
}
