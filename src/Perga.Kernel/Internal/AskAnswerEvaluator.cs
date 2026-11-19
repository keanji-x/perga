using System.Text.Json;

namespace Perga.Kernel.Internal;

public static class AskAnswerEvaluator
{
    private static readonly string[] ChineseNegativePrefixes =
    [
        "拒绝",
        "不同意",
        "不允许",
        "不批准",
        "取消",
        "禁止",
        "中止",
        "阻止"
    ];

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
        "abort",
        "forbid",
        "forbidden",
        "disapprove",
        "拒绝",
        "不同意",
        "取消",
        "禁止",
        "否",
        "不",
        "不允许",
        "不批准",
        "中止",
        "阻止"
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
        "confirmed",
        "proceed",
        "continue",
        "accept",
        "accepted",
        "同意",
        "允许",
        "批准",
        "继续",
        "是",
        "确认",
        "通过",
        "接受",
        "是的"
    };

    public static bool TryGetSelectedOptionIndex(
        JsonElement answer,
        IReadOnlyList<string> options,
        out int selectedIndex)
    {
        selectedIndex = -1;
        if (options.Count == 0)
        {
            return false;
        }

        switch (answer.ValueKind)
        {
            case JsonValueKind.Number:
                if (answer.TryGetInt32(out var idx) && idx >= 0 && idx < options.Count)
                {
                    selectedIndex = idx;
                    return true;
                }
                return false;

            case JsonValueKind.String:
                var raw = (answer.GetString() ?? string.Empty).Trim();
                if (raw.Length == 0)
                {
                    return false;
                }
                for (var i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i].Trim(), raw, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        return true;
                    }
                }
                return false;

            case JsonValueKind.Array:
                foreach (var elem in answer.EnumerateArray())
                {
                    return TryGetSelectedOptionIndex(elem, options, out selectedIndex);
                }
                return false;

            case JsonValueKind.Object:
                int? index = null;
                string? echoText = null;
                foreach (var property in answer.EnumerateObject())
                {
                    var name = property.Name.ToLowerInvariant();
                    switch (name)
                    {
                        case "option" or "optionindex" or "index":
                            if (index is not null || !TryReadIndex(property.Value, out var parsed))
                            {
                                return false;
                            }
                            index = parsed;
                            break;
                        case "value" or "text" or "label":
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                echoText = property.Value.GetString();
                            }
                            break;
                        case "answers" or "answer" or "selected" or "choice":
                            return answer.EnumerateObject().Count() == 1 &&
                                   TryGetSelectedOptionIndex(property.Value, options, out selectedIndex);
                    }
                }

                if (index is { } chosen && chosen >= 0 && chosen < options.Count)
                {
                    if (echoText is not null &&
                        !string.Equals(echoText.Trim(), options[chosen].Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    selectedIndex = chosen;
                    return true;
                }

                if (echoText is not null)
                {
                    for (var i = 0; i < options.Count; i++)
                    {
                        if (string.Equals(options[i].Trim(), echoText.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            return true;
                        }
                    }
                }
                return false;

            default:
                return false;
        }
    }

    public static bool IsApproved(JsonElement answer, IReadOnlyList<string> options)
    {
        if (answer.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (answer.ValueKind is JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (answer.ValueKind == JsonValueKind.Number)
        {
            if (answer.TryGetInt32(out var optIdx) && optIdx >= 0 && optIdx < options.Count)
            {
                return IsOptionIndexApproved(optIdx, options);
            }

            return false;
        }

        if (answer.ValueKind == JsonValueKind.Object)
            return IsObjectApproved(answer, options);

        if (answer.ValueKind == JsonValueKind.Array)
        {
            var sawAny = false;
            foreach (var elem in answer.EnumerateArray())
            {
                sawAny = true;
                if (!IsApproved(elem, options))
                {
                    return false;
                }
            }

            return sawAny;
        }

        if (answer.ValueKind == JsonValueKind.String)
        {
            var text = (answer.GetString() ?? string.Empty).Trim();
            return EvaluateText(text);
        }

        return false;
    }

    private static bool IsObjectApproved(JsonElement answer, IReadOnlyList<string> options)
    {
        int? index = null;
        JsonElement? echo = null;
        string? echoName = null;
        foreach (var property in answer.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            switch (name)
            {
                case "approved" or "allow":
                    return answer.EnumerateObject().Count() == 1 && property.Value.ValueKind == JsonValueKind.True;
                case "option" or "optionindex" or "index":
                    if (index is not null || !TryReadIndex(property.Value, out var parsed))
                        return false;
                    index = parsed;
                    break;
                case "value" or "text" or "label":
                    if (echo is { } previous)
                    {
                        // Older browser clients send the same free-text answer in both fields.
                        if (answer.EnumerateObject().Count() != 2 ||
                            (echoName, name) is not (("text", "value") or ("value", "text")) ||
                            previous.ValueKind != JsonValueKind.String ||
                            property.Value.ValueKind != JsonValueKind.String ||
                            previous.GetString() != property.Value.GetString())
                            return false;
                        break;
                    }
                    echo = property.Value;
                    echoName = name;
                    break;
                case "answers" or "answer" or "selected" or "choice":
                    return answer.EnumerateObject().Count() == 1 && IsApproved(property.Value, options);
                default:
                    return false;
            }
        }

        if (index is not { } selected)
            return echo is { } text && IsApproved(text, options);
        if (!IsOptionIndexApproved(selected, options))
            return false;

        return echo is null || (echo.Value.ValueKind == JsonValueKind.String &&
            string.Equals(echo.Value.GetString(), options[selected], StringComparison.Ordinal));
    }

    private static bool TryReadIndex(JsonElement value, out int index)
    {
        index = -1;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out index),
            JsonValueKind.String => int.TryParse(value.GetString(), out index),
            _ => false
        };
    }

    private static bool IsOptionIndexApproved(int index, IReadOnlyList<string> options)
    {
        if (index < 0 || index >= options.Count)
        {
            return false;
        }

        var label = options[index].Trim();
        var stripped = StripRecommendedPrefix(label);

        if (MatchesNegative(stripped) || MatchesNegative(label))
        {
            return false;
        }

        if (PositiveTokens.Contains(stripped))
        {
            return true;
        }

        if (options.Count >= 2)
        {
            return index == 0;
        }

        return false;
    }

    private static bool EvaluateText(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        var stripped = StripRecommendedPrefix(text);

        return PositiveTokens.Contains(stripped);
    }

    private static string StripRecommendedPrefix(string input)
    {
        const string recPrefix = "(Recommended)";
        if (input.StartsWith(recPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return input[recPrefix.Length..].Trim();
        }

        return input;
    }

    private static bool MatchesNegative(string text)
    {
        if (MatchesTokenOrPrefix(text, NegativeTokens))
        {
            return true;
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

    private static bool MatchesTokenOrPrefix(string text, HashSet<string> tokenSet)
    {
        if (tokenSet.Contains(text))
        {
            return true;
        }

        foreach (var tok in tokenSet)
        {
            if (text.StartsWith(tok + " ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + ":", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + "，", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith(tok + "。", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
