using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FM.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using SI.Interop;
using SI.Translation;
using UnityEngine;
using UnityEngine.UIElements;
using NativeCollection = Il2CppSystem.Collections.ICollection;
using NativeList = Il2CppSystem.Collections.IList;

namespace FmPlayerTalk;

internal sealed record LiveRoster(uint ManagerId, uint ClubId, string ClubName, DirectoryPerson[] Players, DirectoryPerson[] Staff)
{
    internal uint PrimaryTeamId { get; init; }
    internal uint[] ConfirmedDepartures { get; init; } = Array.Empty<uint>();
    internal uint[] ConfirmedStaffDepartures { get; init; } = Array.Empty<uint>();
    internal uint[] VirtualPlayers { get; init; } = Array.Empty<uint>();
    internal bool IsPartial { get; init; }
    // The squad lists can omit out-on-loan or otherwise unavailable people.
    internal bool CompleteMembership => false;
}

internal static class LiveClubRoster
{
    internal const string BoardGroup = "俱乐部高层";
    internal const string MedicalGroup = "医疗组";
    private const string GenericStaffRole = "职员";
    private const string Manager = "game.GameCurrentManager";
    private const string Club = Manager + ".Club";
    private const string PrimaryTeam = "game.GameCurrentManagerClubTeam";
    private const string MedicalPeople = ".StaffMedicalSubGroup.StaffSubGroupPersonList";
    private const string StaffTablePeople = ".AllStaffFilteredByTeam";
    private const string StaffViewPeople = ".StaffAllListFiltered";
    private const float SupplementWait = 1.25f;
    private const float RequestTimeout = 6f;
    private const int TeamLimit = 18;
    private const int TeamMemberLimit = 240;
    private const int PeopleBatchSize = 16;
    private const int KnownReferenceLimit = 1200;
    private const int BoardMemberLimit = 40;
    private const int MedicalStaffLimit = 48;
    private const int StaffContextLimit = 96;
    private static readonly string[] StaffJobPaths = { ".ContextJob.JobString", ".PrimaryClubJobRecord.JobString", ".JobString" };
    private static readonly string[] IdentityPaths = { Manager + ".UniqueId", Club + ".UniqueId", PrimaryTeam + ".UniqueId" };
    private static readonly string[] Roots = IdentityPaths.Concat(new[]
    {
        Club + ".Name", PrimaryTeam + ".Club.UniqueId", Manager + ".IsAtTeam",
        PrimaryTeam + ".Name", PrimaryTeam + ".Players", PrimaryTeam + ".StaffAllList", Club + ".Teams"
    }).ToArray();
    private static readonly string[] ExtraStaffRoots =
    {
        Club + ".BoardMembers", Club + ".BoardSpokesperson", PrimaryTeam + MedicalPeople,
        PrimaryTeam + StaffTablePeople, PrimaryTeam + StaffViewPeople
    };
    private static readonly string[] SquadRoots =
    {
        Manager + ".Team.teamcontainer.HighestLevelYouthTeam",
        Manager + ".Team.teamcontainer.SecondHighestLevelYouthTeam",
        Manager + ".Team.teamcontainer.YouthTeam3",
        Club + ".ReservesTeam"
    };

    private static LiveRead? pending;
    private static readonly Dictionary<uint, KnownRosterPerson> KnownPeople = new();
    private static readonly Dictionary<uint, DirectoryPerson[]> KnownSquads = new();
    private static readonly Dictionary<uint, string> KnownStaffRoles = new();
    private static (string? Career, uint Manager, uint Club, uint PrimaryTeam)? knownClub;
    private static int nextDepartureIndex;
    private static StaffRoleScope? knownStaffRoleScope;
    private static (int Concrete, int Total, string Sources)? lastStaffRoleCoverage;
    private static float nextAttempt;
    private static string? lastFailure;
    private static string? lastSupplementFailure;
    private static string? lastSquadSummary;

    // Read on Unity's update thread. A pending pass returns null until all required data arrive.
    internal static LiveRoster? Read()
    {
        if (!GameUi.HasActiveCareerEntry())
        {
            Reset();
            return null;
        }
        var now = Time.unscaledTime;
        try
        {
            if (pending is null)
            {
                if (now < nextAttempt || FindBindings() is not { } bindings)
                    return null;
                pending = new LiveRead(bindings, now);
            }
            if (pending.Advance(now) is not { } result)
                return null;
            ClosePending();
            lastFailure = null;
            return result;
        }
        catch (Exception error)
        {
            var failure = error is ReadUnavailable
                ? error.Message : $"{pending?.Stage.ToString() ?? "initializing"} native binding ({error.GetType().Name})";
            if (failure != lastFailure)
            {
                Plugin.Logger.LogInfo($"FM Player Talk live roster waiting: {failure}.");
                lastFailure = failure;
            }
            ClosePending();
            nextAttempt = now + 3f;
            return null;
        }
    }

    internal static void Reset()
    {
        ClosePending();
        KnownPeople.Clear();
        KnownSquads.Clear();
        knownClub = null;
        nextDepartureIndex = 0;
        ResetStaffRoles();
        nextAttempt = 0;
        lastFailure = null;
        lastSupplementFailure = null;
        lastSquadSummary = null;
    }

