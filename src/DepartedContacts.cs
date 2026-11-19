using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FmPlayerTalk;

internal enum FavoriteSource { Player, Staff, Contact }

internal sealed record FavoriteContact(DirectoryPerson Person, FavoriteSource Source);

internal sealed class DepartedContacts
{
    internal const string FileName = "departed-contacts.json";
    internal const string PlayerGroup = "离队球员";
    internal const string StaffGroup = "离队职员";
    internal const string IntroducedGroup = "助理介绍";
    private const int Version = 1;
    private readonly string path;
    private ContactState state;

    internal DepartedContacts(string sessionDirectory)
    {
        path = Path.Combine(sessionDirectory, FileName);
        var bytes = ReadFile(path);
        state = bytes is null ? new ContactState(Version, 0, 0, Array.Empty<ClubMember>(), Array.Empty<ClubMember>(), Array.Empty<uint>())
            : ReadState(bytes);
        Visible = ToDirectory(state.Contacts);
    }

    internal IReadOnlyList<DirectoryPerson> Visible { get; private set; }

    internal IEnumerable<DirectoryPerson> Introductions(uint managerId) =>
        state.Introductions.Where(contact => contact.ManagerId == managerId).Select(contact => contact.ToDirectory());

    internal IReadOnlyList<FavoriteContact> Favorites => state.Favorites.Select(favorite => favorite.ToContact()).ToArray();

    internal bool IsRemoved(uint publicId) => Array.IndexOf(state.Removed, publicId) >= 0;

    internal bool IsFavorite(uint publicId) => state.Favorites.Any(favorite => favorite.Id == publicId);

    internal bool IsFavorite(DirectoryPerson person) =>
        FavoriteKey(person) is { } key && state.Favorites.Any(favorite => favorite.Key == key);

