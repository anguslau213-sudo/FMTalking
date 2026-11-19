using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FmPlayerTalk;

internal enum ShadowBoardStage { Ordinary, AskedForExplanation, Active }
internal enum ShadowBoardTransition { None, AskForExplanation, Activate }

internal sealed record ShadowBoardTurn(string Session, uint Manager, uint Club, DateTime? GameDate,
    string? ManagerName, ShadowBoardStage Stage)
{
    internal static ShadowBoardTurn Capture(string session, uint manager, uint club, DateTime? date, string? name) =>
        new(session, manager, club, date, name, ShadowBoardRules.Restore(session, manager, club, date));
}

internal static class ShadowBoardRules
{
    private const int MaximumStateBytes = 4096;
    private const int ExplanationDays = 7;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private sealed record SavedStage(ShadowBoardStage Stage, DateTime Day);

    internal static string AskForExplanation(string? managerName) =>
        ManagerGreeting(managerName) + "董事会的决定可不是一句话就能改的。你哪来的这种底气？";

    internal static string Confirm(string? managerName) =>
        "我明白了，" + (string.IsNullOrWhiteSpace(managerName) ? "教练" : managerName.Trim()) +
        "。这家俱乐部一直都是你的，往哪走由你定。我们只是站在台前的人，需要我出面时，你尽管开口，我会把事情办好。";

    internal static ShadowBoardStage Restore(string session, uint manager, uint club, DateTime? date)
    {
        try
        {
            var saved = Read(PathFor(session, manager, club));
            return EffectiveStage(saved, date);
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return ShadowBoardStage.Ordinary;
        }
    }

    internal static bool TryRecord(ShadowBoardTurn turn, ShadowBoardTransition change) => TryRecord(turn, change, () => true);

    // The native affinity writer invokes this callback only after it has verified
    // the write. A failed conversation commit restores the previous story stage.
    internal static bool TryRecord(ShadowBoardTurn turn, ShadowBoardTransition change, Func<bool> commit)
    {
        if (commit == null || turn.GameDate is not DateTime today || today.Year is < 1900 or > 2200 ||
            change is not (ShadowBoardTransition.Activate or ShadowBoardTransition.AskForExplanation) ||
            turn.Stage == ShadowBoardStage.Active)
            return false;
        try
        {
            var path = PathFor(turn.Session, turn.Manager, turn.Club);
            lock (Gates.GetOrAdd(path, _ => new object()))
            {
                var previous = Read(path);
                if (EffectiveStage(previous, today) != turn.Stage)
                    return false;
                var next = change == ShadowBoardTransition.Activate
                    ? ShadowBoardStage.Active : ShadowBoardStage.AskedForExplanation;
                Write(path, new SavedStage(next, today.Date));
                var committed = false;
                try
                {
                    committed = commit();
                    return committed;
                }
                finally
                {
                    if (!committed)
                        Write(path, previous ?? new SavedStage(ShadowBoardStage.Ordinary, today.Date));
                }
            }
        }
        catch (Exception error) when (IsStorageError(error))
        {
            return false;
        }
    }

    private static void Write(string path, SavedStage state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state));
        File.Move(temporary, path, overwrite: true);
    }

    private static string ManagerGreeting(string? name) => string.IsNullOrWhiteSpace(name) ? "" : name.Trim() + "，";

    private static string PathFor(string session, uint manager, uint club)
    {
        if (!Path.IsPathFullyQualified(session) || manager is 0 or uint.MaxValue || club is 0 or uint.MaxValue)
            throw new ArgumentException("Invalid board session.");
        // SaveCheckpoints copies these hashed per-save JSON object files when FM saves.
        var key = FormattableString.Invariant($"fm27:shadow-board:{manager}:{club}");
        var file = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
        return Path.Combine(session, "conversations", file);
    }

    private static SavedStage? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumStateBytes)
                throw new InvalidDataException("Board state is too large.");
            var saved = JsonSerializer.Deserialize<SavedStage>(stream);
            if (saved is null || saved.Day == default ||
                saved.Stage is not (ShadowBoardStage.Ordinary or ShadowBoardStage.AskedForExplanation or ShadowBoardStage.Active))
                throw new InvalidDataException("Board state is invalid.");
            return saved;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static ShadowBoardStage EffectiveStage(SavedStage? saved, DateTime? date)
    {
        if (saved == null || date is DateTime now && now.Date < saved.Day.Date)
            return ShadowBoardStage.Ordinary;
        if (saved.Stage == ShadowBoardStage.Active ||
            date is DateTime dated && (dated.Date - saved.Day.Date).Days <= ExplanationDays)
            return saved.Stage;
        return ShadowBoardStage.Ordinary;
    }

    private static bool IsStorageError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
