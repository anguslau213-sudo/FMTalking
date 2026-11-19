using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private VisualElement? historyArchiveOverlay;

    private void OpenHistoryArchive()
    {
        if (saveSessionDirectory is not { } session || GameSaveTimeline.DirectoryPath != session || layer is not { panel: not null } owner)
            return;
        StopSettingsInput();
        editor.Stop();
        searchEditor.Stop();
        historyArchiveOverlay?.RemoveFromHierarchy();
        var records = Conversation.ReadStoredHistory(session);
        var overlay = new VisualElement { name = "fm-player-talk-history-archive" };
        historyArchiveOverlay = overlay;
        var controls = new List<string>();
        overlay.style.position = Position.Absolute;
        overlay.style.left = overlay.style.right = overlay.style.top = overlay.style.bottom = 0;
        overlay.style.backgroundColor = ChatSurface;
        Pad(overlay, 12);
        overlay.RegisterCallback<DetachFromPanelEvent>((Action<DetachFromPanelEvent>)(_ =>
        {
            foreach (var key in controls)
                clickable.Remove(key);
            if (ReferenceEquals(historyArchiveOverlay, overlay))
                historyArchiveOverlay = null;
        }));
        var header = Row();
        header.style.justifyContent = Justify.SpaceBetween;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 7;
        header.Add(UiLabel("历史对话（只读）", 18, Foreground));
        header.Add(ArchiveButton("返回设置", () => overlay.RemoveFromHierarchy()));
        overlay.Add(header);
        var notice = UiLabel("这里只保留已保存的对话。人物身份尚待核实；阅读记录不会添加联系人，也不能发送消息或改变游戏。", 13, Secondary);
        notice.style.marginBottom = 10;
        overlay.Add(notice);
        var content = Row();
        content.style.flexGrow = 1;
        content.style.minHeight = 0;
        overlay.Add(content);
        var list = VerticalList();
        list.style.width = Length.Percent(31);
        list.style.minWidth = 160;
        list.style.maxWidth = 300;
        list.style.marginRight = 12;
        list.style.flexGrow = 0;
        var transcriptView = VerticalList();
        transcriptView.style.minWidth = 0;
        content.Add(list);
        content.Add(transcriptView);
        if (records.Count == 0)
            transcriptView.Add(UiLabel("当前存档没有可读取的历史对话。", 14, Secondary));
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var number = index + 1;
            var date = record.LastGameDate?.ToString("yyyy-MM-dd") ?? UiLanguage.Get("未记录日期");
            var preview = record.Preview;
            if (preview.Length > 38)
                preview = preview[..38] + "…";
            var button = ArchiveButton(string.Format(UiLanguage.Get("对话 {0} · {1}"), number, date) + "\n" + preview,
                () => Display(record));
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.unityTextAlign = TextAnchor.MiddleLeft;
            button.style.marginBottom = 7;
            list.Add(button);
        }
        if (records.Count == Conversation.MaximumStoredHistoryEntries)
            list.Add(UiLabel("显示最近的 128 份记录。", 12, Secondary));
        owner.Add(overlay);
        if (records.Count > 0)
            Display(records[0]);

        Button ArchiveButton(string caption, Action action)
        {
            var button = Control(caption, () =>
            {
                if (saveSessionDirectory != session || GameSaveTimeline.DirectoryPath != session)
                    overlay.RemoveFromHierarchy();
                else
                    action();
            }, ControlBackground);
            controls.Add(button.name);
            return button;
        }

        void Display(StoredConversationHistory record)
        {
            transcriptView.Clear();
            foreach (var turn in record.Turns)
            {
                if (turn.GameDate is DateTime date)
                {
                    var divider = Text(date.ToString("yyyy-MM-dd"), 12, Secondary);
                    divider.style.marginTop = 10;
                    transcriptView.Add(divider);
                }
                if (!turn.IsIncoming)
                    AddTurn("我", turn.User, AccentText);
                AddTurn("对方（历史记录）", turn.Reply, Foreground);
            }
            transcriptView.scrollOffset = Vector2.zero;
        }

        void AddTurn(string speaker, string text, Color color)
        {
            var label = Text(UiLanguage.Get(speaker) + ": " + text, 14, color);
            label.style.marginTop = 5;
            label.style.marginBottom = 8;
            label.style.whiteSpace = WhiteSpace.Normal;
            transcriptView.Add(label);
        }
    }
}
