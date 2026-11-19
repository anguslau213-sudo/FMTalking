using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private Button? introductionButton;
    private PlayerIntroductionRequest? introduction;
    private float nextIntroductionCheck;
    private readonly Dictionary<Guid, IntroductionAliases> introductionAliases = new();
    private sealed record IntroductionAliases(IReadOnlyList<string> Names, bool Failed = false);
    private const string IntroductionModelRetry = "姓名核对未完成，请求已保留。确认模型连接正常后，点击「请助理介绍球员」重试。";

    private sealed class PlayerIntroductionRequest : IDisposable
    {
        internal PlayerIntroductionRequest(LiveRoster team, uint assistant, Conversation chat,
            DateTime date, PendingPlayerIntroduction pending, FmPlayerIntroductionLookup.SearchScope source)
        {
            Team = team;
            Assistant = assistant;
            Chat = chat;
            Date = date;
            Pending = pending;
            Source = source;
            Deadline = Time.unscaledTime + (ResolvingName ? 180f : 35f);
        }

        internal LiveRoster Team { get; }
        internal uint Assistant { get; }
        internal Conversation Chat { get; }
        internal DateTime Date { get; }
        internal PendingPlayerIntroduction Pending { get; }
        internal FmPlayerIntroductionLookup.SearchScope Source { get; }
        internal float Deadline { get; }
        internal string? Response { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task<IReadOnlyList<FmPlayerIntroductionLookup.NameHit>?>? Search { get; set; }
        internal Task<IReadOnlyList<string>>? Aliases { get; set; }
        internal bool TriedAliases { get; set; }
        internal IReadOnlyList<FmPlayerIntroductionLookup.NameHit>? Matches { get; set; }
        internal int CandidateIndex { get; set; }
        internal List<PlayerIntroductionCandidate> Candidates { get; } = new();
        internal bool ResolvingName => Pending.ConfirmedPlayerId == null &&
            PlayerIntroductionRules.ParsePublicId(Pending.Target) == null;
        internal uint? PlayerId { get; set; }
        internal FmPlayerIntroductionLookup.Target? Evidence { get; set; }
        internal FmPlayerIntroductionLookup.PersonVerification? Verification { get; set; }

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
            if (Search is { } task)
                _ = task.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (Aliases is { } aliases)
                _ = aliases.ContinueWith(failed => _ = failed.Exception, TaskContinuationOptions.OnlyOnFaulted);
            Verification?.Dispose();
        }
    }

    private uint? AssignedAssistantForIntroduction => player?.IsAssignedManagerAssistant == true &&
        person?.PublicId is uint selected && ManagerAssistantContact.Current?.PublicId == selected ? selected : null;

    private bool CanConfirmPlayerIntroduction(Conversation active, DateTime? date) =>
        AssignedAssistantForIntroduction.HasValue && date.HasValue && active.PlayerIntroductionAwaitingConfirmation != null &&
        roster is { ClubId: > 0 } team && GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId);

    private void AddIntroductionAction(VisualElement actions)
    {
        introductionButton = Control("请助理介绍球员", StartIntroductionDraft, SelectedRow);
        introductionButton.style.alignSelf = Align.FlexStart;
        introductionButton.style.marginBottom = 8;
        introductionButton.style.minHeight = 32;
        introductionButton.style.fontSize = 14;
        introductionButton.style.color = AccentText;
        Frame(introductionButton, Purple, 8);
        introductionButton.tooltip = UiLanguage.Get("填写球员姓名后发送。助理先受理，下一游戏日回复结果。也可以直接在聊天里提出请求。");
        introductionButton.style.display = DisplayStyle.None;
        actions.Add(introductionButton);
    }

    private void RefreshIntroductionAction()
    {
        if (introductionButton != null)
        {
            introductionButton.style.display = AssignedAssistantForIntroduction.HasValue && dinnerStatus == null && conversation?.IsBlocked == false
                ? DisplayStyle.Flex : DisplayStyle.None;
            introductionButton.SetEnabled(pending == null && conversation?.LoadWarning == null && !editor.Composing);
        }
    }

    private void StartIntroductionDraft()
    {
        if (!AssignedAssistantForIntroduction.HasValue || conversation is null || conversation.IsBlocked ||
            dinnerStatus != null || pending != null || editor.Composing)
            return;
        foreach (var id in introductionAliases.Where(item => item.Value.Failed).Select(item => item.Key).ToArray())
            introductionAliases.Remove(id);
        nextIntroductionCheck = 0;
        if (string.IsNullOrWhiteSpace(editor.Text))
            editor.ReplaceDraft(UiLanguage.Get("查找球员") + (UiLanguage.Effective is UiLanguageChoice.Chinese or UiLanguageChoice.Japanese ? "：" : ": "));
        EditDraft();
        if (introductionButton != null)
            introductionButton.tooltip = UiLanguage.Get("输入球员全名。同名时可以填写 FM 人员 ID。");
    }

    private bool TryQueuePlayerIntroduction(string input, DateTime? date, Conversation active)
    {
        if (!AssignedAssistantForIntroduction.HasValue)
            return false;
        var waitingForChoice = active.PlayerIntroductionAwaitingConfirmation;
        if (date is DateTime choiceDate && waitingForChoice != null &&
            PlayerIntroductionRules.ParseNameSupplement(input) is { } identifier &&
            PlayerIntroductionRules.ParsePublicId(identifier).HasValue)
        {
            var saved = active.TryRespondToPlayerIntroductionChoice(input, choiceDate, out var handled);
            if (handled)
            {
                if (saved)
                    CompleteIntroductionInput();
                else
                {
                    statusText = "这次确认未能保存，请稍后再试。";
                    hasError = true;
                    RefreshControls();
                }
                return true;
            }
        }
        var awaitingName = active.PlayerIntroductionAwaitingName;
        var target = PlayerIntroductionRules.ParseTarget(input) ??
            (awaitingName != null ? PlayerIntroductionRules.ParseNameSupplement(input) : null);
        if (target == null || waitingForChoice != null &&
            PlayerIntroductionRules.MatchKey(target) == PlayerIntroductionRules.MatchKey(waitingForChoice.Target))
            return false;
        if (active.UnresolvedPlayerIntroduction is { } retry &&
            PlayerIntroductionRules.MatchKey(retry.Target) == PlayerIntroductionRules.MatchKey(target) &&
            introductionAliases.TryGetValue(retry.Id, out var previous) && previous.Failed)
        {
            introductionAliases.Remove(retry.Id);
            nextIntroductionCheck = 0;
            CompleteIntroductionInput();
            statusText = "已恢复此前的介绍请求，正在重新核对姓名。";
            RefreshControls();
            return true;
        }
        if (roster is not { ClubId: > 0 } team || departedContacts == null ||
            date is not DateTime today || !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
        {
            statusText = "暂时无法登记球员介绍。请在本地单人生涯完成载入后重试。";
            hasError = true;
            RefreshControls();
            return true;
        }
        var queued = awaitingName == null ? active.TryQueuePlayerIntroduction(input, target, today) :
            active.TrySupplyPlayerIntroductionName(input, target, today);
        if (!queued)
        {
            statusText = "这次介绍未能登记。可以等助理处理完已有的请求，或稍后重试。";
            hasError = true;
            RefreshControls();
            return true;
        }
        if (awaitingName != null)
            introductionAliases.Remove(awaitingName.Id);
        nextIntroductionCheck = 0;
        CompleteIntroductionInput();
        return true;
    }

    private void CompleteIntroductionInput()
    {
        editor.Stop();
        editor.Clear();
        drafts.Remove(player!.Id);
        statusText = "";
        hasError = false;
        RenderTranscript();
        RenderRecent();
        RefreshControls();
    }

    private void BeginDuePlayerIntroduction()
    {
        var now = Time.unscaledTime;
        if (now < nextIntroductionCheck)
            return;
        nextIntroductionCheck = now + 1f;
        if (roster is not { ClubId: > 0 } team || departedContacts == null ||
            ManagerAssistantContact.Current?.PublicId is not uint assistant ||
            conversationDirectory.FirstOrDefault(item => item.Profile.IsAssignedManagerAssistant &&
                item.Entry.PublicId == assistant) is not { } sender ||
            GameCalendar.ReadDate() is not DateTime today)
            return;
        var chat = conversations.TryGetValue(sender.Profile.Id, out var existing) && existing.UsesManagerAssistantAffinity
            ? existing : ConversationFor(sender.Profile);
        if (WaitingForIntroductionAssistant(chat) ||
            !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
            return;
        chat.TryResumeLegacyPlayerIntroduction(today);
        var due = chat.DuePlayerIntroduction(today) ?? chat.UnresolvedPlayerIntroduction;
        if (due == null)
            return;
        if (introductionAliases.TryGetValue(due.Id, out var unavailable) && unavailable.Failed)
        {
            PausePlayerIntroduction(chat, IntroductionModelRetry);
            return;
        }
        if (FmPlayerIntroductionLookup.CaptureSearch() is not { } source)
        {
            PausePlayerIntroduction(chat, "游戏资料暂时不可读。球员介绍请求已保留，资料恢复后会自动重试。");
            return;
        }

        var request = new PlayerIntroductionRequest(team, assistant, chat, today.Date, due, source);
        introduction = request;
        request.PlayerId = due.ConfirmedPlayerId ?? PlayerIntroductionRules.ParsePublicId(due.Target);
        if (!request.PlayerId.HasValue)
        {
            var token = request.Cancellation.Token;
            var language = nameLanguage;
            request.Search = Task.Run(() => FmPlayerIntroductionLookup.Search(source, due.Target, language, token), token);
        }
    }

    private void TickPlayerIntroduction()
    {
        if (introduction is not { } request)
        {
            BeginDuePlayerIntroduction();
            return;
        }
        if (roster is not { } live || !SameClub(live, request.Team) || request.Chat.IsBlocked ||
            request.Source.NameScope != NativeNameTable.Version ||
            WaitingForIntroductionAssistant(request.Chat) ||
            ManagerAssistantContact.Current?.PublicId != request.Assistant || GameCalendar.ReadDate()?.Date != request.Date ||
            !GameCalendar.IsIdleSinglePlayerCareer(live.ManagerId, live.ClubId))
        {
            CancelPlayerIntroduction();
            return;
        }
        if (request.Response is { } response)
        {
            if (Time.unscaledTime >= nextIntroductionCheck)
                FinishIntroduction(request, response);
            return;
        }
        if (Time.unscaledTime >= request.Deadline)
        {
            PausePlayerIntroduction(request.Chat, "核对球员资料超时。请求已保留，稍后会自动重试。");
            return;
        }
        if (request.ResolvingName)
        {
            TickIntroductionName(request);
            return;
        }
        if (request.PlayerId is not uint id)
            return;
        if (live.Players.Any(entry => entry.PublicId == id))
        {
            FinishIntroduction(request, "这名球员在我们队里，你可以直接从「球员对话」找他。");
            return;
        }
        if ((departedContacts?.Introductions(live.ManagerId) ?? Enumerable.Empty<DirectoryPerson>())
            .Concat(contacts).FirstOrDefault(entry => entry.PublicId == id) is { } known)
        {
            FinishIntroduction(request, $"{known.Name}已经在你的「外部联系」了，你可以在那里直接找他。");
            return;
        }

        if (request.Verification == null)
        {
            var target = FmPlayerIntroductionLookup.ReadTarget(request.Source.Module, id);
            if (target == null)
                return;
            request.Evidence = target;
            request.Verification = FmPlayerIntroductionLookup.PersonVerification.Begin(target);
            if (request.Verification == null)
                PausePlayerIntroduction(request.Chat, "FM 的人物资料暂时不可读，请求已保留。稍后会自动重试。");
            return;
        }
        if (!request.Verification.TryFinish(out var verified))
            return;
        if (verified == null || request.Evidence is not { } evidence ||
            FmPlayerIntroductionLookup.ReadTarget(request.Source.Module, id) != evidence ||
            FmPlayerIntroductionLookup.ReadManager(request.Source.Module, live.ManagerId) is not { } manager)
        {
            PausePlayerIntroduction(request.Chat, "人物身份和介绍资料暂时无法核实，请求已保留。稍后会自动重试。");
            return;
        }
        if (evidence.ClubId == live.ClubId)
        {
            FinishIntroduction(request, "这名球员属于我们俱乐部，等名册同步后可以直接在球队名单里找他。");
            return;
        }
        var sameNationality = PlayerIntroductionRules.SameNationality(manager.Nation, evidence.Nation);
        if (!sameNationality && !PlayerIntroductionRules.AllowsInternational(manager.WorldReputation, evidence.WorldReputation))
        {
            if (manager.Nation == 0 || evidence.Nation == 0 ||
                manager.WorldReputation == null || evidence.WorldReputation == null)
                PausePlayerIntroduction(request.Chat, "国籍或声望资料暂时不可读，请求已保留。稍后会自动重试。");
            else
                FinishIntroduction(request, $"我试着联系了{verified.Name}。对方知道你的来意，但以你目前的声望，他还不愿意把联系方式交给我们。");
            return;
        }

        try
        {
            if (departedContacts?.AddIntroduction(live.ManagerId, verified, evidence.ClubName, evidence.NationName) != true)
            {
                PausePlayerIntroduction(request.Chat, "联系人暂时未能保存，请求已保留。稍后会自动重试。");
                return;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PausePlayerIntroduction(request.Chat, "联系人暂时未能保存，请求已保留。稍后会自动重试。");
            return;
        }
        var channel = sameNationality ? "我托同国籍的人脉搭上了线。" : "我联系上了愿意帮忙牵线的人。";
        FinishIntroduction(request, $"{channel}{verified.Name}已经加进「外部联系 → 助理介绍」，你可以去跟他聊聊。");
        RefreshRosterView();
    }

    private bool WaitingForIntroductionAssistant(Conversation chat) => pending != null && pendingPerson is { } recipient &&
        conversations.GetValueOrDefault(recipient) == chat;

    private void TickIntroductionName(PlayerIntroductionRequest request)
    {
        if (request.Matches == null)
        {
            if (request.Search is not { IsCompleted: true } search)
                return;
            if (!search.IsCompletedSuccessfully || search.Result == null)
            {
                PausePlayerIntroduction(request.Chat, "游戏球员列表正在变化，请求已保留。稍后会自动重试。");
                return;
            }
            if (search.Result.Count > 0)
                request.Matches = search.Result;
            else if (!request.TriedAliases)
            {
                if (introductionAliases.TryGetValue(request.Pending.Id, out var cached))
                {
                    request.TriedAliases = true;
                    if (cached.Failed)
                        PausePlayerIntroduction(request.Chat, IntroductionModelRetry);
                    else if (cached.Names.Count == 0)
                        ClarifyIntroductionName(request);
                    else
                        SearchIntroductionAliases(request, cached.Names);
                    return;
                }
                if (ModelConnection.IsBusy || saveSessionDirectory == null)
                    return;
                request.TriedAliases = true;
                request.Aliases = PlayerIntroductionNameResolver.ResolveAliasesAsync(request.Pending.Target,
                    Path.Combine(saveSessionDirectory, "introduction-name-lookup"), request.Cancellation.Token);
                return;
            }
            else if (request.Aliases is { } aliases)
            {
                if (!aliases.IsCompleted)
                    return;
                request.Aliases = null;
                if (!aliases.IsCompletedSuccessfully)
                {
                    _ = aliases.Exception;
                    introductionAliases[request.Pending.Id] = new(Array.Empty<string>(), Failed: true);
                    PausePlayerIntroduction(request.Chat, IntroductionModelRetry);
                    return;
                }
                introductionAliases[request.Pending.Id] = new(aliases.Result);
                if (aliases.Result.Count == 0)
                {
                    ClarifyIntroductionName(request);
                    return;
                }
                SearchIntroductionAliases(request, aliases.Result);
                return;
            }
            else
            {
                ClarifyIntroductionName(request);
                return;
            }
        }
        if (request.CandidateIndex >= Math.Min(PlayerIntroductionNameResolver.MaximumAliases, request.Matches.Count))
        {
            if (request.Candidates.Count == 0)
            {
                PausePlayerIntroduction(request.Chat, "FM 暂时没有提供可核实的人物资料，请求已保留。稍后会自动重试。");
                return;
            }
            if (request.Chat.TryOfferPlayerIntroductionCandidates(request.Pending, request.Candidates, request.Date))
            {
                if (request.Chat == conversation)
                    RenderTranscript();
                RefreshUnreadRows();
                introductionAliases.Remove(request.Pending.Id);
                CancelPlayerIntroduction();
            }
            return;
        }
        var hit = request.Matches[request.CandidateIndex];
        if (request.Verification == null)
        {
            if (FmPlayerIntroductionLookup.ReadTarget(request.Source.Module, hit.Id) is not { } target)
                return;
            request.Evidence = target;
            request.Verification = FmPlayerIntroductionLookup.PersonVerification.Begin(target);
            if (request.Verification == null)
                PausePlayerIntroduction(request.Chat, "FM 的人物资料暂时不可读，请求已保留。稍后会自动重试。");
            return;
        }
        if (!request.Verification.TryFinish(out var person))
            return;
        if (person != null && request.Evidence is { } evidence &&
            FmPlayerIntroductionLookup.ReadTarget(request.Source.Module, hit.Id) == evidence)
        {
            NativeNameTable.Remember(request.Source.NameScope, nameLanguage, hit.Id, person.Name);
            var original = hit.OriginalName is { } sourceName &&
                PlayerIntroductionRules.MatchKey(sourceName) != PlayerIntroductionRules.MatchKey(person.Name) ? sourceName : null;
            request.Candidates.Add(new(hit.Id, person.Name, evidence.ClubName, original));
        }
        request.Verification.Dispose();
        request.Verification = null;
        request.CandidateIndex++;
    }

    private void ClarifyIntroductionName(PlayerIntroductionRequest request)
    {
        if (!request.Chat.TryAskForPlayerIntroductionName(request.Pending, Conversation.PlayerIntroductionNameClarification, request.Date))
            return;
        if (request.Chat == conversation)
            RenderTranscript();
        RefreshUnreadRows();
        introductionAliases.Remove(request.Pending.Id);
        CancelPlayerIntroduction();
    }

    private void SearchIntroductionAliases(PlayerIntroductionRequest request, IReadOnlyList<string> terms)
    {
        var source = request.Source;
        var language = nameLanguage;
        var token = request.Cancellation.Token;
        request.Search = Task.Run(() => FmPlayerIntroductionLookup.Search(source, terms, language, token), token);
    }

    private void PausePlayerIntroduction(Conversation chat, string reason)
    {
        CancelPlayerIntroduction();
        nextIntroductionCheck = Time.unscaledTime + 15f;
        if (conversation != chat)
            return;
        statusText = reason;
        hasError = true;
        RefreshControls();
    }

    private void FinishIntroduction(PlayerIntroductionRequest request, string response)
    {
        request.Response = response;
        nextIntroductionCheck = Time.unscaledTime + 1f;
        if (!DeliverIntroductionOutcome(request.Chat, request.Pending, request.Date, response))
            return;
        CancelPlayerIntroduction();
    }

    private bool DeliverIntroductionOutcome(Conversation chat, PendingPlayerIntroduction request, DateTime date, string response)
    {
        if (!chat.TryCompletePlayerIntroduction(request, response, date))
            return false;
        if (chat == conversation)
            RenderTranscript();
        introductionAliases.Remove(request.Id);
        RefreshUnreadRows();
        Plugin.Logger.LogInfo("FM Player Talk delivered a player introduction result.");
        return true;
    }

    private void CancelPlayerIntroduction()
    {
        // A change of game date can interrupt a lookup. Keep completed
        // spellings for that request, and let interrupted ones retry naturally.
        if (introduction is { Aliases: { IsCompletedSuccessfully: true } aliases } request)
            introductionAliases[request.Pending.Id] = new(aliases.Result);
        introduction?.Dispose();
        introduction = null;
    }
}
