using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class PromiseProtocolSmoke
{
    public static string Run() => CheckAsync().GetAwaiter().GetResult();

    private static async Task<string> CheckAsync()
    {
        var directory = Path.Combine(Path.GetDirectoryName(typeof(PromiseProtocolSmoke).Assembly.Location)!,
            "promise-protocol-" + Guid.NewGuid().ToString("N"));
        var today = new DateTime(2026, 11, 17);
        var position = new PromiseOptionForModel("P1",
            "在挑战目标期间至少一半可参加比赛内安排他打喜欢的前锋 (中)位置和肋部前锋角色", new DateTime(2027, 5, 10));
        var second = new PromiseOptionForModel("P2", position.Title, new DateTime(2027, 1, 25));
        var one = new[] { position };
        var two = new[] { position, second };
        const string approval = "教练，我愿意放弃这条承诺。";
        const string question = "你愿意放弃这条承诺吗？";
        const string naturalQuestion = "我们从赛季开始你就一直在前腰位置，你是没问题的，现在我们可以放弃那个承诺了吗？";
        const string naturalApproval = "教练，既然您一直把我放在前腰，也打算继续给我机会，我愿意接受这个安排，不再坚持踢前锋的承诺。"
            + "我还是想多参与进攻，希望您能告诉我，在前腰位置上我还需要提高什么。";
        const string normalJson = "{\"text\":\"我愿意先试试中场。\",\"delta\":0,\"trust\":0,\"kind\":\"normal\","
            + "\"reason\":\"先试踢\",\"proposal\":\"none\",\"dinner\":\"none\"}";
        var prompt = "";
        var noChoice = Conversation.ParseReply(normalJson[..^1] + ",\"promiseWaiverId\":\"none\"}");
        var modelReply = noChoice;
        Conversation Open(string id) => new(id, "test", directory, request: input =>
        {
            prompt = input;
            return Task.FromResult(modelReply);
        });
        var chat = Open("promise-protocol");

        async Task<Reply> Ask(string input, IReadOnlyList<PromiseOptionForModel> options) =>
            await chat.SendAsync(input, today, null, promiseOptions: options);

        Require((await Ask("承诺", one)).PromiseWaiverId == null,
            "The bare topic opens the promise context and the model can choose no action");
        using (var snapshot = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]))
        {
            var context = snapshot.RootElement.GetProperty("promiseContext");
            var option = context.GetProperty("pendingPromises")[0];
            Require(context.GetProperty("canWaivePromise").GetBoolean() &&
                option.GetProperty("id").GetString() == "P1" &&
                option.GetProperty("title").GetString() == position.Title &&
                option.GetProperty("deadline").GetString() == "2027-05-10", "Current native facts in prompt");
        }

        modelReply = new Reply(approval, 0) { PromiseWaiverId = "P1" };
        var bareRequest = await Open("promise-protocol-bare-request").SendAsync("放弃承诺", today, null, promiseOptions: one);
        Require(bareRequest.PromiseWaiverId == "P1",
            "The plain-language request reaches the sole verified record without earlier dialogue");
        modelReply = noChoice;
        foreach (var nonAction in new[]
        {
            "我希望你试试中场，可以吗？", "能告诉我这条承诺现在进展如何吗？",
            "我可以给你承诺，你会成为世界级中场。", "你不要放弃这条承诺，好吗？",
            "我不是叫你放弃承诺，你愿意踢中场吗？", "我不需要你放弃这条承诺，可以继续踢前腰。", "你已经放弃这条承诺了吗？",
            "你愿意取消 2027-05-10 到期的续约承诺吗？", "你愿意放弃 2027-11-11 到期的承诺吗？",
            "Would you not drop this promise?"
        })
        {
            Require((await Ask(nonAction, one)).PromiseWaiverId == null, "The model chose no action: " + nonAction);
            using var snapshot = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
            var context = snapshot.RootElement.GetProperty("promiseContext");
            Require(context.GetProperty("canWaivePromise").GetBoolean(),
                "Neither a keyword alone nor unrelated text during the active context hides verified options");
        }

        Require((await Ask(question, two)).PromiseWaiverId == null, "The model clarifies ambiguous candidate references");
        modelReply = new Reply(approval, 0) { PromiseWaiverId = "P1" };
        Require((await Ask("你愿意放弃 2027年5月10日到期的承诺吗？", two)).PromiseWaiverId == "P1",
            "Exact date distinguishes promises of the same type");
        Require((await Ask("你愿意放弃 2027-05-10 到期的承诺吗？", two)).PromiseWaiverId == "P1",
            "ISO deadline also selects the matching promise");
        modelReply = modelReply with { PromiseWaiverId = "P2" };
        Require((await Ask("你愿意放弃 2027-01-25 到期的承诺吗？", two)).PromiseWaiverId == "P2",
            "The model may select another verified record by date");
        modelReply = Conversation.ParseReply(normalJson);
        Require((await Ask(question, new[] { position with { Deadline = today.AddDays(-1) } })).PromiseWaiverId == null,
            "An expired option leaves no action available");
        using (var snapshot = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]))
        {
            var context = snapshot.RootElement.GetProperty("promiseContext");
            Require(!context.GetProperty("canWaivePromise").GetBoolean() &&
                context.GetProperty("pendingPromises").GetArrayLength() == 0,
                "The keyword still activates the topic when no verified record is actionable");
        }

        foreach (var unconfirmed in new[]
        {
            "教练，我不愿意放弃这条承诺。", "教练，我可以先试试踢中场，以后再说。",
            "教练，我同意放弃这条承诺。前提是我下场首发。"
        })
        {
            var noAction = normalJson.Replace("我愿意先试试中场。", unconfirmed, StringComparison.Ordinal);
            modelReply = Conversation.ParseReply(noAction[..^1] + ",\"promiseWaiverId\":\"none\"}");
            var result = await Ask(question, one);
            Require(result.PromiseWaiverId == null && result.Text == unconfirmed, "Model sent no waiver: " + unconfirmed);
        }
        const string indirectReply = "那就按你的想法来吧";
        var indirectJson = normalJson.Replace("我愿意先试试中场。", indirectReply, StringComparison.Ordinal);
        modelReply = Conversation.ParseReply(indirectJson[..^1] + ",\"promiseWaiverId\":\"P1\"}");
        var indirect = await Ask("你能放弃那条承诺吗？", one);
        Require(indirect.PromiseWaiverId == "P1" && indirect.Text == indirectReply,
            "The model's scoped action works with an indirect natural reply");

        modelReply = new Reply("All right, let's go with your plan.", 0) { PromiseWaiverId = "P1" };
        Require((await Ask("Would you drop this promise?", one)).PromiseWaiverId == "P1",
            "English request and matching model action");

        var action = modelReply;
        foreach (var incompatible in new[]
        {
            action with { Kind = InteractionKind.Uncertain },
            action with { AcceptDinner = true },
            action with { Proposal = RelationshipProposal.Friend }
        })
        {
            modelReply = incompatible;
            Require((await Ask(question, one)).PromiseWaiverId == null,
                "A non-normal reply or competing action cannot submit a waiver");
        }

        modelReply = new Reply(naturalApproval, 0) { PromiseWaiverId = "P1" };
        var screenshot = await Ask(naturalQuestion, one);
        Require(screenshot.PromiseWaiverId == "P1" && screenshot.Text == naturalApproval,
            "Screenshot: accept natural first-person waiver and preserve the follow-up training question");

        const string latestRequest = "从赛季初，你就一直在踢前腰，你也看到成果了，我们作为一支新的队伍，现在11连胜，并且在积分榜头名\n"
            + "基本上所有的比赛你都在前腰，你还看不到成果吗？\n"
            + "我需要你放弃这个承诺，我对你的计划一直在前腰，你要是坚持前锋的想法，这会破坏我的计划，你明白吗？";
        const string latestAgreement = "教练，我明白您的安排，也看到了球队的成绩。我还是喜欢踢前锋，"
            + "但我同意放弃那条关于前锋位置和肋部前锋角色出场比例的承诺，接下来专心争取踢好前腰。"
            + "您觉得我最需要提高的是传球，还是接球前的观察？";
        var latestJson = normalJson.Replace("我愿意先试试中场。", latestAgreement, StringComparison.Ordinal);
        modelReply = Conversation.ParseReply(latestJson[..^1] + ",\"promiseWaiverId\":\"P1\"}");
        var latestReply = await Ask(latestRequest, one);
        Require(latestReply.PromiseWaiverId == "P1" && latestReply.Text == latestAgreement,
            "Live reply: a remaining preference does not retract explicit waiver consent");
        Require((await Ask("我需要你放弃这个承诺。", one)).PromiseWaiverId == "P1",
            "Direct manager request works without a question particle");
        const string positioningPlan = "内森，我需要你放弃你的意向计划，就是让我多安排你踢前锋\n"
            + "我在每个位置上都有明确的规划，我已经看到你的未来，你的未来属于前腰。";
        const string gaveUpPosition = "教练，我愿意接受前腰的安排，不再坚持原来多踢前锋的约定。";
        modelReply = new Reply(gaveUpPosition, 0) { PromiseWaiverId = "P1" };
        Require(PromiseWaiverLanguage.MentionsPromise(positioningPlan) && (await Ask(positioningPlan, one)).PromiseWaiverId == "P1",
            "The 2026-11-24 screenshot position plan can reach the verified promise without an exact keyword");
        modelReply = noChoice;
        Require((await Ask(positioningPlan, two)).PromiseWaiverId == null &&
            (await Ask("请取消今天的训练计划。", one)).PromiseWaiverId == null &&
            (await Ask("不要放弃你踢前锋的意向计划。", one)).PromiseWaiverId == null,
            "Do not waive ambiguous records, a general plan, or a request to keep the position plan");
        modelReply = new Reply("我会先试试前腰。", 0) { HasPromiseWaiverDecision = true };
        Require((await Ask(positioningPlan, one)).PromiseWaiverId == null,
            "A position change without a matching consent action is still a conversation");
        modelReply = Conversation.ParseReply(latestJson[..^1] + ",\"promiseWaiverId\":\"none\"}");
        Require((await Ask(latestRequest, one)).PromiseWaiverId == null,
            "Explicit in-character agreement cannot override the model's no-action decision");
        Require((await Ask("你愿意放弃 2027-05-10 到期的承诺吗？", two)).PromiseWaiverId == null,
            "An empty model choice cannot resolve a request with several options");

        var legacyApproval = normalJson.Replace("我愿意先试试中场。", naturalApproval, StringComparison.Ordinal);
        modelReply = Conversation.ParseReply(legacyApproval);
        await RequireRejectedAsync(chat, () => Ask(naturalQuestion, one), "未返回承诺处理结果",
            "Official-sounding consent without the requested action field does not save");
        Require((await Ask(naturalQuestion, Array.Empty<PromiseOptionForModel>())).PromiseWaiverId == null,
            "A legacy response can still chat with no actionable promise");
        await RequireRejectedAsync(chat, () => Ask(naturalQuestion, two), "未返回承诺处理结果",
            "The action field is also required when the player needs to clarify");
        modelReply = noChoice;
        Require((await Ask(naturalQuestion, one)).PromiseWaiverId == null,
            "An empty model choice and willingness to try another position are insufficient");
        modelReply = Conversation.ParseReply(legacyApproval[..^1] + ",\"promiseWaiverId\":\"P2\"}");
        await RequireRejectedAsync(chat, () => Ask(naturalQuestion, one), "承诺无法与当前记录对应",
            "An unknown option ID rejects the turn");
        await RequireRejectedAsync(chat, () => Ask(naturalQuestion, Array.Empty<PromiseOptionForModel>()), "承诺无法与当前记录对应",
            "An action without any verified record rejects the turn");
        modelReply = Conversation.ParseReply(legacyApproval[..^1] + ",\"promiseWaiverId\":\"P1\"}");
        await RequireRejectedAsync(chat, () => Ask(naturalQuestion, new[] { position, second with { Id = "P1" } }),
            "承诺无法与当前记录对应",
            "A duplicate offered ID cannot identify a unique record");
        Require(Conversation.ParseReply(normalJson[..^1] + ",\"promiseWaiverId\":\"none\"}").PromiseWaiverId == null,
            "Extended schema with no action");
        Require(Conversation.ParseReply(normalJson[..^1] + ",\"promiseWaiverId\":\"P1\"}").PromiseWaiverId == "P1",
            "Extended wire reply reads exact token");
        foreach (var invalid in new[]
        {
            ",\"promiseWaiverId\":7}", ",\"promiseWaiverId\":\"../P1\"}",
            ",\"promiseWaiverId\":\"P1\",\"promiseWaiverId\":\"P2\"}"
        })
        {
            bool rejected = false;
            try { Conversation.ParseReply(normalJson[..^1] + invalid); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Malformed waiver field is rejected");
        }
        await CheckFollowUpContextAsync(directory, today, one, two);
        return "PASS: grounded promise choices, authoritative model action, natural Chinese and English replies, ambiguity, expiry, follow-up context and wire validation.";
    }

    private static async Task CheckFollowUpContextAsync(string directory, DateTime today,
        PromiseOptionForModel[] one, PromiseOptionForModel[] two)
    {
        const string start = "你的前锋承诺现在进展怎么样？";
        const string followUp = "那一条就算了吧。";
        var noChoice = new Reply("我知道您说的是哪一条承诺。", 0) { HasPromiseWaiverDecision = true };
        var agreement = new Reply("那就按你的想法来吧。", 0) { PromiseWaiverId = "P1" };
        var modelReply = noChoice;
        var prompt = "";

        Conversation Open(string id) => new(id, "test", directory, request: input =>
        {
            prompt = input;
            return Task.FromResult(modelReply);
        });

        void RequireContext(bool? canWaive, int optionCount, string check)
        {
            using var snapshot = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
            var hasContext = snapshot.RootElement.TryGetProperty("promiseContext", out var context) &&
                context.ValueKind == JsonValueKind.Object;
            if (!canWaive.HasValue)
            {
                Require(!hasContext, check);
                return;
            }

            Require(hasContext && context.GetProperty("canWaivePromise").GetBoolean() == canWaive.Value &&
                context.GetProperty("pendingPromises").GetArrayLength() == optionCount &&
                context.GetProperty("pendingPromises")[0].GetProperty("id").GetString() == "P1", check);
        }

        var active = Open("promise-follow-up-active");
        await active.SendAsync(start, today, null, promiseOptions: one);
        var unrelated = await active.SendAsync("你希望下场多参与进攻吗？", today, null, promiseOptions: one);
        RequireContext(true, 1, "A plain follow-up receives current native promise context");
        Require(unrelated.PromiseWaiverId == null, "The model can continue chatting without choosing a waiver");
        modelReply = agreement;
        var agreed = await active.SendAsync(followUp, today, null, promiseOptions: one);
        RequireContext(true, 1, "A contextual follow-up can target the sole current promise");
        Require(agreed.PromiseWaiverId == "P1" && agreed.Text == agreement.Text,
            "A follow-up needs no repeated keyword to use the model's verified choice");

        modelReply = noChoice;
        var ambiguous = Open("promise-follow-up-ambiguous");
        await ambiguous.SendAsync(start, today, null, promiseOptions: one);
        var unresolved = await ambiguous.SendAsync(followUp, today, null, promiseOptions: two);
        RequireContext(true, 2, "Follow-ups give the model both current options for clarification");
        Require(unresolved.PromiseWaiverId == null, "An explicit follow-up with two current promises is ambiguous");

        var expires = Open("promise-follow-up-expires");
        await expires.SendAsync(start, today, null, promiseOptions: one);
        foreach (var plain in new[] { "你怎么看今天的训练？", "这场比赛感觉如何？", "你觉得传球还需要练吗？" })
        {
            await expires.SendAsync(plain, today, null, promiseOptions: one);
            RequireContext(true, 1, "Native context remains available for the three following manager turns");
        }
        modelReply = new Reply("聊聊明天吧。", 0);
        await expires.SendAsync("明天还有什么想和我说的吗？", today, null, promiseOptions: one);
        RequireContext(null, 0, "On the fourth plain follow-up, model replies mentioning the promise have not extended its context");
        var expired = await expires.SendAsync(followUp, today, null, promiseOptions: one);
        RequireContext(null, 0, "The indirect follow-up does not revive expired context");
        Require(expired.PromiseWaiverId == null, "An implicit reference cannot trigger a waiver after the context expires");

        var nextDay = Open("promise-follow-up-next-day");
        modelReply = noChoice;
        await nextDay.SendAsync(start, today, null, promiseOptions: one);
        modelReply = new Reply("你说的是哪一条？", 0);
        var overnight = await nextDay.SendAsync(followUp, today.AddDays(1), null, promiseOptions: one);
        RequireContext(null, 0, "Previous-day references do not carry over");
        Require(overnight.PromiseWaiverId == null, "An implicit reference cannot trigger a waiver the next day");

        var anotherContact = Open("promise-follow-up-another-contact");
        var ungrounded = await anotherContact.SendAsync(followUp, today, null, promiseOptions: one);
        RequireContext(null, 0, "Recent promise context is scoped to the same contact");
        Require(ungrounded.PromiseWaiverId == null, "Another contact's promise discussion cannot ground a waiver");
        modelReply = agreement;
        await RequireRejectedAsync(anotherContact, () => anotherContact.SendAsync(followUp, today, null, promiseOptions: one),
            "承诺无法与当前记录对应", "A model cannot act on an ID it was not offered for this contact");
    }

    private static async Task RequireRejectedAsync(Conversation chat, Func<Task<Reply>> send, string errorText, string check)
    {
        var turnsBefore = chat.History.Count;
        var lastSave = chat.LastSavedAtUtc;
        try { await send(); }
        catch (InvalidOperationException error) when (error.Message.Contains(errorText, StringComparison.Ordinal))
        {
            Require(chat.History.Count == turnsBefore && chat.LastSavedAtUtc == lastSave, check + ": history changed");
            return;
        }
        throw new InvalidOperationException(check);
    }

    private static void Require(bool yes, string check)
    {
        if (!yes)
            throw new InvalidOperationException(check);
    }
}
