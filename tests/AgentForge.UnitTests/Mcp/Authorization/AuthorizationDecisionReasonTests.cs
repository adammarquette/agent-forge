using System.Text.RegularExpressions;
using AgentForge.Mcp.Authorization;
using FluentAssertions;

namespace AgentForge.UnitTests.Mcp.Authorization;

/// <summary>
/// The FR-AUTH-2 decision reason as it appears on a <em>metric label</em>, which is a different
/// contract from the audit line's reason: labels are exported, joined into every series of the
/// counter, and kept for the retention of the metrics store. So the set has to be closed, small,
/// and derived from nothing but the decision itself — never a patient id, a clinician identity, a
/// resource identifier or free text (CONVENTIONS.md §7; REQUIREMENTS.md §7.4).
/// </summary>
/// <remarks>
/// <see cref="PatientAccessRefusal.AuditReason"/> is deliberately <em>not</em> reused here: it is
/// prose meant for a human reading the audit trail. A separate change.
/// </remarks>
public sealed class AuthorizationDecisionReasonTests
{
    /// <summary>Lower-case, hyphen-separated, nothing else — the shape a bounded label may take.</summary>
    private static readonly Regex SlugShaped = new("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    [Fact]
    public void All_Always_IsASmallClosedSetOfSlugs()
    {
        // The cardinality claim, asserted rather than commented: one series per reason per outcome
        // forever, so the set has to be enumerable and tiny. The exact count is pinned so that
        // adding a reason is a deliberate edit here, not something that happens by accident.
        AuthorizationDecisionReason.All.Should().HaveCount(3);
        AuthorizationDecisionReason.All.Should().OnlyHaveUniqueItems();
        AuthorizationDecisionReason.All.Should().AllSatisfy(reason =>
            SlugShaped.IsMatch(reason).Should().BeTrue($"'{reason}' must be an enum-like label value, not free text"));
    }

    [Fact]
    public void All_Always_ExcludesTheProseAuditReason()
    {
        // The free-text audit wording carries spaces and an FR- citation; it is a log line, not a label.
        AuthorizationDecisionReason.All.Should().NotContain(PatientAccessRefusal.AuditReason);
    }

    [Fact]
    public void For_RequesterIsRelatedToThePatient_IsThePermitReason()
    {
        var reason = AuthorizationDecisionReason.For(new PatientRelationshipDecision(IsRelated: true, 1));

        reason.Should().Be(AuthorizationDecisionReason.ClinicalRelationship);
    }

    [Fact]
    public void For_RelationshipWasResolvedAndSaidNo_IsTheNoRelationshipReason()
    {
        // A real "not your patient": the clinic day was read, and the requester is not on it.
        var reason = AuthorizationDecisionReason.For(new PatientRelationshipDecision(IsRelated: false, 7));

        reason.Should().Be(AuthorizationDecisionReason.NoClinicalRelationship);
    }

    [Fact]
    public void For_RelationshipCouldNotBeResolvedAtAll_IsDistinctFromARealRefusal()
    {
        // The two refuse identically to the requester by design, and an operator needs to tell an
        // OpenEMR outage from an entitlement boundary without reading the audit stream.
        var reason = AuthorizationDecisionReason.For(PatientRelationshipDecision.Unresolved);

        reason.Should().Be(AuthorizationDecisionReason.RelationshipUnresolved);
        reason.Should().NotBe(AuthorizationDecisionReason.NoClinicalRelationship);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(false, null)]
    public void For_AnyDecisionShape_StaysInsideTheClosedSet(bool isRelated, int? appointments)
    {
        // The count is an unbounded integer; nothing derived from it may reach the label.
        var reason = AuthorizationDecisionReason.For(new PatientRelationshipDecision(isRelated, appointments));

        AuthorizationDecisionReason.All.Should().Contain(reason);
    }
}
