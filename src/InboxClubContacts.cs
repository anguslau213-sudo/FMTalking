using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FM.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SI.Bindable;
using SI.Core;
using SI.Interop;
using SI.UI;
using UnityEngine.UIElements;
using DateTime = System.DateTime;
using NativeCollection = Il2CppSystem.Collections.ICollection;
using NativeList = Il2CppSystem.Collections.IList;

namespace FmPlayerTalk;

internal sealed class InboxClubContacts : IDisposable
{
    private const string Manager = "game.GameCurrentManager";
    private const string ManagedTeam = "game.GameCurrentManagerClubTeam";
    private const string Inbox = Manager + ".HumanIncomingMessages";
    private const int SampleSize = 24;
    private const int BatchSize = 6;
    private const int UiSearchNodeLimit = 2600;
    private const int UiSearchDepthLimit = 64;
    private const int UiSourceLimit = 4;
    private const float InboxWait = 3f;
    private const float BatchWait = 1.4f;
    private const float VerifyWait = 2f;
    private const float ScanLimit = 11f;

    private static readonly string[] SenderFields =
    {
        ".NewsPersonSender.UniqueId", ".NewsPersonSender.Name", ".NewsPersonSender.JobString",
        ".NewsPersonSender.ContractClub.UniqueId", ".NewsPersonSender.Club.UniqueId",
        ".NewsPersonSender.Team.UniqueId", ".NewsPersonSender.Team.Club.UniqueId",
        ".NewsPersonSenderTeam.UniqueId", ".NewsPersonSenderTeam.Club.UniqueId"
    };
    private enum Phase { Inbox, Senders, Verify }

    private readonly BindingSubsystem bindings;
    private readonly uint managerId;
    private readonly uint clubId;
    private readonly uint primaryTeamId;
    private readonly uint? assistantId;
    private readonly string clubName;
    private readonly DateTime? sampledDate;
    private readonly float deadline;
    private readonly Bindings.ValueChangedCallback changed;
    private readonly List<Bindings.Key?> fields = new();
    private readonly HashSet<Bindings.Key> notified = new();
    private readonly List<Bindings.Key> scratch = new();
    private readonly Dictionary<uint, DirectoryPerson> found = new();
    private readonly Dictionary<uint, PlayerPortraits.InboxPortrait> portraits = new();
    private readonly List<InteropReference> messages = new();
    private readonly List<(int NewsStart, int FixtureStart)> eventRows = new();
    private readonly Dictionary<string, RelationshipGameEvent> gameEvents = new(StringComparer.Ordinal);
    private Phase phase;
    private float phaseDeadline;
    private int offset;
    private int batchCount;
    private int appearanceDayIndex;
    private int sameClubWithoutId;
    private int onlyMessageAffiliation;
    private int resolvedRows;
    private bool sampledSelectedMessage;
    private bool started;
    private bool finished;
    private DirectoryPerson[]? result;
    private string sourceLabel = "game binding";
    private static string? lastDiagnostic;

    internal DirectoryPerson? VisibleAssistant { get; private set; }
    internal RelationshipGameEvent[] VerifiedGameEvents { get; private set; } = Array.Empty<RelationshipGameEvent>();

    private InboxClubContacts(BindingSubsystem bindings, uint managerId, uint clubId, uint primaryTeamId,
        uint? assistantId, float now, string clubName, DateTime? sampledDate)
    {
        this.bindings = bindings;
        this.managerId = managerId;
        this.clubId = clubId;
        this.primaryTeamId = primaryTeamId;
        this.assistantId = assistantId;
        this.clubName = clubName;
        this.sampledDate = sampledDate;
        deadline = now + ScanLimit;
        changed = (Action<Bindings.Key, TypedValue>)OnChanged;
    }

    internal static InboxClubContacts Create(BindingSubsystem bindings, uint managerId, uint clubId, uint primaryTeamId,
        uint? assistantId, float now, string clubName, DateTime? sampledDate) =>
        new(bindings, managerId, clubId, primaryTeamId, assistantId, now, clubName, sampledDate);

