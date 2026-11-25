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
    [Theory]
    [InlineData("ResourceChangeApproval", "Routine")]
    [InlineData("PersonalTodoBlocked", "Routine")]
    [InlineData("ProjectHealth", "Routine")]
    [InlineData("ProjectHealthStatus", "Routine")]
    [InlineData("CoordinationFailure", "Important")]
    [InlineData("ArtifactReviewReassignment", "Important")]
    [InlineData("AgentTicketRepeatedFailure", "Urgent")]
    public void OverviewNotificationsAndBackgroundErrorsStayQuiet(string category, string severity)
    {
        using var provider = Services();
        var snackbar = provider.GetRequiredService<ISnackbar>();
        var component = TestLocalization.Inject(new AppRealtimeCoordinator());
        typeof(AppRealtimeCoordinator).GetProperty("Snackbar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, snackbar);
        var notification = new AppNotificationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            severity, category, "Work update", "Review this work", "/work",
            DateTimeOffset.UtcNow, null, null);
        Invoke(component, "ShowNotification", notification);
        Invoke(component, "ShowNotification", notification);
        Assert.Empty(snackbar.ShownSnackbars);
    }

    [Fact]
    public void ResolvingRoutineNotificationRemovesSnackbarAndLateCreationCannotRestoreIt()
    {
        using var provider = Services();
        var snackbar = provider.GetRequiredService<ISnackbar>();
        var component = TestLocalization.Inject(new AppRealtimeCoordinator());
        typeof(AppRealtimeCoordinator).GetProperty("Snackbar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, snackbar);
        var notification = new AppNotificationEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            "Routine", "Mention", "You were mentioned", "Open the conversation", null, DateTimeOffset.UtcNow, null, null);
        Invoke(component, "ShowNotification", notification);
        Invoke(component, "ShowNotification", notification);
        Assert.Single(snackbar.ShownSnackbars);
        Invoke(component, "ResolveNotification", notification.NotificationId);
        Assert.Empty(snackbar.ShownSnackbars);
        Invoke(component, "ShowNotification", notification);
        Assert.Empty(snackbar.ShownSnackbars);

        Invoke(component, "ShowNotification", notification with { NotificationId = Guid.NewGuid() });
        Assert.Single(snackbar.ShownSnackbars);
    }

    private static void Invoke(AppRealtimeCoordinator target, string method, params object[] arguments) =>
        typeof(AppRealtimeCoordinator).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

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
