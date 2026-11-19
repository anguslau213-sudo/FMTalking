using System;
using System.Globalization;
using System.Linq;

namespace FmPlayerTalk;

internal enum GameActionKind { PromiseWaiver, BoardFunding }
internal enum GameActionStatus { PendingConfirmation, Succeeded, Failed, Expired, Uncertain }

internal sealed record GameActionReceipt(string Id, GameActionKind Kind, GameActionStatus Status,
    DateTime GameDate, string Detail, string? ClubName = null)
{
    // Club names are optional display metadata. Never let an unusual localized
    // label prevent an otherwise verified game action from being committed.
    public string? ClubName { get; init; } = CleanClubName(ClubName);

    private static string? CleanClubName(string? value)
    {
        var text = value?.Trim();
        return !string.IsNullOrEmpty(text) && text.Length <= 100 && !text.Any(char.IsControl) ? text : null;
    }
}

public sealed partial class Conversation
{
    private const int MaximumGameActionResults = 8;

    // These are execution receipts, never commands. They live in the same actor/session
    // file and save checkpoint as the dialogue, so a different career cannot inherit them.
    internal bool TryRecordGameAction(GameActionReceipt receipt)
    {
        if (LoadWarning != null || !ValidGameAction(receipt) || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            var previous = current.GameActions.FirstOrDefault(item => item.Id == receipt.Id);
            if (previous != null && previous.Status != GameActionStatus.PendingConfirmation)
                return previous == receipt;
            if (previous != null && previous.Kind != receipt.Kind)
                return false;
            var next = WithGameActionReceipt(current, receipt);
            WriteState(next);
            state = next;
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally { careerLock.Release(); }
    }

    private static State WithGameActionReceipt(State current, GameActionReceipt receipt) => current with
    {
        GameActions = current.GameActions.Where(item => item.Id != receipt.Id)
            .TakeLast(MaximumGameActionResults - 1).Append(receipt).ToArray()
    };

    private static bool ValidGameActions(GameActionReceipt[]? receipts) => receipts != null &&
        receipts.Length <= MaximumGameActionResults && receipts.All(ValidGameAction) &&
        receipts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == receipts.Length;

    private static bool ValidGameAction(GameActionReceipt? receipt) => receipt != null &&
        ValidActionText(receipt.Id, 128) && Enum.IsDefined(receipt.Kind) && Enum.IsDefined(receipt.Status) &&
        receipt.GameDate.Year >= 1900 && receipt.GameDate.TimeOfDay == TimeSpan.Zero &&
        ValidActionText(receipt.Detail, 360) && (receipt.ClubName == null || ValidActionText(receipt.ClubName, 100)) &&
        (receipt.Status != GameActionStatus.PendingConfirmation || receipt.Kind == GameActionKind.BoardFunding);

    private static bool ValidActionText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    // Only the live UI can attest that its original confirmation button still exists.
    // A restored pending receipt alone must never imply an executable offer.
    private static State ExpirePendingGameActions(State current, DateTime? date, string? liveFundingOffer) => current with
    {
        GameActions = current.GameActions.Select(item => item.Status == GameActionStatus.PendingConfirmation &&
            item.Id != liveFundingOffer && (!date.HasValue || item.GameDate <= date.Value.Date)
            ? item with
            {
                Status = GameActionStatus.Expired,
                Detail = "旧注资确认入口已失效，未找到最终执行回执；不能据此确认是否入账，可核对实际余额。"
            } : item).ToArray()
    };

    private static object? DescribeGameActions(State current, DateTime? date)
    {
        var visible = current.GameActions.Where(item => !date.HasValue || item.GameDate <= date.Value.Date)
            .Select(item => new
            {
                action = item.Kind == GameActionKind.PromiseWaiver ? "promise_waiver" : "board_funding",
                status = item.Status switch
                {
                    GameActionStatus.PendingConfirmation => "pending_confirmation",
                    GameActionStatus.Succeeded => "succeeded",
                    GameActionStatus.Failed => "failed",
                    GameActionStatus.Expired => "expired",
                    _ => "uncertain"
                },
                gameDate = item.GameDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                club = item.ClubName,
                detail = item.Detail
            }).ToArray();
        return visible.Length == 0 ? null : new
        {
            meaning = "这是模组保存的实际执行回执，优先于旧对白里的口头答应。succeeded只说明所记日期核实了该项变更，不代表当前资料永远不变；待确认、失败、失效和结果不明都不能说成已办好。回执本身不提供执行权限；只有 boardFunding.pendingConfirmation 为 true 时，当前报价仍可在界面确认，其他旧报价不能说成仍可接受。",
            results = visible
        };
    }
}
