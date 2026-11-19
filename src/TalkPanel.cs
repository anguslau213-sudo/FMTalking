using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    // The session directory scopes history to a career. Keep the original filename namespace for older chats.
    private const string AllGroups = "全部";
    private const string BlockedStatus = "这位联系人已停止接收你的消息。";
    private const float DraftHeight = 70f;
    private const float BubbleRadius = 14f;
    private const float DraftRadius = 12f;
    private const float TranscriptWidth = 820f;
    private const float TypingStepSeconds = 0.45f;
    private const int DinnerScanBatch = 4;
    private const float RosterCheckInterval = 2f;
    private const float InboxCheckInterval = 20f;
    private static readonly Color Foreground = new(0.96f, 0.95f, 0.98f);
    private static readonly Color Secondary = new(0.76f, 0.74f, 0.81f);
    private static readonly Color Border = new(0.24f, 0.20f, 0.29f);
    private static readonly Color Purple = new(0.48f, 0.15f, 0.77f);
    private static readonly Color AccentText = new(0.80f, 0.61f, 0.97f);
    private static readonly Color SelectedRow = new(0.17f, 0.13f, 0.23f);
    private static readonly Color SidebarSurface = new(0.106f, 0.085f, 0.137f);
    private static readonly Color ChatSurface = new(0.082f, 0.067f, 0.110f);
    private static readonly Color HeaderSurface = new(0.097f, 0.079f, 0.123f);
    private static readonly Color DraftSurface = new(0.133f, 0.102f, 0.184f);
    private static readonly Color OutgoingBubble = new(0.267f, 0.153f, 0.392f);
    private static readonly Color IncomingBubble = new(0.155f, 0.129f, 0.196f);
    private static readonly Color ControlBackground = new(0.13f, 0.11f, 0.17f);
    private static readonly Color Caution = new(1f, 0.73f, 0.40f);
    private static readonly string[] TypingDots = { ".", "..", "...", ".." };
    private static readonly TalkPage[] DirectoryPages = { TalkPage.Players, TalkPage.Staff, TalkPage.Contacts, TalkPage.Favorites };
    private readonly GameTextEntry editor = new();
    private readonly GameTextEntry searchEditor = new();
    private readonly Dictionary<string, string> drafts = new();
    private readonly Dictionary<string, Conversation> conversations = new();
    private readonly Dictionary<TalkPage, DirectoryState> directories = new();
    private readonly Dictionary<string, (Button Button, Action Invoke)> clickable = new();
    private readonly List<(DirectoryPerson Person, Button Button, Label Unread, Label Subtitle)> personRows = new();
    private readonly List<Button> recentRows = new();
    private readonly List<(string Name, Button Button)> groupButtons = new();
    private DirectoryPerson? person;
    private PlayerProfile? player;
    private Conversation? conversation;
    private TalkPage page;
    private TalkPage? waitingPage;
    private VisualElement? host;
    private VisualElement? layer;
    private VisualElement? portraitSlot;
    private ScrollView? directoryList;
    private ScrollView? recentList;
    private VisualElement? groupBar;
    private Label? directorySource;
    private Button? searchButton;
    private string renderedQuery = "";
    private int buttonSerial;
    private float pageTop;
    private ScrollView? transcript;
    private VisualElement? transcriptBody;
    private Label? relationship;
    private Label? relationshipDetails;
    private Button? relationshipButton;
    private VisualElement? relationshipPanel;
    private Label? playerName;
    private Label? playerContext;
    private Label? relationshipScores;
    private Button? removeContactButton;
    private Button? favoriteButton;
    private VisualElement? feedbackRow;
    private Label? statusLabel;
    private Button? acceptProposalButton;
    private Label? composerBanner;
    private VisualElement? composerRow;
    private Label? composerHint;
    private Button? draftButton;
    private ScrollView? draftViewport;
    private int draftLayoutFrame = -1;
    private Button? sendButton;
    private string? armedClick;
    private TalkPage? armedPage;
    private Task<Reply>? pending;
    private string? pendingPerson;
    private string pendingMessage = "";
    private float pendingSince;
    private PendingReceipt? pendingSignal;
    private Label? pendingReceipt;
    private Label? pendingTyping;
    private float? typingSince;
    private string? dinnerStatus;
    private DateTime? polledGameDate;
    private float nextDinnerCheck;
    private int dinnerScanIndex;
    private string? saveSessionDirectory;
    private LiveRoster? roster;
    private LiveRoster? previousRosterSample;
    private InboxClubContacts? inboxRead;
    private DirectoryPerson[] inboxStaff = Array.Empty<DirectoryPerson>();
    private DirectoryPerson[] staffEntries = Array.Empty<DirectoryPerson>();
    private float nextInboxCheck;
    private DirectoryPerson? visibleAssistant;
    private string? inboxError;
    private DepartedContacts? departedContacts;
    private DirectoryPerson[] contacts = Array.Empty<DirectoryPerson>();
    private DirectoryConversation[] conversationDirectory = Array.Empty<DirectoryConversation>();
    private DirectoryConversation[] favoriteDirectory = Array.Empty<DirectoryConversation>();
    private float nextRosterCheck;
    private string? rosterError;
    private string nameLanguage = "";
    private VisualElement? scrollTarget;
    private string statusText = "";
    private bool hasError;
    private bool connectionWasBusy;
    private int layerLanguageRevision = -1;

    private string ReplyName => person == null ? "" : DisplayName(person);
    private bool Editing => editor.Active || searchEditor.Active || SettingsEditing;
    private bool WaitingHere => pending != null && player?.Id == pendingPerson;

    private sealed class PendingReceipt
    {
        internal PendingReceipt(DateTime? gameDate) => GameDate = gameDate?.Date;

        internal DateTime? GameDate { get; }
        internal volatile bool Read;
        internal PromiseWaiverRequest? PromiseRequest;
        internal string? PromiseStatus;
        internal bool PromiseMentioned;
        internal bool PromiseContextChanged;
        internal readonly PendingAffinity Affinity = new();
        internal BoardFundingOffer? FundingOffer;
    }

    public void Open(VisualElement document, TalkPage destination)
    {
        if (UseCurrentSaveSession())
        {
            RefreshLiveRoster();
            Show(document, destination, null);
        }
        else
            ShowSaveSyncStatus(document, destination);
    }

    private string DisplayName(DirectoryPerson entry) => !entry.NativeDisplayName && entry.PublicId is uint id
        ? NativeNameTable.Find(nameLanguage, id) ?? entry.Name
        : entry.Name;

    private void RefreshNameLanguage()
    {
        if (Editing)
            return;

        string? language;
        try
        {
            language = SI.Translation.TranslationManager.CurrentLanguage?.Trim().ToLowerInvariant();
        }
        catch (Exception error)
        {
            if (nameLanguage == "")
            {
                nameLanguage = "unavailable";
                Plugin.Logger.LogInfo($"FM Player Talk name language unavailable: {error.GetType().Name}.");
            }
            return;
        }
        if (string.IsNullOrEmpty(language))
            return;
        var key = language switch
        {
            "chinese" or "simplifiedchinese" or "simplified chinese" or "chinese (simplified)" or
                "chinese_simplified" or "zh-cn" or "zh-hans" => "zh-CN",
            "traditionalchinese" or "traditional chinese" or "chinese (traditional)" or
                "chinese traditional" or "chinese_traditional" or "traditional_chinese" or "zh-tw" or "zh-hant" => "zh-TW",
            "english" or "english (us)" or "en-gb" or "en-us" => "en",
            _ => language
        };
        if (key == nameLanguage)
            return;

        nameLanguage = key;
        NativeNameTable.SetScope(saveSessionDirectory, nameLanguage);
        ResetInboxContacts();
        Plugin.Logger.LogInfo($"FM Player Talk roster name locale: {key}.");
        RefreshRosterView();
        if (playerName != null && person != null)
            playerName.text = DisplayName(person);
    }

    private bool UseCurrentSaveSession()
    {
        var live = GameSaveTimeline.DirectoryPath;
        if (saveSessionDirectory == live)
            return live != null;

        var openWhenReady = waitingPage;
        CancelPlayerIntroduction();
        introductionAliases.Clear();
        nextIntroductionCheck = 0;
        Close();
        saveSessionDirectory = live;
        NativeNameTable.SetScope(live, nameLanguage);
        if (pending is { } previous)
            _ = previous.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        pendingPerson = null;
        pendingMessage = "";
        pendingSignal?.Affinity.Cancel();
        pendingSignal = null;
        person = null;
        player = null;
        conversation = null;
        drafts.Clear();
        directories.Clear();
        conversations.Clear();
        roster = null;
        previousRosterSample = null;
        LiveClubRoster.Reset();
        FmNativeAffinity.Reset();
        ClubFinanceContext.Reset();
        ResetInboxContacts();
        ManagerAssistantContact.Refresh(null);
        FinishManualRefresh("");
        departedContacts = null;
        nextRosterCheck = 0;
        rosterError = null;
        if (live != null)
        {
            try { departedContacts = new DepartedContacts(live); }
            catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or
                UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Plugin.Logger.LogWarning($"FM Player Talk contacts could not be loaded: {error.GetType().Name}.");
            }
        }
        RebuildConversationDirectory();
        dinnerStatus = null;
        polledGameDate = null;
        dinnerScanIndex = 0;
        nextDinnerCheck = 0;
        DinnerMessagePin.Tick(null, null);
        if (live != null && openWhenReady is { } destination && GameUi.ActiveCareerDocument is { } document)
            Show(document, destination, null);
        return live != null;
    }

    private void ShowSaveSyncStatus(VisualElement document, TalkPage destination)
    {
        Close();
        host = document;
        page = TalkPage.Home;
        waitingPage = destination;
        layer = new VisualElement { name = "fm-player-talk-save-sync" };
        var style = layer.style;
        style.position = Position.Absolute;
        style.left = 0;
        style.right = 0;
        style.top = GameUi.GetContentTop(document);
        style.bottom = 0;
        style.backgroundColor = ControlBackground;
        if (GameUi.FindTalkFont() is { } font)
            style.unityFontDefinition = new StyleFontDefinition(font);
        Pad(layer, 16);
        var syncStatus = UiLabel(GameSaveTimeline.RestoreFailed ? "交流记录读取失败。重新载入当前 FM 存档可再试。" :
            "正在识别当前 FM 生涯，读取完成后会打开此页面。", 16, Foreground);
        syncStatus.name = "fm-player-talk-sync-status";
        layer.Add(syncStatus);
        layer.Add(Control("返回游戏", Close, ControlBackground));
        if (!MountTalkLayer(document, layer))
            Close();
    }

    private void Show(VisualElement document, TalkPage destination, DirectoryPerson? initial)
    {
        if (layer is { panel: not null } currentLayer && currentLayer.parent?.Pointer == document.Pointer &&
            page == destination && initial == null && layerLanguageRevision == UiLanguage.Revision)
        {
            GameUi.AttachPageContent(document, currentLayer, activate: true);
            return;
        }

        try
        {
            Close();
            host = document;
            page = destination;
            if (DirectoryPages.Contains(page))
            {
                var state = Directory;
                var entries = Entries(page);
                if (state.Group != AllGroups && !entries.Any(entry => DirectoryGroup(entry) == state.Group))
                    state.Group = AllGroups;
                var preferred = initial ?? state.Selected;
                var selected = preferred == null ? null : entries.FirstOrDefault(entry => ProfileIdFor(entry) == ProfileIdFor(preferred));
                selected ??= entries.FirstOrDefault(MatchesDirectoryGroup);
                if (initial != null && selected != null && ProfileIdFor(initial) == ProfileIdFor(selected))
                {
                    state.Query = "";
                    if (!MatchesDirectoryGroup(selected))
                        state.Group = AllGroups;
                }
                else if (page is (TalkPage.Contacts or TalkPage.Favorites) && selected != null && !MatchesDirectoryGroup(selected))
                    selected = entries.FirstOrDefault(MatchesDirectoryGroup);
                searchEditor.ReplaceDraft(state.Query);
                SelectPerson(selected);
            }
            Build(document);
            layerLanguageRevision = UiLanguage.Revision;
        }
        catch (Exception ex)
        {
            Close();
            Plugin.Logger.LogWarning($"FM Player Talk panel: {ex}");
        }
    }

    private DirectoryPerson[] Entries(TalkPage destination) => destination switch
    {
        TalkPage.Players => roster?.Players ?? Array.Empty<DirectoryPerson>(),
        TalkPage.Staff => staffEntries,
        TalkPage.Contacts => contacts,
        TalkPage.Favorites => favoriteDirectory.Select(item => item.Entry).ToArray(),
        _ => Array.Empty<DirectoryPerson>()
    };

    private void RefreshLiveRoster()
    {
        if (Time.unscaledTime < nextRosterCheck || !GameUi.HasActiveCareerEntry())
            return;
        LiveRoster? sample;
        try { sample = LiveClubRoster.Read(); }
        catch (Exception error)
        {
            nextRosterCheck = Time.unscaledTime + RosterCheckInterval;
            var code = error.GetType().Name;
            if (rosterError != code)
            {
                rosterError = code;
                Plugin.Logger.LogWarning($"FM Player Talk live directory read failed: {code}.");
            }
            return;
        }
        if (sample == null)
            return;
        RememberCurrentNames(sample.Players);
        RememberCurrentNames(sample.Staff);
        nextRosterCheck = Time.unscaledTime + RosterCheckInterval;
        rosterError = null;
        RecordManualRoster(sample);

        var prior = previousRosterSample;
        previousRosterSample = sample;
        var contactsChanged = false;
        if (departedContacts != null && sample.ClubId != 0 && prior != null && SameClub(prior, sample) &&
            prior.Players.Select(x => x.PublicId).ToHashSet().SetEquals(sample.Players.Select(x => x.PublicId)) &&
            prior.Staff.Select(x => x.PublicId).ToHashSet().SetEquals(sample.Staff.Select(x => x.PublicId)))
        {
            try
            {
                var confirmed = prior.ConfirmedDepartures.Concat(sample.ConfirmedDepartures).ToHashSet();
                var confirmedStaff = prior.ConfirmedStaffDepartures.Concat(sample.ConfirmedStaffDepartures).ToHashSet();
                var virtualPlayers = prior.VirtualPlayers.Concat(sample.VirtualPlayers).ToHashSet();
                contactsChanged = departedContacts.UpdateRoster(sample.ManagerId, sample.ClubId, sample.Players,
                    confirmed, sample.Staff, confirmedStaff, virtualPlayers);
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Plugin.Logger.LogWarning($"FM Player Talk could not update departed contacts: {error.GetType().Name}.");
            }
        }

        var changedClub = roster == null || !SameClub(roster, sample) || roster.PrimaryTeamId != sample.PrimaryTeamId;
        if (!contactsChanged && !changedClub && roster!.ClubName == sample.ClubName &&
            roster.IsPartial == sample.IsPartial &&
            roster.Players.SequenceEqual(sample.Players) && roster.Staff.SequenceEqual(sample.Staff))
            return;

        if (changedClub)
            ResetInboxContacts();
        var oldStaff = roster?.Staff;
        roster = sample;
        if (changedClub)
            ManagerAssistantContact.Refresh(sample);
        RefreshRosterView();
        if (changedClub)
            Plugin.Logger.LogInfo($"FM Player Talk live directory: {sample.Players.Length} player(s), {sample.Staff.Length} staff.");
        if (changedClub || oldStaff == null || !oldStaff.SequenceEqual(sample.Staff))
        {
            var board = sample.Staff.Count(entry => entry.Team == LiveClubRoster.BoardGroup);
            var medical = sample.Staff.Count(entry => entry.Team == LiveClubRoster.MedicalGroup);
            Plugin.Logger.LogInfo($"FM Player Talk departments: {board} board, {medical} medical.");
        }
    }

    private static bool SameClub(LiveRoster left, LiveRoster right) =>
        left.ManagerId == right.ManagerId && left.ClubId == right.ClubId;

    private void ResetInboxContacts()
    {
        ResetRelationshipGameEvents();
        inboxRead?.Dispose();
        inboxRead = null;
        inboxStaff = Array.Empty<DirectoryPerson>();
        visibleAssistant = null;
        nextInboxCheck = 0;
        inboxError = null;
    }

    private void RefreshInboxContacts()
    {
        var now = Time.unscaledTime;
        if (now < nextInboxCheck || roster is not { ClubId: > 0, PrimaryTeamId: > 0 } active ||
            !GameUi.HasActiveCareerEntry())
            return;
        try
        {
            if (inboxRead == null)
            {
                if (LiveClubRoster.FindBindings() is not { } bindings)
                {
                    nextInboxCheck = now + InboxCheckInterval;
                    return;
                }
                inboxRead = InboxClubContacts.Create(bindings, active.ManagerId, active.ClubId, active.PrimaryTeamId,
                    ManagerAssistantContact.Current?.PublicId, now, active.ClubName,
                    conversations.Values.Any(chat => chat.CanReceiveProactiveMessages) ? GameCalendar.ReadDate() : null);
            }
            if (!inboxRead.TryRead(now, out var read))
                return;

            visibleAssistant = inboxRead.VisibleAssistant ?? visibleAssistant;
            RememberRelationshipGameEvents(active, inboxRead.VerifiedGameEvents);
            inboxRead.Dispose();
            inboxRead = null;
            nextInboxCheck = now + InboxCheckInterval;
            inboxError = null;
            RecordManualInbox(active, read);
            if (read != null)
            {
                RememberCurrentNames(read);
                if (ManagerAssistantContact.Current?.PublicId is uint id)
                {
                    if (visibleAssistant?.PublicId == id)
                        read = read.Where(entry => entry.PublicId != id).Append(visibleAssistant)
                            .OrderBy(entry => entry.PublicId).ToArray();
                    else if (read.All(entry => entry.PublicId != id) &&
                        inboxStaff.FirstOrDefault(entry => entry.PublicId == id) is { } previousAssistant)
                        read = read.Append(previousAssistant).OrderBy(entry => entry.PublicId).ToArray();
                }
                if (!inboxStaff.SequenceEqual(read))
                {
                    inboxStaff = read;
                    RefreshRosterView();
                }
            }
        }
        catch (Exception error)
        {
            inboxRead?.Dispose();
            inboxRead = null;
            nextInboxCheck = now + InboxCheckInterval;
            var code = error.GetType().Name;
            if (inboxError != code)
                Plugin.Logger.LogInfo($"FM Player Talk inbox contacts unavailable: {code}.");
            inboxError = code;
        }
    }

    private void RebuildConversationDirectory()
    {
        if (roster == null)
            staffEntries = Array.Empty<DirectoryPerson>();
        else
        {
            var existing = roster.Players.Concat(roster.Staff).Select(entry => entry.PublicId).ToHashSet();
            var staff = roster.Staff.Concat(inboxStaff.Where(entry => existing.Add(entry.PublicId))).ToArray();
            staffEntries = IncludeManagerAssistant(staff, inboxStaff);
        }
        var activeIds = Entries(TalkPage.Players).Concat(Entries(TalkPage.Staff)).Select(x => x.PublicId).ToHashSet();
        contacts = ResolveExternalContacts(activeIds);
        var live = DirectoryPages.Where(destination => destination != TalkPage.Favorites).Reverse()
            .SelectMany(destination => Entries(destination).Select(entry =>
                new DirectoryConversation(destination, entry, ProfileFor(entry, destination))))
            .DistinctBy(item => item.Profile.Id)
            .ToArray();
        favoriteDirectory = ResolveFavorites(live);
        conversationDirectory = live.Concat(favoriteDirectory).DistinctBy(item => item.Profile.Id).ToArray();
        dinnerScanIndex = 0;
        foreach (var item in conversationDirectory)
            if (conversations.TryGetValue(item.Profile.Id, out var chat))
                chat.UpdateActorContext(item.Profile, roster?.ClubName);
    }

    private void RefreshRosterView()
    {
        RebuildConversationDirectory();
        if (directoryList != null && DirectoryPages.Contains(page))
        {
            var offset = directoryList.scrollOffset;
            RenderGroups();
            var entries = Entries(page);
            var selected = player == null ? null : entries.FirstOrDefault(entry => ProfileIdFor(entry) == player.Id &&
                (page is not (TalkPage.Contacts or TalkPage.Favorites) || MatchesDirectoryGroup(entry)));
            if (selected == null)
                SelectPerson(entries.FirstOrDefault(MatchesDirectoryGroup));
            else if (person != selected || player != ProfileForSelection(selected))
            {
                person = Directory.Selected = selected;
                player = ProfileForSelection(selected);
                conversation = ConversationFor(player);
                RefreshAffinity(force: true);
                RefreshSelection();
                RefreshControls();
            }
            RenderDirectory();
            directoryList.scrollOffset = offset;
        }
        RenderRecent();
    }

    private DirectoryState Directory
    {
        get
        {
            if (!directories.TryGetValue(page, out var state))
            {
                state = new DirectoryState();
                directories.Add(page, state);
            }
            return state;
        }
    }

    private sealed class DirectoryState
    {
        public string Group = AllGroups;
        public string Query = "";
        public DirectoryPerson? Selected;
    }

    private sealed record DirectoryConversation(TalkPage Destination, DirectoryPerson Entry, PlayerProfile Profile,
        bool FavoriteOnly = false);

    private static string ProfileIdFor(DirectoryPerson entry) => entry.Profile?.Id ??
        entry.PublicId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (entry.Name + ":" + entry.Role);

    private PlayerProfile ProfileFor(DirectoryPerson entry, TalkPage destination)
    {
        if (entry.Profile != null)
            return entry.Profile;
        var role = destination switch
        {
            TalkPage.Staff => "主教练 · 俱乐部职员",
            TalkPage.Contacts => entry.Team == DepartedContacts.StaffGroup ? "外部职员" : "外部球员",
            _ => "主教练 · 球员"
        };
        var brief = (destination, entry.Team) switch
        {
            (TalkPage.Contacts, DepartedContacts.PlayerGroup) =>
                $"曾效力于主教练执教过的俱乐部，最后记录的场上位置是{entry.Role}。",
            (TalkPage.Contacts, DepartedContacts.StaffGroup) =>
                $"曾在主教练执教过的俱乐部任职，最后记录的职责是{entry.Role}。",
            (TalkPage.Contacts, _) => "本生涯有过对话，当前效力球队和职责尚待确认。",
            _ => $"当前在{roster?.ClubName}的{entry.Team}，职责是{entry.Role}。"
        };
        return new PlayerProfile(ProfileIdFor(entry), DisplayName(entry), brief)
        {
            Age = entry.Age,
            IsFemale = entry.IsFemale,
            RoleInCareer = role,
            DialogueStyle = "围绕你在球队的角色、训练、人员与比赛交流。根据已给出的资料回答。"
        };
    }

    private Conversation ConversationFor(PlayerProfile profile)
    {
        if (!conversations.TryGetValue(profile.Id, out var result) ||
            profile.IsAssignedManagerAssistant && !result.UsesManagerAssistantAffinity && pendingPerson != profile.Id)
        {
            result = new Conversation(Conversation.ActiveCareerStorageKey,
                saveSessionDirectory ?? throw new InvalidOperationException("未识别当前 FM 生涯。"), profile,
                request: null, timingLog: message => Plugin.Logger.LogInfo(message), managerClub: roster?.ClubName,
                useGameAffinity: true);
            conversations[profile.Id] = result;
        }
        else
            result.UpdateActorContext(profile, roster?.ClubName);
        return result;
    }

    private void SelectPerson(DirectoryPerson? entry)
    {
        if (person != null && person == entry)
            return;
        editor.Stop();
        searchEditor.Stop();
        RefreshSearch();
        if (player != null)
            drafts[player.Id] = editor.Text;
        person = entry;
        Directory.Selected = entry;
        player = entry == null ? null : ProfileForSelection(entry);
        conversation = player == null ? null : ConversationFor(player);
        RefreshAffinity(force: true);
        dinnerStatus = null;
        if (composerBanner != null && conversation != null)
            UpdateDinnerStatus(GameCalendar.ReadDate());
        editor.ReplaceDraft(player != null && drafts.TryGetValue(player.Id, out var draft) ? draft : "");
        statusText = "";
        hasError = false;
        RefreshSelection();
        RenderTranscript();
        RefreshControls();
        if (MarkVisibleFollowUpsRead())
            RefreshUnreadRows();
    }

    public void Tick()
    {
        TickSettings();
        if (!UseCurrentSaveSession())
        {
            if (GameSaveTimeline.RestoreFailed && layer?.Q<Label>("fm-player-talk-sync-status") is { } status)
                status.text = UiLanguage.Get("交流记录读取失败。重新载入当前 FM 存档可再试。");
            return;
        }
        RefreshNameLanguage();
        RefreshLiveRoster();
        if (ManagerAssistantContact.Refresh(roster))
        {
            inboxRead?.Dispose();
            inboxRead = null;
            nextInboxCheck = 0;
            RefreshRosterView();
        }
        RefreshInboxContacts();
        ClubFinanceContext.Tick(roster);
        UpdateManualRefresh();
        TickPlayerIntroduction();
        if (layer != null)
        {
            if (layer.panel == null || host == null || !GameUi.HasEntry(host) || !RefreshTalkLayer())
                Close();
            else if (layerLanguageRevision != UiLanguage.Revision && !Editing && !SettingsBusy && pending == null)
                Show(host, page, null);
            else
            {
                var top = GameUi.GetContentTop(host);
                if (Math.Abs(top - pageTop) > 0.5f)
                {
                    pageTop = top;
                    layer.style.top = top;
                }
            }
        }

        TickActivePromises();
        pendingSignal?.Affinity.Tick();
        if (layer != null && RefreshAffinity())
            RefreshControls();
        if (pending?.IsCompleted == true)
            CompleteSend();
        else if (pending != null)
            RefreshPendingIndicators();

        var busy = SettingsBusy || ModelConnection.IsBusy;
        if (busy != connectionWasBusy)
        {
            connectionWasBusy = busy;
            RefreshControls();
        }
        TickConversationFollowUps();

        if (transcript != null && scrollTarget != null)
        {
            transcript.ScrollTo(scrollTarget);
            scrollTarget = null;
        }
    }

    public void PollTyping()
    {
        try
        {
            UpdateDraftViewport();
            if (!Editing)
                return;
            if (layer?.panel == null)
            {
                Close();
                return;
            }
            if (editor.Poll())
                RefreshControls();
            if (searchEditor.Poll())
                RefreshSearch();
            PollSettingsTyping();
            if (Editing)
                PollEditingMouse();
            else
            {
                armedClick = null;
                armedPage = null;
            }
        }
        catch (Exception ex)
        {
            editor.Stop();
            searchEditor.Stop();
            CloseSettings();
            statusText = "输入已结束，草稿已保留。";
            hasError = true;
            Plugin.Logger.LogWarning($"FM Player Talk keyboard: {ex}");
            RefreshControls();
        }
    }

    private void PollEditingMouse()
    {
        var mouse = Mouse.current;
        var panel = layer?.panel;
        if (mouse == null || panel == null)
            return;

        var down = mouse.leftButton.wasPressedThisFrame;
        var up = mouse.leftButton.wasReleasedThisFrame;
        if (!down && !up)
            return;

        var screen = mouse.position.ReadValue();
        screen.y = Screen.height - screen.y;
        var position = RuntimePanelUtils.ScreenToPanel(panel, screen);
        var target = panel.Pick(position);
        var navigation = GameUi.NavigationTarget(target);
        if (down && navigation == null && target != null && layer is { } overlay && !overlay.Contains(target))
        {
            ReleaseExternalFocus();
            return;
        }
        string? key = null;
        for (var element = target; element != null; element = element.parent)
        {
            if (element.name is { } name && clickable.TryGetValue(name, out var control) &&
                control.Button.enabledInHierarchy)
            {
                key = name;
                break;
            }
            if (element.Pointer == layer!.Pointer)
                break;
        }
        if (down)
        {
            armedClick = key;
            armedPage = navigation;
        }
        if (!up)
            return;

        var clicked = armedClick;
        var chosenPage = armedPage;
        armedClick = null;
        armedPage = null;
        if (navigation.HasValue && navigation == chosenPage && host is { } document)
        {
            GameUi.CloseMenu();
            Open(document, navigation.Value);
        }
        else if (key != null && key == clicked && clickable.TryGetValue(key, out var action))
            action.Invoke();
    }

    private void Build(VisualElement host)
    {
        var backdrop = new VisualElement { name = "fm-player-talk-panel" };
        layer = backdrop;
        var overlay = backdrop.style;
        overlay.position = Position.Absolute;
        overlay.left = 0;
        pageTop = GameUi.GetContentTop(host);
        overlay.top = pageTop;
        overlay.right = 0;
        overlay.bottom = 0;
        overlay.backgroundColor = new Color(0.065f, 0.052f, 0.09f);
        Pad(backdrop, 10);
        var font = GameUi.FindTalkFont();
        if (font is not null)
            overlay.unityFontDefinition = new StyleFontDefinition(font);
        backdrop.RegisterCallback<KeyDownEvent>((Action<KeyDownEvent>)(evt =>
        {
            if (Editing)
                return;
            if (evt.keyCode != KeyCode.Escape)
                return;
            evt.StopImmediatePropagation();
            Close();
        }), TrickleDown.TrickleDown);
        backdrop.RegisterCallback<DetachFromPanelEvent>((Action<DetachFromPanelEvent>)(_ =>
        {
            editor.Stop();
            searchEditor.Stop();
        }));

        var header = Row();
        var section = UiLabel(PageName(page), 16, Foreground);
        section.style.flexGrow = 1;
        header.Add(section);
        if (page != TalkPage.Settings)
            header.Add(Control("设置", () => Open(host, TalkPage.Settings), ControlBackground));
        header.Add(Control("返回游戏", Close, ControlBackground));
        header.style.marginBottom = 8;
        backdrop.Add(header);

        var content = Row();
        content.style.flexGrow = 1;
        content.style.minHeight = 0;
        content.style.alignItems = Align.Stretch;
        content.style.backgroundColor = ChatSurface;
        content.style.overflow = Overflow.Hidden;
        Frame(content, Border, 8);
        backdrop.Add(content);
        if (page == TalkPage.Home)
            BuildHome(content, host);
        else if (page == TalkPage.Settings)
            BuildSettings(content, host);
        else
        {
            BuildDirectory(content, host);
            BuildConversation(content);
            RefreshSelection();
            if (conversation != null)
                UpdateDinnerStatus(GameCalendar.ReadDate());
            RenderTranscript();
            RefreshControls();
        }

        if (!MountTalkLayer(host, backdrop))
        {
            Close();
            return;
        }
        if (MarkVisibleFollowUpsRead())
            RefreshUnreadRows();
    }

    private static string PageName(TalkPage destination) => destination switch
    {
        TalkPage.Players => "球员对话",
        TalkPage.Staff => "职员对话",
        TalkPage.Contacts => "外部联系",
        TalkPage.Favorites => "收藏联系人",
        TalkPage.Settings => "交流设置",
        _ => "交流"
    };

    private void BuildHome(VisualElement content, VisualElement document)
    {
        var groups = MakeSidebar();
        content.Add(groups);
        groups.Add(SectionHeading("人员分组"));
        foreach (var option in DirectoryPages)
        {
            var button = Control(PageName(option), () => Open(document, option), ControlBackground);
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            groups.Add(button);
        }

        var recent = new VisualElement();
        recent.style.flexGrow = 1;
        recent.style.minWidth = 0;
        Pad(recent, 11);
        content.Add(recent);
        recent.Add(SectionHeading("最近对话"));
        recentList = VerticalList();
        recent.Add(recentList);
        RenderRecent();
        Plugin.Logger.LogInfo("FM Player Talk opened home.");
    }

    private void RenderRecent()
    {
        if (recentList == null || host is not { } document)
            return;

        foreach (var row in recentRows)
            clickable.Remove(row.name);
        recentRows.Clear();
        recentList.Clear();
        var saved = conversationDirectory
            .Select(item => (item, chat: conversations.GetValueOrDefault(item.Profile.Id)))
            .Where(item => item.chat?.LastSavedAtUtc != null)
            .OrderByDescending(item => HasUnreadFollowUp(item.chat))
            .ThenByDescending(item => item.chat!.LastSavedAtUtc)
            .Take(18)
            .ToArray();
        if (saved.Length == 0)
            recentList.Add(UiLabel(roster == null || dinnerScanIndex < conversationDirectory.Length
                ? "正在读取当前生涯的对话…" : "还没有对话", 14, Secondary));
        foreach (var item in saved)
        {
            var target = item.item;
            var row = PersonRow(target.Entry, () => ShowConversation(document, target), out _, out _);
            recentRows.Add(row);
            recentList.Add(row);
        }
    }

    private void BuildDirectory(VisualElement content, VisualElement document)
    {
        var sidebar = MakeSidebar();
        content.Add(sidebar);
        var headingRow = Row();
        headingRow.style.marginBottom = 5;
        var heading = SectionHeading("人员分组");
        heading.style.flexGrow = 1;
        heading.style.marginBottom = 0;
        headingRow.Add(heading);
        var back = Control("‹ 返回", () => Open(document, TalkPage.Home), Color.clear);
        back.tooltip = UiLanguage.Get("返回交流首页");
        back.style.flexShrink = 0;
        back.style.fontSize = 12;
        back.style.minHeight = 28;
        Frame(back, Color.clear, 6);
        headingRow.Add(back);
        sidebar.Add(headingRow);
        searchButton = Control("", EditSearch, ControlBackground);
        searchButton.style.unityTextAlign = TextAnchor.MiddleLeft;
        searchButton.style.whiteSpace = WhiteSpace.NoWrap;
        searchButton.style.textOverflow = TextOverflow.Ellipsis;
        searchButton.style.overflow = Overflow.Hidden;
        searchButton.style.minHeight = 35;
        Frame(searchButton, Border, 16);
        searchEditor.Bind(searchButton, RefreshSearch, () =>
        {
            searchEditor.Stop();
            RefreshSearch();
        });
        sidebar.Add(searchButton);

        groupBar = Row();
        groupBar.style.flexWrap = Wrap.Wrap;
        groupBar.style.marginTop = 5;
        groupBar.style.marginBottom = 6;
        sidebar.Add(groupBar);
        RenderGroups();

        directoryList = VerticalList();
        sidebar.Add(directoryList);
        if (page is TalkPage.Players or TalkPage.Staff)
        {
            directorySource = Text("", 11, Secondary);
            directorySource.style.marginTop = 7;
            sidebar.Add(directorySource);
        }
        renderedQuery = searchEditor.Text;
        RefreshSearch();
        RenderDirectory();
        Plugin.Logger.LogInfo($"FM Player Talk opened {page} directory ({Entries(page).Length} people).");
    }

    private void RenderGroups()
    {
        if (groupBar == null)
            return;
        var allNames = Entries(page).Select(DirectoryGroup).Prepend(AllGroups).Distinct().ToArray();
        if (allNames.SequenceEqual(groupButtons.Select(choice => choice.Name)))
            return;
        if (!allNames.Contains(Directory.Group))
            Directory.Group = AllGroups;
        foreach (var choice in groupButtons)
            clickable.Remove(choice.Button.name);
        groupButtons.Clear();
        groupBar.Clear();
        foreach (var group in allNames)
        {
            var chip = Control("", () => SelectGroup(group), ControlBackground);
            chip.text = GroupCaption(group);
            chip.tooltip = chip.text;
            chip.style.maxWidth = Length.Percent(100);
            chip.style.whiteSpace = WhiteSpace.NoWrap;
            chip.style.textOverflow = TextOverflow.Ellipsis;
            chip.style.overflow = Overflow.Hidden;
            chip.style.fontSize = 12;
            chip.style.minHeight = 26;
            chip.style.paddingLeft = 7;
            chip.style.paddingRight = 7;
            groupButtons.Add((group, chip));
            groupBar.Add(chip);
        }
    }

    private string GroupCaption(string group) => group == AllGroups ||
        (page == TalkPage.Favorites && group is "球员" or "职员") ||
        group is "经理办公室" or "俱乐部高层" or "俱乐部联络人" or "医疗组" or "离队球员" or "离队职员" or "已介绍球员"
        ? UiLanguage.Get(group) : group;

    private static ScrollView VerticalList()
    {
        var list = new ScrollView(ScrollViewMode.Vertical);
        list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        list.style.flexGrow = 1;
        list.style.flexShrink = 1;
        list.style.minHeight = 0;
        StyleScrollbar(list);
        return list;
    }

    private static void StyleScrollbar(ScrollView view)
    {
        var scroller = view.verticalScroller;
        scroller.style.backgroundColor = HeaderSurface;
        if (GameUi.FindNamed(scroller, "unity-tracker", 5) is { } track)
        {
            track.style.backgroundImage = StyleKeyword.None;
            track.style.backgroundColor = ControlBackground;
            Frame(track, ControlBackground, 5);
        }
        if (GameUi.FindNamed(scroller, "unity-dragger", 5) is { } thumb)
        {
            thumb.style.backgroundImage = StyleKeyword.None;
            thumb.style.backgroundColor = new Color(0.34f, 0.28f, 0.40f);
            Frame(thumb, Border, 5);
        }
    }

    private static VisualElement MakeSidebar()
    {
        var panel = new VisualElement();
        panel.style.backgroundColor = SidebarSurface;
        panel.style.borderRightWidth = 1;
        panel.style.borderRightColor = Border;
        panel.style.width = Length.Percent(27);
        panel.style.minWidth = 185;
        panel.style.maxWidth = 340;
        Pad(panel, 12);
        return panel;
    }

    private static Label SectionHeading(string title)
    {
        var heading = UiLabel(title, 14, AccentText);
        heading.style.marginBottom = 7;
        return heading;
    }

    private Button PersonRow(DirectoryPerson entry, Action select, out Label unread, out Label subtitle)
    {
        var row = Control("", select, Color.clear);
        row.tooltip = entry.Role;
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.justifyContent = Justify.FlexStart;
        row.style.minHeight = 49;
        row.style.flexShrink = 0;
        row.style.paddingLeft = 7;
        row.style.paddingRight = 7;
        Frame(row, Color.clear, 10);
        var portrait = PlayerPortraits.Create(entry.Initials, entry.PublicId, 32);
        portrait.style.marginRight = 8;
        row.Add(portrait);
        var details = new VisualElement { pickingMode = PickingMode.Ignore };
        details.style.flexGrow = 1;
        details.style.flexShrink = 1;
        details.style.minWidth = 0;
        details.Add(SingleLine(DisplayName(entry), 14, Foreground));
        subtitle = SingleLine(entry.Role, 11, Secondary);
        details.Add(subtitle);
        row.Add(details);
        unread = Text("未读", 12, Caution);
        unread.pickingMode = PickingMode.Ignore;
        unread.style.marginLeft = 6;
        unread.style.flexShrink = 0;
        row.Add(unread);
        RefreshPersonRow(entry, unread, subtitle);
        return row;
    }

    private void RefreshPersonRow(DirectoryPerson entry, Label unread, Label subtitle)
    {
        var chat = conversations.GetValueOrDefault(ProfileIdFor(entry));
        unread.style.display = HasUnreadFollowUp(chat) ? DisplayStyle.Flex : DisplayStyle.None;
        var hideIncoming = chat?.DinnerStatus(polledGameDate) != null;
        var preview = chat?.History.LastOrDefault(turn => !turn.IsIncoming || !hideIncoming)?.Assistant;
        subtitle.text = string.IsNullOrWhiteSpace(preview) ? entry.Role :
            preview.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private void EditSearch()
    {
        if (searchEditor.Active || layer == null || searchButton == null)
            return;
        try
        {
            editor.Stop();
            searchEditor.Start(layer, searchButton);
            RefreshSearch();
            RefreshControls();
        }
        catch (Exception ex)
        {
            searchEditor.Stop();
            Plugin.Logger.LogWarning($"FM Player Talk directory search: {ex}");
            RefreshSearch();
        }
    }

    private void RefreshSearch()
    {
        if (searchButton == null)
            return;
        searchButton.text = searchEditor.Active ? searchEditor.Display :
            searchEditor.Text.Length > 0 ? searchEditor.Text : UiLanguage.Get("搜索姓名或职责");
        Directory.Query = searchEditor.Text;
        if (renderedQuery == Directory.Query)
            return;
        renderedQuery = Directory.Query;
        RenderDirectory();
    }

    private void SelectGroup(string group)
    {
        if (Directory.Group == group)
            return;
        searchEditor.Stop();
        RefreshSearch();
        Directory.Group = group;
        if (page is TalkPage.Contacts or TalkPage.Favorites)
        {
            var entries = Entries(page).Where(MatchesDirectoryGroup);
            var selected = entries.FirstOrDefault(entry => ProfileIdFor(entry) == player?.Id);
            SelectPerson(selected ?? entries.FirstOrDefault());
        }
        RenderDirectory();
    }

    private void RenderDirectory()
    {
        if (directoryList == null)
            return;
        foreach (var row in personRows)
            clickable.Remove(row.Button.name);
        personRows.Clear();
        directoryList.Clear();
        if (directorySource != null)
            directorySource.text = roster?.ClubId > 0
                ? roster.ClubName + " · " + UiLanguage.Get(roster.IsPartial ? "部分人员待同步" : "实时名册") : "";
        foreach (var choice in groupButtons)
        {
            var selected = choice.Name == Directory.Group;
            choice.Button.style.backgroundColor = selected ? SelectedRow : ControlBackground;
            choice.Button.style.color = selected ? AccentText : Foreground;
            Frame(choice.Button, selected ? Purple : Border, 12);
        }
        var query = Directory.Query.Trim();
        var results = Entries(page).Where(entry => MatchesDirectoryGroup(entry) &&
            (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             DisplayName(entry).Contains(query, StringComparison.OrdinalIgnoreCase) ||
             entry.Role.Contains(query, StringComparison.OrdinalIgnoreCase)));
        foreach (var group in results.GroupBy(DirectoryGroup))
        {
            var heading = Text(GroupCaption(group.Key), 12, Secondary);
            heading.style.marginTop = 6;
            heading.style.marginBottom = 4;
            directoryList.Add(heading);
            foreach (var entry in group)
            {
                var row = PersonRow(entry, () => SelectPerson(entry), out var unread, out var subtitle);
                personRows.Add((entry, row, unread, subtitle));
                directoryList.Add(row);
            }
        }
        if (personRows.Count == 0)
        {
            var empty = DirectoryEmptyText(query);
            directoryList.Add(UiLabel(empty, 13, Secondary));
        }
        directoryList.scrollOffset = Vector2.zero;
        HighlightSelection();
    }

    private void HighlightSelection()
    {
        foreach (var row in personRows)
        {
            var selected = row.Person == person;
            row.Button.style.backgroundColor = selected ? SelectedRow : Color.clear;
            Frame(row.Button, selected ? Border : Color.clear, 10);
        }
    }

    private void RefreshSelection()
    {
        HighlightSelection();
        if (portraitSlot == null || playerName == null || playerContext == null)
            return;
        portraitSlot.Clear();
        playerName.text = person == null ? UiLanguage.Get("选择一位联系人") : DisplayName(person);
        playerContext.text = person == null ? "" : $"{GroupCaption(person.Team)} · {person.Role}";
        if (person != null)
            portraitSlot.Add(PlayerPortraits.Create(person.Initials, person.PublicId, 44));
        SetRelationshipPanel(false);
        if (relationshipButton != null)
            relationshipButton.style.display = person == null ? DisplayStyle.None : DisplayStyle.Flex;
        RefreshFavoriteAction();
        RefreshIntroductionAction();
        if (removeContactButton != null)
            removeContactButton.style.display = departedContacts != null && IsRemovableContact(person)
                ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void RemoveSelectedContact()
    {
        if (!UseCurrentSaveSession() || !IsRemovableContact(person) || person?.PublicId is not uint id || departedContacts == null)
            return;
        try
        {
            if (!departedContacts.Remove(id, roster?.ManagerId ?? 0))
                return;
            RefreshRosterView();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            statusText = "暂时无法删除联系人，请重试。";
            hasError = true;
            RefreshControls();
            Plugin.Logger.LogWarning($"FM Player Talk contact removal failed: {error.GetType().Name}.");
        }
    }

    private void ToggleRelationshipPanel()
    {
        if (relationshipPanel != null)
            SetRelationshipPanel(relationshipPanel.style.display.value == DisplayStyle.None);
    }

    private void SetRelationshipPanel(bool visible)
    {
        if (relationshipPanel == null || relationshipButton == null)
            return;
        relationshipPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        relationshipButton.text = UiLanguage.Get(visible ? "收起关系系统" : "关系系统");
    }

    private void BuildConversation(VisualElement content)
    {
        var surface = new VisualElement();
        surface.style.flexGrow = 1;
        surface.style.minWidth = 0;
        content.Add(surface);
        var identity = Row();
        identity.style.flexShrink = 0;
        identity.style.backgroundColor = HeaderSurface;
        Pad(identity, 12);
        identity.style.paddingLeft = 17;
        identity.style.paddingRight = 17;
        identity.style.borderBottomWidth = 1;
        identity.style.borderBottomColor = Border;
        portraitSlot = new VisualElement();
        portraitSlot.style.marginRight = 9;
        identity.Add(portraitSlot);
        var identityText = new VisualElement();
        identityText.style.flexGrow = 1;
        identityText.style.minWidth = 0;
        var nameRow = Row();
        nameRow.style.minWidth = 0;
        playerName = Text("", 18, Foreground);
        playerName.style.unityFontStyleAndWeight = FontStyle.Bold;
        playerName.style.minWidth = 0;
        playerName.style.flexShrink = 1;
        playerName.style.overflow = Overflow.Hidden;
        playerName.style.textOverflow = TextOverflow.Ellipsis;
        nameRow.Add(playerName);
        favoriteButton = Control("☆", ToggleFavorite, Color.clear);
        favoriteButton.style.color = Secondary;
        favoriteButton.style.fontSize = 20;
        favoriteButton.style.width = 28;
        favoriteButton.style.minHeight = 28;
        favoriteButton.style.flexShrink = 0;
        favoriteButton.style.marginLeft = 4;
        favoriteButton.style.unityTextAlign = TextAnchor.MiddleCenter;
        Pad(favoriteButton, 0);
        Frame(favoriteButton, Color.clear, 4);
        favoriteButton.style.display = DisplayStyle.None;
        nameRow.Add(favoriteButton);
        identityText.Add(nameRow);
        playerContext = Text("", 12, Secondary);
        identityText.Add(playerContext);
        relationshipScores = Text("", 12, AccentText);
        relationshipScores.style.marginTop = 3;
        identityText.Add(relationshipScores);
        identity.Add(identityText);
        var identityActions = new VisualElement();
        identityActions.style.marginLeft = 12;
        identityActions.style.maxWidth = Length.Percent(50);
        identityActions.style.alignItems = Align.FlexEnd;
        relationshipButton = Control("关系系统", ToggleRelationshipPanel, ControlBackground);
        relationshipButton.style.fontSize = 12;
        Frame(relationshipButton, Border, 8);
        identityActions.Add(relationshipButton);
        removeContactButton = Control("删除联系人", RemoveSelectedContact, ControlBackground);
        removeContactButton.tooltip = UiLanguage.Get("从外部联系移除，保留对话记录");
        removeContactButton.style.fontSize = 12;
        removeContactButton.style.minHeight = 24;
        removeContactButton.style.marginTop = 4;
        removeContactButton.style.display = DisplayStyle.None;
        identityActions.Add(removeContactButton);
        identity.Add(identityActions);
        surface.Add(identity);

        relationshipPanel = new VisualElement();
        relationshipPanel.style.display = DisplayStyle.None;
        relationshipPanel.style.flexShrink = 0;
        relationshipPanel.style.backgroundColor = HeaderSurface;
        relationshipPanel.style.borderBottomWidth = 1;
        relationshipPanel.style.borderBottomColor = Border;
        relationshipPanel.style.paddingLeft = 17;
        relationshipPanel.style.paddingRight = 17;
        relationshipPanel.style.paddingBottom = 10;
        relationship = Text("", 13, AccentText);
        relationshipPanel.Add(relationship);
        relationshipDetails = Text("", 12, Secondary);
        relationshipDetails.style.marginTop = 2;
        relationshipPanel.Add(relationshipDetails);
        surface.Add(relationshipPanel);

        transcript = VerticalList();
        transcript.style.flexGrow = 1;
        transcript.style.flexBasis = 0;
        transcriptBody = new VisualElement();
        transcriptBody.style.width = Length.Percent(100);
        transcriptBody.style.maxWidth = TranscriptWidth;
        transcriptBody.style.alignSelf = Align.Center;
        transcriptBody.style.paddingLeft = 17;
        transcriptBody.style.paddingRight = 17;
        transcriptBody.style.paddingTop = 13;
        transcriptBody.style.paddingBottom = 8;
        transcript.Add(transcriptBody);
        surface.Add(transcript);

        var composer = new VisualElement();
        composer.style.backgroundColor = HeaderSurface;
        composer.style.borderTopWidth = 1;
        composer.style.borderTopColor = Border;
        composer.style.flexShrink = 0;
        Pad(composer, 10);
        composer.style.paddingLeft = 15;
        composer.style.paddingRight = 15;
        surface.Add(composer);
        AddIntroductionAction(composer);

        feedbackRow = Row();
        feedbackRow.style.marginBottom = 6;
        feedbackRow.style.flexShrink = 0;
        statusLabel = Text("", 13, Secondary);
        statusLabel.style.minHeight = 20;
        statusLabel.style.flexGrow = 1;
        statusLabel.style.minWidth = 0;
        feedbackRow.Add(statusLabel);
        acceptProposalButton = Control("接受关系提议", AcceptProposal, Purple);
        acceptProposalButton.style.marginLeft = 8;
        acceptProposalButton.style.flexShrink = 0;
        acceptProposalButton.style.display = DisplayStyle.None;
        feedbackRow.Add(acceptProposalButton);
        AddBoardFundingControl(feedbackRow);
        composer.Add(feedbackRow);

        composerBanner = Text("", 14, Caution);
        composerBanner.style.display = DisplayStyle.None;
        composerBanner.style.flexShrink = 0;
        composerBanner.style.backgroundColor = DraftSurface;
        Frame(composerBanner, Border, 9);
        Pad(composerBanner, 10);
        composer.Add(composerBanner);

        composerRow = Row();
        composerRow.style.flexShrink = 0;
        composerRow.style.alignItems = Align.FlexEnd;
        composerRow.style.backgroundColor = DraftSurface;
        Frame(composerRow, Border, DraftRadius);
        Pad(composerRow, 4);
        draftViewport = new ScrollView(ScrollViewMode.Vertical)
        {
            horizontalScrollerVisibility = ScrollerVisibility.Hidden,
            verticalScrollerVisibility = ScrollerVisibility.Auto
        };
        draftViewport.style.flexGrow = 1;
        draftViewport.style.flexBasis = 0;
        draftViewport.style.flexShrink = 1;
        draftViewport.style.minWidth = 0;
        draftViewport.style.height = DraftHeight;
        draftViewport.style.backgroundColor = Color.clear;
        draftViewport.contentContainer.style.width = Length.Percent(100);
        StyleScrollbar(draftViewport);

        draftButton = Control("", EditDraft, Color.clear);
        editor.Bind(draftButton, RefreshControls, Send, () => !WaitingHere);
        draftButton.tooltip = UiLanguage.Get("点击后在游戏内输入");
        var draftStyle = draftButton.style;
        draftStyle.alignSelf = Align.Stretch;
        draftStyle.flexShrink = 0;
        draftStyle.minWidth = 0;
        draftStyle.minHeight = DraftHeight - 2;
        draftStyle.fontSize = 15;
        draftStyle.unityTextAlign = TextAnchor.UpperLeft;
        draftStyle.whiteSpace = WhiteSpace.PreWrap;
        draftStyle.textOverflow = TextOverflow.Clip;
        draftStyle.marginLeft = 0;
        draftStyle.marginRight = 0;
        draftStyle.marginTop = 0;
        draftStyle.marginBottom = 0;
        draftStyle.paddingTop = 10;
        draftStyle.paddingBottom = 10;
        draftStyle.borderLeftWidth = draftStyle.borderRightWidth = 0;
        draftStyle.borderTopWidth = draftStyle.borderBottomWidth = 0;
        draftButton.RegisterCallback<GeometryChangedEvent>((Action<GeometryChangedEvent>)(_ => QueueDraftLayout()));
        draftViewport.Add(draftButton);
        composerRow.Add(draftViewport);
        sendButton = Control("发送", Send, Purple);
        sendButton.RegisterCallback<PointerDownEvent>((Action<PointerDownEvent>)(evt =>
        {
            if (editor.Active)
                sendButton?.focusController?.IgnoreEvent(evt);
        }), TrickleDown.TrickleDown);
        sendButton.style.marginLeft = 4;
        sendButton.style.minWidth = 68;
        sendButton.style.minHeight = 35;
        Frame(sendButton, new Color(0.57f, 0.28f, 0.81f), 10);
        composerRow.Add(sendButton);
        composer.Add(composerRow);
        composerHint = UiLabel("回车发送 · Shift + 回车换行", 12, Secondary);
        composerHint.style.marginTop = 5;
        composerHint.style.flexShrink = 0;
        composer.Add(composerHint);
    }

    private void EditDraft()
    {
        if (conversation == null || conversation.IsBlocked || dinnerStatus != null || WaitingHere || editor.Active ||
            layer == null || draftButton == null)
            return;

        try
        {
            searchEditor.Stop();
            RefreshSearch();
            editor.Start(layer, draftButton);
            statusText = "";
            hasError = false;
        }
        catch (Exception ex)
        {
            statusText = "暂时无法锁定 FM 的快捷键，输入未启用。";
            hasError = true;
            Plugin.Logger.LogWarning($"FM Player Talk message editor: {ex}");
        }
        RefreshControls();
    }

    private void AcceptProposal()
    {
        if (!UseCurrentSaveSession())
            return;
        RefreshAffinity(force: true);
        var active = conversation;
        if (active == null || !active.HasPendingProposal || active.IsBlocked || dinnerStatus != null || WaitingHere)
            return;

        editor.Stop();
        searchEditor.Stop();
        RefreshSearch();
        try
        {
            var warning = active.AcceptProposal(SynchronizePermanentAffinity, GameCalendar.ReadDate());
            RefreshAffinity(force: true);
            statusText = warning ?? "已接受关系提议。";
            hasError = warning != null;
            RenderTranscript();
        }
        catch (Exception ex)
        {
            statusText = ex is InvalidOperationException ? ex.Message : "暂时无法接受这项提议，请重试。";
            hasError = true;
            Plugin.Logger.LogWarning($"FM Player Talk proposal failed: {ex.GetType().Name}");
        }
        RefreshControls();
    }

    private void Send()
    {
        if (!UseCurrentSaveSession() || pending != null || conversation == null || conversation.IsBlocked || conversation.LoadWarning != null ||
            player == null || editor.Composing || SettingsBusy || ModelConnection.IsBusy)
            return;

        var date = GameCalendar.ReadDate();
        UpdateDinnerStatus(date);
        if (dinnerStatus != null)
        {
            RefreshControls();
            return;
        }

        var active = conversation;
        var input = editor.ReadCommittedText().Trim();
        if (input.Length == 0)
        {
            RefreshControls();
            return;
        }
        if (RoleBoundary.IsOutOfWorldInput(input))
        {
            editor.Clear();
            drafts.Remove(player.Id);
            statusText = Conversation.RoleBoundaryNotice;
            hasError = true;
            RefreshControls();
            return;
        }
        if (TryQueuePlayerIntroduction(input, date, active))
            return;
        statusText = "";
        hasError = false;
        armedClick = null;
        pendingMessage = input;
        pendingPerson = player.Id;
        pendingSince = Time.unscaledTime;
        var signal = new PendingReceipt(date);
        pendingSignal = signal;
        var nativeAffinity = CaptureAffinity(signal, date);
        signal.PromiseMentioned = PromiseWaiverLanguage.MentionsPromise(input);
        var hasPromiseContext = signal.PromiseMentioned || active.HasRecentPromiseContext(date);
        signal.PromiseRequest = CapturePromiseWaiver(date, active, hasPromiseContext, out signal.PromiseStatus);
        if (signal.PromiseMentioned)
            Plugin.Logger.LogInfo("FM Player Talk promise request: " + (signal.PromiseStatus ?? "active promise context supplied."));
        var finance = FinanceForCurrentContact();
        var shadowBoard = CaptureShadowBoard(date);
        var funding = CaptureBoardFunding(date, active, nativeAffinity?.Value,
            out signal.FundingOffer);
        var canConfirmIntroduction = CanConfirmPlayerIntroduction(active, date);
        var dateLabel = date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "missing";
        Plugin.Logger.LogInfo($"FM Player Talk submitted a conversation request. FM game date={dateLabel}.");
        pending = Task.Run(() => active.SendAsync(input, date, () => signal.Read = true, finance,
            signal.PromiseRequest?.PromptOptions, nativeAffinity, funding, shadowBoard, canConfirmIntroduction));
        RenderTranscript();
        RefreshControls();
    }

    private void CompleteSend()
    {
        var finished = pending!;
        var id = pendingPerson!;
        var selected = player?.Id == id;
        var signal = pendingSignal;
        pending = null;
        pendingPerson = null;
        pendingMessage = "";
        pendingSignal = null;
        var elapsed = (int)Math.Max(0, Time.unscaledTime - pendingSince);
        try
        {
            var reply = finished.GetAwaiter().GetResult();
            var (promiseStatus, promiseError) = CompletePromiseWaiver(signal, reply);
            var fundingStatus = ReceiveBoardFunding(signal, reply);
            drafts.Remove(id);
            if (selected)
            {
                editor.Clear();
                statusText = reply.WasInterrupted ? reply.Text :
                    reply.AffinityWarning ?? promiseStatus ?? fundingStatus ?? "对话已保存。";
                hasError = reply.WasInterrupted || reply.AffinityWarning != null || promiseError;
            }
            if (reply.WasInterrupted)
                Plugin.Logger.LogInfo("FM Player Talk withheld an out-of-world reply.");
            else
                Plugin.Logger.LogInfo($"FM Player Talk received and saved a reply after {elapsed} s.");
        }
        catch (Exception ex)
        {
            if (selected)
            {
                statusText = ex is InvalidOperationException or TimeoutException
                    ? ex.Message
                    : "本次对话未能完成，请重试。";
                hasError = true;
            }
            Plugin.Logger.LogWarning($"FM Player Talk request failed: {ex.GetType().Name}");
        }
        if (selected && player is { } currentProfile)
        {
            conversation = ConversationFor(currentProfile);
            RefreshAffinity(force: true);
        }
        RefreshUnreadRows();
        if (recentList == null)
        {
            if (selected && composerBanner != null)
                UpdateDinnerStatus(GameCalendar.ReadDate());
            RenderTranscript();
            RefreshControls();
        }
    }

    private bool UpdateDinnerStatus(DateTime? date)
    {
        var status = conversation?.DinnerStatus(date);
        if (status == dinnerStatus)
            return false;
        dinnerStatus = status;
        return true;
    }

    private void TickConversationFollowUps()
    {
        var now = Time.unscaledTime;
        var poll = now >= nextDinnerCheck;
        var refreshUnread = false;
        if (poll)
        {
            nextDinnerCheck = now + 1f;
            var date = GameCalendar.ReadDate();
            refreshUnread = date?.Date != polledGameDate?.Date;
            polledGameDate = date;
        }

        if (polledGameDate.HasValue || recentList != null)
            refreshUnread |= ScanStoredConversations();
        if (poll)
        {
            var redrawTranscript = false;
            if (polledGameDate is DateTime today)
            {
                var relationshipEvents = conversations.Values.Any(chat => chat.CanReceiveProactiveMessages)
                    ? ReadRelationshipGameEvents(today) : Array.Empty<RelationshipGameEvent>();
                foreach (var item in conversationDirectory)
                {
                    if (!conversations.TryGetValue(item.Profile.Id, out var chat))
                        continue;
                    var dinnerDelivered = chat.TryDeliverDinnerFollowUp(polledGameDate);
                    var promiseDelivered = chat.TryDeliverPromiseFollowUps(polledGameDate);
                    var relationshipDelivered = !dinnerDelivered && !promiseDelivered &&
                        chat.TryDeliverRelationshipMessage(today, relationshipEvents);
                    if (!dinnerDelivered && !promiseDelivered && !relationshipDelivered)
                        continue;
                    refreshUnread = true;
                    redrawTranscript |= ReferenceEquals(chat, conversation);
                    if (dinnerDelivered)
                        Plugin.Logger.LogInfo("FM Player Talk delivered a dinner follow-up.");
                    if (promiseDelivered)
                        Plugin.Logger.LogInfo("FM Player Talk delivered a promise waiver follow-up.");
                    if (relationshipDelivered)
                        Plugin.Logger.LogInfo("FM Player Talk delivered a relationship message.");
                }
            }

            var lockChanged = composerBanner != null && UpdateDinnerStatus(polledGameDate);
            if (redrawTranscript || lockChanged)
                RenderTranscript();
            if (lockChanged)
                RefreshControls();
            refreshUnread |= MarkVisibleFollowUpsRead();
        }

        if (refreshUnread)
            RefreshUnreadRows();
        var unread = ModelConnection.HomeNotifications ? FindUnreadFollowUp() : null;
        if (unread == null)
            DinnerMessagePin.Tick(null, null);
        else
        {
            var sender = DisplayName(unread.Entry);
            DinnerMessagePin.Tick(sender,
                document => OpenUnreadFollowUp(document, unread));
        }
    }

    private bool ScanStoredConversations()
    {
        if (dinnerScanIndex >= conversationDirectory.Length)
            return false;

        var refreshRecent = false;
        var end = Math.Min(conversationDirectory.Length, dinnerScanIndex + DinnerScanBatch);
        for (; dinnerScanIndex < end; dinnerScanIndex++)
        {
            var profile = conversationDirectory[dinnerScanIndex].Profile;
            if (!conversations.ContainsKey(profile.Id))
                refreshRecent |= ConversationFor(profile).LastSavedAtUtc.HasValue;
        }
        return refreshRecent || dinnerScanIndex == conversationDirectory.Length;
    }

    private bool HasUnreadFollowUp(Conversation? chat) => polledGameDate.HasValue && chat != null &&
        (chat.HasUnreadDinnerFollowUp || chat.HasUnreadPromiseFollowUp || chat.HasUnreadRelationshipMessage || chat.HasUnreadPlayerIntroduction) &&
        chat.DinnerStatus(polledGameDate) == null;

    private DirectoryConversation? FindUnreadFollowUp() => polledGameDate == null ? null :
        conversationDirectory.FirstOrDefault(item => HasUnreadFollowUp(conversations.GetValueOrDefault(item.Profile.Id)));

    private void OpenUnreadFollowUp(VisualElement document, DirectoryConversation target)
    {
        if (GameUi.HasEntry(document) && UseCurrentSaveSession() &&
            HasUnreadFollowUp(conversations.GetValueOrDefault(target.Profile.Id)) &&
            conversationDirectory.FirstOrDefault(item => item.Profile.Id == target.Profile.Id) is { } current)
            ShowConversation(document, current);
    }

    private bool MarkVisibleFollowUpsRead() => layer?.panel != null && transcript != null && Application.isFocused &&
        dinnerStatus == null && conversation is { } chat && HasUnreadFollowUp(chat) &&
        (chat.MarkDinnerFollowUpRead() | chat.MarkPromiseFollowUpRead() | chat.MarkRelationshipMessageRead() | chat.MarkPlayerIntroductionRead());

    private void RefreshUnreadRows()
    {
        foreach (var (entry, _, unread, subtitle) in personRows)
            RefreshPersonRow(entry, unread, subtitle);
        RenderRecent();
    }

    private void RefreshControls()
    {
        if (statusLabel == null || relationship == null)
            return;

        var blocked = conversation?.IsBlocked == true;
        var lockText = blocked ? BlockedStatus : dinnerStatus;
        var composerLocked = lockText != null;
        RefreshIntroductionAction();
        if (composerLocked && editor.Active)
            editor.Stop();
        relationship.text = conversation is { LoadWarning: null } ? UiLanguage.Get(conversation.RelationshipLabel) :
            conversation?.RelationshipLabel ?? "";
        var showAffinity = conversation is { LoadWarning: null };
        var affinityText = showAffinity
            ? ReadDisplayedAffinity() is int affinity
                ? string.Format(UiLanguage.Get("亲密度 {0} / 100"), affinity) +
                    (conversation?.UsesManagerAssistantAffinity == true ? " · " + UiLanguage.Get("模组记录") : "")
                : UiLanguage.Get("亲密度暂不可读")
            : "";
        relationship.tooltip = affinityReadReason ?? affinityText;
        if (relationshipScores != null)
        {
            relationshipScores.style.display = showAffinity ? DisplayStyle.Flex : DisplayStyle.None;
            relationshipScores.text = affinityText;
            relationshipScores.tooltip = affinityReadReason ?? affinityText;
        }
        if (relationshipDetails != null)
        {
            var reason = conversation?.History.LastOrDefault()?.Reason;
            relationshipDetails.text = string.IsNullOrEmpty(reason) ? "" :
                string.Format(UiLanguage.Get("最近一次判定：{0}"), reason);
            relationshipDetails.style.display = string.IsNullOrEmpty(reason) ? DisplayStyle.None : DisplayStyle.Flex;
        }
        statusLabel.text = UiLanguage.Get(composerLocked ? (hasError ? statusText : "") :
            WaitingHere ? (editor.Active ? "等待回复 · FM 快捷键仍暂停" : "") :
            editor.Composing ? "请先确认输入法中的选字" :
            hasError && !string.IsNullOrEmpty(statusText) ? statusText :
            editor.Active ? "正在编辑 · FM 快捷键已暂停" :
            !string.IsNullOrEmpty(statusText) ? statusText :
            pending != null ? "另一条对话正在等待回复" :
            SettingsBusy || ModelConnection.IsBusy ? "模型连接正在处理请求，可以先编辑草稿。" :
            conversation?.LoadWarning ?? "");
        statusLabel.style.color = hasError ? Caution : Secondary;
        var canAcceptProposal = !composerLocked && conversation?.HasPendingProposal == true;
        var canAcceptFunding = RefreshBoardFundingControl(composerLocked);
        if (feedbackRow != null)
            feedbackRow.style.display = statusLabel.text.Length > 0 || canAcceptProposal || canAcceptFunding
                ? DisplayStyle.Flex : DisplayStyle.None;
        if (acceptProposalButton != null)
        {
            acceptProposalButton.style.display = canAcceptProposal ? DisplayStyle.Flex : DisplayStyle.None;
            if (canAcceptProposal)
                acceptProposalButton.text = UiLanguage.Get(conversation!.PendingProposalLabel);
            acceptProposalButton.SetEnabled(canAcceptProposal && !WaitingHere);
        }
        if (composerBanner != null)
        {
            composerBanner.text = UiLanguage.Get(lockText ?? "");
            composerBanner.style.display = composerLocked ? DisplayStyle.Flex : DisplayStyle.None;
        }
        if (composerRow != null)
            composerRow.style.display = composerLocked ? DisplayStyle.None : DisplayStyle.Flex;
        if (composerHint != null)
            composerHint.style.display = composerLocked ? DisplayStyle.None : DisplayStyle.Flex;
        var hasDraft = !string.IsNullOrWhiteSpace(editor.Text);
        if (draftButton != null)
        {
            var display = editor.Active || hasDraft ? editor.Display : UiLanguage.Get("点击输入消息…");
            if (draftButton.text != display)
            {
                draftButton.text = display;
                QueueDraftLayout();
            }
            draftButton.style.color = editor.Active || hasDraft ? Foreground : Secondary;
            if (composerRow != null)
                Frame(composerRow, editor.Active ? Purple : Border, DraftRadius);
            draftButton.SetEnabled(conversation != null && !composerLocked && (!WaitingHere || editor.Active));
        }
        if (sendButton != null)
            sendButton.SetEnabled(pending == null && !SettingsBusy && !ModelConnection.IsBusy && conversation != null &&
                !composerLocked && conversation.LoadWarning == null && hasDraft && !editor.Composing);
    }

    private void QueueDraftLayout() => draftLayoutFrame = Time.frameCount + 1;

    private void UpdateDraftViewport()
    {
        if (draftLayoutFrame < 0 || Time.frameCount < draftLayoutFrame)
            return;

        draftLayoutFrame = -1;
        var view = draftViewport;
        var field = draftButton;
        if (view?.panel == null || field == null)
            return;

        var viewport = view.contentViewport;
        var viewportHeight = viewport.layout.height;
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0)
            return;
        editor.UpdateImeAnchor(viewport);

        var maxScroll = Math.Max(0, field.layout.height - viewportHeight);
        if (maxScroll <= 0 || string.IsNullOrEmpty(editor.Text) && !editor.Active)
        {
            view.scrollOffset = Vector2.zero;
            return;
        }

        var caretIndex = editor.DisplayCaretIndex;
        var text = field.text;
        if (caretIndex < 0 || caretIndex == text.Length - 1)
        {
            view.scrollOffset = new Vector2(0, maxScroll);
            return;
        }
        var content = field.contentRect;
        if (!float.IsFinite(content.width) || content.width <= 0)
            return;
        var prefix = text[..Math.Min(caretIndex + 1, text.Length)];
        var measured = field.MeasureTextSize(prefix, content.width, VisualElement.MeasureMode.Exactly,
            float.NaN, VisualElement.MeasureMode.Undefined).y;
        if (!float.IsFinite(measured))
            return;
        var caretBottom = content.yMin + measured;
        var lineHeight = field.resolvedStyle.fontSize * 1.35f;
        var current = view.scrollOffset.y;
        if (caretBottom + 4f > current + viewportHeight)
            current = caretBottom + 4f - viewportHeight;
        if (caretBottom - lineHeight < current)
            current = caretBottom - lineHeight;
        view.scrollOffset = new Vector2(0, Math.Clamp(current, 0, maxScroll));
    }

    private void RenderTranscript()
    {
        if (transcriptBody == null)
            return;

        scrollTarget = null;
        pendingReceipt = null;
        pendingTyping = null;
        typingSince = null;
        transcriptBody.Clear();
        if (conversation == null)
            return;
        var history = conversation.History;
        if (history.Count == 0 && !WaitingHere)
        {
            transcriptBody.Add(UiLabel("还没有对话。", 14, Secondary));
            return;
        }

        var lastOutgoing = history.Count - 1;
        while (lastOutgoing >= 0 && history[lastOutgoing].IsIncoming)
            lastOutgoing--;

        DateTime? displayedDay = null;
        var hasDateGroup = false;
        void StartDateGroup(DateTime? gameDate)
        {
            var day = gameDate?.Date;
            if (hasDateGroup && day == displayedDay)
                return;
            AddDateDivider(day);
            displayedDay = day;
            hasDateGroup = true;
        }

        for (var i = Math.Max(0, history.Count - 40); i < history.Count; i++)
        {
            var turn = history[i];
            if (turn.IsIncoming && dinnerStatus != null)
                continue;

            StartDateGroup(turn.GameDate);
            if (turn.IsIncoming)
            {
                scrollTarget = AddMessage(ReplyName, turn.Assistant, false);
                continue;
            }
            var message = AddMessage("主教练", turn.User, true);
            if (i == lastOutgoing && !WaitingHere)
                AddReceipt(message, "已读");
            scrollTarget = AddMessage(ReplyName, turn.Assistant, false);
        }
        if (WaitingHere)
        {
            StartDateGroup(pendingSignal?.GameDate);
            var message = AddMessage("主教练", pendingMessage, true);
            pendingReceipt = AddReceipt(message, "已发送");
            var typing = Text(UiLanguage.Get("对方正在输入") + TypingDots[0], 12, Secondary);
            typing.style.alignSelf = Align.FlexStart;
            typing.style.minHeight = 19;
            typing.style.marginLeft = 34;
            typing.style.opacity = 0;
            transcriptBody.Add(typing);
            pendingTyping = typing;
            scrollTarget = typing;
            RefreshPendingIndicators();
        }
    }

    private void AddDateDivider(DateTime? day)
    {
        var display = day?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "日期未记录";
        var divider = Text(display, 12, Secondary);
        divider.pickingMode = PickingMode.Ignore;
        divider.style.alignSelf = Align.Center;
        divider.style.unityTextAlign = TextAnchor.MiddleCenter;
        divider.style.marginTop = 6;
        divider.style.marginBottom = 13;
        transcriptBody!.Add(divider);
    }

    private Label AddReceipt(VisualElement message, string text)
    {
        message.style.marginBottom = 2;
        var receipt = UiLabel(text, 11, Secondary);
        receipt.style.alignSelf = Align.FlexEnd;
        receipt.style.unityTextAlign = TextAnchor.MiddleRight;
        receipt.style.marginBottom = 8;
        receipt.style.marginRight = 3;
        transcriptBody!.Add(receipt);
        return receipt;
    }

    private void RefreshPendingIndicators()
    {
        if (!WaitingHere || pendingReceipt == null || pendingTyping == null || pendingSignal?.Read != true)
            return;

        var now = Time.unscaledTime;
        if (!typingSince.HasValue)
        {
            typingSince = now;
            pendingReceipt.text = UiLanguage.Get("已读");
            pendingTyping.style.opacity = 1;
        }
        var frame = (int)((now - typingSince.Value) / TypingStepSeconds) % TypingDots.Length;
        var text = UiLanguage.Get("对方正在输入") + TypingDots[frame];
        if (pendingTyping.text != text)
            pendingTyping.text = text;
    }

    private VisualElement AddMessage(string who, string message, bool manager)
    {
        var row = Row();
        row.style.alignItems = Align.FlexEnd;
        row.style.justifyContent = manager ? Justify.FlexEnd : Justify.FlexStart;
        row.style.marginBottom = 9;
        row.style.flexShrink = 0;
        if (!manager && person is { } sender)
        {
            var avatar = PlayerPortraits.Create(sender.Initials, sender.PublicId, 26);
            avatar.style.marginRight = 8;
            row.Add(avatar);
        }
        var bubble = new VisualElement { tooltip = who };
        bubble.style.maxWidth = Length.Percent(80);
        bubble.style.flexShrink = 1;
        bubble.style.minWidth = 0;
        var fill = manager ? OutgoingBubble : IncomingBubble;
        bubble.style.backgroundColor = fill;
        Frame(bubble, fill, BubbleRadius);
        Pad(bubble, 10);
        bubble.style.paddingLeft = 13;
        bubble.style.paddingRight = 13;
        var body = Text(message, 15, Foreground);
        body.style.whiteSpace = WhiteSpace.PreWrap;
        bubble.Add(body);
        row.Add(bubble);
        transcriptBody!.Add(row);
        return row;
    }

    private void Close()
    {
        introductionButton = null;
        waitingPage = null;
        FmActivePromises.Reset();
        CloseSettings();
        editor.Stop();
        searchEditor.Stop();
        if (player != null)
            drafts[player.Id] = editor.Text;
        if (directoryList != null)
            Directory.Query = searchEditor.Text;
        if (layer?.parent != null)
            layer.RemoveFromHierarchy();
        layer = null;
        host = null;
        transcript = null;
        transcriptBody = null;
        portraitSlot = null;
        directoryList = null;
        recentList = null;
        groupBar = null;
        directorySource = null;
        searchButton = null;
        draftButton = null;
        draftViewport = null;
        draftLayoutFrame = -1;
        sendButton = null;
        armedClick = null;
        armedPage = null;
        clickable.Clear();
        personRows.Clear();
        recentRows.Clear();
        groupButtons.Clear();
        relationship = null;
        relationshipDetails = null;
        relationshipButton = null;
        relationshipPanel = null;
        playerName = null;
        playerContext = null;
        relationshipScores = null;
        favoriteButton = null;
        removeContactButton = null;
        feedbackRow = null;
        statusLabel = null;
        acceptProposalButton = null;
        acceptBoardFundingButton = null;
        ExpireBoardFundingOffer("交流页面已关闭，旧注资报价的确认入口已失效，未执行入账；需要重新申请。");
        composerBanner = null;
        composerRow = null;
        composerHint = null;
        pendingReceipt = null;
        pendingTyping = null;
        typingSince = null;
        scrollTarget = null;
    }

    private static VisualElement Row()
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        return row;
    }

    private static Label Text(string text, int size, Color color)
    {
        var label = new Label(text) { enableRichText = false };
        label.style.whiteSpace = WhiteSpace.Normal;
        label.style.fontSize = size;
        label.style.color = color;
        return label;
    }

    private static Label SingleLine(string text, int size, Color color)
    {
        var label = Text(text, size, color);
        label.pickingMode = PickingMode.Ignore;
        label.style.whiteSpace = WhiteSpace.NoWrap;
        label.style.textOverflow = TextOverflow.Ellipsis;
        label.style.overflow = Overflow.Hidden;
        return label;
    }

    private static Label UiLabel(string key, int size, Color color) => Text(UiLanguage.Get(key), size, color);

    private Button Control(string text, Action click, Color background)
    {
        var button = new Button { text = UiLanguage.Get(text), enableRichText = false, name = $"fm-player-talk-control-{++buttonSerial}" };
        button.add_clicked(click);
        clickable.Add(button.name, (button, click));
        button.style.backgroundColor = background;
        button.style.color = Foreground;
        button.style.paddingLeft = 12;
        button.style.paddingRight = 12;
        button.style.minHeight = 31;
        Frame(button, Border, 4);
        return button;
    }

    private static void Pad(VisualElement element, float size)
    {
        var style = element.style;
        style.paddingLeft = size;
        style.paddingRight = size;
        style.paddingTop = size;
        style.paddingBottom = size;
    }

    private static void Frame(VisualElement element, Color color, float radius)
    {
        var style = element.style;
        style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 1;
        style.borderTopColor = style.borderBottomColor = style.borderLeftColor = style.borderRightColor = color;
        style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = radius;
    }
}
