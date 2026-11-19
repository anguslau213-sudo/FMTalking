using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class GameActionContextSmoke
{
    public static string Run() => CheckAsync().GetAwaiter().GetResult();

    private static async Task<string> CheckAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-game-action-context-" + Guid.NewGuid().ToString("N"));
        var checkpoints = new SaveCheckpoints(root);
        var session = checkpoints.CreateSession();
        var date = new DateTime(2026, 11, 19);
        var prompt = "";
        var profile = new PlayerProfile("action-context-person", "测试董事", "执行回执用例。");
        Conversation Open(string career = "action-context", string? directory = null, PlayerProfile? actor = null) =>
            new(career, directory ?? session, actor ?? profile, text =>
            {
                prompt = text;
                return Task.FromResult(new Reply("我会按已经核实的结果继续沟通。", 0));
            }, null, null);
        async Task<JsonDocument> Ask(Conversation chat, DateTime? day = null, BoardFundingContext? funding = null)
        {
            await chat.SendAsync("刚才谈的事情现在是什么情况？", day ?? date, null, boardFunding: funding);
            return JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
        }
        static JsonElement Results(JsonDocument context) => context.RootElement.GetProperty("gameActionResults").GetProperty("results");
        static GameActionReceipt Receipt(string id, GameActionKind kind, GameActionStatus status, DateTime day, string detail,
            string? club = "测试俱乐部") => new(id, kind, status, day, detail, club);

        var chat = Open();
        using (var fresh = await Ask(chat))
            Require(fresh.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null,
                "Fresh dialogue has no invented execution receipt");
        await chat.SendAsync("你不要放弃那条承诺。", date, null, promiseOptions: Array.Empty<PromiseOptionForModel>(),
            boardFunding: new BoardFundingContext(null, "此处没有可执行的报价。"));
        using (var incidental = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]))
            Require(incidental.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null,
                "A negative or incidental keyword and model no-action do not create failed execution history");
        var offer = Receipt("offer-1", GameActionKind.BoardFunding, GameActionStatus.PendingConfirmation, date,
            "董事会提出 £10,000，等待玩家确认，余额尚未变更。");
        Require(chat.TryRecordGameAction(offer) && chat.TryRecordGameAction(offer), "A real offer is saved and duplicate registration is harmless");
        var liveOffer = new BoardFundingContext(null, "已有有效报价，本轮不重复报价。")
        { PendingConfirmationId = offer.Id, PendingAmount = "£10,000" };
        using (var waiting = await Ask(chat, funding: liveOffer))
        {
            Require(Results(waiting).GetArrayLength() == 1 && Results(waiting)[0].GetProperty("status").GetString() == "pending_confirmation",
                "A still-live same-contact offer remains pending during follow-up dialogue");
            var funding = waiting.RootElement.GetProperty("boardFunding");
            Require(!funding.GetProperty("canOffer").GetBoolean() && funding.GetProperty("pendingConfirmation").GetBoolean() &&
                funding.GetProperty("pendingAmount").GetString() == "£10,000", "A pending offer cannot produce a second funding action");
        }
        var paid = offer with { Status = GameActionStatus.Succeeded, Detail = "已核实俱乐部余额增加 £10,000。" };
        Require(chat.TryRecordGameAction(paid) && !chat.TryRecordGameAction(offer with { Status = GameActionStatus.Expired }),
            "Verified success cannot be downgraded by a stale pending button");
        using (var completed = await Ask(Open()))
            Require(Results(completed)[0].GetProperty("status").GetString() == "succeeded" &&
                Results(completed)[0].GetProperty("detail").GetString() == paid.Detail,
                "A new conversation instance receives the actual verified outcome");

        var second = Receipt("offer-2", GameActionKind.BoardFunding, GameActionStatus.PendingConfirmation, date,
            "另一笔真实报价等待确认。");
        Require(chat.TryRecordGameAction(second), "Register a pending offer before reload");
        using (var restored = await Ask(Open()))
        {
            var last = Results(restored)[Results(restored).GetArrayLength() - 1];
            Require(last.GetProperty("status").GetString() == "expired" &&
                last.GetProperty("detail").GetString()!.Contains("未找到最终执行回执", StringComparison.Ordinal) &&
                !last.GetProperty("detail").GetString()!.Contains("未执行入账", StringComparison.Ordinal),
                "Reloaded pending state loses its executable button without inventing an unchanged balance");
        }

        foreach (var status in new[] { GameActionStatus.Failed, GameActionStatus.Uncertain })
            Require(chat.TryRecordGameAction(Receipt("promise-" + status, GameActionKind.PromiseWaiver, status, date,
                status == GameActionStatus.Failed ? "原版承诺未移除。" : "承诺写入结果无法核实。")), "Store a concrete attempted action outcome");
        using (var failed = await Ask(Open()))
        {
            var outcomes = Results(failed).EnumerateArray().Select(item => item.GetProperty("status").GetString()).ToArray();
            Require(outcomes.Contains("failed") && outcomes.Contains("uncertain"), "Failure and uncertainty remain distinct from success");
        }
        using (var differentActor = await Ask(Open(actor: profile with { Id = "other-actor" })))
            Require(differentActor.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null,
                "A different person cannot inherit another person's execution status");
        using (var differentCareer = await Ask(Open(career: "another-career")))
            Require(differentCareer.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null,
                "A different career key cannot inherit an execution status");

        var future = Receipt("future", GameActionKind.PromiseWaiver, GameActionStatus.Failed, date.AddDays(1), "未来日期的测试记录。");
        Require(chat.TryRecordGameAction(future), "Store dated receipt for chronology check");
        using (var earlier = await Ask(Open()))
            Require(!Results(earlier).EnumerateArray().Any(item => item.GetProperty("detail").GetString() == future.Detail),
                "Future execution facts are not exposed to an earlier game day");

        var promiseSession = checkpoints.CreateSession();
        var save = Path.Combine(root, "action-copy.fm");
        const string beforeWaiver = "save before waiver";
        File.WriteAllText(save, beforeWaiver);
        Require(checkpoints.Store(save, promiseSession), "Checkpoint a pre-action save with no invented receipt");
        var promise = Open(directory: promiseSession);
        var success = Receipt("waiver-verified", GameActionKind.PromiseWaiver, GameActionStatus.Succeeded, date,
            "原版位置角色承诺已核实移除，次日来信已登记。", new string('界', 101));
        Require(success.ClubName == null && promise.TryRecordPromiseWaiver(new string('A', 64), "位置角色", date, success),
            "Promise follow-up and execution receipt commit together; optional long club metadata cannot veto the action");
        File.WriteAllText(save, "save with verified waiver");
        Require(checkpoints.Store(save, promiseSession), "Checkpoint includes execution receipts with pending promise follow-up");
        var restoredSession = checkpoints.Restore(save) ?? throw new Exception("Checkpoint restore failed.");
        var restoredPromise = Open(directory: restoredSession);
        using (var proof = await Ask(restoredPromise))
            Require(Results(proof)[0].GetProperty("status").GetString() == "succeeded" &&
                Results(proof)[0].GetProperty("club").ValueKind == JsonValueKind.Null,
                "Save restore retains verified receipt and drops invalid optional display metadata");
        Require(restoredPromise.TryDeliverPromiseFollowUps(date.AddDays(1)) &&
            !restoredPromise.TryDeliverPromiseFollowUps(date.AddDays(2)), "The same checkpoint delivers the player's follow-up once");
        File.WriteAllText(save, beforeWaiver);
        var rolledBack = Open(directory: checkpoints.Restore(save) ?? throw new Exception("Earlier checkpoint restore failed."));
        using (var earlierSave = await Ask(rolledBack))
            Require(earlierSave.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null &&
                !rolledBack.TryDeliverPromiseFollowUps(date.AddDays(1)),
                "Rolling back the FM revision removes later action receipts and queued follow-ups together");

        var legacySession = checkpoints.CreateSession();
        var legacy = Open(directory: legacySession);
        using (await Ask(legacy)) { }
        var legacyPath = Directory.GetFiles(Path.Combine(legacySession, "conversations"), "*.json").Single();
        var legacyNode = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        legacyNode.Remove("gameActions");
        File.WriteAllText(legacyPath, legacyNode.ToJsonString());
        var reopenedLegacy = Open(directory: legacySession);
        Require(reopenedLegacy.LoadWarning == null, "Older stores without gameActions remain readable");
        using (var old = await Ask(reopenedLegacy))
            Require(old.RootElement.GetProperty("gameActionResults").ValueKind == JsonValueKind.Null,
                "Older stores do not invent historical actions");

        var boundedSession = checkpoints.CreateSession();
        var bounded = Open(directory: boundedSession);
        for (var i = 0; i < 12; i++)
            Require(bounded.TryRecordGameAction(Receipt("attempt-" + i, GameActionKind.PromiseWaiver, GameActionStatus.Failed,
                date, "尝试结果 " + i)), "Save bounded execution status");
        using (var limit = await Ask(Open(directory: boundedSession)))
            Require(Results(limit).GetArrayLength() == 8 && Results(limit)[0].GetProperty("detail").GetString() == "尝试结果 4",
                "Only the eight most recent concrete outcomes are retained");
        Require(bounded.TryRecordGameAction(Receipt("future-supported-year", GameActionKind.PromiseWaiver, GameActionStatus.Failed,
            new DateTime(2300, 1, 1), "日期不受测试版本年份限制。", "\n")),
            "Optional metadata and a valid later game year do not block receipts");
        Require(!bounded.TryRecordGameAction(Receipt("invalid", GameActionKind.PromiseWaiver, GameActionStatus.PendingConfirmation,
            date, "承诺没有额外确认按钮。")), "An unsupported action/status combination is rejected");
        return "PASS: persisted game action facts, live pending follow-up, success/failure/uncertainty, reload expiry, save checkpoints, actor/career scope, old stores and bounded history.";
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("Game action context: " + detail);
    }
}
