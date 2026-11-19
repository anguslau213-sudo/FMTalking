using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using SI.Bindable;
using UnityEngine;
using UnityEngine.UIElements;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace FmPlayerTalk;

internal enum TalkPage
{
    Home,
    Players,
    Staff,
    Contacts,
    Favorites,
    Settings
}

public static class GameUi
{
    private const string EntryName = "fm-player-talk-entry";
    private const string DocumentName = "MainUIDocument-container";
    private const string NativeOverlayName = "Overlay";
    private const string NativeSecondaryTabs = "SecondaryTabsList";
    private const string NativeSecondaryTabsRow = "SecondaryNavigationTabs";
    private const string NativeTabOverflow = "MoreButton";
    private const string NativeMenuTitle = "MenuTitle";
    private const string NativeMainTabText = "MainTabText";
    private const string NativePortalButton = "PortalButton";
    private const string ChineseFontName = "NotoSansSC-Regular SDF";
    private const float MenuWidth = 208f;
    private const float MenuItemHeight = 30f;
    private const float MenuPadding = 4f;
    private const float MenuHeight = MenuItemHeight * 5 + MenuPadding * 2 + 2f;
    private const float MenuCloseDelay = 0.20f;

    private static VisualElement? _root;
    private static VisualElement? _careerMarker;
    private static TextElement? _mainTabCaption;
    private static TextElement? _portalCaption;
    private static readonly Regex CaptionTags = new("<[^>]+>", RegexOptions.CultureInvariant);
    private static VisualElement? _navigationAnchor;
    private static Button? _entry;
    private static SIText? _entryCaption;
    private static int _entryLanguageRevision = -1;
    private static VisualElement? _menu;
    private static readonly (TalkPage Page, string Caption)[] MenuItems =
    {
        (TalkPage.Players, "球员对话"),
        (TalkPage.Staff, "职员对话"),
        (TalkPage.Contacts, "外部联系"),
        (TalkPage.Favorites, "收藏联系人"),
        (TalkPage.Settings, "设置")
    };
    private static float _menuCloseAt = float.PositiveInfinity;
    private static FontAsset? _chineseFont;
    private static readonly Dictionary<UiLanguageChoice, (FontAsset? Font, int Revision)> TalkFonts = new();
    private static float _nextDiscovery;
    private static float _nextCaptionDiscovery;
    private static float _nextAnchorDiscovery;
    private static float _contentTop;

    internal static void Tick(Action<VisualElement, TalkPage> onOpen)
    {
        // The native career can stay active while its navigation row is hidden.
        if (!HasActiveCareerEntry())
        {
            RemoveEntry();
            if (Time.unscaledTime < _nextDiscovery)
                return;
            _nextDiscovery = Time.unscaledTime + 1.25f;
            FindCareerDocument();
            _nextAnchorDiscovery = 0;
            _contentTop = 0;
        }

        if (_root == null)
            return;

        if (!IsManagerCenter(_root))
        {
            RemoveEntry();
            _nextAnchorDiscovery = 0;
            return;
        }

        if (Time.unscaledTime >= _nextAnchorDiscovery)
        {
            _nextAnchorDiscovery = Time.unscaledTime + 1.25f;
            var anchor = FindVisibleNamed(_root, NativeSecondaryTabs);
            if (anchor?.Pointer != _navigationAnchor?.Pointer)
            {
                RemoveEntry();
                _navigationAnchor = anchor;
            }
        }
        if (_navigationAnchor is not { } current || !_root.Contains(current) || !IsVisible(current) ||
            current.parent is not { name: NativeSecondaryTabsRow } row)
        {
            CloseMenu();
            return;
        }

        if (_entry?.parent?.Pointer == row.Pointer)
        {
            RefreshEntryLanguage(_entry);
            UpdateMenu(_root, _entry);
            return;
        }

        CloseMenu();
        var entry = _entry ?? CreateEntry(_root, onOpen);
        for (int i = row.hierarchy.childCount - 1; i >= 0; i--)
        {
            var child = row.hierarchy.ElementAt(i);
            if (child?.name == EntryName)
                child.RemoveFromHierarchy();
        }

        // Keep the overflow button after all tabs and preserve existing tabs' order.
        var index = 0;
        for (; index < row.hierarchy.childCount; index++)
            if (row.hierarchy.ElementAt(index)?.name == NativeTabOverflow)
                break;
        row.hierarchy.Insert(index, entry);
        if (_entry == null)
            Plugin.Logger.LogInfo("FM Player Talk entry attached to native Manager Center navigation.");
        _entry = entry;
        RefreshEntryLanguage(entry);
    }

