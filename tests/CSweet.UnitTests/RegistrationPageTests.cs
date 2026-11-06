using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Auth;
using CSweet.UI.Layout;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class RegistrationPageTests
{
    [Theory]
    [InlineData("night-glass")]
    [InlineData("csweet-daylight")]
    public async Task QuietFocusUsesSavedAppearanceWithoutProgressNavigation(string theme)
    {
        await using var h = await Harness.Create(theme);
        var html = await h.Html();
        Assert.Contains($"data-appearance=\"{theme}\"", html);
        Assert.Contains("Create your administrator.", html);
        Assert.DoesNotContain("auth-brand-panel", html);
        Assert.DoesNotContain("<ol", html);
        Assert.Contains("autocomplete=\"new-password\"", html);
        await h.Preview("account", theme);
        await h.Call("RegisterAsync");
        await h.Preview("validation", theme);
        h.Fill();
        h.Set("_saving", true);
        await h.Refresh();
        await h.Preview("saving", theme);
        h.Set("_saving", false);
        await h.Call("RegisterAsync");
        await h.Preview("recovery", theme);
        h.Js.FailExport = true;
        await h.Call("CopyCodesAsync");
        await h.Preview("export-error", theme);
    }

    [Fact]
    public async Task InvalidFormDoesNotSubmitAndPasswordMismatchIsInline()
    {
        await using var h = await Harness.Create();
        await h.Call("RegisterAsync");
        Assert.Equal(0, h.Api.Registrations);
        Assert.Contains("Enter your name.", await h.Html());
        h.Fill();
        h.Model("ConfirmPassword", "different");
        await h.Call("RegisterAsync");
        Assert.Equal(0, h.Api.Registrations);
        Assert.Contains("passwords don", WebUtility.HtmlDecode(await h.Html()));
    }

    [Fact]
    public async Task CreatedAccountShowsTenCodesAndRequiresExplicitAcknowledgment()
    {
        await using var h = await Harness.Create();
        h.Fill();
        await h.Call("RegisterAsync");
        var html = await h.Html();
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(html, "<code").Count);
        Assert.DoesNotContain("register-password", html);
        Assert.Contains("Copy", html); Assert.Contains("Download", html); Assert.Contains("Print", html);
        await h.Call("ContinueToSetup");
        Assert.Null(h.Navigation.Destination);
        await h.Call("CopyCodesAsync");
        await h.Call("ContinueToSetup");
        Assert.Null(h.Navigation.Destination);
        h.Set("_codesSaved", true);
        await h.Call("ContinueToSetup");
        Assert.Equal("/setup", h.Navigation.Destination);
        await h.Call("RegisterAsync");
        Assert.Equal(1, h.Api.Registrations);
    }

    [Theory]
    [InlineData("CopyCodesAsync", "copyText")]
    [InlineData("DownloadCodesAsync", "downloadText")]
    [InlineData("PrintCodesAsync", "printText")]
    public async Task ExportFailuresKeepCodesVisibleAndAllowRetry(string method, string jsMethod)
    {
        await using var h = await Harness.Create();
        h.Fill(); await h.Call("RegisterAsync");
        h.Js.FailExport = true;
        await h.Call(method);
        Assert.Contains("registration-error", await h.Html());
        Assert.Contains("DEMO-0000-1234", await h.Html());
        h.Js.FailExport = false;
        await h.Call(method);
        Assert.Equal(jsMethod, h.Js.LastExport);
        Assert.Contains("DEMO-0000-1234", h.Js.LastArguments!.Last()!.ToString());
        Assert.DoesNotContain("registration-feedback registration-error", await h.Html());
    }

    [Fact]
    public async Task NetworkFailureUnlocksFormForRetry()
    {
        await using var h = await Harness.Create();
        h.Fill(); h.Api.Throw = true;
        await h.Call("RegisterAsync");
        Assert.Contains("Check your connection", await h.Html());
        await h.Preview("connection", "aurora");
        h.Api.Throw = false;
        await h.Call("RegisterAsync");
        Assert.Contains("Save your recovery codes.", await h.Html());
    }

    [Fact]
    public async Task ExistingAdministratorShowsSignInInsteadOfRetryingRegistration()
    {
        await using var h = await Harness.Create();
        h.Fill(); h.Api.Result = new(false, "registration_closed", "Closed");
        await h.Call("RegisterAsync");
        Assert.Contains("Already set up.", await h.Html());
        Assert.Contains("href=\"/login\"", await h.Html());
        await h.Call("RegisterAsync");
        Assert.Equal(1, h.Api.Registrations);
        await h.Preview("closed", "aurora");
    }

    [Fact]
    public async Task MissingCodesCanBeRecoveredWithoutCreatingAccountAgain()
    {
        await using var h = await Harness.Create();
        h.Fill(); h.Api.Result = new(true, null, "Created");
        await h.Call("RegisterAsync");
        Assert.Contains("Generate recovery codes", await h.Html());
        await h.Preview("missing-codes", "aurora");
        await h.Call("GenerateCodesAsync");
        Assert.Contains("DEMO-0000-1234", await h.Html());
        Assert.Equal(1, h.Api.Registrations);
        Assert.Equal(1, h.Api.Regenerations);
    }

    [Fact]
    public async Task ServerFieldValidationIsDisplayedBesideTheField()
    {
        await using var h = await Harness.Create();
        h.Fill(); h.Api.Result = new(false, "validation", "Review your password.", ValidationErrors:
            new Dictionary<string, string[]> { ["Password"] = ["Password must contain a symbol."] });
        await h.Call("RegisterAsync");
        Assert.Contains("Password must contain a symbol.", await h.Html());
    }

    private sealed class Api : HttpMessageHandler
    {
        public int Registrations, Regenerations;
        public bool Throw;
        public static AuthActionResponse Success => new(true, null, "Created", RecoveryCodes: Enumerable.Range(0, 10).Select(i => $"DEMO-{i:0000}-1234").ToArray());
        public AuthActionResponse Result = Success;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/register")) Registrations++;
            else if (request.RequestUri.AbsolutePath.EndsWith("/regenerate")) Regenerations++;
            else throw new InvalidOperationException("Unexpected request");
            if (Throw) throw new HttpRequestException("Offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Regenerations > 0 ? Success : Result) });
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Api Api = new();
        public BrowserJs Js = new();
        public TestNavigation Navigation = new();
        private Register _page = default!;
        private ServiceProvider _services = default!;
        private HtmlRenderer _renderer = default!;
        private Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent _root;
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static async Task<Harness> Create(string theme = "aurora")
        {
            var h = new Harness();
            var services = new ServiceCollection().AddLogging();
            services.AddMudServices(); services.AddCSweetLocalization();
            services.AddSingleton<IJSRuntime>(h.Js);
            services.AddSingleton<NavigationManager>(h.Navigation);
            services.AddSingleton(new HttpClient(h.Api) { BaseAddress = new("http://localhost/") });
            services.AddScoped<AuthSessionStore>();
            services.AddScoped<AppearanceState>();
            services.AddScoped<IAuthenticationApiClient, AuthenticationApiClient>();
            h._services = services.BuildServiceProvider();
            await h._services.GetRequiredService<AppearanceState>().SetThemeAsync(theme);
            h._renderer = new(h._services, h._services.GetRequiredService<ILoggerFactory>());
            await h._renderer.Dispatcher.InvokeAsync(async () =>
            {
                RenderFragment body = b => { b.OpenComponent<Register>(0); b.AddComponentReferenceCapture(1, value => h._page = (Register)value); b.CloseComponent(); };
                h._root = await h._renderer.RenderComponentAsync<RegistrationLayout>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Body"] = body }));
            });
            return h;
        }
        public void Fill() { Model("DisplayName", "Avery Morgan"); Model("Email", "avery@example.com"); Model("Password", "TestPass!42"); Model("ConfirmPassword", "TestPass!42"); }
        public void Model(string property, string value) { var model = typeof(Register).GetField("_model", Private)!.GetValue(_page)!; model.GetType().GetProperty(property)!.SetValue(model, value); }
        public void Set(string field, object value) => typeof(Register).GetField(field, Private)!.SetValue(_page, value);
        public Task Refresh() => _renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(_page, null));
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _root.ToHtmlString());
        public Task Call(string method) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(Register).GetMethod(method, Private)!.Invoke(_page, null);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(_page, null);
        });
        public async Task Preview(string state, string theme)
        {
            if (Environment.GetEnvironmentVariable("CSWEET_REGISTRATION_PREVIEWS") is string directory)
                await File.WriteAllTextAsync(Path.Combine(directory, $"registration-implemented-{state}-{theme}.html"), await Html());
        }
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public string? Destination;
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/register");
        protected override void NavigateToCore(string uri, bool forceLoad) => Destination = uri;
    }
    private sealed class BrowserJs : IJSRuntime, IJSObjectReference
    {
        public bool FailExport;
        public string? LastExport;
        public object?[]? LastArguments;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args) => InvokeAsync<T>(id, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken token, object?[]? args)
        {
            if (id == "import") return ValueTask.FromResult((T)(object)this);
            if (id is "copyText" or "downloadText" or "printText")
            {
                LastExport = id; LastArguments = args;
                if (FailExport) throw new JSException("Blocked");
                if (id == "printText") return ValueTask.FromResult((T)(object)true);
            }
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
