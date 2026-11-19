namespace Perga.Kernel.Ports;

public interface IScheduleCronEvaluator
{
    bool TryValidate(string cronExpression, string timezone, out string normalizedTimezone, out string? error);
    long? GetNextOccurrenceMs(string cronExpression, string timezone, long afterMs);
}
