using System.Net;
using System.Text;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class BusinessOnboardingApiClientTests
{
    [Fact]
    public async Task CompleteAsync_NonJsonServerError_ReturnsSafeRetryMessage()
    {
        var client = CreateClient(
            HttpStatusCode.InternalServerError,
            "System.InvalidOperationException: database failure");

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.CompleteAsync(new CompleteBusinessOnboardingRequest(
                "Contoso",
                "Technology",
                null,
                Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal(
            "The C-Sweet server could not complete this operation. Your installed agent was preserved and it is safe to retry.",
            exception.Message);
        Assert.DoesNotContain("System.InvalidOperationException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignChiefAsync_ProblemResponse_PreservesApiMessage()
    {
        var client = CreateClient(
            HttpStatusCode.Conflict,
            """{"succeeded":false,"errorCode":"chief_conflict","message":"This chief is already assigned."}""",
            "application/json");

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.AssignChiefAsync(
                Guid.NewGuid(),
                new CompleteChiefSetupRequest(Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("This chief is already assigned.", exception.Message);
    }

    [Fact]
    public async Task CompleteAsync_MalformedSuccessResponse_ReportsInvalidResponse()
    {
        var client = CreateClient(HttpStatusCode.OK, "not json");

        var exception = await Assert.ThrowsAsync<ApiClientException>(() =>
            client.CompleteAsync(new CompleteBusinessOnboardingRequest(
                "Contoso",
                null,
                null,
                Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal("Business onboarding returned an invalid response.", exception.Message);
    }

    private static BusinessOnboardingApiClient CreateClient(
        HttpStatusCode statusCode,
        string body,
        string mediaType = "text/plain")
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        });
        return new BusinessOnboardingApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://csweet.test/")
        });
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
