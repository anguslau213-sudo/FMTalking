using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using DateTime = System.DateTime;
using NativeCollection = Il2CppSystem.Collections.ICollection;
using NativeList = Il2CppSystem.Collections.IList;

namespace FmPlayerTalk;

internal static class FmActivePromises
{
    private const string ActiveList = "game.GameCurrentManager.HumanActivePromisesMadeList";
    private const string Scratch = "fmPlayerTalkActivePromise";
    private const int MaxRows = 300;
    private const ushort PreferredPositionAndRole = 208;
    private static Watch? watch;

    // Unity's binding subsystem populates list and nested row fields on different
    // updates. Keep the live subscriptions only while the caller's career is active.
    internal static void Tick(bool active)
    {
        if (!active || GameSaveTimeline.DirectoryPath is not { } career)
        {
            Reset();
            return;
        }

        try
        {
            if (watch?.Career != career)
                Reset();
            if (watch != null)
                watch.RefreshRows();
            else if (LiveClubRoster.FindBindings() is { } bindings)
                watch = new Watch(bindings, career);
        }
        catch (Exception)
        {
            Reset();
        }
    }

    internal static void Reset()
    {
        var previous = watch;
        watch = null;
        previous?.Dispose();
    }

    internal static FmPromiseOption? Match(uint playerId, IReadOnlyList<FmPromiseOption> candidates, DateTime gameDate)
    {
        if (playerId is 0 or uint.MaxValue || candidates.Count != 1 ||
            candidates[0] is not { TypeId: PreferredPositionAndRole, DeadlineIsUnique: true } candidate ||
            watch is not { } live || live.Career != GameSaveTimeline.DirectoryPath)
            return null;
        return live.Match(playerId, candidate, gameDate);
    }

    private sealed record Row(IntPtr Identity, Bindings.Key Player, Bindings.Key Ongoing, Bindings.Key DaysRemaining,
        Bindings.Key Description, Bindings.Key Title);

    private sealed class Watch : IDisposable
    {
        private readonly BindingSubsystem bindings;
        private readonly Bindings.Key listKey;
        private readonly Bindings.ValueChangedCallback listChanged;
        private readonly Bindings.ValueChangedCallback fieldChanged;
        private readonly List<Bindings.Key> fieldKeys = new();
        private readonly List<Bindings.Key> temporary = new();
        private Row[] rows = Array.Empty<Row>();
        private IntPtr listIdentity;
        private bool needsRows = true;
        private bool ready;

        internal string Career { get; }

        internal Watch(BindingSubsystem bindings, string career)
        {
            this.bindings = bindings;
            Career = career;
            listChanged = (Action<Bindings.Key, TypedValue>)((_, _) => needsRows = true);
            // The subscription lets FM resolve and update the nested property.
            fieldChanged = (Action<Bindings.Key, TypedValue>)((_, _) => { });
            var path = Span(ActiveList);
            listKey = bindings.Bind(ref path, bindings, listChanged);
        }

        internal void RefreshRows()
        {
            if (!needsRows)
                return;
            if (!TryList(out var list, out var count))
            {
                ClearRows();
                return;
            }

            needsRows = false;
            if (SameList(list, count))
                return;

            // Registering new scratch nodes can update FM's list collection.
            // Materialize the references before touching any binding for a row.
            var references = new DynamicReference[count];
            for (var index = 0; index < count; index++)
            {
                if (ReadReference(list!, index) is not { } reference)
                {
                    ClearRows();
                    needsRows = true;
                    return;
                }
                references[index] = reference;
            }

            ClearRows();
            var pending = new Row[count];
            for (var index = 0; index < count; index++)
            {
                var reference = references[index];
                var root = Scratch + index.ToString(CultureInfo.InvariantCulture);
                var path = Span(root);
                temporary.Add(bindings.Set(ref path, TypedValue.Create(reference), Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default));
                pending[index] = new Row(reference.Pointer,
                    BindField(root + ".InteractionPromisePromiser.UniqueId"),
                    BindField(root + ".InteractionPromiseOngoing"),
                    BindField(root + ".InteractionPromiseDaysRemaining"),
                    BindField(root + ".InteractionPromiseDescription"),
                    BindField(root + ".InteractionPromiseTitle"));
            }
            rows = pending;
            listIdentity = list!.Pointer;
            ready = true;
        }

