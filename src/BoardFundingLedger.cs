using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FmPlayerTalk;

internal static class BoardFundingLedger
{
    internal const string FileName = "board-funding.json";
    internal const int CooldownDays = 90;
    internal const int MaximumFileBytes = 128 * 1024;
    private const int Version = 1;
    private const int MaximumClubs = 2048;

    internal static bool CanOffer(string session, uint clubId, DateTime date)
    {
        if (!ValidClub(clubId) || !ValidDate(date.Date))
            return false;
        try
        {
            return IsDue(ReadState(SessionFile(session)), clubId, date.Date);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
    }

    internal static bool TryRecord(string session, uint clubId, DateTime date)
    {
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (!ValidClub(clubId) || !ValidDate(day))
            return false;
        try
        {
            var path = SessionFile(session);
            var current = ReadState(path);
            if (!IsDue(current, clubId, day))
                return false;

            current.LastGrant[clubId] = day;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, current);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
    }

    // Keep a damaged ledger with the save revision so it cannot be misread as a fresh cooldown.
    internal static byte[]? ReadFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("The board funding ledger must be a regular file.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = stream.Length;
            if (length > MaximumFileBytes)
                throw new InvalidDataException("The board funding ledger is too large.");
            using var reader = new BinaryReader(stream);
            var bytes = reader.ReadBytes((int)length);
            if (bytes.Length != length)
                throw new IOException("The board funding ledger changed while reading.");
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static LedgerState ReadState(string path)
    {
        if (ReadFile(path) is not { } bytes)
            return new(Version, new());

        var state = JsonSerializer.Deserialize<LedgerState>(bytes);
        if (state is null || state.Version != Version || state.LastGrant is null || state.LastGrant.Count > MaximumClubs)
            throw new InvalidDataException("The board funding ledger is unsupported or damaged.");
        foreach (var (clubId, date) in state.LastGrant)
        {
            if (!ValidClub(clubId) || !ValidDate(date))
                throw new InvalidDataException("The board funding ledger contains an invalid grant.");
        }
        return state;
    }

    private static string SessionFile(string session)
    {
        if (!Path.IsPathFullyQualified(session) ||
            (File.GetAttributes(session) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("The active session folder is unavailable.");
        return Path.Combine(session, FileName);
    }

    private static bool IsDue(LedgerState state, uint clubId, DateTime date) =>
        state.LastGrant.TryGetValue(clubId, out var previousDate)
            ? (date - previousDate).Days >= CooldownDays
            : state.LastGrant.Count < MaximumClubs;

    private static bool ValidClub(uint clubId) => clubId is > 0 and < uint.MaxValue;

    private static bool ValidDate(DateTime date) => date > DateTime.MinValue && date.TimeOfDay == TimeSpan.Zero;

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or JsonException or System.Security.SecurityException;

    private sealed record LedgerState(int Version, Dictionary<uint, DateTime> LastGrant);
}
