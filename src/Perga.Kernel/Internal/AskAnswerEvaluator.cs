using System.Text.Json;

namespace Perga.Kernel.Internal;

internal static class AskAnswerEvaluator
{
    private static readonly HashSet<string> NegativeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "deny",
        "denied",
        "reject",
        "rejected",
        "no",
        "false",
        "cancel",
        "canceled",
        "cancelled",
        "block",
        "abort"
    };

    private static readonly HashSet<string> PositiveTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "allow",
        "allowed",
        "approve",
        "approved",
        "yes",
        "true",
        "ok",
        "confirm",
        "proceed",
        "accept"
    };

    public static bool IsApproved(JsonElement answer, IReadOnlyList<string> options)
    {
        if (answer.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (answer.ValueKind == JsonValueKind.False || answer.ValueKind == JsonValueKind.Null || answer.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (answer.ValueKind == JsonValueKind.Object)
        {
            if (JsonHelpers.TryGetPropertyIgnoreCase(answer, ["approved", "allow"], out var approvedProp))
            {
                if (approvedProp.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (approvedProp.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }

            if (JsonHelpers.TryGetPropertyIgnoreCase(answer, ["answers", "answer", "selected", "choice", "value"], out var nested))
            {
                return IsApproved(nested, options);
            }
        }

        if (answer.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in answer.EnumerateArray())
            {
                return IsApproved(elem, options);
            }

            return false;
        }

        if (answer.ValueKind == JsonValueKind.String)
        {
            var text = (answer.GetString() ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            if (NegativeTokens.Contains(text))
            {
                return false;
            }

            if (PositiveTokens.Contains(text))
            {
                return true;
            }

            foreach (var neg in NegativeTokens)
            {
                if (text.StartsWith(neg + " ", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals(neg, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (options.Count >= 2)
            {
                if (string.Equals(text, options[0], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(text, options[^1], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
