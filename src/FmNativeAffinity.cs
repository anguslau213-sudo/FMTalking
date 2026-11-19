using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FmPlayerTalk;

internal sealed record FmAffinityReadResult(bool Available, int Affinity, string? Reason, bool IsPermanent = false);
internal sealed record FmAffinityWriteResult(bool Succeeded, string? Reason, bool OutcomeUncertain = false);

// All addresses are selected only after the complete native build is verified.
// The caller owns the Unity-thread, active-career and idle-game checks.
internal static partial class FmNativeAffinity
{
    private static ulong PlayerList => FmNativeLayout.Current.Players;
    private static ulong StaffList => FmNativeLayout.Current.Staff;
    private const ulong PersonId = 0xC;
    private const ulong PersonRelations = 0x78;
    private const int PointerSize = sizeof(ulong);
    private const int HeaderSize = PointerSize * 3;
    private const int RecordSize = 16;
    private const int ReasonOffset = 8;
    private const int KindOffset = 10;
    private const int TypeOffset = 11;
    private const int LevelOffset = 12;
    private const int FlagsOffset = 13;
    private const byte PersonKind = 3;
    private const byte Likes = 1;
    private const byte Dislikes = 2;
    private const ushort ManagerReason = 6;
    private const byte PermanentFlag = 0x4F;
    private const int MaxPeople = 2_000_000;
    private const int MaxRelations = 4096;
    private const string Unsupported = "当前 FM 版本尚未验证原版关系同步。";
    private const string Stale = "FM 的人物或关系记录已经变化，本次亲密度未写入。";
    private const string Unreadable = "无法核实 FM 的人物关系记录。";

    private sealed record Person(uint Id, ulong List, ulong Slot, ulong Entry, ulong Vtable, ulong Address, ulong PersonVtable);
    private sealed record Pool(ulong Root, ulong Begin, ulong End);
    private sealed record Pools(ulong Module, Pool Staff, Pool Players);
    private sealed record PersonIndex(Dictionary<uint, int>? Slots, string? Error);
    private sealed record Scan(Pools Pools, CancellationTokenSource Cancellation, Task<PersonIndex> Completion);
    private sealed record RelationList(Person Manager, Person Owner, ulong Address,
        ulong Begin, ulong End, ulong Capacity, byte[] Header, byte[] Records, int Like, int Dislike)
    {
        internal int Affinity => ReadLevel(Dislike) > 0 ? -ReadLevel(Dislike) : ReadLevel(Like);
        internal bool HasManagerReason => Like >= 0 && BitConverter.ToUInt16(Records, Like + ReasonOffset) == ManagerReason;
        internal bool IsPermanent => Like >= 0 && Records[Like + FlagsOffset] == PermanentFlag;
        private int ReadLevel(int index) => index >= 0 ? Records[index + LevelOffset] : 0;
    }
    private sealed record Patch(ulong Address, byte[] Before, byte[] After);
    private sealed class NativeAffinityException : Exception
    {
        internal bool OutcomeUncertain { get; }
        internal NativeAffinityException(string reason, bool outcomeUncertain = false) : base(reason) =>
            OutcomeUncertain = outcomeUncertain;
    }

    private static Person? cachedManager;
    private static Person? cachedOwner;
    private static Scan? scan;

    internal static void Reset()
    {
        var previous = scan;
        scan = null;
        cachedManager = null;
        cachedOwner = null;
        previous?.Cancellation.Cancel();
        previous?.Cancellation.Dispose();
    }

    internal static FmAffinityReadResult Read(uint managerId, uint personId)
    {
        try
        {
            var list = Resolve(managerId, personId);
            return new(true, list.Affinity, null, list.Affinity > 0 && list.IsPermanent);
        }
        catch (NativeAffinityException error)
        {
            return new(false, 0, error.Message);
        }
    }

