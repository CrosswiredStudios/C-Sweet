using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Infrastructure.Agents;

/// <summary>
/// Windowed event counter and <c>count_within</c> threshold evaluator.
///
/// <para>
/// <b>Storage decision: the canonical audit ledger is the backing store.</b> No policy table was
/// added. <c>AuditEvents</c> already carries everything a windowed count needs — a normalised
/// <c>OccurredAt</c>, <c>OrganizationId</c>, <c>EventType</c> and <c>ActorInstallationId</c> — and
/// it is indexed on <c>(OrganizationId, OccurredAt, Sequence)</c>, so "this installation's
/// <c>agent.policy.&lt;kind&gt;</c> rows since cutoff" is one bounded range scan plus a scalar COUNT.
/// Reusing it also means the counter survives restarts, cannot drift from the evidence trail, and
/// is already tamper-evident and retention-managed.
/// </para>
/// <para>
/// Appends go through <see cref="IAuditEventWriter.AppendAsync"/> rather than a raw insert. A raw
/// insert would bypass the ledger's hash chain and the per-stream serialisation, which is exactly
/// what makes the counts defensible. The price is one sealed append (a few round trips, serialised
/// per business by an advisory lock) per recorded event, which is why policy events are reserved
/// for destructive/outward executions — a handful per hour — and never for per-tick bookkeeping.
/// </para>
/// <para>
/// The ledger cannot answer one thing: "how many <i>destructive</i> events" if the risk class were
/// the only key. It is not — rules are keyed by <see cref="AgentPolicyThreshold.Kind"/>, and the
/// risk class is stamped into <c>MetadataJson</c> for inspection only, so the hot counting path
/// never has to parse JSON.
/// </para>
/// </summary>
public sealed class AgentPolicyService : IAgentPolicyService
{
    private const int MaximumBrowseLimit = 200;

    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditEventWriter _audit;
    private readonly IAuditExecutionContextAccessor _executionContext;
    private readonly TimeProvider _clock;

    public AgentPolicyService(
        IServiceScopeFactory scopeFactory,
        IAuditEventWriter audit,
        IAuditExecutionContextAccessor executionContext,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _audit = audit;
        _executionContext = executionContext;
        _clock = clock;
    }

    public async Task RecordAsync(AgentPolicyEvent policyEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyEvent);
        var kind = AgentPolicyRules.NormalizeKind(policyEvent.Kind);
        if (kind.Length == 0)
            throw new ArgumentException("A policy event kind is required.", nameof(policyEvent));

        var organizationId = await ResolveOrganizationAsync(
            policyEvent.BusinessId, policyEvent.InstallationId, cancellationToken);

