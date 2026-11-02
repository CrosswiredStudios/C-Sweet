using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.UI.Localization;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class PluginManageConnectionTests
{
    [Fact]
    public async Task RevokedRecoveryOpensReviewedFreshConsentWithoutStartingOAuthOrEnablingAdditionalSets()
    {
        var api = new Api(); var authorization = Guid.NewGuid();
        api.View = api.View with { Accounts = [new(Guid.NewGuid(), "Unverified account", "ConnectionRequired", 1, false, [],
            [new(authorization, "Connected", ["base"], true) { RequiresFreshConsentRecovery = true }], [])] };
        var html = await Render(api, async page => {
            await Invoke(page, "OpenConnect", (Guid?)authorization);
            typeof(PluginManage).GetMethod("ToggleScope", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, ["optional", true]);
            var selected = (HashSet<string>)typeof(PluginManage).GetField("_scopes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            Assert.Equal(["base"], selected);
        });
        Assert.Contains("Reconnect with fresh consent", html); Assert.Contains("Erased credentials will not be restored", html);
        Assert.Contains("I have reviewed the access", html); Assert.Equal(0, api.Starts); Assert.Null(api.Request);
        Assert.Contains("Choose what to enable", html);
        Assert.Matches("<input[^>]*disabled[^>]*>", html);
    }
    [Theory]
    [InlineData("https://connections.c-sweet.com", "Official C-Sweet service address")]
    [InlineData("https://custom.example", "Custom self-hosted service")]
    public async Task ManagedAccountPageShowsTheActualServiceOriginBeforeConsent(string origin, string label)
    {
        var api = new Api(); api.View = api.View with { ConnectionMode = "Managed", ManagedServiceOrigin = origin };
        var html = await Render(api);
        Assert.Contains(origin, html); Assert.Contains(label, html); Assert.Null(api.Request);
    }

    [Fact]
    public async Task InitialPageHasOneConnectActionAndCollapsedAccessDetailsWithVisiblePolicyLinks()
    {
        var api = new Api();
        var html = await Render(api);
        Assert.Contains("Read selected workspaces", html);
        Assert.Contains("https://example.com/privacy", html);
        Assert.Contains("https://example.com/terms", html);
        Assert.Contains("Review the access and policies", html);
        Assert.Contains("What am I allowing?", html);
        Assert.Matches("<details class=\"connection-access\"[^>]*>", html);
        Assert.DoesNotMatch("<details class=\"connection-access\"[^>]* open", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, @">\s*Connect account\s*</span>"));
        Assert.DoesNotContain("Administration</", html);
        Assert.DoesNotContain("I have reviewed the access", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task AdministratorGetsAnActionableSetupLinkInsteadOfADisabledConnectButton()
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, CanAdminister = true, ReadinessMessage = "provider_configuration_required" };
        var html = await Render(api);
        Assert.Contains("Account connections need setup", html);
        Assert.Contains("Set up connections", html);
        Assert.Contains("/settings/plugins#provider-administration", html);
        Assert.DoesNotContain("What am I allowing?", html);
        Assert.DoesNotContain(">Connect account</span>", html);
        Assert.Matches("<details id=\"plugin-settings\" class=\"plugin-settings\"[^>]*>", html);
        Assert.DoesNotMatch("<details id=\"plugin-settings\"[^>]* open", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task AccountOwnerSeesSetupExplanationWithoutAdministratorControls()
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, ReadinessMessage = "provider_configuration_required" };
        var html = await Render(api);
        Assert.Contains("Account connections need setup", html);
        Assert.Contains("deployment administrator", html);
        Assert.DoesNotContain("Set up connections", html);
        Assert.DoesNotContain("Plugin settings", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task BrandIconComesFromTheGenericCatalogueIdentity()
    {
        var api = new Api(); api.View = api.View with { PluginId = "com.csweet.connector.youtube" };
        var html = await Render(api);
        Assert.Contains("_content/CSweet.UI/images/brands/youtube.svg", html);
    }

    [Fact]
    public async Task ReviewedConnectStartsOnlyRequiredAccessAndNavigatesToProvider()
    {
        var api = new Api(); var navigation = new Navigation();
        await Render(api, async page => { await Invoke(page, "OpenConnect", (object?)null); await AcceptAndConnect(page); }, navigation);
        Assert.Equal(new[] { "base" }, api.Request!.ScopeSetIds);
        Assert.Equal("policy-v2", api.Request.PolicyVersion);
        Assert.True(api.Request.AcceptPolicies);
        Assert.Equal(api.View.PackageDigest, api.Request.PackageDigest);
        Assert.Null(api.Request.ReconnectConnectionId);
        Assert.Equal("https://identity.example.com/authorize?state=opaque", navigation.Target);
        Assert.True(navigation.ForceLoad);
    }

    [Fact]
    public async Task ReconnectShowsReviewAndPreservesExistingPermissionsWithoutStartingConsent()
    {
        var authorization = Guid.NewGuid(); var api = new Api();
        api.View = api.View with { Accounts = [new(Guid.NewGuid(), "Example account", "Connected", 1, true, [],
            [new(authorization, "Connected", ["base", "optional"], true)], [])] };
        var html = await Render(api, page => Invoke(page, "OpenConnect", authorization));
        Assert.Contains("Choose what to enable", html);
        Assert.Contains("Optional changes", html);
        Assert.Contains("I have reviewed the access", html);
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task NewAccountNeverInheritsAnExistingAccountsOptionalPermissionSelection()
    {
        var authorization = Guid.NewGuid(); var api = new Api();
        api.View = api.View with { Accounts = [new(Guid.NewGuid(), "Existing account", "Connected", 1, true, [],
            [new(authorization, "Connected", ["base", "optional"], true)], [])] };
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", authorization);
            await Invoke(page, "OpenConnect", (object?)null);
            await AcceptAndConnect(page);
        });
        Assert.Equal(new[] { "base" }, api.Request!.ScopeSetIds);
        Assert.Null(api.Request.ReconnectConnectionId);
    }

    [Theory]
    [InlineData("provider_configuration_required")]
    [InlineData("plugin_paused")]
    public async Task UnavailableConnectionCannotStartConsent(string reason)
    {
        var api = new Api(); api.View = api.View with { CanConnect = false, ReadinessMessage = reason };
        await Render(api, page => Invoke(page, "OpenConnect", (object?)null));
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task ReconnectCannotTargetAnUnassignedAuthorization()
    {
        var api = new Api();
        await Render(api, page => Invoke(page, "OpenConnect", Guid.NewGuid()));
        Assert.Null(api.Request);
    }

    [Fact]
    public async Task UncertainConsentStartRequiresStatusRefreshAndFreshReviewBeforeRetry()
    {
        var api = new Api { Failure = "CANARY access_token=private" };
        var html = await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null); await AcceptAndConnect(page);
            await Invoke(page, "Connect"); Assert.Equal(1, api.Starts);
            api.Failure = null;
            await Invoke(page, "Reload"); await Invoke(page, "Connect"); Assert.Equal(1, api.Starts);
            await AcceptAndConnect(page);
        });
        Assert.Equal(2, api.Starts); Assert.DoesNotContain("CANARY", html);
    }

    [Fact]
    public async Task NonHttpsConsentResponseCannotNavigate()
    {
        var api = new Api { AuthorizationUrl = "http://identity.example.com/authorize" }; var navigation = new Navigation();
        var html = await Render(api, async page => { await Invoke(page, "OpenConnect", (object?)null); await AcceptAndConnect(page); }, navigation);
        Assert.Null(navigation.Target);
        Assert.Contains("The secure sign-in address is unavailable.", html);
    }

    [Fact]
    public async Task ResumeAcknowledgmentIsShownWhileReadStatusIsStaleAndDoubleClicksSubmitOnce()
    {
        var api = new Api(); var setup = FailedSetup(); api.View = api.View with { SetupSessions = [setup] };
        var receipt = new PluginImportResumeReceipt(setup.Id, 8, Guid.NewGuid());
        var pending = new TaskCompletionSource<PluginImportResumeReceipt?>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Resume = (_, _) => pending.Task;
        var html = await Render(api, async page =>
        {
            var first = Invoke(page, "Retry", setup.Id); await Task.Yield();
            await Invoke(page, "Retry", setup.Id); Assert.Equal(1, api.ResumeCalls);
            pending.SetResult(receipt); await first;
            await Invoke(page, "Retry", setup.Id); Assert.Equal(1, api.ResumeCalls);
        });
        Assert.Equal(7, api.ExpectedRevision); Assert.Contains("Resume accepted", html);
        Assert.Contains("Waiting for the saved import status", html); Assert.Contains(receipt.CorrelationId.ToString("N"), html);
        Assert.DoesNotContain("Your progress is saved. Resume the import", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainResumeDoesNotRetryOrRenderTransportSecrets(bool timedOut)
    {
        var api = new Api(); var setup = FailedSetup(); api.View = api.View with { SetupSessions = [setup] };
        api.Resume = (_, _) => timedOut ? Task.FromCanceled<PluginImportResumeReceipt?>(new CancellationToken(true)) :
            Task.FromException<PluginImportResumeReceipt?>(new HttpRequestException("CANARY proof=private"));
        var html = await Render(api, async page =>
        {
            await Invoke(page, "Retry", setup.Id);
            Assert.False((bool)typeof(PluginManage).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
            await Invoke(page, "Retry", setup.Id);
        });
        Assert.Equal(1, api.ResumeCalls); Assert.Contains("acknowledgment unavailable", html);
        Assert.Contains("No automatic retry", html); Assert.DoesNotContain("CANARY", html);
        Assert.DoesNotContain("Resume accepted", html);
    }

    [Fact]
    public async Task AcceptedResumeSurvivesReadFailureWithoutExposingReadException()
    {
        var api = new Api(); var setup = FailedSetup(); api.View = api.View with { SetupSessions = [setup] };
        api.Resume = (_, _) => { api.ReadFailure = new InvalidOperationException("CANARY token=private"); return Task.FromResult<PluginImportResumeReceipt?>(new(setup.Id, 8, Guid.NewGuid())); };
        var html = await Render(api, page => Invoke(page, "Retry", setup.Id));
        Assert.Equal(1, api.ResumeCalls); Assert.Contains("Resume accepted", html); Assert.DoesNotContain("CANARY", html);
        Assert.Contains("Account status is unavailable", html);
    }

    [Fact]
    public async Task ReloadedPageUsesDurableQueuedProgressAndNeverSubmitsOnRender()
    {
        var api = new Api(); var setup = FailedSetup(); var correlation = Guid.NewGuid();
        api.View = api.View with { SetupSessions = [setup with { Status = "Importing", Revision = 8,
            Import = new(correlation, "Queued", "Queued", 0, DateTimeOffset.UtcNow) }] };
        var first = await Render(api); var second = await Render(api);
        Assert.Equal(0, api.ResumeCalls); Assert.Contains(correlation.ToString("N"), first); Assert.Contains("Import queued", second);
        Assert.DoesNotContain("Resume accepted at revision", first);
    }

    [Fact]
    public async Task UnsafeCrossBoundaryDiagnosticLabelsAreNotRendered()
    {
        var api = new Api(); var setup = FailedSetup();
        api.View = api.View with { SetupSessions = [setup with { Import = setup.Import! with { ExceptionType = "CANARY.UntrustedType", Stage = "CANARY body" } }] };
        var html = await Render(api); Assert.DoesNotContain("CANARY", html); Assert.DoesNotContain("Import diagnostic details", html);
    }

    [Fact]
    public async Task CompletedImportShowsSuccessRatherThanGenericConnectionFailure()
    {
        var api = new Api(); var setup = FailedSetup();
        api.View = api.View with { SetupSessions = [setup with { Status = "Ready", Import = setup.Import! with
            { State = "Completed", Stage = "Complete", FailureKind = null, ExceptionType = null, HttpStatus = null, HttpStatusSource = null } }] };
        var html = await Render(api); Assert.Contains("Account connected", html); Assert.Contains("Import completed", html);
        Assert.DoesNotContain("The provider connection did not complete", html);
    }

    [Theory]
    [InlineData("Ready", "Queued")]
    [InlineData("Cancelled", "Running")]
    public async Task HistoricalCheckpointNeverOverridesSavedTerminalSetupState(string setupState, string diagnosticState)
    {
        var api = new Api(); var setup = FailedSetup();
        api.View = api.View with { SetupSessions = [setup with { Status = setupState, Import = setup.Import! with { State = diagnosticState } }] };
        var html = await Render(api); Assert.Contains("Last recorded import checkpoint", html);
        Assert.Contains("current setup state above takes precedence", html);
    }

    [Fact]
    public async Task FirstClickOnlyOpensReviewAndBackDiscardsUnsubmittedConsent()
    {
        var api = new Api();
        var html = await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null);
            await Invoke(page, "Connect"); Assert.Equal(0, api.Starts);
            typeof(PluginManage).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
            typeof(PluginManage).GetMethod("CancelConnect", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            await Invoke(page, "OpenConnect", (object?)null);
            await Invoke(page, "Connect");
        });
        Assert.Equal(0, api.Starts); Assert.Contains("I have reviewed the access", html);
        Assert.DoesNotContain("Connect your first account", html);
    }

    [Fact]
    public async Task RepeatedConsentClicksSubmitOnceEvenBeforeNavigationCompletes()
    {
        var api = new Api(); var pending = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Begin = () => pending.Task;
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null);
            var first = AcceptAndConnect(page); await Task.Yield();
            await Invoke(page, "Connect"); Assert.Equal(1, api.Starts);
            pending.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await first; await Invoke(page, "Connect");
        });
        Assert.Equal(1, api.Starts);
    }

    [Fact]
    public async Task NewPluginNavigationDiscardsAnOldConsentResponseAndLocalSelection()
    {
        var api = new Api(); var navigation = new Navigation();
        var pending = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Begin = () => pending.Task;
        var html = await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null); var first = AcceptAndConnect(page);
            api.View = api.View with { InstallationId = Guid.NewGuid(), Name = "Other plugin" };
            typeof(PluginManage).GetProperty(nameof(PluginManage.InstallationId))!.SetValue(page, api.View.InstallationId);
            await Invoke(page, "OnParametersSetAsync");
            pending.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await first;
        }, navigation);
        Assert.Null(navigation.Target); Assert.Contains("Other plugin", html);
        Assert.DoesNotContain("I have reviewed the access", html); Assert.Equal(1, api.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameInstallationSetupQueryChangeDiscardsLateConsentIncludingBackNavigation(bool backToOriginalQuery)
    {
        var api = new Api(); var navigation = new Navigation();
        var pending = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Begin = () => pending.Task;
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null); var first = AcceptAndConnect(page);
            typeof(PluginManage).GetProperty(nameof(PluginManage.SetupId))!.SetValue(page, (Guid?)Guid.NewGuid());
            await Invoke(page, "OnParametersSetAsync");
            Assert.True(api.ConnectCancellation.IsCancellationRequested);
            Assert.False((bool)typeof(PluginManage).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
            if (backToOriginalQuery)
            {
                typeof(PluginManage).GetProperty(nameof(PluginManage.SetupId))!.SetValue(page, null);
                await Invoke(page, "OnParametersSetAsync");
            }
            pending.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await first;
        }, navigation);
        Assert.Null(navigation.Target); Assert.Equal(1, api.Starts); Assert.True(api.ConnectCancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationChangeDiscardsLateConsentBeforeParameterLifecycleRuns(bool backToOriginalLocation)
    {
        var api = new Api(); var navigation = new Navigation();
        var pending = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Begin = () => pending.Task;
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null); var first = AcceptAndConnect(page);
            var originalLocation = navigation.Uri;
            navigation.ChangeLocation($"https://host.example/settings/plugins/{api.View.InstallationId:D}?setup={Guid.NewGuid():D}");
            if (backToOriginalLocation) navigation.ChangeLocation(originalLocation);
            pending.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await first;
            Assert.False((bool)typeof(PluginManage).GetField("_busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
        }, navigation);
        Assert.Null(navigation.Target); Assert.Equal(1, api.Starts);
    }

    [Fact]
    public async Task DisposalDiscardsNonCooperativeLateConsentAndIsIdempotent()
    {
        var api = new Api(); var navigation = new Navigation();
        var pending = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Begin = () => pending.Task;
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null); var first = AcceptAndConnect(page);
            await page.DisposeAsync();
            Assert.True(api.ConnectCancellation.IsCancellationRequested);
            pending.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await first; await page.DisposeAsync();
        }, navigation);
        Assert.Null(navigation.Target); Assert.Equal(1, api.Starts); Assert.True(api.ConnectCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task UnchangedParametersPreserveTheCurrentReview()
    {
        var api = new Api();
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null);
            typeof(PluginManage).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
            await Invoke(page, "OnParametersSetAsync");
            await Invoke(page, "Connect");
        });
        Assert.Equal(1, api.Starts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RouterParameterReadSurvivesEitherLocationSubscriberOrderAndStillDiscardsOldConsent(bool routerFirst, bool differentInstallation)
    {
        var api = new Api(); var navigation = new Navigation(); PluginManage? currentPage = null; Task? routing = null;
        var read = new TaskCompletionSource<PluginAccountManagementView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<BeginPluginAccountResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = Guid.NewGuid();
        var next = api.View with { InstallationId = differentInstallation ? Guid.NewGuid() : api.View.InstallationId, Name = "New route loaded" };
        EventHandler<LocationChangedEventArgs> router = (_, _) =>
        {
            if (currentPage is null) return;
            typeof(PluginManage).GetProperty(nameof(PluginManage.InstallationId))!.SetValue(currentPage, next.InstallationId);
            typeof(PluginManage).GetProperty(nameof(PluginManage.SetupId))!.SetValue(currentPage, (Guid?)setup);
            // Like an earlier Router subscriber, synchronously deliver parameters until
            // the route's API read yields, then allow later location subscribers to run.
            routing = Invoke(currentPage, "OnParametersSetAsync");
        };
        if (routerFirst) navigation.LocationChanged += router;
        var html = await Render(api, async page =>
        {
            currentPage = page;
            if (!routerFirst) navigation.LocationChanged += router;
            api.Begin = () => consent.Task;
            await Invoke(page, "OpenConnect", (object?)null); var oldConsent = AcceptAndConnect(page);
            api.Read = _ => read.Task;
            navigation.ChangeLocation($"https://host.example/settings/plugins/{next.InstallationId:D}?setup={setup:D}");
            Assert.NotNull(routing); Assert.False(routing!.IsCompleted);
            read.SetResult(next); await routing;
            consent.SetResult(new(Guid.NewGuid(), api.AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10)));
            await oldConsent;
            Assert.Same(next, typeof(PluginManage).GetField("_view", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        }, navigation);
        Assert.Contains("New route loaded", html); Assert.Null(navigation.Target); Assert.Equal(1, api.Starts);
    }

    [Fact]
    public async Task OlderStatusReadCannotReplaceANewerRefresh()
    {
        var api = new Api();
        var html = await Render(api, async page =>
        {
            var old = api.View; var pending = new TaskCompletionSource<PluginAccountManagementView>(TaskCreationOptions.RunContinuationsAsynchronously);
            api.Read = _ => pending.Task; var first = Invoke(page, "Reload");
            api.View = api.View with { Name = "Latest package status" }; api.Read = null;
            await Invoke(page, "Reload"); pending.SetResult(old); await first;
        });
        Assert.Contains("Latest package status", html);
    }

    [Fact]
    public async Task PackageChangeInvalidatesThePolicyReview()
    {
        var api = new Api();
        await Render(api, async page =>
        {
            await Invoke(page, "OpenConnect", (object?)null);
            typeof(PluginManage).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
            api.View = api.View with { PackageDigest = new string('b', 64) };
            await Invoke(page, "Reload"); await Invoke(page, "Connect");
        });
        Assert.Equal(0, api.Starts);
    }

    [Fact]
    public async Task NewSuccessKeepsHistoricalFailuresInCollapsedHistory()
    {
        var api = new Api(); var failed = FailedSetup();
        api.View = api.View with { SetupSessions = [failed with { Id = Guid.NewGuid(), Status = "Ready", Import = null }, failed] };
        var html = await Render(api);
        var history = html.IndexOf("<details class=\"setup-history\"", StringComparison.Ordinal);
        Assert.True(history > 0); Assert.Contains("Account connected", html[..history]);
        Assert.DoesNotContain("Import paused", html[..history]); Assert.Contains("Import paused", html[history..]);
        Assert.DoesNotMatch("<details class=\"setup-history\"[^>]* open", html);
        Assert.Equal(0, api.ResumeCalls);
    }

    [Theory]
    [InlineData("AwaitingConsent", "Waiting for secure sign-in")]
    [InlineData("Expired", "Sign-in expired")]
    [InlineData("NoResources", "no accessible resources")]
    [InlineData("Cancelled", "Connection cancelled")]
    public async Task ReentryUsesSavedStatusWithoutConsentOrGrantSideEffects(string status, string title)
    {
        var api = new Api(); api.View = api.View with { SetupSessions = [FailedSetup() with { Status = status, Import = null }] };
        var html = await Render(api);
        Assert.Contains(title, html); Assert.Null(api.Request); Assert.Equal(0, api.ResumeCalls);
        if (status == "AwaitingConsent") Assert.DoesNotContain("Connect your first account", html);
    }

    [Fact]
    public async Task ExpiredSignInWindowOffersANewReviewedConnectionWithoutSubmittingIt()
    {
        var api = new Api(); api.View = api.View with { SetupSessions = [FailedSetup() with { Status = "AwaitingConsent", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), Import = null }] };
        var html = await Render(api);
        Assert.Contains("Sign-in expired", html); Assert.Contains("Connect your first account", html); Assert.Null(api.Request);
    }

    [Fact]
    public async Task UnusableConnectionReturnsToRequirementsAndExpandsItsRecoveryDetails()
    {
        var api = new Api();
        api.View = api.View with { CanAdminister = true, Declaration = api.View.Declaration with { Permissions = [new() { Id = "read" }] },
            Accounts = [new(Guid.NewGuid(), "Recover account", "ConnectionRequired", 7, false, [], [new(Guid.NewGuid(), "Revoked", ["base"], true)], [])] };
        var html = await Render(api, page =>
        {
            Assert.Equal(PluginSetupSectionKind.Requirements, typeof(PluginManage).GetProperty("CurrentSection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
            return Task.CompletedTask;
        });
        Assert.Matches("<details class=\"account-detail\"[^>]* open", html); Assert.Null(api.Request);
    }

    [Fact]
    public async Task MultipleAccountsOpenOnlyOneAccountWithoutGrantingAccess()
    {
        var api = new Api();
        var account = new PluginManagedAccount(Guid.NewGuid(), "First account", "Connected", 2, true, [], [], []);
        api.View = api.View with { Accounts = [account, account with { Id = Guid.NewGuid(), Name = "Second account" }] };
        var html = await Render(api);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<details class=\"account-panel\"").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<details class=\"account-panel\"[^>]* open"));
        Assert.Null(api.Request);
    }

    private static PluginAccountSetupView FailedSetup() => new(Guid.NewGuid(), "ImportFailed", 0, "discovery_failed", DateTimeOffset.UtcNow.AddMinutes(10))
    { Revision = 7, Import = new(Guid.NewGuid(), "Failed", "ManagedDiscovery", 1, StartedAt: DateTimeOffset.UtcNow, FinishedAt: DateTimeOffset.UtcNow,
        FailureKind: "http_error", ExceptionType: "HttpRequestException", HttpStatus: 503, HttpStatusSource: "ManagedService") };

    private static async Task AcceptAndConnect(PluginManage page)
    {
        typeof(PluginManage).GetField("_accepted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, true);
        await Invoke(page, "Connect");
    }

    private static Task Invoke(PluginManage page, string method, params object?[] args) =>
        (Task)typeof(PluginManage).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args)!;

    private static async Task<string> Render(Api api, Func<PluginManage, Task>? interact = null, Navigation? navigation = null)
    {
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IPluginAccountApiClient>(api); services.AddSingleton<IBusinessContext, StubBusinesses>();
        services.AddSingleton<NavigationManager>(navigation ?? new Navigation()); services.AddSingleton<IJSRuntime, NoJavaScript>();
        var activator = new Activator(); services.AddSingleton<IComponentActivator>(activator);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var theme = await renderer.RenderComponentAsync<MudThemeProvider>();
            var output = await renderer.RenderComponentAsync<PluginManage>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["InstallationId"] = api.View.InstallationId }));
            if (interact is not null)
            {
                await interact(activator.Page!);
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(activator.Page, null);
                await Task.Yield();
            }
            return theme.ToHtmlString() + output.ToHtmlString();
        });
    }

    private sealed class Api : IPluginAccountApiClient
    {
        public PluginAccountManagementView View = new(Guid.NewGuid(), "Example connector", "1.0.0", new string('a', 64), "Built", true, null,
            new() { AccountLabel = "Example account", ResourcesLabel = "Workspaces", ConsentPurpose = "Read selected workspaces", PolicyVersion = "policy-v2",
                PrivacyPolicyUrl = "https://example.com/privacy", TermsOfServiceUrl = "https://example.com/terms" },
            [new() { Id = "base", Required = true, Label = "Read", Purpose = "Read workspace information" },
             new() { Id = "optional", Label = "Optional changes", Purpose = "Change workspace content" }], [], [], false);
        public ConnectPluginAccountRequest? Request; public string? Failure; public int Starts;
        public string AuthorizationUrl = "https://identity.example.com/authorize?state=opaque";
        public int ResumeCalls; public long ExpectedRevision; public Exception? ReadFailure;
        public Func<Guid,long,Task<PluginImportResumeReceipt?>>? Resume;
        public Func<Guid, Task<PluginAccountManagementView>>? Read;
        public Func<Task<BeginPluginAccountResponse>>? Begin;
        public CancellationToken ConnectCancellation;
        public Task<PluginAccountManagementView> GetAsync(Guid id, CancellationToken ct = default) => Read?.Invoke(id) ?? (ReadFailure is null ? Task.FromResult(View) : Task.FromException<PluginAccountManagementView>(ReadFailure));
        public Task<PluginImportResumeReceipt?> ResumeImportAsync(Guid id, Guid setup, long expectedRevision, CancellationToken ct = default)
        { ResumeCalls++; ExpectedRevision = expectedRevision; return Resume?.Invoke(setup, expectedRevision) ?? Task.FromResult<PluginImportResumeReceipt?>(null); }
        public Task<BeginPluginAccountResponse> ConnectAsync(Guid id, ConnectPluginAccountRequest request, CancellationToken ct = default)
        { Starts++; Request = request; ConnectCancellation = ct; return Begin?.Invoke() ?? (Failure is null ? Task.FromResult(new BeginPluginAccountResponse(Guid.NewGuid(), AuthorizationUrl, DateTimeOffset.UtcNow.AddMinutes(10))) : Task.FromException<BeginPluginAccountResponse>(new InvalidOperationException(Failure))); }
        public Task RetryAsync(Guid id, Guid setup, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveGrantAsync(Guid id, Guid account, SavePluginAccountGrantRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeGrantAsync(Guid id, Guid grant, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginAccountImpactView> GetDisconnectImpactAsync(Guid id, Guid account, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync(Guid id, Guid account, DisconnectPluginAccountRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveOperatorAsync(Guid id, SavePluginOperatorRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeOperatorAsync(Guid id, Guid op, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginInstallationImpact> GetImpactAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ControlAsync(Guid id, PluginInstallationControlRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RetryBuildAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginPackageReview> GetReviewAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Activator : IComponentActivator
    {
        public PluginManage? Page;
        public IComponent CreateInstance(Type type)
        { var component = (IComponent)System.Activator.CreateInstance(type)!; if (component is PluginManage page) Page = page; return component; }
    }
    private sealed class Navigation : NavigationManager
    {
        public string? Target; public bool ForceLoad;
        public Navigation() => Initialize("https://host.example/", "https://host.example/settings/plugins");
        protected override void NavigateToCore(string uri, bool forceLoad) { Target = uri; ForceLoad = forceLoad; }
        public void ChangeLocation(string uri) { Uri = uri; NotifyLocationChanged(false); }
    }
    private sealed class StubBusinesses : IBusinessContext
    {
        public IReadOnlyList<OrganizationResponse> Businesses => [];
        public OrganizationResponse? SelectedBusiness => null; public bool IsLoading => false; public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectAsync(Guid id, bool navigate = true, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
