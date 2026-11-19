using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FmPlayerTalk;

internal sealed record FmPromiseReadResult(bool Available, IReadOnlyList<FmPromiseOption> Promises, string? Reason);
internal sealed record FmPromiseRemoveResult(bool Succeeded, string? Reason, bool OutcomeUncertain = false);

internal sealed record FmPromiseOption(string Key, ushort TypeId, string Title, DateTime Made, DateTime Deadline)
{
    internal FmNativePromises.Baseline? NativeBaseline { get; init; }
    internal bool DeadlineIsUnique { get; init; }
}

// The caller checks the active career, current club roster and idle game state on
// Unity's main thread. This backend revalidates their IDs against live records.
internal static class FmNativePromises
{
    private static ulong InteractionRootRva => FmNativeLayout.Current.Interactions;
    private const ulong GlobalListOffset = 0xF8;
    private const ulong PersonOffset = 0x288;
    private const ulong HolderOffset = 0x360 - PersonOffset;
    private const ulong EntityUidOffset = 0xC;
    private static ulong PlayerVtableRva => FmNativeLayout.Current.Player;
    private static ulong AlternatePlayerVtableRva => FmNativeLayout.Current.AlternatePlayer;
    private static ulong ClubVtableRva => FmNativeLayout.Current.Club;
    private static ulong TeamVtableRva => FmNativeLayout.Current.Team;
    private const ulong TeamClubOffset = 0x30;
    private const int RecordSize = 0x60;
    private const int ManagerOffset = 0x18;
    private const int OwnerOffset = 0x20;
    private const int ClubOffset = 0x28;
    private const int MadeOffset = 0x38;
    private const int DeadlineOffset = 0x3C;
    private const int FirstParameterOffset = 0x40;
    private const int SecondParameterOffset = 0x44;
    private const int TypeOffset = 0x4E;
    private const ushort PreferredPositionAndRole = 208;
    private const int PointerSize = sizeof(ulong);
    private const int HeaderSize = PointerSize * 3;
    private const int MaxRecords = 50_000;
    private const int MaxCapacity = 1_000_000;
    private const string Stale = "游戏里的承诺已变化，请重新确认。";

    internal sealed record Baseline(uint ManagerId, uint PlayerId, uint ClubId, ulong Address, string Record);

