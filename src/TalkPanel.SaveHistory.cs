using System;
using System.IO;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private SettingsField? saveHistoryPath;
    private VisualElement? saveHistorySources;
    private Label? saveHistoryStatus;
    private Label? saveHistoryCurrent;
    private Button? saveHistoryLookup;
    private Button? saveHistoryPreview;
    private Button? saveHistoryConfirm;
    private int saveHistoryGeneration = -1;

    private void BuildSaveHistorySettings(VisualElement body)
    {
        var card = SettingsCard(body, "存档历史重新关联");
        card.Add(UiLabel("适用于同一份存档搬家、重命名或换电脑后的历史恢复。换电脑时也需要带上模组的本地记录目录。只有存档内容完全相同才能关联；当前已有聊天或历史时不会覆盖、合并。", 13, Secondary));
        saveHistoryCurrent = Text("", 13, AccentText);
        saveHistoryCurrent.style.whiteSpace = WhiteSpace.Normal;
        saveHistoryCurrent.style.marginTop = 6;
        card.Add(saveHistoryCurrent);
        saveHistoryLookup = Control("查找同一存档的历史", StartSaveHistoryLookup, ControlBackground);
        saveHistoryLookup.style.alignSelf = Align.FlexStart;
        saveHistoryLookup.style.marginTop = 6;
        card.Add(saveHistoryLookup);
        saveHistoryStatus = Text("", 13, Secondary);
        saveHistoryStatus.style.whiteSpace = WhiteSpace.Normal;
        saveHistoryStatus.style.marginTop = 5;
        card.Add(saveHistoryStatus);
        saveHistorySources = new VisualElement();
        card.Add(saveHistorySources);
        saveHistoryPath = AddConnectionField(card, "旧版历史：原存档完整路径（原文件可以已搬走）");
        RenderConnectionField(saveHistoryPath);
        saveHistoryPreview = Control("核对这条原路径", () =>
        {
            StopSettingsInput();
            StartSaveHistoryPreview(saveHistoryPath?.Editor.ReadCommittedText() ?? "");
        }, ControlBackground);
        saveHistoryPreview.style.alignSelf = Align.FlexStart;
        saveHistoryPreview.style.marginTop = 5;
        card.Add(saveHistoryPreview);
        saveHistoryConfirm = Control("确认关联这份历史", ConfirmSaveHistory, Purple);
        saveHistoryConfirm.style.alignSelf = Align.FlexStart;
        saveHistoryConfirm.style.marginTop = 7;
        card.Add(saveHistoryConfirm);
        saveHistoryGeneration = -1;
        RefreshSaveHistorySettings();
    }

    private bool RenderSaveHistoryField(SettingsField field)
    {
        if (!ReferenceEquals(field, saveHistoryPath)) return false;
        field.Button.text = field.Editor.Display.Length > 0 ? field.Editor.Display : UiLanguage.Get("点击输入原存档的完整 .fm 路径");
        Frame(field.Button, field.Editor.Active ? Purple : Border, 7);
        return true;
    }

    private void StartSaveHistoryLookup()
    {
        StopSettingsInput();
        try
        {
            if (pending != null || roster is not { ManagerId: > 0, ClubId: > 0 } team)
                throw new InvalidOperationException("请等待当前生涯和对话完成后再查找。");
            GameSaveTimeline.StartHistoryLookup(team.ManagerId, team.ClubId);
        }
        catch (Exception error) { GameSaveTimeline.ReportHistoryProblem(error); }
        RefreshSaveHistorySettings();
    }

    private void StartSaveHistoryPreview(string path)
    {
        StopSettingsInput();
        try { GameSaveTimeline.PreviewHistorySource(path); }
        catch (Exception error) { GameSaveTimeline.ReportHistoryProblem(error); }
        RefreshSaveHistorySettings();
    }

    private void ConfirmSaveHistory()
    {
        StopSettingsInput();
        var document = host;
        try
        {
            if (GameSaveTimeline.ConfirmHistorySource() && document != null)
                Open(document, TalkPage.Settings);
        }
        catch (Exception error) { GameSaveTimeline.ReportHistoryProblem(error); }
        RefreshSaveHistorySettings();
    }

    private void RefreshSaveHistorySettings()
    {
        if (saveHistoryStatus == null || saveHistorySources == null) return;
        saveHistoryStatus.text = GameSaveTimeline.HistoryMigrationStatus;
        if (saveHistoryCurrent != null)
            saveHistoryCurrent.text = GameSaveTimeline.ActiveLocalSave is { } save
                ? string.Format(UiLanguage.Get("当前已载入的本地存档：{0}"), save.Path)
                : UiLanguage.Get("当前没有已核实的本地存档。请先保存或载入本地副本。");
        var ready = pending == null && settingsTest == null && !ModelConnection.IsBusy &&
            !GameSaveTimeline.HistoryMigrationBusy && GameSaveTimeline.CanChangeGameData;
        SetControlEnabled(saveHistoryLookup, ready);
        SetControlEnabled(saveHistoryPath?.Button, ready);
        SetControlEnabled(saveHistoryPreview, ready && GameSaveTimeline.HasHistoryCatalog);
        if (saveHistoryConfirm != null)
        {
            saveHistoryConfirm.style.display = GameSaveTimeline.HistoryPreviewSource == null ? DisplayStyle.None : DisplayStyle.Flex;
            SetControlEnabled(saveHistoryConfirm, ready);
        }
        if (saveHistoryGeneration == GameSaveTimeline.HistoryMigrationGeneration) return;
        saveHistoryGeneration = GameSaveTimeline.HistoryMigrationGeneration;
        saveHistorySources.Clear();
        foreach (var source in GameSaveTimeline.HistorySources)
        {
            var path = Text(source.SourcePath, 12, Secondary);
            path.style.whiteSpace = WhiteSpace.Normal;
            path.style.marginTop = 5;
            saveHistorySources.Add(path);
            var choose = Control(string.Format(UiLanguage.Get("预览「{0}」的历史（{1} 份记录）"),
                    Path.GetFileNameWithoutExtension(source.SourcePath), source.RecordFiles),
                () => StartSaveHistoryPreview(source.SourcePath), ControlBackground);
            choose.style.alignSelf = Align.FlexStart;
            SetControlEnabled(choose, ready);
            saveHistorySources.Add(choose);
        }
        if (GameSaveTimeline.HistoryPreviewSource is { } selected)
        {
            var label = Text(string.Format(UiLanguage.Get("待关联来源：{0}"), selected), 13, AccentText);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 6;
            saveHistorySources.Add(label);
        }
    }

    private void CloseSaveHistorySettings()
    {
        saveHistoryPath?.Editor.Clear();
        saveHistoryPath = null;
        saveHistorySources = null;
        saveHistoryStatus = saveHistoryCurrent = null;
        saveHistoryLookup = saveHistoryPreview = saveHistoryConfirm = null;
        saveHistoryGeneration = -1;
    }
}
