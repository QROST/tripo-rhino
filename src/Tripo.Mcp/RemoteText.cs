using System.Globalization;

namespace Tripo.Mcp;

internal static class RemoteText
{
    public static string Bound(
        string? value,
        int maximumLength,
        string fallback = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);

        string source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        char[] characters = source.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            if (char.IsHighSurrogate(characters[index]) &&
                index + 1 < characters.Length &&
                char.IsLowSurrogate(characters[index + 1]))
            {
                index++;
                continue;
            }

            UnicodeCategory category = char.GetUnicodeCategory(characters[index]);
            if (char.IsControl(characters[index]) ||
                char.IsSurrogate(characters[index]) ||
                category is UnicodeCategory.Format or
                    UnicodeCategory.LineSeparator or
                    UnicodeCategory.ParagraphSeparator)
            {
                characters[index] = ' ';
            }
        }

        string sanitized = new(characters);
        if (sanitized.Length <= maximumLength)
        {
            return sanitized;
        }

        int length = maximumLength;
        if (length > 0 &&
            char.IsHighSurrogate(sanitized[length - 1]) &&
            char.IsLowSurrogate(sanitized[length]))
        {
            length--;
        }

        return sanitized[..length];
    }
}
