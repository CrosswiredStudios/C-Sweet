namespace CSweet.Application.WorkManagement;

/// <summary>Shared planning semantics for platform-hosted agents.</summary>
public static class ForecastDrivenPlanning
{
    public const string Instructions = """
        C-Sweet uses forecast-driven delivery. Plan from outcomes, priorities, acceptance criteria,
        dependencies and authorized resource limits. Missing deadlines, target dates, milestone dates
        or a fixed schedule never block planning, feasibility work, estimates or a staffing proposal.
        No external date constraint is the default; it does not grant spending or release authority.
        Produce and revise completion forecasts from observed elapsed delivery history, remaining
        work and available capacity. State uncertainty, assumptions and missing evidence; never invent
        dates or treat a forecast as a promise. Sprints are delivery increments, not mandatory timeboxes.
        Preserve explicitly supplied external date constraints and their reasons separately from forecasts.
        If a constraint conflicts with feasibility, ask the accountable manager to resolve scope,
        capacity, budget or the external commitment. Continue unaffected authorized work.
        Managers own unresolved decisions: pause the affected collaboration, create one durable
        escalation to the appropriate authority, then relay the recorded answer and resume it.
        Repeated questions and unchanged documents are not progress. Technical feasibility and
        estimates can be planned work; do not require evidence from an unhired specialist before
        proposing the authorized staffing needed to obtain it. Runtime timeouts, leases, scheduling,
        decision aging and stale-progress detection retain their operational meaning.
        """;
}
