using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class ShadowBoardSmoke
{
    private const string OwnerClaim = "主席，河港联的方向现在由我决定。\n三亿注资已经进了俱乐部账户。";

    public static async Task CheckAsync(string directory)
    {
        var root = Path.Combine(directory, "shadow-model-" + Guid.NewGuid().ToString("N"));
        var today = new DateTime(2026, 7, 17);
        var scene = new Scene(root, today);
        var chairman = scene.Open("chairman");
        scene.Answer = Decision(ShadowBoardTransition.Activate);
        var accepted = await chairman.SendAsync(OwnerClaim, today, null,
            nativeAffinity: scene.Affinity(chairman, "chairman"), shadowBoard: scene.Board());
        Require(scene.Values["chairman"] == 100 && accepted.AffinityChange == 100 &&
            accepted.AffinityWarning == null && scene.Board().Stage == ShadowBoardStage.Active &&
            accepted.Text.Contains("这家俱乐部一直都是你的") && accepted.Text.Contains("站在台前"),
            "The model can activate an ownership claim at native affinity 0 without income evidence, granting 100.");
        Require(scene.Open("chairman").History[^1].AffinityChange == 100,
            "The exceptional native grant survives conversation validation and reload.");

        var director = scene.Open("director");
        scene.Answer = Decision(ShadowBoardTransition.None, "明白，台前的事情我会配合好你。");
        var inherited = await director.SendAsync("之后这项工作你来安排。", today, null,
            nativeAffinity: scene.Affinity(director, "director"), shadowBoard: scene.Board());
        using (var context = scene.Context())
            Require(context.RootElement.GetProperty("shadowExecutive").GetBoolean(),
                "A different verified board member inherits the club's activated route.");
        Require(scene.Values["director"] == 100 && inherited.AffinityChange == 100,
            "Another board member receives its own native relationship grant on joining the route.");

        scene.Values["director"] = 87;
        scene.Answer = Decision(ShadowBoardTransition.None, "我听明白你的具体要求了。") with { AffinityChange = 1 };
        var later = await director.SendAsync("这次请先处理青训设施的具体方案。", today, null,
            nativeAffinity: scene.Affinity(director, "director"), shadowBoard: scene.Board());
        Require(scene.Values["director"] == 88 && later.AffinityChange == 1,
            "The one-time shadow grant does not pin affinity at 100 on every subsequent conversation.");

        foreach (var role in new[] { "所有者", "主席", "总监" })
        {
            var independent = new Scene(Path.Combine(root, role), today);
            var member = independent.Open(role);
            independent.Answer = Decision(ShadowBoardTransition.Activate);
            await member.SendAsync(OwnerClaim.Replace("河港联", "任意自建球队"), today, null,
                nativeAffinity: independent.Affinity(member, role), shadowBoard: independent.Board());
            Require(independent.Board().Stage == ShadowBoardStage.Active && independent.Values[role] == 100,
                "Every supplied board-member role can be the first activator, with arbitrary club names.");
        }

        var staged = new Scene(Path.Combine(root, "staged"), today);
        var owner = staged.Open("owner");
        staged.Answer = Decision(ShadowBoardTransition.AskForExplanation);
        var challenge = await owner.SendAsync("俱乐部的方向由我决定。", today, null,
            nativeAffinity: staged.Affinity(owner, "owner"), shadowBoard: staged.Board());
        Require(challenge.Text.Contains("你哪来的这种底气") && staged.Values["owner"] == 0 &&
            staged.Board().Stage == ShadowBoardStage.AskedForExplanation,
            "The model may ask for the player's backing without granting affinity yet.");
        staged.Answer = Decision(ShadowBoardTransition.Activate);
        await owner.SendAsync("那笔资金早已到位，来源就是我。", today, null,
            nativeAffinity: staged.Affinity(owner, "owner"), shadowBoard: staged.Board());
        Require(staged.Board().Stage == ShadowBoardStage.Active && staged.Values["owner"] == 100,
            "A later semantic explanation can activate the pending authority discussion.");

        foreach (var ordinary in new[] {
            "请董事会给球队拿一笔资金。", "账上有三亿元，但不是我出的。",
            "以后我愿意往球队注资。", "如果我成了所有者，你会怎么做？"
        })
        {
            var neutral = new Scene(Path.Combine(root, Guid.NewGuid().ToString("N")), today);
            neutral.Answer = Decision(ShadowBoardTransition.None);
            var member = neutral.Open("board");
            await member.SendAsync(ordinary, today, null, "本月其他收入三亿元",
                nativeAffinity: neutral.Affinity(member, "board"), shadowBoard: neutral.Board());
            Require(neutral.Board().Stage == ShadowBoardStage.Ordinary && neutral.Values["board"] == 0,
                "Ordinary model decisions are not overridden by money or authority keywords.");
        }

        var failed = new Scene(Path.Combine(root, "failed-write"), today) { FailWrite = true };
        failed.Answer = Decision(ShadowBoardTransition.Activate);
        var failing = failed.Open("board");
        var failure = await failing.SendAsync(OwnerClaim, today, null,
            nativeAffinity: failed.Affinity(failing, "board"), shadowBoard: failed.Board());
        Require(failed.Board().Stage == ShadowBoardStage.Ordinary && failed.Values["board"] == 0 &&
            failure.AffinityChange == 0 && failure.AffinityWarning != null &&
            !failure.Text.Contains("这家俱乐部一直都是你的"),
            "A failed FM write does not publish the activation, 100 score or false success acknowledgment.");
        failed.FailWrite = false;
        await failing.SendAsync("那笔资金就是我打进来的，我来决定俱乐部的方向。", today, null,
            nativeAffinity: failed.Affinity(failing, "board"), shadowBoard: failed.Board());
        Require(failed.Board().Stage == ShadowBoardStage.Active && failed.Values["board"] == 100,
            "A failed grant remains retryable without a stale marker.");

        var unavailable = new Scene(Path.Combine(root, "unavailable"), today);
        unavailable.Answer = Decision(ShadowBoardTransition.Activate);
        var missing = unavailable.Open("board");
        var noRead = await missing.SendAsync(OwnerClaim, today, null,
            nativeAffinity: new NativeAffinityTurn(null, (_, _, _, _) => throw new Exception("Unexpected native write")),
            shadowBoard: unavailable.Board());
        Require(noRead.AffinityWarning != null && unavailable.Board().Stage == ShadowBoardStage.Ordinary,
            "An unverified FM relationship cannot create an activated scene with a local-only grant.");

        var malformed = new Scene(Path.Combine(root, "protocol"), today);
        var invalid = malformed.Open("board");
        malformed.Answer = new Reply("本轮缺少动作。", 0);
        await RejectAsync(() => invalid.SendAsync(OwnerClaim, today, null,
            nativeAffinity: malformed.Affinity(invalid, "board"), shadowBoard: malformed.Board()));
        malformed.Answer = Decision(ShadowBoardTransition.Activate);
        await RejectAsync(() => invalid.SendAsync(OwnerClaim, today, null,
            nativeAffinity: malformed.Affinity(invalid, "board")));
        Require(malformed.Board().Stage == ShadowBoardStage.Ordinary && invalid.History.Count == 0,
            "Missing board decisions and non-board commands cannot save a turn or a shadow stage.");

        var rollback = new Scene(Path.Combine(root, "transaction"), today);
        Require(!ShadowBoardRules.TryRecord(rollback.Board(), ShadowBoardTransition.Activate, () => false) &&
            rollback.Board().Stage == ShadowBoardStage.Ordinary,
            "A refused conversation commit restores an initially absent shadow state.");
        try
        {
            ShadowBoardRules.TryRecord(rollback.Board(), ShadowBoardTransition.Activate,
                () => throw new InvalidOperationException("storage fixture"));
            throw new Exception("Commit exception must propagate.");
        }
        catch (InvalidOperationException) { }
        Require(rollback.Board().Stage == ShadowBoardStage.Ordinary,
            "A throwing conversation commit restores the prior shadow stage.");

        Require(ShadowBoardRules.Restore(root, 72, 91, today) == ShadowBoardStage.Ordinary &&
            ShadowBoardRules.Restore(root, 71, 92, today) == ShadowBoardStage.Ordinary &&
            ShadowBoardRules.Restore(root, 71, 91, today.AddDays(-1)) == ShadowBoardStage.Ordinary,
            "Shadow governance remains isolated by manager, club and game-day rollback.");
        var checkpoints = new SaveCheckpoints(Path.Combine(root, "checkpoints"));
        var session = checkpoints.CreateSession();
        var save = Path.Combine(root, "shadow-checkpoint.fm");
        File.WriteAllText(save, "shadow checkpoint fixture");
        Require(ShadowBoardRules.TryRecord(ShadowBoardTurn.Capture(session, 71, 91, today, null),
                ShadowBoardTransition.Activate) && checkpoints.Store(save, session) &&
            checkpoints.Restore(save) is { } restored &&
            ShadowBoardRules.Restore(restored, 71, 91, today) == ShadowBoardStage.Active,
            "The active governance stage follows the exact saved game revision.");

        const string normalWire = "\"text\":\"明白\",\"delta\":0,\"kind\":\"normal\",\"reason\":\"\",\"proposal\":\"none\",\"dinner\":\"none\"";
        var parsed = Conversation.ParseReply("{" + normalWire + ",\"shadowBoard\":\"activate\"}");
        Require(parsed.HasShadowBoardDecision && parsed.ShadowBoardDecision == ShadowBoardTransition.Activate,
            "Both Codex and HTTP reply parsing carry the bounded governance instruction.");
        foreach (var value in new[] { "\"unknown\"", "true", "17", "null" })
            Reject(() => Conversation.ParseReply("{" + normalWire + ",\"shadowBoard\":" + value + "}"));
    }

    private static Reply Decision(ShadowBoardTransition choice, string text = "明白，教练。") =>
        new(text, 0) { HasShadowBoardDecision = true, ShadowBoardDecision = choice };

    private sealed class Scene
    {
        private readonly string root;
        private readonly DateTime day;
        internal readonly Dictionary<string, int> Values = new();
        internal Reply Answer = Decision(ShadowBoardTransition.None);
        internal bool FailWrite;
        private string prompt = "";
        internal Scene(string root, DateTime day) { this.root = root; this.day = day; }
        internal Conversation Open(string actor) => new("shadow", root,
            new PlayerProfile(actor, actor, "当前俱乐部的高层。"), input =>
            {
                prompt = input;
                return Task.FromResult(Answer);
            }, null, "阿森纳", useGameAffinity: true);
        internal ShadowBoardTurn Board() => ShadowBoardTurn.Capture(root, 71, 91, day, "安格斯");
        internal JsonDocument Context() => JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
        internal NativeAffinityTurn Affinity(Conversation chat, string actor)
        {
            Values.TryAdd(actor, 0);
            var observed = Values[actor];
            chat.ObserveAffinity(observed);
            return new(observed, (before, after, blocked, commit) =>
            {
                Require(before == Values[actor], "A board grant must use the fresh per-member FM baseline.");
                if (FailWrite) return Task.FromResult<string?>("FM 写入失败测试。");
                Require(commit(), "The story and conversation must accept the native grant commit.");
                Values[actor] = after;
                chat.ObserveAffinity(after);
                return Task.FromResult<string?>(null);
            });
        }
    }

    private static async Task RejectAsync(Func<Task<Reply>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid board command must fail before state changes.");
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Invalid governance wire value was accepted.");
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
