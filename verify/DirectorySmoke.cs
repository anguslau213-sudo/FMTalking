using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FmPlayerTalk;

public static class DirectorySmoke
{
    public static string Run()
    {
        const string wang = "\u0001BAAAACAxMTEBIgMgMDExHAAAAA==\u0002王大雷\u0001";
        const string club = "\u0001AwAAACAxMTEBIgMgMDExHAAAAA==\u0002山东泰山\u0001";
        if (FmDisplayText.Read(wang) != "王大雷" || FmDisplayText.Read(club + "20岁以下青年队") != "山东泰山20岁以下青年队" ||
            FmDisplayText.Read(wang + " · " + club) != "王大雷 · 山东泰山" ||
            FmDisplayText.Read("后卫\u200b（左中）") != "后卫（左中）" || FmDisplayText.Read("Kylian Mbappé") != "Kylian Mbappé" ||
            FmDisplayText.Read("\u0001BAAAACAxMTEBIgMgMDExHAAAAA==") != null || FmDisplayText.Initials("王大雷") != "王")
            throw new InvalidOperationException("FM link metadata must not be shown as part of a roster name.");
        var birth = new DateTime(1994, 3, 9);
        if (!ClubDirectory.UsesLegacyAssistantKey(2002119250u, 2002119251u, birth) ||
            ClubDirectory.UsesLegacyAssistantKey(2002119252u, 2002119251u, birth) ||
            ClubDirectory.UsesLegacyAssistantKey(2002119250u, 23276508u, birth) ||
            ClubDirectory.UsesLegacyAssistantKey(2002119250u, 2002119251u, birth.AddDays(1)))
            throw new InvalidOperationException("Only the verified saved assistant can reuse the old history key.");
        if (PlayerIntroductionRules.ParseTarget("助理，能不能帮我找一下基利安·姆巴佩的联系方式？") != "基利安·姆巴佩" ||
            PlayerIntroductionRules.ParseTarget("帮我查一下Kylian Mbappé的联系方式") != "Kylian Mbappé" ||
            PlayerIntroductionRules.ParseTarget("我需要姆巴佩的联系方式，你帮我去找一找") != "姆巴佩" ||
            PlayerIntroductionRules.ParseTarget("你帮我去找一找；我需要姆巴佩的联系方式。") != "姆巴佩" ||
            PlayerIntroductionRules.ParseTarget("刘一凡，我想要哈兰德的联络方式，麻烦你试试看。") != "哈兰德" ||
            PlayerIntroductionRules.ParseTarget("姆巴佩的联系方式可以帮我去找找吗？") != "姆巴佩" ||
            PlayerIntroductionRules.ParseTarget("我希望你帮我找一找Kylian Mbappé的联系渠道。") != "Kylian Mbappé" ||
            PlayerIntroductionRules.ParseTarget("查找球员：一凡，我需要你给我姆巴佩的联系方式") != "姆巴佩" ||
            PlayerIntroductionRules.ParseTarget("查找球员：不要帮我找姆巴佩的联系方式") != null ||
            PlayerIntroductionRules.ParseTarget("查找球员：给我姆巴佩的联系方式，也给我哈兰德的联系方式") != null ||
            PlayerIntroductionRules.ParseNameSupplement("基利安·姆巴佩") != "基利安·姆巴佩" ||
            PlayerIntroductionRules.ParseNameSupplement("Kylian Mbappé") != "Kylian Mbappé" ||
            PlayerIntroductionRules.ParseNameSupplement("今天比赛挺顺利的") != null ||
            PlayerIntroductionRules.ParseTarget("查找球员：2002119250") != "2002119250" ||
            PlayerIntroductionRules.ParseTarget("球队拿到了他的联系方式，今天比赛挺顺利的。") != null ||
            PlayerIntroductionRules.ParseTarget("我已经有梅森的联系方式。") != null ||
            PlayerIntroductionRules.ParseTarget("我不需要梅森的联系方式。") != null ||
            PlayerIntroductionRules.ParseTarget("这是姆巴佩的联系方式。") != null ||
            PlayerIntroductionRules.ParseTarget("别帮我找姆巴佩的联系方式了。") != null ||
            PlayerIntroductionRules.ParseTarget("我需要姆巴佩的联系方式，不过你先别去找。") != null ||
            PlayerIntroductionRules.ParseTarget("我需要姆巴佩的联系方式，取消这个请求。") != null ||
            PlayerIntroductionRules.ParseTarget("帮我找姆巴佩的联系方式，再帮我找哈兰德的联系方式。") != null ||
            PlayerIntroductionRules.ParseTarget("帮我找他的联系方式。") != null ||
            PlayerIntroductionRules.ParsePublicId("#2002119250") != 2002119250u ||
            PlayerIntroductionRules.ParsePublicId("4294967295") != null ||
            PlayerIntroductionRules.MatchKey("Kylian Mbappé") != PlayerIntroductionRules.MatchKey("Kylian-Mbappe") ||
            !PlayerIntroductionRules.SameNationality(10, 10) || PlayerIntroductionRules.SameNationality(0, 0) ||
            PlayerIntroductionRules.SameNationality(10, 20))
            throw new InvalidOperationException("Only concrete player introductions can use the verified nationality or reputation path.");
        if (PlayerIntroductionRules.AllowsInternational(null, 1000) ||
            PlayerIntroductionRules.AllowsInternational(3000, null) ||
            PlayerIntroductionRules.AllowsInternational(-1, 1000) ||
            PlayerIntroductionRules.AllowsInternational(10000, 10001) ||
            PlayerIntroductionRules.AllowsInternational(2999, 4500) ||
            !PlayerIntroductionRules.AllowsInternational(3000, 4500) ||
            !PlayerIntroductionRules.AllowsInternational(1000, 2500) ||
            PlayerIntroductionRules.AllowsInternational(1000, 2501) ||
            !PlayerIntroductionRules.AllowsInternational(0, 1500))
            throw new InvalidOperationException("International introductions permit a gap of up to 1500 valid reputation points, with no minimum.");
        VerifyIntroductionScheduling();
        VerifyIntroductionNameAliases();
        VerifyIntroductionConfirmation();
        VerifyLegacyIntroductionContinuation();
        VerifyIntroductionModelChoice();
        return "Directory checks passed (display text, assistant identity, natural requests, bounded aliases, candidate confirmation, next-day requests, and save rollback).";
    }

