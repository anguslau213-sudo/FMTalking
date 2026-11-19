using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class PlayerIntroductionRules
{
    private static readonly Regex Direct = new(
        @"\A\s*(?:查找球员|介绍球员|球员联系方式|Find player|選手を探す|선수 찾기)\s*[:：]\s*(?<name>[^\r\n]{1,100})\s*\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const string SearchVerb = "找一找|找一下|查一下|打听一下|介绍一下|弄到|拿到|找到|找找|找|查|打听|要|了解|获取";
    private const string ContactNoun = "联系方式|联系渠道|联络方式";
    private const string SentenceEnd = @"\s*(?:吗|吧|呢|行吗|可以吗)?\s*\z";
    private static readonly Regex Clauses = new(@"[，,。？?！!；;\r\n]+", RegexOptions.CultureInvariant);
    private static readonly Regex CancelledRequest = new(
        @"(?:不需要|不想要|不想找|不要(?!忘记)|不用|不必|无需|别(?!忘了))\s*(?:再)?\s*(?:[^，,。？?！!；;\r\n]{0,100}(?:" + ContactNoun +
        @")|(?:你|帮我|替我|去|再|继续){0,3}(?:找|查|打听|联系|介绍))|(?:取消|撤销)(?:这个|刚才的)?(?:请求|介绍|查找)",
        RegexOptions.CultureInvariant);
    private static readonly Regex Natural = new(
        @"\A\s*(?:再|另外|还|也|同时|顺便)?\s*(?:(?<intent>你?能不能|你?能|能否|可以|请|麻烦你?|我想请你|我需要你|我想让你|我希望你|我需要|我想要|我要|我想|别忘了|不要忘记)\s*){0,2}" +
        @"(?<intent>帮我|替我|给我|帮忙|让我)?\s*(?:去|试着)?\s*(?<intent>" + SearchVerb +
        @")?\s*(?<name>[^\r\n，,。？?！!；;]{1,100}?)\s*(?:的)?\s*(?:" + ContactNoun + ")" + SentenceEnd,
        RegexOptions.CultureInvariant);
    private static readonly Regex TrailingRequest = new(
        @"\A\s*(?<name>[^\r\n，,。？?！!；;]{1,100}?)\s*(?:的)?\s*(?:" + ContactNoun +
        @")\s*(?:(?:你?能不能|你?能|能否|可以|请|麻烦你?)\s*){0,2}(?:帮我|替我|给我|帮忙)?\s*(?:去|试着)?\s*(?:" +
        SearchVerb + ")" + SentenceEnd,
        RegexOptions.CultureInvariant);
    private static readonly Regex NameCharacters = new(
        @"\A\p{L}[\p{L}\p{M} .·'’\-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex ContactName = new(
        @"\A\s*(?<name>[^\r\n，,。？?！!；;]{1,100}?)\s*(?:的)?\s*(?:" + ContactNoun + @")\s*\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ChineseConversation = new(
        @"\A(?:好|好的|好吧|谢谢|多谢|你好|您好|明白|明白了|知道|知道了|明天再说|下次再说|不用|不必|不需要|不想|不要|不对|不是|不是他|不是她|不是这位|不是这个人|都不是|对的|对了|是的|就是他|就是她|取消|取消吧|不用找了|别找了|可以|愿意|需要|想要|再说|试试|看看|找找|查查|算了|没错|没事|没关系|辛苦|收到|当然)\z|" +
        @"\A(?:再|去)?(?:找一找|找找|查一下|查查|试试|试试看|看看)(?:吧|看|啊|呀)?\z|" +
        @"\A(?:(?:今天|明天|昨天|现在|刚才|以后|之后|下次)(?:比赛|训练|再说|再聊|先|再)|(?:我|你|您)(?:知道|明白|需要|想|可以|愿意|先|再|已经|还|也)|(?:已经|还没|还没有|暂时|刚刚)(?:拿到|找到|联系|核实|确认|收到)|那就|请|麻烦|帮我|给我|不用|不必|不需要|不要|是否|怎么|什么|如何)|(?:我需要|我想请|我想要|我想让|帮我|给我|联系方式|联系渠道|联络方式)",
        RegexOptions.CultureInvariant);
    private static readonly Regex EnglishConversation = new(
        @"\A(?:yes|no|ok|okay|thanks|thank you|thank you very much|hello|hi|please|sorry|sure|fine|great|good|good morning|good evening|good job|well done|nice work|understood|i understand|tomorrow|today|later|all right|not now|go ahead|sounds good|find him|find her|not him|not her)\z|\A(?:(?:i|we)\s+(?:am|are|need|want|will|would|can|understand)|(?:can|could|would|will)\s+you|please\s+(?:find|search|contact)|my\s+name\s+is)(?:\s|\z)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static string? ParseTarget(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 160)
            return null;
        if (CancelledRequest.IsMatch(input))
            return null;
        var match = Direct.Match(input);
        if (match.Success)
        {
            var body = match.Groups["name"].Value;
            // The button contributes the intent, not a second name grammar.
            // A natural sentence after its prefix still needs name extraction.
            var target = ParseNaturalTarget(body);
            if (target != null)
                return target;
            var contact = ContactName.Match(body);
            if (contact.Success && Regex.IsMatch(body, @"\A\s*(?:这是|那是|这个是|那个是|我已经有|我们已经有)"))
                return null;
            return contact.Success ? ParseNameSupplement(contact.Groups["name"].Value) : ParseNameSupplement(body);
        }
        return ParseNaturalTarget(input);
    }

    private static string? ParseNaturalTarget(string input)
    {
        string? target = null;
        foreach (var clause in Clauses.Split(input))
        {
            var match = Natural.Match(clause);
            if (!match.Success || !match.Groups["intent"].Success)
                match = TrailingRequest.Match(clause);
            if (!match.Success || ConcreteTarget(match.Groups["name"].Value) is not { } candidate)
                continue;
            if (target != null && MatchKey(target) != MatchKey(candidate))
                return null;
            target = candidate;
        }
        return target;
    }

    // Only the caller's explicit "awaiting a name" state may use this. A plain
    // name is not a new introduction request during an unrelated conversation.
    internal static string? ParseNameSupplement(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 100)
            return null;
        var name = input.Trim();
        var explicitId = Regex.Match(name, @"\A(?:ID|人员ID)\s*[:：]\s*(?<id>#?[0-9]{1,10})\z",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (explicitId.Success)
            name = explicitId.Groups["id"].Value;
        if (ParsePublicId(name) is uint id)
            return id.ToString(CultureInfo.InvariantCulture);
        if (ConcreteTarget(name) == null || !NameCharacters.IsMatch(name) ||
            ChineseConversation.IsMatch(name) || EnglishConversation.IsMatch(name.TrimEnd('.')))
            return null;
        var letters = 0;
        foreach (var rune in name.EnumerateRunes())
            if (Rune.IsLetter(rune))
                letters++;
        return letters >= 2 ? name : null;
    }

    private static string? ConcreteTarget(string value)
    {
        var target = value.Trim();
        return target is "他" or "她" or "他们" or "她们" or "你" or "我" or "这名球员" or "那名球员" or
            "这位球员" or "那位球员" or "这个人" or "那个人" || MatchKey(target).Length == 0 ? null : target;
    }

    internal static string MatchKey(string value)
    {
        var key = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune))
                key.Append(Rune.ToUpperInvariant(rune).ToString());
        return key.ToString();
    }

    internal static uint? ParsePublicId(string target) =>
        uint.TryParse(target.Trim().TrimStart('#', '＃'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
        id is > 0 and < uint.MaxValue ? id : null;

    // The game does not keep a contact address book. The mod uses the manager's verified
    // primary nationality as a route to an introduction and reputation for international reach.
    internal static bool AllowsInternational(int? managerWorldReputation, int? playerWorldReputation) =>
        managerWorldReputation is >= 0 and <= 10000 && playerWorldReputation is >= 0 and <= 10000 &&
        managerWorldReputation >= playerWorldReputation.Value - 1500;

    internal static bool SameNationality(uint managerNation, uint playerNation) =>
        managerNation is > 0 and < uint.MaxValue && managerNation == playerNation;
}
