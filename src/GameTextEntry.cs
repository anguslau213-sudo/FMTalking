using System;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed class GameTextEntry
{
    private readonly StringBuilder incoming = new();
    private readonly GameInputBlock inputBlock = new();
    private readonly DraftBuffer draft = new();
    private Keyboard? keyboard;
    private VisualElement? activeField;
    private IMECompositionMode initialImeMode;
    private Vector2 initialImePosition;
    private bool restoreIme;
    private string composition = "";
    private string lastDisplay = "";
    private bool composingLastFrame;
    private bool sawTextEvent;
    private bool sawComposition;
    private Action? submit;
    private Func<bool>? canEdit;
    private (int Frame, bool NewLine, bool Consumed)? pendingEnter;
    private int lastResolvedEnterFrame = -1;
    private bool enterLatched;

    public bool Active => keyboard is not null;
    public bool Composing => composition.Length != 0;
    public string Text => draft.Text;
    public string Display => Active ? draft.Display(composition) : Text;
    public int DisplayCaretIndex => Active ? draft.DisplayCaretIndex(composition) : -1;

    private bool CanEdit => canEdit?.Invoke() != false;

    public void Bind(VisualElement field, Action onExit, Action onSubmit, Func<bool>? editingAllowed = null)
    {
        submit = onSubmit;
        canEdit = editingAllowed;
        field.RegisterCallback<FocusEvent>((Action<FocusEvent>)(_ =>
        {
            if (Owns(field))
                EnableIme(field);
        }));
        field.RegisterCallback<BlurEvent>((Action<BlurEvent>)(_ =>
        {
            if (!Owns(field))
                return;
            Stop(blur: false);
            onExit();
        }));
        field.RegisterCallback<IMEEvent>((Action<IMEEvent>)(evt =>
        {
            if (!Owns(field))
                return;
            UpdateComposition(evt.compositionString);
            evt.StopImmediatePropagation();
        }));
        field.RegisterCallback<KeyDownEvent>((Action<KeyDownEvent>)(evt =>
        {
            if (!Owns(field))
                return;
            var character = evt.character;
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                if (!evt.actionKey && !evt.altKey)
                    QueueEnter(evt.shiftKey);
            }
            else if (!char.IsControl(character) && (!evt.actionKey || evt.altKey))
                OnText(character);
            evt.StopImmediatePropagation();
        }), TrickleDown.TrickleDown);
        field.RegisterCallback<NavigationSubmitEvent>((Action<NavigationSubmitEvent>)(evt =>
        {
            if (Owns(field))
                evt.StopImmediatePropagation();
        }), TrickleDown.TrickleDown);
    }

    public void Start(VisualElement owner, VisualElement field)
    {
        if (Active)
            return;

        var device = Keyboard.current ?? throw new InvalidOperationException("FM 未检测到可用键盘。");
        inputBlock.Acquire(owner);
        keyboard = device;
        activeField = field;
        try
        {
            draft.End();
            enterLatched = IsEnterDown(device);
            sawTextEvent = false;
            sawComposition = false;
            initialImeMode = GUIUtility.imeCompositionMode;
            initialImePosition = GUIUtility.compositionCursorPos;
            restoreIme = true;
            field.Focus();
            EnableIme(field);
            Plugin.Logger.LogInfo("FM Player Talk editing started with UI Toolkit IME.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private bool Owns(VisualElement field) => Active && ReferenceEquals(activeField, field);

    private static void EnableIme(VisualElement field)
    {
        // UI Toolkit's keyboard editor uses this Windows composition path on focus.
        GUIUtility.imeCompositionMode = IMECompositionMode.On;
        SetImeAnchor(field);
    }

    public void UpdateImeAnchor(VisualElement viewport)
    {
        if (Active)
            SetImeAnchor(viewport);
    }

    private static void SetImeAnchor(VisualElement field)
    {
        var tree = field.panel?.visualTree;
        if (tree == null || tree.layout.height <= 0)
            return;
        var scale = Screen.height / tree.layout.height;
        var bounds = field.worldBound;
        GUIUtility.compositionCursorPos = new Vector2(bounds.xMin * scale, bounds.yMax * scale);
    }

    public bool Poll()
    {
        var device = keyboard;
        if (device == null)
            return false;
        if (!Application.isFocused || Keyboard.current?.Pointer != device.Pointer)
        {
            Stop();
            return true;
        }

        UpdateComposition(GUIUtility.compositionString);
        FlushText();

        var imeOwnsKeys = Composing || composingLastFrame;
        if (!imeOwnsKeys && device.escapeKey.wasPressedThisFrame)
        {
            Stop();
            return true;
        }

        PollEnter(device, imeOwnsKeys);
        if (Active && !imeOwnsKeys && CanEdit)
            ApplyEditingKeys(device);
        composingLastFrame = Composing;
        var display = Display;
        if (display == lastDisplay)
            return false;
        lastDisplay = display;
        return true;
    }

    public void Stop(bool blur = true)
    {
        var field = activeField;
        activeField = null;
        keyboard = null;
        try
        {
            FlushText();
            if (restoreIme)
                Release(() =>
                {
                    GUIUtility.imeCompositionMode = initialImeMode;
                    GUIUtility.compositionCursorPos = initialImePosition;
                });
            if (blur && field?.panel != null && field.focusController?.focusedElement?.Pointer == field.Pointer)
                Release(field.Blur);
        }
        finally
        {
            restoreIme = false;
            composition = "";
            lastDisplay = "";
            composingLastFrame = false;
            pendingEnter = null;
            enterLatched = false;
            inputBlock.Release();
        }
    }

    public void Clear() => draft.Clear();

    public string ReadCommittedText()
    {
        FlushText();
        return draft.Text;
    }

    public void ReplaceDraft(string value)
    {
        draft.Clear();
        draft.Insert(value);
    }

    private void OnText(char value)
    {
        if (Active && CanEdit && !char.IsControl(value))
        {
            if (!sawTextEvent)
            {
                sawTextEvent = true;
                Plugin.Logger.LogInfo("FM Player Talk received a keyboard text event.");
            }
            incoming.Append(value);
        }
    }

    private void UpdateComposition(string? value)
    {
        if (Active)
        {
            composition = value ?? "";
            if (composition.Length > 0)
            {
                if (!sawComposition)
                {
                    sawComposition = true;
                    Plugin.Logger.LogInfo("FM Player Talk received an IME composition event.");
                }
                composingLastFrame = true;
            }
        }
    }

    private void FlushText()
    {
        if (incoming.Length == 0)
            return;
        draft.Insert(incoming.ToString());
        incoming.Clear();
    }

    private void QueueEnter(bool newLine)
    {
        var frame = Time.frameCount;
        if (enterLatched || pendingEnter.HasValue || lastResolvedEnterFrame == frame)
            return;

        enterLatched = true;
        pendingEnter = (frame, newLine, Composing || composingLastFrame || !CanEdit);
    }

    private static bool IsEnterDown(Keyboard device) =>
        device.enterKey.isPressed || device.numpadEnterKey.isPressed;

    private void PollEnter(Keyboard device, bool imeOwnsKeys)
    {
        if (!device.ctrlKey.isPressed && !device.altKey.isPressed &&
            (device.enterKey.wasPressedThisFrame || device.numpadEnterKey.wasPressedThisFrame))
            QueueEnter(device.shiftKey.isPressed);

        if (pendingEnter is { } enter)
        {
            enter.Consumed |= imeOwnsKeys || !CanEdit;
            // Let the UI Toolkit deliver the associated IME commit and text before acting on Enter.
            if (enter.Frame == Time.frameCount)
                pendingEnter = enter;
            else
            {
                pendingEnter = null;
                lastResolvedEnterFrame = Time.frameCount;
                if (!enter.Consumed)
                {
                    if (enter.NewLine)
                        draft.Insert("\n");
                    else
                    {
                        submit?.Invoke();
                        return;
                    }
                }
            }
        }

        if (!pendingEnter.HasValue && !IsEnterDown(device))
            enterLatched = false;
    }

    private void ApplyEditingKeys(Keyboard device)
    {
        if (device.ctrlKey.isPressed)
        {
            if (device.aKey.wasPressedThisFrame)
                draft.SelectAll();
            if (device.vKey.wasPressedThisFrame)
                draft.Insert(GUIUtility.systemCopyBuffer ?? "");
        }

        if (device.backspaceKey.wasPressedThisFrame)
            draft.Backspace();
        if (device.deleteKey.wasPressedThisFrame)
            draft.Delete();
        if (device.leftArrowKey.wasPressedThisFrame)
            draft.MoveLeft();
        if (device.rightArrowKey.wasPressedThisFrame)
            draft.MoveRight();
        if (device.homeKey.wasPressedThisFrame)
            draft.Home();
        if (device.endKey.wasPressedThisFrame)
            draft.End();
    }

    private static void Release(Action release)
    {
        try { release(); }
        catch (Exception ex) { Plugin.Logger.LogWarning($"FM Player Talk keyboard release: {ex}"); }
    }
}
