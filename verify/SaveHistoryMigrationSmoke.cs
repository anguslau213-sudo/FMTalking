using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FmPlayerTalk;

public static class SaveHistoryMigrationSmoke
{
    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-history-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "data");
        var saves = Path.Combine(root, "original-saves");
        var moved = Path.Combine(root, "moved-saves");
        Directory.CreateDirectory(saves);
        Directory.CreateDirectory(moved);
        var sourceSave = Path.Combine(saves, "career.fm");
        var targetSave = Path.Combine(moved, "renamed.fm");
        File.WriteAllText(sourceSave, "native save revision A");
        File.Copy(sourceSave, targetSave);
        var originalSaveHash = Hash(sourceSave);
        var checkpoints = new SaveCheckpoints(data);
        var sourceSession = checkpoints.CreateSession();
        var actor = new string('A', 64) + ".json";
        var originalHistory = Encoding.UTF8.GetBytes("{\"marker\":\"source-history\",\"affinity\":80}");
        File.WriteAllBytes(Path.Combine(sourceSession, "conversations", actor), originalHistory);
        var contact = new DirectoryPerson("历史介绍球员", "历", "助理介绍", "中场", 555,
            NativeDisplayName: true, Age: 25, IsFemale: false);
        var contacts = new DepartedContacts(sourceSession);
        Require(contacts.AddIntroduction(111, contact, "外队", "英国") && contacts.AddFavorite(contact, FavoriteSource.Contact),
            "Prepare introduced and favorite contact history");
        var day = new DateTime(2026, 11, 19);
        Require(BoardFundingLedger.TryRecord(sourceSession, 222, day) && checkpoints.Store(sourceSave, sourceSession),
            "Create source snapshot including funding cooldown");
        var sourceManifest = Directory.GetFiles(Path.Combine(data, "checkpoints"), "*.json").Single();
        var sourceManifestBytes = File.ReadAllBytes(sourceManifest);
        var fresh = checkpoints.Restore(targetSave)!;
        Require(Directory.GetFiles(Path.Combine(fresh, "conversations")).Length == 0,
            "A moved identical save never silently inherits another path's history");
        // The live roster reader may already have produced this observational file.
        var targetContacts = new DepartedContacts(fresh);
        Require(targetContacts.UpdateRoster(111, 222, new[] { contact }, new HashSet<uint>(),
            Array.Empty<DirectoryPerson>(), new HashSet<uint>()), "A fresh observed roster is not user history");
        var catalog = checkpoints.InspectHistory(targetSave);
        Require(catalog.Sources.Any(item => item.SourcePath == sourceSave), "Index identifies an exact-content source at another path");
        var plan = checkpoints.PrepareHistoryMigration(catalog, fresh, sourceSave);
        Require(Directory.GetFiles(Path.Combine(data, "checkpoints"), "*.json").Length == 1 &&
            Directory.GetFiles(Path.Combine(fresh, "conversations")).Length == 0,
            "Preview is read-only and does not replace the active session");
        var imported = checkpoints.ApplyHistoryMigration(plan);
        Require(imported != fresh && File.ReadAllBytes(Path.Combine(imported, "conversations", actor)).SequenceEqual(originalHistory),
            "Explicit confirmation materializes a separate exact source snapshot");
        var importedContacts = new DepartedContacts(imported);
        Require(importedContacts.Introductions(111).Single().PublicId == 555 && importedContacts.IsFavorite(555) &&
            !BoardFundingLedger.CanOffer(imported, 222, day), "Contacts, favorites and funding cooldown migrate together");
        Require(File.ReadAllBytes(sourceManifest).SequenceEqual(sourceManifestBytes) &&
            Hash(sourceSave) == originalSaveHash && Hash(targetSave) == originalSaveHash &&
            Directory.GetFiles(Path.Combine(fresh, "conversations")).Length == 0,
            "Source checkpoint, both game files and previous target session remain unchanged");
        var restored = checkpoints.Restore(targetSave)!;
        Require(File.ReadAllBytes(Path.Combine(restored, "conversations", actor)).SequenceEqual(originalHistory),
            "Ordinary reload uses the explicitly associated target checkpoint");
        Reject(() => checkpoints.ApplyHistoryMigration(plan), "A repeat confirmation cannot overwrite existing target history");

        var occupiedSave = CopySave(sourceSave, moved, "occupied.fm");
        var occupied = checkpoints.Restore(occupiedSave)!;
        File.WriteAllText(Path.Combine(occupied, "conversations", actor), "{\"marker\":\"target-history\"}");
        Reject(() => checkpoints.PrepareHistoryMigration(checkpoints.InspectHistory(occupiedSave), occupied, sourceSave),
            "An active unsaved dialogue is preserved rather than merged");
        Require(File.ReadAllText(Path.Combine(occupied, "conversations", actor)).Contains("target-history", StringComparison.Ordinal),
            "Rejected migration leaves target history intact");

        var favoriteSave = CopySave(sourceSave, moved, "favorite-only.fm");
        var favoriteSession = checkpoints.Restore(favoriteSave)!;
        Require(new DepartedContacts(favoriteSession).AddFavorite(contact, FavoriteSource.Contact), "Set a target favorite");
        Reject(() => checkpoints.PrepareHistoryMigration(checkpoints.InspectHistory(favoriteSave), favoriteSession, sourceSave),
            "A favorite-only target also counts as existing history");

        var changedSave = CopySave(sourceSave, moved, "same-stamp.fm");
        var changedSession = checkpoints.Restore(changedSave)!;
        var changePlan = checkpoints.PrepareHistoryMigration(checkpoints.InspectHistory(changedSave), changedSession, sourceSave);
        var savedStamp = File.GetLastWriteTimeUtc(changedSave);
        File.WriteAllText(changedSave, "native save revision B");
        File.SetLastWriteTimeUtc(changedSave, savedStamp);
        Reject(() => checkpoints.ApplyHistoryMigration(changePlan), "Same length and timestamp cannot substitute for the exact content hash");
        Require(!checkpoints.InspectHistory(changedSave).Sources.Any(), "A different world revision has no matching source");

        var changingSourceSave = CopySave(sourceSave, moved, "source-changed.fm");
        var changingSession = checkpoints.Restore(changingSourceSave)!;
        var stalePlan = checkpoints.PrepareHistoryMigration(checkpoints.InspectHistory(changingSourceSave), changingSession, sourceSave);
        File.WriteAllText(Path.Combine(sourceSession, "conversations", actor), "{\"marker\":\"new-source-history\"}");
        Require(checkpoints.Store(sourceSave, sourceSession), "Change only the source history after preview");
        Reject(() => checkpoints.ApplyHistoryMigration(stalePlan), "A source history changed after preview is not silently substituted");

        CheckLegacyMissingPath(root, sourceSave, actor, originalHistory);
        CheckMovedProfile(root, data, sourceSave, actor);
        CheckRotation(root, actor);
        return "PASS: explicit history reassociation, exact hashes, no automatic merging or overwrite, source/target preservation, legacy missing paths, moved profiles and three-slot revisions.";
    }

    private static void CheckLegacyMissingPath(string root, string matchingSave, string actor, byte[] history)
    {
        var data = Path.Combine(root, "legacy-data");
        var checkpoints = new SaveCheckpoints(data);
        var target = CopySave(matchingSave, Path.GetDirectoryName(matchingSave)!, "legacy-copy.fm");
        var session = checkpoints.Restore(target)!;
        var missingOriginal = Path.Combine(root, "old-computer", "career.fm");
        var catalog = checkpoints.InspectHistory(target);
        var normalized = OperatingSystem.IsWindows() ? missingOriginal.ToUpperInvariant() : missingOriginal;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n" + catalog.Revision.ContentHash)));
        File.WriteAllBytes(Path.Combine(data, "checkpoints", key + ".json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Key = key, Files = new Dictionary<string, byte[]> { [actor] = history }
        }));
        Require(!File.Exists(missingOriginal) && checkpoints.InspectHistory(target).Sources.Length == 0,
            "Legacy snapshots need neither an original file nor a fabricated index");
        var plan = checkpoints.PrepareHistoryMigration(catalog, session, "\"" + missingOriginal + "\"");
        var imported = checkpoints.ApplyHistoryMigration(plan);
        Require(File.ReadAllBytes(Path.Combine(imported, "conversations", actor)).SequenceEqual(history),
            "An explicit quoted original path recovers a pre-index snapshot without accessing the missing game file");
        Reject(() => checkpoints.PrepareHistoryMigration(catalog, session, "relative.fm"), "Relative source paths cannot pick arbitrary history");
    }

    private static void CheckMovedProfile(string root, string originalData, string matchingSave, string actor)
    {
        var copiedData = Path.Combine(root, "new-user-profile");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(originalData, "checkpoints"), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(originalData, file);
            var target = Path.Combine(copiedData, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var targetSave = CopySave(matchingSave, Path.GetDirectoryName(matchingSave)!, "new-user-copy.fm");
        var checkpoints = new SaveCheckpoints(copiedData);
        var session = checkpoints.Restore(targetSave)!;
        var catalog = checkpoints.InspectHistory(targetSave);
        var source = catalog.Sources.Single(item => item.SourcePath == matchingSave);
        var result = checkpoints.ApplyHistoryMigration(checkpoints.PrepareHistoryMigration(catalog, session, source.SourcePath));
        Require(File.ReadAllText(Path.Combine(result, "conversations", actor)).Contains("new-source-history", StringComparison.Ordinal),
            "Copied checkpoint data can be explicitly associated under a new user/data root");
    }

    private static void CheckRotation(string root, string actor)
    {
        var data = Path.Combine(root, "rotation-data");
        var saves = Path.Combine(root, "rotation-original");
        var moved = Path.Combine(root, "rotation-new");
        Directory.CreateDirectory(saves);
        Directory.CreateDirectory(moved);
        var primary = Path.Combine(saves, "career.fm");
        var checkpoints = new SaveCheckpoints(data);
        var session = checkpoints.CreateSession();
        File.WriteAllText(primary, "slot A");
        File.WriteAllText(Path.Combine(session, "conversations", actor), "{\"slot\":\"A\"}");
        Require(checkpoints.Store(primary, session), "Store original primary revision A");
        File.WriteAllText(primary, "slot B");
        File.WriteAllText(Path.Combine(session, "conversations", actor), "{\"slot\":\"B\"}");
        Require(checkpoints.Store(primary, session), "Store original primary revision B");
        foreach (var slot in new[] { " (v02)", " (v03)" })
        {
            var newSlot = Path.Combine(moved, "career" + slot + ".fm");
            var oldSlot = Path.Combine(saves, "career" + slot + ".fm");
            File.WriteAllText(newSlot, "slot A");
            var fresh = checkpoints.Restore(newSlot)!;
            var imported = checkpoints.ApplyHistoryMigration(checkpoints.PrepareHistoryMigration(
                checkpoints.InspectHistory(newSlot), fresh, oldSlot));
            Require(File.ReadAllText(Path.Combine(imported, "conversations", actor)) == "{\"slot\":\"A\"}",
                "Explicit moved rotated slot resolves its exact prior primary revision: " + slot);
        }
        Require(File.ReadAllText(Path.Combine(checkpoints.Restore(primary)!, "conversations", actor)) == "{\"slot\":\"B\"}",
            "Importing older slots cannot replace the current primary revision's history");
        var exactSecond = Path.Combine(saves, "career (v02).fm");
        File.WriteAllText(exactSecond, "slot A");
        File.WriteAllText(Path.Combine(session, "conversations", actor), "{\"slot\":\"explicit-second\"}");
        Require(checkpoints.Store(exactSecond, session), "Record an exact second-slot checkpoint with its own mod history");
        var exactCopy = CopySave(exactSecond, moved, "explicit-second-copy.fm");
        var exactSession = checkpoints.Restore(exactCopy)!;
        var exactImport = checkpoints.ApplyHistoryMigration(checkpoints.PrepareHistoryMigration(
            checkpoints.InspectHistory(exactCopy), exactSession, exactSecond));
        Require(File.ReadAllText(Path.Combine(exactImport, "conversations", actor)) == "{\"slot\":\"explicit-second\"}",
            "An explicitly selected exact slot takes priority over older fallback primary history");
    }

    private static string CopySave(string source, string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.Copy(source, path);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Reject(Action action, string detail)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new InvalidOperationException("History migration should reject: " + detail);
    }
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("History migration: " + detail);
    }
}