    private static void VerifyIntroductionNameAliases()
    {
        var aliases = PlayerIntroductionNameResolver.ParseAliases("[\"Kylian Mbappé\",\"Kylian-Mbappe\",\"姆巴佩\"]", "姆巴佩");
        Require(aliases.Count == 1 && aliases[0] == "Kylian Mbappé", "Name spellings are normalized and deduplicated");
        foreach (var unsafeReply in new[]
        {
            "[\"85139014\"]", "[85139014]", "[\"Kylian Mbappé\",null]", "{\"name\":\"Kylian Mbappé\",\"id\":85139014}",
            "[\"One\",\"Two\",\"Three\",\"Four\"]", "Kylian Mbappé", "[\"Kylian\\nMbappé\"]"
        })
            Require(PlayerIntroductionNameResolver.ParseAliases(unsafeReply, "姆巴佩").Count == 0,
                "Malformed replies and invented IDs or metadata cannot become candidates");
        var calls = 0;
        var resolved = PlayerIntroductionNameResolver.ResolveAliasesAsync("姆巴佩", prompt =>
        {
            calls++;
            Require(prompt.Contains("不猜测或返回人员ID", StringComparison.Ordinal), "Alias lookup cannot assert career identity");
            return Task.FromResult(new Reply("[\"Kylian Mbappé\",\"Mbappé\"]", 0));
        }).GetAwaiter().GetResult();
        Require(calls == 1 && resolved.SequenceEqual(new[] { "Kylian Mbappé", "Mbappé" }), "One bounded model response provides spellings only");
        var action = PlayerIntroductionNameResolver.ResolveAliasesAsync("姆巴佩", _ =>
            Task.FromResult(new Reply("[\"Kylian Mbappé\"]", 1))).GetAwaiter().GetResult();
        Require(action.Count == 0, "An alias response cannot modify affinity or issue a game action");
    }

