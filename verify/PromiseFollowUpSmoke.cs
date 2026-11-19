using System;
using System.IO;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class PromiseFollowUpSmoke
{
    public static string Run()
    {
        const string title = "按约定位置和角色出场";
        var key = new string('A', 64);
        var day = new DateTime(2026, 11, 17);
        var nextDay = day.AddDays(1);
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-waiver-" + Guid.NewGuid().ToString("N"));
        var checkpoints = new SaveCheckpoints(root);
        var session = checkpoints.CreateSession();
        var save = Path.Combine(root, "trial.fm");
        var requests = 0;
        Task<Reply> FakeModel(string input)
        {
            requests++;
            return Task.FromResult(new Reply("好的教练，我想试试用左脚接球再转身。", 1) { TrustChange = 1 });
        }

        Conversation Open(string directory) => new("PromiseFollowUp", "PromiseFollowUp", directory, FakeModel);
        string Restore() => checkpoints.Restore(save) ?? throw new InvalidOperationException("Test save did not restore");

        var chat = Open(session);
        chat.SendAsync("纳瓦兹，你今天接球前的观察做得很好。明天我们一起练接球后的第一脚。", day, null)
            .GetAwaiter().GetResult();
        var affinity = chat.Affinity;
        var trust = chat.Trust;
        Require(affinity > 0 && trust > 0 && chat.History.Count == 1 && requests == 1, "Initial chat has nonzero scores");

        const string before = "FM revision before the waiver";
        File.WriteAllText(save, before);
        Require(checkpoints.Store(save, session), "Store pre-waiver checkpoint");
        Require(!chat.TryRecordPromiseWaiver(key, "", day) && !chat.TryDeliverPromiseFollowUps(nextDay) &&
                chat.History.Count == 1, "A failed recording does not queue a confirmation");
        Require(chat.TryRecordPromiseWaiver(key, title, day.AddHours(16)) &&
                !chat.TryRecordPromiseWaiver(key, title, day) && !chat.HasUnreadPromiseFollowUp &&
                !chat.TryDeliverPromiseFollowUps(null) && !chat.TryDeliverPromiseFollowUps(day.AddDays(-1)) &&
                !chat.TryDeliverPromiseFollowUps(day.AddHours(23)) && chat.History.Count == 1,
            "A recorded waiver queues one follow-up and waits for the next known game date");

        const string queued = "FM revision with the waiver and pending message";
        File.WriteAllText(save, queued);
        Require(checkpoints.Store(save, session), "Store the checkpoint with an undelivered follow-up");
        var deliverySession = Restore();
        var restored = Open(deliverySession);
        Require(restored.LoadWarning is null && !restored.HasUnreadPromiseFollowUp &&
                !restored.TryDeliverPromiseFollowUps(day) && restored.TryDeliverPromiseFollowUps(nextDay),
            "An undelivered follow-up is restored and delivered on the next game day");
        Require(restored.History.Count == 2 && restored.HasUnreadPromiseFollowUp, "Delivery creates one unread message");
        var delivered = restored.History[^1];
        Require(delivered.IsIncoming && delivered.User.Length == 0 && delivered.GameDate == nextDay &&
                delivered.Assistant.Contains(title, StringComparison.Ordinal) && delivered.AffinityChange == 0 &&
                delivered.TrustChange == 0 && restored.Affinity == affinity && restored.Trust == trust && requests == 1,
            "Delivery identifies the promise without contacting the model or changing scores");

        var reopened = Open(deliverySession);
        Require(reopened.HasUnreadPromiseFollowUp && reopened.History.Count == 2 && reopened.History[^1] == delivered &&
                !reopened.TryDeliverPromiseFollowUps(nextDay) && !reopened.TryDeliverPromiseFollowUps(nextDay.AddDays(1)),
            "Opening the same conversation preserves the message without redelivery");
        Require(reopened.MarkPromiseFollowUpRead() && !reopened.HasUnreadPromiseFollowUp &&
                !reopened.MarkPromiseFollowUpRead(), "Reading the follow-up is idempotent");

        File.WriteAllText(save, "FM revision after reading the waiver message");
        Require(checkpoints.Store(save, deliverySession), "Store the checkpoint after delivery and reading");
        var read = Open(Restore());
        Require(read.History.Count == 2 && read.History[^1] == delivered && !read.HasUnreadPromiseFollowUp &&
                !read.TryDeliverPromiseFollowUps(nextDay.AddDays(2)) && requests == 1,
            "Reopening a completed revision keeps the read receipt and does not duplicate the message");

        File.WriteAllText(save, before);
        var rolledBack = Open(Restore());
        Require(rolledBack.History.Count == 1 && !rolledBack.HasUnreadPromiseFollowUp &&
                !rolledBack.TryDeliverPromiseFollowUps(nextDay), "An earlier save revision does not inherit the waiver message");
        return "PASS: promise waiver scheduling, next game day, save rollback, unread receipt, and no duplicate or scored follow-up.";
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Promise follow-up check failed: " + scenario);
    }
}