        internal FmPromiseOption? Match(uint playerId, FmPromiseOption candidate, DateTime gameDate)
        {
            try
            {
                if (!CheckList())
                    return null;

                var daysExpected = (candidate.Deadline.Date - gameDate.Date).Days + 1;
                FmPromiseOption? match = null;
                foreach (var row in rows)
                {
                    if (LiveClubRoster.PublicId(Read(row.Player)) is not { } rowPlayer)
                        return null;
                    if (rowPlayer != playerId)
                        continue;
                    if (LiveClubRoster.ReadBoolean(Read(row.Ongoing)) is not { } ongoing)
                        return null;
                    if (!ongoing)
                        continue;

                    if (match != null || Read(row.DaysRemaining) is not { IsNull: false, DataType.Name: "DynamicNumber" } days ||
                        !int.TryParse(days.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var daysRemaining) ||
                        daysRemaining <= 0 || daysRemaining != daysExpected)
                        return null;

                    var title = ReadText(row.Description) ?? ReadText(row.Title);
                    if (title == null)
                        return null;
                    match = candidate with { Title = title };
                }

                return match != null && CheckList() ? match : null;
            }
            catch (Exception)
            {
                needsRows = true;
                return null;
            }
        }

        private bool CheckList()
        {
            if (!needsRows && TryList(out var list, out var count) && SameList(list, count))
                return true;
            needsRows = true;
            return false;
        }

        private bool TryList(out NativeList? list, out int count)
        {
            var value = Read(listKey);
            list = value is { IsNull: false } ? value.Get()?.TryCast<NativeList>() : null;
            if (list?.TryCast<NativeCollection>() is { Count: >= 0 and <= MaxRows } collection)
            {
                count = collection.Count;
                return true;
            }
            count = 0;
            return false;
        }

        private bool SameList(NativeList? list, int count)
        {
            if (!ready || list == null || list.Pointer != listIdentity || count != rows.Length)
                return false;
            for (var index = 0; index < count; index++)
                if (ReadReference(list, index)?.Pointer != rows[index].Identity)
                    return false;
            return true;
        }

        private static DynamicReference? ReadReference(NativeList list, int index) =>
            list[index]?.TryCast<TypedValue>() is { IsNull: false } value
                ? value.Get()?.TryCast<DynamicReference>() : null;

        private Bindings.Key BindField(string name)
        {
            var path = Span(name);
            var key = bindings.Bind(ref path, bindings, fieldChanged);
            fieldKeys.Add(key);
            return key;
        }

        private TypedValue? Read(Bindings.Key key) =>
            bindings.IsDataSet(key) ? bindings.Get(ref key) : null;

        private string? ReadText(Bindings.Key key) =>
            Read(key) is { IsNull: false, DataType.Name: "String" } value &&
            FmDisplayText.Read(value.AsString()) is { Length: > 0 and <= 140 } text ? text : null;

        public void Dispose()
        {
            Unbind(listKey, listChanged);
            ClearRows();
        }

        private void ClearRows()
        {
            ready = false;
            rows = Array.Empty<Row>();
            listIdentity = IntPtr.Zero;
            foreach (var key in fieldKeys)
                Unbind(key, fieldChanged);
            fieldKeys.Clear();
            foreach (var key in temporary)
            {
                try
                {
                    var local = key;
                    bindings.Remove(ref local);
                }
                catch (Exception) { /* The active native tree may already have changed. */ }
            }
            temporary.Clear();
        }

        private void Unbind(Bindings.Key key, Bindings.ValueChangedCallback callback)
        {
            try { bindings.Unbind(ref key, callback); }
            catch (Exception) { /* The active native tree may already have changed. */ }
        }
    }

    private static Il2CppSystem.ReadOnlySpan<char> Span(string value) =>
        new(new Il2CppStructArray<char>(value.ToCharArray()));
}