    private sealed record Vector(ulong Address, ulong Begin, ulong End, ulong Capacity, byte[] Slots)
    {
        public byte[] Header()
        {
            var bytes = new byte[HeaderSize];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, PointerSize), Begin);
            BitConverter.TryWriteBytes(bytes.AsSpan(PointerSize, PointerSize), End);
            BitConverter.TryWriteBytes(bytes.AsSpan(2 * PointerSize, PointerSize), Capacity);
            return bytes;
        }
    }

    private sealed record Candidate(FmPromiseOption Option, Vector[] Lists);
    private sealed record Patch(ulong Address, byte[] Before, byte[] After, bool EndsVector = false);
    private sealed class NativeStateException : Exception
    {
        internal NativeStateException(string reason) : base(reason) { }
    }

    internal static bool IsSupported => ModuleBase() != 0 && InteractionRootRva != 0;

    // An empty result means no eligible, verified type-208 record in this scope.
    internal static FmPromiseReadResult Read(uint managerId, uint playerId, uint clubId, DateTime gameDate)
    {
        var module = ModuleBase();
        if (module == 0 || InteractionRootRva == 0)
            return new(false, Array.Empty<FmPromiseOption>(), "当前游戏版本尚未验证承诺功能。");
        if (!ValidScope(managerId, playerId, clubId))
            return new(false, Array.Empty<FmPromiseOption>(), "当前生涯的球员身份尚未确认。");

        try
        {
            return new(true, FindCandidates(module, managerId, playerId, clubId, gameDate)
                .Select(candidate => candidate.Option).ToArray(), null);
        }
        catch (NativeStateException error)
        {
            return new(false, Array.Empty<FmPromiseOption>(), error.Message);
        }
    }

    internal static FmPromiseRemoveResult Remove(FmPromiseOption expected, DateTime gameDate, Func<bool> commit)
    {
        if (expected.NativeBaseline is not { } baseline || commit == null)
            return new(false, Stale);

        var module = ModuleBase();
        if (module == 0 || InteractionRootRva == 0)
            return new(false, "当前游戏版本尚未验证承诺功能。");
        if (!ValidScope(baseline.ManagerId, baseline.PlayerId, baseline.ClubId))
            return new(false, Stale);

        var attempted = new List<Patch>(6);
        Vector[]? originalLists = null;
        try
        {
            var candidate = FindCandidates(module, baseline.ManagerId, baseline.PlayerId, baseline.ClubId, gameDate)
                .SingleOrDefault(item => item.Option.Key == expected.Key);
            if (candidate?.Option.NativeBaseline != baseline ||
                candidate.Option.DeadlineIsUnique != expected.DeadlineIsUnique)
                return new(false, Stale);

            var patches = PrepareRemoval(candidate.Lists, baseline.Address);
            CheckWritableRanges(candidate.Lists, baseline.Address, patches);
            CheckCurrent(module, baseline, candidate.Lists);
            originalLists = candidate.Lists;
            foreach (var patch in patches)
            {
                if (!ReadBytes(patch.Address, patch.Before.Length).AsSpan().SequenceEqual(patch.Before))
                    throw new NativeStateException(Stale);
                attempted.Add(patch);
                if (!WriteBytes(patch.Address, patch.After))
                    throw new NativeStateException("未能核实游戏已移除这条承诺。");
            }

            foreach (var list in candidate.Lists)
                CheckRemoval(list, baseline.Address);
            CheckIdentity(module, baseline, candidate.Lists);
        }
        catch (NativeStateException error)
        {
            return Failed(error.Message, module, baseline, originalLists, attempted);
        }

        try
        {
            if (commit())
                return new(true, null);
        }
        catch (Exception)
        {
            return Failed("未能保存承诺变更的对话记录。", module, baseline, originalLists, attempted);
        }
        return Failed("未能保存承诺变更的对话记录。", module, baseline, originalLists, attempted);
    }

    private static FmPromiseRemoveResult Failed(string reason, ulong module, Baseline baseline, Vector[]? originalLists,
        List<Patch> attempted) =>
        attempted.Count == 0 || originalLists != null && Restore(module, baseline, originalLists, attempted)
            ? new(false, reason)
            : new(false, "无法核实承诺已恢复，请重新载入这次修改前的存档。", OutcomeUncertain: true);

    private static bool ValidScope(uint managerId, uint playerId, uint clubId) =>
        managerId is > 0 and < uint.MaxValue && playerId is > 0 and < uint.MaxValue &&
        clubId is > 0 and < uint.MaxValue && managerId != playerId;

    private static Candidate[] FindCandidates(ulong module, uint managerId, uint playerId, uint clubId, DateTime gameDate)
    {
        var root = ReadPointer(module + InteractionRootRva);
        if (!IsAddress(root))
            throw new NativeStateException("游戏尚未加载承诺列表。");
        var global = ReadVector(root + GlobalListOffset);
        var options = new List<Candidate>();
        var sameDeadline = new Dictionary<DateTime, int>();
        var sameDates = new Dictionary<(DateTime Made, DateTime Deadline), int>();
        var record = new byte[RecordSize];
        var date = gameDate.Date;
        for (var offset = 0; offset < global.Slots.Length; offset += PointerSize)
        {
            var address = BitConverter.ToUInt64(global.Slots, offset);
            if (address == 0)
                continue;
            RequireRead(address, record);
            if (ReadDate(record, DeadlineOffset) is not { } deadline || deadline <= date)
                continue;
            var owner = BitConverter.ToUInt64(record, OwnerOffset);
            var manager = BitConverter.ToUInt64(record, ManagerOffset);
            var club = BitConverter.ToUInt64(record, ClubOffset);
            if (!MatchesScope(module, manager, owner, club, managerId, playerId, clubId))
                continue;

            // The native UI exposes days elapsed and remaining. Refuse an ambiguous
            // date match even when the other record is of an unsupported type.
            sameDeadline[deadline] = sameDeadline.GetValueOrDefault(deadline) + 1;
            if (ReadDate(record, MadeOffset) is not { } made)
                continue;
            var dates = (made, deadline);
            sameDates[dates] = sameDates.GetValueOrDefault(dates) + 1;

            // The native report and the reload trial verified type 208.
            // The record bytes do not expose a verified terminal-state field.
            if (BitConverter.ToUInt16(record, TypeOffset) != PreferredPositionAndRole || made > date)
                continue;
            var lists = new[] { ReadHolder(owner), ReadHolder(manager), global };
            if (lists.Select(list => list.Address).Distinct().Count() != lists.Length ||
                lists.Any(list => ReferenceIndex(list, address) < 0) ||
                BitConverter.ToUInt32(record, 0) != lists.Length)
                continue;
            var semantic = FormattableString.Invariant(
                $"{managerId}:{playerId}:{clubId}:{PreferredPositionAndRole}:{made:yyyy-MM-dd}:{deadline:yyyy-MM-dd}:{BitConverter.ToInt32(record, FirstParameterOffset)}:{BitConverter.ToInt32(record, SecondParameterOffset)}");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(semantic)));
            var option = new FmPromiseOption(key, PreferredPositionAndRole, "按约定位置和角色出场", made, deadline)
            {
                NativeBaseline = new Baseline(managerId, playerId, clubId, address, Convert.ToHexString(record))
            };
            options.Add(new Candidate(option, lists));
        }

        return options.Where(item => sameDates[(item.Option.Made, item.Option.Deadline)] == 1)
            .Select(item => item with
            {
                Option = item.Option with { DeadlineIsUnique = sameDeadline[item.Option.Deadline] == 1 }
            }).ToArray();
    }

    private static bool MatchesScope(ulong module, ulong manager, ulong owner, ulong club,
        uint managerId, uint playerId, uint clubId)
    {
        if (!IsAddress(manager) || !IsAddress(owner) || !IsAddress(club) || owner == manager ||
            ReadUInt32(owner + EntityUidOffset) != playerId || ReadUInt32(manager + EntityUidOffset) != managerId ||
            !MatchesClub(module, club, clubId))
            return false;
        return ReadPointer(owner - PersonOffset) == module + PlayerVtableRva ||
            ReadPointer(owner - 0x380) == module + AlternatePlayerVtableRva;
    }

    private static bool MatchesClub(ulong module, ulong recordClub, uint clubId)
    {
        // A native record can name a squad here, with the club on the squad itself.
        var vtable = ReadPointer(recordClub);
        var club = vtable == module + TeamVtableRva ? ReadPointer(recordClub + TeamClubOffset) : recordClub;
        return IsAddress(club) && ReadPointer(club) == module + ClubVtableRva &&
            ReadUInt32(club + EntityUidOffset) == clubId;
    }

    private static Vector ReadHolder(ulong person) => ReadVector(ReadPointer(person + HolderOffset));

    private static Vector ReadVector(ulong address)
    {
        var header = ReadBytes(address, HeaderSize);
        var begin = BitConverter.ToUInt64(header, 0);
        var end = BitConverter.ToUInt64(header, PointerSize);
        var capacity = BitConverter.ToUInt64(header, 2 * PointerSize);
        if (begin == 0 && end == 0 && capacity == 0)
            return new Vector(address, 0, 0, 0, Array.Empty<byte>());
        if (!IsAddress(begin) || !IsAddress(end) || !IsAddress(capacity) || end < begin || capacity < end ||
            begin % PointerSize != 0 || end % PointerSize != 0 || capacity % PointerSize != 0 ||
            end - begin > MaxRecords * PointerSize || capacity - begin > MaxCapacity * PointerSize)
            throw new NativeStateException("无法核实游戏的承诺列表。");
        return new Vector(address, begin, end, capacity, ReadBytes(begin, (int)(end - begin)));
    }

    private static int ReferenceIndex(Vector list, ulong address)
    {
        var found = -1;
        for (var offset = 0; offset < list.Slots.Length; offset += PointerSize)
        {
            if (BitConverter.ToUInt64(list.Slots, offset) != address)
                continue;
            if (found >= 0)
                return -2;
            found = offset;
        }
        return found;
    }

    private static Patch[] PrepareRemoval(Vector[] lists, ulong target)
    {
        var patches = new List<Patch>(6);
        foreach (var list in lists)
        {
            var index = ReferenceIndex(list, target);
            if (index < 0)
                throw new NativeStateException(Stale);
            var shift = list.Slots.Length - index - PointerSize;
            if (shift > 0)
                patches.Add(new Patch(list.Begin + (ulong)index, list.Slots.AsSpan(index, shift).ToArray(),
                    list.Slots.AsSpan(index + PointerSize, shift).ToArray()));
            patches.Add(new Patch(list.Address + PointerSize, BitConverter.GetBytes(list.End),
                BitConverter.GetBytes(list.End - PointerSize), EndsVector: true));
        }
        return patches.ToArray();
    }

    private static void CheckWritableRanges(Vector[] lists, ulong target, Patch[] patches)
    {
        var ranges = lists.SelectMany(list => new[]
        {
            (Start: list.Address, End: list.Address + HeaderSize),
            (Start: list.Begin, End: list.Capacity)
        }).Append((Start: target, End: target + RecordSize)).OrderBy(range => range.Start).ToArray();
        for (var index = 1; index < ranges.Length; index++)
            if (ranges[index - 1].End > ranges[index].Start)
                throw new NativeStateException(Stale);
        if (patches.Any(patch => !CanWrite(patch.Address, patch.Before.Length)))
            throw new NativeStateException("无法核实当前承诺列表可安全写入。");
    }

    private static void CheckCurrent(ulong module, Baseline baseline, Vector[] lists)
    {
        CheckIdentity(module, baseline, lists);
        foreach (var list in lists)
            if (!ReadBytes(list.Address, HeaderSize).AsSpan().SequenceEqual(list.Header()) ||
                !ReadBytes(list.Begin, list.Slots.Length).AsSpan().SequenceEqual(list.Slots))
                throw new NativeStateException(Stale);
    }

    private static void CheckIdentity(ulong module, Baseline baseline, Vector[] lists)
    {
        var record = ReadBytes(baseline.Address, RecordSize);
        var manager = BitConverter.ToUInt64(record, ManagerOffset);
        var owner = BitConverter.ToUInt64(record, OwnerOffset);
        var club = BitConverter.ToUInt64(record, ClubOffset);
        if (!record.AsSpan().SequenceEqual(Convert.FromHexString(baseline.Record)) ||
            !MatchesScope(module, manager, owner, club, baseline.ManagerId, baseline.PlayerId, baseline.ClubId) ||
            ReadPointer(owner + HolderOffset) != lists[0].Address ||
            ReadPointer(manager + HolderOffset) != lists[1].Address ||
            ReadPointer(module + InteractionRootRva) + GlobalListOffset != lists[2].Address)
            throw new NativeStateException(Stale);
    }

    private static void CheckRemoval(Vector before, ulong target)
    {
        var after = ReadVector(before.Address);
        var offset = ReferenceIndex(before, target);
        if (offset < 0 || after.Begin != before.Begin || after.End != before.End - PointerSize ||
            after.Capacity != before.Capacity || ReferenceIndex(after, target) != -1 ||
            !after.Slots.AsSpan(0, offset).SequenceEqual(before.Slots.AsSpan(0, offset)) ||
            !after.Slots.AsSpan(offset).SequenceEqual(before.Slots.AsSpan(offset + PointerSize)))
            throw new NativeStateException("未能核实游戏已移除这条承诺。");
    }

    private static bool Restore(ulong module, Baseline baseline, Vector[] lists, List<Patch> patches)
    {
        try
        {
            CheckIdentity(module, baseline, lists);
            foreach (var list in lists)
            {
                var header = ReadBytes(list.Address, HeaderSize);
                var end = BitConverter.ToUInt64(header, PointerSize);
                if (BitConverter.ToUInt64(header, 0) != list.Begin ||
                    BitConverter.ToUInt64(header, 2 * PointerSize) != list.Capacity ||
                    end != list.End && end != list.End - PointerSize)
                    return false;
            }
            if (!RestorePatches(patches))
                return false;
            CheckCurrent(module, baseline, lists);
            return true;
        }
        catch (NativeStateException)
        {
            return false;
        }
    }

    private static bool RestorePatches(List<Patch> patches)
    {
        var restored = true;
        var canRestoreEnd = true;
        foreach (var patch in patches)
        {
            if (patch.EndsVector && !canRestoreEnd)
            {
                // Do not expand a vector unless its original live slots are in place.
                canRestoreEnd = true;
                continue;
            }
            if (!RestorePatch(patch))
            {
                restored = false;
                canRestoreEnd = patch.EndsVector;
            }
        }
        return restored;
    }

    private static bool RestorePatch(Patch patch)
    {
        var observed = new byte[patch.Before.Length];
        if (!TryRead(patch.Address, observed))
            return false;
        if (observed.AsSpan().SequenceEqual(patch.Before))
            return true;
        // A failed write may have written a prefix of our data. Refuse other changes.
        var prefix = 0;
        while (prefix < observed.Length && observed[prefix] == patch.After[prefix])
            prefix++;
        return observed.AsSpan(prefix).SequenceEqual(patch.Before.AsSpan(prefix)) &&
            WriteBytes(patch.Address, patch.Before);
    }

    private static DateTime? ReadDate(byte[] data, int offset)
    {
        var day = BitConverter.ToUInt16(data, offset) & 0x1FF;
        var year = BitConverter.ToUInt16(data, offset + sizeof(ushort));
        if (year is < 1900 or > 2200 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
            return null;
        return new DateTime(year, 1, 1).AddDays(day - 1);
    }

    internal static ulong ModuleBase()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return 0;
        var handle = GetModuleHandle("game_plugin.dll");
        if (handle == IntPtr.Zero)
            return 0;
        var address = (ulong)handle.ToInt64();
        var dos = new byte[0x40];
        if (!TryRead(address, dos) || BitConverter.ToUInt16(dos, 0) != 0x5A4D)
            return 0;
        var peOffset = BitConverter.ToInt32(dos, 0x3C);
        var pe = new byte[0x54];
        if (peOffset is < 0x40 or > 0x1000 || !TryRead(address + (ulong)peOffset, pe) ||
            BitConverter.ToUInt32(pe, 0) != 0x4550 || BitConverter.ToUInt16(pe, 4) != 0x8664 ||
            BitConverter.ToUInt16(pe, 0x18) != 0x20B ||
            FmNativeLayout.Match(BitConverter.ToUInt32(pe, 0x8), BitConverter.ToUInt32(pe, 0x50)) is not { } layout)
            return 0;
        var expected = Encoding.ASCII.GetBytes(layout.Build + "\0");
        var actual = new byte[expected.Length];
        if (!TryRead(address + layout.BuildString, actual) || !actual.AsSpan().SequenceEqual(expected))
            return 0;
        FmNativeLayout.Select(layout);
        return address;
    }

    private static bool IsAddress(ulong address) => address is >= 0x10000 and < 0x800000000000;

    private static ulong ReadPointer(ulong address) => BitConverter.ToUInt64(ReadBytes(address, PointerSize), 0);
    private static uint ReadUInt32(ulong address) => BitConverter.ToUInt32(ReadBytes(address, sizeof(uint)), 0);

    private static byte[] ReadBytes(ulong address, int length)
    {
        if (length == 0)
            return Array.Empty<byte>();
        var bytes = new byte[length];
        RequireRead(address, bytes);
        return bytes;
    }

    private static void RequireRead(ulong address, byte[] bytes)
    {
        if (!TryRead(address, bytes))
            throw new NativeStateException("无法核实游戏的承诺数据。");
    }

    internal static bool TryRead(ulong address, byte[] bytes) =>
        IsAddress(address) && ReadProcessMemory(new IntPtr(-1), new IntPtr((long)address), bytes, (UIntPtr)bytes.Length,
            out var read) && read.ToUInt64() == (ulong)bytes.Length;

    internal static bool WriteBytes(ulong address, byte[] bytes)
    {
        if (!CanWrite(address, bytes.Length) ||
            !WriteProcessMemory(new IntPtr(-1), new IntPtr((long)address), bytes, (UIntPtr)bytes.Length, out var written) ||
            written.ToUInt64() != (ulong)bytes.Length)
            return false;
        var readback = new byte[bytes.Length];
        return TryRead(address, readback) && readback.AsSpan().SequenceEqual(bytes);
    }

    internal static bool CanWrite(ulong address, int length)
    {
        if (!IsAddress(address) || length <= 0 ||
            VirtualQuery(new IntPtr((long)address), out var region, (UIntPtr)Marshal.SizeOf<MemoryRegion>()) == UIntPtr.Zero)
            return false;
        const uint committed = 0x1000;
        const uint readWrite = 0x04;
        var regionBase = (ulong)region.Base.ToInt64();
        var end = address + (ulong)length;
        return region.State == committed && region.Protection == readWrite &&
            address >= regionBase && end <= regionBase + region.Size.ToUInt64();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        internal IntPtr Base;
        internal IntPtr AllocationBase;
        internal uint AllocationProtection;
        internal ushort PartitionId;
        internal UIntPtr Size;
        internal uint State;
        internal uint Protection;
        internal uint Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] bytes, UIntPtr size,
        out UIntPtr read);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size,
        out UIntPtr written);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryRegion information, UIntPtr length);
}
