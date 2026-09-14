using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;

namespace CSweet.UnitTests;

public sealed class PersonalTodoActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Renewed_lease_does_not_claim_progress_when_the_last_report_is_old()
    {
        var result = PersonalTodoActivityReader.Describe("Running", true, null, AgentWorkStatus.Leased,
            Now.AddMinutes(3), Now.AddMinutes(-10), Now);
        Assert.Equal("Progress unconfirmed", result.State);
        Assert.Contains("heartbeat alone does not prove progress", result.Message);
    }

    [Fact]
    public void Expired_lease_reports_recovery_even_with_a_recent_progress_report()
    {
        var result = PersonalTodoActivityReader.Describe("Running", true, null, AgentWorkStatus.Leased,
            Now.AddSeconds(-1), Now.AddSeconds(-2), Now);
        Assert.Equal("Recovering", result.State);
    }

    [Fact]
    public void Scheduled_wait_is_not_misrepresented_as_a_stalled_worker()
    {
        var result = PersonalTodoActivityReader.Describe("Ready", true, "Waiting for compute", AgentWorkStatus.Completed,
            null, Now.AddHours(-1), Now);
        Assert.Equal("Waiting", result.State);
        Assert.Equal("Waiting for compute", result.Message);
    }

    [Fact]
    public void Exhausted_delivery_reports_attention_instead_of_waiting_forever()
    {
        Assert.Equal("Needs attention", PersonalTodoActivityReader.Describe("Ready", true, null,
            AgentWorkStatus.DeadLetter, null, null, Now).State);
    }
}