    internal static FmAffinityWriteResult Apply(uint managerId, uint personId, int expectedAffinity, int nextAffinity,
        bool blocked, Func<bool> commit, bool permanentLike = false)
    {
        if (expectedAffinity is < -100 or > 100 || nextAffinity is < -100 or > 100 ||
            blocked && nextAffinity >= 0 || permanentLike && (blocked || nextAffinity <= 0))
            return new(false, Stale);

        RelationList? original = null;
        var attempted = new List<Patch>(3);
        ulong grown = 0;
        try
        {
            original = Resolve(managerId, personId);
            if (original.Affinity != expectedAffinity)
                return new(false, Stale);

            // Let FM construct and own a missing holder on the game thread. A
            // later rollback restores its empty vector, retaining the valid FM
            // object rather than detaching it or freeing a native pool object.
            if (original.Address == 0 && nextAffinity != 0)
                original = InitializeRelations(original);

            var patches = Prepare(original, nextAffinity, permanentLike, ref grown);
            CheckUnchanged(original);
            foreach (var patch in patches)
            {
                if (!ReadBytes(patch.Address, patch.Before.Length).AsSpan().SequenceEqual(patch.Before))
                    throw new NativeAffinityException(Stale);
                attempted.Add(patch);
                if (!FmNativePromises.WriteBytes(patch.Address, patch.After))
                    throw new NativeAffinityException("未能核实 FM 已写入亲密度。");
            }

            CheckPeople(original);
            var observed = ReadRelations(original.Manager, original.Owner);
            if (observed.Affinity != nextAffinity || observed.Begin != (grown != 0 ? grown : original.Begin))
                throw new NativeAffinityException("FM 读回的亲密度与本次变更不一致。");
            if (nextAffinity > 0 && !observed.HasManagerReason)
                throw new NativeAffinityException("FM 未确认原版关系的原因是「主教练」。");
            if (permanentLike && !observed.IsPermanent)
                throw new NativeAffinityException("FM 未确认原版关系的「持续性」。");
            foreach (var patch in attempted)
                if (!ReadBytes(patch.Address, patch.After.Length).AsSpan().SequenceEqual(patch.After))
                    throw new NativeAffinityException("FM 读回的亲密度与本次变更不一致。");
            try
            {
                if (commit())
                    return new(true, null);
            }
            catch (Exception)
            {
                // Restore the native data if the accompanying conversation could not be saved.
            }
            return Failed("未能保存本轮对话。", original, attempted, grown);
        }
        catch (NativeAffinityException error)
        {
            var failure = Failed(error.Message, original, attempted, grown);
            return error.OutcomeUncertain ? failure with { OutcomeUncertain = true } : failure;
        }
    }

    private static FmAffinityWriteResult Failed(string reason, RelationList? original, List<Patch> attempted, ulong grown)
    {
        if (attempted.Count != 0 && (original is null || !Restore(original, attempted)))
            return new(false, "无法核实 FM 原版关系已恢复，请载入修改前的存档。", OutcomeUncertain: true);

        if (grown != 0)
            Free(new IntPtr((long)grown));
        return new(false, reason);
    }

    private static RelationList Resolve(uint managerId, uint personId)
    {
        if (managerId == personId || managerId is 0 or uint.MaxValue || personId is 0 or uint.MaxValue)
            throw new NativeAffinityException("当前经理或联系人身份无法核实。");
        var module = FmNativePromises.ModuleBase();
        if (module == 0)
            throw new NativeAffinityException(Unsupported);
        var (pools, slots) = IndexedPeople(module);
        var manager = cachedManager;
        if (manager?.Id != managerId || !IsCurrent(manager))
            cachedManager = manager = FindPerson(pools, slots, managerId, managerOnly: true);
        var owner = cachedOwner;
        if (owner?.Id != personId || !IsCurrent(owner))
            cachedOwner = owner = FindPerson(pools, slots, personId, managerOnly: false);
        if (manager.Address == owner.Address)
            throw new NativeAffinityException(Unreadable);
        return ReadRelations(manager, owner);
    }

    internal static ulong FindPlayerRecord(ulong module, uint playerId)
    {
        try
        {
            var (pools, slots) = IndexedPeople(module);
            return slots.TryGetValue(playerId, out var slot) && slot > 0
                ? FindPerson(pools, slots, playerId, managerOnly: false).Entry : 0;
        }
        catch (NativeAffinityException) { return 0; }
    }

    internal static (ulong Entry, ulong Person)? FindManagerRecord(ulong module, uint managerId)
    {
        try
        {
            var (pools, slots) = IndexedPeople(module);
            if (cachedManager?.Id != managerId || !IsCurrent(cachedManager))
                cachedManager = FindPerson(pools, slots, managerId, managerOnly: true);
            return (cachedManager.Entry, cachedManager.Address);
        }
        catch (NativeAffinityException) { return null; }
    }

