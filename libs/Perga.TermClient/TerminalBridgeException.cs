namespace Perga.TermClient;

/// <summary>
/// Exception thrown when <c>perga-term-bridge</c> returns a JSON-RPC error or encounters a protocol failure.
/// </summary>
public sealed class TerminalBridgeException : Exception
{
    public int? ErrorCode { get; }

    public TerminalBridgeException(string message, int? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
