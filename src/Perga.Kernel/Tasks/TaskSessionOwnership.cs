using Perga.Contracts;
using Perga.Kernel.Flow;

namespace Perga.Kernel.Tasks;

public static class TaskSessionOwnership
{
    public static bool UsesBorrowedSession(TaskDto task) =>
        FlowConstants.IsFlowTask(task) && (task.Kind == "flow_wake" || FlowConstants.IsFlowSessionReuse(task));

    public static bool OwnsSessionLifecycle(TaskDto task) =>
        task.SessionId is not null && !UsesBorrowedSession(task);
}
