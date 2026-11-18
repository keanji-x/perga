namespace Perga.Kernel;

public enum KernelErrorCode
{
    NotFound,
    InvalidArgument,
    Conflict,
    Forbidden,
    PolicyDenied,
    PreconditionFailed
}

public sealed class KernelException : Exception
{
    public KernelErrorCode Code { get; }
    public string? NextStep { get; }

    public KernelException(KernelErrorCode code, string message, string? nextStep = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        NextStep = nextStep;
    }

    public static KernelException NotFound(string message) =>
        new(KernelErrorCode.NotFound, message);

    public static KernelException InvalidArgument(string message) =>
        new(KernelErrorCode.InvalidArgument, message);

    public static KernelException Conflict(string message, string? nextStep = null) =>
        new(KernelErrorCode.Conflict, message, nextStep);

    public static KernelException Forbidden(string message, string? nextStep = null) =>
        new(KernelErrorCode.Forbidden, message, nextStep);

    public static KernelException PolicyDenied(string reason) =>
        new(KernelErrorCode.PolicyDenied, reason);

    public static KernelException PreconditionFailed(string message) =>
        new(KernelErrorCode.PreconditionFailed, message);
}
