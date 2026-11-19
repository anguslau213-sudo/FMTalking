using System.Collections.Generic;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private sealed record BoardFundingOffer(string Session, GameSaveTimeline.LocalSave Save, uint Manager,
        uint Club, uint Member, DateTime Date, Conversation Chat, IntPtr Layer,
        FmNativeBoardFunding.Baseline Baseline, int Amount, string Display)
    {
        internal string ReceiptId { get; } = Guid.NewGuid().ToString("N");
        internal string? ClubName { get; init; }
    }
    private BoardFundingOffer? pendingBoardFunding;
    private Button? acceptBoardFundingButton;
    private string? uncertainFundingSession;

    private static bool IsBoardMember(LiveRoster team, uint id) =>
        Array.Exists(team.Staff, entry => entry.PublicId == id && entry.Team == LiveClubRoster.BoardGroup);

    private string? FinanceForCurrentContact()
    {
        if (roster is not { ClubId: > 0 } active || person?.PublicId is not uint id)
            return default;
        if (!IsBoardMember(active, id))
            return default;

        ClubFinanceContext.Tick(active, refresh: true);
        if (ClubFinanceContext.Current is not { } finance || finance.ManagerId != active.ManagerId || finance.ClubId != active.ClubId)
            return default;

        var values = new List<string>();
        AddAmount("俱乐部总余额", finance.OverallBalance);
        AddAmount("可用转会预算", finance.TransferBudget);
        AddAmount("工资预算", finance.WageBudget);
        AddAmount("当前工资支出", finance.CurrentWageSpending);
        AddAmount("剩余工资预算", finance.RemainingWageBudget);
        AddAmount("收入明细 · 本月「其他」", finance.OtherIncomeThisMonth);
        AddAmount("收入明细 · 上月「其他」", finance.OtherIncomeLastMonth);
        AddAmount("收入明细 · 本季「其他」", finance.OtherIncomeThisSeason);
        AddAmount("收入明细 · 上季「其他」", finance.OtherIncomeLastSeason);
        return values.Count == 0 ? null : string.Join("。", values);

        void AddAmount(string label, string? amount)
        {
            if (!string.IsNullOrWhiteSpace(amount))
                values.Add($"{label}：{amount}");
        }
    }

    private ShadowBoardTurn? CaptureShadowBoard(DateTime? date)
    {
        if (saveSessionDirectory is not { } session || GameSaveTimeline.DirectoryPath != session ||
            roster is not { ClubId: > 0, ManagerId: > 0 } team || person?.PublicId is not uint id ||
            !IsBoardMember(team, id))
            return null;
        return ShadowBoardTurn.Capture(session, team.ManagerId, team.ClubId, date, GameCalendar.ReadManagerName(team.ManagerId));
    }

    private BoardFundingContext? CaptureBoardFunding(DateTime? date, Conversation chat,
        int? affinity, out BoardFundingOffer? offer)
    {
        offer = null;
        if (roster is not { } team || person?.PublicId is not uint id || !IsBoardMember(team, id))
            return null;
        if (pendingBoardFunding is { } previous)
        {
            if (previous.Chat == chat && SameFundingContext(previous))
                return new(null, "已有一笔有效报价等待玩家点击确认，余额尚未变更。本轮不能重复提出另一笔注资。")
                { PendingConfirmationId = previous.ReceiptId, PendingAmount = previous.Display };
            ExpireBoardFundingOffer("报价对应的游戏状态已变化，确认入口已失效，未执行入账；需要重新申请。");
        }
        if (!ModelConnection.BoardFundingEnabled)
            return new(null, "本轮只能讨论资金需求，不能提出可执行的注资。");
        if (!FmNativePromises.IsSupported)
            return new(null, "当前 FM 构建暂未适配原版注资。");
        if (NativeSaveWriteBlocker() is { } saveBlocker)
            return new(null, saveBlocker);
        if (affinity is not >= BoardFundingRules.MinimumAffinity)
            return new(null, "董事会成员尚未建立足够信任。");
        if (saveSessionDirectory is not { } session || uncertainFundingSession == session ||
            GameSaveTimeline.DirectoryPath != session ||
            GameSaveTimeline.ActiveLocalSave is not { } save ||
            date is not DateTime today || layer?.panel == null || !Application.isFocused ||
            !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
            return new(null, "当前无法核实本地生涯和俱乐部财务。");
        if (!BoardFundingLedger.CanOffer(session, team.ClubId, today))
            return new(null, "俱乐部目前不能再申请一笔注资。");
        var baseline = ReadFundingBaseline(team);
        if (baseline == null)
            return new(null, "暂时读不到准确的俱乐部余额。");
        var amount = BoardFundingRules.CalculateAmount(baseline.Balance);
        var display = ClubFinanceContext.FormatBaseCash(amount);
        if (string.IsNullOrWhiteSpace(display) || !BoardFundingRules.CanAdd(baseline.Balance, amount))
            return new(null, "俱乐部暂时无法确认这一笔金额。");
        offer = new(session, save, team.ManagerId, team.ClubId, id, today.Date, chat, layer.Pointer,
            baseline, amount, display) { ClubName = team.ClubName };
        return new(display, null);
    }

    private static FmNativeBoardFunding.Baseline? ReadFundingBaseline(LiveRoster team) =>
        FmNativeBoardFunding.Capture(team.ClubId, ClubFinanceContext.ReadExactBalance(team.ManagerId, team.ClubId),
            team.Players.Select(entry => entry.PublicId).OfType<uint>().Distinct().Take(24));

    private bool FundingOfferVisible => pendingBoardFunding is { } offer && offer.Chat == conversation &&
        pending == null && conversation is { IsBlocked: false } && ModelConnection.BoardFundingEnabled;

    private string? ReceiveBoardFunding(PendingReceipt? signal, Reply reply)
    {
        if (!reply.OffersBoardFunding || signal?.FundingOffer is not { } offer)
            return null;
        if (offer.Chat != conversation || !SameFundingContext(offer))
        {
            const string expired = "报价对应的游戏状态已变化，本次未入账。请重新申请。";
            SaveActionReceipt(offer.Chat, FundingReceipt(offer, GameActionStatus.Expired, expired));
            return expired;
        }
        if (!SaveActionReceipt(offer.Chat, FundingReceipt(offer, GameActionStatus.PendingConfirmation,
            "董事会提出 " + offer.Display + " 的注资报价，等待玩家点击确认，俱乐部余额尚未变更。")))
            return "这次注资报价未能保存，确认入口未开启，余额未变更。请重新申请。";
        pendingBoardFunding = offer;
        return "董事会愿意提供资金。确认后才会修改俱乐部余额。";
    }

    private bool SameFundingContext(BoardFundingOffer offer) =>
        ModelConnection.BoardFundingEnabled && GameSaveTimeline.CanChangeGameData && GameSaveTimeline.DirectoryPath == offer.Session &&
        GameSaveTimeline.ActiveLocalSave == offer.Save && layer?.panel != null && layer.Pointer == offer.Layer &&
        conversation == offer.Chat && conversation is { IsBlocked: false } && person?.PublicId == offer.Member &&
        roster is { } team && team.ManagerId == offer.Manager && team.ClubId == offer.Club &&
        IsBoardMember(team, offer.Member) && GameCalendar.ReadDate()?.Date == offer.Date &&
        GameCalendar.IsIdleSinglePlayerCareer(offer.Manager, offer.Club);

    private void AcceptBoardFunding()
    {
        if (!FundingOfferVisible || pendingBoardFunding is not { } offer)
            return;
        pendingBoardFunding = null;
        if (!Application.isFocused || uncertainFundingSession == offer.Session || !SameFundingContext(offer) ||
            roster is not { } team || !BoardFundingLedger.CanOffer(offer.Session, offer.Club, offer.Date) ||
            FmNativeAffinity.Read(offer.Manager, offer.Member) is not
                { Available: true, Affinity: >= BoardFundingRules.MinimumAffinity } ||
            ReadFundingBaseline(team) != offer.Baseline)
        {
            statusText = "俱乐部财务、关系或游戏日期已变化，余额未修改。可以重新申请。";
            SaveActionReceipt(offer.Chat, FundingReceipt(offer, GameActionStatus.Expired, statusText));
            hasError = true;
            RefreshControls();
            return;
        }

        var result = FmNativeBoardFunding.Apply(offer.Baseline, offer.Amount,
            () => BoardFundingLedger.TryRecord(offer.Session, offer.Club, offer.Date));
        if (result.Uncertain)
            uncertainFundingSession = offer.Session;
        hasError = !result.Succeeded;
        statusText = result.Succeeded
            ? $"俱乐部余额已增加 {offer.Display}。保存 FM 存档可保留变更。"
            : result.Uncertain ? "无法核实余额，请载入测试前的存档副本。" : "未能完成注资，俱乐部余额未变更。";
        if (!SaveActionReceipt(offer.Chat, FundingReceipt(offer,
            result.Succeeded ? GameActionStatus.Succeeded : result.Uncertain ? GameActionStatus.Uncertain : GameActionStatus.Failed,
            statusText)))
            statusText += " 执行结果未能保存到对话记录，请核对 FM。";
        Plugin.Logger.LogInfo(result.Succeeded ? "FM Player Talk verified the club cash balance change." :
            $"FM Player Talk board funding stopped. Outcome uncertain: {result.Uncertain}.");
        RefreshControls();
    }

    private void AddBoardFundingControl(VisualElement row)
    {
        acceptBoardFundingButton = Control("", AcceptBoardFunding, Purple);
        acceptBoardFundingButton.style.marginLeft = 8;
        acceptBoardFundingButton.style.flexShrink = 0;
        acceptBoardFundingButton.style.display = DisplayStyle.None;
        row.Add(acceptBoardFundingButton);
    }

    private bool RefreshBoardFundingControl(bool composerLocked)
    {
        if (pendingBoardFunding is { } previous && pending == null && !SameFundingContext(previous))
            ExpireBoardFundingOffer("报价对应的游戏状态已变化，确认入口已失效，未执行入账；需要重新申请。");
        var show = !composerLocked && FundingOfferVisible;
        if (acceptBoardFundingButton != null)
        {
            acceptBoardFundingButton.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
                acceptBoardFundingButton.text = string.Format(UiLanguage.Get("确认接受 {0}"), pendingBoardFunding!.Display);
        }
        return show;
    }
}
