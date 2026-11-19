using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FmPlayerTalk;

internal static class RelationshipEventRules
{
    internal const int RecentDays = 2;

    private static readonly Regex NonFinalHeadline = new(
        @"(?:有望|能否|力争|冲击|争取|计划|或将|如果|无缘|未能|未夺|卫冕失败|预测|往事|回顾|小组|预选|半程|\?|？|\b(?:could|might|hope|preview|prediction|if|previously|group|qualifying)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsRecent(DateTime date, DateTime current) =>
        date.Year >= 1900 && (current.Date - date.Date).Days is >= 0 and <= RecentDays;

    internal static RelationshipGameEvent? Match(DateTime date, DateTime current, uint primaryTeam, uint fixtureId,
        uint homeId, uint awayId, int homeGoals, int awayGoals, bool hasResult, string? homeName, string? awayName)
    {
        if (!hasResult || !IsRecent(date, current) || primaryTeam == 0 || fixtureId == 0 || homeId == 0 || awayId == 0 ||
            homeId == awayId || (primaryTeam != homeId && primaryTeam != awayId) ||
            homeGoals is < 0 or > 30 || awayGoals is < 0 or > 30)
            return null;
        var home = primaryTeam == homeId;
        var goals = home ? homeGoals : awayGoals;
        var against = home ? awayGoals : homeGoals;
        if (goals - against < 3 || !TryText(home ? awayName : homeName, 60, out var opponent))
            return null;
        return new(FormattableString.Invariant($"fixture:{date:yyyyMMdd}:{fixtureId}"), date.Date,
            $"以 {goals} 比 {against} 赢了{opponent}");
    }

    internal static RelationshipGameEvent? Trophy(DateTime date, DateTime current, uint primaryTeam, uint newsTeam,
        uint newsId, string? title, string? club)
    {
        if (!IsRecent(date, current) || primaryTeam == 0 || newsTeam != primaryTeam || newsId == 0 ||
            !TryText(club, 70, out var clubName) || !TryText(title, 110, out var headline) ||
            NonFinalHeadline.IsMatch(headline))
            return null;

        var name = Regex.Escape(clubName);
        var chinese = @"\A" + name +
            @"(?:队)?\s*(?:正式|成功|最终|已经|刚刚)?\s*(?:夺冠|(?:夺得|获得|赢得|拿下|问鼎|赢下|夺取)\s*(?:了)?\s*[^，。！？；?]{0,48}(?:冠军|冠军奖杯)|捧起\s*(?:了)?\s*[^，。！？；?]{0,48}(?:奖杯|杯))\s*[!！。]?\z";
        var english = @"\A" + name +
            @"\s+(?:(?:are|were)\s+(?:crowned\s+)?(?:the\s+)?[^.!?]{0,48}champions|(?:have\s+)?(?:won|win|claim|claimed|lift|lifted|clinch|clinched)\s+(?:the\s+)?[^.!?]{0,48}(?:title|cup|trophy))\s*[!.]?\z";
        if (!Regex.IsMatch(headline, chinese, RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(headline, english, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            return null;
        var key = "trophy:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{primaryTeam}:{date:yyyyMMdd}:{headline.ToUpperInvariant()}"))))[..32];
        return new(key, date.Date, headline.TrimEnd('。', '.', '!', '！'));
    }

    private static bool TryText(string? value, int limit, out string text)
    {
        text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > limit)
            return false;
        foreach (var c in text)
            if (char.IsControl(c) || c is '<' or '>' or '\uFFFD')
                return false;
        return true;
    }
}
