using System;
using System.IO;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class RelationshipMessageSmoke
{
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<string> RunAsync()
    {
        var data = Path.Combine(Path.GetDirectoryName(typeof(RelationshipMessageSmoke).Assembly.Location)!,
            "relationship-message-" + Guid.NewGuid().ToString("N"));
        var day = new DateTime(2026, 11, 23);
        CheckGameFacts(day);
        var profile = new PlayerProfile("relationship-message-person", "测试人物", "关系来信测试")
        {
            Age = 26, IsFemale = true, StartingAffinity = 100
        };
        var modelRequests = 0;
        Task<Reply> ReplyTo(string prompt)
        {
            modelRequests++;
            return Task.FromResult(new Reply("愿意和你确认这段关系。", 0) { Proposal = RelationshipProposal.Partner });
        }
        Conversation Open(string career, PlayerProfile? actor = null) =>
            new(career, data, actor ?? profile, ReplyTo, null, "塞特尔");

        var partner = Open("partner");
        await partner.SendAsync("你愿意与我确认关系吗？", day, null);
        var win = new RelationshipGameEvent("fixture:2026:42", day.AddDays(2), "主场以 5 比 0 赢了伯恩利");
        Require(!partner.TryDeliverRelationshipMessage(day.AddDays(2), new[] { win }),
            "An unaccepted proposal cannot send relationship mail");
        partner.AcceptProposal(gameDate: day);
        Require(partner.RelationshipLabel == "爱人" && !partner.TryDeliverRelationshipMessage(null) &&
            !partner.TryDeliverRelationshipMessage(day.AddDays(1)) &&
            !partner.TryDeliverRelationshipMessage(day.AddDays(1), new[] { win }),
            "Confirmation starts the cooldown and future news cannot arrive early");
        Require(partner.TryDeliverRelationshipMessage(day.AddDays(2), new[] { win }) &&
            partner.History[^1].IsIncoming && partner.History[^1].Assistant.Contains("5 比 0", StringComparison.Ordinal) &&
            partner.History[^1].GameDate == day.AddDays(2) && partner.History[^1].AffinityChange == 0 &&
            partner.HasUnreadRelationshipMessage, "A verified win arrives as a dated unread message tied to the relationship");
        Require(modelRequests == 1, "Proactive messages need no model request");

        var reopened = Open("partner");
        Require(reopened.HasUnreadRelationshipMessage && !reopened.TryDeliverRelationshipMessage(day.AddDays(2), new[] { win }) &&
            reopened.MarkRelationshipMessageRead(), "Reload retains unread state without sending the message twice");
        reopened = Open("partner");
        Require(!reopened.HasUnreadRelationshipMessage && !reopened.TryDeliverRelationshipMessage(day.AddDays(3), new[] { win }) &&
            reopened.History.Count == 2, "Reading the letter persists and the same fixture cannot notify again the next day");
        Require(reopened.TryDeliverRelationshipMessage(day.AddDays(8)) && reopened.History[^1].IsIncoming &&
            !reopened.History[^1].Assistant.Contains("伯恩利", StringComparison.Ordinal) &&
            !reopened.TryDeliverRelationshipMessage(day.AddDays(20)), "A quiet check-in carries no invented result or unread backlog");
        Require(!Open("different-save").HasUnreadRelationshipMessage, "Relationship mail does not cross save branches");

        foreach (var age in new int?[] { null, 17 })
        {
            var unverified = Open("age-guard-" + age, profile with
            {
                Age = age, StartingRelationship = RelationshipProposal.Partner
            });
            Require(!unverified.TryDeliverRelationshipMessage(day, new[] { win with { Date = day } }) &&
                !unverified.HasUnreadRelationshipMessage, "An adult-only letter may not be sent to a minor or unverified adult");
        }
        var friend = Open("close-friend", profile with { StartingRelationship = RelationshipProposal.CloseFriend });
        Require(!friend.TryDeliverRelationshipMessage(day, new[] { win with { Date = day } }),
            "The lower friendship stage does not receive these messages");

        var confidant = Open("confidant", profile with
        {
            IsFemale = false, StartingRelationship = RelationshipProposal.Confidant
        });
        Require(!confidant.TryDeliverRelationshipMessage(day, new[] { win with { Date = day.AddDays(-1) } }),
            "Old top tier saves start their timer without replaying old news");
        Require(confidant.TryRecordPromiseWaiver("promise-test", "先前的承诺", day) &&
            !confidant.TryDeliverRelationshipMessage(day.AddDays(1), new[] { win with { Date = day.AddDays(1) } }) &&
            confidant.TryDeliverPromiseFollowUps(day.AddDays(1)) && confidant.MarkPromiseFollowUpRead() &&
            !confidant.TryDeliverRelationshipMessage(day.AddDays(1), new[] { win with { Date = day.AddDays(1) } }),
            "An existing promise follow-up takes precedence and there is no second incoming message the same day");
        Require(confidant.TryDeliverRelationshipMessage(day.AddDays(6)) && confidant.RelationshipLabel == "挚友",
            "The confidant receives a check-in after the existing follow-up");
        return "Relationship messages checks passed (confirmed stage, consent, age, game-day scheduling, verified event, unread and reload).";
    }

    private static void CheckGameFacts(DateTime day)
    {
        var home = RelationshipEventRules.Match(day, day, 1, 22, 1, 2, 5, 0, true, "塞特尔", "伯恩利");
        var away = RelationshipEventRules.Match(day, day, 1, 23, 2, 1, 1, 4, true, "伯恩利", "塞特尔");
        Require(home?.Summary == "以 5 比 0 赢了伯恩利" && away?.Summary == "以 4 比 1 赢了伯恩利",
            "Use the verified team side for a real win, home or away");
        Require(RelationshipEventRules.Match(day, day, 1, 22, 2, 3, 5, 0, true, "伯恩利", "利兹") == null &&
            RelationshipEventRules.Match(day, day, 1, 22, 1, 2, 1, 4, true, "塞特尔", "伯恩利") == null &&
            RelationshipEventRules.Match(day, day, 1, 22, 1, 2, 5, 0, false, "塞特尔", "伯恩利") == null,
            "Exclude other teams, losses and unfinished fixtures");
        var first = RelationshipEventRules.Trophy(day, day, 1, 1, 10, "塞特尔夺得足总杯冠军", "塞特尔");
        var same = RelationshipEventRules.Trophy(day, day, 1, 1, 11, "塞特尔夺得足总杯冠军", "塞特尔");
        Require(first != null && first == same &&
            RelationshipEventRules.Trophy(day, day, 1, 2, 10, "塞特尔夺得足总杯冠军", "塞特尔") == null &&
            RelationshipEventRules.Trophy(day, day, 1, 1, 10, "谁能阻止塞特尔夺得足总杯冠军？", "塞特尔") == null &&
            RelationshipEventRules.Trophy(day.AddDays(-7), day, 1, 1, 10, "塞特尔夺得足总杯冠军", "塞特尔") == null,
            "Accept only a recent completed win in a team-scoped headline and deduplicate the same story");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
