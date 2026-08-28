using System.Net;
using System.Text;
using CSweet.Contracts.Core;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CSweet.UnitTests;

public sealed class BusinessDeletionUiStateTests
{
    [Fact]
    public async Task RemoveDeletedBusinessAsync_WhenFocused_SelectsFirstRemainingBusiness()
    {
        var first = Business("Alpha");
        var focused = Business("Beta");
        var js = new LocalStorageJsRuntime(focused.Id.ToString());
        using var context = new BusinessContext(
            new OrganizationClient([first, focused]),
            new TestNavigationManager(),
            js);
        await context.InitializeAsync();

        await context.RemoveDeletedBusinessAsync(focused.Id);

        Assert.Equal(first.Id, context.SelectedBusiness?.Id);
        Assert.Equal(first.Id.ToString(), js.Value);
    }

    [Fact]
    public async Task RemoveDeletedBusinessAsync_WhenNotFocused_PreservesFocus()
    {
        var first = Business("Alpha");
        var focused = Business("Beta");
        var js = new LocalStorageJsRuntime(focused.Id.ToString());
        using var context = new BusinessContext(
            new OrganizationClient([first, focused]),
            new TestNavigationManager(),
            js);
        await context.InitializeAsync();

        await context.RemoveDeletedBusinessAsync(first.Id);

        Assert.Equal(focused.Id, context.SelectedBusiness?.Id);
        Assert.Equal(focused.Id.ToString(), js.Value);
    }

    [Fact]
    public async Task RemoveDeletedBusinessAsync_WhenLastBusiness_ClearsFocusAndStorage()
    {
        var only = Business("Only");
        var js = new LocalStorageJsRuntime(only.Id.ToString());
        using var context = new BusinessContext(
            new OrganizationClient([only]),
            new TestNavigationManager(),
            js);
        await context.InitializeAsync();

        await context.RemoveDeletedBusinessAsync(only.Id);

        Assert.Empty(context.Businesses);
        Assert.Null(context.SelectedBusiness);
        Assert.Null(js.Value);
    }

    [Theory]
    [InlineData("{\"succeeded\":false,\"message\":\"Cleanup is busy.\"}", "Cleanup is busy.")]
    [InlineData("{\"title\":\"Conflict\",\"detail\":\"A workload could not be stopped.\"}", "A workload could not be stopped.")]
    [InlineData("not json", "HTTP 409")]
    [InlineData("", "HTTP 409")]
    public async Task OrganizationApiClient_DeleteFailureAlwaysHasUsefulMessage(string body, string expected)
    {
        var http = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var client = new OrganizationApiClient(http);

        var exception = await Assert.ThrowsAsync<ApiClientException>(() => client.DeleteAsync(Guid.NewGuid()));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OrganizationResponse Business(string name) => new(
        Guid.NewGuid(), name, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class OrganizationClient(IReadOnlyList<OrganizationResponse> businesses) : IOrganizationApiClient
    {
        public Task<IReadOnlyList<OrganizationResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(businesses);
        public Task<OrganizationResponse> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationResponse> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OrganizationResponse> UpdateAsync(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://example.test/", "https://example.test/");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }

    private sealed class LocalStorageJsRuntime(string? value) : IJSRuntime
    {
        public string? Value { get; private set; } = value;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem")
                return ValueTask.FromResult((TValue)(object?)Value!);
            if (identifier == "localStorage.setItem")
                Value = args?[1]?.ToString();
            else if (identifier == "localStorage.removeItem")
                Value = null;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
