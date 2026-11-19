using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using FM.GamePlugin;
using FM.UI;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SI.Bindable;
using SI.Core;
using SI.Interop;
using DateTime = System.DateTime;

namespace FmPlayerTalk;

internal static partial class GameSaveTimeline
{
    private const uint FilenameField = 1818715758; // The native save record's lgfn field.
    private const uint FolderField = 1718052462; // The native save record's fgfn field.
    private const uint RecentExtensionField = 0x434E714D;
    private const uint RecentModifiedField = 0x43714D41;
    private const uint RecentSourceField = 0x79703161;
    private const uint RecentVariantField = 0x41383020;
    private const int MaxPreferenceBytes = 16 * 1024;
    private static readonly TimeSpan NativeTimestampGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LastSaveHandoffLimit = TimeSpan.FromSeconds(2);
    private static readonly string GameDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sports Interactive", "Football Manager 27");
    private static readonly SaveCheckpoints Checkpoints = new(ModDataPath.Root);
    private static readonly string[] LastCareerPreferences =
    {
        "last_saved_career_like.xml", "last_saved_career.xml", "last_saved_game.xml"
    };
    private static Harmony? harmony;
    private static EventSubsystem? eventSource;
    private static Il2CppSystem.Action<EventFromPlugin>? handler;
    private static Task<LoadedSession>? restoring;
    private static Task publishing = Task.CompletedTask;
    private static Task<string?>? finishingSave;
    private static long localSaveRevision;
    private static string? selectingPath;
    private static LastSaveHandoff? lastSaveHandoff;
    private static BrowserSelection? browserSelection;
    private static DateTime? loadStartedUtc;
    private static OperationWindow? unlinkedLoadWindow;
    private static PendingSave? pendingSave;
    private static LocalOverwrite? activeSaveBaseline;
    private static bool gameSaving;
    private static bool loading;
    private static bool needsActiveWorld;
    private static bool hooked;
    private static bool traceMissingBus;

    internal static string? DirectoryPath { get; private set; }
    internal static LocalSave? ActiveLocalSave { get; private set; }
    internal static DateTime? LoadedWorldDate { get; private set; }
    internal static bool RestoreFailed { get; private set; }
    private static bool HasStableLocalSave => hooked && eventSource != null && !loading && !gameSaving && !needsActiveWorld &&
        restoring == null && pendingSave == null && finishingSave == null && !RestoreFailed &&
        ActiveLocalSave != null && DirectoryPath != null;
    internal static bool CanChangeGameData => HasStableLocalSave && !HistoryMigrationBusy;

    internal sealed record LocalSave(string Path, long Revision);

    internal static void Install()
    {
        try
        {
            harmony = new Harmony("fmplayertalk.save-timeline");
            Patch(typeof(LoadGameHandlerModule), "StartLoadingSave", nameof(LoadStarting));
            Patch(typeof(SaveGameHandlerModule), "DoSaveGame", nameof(SaveStarting));
            hooked = true;
            Plugin.Logger.LogInfo("FM save timeline: load and save intent hooks installed.");
            TryPatchLastSave();
            TryPatchOutgoingSaveEvents();
            TryPatchFileBrowser();
            TryPatchGameSavingState();
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline hooks unavailable: {error.GetType().Name}.");
        }
    }

    private static void Patch(Type target, string method, string prefix)
    {
        var original = AccessTools.Method(target, method, new[] { typeof(Record) }) ??
            throw new MissingMethodException(target.FullName, method);
        var patch = AccessTools.Method(typeof(GameSaveTimeline), prefix) ??
            throw new MissingMethodException(typeof(GameSaveTimeline).FullName, prefix);
        harmony!.Patch(original, prefix: new HarmonyMethod(patch));
    }

    private static void TryPatchLastSave()
    {
        try
        {
            var owner = typeof(GameSaveTimeline);
            var signature = new[]
            {
                typeof(string), typeof(DialogManager.DialogButtonData).MakeByRefType(), typeof(DynamicReference)
            };
            var original = AccessTools.Method(typeof(LoadGameHandlerModule), "ConfirmLoadLastSaveOnClick", signature)
                ?? throw new MissingMethodException(nameof(LoadGameHandlerModule), "ConfirmLoadLastSaveOnClick");
            harmony!.Patch(original, prefix: new HarmonyMethod(AccessTools.Method(owner, nameof(LastSaveStarting))),
                finalizer: new HarmonyMethod(AccessTools.Method(owner, nameof(LastSaveEnded))));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline: the native last-save path hook is unavailable ({error.GetType().Name}).");
        }
    }

