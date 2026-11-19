using System;
using Il2CppInterop.Runtime;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private bool MountTalkLayer(VisualElement document, VisualElement overlay)
    {
        if (!GameUi.AttachPageContent(document, overlay))
        {
            Plugin.Logger.LogWarning("FM Player Talk could not find the native career content slot below Overlay.");
            return false;
        }

        EventCallback<PointerDownEvent> onNavigation = (Action<PointerDownEvent>)HandleNativeNavigation;
        document.RegisterCallback(onNavigation, TrickleDown.TrickleDown);
        overlay.RegisterCallback<DetachFromPanelEvent>((Action<DetachFromPanelEvent>)(_ =>
            document.UnregisterCallback(onNavigation, TrickleDown.TrickleDown)));
        return true;
    }

    private bool RefreshTalkLayer() =>
        host is { } document && layer is { } overlay &&
        GameUi.AttachPageContent(document, overlay);

    private void HandleNativeNavigation(PointerDownEvent pointer)
    {
        if (pointer.button != 0 || pointer.target?.TryCast<VisualElement>() is not { } target)
            return;
        if (pendingSignal is { PromiseRequest: not null } signal && layer is { } pageLayer && !pageLayer.Contains(target))
            signal.PromiseContextChanged = true;
        if (GameUi.IsExternalNavigationTarget(target))
        {
            // Leave the original event and the other page untouched.
            Close();
        }
        else if (GameUi.NavigationTarget(target) == null && layer is { } overlay && !overlay.Contains(target))
            ReleaseExternalFocus();
    }

    private void ReleaseExternalFocus()
    {
        if (!Editing)
            return;
        editor.Stop();
        searchEditor.Stop();
        StopSettingsInput();
        armedClick = null;
        armedPage = null;
        RefreshSearch();
        RefreshControls();
    }
}
