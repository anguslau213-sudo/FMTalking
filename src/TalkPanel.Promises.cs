using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private string? uncertainPromiseSession;
    private sealed record PromiseWaiverRequest(string Session, GameSaveTimeline.LocalSave LocalSave,
        uint Manager, uint Club, uint Player, IntPtr Layer, DateTime GameDate, Conversation Chat,
        FmPromiseOption Option, IReadOnlyList<PromiseOptionForModel> PromptOptions)
    {
        internal string ReceiptId { get; } = Guid.NewGuid().ToString("N");
        internal string? ClubName { get; init; }
    }

    private const string PromiseOptionId = "P1";

    private void TickActivePromises()
    {
        FmActivePromises.Tick(ModelConnection.PromiseWaiversEnabled && GameSaveTimeline.CanChangeGameData &&
            layer?.panel != null && person?.PublicId is uint playerId && roster is { } team &&
            team.Players.Any(entry => entry.PublicId == playerId));
    }

    private PromiseWaiverRequest? CapturePromiseWaiver(DateTime? date, Conversation chat, bool hasPromiseContext,
        out string? status)
    {
        status = null;
        if (!hasPromiseContext)
            return null;
        if (!ModelConnection.PromiseWaiversEnabled)
        {
            status = "「放弃原版承诺」尚未开启。本轮只保留对话。";
            return null;
        }
        if (person?.PublicId is not uint playerId || roster is not { } team ||
            !team.Players.Any(entry => entry.PublicId == playerId))
        {
            status = "未能在当前俱乐部的阵容中确认这名球员。本轮只保留对话。";
            return null;
        }

        if (saveSessionDirectory != null && uncertainPromiseSession == saveSessionDirectory)
        {
            status = "承诺变更暂时停用，请重新载入修改前的 FM 存档。";
            return null;
        }

        if (!FmNativePromises.IsSupported)
        {
            status = "当前 FM 构建暂未适配放弃原版承诺。";
            return null;
        }
        var saveBlocker = NativeSaveWriteBlocker();
        if (saveBlocker != null || GameSaveTimeline.ActiveLocalSave is not { } save ||
            saveSessionDirectory is not { } session)
        {
            status = saveBlocker ?? "未能核实当前生涯。请重新载入本地存档。";
            return null;
        }
        if (layer?.panel == null || date is not DateTime today ||
            !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
        {
            status = "请在本地单人生涯停止推进、比赛或载入后，再谈这条承诺。";
            return null;
        }

        var observed = FmNativePromises.Read(team.ManagerId, playerId, team.ClubId, today);
        if (!observed.Available || observed.Promises.Count == 0)
        {
            status = observed.Reason ?? "没有读到可放弃的位置和角色承诺。";
            return null;
        }
        if (observed.Promises.Count != 1)
        {
            status = "读到多条相似的承诺，暂时无法逐条确认。";
            return null;
        }
        var option = FmActivePromises.Match(playerId, observed.Promises, today);
        if (option == null)
        {
            status = "暂未能把这条承诺和 FM 的进行中名单对应起来。这次只保留对话。";
            return null;
        }
        var promptOptions = new[] { new PromiseOptionForModel(PromiseOptionId, option.Title, option.Deadline) };
        return new(session, save, team.ManagerId, team.ClubId, playerId, layer.Pointer, today.Date,
            chat, option, promptOptions) { ClubName = team.ClubName };
    }

    private (string? Status, bool Error) CompletePromiseWaiver(PendingReceipt? signal, Reply reply)
    {
        if (reply.WasInterrupted)
            return default;
        if (reply.PromiseWaiverId is not { } optionId)
        {
            if (signal is { PromiseMentioned: true, PromiseStatus: { } blocker })
            {
                Plugin.Logger.LogInfo("FM Player Talk promise waiver unavailable: " + blocker);
                return (blocker, true);
            }
            return default;
        }

        if (signal?.PromiseRequest is not { } request || signal.PromiseContextChanged ||
            !string.Equals(optionId, PromiseOptionId, StringComparison.Ordinal) ||
            !GameSaveTimeline.CanChangeGameData || !ModelConnection.PromiseWaiversEnabled ||
            GameSaveTimeline.DirectoryPath != request.Session ||
            GameSaveTimeline.ActiveLocalSave != request.LocalSave || layer?.panel == null ||
            layer.Pointer != request.Layer || !Application.isFocused ||
            conversation != request.Chat || person?.PublicId != request.Player ||
            roster is not { } team || team.ManagerId != request.Manager || team.ClubId != request.Club ||
            !team.Players.Any(entry => entry.PublicId == request.Player) ||
            GameCalendar.ReadDate()?.Date != request.GameDate ||
            !GameCalendar.IsIdleSinglePlayerCareer(request.Manager, request.Club))
        {
            Plugin.Logger.LogInfo("FM Player Talk promise waiver stopped because the conversation or FM career context changed.");
            return (RecordPromiseActionFailure(signal, GameActionStatus.Expired,
                "游戏状态已变化，承诺未变更。可以在当前存档重新谈这条承诺。"), true);
        }

        var option = request.Option;
        if (FmActivePromises.Match(request.Player, new[] { option }, request.GameDate) != option)
        {
            Plugin.Logger.LogInfo("FM Player Talk promise waiver stopped because the active promise changed.");
            return (RecordPromiseActionFailure(signal, GameActionStatus.Expired,
                "FM 的进行中承诺已变化，本次未移除承诺，请重新确认。"), true);
        }

        var result = FmNativePromises.Remove(option, request.GameDate,
            () => request.Chat.TryRecordPromiseWaiver(option.Key, "我的上场位置和角色", request.GameDate,
                new GameActionReceipt(request.ReceiptId, GameActionKind.PromiseWaiver, GameActionStatus.Succeeded,
                    request.GameDate, "已核实移除原版的位置和角色承诺，球员次日来信已登记。保存 FM 存档可保留变更。", request.ClubName)));
        if (!result.Succeeded)
        {
            if (result.OutcomeUncertain)
                uncertainPromiseSession = request.Session;
            Plugin.Logger.LogWarning("FM Player Talk promise waiver: " + result.Reason);
            return (RecordPromiseActionFailure(signal,
                result.OutcomeUncertain ? GameActionStatus.Uncertain : GameActionStatus.Failed,
                result.Reason ?? "承诺变更未完成，请打开 FM 原版承诺页面核对。"), true);
        }
        Plugin.Logger.LogInfo("FM Player Talk verified an in-game promise waiver and queued the player's follow-up.");
        return ("已移除这条原版承诺，球员将在下一游戏日来信。保存 FM 存档可保留变更。", false);
    }
}