    // false means pending. A null completed result denotes an unavailable source or a scope change.
    internal bool TryRead(float now, out DirectoryPerson[]? contacts)
    {
        if (!finished)
        {
            try
            {
                Advance(now);
            }
            catch (Exception error)
            {
                Complete(null, $"unavailable ({phase}, {error.GetType().Name})");
            }
        }
        contacts = result;
        return finished;
    }

    private void Advance(float now)
    {
        if (now > deadline)
        {
            Complete(null, "scan timed out");
            return;
        }
        if (!started)
        {
            started = true;
            Open(Phase.Inbox, now + InboxWait);
            Bind(Inbox, optional: true);
        }
        if (now <= phaseDeadline && !fields.All(Arrived))
            return;

        switch (phase)
        {
            case Phase.Inbox:
                var bindingStatus = SampleGameBinding(out var emptyBindingList);
                if ((messages.Count == 0 || assistantId.HasValue) && GameUi.ActiveCareerDocument is { } document)
                {
                    var nodes = UiSearchNodeLimit;
                    var sources = UiSourceLimit;
                    if (SampleNativeView(document, UiSearchDepthLimit, ref nodes, ref sources))
                        sourceLabel = "original inbox UI";
                }
                if (messages.Count == 0 && !emptyBindingList)
                {
                    Complete(null, $"inbox list unavailable ({bindingStatus}; no populated original inbox view sampled)");
                    return;
                }
                NextBatch(now);
                break;
            case Phase.Senders:
                ReadSenders();
                ReadGameEvents();
                offset += batchCount;
                NextBatch(now);
                break;
            case Phase.Verify:
                if (Id(Read(0)) != managerId || Id(Read(1)) != clubId || Id(Read(2)) != primaryTeamId)
                {
                    Complete(null, "managed club changed or is unavailable");
                    return;
                }
                VerifiedGameEvents = gameEvents.Values.ToArray();
                ReadVisibleAssistantName();
                if (messages.Count > 0 && resolvedRows == 0 && found.Count == 0)
                {
                    Complete(null, "sender fields unavailable");
                    return;
                }
                PlayerPortraits.SetInboxPortraits(portraits);
                var assistantFound = assistantId is uint id && found.ContainsKey(id)
                    ? ", read the verified manager assistant's sender name" : "";
                Complete(found.Values.OrderBy(entry => entry.PublicId).ToArray(),
                    $"{sourceLabel}: {messages.Count} message rows, {found.Count} verified senders with public IDs, " +
                    $"{sameClubWithoutId} same-club sender rows without public IDs, " +
                    $"{onlyMessageAffiliation} rows with only historical message affiliation{assistantFound}");
                break;
        }
    }

    private void ReadVisibleAssistantName()
    {
        if (assistantId is not uint id || GameUi.ActiveCareerDocument is not { } document)
            return;
        try
        {
            if (document.Q<MessagesStreamedObjectList>("PortalMessages") is not { panel: not null } view ||
                view.ListElement is not { } list)
                return;
            foreach (var labelName in new[] { "PersonSenderNameNoLink", "PersonSenderName" })
            {
                    var labels = view.Query<TextElement>(labelName, (string?)null).ToList();
                foreach (var label in labels)
                {
                    if (!label.visible || label.worldBound.width <= 1 || label.worldBound.height <= 1 ||
                        string.IsNullOrWhiteSpace(label.text) ||
                        list.FindActiveItemContaining(label) is not { Content: { } row } ||
                        ReadVisibleSenderId(label, row) != id ||
                        FmDisplayText.Read(label.text) is not { Length: > 0 } name)
                        continue;

                    var initials = FmDisplayText.Initials(name);
                    VisibleAssistant = found.TryGetValue(id, out var sender)
                        ? sender with { Name = name, Initials = initials }
                        : new DirectoryPerson(name, initials, ClubDirectory.AssistantGroup, "经理专属联络人", id, NativeDisplayName: true);
                    found[id] = VisibleAssistant;
                    return;
                }
            }
        }
        catch (Exception) { /* The native list may recycle its rows during a page change. */ }
    }

