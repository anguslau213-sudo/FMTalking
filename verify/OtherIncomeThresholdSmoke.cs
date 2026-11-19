using System;
using FmPlayerTalk;

public static class OtherIncomeThresholdSmoke
{
    public static string Run()
    {
        foreach (var amount in new[]
        {
            "€20,000,000", "£20,000,000.00", "20.000.000,00 €", "20\u202F000\u202F000 EUR",
            "CHF 20’000’000.00", "₹2,00,00,000", "¥25,000,000", "٢٠٬٠٠٠٬٠٠٠ ر.س",
            "20\u00A0000\u00A0000 zł", "￥２０００００００", "20000000"
        })
            Expect(amount, true);
        foreach (var amount in new[]
        {
            "€19,999,999", "€19.999.999,99", "₹1,99,99,999", "$199,999.99", "€20M",
            "−€20,000,000", "(€20,000,000)", "€20,000,000 – 21,000,000", "未读取", null
        })
            Expect(amount, false);

        return "Other income threshold checks passed (20,000,000 in the displayed currency, exact boundary and cash formatting).";
    }

    private static void Expect(string? amount, bool expected)
    {
        if (OtherIncomeThreshold.IsReached(amount) != expected)
            throw new InvalidOperationException($"Unexpected income threshold result: {amount}");
    }
}