    private static void RemoveEntry()
    {
        CloseMenu();
        if (_entry?.parent != null)
            _entry.RemoveFromHierarchy();
        _entry = null;
        _entryCaption = null;
        _entryLanguageRevision = -1;
        _navigationAnchor = null;
    }

    private static void RefreshEntryLanguage(Button entry)
    {
        var revision = UiLanguage.Revision;
        if (_entryLanguageRevision == revision || _entryCaption == null)
            return;

        _entryCaption.text = UiLanguage.Get("交流");
        entry.tooltip = UiLanguage.Get("点击进入交流首页，悬停查看子菜单");
        var font = FindTalkFont();
        _entryCaption.style.unityFontDefinition = font is null
            ? new StyleFontDefinition(StyleKeyword.Null) : new StyleFontDefinition(font);
        if (_menu != null)
        {
            _menu.style.unityFontDefinition = new StyleFontDefinition(font ?? _entryCaption.resolvedStyle.unityFontDefinition);
            foreach (var (page, caption) in MenuItems)
                if (_menu.Q<Button>(MenuItemName(page)) is { } item)
                    item.text = UiLanguage.Get(caption);
        }
        _entryLanguageRevision = revision;
    }

    private static string MenuItemName(TalkPage page) => EntryName + "-" + page.ToString().ToLowerInvariant();

    internal static bool HasEntry(VisualElement host) =>
        host.Pointer == _root?.Pointer && HasActiveCareerEntry() && IsManagerCenter(host);

    internal static bool HasActiveCareerEntry() =>
        _root?.panel != null && _careerMarker?.panel != null && _root.Contains(_careerMarker);

    internal static VisualElement? ActiveCareerDocument => HasActiveCareerEntry() ? _root : null;

    internal static bool IsNativeGameMenuButton(VisualElement? target)
    {
        for (var node = target; node != null; node = node.parent)
        {
            if (node.ClassListContains("dashboard-hud-fm-button"))
                return true;
        }
        return false;
    }

    internal static bool AttachPageContent(VisualElement document, VisualElement content, bool activate = false)
    {
        var nativeOverlay = NativeOverlay(document);
        if (!HasEntry(document) || nativeOverlay == null)
            return false;
        // A newly opened page belongs immediately below FM's modal layer.
        if (content.parent?.Pointer != document.Pointer)
        {
            document.hierarchy.Insert(document.hierarchy.IndexOf(nativeOverlay), content);
            return true;
        }
        var overlayIndex = document.hierarchy.IndexOf(nativeOverlay);
        var contentIndex = document.hierarchy.IndexOf(content);
        if (contentIndex > overlayIndex || (activate && contentIndex != overlayIndex - 1))
            content.PlaceBehind(nativeOverlay);
        return true;
    }

    private static VisualElement? NativeOverlay(VisualElement document)
    {
        for (var index = 0; index < document.hierarchy.childCount; index++)
            if (document.hierarchy.ElementAt(index) is { name: NativeOverlayName } overlay)
                return overlay;
        return null;
    }

    internal static bool IsExternalNavigationTarget(VisualElement? target)
    {
        if (NavigationTarget(target).HasValue || IsNativeGameMenuButton(target))
            return false;
        for (var node = target; node != null; node = node.parent)
        {
            if (node.name is NativeSecondaryTabs or "PrimaryNavigationBar" or NativePortalButton)
                return true;
        }
        return false;
    }