    private uint? ReadVisibleSenderId(VisualElement label, VisualElement row)
    {
        if (!row.Contains(label))
            return null;
        for (var node = label; node != null; node = node.parent)
        {
            if (node.TryCast<BindingRoot>() is { } root)
            {
                var key = root.Key;
                var path = Span("newsitem.NewsPersonSender.UniqueId");
                if (Id(bindings.Get(ref key, ref path)) is { } id)
                    return id;
            }
            if (node.Pointer == row.Pointer)
                break;
        }
        return null;
    }

    private string SampleGameBinding(out bool emptyList)
    {
        emptyList = false;
        try
        {
            var source = Read(0);
            if (source is not { IsNull: false } || source.Get() is not { } boxed)
                return "game binding returned no value";
            var type = SourceType(source);
            if (boxed.TryCast<NativeList>() is not { } inbox || boxed.TryCast<NativeCollection>() is not
                { Count: >= 0 } collection)
                return $"game binding type {type} is not a readable list";
            var count = collection.Count;
            emptyList = count == 0;
            Sample(inbox, count);
            return messages.Count > 0 ? "game binding sampled" : emptyList ? "game binding list empty" :
                $"game binding type {type} has no supported rows";
        }
        catch (Exception error)
        {
            return $"game binding read failed ({error.GetType().Name})";
        }
    }

