using System;
using System.Threading.Tasks;

namespace FmPlayerTalk;

internal sealed partial record PlayerProfile
{
    internal static readonly PlayerProfile Umar = new("2000397002", "奥马尔·纳瓦兹",
        "Umar Nawaz。FM27 初始截图中的青训球员，14 岁，2012-01-28 出生，可踢中场、前腰（左右两路）和前锋（中路）。")
    {
        RoleInCareer = "主教练 · 球员",
        Age = 14,
        DialogueStyle = "你面对的是主教练。可以害羞、犹豫或不同意，围绕训练、上场、球队和感受交流。遇到越界话题时简短拒绝，回到青训场景。"
    };

    internal static readonly PlayerProfile Mbappe = new("85139014", "基利安·姆巴佩",
        "Kylian Mbappé。FM27 测试存档快照中效力皇家马德里，27 岁，可踢中锋和左边锋，属于世界级球员。")
    {
        RoleInCareer = "外部球员",
        Age = 27,
        DialogueStyle = "你的态度自信而直接，可以对不切实际的提议冷淡或不以为然。"
            + "如果对方要邀请你加盟，先比较对方俱乐部的联赛层级和你在皇家马德里的竞技平台，再考虑薪资条件与职业规划。"
            + "面对相差悬殊的竞技平台和没有现实条件的直接邀约，应明确拒绝，并说明一个足球上的理由。"
            + "如果教练给出可信且符合职业目标的计划，你可以从拒绝转为愿意听方案，或在可验证的条件达成后再谈。"
            + "不要承诺加盟、试训或续约，不要捏造具体薪资、合同、比赛或真实人物说过的话。"
    };

    internal static readonly PlayerProfile RelationshipTestClose = new("relationship-test:2000455984",
        "艾米莉·希尔", "Emily Hill，塞特尔一线队球探，测试档中 27 岁。此独立测试情境设定双方成年，已是相熟的朋友。")
    {
        RoleInCareer = "关系测试 · 塞特尔球探",
        DialogueStyle = "和主教练相熟，可以用自然、轻松的口吻沟通；在工作上仍有自己的判断。",
        Age = 27,
        IsFemale = true,
        StartingAffinity = 100,
        StartingRelationship = RelationshipProposal.Friend
    };

    internal static readonly PlayerProfile RelationshipTestNearBlock = new("relationship-test:85139014",
        Mbappe.Name, Mbappe.Brief + "这是独立测试情境，你因先前摩擦已接近中断联系。")
    {
        RoleInCareer = "关系测试 · 外部球员",
        DialogueStyle = Mbappe.DialogueStyle + "当下对对方戒心较强，对合理陈述仍可回应。",
        StartingAffinity = -19
    };
}

public sealed partial class Conversation
{
    internal Conversation(string careerKey, string careerLabel, string dataDirectory,
        Func<string, Task<Reply>>? request = null)
        : this(careerKey, dataDirectory, PlayerProfile.Umar, request, timingLog: null, managerClub: null)
    {
    }
}
