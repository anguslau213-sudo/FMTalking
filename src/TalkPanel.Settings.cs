using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private readonly Dictionary<ModelMode, Button> connectionChoices = new();
    private readonly Dictionary<ModelMode, ConnectionDraft> connectionDrafts = new();
    private readonly List<Button> languageChoices = new();
    private ModelMode chosenMode;
    private SettingsField? endpointField;
    private SettingsField? modelField;
    private SettingsField? keyField;
    private SettingsField? activeSettingsField;
    private VisualElement? settingsHost;
    private Label? activeConnectionLabel;
    private Label? connectionHint;
    private Label? connectionStatus;
    private Label? refreshStatus;
    private Button? activateConnection;
    private Button? testConnection;
    private Button? notifications;
    private Button? promiseWaivers;
    private Button? boardFunding;
    private Label? promiseWaiverSettingsStatus;
    private Label? nativeDataStatus;
    private bool settingsNativeBuildSupported;
    private float nextNativeSettingsCheck;
    private Task? settingsTest;
    private CancellationTokenSource? settingsTestCancellation;

    private sealed record ConnectionDraft(string BaseUrl, string Model, string NewKey);

    private sealed record SettingsField(VisualElement Row, Button Button, Label Title, GameTextEntry Editor, bool Secret);

    private bool SettingsEditing => activeSettingsField?.Editor.Active == true;
    private bool SettingsBusy => settingsTest is { IsCompleted: false } || GameSaveTimeline.HistoryMigrationBusy;
    private bool CanChangeSettings => pending == null && settingsTest == null && !ModelConnection.IsBusy && !GameSaveTimeline.HistoryMigrationBusy;

    private void BuildSettings(VisualElement content, VisualElement document)
    {
        var view = VerticalList();
        view.contentContainer.style.alignItems = Align.Center;
        content.Add(view);
        var body = new VisualElement();
        body.style.width = Length.Percent(100);
        body.style.maxWidth = 860;
        Pad(body, 16);
        view.Add(body);
        settingsHost = body;

        var back = Control("‹ 返回交流", () => Open(document, TalkPage.Home), Color.clear);
        back.style.alignSelf = Align.FlexStart;
        back.style.marginBottom = 8;
        body.Add(back);

        var credit = Text($"{PluginVersion.Name} {PluginVersion.Release} · {PluginVersion.Credit}", 13, Secondary);
        credit.style.marginBottom = 10;
        body.Add(credit);

        var languageCard = SettingsCard(body, "界面语言");
        var languages = Row();
        languages.style.flexWrap = Wrap.Wrap;
        languageCard.Add(languages);
        var languageStatus = Text("", 13, Caution);
        languageStatus.style.display = DisplayStyle.None;
        languageCard.Add(languageStatus);
        AddLanguageChoice(UiLanguageChoice.Auto, UiLanguage.Get("跟随游戏"));
        AddLanguageChoice(UiLanguageChoice.Chinese, "简体中文");
        AddLanguageChoice(UiLanguageChoice.English, "English");
        AddLanguageChoice(UiLanguageChoice.Japanese, "日本語");
        AddLanguageChoice(UiLanguageChoice.Korean, "한국어");

        var modelCard = SettingsCard(body, "模型连接");
        activeConnectionLabel = Text("", 14, AccentText);
        activeConnectionLabel.style.marginBottom = 7;
        modelCard.Add(activeConnectionLabel);
        var modes = Row();
        modes.style.flexWrap = Wrap.Wrap;
        modes.style.marginBottom = 5;
        foreach (var mode in new[] { ModelMode.Codex, ModelMode.LocalHttp, ModelMode.RemoteHttp })
        {
            var choice = Control("", () => SelectConnection(mode), ControlBackground);
            choice.style.minHeight = 34;
            choice.style.marginRight = 6;
            choice.style.marginBottom = 4;
            connectionChoices.Add(mode, choice);
            modes.Add(choice);
        }
        modelCard.Add(modes);
        connectionHint = Text("", 13, Secondary);
        connectionHint.style.marginBottom = 6;
        modelCard.Add(connectionHint);
        endpointField = AddConnectionField(modelCard, "接口地址");
        modelField = AddConnectionField(modelCard, "模型 ID");
        keyField = AddConnectionField(modelCard, "API 密钥", secret: true);

        var actions = Row();
        actions.style.flexWrap = Wrap.Wrap;
        actions.style.marginTop = 5;
        testConnection = Control("测试连接", StartSettingsTest, ControlBackground);
        testConnection.style.marginRight = 6;
        testConnection.style.marginBottom = 4;
        actions.Add(testConnection);
        activateConnection = Control("保存并启用", ActivateConnection, Purple);
        activateConnection.style.marginBottom = 4;
        actions.Add(activateConnection);
        modelCard.Add(actions);
        connectionStatus = Text("", 13, Secondary);
        connectionStatus.style.marginTop = 3;
        modelCard.Add(connectionStatus);

        var contactsCard = SettingsCard(body, "联系人与通知");
        var refresh = Control("立即刷新联系人", () =>
        {
            StopSettingsInput();
            RequestManualRefresh();
            RefreshSettingsStatus();
        }, ControlBackground);
        refresh.style.alignSelf = Align.FlexStart;
        contactsCard.Add(refresh);
        refreshStatus = Text("", 13, Secondary);
        refreshStatus.style.marginTop = 4;
        refreshStatus.style.marginBottom = 8;
        contactsCard.Add(refreshStatus);
        notifications = Control("", ToggleHomeNotifications, ControlBackground);
        notifications.style.alignSelf = Align.FlexStart;
        contactsCard.Add(notifications);

        var history = Control("查看历史对话（只读）", OpenHistoryArchive, ControlBackground);
        history.style.alignSelf = Align.FlexStart;
        history.style.marginTop = 8;
        contactsCard.Add(history);
        contactsCard.Add(Text(UiLanguage.Get("人物尚未在当前生涯核实时，也可以阅读已保存的原始对话。历史记录不会自动成为联系人。"), 13, Secondary));

        var gameplay = SettingsCard(body, "影响 FM 原版数据");
        nativeDataStatus = Text("", 13, Secondary);
        nativeDataStatus.style.marginBottom = 8;
        gameplay.Add(nativeDataStatus);
        nextNativeSettingsCheck = 0;
        promiseWaivers = Control("", TogglePromiseWaivers, ControlBackground);
        promiseWaivers.style.alignSelf = Align.FlexStart;
        gameplay.Add(promiseWaivers);
        promiseWaiverSettingsStatus = Text(UiLanguage.Get("和球员提到「承诺」就会进入协商，也可以直接发送「放弃承诺」。球员同意后，模组当日移除已核实的位置和角色承诺，次日投递来信。保存 FM 存档可保留变更。"), 13, Secondary);
        promiseWaiverSettingsStatus.style.marginTop = 4;
        gameplay.Add(promiseWaiverSettingsStatus);
        boardFunding = Control("", ToggleBoardFunding, ControlBackground);
        boardFunding.style.alignSelf = Align.FlexStart;
        boardFunding.style.marginTop = 8;
        gameplay.Add(boardFunding);
        gameplay.Add(Text(UiLanguage.Get("实验功能。亲密度满 80 后可向本俱乐部高层申请。确认后增加俱乐部余额，每 90 个游戏日最多一笔。请用本地存档副本测试。"), 13, Secondary));

        BuildSaveHistorySettings(body);

        var help = SettingsCard(body, "使用说明");
        var guide = new VisualElement();
        guide.style.display = DisplayStyle.None;
        Button? expand = null;
        expand = Control("展开说明", () =>
        {
            StopSettingsInput();
            var closed = guide.style.display.value == DisplayStyle.None;
            guide.style.display = closed ? DisplayStyle.Flex : DisplayStyle.None;
            if (expand != null)
                expand.text = UiLanguage.Get(closed ? "收起说明" : "展开说明");
        }, ControlBackground);
        expand.style.alignSelf = Align.FlexStart;
        help.Add(expand);
        guide.style.marginTop = 7;
        AddHelp(guide, "本机 Codex", "使用这台电脑上已登录的 Codex。模型 ID 留空时由 Codex 选择。");
        AddHelp(guide, "本地模型", "先启动本机的 OpenAI 兼容模型服务，再填它的接口地址和模型 ID。接口只接受本机地址，密钥按服务设置选填。");
        AddHelp(guide, "远程 API", "填服务商提供的 HTTPS 接口地址、模型 ID 和 API 密钥。测试会发送一条示例请求，可能计入服务商用量。密钥由 Windows 加密保存。");
        AddHelp(guide, "存档和上下文", "聊天、关系和约定保存在本机模组目录，并与 FM 存档版本配对。当前模型会收到本轮所需的人物资料、关系和最近最多 8 轮对话。与俱乐部高层交谈时，模组也会附上能核实的本队财务摘要。");
        AddHelp(guide, "保存位置", ModDataPath.Root);
        help.Add(guide);

        chosenMode = ModelConnection.Current.Mode;
        LoadConnectionDraft();
        SetConnectionStatus("选好连接方式后，点击「保存并启用」。");
        RefreshSettingsStatus();

        void AddLanguageChoice(UiLanguageChoice language, string name)
        {
            var selected = UiLanguage.Selection == language;
            var button = Control((selected ? "◉ " : "○ ") + name,
                () => ChangeSettingsLanguage(document, language, languageStatus), selected ? SelectedRow : ControlBackground);
            if (GameUi.FindTalkFont(language) is { } font)
                button.style.unityFontDefinition = new StyleFontDefinition(font);
            button.style.marginRight = 6;
            button.style.marginBottom = 4;
            Frame(button, selected ? Purple : Border, 7);
            languageChoices.Add(button);
            languages.Add(button);
        }
    }

    private void ChangeSettingsLanguage(VisualElement document, UiLanguageChoice language, Label languageStatus)
    {
        if (UiLanguage.Selection == language || !CanChangeSettings)
            return;

        StopSettingsInput();
        var previousMode = chosenMode;
        var drafts = new Dictionary<ModelMode, ConnectionDraft>(connectionDrafts)
        {
            [chosenMode] = ReadConnectionDraft()
        };
        var historyPath = saveHistoryPath?.Editor.ReadCommittedText();
        if (!UiLanguage.Set(language))
        {
            languageStatus.text = UiLanguage.Get("无法保存界面语言，请重试。");
            languageStatus.style.display = DisplayStyle.Flex;
            return;
        }

        Open(document, TalkPage.Settings);
        if (settingsHost == null)
            return;

        foreach (var (mode, draft) in drafts)
            connectionDrafts[mode] = draft;
        chosenMode = previousMode;
        LoadConnectionDraft();
        if (historyPath != null && saveHistoryPath is { } history)
        {
            history.Editor.ReplaceDraft(historyPath);
            RenderConnectionField(history);
        }
        RefreshSettingsStatus();
    }

    private static VisualElement SettingsCard(VisualElement parent, string title)
    {
        var card = new VisualElement();
        card.style.flexShrink = 0;
        card.style.backgroundColor = HeaderSurface;
        card.style.marginBottom = 10;
        Pad(card, 12);
        Frame(card, Border, 8);
        card.Add(SectionHeading(title));
        parent.Add(card);
        return card;
    }

    private static void AddHelp(VisualElement guide, string title, string detail)
    {
        var heading = Text(UiLanguage.Get(title), 13, AccentText);
        heading.style.marginTop = 6;
        guide.Add(heading);
        guide.Add(Text(UiLanguage.Get(detail), 13, Secondary));
    }

    private SettingsField AddConnectionField(VisualElement parent, string label, bool secret = false)
    {
        var row = new VisualElement();
        row.style.marginTop = 4;
        var title = Text(UiLanguage.Get(label), 13, Foreground);
        row.Add(title);
        var input = new GameTextEntry();
        SettingsField? field = null;
        var button = Control("", () => { if (field != null) StartSettingsInput(field); }, ControlBackground);
        button.style.unityTextAlign = TextAnchor.MiddleLeft;
        button.style.whiteSpace = WhiteSpace.NoWrap;
        button.style.textOverflow = TextOverflow.Ellipsis;
        button.style.overflow = Overflow.Hidden;
        Frame(button, Border, 7);
        field = new SettingsField(row, button, title, input, secret);
        input.Bind(button, () => SettingsInputExited(field), StopSettingsInput, () => !SettingsBusy && pending == null);
        row.Add(button);
        parent.Add(row);
        return field;
    }

    private void StartSettingsInput(SettingsField field)
    {
        if (layer == null || ReferenceEquals(activeSettingsField, field) || SettingsBusy || pending != null)
            return;
        StopSettingsInput();
        activeSettingsField = field;
        try { field.Editor.Start(layer, field.Button); }
        catch (Exception)
        {
            activeSettingsField = null;
            SetConnectionStatus("当前无法输入，请重新点击该栏。", error: true);
        }
        RenderConnectionField(field);
    }

    private void StopSettingsInput()
    {
        var field = activeSettingsField;
        if (field == null)
            return;
        activeSettingsField = null;
        field.Editor.Stop();
        RenderConnectionField(field);
    }

    private void SettingsInputExited(SettingsField field)
    {
        if (ReferenceEquals(activeSettingsField, field))
            activeSettingsField = null;
        RenderConnectionField(field);
    }

    private void PollSettingsTyping()
    {
        if (activeSettingsField is not { } field)
            return;
        if (field.Editor.Poll())
            RenderConnectionField(field);
        if (!field.Editor.Active)
            SettingsInputExited(field);
    }

    private static string ConnectionName(ModelMode mode) => UiLanguage.Get(mode switch
    {
        ModelMode.LocalHttp => "本地模型",
        ModelMode.RemoteHttp => "远程 API",
        _ => "本机 Codex"
    });

    private void SelectConnection(ModelMode mode)
    {
        if (mode == chosenMode || SettingsBusy || pending != null)
            return;
        StopSettingsInput();
        connectionDrafts[chosenMode] = ReadConnectionDraft();
        chosenMode = mode;
        LoadConnectionDraft();
        SetConnectionStatus("点击「保存并启用」切换当前连接。");
        RefreshSettingsStatus();
    }

    private ConnectionDraft ReadConnectionDraft() => new(
        endpointField?.Editor.ReadCommittedText() ?? "",
        modelField?.Editor.ReadCommittedText() ?? "",
        keyField?.Editor.ReadCommittedText() ?? "");

    private void LoadConnectionDraft()
    {
        if (!connectionDrafts.TryGetValue(chosenMode, out var draft))
        {
            var saved = ModelConnection.SettingsFor(chosenMode);
            draft = new ConnectionDraft(saved.BaseUrl, saved.Model, "");
        }
        if (endpointField is { } endpoint)
        {
            endpoint.Editor.ReplaceDraft(draft.BaseUrl);
            endpoint.Row.style.display = chosenMode == ModelMode.Codex ? DisplayStyle.None : DisplayStyle.Flex;
            RenderConnectionField(endpoint);
        }
        if (modelField is { } model)
        {
            model.Editor.ReplaceDraft(draft.Model);
            model.Title.text = UiLanguage.Get(chosenMode == ModelMode.Codex ? "模型 ID（选填）" : "模型 ID");
            RenderConnectionField(model);
        }
        if (keyField is { } key)
        {
            key.Editor.ReplaceDraft(draft.NewKey);
            key.Row.style.display = chosenMode == ModelMode.Codex ? DisplayStyle.None : DisplayStyle.Flex;
            key.Title.text = UiLanguage.Get(chosenMode == ModelMode.LocalHttp ? "API 密钥（选填）" : "API 密钥");
            RenderConnectionField(key);
        }
        if (connectionHint != null)
            connectionHint.text = UiLanguage.Get(chosenMode switch
            {
                ModelMode.LocalHttp => "连接本机运行的模型服务，支持 OpenAI 兼容接口。",
                ModelMode.RemoteHttp => "填写服务商提供的 HTTPS API 基础地址。连接测试会发送一条示例请求，可能计入用量。",
                _ => "使用本机当前的 Codex 登录。"
            });
    }

    private void RenderConnectionField(SettingsField field)
    {
        if (RenderSaveHistoryField(field))
            return;
        var editing = field.Editor.Active;
        if (field.Secret)
        {
            var length = field.Editor.Text.Length;
            field.Button.text = length > 0 ? new string('•', Math.Min(length, 24)) + (editing ? "│" : "") :
                editing ? "│" : UiLanguage.Get(ModelConnection.SettingsFor(chosenMode).HasApiKey ? "已保存密钥 · 点击更换" : "点击输入 API 密钥");
        }
        else
        {
            var value = field.Editor.Display;
            field.Button.text = value.Length > 0 ? value : UiLanguage.Get(ReferenceEquals(field, endpointField) ? "点击输入接口地址" :
                chosenMode == ModelMode.Codex ? "留空使用 Codex 默认模型" : "点击输入模型 ID");
        }
        Frame(field.Button, editing ? Purple : Border, 7);
    }

    private void ActivateConnection()
    {
        if (SettingsBusy || pending != null)
            return;
        StopSettingsInput();
        var draft = ReadConnectionDraft();
        try
        {
            ModelConnection.Save(chosenMode, draft.BaseUrl, draft.Model, draft.NewKey);
            connectionDrafts.Remove(chosenMode);
            LoadConnectionDraft();
            SetConnectionStatus(string.Format(UiLanguage.Get("已启用{0}。下一条对话会使用此连接。"), ConnectionName(chosenMode)));
        }
        catch (Exception error) { SetConnectionStatus(SettingsProblem(error), error: true); }
        RefreshSettingsStatus();
    }

    private void StartSettingsTest()
    {
        if (settingsTest != null || pending != null)
            return;
        StopSettingsInput();
        var draft = ReadConnectionDraft();
        var cancellation = new CancellationTokenSource();
        try
        {
            settingsTest = ModelConnection.TestAsync(chosenMode, draft.BaseUrl, draft.Model, draft.NewKey,
                cancellationToken: cancellation.Token);
            settingsTestCancellation = cancellation;
            SetConnectionStatus("正在测试连接…");
        }
        catch (Exception error)
        {
            cancellation.Dispose();
            SetConnectionStatus(SettingsProblem(error), error: true);
        }
        RefreshSettingsStatus();
    }

    private void TickSettings()
    {
        RefreshSaveHistorySettings();
        if (settingsTest is { IsCompleted: true } test)
        {
            settingsTest = null;
            var canceled = settingsTestCancellation?.IsCancellationRequested == true;
            settingsTestCancellation?.Dispose();
            settingsTestCancellation = null;
            try
            {
                test.GetAwaiter().GetResult();
                if (!canceled)
                    SetConnectionStatus("测试通过。点击「保存并启用」应用这组设置。");
            }
            catch (Exception error)
            {
                if (!canceled)
                    SetConnectionStatus(SettingsProblem(error), error: true);
            }
        }
        RefreshSettingsStatus();
    }

    private void ToggleHomeNotifications()
    {
        StopSettingsInput();
        try { ModelConnection.SetHomeNotifications(!ModelConnection.HomeNotifications); }
        catch (Exception) { SetConnectionStatus("无法保存通知设置，请重试。", error: true); }
        RefreshSettingsStatus();
    }

    private void TogglePromiseWaivers()
    {
        StopSettingsInput();
        try
        {
            ModelConnection.SetPromiseWaiversEnabled(!ModelConnection.PromiseWaiversEnabled);
        }
        catch (Exception)
        {
            if (promiseWaiverSettingsStatus != null)
                promiseWaiverSettingsStatus.text = UiLanguage.Get("无法保存放弃承诺设置，请重试。");
        }
        RefreshSettingsStatus();
    }

    private void ToggleBoardFunding()
    {
        StopSettingsInput();
        try { ModelConnection.SetBoardFundingEnabled(!ModelConnection.BoardFundingEnabled); }
        catch (Exception) { SetConnectionStatus("无法保存董事会注资设置，请重试。", error: true); }
        RefreshSettingsStatus();
    }

    private void RefreshSettingsStatus()
    {
        if (settingsHost == null)
            return;
        var active = ModelConnection.Current;
        if (activeConnectionLabel != null)
        {
            var label = $"{UiLanguage.Get("当前连接")} · {ConnectionName(active.Mode)}";
            if (active.Model.Length > 0)
                label += $" · {active.Model}";
            if (active.Mode != chosenMode)
                label += $"    {UiLanguage.Get("待启用")} · {ConnectionName(chosenMode)}";
            if (activeConnectionLabel.text != label)
                activeConnectionLabel.text = label;
        }
        var canChange = CanChangeSettings;
        foreach (var button in languageChoices)
            SetControlEnabled(button, canChange);
        foreach (var (mode, button) in connectionChoices)
        {
            var selected = mode == chosenMode;
            var title = (selected ? "◉ " : "○ ") + ConnectionName(mode);
            if (button.text != title)
            {
                button.text = title;
                button.style.backgroundColor = selected ? SelectedRow : ControlBackground;
                button.style.color = selected ? AccentText : Foreground;
                Frame(button, selected ? Purple : Border, 7);
            }
            SetControlEnabled(button, canChange);
        }
        SetControlEnabled(endpointField?.Button, canChange);
        SetControlEnabled(modelField?.Button, canChange);
        SetControlEnabled(keyField?.Button, canChange);
        SetControlEnabled(activateConnection, canChange);
        SetControlEnabled(testConnection, canChange);
        var refreshText = UiLanguage.Get(ManualRefreshStatus);
        if (refreshStatus != null && refreshStatus.text != refreshText)
            refreshStatus.text = refreshText;
        var notificationText = UiLanguage.Get(ModelConnection.HomeNotifications ? "首页来信提示 · 已开启" : "首页来信提示 · 已关闭");
        if (notifications != null && notifications.text != notificationText)
            notifications.text = notificationText;
        RefreshNativeDataStatus();
        if (promiseWaivers != null)
        {
            var supported = settingsNativeBuildSupported;
            var enabled = ModelConnection.PromiseWaiversEnabled;
            promiseWaivers.text = UiLanguage.Get(!supported ? "放弃原版承诺 · 当前版本暂不可用" :
                enabled ? "放弃原版承诺 · 已开启" : "放弃原版承诺 · 已关闭");
            SetControlEnabled(promiseWaivers, supported && canChange);
        }
        if (boardFunding != null)
        {
            var supported = settingsNativeBuildSupported;
            boardFunding.text = UiLanguage.Get(!supported ? "董事会注资 · 当前版本暂不可用" :
                ModelConnection.BoardFundingEnabled ? "董事会注资 · 已开启" : "董事会注资 · 已关闭");
            SetControlEnabled(boardFunding, supported && canChange);
        }
    }

    private void RefreshNativeDataStatus()
    {
        if (nativeDataStatus == null || Time.unscaledTime < nextNativeSettingsCheck)
            return;

        nextNativeSettingsCheck = Time.unscaledTime + 1f;
        settingsNativeBuildSupported = FmNativePromises.IsSupported;
        var build = settingsNativeBuildSupported ? FmNativeLayout.Current.Build : Application.version;
        var availability = UiLanguage.Get(!settingsNativeBuildSupported
            ? "此构建暂未适配原版数据写入，可以继续使用模组对话。"
            : NativeSaveWriteBlocker() ?? "已核实本地存档。具体操作还需要核对当前人物和游戏记录。");
        var affinity = settingsNativeBuildSupported && FmNativeLayout.Current.RelationsInitializer == null
            ? "\n" + UiLanguage.Get("此构建支持已有亲密度记录。创建空关系表需要已验证的新版构建。")
            : "";
        nativeDataStatus.text = $"{UiLanguage.Get("FM 构建")} · {build}\n{availability}{affinity}";
    }

    private static string? NativeSaveWriteBlocker() =>
        GameSaveTimeline.ActiveLocalSave == null
            ? "原版写入需要本地存档。新开档请先保存，云存档请先另存为本地副本。"
            : GameSaveTimeline.CanChangeGameData ? null
                : "当前存档正在同步或暂未核实，原版写入暂不可用。";

    private static void SetControlEnabled(Button? button, bool enabled)
    {
        if (button != null && button.enabledSelf != enabled)
            button.SetEnabled(enabled);
    }

    private void SetConnectionStatus(string text, bool error = false)
    {
        if (connectionStatus == null)
            return;
        connectionStatus.text = UiLanguage.Get(text);
        connectionStatus.style.color = error ? Caution : Secondary;
    }

    private static string SettingsProblem(Exception error) => error switch
    {
        ArgumentException or InvalidOperationException or TimeoutException => error.Message,
        _ => "设置未完成，请检查连接参数后重试。"
    };

    private void CloseSettings()
    {
        StopSettingsInput();
        CloseSaveHistorySettings();
        settingsTestCancellation?.Cancel();
        keyField?.Editor.Clear();
        connectionDrafts.Clear();
        connectionChoices.Clear();
        languageChoices.Clear();
        endpointField = modelField = keyField = null;
        settingsHost = null;
        activeConnectionLabel = connectionHint = connectionStatus = refreshStatus = null;
        activateConnection = testConnection = notifications = null;
        promiseWaivers = null;
        boardFunding = null;
        promiseWaiverSettingsStatus = null;
        nativeDataStatus = null;
    }
}
