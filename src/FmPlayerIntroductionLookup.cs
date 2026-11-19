using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using FM.UI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using SI.Interop;
using UnityEngine;

namespace FmPlayerTalk;

// Read-only, build-specific layout selected by the native compatibility gate.
// The live UID and non-virtual indicator are verified independently before accepting a contact.
internal static class FmPlayerIntroductionLookup
{
    private static ulong PlayerList => FmNativeLayout.Current.Players;
    private static ulong PlayerVtable => FmNativeLayout.Current.Player;
    private static ulong PlayerAltVtable => FmNativeLayout.Current.AlternatePlayer;
    private static ulong NationVtable => FmNativeLayout.Current.Nation;
    private static ulong ClubVtable => FmNativeLayout.Current.Club;
    private static ulong TeamVtable => FmNativeLayout.Current.Team;
    private const int PlayerIdentityLength = 0x70;
    private const int MaxPlayers = 2_000_000;
    private const int MaxExactMatches = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record SearchScope(ulong Module, ulong Begin, ulong End, long NameScope = 0);
    internal sealed record NameHit(uint Id, string Name, string? OriginalName = null);
    internal sealed record Target(uint Id, int PersonIndex, uint Nation, int? WorldReputation,
        string? NationName, uint? ClubId, string? ClubName);
    internal sealed record ManagerInfluence(uint Nation, int? WorldReputation, string? NationName);

    internal static SearchScope? CaptureSearch()
    {
        var module = FmNativePromises.ModuleBase();
        return module != 0 ? ReadPlayerList(module) : null;
    }

    internal static IReadOnlyList<NameHit>? Search(SearchScope scope, string text, string language,
        CancellationToken cancellation) => Search(scope, new[] { text }, language, cancellation);

