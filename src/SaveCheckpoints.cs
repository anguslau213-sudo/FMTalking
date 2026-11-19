using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FmPlayerTalk;

internal sealed partial class SaveCheckpoints
{
    private const int Version = 1;
    private const string Conversations = "conversations";
    private const string SecondSlotSuffix = " (v02)";
    private const string ThirdSlotSuffix = " (v03)";
    private readonly string checkpoints;
    private readonly string sessions;

    public SaveCheckpoints(string appDataRoot)
    {
        var root = Path.GetFullPath(appDataRoot);
        checkpoints = Path.Combine(root, "checkpoints");
        sessions = Path.Combine(root, "sessions");
    }

    public string CreateSession()
    {
        CreatePlainDirectory(sessions);
        var path = Path.Combine(sessions, Guid.NewGuid().ToString("N"));
        CreatePlainDirectory(Path.Combine(path, Conversations));
        return path;
    }

    internal static string? FindRecentLocalSave(string basename, string extension, long modifiedLocalMilliseconds,
        IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(basename) || basename.Length > 256 ||
            basename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(extension, ".fm", StringComparison.OrdinalIgnoreCase) ||
            modifiedLocalMilliseconds <= 0)
            return null;

        var filename = basename + extension;
        string? match = null;
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = new FileInfo(Path.Combine(directory, filename));
            if (!candidate.Exists || candidate.Length <= 0 || (candidate.Attributes & FileAttributes.ReparsePoint) != 0 ||
                candidate.LastWriteTime.Ticks / TimeSpan.TicksPerMillisecond != modifiedLocalMilliseconds)
                continue;
            if (match != null && !string.Equals(match, candidate.FullName, StringComparison.OrdinalIgnoreCase))
                return null;
            match = candidate.FullName;
        }
        return match;
    }

    public string? Freeze(string sessionDirectory)
    {
        try
        {
            return ReadSession(sessionDirectory) is { } snapshot ? Materialize(snapshot) : null;
        }
        catch (Exception error) when (IsAccessError(error))
        {
            return null;
        }
    }

    // A known save revision with no checkpoint starts fresh. An unreadable revision cannot be restored.
    public string? Restore(string fmPath)
    {
        try
        {
            if (Fingerprint(fmPath) is not { } revision)
                return null;

            CreatePlainDirectory(checkpoints);
            var checkpoint = ReadCheckpoint(revision.Key) ?? ReadRotatedCheckpoint(revision);
            if (!revision.IsCurrent())
                return null;
            if (checkpoint == null)
                return CreateSession();

            return Materialize(new SessionSnapshot(checkpoint.Files, checkpoint.RosterContacts, checkpoint.FundingLedger));
        }
        catch (Exception error) when (IsAccessError(error))
        {
            return null;
        }
    }

    private Checkpoint? ReadCheckpoint(string key)
    {
        var path = CheckpointPath(key);
        try
        {
            RequirePlainFile(path);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(stream);
            if (checkpoint is null || checkpoint.Version != Version || checkpoint.Key != key ||
                checkpoint.Files is null || !ValidFiles(checkpoint.Files) ||
                checkpoint.FundingLedger?.Length > BoardFundingLedger.MaximumFileBytes)
                throw new InvalidDataException("Invalid conversation checkpoint.");
            if (checkpoint.RosterContacts is { } contacts)
                DepartedContacts.Validate(contacts);
            return checkpoint;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private Checkpoint? ReadRotatedCheckpoint(Revision revision)
    {
        // FM moves the old primary save to (v02), and the old (v02) to (v03).
        // Keep arbitrary copies and unrelated save paths isolated.
        var name = Path.GetFileNameWithoutExtension(revision.Path);
        var isThird = name.EndsWith(ThirdSlotSuffix, StringComparison.Ordinal);
        var suffix = isThird ? ThirdSlotSuffix : SecondSlotSuffix;
        if (!name.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var stem = name[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(stem))
            return null;

        var directory = Path.GetDirectoryName(revision.Path)!;
        var extension = Path.GetExtension(revision.Path);
        var primary = Path.Combine(directory, stem + extension);
        if (Stamp.Read(primary) is not { Length: > 0 })
            return null;
        RequirePlainFile(primary);

        var fromPrimary = ReadCheckpoint(RevisionKey(primary, revision.ContentHash));
        if (!isThird)
            return fromPrimary;

        var previous = SecondSlotPath(primary);
        var fromSecond = ReadCheckpoint(RevisionKey(previous, revision.ContentHash));
        if (fromPrimary != null && fromSecond != null && !SameCheckpointContents(fromPrimary, fromSecond))
            throw new InvalidDataException("The rotated save matches different conversation checkpoints.");
        return fromSecond ?? fromPrimary;
    }

    internal static string SecondSlotPath(string primary) => Path.Combine(Path.GetDirectoryName(primary)!,
        Path.GetFileNameWithoutExtension(primary) + SecondSlotSuffix + Path.GetExtension(primary));

    private static bool SameCheckpointContents(Checkpoint left, Checkpoint right)
    {
        if (left.Files.Count != right.Files.Count ||
            !SameBytes(left.RosterContacts, right.RosterContacts) || !SameBytes(left.FundingLedger, right.FundingLedger))
            return false;
        foreach (var (name, contents) in left.Files)
        {
            if (!right.Files.TryGetValue(name, out var other) || !SameBytes(contents, other))
                return false;
        }
        return true;
    }

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    public bool Store(string fmPath, string sessionDirectory) =>
        StoreMatchingRevision(fmPath, sessionDirectory, expected: null);

    internal bool StoreMatchingRevision(string fmPath, string sessionDirectory, Stamp? expected)
    {
        try
        {
            if (Fingerprint(fmPath) is not { } revision || (expected.HasValue && expected.Value != revision.Stamp) ||
                ReadSession(sessionDirectory) is not { } snapshot)
                return false;

            CreatePlainDirectory(checkpoints);
            var path = CheckpointPath(revision.Key);
            var temp = Path.Combine(checkpoints, Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Checkpoint(Version, revision.Key, snapshot.Files,
                    snapshot.RosterContacts, snapshot.FundingLedger));
                stream.Flush(flushToDisk: true);
            }
            if (!revision.IsCurrent())
                return false;

            File.Move(temp, path, overwrite: true);
            WriteHistoryLink(revision);
            return true;
        }
        catch (Exception error) when (IsAccessError(error))
        {
            return false;
        }
    }

    private static Revision? Fingerprint(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".fm", StringComparison.OrdinalIgnoreCase))
            return null;
        var fullPath = Path.GetFullPath(path);
        if (Stamp.Read(fullPath) is not { } before || before.Length == 0)
            return null;

        // Reading a revision must not prevent FM from moving it to the next rotation slot.
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != before.Length)
            return null;
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(stream));
        if (Stamp.Read(fullPath) != before || stream.Length != before.Length)
            return null;

        return new Revision(fullPath, before, hash);
    }

    private static string RevisionKey(string path, string contentHash)
    {
        var normalizedPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath + "\n" + contentHash)));
    }

    private SessionSnapshot? ReadSession(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Guid.TryParseExact(Path.GetRelativePath(sessions, path), "N", out _) ||
            !Directory.Exists(sessions) || !Directory.Exists(path))
            return null;
        RequirePlainFile(sessions);
        RequirePlainFile(path);
        var source = Path.Combine(path, Conversations);
        RequirePlainFile(source);
        var contactsPath = Path.Combine(path, DepartedContacts.FileName);
        var contactsBefore = Stamp.Read(contactsPath);
        var fundingPath = Path.Combine(path, BoardFundingLedger.FileName);
        var fundingBefore = Stamp.Read(fundingPath);
        var before = Inventory(source);
        var copies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, stamp) in before)
        {
            var bytes = File.ReadAllBytes(Path.Combine(source, name));
            if (bytes.LongLength != stamp.Length)
                return null;
            copies.Add(name, bytes);
        }
        var contacts = DepartedContacts.ReadFile(contactsPath);
        var funding = BoardFundingLedger.ReadFile(fundingPath);
        if (contacts?.LongLength != contactsBefore?.Length || funding?.LongLength != fundingBefore?.Length)
            return null;
        if (contacts != null)
            DepartedContacts.Validate(contacts);
        var after = Inventory(source);
        if (before.Count != after.Count || !ValidFiles(copies) ||
            Stamp.Read(contactsPath) != contactsBefore || Stamp.Read(fundingPath) != fundingBefore)
            return null;
        foreach (var (name, stamp) in before)
        {
            if (!after.TryGetValue(name, out var latest) || latest != stamp)
                return null;
        }
        return new SessionSnapshot(copies, contacts, funding);
    }

    private static Dictionary<string, Stamp> Inventory(string directory)
    {
        var files = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (!IsActorFile(name))
                continue;
            RequirePlainFile(path);
            if (Stamp.Read(path) is not { } stamp || !files.TryAdd(name, stamp))
                throw new IOException("Conversation files changed during the checkpoint.");
        }
        return files;
    }

    private static bool ValidFiles(Dictionary<string, byte[]> files)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, contents) in files)
        {
            if (!IsActorFile(name) || !names.Add(name) || contents is null)
                return false;
            var start = contents.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            using var json = JsonDocument.Parse(contents.AsMemory(start));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;
        }
        return true;
    }

    private static bool IsActorFile(string name)
    {
        if (name.Length != 69 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var character in name.AsSpan(0, 64))
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    private string Materialize(SessionSnapshot snapshot)
    {
        var session = CreateSession();
        var directory = Path.Combine(session, Conversations);
        foreach (var (name, contents) in snapshot.Files)
            File.WriteAllBytes(Path.Combine(directory, name), contents);
        if (snapshot.RosterContacts is { } contacts)
            File.WriteAllBytes(Path.Combine(session, DepartedContacts.FileName), contacts);
        if (snapshot.FundingLedger is { } funding)
            File.WriteAllBytes(Path.Combine(session, BoardFundingLedger.FileName), funding);
        return session;
    }

    private string CheckpointPath(string key) => Path.Combine(checkpoints, key + ".json");

    private static void CreatePlainDirectory(string path)
    {
        Directory.CreateDirectory(path);
        RequirePlainFile(path);
    }

    private static void RequirePlainFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Checkpoint folders and files must not be links.");
    }

    private static bool IsAccessError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or JsonException or System.Security.SecurityException;

    internal sealed record SessionSnapshot(Dictionary<string, byte[]> Files, byte[]? RosterContacts, byte[]? FundingLedger);

    private sealed record Checkpoint(int Version, string Key, Dictionary<string, byte[]> Files, byte[]? RosterContacts = null,
        byte[]? FundingLedger = null);

    internal sealed record Revision(string Path, Stamp Stamp, string ContentHash)
    {
        public string Key => RevisionKey(Path, ContentHash);
        public bool IsCurrent() => SaveCheckpoints.Stamp.Read(Path) == Stamp;
    }

    internal readonly record struct Stamp(long Length, DateTime LastWriteUtc)
    {
        public static Stamp? Read(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new Stamp(file.Length, file.LastWriteTimeUtc) : null;
        }
    }
}
