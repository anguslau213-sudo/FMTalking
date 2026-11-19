using System;
using System.Collections.Generic;
using System.Globalization;
using FM.GamePlugin;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using DateTime = System.DateTime;

namespace FmPlayerTalk;

internal static class RelationshipGameEvents
{
    internal static readonly string[] FixtureFields =
    {
        ".FixtureID", ".FixtureDate", ".FixtureHasResult", ".HomeTeam.UniqueId", ".AwayTeam.UniqueId",
        ".HomeGoals", ".AwayGoals", ".HomeTeam.Name", ".AwayTeam.Name", ".HomeTeamUniqueID", ".AwayTeamUniqueID"
    };
    internal static readonly string[] NewsFields =
    {
        ".NewsItemUniqueId", ".NewsItemId", ".NewsItemTimeSent", ".NewsContextTeam.UniqueId", ".NewsTitleString"
    };

    internal static RelationshipGameEvent? FromFixture(Func<int, TypedValue?> at, uint team, DateTime current)
    {
        try
        {
            if (Date(at(1)) is not { } date)
                return null;
            return RelationshipEventRules.Match(date, current, team, LiveClubRoster.PublicId(at(0)) ?? 0,
                LiveClubRoster.PublicId(at(3)) ?? LiveClubRoster.PublicId(at(9)) ?? 0,
                LiveClubRoster.PublicId(at(4)) ?? LiveClubRoster.PublicId(at(10)) ?? 0,
                Integer(at(5)) ?? -1, Integer(at(6)) ?? -1, LiveClubRoster.ReadBoolean(at(2)) == true,
                Text(at(7)), Text(at(8)));
        }
        catch (Exception) { return null; }
    }

    internal static RelationshipGameEvent? FromNews(Func<int, TypedValue?> at, uint team, string club, DateTime current)
    {
        try
        {
            if (Date(at(2)) is not { } date)
                return null;
            return RelationshipEventRules.Trophy(date, current, team, LiveClubRoster.PublicId(at(3)) ?? 0,
                LiveClubRoster.PublicId(at(0)) ?? LiveClubRoster.PublicId(at(1)) ?? 0, Text(at(4)), club);
        }
        catch (Exception) { return null; }
    }

    private static DateTime? Date(TypedValue? value)
    {
        if (value is not { IsNull: false })
            return null;
        if (value.DataType?.Name == nameof(GameDate) && value.Get() is { } boxed)
        {
            var day = GameDate.ToDateTime(boxed.Unbox<GameDate>());
            return day.Year >= 1900 ? new DateTime(day.Year, day.Month, day.Day) : null;
        }
        var textDate = GameCalendar.ParseDateText(Text(value));
        if (textDate.HasValue)
            return textDate;
        return value.DataType?.Name == "DateTime" && value.Get()?.TryCast<SI.Core.DateTime>() is { } gameDateTime &&
            gameDateTime.IsValid ? GameCalendar.ParseDateText(gameDateTime.ToString()) : null;
    }

    private static int? Integer(TypedValue? value)
    {
        if (value is not { IsNull: false })
            return null;
        if (value.Get() is { } boxed)
        {
            switch (value.DataType?.Name)
            {
                case "Int32": return boxed.Unbox<int>();
                case "UInt32": return boxed.Unbox<uint>() is <= int.MaxValue and var n ? (int)n : null;
                case "Int16": return boxed.Unbox<short>();
                case "UInt16": return boxed.Unbox<ushort>();
                case "Byte": return boxed.Unbox<byte>();
            }
        }
        return int.TryParse(value.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string? Text(TypedValue? value) =>
        value is { IsNull: false } ? FmDisplayText.Read(value.AsString()) : null;

    // The current first-team schedule supplies the same event if the match mail has
    // no fixture attached. No score comes from a conversation or an unscoped headline.
    internal sealed class FixtureRead : IDisposable
    {
        private const string Manager = "game.GameCurrentManager";
        private const string Team = "game.GameCurrentManagerClubTeam";
        private readonly BindingSubsystem bindings;
        private readonly List<Bindings.Key?> keys = new();
        private readonly Bindings.ValueChangedCallback changed = (Action<Bindings.Key, TypedValue>)Ignore;
        private static void Ignore(Bindings.Key key, TypedValue value) { }

        internal FixtureRead(BindingSubsystem bindings)
        {
            this.bindings = bindings;
            Add(Manager + ".UniqueId");
            Add(Manager + ".Club.UniqueId");
            Add(Team + ".UniqueId");
            foreach (var field in FixtureFields)
                Add(Team + ".LastFixture" + field);
        }

        internal RelationshipGameEvent? Read(uint manager, uint club, uint team, DateTime date)
        {
            if (LiveClubRoster.PublicId(Get(0)) != manager || LiveClubRoster.PublicId(Get(1)) != club ||
                LiveClubRoster.PublicId(Get(2)) != team)
                return null;
            return FromFixture(index => Get(index + 3), team, date);
        }

        private TypedValue? Get(int i)
        {
            try { return keys[i] is { } key && bindings.IsDataSet(key) ? bindings.Get(ref key) : null; }
            catch (Exception) { return null; }
        }

        private void Add(string text)
        {
            var path = new Il2CppSystem.ReadOnlySpan<char>(new Il2CppStructArray<char>(text.ToCharArray()));
            try { keys.Add(bindings.Bind(ref path, bindings, changed)); }
            catch (Exception) { keys.Add(null); }
        }

        public void Dispose()
        {
            foreach (var candidate in keys)
            {
                if (candidate is not { } key)
                    continue;
                try { bindings.Unbind(ref key, changed); }
                catch (Exception) { }
            }
            keys.Clear();
        }
    }
}