    private static void LastSaveStarting(DynamicReference __2)
    {
        try
        {
            var path = SavePath(__2, mustExist: true) ??
                ResolveNativeFile(ManualSingleton<FileBrowserHandler>.Instance?.LastSavePath, mustExist: true);
            lastSaveHandoff = path == null ? null : new LastSaveHandoff(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            lastSaveHandoff = null;
        }
    }

    private static void LastSaveEnded() => lastSaveHandoff = null;

    private static void TryPatchGameSavingState()
    {
        try
        {
            var original = AccessTools.Method(typeof(ContinueManagerModule), "OnGameSavingChanged",
                new[] { typeof(Bindings.Key), typeof(TypedValue) })
                ?? throw new MissingMethodException(nameof(ContinueManagerModule), "OnGameSavingChanged");
            var prefix = AccessTools.Method(typeof(GameSaveTimeline), nameof(GameSavingChanged));
            harmony!.Patch(original, prefix: new HarmonyMethod(prefix));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline: automatic save state is unavailable ({error.GetType().Name}).");
        }
    }

    private static void GameSavingChanged(TypedValue? __1)
    {
        try
        {
            if (__1 == null || !__1.TryGet<bool>(out var saving))
                return;
            var capture = saving && CanChangeGameData;
            gameSaving = saving;
            if (!saving)
            {
                if (pendingSave is { FromSavingState: true })
                    pendingSave = null;
                return;
            }
            if (!capture || activeSaveBaseline is not { } baseline)
                return;

            // FM can rotate the previous primary out of the way before it signals saving.
            // Keep the last verified revision while the new file is being written.
            if (!MatchesBaseline(baseline.Path, baseline.Before) &&
                !MatchesBaseline(SaveCheckpoints.SecondSlotPath(baseline.Path), baseline.Before))
                return;
            CaptureSave(null, baseline);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline: automatic save could not be captured ({error.GetType().Name}).");
        }
    }

    private static bool MatchesBaseline(string path, SaveCheckpoints.Stamp before) =>
        VerifiedLocalSave(path) && SaveCheckpoints.Stamp.Read(path) == before;

    private static void TryPatchOutgoingSaveEvents()
    {
        try
        {
            var original = AccessTools.Method(typeof(GameInteropSubsystem), "SendEvent",
                new[] { typeof(uint), typeof(InteropReference), typeof(TypedValue) })
                ?? throw new MissingMethodException(nameof(GameInteropSubsystem), "SendEvent");
            var prefix = AccessTools.Method(typeof(GameSaveTimeline), nameof(OutgoingSaveEvent));
            harmony!.Patch(original, prefix: new HarmonyMethod(prefix));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline: outgoing save and load data is unavailable ({error.GetType().Name}).");
        }
    }

    private static void OutgoingSaveEvent(uint __0, TypedValue? __2)
    {
        if (__0 == (uint)InteropEventIDs.EventFmxLoadGame && loading && selectingPath == null &&
            SavePath(__2, mustExist: true) is { } loadPath)
        {
            selectingPath = loadPath;
            Plugin.Logger.LogInfo("FM save timeline: verified the local path in the native load request.");
        }
        else if (__0 == (uint)InteropEventIDs.EventFmxSaveGame && !loading)
        {
            if (pendingSave == null)
                CaptureSave(SavePath(__2, mustExist: false));
            else if (pendingSave is { Path: null } saving && SavePath(__2, mustExist: false) is { } savePath)
            {
                pendingSave = saving with { Path = savePath };
                Plugin.Logger.LogInfo("FM save timeline: verified the local path in the native save request.");
            }
        }
    }

    private static void TryPatchFileBrowser()
    {
        try
        {
            var original = AccessTools.Method(typeof(MapFileBrowserEntryToSaveReferenceEventContext), "FillEvent",
                new[] { typeof(MapFileBrowserEntryToSaveReferenceEvent) })
                ?? throw new MissingMethodException("File browser load mapping");
            var postfix = AccessTools.Method(typeof(GameSaveTimeline), nameof(FileBrowserSelected));
            harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception error)
        {
            Plugin.Logger.LogInfo($"FM save timeline: file browser associations unavailable ({error.GetType().Name}).");
        }
    }

    private static void FileBrowserSelected(MapFileBrowserEntryToSaveReferenceEventContext __instance,
        MapFileBrowserEntryToSaveReferenceEvent __0)
    {
        try
        {
            browserSelection = __0.SaveGameRecord is { } record &&
                ResolveNativeFile(__instance.m_filePath, mustExist: true) is { } path
                ? new BrowserSelection(record, path) : null;
        }
        catch (Exception) { browserSelection = null; }
    }

    internal static void Tick(TalkUpdate owner)
    {
        if (!hooked)
            return;
        if (eventSource == null)
        {
            try
            {
                if (GameSubsystems.GetOptional<EventSubsystem>() is not { } source)
                    return;
                handler ??= (Il2CppSystem.Action<EventFromPlugin>)(Action<EventFromPlugin>)OnEvent;
                source.Register(owner, handler);
                eventSource = source;
                Plugin.Logger.LogInfo("FM save timeline: listening for native completion events.");
            }
            catch (Exception error)
            {
                if (!traceMissingBus)
                    Plugin.Logger.LogWarning($"FM save timeline event subscription unavailable: {error.GetType().Name}.");
                traceMissingBus = true;
                return;
            }
        }
        if (!loading && LoadedWorldDate == null && (needsActiveWorld || restoring != null || DirectoryPath != null))
            LoadedWorldDate = GameCalendar.ReadDate();
        if (restoring is { IsCompleted: true } ready && GameUi.HasActiveCareerEntry())
        {
            restoring = null;
            needsActiveWorld = false;
            if (ready.IsCompletedSuccessfully && ready.Result.Directory is { } path)
            {
                DirectoryPath = path;
                if (ActiveLocalSave == null && ready.Result.FmPath is { } fmPath)
                    SetActiveLocalSave(fmPath);
                Plugin.Logger.LogInfo("FM save timeline: loaded isolated conversation session.");
            }
            else
            {
                RestoreFailed = true;
                Plugin.Logger.LogWarning("FM save timeline: the checkpoint could not be read; older conversation data was left in place.");
            }
        }
        if (finishingSave is { IsCompleted: true } saved)
        {
            finishingSave = null;
            if (saved.IsCompletedSuccessfully && saved.Result is { } path)
                SetActiveLocalSave(path);
        }
        if (needsActiveWorld && !loading && restoring == null && GameUi.HasActiveCareerEntry())
        {
            needsActiveWorld = false;
            var window = unlinkedLoadWindow;
            unlinkedLoadWindow = null;
            var writes = publishing;
            restoring = Task.Run(async () =>
            {
                await writes.ConfigureAwait(false);
                if (window is { } load && RecentPreferencePath(load) is { } exact)
                {
                    Plugin.Logger.LogInfo("FM save timeline: verified local load path from fresh FM preferences.");
                    return new LoadedSession(Checkpoints.Restore(exact), exact);
                }
                return new LoadedSession(Checkpoints.CreateSession(), null);
            });
        }
        TickHistoryMigration();
    }

    private static void LoadStarting(Record __0)
    {
        loadStartedUtc = DateTime.UtcNow;
        unlinkedLoadWindow = null;
        var handoff = lastSaveHandoff;
        lastSaveHandoff = null;
        selectingPath = SavePath(__0, mustExist: true);
        if (selectingPath == null && browserSelection is { } browser && __0.Pointer == browser.Record.Pointer)
        {
            selectingPath = browser.Path;
            Plugin.Logger.LogInfo("FM save timeline: verified the local path from the selected file browser record.");
        }
        browserSelection = null;
        if (selectingPath == null && handoff != null && loadStartedUtc.Value >= handoff.StartedUtc &&
            loadStartedUtc.Value - handoff.StartedUtc <= LastSaveHandoffLimit)
        {
            selectingPath = handoff.Path;
            Plugin.Logger.LogInfo("FM save timeline: verified the confirmed last-save path.");
        }
        if (selectingPath == null && RecentLocalSelection(__0) is { } recent)
        {
            selectingPath = recent;
            Plugin.Logger.LogInfo("FM save timeline: verified the local file selected in the recent-saves list.");
        }
        loading = true;
        DirectoryPath = null;
        ForgetLocalSave();
        restoring = null;
        pendingSave = null;
        if (selectingPath != null)
            Plugin.Logger.LogInfo("FM save timeline: selected an exact local load path.");
        else
            LogMissingPath("selected load", DescribeRecord(__0));
    }

    private static void SaveStarting(Record __0) => CaptureSave(SavePath(__0, mustExist: false));

    private static void CaptureSave(string? path, LocalOverwrite? automaticOverwrite = null)
    {
        try
        {
            pendingSave = null;
            var startedUtc = DateTime.UtcNow;
            var overwrite = path == null ? automaticOverwrite ?? CaptureLocalOverwrite() : null;
            var saving = new PendingSave(path, null, startedUtc, overwrite, automaticOverwrite != null);
            pendingSave = saving;
            if (DirectoryPath is string active && Checkpoints.Freeze(active) is string frozen)
            {
                pendingSave = saving with { FrozenSession = frozen };
                Plugin.Logger.LogInfo(automaticOverwrite != null
                    ? "FM save timeline: froze conversation data for the automatic save."
                    : "FM save timeline: froze conversation data for the requested save.");
            }
            else if (DirectoryPath != null)
                Plugin.Logger.LogWarning("FM save timeline: no exact local save path or complete conversation snapshot was available.");
        }
        catch (Exception error)
        {
            pendingSave = null;
            Plugin.Logger.LogWarning($"FM save timeline could not capture the session: {error.GetType().Name}.");
        }
    }

    private static LocalOverwrite? CaptureLocalOverwrite()
    {
        try
        {
            return ActiveLocalSave is { } active && VerifiedLocalSave(active.Path) &&
                SaveCheckpoints.Stamp.Read(active.Path) is { Length: > 0 } before ? new(active.Path, before) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void OnEvent(EventFromPlugin message)
    {
        try
        {
            switch ((InteropEventIDs)message.ID)
            {
                case InteropEventIDs.EventFmxClearGame:
                    DirectoryPath = null;
                    ForgetLocalSave();
                    browserSelection = null;
                    restoring = null;
                    pendingSave = null;
                    unlinkedLoadWindow = null;
                    break;
                case InteropEventIDs.EventFmxNewGameCreated:
                    EndLoad(null, false);
                    break;
                case InteropEventIDs.EventFmxLoadedSaveGame:
                    var completedPath = SavePath(message.Value, mustExist: true);
                    if (selectingPath != null && PathsDisagree(selectingPath, completedPath))
                    {
                        Plugin.Logger.LogWarning("FM save timeline: native load paths disagree; no existing conversation session was attached.");
                        EndLoad(null, false);
                    }
                    else
                    {
                        if (selectingPath == null)
                        {
                            if (completedPath != null)
                                Plugin.Logger.LogInfo("FM save timeline: verified the completed load path from its native event.");
                            else
                                LogMissingPath("completed load", DescribeValue(message.Value));
                        }
                        EndLoad(completedPath ?? selectingPath, true);
                    }
                    break;
                case InteropEventIDs.EventFmxSaveGameCompleted:
                    if (pendingSave != null)
                        CompleteSave(SavePath(message.Value, mustExist: true));
                    break;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline event failed: {error.GetType().Name}.");
        }
    }

    private static void CompleteSave(string? completedPath)
    {
        if (pendingSave is not { } saved)
            return;
        pendingSave = null;
        if (saved.Path != null && PathsDisagree(saved.Path, completedPath))
        {
            Plugin.Logger.LogWarning("FM save timeline: native save paths disagree; no conversation checkpoint was replaced.");
            return;
        }
        var completedUtc = DateTime.UtcNow;
        var window = new OperationWindow(saved.StartedUtc, completedUtc);
        if (completedPath != null && saved.Path == null &&
            window.ContainsSaveWrite(File.GetLastWriteTimeUtc(completedPath)))
            saved = saved with { Path = completedPath };
        SaveCheckpoints.Stamp? observed = saved.Path == null ? null : SaveCheckpoints.Stamp.Read(saved.Path);
        if (saved.FromSavingState && saved.Path == null)
        {
            if (saved.Overwrite is { } baseline && SavedOverwriteStamp(baseline, window) is { } revision)
            {
                saved = saved with { Path = baseline.Path };
                observed = revision;
                Plugin.Logger.LogInfo("FM save timeline: verified the new revision of the automatic save.");
            }
            else if (RecentPreferencePath(window, requireNewSaveWrite: true) is { } exact)
            {
                saved = saved with { Path = exact };
                observed = SaveCheckpoints.Stamp.Read(exact);
            }
            else
            {
                Plugin.Logger.LogWarning("FM save timeline: no local revision could be verified for the automatic save.");
                return;
            }
        }
        var previous = publishing;
        finishingSave = Task.Run(() => PublishCheckpoint(previous, saved, observed, completedUtc));
        publishing = finishingSave;
    }

    private static void EndLoad(string? path, bool existingSave)
    {
        DirectoryPath = null;
        ForgetLocalSave();
        selectingPath = null;
        loading = false;
        needsActiveWorld = path == null;
        unlinkedLoadWindow = existingSave && path == null && loadStartedUtc is { } startedUtc
            ? new OperationWindow(startedUtc, DateTime.UtcNow) : null;
        loadStartedUtc = null;
        var writes = publishing;
        restoring = path == null ? null : Task.Run(async () =>
        {
            await writes.ConfigureAwait(false);
            return new LoadedSession(Checkpoints.Restore(path), path);
        });
    }

    private static void ForgetLocalSave()
    {
        CancelHistoryMigration();
        ActiveLocalSave = null;
        activeSaveBaseline = null;
        gameSaving = false;
        LoadedWorldDate = null;
        finishingSave = null;
        RestoreFailed = false;
    }

    private static void SetActiveLocalSave(string path)
    {
        ActiveLocalSave = new LocalSave(path, ++localSaveRevision);
        activeSaveBaseline = CaptureLocalOverwrite();
    }

    private static SaveCheckpoints.Stamp? SavedOverwriteStamp(LocalOverwrite overwrite, OperationWindow window) =>
        SaveCheckpoints.Stamp.Read(overwrite.Path) is { Length: > 0 } changed && changed != overwrite.Before &&
        window.ContainsSaveWrite(changed.LastWriteUtc) ? changed : null;

    private static async Task<string?> PublishCheckpoint(Task previous, PendingSave saved, SaveCheckpoints.Stamp? observed,
        DateTime completedUtc)
    {
        string? verified = null;
        try
        {
            await previous.ConfigureAwait(false);
            var window = new OperationWindow(saved.StartedUtc, completedUtc);
            var path = saved.Path;
            if (path == null && RecentPreferencePath(window, requireNewSaveWrite: true) is { } exact)
            {
                Plugin.Logger.LogInfo("FM save timeline: verified completed save path from fresh FM preferences.");
                path = exact;
                observed = SaveCheckpoints.Stamp.Read(path);
            }
            if (path == null && saved.Overwrite is { } overwrite &&
                SavedOverwriteStamp(overwrite, window) is { } changed)
            {
                // An ordinary overwrite can leave FM's last-save preferences unchanged.
                // Only consider the already verified active file and require a new revision.
                path = overwrite.Path;
                observed = changed;
                Plugin.Logger.LogInfo("FM save timeline: verified an overwrite of the active local save.");
            }
            if (path != null && observed is { Length: > 0 } stamp && VerifiedLocalSave(path) &&
                SaveCheckpoints.Stamp.Read(path) == stamp && window.ContainsSaveWrite(stamp.LastWriteUtc))
                verified = path;
            if (saved.FrozenSession == null)
                return verified;
            if (verified != null && Checkpoints.StoreMatchingRevision(verified, saved.FrozenSession, observed))
                Plugin.Logger.LogInfo("FM save timeline: paired a conversation checkpoint with the completed save.");
            else
                Plugin.Logger.LogWarning("FM save timeline: completed save file could not be paired with a conversation checkpoint.");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"FM save timeline could not publish the checkpoint: {error.GetType().Name}.");
        }
        return verified;
    }

    private static string? RecentPreferencePath(OperationWindow window, bool requireNewSaveWrite = false)
    {
        try
        {
            var preferences = Path.Combine(GameDataPath, "Preferences", "version 6");
            string? agreedPath = null;
            foreach (var name in LastCareerPreferences)
            {
                if (ReadRecentPreference(Path.Combine(preferences, name), window) is not { } value ||
                    (agreedPath != null && !string.Equals(agreedPath, value, StringComparison.OrdinalIgnoreCase)))
                    return null;
                agreedPath = value;
            }

            if (agreedPath == null)
                return null;
            var save = new FileInfo(agreedPath);
            return save.Exists && save.Length > 0 && (save.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   (!requireNewSaveWrite || window.ContainsSaveWrite(save.LastWriteTimeUtc))
                ? agreedPath : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or XmlException)
        {
            return null;
        }
    }

    private static string? ReadRecentPreference(string path, OperationWindow window)
    {
        var entry = new FileInfo(path);
        if (!entry.Exists || entry.Length is <= 0 or > MaxPreferenceBytes ||
            !window.ContainsPreferenceWrite(entry.LastWriteTimeUtc) || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            return null;

        var timestamp = entry.LastWriteTimeUtc;
        var length = entry.Length;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxPreferenceBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "record" || reader.IsEmptyElement ||
            !reader.Read() || reader.NodeType != XmlNodeType.Element || reader.Name != "string" ||
            reader.GetAttribute("id") != "value")
            return null;

        var value = NormalizeLocalSavePath(reader.GetAttribute("value"));
        reader.ReadStartElement("string");
        if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "string")
            reader.ReadEndElement();
        if (reader.NodeType != XmlNodeType.EndElement || reader.Name != "record")
            return null;
        reader.ReadEndElement();
        if (!reader.EOF)
            return null;
        entry.Refresh();
        return entry.Exists && entry.LastWriteTimeUtc == timestamp && entry.Length == length ? value : null;
    }

    private static bool PathsDisagree(string selected, string? completed) =>
        completed != null && !string.Equals(selected, completed, StringComparison.OrdinalIgnoreCase);

    private static void LogMissingPath(string stage, string structure) =>
        Plugin.Logger.LogInfo($"FM save timeline: {stage} has no verified local path ({structure}).");

    private static string DescribeRecord(Record? record)
    {
        try { return record == null ? "no record" : $"{record.Count} native record fields"; }
        catch (Exception) { return "record shape unavailable"; }
    }

    private static string DescribeValue(TypedValue? value)
    {
        try { return value?.DataType?.Name ?? "no payload"; }
        catch (Exception) { return "payload shape unavailable"; }
    }

    private static string? RecentLocalSelection(Record record)
    {
        try
        {
            // FM27's local recent-saves list omits the folder. Its selected row contains
            // the actual basename, extension and filesystem modification time in local milliseconds.
            if (record.Count < 6 || !HasZeroField(record, RecentSourceField) || !HasZeroField(record, RecentVariantField) ||
                !record.TryGetValue(FolderField, out var folder) || folder?.DataType?.Name != "String" ||
                !string.IsNullOrEmpty(folder.AsString()) ||
                !record.TryGetValue(FilenameField, out var display) || display?.DataType?.Name != "String" ||
                !record.TryGetValue(RecentExtensionField, out var extension) || extension?.DataType?.Name != "String" ||
                !record.TryGetValue(RecentModifiedField, out var modified) ||
                modified?.Get()?.TryCast<SI.Core.DateTime>() is not { IsValid: true, Type: SI.Translation.CppTranslatableType.SicoreDate } date)
                return null;
            return SaveCheckpoints.FindRecentLocalSave(display.AsString(), extension.AsString(), date.Data1, SaveSearchDirectories());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasZeroField(Record record, uint key) => record.TryGetValue(key, out var value) &&
        value?.DataType?.Name == "DynamicNumber" && value.TryGet<DynamicNumber>(out var number) &&
        number.Kind is DynamicNumber.NumberKind.SignedWhole or DynamicNumber.NumberKind.UnsignedWhole && number.GetDouble() == 0;

    private static string? SavePath(Record? record, bool mustExist, int depth = 0)
    {
        try
        {
            if (record == null || record.Count > 64 || depth > 2)
                return null;
            if (record.TryGetValue(FilenameField, out var filename) && record.TryGetValue(FolderField, out var folder) &&
                filename?.DataType?.Name == "String" && folder?.DataType?.Name == "String")
            {
                var name = filename.AsString();
                var directory = folder.AsString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(directory) &&
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                    Path.IsPathFullyQualified(directory))
                {
                    var combined = NormalizeLocalSavePath(Path.Combine(directory,
                        name.EndsWith(".fm", StringComparison.OrdinalIgnoreCase) ? name : name + ".fm"));
                    if (combined != null && (!mustExist || VerifiedLocalSave(combined)))
                        return combined;
                }
                // When FM supplies a directory explicitly, a same-named file elsewhere is not the selected revision.
                return null;
            }
            if (record.TryGetValue(FilenameField, out var field) && SavePath(field, mustExist, depth) is { } exact)
                return exact;
            string? onlyPath = null;
            foreach (var value in record.Values)
            {
                var candidate = SavePath(value, mustExist, depth);
                if (candidate == null)
                    continue;
                if (onlyPath != null && !string.Equals(onlyPath, candidate, StringComparison.OrdinalIgnoreCase))
                    return null;
                onlyPath = candidate;
            }
            return onlyPath;
        }
        catch (Exception)
        {
            // The native record may represent a cloud-only file.
        }
        return null;
    }

    private static string? SavePath(TypedValue? field, bool mustExist, int depth = 0)
    {
        try
        {
            if (field is not { IsNull: false } || depth > 2)
                return null;
            if (field.DataType?.Name == "String")
                return ResolveNativeFile(field.AsString(), mustExist);
            var native = field.Get();
            if (native?.TryCast<FileBrowserEntryReference>() is { IsFile: true } entry)
                return ResolveNativeFile(entry.Path, mustExist);
            return native?.TryCast<Record>() is { } record ? SavePath(record, mustExist, depth + 1) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveNativeFile(string? value, bool mustExist)
    {
        var normalized = NormalizeLocalSavePath(value);
        if (normalized != null)
            return !mustExist || VerifiedLocalSave(normalized) ? normalized : null;
        if (!mustExist)
            return null;

        var name = value?.Trim();
        if (name == null || name.Length > 260 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetExtension(name), ".fm", StringComparison.OrdinalIgnoreCase))
            return null;

        // A basename alone cannot identify a new Save As target. Match existing files in known FM data roots.
        string? only = null;
        foreach (var root in SaveSearchDirectories())
        {
            var candidate = Path.GetFullPath(Path.Combine(root, name));
            if (!VerifiedLocalSave(candidate))
                continue;
            if (only != null && PathsDisagree(only, candidate))
                return null;
            only = candidate;
        }
        return only;
    }

    internal static string[] SaveSearchDirectories()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fallback = Path.Combine(documents, "Sports Interactive", "Football Manager 27", "games");
        var preferred = ManualSingleton<FileBrowserHandler>.Instance?.DefaultSavePath;
        if (preferred == null || !Path.IsPathFullyQualified(preferred))
            return Path.IsPathFullyQualified(fallback) ? new[] { fallback } : Array.Empty<string>();
        return Path.IsPathFullyQualified(fallback) && PathsDisagree(preferred, fallback)
            ? new[] { preferred, fallback } : new[] { preferred };
    }

    private static bool VerifiedLocalSave(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static string? NormalizeLocalSavePath(string? value)
    {
        var path = value?.Trim();
        if (path is { Length: > 1 } && path[0] == '"' && path[^1] == '"')
            path = path[1..^1].Trim();
        if (path != null && path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile && uri.IsLoopback &&
            uri.Query.Length == 0 && uri.Fragment.Length == 0)
            path = uri.LocalPath;
        return path != null && Path.IsPathFullyQualified(path) &&
               string.Equals(Path.GetExtension(path), ".fm", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path) : null;
    }

    private readonly record struct OperationWindow(DateTime StartedUtc, DateTime CompletedUtc)
    {
        internal bool ContainsPreferenceWrite(DateTime writtenUtc) =>
            writtenUtc >= StartedUtc - NativeTimestampGrace && writtenUtc <= CompletedUtc + NativeTimestampGrace;

        internal bool ContainsSaveWrite(DateTime writtenUtc) =>
            writtenUtc >= StartedUtc && writtenUtc <= CompletedUtc + NativeTimestampGrace;
    }

    private sealed record LoadedSession(string? Directory, string? FmPath);
    private sealed record BrowserSelection(Record Record, string Path);
    private sealed record LocalOverwrite(string Path, SaveCheckpoints.Stamp Before);
    private sealed record PendingSave(string? Path, string? FrozenSession, DateTime StartedUtc, LocalOverwrite? Overwrite,
        bool FromSavingState);
    private sealed record LastSaveHandoff(string Path, DateTime StartedUtc);
}
