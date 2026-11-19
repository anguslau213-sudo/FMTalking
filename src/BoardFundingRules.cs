using System;

namespace FmPlayerTalk;

internal sealed record BoardFundingContext(string? Amount, string? UnavailableReason)
{
    internal string? PendingConfirmationId { get; init; }
    internal string? PendingAmount { get; init; }
    internal bool CanOffer => Amount != null && PendingConfirmationId == null;
}

internal readonly record struct BoardFundingResult(bool Succeeded, bool Uncertain = false);

internal static class BoardFundingRules
{
    internal const int MinimumAffinity = 80;
    internal const string ChoiceField = "boardFunding";
    internal const string Offer = "offer";

    internal static int CalculateAmount(int balance)
    {
        // Game base currency. The game formats the result in the player's chosen currency.
        var scaled = Math.Clamp(Math.Abs((long)balance) / 50, 1_000, 250_000);
        return (int)(scaled / 100 * 100);
    }

    internal static bool CanAdd(int balance, int amount) =>
        amount == CalculateAmount(balance) && (long)balance + amount <= int.MaxValue;

    internal static BoardFundingResult Apply(int before, int amount, Func<int?> read, Func<int, bool> write,
        Func<bool> commit)
    {
        if (!CanAdd(before, amount) || read() != before)
            return new(false);
        var after = before + amount;
        if (write(after) && read() == after)
        {
            try
            {
                if (commit())
                    return new(true);
            }
            catch (Exception) { /* Receipt failure must not leave a verified cash change. */ }
        }
        var observed = read();
        if (observed == before)
            return new(false);
        return new(false, observed != after || !write(before) || read() != before);
    }
}
