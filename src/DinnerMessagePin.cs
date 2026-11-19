using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal static class DinnerMessagePin
{
    private const string AnchorName = "HasMessagesSwitch";
    private const string PinName = "fm-player-talk-dinner-message-pin";
    private const float DiscoveryInterval = 0.8f;
    private const int DiscoveryDepth = 90;

    private static readonly Dictionary<IntPtr, MessagePin> pins = new();
    private static IntPtr documentPointer;
    private static Action<VisualElement>? openConversation;
    private static float nextDiscovery;
    private static int lastReportedSlots = -1;
    private static int languageRevision = -1;

    private sealed record MessagePin(Button Button, Label Caption);

    internal static void Tick(string? sender, Action<VisualElement>? onOpen)
    {
        openConversation = onOpen;
        if (string.IsNullOrWhiteSpace(sender) || onOpen is null)
        {
            Detach();
            return;
        }

        if (GameUi.ActiveCareerDocument is not { } document)
        {
            Detach();
            return;
        }

        if (document.Pointer != documentPointer || UiLanguage.Revision != languageRevision)
        {
            Detach();
            documentPointer = document.Pointer;
            languageRevision = UiLanguage.Revision;
        }
        var headline = UiLanguage.Get("{0}发来一条新消息").Replace("{0}", sender, StringComparison.Ordinal);
        foreach (var pin in pins.Values)
            if (pin.Caption.text != headline)
                pin.Caption.text = headline;
        if (Time.unscaledTime < nextDiscovery)
            return;
        nextDiscovery = Time.unscaledTime + DiscoveryInterval;

        var containers = new Dictionary<IntPtr, VisualElement>();
        CollectMessageContainers(document, DiscoveryDepth, containers);
        foreach (var (key, container) in containers)
        {
            if (!pins.TryGetValue(key, out var pin))
            {
                pin = BuildPin(document, container);
                pin.Caption.text = headline;
                pins.Add(key, pin);
            }
            if (container.hierarchy.IndexOf(pin.Button) == 0)
                continue;
            if (pin.Button.parent != null)
                pin.Button.RemoveFromHierarchy();
            // Insert alongside the switch so its streamed rows remain under native control.
            container.hierarchy.Insert(0, pin.Button);
        }
        foreach (var (key, pin) in pins)
        {
            if (containers.ContainsKey(key))
                continue;
            if (pin.Button.parent != null)
                pin.Button.RemoveFromHierarchy();
            pins.Remove(key);
        }
        if (lastReportedSlots != containers.Count)
        {
            lastReportedSlots = containers.Count;
            Plugin.Logger.LogInfo(containers.Count == 0
                ? "FM Player Talk inbox pin: no message slots in the active career document yet."
                : $"FM Player Talk inbox pin: attached to {containers.Count} message slot(s).");
        }
    }

    private static void Detach()
    {
        foreach (var pin in pins.Values)
        {
            if (pin.Button.parent != null)
                pin.Button.RemoveFromHierarchy();
        }
        pins.Clear();
        documentPointer = IntPtr.Zero;
        nextDiscovery = 0;
        lastReportedSlots = -1;
    }

    private static void CollectMessageContainers(VisualElement? node, int depth, Dictionary<IntPtr, VisualElement> containers)
    {
        if (node is null || node.name is PinName or "fm-player-talk-panel")
            return;
        // This switch is the portal inbox template's list/empty-state host, including the small and international tiles.
        if (node.name == AnchorName && node.parent is { } container)
        {
            containers.TryAdd(container.Pointer, container);
            return;
        }
        if (depth == 0)
            return;

        for (var index = 0; index < node.hierarchy.childCount; index++)
            CollectMessageContainers(node.hierarchy.ElementAt(index), depth - 1, containers);
    }

    private static MessagePin BuildPin(VisualElement document, VisualElement container)
    {
        var peer = GameUi.FindNamed(container, "MessageHeadline", 8)?.resolvedStyle;
        var foreground = peer?.color ?? new Color(0.92f, 0.92f, 0.94f);
        var font = GameUi.FindTalkFont() ?? peer?.unityFontDefinition;
        var fontSize = peer?.fontSize ?? 0f;
        var button = new Button { name = PinName, tooltip = UiLanguage.Get("查看交流消息") };
        button.add_clicked((Action)(() => openConversation?.Invoke(document)));

        var style = button.style;
        style.height = 59f;
        style.flexShrink = 0;
        style.flexDirection = FlexDirection.Column;
        style.justifyContent = Justify.Center;
        style.alignItems = Align.Stretch;
        style.marginTop = style.marginBottom = style.marginLeft = style.marginRight = 0f;
        style.paddingTop = style.paddingBottom = 4f;
        style.paddingLeft = style.paddingRight = 12f;
        style.borderTopWidth = style.borderLeftWidth = style.borderRightWidth = 0f;
        style.borderBottomWidth = 1f;
        style.borderBottomColor = new Color(foreground.r, foreground.g, foreground.b, 0.18f);
        style.backgroundColor = new Color(foreground.r, foreground.g, foreground.b, 0.09f);
        var section = CreateLabel(UiLanguage.Get("交流 · 新回复"), 12f,
            new Color(foreground.r, foreground.g, foreground.b, 0.75f), font);
        var caption = CreateLabel("", fontSize > 0 ? fontSize : 15f, foreground, font);
        button.Add(section);
        button.Add(caption);
        return new MessagePin(button, caption);
    }

    private static Label CreateLabel(string text, float size, Color color, FontDefinition? font)
    {
        var label = new Label(text) { pickingMode = PickingMode.Ignore, enableRichText = false };
        var style = label.style;
        style.marginTop = style.marginBottom = style.marginLeft = style.marginRight = 0f;
        style.fontSize = size;
        style.color = color;
        style.unityTextAlign = TextAnchor.MiddleLeft;
        style.whiteSpace = WhiteSpace.NoWrap;
        style.overflow = Overflow.Hidden;
        style.textOverflow = TextOverflow.Ellipsis;
        if (font is not null)
            style.unityFontDefinition = new StyleFontDefinition(font);
        return label;
    }
}