    private static void VerifyIntroductionConfirmation()
    {
        var day = new DateTime(2026, 11, 17);
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-introduction-choice-" + Guid.NewGuid().ToString("N"));
        var assistant = new PlayerProfile("assistant-choice-fixture", "助理", "球员介绍确认测试。") { IsAssignedManagerAssistant = true };
        Conversation Open() => new("introductions", root, assistant,
            _ => throw new InvalidOperationException("Candidate confirmation should not call the model."), null, null);
        var chat = Open();
        Require(chat.TryQueuePlayerIntroduction("我需要姆巴佩的联系方式，你帮我去找一找", "姆巴佩", day), "Queue the screenshot request");
        var unresolved = chat.UnresolvedPlayerIntroduction ?? throw new InvalidOperationException("Unresolved request missing.");
        var candidates = new[] { new PlayerIntroductionCandidate(85139014u, "Kylian Mbappé", "皇家马德里") };
        Require(chat.TryOfferPlayerIntroductionCandidates(unresolved, candidates, day) &&
            chat.DuePlayerIntroduction(day.AddDays(5)) == null && chat.UnresolvedPlayerIntroduction == null &&
            chat.PlayerIntroductionAwaitingConfirmation?.Candidates[0].PublicId == 85139014u &&
            chat.History[^1].Assistant.Contains("皇家马德里", StringComparison.Ordinal), "A real candidate is offered without scheduling an unconfirmed identity");
        var reloaded = Open();
        Require(reloaded.PlayerIntroductionAwaitingConfirmation?.Candidates[0].Name == "Kylian Mbappé" &&
            !reloaded.TryRespondToPlayerIntroductionChoice("今天比赛挺顺利的", day, out var unrelated) && !unrelated &&
            !reloaded.TryRespondToPlayerIntroductionChoice("他不是我想找的人吗", day, out var question) && !question &&
            !reloaded.TryRespondToPlayerIntroductionChoice("是这个吗？", day, out var uncertain) && !uncertain,
            "The actual UID and question survive reload and unrelated discussion cannot confirm it");
        Require(reloaded.TryRespondToPlayerIntroductionChoice("是这个", day.AddDays(1), out var handled) && handled &&
            reloaded.PlayerIntroductionAwaitingConfirmation == null && reloaded.DuePlayerIntroduction(day.AddDays(1)) == null &&
            !chat.TryRespondToPlayerIntroductionChoice("是的", day.AddDays(1), out _), "Contextual confirmation is persisted once and waits until the next game day");
        var confirmed = Open().DuePlayerIntroduction(day.AddDays(2)) ?? throw new InvalidOperationException("Confirmed request missing after reload.");
        var addressBook = new DepartedContacts(root);
        Require(!addressBook.Introductions(902u).Any(), "Confirming a player leaves the contact list empty on the same day");
        var match = candidates.Single(candidate => candidate.PublicId == confirmed.ConfirmedPlayerId);
        Require(confirmed.RequestedOn == day.AddDays(1) &&
            !reloaded.TryCompletePlayerIntroduction(unresolved, "不得写入旧请求", day.AddDays(2)) &&
            addressBook.AddIntroduction(902u, new DirectoryPerson(match.Name, "KM", DepartedContacts.IntroducedGroup,
                "前锋", match.PublicId), match.ClubName, "法国") &&
            reloaded.TryCompletePlayerIntroduction(confirmed, "联系结果已确认。", day.AddDays(2)) &&
            Open().HasUnreadPlayerIntroduction &&
            new DepartedContacts(root).Introductions(902u).Single().PublicId == match.PublicId &&
            !new DepartedContacts(root).Introductions(903u).Any(),
            "The confirmed UID and incoming result survive reload, and the contact belongs to the correct manager");

        Require(reloaded.TryQueuePlayerIntroduction("查找球员：Mbappe", "Mbappe", day.AddDays(3)), "Queue an ambiguous surname");
        var ambiguous = reloaded.UnresolvedPlayerIntroduction ?? throw new InvalidOperationException("Ambiguous request missing.");
        var multiple = new[]
        {
            new PlayerIntroductionCandidate(85139014u, "Kylian Mbappé", "皇家马德里"),
            new PlayerIntroductionCandidate(2000133971u, "Ethan Mbappé", "里尔")
        };
        Require(reloaded.TryOfferPlayerIntroductionCandidates(ambiguous, multiple, day.AddDays(3)) &&
            reloaded.TryRespondToPlayerIntroductionChoice("是这个", day.AddDays(3), out _) &&
            reloaded.PlayerIntroductionAwaitingConfirmation?.Candidates.Length == 2 &&
            reloaded.DuePlayerIntroduction(day.AddDays(10)) == null, "A generic yes cannot choose between actual players");
        Require(reloaded.TryRespondToPlayerIntroductionChoice("ID：2000133971", day.AddDays(4), out _) &&
            Open().DuePlayerIntroduction(day.AddDays(5))?.ConfirmedPlayerId == 2000133971u,
            "An explicit persisted UID selects the intended person");
        var second = reloaded.DuePlayerIntroduction(day.AddDays(5))!;
        Require(!reloaded.TryAskForPlayerIntroductionName(second, "不得撤销已确认的球员", day.AddDays(4)) &&
            reloaded.TryCompletePlayerIntroduction(second, "联系结果。", day.AddDays(5)) &&
            reloaded.TryQueuePlayerIntroduction("查找球员：哈兰德", "哈兰德", day.AddDays(5)), "Complete one request before testing cancellation");
        var cancel = reloaded.UnresolvedPlayerIntroduction!;
        Require(reloaded.TryOfferPlayerIntroductionCandidates(cancel,
            new[] { new PlayerIntroductionCandidate(28049320u, "Erling Haaland", "曼城") }, day.AddDays(5)) &&
            reloaded.TryRespondToPlayerIntroductionChoice("不是他", day.AddDays(5), out _) &&
            Open().DuePlayerIntroduction(day.AddDays(9)) == null && Open().PlayerIntroductionAwaitingConfirmation == null,
            "A denied identity is cancelled without adding or scheduling a contact");
        const string clarification = "当前存档里还没找到这个名字，请补充原文姓名或球员ID，我再查一次。";
        Require(reloaded.TryQueuePlayerIntroduction("查找球员：不存在的球员", "不存在的球员", day.AddDays(6)), "Queue a name without a verified career match");
        var missing = reloaded.UnresolvedPlayerIntroduction!;
        Require(!reloaded.TryCompletePlayerIntroduction(missing, clarification, day.AddDays(6)) &&
            !reloaded.TryAskForPlayerIntroductionName(missing, clarification, day.AddDays(5)) &&
            reloaded.TryAskForPlayerIntroductionName(missing, clarification, day.AddDays(6)) &&
            !reloaded.TryAskForPlayerIntroductionName(missing, clarification, day.AddDays(6)) &&
            reloaded.History[^1].IsIncoming && reloaded.History[^1].Assistant == clarification &&
            reloaded.History[^1].GameDate == day.AddDays(6) && reloaded.HasUnreadPlayerIntroduction &&
            Open().UnresolvedPlayerIntroduction == null && Open().DuePlayerIntroduction(day.AddDays(7)) == null &&
            Open().PlayerIntroductionAwaitingName?.Id == missing.Id,
            "An unmatched name waits for clarification across reload without a repeated search");

        reloaded = Open();
        const string supplement = "基利安·姆巴佩";
        Require(reloaded.TrySupplyPlayerIntroductionName(supplement, PlayerIntroductionRules.ParseNameSupplement(supplement)!,
                day.AddDays(7)) && reloaded.PlayerIntroductionAwaitingName == null &&
            !reloaded.TryOfferPlayerIntroductionCandidates(missing, candidates, day.AddDays(7)) &&
            Open().UnresolvedPlayerIntroduction?.Target == supplement,
            "A bare full-name supplement resumes lookup after reload and invalidates stale candidate results");
        var localized = reloaded.UnresolvedPlayerIntroduction!;
        var localizedCandidates = new[]
        {
            new PlayerIntroductionCandidate(85139014u, "基利安·姆巴佩", "皇家马德里", "Kylian Mbappé"),
            new PlayerIntroductionCandidate(2000133971u, "伊桑·姆巴佩", "里尔", "Ethan Mbappé")
        };
        Require(!reloaded.TryOfferPlayerIntroductionCandidates(localized,
                new[] { localizedCandidates[0] with { OriginalName = "bad\nname" } }, day.AddDays(7)) &&
            reloaded.TryOfferPlayerIntroductionCandidates(localized, localizedCandidates, day.AddDays(7)) &&
            reloaded.History[^1].Assistant.Contains("基利安·姆巴佩（Kylian Mbappé）", StringComparison.Ordinal) &&
            Open().PlayerIntroductionAwaitingConfirmation?.Candidates[0].Name == "基利安·姆巴佩",
            "Display both real names while persisting each spelling separately and rejecting invalid originals");
        var originalChoice = Open();
        Require(originalChoice.TryRespondToPlayerIntroductionChoice("Kylian Mbappé", day.AddDays(7), out _) &&
            originalChoice.DuePlayerIntroduction(day.AddDays(8))?.ConfirmedPlayerId == 85139014u,
            "An exact original name can select a localized candidate after reload");
        var chosenLocalized = originalChoice.DuePlayerIntroduction(day.AddDays(8))!;
        Require(originalChoice.TryCompletePlayerIntroduction(chosenLocalized, "联系结果。", day.AddDays(8)) &&
            originalChoice.TryQueuePlayerIntroduction("查找球员：李明", "李明", day.AddDays(8)), "Queue distinct UIDs with the same actual name");
        var sameName = originalChoice.UnresolvedPlayerIntroduction!;
        Require(originalChoice.TryOfferPlayerIntroductionCandidates(sameName, new[]
            {
                new PlayerIntroductionCandidate(1001u, "李明", "球队一", "Li Ming"),
                new PlayerIntroductionCandidate(1002u, "李明", "球队二", "Li Ming")
            }, day.AddDays(8)) &&
            originalChoice.TryRespondToPlayerIntroductionChoice("李明", day.AddDays(8), out _) &&
            originalChoice.PlayerIntroductionAwaitingConfirmation?.Candidates.Length == 2 &&
            originalChoice.TryRespondToPlayerIntroductionChoice("Li Ming", day.AddDays(8), out _) &&
            originalChoice.PlayerIntroductionAwaitingConfirmation?.Candidates.Length == 2 &&
            originalChoice.TryRespondToPlayerIntroductionChoice("第二位", day.AddDays(8), out _) &&
            Open().DuePlayerIntroduction(day.AddDays(9))?.ConfirmedPlayerId == 1002u,
            "Duplicated localized or original names require a UID or ordinal instead of selecting the first person");
    }

