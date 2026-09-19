using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using HiringResponse = CSweet.Contracts.Core.HiringRecommendationResponse;
namespace CSweet.UnitTests;

public sealed class ProjectStaffingTests
{
    [Fact]
    public async Task Free_manager_is_reserved_once_and_a_second_request_cannot_take_it()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var available = await f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId!.Value, new(f.Intake.Id, null, "find"), default);
        Assert.Equal(f.Manager.Id, Assert.Single(available.Candidates).Id);
        Assert.Empty(f.Hiring.Calls); Assert.Empty(f.Coordination.Calls);
        await f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId.Value, new(f.Intake.Id, f.Manager.Id, "select"), default);
        await f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId.Value, new(f.Intake.Id, f.Manager.Id, "select"), default);
        Assert.Single(f.Db.ProjectManagerReservations);
        Assert.Equal("AwaitingManagerAssistance", f.Intake.Status); Assert.Null(f.Intake.ProjectId); Assert.Empty(f.Db.CoreWorkTasks);
        var handoffs = f.Coordination.Calls.Select(x => Assert.IsType<StartAgentCoordinationRequest>(x[3])).ToArray();
        Assert.Equal(handoffs[0].IdempotencyKey, handoffs[1].IdempotencyKey);
        Assert.All(handoffs, x => { Assert.Equal(f.Intake.Id, x.SourceIntakeId); Assert.Equal("project-manager-setup.v1", x.Artifact!.Type); });
        var other = new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = f.Org, ChiefId = f.Chief.Id, Status = "AwaitingManagerAssistance" };
        f.Db.ProjectIntakes.Add(other); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId.Value, new(other.Id, f.Manager.Id, "competing"), default));
        Assert.Equal(f.Intake.Id, (await f.Db.ProjectManagerReservations.SingleAsync()).IntakeId);
    }
    [Fact]
    public async Task Busy_manager_creates_one_hiring_recommendation_and_cancellation_withdraws_it()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Db.Workstreams.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, AccountableManagerOrganizationUserId = f.Manager.Id, Status = WorkstreamStatus.Active });
        await f.Db.SaveChangesAsync();
        var result = await f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId!.Value, new(f.Intake.Id, null, "find"), default);
        await f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId.Value, new(f.Intake.Id, null, "retry"), default);
        Assert.Empty(result.Candidates); Assert.NotNull(result.HiringRecommendationId); Assert.Single(f.Hiring.Calls);
        Assert.Equal("AwaitingManagerAssistance", result.Intake.Status); Assert.Empty(f.Db.ProjectManagerReservations);
        var proposal = Assert.IsType<CSweet.Contracts.Core.UpsertHiringRecommendationRequest>(f.Hiring.Calls[0][2]);
        Assert.Equal("software-product-manager", proposal.RoleKey); Assert.Empty(proposal.CandidateReferences);
        var cancelled = await f.Service.ChooseAsync(f.Org, f.Developer.AgentInstallationId!.Value,
            new(f.Intake.Id, f.Intake.Revision, "cancel", null, f.Intake.SourceMessageId, "cancel"), default);
        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal(2, f.Hiring.Calls.Count);
        Assert.IsType<CSweet.Contracts.Core.WithdrawHiringRecommendationRequest>(f.Hiring.Calls[1][2]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.StaffAsync(f.Org, f.Chief.AgentInstallationId.Value, new(f.Intake.Id, null, "late"), default));
    }
    [Fact]
    public async Task Missing_chief_grants_keep_actionable_setup_state_without_hiring_or_handoff()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Intake.Status = "AwaitingProjectChoice"; f.Intake.ChiefId = null;
        await f.Db.SaveChangesAsync();
        var result = await f.Service.RequestManagerAsync(f.Org, f.Developer.AgentInstallationId!.Value,
            new(f.Intake.Id, f.Intake.Revision, "manager", null, f.Intake.SourceMessageId, "choose"), default);
        Assert.Equal("AwaitingManagerAssistance", result.Status); Assert.Contains("grants", result.Issue);
        Assert.Contains("projects/new?intake=", result.SetupUrl); Assert.Empty(f.Hiring.Calls); Assert.Empty(f.Coordination.Calls);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Org { get; } = Guid.NewGuid();
        public OrganizationUser Chief = new(), Manager = new(), Developer = new(), Human = new();
        public ProjectIntake Intake = new();
        public CallProxy Coordination { get; }
        public CallProxy Hiring { get; }
        public ProjectIntakeService Service { get; }
        public Fixture()
        {
            var coordination = DispatchProxy.Create<IAgentCoordinationService, CallProxy>(); Coordination = (CallProxy)(object)coordination;
            var hiring = DispatchProxy.Create<IHiringService, CallProxy>(); Hiring = (CallProxy)(object)hiring;
            Service = new(Db, TimeProvider.System, new(Db, TimeProvider.System, new(Db, TimeProvider.System)), new(Db, TimeProvider.System), coordination, hiring);
        }
        public async Task SeedAsync()
        {
            foreach (var person in new[] { Chief, Manager, Developer, Human })
            { person.Id = Guid.NewGuid(); person.OrganizationId = Org; person.EmployeeType = person == Human ? EmployeeType.Human : EmployeeType.Agent; person.AgentInstallationId = person == Human ? null : Guid.NewGuid(); Db.CoreOrganizationUsers.Add(person); }
            Human.PermissionLevel = OrganizationPermissionLevel.Owner;
            Db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = Org, OrganizationUserId = Chief.Id, PositionKey = "chief-of-staff" });
            Db.AgentInstallations.Add(new() { Id = Manager.AgentInstallationId!.Value, BusinessId = Org.ToString("D"),
                PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = "{\"rolePolicy\":{\"declaredRoleKeys\":[\"software-product-manager\"]}}" },
                Grant = new() { Id = Guid.NewGuid(), RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { ProjectIntakeCapabilities.ManagerSetup, CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond }) } });
            Intake = new() { Id = Guid.NewGuid(), OrganizationId = Org, ChiefId = Chief.Id, DeveloperId = Developer.Id, DeveloperInstallationId = Developer.AgentInstallationId!.Value,
                RequestingHumanId = Human.Id, Status = "AwaitingManagerAssistance", Name = "Prototype", Goal = "Build the prototype", OriginalRequest = "Build a game", SourceChatTurnId = Guid.NewGuid(),
                SourceMessageId = Guid.NewGuid(), ConversationId = Guid.NewGuid(), LastChoiceMessageId = Guid.NewGuid() };
            Db.ProjectIntakes.Add(Intake);
            Db.CoreConversationMessages.Add(new() { Id = Intake.SourceMessageId, ConversationId = Intake.ConversationId, SenderOrganizationUserId = Human.Id, Content = "Request manager assistance" });
            Db.ChatTurns.Add(new() { Id = Intake.SourceChatTurnId.Value, OrganizationId = Org, ConversationId = Intake.ConversationId, UserMessageId = Intake.SourceMessageId, TargetAgentOrganizationUserId = Developer.Id });
            await Db.SaveChangesAsync();
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    public class CallProxy : DispatchProxy
    {
        public List<object?[]> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(args!);
            if (method!.Name == "StartAsync")
            {
                var request = (StartAgentCoordinationRequest)args![3]!;
                return Task.FromResult(new AgentCoordinationSession(Guid.NewGuid(), Guid.NewGuid(), request.SourceConversationId, request.SourceChatTurnId,
                    request.SourceMessageId, new((Guid)args[1]!, (Guid)args[2]!, "Chief", "chief-of-staff"), new(request.TargetOrganizationUserId, Guid.NewGuid(), "Manager", "software-product-manager"),
                    request.Subject, request.Objective, request.SuccessCriteria, "Active", 1, 1, request.TargetOrganizationUserId, false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
            }
            if (method.Name is "UpsertRecommendationAsync" or "WithdrawRecommendationAsync")
                return Task.FromResult(new HiringResponse(Guid.NewGuid(), null, "Manager", "Setup", "Pending", null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            throw new NotSupportedException(method.Name);
        }
    }
}
