using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class NativeAffinityConversationSmoke
{
    public static string Run() => CheckAsync().GetAwaiter().GetResult();

    private static async Task<string> CheckAsync()
    {
        var directory = Path.Combine(Path.GetDirectoryName(typeof(NativeAffinityConversationSmoke).Assembly.Location)!,
            "native-affinity-conversation-" + Guid.NewGuid().ToString("N"));
        var today = new DateTime(2026, 11, 17);
        var profile = new PlayerProfile("affinity-fixture", "测试球员", "亲密度用例。") { StartingAffinity = 42 };
        var response = new Reply("明白，教练。谢谢你听我说。", 1) { TrustChange = 1 };
        var prompt = "";
        Task<Reply> Ask(string text)
        {
            prompt = text;
            return Task.FromResult(response);
        }

        Conversation Open(string career, bool native = true) => new(career, directory, profile, Ask, null, null,
            useGameAffinity: native);

        var changes = new List<(int Before, int After, bool Blocked)>();
        var gameValue = 100;
        var failWrite = false;
        NativeAffinityTurn Capture(Conversation chat) => new(gameValue, (before, after, blocked, commit) =>
        {
            Require(before == gameValue, "Write must use current FM relationship as its baseline");
            changes.Add((before, after, blocked));
            if (failWrite)
                return Task.FromResult<string?>("测试：FM 未接受变更。");
            Require(commit(), "A successful native write commits the matching conversation turn");
            gameValue = after;
            chat.ObserveAffinity(gameValue);
            return Task.FromResult<string?>(null);
        });

        JsonDocument PromptContext() => JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);

        var legacy = Open("existing", native: false);
        await legacy.SendAsync("旧版保存过的聊天。", today, null);
        Require(legacy.Affinity == 43 && legacy.Trust == 1, "Seed a stale local score independently of FM");

        var existing = Open("existing");
        existing.ObserveAffinity(gameValue);
        Require(existing.Affinity == 100 && existing.History.Count == 1,
            "The displayed affinity uses the FM value and keeps legacy dialogue");
        var saturated = await existing.SendAsync("你的努力我一直都看得到。", today, null, nativeAffinity: Capture(existing));
        Require(saturated.AffinityChange == 0 && changes.Count == 0 && gameValue == 100,
            "Affinity at the cap does not trigger another FM write");
        using (var context = PromptContext())
        {
            var root = context.RootElement;
            Require(root.GetProperty("privateAffinity").GetInt32() == 100 && root.GetProperty("privateAffinitySource").GetString() == "fm" &&
                !root.GetProperty("allowAffinityGain").GetBoolean() &&
                !root.TryGetProperty("professionalTrust", out _), "Model receives one verified FM score");
        }
        Require(existing.Trust == 1 && existing.History[^1].TrustChange == 0,
            "The legacy trust field is not advanced by real FM conversations");

        gameValue = 0;
        var fresh = Open("fresh");
        fresh.ObserveAffinity(gameValue);
        const string first = "刚才那场比赛，你的无球跑动帮助了球队。";
        const string second = "我们赛前说过的接球观察，你今天也做到了。";
        var firstReply = await fresh.SendAsync(first, today, null, nativeAffinity: Capture(fresh));
        var secondReply = await fresh.SendAsync(second, today, null, nativeAffinity: Capture(fresh));
        Require(firstReply.AffinityChange == 1 && secondReply.AffinityChange == 1 &&
            gameValue == 2 && fresh.Affinity == 2 && changes.Count == 2 && changes[0] == (0, 1, false) && changes[1] == (1, 2, false),
            "Distinct +1 turns go from no relation to 1 and then 2");

        fresh = Open("fresh");
        fresh.ObserveAffinity(gameValue);
        var repeated = await fresh.SendAsync(second.TrimEnd('。') + "！", today, null, nativeAffinity: Capture(fresh));
        Require(repeated.AffinityChange == 0 && changes.Count == 2 && fresh.Affinity == 2 && fresh.History.Count == 3,
            "After loading the dialogue, the same turn cannot replay the FM gain");

        gameValue = 23;
        failWrite = true;
        var failed = await fresh.SendAsync("我看到你今天主动承担了协调的工作。", today, null, nativeAffinity: Capture(fresh));
        Require(changes.Count == 3 && changes[^1] == (23, 24, false) && gameValue == 23 &&
            failed.AffinityChange == 0 && failed.AffinityWarning != null && Open("fresh", native: false).Affinity == 23 &&
            Open("fresh").History[^1].AffinityChange == 0, "A failed write saves the dialogue without the proposed gain");
        using (var context = PromptContext())
            Require(context.RootElement.GetProperty("privateAffinity").GetInt32() == 23, "Each request starts from fresh FM state");

        var missing = Open("unavailable");
        var unavailable = await missing.SendAsync("谢谢你今天帮球队适应新的打法。", today, null,
            nativeAffinity: new NativeAffinityTurn(null, (_, _, _, _) => throw new InvalidOperationException("Unexpected native write")));
        Require(unavailable.AffinityChange == 0 && unavailable.AffinityWarning != null && missing.History[^1].AffinityChange == 0,
            "An unavailable FM value cannot create a local-only gain");
        using (var context = PromptContext())
            Require(context.RootElement.GetProperty("privateAffinity").ValueKind == JsonValueKind.Null &&
                !context.RootElement.GetProperty("allowAffinityGain").GetBoolean(), "Unverified relation stays unknown in the prompt");

        gameValue = 91;
        failWrite = false;
        response = new Reply("我们聊点别的吧。", 0) { Kind = InteractionKind.Severe };
        var blocked = Open("blocked");
        blocked.ObserveAffinity(gameValue);
        var block = await blocked.SendAsync("我会伤害你。", today, null, nativeAffinity: Capture(blocked));
        Require(block.AffinityWarning == null && blocked.IsBlocked && gameValue < 0 &&
            changes.Count == 4 && changes[^1] == (91, gameValue, true) && Open("blocked").IsBlocked,
            "A block from high affinity writes the appropriate hateful relation and survives dialogue reload");

        var newReply = Conversation.ParseReply("{\"text\":\"知道了。\",\"delta\":1,\"kind\":\"normal\",\"reason\":\"\",\"proposal\":\"none\",\"dinner\":\"none\"}");
        Require(newReply.AffinityChange == 1 && newReply.TrustChange == 0, "The new wire format omits legacy trust");
        await VerifyPermanentRelations(directory, today);
        await VerifyManagerAssistantAffinity(directory, today);
        return "PASS: native affinity, duplicate protection, read/write failure, blocking, single-score replies, confirmed permanent relations, and assigned assistant's local affinity.";
    }

    private static async Task VerifyManagerAssistantAffinity(string directory, DateTime today)
    {
        var profile = new PlayerProfile("assistant-fixture", "专属助理", "助理亲密度用例。")
        {
            Age = 32, IsFemale = true, IsAssignedManagerAssistant = true
        };
        var response = new Reply("谢谢你一直这么信任我。", 1) { TrustChange = 1 };
        var unexpectedWrite = new NativeAffinityTurn(99, (_, _, _, _) => throw new InvalidOperationException(
            "The assigned manager assistant cannot read or write FM's native affinity."));
        var prompt = "";
        Conversation Open(string career, PlayerProfile selected, bool useGameAffinity = true) =>
            new(career, directory, selected, input =>
            {
                prompt = input;
                return Task.FromResult(response);
            }, null, null, useGameAffinity);

        var fresh = Open("new-assistant", profile);
        fresh.UpdateActorContext(profile with { IsAssignedManagerAssistant = false, IsFemale = null }, "新球队");
        fresh.ObserveAffinity(99);
        Require(fresh.UsesManagerAssistantAffinity && fresh.Affinity == 60,
            "A verified newly assigned assistant starts at 60 with no dependence on a native score");
        var first = await fresh.SendAsync("谢谢你今天帮我整理赛前工作。", today, null, nativeAffinity: unexpectedWrite);
        Require(first.AffinityChange == 1 && first.AffinityWarning == null && fresh.Affinity == 61 && fresh.Trust == 0,
            "An assistant's dialogue gains one point in the mod and does not create professional trust");
        using (var context = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]))
            Require(context.RootElement.GetProperty("privateAffinity").GetInt32() == 60 &&
                context.RootElement.GetProperty("privateAffinitySource").GetString() == "mod_assistant" &&
                context.RootElement.GetProperty("allowAffinityGain").GetBoolean(),
                "The model receives the assistant's actual local score and can build the same relationship");
        Require(Open("new-assistant", profile).Affinity == 61 && Open("other-career-assistant", profile).Affinity == 60,
            "Local affinity is saved, and another career starts independently");

        var previousProfile = profile with { IsAssignedManagerAssistant = false };
        var placeholder = Open("old-native-placeholder", previousProfile);
        var skipped = await placeholder.SendAsync("模组当时读不到亲密度。", today, null);
        var migrated = Open("old-native-placeholder", profile);
        Require(skipped.AffinityChange == 0 && migrated.History.Count == 1 && migrated.Affinity == 60,
            "Previous native-only placeholder preserves history without overriding the new starting affinity");

        var legacy = Open("old-local-assistant", previousProfile with { StartingAffinity = 99 }, useGameAffinity: false);
        await legacy.SendAsync("之前使用旧版建立了亲密度。", today, null);
        var confirmed = Open("old-local-assistant", profile);
        Require(confirmed.Affinity == 100 && confirmed.History.Count == 1,
            "Historical local affinity is preserved when migrating the assigned assistant");

        response = new Reply("我也愿意正式和你在一起。", 0) { Proposal = RelationshipProposal.Partner };
        await confirmed.SendAsync("我们愿意成为恋人吗？", today, null, nativeAffinity: unexpectedWrite);
        Require(confirmed.HasPendingProposal, "An adult female assistant with 100 can offer the existing romance proposal");
        var warning = confirmed.AcceptProposal((_, _) => throw new InvalidOperationException("Unexpected permanent native write"));
        var reopened = Open("old-local-assistant", profile);
        Require(warning == null && reopened.HasPermanentRelationship && reopened.RelationshipLabel == "爱人",
            "Confirmed romance is durable in mod state and does not require an FM Permanent flag");
    }

    private static async Task VerifyPermanentRelations(string directory, DateTime today)
    {
        var player = new PlayerProfile("permanent-fixture", "测试联系人", "持续性用例。") { Age = 32, IsFemale = true };
        foreach (var (stage, score, isFemale, shouldMarkPermanent, label) in new[]
        {
            (RelationshipProposal.Friend, 50, true, false, "朋友"),
            (RelationshipProposal.CloseFriend, 80, true, false, "好友"),
            (RelationshipProposal.Confidant, 100, false, true, "挚友"),
            (RelationshipProposal.Partner, 100, true, true, "爱人")
        })
        {
            var profile = player with { IsFemale = isFemale };
            var modelReply = new Reply("我愿意进一步确定关系。", 0) { Proposal = stage };
            Conversation Open() => new($"permanent-{stage}", directory, profile, _ => Task.FromResult(modelReply), null, null,
                useGameAffinity: true);

            var contact = Open();
            contact.ObserveAffinity(score);
            var unexpectedWrite = new NativeAffinityTurn(score, (_, _, _, _) =>
                throw new InvalidOperationException("Unexpected write of the permanent flag"), IsPermanent: false);

            await contact.SendAsync("我们能确定下一步的关系吗？", today, null, nativeAffinity: unexpectedWrite);
            Require(contact.HasPendingProposal, "Proposal alone stays pending and cannot mark a native relationship permanent");

            var attempted = 0;
            const string failedWrite = "测试：FM 持续性未写入。";
            var warning = contact.AcceptProposal((baseline, commit) =>
            {
                attempted++;
                Require(baseline == score, "Confirmation passes the observed native score without changing it");
                if (stage == RelationshipProposal.Partner)
                    return failedWrite;
                Require(commit(), "Native success commits the confirmed relationship stage");
                return null;
            });
            Require(attempted == (shouldMarkPermanent ? 1 : 0) && contact.RelationshipLabel == label && !contact.HasPendingProposal,
                "Only the two highest confirmed relationship tiers request Permanent");
            Require(warning == (stage == RelationshipProposal.Partner ? failedWrite : null),
                "A native failure is reported while keeping the confirmed relationship stage");

            contact = Open();
            contact.ObserveAffinity(score);
            Require(contact.RelationshipLabel == label && !contact.HasPendingProposal,
                "Both native success and failure preserve the confirmed stage after reload");

            modelReply = new Reply("以后我们也可以多聊聊。", 0);
            var repaired = 0;
            var missingFlag = new NativeAffinityTurn(score, (before, after, blocked, commit) =>
            {
                repaired++;
                Require(before == score && after == score && !blocked && commit(),
                    "The next conversation can repair an unchecked flag without changing the affinity score");
                return Task.FromResult<string?>(null);
            }, IsPermanent: false);
            var followUp = await contact.SendAsync("谢谢你一直陪着我。", today, null, nativeAffinity: missingFlag);
            Require(repaired == (shouldMarkPermanent ? 1 : 0) && followUp.AffinityChange == 0 && followUp.AffinityWarning == null,
                "Only a confirmed top-tier relationship repairs an unchecked Permanent flag");

            var alreadyPermanent = unexpectedWrite with { IsPermanent = true };
            await contact.SendAsync("今天我们还可以晚些时候交流。", today, null, nativeAffinity: alreadyPermanent);
            Require(contact.History.Count == 3, "An already permanent relationship saves regular dialogue without rewriting the flag");
        }
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new InvalidOperationException(check);
    }
}
