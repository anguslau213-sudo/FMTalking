using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class BoardFundingSmoke
{
    private const string OfferReply = "{\"text\":\"我愿意提供 £10,000，等你确认。\",\"delta\":0,\"kind\":\"normal\","
        + "\"reason\":\"同意给俱乐部提供现金\",\"proposal\":\"none\",\"dinner\":\"none\",\"boardFunding\":\"offer\"}";
    private const string NoDecisionReply = "{\"text\":\"我现在不能提供资金。\",\"delta\":0,\"kind\":\"normal\","
        + "\"reason\":\"拒绝提供资金\",\"proposal\":\"none\",\"dinner\":\"none\"}";

    public static string Run()
    {
        CheckDialogue().GetAwaiter().GetResult();
        CheckWrite();
        return "Board funding checks passed (model decision, 80 threshold, shadow story separation, cash cap, readback and rollback).";
    }

    private static async Task CheckDialogue()
    {
        var directory = Path.Combine(Path.GetDirectoryName(typeof(BoardFundingSmoke).Assembly.Location)!,
            "board-funding-" + Guid.NewGuid().ToString("N"));
        var profile = new PlayerProfile("board-fixture", "测试董事", "俱乐部董事");
        var answer = Conversation.ParseReply(OfferReply);
        Require(answer.OffersBoardFunding && answer.HasBoardFundingDecision, "Structured funding offer must parse");
        try
        {
            Conversation.ParseReply(OfferReply.Replace("\"offer\"", "10000000", StringComparison.Ordinal));
            throw new Exception("The model cannot supply its own amount");
        }
        catch (InvalidOperationException) { }
        string prompt = "";
        var requested = new BoardFundingContext("£10,000", null);
        var today = new DateTime(2026, 11, 17);
        var shadowTurn = ShadowBoardTurn.Capture(directory, 11, 22, today, "安格斯");
        Require(ShadowBoardRules.TryRecord(shadowTurn, ShadowBoardTransition.Activate), "Set up a shadow dialogue");
        Conversation? lastChat = null;

        async Task<Reply> Play(string branch, int? affinity, bool shadow, BoardFundingContext? context)
        {
            var chat = new Conversation(branch, directory, profile, text =>
            {
                prompt = text;
                return Task.FromResult(answer with { HasShadowBoardDecision = shadow });
            }, null, null, useGameAffinity: true);
            lastChat = chat;
            return await chat.SendAsync("能否请董事会提供一笔资金？", today, null,
                boardFunding: context, shadowBoard: shadow ? ShadowBoardTurn.Capture(directory, 11, 22, today, "安格斯") : null,
                nativeAffinity: new NativeAffinityTurn(affinity, (before, after, blocked, commit) =>
                {
                    // Entering an already-active shadow route rechecks an existing
                    // 100 relationship before recording this member's one-time grant.
                    if (shadow && before == 100 && after == 100 && !blocked && commit())
                        return Task.FromResult<string?>(null);
                    throw new Exception("A cash offer must not change native affinity.");
                }));
        }

        async Task RequireRejectedAsync(string branch, int? affinity, BoardFundingContext? context, string errorText)
        {
            try { await Play(branch, affinity, false, context); }
            catch (InvalidOperationException error) when (error.Message.Contains(errorText, StringComparison.Ordinal))
            {
                Require(lastChat is { History.Count: 0, LastSavedAtUtc: null },
                    "The invalid funding decision does not save the turn");
                return;
            }
            throw new InvalidOperationException("Funding decision was not rejected: " + branch);
        }

        Require((await Play("exact", 80, false, requested)).OffersBoardFunding, "Native affinity 80 qualifies");
        using (var data = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]))
            Require(data.RootElement.GetProperty("boardFunding").GetProperty("amount").GetString() == "£10,000",
                "The offer amount comes from the mod");
        Require((await Play("shadow", 100, true, requested)).OffersBoardFunding,
            "A verified offer remains available in an active shadow board dialogue");
        await RequireRejectedAsync("79-offer", 79, requested, "没有可执行的董事会注资");
        await RequireRejectedAsync("unreadable-offer", null, requested, "没有可执行的董事会注资");
        await RequireRejectedAsync("ordinary-offer", 100, null, "没有可执行的董事会注资");

        answer = Conversation.ParseReply(NoDecisionReply);
        Require(!answer.HasBoardFundingDecision, "An omitted field stays distinguishable after parsing");
        Require(!(await Play("79", 79, false, requested)).OffersBoardFunding &&
            !(await Play("unreadable", null, false, requested)).OffersBoardFunding &&
            !(await Play("ordinary", 100, false, null)).OffersBoardFunding,
            "An ineligible conversation can still proceed without a funding decision");
        await RequireRejectedAsync("missing-decision", 80, requested, "未返回董事会注资");

        answer = Conversation.ParseReply(NoDecisionReply[..^1] + ",\"boardFunding\":\"none\"}");
        Require(answer.HasBoardFundingDecision && !answer.OffersBoardFunding &&
            !(await Play("refused", 80, false, requested)).OffersBoardFunding,
            "An explicit no-action decision preserves the board's refusal");
    }

    private static void CheckWrite()
    {
        Require(BoardFundingRules.CalculateAmount(int.MinValue) == 250_000 &&
            BoardFundingRules.CalculateAmount(1) == 1_000 &&
            BoardFundingRules.CalculateAmount(500_000) == 10_000 &&
            !BoardFundingRules.CanAdd(int.MaxValue, 250_000), "Scaled amount respects lower/upper bounds and size");
        const int before = 500_000, amount = 10_000;
        int? balance = before;
        var receipts = 0;
        bool Write(int value) { balance = value; return true; }
        Require(BoardFundingRules.Apply(before, amount, () => balance, Write, () => ++receipts == 1).Succeeded &&
            balance == before + amount && receipts == 1, "One confirmed change writes and commits the ledger");
        Require(!BoardFundingRules.Apply(before, amount, () => balance, Write, () => ++receipts == 1).Succeeded &&
            balance == before + amount && receipts == 1, "A changed baseline prevents replay");
        balance = before;
        Require(BoardFundingRules.Apply(before, amount, () => balance, Write, () => false) == new BoardFundingResult(false) &&
            balance == before, "Receipt refusal rolls back the verified balance change");
        Require(BoardFundingRules.Apply(before, amount, () => balance, _ => { balance = before + 2; return false; },
            () => throw new Exception("Do not commit unknown balance")) == new BoardFundingResult(false, true) &&
            balance == before + 2, "An unexpected balance must not be overwritten by rollback");
    }

    private static void Require(bool result, string reason)
    {
        if (!result)
            throw new InvalidOperationException(reason);
    }
}