    internal static string? FavoriteKey(DirectoryPerson? person)
    {
        if (person?.Profile?.Id == ClubDirectory.LegacyAssistantActorId)
            return ClubDirectory.LegacyAssistantActorId;
        return person?.PublicId is > 0 and < uint.MaxValue ? person.PublicId.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    internal bool AddFavorite(DirectoryPerson person, FavoriteSource source)
    {
        var alias = person.Profile?.Id == ClubDirectory.LegacyAssistantActorId ? ClubDirectory.LegacyAssistantActorId : null;
        var id = alias == null ? person.PublicId.GetValueOrDefault() : 0;
        var favorite = new Favorite(id, person.Name, person.Initials, person.Team, person.Role, person.NativeDisplayName, source, alias);
        if (!favorite.IsValid || IsFavorite(person) || (source == FavoriteSource.Contact && IsRemoved(id)))
            return false;

        WriteState(state with { Favorites = state.Favorites.Append(favorite).ToArray() });
        return true;
    }

    internal bool RemoveFavorite(uint publicId)
    {
        if (!IsFavorite(publicId))
            return false;

        WriteState(state with { Favorites = state.Favorites.Where(favorite => favorite.Id != publicId).ToArray() });
        return true;
    }

    internal bool RemoveFavorite(DirectoryPerson person)
    {
        if (FavoriteKey(person) is not { } key || !IsFavorite(person))
            return false;
        WriteState(state with { Favorites = state.Favorites.Where(favorite => favorite.Key != key).ToArray() });
        return true;
    }

    internal IReadOnlyList<uint> PriorPlayerIds(uint managerId, uint clubId) =>
        state.ManagerId == managerId && state.ClubId == clubId
            ? state.Roster.Where(member => !member.IsStaff).Select(member => member.Id).ToArray()
            : Array.Empty<uint>();

    internal bool CanRemove(uint publicId, uint managerId = 0) =>
        state.Contacts.Any(member => member.Id == publicId) ||
        state.Introductions.Any(member => member.Id == publicId && member.ManagerId == managerId);

    internal bool Remove(uint publicId, uint managerId = 0)
    {
        if (!CanRemove(publicId, managerId))
            return false;

        WriteState(state with
        {
            Contacts = state.Contacts.Where(player => player.Id != publicId).ToArray(),
            Introductions = state.Introductions.Where(player => player.Id != publicId || player.ManagerId != managerId).ToArray(),
            Removed = IsRemoved(publicId) ? state.Removed : state.Removed.Append(publicId).ToArray(),
            Favorites = state.Favorites.Where(favorite => favorite.Id != publicId).ToArray()
        });
        return true;
    }

    internal bool AddIntroduction(uint managerId, DirectoryPerson player, string? clubName, string? nationName)
    {
        var id = player.PublicId.GetValueOrDefault();
        var contact = new IntroducedPlayer(managerId, id, player.Name, player.Initials, player.Role, player.Age,
            player.IsFemale, clubName, nationName);
        if (!contact.Valid || state.Introductions.Length >= 2048 || state.Contacts.Any(entry => entry.Id == id) ||
            state.Introductions.Any(entry => entry.ManagerId == managerId && entry.Id == id))
            return false;
        WriteState(state with
        {
            Introductions = state.Introductions.Append(contact).ToArray(),
            Removed = state.Removed.Where(removed => removed != id).ToArray()
        });
        return true;
    }

    internal bool UpdateRoster(uint managerId, uint clubId, IReadOnlyList<DirectoryPerson> currentPlayers,
        IReadOnlySet<uint> confirmedDepartures, IReadOnlyList<DirectoryPerson> currentStaff,
        IReadOnlySet<uint> confirmedStaffDepartures, IReadOnlySet<uint>? virtualPlayers = null)
    {
        if (managerId == 0 || clubId == 0)
            return false;

        var members = new Dictionary<uint, ClubMember>();
        foreach (var person in currentPlayers)
        {
            var player = new ClubMember(person.PublicId.GetValueOrDefault(), person.Name, person.Initials, person.Role);
            if (virtualPlayers?.Contains(player.Id) == true)
                continue;
            if (!player.IsValid || !members.TryAdd(player.Id, player))
                return false;
        }
        foreach (var person in currentStaff)
        {
            var staff = new ClubMember(person.PublicId.GetValueOrDefault(), person.Name, person.Initials, person.Role, IsStaff: true);
            if (!staff.IsValid)
                return false;
            members.TryAdd(staff.Id, staff);
        }

        var contacts = state.Contacts.ToDictionary(player => player.Id);
        foreach (var id in members.Keys)
            contacts.Remove(id);
        if (virtualPlayers != null)
            foreach (var id in virtualPlayers)
                if (contacts.TryGetValue(id, out var member) && !member.IsStaff)
                    contacts.Remove(id);

        if (state.ManagerId == managerId && state.ClubId == clubId)
        {
            var removed = new HashSet<uint>(state.Removed);
            foreach (var previous in state.Roster)
            {
                if (members.ContainsKey(previous.Id) || !previous.IsStaff && virtualPlayers?.Contains(previous.Id) == true)
                    continue;

                var confirmed = previous.IsStaff ? confirmedStaffDepartures : confirmedDepartures;
                if (!confirmed.Contains(previous.Id))
                    members.Add(previous.Id, previous);
                else if (!removed.Contains(previous.Id))
                    contacts.TryAdd(previous.Id, previous);
            }
        }

        var nextRoster = members.Values.OrderBy(player => player.Id).ToArray();
        var nextContacts = contacts.Values.OrderBy(player => player.Id).ToArray();
        var favorites = virtualPlayers is { Count: > 0 }
            ? state.Favorites.Where(favorite => favorite.Source == FavoriteSource.Staff || !virtualPlayers.Contains(favorite.Id)).ToArray()
            : state.Favorites;
        if (state.ManagerId == managerId && state.ClubId == clubId && state.Roster.SequenceEqual(nextRoster) &&
            state.Contacts.SequenceEqual(nextContacts) && state.Favorites.Length == favorites.Length)
            return false;

        WriteState(state with { ManagerId = managerId, ClubId = clubId, Roster = nextRoster, Contacts = nextContacts, Favorites = favorites });
        return true;
    }

    internal static byte[]? ReadFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The contact state file must not be a link.");
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static void Validate(byte[] bytes) => ReadState(bytes);

    private static ContactState ReadState(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<ContactState>(bytes);
        if (result is null || result.Version != Version || result.Roster is null ||
            result.Contacts is null || result.Removed is null || (result.ManagerId == 0) != (result.ClubId == 0) ||
            (result.ClubId == 0 && result.Roster.Length != 0) ||
            result.Roster.Any(player => player is null || !player.IsValid) ||
            result.Contacts.Any(player => player is null || !player.IsValid) || result.Removed.Contains(0u) ||
            result.Favorites is null || result.Favorites.Any(favorite => favorite is null || !favorite.IsValid) ||
            result.Introductions is null || result.Introductions.Length > 2048 ||
            result.Introductions.Any(entry => entry is null || !entry.Valid))
            throw new InvalidDataException("The contact state is unsupported or damaged.");

        var members = new HashSet<uint>();
        var removed = new HashSet<uint>(result.Removed);
        var favorites = new HashSet<string>(StringComparer.Ordinal);
        if (removed.Count != result.Removed.Length || result.Roster.Any(player => !members.Add(player.Id)) ||
            result.Contacts.Any(player => !members.Add(player.Id) || removed.Contains(player.Id)) ||
            result.Introductions.Select(contact => (contact.ManagerId, contact.Id)).Distinct().Count() != result.Introductions.Length ||
            result.Favorites.Any(favorite => !favorites.Add(favorite.Key) ||
                (favorite.Source == FavoriteSource.Contact && removed.Contains(favorite.Id))))
            throw new InvalidDataException("The contact state contains conflicting person identifiers.");
        return result with
        {
            Roster = result.Roster.Select(CleanLegacyText).ToArray(),
            Contacts = result.Contacts.Select(CleanLegacyText).ToArray()
        };
    }

    private static ClubMember CleanLegacyText(ClubMember player)
    {
        var name = FmDisplayText.Read(player.Name);
        if (string.IsNullOrWhiteSpace(name))
            name = "姓名未读取";
        var initials = FmDisplayText.Read(player.Initials);
        var role = FmDisplayText.Read(player.Role);
        return player with
        {
            Name = name,
            Initials = name == player.Name && !string.IsNullOrWhiteSpace(initials) ? initials : FmDisplayText.Initials(name),
            Role = string.IsNullOrWhiteSpace(role) ? (player.IsStaff ? "职员" : "球员") : role
        };
    }

    private void WriteState(ContactState next)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, next);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
        state = next;
        Visible = ToDirectory(next.Contacts);
    }

    private static IReadOnlyList<DirectoryPerson> ToDirectory(ClubMember[] contacts) => contacts
        .Select(player => new DirectoryPerson(player.Name, player.Initials, player.IsStaff ? StaffGroup : PlayerGroup, player.Role, player.Id,
            NativeDisplayName: true)).ToArray();

    private sealed record ContactState(int Version, uint ManagerId, uint ClubId, ClubMember[] Roster, ClubMember[] Contacts, uint[] Removed)
    {
        public Favorite[] Favorites { get; init; } = Array.Empty<Favorite>();
        public IntroducedPlayer[] Introductions { get; init; } = Array.Empty<IntroducedPlayer>();
    }

    private sealed record IntroducedPlayer(uint ManagerId, uint Id, string Name, string Initials, string Position,
        int? Age, bool? IsFemale, string? Club, string? Nation)
    {
        internal bool Valid => ManagerId is > 0 and < uint.MaxValue && Id is > 0 and < uint.MaxValue && Id != ManagerId &&
            !string.IsNullOrWhiteSpace(Name) && Name.Length <= 100 &&
            Initials is { Length: <= 30 } && Position is { Length: > 0 and <= 160 } &&
            (Age == null || Age is >= 10 and <= 125) &&
            (Club == null || Club.Length <= 100) && (Nation == null || Nation.Length <= 100);

        internal DirectoryPerson ToDirectory()
        {
            var club = string.IsNullOrWhiteSpace(Club) ? "" : $"助理介绍时记录的球队是{Club}。";
            var nation = string.IsNullOrWhiteSpace(Nation) ? "" : $"主要国籍是{Nation}。";
            var profile = new PlayerProfile(Id.ToString(CultureInfo.InvariantCulture), Name,
                $"通过主教练的助理认识的球员。{club}{nation}介绍时记录的场上位置是{Position}。")
            {
                RoleInCareer = "外部球员 · 助理介绍",
                DialogueStyle = "你刚通过经理助理与主教练建立联系。可以交流，但你还没有答应加盟或参加谈判。",
                Age = Age, IsFemale = IsFemale
            };
            return new DirectoryPerson(Name, Initials, IntroducedGroup, Position, Id, profile,
                NativeDisplayName: true, Age: Age, IsFemale: IsFemale);
        }
    }

    private sealed record Favorite(uint Id, string Name, string Initials, string Team, string Role, bool NativeDisplayName,
        FavoriteSource Source, string? Alias = null)
    {
        internal string Key => Alias ?? Id.ToString(CultureInfo.InvariantCulture);
        internal bool IsValid => (Alias == null ? Id is > 0 and < uint.MaxValue :
            Alias == ClubDirectory.LegacyAssistantActorId && Id == 0 && Source == FavoriteSource.Staff) && !string.IsNullOrWhiteSpace(Name) &&
            Initials != null && Team != null && Role != null && Enum.IsDefined(Source);

        internal FavoriteContact ToContact() => new(new DirectoryPerson(Name, Initials, Team, Role, Id > 0 ? Id : null,
            Profile: Alias == ClubDirectory.LegacyAssistantActorId ? new PlayerProfile(Alias, Name, "") : null,
            NativeDisplayName: NativeDisplayName), Source);
    }

    private sealed record ClubMember(uint Id, string Name, string Initials, string Role, bool IsStaff = false)
    {
        internal bool IsValid => Id is > 0 and < uint.MaxValue && !string.IsNullOrWhiteSpace(Name) && Initials != null && Role != null;
    }
}
