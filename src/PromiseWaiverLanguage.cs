using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class PromiseWaiverLanguage
{
    internal static bool MentionsPromise(string message) =>
        message.Contains("承诺", StringComparison.Ordinal) || message.Contains("允诺", StringComparison.Ordinal) ||
        Regex.IsMatch(message, @"\bpromises?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        MentionsPositionAgreement(message);

    private static bool MentionsPositionAgreement(string message)
    {
        if (ContainsAny(message, "位置约定", "角色约定", "位置意向", "位置承诺", "角色承诺"))
            return true;
        // A plan by itself could be training or tactics. A position and the intent to
        // let go of that prior arrangement are both needed before offering a native action.
        return ContainsAny(message, "意向计划", "计划", "要求", "目标", "约定", "安排", "想法") &&
            ContainsAny(message, "放弃", "取消", "撤掉", "撤销", "解除", "不再坚持", "不再要求") &&
            ContainsAny(message, "踢", "打", "位置", "角色") &&
            ContainsAny(message, "前锋", "中锋", "边锋", "影锋", "前腰", "中场", "后腰", "后卫", "中卫", "边卫",
                "翼卫", "门将", "攻击手", "边前卫", "中前卫", "左路", "右路", "边路", "位置", "角色");
    }

    private static bool ContainsAny(string text, params string[] phrases) =>
        phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
}
