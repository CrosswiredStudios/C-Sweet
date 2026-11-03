using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class PluginSetupPresentationTests
{
    [Fact]
    public void ExactYouTubeDeclarationAndUnrelatedWorkspaceDeclarationUseTheSameSections()
    {
        using var source = typeof(PluginSetupPresentationTests).Assembly.GetManifestResourceStream("YouTubeConnector.0.2.0.json")!;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var youtube = View(manifest.Accounts!) with { PluginId = manifest.Id };
        var workspace = View(new() { ResourceType = "example.workspace", ResourceLabel = "Workspace", ResourcesLabel = "Workspaces",
            Permissions = [new() { Id = "read", Label = "Read workspace" }] }) with { PluginId = "example.connector.workspace" };
        Assert.Equal(PluginSetupPresentation.ForAccounts(workspace), PluginSetupPresentation.ForAccounts(youtube));
        Assert.All(PluginSetupPresentation.ForAccounts(youtube), s => Assert.False(s.Complete));
    }

    [Fact]
    public void OperatorAndPermissionFreeDeclarationsOmitBusinessAccess()
    {
        var view = View(new() { Permissions = [new() { Id = "read" }] });
        Assert.DoesNotContain(PluginSetupPresentation.ForAccounts(view with { CanAdminister = false }), s => s.Kind == PluginSetupSectionKind.Access);
        Assert.DoesNotContain(PluginSetupPresentation.ForAccounts(view with { Declaration = new() }), s => s.Kind == PluginSetupSectionKind.Access);
    }

    [Theory]
    [InlineData("Connected", true, false, true)]
    [InlineData("ConnectionRequired", true, false, false)]
    [InlineData("Disconnecting", true, false, false)]
    [InlineData("Connected", false, false, false)]
    [InlineData("Connected", true, true, false)]
    public void ProgressRequiresUsableResourcesAndAnActiveSavedGrant(string state, bool available, bool revoked, bool complete)
    {
        var resource = Guid.NewGuid();
        var account = new PluginManagedAccount(Guid.NewGuid(), "Example", state, 9, true,
            [new(resource, "workspace-a", "Workspace A", null, available)], [new(Guid.NewGuid(), "Connected", ["base"], true)],
            [new(Guid.NewGuid(), Guid.NewGuid(), "SelectedResources", [resource], ["read"], 4, revoked)]);
        var sections = PluginSetupPresentation.ForAccounts(View(new() { Permissions = [new() { Id = "read" }] }) with { Accounts = [account] });
        Assert.Equal(complete, sections.Single(s => s.Kind == PluginSetupSectionKind.Access).Complete);
        // A presentation indicator never asserts full agent readiness.
        Assert.False(sections.Single(s => s.Kind == PluginSetupSectionKind.Status).Complete);
    }

    [Fact]
    public void ReturnTargetIsExactAndUnknownTargetsDoNotInventSuccess()
    {
        var latest = new PluginAccountSetupView(Guid.NewGuid(), "Ready", 1, null, DateTimeOffset.UtcNow);
        var previous = latest with { Id = Guid.NewGuid(), Status = "Failed" };
        var view = View(new()) with { SetupSessions = [latest, previous] };
        Assert.Equal(latest, PluginSetupPresentation.CurrentAttempt(view, null));
        Assert.Equal(previous, PluginSetupPresentation.CurrentAttempt(view, previous.Id));
        Assert.Null(PluginSetupPresentation.CurrentAttempt(view, Guid.NewGuid()));
    }

    private static PluginAccountManagementView View(PluginAccountManagement declaration) =>
        new(Guid.NewGuid(), "Example connector", "1.0", new string('a', 64), "Built", true, null, declaration, [], [], [], true);
}
