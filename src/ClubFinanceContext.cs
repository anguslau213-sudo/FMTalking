using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FM.GamePlugin;
using FM.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using SI.Interop;
using SI.Translation;
using UnityEngine;
using UnityEngine.UIElements;
using NativeCollection = Il2CppSystem.Collections.ICollection;
using NativeList = Il2CppSystem.Collections.IList;

namespace FmPlayerTalk;

internal sealed record ClubFinanceSnapshot(uint ManagerId, uint ClubId, string? OverallBalance,
    string? TransferBudget, string? RemainingWageBudget, string? WageBudget, string? CurrentWageSpending)
{
    internal string? OtherIncomeThisMonth { get; init; }
    internal string? OtherIncomeLastMonth { get; init; }
    internal string? OtherIncomeThisSeason { get; init; }
    internal string? OtherIncomeLastSeason { get; init; }
}

internal static class ClubFinanceContext
{
    private const string Manager = "game.GameCurrentManager";
    private const string Club = Manager + ".Club";
    private const string TeamClub = "game.GameCurrentManagerClubTeam.Club";
    private const float PollInterval = 1f;
    private const float RetryInterval = 3f;
    private const float IncomePollInterval = 12f;
    private const int IncomePathOffset = 8;
    private const int IncomePeriodCount = 4;
    private const int TeamClubIdIndex = IncomePathOffset + IncomePeriodCount;
    private const int TeamIncomePathOffset = TeamClubIdIndex + 1;
    private static readonly string[] Paths =
    {
        Manager + ".UniqueId", Club + ".UniqueId", Club + ".OverallBalance", Club + ".TransferBudget",
        Club + ".RemainingWageBudgetWithText", Club + ".WagesData.CurrentWageBudgetValueWithText",
        Club + ".WagesData.CurrentWageBudgetValueWithShortText", Club + ".CurrentWageSpendingWithText",
        Club + ".IncomeBreakdownThisMonth", Club + ".IncomeBreakdownLastMonth",
        Club + ".IncomeBreakdownThisSeason", Club + ".IncomeBreakdownLastSeason",
        TeamClub + ".UniqueId",
        TeamClub + ".IncomeBreakdownThisMonth", TeamClub + ".IncomeBreakdownLastMonth",
        TeamClub + ".IncomeBreakdownThisSeason", TeamClub + ".IncomeBreakdownLastSeason"
    };

    private sealed record Scope(string SessionDirectory, uint ManagerId, uint ClubId);
    private sealed record ReportedIncome(string? Display, long? BaseAmount, bool ExactDisplay = false);

    private static Scope? scope;
    private static Reader? reader;
    private static float nextPoll;
    private static string? lastAvailability;

    internal static ClubFinanceSnapshot? Current { get; private set; }

    // Run from Unity's main thread. Finance is independent of roster and inbox reads.
    internal static void Tick(LiveRoster? active, bool refresh = false)
    {
        if (active is not { ManagerId: > 0, ClubId: > 0 } ||
            GameSaveTimeline.DirectoryPath is not { } directory || !GameUi.HasActiveCareerEntry())
        {
            Reset();
            return;
        }

        var expected = new Scope(directory, active.ManagerId, active.ClubId);
        if (scope != expected)
        {
            Reset();
            scope = expected;
        }

        var now = Time.unscaledTime;
        if (!refresh && now < nextPoll && reader is not { Changed: true } && reader is not { IncomePending: true })
            return;

        try
        {
            if (reader == null && LiveClubRoster.FindBindings() is { } bindings)
                reader = new Reader(bindings, now);
            if (reader is not { } probe)
            {
                nextPoll = now + RetryInterval;
                return;
            }

            Current = probe.Read(expected, now);
            if (!probe.HasIdentity && now >= probe.StartedAt + RetryInterval)
            {
                CloseReader();
                nextPoll = now + RetryInterval;
                return;
            }

            nextPoll = now + PollInterval;
            if (Current is { } sample)
                Report(sample);
        }
        catch (Exception)
        {
            Current = null;
            CloseReader();
            nextPoll = now + RetryInterval;
        }
    }

