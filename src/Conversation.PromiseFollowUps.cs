using System;
using System.Collections.Generic;
using System.Linq;

namespace FmPlayerTalk;

public sealed partial class Conversation
{
    private const int MaximumPendingPromiseFollowUps = 8;

    private sealed record PromiseFollowUp(string Key, string Title, DateTime ConfirmedOn);

    internal bool HasUnreadPromiseFollowUp => state.PromiseFollowUpUnread && !state.Blocked && LoadWarning == null;

    internal bool TryRecordPromiseWaiver(string key, string title, DateTime gameDate, GameActionReceipt? receipt = null)
    {
        var followUp = new PromiseFollowUp(key, title, gameDate.Date);
        if (LoadWarning != null || !ValidPromiseFollowUp(followUp) || receipt != null &&
            (!ValidGameAction(receipt) || receipt.Kind != GameActionKind.PromiseWaiver ||
                receipt.Status != GameActionStatus.Succeeded || receipt.GameDate != gameDate.Date) || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked || current.PromiseFollowUps.Length >= MaximumPendingPromiseFollowUps ||
                Array.Exists(current.PromiseFollowUps, entry => entry.Key == key))
                return false;
            var next = current with { PromiseFollowUps = current.PromiseFollowUps.Append(followUp).ToArray() };
            if (receipt != null)
                next = WithGameActionReceipt(next, receipt);
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

    internal bool TryDeliverPromiseFollowUps(DateTime? gameDate)
    {
        if (gameDate is not DateTime today || LoadWarning != null || state.Blocked ||
            !Array.Exists(state.PromiseFollowUps, entry => entry.ConfirmedOn < today.Date) || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (current.Blocked)
                return false;
            var due = current.PromiseFollowUps.Where(entry => entry.ConfirmedOn < today.Date).ToArray();
            if (due.Length == 0)
                return false;
            var turns = new List<Turn>(current.Turns.TakeLast(MaximumTurns - due.Length));
            foreach (var followUp in due)
            {
                var message = $"教练，之前关于{followUp.Title}的承诺，我已经放弃了。";
                turns.Add(new("", message, 0) { IsIncoming = true, GameDate = today.Date });
            }
            var next = current with
            {
                Turns = turns,
                PromiseFollowUps = current.PromiseFollowUps.Where(entry => entry.ConfirmedOn >= today.Date).ToArray(),
                PromiseFollowUpUnread = true
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

    internal bool MarkPromiseFollowUpRead()
    {
        if (!HasUnreadPromiseFollowUp || !careerLock.Wait(0))
            return false;
        try
        {
            var current = ReadState();
            if (!current.PromiseFollowUpUnread || current.Blocked)
                return false;
            var next = current with { PromiseFollowUpUnread = false };
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

    private static bool ValidPromiseFollowUp(PromiseFollowUp? entry) => entry is not null &&
        !string.IsNullOrWhiteSpace(entry.Key) && entry.Key.Length <= 128 &&
        !string.IsNullOrWhiteSpace(entry.Title) && entry.Title.Length <= 140 &&
        entry.ConfirmedOn.Year >= 1900 && entry.ConfirmedOn == entry.ConfirmedOn.Date;
}