    private static (Pools, Dictionary<uint, int>) IndexedPeople(ulong module)
    {
        var pools = new Pools(module, ReadPool(module + StaffList), ReadPool(module + PlayerList));
        if (scan?.Pools != pools)
        {
            Reset();
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            scan = new Scan(pools, cancellation, Task.Run(() => ScanPeople(pools, token)));
        }
        if (!scan.Completion.IsCompleted)
            throw new NativeAffinityException("正在核对原版人物身份，稍后会刷新亲密度。");
        var index = scan.Completion.GetAwaiter().GetResult();
        if (index.Slots is not { } slots)
            throw new NativeAffinityException(index.Error ?? Unreadable);

        return (pools, slots);
    }

    private static Pool ReadPool(ulong root)
    {
        var header = ReadBytes(root, 2 * PointerSize);
        var begin = BitConverter.ToUInt64(header, 0);
        var end = BitConverter.ToUInt64(header, PointerSize);
        if (!IsAddress(begin) || end < begin || begin % PointerSize != 0 || (end - begin) % PointerSize != 0 ||
            (end - begin) / PointerSize > MaxPeople)
            throw new NativeAffinityException(Unreadable);
        return new Pool(root, begin, end);
    }

    private static PersonIndex ScanPeople(Pools pools, CancellationToken cancellation)
    {
        try
        {
            var slots = new Dictionary<uint, int>();
            ScanPool(pools, slots, staff: true, cancellation);
            ScanPool(pools, slots, staff: false, cancellation);
            if (ReadPool(pools.Staff.Root) != pools.Staff || ReadPool(pools.Players.Root) != pools.Players)
                return new(null, "FM 的人物列表正在更新，请稍后重试。");
            return new(slots, null);
        }
        catch (Exception error) when (error is NativeAffinityException or OperationCanceledException)
        {
            return new(null, error is NativeAffinityException ? error.Message : Unreadable);
        }
    }

    private static void ScanPool(Pools pools, Dictionary<uint, int> slots, bool staff, CancellationToken cancellation)
    {
        var pool = staff ? pools.Staff : pools.Players;
        var pointers = ReadBytes(pool.Begin, (int)(pool.End - pool.Begin));
        var identity = new byte[16];
        // A page usually contains several native objects. Reuse its small snapshot
        // during indexing and fall back to a targeted read at page boundaries.
        var page = new byte[4096];
        ulong previousPage = 0;
        for (var index = 0; index < pointers.Length; index += PointerSize)
        {
            cancellation.ThrowIfCancellationRequested();
            var entry = BitConverter.ToUInt64(pointers, index);
            if (!ReadIndexIdentity(entry, identity, page, ref previousPage))
                continue;
            var vtable = BitConverter.ToUInt64(identity, 0);
            var personOffset = ResolvePersonOffset(vtable - pools.Module, staff, managerOnly: false);
            if (personOffset == 0)
                continue;
            if (!ReadIndexIdentity(entry + personOffset, identity, page, ref previousPage))
                continue;
            var id = BitConverter.ToUInt32(identity, (int)PersonId);
            var slot = index / PointerSize + 1;
            if (id is > 0 and < uint.MaxValue && !slots.TryAdd(id, staff ? -slot : slot))
            {
                // A player who also has a staff role appears in both pools. Only
                // merge them if both entries resolve to the very same person.
                // Keep the player slot so player-only lookups can still find it.
                var previous = slots[id];
                slots[id] = previous != 0 && SameIndexedPerson(pools, previous, entry + personOffset)
                    ? staff ? previous : slot : 0;
            }
        }
    }

    private static bool SameIndexedPerson(Pools pools, int index, ulong address)
    {
        var staff = index < 0;
        var pool = staff ? pools.Staff : pools.Players;
        var entry = ReadPointer(pool.Begin + (ulong)(Math.Abs(index) - 1) * PointerSize);
        var offset = ResolvePersonOffset(ReadPointer(entry) - pools.Module, staff, managerOnly: false);
        return offset != 0 && entry + offset == address;
    }

