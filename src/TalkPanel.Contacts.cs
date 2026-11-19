using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private float manualRefreshDeadline;
    private LiveRoster? manualRoster;
    private string ManualRefreshStatus { get; set; } = "";

    private static DirectoryPerson[] IncludeManagerAssistant(DirectoryPerson[] current, DirectoryPerson[] inbox)
    {
        if (ManagerAssistantContact.Current is not { } assistant)
            return current;
        if (assistant.PublicId is not uint id)
            return current.Prepend(assistant).ToArray();

        var known = inbox.Concat(current).FirstOrDefault(entry => entry.PublicId == id && entry.NativeDisplayName);
        if (known != null)
        {
            var age = known.Age ?? assistant.Age;
            var isFemale = known.IsFemale ?? assistant.IsFemale;
            var role = known.Role is { Length: > 0 } && known.Role is not ("职员" or "消息发件人" or "经理专属联络人")
                ? known.Role : assistant.Role;
            assistant = assistant with
            {
                Name = known.Name,
                Initials = known.Initials,
                NativeDisplayName = true,
                Age = age,
                IsFemale = isFemale,
                Role = role,
                Profile = assistant.Profile is { } actor ? actor with
                {
                    Name = known.Name, Age = age, IsFemale = isFemale,
                    RoleInCareer = "主教练 · " + role
                } : null
            };
        }

        return current.Where(entry => entry.PublicId != id).Prepend(assistant).ToArray();
    }

    private DirectoryPerson[] ResolveExternalContacts(HashSet<uint?> activeIds)
    {
        var external = departedContacts?.Visible ?? Array.Empty<DirectoryPerson>();
        if (roster is { } current && departedContacts != null)
            external = departedContacts.Introductions(current.ManagerId).Concat(external).ToArray();
        return external.Where(entry => !activeIds.Contains(entry.PublicId)).DistinctBy(ProfileIdFor).ToArray();
    }

    private void RememberCurrentNames(IEnumerable<DirectoryPerson> people)
    {
        var scope = NativeNameTable.Version;
        foreach (var entry in people)
            if (entry.NativeDisplayName && entry.PublicId is uint id)
                NativeNameTable.Remember(scope, nameLanguage, id, entry.Name);
    }

    private DirectoryConversation? FindFavorite(DirectoryPerson entry) =>
        favoriteDirectory.FirstOrDefault(item => ProfileIdFor(item.Entry) == ProfileIdFor(entry));

    private string DirectoryGroup(DirectoryPerson entry)
    {
        if (page == TalkPage.Favorites)
        {
            return FindFavorite(entry)?.Destination switch
            {
                TalkPage.Players => "球员",
                TalkPage.Staff => "职员",
                _ => entry.Team
            };
        }
        return entry.Team;
    }

    private bool MatchesDirectoryGroup(DirectoryPerson entry) =>
        Directory.Group == AllGroups || Directory.Group == DirectoryGroup(entry);

    private string DirectoryEmptyText(string query)
    {
        if (page is TalkPage.Contacts or TalkPage.Favorites && roster == null)
            return "正在同步当前生涯的联系人…";
        if (page is TalkPage.Players or TalkPage.Staff)
        {
            if (roster == null)
                return "等待读取当前球队的人员名单…";
            if (roster.ClubId == 0)
                return "当前没有执教俱乐部";
            if (roster.IsPartial && query.Length == 0)
                return "这组人员正在同步…";
        }
        if (query.Length > 0)
            return "没有匹配的人员";
        return page switch
        {
            TalkPage.Contacts => "暂无外部联系。助理介绍的球员和确认离队的人员会出现在这里。",
            TalkPage.Favorites => "暂无收藏。点击联系人名字旁的星标即可收藏。",
            _ => "暂无人员"
        };
    }

    private void RequestManualRefresh()
    {
        if (!UseCurrentSaveSession() || !GameUi.HasActiveCareerEntry())
        {
            ManualRefreshStatus = "请先载入一个 FM 生涯。";
            return;
        }
        if (manualRefreshDeadline > 0)
            return;

        manualRoster = null;
        manualRefreshDeadline = Time.unscaledTime + 35f;
        ManualRefreshStatus = "正在读取当前俱乐部和收件箱…";
        nextRosterCheck = 0;
        nextInboxCheck = 0;
    }

    private void RecordManualRoster(LiveRoster sample)
    {
        if (manualRefreshDeadline <= 0)
            return;
        if (sample.ClubId == 0)
        {
            FinishManualRefresh("当前没有执教俱乐部。");
            return;
        }
        if (manualRoster != null && SameClub(manualRoster, sample) && manualRoster.PrimaryTeamId == sample.PrimaryTeamId)
            return;

        manualRoster = sample;
        nextInboxCheck = 0;
        ManualRefreshStatus = ManualRosterLabel(sample) + " · 正在检查收件箱…";
    }

    private void RecordManualInbox(LiveRoster active, DirectoryPerson[]? read)
    {
        if (manualRoster == null || !SameClub(manualRoster, active) || manualRoster.PrimaryTeamId != active.PrimaryTeamId)
            return;
        var inbox = read == null ? "FM 收件箱暂未提供发件人数据" : $"收件箱核实发件人 {read.Length} 名";
        FinishManualRefresh(ManualRosterLabel(manualRoster) + " · " + inbox);
    }

    private static string ManualRosterLabel(LiveRoster sample)
    {
        var label = $"已读取球员 {sample.Players.Length} · 职员 {sample.Staff.Length}";
        var board = sample.Staff.Count(entry => entry.Team == LiveClubRoster.BoardGroup);
        var medical = sample.Staff.Count(entry => entry.Team == LiveClubRoster.MedicalGroup);
        if (board > 0 || medical > 0)
            label += $"（含高层 {board}、医疗 {medical}）";
        return sample.IsPartial ? label + " · 仍在补读部分分组或职务" : label;
    }

    private void UpdateManualRefresh()
    {
        if (manualRefreshDeadline > 0 && Time.unscaledTime >= manualRefreshDeadline)
            FinishManualRefresh(manualRoster == null ? "名册仍在同步，已显示的名单可继续使用。"
                : ManualRosterLabel(manualRoster) + " · 收件箱仍在同步");
    }

    private void FinishManualRefresh(string status)
    {
        ManualRefreshStatus = status;
        manualRoster = null;
        manualRefreshDeadline = 0;
    }

    private DirectoryConversation[] ResolveFavorites(DirectoryConversation[] live)
    {
        if (departedContacts == null || departedContacts.Favorites is not { Count: > 0 } saved)
            return Array.Empty<DirectoryConversation>();

        var byId = new Dictionary<string, DirectoryConversation>(StringComparer.Ordinal);
        foreach (var item in live)
            if (DepartedContacts.FavoriteKey(item.Entry) is { } key)
                byId.TryAdd(key, item);

        return saved.Select<FavoriteContact, DirectoryConversation?>(favorite =>
        {
            var entry = favorite.Person;
            var key = DepartedContacts.FavoriteKey(entry);
            if (key != null && byId.TryGetValue(key, out var current))
                return current;
            if (key == ClubDirectory.LegacyAssistantActorId)
                return null;
            var source = favorite.Source switch
            {
                FavoriteSource.Player => TalkPage.Players,
                FavoriteSource.Staff => TalkPage.Staff,
                _ => TalkPage.Contacts
            };
            var profile = ProfileFor(entry, source) with
            {
                Brief = $"收藏联系人。上次记录的分组是{entry.Team}，职责是{entry.Role}。目前归属尚待确认。",
                RoleInCareer = "收藏联系人 · 归属待确认"
            };
            return new DirectoryConversation(source, entry, profile, FavoriteOnly: true);
        }).OfType<DirectoryConversation>().ToArray();
    }

    private PlayerProfile ProfileForSelection(DirectoryPerson entry)
    {
        if (page == TalkPage.Favorites && FindFavorite(entry) is { } favorite)
            return favorite.Profile;
        return ProfileFor(entry, page);
    }

    private void ShowConversation(VisualElement document, DirectoryConversation target)
    {
        Show(document, target.FavoriteOnly ? TalkPage.Favorites : target.Destination, target.Entry);
    }

    private bool IsRemovableContact(DirectoryPerson? entry)
    {
        return page is TalkPage.Contacts or TalkPage.Favorites && entry?.PublicId is uint id &&
            departedContacts?.CanRemove(id, roster?.ManagerId ?? 0) == true &&
            contacts.Any(item => item.PublicId == id);
    }

    private void ToggleFavorite()
    {
        if (!UseCurrentSaveSession() || person is not { } entry || DepartedContacts.FavoriteKey(entry) == null ||
            departedContacts == null)
            return;
        var origin = page switch
        {
            TalkPage.Players => FavoriteSource.Player,
            TalkPage.Staff => FavoriteSource.Staff,
            _ => FavoriteSource.Contact
        };
        try
        {
            var changed = departedContacts.IsFavorite(entry)
                ? departedContacts.RemoveFavorite(entry)
                : departedContacts.AddFavorite(entry, origin);
            if (changed)
            {
                RefreshRosterView();
                RefreshFavoriteAction();
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            statusText = "暂时无法保存收藏，请重试。";
            hasError = true;
            RefreshControls();
            Plugin.Logger.LogWarning($"FM Player Talk favorite update failed: {error.GetType().Name}.");
        }
    }

    private void RefreshFavoriteAction()
    {
        if (favoriteButton == null)
            return;
        var visible = DepartedContacts.FavoriteKey(person) != null && departedContacts != null;
        favoriteButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        if (visible)
        {
            var saved = departedContacts!.IsFavorite(person!);
            favoriteButton.text = saved ? "★" : "☆";
            favoriteButton.style.color = saved ? new Color(1f, 0.81f, 0.29f) : Secondary;
            favoriteButton.tooltip = UiLanguage.Get(saved ? "取消收藏" : "收藏联系人");
        }
    }
}
