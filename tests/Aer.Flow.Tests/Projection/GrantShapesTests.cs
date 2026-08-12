using Aer.Flow.Concurrency;
using Aer.Flow.Domain;
using Aer.Flow.Mutation;
using Aer.Flow.Projection;
using Aer.Flow.Store;
using Aer.Tests.Shared;
using System.Text.Json;
using Xunit;

namespace Aer.Flow.Tests.Projection;

public class GrantShapesTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _roomLogPath;

    public GrantShapesTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "aer_grant_shapes_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _roomLogPath = Path.Combine(_tempDirectory, "room.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            DirectoryCleanup.DeleteRecursively(_tempDirectory);
        }
    }

    [Fact]
    public void Record_grant_projects_grant_with_bounds()
    {
        var grantId = new GrantId("grant-001");
        var workerId = new WorkerId("worker-alpha");
        var scope = new GrantScope(AnyTemplates: true, Budget: TimeSpan.FromMinutes(15));
        var bounds = new SpendBounds(MaxPerRunMinutes: 20, MaxConcurrentRunsPerRoom: 3);

        var events = new List<RoomEvent>
        {
            new RoomEvent.GrantRecorded(grantId, workerId, GrantLevel.L1Dispatch, scope, bounds, "operator", DateTimeOffset.UtcNow)
        };

        var state = RoomProjector.Project(events);

        Assert.Single(state.ActiveGrants);
        var grant = state.ActiveGrants[grantId];
        Assert.Equal(workerId, grant.WorkerId);
        Assert.Equal(GrantLevel.L1Dispatch, grant.Level);
        Assert.True(grant.Scope.AnyTemplates);
        Assert.Equal(20, grant.SpendBounds.MaxPerRunMinutes);
    }

    [Fact]
    public void Amend_grant_replaces_old_bounds_with_new_bounds()
    {
        var g1 = new GrantId("grant-001");
        var g2 = new GrantId("grant-002");
        var workerId = new WorkerId("worker-alpha");
        var scope1 = new GrantScope(AnyTemplates: true);
        var bounds1 = new SpendBounds(MaxPerRunMinutes: 20);

        var scope2 = new GrantScope(TemplateIds: [new WorkflowTemplateId("tmpl-a")]);
        var bounds2 = new SpendBounds(MaxPerRunMinutes: 10);

        var events = new List<RoomEvent>
        {
            new RoomEvent.GrantRecorded(g1, workerId, GrantLevel.L1Dispatch, scope1, bounds1, "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantAmended(g2, g1, workerId, GrantLevel.L2Tend, scope2, bounds2, "operator", DateTimeOffset.UtcNow)
        };

        var state = RoomProjector.Project(events);

        Assert.Single(state.ActiveGrants);
        Assert.False(state.ActiveGrants.ContainsKey(g1));
        Assert.True(state.ActiveGrants.ContainsKey(g2));

        var amendedGrant = state.ActiveGrants[g2];
        Assert.Equal(GrantLevel.L2Tend, amendedGrant.Level);
        Assert.Equal(10, amendedGrant.SpendBounds.MaxPerRunMinutes);
        Assert.Equal(g1, amendedGrant.BaseGrantId);
    }

    [Fact]
    public void Revoke_grant_removes_grant_from_active_view()
    {
        var g1 = new GrantId("grant-001");
        var workerId = new WorkerId("worker-alpha");

        var events = new List<RoomEvent>
        {
            new RoomEvent.GrantRecorded(g1, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantRevoked(g1, "operator", DateTimeOffset.UtcNow, "Policy change")
        };

        var state = RoomProjector.Project(events);

        Assert.Empty(state.ActiveGrants);
        Assert.Empty(state.UnmatchedEntries);
    }

    [Fact]
    public void Legacy_decision_event_without_decider_still_deserializes_as_human()
    {
        const string jsonLine = """{"owner":"flow","Event":{"eventType":"externalDecisionRecorded","DecisionId":"dec-123","ReferencedExecutionId":"exec-456","DecisionType":"Resume","TargetStepId":null,"SupplementaryExecutionId":null}}""";


        var entry = JsonSerializer.Deserialize<LogEntry>(jsonLine, FlowEventLogJson.Options);
        var flowLogEntry = Assert.IsType<LogEntry.FlowLogEntry>(entry);
        var decisionEvent = Assert.IsType<FlowEvent.ExternalDecisionRecorded>(flowLogEntry.Event);

        Assert.Equal(DeciderKind.Human, decisionEvent.EffectiveDecider.Kind);
        Assert.Null(decisionEvent.EffectiveDecider.WorkerId);
        Assert.Null(decisionEvent.EffectiveDecider.GrantId);

        // Assert decisionId matches
        Assert.Equal("dec-123", decisionEvent.DecisionId.Value);

    }


    [Fact]
    public void Escalation_raised_with_each_subject_arm_round_trips()
    {
        var workerId = new WorkerId("worker-beta");

        var subjDecision = new EscalationSubject.Decision(new DecisionId("dec-789"));
        var subjOrigination = new EscalationSubject.ProposedOrigination(new WorkflowTemplateId("tmpl-orig"), "brief-ref-001");

        var event1 = new RoomEvent.EscalationRaised(workerId, EscalationTrigger.Spend, subjDecision, DateTimeOffset.UtcNow);
        var event2 = new RoomEvent.EscalationRaised(workerId, EscalationTrigger.Direction, subjOrigination, DateTimeOffset.UtcNow);

        var state = RoomProjector.Project([event1, event2]);

        Assert.Equal(2, state.OpenEscalations.Count);
        Assert.IsType<EscalationSubject.Decision>(state.OpenEscalations[0].Subject);
        Assert.IsType<EscalationSubject.ProposedOrigination>(state.OpenEscalations[1].Subject);

        // Test serialization / deserialization round trip
        var entry1 = new LogEntry.RoomLogEntry(event1, DateTime.UtcNow);
        var json1 = JsonSerializer.Serialize<LogEntry>(entry1, FlowEventLogJson.Options);
        var deserialized1 = JsonSerializer.Deserialize<LogEntry>(json1, FlowEventLogJson.Options);
        var roomLog1 = Assert.IsType<LogEntry.RoomLogEntry>(deserialized1);
        var esc1 = Assert.IsType<RoomEvent.EscalationRaised>(roomLog1.Event);
        Assert.Equal(EscalationTrigger.Spend, esc1.Trigger);
        var decSubject = Assert.IsType<EscalationSubject.Decision>(esc1.Subject);
        Assert.Equal("dec-789", decSubject.DecisionId.Value);

        // BOTH arms round-trip through JSON, as the test's name promises -- the origination
        // arm is the one a future orchestrator actually raises, so its wire shape is the claim.
        var entry2 = new LogEntry.RoomLogEntry(event2, DateTime.UtcNow);
        var json2 = JsonSerializer.Serialize<LogEntry>(entry2, FlowEventLogJson.Options);
        var deserialized2 = JsonSerializer.Deserialize<LogEntry>(json2, FlowEventLogJson.Options);
        var roomLog2 = Assert.IsType<LogEntry.RoomLogEntry>(deserialized2);
        var esc2 = Assert.IsType<RoomEvent.EscalationRaised>(roomLog2.Event);
        Assert.Equal(EscalationTrigger.Direction, esc2.Trigger);
        var origSubject = Assert.IsType<EscalationSubject.ProposedOrigination>(esc2.Subject);
        Assert.Equal("tmpl-orig", origSubject.TemplateId.Value);
        Assert.Equal("brief-ref-001", origSubject.BriefRef);
    }

    [Fact]
    public void Revoking_by_the_original_id_after_an_amendment_still_removes_the_grant()
    {
        // The projector's base-id fallback: an operator revoking "the grant I gave" by its
        // original id must kill the amended successor too, not miss it.
        var g1 = new GrantId("grant-001");
        var g2 = new GrantId("grant-002");
        var workerId = new WorkerId("worker-alpha");

        var events = new List<RoomEvent>
        {
            new RoomEvent.GrantRecorded(g1, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantAmended(g2, g1, workerId, GrantLevel.L2Tend, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantRevoked(g1, "operator", DateTimeOffset.UtcNow, "revoked by original id")
        };

        var state = RoomProjector.Project(events);

        Assert.Empty(state.ActiveGrants);
        Assert.Empty(state.UnmatchedEntries);
    }

    [Fact]
    public void A_two_hop_amend_chain_still_revokes_by_the_original_id()
    {
        // The BaseGrantId propagation takes the EXISTING base when set, so every successor in a
        // chain roots at the original grant -- revoking g1 must kill g3, two amendments later.
        var g1 = new GrantId("grant-001");
        var g2 = new GrantId("grant-002");
        var g3 = new GrantId("grant-003");
        var workerId = new WorkerId("worker-alpha");

        var events = new List<RoomEvent>
        {
            new RoomEvent.GrantRecorded(g1, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantAmended(g2, g1, workerId, GrantLevel.L2Tend, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantAmended(g3, g2, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(), "operator", DateTimeOffset.UtcNow),
            new RoomEvent.GrantRevoked(g1, "operator", DateTimeOffset.UtcNow, "revoked at the root")
        };

        var state = RoomProjector.Project(events);

        Assert.Empty(state.ActiveGrants);
        Assert.Empty(state.UnmatchedEntries);
    }

    [Fact]
    public async Task Recording_a_duplicate_grant_id_and_amending_or_revoking_an_unknown_one_are_refused_loudly()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);
        var g1 = new GrantId("grant-001");
        var workerId = new WorkerId("w-1");

        await RoomMutationInterface.RecordGrantAsync(
            _tempDirectory, g1, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(),
            "operator", reader, writer, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() =>
            RoomMutationInterface.RecordGrantAsync(
                _tempDirectory, g1, workerId, GrantLevel.L1Dispatch, new GrantScope(), new SpendBounds(),
                "operator", reader, writer, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() =>
            RoomMutationInterface.AmendGrantAsync(
                _tempDirectory, new GrantId("grant-new"), new GrantId("grant-unknown"), workerId,
                GrantLevel.L2Tend, new GrantScope(), new SpendBounds(),
                "operator", reader, writer, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidRoomMutationException>(() =>
            RoomMutationInterface.RevokeGrantAsync(
                _tempDirectory, new GrantId("grant-unknown"), "operator", "no such grant",
                reader, writer, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mutation_surface_acquires_lock_and_prevents_bypassing()
    {
        var reader = new RoomEventLogReader(_roomLogPath);
        await using var writer = new RoomEventLogWriter(_roomLogPath);

        using (ConcurrencyGuard.AcquireRoomEvents(_tempDirectory))
        {
            await Assert.ThrowsAsync<WorkflowLockedException>(() =>
                RoomMutationInterface.RecordGrantAsync(
                    _tempDirectory,
                    new GrantId("g-locked"),
                    new WorkerId("w-1"),
                    GrantLevel.L1Dispatch,
                    new GrantScope(),
                    new SpendBounds(),
                    "operator",
                    reader,
                    writer,
                    cancellationToken: TestContext.Current.CancellationToken));
        }

        // After releasing the lock, mutation succeeds
        var state = await RoomMutationInterface.RecordGrantAsync(
            _tempDirectory,
            new GrantId("g-locked"),
            new WorkerId("w-1"),
            GrantLevel.L1Dispatch,
            new GrantScope(),
            new SpendBounds(),
            "operator",
            reader,
            writer,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(state.ActiveGrants);
    }
}
