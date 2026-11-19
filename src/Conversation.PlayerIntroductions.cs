using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal sealed record PlayerIntroductionCandidate(uint PublicId, string Name, string? ClubName, string? OriginalName = null);

internal sealed record PendingPlayerIntroduction(string Target, DateTime RequestedOn, Guid Id)
{
    public PlayerIntroductionCandidate[] Candidates { get; init; } = Array.Empty<PlayerIntroductionCandidate>();
    public uint? ConfirmedPlayerId { get; init; }
    public bool AwaitingName { get; init; }
}

public sealed partial class Conversation
{
    private const int MaximumPendingPlayerIntroductions = 8;
    private const string PlayerIntroductionReason = "经理办公室 · 球员介绍";
    private const string PlayerIntroductionAcknowledgement = "我试着去找找，但不能保证能拿到联系方式。明天有结果我告诉你。";
    internal const string PlayerIntroductionNameClarification =
        "我还没在当前存档里找到能确认的球员。请补充他在 FM 里的全名、原文姓名或人员 ID，我再帮你查。";

    internal bool HasUnreadPlayerIntroduction => CanHandlePlayerIntroductions && state.PlayerIntroductionUnread;

    internal PendingPlayerIntroduction? PlayerIntroductionAwaitingConfirmation => CanHandlePlayerIntroductions
        ? Array.Find(state.PlayerIntroductions, entry => entry.Candidates.Length > 0)
        : null;

    internal PendingPlayerIntroduction? PlayerIntroductionAwaitingName => CanHandlePlayerIntroductions
        ? Array.Find(state.PlayerIntroductions, entry => entry.AwaitingName)
        : null;

    internal PendingPlayerIntroduction? UnresolvedPlayerIntroduction => CanHandlePlayerIntroductions &&
        PlayerIntroductionAwaitingConfirmation == null && PlayerIntroductionAwaitingName == null
        ? Array.Find(state.PlayerIntroductions, LooksUpName)
        : null;

    private bool CanHandlePlayerIntroductions => UsesManagerAssistantAffinity && LoadWarning == null && !state.Blocked;