    private static string SourceType(TypedValue source)
    {
        var name = source.DataType?.Name;
        return name is { Length: > 0 and <= 60 } &&
            name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '`')
            ? name : "unknown";
    }

    private bool SampleNativeView(VisualElement node, int depth, ref int nodes, ref int sources)
    {
        if (depth < 0 || nodes <= 0 || sources <= 0)
            return false;
        nodes--;
        if (node.name == "fm-player-talk-panel" || !node.visible || node.resolvedStyle.display == DisplayStyle.None)
            return false;
        if (node.TryCast<MessagesStreamedObjectList>() is { } view)
        {
            sources--;
            var hadMessages = messages.Count > 0;
            SampleSelectedMessage(view);
            try
            {
                // The original UI owns this list and its selection. Reading the existing list sends no FM event.
                if (!hadMessages && view.SourceData is { } inbox &&
                    inbox.TryCast<NativeCollection>() is { Count: > 0 } collection)
                    Sample(inbox, collection.Count);
            }
            catch (Exception)
            {
                // The game can rebuild a streamed list between rendering and this tick.
            }
            if (sampledSelectedMessage || !hadMessages && messages.Count > 0)
                return true;
        }
        for (var index = 0; index < node.hierarchy.childCount && nodes > 0 && sources > 0; index++)
        {
            if (node.hierarchy.ElementAt(index) is { } child &&
                SampleNativeView(child, depth - 1, ref nodes, ref sources))
                return true;
        }
        return false;
    }

    private void SampleSelectedMessage(MessagesStreamedObjectList view)
    {
        try
        {
            // Its selection can change independently of the inbox stream.
            if (view.GetSelectedItem() is not { IsNull: false } selected ||
                selected.Get()?.TryCast<NewsItemReference>() is not { } message)
                return;
            var existing = messages.FindIndex(row => row.Pointer == message.Pointer);
            if (existing >= 0)
                messages.RemoveAt(existing);
            else if (messages.Count >= SampleSize)
                messages.RemoveAt(messages.Count - 1);
            messages.Insert(0, message);
            sampledSelectedMessage = true;
        }
        catch (Exception)
        {
            // A rebuilding view can lose its selection temporarily. The ordinary sample is still valid.
        }
    }

    private void Sample(NativeList inbox, int count)
    {
        // Check both ends because the view can reorder the list independently from the binding.
        var sample = Math.Min(count, SampleSize);
        for (var position = 0; position < sample && messages.Count < SampleSize; position++)
        {
            var index = count <= SampleSize || position < SampleSize / 2
                ? position
                : count - (SampleSize - position);
            var row = inbox[index];
            var item = row?.TryCast<TypedValue>() is { IsNull: false } value ? value.Get() : row;
            if (item?.TryCast<InteropReference>() is { } message &&
                (!sampledSelectedMessage || messages[0].Pointer != message.Pointer))
                messages.Add(message);
        }
    }

    private void NextBatch(float now)
    {
        batchCount = Math.Min(BatchSize, messages.Count - offset);
        if (batchCount == 0 || now >= deadline - VerifyWait)
        {
            Open(Phase.Verify, Math.Min(deadline, now + VerifyWait));
            Bind(Manager + ".UniqueId");
            Bind(Manager + ".Club.UniqueId");
            Bind(ManagedTeam + ".UniqueId");
            return;
        }

        Open(Phase.Senders, now + BatchWait);
        eventRows.Clear();
        for (var index = 0; index < batchCount; index++)
        {
            var value = TypedValue.Create(messages[offset + index]);
            var root = "fmPlayerTalkInboxProbe" + index.ToString(CultureInfo.InvariantCulture);
            var path = Span(root);
            scratch.Add(bindings.Set(ref path, value, Bindings.NodeFlags.Temporary, Bindings.SetFlags.Default));
            foreach (var field in SenderFields)
                Bind(root + field);
        }
        for (var index = 0; index < batchCount; index++)
        {
            var root = "fmPlayerTalkInboxProbe" + index.ToString(CultureInfo.InvariantCulture);
            Bind(root + ".NewsPersonSender.Regenerated", optional: true);
            Bind(root + ".NewsPersonSender.IsPersonFemale", optional: true);
            Bind(root + ".NewsPersonSender.NewgenAppearanceDescription", optional: true);
            Bind(root + ".NewsPersonSender.Age", optional: true);
        }
        appearanceDayIndex = fields.Count;
        Bind("game.CurrentGameDay", optional: true);
        if (sampledDate.HasValue)
        {
            for (var index = 0; index < batchCount; index++)
            {
                var absolute = index + offset;
                if (absolute >= 4 && absolute < messages.Count - 4)
                    continue;
                var root = "fmPlayerTalkInboxProbe" + index.ToString(CultureInfo.InvariantCulture);
                var newsStart = fields.Count;
                foreach (var field in RelationshipGameEvents.NewsFields)
                    Bind(root + field, optional: true);
                var fixtureStart = fields.Count;
                foreach (var field in RelationshipGameEvents.FixtureFields)
                    Bind(root + ".Fixture" + field, optional: true);
                eventRows.Add((newsStart, fixtureStart));
            }
        }
    }

    private void ReadGameEvents()
    {
        if (sampledDate is not { } date)
            return;
        foreach (var (newsStart, fixtureStart) in eventRows)
        {
            if (RelationshipGameEvents.FromNews(i => ReadExtra(newsStart + i), primaryTeamId, clubName, date) is { } trophy)
                gameEvents.TryAdd(trophy.Key, trophy);
            if (RelationshipGameEvents.FromFixture(i => ReadExtra(fixtureStart + i), primaryTeamId, date) is { } match)
                gameEvents.TryAdd(match.Key, match);
        }
    }

    private void ReadSenders()
    {
        const int pictureFieldCount = 4;
        var day = ReadExtra(appearanceDayIndex);
        for (var index = 0; index < batchCount; index++)
        {
            var start = index * SenderFields.Length;
            var id = Id(Read(start));
            var name = Text(Read(start + 1));
            var contractClub = Id(Read(start + 3));
            var currentClub = Id(Read(start + 4));
            var currentTeam = Id(Read(start + 5));
            var currentTeamClub = Id(Read(start + 6));
            var senderTeam = Id(Read(start + 7));
            var senderClub = Id(Read(start + 8));
            if (id.HasValue || !string.IsNullOrWhiteSpace(name) ||
                contractClub.HasValue || currentClub.HasValue || currentTeam.HasValue ||
                currentTeamClub.HasValue || senderTeam.HasValue || senderClub.HasValue)
                resolvedRows++;

            var pictureStart = batchCount * SenderFields.Length + index * pictureFieldCount;
            // An old inbox message may still show the former team of a sender who has since left.
            var isAssistant = id.HasValue && id == assistantId;
            if (!isAssistant && ((contractClub.HasValue && contractClub != clubId) ||
                (currentClub.HasValue && currentClub != clubId) ||
                (currentTeamClub.HasValue && currentTeamClub != clubId)))
                continue;
            var currentMember = isAssistant || contractClub == clubId || currentClub == clubId ||
                currentTeamClub == clubId || currentTeam == primaryTeamId;
            var messageMember = senderClub == clubId || senderTeam == primaryTeamId;
            if (id is uint portraitId && portraitId != managerId && currentMember)
            {
                var portrait = PlayerPortraits.ReadInboxPortrait(portraitId, ReadExtra(pictureStart),
                    ReadExtra(pictureStart + 1), ReadExtra(pictureStart + 2), day);
                RecordPortrait(portraits, portrait);
            }
            if (!currentMember && !messageMember)
                continue;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!id.HasValue)
            {
                sameClubWithoutId++;
                continue;
            }
            if (!currentMember)
            {
                onlyMessageAffiliation++;
                continue;
            }
            if (id == managerId)
                continue;

            var job = Text(Read(start + 2));
            found.TryAdd(id.Value, new DirectoryPerson(name, FmDisplayText.Initials(name), "俱乐部联络人",
                string.IsNullOrWhiteSpace(job) ? "消息发件人" : job, id, NativeDisplayName: true,
                Age: LiveClubRoster.ReadAge(ReadExtra(pictureStart + 3)),
                IsFemale: LiveClubRoster.ReadFemale(ReadExtra(pictureStart + 1))));
        }
    }

    private static void RecordPortrait(Dictionary<uint, PlayerPortraits.InboxPortrait> pictures,
        PlayerPortraits.InboxPortrait portrait)
    {
        if (!pictures.TryGetValue(portrait.PublicId, out var existing) ||
            (string.IsNullOrEmpty(existing.AssetId) && !string.IsNullOrEmpty(portrait.AssetId)))
            pictures[portrait.PublicId] = portrait;
    }

    private void Open(Phase next, float until)
    {
        ReleaseBindings();
        phase = next;
        phaseDeadline = until;
    }

    private void Bind(string value, bool optional = false)
    {
        var path = Span(value);
        try { fields.Add(bindings.Bind(ref path, bindings, changed)); }
        catch (Exception) when (optional) { fields.Add(null); }
    }

    private void OnChanged(Bindings.Key key, TypedValue value) => notified.Add(key);

    private bool Arrived(Bindings.Key? key) => key is not { } slot || notified.Contains(slot) || bindings.IsDataSet(slot);

    private TypedValue? Read(int index)
    {
        return fields[index] is { } key && Arrived(key) ? bindings.Get(ref key) : null;
    }

    private TypedValue? ReadExtra(int index)
    {
        try { return Read(index); }
        catch (Exception) { return null; }
    }

    private static Il2CppSystem.ReadOnlySpan<char> Span(string path) => new(new Il2CppStructArray<char>(path.ToCharArray()));

    private static string? Text(TypedValue? value) =>
        value is { IsNull: false } ? FmDisplayText.Read(value.AsString()) : null;

    private static uint? Id(TypedValue? value)
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
            "Int32" when boxed.Unbox<int>() is > 0 => (uint)boxed.Unbox<int>(),
            _ => null
        };
    }

    private void Complete(DirectoryPerson[]? contacts, string diagnostic)
    {
        result = contacts;
        Dispose();
        if (diagnostic == lastDiagnostic)
            return;
        lastDiagnostic = diagnostic;
        Plugin.Logger.LogInfo($"FM Player Talk inbox contacts: {diagnostic}.");
    }

    public void Dispose()
    {
        finished = true;
        ReleaseBindings();
        messages.Clear();
        found.Clear();
        portraits.Clear();
    }

    private void ReleaseBindings()
    {
        foreach (var field in fields)
        {
            if (field is not { } key)
                continue;
            try
            {
                bindings.Unbind(ref key, changed);
            }
            catch (Exception)
            {
                // Load can replace the native binding tree before polling again.
            }
        }
        fields.Clear();
        notified.Clear();
        foreach (var slot in scratch)
        {
            try
            {
                var key = slot;
                bindings.Remove(ref key);
            }
            catch (Exception)
            {
                // Attempt the remaining removals even after a game transition.
            }
        }
        scratch.Clear();
    }
}