    private sealed record StaffRoleScope(string Career, uint Manager, uint Club, string Language);

    private static void ResetStaffRoles()
    {
        KnownStaffRoles.Clear();
        knownStaffRoleScope = null;
        lastStaffRoleCoverage = null;
    }

    private static string StaffRoleLanguage()
    {
        try { return TranslationManager.CurrentLanguage ?? ""; }
        catch (Exception) { return ""; }
    }

    private static DirectoryPerson[] RestoreStaffRoles(DirectoryPerson[] entries, string? career,
        uint manager, uint club, string language)
    {
        if (career == null)
            return entries;
        var scope = new StaffRoleScope(career, manager, club, language);
        if (scope != knownStaffRoleScope)
        {
            ResetStaffRoles();
            knownStaffRoleScope = scope;
        }
        var current = new HashSet<uint>();
        for (var index = 0; index < entries.Length; index++)
        {
            var person = entries[index];
            if (person.PublicId is not { } id)
                continue;
            current.Add(id);
            if (IsConcreteStaffRole(person.Role))
                KnownStaffRoles[id] = person.Role;
            else if (KnownStaffRoles.TryGetValue(id, out var previous))
                entries[index] = person with { Role = previous };
        }
        if (KnownStaffRoles.Count > KnownReferenceLimit)
            foreach (var id in KnownStaffRoles.Keys.Where(id => !current.Contains(id))
                         .Take(KnownStaffRoles.Count - KnownReferenceLimit).ToArray())
                KnownStaffRoles.Remove(id);
        return entries;
    }