    internal bool TryQueuePlayerIntroduction(string input, string target, DateTime date)
    {
        if (!CanHandlePlayerIntroductions || string.IsNullOrWhiteSpace(input) || input.Length > 160 ||
            !ValidPlayerIntroductionTarget(target) || date.Year < 1900 || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            var targetKey = PlayerIntroductionRules.MatchKey(target);
            if (current.Blocked || current.PlayerIntroductions.Length >= MaximumPendingPlayerIntroductions ||
                current.PlayerIntroductions.Any(entry => PlayerIntroductionRules.MatchKey(entry.Target) == targetKey))
                return false;

            var pending = new PendingPlayerIntroduction(target.Trim(), date.Date, Guid.NewGuid());
            var turn = new Turn(input, LookupAcknowledgement(target), 0)
            {
                GameDate = pending.RequestedOn,
                Reason = PlayerIntroductionReason
            };
            var next = current with
            {
                Turns = current.Turns.TakeLast(MaximumTurns - 1).Append(turn).ToList(),
                PlayerIntroductions = current.PlayerIntroductions.Append(pending).ToArray()
            };
            WriteState(next);
            state = next;
            LastSavedAtUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
        finally { careerLock.Release(); }
    }

    internal PendingPlayerIntroduction? DuePlayerIntroduction(DateTime date) => CanHandlePlayerIntroductions
        ? Array.Find(state.PlayerIntroductions, entry => entry.RequestedOn < date.Date &&
            !entry.AwaitingName && entry.Candidates.Length == 0 &&
            (!LooksUpName(entry) || PlayerIntroductionAwaitingConfirmation == null && PlayerIntroductionAwaitingName == null))
        : null;

    private static bool LooksUpName(PendingPlayerIntroduction entry) =>
        entry.ConfirmedPlayerId == null && entry.Candidates.Length == 0 && !entry.AwaitingName &&
        PlayerIntroductionRules.ParsePublicId(entry.Target) == null;

    private static string LookupAcknowledgement(string target) => PlayerIntroductionRules.ParsePublicId(target).HasValue
        ? PlayerIntroductionAcknowledgement
        : "我先去核对是哪一位。确认后我会试着联系，但不能保证能拿到联系方式。";

    internal bool TrySupplyPlayerIntroductionName(string input, string target, DateTime date)
    {
        if (PlayerIntroductionAwaitingName is not { } waiting || date.Date < waiting.RequestedOn ||
            !ValidPlayerIntroductionTarget(target) || input.Length is < 1 or > 160 || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || !current.PlayerIntroductions.Any(entry => SameIntroductionRequest(entry, waiting)))
                return false;
            var key = PlayerIntroductionRules.MatchKey(target);
            if (current.PlayerIntroductions.Any(entry => entry.Id != waiting.Id && PlayerIntroductionRules.MatchKey(entry.Target) == key))
                return false;
            var retry = new PendingPlayerIntroduction(target.Trim(), date.Date, Guid.NewGuid());
            var turn = new Turn(input, LookupAcknowledgement(target), 0) { GameDate = date.Date, Reason = PlayerIntroductionReason };
            StoreIntroductionTurn(current, turn, current.PlayerIntroductions.Select(entry => entry.Id == waiting.Id ? retry : entry).ToArray(), false);
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally { careerLock.Release(); }
    }

    internal bool TryResumeLegacyPlayerIntroduction(DateTime date)
    {
        if (!CanHandlePlayerIntroductions || LegacyIntroductionRequests(state, date) == null || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || LegacyIntroductionRequests(current, date) is not { } requests)
                return false;
            var next = current with { PlayerIntroductions = requests };
            WriteState(next);
            state = next;
            LastSavedAtUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally { careerLock.Release(); }
    }

    private static PendingPlayerIntroduction[]? LegacyIntroductionRequests(State current, DateTime date)
    {
        if (current.PlayerIntroductions.Length == 0)
            return LegacyIntroductionName(current, date) is { } target
                ? new[] { new PendingPlayerIntroduction(target, date.Date, Guid.NewGuid()) }
                : null;
        if (Array.Find(current.PlayerIntroductions, entry => entry.Candidates.Length > 0) is not { } request ||
            current.Turns.Count < 2)
            return null;

        var offer = current.Turns[^2];
        var answer = current.Turns[^1];
        // An older build could save a normal model turn when the player confirmed a real offer.
        // Recover from the player's answer and the persisted candidates, never the model's reply.
        if (!offer.IsIncoming || offer.Reason != PlayerIntroductionReason ||
            offer.Assistant != IntroductionCandidateOffer(request) || answer.IsIncoming ||
            answer.Reason == PlayerIntroductionReason || answer.User.Length is < 1 or > 160 ||
            offer.GameDate is not DateTime offered || answer.GameDate is not DateTime confirmed ||
            offered < request.RequestedOn || confirmed < offered || confirmed > date.Date)
            return null;
        var choice = IntroductionChoice(answer.User, request);
        return choice >= 0
            ? current.PlayerIntroductions.Select(entry => entry.Id == request.Id
                ? ConfirmPlayerIntroduction(request, choice, confirmed) : entry).ToArray()
            : null;
    }

    private static string? LegacyIntroductionName(State current, DateTime date)
    {
        if (current.Turns.Count < 3)
            return null;
        var received = current.Turns[^3];
        var question = current.Turns[^2];
        var answer = current.Turns[^1];
        return received.Reason == PlayerIntroductionReason && !received.IsIncoming && received.Assistant == PlayerIntroductionAcknowledgement &&
            question.IsIncoming && question.Reason == PlayerIntroductionReason && question.Assistant == PlayerIntroductionNameClarification &&
            !answer.IsIncoming && received.GameDate is DateTime started && question.GameDate is DateTime asked &&
            answer.GameDate is DateTime supplied && started <= asked && asked <= supplied && supplied <= date.Date
            ? PlayerIntroductionRules.ParseNameSupplement(answer.User)
            : null;
    }

    internal bool TryOfferPlayerIntroductionCandidates(PendingPlayerIntroduction request,
        IReadOnlyList<PlayerIntroductionCandidate> candidates, DateTime date)
    {
        if (!CanHandlePlayerIntroductions || !LooksUpName(request) ||
            date.Date < request.RequestedOn || !ValidIntroductionCandidates(candidates) || candidates.Count == 0 ||
            !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || current.PlayerIntroductions.Any(entry => entry.Candidates.Length > 0 || entry.AwaitingName) ||
                !current.PlayerIntroductions.Any(entry => SameIntroductionRequest(entry, request)))
                return false;
            var offered = request with { Candidates = candidates.ToArray() };
            var turn = new Turn("", IntroductionCandidateOffer(offered), 0)
            {
                IsIncoming = true, GameDate = date.Date, Reason = PlayerIntroductionReason
            };
            var next = current with
            {
                Turns = current.Turns.TakeLast(MaximumTurns - 1).Append(turn).ToList(),
                PlayerIntroductions = current.PlayerIntroductions.Select(entry => entry.Id == request.Id ? offered : entry).ToArray(),
                PlayerIntroductionUnread = true
            };
            WriteState(next);
            state = next;
            LastSavedAtUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally { careerLock.Release(); }
    }

    private static string IntroductionCandidateOffer(PendingPlayerIntroduction offer)
    {
        var options = string.Join("\n", offer.Candidates.Select((candidate, index) =>
            $"{index + 1}. {IntroductionCandidateDisplayName(candidate)} · {candidate.ClubName ?? "当前俱乐部未确认"}（ID：{candidate.PublicId}）"));
        return offer.Candidates.Length == 1
            ? "我在当前存档里找到了这位球员：\n" + options + "\n你说的是这位吗？确认后我试着联系，明天告诉你结果。"
            : "当前存档里有几位可能的球员：\n" + options + "\n你要找哪一位？回复序号、姓名或ID。确认后我试着联系，明天告诉你结果。";
    }

    // handled stays true for recognized choices even if persistence fails, so they do not
    // fall through to an unrelated model turn or accidentally accept a different person.
    internal bool TryRespondToPlayerIntroductionChoice(string input, DateTime date, out bool handled)
    {
        handled = false;
        if (PlayerIntroductionAwaitingConfirmation is not { } waiting || string.IsNullOrWhiteSpace(input) ||
            input.Length > 160 || IntroductionChoice(input, waiting) == UnknownIntroductionChoice)
            return false;
        handled = true;
        if (date.Date < waiting.RequestedOn || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            var request = Array.Find(current.PlayerIntroductions, entry => SameIntroductionRequest(entry, waiting));
            if (current.Blocked || request == null)
                return false;
            var choice = IntroductionChoice(input, request);
            if (choice == UnknownIntroductionChoice)
                return false;
            StorePlayerIntroductionChoice(current, request, input, date, choice);
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally { careerLock.Release(); }
    }

    private Reply StorePlayerIntroductionChoice(State current, PendingPlayerIntroduction request, string input,
        DateTime date, int choice)
    {
        var response = choice switch
        {
            CancelIntroductionChoice => "好，那我先不联系。你确定要找谁时再告诉我。",
            ClarifyIntroductionChoice => "为了找对人，你再告诉我一下，是上面哪一位？可以说姓名、球队或序号。",
            _ => $"好，是{request.Candidates[choice].Name}。" + PlayerIntroductionAcknowledgement
        };
        var turn = new Turn(input, response, 0) { GameDate = date.Date, Reason = PlayerIntroductionReason };
        var requests = choice == CancelIntroductionChoice ? current.PlayerIntroductions.Where(entry => entry.Id != request.Id).ToArray()
            : current.PlayerIntroductions.Select(entry => entry.Id == request.Id && choice >= 0
                ? ConfirmPlayerIntroduction(request, choice, date) : entry).ToArray();
        StoreIntroductionTurn(current, turn, requests, false);
        return new Reply(response, 0) { Reason = PlayerIntroductionReason };
    }

    private static PendingPlayerIntroduction ConfirmPlayerIntroduction(PendingPlayerIntroduction request, int choice, DateTime date) =>
        request with
        {
            RequestedOn = date.Date,
            ConfirmedPlayerId = request.Candidates[choice].PublicId,
            Candidates = Array.Empty<PlayerIntroductionCandidate>()
        };

    internal bool TryCompletePlayerIntroduction(PendingPlayerIntroduction request, string response, DateTime date)
    {
        if (request.Candidates.Length != 0 || request.AwaitingName || request.RequestedOn >= date.Date)
            return false;
        return TryFinishPlayerIntroduction(request, response, date, askForName: false);
    }

    internal bool TryAskForPlayerIntroductionName(PendingPlayerIntroduction request, string response, DateTime date)
    {
        if (!LooksUpName(request) || date.Date < request.RequestedOn)
            return false;
        return TryFinishPlayerIntroduction(request, response, date, askForName: true);
    }

    private bool TryFinishPlayerIntroduction(PendingPlayerIntroduction request, string response, DateTime date, bool askForName)
    {
        if (!CanHandlePlayerIntroductions || string.IsNullOrWhiteSpace(response) || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || !Array.Exists(current.PlayerIntroductions, entry => SameIntroductionRequest(entry, request)))
                return false;
            if (askForName && current.PlayerIntroductions.Any(entry => entry.Candidates.Length > 0 || entry.AwaitingName))
                return false;

            var turn = new Turn("", response, 0) { IsIncoming = true, GameDate = date.Date, Reason = PlayerIntroductionReason };
            var remaining = askForName
                ? current.PlayerIntroductions.Select(entry => entry.Id == request.Id ? entry with { AwaitingName = true } : entry)
                : current.PlayerIntroductions.Where(entry => entry.Id != request.Id);
            StoreIntroductionTurn(current, turn, remaining.ToArray(), true);
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
        finally { careerLock.Release(); }
    }

    private void StoreIntroductionTurn(State current, Turn turn, PendingPlayerIntroduction[] requests, bool unread)
    {
        var next = current with
        {
            Turns = current.Turns.TakeLast(MaximumTurns - 1).Append(turn).ToList(),
            PlayerIntroductions = requests,
            PlayerIntroductionUnread = unread
        };
        WriteState(next);
        state = next;
        LastSavedAtUtc = DateTime.UtcNow;
    }

    internal bool MarkPlayerIntroductionRead()
    {
        if (!HasUnreadPlayerIntroduction || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || !current.PlayerIntroductionUnread)
                return false;

            var next = current with { PlayerIntroductionUnread = false };
            WriteState(next);
            state = next;
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
        finally { careerLock.Release(); }
    }

    private static bool ValidPlayerIntroductionTarget(string? target) => !string.IsNullOrWhiteSpace(target) &&
        target.Length <= 100 && !target.Any(char.IsControl) && PlayerIntroductionRules.MatchKey(target).Length > 0;

    private const int UnknownIntroductionChoice = -1;
    private const int CancelIntroductionChoice = -2;
    private const int ClarifyIntroductionChoice = -3;

    // Used for explicit numeric UI choices and for recovering an old saved reply.
    // Ordinary natural language is interpreted by the configured model.
    private static int IntroductionChoice(string input, PendingPlayerIntroduction request)
    {
        if (Regex.IsMatch(input, @"[?？]|(?:吗|么|呢|对吧|是吧)\s*[。.!！]*\z", RegexOptions.CultureInvariant))
            return UnknownIntroductionChoice;
        var numeric = Regex.Match(input.Trim(), @"\A(?<prefix>(?:人员)?ID\s*[:：]?\s*)?#?(?<id>[0-9]{1,10})\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (numeric.Success && PlayerIntroductionRules.ParsePublicId(numeric.Groups["id"].Value) is { } id)
        {
            var match = Array.FindIndex(request.Candidates, candidate => candidate.PublicId == id);
            if (match >= 0)
                return match;
            return !numeric.Groups["prefix"].Success && id <= request.Candidates.Length
                ? (int)id - 1 : ClarifyIntroductionChoice;
        }

        var key = PlayerIntroductionRules.MatchKey(input);
        if (key is "不" or "不是" or "不是他" or "不是她" or "不对" or "都不是" or "不是这个" or
            "不是这位" or "取消" or "取消吧" or "算了" or "不用找了" or "别找了")
            return CancelIntroductionChoice;
        var ordinal = key switch
        {
            "第一个" or "第一位" or "第一名" => 0,
            "第二个" or "第二位" or "第二名" => 1,
            "第三个" or "第三位" or "第三名" => 2,
            _ => UnknownIntroductionChoice
        };
        if (ordinal >= 0)
            return ordinal < request.Candidates.Length ? ordinal : ClarifyIntroductionChoice;
        var names = Enumerable.Range(0, request.Candidates.Length).Where(index =>
            PlayerIntroductionRules.MatchKey(request.Candidates[index].Name) == key ||
            request.Candidates[index].OriginalName is { } name && PlayerIntroductionRules.MatchKey(name) == key).Take(2).ToArray();
        if (names.Length > 0)
            return names.Length == 1 ? names[0] : ClarifyIntroductionChoice;
        return key is "好" or "好的" or "是" or "是的" or "对" or "对的" or "没错" or "是他" or "是她" or "是这个" or
            "是这位" or "就是他" or "就是她" or "就是这个" or "就是这位" or "确认" or "对就是这个" or "对就按这个来"
            ? request.Candidates.Length == 1 ? 0 : ClarifyIntroductionChoice
            : UnknownIntroductionChoice;
    }

    private static string IntroductionCandidateDisplayName(PlayerIntroductionCandidate candidate) =>
        candidate.OriginalName is { } original &&
        PlayerIntroductionRules.MatchKey(original) != PlayerIntroductionRules.MatchKey(candidate.Name)
            ? $"{candidate.Name}（{original}）" : candidate.Name;

    private static bool SameIntroductionRequest(PendingPlayerIntroduction first, PendingPlayerIntroduction second) =>
        first.Id == second.Id && first.Target == second.Target && first.RequestedOn == second.RequestedOn &&
        first.ConfirmedPlayerId == second.ConfirmedPlayerId && first.AwaitingName == second.AwaitingName &&
        first.Candidates.SequenceEqual(second.Candidates);

    private static bool ValidIntroductionCandidates(IReadOnlyList<PlayerIntroductionCandidate>? candidates) =>
        candidates is not null && candidates.Count <= PlayerIntroductionNameResolver.MaximumAliases &&
        candidates.All(candidate => candidate is not null && candidate.PublicId is > 0 and < uint.MaxValue &&
            ValidPlayerIntroductionTarget(candidate.Name) && (candidate.ClubName == null ||
                candidate.ClubName.Length is > 0 and <= 160 && !candidate.ClubName.Any(char.IsControl)) &&
            (candidate.OriginalName == null || ValidPlayerIntroductionTarget(candidate.OriginalName))) &&
        candidates.Select(candidate => candidate.PublicId).Distinct().Count() == candidates.Count;

    private static bool ValidPlayerIntroductions(PendingPlayerIntroduction[]? entries) => entries is not null &&
        entries.Length <= MaximumPendingPlayerIntroductions && entries.All(entry => entry is not null &&
            ValidPlayerIntroductionTarget(entry.Target) && entry.Id != Guid.Empty &&
            entry.RequestedOn.Year >= 1900 && entry.RequestedOn == entry.RequestedOn.Date &&
            ValidIntroductionCandidates(entry.Candidates) &&
            (entry.ConfirmedPlayerId == null || entry.ConfirmedPlayerId is > 0 and < uint.MaxValue && entry.Candidates.Length == 0) &&
            (!entry.AwaitingName || entry.ConfirmedPlayerId == null && entry.Candidates.Length == 0)) &&
        entries.Count(entry => entry.AwaitingName || entry.Candidates.Length > 0) <= 1 &&
        entries.Select(entry => entry.Id).Distinct().Count() == entries.Length &&
        entries.Select(entry => PlayerIntroductionRules.MatchKey(entry.Target)).Distinct().Count() == entries.Length;
}
