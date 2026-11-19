using System;
using System.Text;

namespace FmPlayerTalk;

// The assigned contact is owned by the human manager, rather than selected by a
// translated job title, a neighbouring ID, or the club's player liaison role.
// This reader only observes an identified native layout; it never writes data.
internal static class FmNativeManagerContact
{
    private const int PointerSize = sizeof(ulong);
    private const int HeaderSize = PointerSize * 3;
    private const int SupportStaffSize = 0xA8;
    private const int PersonOffset = 0x88;
    private const int PersonIndexOffset = 0x90;
    private const int PublicIdOffset = 0x94;
    private const int BirthdayOffset = 0x60;
    private const int JobOffset = 0x79;
    private const byte AssignedContactJob = 82;
    private const int MaximumPeople = 250_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record Contact(ManagerAssistantRecord Record, uint ManagerId,
        ulong Module, ulong Manager, ulong ManagerPerson, ulong Entry,
        ulong PoolBegin, ulong PoolEnd, ulong PoolCapacity, ulong PoolSlot);

    private sealed record Pool(ulong Begin, ulong End, ulong Capacity);

    internal static Contact? Read(uint managerId, DateTime date)
    {
        if (managerId is 0 or uint.MaxValue || date.Year is < 1900 or > 2200)
            return null;
        var module = FmNativePromises.ModuleBase();
        if (module == 0 || !HasLayout() ||
            FmNativeAffinity.FindManagerRecord(module, managerId) is not { } manager ||
            !TryPointer(manager.Entry + FmNativeLayout.Current.ManagerContactOffset, out var entry) ||
            !IsPointer(entry) ||
            ReadPool(module) is not { } pool)
            return null;

        var slots = ReadBytes(pool.Begin, checked((int)(pool.End - pool.Begin)));
        if (slots == null)
            return null;
        ulong slot = 0;
        for (var offset = 0; offset < slots.Length; offset += PointerSize)
        {
            if (BitConverter.ToUInt64(slots, offset) != entry)
                continue;
            if (slot != 0)
                return null;
            slot = pool.Begin + (ulong)offset;
        }
        var data = slot == 0 ? null : ReadBytes(entry, SupportStaffSize);
        if (data == null || !ReadIdentity(module, data, out var id, out var index, out var birthday) ||
            id == managerId || birthday > date.Date || ReadName(data) is not { } name)
            return null;

        var record = new ManagerAssistantRecord(id, index, name, birthday, date.Date);
        var contact = new Contact(record, managerId, module, manager.Entry, manager.Person, entry,
            pool.Begin, pool.End, pool.Capacity, slot);
        return IsCurrent(contact, managerId) ? contact : null;
    }

    internal static bool IsCurrent(Contact contact, uint managerId)
    {
        if (contact.ManagerId != managerId || FmNativePromises.ModuleBase() != contact.Module || !HasLayout() ||
            FmNativeAffinity.FindManagerRecord(contact.Module, managerId) is not { } manager ||
            manager.Entry != contact.Manager || manager.Person != contact.ManagerPerson ||
            !TryPointer(contact.Manager + FmNativeLayout.Current.ManagerContactOffset, out var assigned) || assigned != contact.Entry ||
            ReadPool(contact.Module) is not { } pool || pool.Begin != contact.PoolBegin ||
            pool.End != contact.PoolEnd || pool.Capacity != contact.PoolCapacity ||
            contact.PoolSlot < pool.Begin || contact.PoolSlot >= pool.End ||
            (contact.PoolSlot - pool.Begin) % PointerSize != 0 ||
            !TryPointer(contact.PoolSlot, out var selected) || selected != contact.Entry)
            return false;

        var data = ReadBytes(contact.Entry, SupportStaffSize);
        if (data == null || !ReadIdentity(contact.Module, data, out var id, out var index, out var birthday) ||
            id != contact.Record.PublicId || index != contact.Record.PersonIndex || birthday != contact.Record.DateOfBirth ||
            ReadName(data) != contact.Record.Name)
            return false;

        // Native lists can be replaced while a career is loading or a person changes.
        return ReadPool(contact.Module) == pool &&
            TryPointer(contact.PoolSlot, out selected) && selected == contact.Entry &&
            TryPointer(contact.Manager + FmNativeLayout.Current.ManagerContactOffset, out assigned) && assigned == contact.Entry;
    }

