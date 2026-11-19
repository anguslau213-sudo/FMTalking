using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FmPlayerTalk;

internal static partial class GameSaveTimeline
{
    private sealed record HistoryMigrationContext(LocalSave Save, string Session, DateTime Date, uint Manager, uint Club);
    private static HistoryMigrationContext? historyContext;
    private static Task<SaveCheckpoints.HistoryCatalog>? historyLookup;
    private static Task<SaveCheckpoints.HistoryMigrationPlan>? historyPreparation;
    private static SaveCheckpoints.HistoryCatalog? historyCatalog;
    private static SaveCheckpoints.HistoryMigrationPlan? historyPlan;

    internal static bool HistoryMigrationBusy => historyLookup != null || historyPreparation != null;
    internal static int HistoryMigrationGeneration { get; private set; }
    internal static string HistoryMigrationStatus { get; private set; } = "迁盘或重命名后，可主动关联内容完全相同的存档历史。不会自动合并，也不会覆盖当前记录。";
    internal static IReadOnlyList<SaveCheckpoints.HistorySource> HistorySources =>
        historyCatalog?.Sources ?? Array.Empty<SaveCheckpoints.HistorySource>();
    internal static bool HasHistoryCatalog => historyCatalog != null;
    internal static string? HistoryPreviewSource => historyPlan?.Source.SourcePath;

    internal static void StartHistoryLookup(uint manager, uint club)
    {
        if (HistoryMigrationBusy) return;
        var context = CaptureHistoryContext(manager, club);
        historyContext = context;
        historyCatalog = null;
        historyPlan = null;
        HistoryMigrationStatus = "正在核对当前存档的完整内容并查找可关联的历史…";
        HistoryMigrationGeneration++;
        historyLookup = Task.Run(() => Checkpoints.InspectHistory(context.Save.Path));
    }

    internal static void PreviewHistorySource(string originalPath)
    {
        if (HistoryMigrationBusy || historyCatalog is not { } catalog || historyContext is not { } context ||
            !SameHistoryContext(context))
            throw new InvalidOperationException("当前生涯已变化，请重新查找历史。");
        historyPlan = null;
        HistoryMigrationStatus = "正在核对来源历史和当前会话…";
        HistoryMigrationGeneration++;
        historyPreparation = Task.Run(() => Checkpoints.PrepareHistoryMigration(catalog, context.Session, originalPath));
    }

    internal static bool ConfirmHistorySource()
    {
        if (HistoryMigrationBusy || historyPlan is not { } plan || historyContext is not { } context ||
            !SameHistoryContext(context))
            throw new InvalidOperationException("当前生涯已变化，历史未关联，请重新查找。");
        var imported = Checkpoints.ApplyHistoryMigration(plan);
        // Apply above runs on Unity's thread, so no UI save/load handler can race
        // this switch. The source and former active session remain untouched.
        DirectoryPath = imported;
        SetActiveLocalSave(context.Save.Path);
        historyCatalog = null;
        historyPlan = null;
        historyContext = null;
        HistoryMigrationStatus = "历史已关联到当前存档。原历史与原会话均保留，可继续交流；正常保存 FM 后，新进度也会继续配对。";
        HistoryMigrationGeneration++;
        Plugin.Logger.LogInfo("FM save timeline: explicitly associated matching save history without merging or overwriting.");
        return true;
    }

    private static HistoryMigrationContext CaptureHistoryContext(uint manager, uint club)
    {
        if (!HasStableLocalSave || ActiveLocalSave is not { } save || DirectoryPath is not { } session ||
            ModelConnection.IsBusy || GameCalendar.ReadDate() is not DateTime day ||
            !GameCalendar.IsIdleSinglePlayerCareer(manager, club))
            throw new InvalidOperationException("请等待本地单人生涯载入、保存和对话结束后，再关联历史。");
        return new(save, session, day.Date, manager, club);
    }

    private static bool SameHistoryContext(HistoryMigrationContext context) => HasStableLocalSave &&
        ActiveLocalSave == context.Save && DirectoryPath == context.Session && !ModelConnection.IsBusy &&
        GameCalendar.ReadDate()?.Date == context.Date && GameCalendar.IsIdleSinglePlayerCareer(context.Manager, context.Club);

    private static void TickHistoryMigration()
    {
        if (historyLookup is { IsCompleted: true } lookup)
        {
            historyLookup = null;
            if (historyContext is not { } context || !SameHistoryContext(context))
            {
                _ = lookup.Exception;
                CancelHistoryMigration();
                return;
            }
            try
            {
                historyCatalog = lookup.GetAwaiter().GetResult();
                HistoryMigrationStatus = historyCatalog.Sources.Length == 0
                    ? "没有找到已建立索引的同内容历史。旧版历史可填写原存档完整路径进行核对；原文件可以已搬走。"
                    : "找到内容完全相同的历史来源。先选择一份预览，确认后才关联；不会合并或覆盖当前记录。";
            }
            catch (Exception error) { HistoryMigrationStatus = HistoryProblem(error); }
            HistoryMigrationGeneration++;
        }
        if (historyPreparation is { IsCompleted: true } preparation)
        {
            historyPreparation = null;
            if (historyContext is not { } context || !SameHistoryContext(context))
            {
                _ = preparation.Exception;
                CancelHistoryMigration();
                return;
            }
            try
            {
                historyPlan = preparation.GetAwaiter().GetResult();
                HistoryMigrationStatus = $"已核实来源含 {historyPlan.Source.RecordFiles} 份人物与剧情记录，与当前存档内容完全相同。点击「确认关联这份历史」后生效。";
            }
            catch (Exception error) { HistoryMigrationStatus = HistoryProblem(error); }
            HistoryMigrationGeneration++;
        }
        if (!HistoryMigrationBusy && historyContext is { } active && !SameHistoryContext(active))
            CancelHistoryMigration();
    }

    internal static void ReportHistoryProblem(Exception error)
    {
        historyPlan = null;
        HistoryMigrationStatus = HistoryProblem(error);
        HistoryMigrationGeneration++;
    }

    private static string HistoryProblem(Exception error) => error is System.IO.IOException or System.IO.InvalidDataException or InvalidOperationException or ArgumentException
        ? error.Message : "历史资料无法核实，未关联或覆盖任何现有记录。";

    private static void CancelHistoryMigration()
    {
        if (historyLookup != null)
            _ = historyLookup.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (historyPreparation != null)
            _ = historyPreparation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var hadContext = historyContext != null;
        historyLookup = null;
        historyPreparation = null;
        historyCatalog = null;
        historyPlan = null;
        historyContext = null;
        if (hadContext)
        {
            HistoryMigrationStatus = "保存、载入、日期或对话状态已变化，请重新查找历史。现有历史保持原样。";
            HistoryMigrationGeneration++;
        }
    }
}