    internal static string? ReadHeaderDateText()
    {
        var header = FindNamed(_root, "CurrentDayWidget", 18);
        if (header == null || !IsVisible(header))
            return null;
        var dateRow = FindNamed(header, "BottomRow", 6);
        if (FindNamed(dateRow, "WeekDay", 2) == null || FindNamed(FindNamed(header, "TopRow", 6), "Time", 2) == null)
            return null;
        var date = FindNamed(dateRow, "Month", 2);
        return date != null && IsVisible(date) ? date.TryCast<TextElement>()?.text : null;
    }

    internal static float GetContentTop(VisualElement host)
    {
        if (host.Pointer != _root?.Pointer)
            return 0;
        if ((_navigationAnchor ?? _careerMarker) is { } edge && IsVisible(edge))
            _contentTop = Mathf.Max(0, host.WorldToLocal(edge.worldBound).yMax);
        return _contentTop;
    }

    internal static TalkPage? NavigationTarget(VisualElement? target)
    {
        for (var node = target; node != null; node = node.parent)
        {
            switch (node.name)
            {
                case EntryName: return TalkPage.Home;
                case EntryName + "-players": return TalkPage.Players;
                case EntryName + "-staff": return TalkPage.Staff;
                case EntryName + "-contacts": return TalkPage.Contacts;
                case EntryName + "-favorites": return TalkPage.Favorites;
                case EntryName + "-settings": return TalkPage.Settings;
            }
        }
        return null;
    }

    private static void UpdateMenu(VisualElement root, Button entry)
    {
        if (_menu == null)
            return;
        if (_menu.parent == null || !IsVisible(entry) || Time.unscaledTime >= _menuCloseAt)
            CloseMenu();
        else
            PositionMenu(root, entry, _menu);
    }

    internal static void CloseMenu()
    {
        if (_menu?.parent != null)
            _menu.RemoveFromHierarchy();
        _menu = null;
        _menuCloseAt = float.PositiveInfinity;
    }

    private static void AllowMenuHover() => _menuCloseAt = float.PositiveInfinity;

    private static void ScheduleMenuClose() => _menuCloseAt = Time.unscaledTime + MenuCloseDelay;

    private static void PositionMenu(VisualElement root, Button entry, VisualElement menu)
    {
        var row = root.WorldToLocal(entry.worldBound);
        var viewport = root.contentRect;
        var width = Mathf.Min(MenuWidth, viewport.width);
        var height = Mathf.Min(MenuHeight, viewport.height);
        var top = row.yMax + height <= viewport.yMax ? row.yMax : row.yMin - height;
        var style = menu.style;
        style.width = width;
        style.height = height;
        style.left = Mathf.Clamp(row.xMin, viewport.xMin, viewport.xMax - width);
        style.top = Mathf.Clamp(top, viewport.yMin, viewport.yMax - height);
    }

