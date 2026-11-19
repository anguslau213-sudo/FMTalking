using System;
using System.Collections.Generic;

namespace FmPlayerTalk;

internal static class FmNativeBoardFunding
{
    // Match the independently bound FM balance before
    // accepting this layout. The initial experiment only updates the club cash balance.
    private static ulong TeamVtable => FmNativeLayout.Current.Team;
    private static ulong ClubVtable => FmNativeLayout.Current.Club;
    private static ulong FinanceVtable => FmNativeLayout.Current.Finance;
    private const ulong PlayerTeam = 0x130;
    private const ulong TeamClub = 0x30;
    private const ulong ClubId = 0x0C;
    private const ulong ClubFinance = 0x150;
    private const ulong CashBalance = 0x0C;
    internal sealed record Baseline(ulong Module, ulong Club, uint ClubId, ulong Finance, int Balance);

    internal static Baseline? Capture(uint clubId, long? exactUiBalance, IEnumerable<uint> currentPlayerIds)
    {
        var module = FmNativePromises.ModuleBase();
        if (module == 0 || exactUiBalance is not (>= int.MinValue and <= int.MaxValue) || clubId == 0)
            return null;
        foreach (var playerId in currentPlayerIds)
        {
            var player = FmNativeAffinity.FindPlayerRecord(module, playerId);
            if (player == 0 || !TryPointer(player + PlayerTeam, out var team) || !HasVtable(team, module + TeamVtable) ||
                !TryPointer(team + TeamClub, out var club) || !HasVtable(club, module + ClubVtable) ||
                !TryInt(club + ClubId, out var id) || unchecked((uint)id) != clubId)
                continue;
            if (!TryPointer(club + ClubFinance, out var finance) || !HasVtable(finance, module + FinanceVtable) ||
                !TryInt(finance + CashBalance, out var balance) || balance != exactUiBalance)
                return null;
            return new(module, club, clubId, finance, balance);
        }
        return null;
    }

    internal static BoardFundingResult Apply(Baseline baseline, int amount, Func<bool> commit)
    {
        if (FmNativePromises.ModuleBase() != baseline.Module)
            return new(false);
        var address = baseline.Finance + CashBalance;
        if (!FmNativePromises.CanWrite(address, sizeof(int)))
            return new(false);
        return BoardFundingRules.Apply(baseline.Balance, amount,
            () => SameClub(baseline) && TryInt(address, out var current) ? current : null,
            value => SameClub(baseline) && FmNativePromises.WriteBytes(address, BitConverter.GetBytes(value)),
            commit);
    }

    private static bool SameClub(Baseline b) =>
        HasVtable(b.Club, b.Module + ClubVtable) && TryInt(b.Club + ClubId, out var id) &&
        unchecked((uint)id) == b.ClubId && TryPointer(b.Club + ClubFinance, out var finance) &&
        finance == b.Finance && HasVtable(finance, b.Module + FinanceVtable);

    private static bool HasVtable(ulong p, ulong vtable) => TryPointer(p, out var actual) && actual == vtable;

    private static bool TryPointer(ulong p, out ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        var read = FmNativePromises.TryRead(p, bytes);
        value = read ? BitConverter.ToUInt64(bytes, 0) : 0;
        return read && value is >= 0x10000 and < 0x800000000000;
    }

    private static bool TryInt(ulong p, out int value)
    {
        var bytes = new byte[sizeof(int)];
        var read = FmNativePromises.TryRead(p, bytes);
        value = read ? BitConverter.ToInt32(bytes, 0) : 0;
        return read;
    }
}
