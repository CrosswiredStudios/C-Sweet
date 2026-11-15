using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Hiring;
using Microsoft.AspNetCore.Components;

namespace CSweet.UnitTests;

public sealed class StaffingCorrectionButtonTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task CorrectionBindsSourceAndReasonAndRefreshesOnlyOnConfirmedAcceptance(HttpStatusCode status, bool accepted)
    {
        var org = Guid.NewGuid(); var recommendation = new HiringRecommendationResponse(Guid.NewGuid(), null, "Misclassified slot", "Technical planning", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            { CorrectionSourceRevision = 7, CorrectionSourceRequestId = Guid.NewGuid(), SourceResourceChangeRequestId = Guid.NewGuid(), RemainingHeadcount = 1 };
        var handler = new Handler(status); using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var refreshes = 0;
        var button = new StaffingCorrectionButton { OrganizationId = org, Recommendation = recommendation,
            Changed = EventCallback.Factory.Create(new object(), () => refreshes++) };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(StaffingCorrectionButton).GetProperty("Http", flags)!.SetValue(button, http);
        typeof(StaffingCorrectionButton).GetField("_reason", flags)!.SetValue(button, "  Request technical planning capacity instead.  ");
        await (Task)typeof(StaffingCorrectionButton).GetMethod("SubmitAsync", flags)!.Invoke(button, null)!;
        Assert.Equal($"/api/core/organizations/{org:D}/hiring/recommendations/{recommendation.Id:D}/correction", handler.Path);
        Assert.Equal(7, handler.Request!.ExpectedSourceRevision);
        Assert.Equal(recommendation.CorrectionSourceRequestId, handler.Request.ExpectedSourceRequestId);
        Assert.Equal("Request technical planning capacity instead.", handler.Request.Reason);
        Assert.Equal(accepted ? 1 : 0, refreshes);
        Assert.Equal(accepted, (bool)typeof(StaffingCorrectionButton).GetField("_submitted", flags)!.GetValue(button)!);
        if (accepted)
        {
            await (Task)typeof(StaffingCorrectionButton).GetMethod("SubmitAsync", flags)!.Invoke(button, null)!;
            Assert.Equal(1, handler.Calls);
        }
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Path; public RequestStaffingCorrection? Request; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Assert.Equal(HttpMethod.Post, request.Method); Path = request.RequestUri!.AbsolutePath;
            Request = await request.Content!.ReadFromJsonAsync<RequestStaffingCorrection>(token);
            return new(status) { Content = JsonContent.Create(new { message = "Refresh the source before retrying." }) };
        }
    }
}
