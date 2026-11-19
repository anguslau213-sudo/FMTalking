using System;
using SI.Core;
using SI.Input;
using UnityEngine.UIElements;

namespace FmPlayerTalk;

internal sealed class GameInputBlock
{
    private InputManager? manager;
    private InputRequestKey? request;
    private bool restoreExclusiveInput;

    public void Acquire(VisualElement owner)
    {
        if (request is not null)
            return;

        var input = ManualSingleton<InputManager>.Instance;
        if (input == null)
            throw new InvalidOperationException("游戏的输入管理器尚未就绪。");

        var key = new InputRequestKey(owner, "fm-player-talk-composer");
        restoreExclusiveInput = !input.IsExclusiveInputActive;
        manager = input;
        request = key;

        try
        {
            if (restoreExclusiveInput)
                input.IsExclusiveInputActive = true;
            input.BlockInput(key);
        }
        catch
        {
            Release();
            throw;
        }
    }

    public void Release()
    {
        var input = manager;
        var key = request;
        var restore = restoreExclusiveInput;
        manager = null;
        request = null;
        restoreExclusiveInput = false;

        if (input == null || key is null)
            return;

        try
        {
            if (restore)
                input.IsExclusiveInputActive = false;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"FM Player Talk could not restore exclusive input: {ex}");
        }

        try
        {
            input.UnblockInput(key);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"FM Player Talk could not release its input request: {ex}");
        }
    }
}
