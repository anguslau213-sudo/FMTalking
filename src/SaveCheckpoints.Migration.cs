using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FmPlayerTalk;

internal sealed partial class SaveCheckpoints
{
    private const int HistoryLinkLimit = 16 * 1024;
    private const int HistorySourceLimit = 12;
    internal sealed record HistorySource(string SourcePath, string CheckpointKey, int RecordFiles);
    internal sealed record HistoryCatalog(Revision Revision, HistorySource[] Sources);
    internal sealed record HistoryMigrationPlan(Revision Revision, string Session, HistorySource Source, SessionSnapshot Snapshot);
    private sealed record HistoryLink(int Version, string SavePath, string ContentHash, string Key);

    // This optional index has no role in ordinary Restore. Moving or copying a save
    // never silently imports another path's history, even when its bytes match.
    private void WriteHistoryLink(Revision revision)
    {
        try
        {
            var folder = Path.Combine(checkpoints, "history-links");
            CreatePlainDirectory(folder);
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new HistoryLink(1, revision.Path, revision.ContentHash, revision.Key));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(folder, revision.Key + ".json"), overwrite: true);
        }
        catch (Exception error) when (IsAccessError(error))
        {
            // An index failure cannot invalidate the successfully paired save.
            // Explicit old-path lookup remains available for older checkpoints.
        }
    }

    internal HistoryCatalog InspectHistory(string currentSave)
    {
        var revision = Fingerprint(currentSave) ?? throw new InvalidDataException("当前本地存档无法核实，请重新载入后再试。");
        var folder = Path.Combine(checkpoints, "history-links");
        var candidates = new List<HistorySource>();
        if (Directory.Exists(folder))
        {
            RequirePlainFile(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Take(20000))
            {
                try
                {
                    RequirePlainFile(file);
                    var info = new FileInfo(file);
                    if (info.Length is <= 0 or > HistoryLinkLimit)
                        continue;
                    using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    var link = JsonSerializer.Deserialize<HistoryLink>(stream);
                    if (link is null || link.Version != 1 || link.ContentHash != revision.ContentHash ||
                        !IsHistoryPath(link.SavePath) || link.Key != RevisionKey(link.SavePath, link.ContentHash) ||
                        Path.GetFileNameWithoutExtension(file) != link.Key || link.Key == revision.Key ||
                        candidates.Any(item => item.CheckpointKey == link.Key))
                        continue;
                    if (ReadCheckpoint(link.Key) is { } checkpoint)
                        candidates.Add(new(link.SavePath, link.Key, checkpoint.Files.Count));
                    if (candidates.Count == HistorySourceLimit)
                        break;
                }
                catch (Exception error) when (IsAccessError(error)) { }
            }
        }
        if (!revision.IsCurrent())
            throw new InvalidDataException("检查期间存档发生变化，请重新查找。");
        return new(revision, candidates.ToArray());
    }

    internal HistoryMigrationPlan PrepareHistoryMigration(HistoryCatalog catalog, string currentSession, string sourcePath)
    {
        sourcePath = NormalizeHistoryPath(sourcePath);
        if (!IsHistoryPath(sourcePath))
            throw new InvalidDataException("请填写原存档的完整本地 .fm 路径，文件可以已经搬走。");
        var source = Path.GetFullPath(sourcePath.Trim());
        var revision = Fingerprint(catalog.Revision.Path);
        if (revision == null || revision.Key != catalog.Revision.Key || !catalog.Revision.IsCurrent())
            throw new InvalidDataException("当前存档已变化，请重新查找历史。");
        EnsureEmptyDestination(revision, currentSession);
        var found = FindExplicitHistory(source, revision.ContentHash) ??
            throw new InvalidDataException("没有找到与当前存档内容完全相同的历史。请核对原路径，或改用对应的未修改副本。");
        if (found.Source.CheckpointKey == revision.Key)
            throw new InvalidDataException("这就是当前存档的历史，无需重新关联。");
        return new(revision, currentSession, found.Source, found.Snapshot);
    }

    internal string ApplyHistoryMigration(HistoryMigrationPlan plan)
    {
        // Rehash at explicit confirmation: a same-size/timestamp replacement is
        // a different revision too. No game file is ever opened for writing.
        var revision = Fingerprint(plan.Revision.Path);
        if (revision == null || revision.Key != plan.Revision.Key || revision.Stamp != plan.Revision.Stamp)
            throw new InvalidDataException("确认前存档内容已变化，历史未关联，请重新查找。");
        EnsureEmptyDestination(revision, plan.Session);
        var currentSource = ReadCheckpoint(plan.Source.CheckpointKey) ??
            throw new InvalidDataException("来源历史已经变化或不可读，请重新查找。");
        var expected = new Checkpoint(Version, plan.Source.CheckpointKey, plan.Snapshot.Files,
            plan.Snapshot.RosterContacts, plan.Snapshot.FundingLedger);
        if (!SameCheckpointContents(currentSource, expected))
            throw new InvalidDataException("来源历史在确认期间发生变化，未合并或覆盖任何记录。");

        var session = Materialize(plan.Snapshot);
        if (!revision.IsCurrent())
            throw new InvalidDataException("存档发生变化，历史未关联。");
        CreatePlainDirectory(checkpoints);
        var temporary = Path.Combine(checkpoints, Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new Checkpoint(Version, revision.Key, plan.Snapshot.Files,
                plan.Snapshot.RosterContacts, plan.Snapshot.FundingLedger));
            stream.Flush(flushToDisk: true);
        }
        if (!revision.IsCurrent())
            throw new InvalidDataException("存档发生变化，历史未关联。");
        // Atomic, without overwrite: failed serialization cannot create a partial
        // target checkpoint, and an existing history is never replaced.
        File.Move(temporary, CheckpointPath(revision.Key), overwrite: false);
        WriteHistoryLink(revision);
        return session;
    }

    private (HistorySource Source, SessionSnapshot Snapshot)? FindExplicitHistory(string sourcePath, string hash)
    {
        var paths = new List<string> { sourcePath };
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var third = name.EndsWith(ThirdSlotSuffix, StringComparison.Ordinal);
        var suffix = third ? ThirdSlotSuffix : SecondSlotSuffix;
        if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
        {
            var main = Path.Combine(Path.GetDirectoryName(sourcePath)!, name[..^suffix.Length] + Path.GetExtension(sourcePath));
            paths.Add(main);
            if (third) paths.Add(SecondSlotPath(main));
        }
        (HistorySource Source, SessionSnapshot Snapshot)? found = null;
        Checkpoint? selected = null;
        foreach (var path in paths)
        {
            var key = RevisionKey(path, hash);
            if (ReadCheckpoint(key) is not { } checkpoint)
                continue;
            if (path == sourcePath)
                return (new HistorySource(path, key, checkpoint.Files.Count),
                    new SessionSnapshot(checkpoint.Files, checkpoint.RosterContacts, checkpoint.FundingLedger));
            if (selected != null && !SameCheckpointContents(selected, checkpoint))
                throw new InvalidDataException("轮转来源存在不同的历史记录，请输入准确的原始保存路径；不会自动合并。");
            selected = checkpoint;
            found ??= (new HistorySource(path, key, checkpoint.Files.Count),
                new SessionSnapshot(checkpoint.Files, checkpoint.RosterContacts, checkpoint.FundingLedger));
        }
        return found;
    }

    private void EnsureEmptyDestination(Revision revision, string session)
    {
        if (File.Exists(CheckpointPath(revision.Key)))
            throw new InvalidDataException("当前存档版本已有关联记录，为保留现有历史，不覆盖或合并。");
        var current = ReadSession(session) ?? throw new InvalidDataException("当前会话无法核实，历史未关联。");
        if (current.Files.Count > 0 || current.FundingLedger != null || HasContactHistory(current.RosterContacts))
            throw new InvalidDataException("当前会话已有聊天或联系人历史，为保留这些记录，不覆盖或合并。");
    }

    private static bool HasContactHistory(byte[]? bytes)
    {
        if (bytes == null) return false;
        using var document = JsonDocument.Parse(bytes);
        foreach (var item in document.RootElement.EnumerateObject())
        {
            if (item.Name is "Version" or "ManagerId" or "ClubId" or "Roster")
                continue; // A freshly read roster is observational, not user history.
            if (item.Value.ValueKind != JsonValueKind.Array || item.Value.GetArrayLength() != 0)
                return true;
        }
        return false;
    }

    private static bool IsHistoryPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path.Trim()) && string.Equals(Path.GetExtension(path.Trim()), ".fm", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHistoryPath(string? path)
    {
        var value = path?.Trim() ?? "";
        return value.Length > 1 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;
    }
}
