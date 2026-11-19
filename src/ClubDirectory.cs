namespace FmPlayerTalk;

internal sealed record DirectoryPerson(string Name, string Initials, string Team, string Role,
    uint? PublicId = null, PlayerProfile? Profile = null, bool NativeDisplayName = false, int? Age = null,
    bool? IsFemale = null);

internal static class ClubDirectory
{
    internal const string AssistantGroup = "经理办公室";
    internal const string LegacyAssistantActorId = "settle-inbox:liu-yifan";

    // Only a verified saved identity can reuse the old conversation key.
    internal static bool UsesLegacyAssistantKey(uint managerId, uint personId, System.DateTime birthDate) =>
        managerId == 2002119250 && personId == 2002119251 &&
        birthDate.Date == new System.DateTime(1994, 3, 9);
}
