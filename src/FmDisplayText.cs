using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class FmDisplayText
{
    // FM links carry Base64 data before the text that its native labels display.
    private static readonly Regex EmbeddedLink = new(
        "\u0001[A-Za-z0-9+/]+={0,2}\u0002([^\u0001\u0002]*)\u0001", RegexOptions.CultureInvariant);

    internal static string? Read(string? value)
    {
        if (value == null)
            return null;
        if (value.Contains('\u0001'))
            value = EmbeddedLink.Replace(value, "$1");
        // An incomplete link has no verified display text. Keep its metadata out of names and prompts.
        if (value.Contains('\u0001') || value.Contains('\u0002'))
            return null;
        return value.Replace("\u200B", "", StringComparison.Ordinal).Trim();
    }

    internal static string Initials(string name) => string.Concat(name
        .Split(new[] { ' ', '·', '・' }, StringSplitOptions.RemoveEmptyEntries)
        .Take(2)
        .Select(piece => StringInfo.GetNextTextElement(piece)));
}
