using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FmPlayerTalk;

internal sealed record ManagerAssistantRecord(uint PublicId, int PersonIndex, string Name,
    DateTime DateOfBirth, DateTime SaveDate);

internal static class ManagerAssistantSaveReader
{
    private const int HumanManagerKind = 9;
    private const int GeneratedPersonKind = 8;
    private const int PersonHeaderSize = 13;
    private const uint UnsetDate = 0x076C0001; // 1 January 1900 in FM's packed date format.
    private sealed record BuildProfile(string Version, uint Number)
    {
        internal readonly byte[] Text = Encoding.ASCII.GetBytes(Version);
    }
    private static readonly BuildProfile[] VerifiedBuilds =
    {
        new("27.0.1+2503222", 2503222),
        new("27.1.0+2505117", 2505117)
    };
    private static readonly byte[] ManagerFieldTail = Convert.FromHexString("01006C07000000000000000000000000");
    private static readonly byte[] ManagerFieldNextList = Convert.FromHexString("0A00000001");
    private static readonly byte[] NamePoolPrefix = Convert.FromHexString("55B5000000");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Task<ManagerAssistantRecord?> ReadAsync(string savePath, uint expectedManagerPublicId,
        CancellationToken cancellationToken) =>
        Task.Run(() => ReadCandidate(savePath, expectedManagerPublicId, cancellationToken), cancellationToken);

    internal static Task<ManagerAssistantRecord?> ReadMatchingAsync(string[] directories, uint managerId,
        uint clubId, DateTime gameDate, CancellationToken token) => Task.Run(() =>
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ManagerAssistantRecord? match = null;
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.fm"))
                {
                    token.ThrowIfCancellationRequested();
                    if (!files.Add(file))
                        continue;
                    if (files.Count > 256)
                        return null;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var found = ReadCandidate(file, managerId, token, clubId, gameDate);
                    if (found == null)
                        continue;
                    if (match != null && match != found)
                        return null;
                    match = found;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return match;
    }, token);

    private static ManagerAssistantRecord? ReadCandidate(string savePath, uint expectedManagerPublicId,
        CancellationToken cancellationToken, uint? clubId = null, DateTime? gameDate = null)
    {
        try
        {
            return Read(savePath, expectedManagerPublicId, cancellationToken, clubId, gameDate);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            DecoderFallbackException or OverflowException or NotSupportedException or TypeLoadException)
        {
            return null;
        }
    }