    private static void ShowMenu(VisualElement root, Button entry, Color foreground,
        float fontSize, FontDefinition? font, Action<VisualElement, TalkPage> onOpen)
    {
        if (!HasEntry(root))
            return;
        AllowMenuHover();
        if (_menu != null)
            return;

        var menu = new VisualElement { name = EntryName + "-menu" };
        var style = menu.style;
        style.position = Position.Absolute;
        style.paddingTop = MenuPadding;
        style.paddingBottom = MenuPadding;
        style.paddingLeft = MenuPadding;
        style.paddingRight = MenuPadding;
        style.backgroundColor = new Color(0.10f, 0.088f, 0.12f);
        var border = new Color(foreground.r, foreground.g, foreground.b, 0.23f);
        style.borderTopWidth = style.borderRightWidth = style.borderBottomWidth = style.borderLeftWidth = 1f;
        style.borderTopColor = style.borderRightColor = style.borderBottomColor = style.borderLeftColor = border;
        style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = 4f;
        if (font is not null)
            style.unityFontDefinition = new StyleFontDefinition(font);

        foreach (var (page, caption) in MenuItems)
            AddItem(caption, page);

        menu.RegisterCallback<PointerEnterEvent>((Action<PointerEnterEvent>)(_ => AllowMenuHover()));
        menu.RegisterCallback<PointerLeaveEvent>((Action<PointerLeaveEvent>)(_ => ScheduleMenuClose()));
        PositionMenu(root, entry, menu);
        if (AttachPageContent(root, menu))
            _menu = menu;

        void AddItem(string title, TalkPage page)
        {
            var button = new Button { text = UiLanguage.Get(title), name = MenuItemName(page) };
            var item = button.style;
            item.height = MenuItemHeight;
            item.minHeight = 0;
            item.marginTop = item.marginBottom = item.marginLeft = item.marginRight = 0f;
            item.paddingLeft = 8f;
            item.paddingRight = 8f;
            item.borderTopWidth = item.borderRightWidth = item.borderBottomWidth = item.borderLeftWidth = 0f;
            item.backgroundColor = Color.clear;
            item.color = foreground;
            item.fontSize = fontSize;
            item.unityTextAlign = TextAnchor.MiddleLeft;
            item.whiteSpace = WhiteSpace.NoWrap;
            item.overflow = Overflow.Hidden;
            item.textOverflow = TextOverflow.Ellipsis;
            button.add_clicked((Action)(() =>
            {
                CloseMenu();
                onOpen(root, page);
            }));
            button.RegisterCallback<PointerEnterEvent>((Action<PointerEnterEvent>)(_ =>
                button.style.backgroundColor = new Color(foreground.r, foreground.g, foreground.b, 0.13f)));
            button.RegisterCallback<PointerLeaveEvent>((Action<PointerLeaveEvent>)(_ =>
                button.style.backgroundColor = Color.clear));
            menu.Add(button);
        }
    }

    public static FontDefinition? FindChineseFont()
    {
        if (_chineseFont != null)
            return FontDefinition.FromSDFFont(_chineseFont);

        var assets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<FontAsset>());
        if (assets == null)
            return null;
        FontAsset? fallback = null;
        for (int i = 0; i < assets.Length; i++)
        {
            var candidate = assets[i]?.TryCast<FontAsset>();
            if (candidate == null)
                continue;

            if (string.Equals(candidate.name, ChineseFontName, StringComparison.Ordinal))
            {
                _chineseFont = candidate;
                break;
            }

            if (fallback != null ||
                !candidate.HasCharacter('交', true, false) ||
                !candidate.HasCharacter('流', true, false) ||
                !candidate.HasCharacter('请', true, false) ||
                !candidate.HasCharacter('球', true, false) ||
                !candidate.HasCharacter('员', true, false))
                continue;

            fallback = candidate;
        }

