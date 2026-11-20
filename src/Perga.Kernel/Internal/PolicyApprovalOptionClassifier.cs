using Perga.Contracts;

namespace Perga.Kernel.Internal;

/// <summary>
/// Converts policy-declared option labels into structured <see cref="ApprovalChoiceDto"/> entries once at registration time.
/// </summary>
public static class PolicyApprovalOptionClassifier
{
    private static readonly string[] ChineseNegativePrefixes =
    [
        "拒绝", "不同意", "不允许", "不批准", "取消", "禁止", "中止", "阻止"
    ];

    private static readonly HashSet<string> NegativeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "deny", "denied", "reject", "rejected", "no", "false", "cancel", "canceled", "cancelled",
        "block", "abort", "forbid", "forbidden", "disapprove",
        "拒绝", "不同意", "取消", "禁止", "否", "不", "不允许", "不批准", "中止", "阻止"
    };

    private static readonly HashSet<string> PositiveTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "allow", "allowed", "approve", "approved", "yes", "true", "ok", "confirm", "confirmed",
        "proceed", "continue", "accept", "accepted",
        "同意", "允许", "批准", "继续", "是", "确认", "通过", "接受", "是的"
    };

    public static IReadOnlyList<ApprovalChoiceDto> DefaultBinaryChoices { get; } =
    [
        new("approve", "Approve", true),
        new("deny", "Deny", false)
    ];

    public static IReadOnlyList<ApprovalChoiceDto> BuildChoices(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return DefaultBinaryChoices;
        }

        var result = new ApprovalChoiceDto[options.Count];
        for (var i = 0; i < options.Count; i++)
        {
            var label = options[i].Trim();
            result[i] = new ApprovalChoiceDto($"option_{i}", label, IsOptionApproved(i, label, options.Count));
        }

        return result;
    }

    private static bool IsOptionApproved(int index, string label, int totalCount)
    {
        var stripped = StripRecommendedPrefix(label);
        if (MatchesNegative(stripped) || MatchesNegative(label))
        {
            return false;
        }

        if (PositiveTokens.Contains(stripped))
        {
            return true;
        }

        return totalCount >= 2 && index == 0;
    }

    private static string StripRecommendedPrefix(string input)
    {
        const string recPrefix = "(Recommended)";
        return input.StartsWith(recPrefix, StringComparison.OrdinalIgnoreCase)
            ? input[recPrefix.Length..].Trim()
            : input;
    }

    private static bool MatchesNegative(string text)
    {
        if (NegativeTokens.Contains(text))
        {
            return true;
        }

        foreach (var tok in NegativeTokens)
        {
            if (text.StartsWith(tok + " ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + ":", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + "，", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + "。", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in ChineseNegativePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
