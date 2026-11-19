using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FM.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using UnityEngine;
using DateTime = System.DateTime;

namespace FmPlayerTalk;

internal static class ManagerAssistantContact
{
    private static (string? Session, uint? Manager, uint? Club) scope;
    private static GameSaveTimeline.LocalSave? requested;
    private static bool searchedCandidates;
    private static bool requireLiveIdentity;
    private static Task<ManagerAssistantRecord?>? reading;
    private static CancellationTokenSource? cancellation;
    private static NativePerson? native;
    private static FmNativeManagerContact.Contact? linked;
    private static float nextLiveCheck;

    internal static DirectoryPerson? Current { get; private set; }

    // Save parsing runs in the background. Native display fields are bound on Unity's thread.
    internal static bool Refresh(LiveRoster? roster)
    {
        var previous = Current;
        var managerId = roster?.ManagerId;
        var active = (GameSaveTimeline.DirectoryPath, managerId, roster?.ClubId);
        if (scope != active)
        {
            AbandonRequest();
            scope = active;
            requested = null;
            searchedCandidates = false;
            linked = null;
            nextLiveCheck = 0;
            Current = null;
        }
        if (active.Item1 == null || managerId is not > 0)
            return previous != Current;

        if (FmNativePromises.ModuleBase() != 0 && FmNativeLayout.Current.ManagerContactOffset != 0)
        {
            RefreshLiveContact(managerId.Value);
            FinishNativeMetadata(managerId.Value);
            return previous != Current;
        }

        var save = GameSaveTimeline.ActiveLocalSave;
        if (save != null && requested != save)
        {
            AbandonRequest();
            requested = save;
            requireLiveIdentity = false;
            cancellation = new CancellationTokenSource();
            reading = ManagerAssistantSaveReader.ReadAsync(save.Path, managerId.Value, cancellation.Token);
        }
        else if (save == null && !searchedCandidates && roster is { ClubId: > 0 } &&
            GameSaveTimeline.LoadedWorldDate is DateTime date)
        {
            searchedCandidates = true;
            requireLiveIdentity = true;
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            reading = ManagerAssistantSaveReader.ReadMatchingAsync(GameSaveTimeline.SaveSearchDirectories(),
                managerId.Value, roster.ClubId, date, cancellation.Token);
        }
        if (reading is { IsCompleted: true } complete)
        {
            reading = null;
            cancellation?.Dispose();
            cancellation = null;
            if (complete.IsCompletedSuccessfully && complete.Result is { } record)
            {
                native = NativePerson.Start(record);
                if (native == null && !requireLiveIdentity)
                    Current = ToDirectory(managerId.Value, record, null);
                Plugin.Logger.LogInfo(requireLiveIdentity
                    ? "FM Player Talk found a matching manager contact candidate."
                    : "FM Player Talk verified a manager contact from the active save.");
            }
            else if (complete.IsFaulted)
            {
                Plugin.Logger.LogInfo($"FM Player Talk manager contact read unavailable ({complete.Exception?.GetBaseException().GetType().Name}).");
            }
            else
                Plugin.Logger.LogInfo("FM Player Talk manager contact: no matching record was available.");
        }
        FinishNativeMetadata(managerId.Value);
        return previous != Current;
    }

    private static void RefreshLiveContact(uint managerId)
    {
        if (Time.unscaledTime < nextLiveCheck || GameCalendar.ReadDate() is not DateTime date)
            return;
        nextLiveCheck = Time.unscaledTime + 2f;
        var found = FmNativeManagerContact.Read(managerId, date);
        if (found == null)
        {
            if (linked != null && !FmNativeManagerContact.IsCurrent(linked, managerId))
            {
                AbandonRequest();
                linked = null;
                Current = null;
            }
            return;
        }
        var changed = linked == null || found.Record.PublicId != linked.Record.PublicId ||
            found.Record.PersonIndex != linked.Record.PersonIndex || found.Record.SaveDate != linked.Record.SaveDate;
        linked = found;
        if (!changed)
            return;
        AbandonRequest();
        requireLiveIdentity = false; // The manager reference and native pool already verified this identity.
        Current = ToDirectory(managerId, found.Record, null);
        native = NativePerson.Start(found.Record);
        Plugin.Logger.LogInfo("FM Player Talk verified the manager-owned contact from the loaded world.");
    }

    private static void FinishNativeMetadata(uint managerId)
    {
        if (native is { } probe && probe.TryFinish(out var metadata))
        {
            native = null;
            if (linked != null && !FmNativeManagerContact.IsCurrent(linked, managerId))
            {
                Current = null;
                linked = null;
                return;
            }
            if (metadata != null || !requireLiveIdentity)
                Current = ToDirectory(managerId, probe.Record, metadata);
            if (requireLiveIdentity)
                Plugin.Logger.LogInfo(metadata != null ? "FM Player Talk verified the matching contact against the loaded world."
                    : "FM Player Talk could not verify that candidate against the loaded world.");
        }
    }

    private static void AbandonRequest()
    {
        native?.Dispose();
        native = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        if (reading != null)
            _ = reading.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        reading = null;
    }

