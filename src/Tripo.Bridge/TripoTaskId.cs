namespace Tripo.Bridge;

public static class TripoTaskId
{
    private const int LegacyPrefixLength = 5;
    private const int MinimumLegacySuffixLength = 3;
    private const int MaximumLegacySuffixLength = 128;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith("task_", StringComparison.Ordinal))
        {
            int suffixLength = value.Length - LegacyPrefixLength;
            if (suffixLength is < MinimumLegacySuffixLength or
                > MaximumLegacySuffixLength)
            {
                return false;
            }

            for (int index = LegacyPrefixLength; index < value.Length; index++)
            {
                char character = value[index];
                bool allowed =
                    character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or
                    '_' or '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }

        return Guid.TryParseExact(value, "D", out Guid parsed) &&
               string.Equals(
                   parsed.ToString("D"),
                   value,
                   StringComparison.Ordinal);
    }
}