    private static bool ReadIndexIdentity(ulong at, byte[] identity, byte[] page, ref ulong previousPage)
    {
        if (!IsAddress(at))
            return false;
        var offset = (int)(at % (ulong)page.Length);
        var pageAt = at - (ulong)offset;
        if (offset + identity.Length > page.Length)
            return FmNativePromises.TryRead(at, identity);
        if (pageAt != previousPage)
        {
            previousPage = 0;
            if (!FmNativePromises.TryRead(pageAt, page))
                return FmNativePromises.TryRead(at, identity);
            previousPage = pageAt;
        }
        page.AsSpan(offset, identity.Length).CopyTo(identity);
        return true;
    }

    private static Person FindPerson(Pools pools, Dictionary<uint, int> slots, uint id, bool managerOnly)
    {
        if (!slots.TryGetValue(id, out var index))
            throw new NativeAffinityException("原版人物名单里没有核实到当前经理或联系人。");
        if (index == 0)
            throw new NativeAffinityException("同一人物 ID 对应多条原版记录，无法安全同步。");
        var staff = index < 0;
        var pool = staff ? pools.Staff : pools.Players;
        var slot = pool.Begin + (ulong)(Math.Abs(index) - 1) * PointerSize;
        var entry = ReadPointer(slot);
        var vtable = ReadPointer(entry);
        var offset = ResolvePersonOffset(vtable - pools.Module, staff, managerOnly);
        if (offset == 0 || managerOnly && !staff)
            throw new NativeAffinityException("当前人物的原版身份无法核实。");
        var address = entry + offset;
        var identity = ReadBytes(address, 16);
        if (BitConverter.ToUInt32(identity, (int)PersonId) != id)
        {
            Reset();
            throw new NativeAffinityException(Stale);
        }
        var resolved = new Person(id, pool.Root, slot, entry, vtable, address, BitConverter.ToUInt64(identity, 0));
        if (!IsCurrent(resolved))
            throw new NativeAffinityException(Stale);
        return resolved;
    }

    private static ulong ResolvePersonOffset(ulong vtable, bool staff, bool managerOnly) =>
        FmNativeLayout.Current.PersonOffset(vtable, staff, managerOnly);

    private static bool IsCurrent(Person person)
    {
        try
        {
            var header = ReadBytes(person.List, 2 * PointerSize);
            var start = BitConverter.ToUInt64(header, 0);
            var end = BitConverter.ToUInt64(header, PointerSize);
            if (!IsAddress(start) || end < start || person.Slot < start || person.Slot + PointerSize > end ||
                (person.Slot - start) % PointerSize != 0 ||
                ReadPointer(person.Slot) != person.Entry || ReadPointer(person.Entry) != person.Vtable)
                return false;
            var identity = ReadBytes(person.Address, 16);
            return BitConverter.ToUInt64(identity, 0) == person.PersonVtable &&
                BitConverter.ToUInt32(identity, (int)PersonId) == person.Id;
        }
        catch (NativeAffinityException)
        {
            return false;
        }
    }

    private static RelationList ReadRelations(Person manager, Person owner)
    {
        var holder = ReadPointer(owner.Address + PersonRelations);
        if (holder == 0)
            return new(manager, owner, 0, 0, 0, 0, Array.Empty<byte>(), Array.Empty<byte>(), -1, -1);
        if (holder % PointerSize != 0 ||
            Overlaps(holder, holder + HeaderSize, owner.Entry, owner.Address + PersonRelations + PointerSize) ||
            Overlaps(holder, holder + HeaderSize, manager.Entry, manager.Address + PersonRelations + PointerSize))
            throw new NativeAffinityException(Unreadable);
        var header = ReadBytes(holder, HeaderSize);
        var begin = BitConverter.ToUInt64(header, 0);
        var end = BitConverter.ToUInt64(header, PointerSize);
        var capacity = BitConverter.ToUInt64(header, 2 * PointerSize);
        if (begin == 0 && end == 0 && capacity == 0)
            return new(manager, owner, holder, 0, 0, 0, header, Array.Empty<byte>(), -1, -1);
        if (!IsAddress(begin) || !IsAddress(capacity) || end < begin || capacity < end ||
            begin % PointerSize != 0 || (end - begin) % RecordSize != 0 || (capacity - begin) % RecordSize != 0 ||
            (capacity - begin) / RecordSize > MaxRelations ||
            Overlaps(holder, holder + HeaderSize, begin, capacity) ||
            Overlaps(owner.Entry, owner.Address + PersonRelations + PointerSize, begin, capacity) ||
            Overlaps(manager.Entry, manager.Address + PersonRelations + PointerSize, begin, capacity))
            throw new NativeAffinityException(Unreadable);

        var records = ReadBytes(begin, (int)(end - begin));
        var like = -1;
        var dislike = -1;
        for (var index = 0; index < records.Length; index += RecordSize)
        {
            var type = records[index + TypeOffset];
            if (type is not (Likes or Dislikes) || BitConverter.ToUInt64(records, index) != manager.Address)
                continue;
            if (records[index + KindOffset] != PersonKind || records[index + LevelOffset] > 100 ||
                (type == Likes ? like : dislike) >= 0)
                throw new NativeAffinityException("当前经理的原版关系记录不唯一或级别无法核实。");
            if (type == Likes)
                like = index;
            else
                dislike = index;
        }
        return new(manager, owner, holder, begin, end, capacity, header, records, like, dislike);
    }