    private static DirectoryPerson ToDirectory(uint managerId, ManagerAssistantRecord record, NativeMetadata? native)
    {
        var role = native?.Role ?? "经理专属联络人";
        var name = native?.Name ?? record.Name;
        var date = record.SaveDate;
        var birthday = record.DateOfBirth;
        var years = date.Year - birthday.Year;
        if (date < birthday.AddYears(years))
            years--;
        var age = native?.Age ?? (years is >= 10 and <= 125 ? years : (int?)null);
        var actorId = ClubDirectory.UsesLegacyAssistantKey(managerId, record.PublicId, birthday)
            ? ClubDirectory.LegacyAssistantActorId : record.PublicId.ToString(CultureInfo.InvariantCulture);
        var profile = new PlayerProfile(actorId, name, "当前经理的专属联络人。")
        {
            Age = age,
            IsFemale = native?.IsFemale,
            IsAssignedManagerAssistant = true,
            RoleInCareer = "主教练 · " + role,
            DialogueStyle = "自然地谈论已知的经理事务、俱乐部通知和赛事安排。"
        };
        return new DirectoryPerson(name, FmDisplayText.Initials(name), ClubDirectory.AssistantGroup, role,
            record.PublicId, profile, NativeDisplayName: true, Age: age, IsFemale: native?.IsFemale);
    }

    private sealed record NativeMetadata(string? Name, int? Age, bool? IsFemale, string? Role);

    private sealed class NativePerson : IDisposable
    {
        private const string Root = "fmPlayerTalkManagerContact";
        private static readonly string[] Paths =
        {
            Root + ".UniqueId", Root + ".Name", Root + ".Age", Root + ".IsPersonFemale",
            Root + ".Regenerated", Root + ".NewgenAppearanceDescription", "game.CurrentGameDay",
            Root + ".JobString", Root + ".PrimaryClubJobRecord.JobString"
        };
        private readonly BindingSubsystem bindings;
        private readonly Bindings.ValueChangedCallback changed;
        private readonly List<Bindings.Key?> fields = new();
        private Bindings.Key? scratch;
        private readonly float deadline = Time.unscaledTime + 2f;

        internal ManagerAssistantRecord Record { get; }

        private NativePerson(BindingSubsystem bindings, ManagerAssistantRecord record)
        {
            this.bindings = bindings;
            Record = record;
            changed = (Action<Bindings.Key, TypedValue>)((_, _) => { });
        }

        internal static NativePerson? Start(ManagerAssistantRecord record)
        {
            if (LiveClubRoster.FindBindings() is not { } bindings)
                return null;
            var probe = new NativePerson(bindings, record);
            try
            {
                var root = Span(Root);
                var person = new PersonReference(record.PersonIndex);
                probe.scratch = bindings.Set(ref root, TypedValue.Create(person),
                    Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default);
                foreach (var path in Paths)
                    probe.Bind(path);
                return probe;
            }
            catch (Exception)
            {
                probe.Dispose();
                return null;
            }
        }

        internal bool TryFinish(out NativeMetadata? result)
        {
            result = null;
            try
            {
                if (Time.unscaledTime < deadline && fields.Exists(key => key is { } value && !bindings.IsDataSet(value)))
                    return false;
                if (LiveClubRoster.PublicId(Read(0)) is uint id && id == Record.PublicId)
                {
                    var name = Read(1);
                    var female = Read(3);
                    result = new NativeMetadata(name is { IsNull: false } ? FmDisplayText.Read(name.AsString()) : null,
                        LiveClubRoster.ReadAge(Read(2)), LiveClubRoster.ReadFemale(female), ReadRole(7) ?? ReadRole(8));
                    PlayerPortraits.RememberNativePortrait(PlayerPortraits.ReadInboxPortrait(id, Read(4), female,
                        Read(5), Read(6)));
                }
            }
            catch (Exception) { }
            Dispose();
            return true;
        }

        private void Bind(string path)
        {
            try
            {
                var span = Span(path);
                fields.Add(bindings.Bind(ref span, bindings, changed));
            }
            catch (Exception) { fields.Add(null); }
        }

        private TypedValue? Read(int index) =>
            fields[index] is { } key && bindings.IsDataSet(key) ? bindings.Get(ref key) : null;

        private string? ReadRole(int index)
        {
            if (Read(index) is not { IsNull: false } value || value.DataType?.Name != "String")
                return null;
            var role = FmDisplayText.Read(value.AsString());
            return role is { Length: > 0 and <= 100 } && role is not ("职员" or "消息发件人" or "-" or "—") ? role : null;
        }

        private static Il2CppSystem.ReadOnlySpan<char> Span(string value) =>
            new(new Il2CppStructArray<char>(value.ToCharArray()));

        public void Dispose()
        {
            foreach (var field in fields)
            {
                if (field is not { } key)
                    continue;
                try { bindings.Unbind(ref key, changed); }
                catch (Exception) { }
            }
            fields.Clear();
            if (scratch is not { } slot)
                return;
            scratch = null;
            try { bindings.Remove(ref slot); }
            catch (Exception) { }
        }
    }
}
