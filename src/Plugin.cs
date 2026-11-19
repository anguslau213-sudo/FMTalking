using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace FmPlayerTalk;

[BepInPlugin("fmplayertalk.demo", PluginVersion.Name, PluginVersion.Build)]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource Logger { get; private set; } = null!;

    public override void Load()
    {
        Logger = Log;
        GameSaveTimeline.Install();
        AddComponent<TalkUpdate>();
        Log.LogInfo($"{PluginVersion.Name} {PluginVersion.Release} · {PluginVersion.Credit}");
    }
}

public sealed class TalkUpdate : MonoBehaviour
{
    private static readonly TalkPanel Window = new();
    private float nextTick;
    private bool reportError = true;
    private string? lastGameLanguage;

    public TalkUpdate(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        Window.PollTyping();
        if (Time.unscaledTime < nextTick)
            return;

        nextTick = Time.unscaledTime + 0.35f;
        try
        {
            UpdateUiLanguage();
            GameUi.Tick(Window.Open);
            GameSaveTimeline.Tick(this);
            Window.Tick();
            reportError = true;
        }
        catch (Exception ex)
        {
            if (reportError)
                Plugin.Logger.LogWarning($"FM Player Talk interface: {ex}");

            reportError = false;
            nextTick = Time.unscaledTime + 2f;
        }
    }

    private void UpdateUiLanguage()
    {
        string? language;
        try { language = SI.Translation.TranslationManager.CurrentLanguage; }
        catch (Exception) { return; }

        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, lastGameLanguage, StringComparison.Ordinal))
            return;
        UiLanguage.ApplyGameLanguage(language);
        lastGameLanguage = language;
    }
}