    private static List<Patch> Prepare(RelationList list, int next, bool permanentLike, ref ulong grown)
    {
        var patches = new List<Patch>(3);
        if (next == list.Affinity && !permanentLike && (next <= 0 || list.HasManagerReason))
            return patches;

        if (next >= 0)
            StageLevel(list, patches, list.Dislike, 0);
        if (next == 0)
            StageLevel(list, patches, list.Like, 0);
        else
        {
            var index = next < 0 ? list.Dislike : list.Like;
            var level = (byte)Math.Abs(next);
            if (index >= 0 && next > 0)
                StageLike(list, patches, index, level, permanentLike);
            else if (index >= 0)
                StageLevel(list, patches, index, level);
            else
                StageAppend(list, patches, next < 0 ? Dislikes : Likes, level, permanentLike, ref grown);
        }

        foreach (var patch in patches)
            if (!FmNativePromises.CanWrite(patch.Address, patch.After.Length))
                throw new NativeAffinityException("无法核实 FM 关系记录可以写入。");
        return patches;
    }

    private static void StageLike(RelationList list, List<Patch> patches, int index, byte level, bool permanent)
    {
        // Keep the relation's target and any existing flags unless permanence is requested.
        var field = index + ReasonOffset;
        var lastField = permanent ? FlagsOffset : LevelOffset;
        var before = list.Records.AsSpan(field, lastField - ReasonOffset + 1).ToArray();
        var after = (byte[])before.Clone();
        BitConverter.TryWriteBytes(after, ManagerReason);
        after[LevelOffset - ReasonOffset] = level;
        if (permanent)
            after[FlagsOffset - ReasonOffset] = PermanentFlag;
        if (!after.AsSpan().SequenceEqual(before))
            patches.Add(new(list.Begin + (ulong)field, before, after));
    }

    private static void StageLevel(RelationList list, List<Patch> patches, int index, byte level)
    {
        if (index < 0 || list.Records[index + LevelOffset] == level)
            return;
        var field = index + LevelOffset;
        patches.Add(new(list.Begin + (ulong)field, new[] { list.Records[field] }, new[] { level }));
    }

    private static void StageAppend(RelationList list, List<Patch> patches, byte type, byte level, bool permanent, ref ulong grown)
    {
        // Missing holders are constructed by FM before this plan is prepared.
        if (list.Address == 0)
            throw new NativeAffinityException("FM 尚未初始化此人的关系列表，暂时不能新增原版关系。");
        var record = new byte[RecordSize];
        BitConverter.TryWriteBytes(record.AsSpan(), list.Manager.Address);
        if (type == Likes)
            BitConverter.TryWriteBytes(record.AsSpan(ReasonOffset), ManagerReason);
        record[KindOffset] = PersonKind;
        record[TypeOffset] = type;
        record[LevelOffset] = level;
        if (permanent)
            record[FlagsOffset] = PermanentFlag;
        record[^1] = 0xFF;

        if (list.Capacity - list.End >= RecordSize)
        {
            patches.Add(new(list.End, ReadBytes(list.End, RecordSize), record));
            patches.Add(new(list.Address + PointerSize, BitConverter.GetBytes(list.End),
                BitConverter.GetBytes(list.End + RecordSize)));
            return;
        }

        var count = list.Records.Length / RecordSize;
        if (count >= MaxRelations)
            throw new NativeAffinityException("FM 的人物关系列表容量无法核实。");
        var capacity = Math.Min(MaxRelations, Math.Max(4, count * 2));
        var data = new byte[list.Records.Length + RecordSize];
        list.Records.CopyTo(data, 0);
        // Apply prior changes to the copied rows before publishing the new vector.
        foreach (var patch in patches)
            patch.After.CopyTo(data, (int)(patch.Address - list.Begin));
        record.CopyTo(data, list.Records.Length);
        try
        {
            grown = (ulong)Allocate((UIntPtr)1, (UIntPtr)FmRelationBufferAllocation.BytesForCapacity(capacity)).ToInt64();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new NativeAffinityException("未找到此 FM 版本用于关系数据的内存分配器。");
        }
        if (!IsAddress(grown) || !FmNativePromises.WriteBytes(grown, data))
            throw new NativeAffinityException("未能扩展 FM 的人物关系列表。");

        var header = new byte[HeaderSize];
        BitConverter.TryWriteBytes(header.AsSpan(0, PointerSize), grown);
        BitConverter.TryWriteBytes(header.AsSpan(PointerSize, PointerSize), grown + (ulong)data.Length);
        BitConverter.TryWriteBytes(header.AsSpan(2 * PointerSize, PointerSize), grown + (ulong)capacity * RecordSize);
        // Keep the original buffer intact. The game owns the published buffer.
        patches.Clear();
        patches.Add(new(list.Address, list.Header, header));
    }

