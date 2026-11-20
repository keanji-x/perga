namespace Perga.AgentKit;

public sealed record CommandInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
