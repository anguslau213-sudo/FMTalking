using System;
using System.Threading.Tasks;
using UnityEngine;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private const string AffinityContextChanged = "游戏状态已变化，本轮关系未写入 FM。";
    private Conversation? affinityContact;
    private int? displayedAffinity;
    private bool displayedPermanent;
    private string? affinityReadReason;
    private float nextAffinityCheck;
    private string? uncertainAffinitySession;

    private int? ReadDisplayedAffinity() => affinityContact == conversation ? displayedAffinity : null;

    private bool RefreshAffinity(bool force = false)
    {
        if (!force && affinityContact == conversation && Time.unscaledTime < nextAffinityCheck)
            return false;

        nextAffinityCheck = Time.unscaledTime + 2f;
        var previous = displayedAffinity;
        var previousReason = affinityReadReason;
        var previousContact = affinityContact;
        affinityContact = conversation;
        displayedAffinity = null;
        displayedPermanent = false;
        affinityReadReason = null;
        if (affinityContact is { UsesManagerAssistantAffinity: true, LoadWarning: null } assistant)
            displayedAffinity = assistant.Affinity;
        else if (affinityContact is { UsesManagerAssistantAffinity: false } && person?.PublicId is uint target && roster is { } team &&
            GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
        {
            var result = FmNativeAffinity.Read(team.ManagerId, target);
            if (result.Available)
            {
                displayedAffinity = result.Affinity;
                displayedPermanent = result.IsPermanent;
            }
            else
                affinityReadReason = result.Reason;
        }
        affinityContact?.ObserveAffinity(displayedAffinity);
        return previousContact != affinityContact || previous != displayedAffinity || previousReason != affinityReadReason;
    }

    private NativeAffinityTurn? CaptureAffinity(PendingReceipt signal, DateTime? date)
    {
        RefreshAffinity(force: true);
        if (conversation?.UsesManagerAssistantAffinity == true)
            return null;
        var value = ReadDisplayedAffinity();
        var permanent = conversation?.HasPermanentRelationship == true;
        var saveBlocker = NativeSaveWriteBlocker();
        if (person?.PublicId is not uint target || roster is not { } team ||
            date is not DateTime today || saveSessionDirectory is not { } session ||
            GameSaveTimeline.ActiveLocalSave is not { } save || saveBlocker != null)
        {
            return new(value, (_, _, _, _) => Task.FromResult<string?>(saveBlocker ?? "本轮无法核实人物关系。"), displayedPermanent);
        }

        return new(value, (before, after, blocked, commit) => signal.Affinity.Queue(() =>
        {
            if (GameSaveTimeline.DirectoryPath != session || GameSaveTimeline.ActiveLocalSave != save ||
                !GameSaveTimeline.CanChangeGameData || GameCalendar.ReadDate()?.Date != today.Date ||
                !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
                return AffinityContextChanged;

            return WriteAffinity(session, team.ManagerId, target, before, after, blocked, commit, permanent && !blocked && after > 0);
        }), displayedPermanent);
    }

    private string? SynchronizePermanentAffinity(int affinity, Func<bool> commit)
    {
        if (affinity <= 0 || saveSessionDirectory is not { } session || GameSaveTimeline.DirectoryPath != session ||
            !GameSaveTimeline.CanChangeGameData || person?.PublicId is not uint target || roster is not { } team ||
            !GameCalendar.IsIdleSinglePlayerCareer(team.ManagerId, team.ClubId))
            return "关系已确认，FM 的「持续性」暂未同步。";

        return WriteAffinity(session, team.ManagerId, target, affinity, affinity, false, commit, permanentLike: true);
    }

    private string? WriteAffinity(string session, uint manager, uint target, int before, int after, bool blocked,
        Func<bool> commit, bool permanentLike)
    {
        if (uncertainAffinitySession == session)
            return "此前的原版亲密度写入结果未确认，请重新载入修改前的 FM 存档。";

        var result = FmNativeAffinity.Apply(manager, target, before, after, blocked, commit, permanentLike);
        if (!result.Succeeded)
        {
            if (result.OutcomeUncertain)
                uncertainAffinitySession = session;
            Plugin.Logger.LogWarning("FM Player Talk affinity write: " + result.Reason);
            return result.Reason ?? "FM 未确认本轮亲密度变更。";
        }
        Plugin.Logger.LogInfo(permanentLike ? "FM Player Talk verified a permanent in-game relationship." :
            "FM Player Talk verified an in-game affinity update.");
        return null;
    }

    private sealed class PendingAffinity
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource<string?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<string?>? pendingWrite;

        internal Task<string?> Queue(Func<string?> write)
        {
            lock (gate)
            {
                if (!completion.Task.IsCompleted)
                    pendingWrite = write;
                return completion.Task;
            }
        }

        internal void Tick()
        {
            lock (gate)
            {
                if (pendingWrite is not { } write)
                    return;
                pendingWrite = null;
                try
                {
                    completion.TrySetResult(write());
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }
        }

        internal void Cancel()
        {
            lock (gate)
            {
                pendingWrite = null;
                completion.TrySetResult(AffinityContextChanged);
            }
        }
    }
}