    private static ManagerAssistantRecord? Read(string savePath, uint expectedManagerPublicId, CancellationToken token,
        uint? clubId, DateTime? gameDate)
    {
        if (expectedManagerPublicId == 0 || string.IsNullOrWhiteSpace(savePath))
            return null;

        using var save = ManagerAssistantSaveFile.Open(savePath, token);
        var summary = save.ReadSection("save_game_summary", 31, 4 * 1024 * 1024, token);
        var date = ReadSummaryDate(summary, out var savedClub, out var build);
        if (date == null || build == null || (clubId.HasValue && clubId != savedClub) ||
            (gameDate.HasValue && gameDate.Value.Date != date))
            return null;

        var info = save.ReadSection("game_info", 47, 4 * 1024 * 1024, token);
        if (!IsVerifiedGameInfo(info, build))
            return null;
        var humans = save.ReadSection("humans", 24, 32 * 1024 * 1024, token);
        // This layout has only been validated for a single active human manager.
        if (humans.Length < 14 || U16(humans, 8) != 1)
            return null;
        var managerSelector = U32(humans, 10);
        if (managerSelector < 2 || managerSelector >= int.MaxValue)
            return null;

        var game = save.ReadSection("game_db", 4000, 768 * 1024 * 1024, token);
        var start = FindPersonHeader(game, managerSelector - 1, HumanManagerKind, 0, game.Length,
            null, out _, token);
        if (start < 0)
            return null;
        var end = FindPersonHeader(game, managerSelector, GeneratedPersonKind, start + PersonHeaderSize,
            game.Length, expectedManagerPublicId, out _, token);
        if (end < 0)
            return null;

        // The kind-9 manager record owns this person selector. Following that field keeps the
        // assistant independent of the club or the numbering of neighboring generated people.
        var assistantSelector = FindAssistantSelector(game, start + PersonHeaderSize, end, date.Value, token);
        if (assistantSelector < 2 || assistantSelector == managerSelector || assistantSelector >= int.MaxValue)
            return null;
        var assistantEnd = FindPersonHeader(game, assistantSelector, GeneratedPersonKind, 0, game.Length,
            null, out var assistantId, token);
        if (assistantEnd < 0 || assistantId == expectedManagerPublicId)
            return null;
        var assistantStart = FindPersonHeader(game, assistantSelector - 1, GeneratedPersonKind, 0, assistantEnd,
            null, out _, token);
        if (assistantStart < 0 || assistantEnd - assistantStart < 75 || assistantEnd - assistantStart > 190)
            return null;

        var nameStart = assistantStart + 19;
        if (U32(game, assistantStart + 13) != uint.MaxValue || U16(game, assistantStart + 17) != 82 ||
            game[nameStart + 4] != 0 || game[nameStart + 9] != 0 || game[nameStart + 14] != 0)
            return null;
        var legalNameLength = U32(game, nameStart + 15);
        if (legalNameLength > 80 || legalNameLength == 1)
            return null;
        var birthdayAt = nameStart + 19 + (int)legalNameLength;
        if (birthdayAt + 40 > assistantEnd || U16(game, birthdayAt) >= 512 ||
            !AllZero(game.AsSpan(birthdayAt + 15, 7)))
            return null;
        var birthday = ReadDate(game, birthdayAt);
        if (birthday == null || birthday > date)
            return null;
        foreach (var score in game.AsSpan(birthdayAt + 22, 8))
            if (score > 20)
                return null;

        var firstId = U32(game, nameStart);
        var lastId = U32(game, nameStart + 5);
        var commonId = U32(game, nameStart + 10);
        if (!ReadNames(game, firstId, lastId, commonId, out var name, token))
            return null;
        if (string.IsNullOrWhiteSpace(name) && legalNameLength > 0)
            name = StrictUtf8.GetString(game, nameStart + 19, (int)legalNameLength);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || HasControlCharacters(name))
            return null;
        token.ThrowIfCancellationRequested();
        if (!save.IsUnchanged())
            return null;
        // The serialized selector is the native PersonReference index. The header
        // before the record uses selector-1, but the person itself does not.
        return new ManagerAssistantRecord(assistantId, checked((int)assistantSelector), name,
            birthday.Value, date.Value);
    }

    private static int FindPersonHeader(byte[] game, uint selector, int kind, int start, int end,
        uint? requiredId, out uint publicId, CancellationToken token)
    {
        publicId = 0;
        var found = -1;
        Span<byte> marker = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(marker, selector);
        while (start <= end - PersonHeaderSize)
        {
            token.ThrowIfCancellationRequested();
            var window = Math.Min(end - start, 16 * 1024 * 1024);
            var relative = game.AsSpan(start, window).IndexOf(marker);
            if (relative < 0)
            {
                start += window - (marker.Length - 1);
                continue;
            }
            var at = start + relative;
            start = at + 1;
            if (at > end - PersonHeaderSize || game[at + 12] != kind)
                continue;
            var uid = U32(game, at + 4);
            if (uid == 0 || uid == uint.MaxValue || uid != U32(game, at + 8) ||
                (requiredId != null && uid != requiredId))
                continue;
            if (found >= 0)
            {
                publicId = 0;
                return -1;
            }
            found = at;
            publicId = uid;
        }
        return found;
    }

    private static uint FindAssistantSelector(byte[] game, int start, int end, DateTime date,
        CancellationToken token)
    {
        var managerBodyStart = start;
        var found = 0u;
        while (start <= end - ManagerFieldTail.Length)
        {
            token.ThrowIfCancellationRequested();
            var foundTail = game.AsSpan(start, end - start).IndexOf(ManagerFieldTail);
            if (foundTail < 0)
                break;
            var at = start + foundTail - 16;
            start += foundTail + 1;
            if (at < managerBodyStart + 13 || at + 41 > end || game[at - 5] != 6 ||
                U32(game, at - 13) != uint.MaxValue || !game.AsSpan(at + 36, 5).SequenceEqual(ManagerFieldNextList) ||
                ReadDate(game, at - 4) is not DateTime started || started > date ||
                (U32(game, at + 8) != UnsetDate && ReadDate(game, at + 8) == null) ||
                ReadDate(game, at + 12) == null)
                continue;
            var selector = U32(game, at);
            if (selector < 2 || selector >= int.MaxValue)
                continue;
            if (found != 0)
                return 0;
            found = selector;
        }
        return found;
    }

    private static bool ReadNames(byte[] game, uint firstId, uint lastId, uint commonId, out string? name,
        CancellationToken token)
    {
        name = null;
        var found = false;
        for (var cursor = 0; cursor <= game.Length - 24;)
        {
            token.ThrowIfCancellationRequested();
            var window = Math.Min(game.Length - cursor, 16 * 1024 * 1024);
            var relative = game.AsSpan(cursor, window).IndexOf(NamePoolPrefix);
            if (relative < 0)
            {
                cursor += window - (NamePoolPrefix.Length - 1);
                continue;
            }
            var at = cursor + relative;
            cursor = at + 1;
            if (at > game.Length - 24)
                break;
            var scale = U32(game, at + 12) / 256;
            if (scale < 1 || scale > 1024 || U32(game, at + 12) != scale * 256 ||
                (U32(game, at + 8) & 0xFFFFFF00) != scale * 1024 || U32(game, at + 16) != scale * 2048 ||
                U32(game, at + 20) != 0)
                continue;

            var pool = at + 24;
            if (!ReadNamePool(game, ref pool, firstId, out var first, token) ||
                !ReadNamePool(game, ref pool, lastId, out var last, token) ||
                !ReadNamePool(game, ref pool, commonId, out var common, token))
                continue;
            if (found)
                return false;
            found = true;
            name = common ?? string.Join(" ", first ?? "", last ?? "").Trim();
        }
        return found;
    }

    private static bool ReadNamePool(byte[] game, ref int cursor, uint desired, out string? name,
        CancellationToken token)
    {
        name = null;
        if (cursor > game.Length - 4)
            return false;
        var count = U32(game, cursor);
        cursor += 4;
        if (count > (game.Length - cursor) / 8 || (desired != uint.MaxValue && desired >= count))
            return false;
        for (var row = 0u; row < count; row++)
        {
            if ((row & 0xFFF) == 0)
                token.ThrowIfCancellationRequested();
            if (cursor > game.Length - 8 || U32(game, cursor) != row)
                return false;
            var length = U32(game, cursor + 4);
            cursor += 8;
            if (length > 1024 || length > game.Length - cursor)
                return false;
            if (row == desired && length != 0)
                name = StrictUtf8.GetString(game, cursor, (int)length);
            cursor += (int)length;
        }
        return true;
    }

    private static DateTime? ReadSummaryDate(byte[] summary, out uint clubId, out BuildProfile? build)
    {
        clubId = 0;
        build = null;
        var cursor = 8;
        if (!SkipString(summary, ref cursor, 65_536) || cursor > summary.Length - 4)
            return null;
        var versionLength = U32(summary, cursor);
        if (versionLength is 0 or > 64 || versionLength > summary.Length - cursor - 4)
            return null;
        foreach (var profile in VerifiedBuilds)
            if (summary.AsSpan(cursor + 4, (int)versionLength).SequenceEqual(profile.Text))
            {
                build = profile;
                break;
            }
        if (build == null)
            return null;
        cursor += 4 + (int)versionLength;
        if (cursor > summary.Length - 4)
            return null;
        var divisions = U32(summary, cursor);
        if (divisions > 4096)
            return null;
        cursor += 4;
        for (var index = 0u; index < divisions; index++)
            if (!SkipString(summary, ref cursor, 1024))
                return null;
        cursor += 8; // Manager summary header.
        if (!SkipString(summary, ref cursor, 1024) || !SkipString(summary, ref cursor, 1024) ||
            cursor > summary.Length - 8)
            return null;
        clubId = U32(summary, cursor);
        return ReadDate(summary, cursor + 4); // The club UID precedes the save clock.
    }

    private static bool IsVerifiedGameInfo(byte[] info, BuildProfile build)
    {
        if (info.Length < 12 || U32(info, 8) is < 1 or > 64)
            return false;
        var afterVersion = 12 + (int)U32(info, 8);
        var filenamesAt = afterVersion + 167;
        if (filenamesAt > info.Length - 4 || U32(info, afterVersion + 38) != build.Number)
            return false;
        var count = U32(info, filenamesAt);
        if (count > 128)
            return false;
        var cursor = filenamesAt + 4;
        for (var index = 0u; index < count; index++)
            if (!SkipString(info, ref cursor, 4096))
                return false;
        var extra = cursor - (filenamesAt + 4);
        var lateBuild = afterVersion + extra + 382;
        if (lateBuild > info.Length - 4)
            return false;
        Span<byte> marker = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(marker, build.Number);
        return info.AsSpan(lateBuild, Math.Min(64, info.Length - lateBuild)).IndexOf(marker) >= 0;
    }

    private static bool SkipString(byte[] bytes, ref int cursor, int limit)
    {
        if (cursor > bytes.Length - 4)
            return false;
        var length = U32(bytes, cursor);
        cursor += 4;
        if (length > limit || length > bytes.Length - cursor)
            return false;
        cursor += (int)length;
        return true;
    }

    private static DateTime? ReadDate(byte[] data, int at)
    {
        if (at < 0 || at > data.Length - 4)
            return null;
        var year = U16(data, at + 2);
        var day = U16(data, at) & 0x1FF;
        if (year < 1901 || year > 2200 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
            return null;
        return new DateTime(year, 1, 1).AddDays(day - 1);
    }

    private static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            if (value != 0)
                return false;
        return true;
    }

    private static bool HasControlCharacters(string text)
    {
        foreach (var character in text)
            if (char.IsControl(character))
                return true;
        return false;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
}