    private static void VerifyLegacyIntroductionContinuation()
    {
        var day = new DateTime(2026, 12, 13);
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-introduction-legacy-" + Guid.NewGuid().ToString("N"));
        var assistant = new PlayerProfile("assistant-legacy-intro", "助理", "测试") { IsAssignedManagerAssistant = true };
        Conversation Open() => new("introductions", root, assistant, _ => throw new InvalidOperationException("No model call."), null, null);
        var chat = Open();
        Require(chat.TryQueuePlayerIntroduction("查找球员：一凡，我需要你给我姆巴佩的联系方式",
            "一凡，我需要你给我姆巴佩的联系方式", day), "Create the original request shape");
        var request = chat.UnresolvedPlayerIntroduction!;
        Require(chat.TryAskForPlayerIntroductionName(request, Conversation.PlayerIntroductionNameClarification, day),
            "Create the original clarification message");
        var path = Directory.GetFiles(Path.Combine(root, "conversations"), "*.json").Single();
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        const string oldAcknowledgement = "我试着去找找，但不能保证能拿到联系方式。明天有结果我告诉你。";
        state["playerIntroductions"] = new System.Text.Json.Nodes.JsonArray();
        var turns = state["turns"]!.AsArray();
        turns[0]!["assistant"] = oldAcknowledgement;
        turns.Add(System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            user = "基利安·姆巴佩", assistant = "我再按全名核对一下。", affinityChange = 0, gameDate = day
        }));
        File.WriteAllText(path, state.ToJsonString());
        var restored = Open();
        Require(restored.TryResumeLegacyPlayerIntroduction(day.AddDays(2)) &&
            restored.UnresolvedPlayerIntroduction?.Target == "基利安·姆巴佩" &&
            !Open().TryResumeLegacyPlayerIntroduction(day.AddDays(2)) && restored.History.Count == 3,
            "The old clarification and saved bare reply resume once without duplicating the conversation");

        var pending = restored.UnresolvedPlayerIntroduction!;
        var candidate = new PlayerIntroductionCandidate(85139014u, "基利安·姆巴佩", "皇家马德里", "Kylian Mbappé Lottin");
        var confirmationDate = day.AddDays(2);
        Require(restored.TryOfferPlayerIntroductionCandidates(pending, new[] { candidate }, confirmationDate),
            "Persist the native candidate before simulating the old chat fallback");
        state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        turns = state["turns"]!.AsArray();
        turns.Add(System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            user = "是这个", assistant = "我会试着联系，明天告诉你结果。", affinityChange = 0, gameDate = confirmationDate
        }));
        File.WriteAllText(path, state.ToJsonString());
        restored = Open();
        Require(!restored.TryResumeLegacyPlayerIntroduction(day) &&
            restored.TryResumeLegacyPlayerIntroduction(confirmationDate) &&
            restored.DuePlayerIntroduction(confirmationDate) == null && restored.PlayerIntroductionAwaitingConfirmation == null &&
            Open().DuePlayerIntroduction(confirmationDate.AddDays(1))?.ConfirmedPlayerId == candidate.PublicId &&
            !Open().TryResumeLegacyPlayerIntroduction(confirmationDate.AddDays(1)) && restored.History.Count == 5,
            "A saved confirmation from the old chat fallback retains its game date and resumes only once");
    }

    private static void VerifyIntroductionModelChoice()
    {
        var day = new DateTime(2026, 12, 13);
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-model-choice-" + Guid.NewGuid().ToString("N"));
        var assistant = new PlayerProfile("model-choice", "助理", "测试") { IsAssignedManagerAssistant = true };
        const string modelText = "我已经联系了这名球员。";
        var selectedNumber = 2;
        var calls = 0;
        Conversation Open() => new("introductions", root, assistant, prompt =>
        {
            calls++;
            Require(prompt.Contains("playerIntroductionChoice", StringComparison.Ordinal) &&
                prompt.Contains("playerIntroductionOffer", StringComparison.Ordinal) && prompt.Contains("85139014", StringComparison.Ordinal),
                "The model sees the verified offer and its action contract");
            return Task.FromResult(new Reply(modelText, 0) { PlayerIntroductionChoice = selectedNumber });
        }, null, null);

        var chat = Open();
        Require(chat.TryQueuePlayerIntroduction("我需要姆巴佩的联系方式", "姆巴佩", day) &&
            chat.TryOfferPlayerIntroductionCandidates(chat.UnresolvedPlayerIntroduction!,
                new[] { new PlayerIntroductionCandidate(85139014u, "基利安·姆巴佩", "皇家马德里") }, day),
            "Prepare a single verified candidate");
        Require(!chat.TryRespondToPlayerIntroductionChoice("那就麻烦你牵个线了", day, out var handled) && !handled,
            "The natural reply requires model interpretation");
        try
        {
            chat.SendAsync("那就麻烦你牵个线了", day, null, canConfirmPlayerIntroduction: true).GetAwaiter().GetResult();
            throw new Exception("An unlisted candidate was accepted.");
        }
        catch (InvalidOperationException) { }
        Require(Open().History.Count == 2 && Open().DuePlayerIntroduction(day.AddDays(1)) == null,
            "An unlisted model choice leaves the original offer intact");

        selectedNumber = 1;
        var reply = chat.SendAsync("那就麻烦你牵个线了", day, null, canConfirmPlayerIntroduction: true).GetAwaiter().GetResult();
        Require(calls == 2 && reply.Text != modelText && reply.Text.Contains("明天", StringComparison.Ordinal) &&
            Open().DuePlayerIntroduction(day) == null && Open().DuePlayerIntroduction(day.AddDays(1))?.ConfirmedPlayerId == 85139014u,
            "The model interprets intent, and only the stored candidate supplies the next-day action and acknowledgment");
    }

    private static void VerifyIntroductionScheduling()
    {
        const string target = "Kylian Mbappé";
        const string input = "助理，能不能帮我找一下Kylian Mbappé的联系方式？";
        const string failure = "我托人问过了。以你目前的声望，对方还不愿意留下联系方式。";
        const string success = "我拿到联系方式了，已经帮你加进联系人。";
        var day = new DateTime(2026, 11, 17);
        var nextDay = day.AddDays(1);
        var root = Path.Combine(Path.GetTempPath(), "fm-player-talk-introductions-" + Guid.NewGuid().ToString("N"));
        var checkpoints = new SaveCheckpoints(root);
        var session = checkpoints.CreateSession();
        var save = Path.Combine(root, "trial.fm");
        var assistant = new PlayerProfile("assistant-intro-fixture", "助理", "球员介绍测试。") { IsAssignedManagerAssistant = true };

        Conversation Open(string directory) => new("introductions", directory, assistant,
            _ => throw new InvalidOperationException("The introduction queue should not call the model."), null, null);
        string Restore() => checkpoints.Restore(save) ?? throw new InvalidOperationException("The introduction save did not restore.");

        const string before = "FM revision before asking for an introduction";
        File.WriteAllText(save, before);
        Require(checkpoints.Store(save, session), "Store checkpoint before making a request");

        var chat = Open(session);
        var affinity = chat.Affinity;
        Require(chat.TryQueuePlayerIntroduction(input, target, day.AddHours(14)) &&
            !chat.TryQueuePlayerIntroduction(input, "Kylian-Mbappe", day.AddHours(15)) &&
            !chat.HasUnreadPlayerIntroduction && chat.DuePlayerIntroduction(day.AddHours(23)) == null,
            "A same-day request creates one waiting entry without unread news");
        Require(chat.History.Count == 1 && chat.History[0].User == input && chat.History[0].GameDate == day &&
            !chat.History[0].IsIncoming && chat.History[0].Assistant.Contains("不能保证", StringComparison.Ordinal),
            "Only the acknowledgement appears on the request day");

        File.WriteAllText(save, "FM revision with an introduction pending");
        Require(checkpoints.Store(save, session), "Store the pending request");
        var deliverySession = Restore();
        var restored = Open(deliverySession);
        var waiting = restored.DuePlayerIntroduction(nextDay) ?? throw new InvalidOperationException("Pending request missing after reload.");
        Require(waiting.Target == target && waiting.RequestedOn == day &&
            !restored.TryCompletePlayerIntroduction(waiting, failure, day.AddHours(23)) &&
            restored.TryQueuePlayerIntroduction("查找球员：Player Two", "Player Two", nextDay),
            "The first request survives a save and an unrelated request can be queued on the following day");

        var stale = Open(deliverySession);
        Require(restored.TryCompletePlayerIntroduction(waiting, failure, nextDay) &&
            !stale.TryCompletePlayerIntroduction(waiting, failure, nextDay) &&
            restored.DuePlayerIntroduction(nextDay.AddHours(23)) == null && restored.History.Count == 3 &&
            restored.History[^1].IsIncoming && restored.History[^1].Assistant == failure &&
            restored.History[^1].GameDate == nextDay && restored.HasUnreadPlayerIntroduction && restored.Affinity == affinity,
            "Next-day resolution is unread and incoming, leaves the affinity intact, and cannot be repeated by a stale chat");

        var reopened = Open(deliverySession);
        var other = reopened.DuePlayerIntroduction(nextDay.AddDays(1)) ?? throw new InvalidOperationException("Second pending request missing.");
        Require(other.Target == "Player Two" && reopened.TryCompletePlayerIntroduction(other, success, nextDay.AddDays(1)) &&
            reopened.HasUnreadPlayerIntroduction && reopened.MarkPlayerIntroductionRead() && !reopened.MarkPlayerIntroductionRead(),
            "A distinct request resolves on its own following day and the unread receipt is idempotent");
        var read = Open(deliverySession);
        Require(!read.HasUnreadPlayerIntroduction && read.DuePlayerIntroduction(nextDay.AddDays(2)) == null &&
            read.History.Count == 4 && read.History[^1].IsIncoming && read.History[^1].Assistant == success,
            "Completed requests and their read receipts survive reopening");

        File.WriteAllText(save, before);
        var rolledBack = Open(Restore());
        Require(rolledBack.History.Count == 0 && rolledBack.DuePlayerIntroduction(nextDay) == null &&
            !rolledBack.HasUnreadPlayerIntroduction, "An earlier FM revision has no future requests or replies");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Player introduction check failed: " + scenario);
    }
}
