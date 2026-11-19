using System;
using System.Collections.Generic;
using System.Linq;

namespace FmPlayerTalk;

internal sealed partial class TalkPanel
{
    private sealed record RelationshipEventScope(string Save, uint Manager, uint Club, uint Team);
    private RelationshipEventScope? relationshipEventScope;
    private RelationshipGameEvents.FixtureRead? relationshipFixture;
    private readonly Dictionary<string, RelationshipGameEvent> relationshipGameEvents = new(StringComparer.Ordinal);

    private void ResetRelationshipGameEvents()
    {
        relationshipFixture?.Dispose();
        relationshipFixture = null;
        relationshipEventScope = null;
        relationshipGameEvents.Clear();
    }

    private bool MaintainRelationshipEventScope(LiveRoster active)
    {
        if (GameSaveTimeline.DirectoryPath is not { } path || active is not
            { ManagerId: > 0, ClubId: > 0, PrimaryTeamId: > 0 })
            return false;
        var scope = new RelationshipEventScope(path, active.ManagerId, active.ClubId, active.PrimaryTeamId);
        if (relationshipEventScope != scope)
        {
            ResetRelationshipGameEvents();
            relationshipEventScope = scope;
        }
        return true;
    }

    private void RememberRelationshipGameEvents(LiveRoster active, IReadOnlyList<RelationshipGameEvent> events)
    {
        if (events.Count == 0 || !MaintainRelationshipEventScope(active))
            return;
        foreach (var entry in events)
            relationshipGameEvents[entry.Key] = entry;
    }

    private IReadOnlyList<RelationshipGameEvent> ReadRelationshipGameEvents(DateTime date)
    {
        if (roster is not { } active || !MaintainRelationshipEventScope(active))
            return Array.Empty<RelationshipGameEvent>();

        if (relationshipFixture == null && LiveClubRoster.FindBindings() is { } bindings)
            relationshipFixture = new RelationshipGameEvents.FixtureRead(bindings);

        if (relationshipFixture?.Read(active.ManagerId, active.ClubId, active.PrimaryTeamId, date) is { } match)
            relationshipGameEvents[match.Key] = match;
        foreach (var expired in relationshipGameEvents.Values.Where(item => !RelationshipEventRules.IsRecent(item.Date, date)).ToArray())
            relationshipGameEvents.Remove(expired.Key);
        return relationshipGameEvents.Values
            .OrderByDescending(item => item.Key.StartsWith("trophy:", StringComparison.Ordinal))
            .ThenByDescending(item => item.Date).ToArray();
    }
}
