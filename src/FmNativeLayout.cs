using System;

namespace FmPlayerTalk;

internal sealed record FmNativeRoutine(ulong Rva, int Length, string Sha256);

// Native addresses belong to a complete, identified game build. Never infer a
// new build by applying one global shift: the game's data blocks move separately.
internal sealed record FmNativeLayout(
    string Build, uint Timestamp, uint ImageSize, ulong BuildString,
    ulong Players, ulong Staff, ulong Player, ulong AlternatePlayer,
    ulong NonPlayer, ulong AlternateNonPlayer, ulong HumanManager,
    ulong Nation, ulong Team, ulong Club, ulong Finance, ulong Interactions)
{
    internal static readonly FmNativeLayout Steam2701 = new(
        "27.0.1+2503222", 0x6AF38E5C, 0x185FA000, 0x4BE54B2,
        0x5820810, 0x5820858, 0x4E2B4E8, 0x508FCB8,
        0x4E90848, 0x508FCA0, 0x4DC48CC,
        0x4DE36F8, 0x4DE51A8, 0x4DDE298, 0x4DF4E38, 0x580F360);

    // Steam 27.1.0: independently checked RTTI, live pools and record layouts.
    // Pool addresses/build metadata also agree with FMLibreEditor profile
    // 3000c4de52b69fb31a61bf27eaf0e483a9ddf3a6, steam-26620374.toml.
    internal static readonly FmNativeLayout Steam2710 = new(
        "27.1.0+2505117", 0x6AF5C68F, 0x18878000, 0x4C3E532,
        0x587B8B8, 0x587B900, 0x4E893D8, 0x50EAC28,
        0x4EECF08, 0x50EAC10, 0x4E2170C,
        0x4E41708, 0x4E42F38, 0x4E3C1C8, 0x4E52C38, 0x586BE40)
    {
        SupportPeople = 0x587B948,
        SupportStaff = 0x50EB9B8,
        SupportPerson = 0x50EB9E8,
        ManagerContactOffset = 0x340,
        RelationsInitializer = new(0x318A2F0, 0xC9,
            "48E62AEEDA80F910B6063533F02F63D6774CC215C5AE299AE7662CEFF8780035"),
        RelationsConstructor = new(0x366C2F0, 0xB9,
            "8F47D7D7E94CE2AC1DC93C4CB6B36A81AA72D75F2DBABD7DECF40B4B2ABEA044")
    };

    internal ulong SupportPeople { get; init; }
    internal ulong SupportStaff { get; init; }
    internal ulong SupportPerson { get; init; }
    internal ulong ManagerContactOffset { get; init; }
    internal FmNativeRoutine? RelationsInitializer { get; init; }
    internal FmNativeRoutine? RelationsConstructor { get; init; }

    internal static FmNativeLayout Current { get; private set; } = Steam2701;

    internal static FmNativeLayout? Match(uint timestamp, uint imageSize) =>
        timestamp == Steam2701.Timestamp && imageSize == Steam2701.ImageSize ? Steam2701 :
        timestamp == Steam2710.Timestamp && imageSize == Steam2710.ImageSize ? Steam2710 : null;

    internal static void Select(FmNativeLayout layout) => Current = layout;

    internal ulong PersonOffset(ulong vtable, bool staff, bool managerOnly)
    {
        if (staff)
            return vtable == HumanManager ? 0x440UL :
                !managerOnly && (vtable == NonPlayer || vtable == AlternateNonPlayer) ? 0x100UL : 0;
        return vtable == Player ? 0x288UL : vtable == AlternatePlayer ? 0x380UL : 0;
    }
}