    internal static void Reset()
    {
        CloseReader();
        scope = null;
        Current = null;
        nextPoll = 0;
        lastAvailability = null;
    }

    private static void CloseReader()
    {
        reader?.Dispose();
        reader = null;
    }

    private static void Report(ClubFinanceSnapshot sample)
    {
        var fields = new[]
        {
            ("balance", sample.OverallBalance), ("transfer budget", sample.TransferBudget),
            ("remaining wage budget", sample.RemainingWageBudget), ("wage budget", sample.WageBudget),
            ("wage spending", sample.CurrentWageSpending), ("other income this month", sample.OtherIncomeThisMonth),
            ("other income last month", sample.OtherIncomeLastMonth), ("other income this season", sample.OtherIncomeThisSeason),
            ("other income last season", sample.OtherIncomeLastSeason)
        };
        var available = string.Join(", ", fields.Where(field => field.Item2 != null).Select(field => field.Item1));
        if (string.Equals(lastAvailability, available, StringComparison.Ordinal))
            return;
        lastAvailability = available;
        Plugin.Logger.LogInfo($"FM Player Talk finance fields: {(available.Length == 0 ? "unavailable" : available)}.");
    }

    private static uint? PublicId(TypedValue? value)
    {
        if (value is not { IsNull: false })
            return null;
        if (uint.TryParse(value.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return id is > 0 and < uint.MaxValue ? id : null;
        if (value.Get() is not { } boxed)
            return null;
        return value.DataType?.Name switch
        {
            "UInt32" when boxed.Unbox<uint>() is > 0 and < uint.MaxValue => boxed.Unbox<uint>(),
            "Int32" when boxed.Unbox<int>() > 0 => (uint)boxed.Unbox<int>(),
            _ => null
        };
    }

    private static string? DisplayAmount(TypedValue? value, bool withWagePeriod = false)
    {
        if (value is not { IsNull: false })
            return null;

        if (value.DataType?.Name == "String")
            return ReadDisplayText(value.AsString());

        // The native wage strings include the configured pay period. A raw number alone does not.
        if (withWagePeriod || value.Get() is not { } boxed)
            return null;

        if (value.DataType?.Name == nameof(CashTranslatable))
            return FormatCash(boxed.Unbox<CashTranslatable>());

        double? amount = value.DataType?.Name switch
        {
            "Int32" => boxed.Unbox<int>(),
            "UInt32" => boxed.Unbox<uint>(),
            "Int64" => boxed.Unbox<long>(),
            "UInt64" => boxed.Unbox<ulong>(),
            "Single" => boxed.Unbox<float>(),
            "Double" => boxed.Unbox<double>(),
            _ => null
        };

        if (amount is not double raw || !double.IsFinite(raw) || Math.Abs(raw) > 9_007_199_254_740_991d)
            return null;

        return FormatCash(new CashTranslatable(raw)) ?? ReadDisplayText(BindableTranslationUtils.FormatCashValue(raw));
    }

    internal static string? FormatBaseCash(long value) => FormatCash(new CashTranslatable(value));

    internal static long? ReadExactBalance(uint managerId, uint clubId)
    {
        try { return reader?.ReadExactBalance(managerId, clubId); }
        catch (Exception) { return null; }
    }

    private static string? FormatCash(CashTranslatable value)
    {
        try
        {
            var translation = TranslationManager.Format("[%cash-long#1]");
            translation.Add((Translation.TranslationIndex)1u, ref value);
            return ReadDisplayText(translation);
        }
        catch (Exception) { return null; }
    }

    private static string? ReadDisplayText(string? value)
    {
        var text = FmDisplayText.Read(value)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 120 || text.Contains('[', StringComparison.Ordinal) ||
            !text.Any(char.IsDigit) || !text.Any(c => char.IsLetter(c) || char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol))
            return null;
        return text;
    }

    private sealed class Reader : IDisposable
    {
        private readonly BindingSubsystem bindings;
        private readonly Bindings.ValueChangedCallback changed;
        private readonly Bindings.Key?[] keys = new Bindings.Key?[Paths.Length];
        private readonly HashSet<Bindings.Key> arrived = new();
        private IncomeReader? income;
        private StreamedTable? lastMonthTable;
        private Bindings.Key? lastMonthClubKey;
        private NativeList? lastMonthSource;
        private ReportedIncome? lastMonthReportedIncome;
        private string? lastVisibleOtherIncome;
        private (int Year, int Month)? incomeMonth;
        private float nextVisualIncomePoll;
        private ReportedIncome?[] otherIncome = new ReportedIncome?[IncomePeriodCount];
        private float nextIncomePoll;
        private bool incomeChanged;
        private string? lastIncomeAvailability;

        internal Reader(BindingSubsystem bindings, float now)
        {
            this.bindings = bindings;
            StartedAt = now;
            changed = (Action<Bindings.Key, TypedValue>)OnChanged;
            for (var i = 0; i < Paths.Length; i++)
            {
                try
                {
                    var path = new Il2CppSystem.ReadOnlySpan<char>(new Il2CppStructArray<char>(Paths[i].ToCharArray()));
                    keys[i] = bindings.Bind(ref path, bindings, changed);
                }
                catch (Exception)
                {
                    // The other current finance fields can still be read if one property is unavailable.
                }
            }
        }

        internal float StartedAt { get; }
        internal bool Changed { get; private set; }
        internal bool HasIdentity { get; private set; }
        internal bool IncomePending => income != null;

        private void OnChanged(Bindings.Key key, TypedValue value)
        {
            arrived.Add(key);
            Changed = true;
            for (var index = IncomePathOffset; index < Paths.Length; index++)
            {
                if (keys[index] == key)
                    incomeChanged = true;
            }
        }

        internal ClubFinanceSnapshot? Read(Scope expected, float now)
        {
            Changed = false;
            HasIdentity = PublicId(Get(0)) == expected.ManagerId && PublicId(Get(1)) == expected.ClubId;
            if (!HasIdentity)
            {
                CloseIncome();
                Array.Clear(otherIncome, 0, otherIncome.Length);
                ClearVisualIncome();
                return null;
            }

            ReadVisualOtherIncome(expected, now);
            ReadOtherIncome(expected, now);
            var thisMonth = InCurrentCurrency(otherIncome[0]);
            var lastMonth = InCurrentCurrency(otherIncome[1]?.BaseAmount != null || otherIncome[1]?.ExactDisplay == true
                ? otherIncome[1] : lastMonthReportedIncome ??
                    (lastVisibleOtherIncome != null ? new ReportedIncome(lastVisibleOtherIncome, null, ExactDisplay: true) : otherIncome[1]));
            var thisSeason = InCurrentCurrency(otherIncome[2]);
            var lastSeason = InCurrentCurrency(otherIncome[3]);
            return new ClubFinanceSnapshot(expected.ManagerId, expected.ClubId, Amount(2), Amount(3),
                Amount(4, withWagePeriod: true), Amount(5, withWagePeriod: true) ?? Amount(6, withWagePeriod: true),
                Amount(7, withWagePeriod: true))
            {
                OtherIncomeThisMonth = thisMonth,
                OtherIncomeLastMonth = lastMonth,
                OtherIncomeThisSeason = thisSeason,
                OtherIncomeLastSeason = lastSeason
            };
        }

        private static string? InCurrentCurrency(ReportedIncome? income)
        {
            if (income?.BaseAmount is not long amount)
                return income?.Display;

            var fullCash = FormatCash(new CashTranslatable(amount));
            return fullCash ?? income.Display;
        }

        private void ReadOtherIncome(Scope expected, float now)
        {
            try
            {
                if (incomeChanged)
                {
                    CloseIncome();
                    Array.Clear(otherIncome, 0, otherIncome.Length);
                    nextIncomePoll = now;
                    incomeChanged = false;
                }
                if (income == null)
                {
                    if (now < nextIncomePoll)
                        return;
                    nextIncomePoll = now + IncomePollInterval;
                    var teamVerified = PublicId(Get(TeamClubIdIndex)) == expected.ClubId;
                    income = new IncomeReader(bindings, ReadIncomeLists(IncomePathOffset),
                        teamVerified ? ReadIncomeLists(TeamIncomePathOffset) : null, lastMonthSource);
                }
                if (income.Advance(now) is { } result)
                {
                    otherIncome = result;
                    if (income.UsedVisibleLastMonth)
                        lastMonthReportedIncome = result[1];
                    var availability = income.Availability;
                    if (!string.Equals(lastIncomeAvailability, availability, StringComparison.Ordinal))
                    {
                        lastIncomeAvailability = availability;
                        Plugin.Logger.LogInfo($"FM Player Talk income bindings: {availability}.");
                    }
                    CloseIncome();
                    nextIncomePoll = now + IncomePollInterval;
                }
            }
            catch (Exception)
            {
                CloseIncome();
                Array.Clear(otherIncome, 0, otherIncome.Length);
                nextIncomePoll = now + RetryInterval;
            }
        }

        private TypedValue?[] ReadIncomeLists(int start)
        {
            var lists = new TypedValue?[IncomePeriodCount];
            for (var period = 0; period < lists.Length; period++)
            {
                try { lists[period] = Get(start + period); }
                catch (Exception) { /* Each period has a separate native list. */ }
            }
            return lists;
        }

        private void CloseIncome()
        {
            income?.Dispose();
            income = null;
        }

        private void ReadVisualOtherIncome(Scope expected, float now)
        {
            if (now < nextVisualIncomePoll)
                return;
            nextVisualIncomePoll = now + PollInterval;
            try
            {
                var date = GameCalendar.ReadDate();
                (int Year, int Month)? period = date is { } today ? (today.Year, today.Month) : null;
                if (incomeMonth.HasValue && incomeMonth != period)
                {
                    CloseIncome();
                    Array.Clear(otherIncome, 0, otherIncome.Length);
                    nextIncomePoll = now;
                    ClearVisualIncome();
                }
                incomeMonth = period;
                if (!period.HasValue)
                    return;

                var table = GameUi.ActiveCareerDocument?.Q<StreamedTable>("StreamedTable_IncomeLastMonth");
                if (table == null || !Visible(table))
                {
                    ReleaseVisualClubKey();
                    return;
                }
                if (lastMonthTable?.Pointer != table.Pointer)
                {
                    ReleaseVisualClubKey();
                    lastMonthTable = table;
                    var tableKey = table.Key;
                    var path = IncomeReader.Span("Club.UniqueId");
                    lastMonthClubKey = bindings.Bind(ref tableKey, ref path, bindings, changed);
                }
                if (lastMonthClubKey is not { } key || (!arrived.Contains(key) && !bindings.IsDataSet(key)))
                    return;
                if (PublicId(bindings.Get(ref key)) != expected.ClubId)
                {
                    ClearVisualIncome();
                    return;
                }

                var source = table.SourceData;
                if (source != null)
                {
                    if (lastMonthSource?.Pointer != source.Pointer)
                    {
                        CloseIncome();
                        lastMonthSource = source;
                        nextIncomePoll = now;
                    }
                }
                if (ReadVisibleOtherRow(table) is { } value)
                    lastVisibleOtherIncome = value;
            }
            catch (Exception)
            {
                // The finance view may be unloaded between polls.
                ReleaseVisualClubKey();
            }
        }

        private static string? ReadVisibleOtherRow(StreamedTable table)
        {
            var labels = new List<TextElement>();
            GetVisibleLabels(table, labels, 10, 128);
            var other = labels.Where(label => IncomeReader.IsOther(FmDisplayText.Read(label.text)?.Trim())).ToArray();
            if (other.Length != 1)
                return null;
            for (var row = other[0].parent; row != null && row.Pointer != table.Pointer; row = row.parent)
            {
                var cells = new List<TextElement>();
                GetVisibleLabels(row, cells, 8, 3);
                if (cells.Count > 2)
                    return null;
                if (cells.Count == 2)
                    return ReadDisplayText(cells.Single(label => label.Pointer != other[0].Pointer).text);
            }
            return null;
        }

        private static void GetVisibleLabels(VisualElement element, List<TextElement> labels, int depth, int limit)
        {
            if (depth < 0 || labels.Count >= limit || !Visible(element))
                return;
            if (element.TryCast<TextElement>() is { } label && !string.IsNullOrWhiteSpace(label.text))
                labels.Add(label);
            for (var i = 0; i < element.hierarchy.childCount && labels.Count < limit; i++)
                GetVisibleLabels(element.hierarchy.ElementAt(i), labels, depth - 1, limit);
        }

        private static bool Visible(VisualElement element)
        {
            for (var node = element; node != null; node = node.parent)
                if (node.panel == null || node.resolvedStyle.display == DisplayStyle.None ||
                    node.resolvedStyle.visibility == Visibility.Hidden)
                    return false;
            return true;
        }

        private void ClearVisualIncome()
        {
            lastMonthReportedIncome = null;
            lastVisibleOtherIncome = null;
            ReleaseVisualClubKey();
        }

        private void ReleaseVisualClubKey()
        {
            if (lastMonthClubKey is { } key)
            {
                try { bindings.Unbind(ref key, changed); }
                catch (Exception) { }
                arrived.Remove(key);
            }
            lastMonthClubKey = null;
            lastMonthTable = null;
            lastMonthSource = null;
        }

        private string? Amount(int index, bool withWagePeriod = false)
        {
            try { return DisplayAmount(Get(index), withWagePeriod); }
            catch (Exception) { return null; }
        }

        internal long? ReadExactBalance(uint managerId, uint clubId)
        {
            if (PublicId(Get(0)) != managerId || PublicId(Get(1)) != clubId ||
                Get(2) is not { IsNull: false } value || value.Get() is not { } boxed)
                return null;
            return value.DataType?.Name switch
            {
                "Int32" => boxed.Unbox<int>(),
                "Int64" => boxed.Unbox<long>(),
                "Double" when boxed.Unbox<double>() is var amount && amount >= int.MinValue &&
                    amount <= int.MaxValue && amount == Math.Truncate(amount) => (long)amount,
                nameof(CashTranslatable) => boxed.Unbox<CashTranslatable>().Data1,
                "String" => null,
                _ => boxed.TryCast<ICppTranslatable>() is { Type: CppTranslatableType.SicompsCash or
                    CppTranslatableType.SicompsCashExact } nativeCash ? nativeCash.Data1 : null
            };
        }

        private TypedValue? Get(int index)
        {
            if (keys[index] is not { } key || (!arrived.Contains(key) && !bindings.IsDataSet(key)))
                return null;
            return bindings.Get(ref key);
        }

        public void Dispose()
        {
            CloseIncome();
            ReleaseVisualClubKey();
            foreach (var bound in keys)
            {
                if (bound is not { } key)
                    continue;
                try { bindings.Unbind(ref key, changed); }
                catch (Exception) { /* The native binding tree can disappear during a career change. */ }
            }
            arrived.Clear();
        }
    }

    private sealed class IncomeReader : IDisposable
    {
        private const int RowLimit = 64;
        private const int BatchSize = 12;
        private const float BatchWait = 1.1f;

        private sealed record Row(int Period, Il2CppSystem.Object Reference);

        private readonly BindingSubsystem bindings;
        private readonly Bindings.ValueChangedCallback changed;
        private readonly List<Row> rows = new();
        private readonly List<Bindings.Key> scratch = new();
        private readonly List<(Row Row, Bindings.Key Name, Bindings.Key Amount)> batch = new();
        private readonly HashSet<Bindings.Key> arrived = new();
        private readonly int[] matches = new int[IncomePeriodCount];
        private readonly ReportedIncome?[] amounts = new ReportedIncome?[IncomePeriodCount];
        private readonly string[] sources = new string[IncomePeriodCount];
        private int offset;
        private float deadline;
        private bool started;

        internal IncomeReader(BindingSubsystem bindings, IReadOnlyList<TypedValue?> managerLists,
            IReadOnlyList<TypedValue?>? teamLists, NativeList? visibleLastMonth)
        {
            this.bindings = bindings;
            changed = (Action<Bindings.Key, TypedValue>)OnChanged;
            for (var period = 0; period < managerLists.Count; period++)
            {
                var visible = period == 1 ? TryReadRows(period, visibleLastMonth) : 0;
                if (visible > 0)
                {
                    sources[period] = $"finance table:{visible}";
                    UsedVisibleLastMonth = true;
                    continue;
                }
                var manager = TryReadTypedRows(period, managerLists[period]);
                var team = manager > 0 || teamLists == null ? 0 : TryReadTypedRows(period, teamLists[period]);
                sources[period] = manager > 0 ? $"manager:{manager}" : team > 0 ? $"team:{team}" : "unavailable";
            }
        }

        internal bool UsedVisibleLastMonth { get; }

        internal string Availability => string.Join(", ", sources) +
            $"; Other matches: {string.Join("/", matches)}";

        private int TryReadTypedRows(int period, TypedValue? value)
        {
            try { return TryReadRows(period, value is { IsNull: false } ? value.Get()?.TryCast<NativeList>() : null); }
            catch (Exception) { return 0; }
        }

        private int TryReadRows(int period, NativeList? list)
        {
            var start = rows.Count;
            try
            {
                if (list?.TryCast<NativeCollection>() is not { Count: > 0 and <= RowLimit } collection)
                    return 0;
                var count = collection.Count;
                for (var index = 0; index < count; index++)
                {
                    var item = list[index];
                    var actual = item?.TryCast<TypedValue>() is { IsNull: false } wrapped ? wrapped.Get() : item;
                    var reference = actual?.TryCast<FinanceBreakdownRowReference>() as Il2CppSystem.Object ??
                                    actual?.TryCast<InteropReference>() as Il2CppSystem.Object ??
                                    actual?.TryCast<DynamicReference>();
                    if (reference != null)
                        rows.Add(new Row(period, reference));
                }
                if (rows.Count - start == count)
                    return count;
            }
            catch (Exception) { /* Another verified club path can supply this list. */ }
            rows.RemoveRange(start, rows.Count - start);
            return 0;
        }

        internal ReportedIncome?[]? Advance(float now)
        {
            if (started)
            {
                if (now < deadline && batch.Any(row => !IsSet(row.Name) || !IsSet(row.Amount)))
                    return null;
                foreach (var row in batch)
                {
                    if (!IsOther(Name(row.Name)))
                        continue;
                    var period = row.Row.Period;
                    matches[period]++;
                    amounts[period] = matches[period] == 1 ? Amount(row.Amount) : null;
                }
                ReleaseBatch();
            }
            if (offset >= rows.Count)
                return amounts;

            var count = Math.Min(rows.Count - offset, BatchSize);
            for (var index = 0; index < count; index++)
            {
                var row = rows[offset + index];
                var root = "fmPlayerTalkFinanceIncomeRow" + index.ToString(CultureInfo.InvariantCulture);
                var path = Span(root);
                var value = row.Reference switch
                {
                    FinanceBreakdownRowReference native => TypedValue.Create(native),
                    InteropReference interop => TypedValue.Create(interop),
                    DynamicReference dynamic => TypedValue.Create(dynamic),
                    _ => throw new InvalidOperationException("Unsupported income breakdown reference.")
                };
                scratch.Add(bindings.Set(ref path, value, Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default));
                var namePath = Span(root + ".Name");
                var nameKey = bindings.Bind(ref namePath, bindings, changed);
                try
                {
                    var amountPath = Span(root + ".PropertyValue");
                    var amountKey = bindings.Bind(ref amountPath, bindings, changed);
                    batch.Add((row, nameKey, amountKey));
                }
                catch (Exception)
                {
                    Unbind(nameKey);
                }
            }
            offset += count;
            deadline = now + BatchWait;
            started = true;
            return null;
        }

        internal static Il2CppSystem.ReadOnlySpan<char> Span(string path) => new(new Il2CppStructArray<char>(path.ToCharArray()));

        internal static bool IsOther(string? name) => name is "其他" or "其它" or "其他收入" or "其它收入" ||
            string.Equals(name, "Other", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Other Income", StringComparison.OrdinalIgnoreCase);

        private void OnChanged(Bindings.Key key, TypedValue value) => arrived.Add(key);

        private bool IsSet(Bindings.Key key) => arrived.Contains(key) || bindings.IsDataSet(key);

        private string? Name(Bindings.Key key)
        {
            try
            {
                if (!IsSet(key) || bindings.Get(ref key) is not { IsNull: false } value)
                    return null;
                if (value.DataType?.Name == nameof(TranslationID) && value.Get() is { } boxed)
                    return FmDisplayText.Read(boxed.Unbox<TranslationID>().TranslatedString)?.Trim();
                return FmDisplayText.Read(value.AsString())?.Trim();
            }
            catch (Exception) { return null; }
        }

        private ReportedIncome? Amount(Bindings.Key key)
        {
            try
            {
                if (!IsSet(key) || bindings.Get(ref key) is not { IsNull: false } value)
                    return null;
                if (FormatBoundCash(value) is { } formatted)
                    return new ReportedIncome(formatted, null, ExactDisplay: true);
                if (value.DataType?.Name == "String")
                {
                    var text = ReadDisplayText(value.AsString());
                    return text == null ? null : new ReportedIncome(text, null, ExactDisplay: true);
                }
                if (value.DataType?.Name == nameof(CashTranslatable) && value.Get() is { } boxed)
                {
                    var cash = boxed.Unbox<CashTranslatable>();
                    return new ReportedIncome(FormatCash(cash), cash.Data1);
                }
                if (value.Get()?.TryCast<ICppTranslatable>() is { } nativeCash &&
                    nativeCash.Type is CppTranslatableType.SicompsCash or CppTranslatableType.SicompsCashExact)
                    return new ReportedIncome(FormatCash(new CashTranslatable(nativeCash.Data1)), nativeCash.Data1);
                if (DisplayAmount(value) is { } display)
                    return new ReportedIncome(display, null);
                return null;
            }
            catch (Exception) { return null; }
        }

        private static string? FormatBoundCash(TypedValue value)
        {
            try
            {
                // Match the native FinancesReportBreakdownTotalCell's cash argument.
                var translation = TranslationManager.Format("[%cash-long#1]");
                var argument = new TranslationArg
                {
                    Index = "1",
                    m_format = new TranslationFormat { Value = "cash" },
                    AddEmbeddedData = false
                };
                BindableTranslationUtils.AddArgumentToTranslation(translation, value, ref argument, errorOnInvalidValue: false);
                return ReadDisplayText(translation);
            }
            catch (Exception) { return null; }
        }

        private void Unbind(Bindings.Key key)
        {
            try { bindings.Unbind(ref key, changed); }
            catch (Exception) { /* The native binding tree can disappear during a career change. */ }
        }

        private void ReleaseBatch()
        {
            foreach (var row in batch)
            {
                Unbind(row.Name);
                Unbind(row.Amount);
            }
            batch.Clear();
            arrived.Clear();
            foreach (var scratchKey in scratch)
            {
                var key = scratchKey;
                try { bindings.Remove(ref key); }
                catch (Exception) { /* The native binding tree can disappear during a career change. */ }
            }
            scratch.Clear();
        }

        public void Dispose() => ReleaseBatch();
    }
}
