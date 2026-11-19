using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class OtherIncomeThreshold
{
    private const int Minimum = 20_000_000;

    // FM's full cash formatter can use Western or Indian grouping and can place the currency on either side.
    private static readonly Regex FullCash = new(
        @"\A [^\p{Nd}()\-−]*
          (?<whole>
              \p{Nd}{1,2} (?<group>[,.]) (?:\p{Nd}{2}\k<group>)+ \p{Nd}{3}
            | \p{Nd}{1,3} (?<group>[,.'’\p{Zs}\u066C]) \p{Nd}{3} (?:\k<group>\p{Nd}{3})*
            | \p{Nd}+
          )
          (?:(?!\k<group>)[,.\u066B]\p{Nd}{1,2})?
          [^\p{Nd}()\-−]* \z",
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    internal static bool IsReached(string? fullCash)
    {
        if (string.IsNullOrWhiteSpace(fullCash))
            return false;

        var whole = FullCash.Match(fullCash.Normalize(NormalizationForm.FormKC)).Groups["whole"];
        var value = 0;
        foreach (var character in whole.ValueSpan)
        {
            var digit = CharUnicodeInfo.GetDecimalDigitValue(character);
            if (digit < 0)
                continue;
            value = value * 10 + digit;
            if (value >= Minimum)
                return true;
        }
        return false;
    }
}
