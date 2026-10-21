using System.Reflection;
using CSweet.Contracts.Realtime;
using CSweet.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class NotificationSnackbarTests
{
    [Fact]
    public void HiringApprovalNotificationsStayQuietWhileOtherWarningsStillAppear()
    {
        using var provider = Services();
        var snackbar = provider.GetRequiredService<ISnackbar>();
        var component = TestLocalization.Inject(new AppRealtimeCoordinator());
        typeof(AppRealtimeCoordinator).GetProperty("Snackbar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, snackbar);
        var notification = new AppNotificationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Important", "ResourceChangeApproval", "Hiring plan", "Review team changes", "/approvals",
            DateTimeOffset.UtcNow, null, null);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification with
        {
            NotificationId = Guid.NewGuid(), Category = "ArtifactReviewReassignment", ActionUri = null
        }, Severity.Warning);
        Assert.Single(snackbar.ShownSnackbars);
    }

    [Fact]
    public void ResolvingWarningRemovesSnackbarAndLateCreationCannotRestoreIt()
    {
        using var provider = Services();
        var snackbar = provider.GetRequiredService<ISnackbar>();
        var component = TestLocalization.Inject(new AppRealtimeCoordinator());
        typeof(AppRealtimeCoordinator).GetProperty("Snackbar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, snackbar);
        var notification = new AppNotificationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Important", "ArtifactReviewReassignment", "Document review needs reassignment",
            "Assign an active reviewer", null, DateTimeOffset.UtcNow, null, null);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Assert.Single(snackbar.ShownSnackbars);
        Invoke(component, "ResolveNotification", notification.NotificationId);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Assert.Empty(snackbar.ShownSnackbars);

        // A genuine independent error is still surfaced.
        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid() }, Severity.Error);
        Assert.Single(snackbar.ShownSnackbars);
    }

    private static void Invoke(AppRealtimeCoordinator target, string method, params object[] arguments) =>
        typeof(AppRealtimeCoordinator).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    [Theory]
    [InlineData("ProjectHealth")]
    [InlineData("ProjectHealthStatus")]
    public void AllProjectHealthUpdatesStayQuietAndIndependentWarningsStillAppear(string category)
    {
        using var provider = Services();
        var snackbar = provider.GetRequiredService<ISnackbar>();
        var component = TestLocalization.Inject(new AppRealtimeCoordinator());
        typeof(AppRealtimeCoordinator).GetProperty("Snackbar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, snackbar);
        var notification = new AppNotificationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Important", category, "Project needs management attention", "Quality is blocked", null,
            DateTimeOffset.UtcNow, null, null);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification, Severity.Warning);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid(), Severity = "Routine",
            Title = "Project incident resolved" }, Severity.Info);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid() }, Severity.Warning);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid(), Severity = "Urgent" }, Severity.Error);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid(), Category = "ArtifactReviewReassignment" }, Severity.Warning);
        Assert.Single(snackbar.ShownSnackbars);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new TestNavigation());
        services.AddMudServices(); services.AddCSweetLocalization();
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