    private static void CheckPeople(RelationList list)
    {
        if (!IsCurrent(list.Manager) || !IsCurrent(list.Owner) ||
            ReadPointer(list.Owner.Address + PersonRelations) != list.Address)
            throw new NativeAffinityException(Stale);
    }

    private static void CheckUnchanged(RelationList list)
    {
        CheckPeople(list);
        if (!ReadBytes(list.Address, list.Header.Length).AsSpan().SequenceEqual(list.Header) ||
            !ReadBytes(list.Begin, list.Records.Length).AsSpan().SequenceEqual(list.Records))
            throw new NativeAffinityException(Stale);
    }

    private static bool Restore(RelationList list, List<Patch> attempted)
    {
        try
        {
            CheckPeople(list);
            var header = ReadBytes(list.Address, HeaderSize);
            var headerPatch = attempted.Find(patch => patch.Address == list.Address ||
                patch.Address == list.Address + PointerSize);
            if (headerPatch is { } changed)
            {
                var offset = (int)(changed.Address - list.Address);
                list.Header.AsSpan(offset, changed.Before.Length).CopyTo(header.AsSpan(offset));
            }
            if (!header.AsSpan().SequenceEqual(list.Header))
                return false;
            for (var index = attempted.Count - 1; index >= 0; index--)
            {
                var patch = attempted[index];
                var actual = ReadBytes(patch.Address, patch.Before.Length);
                if (actual.AsSpan().SequenceEqual(patch.Before))
                    continue;
                // WriteProcessMemory can change a prefix even if it reports failure.
                var prefix = 0;
                while (prefix < actual.Length && actual[prefix] == patch.After[prefix])
                    prefix++;
                if (!actual.AsSpan(prefix).SequenceEqual(patch.Before.AsSpan(prefix)) ||
                    !FmNativePromises.WriteBytes(patch.Address, patch.Before))
                    return false;
            }
            CheckUnchanged(list);
            return true;
        }
        catch (NativeAffinityException)
        {
            return false;
        }
    }

    private static bool Overlaps(ulong first, ulong firstEnd, ulong second, ulong secondEnd) =>
        first < secondEnd && second < firstEnd;

    private static bool IsAddress(ulong address) => address is >= 0x10000 and < 0x800000000000;

    private static ulong ReadPointer(ulong address) => BitConverter.ToUInt64(ReadBytes(address, PointerSize), 0);

    private static byte[] ReadBytes(ulong address, int length)
    {
        if (length == 0)
            return Array.Empty<byte>();
        var bytes = new byte[length];
        return IsAddress(address) && FmNativePromises.TryRead(address, bytes)
            ? bytes : throw new NativeAffinityException(Unreadable);
    }

    // The pinned external editor traced ucrtbase!free for a grown relationship vector.
    [DllImport("ucrtbase.dll", EntryPoint = "calloc", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Allocate(UIntPtr count, UIntPtr size);

    [DllImport("ucrtbase.dll", EntryPoint = "free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(IntPtr address);
}
