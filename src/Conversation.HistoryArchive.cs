using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace FmPlayerTalk;

internal sealed record StoredHistoryTurn(string User, string Reply, DateTime? GameDate, bool IsIncoming);

// An archive record intentionally has no person ID or live-game profile.
internal sealed record StoredConversationHistory(string Key, StoredHistoryTurn[] Turns, DateTime ModifiedUtc)
{
    internal DateTime? LastGameDate => Turns.LastOrDefault(turn => turn.GameDate.HasValue)?.GameDate;
    internal string Preview => Turns[^1].Reply.ReplaceLineEndings(" ");
}

public sealed partial class Conversation
{
    internal const int MaximumStoredHistoryEntries = 128;
    private const int MaximumStoredHistoryBytes = 2 * 1024 * 1024;
    private const int MaximumStoredHistoryText = 4096;

    // The caller supplies only the active isolated session, never a filename from player input.
    // Reading an old turn does not create a contact or grant permission to use a stored UID.
    internal static IReadOnlyList<StoredConversationHistory> ReadStoredHistory(string sessionDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(sessionDirectory) || !PlainDirectory(sessionDirectory))
                return Array.Empty<StoredConversationHistory>();
            var folder = Path.Combine(sessionDirectory, "conversations");
            if (!PlainDirectory(folder))
                return Array.Empty<StoredConversationHistory>();
            return Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => ArchiveKey(Path.GetFileName(path)))
                .Take(2048).Select(ReadStoredHistoryFile).OfType<StoredConversationHistory>()
                .OrderByDescending(entry => entry.LastGameDate)
                .ThenByDescending(entry => entry.ModifiedUtc).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(MaximumStoredHistoryEntries).ToArray();
        }
        catch (Exception error) when (ArchiveAccessError(error))
        {
            return Array.Empty<StoredConversationHistory>();
        }
    }

    private static StoredConversationHistory? ReadStoredHistoryFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                file.Length is <= 0 or > MaximumStoredHistoryBytes)
                return null;
            var modified = file.LastWriteTimeUtc;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumStoredHistoryBytes)
                return null;
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var bytes = reader.ReadBytes(MaximumStoredHistoryBytes + 1);
            if (bytes.Length is 0 or > MaximumStoredHistoryBytes)
                return null;
            var start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            using var json = JsonDocument.Parse(bytes.AsMemory(start), new JsonDocumentOptions { MaxDepth = 32 });
            if (!ArchiveProperty(json.RootElement, "turns", out var turns) ||
                turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() is < 1 or > 500)
                return null;
            var result = new List<StoredHistoryTurn>();
            foreach (var turn in turns.EnumerateArray())
            {
                if (turn.ValueKind != JsonValueKind.Object || turn.EnumerateObject()
                    .Where(property => property.Name.Equals("isIncoming", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("gameDate", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                    return null;
                if (!ArchiveText(turn, "user", allowEmpty: true, out var user) ||
                    !ArchiveText(turn, "assistant", allowEmpty: false, out var reply))
                    return null;
                var incoming = false;
                if (ArchiveProperty(turn, "isIncoming", out var incomingValue))
                {
                    if (incomingValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return null;
                    incoming = incomingValue.GetBoolean();
                }
                if (incoming ? user.Length != 0 : string.IsNullOrWhiteSpace(user))
                    return null;
                DateTime? day = null;
                if (ArchiveProperty(turn, "gameDate", out var date) && date.ValueKind != JsonValueKind.Null)
                {
                    if (date.ValueKind != JsonValueKind.String || !date.TryGetDateTime(out var value) ||
                        value.Year is < 1900 or > 2200 || value.TimeOfDay != TimeSpan.Zero)
                        return null;
                    day = value.Date;
                }
                result.Add(new(user, reply, day, incoming));
            }
            return new(Path.GetFileNameWithoutExtension(path), result.ToArray(), modified);
        }
        catch (Exception error) when (ArchiveAccessError(error)) { return null; }
    }

    private static bool ArchiveText(JsonElement parent, string name, bool allowEmpty, out string text)
    {
        text = "";
        if (!ArchiveProperty(parent, name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } candidate || candidate.Length > MaximumStoredHistoryText ||
            !allowEmpty && string.IsNullOrWhiteSpace(candidate) ||
            candidate.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            return false;
        text = candidate;
        return true;
    }

    private static bool ArchiveProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
            return false;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found)
                return false;
            found = true;
            value = property.Value;
        }
        return found;
    }

    private static bool ArchiveKey(string name) => name.Length == 69 &&
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.AsSpan(0, 64).ToString().All(Uri.IsHexDigit);

    private static bool PlainDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    private static bool ArchiveAccessError(Exception error) => error is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