    private static bool IsConcreteStaffRole(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text is not (GenericStaffRole or "俱乐部职员" or "工作人员" or "岗位未细分" or "-" or "—") &&
        !text.Equals("staff", StringComparison.OrdinalIgnoreCase) &&
        !text.Equals("club staff", StringComparison.OrdinalIgnoreCase) &&
        !text.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static void ClosePending()
    {
        var read = pending;
        pending = null;
        read?.Release();
    }

    internal static BindingSubsystem? FindBindings()
    {
        var subsystems = GameSubsystems.s_subsystems;
        if (subsystems != null)
        {
            for (var i = 0; i < subsystems.Count; i++)
                if (subsystems[i]?.TryCast<BindingSubsystem>() is { } bindings)
                    return bindings;
        }
        return GameSubsystems.GetOptional<BindingSubsystem>();
    }

    private static Il2CppSystem.ReadOnlySpan<char> Span(string path) => new(new Il2CppStructArray<char>(path.ToCharArray()));

    internal static uint? PublicId(TypedValue? value)
    {
        if (value is not { IsNull: false })
            return null;
        if (uint.TryParse(value.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return id is > 0 and < uint.MaxValue ? id : null;
        if (value.Get() is not { } boxed)
            return null;
        return value.DataType?.Name switch
        {
            "UInt32" when boxed.Unbox<uint>() is > 0 and < uint.MaxValue => boxed.Unbox<uint>(),
            "Int32" when boxed.Unbox<int>() is > 0 => (uint)boxed.Unbox<int>(),
            _ => null
        };
    }

    private static uint RequireId(TypedValue? value, string subject) =>
        PublicId(value) ?? throw new ReadUnavailable(subject);

    private static string? ReadText(TypedValue? value) =>
        value is { IsNull: false } ? FmDisplayText.Read(value.AsString()) : null;

    internal static int? ReadAge(TypedValue? value)
    {
        try
        {
            return value is { IsNull: false } &&
                int.TryParse(value.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var age) &&
                age is >= 10 and <= 125 ? age : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool? ReadFemale(TypedValue? value) => ReadBoolean(value);

    internal static bool? ReadBoolean(TypedValue? value)
    {
        try
        {
            return value is { IsNull: false, DataType.Name: "Boolean" } && value.Get() is { } boxed
                ? boxed.Unbox<bool>() : null;
        }
        catch (Exception) { return null; }
    }

    private static string RequireText(TypedValue? value, string subject) =>
        ReadText(value) is { Length: > 0 } text ? text : throw new ReadUnavailable(subject);

    private static RowReference? ReadReference(Il2CppSystem.Object? item)
    {
        var reference = item?.TryCast<TypedValue>() is { IsNull: false } wrapped ? wrapped.Get() : item;
        var actual = reference?.TryCast<PersonReference>() as Il2CppSystem.Object ??
                     reference?.TryCast<TeamReference>() as Il2CppSystem.Object ??
                     reference?.TryCast<InteropReference>() as Il2CppSystem.Object ??
                     reference?.TryCast<DynamicReference>();
        return actual == null ? null : new RowReference(actual);
    }

    private static RowReference[]? ListValues(TypedValue? value, int limit, bool skipUnresolved = false)
    {
        var boxed = value is { IsNull: false } ? value.Get() : null;
        return ListValues(boxed?.TryCast<NativeList>(), limit, skipUnresolved);
    }

    private static RowReference[]? ListValues(NativeList? list, int limit, bool skipUnresolved)
    {
        if (list?.TryCast<NativeCollection>() is not { Count: >= 0 } collection || collection.Count > limit)
            return null;
        var count = collection.Count;
        var items = new List<RowReference>(count);
        for (var i = 0; i < count; i++)
        {
            if (ReadReference(list[i]) is { } item)
                items.Add(item);
            else if (!skipUnresolved)
                return null;
        }
        return items.ToArray();
    }

    private static string TypeDescription(TypedValue? value) => value?.DataType?.Name ?? "unavailable";

    private sealed class ReadUnavailable : Exception
    {
        internal ReadUnavailable(string subject) : base(subject) { }
    }
    private enum Phase { Roots, Teams, People, Departures, Verify }
    // Keep the referenced game object independently of the group's pooled TypedValue wrapper.
    private sealed record RowReference(Il2CppSystem.Object Object)
    {
        internal TypedValue NewValue() => Object switch
        {
            PersonReference person => TypedValue.Create(person),
            TeamReference team => TypedValue.Create(team),
            InteropReference interop => TypedValue.Create(interop),
            DynamicReference dynamic => TypedValue.Create(dynamic),
            _ => throw new ReadUnavailable("unsupported row reference")
        };
    }

    private sealed record KnownRosterPerson(RowReference Reference, bool Staff, bool FromOwnedTeam);

    private sealed record SquadReference(RowReference Value, bool NativeSquad);

    private enum StaffSource { Team, Medical, Board }

    private sealed record PersonValue(RowReference Value, string Team, bool Staff, bool IsPrimary,
        StaffSource Source = StaffSource.Team, bool RoleOnly = false, uint SquadId = 0, bool FromOwnedTeam = true)
    {
        internal bool Optional => Source != StaffSource.Team || RoleOnly;
    }

    // GroupChangedCallback takes FM's Group struct, which IL2CPP cannot marshal
    // to a managed callback. Subscribe to the individual paths instead.
    private sealed class BoundValues : IDisposable
    {
        private readonly BindingSubsystem bindings;
        private readonly List<Bindings.Key?> keys = new();
        private readonly HashSet<Bindings.Key> received = new();
        private readonly Bindings.ValueChangedCallback changed;
        private readonly int requiredCount;

        internal BoundValues(BindingSubsystem bindings, IReadOnlyList<string?> paths, int requiredCount, int optionalCount,
            Phase stage)
        {
            this.bindings = bindings;
            this.requiredCount = requiredCount;
            this.optionalCount = optionalCount;
            try
            {
                changed = (Action<Bindings.Key, TypedValue>)OnChanged;
            }
            catch (Exception error)
            {
                throw new ReadUnavailable($"{stage} value callback ({error.GetType().Name})");
            }

            try
            {
                foreach (var path in paths)
                {
                    if (path == null)
                    {
                        keys.Add(null);
                        continue;
                    }
                    var nativePath = Span(path);
                    try { keys.Add(bindings.Bind(ref nativePath, bindings, changed)); }
                    catch (Exception) when (keys.Count >= requiredCount) { keys.Add(null); }
                }
            }
            catch (Exception error)
            {
                var index = keys.Count;
                Dispose();
                throw new ReadUnavailable($"{stage} binding #{index + 1} ({error.GetType().Name})");
            }
        }

        private void OnChanged(Bindings.Key key, TypedValue value) => received.Add(key);

        private bool IsSet(Bindings.Key? key) => key is not { } slot || received.Contains(slot) || bindings.IsDataSet(slot);
        internal bool IsDone => keys.Take(requiredCount).All(IsSet);
        private readonly int optionalCount;
        internal bool ExtraIsDone => keys.Skip(requiredCount).Take(optionalCount).All(ExtraIsSet);

        private bool ExtraIsSet(Bindings.Key? key)
        {
            try { return IsSet(key); }
            catch (Exception) { return true; }
        }

        internal TypedValue? Get(int index)
        {
            if (keys[index] is not { } key)
                return null;
            return bindings.Get(ref key);
        }

        internal TypedValue? GetExtra(int index)
        {
            try { return IsSet(keys[index]) ? Get(index) : null; }
            catch (Exception) { return null; }
        }

        public void Dispose()
        {
            foreach (var key in keys)
            {
                if (key is not { } slot)
                    continue;
                try
                {
                    bindings.Unbind(ref slot, changed);
                }
                catch (Exception)
                {
                    // A save transition can destroy the binding tree before a poll.
                }
            }
            keys.Clear();
            received.Clear();
        }
    }

    private sealed class LiveRead
    {
        private const int PersonFieldCount = 4;
        private readonly BindingSubsystem bindings;
        private readonly string? careerSession;
        private readonly List<Bindings.Key> scratch = new();
        private readonly Dictionary<uint, DirectoryPerson> players = new();
        private readonly Dictionary<uint, uint> playerSquads = new();
        private readonly HashSet<uint> listedSquads = new();
        private readonly HashSet<uint> readSquads = new();
        private readonly HashSet<uint> managedSquadOwners = new();
        private readonly Dictionary<uint, (DirectoryPerson Person, StaffSource Source)> staff = new();
        private readonly Dictionary<uint, string> contextualStaffRoles = new();
        private readonly Dictionary<uint, KnownRosterPerson> references = new();
        private readonly List<PersonValue> work = new();
        private readonly List<PersonValue> supplement = new();
        private readonly List<PersonValue> staffContext = new();
        private readonly HashSet<string> staffJobValueTypes = new(StringComparer.Ordinal);
        private readonly List<uint> departures = new();
        private readonly List<uint> staffDepartures = new();
        private readonly HashSet<uint> virtualPlayers = new();
        private BoundValues? group;
        private Phase phase;
        private float requestDeadline;
        private float supplementDeadline;
        private uint managerId, clubId, primaryId;
        private bool partial;
        private bool squadCatalogComplete;
        private string clubName = "";
        private int teamCount, offset, batchCount;
        private SquadReference[] squads = Array.Empty<SquadReference>();
        private KeyValuePair<uint, KnownRosterPerson>[] missing = Array.Empty<KeyValuePair<uint, KnownRosterPerson>>();
        private int departureIndex;

        private (string? Career, uint Manager, uint Club, uint PrimaryTeam) ClubScope => (careerSession, managerId, clubId, primaryId);

        internal LiveRead(BindingSubsystem bindings, float now)
        {
            this.bindings = bindings;
            careerSession = GameSaveTimeline.DirectoryPath;
            OpenGroup(Phase.Roots, Roots.Concat(ExtraStaffRoots).Concat(SquadRoots), now, Roots.Length);
        }

        internal Phase Stage => phase;

        internal LiveRoster? Advance(float now)
        {
            var result = group ?? throw new ReadUnavailable("group unavailable");
            if (!result.IsDone)
            {
                if (now <= requestDeadline)
                    return null;
                if (phase is Phase.Roots or Phase.Teams or Phase.People)
                    partial = true;
                else
                    throw new ReadUnavailable(phase + " timed out");
            }
            else if (now < supplementDeadline && !result.ExtraIsDone)
                return null;
            switch (phase)
            {
                case Phase.Roots:
                    managerId = RequireId(result.Get(0), "manager ID");
                    if (PublicId(result.Get(1)) is null && PublicId(result.Get(2)) is null &&
                        result.Get(5) is { IsNull: false, DataType.Name: "Boolean" } atTeam &&
                        atTeam.Get() is { } employed && !employed.Unbox<bool>())
                    {
                        KnownPeople.Clear();
                        KnownSquads.Clear();
                        knownClub = null;
                        nextDepartureIndex = 0;
                        ResetStaffRoles();
                        return new LiveRoster(managerId, 0, "", Array.Empty<DirectoryPerson>(), Array.Empty<DirectoryPerson>());
                    }
                    clubId = RequireId(result.Get(1), "club ID");
                    primaryId = RequireId(result.Get(2), "managed team ID");
                    clubName = RequireText(result.Get(3), "club name");
                    if (PublicId(result.Get(4)) is uint teamOwner && teamOwner != clubId)
                        throw new ReadUnavailable("managed team and club mismatch");
                    var primaryName = ReadText(result.Get(6));
                    if (string.IsNullOrWhiteSpace(primaryName) || primaryName == clubName)
                        primaryName = clubName + "一线队";
                    var playerSource = result.Get(7);
                    var primaryPlayers = ListValues(playerSource, TeamMemberLimit);
                    if (primaryPlayers is not { Length: > 0 })
                        throw new ReadUnavailable("main team players (" + TypeDescription(playerSource) + ")");
                    var primaryStaff = ListValues(result.Get(8), TeamMemberLimit);
                    if (primaryStaff is null)
                        partial = true;
                    work.AddRange(primaryPlayers.Select(value => new PersonValue(value, primaryName, false, true)));
                    if (primaryStaff is not null)
                        work.AddRange(primaryStaff.Select(value => new PersonValue(value, primaryName, true, true)));
                    AddStaffList(result, Roots.Length, StaffSource.Board, BoardMemberLimit);
                    AddSpokesperson(result, Roots.Length + 1);
                    AddStaffList(result, Roots.Length + 2, StaffSource.Medical, MedicalStaffLimit);
                    AddStaffContext(result, Roots.Length + 3, Roots.Length + 4, primaryName, primary: true);
                    AddVisibleStaffTable(primaryName);
                    var teams = ListValues(result.Get(9), TeamLimit);
                    CollectSquads(result, teams);
                    EndGroup();
                    if (squads.Length > 0)
                    {
                        teamCount = squads.Length;
                        var teamRoots = squads.Select((squad, index) => SetScratch(index, squad.Value)).ToArray();
                        var teamFields = new[] { ".UniqueId", ".Club.UniqueId", ".Name", ".Players", ".StaffAllList" };
                        OpenGroup(Phase.Teams, teamRoots.SelectMany(root => teamFields.Select(field => root + field))
                            .Concat(teamRoots.Select(root => root + MedicalPeople))
                            .Concat(teamRoots.Select(root => root + StaffTablePeople))
                            .Concat(teamRoots.Select(root => root + StaffViewPeople))
                            .Concat(teamRoots.Select(root => root + ".ControlledByContextHuman")), now, teamCount * teamFields.Length);
                    }
                    else
                    {
                        partial |= !squadCatalogComplete;
                        StartPeople(now);
                    }
                    return null;

                case Phase.Teams:
                    ReadTeams(result);
                    EndGroup();
                    StartPeople(now);
                    return null;

                case Phase.People:
                    ReadPeople(result);
                    offset += batchCount;
                    EndGroup();
                    NextBatch(now);
                    return null;

                case Phase.Departures:
                    for (var i = 0; i < batchCount; i++)
                    {
                        var person = missing[offset + i];
                        if (PublicId(result.GetExtra(i)) != person.Key)
                            continue;
                        var isVirtual = person.Value.Staff ? false : ReadBoolean(result.GetExtra(batchCount * 2 + i));
                        if (isVirtual == true)
                            virtualPlayers.Add(person.Key);
                        if (isVirtual != false || PublicId(result.GetExtra(batchCount + i)) is not { } owner ||
                            owner == clubId || managedSquadOwners.Contains(owner))
                            continue;
                        (person.Value.Staff ? staffDepartures : departures).Add(person.Key);
                    }
                    offset += batchCount;
                    EndGroup();
                    OpenDepartureBatch(now);
                    return null;

                case Phase.Verify:
                    if (careerSession != GameSaveTimeline.DirectoryPath ||
                        PublicId(result.Get(0)) != managerId || PublicId(result.Get(1)) != clubId ||
                        PublicId(result.Get(2)) != primaryId)
                        throw new ReadUnavailable("the active career or club changed during the read");
                    if (knownClub != ClubScope)
                    {
                        KnownPeople.Clear();
                        KnownSquads.Clear();
                    }
                    knownClub = ClubScope;
                    RecordSquads();
                    var summary = string.Join(", ", players.Values.GroupBy(person => person.Team)
                        .Select(team => $"{team.Key}: {team.Count()}"));
                    if (summary != lastSquadSummary)
                    {
                        lastSquadSummary = summary;
                        Plugin.Logger.LogInfo($"FM Player Talk player squads: {summary}.");
                    }
                    nextDepartureIndex = departureIndex;
                    foreach (var person in references)
                        KnownPeople[person.Key] = person.Value;
                    foreach (var id in departures.Concat(staffDepartures).Concat(virtualPlayers))
                        KnownPeople.Remove(id);
                    if (KnownPeople.Count > KnownReferenceLimit)
                    {
                        foreach (var id in KnownPeople.Keys.Where(id => !references.ContainsKey(id))
                                     .Take(KnownPeople.Count - KnownReferenceLimit).ToArray())
                            KnownPeople.Remove(id);
                    }
                    var staffEntries = RestoreStaffRoles(staff.Values.Select(value => value.Person).Select(person =>
                        !IsConcreteStaffRole(person.Role) && person.PublicId is { } id &&
                        contextualStaffRoles.TryGetValue(id, out var role) ? person with { Role = role } : person).ToArray(),
                        careerSession, managerId, clubId, StaffRoleLanguage());
                    var roleCoverage = (Concrete: staffEntries.Count(entry => IsConcreteStaffRole(entry.Role)),
                        Total: staffEntries.Length,
                        Sources: string.Join(", ", staffJobValueTypes.OrderBy(type => type, StringComparer.Ordinal)));
                    if (roleCoverage != lastStaffRoleCoverage)
                    {
                        lastStaffRoleCoverage = roleCoverage;
                        Plugin.Logger.LogInfo($"FM Player Talk staff duties: {roleCoverage.Concrete}/{roleCoverage.Total} concrete roles.");
                        Plugin.Logger.LogInfo($"FM Player Talk staff duty binding types: {(roleCoverage.Sources.Length == 0 ? "unavailable" : roleCoverage.Sources)}.");
                    }
                    return new LiveRoster(managerId, clubId, clubName, players.Values.ToArray(), staffEntries)
                    {
                        PrimaryTeamId = primaryId,
                        ConfirmedDepartures = departures.ToArray(),
                        ConfirmedStaffDepartures = staffDepartures.ToArray(),
                        VirtualPlayers = virtualPlayers.ToArray(),
                        IsPartial = partial
                    };
                default:
                    throw new ReadUnavailable("unknown stage");
            }
        }

        private void CollectSquads(BoundValues data, RowReference[]? clubTeams)
        {
            var nativeSquads = new List<SquadReference>(SquadRoots.Length);
            squadCatalogComplete = clubTeams != null;
            for (var index = 0; index < SquadRoots.Length; index++)
            {
                var value = data.GetExtra(Roots.Length + ExtraStaffRoots.Length + index);
                if (value is { IsNull: true })
                    continue;
                if (value is { IsNull: false } && ReadReference(value.Get()) is { } team)
                    nativeSquads.Add(new SquadReference(team, NativeSquad: true));
                else
                    squadCatalogComplete = false;
            }
            // Prefer the explicit Squad menu scopes when both native sources mention the same team.
            squads = nativeSquads.Concat((clubTeams ?? Array.Empty<RowReference>())
                    .Select(team => new SquadReference(team, NativeSquad: false)))
                .DistinctBy(team => team.Value.Object.Pointer).ToArray();
        }

        private void AddStaffList(BoundValues data, int index, StaffSource source, int limit, bool fromOwnedTeam = true)
        {
            try
            {
                if (ListValues(data.GetExtra(index), limit, skipUnresolved: true) is { } rows)
                    foreach (var row in rows)
                        AddStaffReference(row, source, fromOwnedTeam);
            }
            catch (Exception error) { LogSupplementFailure(source, error); }
        }

        private void AddSpokesperson(BoundValues data, int index)
        {
            try
            {
                var value = data.GetExtra(index);
                if (value is { IsNull: false } && ReadReference(value.Get()) is { } representative)
                    AddStaffReference(representative, StaffSource.Board);
            }
            catch (Exception error) { LogSupplementFailure(StaffSource.Board, error); }
        }

        private static void LogSupplementFailure(StaffSource source, Exception error)
        {
            var failure = $"{source} ({error.GetType().Name})";
            if (failure == lastSupplementFailure)
                return;
            lastSupplementFailure = failure;
            Plugin.Logger.LogInfo($"FM Player Talk optional department unavailable: {failure}.");
        }

        private void AddStaffReference(RowReference reference, StaffSource source, bool fromOwnedTeam = true)
        {
            var groupName = source == StaffSource.Board ? BoardGroup : MedicalGroup;
            supplement.Add(new PersonValue(reference, groupName, true, false, source, FromOwnedTeam: fromOwnedTeam));
        }

        private void AddStaffContext(BoundValues data, int tableIndex, int overviewIndex, string team, bool primary = false)
        {
            try
            {
                var rows = ListValues(data.GetExtra(tableIndex), TeamMemberLimit, skipUnresolved: true);
                if (rows is not { Length: > 0 })
                    rows = ListValues(data.GetExtra(overviewIndex), TeamMemberLimit, skipUnresolved: true);
                if (rows != null)
                    AddStaffContextRows(rows, team, primary);
            }
            catch (Exception error) { LogSupplementFailure(StaffSource.Team, error); }
        }

        private void AddVisibleStaffTable(string team)
        {
            try
            {
                if (GameUi.ActiveCareerDocument?.Q<StreamedTable>("StaffStreamedTable") is not { panel: not null } table)
                    return;
                for (var element = (VisualElement)table; element != null; element = element.parent)
                    if (element.resolvedStyle.display == DisplayStyle.None ||
                        element.resolvedStyle.visibility == Visibility.Hidden)
                        return;
                var key = table.Key;
                var teamPath = Span("team.UniqueId");
                var clubPath = Span("team.Club.UniqueId");
                if (PublicId(bindings.Get(ref key, ref teamPath)) != primaryId ||
                    PublicId(bindings.Get(ref key, ref clubPath)) != clubId)
                    return;
                if (ListValues(table.SourceData, TeamMemberLimit, skipUnresolved: true) is { } rows)
                    AddStaffContextRows(rows, team, primary: true);
            }
            catch (Exception) { /* The native staff view can disappear during a page change. */ }
        }

        private void AddStaffContextRows(IEnumerable<RowReference> rows, string team, bool primary)
        {
            foreach (var row in rows.Take(StaffContextLimit - staffContext.Count))
                staffContext.Add(new PersonValue(row, team, true, primary, RoleOnly: true));
        }

        private void StartPeople(float now)
        {
            work.AddRange(supplement.Take(KnownReferenceLimit - work.Count));
            var existingStaff = work.Where(person => person.Staff).Select(person => person.Value.Object.Pointer).ToHashSet();
            work.AddRange(staffContext.Where(person => existingStaff.Add(person.Value.Object.Pointer))
                .Take(KnownReferenceLimit - work.Count));
            NextBatch(now);
        }

        private void ReadTeams(BoundValues data)
        {
            for (var i = 0; i < teamCount; i++)
            {
                var start = i * 5;
                var id = PublicId(data.Get(start));
                var owner = PublicId(data.Get(start + 1));
                if (!id.HasValue)
                {
                    partial = true;
                    squadCatalogComplete = false;
                    continue;
                }
                if (owner.HasValue && owner.Value != clubId && !squads[i].NativeSquad &&
                    ReadBoolean(data.GetExtra(teamCount * 8 + i)) != true)
                    continue;
                if (id == primaryId || readSquads.Contains(id.Value))
                    continue;
                listedSquads.Add(id.Value);
                if (owner.HasValue)
                    managedSquadOwners.Add(owner.Value);
                AddStaffList(data, teamCount * 5 + i, StaffSource.Medical, MedicalStaffLimit, fromOwnedTeam: owner == clubId);
                var team = ReadText(data.Get(start + 2));
                var playerRows = ListValues(data.Get(start + 3), TeamMemberLimit);
                var staffRows = ListValues(data.Get(start + 4), TeamMemberLimit);
                if (string.IsNullOrWhiteSpace(team) || playerRows is null || staffRows is null)
                    partial = true;
                if (string.IsNullOrWhiteSpace(team))
                    continue;
                if (playerRows is not null)
                {
                    readSquads.Add(id.Value);
                    work.AddRange(playerRows.Select(value => new PersonValue(value, team, false, false,
                        SquadId: id.Value, FromOwnedTeam: owner == clubId)));
                }
                if (staffRows is not null)
                    work.AddRange(staffRows.Select(value => new PersonValue(value, team, true, false,
                        FromOwnedTeam: owner == clubId)));
                AddStaffContext(data, teamCount * 6 + i, teamCount * 7 + i, team);
            }
            if (work.Count > KnownReferenceLimit)
                throw new ReadUnavailable("club has too many roster rows");
        }

        private void ReadPeople(BoundValues data)
        {
            for (var i = 0; i < batchCount; i++)
            {
                var item = work[offset + i];
                try { ReadPerson(data, i, item); }
                catch (Exception error) when (item.Optional)
                { LogSupplementFailure(item.Source, error); }
            }
        }

        private void ReadPerson(BoundValues data, int rowIndex, PersonValue item)
        {
            var index = rowIndex * PersonFieldCount;
            var extra = item.Optional;
            var id = PublicId(extra ? data.GetExtra(index) : data.Get(index));
            if (id == managerId)
                return;
            var jobIndex = batchCount * PersonFieldCount + rowIndex * StaffJobPaths.Length;
            if (item.RoleOnly)
            {
                if (id.HasValue && JobText(data, jobIndex) is { } contextualRole)
                    contextualStaffRoles.TryAdd(id.Value, contextualRole);
                return;
            }
            var isVirtual = item.Staff ? false : ReadBoolean(data.GetExtra(index + 3));
            if (isVirtual != false)
            {
                if (id.HasValue && isVirtual == true)
                    virtualPlayers.Add(id.Value);
                else
                {
                    partial = true;
                    if (item.SquadId != 0)
                        readSquads.Remove(item.SquadId);
                }
                return;
            }
            var name = ReadText(extra ? data.GetExtra(index + 1) : data.Get(index + 1));
            if (!id.HasValue || string.IsNullOrWhiteSpace(name))
            {
                if (item.IsPrimary && !item.Staff)
                    throw new ReadUnavailable("main team player identity");
                if (!extra)
                    partial = true;
                if (item.SquadId != 0)
                    readSquads.Remove(item.SquadId);
                return;
            }
            var role = item.Staff ? JobText(data, jobIndex) : ReadText(data.Get(index + 2));
            var ageIndex = batchCount * (PersonFieldCount + StaffJobPaths.Length) + rowIndex;
            var ageValue = data.GetExtra(ageIndex);
            var femaleValue = data.GetExtra(ageIndex + batchCount);
            var person = new DirectoryPerson(name, FmDisplayText.Initials(name), item.Team,
                string.IsNullOrWhiteSpace(role) ? item.Staff ? GenericStaffRole : "球员" : role,
                id, NativeDisplayName: true, Age: ReadAge(ageValue), IsFemale: ReadFemale(femaleValue));
            if (item.Staff)
                MergeStaff(id.Value, person, item.Source);
            else if (players.TryAdd(id.Value, person) && item.SquadId != 0)
                playerSquads.Add(id.Value, item.SquadId);
            if (item.Value.Object is InteropReference)
                references.TryAdd(id.Value, new KnownRosterPerson(item.Value, item.Staff, item.FromOwnedTeam));
        }

        private void RetainUnreadSquads()
        {
            foreach (var (teamId, members) in KnownSquads)
            {
                if (readSquads.Contains(teamId) || squadCatalogComplete && !listedSquads.Contains(teamId))
                    continue;
                partial = true;
                foreach (var person in members)
                    if (person.PublicId is uint id && !virtualPlayers.Contains(id) && players.TryAdd(id, person))
                        playerSquads.Add(id, teamId);
            }
        }

        private void RecordSquads()
        {
            KnownSquads.Clear();
            foreach (var team in playerSquads.GroupBy(member => member.Value))
                KnownSquads.Add(team.Key, team.Select(member => players[member.Key]).ToArray());
        }

        private string? JobText(BoundValues data, int index)
        {
            for (var path = 0; path < StaffJobPaths.Length; path++)
            {
                try
                {
                    if (data.GetExtra(index + path) is not { IsNull: false } value || value.DataType?.Name is not { } kind)
                        continue;
                    staffJobValueTypes.Add(StaffJobPaths[path] + "=" + kind);
                    var text = kind switch
                    {
                        nameof(TranslationID) when value.Get() is { } boxed =>
                            FmDisplayText.Read(boxed.Unbox<TranslationID>().TranslatedString),
                        "String" => ReadText(value),
                        _ => null
                    };
                    if (IsConcreteStaffRole(text))
                        return text;
                }
                catch (Exception) { /* An unavailable job should not prevent valid identities from syncing. */ }
            }
            return null;
        }

        private void MergeStaff(uint id, DirectoryPerson person, StaffSource source)
        {
            if (staff.TryAdd(id, (person, source)))
                return;
            var previous = staff[id];
            var preferred = source > previous.Source ? person : previous.Person;
            var supplement = source > previous.Source ? previous.Person : person;
            staff[id] = (preferred with
            {
                Role = IsConcreteStaffRole(preferred.Role) ? preferred.Role : supplement.Role,
                Age = preferred.Age ?? supplement.Age,
                IsFemale = preferred.IsFemale ?? supplement.IsFemale
            }, source > previous.Source ? source : previous.Source);
        }

        private void NextBatch(float now)
        {
            var count = Math.Min(PeopleBatchSize, work.Count - offset);
            if (count > 0)
            {
                batchCount = count;
                var paths = new List<string?>(count * PersonFieldCount);
                var jobs = new List<string?>(count * StaffJobPaths.Length);
                var ages = new List<string?>(count);
                var genders = new List<string?>(count);
                var requiredCount = 0;
                for (var i = 0; i < count; i++)
                {
                    var item = work[offset + i];
                    try
                    {
                        var root = SetScratch(i, item.Value);
                        paths.Add(root + ".UniqueId");
                        paths.Add(item.RoleOnly ? null : root + ".Name");
                        paths.Add(item.Staff ? null : root + ".PositionCombinedString");
                        paths.Add(item.Staff ? null : root + ".IsVirtual");
                        foreach (var jobPath in StaffJobPaths)
                            jobs.Add(item.Staff ? root + jobPath : null);
                        ages.Add(item.RoleOnly ? null : root + ".Age");
                        genders.Add(item.RoleOnly ? null : root + ".IsPersonFemale");
                    }
                    catch (Exception error) when (item.Optional)
                    {
                        LogSupplementFailure(item.Source, error);
                        paths.AddRange(new string?[PersonFieldCount]);
                        jobs.AddRange(new string?[StaffJobPaths.Length]);
                        ages.Add(null);
                        genders.Add(null);
                    }
                    if (!item.Optional)
                        requiredCount += PersonFieldCount;
                }
                var optionalCount = paths.Count - requiredCount + jobs.Count + ages.Count + genders.Count;
                paths.AddRange(jobs);
                paths.AddRange(ages);
                paths.AddRange(genders);
                OpenGroup(Phase.People, paths, now, requiredCount, optionalCount);
                return;
            }
            if (players.Count == 0)
                throw new ReadUnavailable("no validated players");
            if (knownClub == ClubScope)
            {
                RetainUnreadSquads();
                var candidates = KnownPeople.Where(person =>
                    person.Value.FromOwnedTeam && !virtualPlayers.Contains(person.Key) &&
                    !players.ContainsKey(person.Key) && !staff.ContainsKey(person.Key)).ToArray();
                if (candidates.Length > 0)
                {
                    var start = nextDepartureIndex % candidates.Length;
                    missing = candidates.Skip(start).Concat(candidates.Take(start)).Take(PeopleBatchSize * 4).ToArray();
                    departureIndex = (start + missing.Length) % candidates.Length;
                }
            }
            offset = 0;
            OpenDepartureBatch(now);
        }

        private void OpenDepartureBatch(float now)
        {
            batchCount = Math.Min(PeopleBatchSize, missing.Length - offset);
            if (batchCount == 0)
            {
                OpenGroup(Phase.Verify, IdentityPaths, now);
                return;
            }
            var paths = new string?[batchCount * 3];
            for (var i = 0; i < batchCount; i++)
            {
                try
                {
                    var previous = missing[offset + i].Value;
                    var root = SetScratch(i, previous.Reference);
                    paths[i] = root + ".UniqueId";
                    paths[batchCount + i] = root + ".ContractClub.UniqueId";
                    paths[batchCount * 2 + i] = previous.Staff ? null : root + ".IsVirtual";
                }
                catch (Exception) { /* An old reference can disappear from the game database. */ }
            }
            OpenGroup(Phase.Departures, paths, now, requiredCount: 0);
        }

        private string SetScratch(int index, RowReference value)
        {
            var root = "fmPlayerTalkRosterProbe" + index.ToString(CultureInfo.InvariantCulture);
            var path = Span(root);
            scratch.Add(bindings.Set(ref path, value.NewValue(), Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default));
            return root;
        }

        private void OpenGroup(Phase stage, IEnumerable<string?> paths, float now, int? requiredCount = null,
            int? optionalCount = null)
        {
            phase = stage;
            var allPaths = paths as IReadOnlyList<string?> ?? paths.ToArray();
            var required = requiredCount ?? allPaths.Count;
            group = new BoundValues(bindings, allPaths, required, optionalCount ?? allPaths.Count - required, stage);
            requestDeadline = now + RequestTimeout;
            supplementDeadline = now + SupplementWait;
        }

        private void EndGroup()
        {
            var previous = group;
            group = null;
            try
            {
                previous?.Dispose();
            }
            finally
            {
                foreach (var key in scratch)
                {
                    try
                    {
                        var slot = key;
                        bindings.Remove(ref slot);
                    }
                    catch (Exception)
                    {
                        // Attempt every removal even if the native tree was rebuilt.
                    }
                }
                scratch.Clear();
            }
        }

        internal void Release()
        {
            try
            {
                EndGroup();
            }
            catch (Exception)
            {
                // The native binding tree may already have been destroyed during a save transition.
            }
        }
    }
}
