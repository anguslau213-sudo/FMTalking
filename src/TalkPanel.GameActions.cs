using System;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private static bool SaveActionReceipt(Conversation chat, GameActionReceipt receipt)
    {
        if (chat.TryRecordGameAction(receipt))
            return true;
        Plugin.Logger.LogWarning("FM Player Talk could not save an execution receipt: " + receipt.Kind + "/" + receipt.Status + ".");
        return false;
    }

    private static string RecordPromiseActionFailure(PendingReceipt? signal, GameActionStatus status, string detail)
    {
        if (signal?.PromiseRequest is not { } request)
            return detail;
        var receipt = new GameActionReceipt(request.ReceiptId, GameActionKind.PromiseWaiver, status,
            request.GameDate, detail, request.ClubName);
        return SaveActionReceipt(request.Chat, receipt) ? detail :
            detail + " 执行结果未能保存到对话记录，请核对 FM。";
    }

    private void ExpireBoardFundingOffer(string reason)
    {
        if (pendingBoardFunding is not { } offer)
            return;
        pendingBoardFunding = null;
        SaveActionReceipt(offer.Chat, FundingReceipt(offer, GameActionStatus.Expired, reason));
    }

    private static GameActionReceipt FundingReceipt(BoardFundingOffer offer, GameActionStatus status, string detail) =>
        new(offer.ReceiptId, GameActionKind.BoardFunding, status, offer.Date, detail, offer.ClubName);
}
