namespace Perga.Kernel;

public enum KernelErrorCode
{
    NotFound,
    Gone,
    InvalidArgument,
    Conflict,
    Forbidden,
    PolicyDenied,
    PreconditionFailed,
    SessionRecoveryRequired,
    IdempotencyKeyInvalid,
    IdempotencyKeyReused,
    IdempotencyKeyExhausted,
    WorkerTurnChanged,
    SystemUpgradeLeadRequired,
    PayloadTooLarge,
    AttachmentsUnsupported,
    AttachmentUnavailable
}

public sealed class KernelException : Exception
{
    public KernelErrorCode Code { get; }
    public string? NextStep { get; }
    public string? Field { get; private init; }

    public KernelException(KernelErrorCode code, string message, string? nextStep = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        NextStep = nextStep;
    }

    public static KernelException NotFound(string message) =>
        new(KernelErrorCode.NotFound, message);

    public static KernelException Gone(string message) =>
        new(KernelErrorCode.Gone, message);

    public static KernelException InvalidArgument(string message, string? nextStep = null, string? field = null) =>
        new(KernelErrorCode.InvalidArgument, message, nextStep) { Field = field };

    public static KernelException Conflict(string message, string? nextStep = null) =>
        new(KernelErrorCode.Conflict, message, nextStep);

    public static KernelException Forbidden(string message, string? nextStep = null) =>
        new(KernelErrorCode.Forbidden, message, nextStep);

    public static KernelException PolicyDenied(string reason) =>
        new(KernelErrorCode.PolicyDenied, reason);

    public static KernelException PreconditionFailed(string message, string? nextStep = null) =>
        new(KernelErrorCode.PreconditionFailed, message, nextStep);
}
