using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using CSweet.UI.Pages;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class LlmProviderModelSelectionTests
{
    [Fact]
    public async Task CatalogOffersEveryModelWithoutAutomaticallyChoosingTheFirst()
    {
        using var http = new HttpClient(new CatalogHandler((_, _) => Task.FromResult(Catalog("first", "second", "third"))))
            { BaseAddress = new Uri("http://localhost/") };
        var page = CreatePage(http);

        await InvokeAsync(page, "PreviewModelCatalogAsync");

        Assert.Equal(["first", "second", "third"], Models(page).Select(model => model.Id));
        Assert.Equal(string.Empty, Field<string>(page, "_defaultChatModel"));

        SetField(page, "_defaultChatModel", "second");
        await InvokeAsync(page, "PreviewModelCatalogAsync");
        Assert.Equal("second", Field<string>(page, "_defaultChatModel"));
    }

    [Fact]
    public async Task ConnectionChangeDiscardsModelsAndIgnoresAnOlderCatalogResponse()
    {
        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new CatalogHandler((_, _) =>
            ++calls == 1 ? firstResponse.Task : Task.FromResult(Catalog("current-model"))))
            { BaseAddress = new Uri("http://localhost/") };
        var page = CreatePage(http);

        var oldRequest = InvokeAsync(page, "PreviewModelCatalogAsync");
        Invoke(page, "OnBaseUrlChanged", "https://api.meta.ai/v1");
        Assert.Empty(Models(page));
        await InvokeAsync(page, "PreviewModelCatalogAsync");
        SetField(page, "_defaultChatModel", "current-model");

        firstResponse.SetResult(Catalog("outdated-model"));
        await oldRequest;

        Assert.Equal("current-model", Assert.Single(Models(page)).Id);
        Assert.Equal("current-model", Field<string>(page, "_defaultChatModel"));
        Invoke(page, "OnApiKeyChanged", "replacement-key");
        Assert.Empty(Models(page));
        Assert.Equal(string.Empty, Field<string>(page, "_defaultChatModel"));
    }

    [Fact]
    public async Task SavedProviderLoadsModelsUsingItsStoredCredentialsAndClearsUnavailableSelection()
    {
        var profile = new LlmProviderProfileResponse(Guid.NewGuid(), "Meta Muse", LlmProviderType.MetaMuse,
            "https://api.meta.ai/v1", "stored-secret", "removed-model", null, null, null,
            true, false, false, false, true, null, default, default);
        using var http = new HttpClient(new CatalogHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/llm-provider-profiles/{profile.Id}/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Catalog("available-model"));
        })) { BaseAddress = new Uri("http://localhost/") };
        var page = CreatePage(http);
        Invoke(page, "OpenDialog", profile);

        await InvokeAsync(page, "EnsureModelCatalogAsync");

        Assert.Equal("available-model", Assert.Single(Models(page)).Id);
        Assert.Equal(string.Empty, Field<string>(page, "_defaultChatModel"));
    }

    private static LlmProviders CreatePage(HttpClient http)
    {
        var page = TestLocalization.Inject(new LlmProviders());
        typeof(LlmProviders).GetProperty("LlmProviderApi", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new LlmProviderApiClient(http));
        Invoke(page, "StartNewProvider");
        return page;
    }

    private static IReadOnlyList<ModelDescriptor> Models(LlmProviders page) =>
        Field<IReadOnlyList<ModelDescriptor>>(page, "_availableChatModels");

    private static T Field<T>(LlmProviders page, string name) =>
        (T)typeof(LlmProviders).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static void SetField(LlmProviders page, string name, object value) =>
        typeof(LlmProviders).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? Invoke(LlmProviders page, string method, params object[] args) =>
        typeof(LlmProviders).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);

    private static Task InvokeAsync(LlmProviders page, string method) => (Task)Invoke(page, method)!;

    private static HttpResponseMessage Catalog(params string[] models) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new PreviewModelCatalogResponse(true, null, null,
            models.Select(model => new ModelDescriptor(model, null)).ToArray()))
    };

    private sealed class CatalogHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
