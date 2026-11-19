using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FmPlayerTalk;

public static class SaveCheckpointSmoke
{
    public static string Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "fm-player-talk-checkpoint-" + Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(sandbox, "mod-data");
        var game = Path.Combine(sandbox, "career.fm");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(game, "revision A");
        CheckRecentLocalSelection(sandbox, game);
        var gameTimestamp = File.GetLastWriteTimeUtc(game);
        var actor = new string('A', 64) + ".json";
        var otherActor = new string('B', 64) + ".json";
        var beforeSave = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"affinity\":1}")).ToArray();

        var legacyFolder = Path.Combine(appData, "conversations");
        Directory.CreateDirectory(legacyFolder);
        var legacyPath = Path.Combine(legacyFolder, actor);
        File.WriteAllText(legacyPath, "{\"affinity\":50}");

        var checkpoints = new SaveCheckpoints(appData);
        var session = checkpoints.CreateSession();
        WriteSession(session, actor, beforeSave);
        WriteSession(session, otherActor, Encoding.UTF8.GetBytes("{\"trust\":2}"));
        WriteSession(session, "notes.json", Encoding.UTF8.GetBytes("{\"notes\":true}"));
        var frozen = checkpoints.Freeze(session);
        Require(frozen is not null && frozen != session, "Freeze produces a new working session");
        WriteSession(session, actor, Encoding.UTF8.GetBytes("{\"affinity\":9}"));
        Require(checkpoints.Store(game, frozen!), "Store the state frozen at the save request");
        var firstManifest = Directory.GetFiles(Path.Combine(appData, "checkpoints"), "*.json").Single();

        var reopened = new SaveCheckpoints(appData);
        var restored = reopened.Restore(Path.Combine(sandbox, ".", "career.fm"));
        Require(restored is not null && restored != session && restored != frozen &&
                ReadSession(restored, actor).SequenceEqual(beforeSave) &&
                Encoding.UTF8.GetString(ReadSession(restored, otherActor)) == "{\"trust\":2}" &&
                !File.Exists(ActorPath(restored, "notes.json")), "Reload preserves exact bytes for each conversation only");
        WriteSession(restored!, actor, Encoding.UTF8.GetBytes("{\"affinity\":24}"));
        var again = reopened.Restore(game);
        Require(again is not null && again != restored && ReadSession(again, actor).SequenceEqual(beforeSave),
            "Reopening the same FM revision starts a detached session from its original checkpoint");

        var differentPath = Path.Combine(sandbox, "other-career.fm");
        File.Copy(game, differentPath);
        var unrelated = reopened.Restore(differentPath);
        Require(unrelated is not null && Directory.GetFiles(Path.Combine(unrelated, "conversations")).Length == 0,
            "Identical FM bytes at a different save path do not inherit conversations");

        File.WriteAllText(game, "revision B");
        File.SetLastWriteTimeUtc(game, gameTimestamp);
        var otherRevision = reopened.Restore(game);
        Require(otherRevision is not null && Directory.GetFiles(Path.Combine(otherRevision, "conversations")).Length == 0,
            "A same-length and same-timestamp revision does not inherit the prior checkpoint");
        Require(reopened.Store(game, session), "An explicit successful save can checkpoint the new revision");
        var latest = reopened.Restore(game);
        Require(latest is not null && Encoding.UTF8.GetString(ReadSession(latest, actor)) == "{\"affinity\":9}",
            "The new save revision restores its own state");

        File.WriteAllText(game, "revision A");
        var rollback = reopened.Restore(game);
        Require(rollback is not null && ReadSession(rollback, actor).SequenceEqual(beforeSave),
            "Returning to earlier FM bytes reloads the earlier conversation state");
        Require(File.ReadAllText(legacyPath) == "{\"affinity\":50}" && File.ReadAllText(game) == "revision A",
            "No checkpoint operation changes the FM file or preexisting global conversations");

        Require(!reopened.Store(game, Path.GetDirectoryName(legacyFolder)!) && reopened.Freeze(appData) is null &&
                reopened.Restore(Path.Combine(sandbox, "missing.fm")) is null,
            "Checkpointing accepts only an owned session and a readable explicit FM file");

        CheckDepartedContacts(reopened, sandbox, actor, beforeSave);
        CheckPlayerIntroductions(reopened, sandbox);
        CheckBoardFundingLedger(reopened, sandbox, actor, beforeSave);
        CheckRotatingSlots(sandbox, actor);

        File.WriteAllBytes(firstManifest, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Key = Path.GetFileNameWithoutExtension(firstManifest),
            Files = new Dictionary<string, byte[]> { [actor] = beforeSave }
        }));
        var legacyCheckpoint = reopened.Restore(game);
        Require(legacyCheckpoint != null && ReadSession(legacyCheckpoint, actor).SequenceEqual(beforeSave) &&
                new DepartedContacts(legacyCheckpoint).Visible.Count == 0 &&
                BoardFundingLedger.CanOffer(legacyCheckpoint, 20, new DateTime(2026, 11, 17)),
            "A checkpoint without contact or grant data still restores");

        File.WriteAllBytes(firstManifest, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Key = Path.GetFileNameWithoutExtension(firstManifest),
            Files = new System.Collections.Generic.Dictionary<string, byte[]>
            {
                ["../outside.json"] = beforeSave
            }
        }));
        Require(reopened.Restore(game) is null, "A checkpoint with a traversal filename cannot be restored");
        return "Save checkpoint checks passed (revision isolation, three rotating save slots, legacy preservation, path validation, contacts, favorites, and per-club board grant cooldown).";
    }

    private static void CheckRecentLocalSelection(string sandbox, string game)
    {
        var modified = File.GetLastWriteTime(game).Ticks / TimeSpan.TicksPerMillisecond;
        var otherDirectory = Path.Combine(sandbox, "others");
        Directory.CreateDirectory(otherDirectory);
        var roots = new[] { sandbox, otherDirectory };
        var duplicate = Path.Combine(otherDirectory, "career.fm");
        File.Copy(game, duplicate);
        File.SetLastWriteTimeUtc(duplicate, File.GetLastWriteTimeUtc(game).AddSeconds(-5));
        Require(SaveCheckpoints.FindRecentLocalSave("career", ".fm", modified, roots) == game &&
                SaveCheckpoints.FindRecentLocalSave("career", ".fm", modified, roots) == game,
            "Selecting the same recent row again resolves its path without any change to preferences");
        Require(SaveCheckpoints.FindRecentLocalSave("other", ".fm", modified, roots) is null &&
                SaveCheckpoints.FindRecentLocalSave("career", ".fm", modified + 1, roots) is null &&
                SaveCheckpoints.FindRecentLocalSave(game, ".fm", modified, roots) is null &&
                SaveCheckpoints.FindRecentLocalSave("career", ".fm26", modified, roots) is null,
            "The recent row must supply a matching basename, expected extension and timestamp");
        File.SetLastWriteTimeUtc(duplicate, File.GetLastWriteTimeUtc(game));
        Require(SaveCheckpoints.FindRecentLocalSave("career", ".fm", modified, roots) is null,
            "The recent row cannot choose between indistinguishable files in different roots");
    }

    private static void CheckRotatingSlots(string sandbox, string actor)
    {
        var store = Path.Combine(sandbox, "rotation-mod-data");
        var checkpoints = new SaveCheckpoints(store);
        var main = Path.Combine(sandbox, "rotation.fm");
        var second = Path.Combine(sandbox, "rotation (v02).fm");
        var third = Path.Combine(sandbox, "rotation (v03).fm");
        var session = checkpoints.CreateSession();

        void Check(string path, int affinity)
        {
            var restored = new SaveCheckpoints(store).Restore(path);
            Require(restored != null && ReadSession(restored, actor).SequenceEqual(Score(affinity)),
                $"Rotating slot {Path.GetFileName(path)} restores revision {affinity} after reopening");
        }

        void CheckEmpty(string path)
        {
            var restored = new SaveCheckpoints(store).Restore(path);
            Require(restored != null && !File.Exists(ActorPath(restored, actor)),
                $"Save {Path.GetFileName(path)} without an exact revision stays isolated");
        }

        static byte[] Score(int value) => Encoding.UTF8.GetBytes($"{{\"affinity\":{value}}}");

        for (var revision = 1; revision <= 4; revision++)
        {
            if (File.Exists(second))
                File.Copy(second, third, overwrite: true);
            if (File.Exists(main))
                File.Copy(main, second, overwrite: true);
            File.WriteAllText(main, $"Game revision {revision}");
            WriteSession(session, actor, Score(revision));
            Require(checkpoints.Store(main, session), "Checkpoint each new primary revision as FM rotates older bytes");
        }

        WriteSession(session, actor, Score(99));
        Check(main, 4);
        Check(second, 3);
        Check(third, 2);
        WriteSession(checkpoints.Restore(second)!, actor, Score(99));
        Check(second, 3);

        var manualCopy = Path.Combine(sandbox, "rotation - copy.fm");
        File.Copy(second, manualCopy);
        CheckEmpty(manualCopy);
        var unsupportedSlot = Path.Combine(sandbox, "rotation (v04).fm");
        File.Copy(second, unsupportedSlot);
        CheckEmpty(unsupportedSlot);
        var revisionTime = File.GetLastWriteTimeUtc(third);
        File.WriteAllText(third, "Game revision 0");
        File.SetLastWriteTimeUtc(third, revisionTime);
        CheckEmpty(third);

        // An explicitly checkpointed second slot can itself be rotated into the third.
        File.WriteAllText(second, "An explicitly saved second-slot revision");
        WriteSession(session, actor, Score(20));
        Require(checkpoints.Store(second, session), "Store a second-slot checkpoint");
        File.Copy(second, third, overwrite: true);
        Check(third, 20);

        // If the same game bytes have conflicting conversation snapshots on two possible
        // predecessor paths, never guess. The exact slot still takes precedence.
        File.Copy(main, second, overwrite: true);
        File.Copy(main, third, overwrite: true);
        WriteSession(session, actor, Score(21));
        var existing = Directory.GetFiles(Path.Combine(store, "checkpoints"), "*.json");
        Require(checkpoints.Store(second, session), "Store different conversation history for the same game bytes");
        var exactSecond = Directory.GetFiles(Path.Combine(store, "checkpoints"), "*.json").Except(existing).Single();
        Check(second, 21);
        Require(checkpoints.Restore(third) == null, "Conflicting predecessor checkpoints cannot restore a rotated slot");

        WriteSession(session, actor, Score(4));
        Require(checkpoints.Store(second, session), "Make the two predecessor snapshots identical");
        Check(third, 4);
        File.WriteAllText(exactSecond, "{broken checkpoint");
        Require(checkpoints.Restore(second) == null, "A corrupt exact slot cannot silently fall back to the primary checkpoint");
    }

    private static void CheckBoardFundingLedger(SaveCheckpoints checkpoints, string sandbox, string actor, byte[] history)
    {
        const uint club = 20, differentClub = 30;
        var day = new DateTime(2026, 11, 17, 15, 0, 0);
        var session = checkpoints.CreateSession();
        WriteSession(session, actor, history);
        Require(BoardFundingLedger.CanOffer(session, club, day) && BoardFundingLedger.TryRecord(session, club, day) &&
                !BoardFundingLedger.TryRecord(session, club, day) && !BoardFundingLedger.CanOffer(session, club, day.AddDays(-1)) &&
                !BoardFundingLedger.CanOffer(session, club, day.AddDays(89)) &&
                BoardFundingLedger.CanOffer(session, club, day.Date.AddDays(90)),
            "Each club can receive one grant every 90 game days and future grant dates block a repeat");
        Require(BoardFundingLedger.CanOffer(session, differentClub, day) && !BoardFundingLedger.CanOffer(session, 0, day) &&
                !BoardFundingLedger.CanOffer(session, club, DateTime.MinValue),
            "Other clubs have independent cooldowns and invalid identities or dates cannot claim");

        var file = Path.Combine(session, BoardFundingLedger.FileName);
        var original = File.ReadAllBytes(file);
        var frozen = checkpoints.Freeze(session);
        Require(frozen is not null && BoardFundingLedger.TryRecord(session, differentClub, day), "Freeze the grant history at save request");

        var game = Path.Combine(sandbox, "board-grant.fm");
        File.WriteAllText(game, "board grant revision A");
        Require(checkpoints.Store(game, frozen!), "Store the grant ledger with the same game revision as its finance balance");
        var restored = checkpoints.Restore(game);
        Require(restored != null && File.ReadAllBytes(Path.Combine(restored, BoardFundingLedger.FileName)).SequenceEqual(original) &&
                !BoardFundingLedger.CanOffer(restored, club, day) && BoardFundingLedger.CanOffer(restored, differentClub, day),
            "Save reload restores exactly the recorded grants and excludes later changes");

        File.WriteAllText(file, "{not json");
        Require(!BoardFundingLedger.CanOffer(session, club, day.AddYears(1)) &&
                !BoardFundingLedger.TryRecord(session, differentClub, day.AddYears(1)) && File.ReadAllText(file) == "{not json",
            "A damaged ledger disables new grants and keeps the original bytes");
        File.WriteAllText(game, "board grant revision B");
        Require(checkpoints.Store(game, session), "An unreadable ledger must preserve the save's conversations and its closed grant state");
        var damaged = checkpoints.Restore(game);
        Require(damaged != null && ReadSession(damaged, actor).SequenceEqual(history) &&
                !BoardFundingLedger.CanOffer(damaged, club, day.AddYears(1)),
            "Reloading a damaged grant ledger cannot silently reset the cooldown");
    }

    private static void CheckPlayerIntroductions(SaveCheckpoints checkpoints, string sandbox)
    {
        const uint manager = 24000, anotherManager = 24001, playerId = 33000;
        var save = Path.Combine(sandbox, "introduced-player.fm");
        File.WriteAllText(save, "first native save contents");
        var session = checkpoints.CreateSession();
        var contacts = new DepartedContacts(session);
        var player = new DirectoryPerson("例示球员", "例", DepartedContacts.IntroducedGroup, "后卫", playerId,
            NativeDisplayName: true, Age: 25, IsFemale: false);
        Require(contacts.AddIntroduction(manager, player, "外队", "法国") &&
                !contacts.AddIntroduction(manager, player, "外队", "法国") &&
                !contacts.AddIntroduction(manager, player with { PublicId = 0 }, "外队", "法国") &&
                !contacts.Introductions(anotherManager).Any(), "Only a unique verified player may appear for this manager");
        Require(checkpoints.Store(save, session), "Introduction follows the FM save checkpoint");
        var restored = new DepartedContacts(checkpoints.Restore(save)!);
        var entry = restored.Introductions(manager).Single();
        Require(entry.PublicId == playerId && entry.Team == DepartedContacts.IntroducedGroup &&
                entry.Profile?.Brief.Contains("外队", StringComparison.Ordinal) == true &&
                entry.Age == 25 && entry.IsFemale == false && !restored.Introductions(anotherManager).Any(),
            "Reload restores the target identity and route outside the roster");
        Require(!restored.Remove(playerId, anotherManager) && restored.Remove(playerId, manager) &&
                !restored.Introductions(manager).Any() && restored.AddIntroduction(manager, player, "外队", "法国"),
            "An introduction can be removed and intentionally requested again");
    }

    private static void CheckDepartedContacts(SaveCheckpoints checkpoints, string sandbox, string actor, byte[] history)
    {
        const uint manager = 10, club = 20, builtInFriend = 99;
        var keeper = new DirectoryPerson("Keeper", "K", "First team", "Goalkeeper", 100);
        var forward = new DirectoryPerson("Forward", "F", "First team", "Forward", 200);
        var midfielder = new DirectoryPerson("Midfielder", "M", "Under 21s", "Midfielder", 300);
        var staff = new DirectoryPerson("Doctor", "D", "Medical team", "Chief Doctor", 400, NativeDisplayName: true);
        var friend = new DirectoryPerson("Friend", "F", "External", "Forward", builtInFriend);
        var none = new HashSet<uint>();
        var noStaff = Array.Empty<DirectoryPerson>();
        var session = checkpoints.CreateSession();
        File.WriteAllText(Path.Combine(session, DepartedContacts.FileName),
            "{\"Version\":1,\"ManagerId\":0,\"ClubId\":0,\"Roster\":[],\"Contacts\":[],\"Removed\":[]}");
        var contacts = new DepartedContacts(session);
        WriteSession(session, actor, history);
        Require(contacts.Favorites.Count == 0 && contacts.AddFavorite(keeper, FavoriteSource.Player) &&
                contacts.AddFavorite(staff, FavoriteSource.Staff) && contacts.AddFavorite(friend, FavoriteSource.Contact) &&
                !contacts.AddFavorite(keeper, FavoriteSource.Contact) &&
                !contacts.AddFavorite(staff with { PublicId = null }, FavoriteSource.Staff),
            "An old contact file starts with no favorites, and each public person ID can be starred once");

        Require(contacts.UpdateRoster(manager, club, new[] { keeper, forward, midfielder }, none, new[] { staff }, none) &&
                contacts.Visible.Count == 0 && !contacts.Remove(100) && !contacts.Remove(400) && !contacts.Remove(builtInFriend),
            "First roster establishes a baseline without adding friends");
        Require(!contacts.UpdateRoster(manager, club, new[] { midfielder, keeper, forward with { Team = "Under 21s" } },
                none, noStaff, none) &&
                contacts.Visible.Count == 0, "Team and list order changes do not create departed players");
        Require(!contacts.UpdateRoster(manager, club, new[] { keeper, keeper }, none, noStaff, none) &&
                contacts.PriorPlayerIds(manager, club).Count == 3,
            "A duplicate roster is rejected without discarding the baseline");

        Require(contacts.UpdateRoster(manager, club, new[] { keeper }, new HashSet<uint> { 200 }, noStaff, new HashSet<uint> { 400 }) &&
                contacts.Visible.Single(entry => entry.Team == DepartedContacts.PlayerGroup).PublicId == 200 &&
                contacts.Visible.Single(entry => entry.Team == DepartedContacts.StaffGroup).PublicId == 400 &&
                contacts.Visible.Single(entry => entry.PublicId == 400).Role == "Chief Doctor" &&
                contacts.PriorPlayerIds(manager, club).Contains(300u),
            "Only confirmed departures move to their respective groups and unresolved members stay in the baseline");

        var save = Path.Combine(sandbox, "contacts.fm");
        File.WriteAllText(save, "contacts revision A");
        var frozen = checkpoints.Freeze(session);
        Require(frozen != null && contacts.Remove(200) && contacts.Remove(400) && !contacts.Remove(200) &&
                !contacts.IsFavorite(400) && !contacts.AddFavorite(staff, FavoriteSource.Contact) &&
                contacts.RemoveFavorite(friend) && !contacts.IsRemoved(builtInFriend) &&
                ReadSession(session, actor).SequenceEqual(history),
            "Removing a contact clears its star without removing chat history, while unstar alone does not hide a person");
        Require(checkpoints.Store(save, frozen!), "The frozen session includes contact state before future edits");
        var restored = checkpoints.Restore(save);
        Require(restored != null, "Contact state is restored with the FM revision");
        var earlier = new DepartedContacts(restored!);
        Require(earlier.CanRemove(200) && earlier.CanRemove(400) && !earlier.IsRemoved(200) && !earlier.IsRemoved(400) &&
                earlier.PriorPlayerIds(manager, club).Contains(300u), "The restored revision retains its contacts and pending roster");
        Require(earlier.Favorites.SequenceEqual(new[] { new FavoriteContact(keeper, FavoriteSource.Player),
                    new FavoriteContact(staff, FavoriteSource.Staff), new FavoriteContact(friend, FavoriteSource.Contact) }),
            "The checkpoint restores favorite identities and fallback details for players, staff and friends");
        Require(earlier.UpdateRoster(manager, club, new[] { keeper, forward }, new HashSet<uint> { 300 }, new[] { staff },
                new HashSet<uint> { 400 }) && earlier.Visible.Single().PublicId == 300 && !earlier.Remove(400),
            "Returning players and staff rejoin the roster while a later departure is added");
        Require(earlier.UpdateRoster(manager, club + 1, new[] { keeper }, none, noStaff, none) &&
                earlier.Visible.Single().PublicId == 300 &&
                earlier.PriorPlayerIds(manager, club).Count == 0, "Changing club does not count its previous roster as departures");

        File.WriteAllText(save, "contacts revision B");
        Require(checkpoints.Store(save, session), "Contact removals can be saved in a new revision");
        var current = new DepartedContacts(checkpoints.Restore(save)!);
        Require(current.IsRemoved(200) && current.IsRemoved(400) && current.Visible.Count == 0 &&
                current.Favorites.Single().Person.PublicId == keeper.PublicId,
            "Removed players and staff remain hidden after reload and do not reappear in favorites");
        Require(current.UpdateRoster(manager, club, new[] { keeper, forward }, none, noStaff, none) &&
                current.UpdateRoster(manager, club, new[] { keeper }, new HashSet<uint> { 200 }, noStaff, none) &&
                current.IsRemoved(200) && current.Visible.All(player => player.PublicId != 200),
            "A removed friend never reappears automatically even after a new departure");

        var ghost = new DirectoryPerson("Match fill", "MF", "First team", "Forward", 500);
        var virtualIds = new HashSet<uint> { 500 };
        var previousRoster = new DepartedContacts(checkpoints.CreateSession());
        Require(previousRoster.UpdateRoster(manager, club, new[] { keeper, ghost }, none, noStaff, none) &&
                previousRoster.UpdateRoster(manager, club, new[] { keeper }, virtualIds, noStaff, none) &&
                previousRoster.AddFavorite(ghost, FavoriteSource.Contact),
            "A previous build may have saved a match-fill player as a departing contact");
        Require(previousRoster.UpdateRoster(manager, club, new[] { keeper, ghost }, virtualIds, noStaff, none, virtualIds) &&
                previousRoster.Visible.Count == 0 && !previousRoster.IsFavorite(500) &&
                !previousRoster.PriorPlayerIds(manager, club).Contains(500u) && !previousRoster.IsRemoved(500),
            "A confirmed match-fill player is removed from the directory, departed contacts and favorites without being tombstoned");
    }

    private static string ActorPath(string session, string file) => Path.Combine(session, "conversations", file);

    private static void WriteSession(string session, string file, byte[] contents) =>
        File.WriteAllBytes(ActorPath(session, file), contents);

    private static byte[] ReadSession(string session, string file) => File.ReadAllBytes(ActorPath(session, file));

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException("Save checkpoint check failed: " + scenario);
    }
}