    internal static IReadOnlyList<NameHit>? Search(SearchScope scope, IReadOnlyList<string> texts, string language,
        CancellationToken cancellation)
    {
        var keys = new List<string>();
        foreach (var text in texts)
        {
            var key = PlayerIntroductionRules.MatchKey(text);
            if (key.Length > 0 && !keys.Contains(key))
                keys.Add(key);
            if (keys.Count == 4)
                break;
        }
        var pointers = new byte[checked((int)(scope.End - scope.Begin))];
        if (!FmNativePromises.TryRead(scope.Begin, pointers))
            return null;

        var match = new Dictionary<uint, (NameHit Hit, int Score)>();
        var observedNames = NativeNameTable.Capture(scope.NameScope, language);
        var strings = new Dictionary<ulong, NamedText?>();
        var entries = new Dictionary<ulong, ulong>();
        var identity = new byte[PlayerIdentityLength];
        var vtable = new byte[8];
        for (var i = 0; i < pointers.Length; i += 8)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = BitConverter.ToUInt64(pointers, i);
            if (!IsPlayer(scope.Module, entry, vtable) ||
                !FmNativePromises.TryRead(entry + PlayerNameData(scope.Module, BitConverter.ToUInt64(vtable)), identity))
                continue;
            var index = BitConverter.ToInt32(identity, 8);
            var id = BitConverter.ToUInt32(identity, 12);
            if (id is 0 or uint.MaxValue || !ValidIndex(index))
                continue;

            var alias = observedNames.TryGetValue(id, out var observed) ? observed : null;
            var common = ReadName(0x60, indirect: true);
            var full = ReadName(0x40, indirect: false);
            var first = ReadName(0x50, indirect: true);
            var last = ReadName(0x58, indirect: true);
            var best = int.MaxValue;
            foreach (var key in keys)
            {
                if (alias != null && MatchesAlias(alias, key) || full?.Key == key || common?.Key == key || last?.Key == key ||
                    first != null && last != null && (first.Key + last.Key == key || last.Key + first.Key == key))
                    best = 0;
                else if (key.Length >= 3 && (full?.Key.Contains(key, StringComparison.Ordinal) == true ||
                    common?.Key.Contains(key, StringComparison.Ordinal) == true))
                    best = Math.Min(best, 2);
            }
            if (best == int.MaxValue)
                continue;
            var original = full?.Text ?? common?.Text ??
                (first == null || last == null ? last?.Text : first.Text + " " + last.Text);
            var name = alias ?? original;
            if (name != null)
                match.TryAdd(id, (new(id, name, original), best));
            if (match.Count > MaxExactMatches * 2)
            {
                var worst = match.OrderByDescending(pair => pair.Value.Score).First();
                match.Remove(worst.Key);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if (ReadPlayerList(scope.Module) != scope)
            return null;
        var minimum = match.Count == 0 ? int.MaxValue : match.Values.Min(item => item.Score);
        return match.Values.Where(item => item.Score == minimum).OrderBy(item => item.Hit.Id)
            .Take(MaxExactMatches).Select(item => item.Hit).ToArray();

        NamedText? ReadName(int offset, bool indirect)
        {
            var pointer = BitConverter.ToUInt64(identity, offset);
            if (indirect && pointer != 0)
            {
                if (!entries.TryGetValue(pointer, out var address))
                    entries[pointer] = address = ReadPointer(pointer);
                pointer = address;
            }
            if (pointer == 0)
                return null;
            if (!strings.TryGetValue(pointer, out var val))
            {
                var name = ReadText(pointer);
                val = name == null ? null : new(name, PlayerIntroductionRules.MatchKey(name));
                strings[pointer] = val;
            }
            return val;
        }
    }

    private static bool MatchesAlias(string alias, string key)
    {
        if (PlayerIntroductionRules.MatchKey(alias) == key)
            return true;
        var surname = alias.LastIndexOf('·');
        return surname >= 0 && surname < alias.Length - 1 &&
            PlayerIntroductionRules.MatchKey(alias[(surname + 1)..]) == key;
    }

    private sealed record NamedText(string Text, string Key);

    internal static Target? ReadTarget(ulong module, uint id)
    {
        var entry = FmNativeAffinity.FindPlayerRecord(module, id);
        var vtable = new byte[8];
        if (!IsPlayer(module, entry, vtable))
            return null;
        var identity = new byte[PlayerIdentityLength];
        if (!FmNativePromises.TryRead(entry + PlayerNameData(module, BitConverter.ToUInt64(vtable)), identity) ||
            BitConverter.ToUInt32(identity, 12) != id)
            return null;
        var index = BitConverter.ToInt32(identity, 8);
        if (!ValidIndex(index))
            return null;

        var nation = ReadNation(module, BitConverter.ToUInt64(identity, 0x68));
        var team = ReadPointer(entry + 0x130);
        var club = ReadPointer(team) == module + TeamVtable ? ReadPointer(team + 0x30) : 0;
        var clubId = ObjectId(module, club, ClubVtable);
        return new(id, index, nation.Id, ReadReputation(entry + 0x262), nation.Name,
            clubId, clubId.HasValue ? ReadText(ReadPointer(club + 0xC8)) : null);
    }

    internal static ManagerInfluence? ReadManager(ulong module, uint managerId)
    {
        if (FmNativeAffinity.FindManagerRecord(module, managerId) is not { } manager)
            return null;
        var nation = ReadNation(module, ReadPointer(manager.Person + 0x68));
        return new(nation.Id, ReadReputation(manager.Entry + 0xD8), nation.Name);
    }

    private static SearchScope? ReadPlayerList(ulong module)
    {
        var bytes = new byte[16];
        if (!FmNativePromises.TryRead(module + PlayerList, bytes))
            return null;
        var begin = BitConverter.ToUInt64(bytes, 0);
        var end = BitConverter.ToUInt64(bytes, 8);
        if (begin < 0x10000 || begin % 8 != 0 || end <= begin || (end - begin) % 8 != 0 ||
            (end - begin) / 8 > MaxPlayers)
            return null;
        return new(module, begin, end, NativeNameTable.Version);
    }

    private static bool IsPlayer(ulong module, ulong entry, byte[] buffer)
    {
        if (!FmNativePromises.TryRead(entry, buffer))
            return false;
        var vtable = BitConverter.ToUInt64(buffer);
        return vtable == module + PlayerVtable || vtable == module + PlayerAltVtable;
    }

    private static ulong PlayerNameData(ulong module, ulong vtable) =>
        FmNativeLayout.Current.PersonOffset(vtable - module, staff: false, managerOnly: false);

    private static bool ValidIndex(int value) => value is >= 0 and < 32_000_000;

    private static (uint Id, string? Name) ReadNation(ulong module, ulong address) =>
        ObjectId(module, address, NationVtable) is uint id ? (id, ReadText(ReadPointer(address + 0x18))) : default;

    private static uint? ObjectId(ulong module, ulong address, ulong expectedVtable)
    {
        var fields = new byte[16];
        if (!FmNativePromises.TryRead(address, fields) || BitConverter.ToUInt64(fields) != module + expectedVtable)
            return null;
        var id = BitConverter.ToUInt32(fields, 12);
        return id is > 0 and < uint.MaxValue ? id : null;
    }

    private static int? ReadReputation(ulong address)
    {
        var bytes = new byte[2];
        return FmNativePromises.TryRead(address, bytes) && BitConverter.ToInt16(bytes) is >= 0 and <= 10000
            ? BitConverter.ToInt16(bytes) : null;
    }

    private static ulong ReadPointer(ulong address)
    {
        var bytes = new byte[8];
        return FmNativePromises.TryRead(address, bytes) ? BitConverter.ToUInt64(bytes) : 0;
    }

    private static string? ReadText(ulong address)
    {
        var length = new byte[4];
        if (!FmNativePromises.TryRead(address, length))
            return null;
        var count = BitConverter.ToInt32(length);
        if (count is <= 0 or > 320)
            return null;
        var text = new byte[count];
        if (!FmNativePromises.TryRead(address + 4, text))
            return null;
        try
        {
            var result = StrictUtf8.GetString(text).TrimEnd('\0').Trim();
            if (result.Length == 0 || result.Length > 100)
                return null;
            foreach (var character in result)
                if (char.IsControl(character))
                    return null;
            return result;
        }
        catch (DecoderFallbackException) { return null; }
    }

    internal sealed class PersonVerification : IDisposable
    {
        private const string Root = "fmPlayerTalkPlayerIntroduction";
        private static readonly string[] Fields =
        {
            ".UniqueId", ".Name", ".IsVirtual", ".Age", ".IsPersonFemale", ".PositionCombinedString"
        };
        private readonly BindingSubsystem bindings;
        private readonly Bindings.ValueChangedCallback changed = (Action<Bindings.Key, TypedValue>)((_, _) => { });
        private readonly Bindings.Key?[] keys = new Bindings.Key?[Fields.Length];
        private readonly Target target;
        private readonly float deadline;
        private Bindings.Key? scratch;

        private PersonVerification(BindingSubsystem bindings, Target target)
        {
            this.bindings = bindings;
            this.target = target;
            deadline = Time.unscaledTime + 5;
        }

        internal static PersonVerification? Begin(Target target)
        {
            if (LiveClubRoster.FindBindings() is not { } subsystem)
                return null;
            var reader = new PersonVerification(subsystem, target);
            try
            {
                var path = Span(Root);
                var reference = new PersonReference(target.PersonIndex);
                reader.scratch = subsystem.Set(ref path, TypedValue.Create(reference),
                    Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default);
                for (var i = 0; i < Fields.Length; i++)
                    reader.Bind(i);
                return reader;
            }
            catch (Exception)
            {
                reader.Dispose();
                return null;
            }
        }

        private void Bind(int index)
        {
            var path = Span(Root + Fields[index]);
            try { keys[index] = bindings.Bind(ref path, bindings, changed); }
            catch (Exception) { /* An unavailable optional field is omitted from the preview. */ }
        }

        internal bool TryFinish(out DirectoryPerson? person)
        {
            person = null;
            try
            {
                var id = LiveClubRoster.PublicId(Read(0));
                var name = Text(Read(1));
                var isVirtual = LiveClubRoster.ReadBoolean(Read(2));
                if (id == target.Id && name is { Length: > 0 and <= 100 } && isVirtual == false)
                {
                    var age = LiveClubRoster.ReadAge(Read(3));
                    var female = LiveClubRoster.ReadFemale(Read(4));
                    var position = Text(Read(5));
                    person = new DirectoryPerson(name, FmDisplayText.Initials(name), DepartedContacts.IntroducedGroup,
                        string.IsNullOrWhiteSpace(position) ? "球员" : position, target.Id, NativeDisplayName: true,
                        Age: age, IsFemale: female);
                    return true;
                }
                return Time.unscaledTime >= deadline || isVirtual == true || id.HasValue && id != target.Id;
            }
            catch (Exception) { return true; }
        }

        private TypedValue? Read(int index) =>
            keys[index] is { } key && bindings.IsDataSet(key) ? bindings.Get(ref key) : null;

        private static string? Text(TypedValue? value) =>
            value is { IsNull: false } ? FmDisplayText.Read(value.AsString())?.Trim() : null;

        private static Il2CppSystem.ReadOnlySpan<char> Span(string value) =>
            new(new Il2CppStructArray<char>(value.ToCharArray()));

        public void Dispose()
        {
            foreach (var item in keys)
                if (item is { } key)
                    try { bindings.Unbind(ref key, changed); } catch (Exception) { }
            Array.Clear(keys, 0, keys.Length);
            if (scratch is { } slot)
                try { bindings.Remove(ref slot); } catch (Exception) { }
            scratch = null;
        }
    }
}
