using System;
using System.Globalization;
using System.Text.RegularExpressions;
using FM.GamePlugin;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using GameSubsystems = SI.Core.GameSubsystems;
using TypedValue = SI.Core.TypedValue;

namespace FmPlayerTalk;

internal static class GameCalendar
{
    private static readonly Regex CalendarText = new(
        @"(?<!\d)(?:\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{4}年\d{1,2}月\d{1,2}日)(?!\d)",
        RegexOptions.CultureInvariant);
    private static readonly string[] CalendarFormats =
        { "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy年M月d日", "d MMM yyyy", "d MMMM yyyy" };
    private static string? lastResult;

    // Read from Unity's main thread. The bindings belong to the active game world.
    internal static DateTime? ReadDate()
    {
        if (!GameUi.HasActiveCareerEntry())
            return null;
        try
        {
            if (ParseDateText(GameUi.ReadHeaderDateText()) is DateTime header)
                return Report(header, "FM header");
            var bindings = FindBindings();
            if (bindings is null)
                return Report(null, "bindings unavailable");
            if (ReadNativeDate(bindings) is DateTime date)
                return Report(date, "GameDate");
            return Report(ParseDateText(ReadText(bindings, "game.CurrentGameDateString")), "CurrentGameDateString");
        }
        catch (Exception error)
        {
            return Report(null, error.GetType().Name);
        }
    }

    internal static bool IsIdleSinglePlayerCareer(uint manager, uint club)
    {
        if (!GameUi.HasActiveCareerEntry() || FindBindings() is not { } bindings)
            return false;
        try
        {
            return IsFalse(FM.UI.ContinueManagerModule.s_gameProcessingBindingString) &&
                   IsFalse(FM.UI.ContinueManagerModule.s_isOnlineGameBindingString) &&
                   IsFalse(FM.UI.ContinueManagerModule.s_isDuringMatchBindingString) &&
                   LiveClubRoster.PublicId(Read(bindings, "game.GameCurrentManager.UniqueId")) == manager &&
                   LiveClubRoster.PublicId(Read(bindings, "game.GameCurrentManager.Club.UniqueId")) == club;
        }
        catch (Exception)
        {
            return false;
        }

        bool IsFalse(string? path) => !string.IsNullOrWhiteSpace(path) &&
            LiveClubRoster.ReadBoolean(Read(bindings, path)) == false;
    }

    internal static string? ReadManagerName(uint manager)
    {
        if (manager == 0 || !GameUi.HasActiveCareerEntry() || FindBindings() is not { } bindings)
            return null;
        try
        {
            const string path = "game.GameCurrentManager";
            if (LiveClubRoster.PublicId(Read(bindings, path + ".UniqueId")) != manager)
                return null;
            foreach (var member in new[] { ".FirstName", ".ShortName", ".Name", ".FullName" })
            {
                var name = FmDisplayText.Read(ReadText(bindings, path + member))?.Trim();
                if (name is { Length: > 0 and <= 80 } && name.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0)
                    return name;
            }
        }
        catch (Exception)
        {
            // A missing name only changes the form of address.
        }
        return null;
    }

    private static DateTime? ReadNativeDate(BindingSubsystem bindings)
    {
        try
        {
            var date = Read(bindings, "game.CurrentGameDate");
            if (date is not { IsNull: false } || date.DataType?.Name != nameof(GameDate) || date.Get() is not { } boxed)
                return null;
            var native = GameDate.ToDateTime(boxed.Unbox<GameDate>());
            return native.Year >= 1900 ? new DateTime(native.Year, native.Month, native.Day) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static DateTime? ParseDateText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = CalendarText.Match(value);
        return DateTime.TryParseExact(match.Success ? match.Value : value.Trim(), CalendarFormats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1900 ? date : null;
    }

    private static DateTime? Report(DateTime? date, string source)
    {
        var result = date.HasValue ? $"FM calendar {date.Value:yyyy-MM-dd} via {source}." : $"FM calendar unavailable: {source}.";
        if (!string.Equals(lastResult, result, StringComparison.Ordinal))
        {
            lastResult = result;
            Plugin.Logger.LogInfo(result);
        }
        return date;
    }

    private static BindingSubsystem? FindBindings()
    {
        var subsystems = GameSubsystems.s_subsystems;
        if (subsystems is not null)
        {
            for (var index = 0; index < subsystems.Count; index++)
            {
                if (subsystems[index]?.TryCast<BindingSubsystem>() is { } bindings)
                    return bindings;
            }
        }
        return GameSubsystems.GetOptional<BindingSubsystem>();
    }

    private static TypedValue? Read(BindingSubsystem bindings, string path)
    {
        var nativePath = new Il2CppStructArray<char>(path.ToCharArray());
        return bindings.Get(new Il2CppSystem.ReadOnlySpan<char>(nativePath));
    }

    private static string? ReadText(BindingSubsystem bindings, string path)
    {
        try
        {
            var text = Read(bindings, path) is { IsNull: false } value ? value.AsString()?.Trim() : null;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
