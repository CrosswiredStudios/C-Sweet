using System.Reflection;
using CSweet.Contracts.Setup;
using CSweet.UI.Setup;

namespace CSweet.UnitTests;

public sealed class OfficeUpgradeCompletionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewUpdateAcceptsCompletionAfterReconnect(bool freshHeartbeat)
    {
        var component = CreateComponent(freshHeartbeat);
        Invoke(component, "BeginUpdate");
        var session = Session(component.Office.Id, "created");
        Accept(component, session);
        Accept(component, session with { State = "redeemed" });
        Accept(component, session with { State = "connected" });
        Assert.True(InProgress(component));

        Accept(component, session with { State = "ready" });

        Assert.False(InProgress(component));
        Assert.Equal("ready", CurrentSession(component)!.State);
        Assert.Equal(session.Id, CurrentSession(component)!.Id);
    }

    [Fact]
    public void OpeningUpdateIgnoresPreviousCompletionUntilNewSessionIsAccepted()
    {
        var component = CreateComponent(freshHeartbeat: false);
        Invoke(component, "BeginUpdate");

        Accept(component, Session(component.Office.Id, "ready"));
        Assert.Null(CurrentSession(component));

        var current = Session(component.Office.Id, "created");
        Accept(component, current);
        Accept(component, current with { State = "ready" });

        Assert.Equal(current.Id, CurrentSession(component)!.Id);
        Assert.False(InProgress(component));
    }

    [Fact]
    public void AnotherOfficesProgressDoesNotEnableOldCompletion()
    {
        var component = CreateComponent(freshHeartbeat: false);
        Invoke(component, "BeginUpdate");
        Accept(component, Session(Guid.NewGuid(), "created"));
        Accept(component, Session(component.Office.Id, "ready"));

        Assert.Null(CurrentSession(component));
        Assert.False(InProgress(component));
    }

    private static LocalOfficeUpgrade CreateComponent(bool freshHeartbeat)
    {
        var now = DateTimeOffset.UtcNow;
        var office = new ExecutionNodeSummaryResponse(Guid.NewGuid(), Guid.NewGuid(), "Office", "PC",
            "windows", "x64", "0.6.3", "1.0", "draining", "thumbprint", now.AddDays(1),
            4, 8192, 10240, 2, freshHeartbeat ? now : now.AddMinutes(-5), [], new Dictionary<string, string>());
        var component = new LocalOfficeUpgrade();
        typeof(LocalOfficeUpgrade).GetProperty(nameof(LocalOfficeUpgrade.Office))!.SetValue(component, office);
        return component;
    }

    private static LocalOfficeSetupSessionResponse Session(Guid officeId, string state) =>
        new(Guid.NewGuid(), state, "", "", "", 0, DateTimeOffset.UtcNow.AddHours(2),
            4, 8192, 10240, 2, null, null, RecoveryAction: "upgrade", UpgradeOfficeId: officeId);

    private static void Accept(LocalOfficeUpgrade component, LocalOfficeSetupSessionResponse session) =>
        Invoke(component, "Accept", new LocalOfficeSetupActionResponse(true, null, "", session, null!));

    private static void Invoke(LocalOfficeUpgrade component, string method, params object[] arguments) =>
        typeof(LocalOfficeUpgrade).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, arguments);

    private static bool InProgress(LocalOfficeUpgrade component) =>
        (bool)typeof(LocalOfficeUpgrade).GetProperty("OperationInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private static LocalOfficeSetupSessionResponse? CurrentSession(LocalOfficeUpgrade component) =>
        (LocalOfficeSetupSessionResponse?)typeof(LocalOfficeUpgrade)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
}