    private static bool HasLayout() => FmNativeLayout.Current.SupportPeople != 0 &&
        FmNativeLayout.Current.SupportStaff != 0 && FmNativeLayout.Current.SupportPerson != 0 &&
        FmNativeLayout.Current.ManagerContactOffset != 0;

    private static Pool? ReadPool(ulong module)
    {
        var header = ReadBytes(module + FmNativeLayout.Current.SupportPeople, HeaderSize);
        if (header == null)
            return null;
        var begin = BitConverter.ToUInt64(header, 0);
        var end = BitConverter.ToUInt64(header, PointerSize);
        var capacity = BitConverter.ToUInt64(header, PointerSize * 2);
        if (!IsPointer(begin) || !IsPointer(end) || !IsPointer(capacity) || end < begin || capacity < end ||
            (end - begin) % PointerSize != 0 || (capacity - begin) % PointerSize != 0 ||
            (capacity - begin) / PointerSize > MaximumPeople)
            return null;
        return new(begin, end, capacity);
    }

    private static bool ReadIdentity(ulong module, byte[] data, out uint id, out int index, out DateTime birthday)
    {
        id = 0;
        index = -1;
        birthday = default;
        if (BitConverter.ToUInt64(data, 0) != module + FmNativeLayout.Current.SupportStaff ||
            BitConverter.ToUInt64(data, PersonOffset) != module + FmNativeLayout.Current.SupportPerson ||
            data[JobOffset] != AssignedContactJob)
            return false;
        id = BitConverter.ToUInt32(data, PublicIdOffset);
        index = BitConverter.ToInt32(data, PersonIndexOffset);
        var packed = BitConverter.ToUInt32(data, BirthdayOffset);
        var year = (int)(packed >> 16);
        var day = (int)(packed & 0x1FF);
        if (id is 0 or uint.MaxValue || index is < 0 or >= 32_000_000 ||
            year is < 1800 or > 2200 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
            return false;
        birthday = new DateTime(year, 1, 1).AddDays(day - 1);
        return true;
    }

    private static string? ReadName(byte[] data)
    {
        var commonEntry = BitConverter.ToUInt64(data, 0x38);
        if (commonEntry != 0 && ReadNameEntry(commonEntry) is { } common)
            return common;
        var firstEntry = BitConverter.ToUInt64(data, 0x28);
        var lastEntry = BitConverter.ToUInt64(data, 0x30);
        var first = ReadNameEntry(firstEntry);
        var last = ReadNameEntry(lastEntry);
        if (firstEntry != 0 && first == null || lastEntry != 0 && last == null)
            return null;
        var name = (first + " " + last).Trim();
        return name.Length is > 0 and <= 160 ? name : null;
    }

    private static string? ReadNameEntry(ulong entry)
    {
        if (!TryPointer(entry, out var text))
            return null;
        var lengthBytes = ReadBytes(text, sizeof(int));
        if (lengthBytes == null)
            return null;
        var count = BitConverter.ToInt32(lengthBytes, 0);
        if (count is <= 0 or > 320 || ReadBytes(text + sizeof(int), count) is not { } bytes)
            return null;
        try
        {
            var name = StrictUtf8.GetString(bytes).TrimEnd('\0').Trim();
            if (name.Length is <= 0 or > 160)
                return null;
            foreach (var character in name)
                if (char.IsControl(character))
                    return null;
            return name;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool IsPointer(ulong value) => value is >= 0x10000 and < 0x800000000000 && value % PointerSize == 0;

    private static bool TryPointer(ulong address, out ulong value)
    {
        var bytes = ReadBytes(address, PointerSize);
        value = bytes == null ? 0 : BitConverter.ToUInt64(bytes, 0);
        // SI's length-prefixed UTF-8 text can begin at an unaligned address.
        return value is >= 0x10000 and < 0x800000000000;
    }

    private static byte[]? ReadBytes(ulong address, int length)
    {
        if (length < 0)
            return null;
        var bytes = new byte[length];
        return FmNativePromises.TryRead(address, bytes) ? bytes : null;
    }
}
