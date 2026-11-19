using System;
using System.Collections.Generic;
using System.Linq;

namespace FmPlayerTalk;

internal sealed record RelationshipGameEvent(string Key, DateTime Date, string Summary);

public sealed partial class Conversation
{
    private const int MaximumRelationshipEvents = 32;
    private const int MaximumEventKeyLength = 180;

    private sealed record RelationshipMail(
        DateTime StartedOn, DateTime? LastSentOn, bool Unread, string[] SentEvents);

    internal bool HasUnreadRelationshipMessage => CanReceiveProactiveMessages && state.RelationshipMessages?.Unread == true;

    private static bool CanReceiveRelationshipMessages(State current, PlayerProfile profile) =>
        !current.Blocked && (current.Stage == RelationshipProposal.Confidant ||
            current.Stage == RelationshipProposal.Partner && profile.CanProgressRomance);

    internal bool TryDeliverRelationshipMessage(DateTime? gameDate, IReadOnlyList<RelationshipGameEvent>? events = null)
    {
        if (gameDate is not DateTime value || !IsRelationshipDay(value.Date) || !CanReceiveProactiveMessages)
            return false;
        var today = value.Date;
        if (state.RelationshipMessages != null && !ShouldSendRelationshipMessage(state, today, events, out _))
            return false;
        if (!careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (!CanReceiveRelationshipMessages(current, player))
                return false;
            var firstDay = current.RelationshipMessages == null;
            var mail = current.RelationshipMessages ?? StartRelationshipMail(today);
            current = current with { RelationshipMessages = mail };
            if (!ShouldSendRelationshipMessage(current, today, events, out var gameEvent))
            {
                // Old saves have no activation date. Start their timer the first time a
                // verified game day is available, rather than flooding them on load.
                if (firstDay)
                    WriteState(current);
                state = current;
                return false;
            }
            var turns = new List<Turn>(current.Turns.TakeLast(MaximumTurns - 1))
            {
                new("", RelationshipMessageText(current.Stage, today, gameEvent), 0)
                {
                    IsIncoming = true, GameDate = today
                }
            };
            var seen = gameEvent == null ? mail.SentEvents :
                mail.SentEvents.TakeLast(MaximumRelationshipEvents - 1).Append(gameEvent.Key).ToArray();
            var next = current with
            {
                Turns = turns,
                RelationshipMessages = mail with { LastSentOn = today, Unread = true, SentEvents = seen }
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
        finally
        {
            careerLock.Release();
        }
    }

    internal bool MarkRelationshipMessageRead()
    {
        if (!HasUnreadRelationshipMessage || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.RelationshipMessages is not { Unread: true } mail || current.Blocked)
                return false;
            var next = current with { RelationshipMessages = mail with { Unread = false } };
            WriteState(next);
            state = next;
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
        finally
        {
            careerLock.Release();
        }
    }

    private bool ShouldSendRelationshipMessage(State current, DateTime today,
        IReadOnlyList<RelationshipGameEvent>? events, out RelationshipGameEvent? gameEvent)
    {
        gameEvent = null;
        if (current.RelationshipMessages is not { Unread: false } mail ||
            mail.StartedOn > today || mail.LastSentOn >= today ||
            current.DinnerFollowUpUnread || current.PromiseFollowUpUnread ||
            DinnerStatus(current, today) != null || DinnerFollowUpIsDue(current, today) ||
            Array.Exists(current.PromiseFollowUps, entry => entry.ConfirmedOn < today) ||
            current.Turns.Any(turn => turn.IsIncoming && turn.GameDate?.Date == today))
            return false;

        gameEvent = events?.FirstOrDefault(entry =>
            ValidRelationshipEvent(entry, today, mail.StartedOn) &&
            !mail.SentEvents.Contains(entry.Key, StringComparer.Ordinal));
        if (gameEvent != null)
            return true;

        var lastContact = mail.LastSentOn ?? mail.StartedOn;
        foreach (var turn in current.Turns)
        {
            if (!turn.Moderated && turn.GameDate?.Date is DateTime on && on > lastContact && on <= today)
                lastContact = on;
        }
        return (today - lastContact).Days >= 3 + (int)(RelationshipMessageSeed(player.Id) % 3);
    }

    private string RelationshipMessageText(RelationshipProposal stage, DateTime today, RelationshipGameEvent? gameEvent)
    {
        if (gameEvent != null)
        {
            var fact = gameEvent.Summary.TrimEnd('。', '.', '！', '!', '？', '?');
            var firstLine = gameEvent.Key.StartsWith("fixture:", StringComparison.Ordinal)
                ? $"听说你们{fact}。"
                : $"刚看到消息，{fact}。";
            return stage == RelationshipProposal.Partner
                ? firstLine + "真替你高兴，等你有空，我想听你亲口讲讲。"
                : firstLine + "好样的！这么好的日子，记得给自己也放一会儿假。";
        }

        // Pick a stable variation without a background model call or real-world clock.
        var variant = (RelationshipMessageSeed(player.Id) + (ulong)(today.Ticks / TimeSpan.TicksPerDay)) % 4;
        return (stage, variant) switch
        {
            (RelationshipProposal.Partner, 0) => "这几天有点想你。忙完了给我回个消息，我们聊会儿？",
            (RelationshipProposal.Partner, 1) => "我想听听你今天过得怎么样。球队的事也好，别的事也好，我都想听。",
            (RelationshipProposal.Partner, 2) => "忙的时候记得留点时间给自己。等你有空，我也想占你一小会儿。",
            (RelationshipProposal.Partner, _) => "今天突然想跟你说一声，有你在真好。等你忙完再回我就行。",
            (_, 0) => "好久没找你聊了。最近怎么样？有想说的就来找我。",
            (_, 1) => "问候你一声。忙完球队的事，也留点时间给自己。",
            (_, 2) => "最近有什么事让你高兴吗？有空跟我讲讲，我想听。",
            (_, _) => "想起有阵子没跟你聊了。哪天忙完，我们找个时间说会儿话。"
        };
    }

    private static ulong RelationshipMessageSeed(string actorId)
    {
        ulong seed = 0;
        foreach (var character in actorId)
            seed = unchecked(seed * 31 + character);
        return seed;
    }

    private static RelationshipMail StartRelationshipMail(DateTime date) =>
        new(date.Date, null, false, Array.Empty<string>());

    private static bool IsRelationshipDay(DateTime date) => date == date.Date && date.Year >= 1900;

    private static bool ValidRelationshipMail(RelationshipMail? mail) =>
        mail == null || IsRelationshipDay(mail.StartedOn) &&
        (mail.LastSentOn is not DateTime last || IsRelationshipDay(last) && last >= mail.StartedOn) &&
        (!mail.Unread || mail.LastSentOn.HasValue) &&
        mail.SentEvents is { Length: <= MaximumRelationshipEvents } &&
        mail.SentEvents.All(ValidRelationshipEventKey);

    private static bool ValidRelationshipEvent(RelationshipGameEvent? item, DateTime today, DateTime since) =>
        item is { Summary.Length: > 0 and <= 120 } && ValidRelationshipEventKey(item.Key) &&
        IsRelationshipDay(item.Date) && item.Date >= since && item.Date <= today && (today - item.Date).Days <= 2 &&
        !string.IsNullOrWhiteSpace(item.Summary) && !item.Summary.Any(char.IsControl);

    private static bool ValidRelationshipEventKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaximumEventKeyLength && !key.Any(char.IsControl);
}
