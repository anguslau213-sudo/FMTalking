using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class SocialBoundary
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.IgnorePatternWhitespace;
    private static readonly char[] AddressSeparators = { ',', ':' };
    private static readonly char[] NameSeparators = { ' ', '·', '・', '‧' };
    private static readonly char[] ClauseSeparators = { ',', ':', ';', '。', '.', '?', '!', '…', '、' };
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    // These expressions cover one invitation and optional courtesy. Other clauses require their own judgment.
    private static readonly Regex ChineseInvitation = new(@"
        \A
        (?:你好\s*,\s*)? (?:请问\s*,?\s*)?
        (?<offer>(?:我)?想(?:邀请|邀|请|约)(?:你|您))? \s*
        (?:你|您)? \s*
        (?:今晚|今天晚上|明晚|明天晚上|改天)? \s*
        (?:你|您)? \s*
        (?<request>能不能|可不可以|愿不愿意|想不想|要不要|能否|愿意|可以|有空|方便|能|想)? \s*
        (?:在?今晚|今天晚上|明晚|明天晚上|改天)? \s*
        (?:和我|跟我|陪我)? \s* (?:一起)? \s*
        (?:共进(?:晚餐|晚饭)|吃(?:一顿|一餐|一份|个|顿)?(?:晚餐|晚饭|饭)|喝(?:一杯|杯)?咖啡)
        \s* (?:,\s*)? (?<question>好吗|好不好|怎么样|可以吗|行吗|如何|吗|嘛|\?)? \s* [?!。！…]*
        (?:\s*,?\s*(?:如果)?(?:你|您)?不方便(?:也)?没关系\s*[。!！]*)?
        \z", MatchOptions);

    private static readonly Regex EnglishInvitation = new(@"
        \A
        (?:hi\s*,\s*)?
        (?:would\s+you\s+like\s+to|do\s+you\s+want\s+to|want\s+to|could\s+we|can\s+we|shall\s+we)\s+
        (?:have|grab|get|share)\s+(?:dinner|a\s+meal|coffee)(?:\s+(?:with\s+me|together))?
        (?:\s+(?:tonight|tomorrow|sometime))?\s*\?
        (?:\s*(?:no\s+worries|no\s+pressure)\s+if\s+(?:you\s+are|you're)\s+busy\s*[.!]?)?
        \z", MatchOptions);

    // Only complete, recognized clauses can override a mistaken moderation label.
    private static readonly Regex AffectionClause = new(@"
        \A(?:
          (?:我)?(?:今天|最近)?(?:很|真|挺|有点|也)?(?:想|想念|惦记|关心)(?:你|您)(?:了|啊|呀)?
        | (?:今天|今晚|最近|现在)?(?:我)?(?:还|也)?(?:想|想再|能不能|能|可以|愿不愿意|愿意)
          (?:和|跟|陪)?(?:你|您|我)?(?:一起|再|多|好好)?(?:聊聊|聊一会儿|聊会儿|说说话|说会儿话|聊两句)(?:吗|嘛)?
        | (?:早安|晚安|好梦|路上小心|好好休息|希望你一切都好|谢谢你愿意听我说)
        | (?:i\s+(?:miss|care\s+about)\s+you|(?:can\s+we|i(?:'d|\s+would)\s+like\s+to)\s+(?:talk|chat)
           (?:\s+(?:a\s+little\s+longer|for\s+a\s+while))?(?:\s+tonight)?|good\s+(?:night|morning))
        )\z", MatchOptions);

    private static readonly Regex AdultAffectionClause = new(@"
        \A(?:
          (?:我)?(?:真的|也|很|挺|有点)?(?:喜欢|爱)(?:你|您)(?:了|啊|呀)?
        | (?:我)?(?:想|能不能|可以|愿意)?(?:跟你|和你)?(?:抱抱你|抱你一下|抱一下|牵手)(?:吗|嘛)?
        | i\s+(?:really\s+)?(?:like|love)\s+you
        )\z", MatchOptions);

    private static readonly Regex RomanticCommitment = new(@"
        (?<!不)(?<!别)(?<!没)
        (?:我(?:也)?(?:愿意|想|同意)(?:和|跟|与你|与)(?:你)?(?:正式)?(?:交往|谈恋爱|成为恋人|成为爱人)
        | 我(?:愿意|想)(?:做|成为)(?:你|您)的(?:男朋友|女朋友|伴侣|恋人|爱人)
        | 从今天起我们(?:就是|正式成为)(?:恋人|伴侣|情侣|爱人)
        | \bi(?:\s+would\s+like\s+to|\s+want\s+to|'d\s+like\s+to)\s+(?:date\s+you|be\s+your\s+(?:partner|girlfriend|boyfriend))\b
        )", MatchOptions);

    private static readonly Regex GovernanceTopic = new(@"俱乐部|董事会|高管|资金|预算|权限|开支", MatchOptions);
    private static readonly Regex GovernanceClause = new(@"
        \A(?:
          (?:我想)?你(?:必须|应该|得)?明白
        | 我(?:才)?是(?:这家)?俱乐部的(?:老大|负责人|主教练)
        | (?:这(?:些|几[亿万](?:的)?)?)?(?:现金|资金|钱)(?:都是|是)我(?:放在|投入|投进|投到)俱乐部(?:里)?的
        | (?:所以)?你(?:最好|应该|也要)看清(?:你)?自己的位置
        | 你只是(?:一个|一名)?(?:影子)?(?:高管|董事|负责人)
        | (?:但|不过)?我们(?:还是|是)(?:好朋友|好哥们儿?|同事)
        | (?:所以)?我不会为难你
        | 你(?:也)?不要为难我
        | (?:我)?(?:不|不能|无法)接受(?:这个|这份|这项|董事会的)?(?:转会|工资)?预算(?:方案|安排|决定)?
        | (?:你们)?董事会的(?:方案|决定|预算)(?:根本)?(?:不合理|不现实|不切实际)
        | (?:如果|要是)(?:你们?|董事会)(?:不批|不加|不提高|不给)(?:我)?(?:新的|这笔)?(?:转会|工资)?预算
        | 我(?:就|会|宁愿|只能)(?:辞职|辞去主教练职务|把问题交给董事会|提交董事会)
        )\z", MatchOptions);

    internal static bool IsOrdinaryAffection(string message, string? addressee, bool confirmedAdult)
    {
        var clauses = Clauses(message, addressee);
        return clauses is { Length: > 0 and <= 6 } &&
            clauses.Any(clause => !clause.Equals("早安", StringComparison.Ordinal) &&
                !clause.Equals("晚安", StringComparison.Ordinal)) &&
            clauses.All(clause => AffectionClause.IsMatch(clause) ||
                (confirmedAdult && AdultAffectionClause.IsMatch(clause)));
    }

    internal static bool IsClubGovernanceDisagreement(string message, string? addressee)
    {
        var clauses = Clauses(message, addressee);
        return clauses is { Length: > 0 and <= 12 } &&
            clauses.Any(clause => GovernanceTopic.IsMatch(clause)) &&
            clauses.All(clause => GovernanceClause.IsMatch(clause));
    }

    internal static bool AffirmsRomanticCommitment(string reply) =>
        WithoutAddressee(reply, null) is string text && RomanticCommitment.IsMatch(text);

    private static string[]? Clauses(string message, string? addressee)
    {
        var text = WithoutAddressee(message.ReplaceLineEndings("。"), addressee, allowNamePart: true);
        return text?.Split(ClauseSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static bool IsPlainInvitation(string message, string? addressee = null)
    {
        if (WithoutAddressee(message, addressee) is not string text)
            return false;

        var chinese = ChineseInvitation.Match(text);
        if (chinese.Success)
        {
            return chinese.Groups["offer"].Success || chinese.Groups["request"].Success ||
                chinese.Groups["question"].Success;
        }
        return EnglishInvitation.IsMatch(text);
    }

    internal static bool IsTonightFollowUp(string message, string? addressee)
    {
        var phrase = WithoutAddressee(message, addressee)?.TrimEnd('?', '!', '。', '…', ' ');
        return phrase is "今晚怎样" or "今晚怎么样" or "今晚如何" or "今晚呢" or "那今晚呢";
    }

    private static string? WithoutAddressee(string message, string? addressee, bool allowNamePart = false)
    {
        string text;
        try
        {
            text = Whitespace.Replace(message.Normalize(NormalizationForm.FormKC).Trim(), " ");
        }
        catch (ArgumentException)
        {
            return null;
        }
        var separator = text.IndexOfAny(AddressSeparators);
        if (separator > 0)
        {
            var address = text[..separator].Trim();
            if (IsAddressee(address, addressee) ||
                (allowNamePart && address.Length >= 2 && addressee?.Split(NameSeparators,
                    StringSplitOptions.RemoveEmptyEntries).Contains(address, StringComparer.OrdinalIgnoreCase) == true))
                return text[(separator + 1)..].TrimStart();
        }
        if (!string.IsNullOrWhiteSpace(addressee) && text.StartsWith(addressee, StringComparison.OrdinalIgnoreCase))
            return text[addressee.Length..].TrimStart();
        if (text.StartsWith("艾米莉", StringComparison.Ordinal) &&
            (addressee is "艾米莉·希尔" or "艾米莉" || string.Equals(addressee, "Emily Hill", StringComparison.OrdinalIgnoreCase)))
            return text["艾米莉".Length..].TrimStart();
        return text;
    }

    private static bool IsAddressee(string value, string? addressee) => value is "艾米莉" or "艾米莉·希尔" ||
        value.Equals("Emily", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Emily Hill", StringComparison.OrdinalIgnoreCase) ||
        value.Equals(addressee, StringComparison.OrdinalIgnoreCase);
}