        var selected = _chineseFont ?? fallback;
        return selected == null ? null : FontDefinition.FromSDFFont(selected);
    }

    internal static FontDefinition? FindTalkFont() => FindTalkFont(UiLanguage.Effective);

    internal static FontDefinition? FindTalkFont(UiLanguageChoice language)
    {
        if (language == UiLanguageChoice.Auto)
            language = UiLanguage.Effective;
        if (language is not (UiLanguageChoice.Japanese or UiLanguageChoice.Korean))
            return FindChineseFont();

        var revision = UiLanguage.Revision;
        if (!TalkFonts.TryGetValue(language, out var cached) || (cached.Font == null && cached.Revision != revision))
        {
            var japanese = language == UiLanguageChoice.Japanese;
            FontAsset? matched = null;
            try
            {
                matched = FindScriptFont(japanese ? "NotoSansJP" : "NotoSansKR",
                    japanese ? "会話設定ひらがなカナー" : "대화선수스태프설정읽지않은메시지",
                    japanese ? new[] { "Yu Gothic", "MS Gothic" } : new[] { "Malgun Gothic", "Gulim" });
            }
            catch (Exception error)
            {
                Plugin.Logger.LogDebug($"FMTalking could not inspect font assets for {language}: {error.GetType().Name}.");
            }
            cached = (matched, revision);
            TalkFonts[language] = cached;
        }
        return cached.Font == null ? FindChineseFont() : FontDefinition.FromSDFFont(cached.Font);
    }

    private static FontAsset? FindScriptFont(string preferredName, string sample, string[] systemFamilies)
    {
        FontAsset? fallback = null;
        var assets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<FontAsset>());
        if (assets != null)
        {
            for (var i = 0; i < assets.Length; i++)
            {
                var asset = assets[i]?.TryCast<FontAsset>();
                if (asset == null || !HasScriptGlyphs(asset, sample, addMissing: false))
                    continue;
                if (asset.name.StartsWith(preferredName, StringComparison.OrdinalIgnoreCase))
                    return asset;
                fallback ??= asset;
            }
        }
        if (fallback != null || !OperatingSystem.IsWindows())
            return fallback;

        // Borrow an installed typeface without changing the game's font assets.
        foreach (var family in systemFamilies)
        {
            try
            {
                var font = FontAsset.CreateFontAsset(family, "Regular", 48);
                if (font != null && HasScriptGlyphs(font, sample, addMissing: true))
                    return font;
            }
            catch (Exception error)
            {
                Plugin.Logger.LogDebug($"FMTalking could not use the installed font {family}: {error.GetType().Name}.");
            }
        }
        return null;
    }

    private static bool HasScriptGlyphs(FontAsset font, string sample, bool addMissing)
    {
        foreach (var glyph in sample)
            if (!font.HasCharacter(glyph, searchFallbacks: false, tryAddCharacter: addMissing))
                return false;
        return true;
    }

    private static void FindCareerDocument()
    {
        _root = null;
        _careerMarker = null;
        _mainTabCaption = null;
        _portalCaption = null;
        _nextCaptionDiscovery = 0;
        var panels = UIElementsRuntimeUtility.GetSortedPlayerPanels();
        if (panels == null)
            return;
        for (int i = panels.Count - 1; i >= 0; i--)
        {
            var tree = panels[i]?.visualTree;
            if (tree == null)
                continue;

            var root = FindNamed(tree, DocumentName, 5);
            if (root == null)
                continue;

            var day = FindVisibleNamed(root, "CurrentDayWidget");
            var marker = day ?? FindVisibleNamed(root, NativeSecondaryTabs);
            if (marker == null || FindVisibleNamed(root, NativePortalButton) == null)
                continue;

            _root = root;
            _careerMarker = marker;
            return;
        }
    }

    private static bool IsManagerCenter(VisualElement root)
    {
        if (!VisibleInDocument(root, _mainTabCaption) || !VisibleInDocument(root, _portalCaption))
        {
            if (Time.unscaledTime < _nextCaptionDiscovery)
                return false;
            _nextCaptionDiscovery = Time.unscaledTime + 0.35f;
            var title = FindVisibleNamed(root, NativeMenuTitle);
            _mainTabCaption = title == null ? null : FindVisibleNamed(title, NativeMainTabText)?.TryCast<TextElement>();
            var portal = title == null ? null : FindVisibleNamed(title, NativePortalButton);
            _portalCaption = portal == null ? null : FindVisibleNamed(portal, "text")?.TryCast<TextElement>() ?? FindCaption(portal, 4);
            if (!VisibleInDocument(root, _mainTabCaption) || !VisibleInDocument(root, _portalCaption))
                return false;
        }

        // The breadcrumb and the Manager Center button carry the same caption in the active locale.
        var current = NormalizedCaption(_mainTabCaption?.text);
        return current.Length > 0 && string.Equals(current, NormalizedCaption(_portalCaption?.text),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool VisibleInDocument(VisualElement root, VisualElement? element)
    {
        if (element == null || !IsVisible(element))
            return false;
        for (var node = element; node != null; node = node.parent)
        {
            var style = node.resolvedStyle;
            if (!node.visible || style.display == DisplayStyle.None || style.opacity <= 0.01f)
                return false;
            if (node.Pointer == root.Pointer)
                return true;
        }
        return false;
    }

    private static string NormalizedCaption(string? text) =>
        CaptionTags.Replace(FmDisplayText.Read(text) ?? "", "").Trim();

    private static VisualElement? FindVisibleNamed(VisualElement root, string name)
    {
        var first = root.Q<VisualElement>(name);
        if (first == null)
            return null;
        if (IsVisible(first))
            return first;
        var remaining = 2200;
        return FindVisibleNamed(root, name, 28, ref remaining);
    }

    private static VisualElement? FindVisibleNamed(VisualElement root, string name, int depth, ref int remaining)
    {
        if (depth < 0 || remaining-- <= 0 || root.resolvedStyle.display == DisplayStyle.None || !root.visible)
            return null;
        if (root.name == name && IsVisible(root))
            return root;
        for (var index = 0; index < root.hierarchy.childCount && remaining > 0; index++)
            if (root.hierarchy.ElementAt(index) is { } child && FindVisibleNamed(child, name, depth - 1, ref remaining) is { } match)
                return match;
        return null;
    }

    internal static VisualElement? FindNamed(VisualElement? root, string name, int depth)
    {
        if (root == null)
            return null;
        if (root.name == name)
            return root;

        if (depth == 0)
            return null;

        for (int i = 0; i < root.hierarchy.childCount; i++)
        {
            var found = FindNamed(root.hierarchy.ElementAt(i), name, depth - 1);
            if (found != null)
                return found;
        }

        return null;
    }

    private static bool IsVisible(VisualElement element)
    {
        var bounds = element.worldBound;
        return element.panel != null && element.visible &&
               element.resolvedStyle.display != DisplayStyle.None &&
               bounds.width > 1 && bounds.height > 1;
    }

    private static TextElement? FindCaption(VisualElement? element, int depth)
    {
        if (element == null)
            return null;
        var text = element.TryCast<TextElement>();
        if (text != null && !string.IsNullOrWhiteSpace(text.text))
            return text;

        if (depth == 0)
            return null;

        for (int i = 0; i < element.hierarchy.childCount; i++)
        {
            var found = FindCaption(element.hierarchy.ElementAt(i), depth - 1);
            if (found != null)
                return found;
        }

        return null;
    }

    private static Button CreateEntry(VisualElement root, Action<VisualElement, TalkPage> onOpen)
    {
        var entry = new Button { name = EntryName };
        entry.add_clicked((Action)(() =>
        {
            CloseMenu();
            if (HasEntry(root))
                onOpen(root, TalkPage.Home);
        }));

        // Use the classes from FM's secondary navigation dropdown template.
        entry.RemoveFromClassList(Button.ussClassName);
        entry.AddToClassList("secondary-navigation-tab-dropdown");
        entry.AddToClassList("secondary-navigation-padding-box");
        entry.AddToClassList("navigation-secondary-text-portal-default-active-false");
        var style = entry.style;
        style.backgroundColor = Color.clear;
        style.alignSelf = Align.Stretch;
        style.alignItems = Align.Center;
        style.flexDirection = FlexDirection.Row;
        style.justifyContent = Justify.Center;
        style.flexShrink = 0;

        var label = new SIText { pickingMode = PickingMode.Ignore };
        _entryCaption = label;
        label.AddToClassList("body-md");
        label.AddToClassList("secondary-navigation-tab-text");
        label.AddToClassList("padding-top-global-padding-extra-small");
        label.style.whiteSpace = WhiteSpace.NoWrap;
        entry.Add(label);

        var chevron = new VisualElement { pickingMode = PickingMode.Ignore };
        chevron.AddToClassList("ui-concepts-chevron-down_small");
        chevron.AddToClassList("si-dropdown__arrow");
        chevron.AddToClassList("margin-top-global-gap-extra-small");
        entry.Add(chevron);

        entry.RegisterCallback<PointerEnterEvent>((Action<PointerEnterEvent>)(_ =>
        {
            var source = label.resolvedStyle;
            var foreground = source.color;
            entry.style.backgroundColor = new Color(foreground.r, foreground.g, foreground.b, 0.10f);
            ShowMenu(root, entry, foreground, source.fontSize > 0 ? source.fontSize : 14f,
                FindTalkFont() ?? source.unityFontDefinition, onOpen);
        }));
        entry.RegisterCallback<PointerLeaveEvent>((Action<PointerLeaveEvent>)(_ =>
        {
            entry.style.backgroundColor = Color.clear;
            ScheduleMenuClose();
        }));
        return entry;
    }
}