        // An unattributable event is still evidence. It lands on the platform stream rather than
        // being dropped, so it can be inspected, but it never enters a tenant's window (the same
        // fail-closed posture CountWithinAsync and EvaluateAsync take below).
        await _audit.AppendAsync(new AuditEventWriteRequest(
            EventType: AgentPolicyRules.ToEventType(kind),
            Category: AgentPolicyRules.Category,
            Direction: "Internal",
            Outcome: "Completed",
            OrganizationId: organizationId,
            EntityType: AgentPolicyRules.EntityType,
            EntityId: policyEvent.InstallationId,
            Summary: Summary(kind, policyEvent.Risk),
            MetadataJson: Metadata(kind, policyEvent.Risk),
            OccurredAt: ResolveOccurredAt(policyEvent.OccurredAt),
            Actor: PolicyActor(policyEvent.InstallationId),
            UseAmbientOrganization: false), cancellationToken);
    }

    public async Task<int> CountWithinAsync(
        AgentPolicyWindow window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        var kind = AgentPolicyRules.NormalizeKind(window.Kind);
        if (kind.Length == 0) return 0;
        var organizationId = await ResolveOrganizationAsync(window.BusinessId, window.InstallationId, cancellationToken);
        if (organizationId is null) return 0;
        return await CountAsync(organizationId.Value, kind,
            AgentPolicyRules.ClampWindow(window.Window), window.InstallationId, cancellationToken);
    }

    public async Task<AgentPolicyDecision> EvaluateAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await CheckAsync(threshold, candidate, cancellationToken);
        return evaluation.Allowed
            ? AgentPolicyDecision.Permit
            : new AgentPolicyDecision(false, evaluation.Rule, evaluation.Reason);
    }

    public async Task<AgentPolicyEvaluation> CheckAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threshold);
        ArgumentNullException.ThrowIfNull(candidate);

        var window = AgentPolicyRules.ClampWindow(threshold.Window);
        var maximum = Math.Max(threshold.Maximum, 0);
        var kind = AgentPolicyRules.NormalizeKind(threshold.Kind);
        if (kind.Length == 0)
            return AgentPolicyEvaluation.From(new AgentPolicyDecision(false, AgentPolicyRules.CountWithin,
                "The count_within rule names no event kind, so there is no window to count."), threshold, 0, window);

        var organizationId = await ResolveOrganizationAsync(candidate.BusinessId, candidate.InstallationId, cancellationToken);
        if (organizationId is null)
            return AgentPolicyEvaluation.From(new AgentPolicyDecision(false, AgentPolicyRules.CountWithin,
                "The event cannot be attributed to a single business, so its windowed count cannot be trusted."),
                threshold, 0, window);

        var count = await CountAsync(organizationId.Value, kind, window, candidate.InstallationId, cancellationToken);
        if (count < maximum) return AgentPolicyEvaluation.From(AgentPolicyDecision.Permit, threshold, count, window);

        var reason =
            $"{count} '{kind}' ({Describe(candidate.Risk)}) event(s) recorded for this " +
            $"{(candidate.InstallationId is null ? "business" : "installation")} in the last " +
            $"{AgentPolicyRules.WindowMinutesOf(window)} minute(s); the rule permits {maximum}.";
        return AgentPolicyEvaluation.From(
            new AgentPolicyDecision(false, AgentPolicyRules.CountWithin, reason), threshold, count, window);
    }

    public async Task<AgentPolicyDecision> TryAdmitAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threshold);
        ArgumentNullException.ThrowIfNull(candidate);

        var organization = await ResolveOrganizationAsync(candidate.BusinessId, candidate.InstallationId, cancellationToken);
        if (organization is null) return new(false, AgentPolicyRules.CountWithin, "The execution scope cannot be resolved.");
        if (!string.Equals(AgentPolicyRules.NormalizeKind(threshold.Kind), AgentPolicyRules.NormalizeKind(candidate.Kind), StringComparison.Ordinal))
            return new(false, AgentPolicyRules.CountWithin, "The admission event kind does not match its policy.");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        await using var admission = await GovernanceTransaction.OpenAsync(db, "policy:" + organization.Value.ToString("D"), cancellationToken);
        var current = candidate with { BusinessId = organization.Value.ToString("D"), OccurredAt = _clock.GetUtcNow() };
        var decision = await EvaluateAsync(threshold, current, cancellationToken);
        if (!decision.Allowed) return decision;
        await RecordAsync(current, cancellationToken);
        await admission.CommitAsync(cancellationToken);
        return decision;
    }

    public async Task<AgentPolicyEventPage> BrowseAsync(
        AgentPolicyEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var window = AgentPolicyRules.ClampWindow(
            query.Window == default ? AgentPolicyRules.DefaultWindow : query.Window);
        var limit = Math.Clamp(query.Limit, 1, MaximumBrowseLimit);
        var organizationId = await ResolveOrganizationAsync(query.BusinessId, query.InstallationId, cancellationToken);
        if (organizationId is null)
            return new AgentPolicyEventPage(query.BusinessId, query.InstallationId, 0,
                AgentPolicyRules.WindowMinutesOf(window), []);

        var kind = AgentPolicyRules.NormalizeKind(query.Kind);
        var cutoff = _clock.GetUtcNow() - window;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();

        var events = db.AuditEvents.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.OccurredAt >= cutoff);
        if (kind.Length > 0)
        {
            var eventType = AgentPolicyRules.ToEventType(kind);
            events = events.Where(x => x.EventType == eventType);
        }
        else
        {
            var prefix = AgentPolicyRules.EventTypePrefix;
            events = events.Where(x => x.EventType.StartsWith(prefix));
        }
        if (query.InstallationId is { } installation)
            events = events.Where(x => x.ActorInstallationId == installation);

        var count = await events.CountAsync(cancellationToken);
        var rows = await events
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Sequence)
            .Take(limit)
            .Select(x => new { x.EventType, x.ActorInstallationId, x.OccurredAt, x.MetadataJson })
            .ToListAsync(cancellationToken);

        var business = organizationId.Value.ToString("D");
        var items = new List<AgentPolicyEvent>(rows.Count);
        foreach (var row in rows)
            items.Add(new AgentPolicyEvent(
                AgentPolicyRules.TryGetKind(row.EventType, out var foundKind) ? foundKind : row.EventType,
                row.ActorInstallationId,
                business,
                ParseRisk(row.MetadataJson),
                row.OccurredAt));

        return new AgentPolicyEventPage(business, query.InstallationId, count,
            AgentPolicyRules.WindowMinutesOf(window), items);
    }

    /// <summary>
    /// Scalar COUNT over the ledger window. One query, no materialised rows and no JSON parsing,
    /// so a threshold check stays cheap even when it runs per capability call.
    /// </summary>
    private async Task<int> CountAsync(
        Guid organizationId,
        string normalizedKind,
        TimeSpan window,
        Guid? installationId,
        CancellationToken cancellationToken)
    {
        var cutoff = _clock.GetUtcNow() - window;
        var eventType = AgentPolicyRules.ToEventType(normalizedKind);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var events = db.AuditEvents.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EventType == eventType && x.OccurredAt >= cutoff);
        if (installationId is { } installation)
            events = events.Where(x => x.ActorInstallationId == installation);
        return await events.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the tenant for a scope. An explicit business id is authoritative; an installation
    /// id is the fallback (looked up once, in its own scope) and the ambient audit execution
    /// context is the last resort. Anything unresolvable returns null, which every caller treats
    /// as "this scope has no trustworthy tenant" — never as "every tenant matches".
    /// </summary>
    private async Task<Guid?> ResolveOrganizationAsync(
        string? businessId,
        Guid? installationId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(businessId))
        {
            if (!Guid.TryParse(businessId, out var parsedBusiness)) return null;
            if (installationId is { } scopedInstallation)
            {
                using var checkScope = _scopeFactory.CreateScope();
                var checkDb = checkScope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                var actual = await checkDb.AgentInstallations.AsNoTracking().Where(x => x.Id == scopedInstallation)
                    .Select(x => x.BusinessId).SingleOrDefaultAsync(cancellationToken);
                if (!Guid.TryParse(actual, out var actualBusiness) || actualBusiness != parsedBusiness) return null;
            }
            return parsedBusiness;
        }

        if (installationId is { } installation)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var business = await db.AgentInstallations.AsNoTracking()
                .Where(x => x.Id == installation)
                .Select(x => x.BusinessId)
                .SingleOrDefaultAsync(cancellationToken);
            return Guid.TryParse(business, out var installationBusiness) ? installationBusiness : null;
        }

        return _executionContext.Current?.OrganizationId;
    }

    private DateTimeOffset ResolveOccurredAt(DateTimeOffset occurredAt)
    {
        var now = _clock.GetUtcNow();
        if (occurredAt == default) return now;
        return occurredAt > now + MaximumFutureSkew ? now : occurredAt;
    }

    private static AuditActor? PolicyActor(Guid? installationId) =>
        installationId is { } installation
            ? new AuditActor("Agent", true, DisplayName: "Agent installation", InstallationId: installation)
            : null;

    private static string Summary(string kind, CapabilityRiskClass risk) =>
        $"'{kind}' ({Describe(risk)}) recorded for temporal policy.";

    private static string Describe(CapabilityRiskClass risk) => risk.ToString().ToLowerInvariant();

    /// <summary>
    /// Small, JSON-safe envelope. The kind is escaped by the serializer rather than interpolated,
    /// so a hostile kind cannot inject ledger metadata. The counting path never reads this back.
    /// </summary>
    private static string Metadata(string kind, CapabilityRiskClass risk) =>
        "{\"kind\":" + JsonSerializer.Serialize(kind) + ",\"risk\":\"" + risk + "\"}";

    /// <summary>
    /// Risk is presentation-only, so it is recovered on the inspection path (bounded page size) and
    /// never on the counting path. Unparseable metadata degrades to <see cref="CapabilityRiskClass.Read"/>.
    /// </summary>
    private static CapabilityRiskClass ParseRisk(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return CapabilityRiskClass.Read;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("risk", out var risk) ||
                risk.ValueKind != JsonValueKind.String) return CapabilityRiskClass.Read;
            return Enum.TryParse<CapabilityRiskClass>(risk.GetString(), ignoreCase: true, out var parsed) &&
                Enum.IsDefined(parsed) ? parsed : CapabilityRiskClass.Read;
        }
        catch (JsonException)
        {
            return CapabilityRiskClass.Read;
        }
    }
}
