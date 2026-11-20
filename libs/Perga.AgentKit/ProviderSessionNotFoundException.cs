using System.Text.RegularExpressions;

namespace Perga.AgentKit;

/// <summary>The provider confirmed that the requested native session cannot be restored.</summary>
public sealed class ProviderSessionNotFoundException(string message, Exception? inner = null) : Exception(message, inner)
{
    internal static bool IsMissingSession(string message) => Regex.IsMatch(message,
        @"\b(thread|session|conversation)([_ ](id|not_found))?\b.{0,100}\b(not found|does not exist|doesn't exist|corrupt)\b|\b(invalid|corrupt) (thread|session|conversation)( id)?\b|\b(thread|session)_not_found\b|\bno (thread|session|conversation) found\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
