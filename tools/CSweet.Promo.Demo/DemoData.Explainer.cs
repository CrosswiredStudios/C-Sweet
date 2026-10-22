using System.Text.Json;
using AvailableAgent = CSweet.Agent.SDK.AvailableAgent;
using AgentCatalogSource = CSweet.Agent.SDK.AgentCatalogSource;
using AgentAvailabilityState = CSweet.Agent.SDK.AgentAvailabilityState;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.Employees.Models;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Promo.Demo;

public sealed partial class DemoData
{
    private static readonly (string Name, string Role, string Portrait)[] FilmCast = [
        ("Evelyn Brooks", "Chief of Staff", "chief-of-staff"),
        ("Naomi Chen", "Creative Director", "video-game-creative-director"),
        ("Gabriel Reyes", "Producer", "video-game-producer"),
        ("Adrian Silva", "Engineer", "video-game-engineer"),
        ("Victor Lin", "Technical Director", "video-game-technical-director"),
        ("Rohan Desai", "QA", "video-game-qa")];

    public AvailableAgent FilmAgent(int index)
    {
        var p = FilmCast[index];
        return new($"first-party:{p.Portrait}", $"com.csweet.{p.Portrait}", (AgentCatalogSource)0, [],
            (AgentAvailabilityState)0, Id(60 + index), p.Name,
            index == 0 ? "Connect your direction to a clear plan, the right leadership, and accountable work." :
            index == 1 ? "Shape a distinctive game vision, refine the pitch, and bring the right creative team together." :
            "Turn the approved game vision into owned milestones, clear assignments, and reviewed delivery.",
            "C-Sweet", "Agents", [p.Role], [], [], null, null, null, 0, null, null, 1, "First-party",
            RoleKey: p.Portrait, RoleName: p.Role)
        { ImageUrl = $"_content/CSweet.UI/images/agents/{p.Portrait}-v1.jpg" };
    }

    public PendingAgentQuestionResponse FilmQuestion => new(ChatId, Id(20), FilmCast[0].Name,
        new ExecutiveDecisionCardResponse(Id(500), "Where would you like the company to focus first?", "Pending",
        [new("creative-product", "Creative & Product", "Clarify who owns the creative vision and who owns player and product outcomes.", false),
         new("research", "Player & Market Research", "Reduce uncertainty about the audience, genre, market, or player needs.", false),
         new("financial", "Financial", "Establish funding, runway, budget, and financial leadership coverage.", false),
         new("legal", "Legal & IP", "Establish intellectual-property, publishing, licensing, privacy, and legal ownership.", false)],
        "creative-product", null, null, Epoch, null) { AllowFreeText = false });

    public IReadOnlyList<SuggestedUserActionResponse> FilmSuggestions =>
        (Scene == "staffing" ? new[] { 2, 3, 4, 5 } : Scene == "producer" ? new[] { 2 } : new[] { 1 }).Select(i =>
        new SuggestedUserActionResponse(Id(510 + i), "hiring.marketplace.browse.v1", "Hire", FilmCast[i].Role,
            $"/demo/cinematic/team", Scene == "hired" ? "Completed" : "Pending", Epoch)
        { HiringRecommendationId = Id(520 + i), HiringRole = FilmCast[i].Role,
          ResultOrganizationUserId = Scene == "hired" ? Id(20 + i) : null,
          ResultOrganizationUserDisplayName = Scene == "hired" ? FilmCast[i].Name : null }).ToArray();

    public HiringRecommendationResponse FilmRecommendation(Guid id)
    {
        var index = Enumerable.Range(1, 5).Single(i => Id(520 + i) == id);
        return new(id, null, FilmCast[index].Role, "Deliver the first playable of Prism Break.", "Pending", null, [], Epoch, Epoch)
        { SelectedCatalogAgentJson = JsonSerializer.Serialize(FilmAgent(index), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
          SelectionRationale = index == 1 ? "Own the creative direction and design the team for our first browser game." :
              index == 2 ? "Own the first playable: scope, sprint planning, team coordination and acceptance." :
              index == 3 ? "Implement responsive controls, collisions and the replay loop." :
              index == 4 ? "Own technical architecture, independent code review and integration readiness." :
              "Verify the integrated game against its player-facing acceptance criteria.",
          RequestingEmployee = index == 1 ? "Evelyn Brooks" : "Naomi Chen", HiringUrl = "/demo/cinematic/team" };
    }

    public MarketplaceHirePreviewResponse FilmHirePreview(PreviewMarketplaceHireRequest request)
    {
        var i = Array.FindIndex(FilmCast, p => request.AgentReference.EndsWith(p.Portrait, StringComparison.Ordinal));
        if (i < 0) throw new InvalidOperationException("Unknown fixture candidate.");
        return new(Id(550 + i), request.AgentReference, FilmCast[i].Name, request.EmployeeDisplayName,
            request.RoleTitle, i == 0 ? OwnerId : Id(i == 1 ? 20 : 21), "C-Sweet first-party catalog", "First-party", null, null,
            ["Company coordination", "Project planning"], ["Read authorized company context", "Propose hiring and work"], [], [],
            "Illustrative hire review. Model and Office preparation are assumed complete.", "Ready")
        { TeamId = request.RecommendationId.HasValue ? Id(100) : null };
    }

    public TeamDirectoryResponse FilmTeams => new(OwnerId, true,
        [new(Id(100), "prism-break", "Prism Break", "The first playable", Id(22), "Gabriel Reyes", 1, false, 5, 0, 5, [])]);

    public IReadOnlyList<EmployeeViewModel> FilmEmployees => FilmCast.Select((p,i) =>
        new EmployeeViewModel(People[i+1],p.Name,string.Concat(p.Name.Split(' ').Select(x=>x[0])),p.Role,p.Role,
            i<2?"You":i==2?"Naomi Chen":"Gabriel Reyes",true,false,EmployeeRuntimeStatus.Online,i==2?3:0,[],false,false,false,[EmployeeAction.OpenChat,EmployeeAction.OpenMemory])
        { Teams=[new EmployeeTeamViewModel(Id(100),"Prism Break",null,i==2,false,"#735090")] }).ToArray();
    public IReadOnlyList<WorkSprintResponse> FilmSprints => [new(SprintId,BoardId,"Sprint 01","Deliver the first playable of Prism Break.","Active",Epoch,Epoch.AddDays(14),Epoch,null,12,4,Scene=="board-done"?4:1,12,Scene=="board-done"?12:3,1,Epoch,Epoch)];

    private IReadOnlyList<CommunicationHubMessageResponse> FilmMessages => Scene.StartsWith("chat-") ? SequenceMessages : [
        new(Id(110), 1, ChatId, OwnerId, "You", "Human", "Let's make a vibrant browser arcade game. One minute to learn. One more go to master.", Epoch),
        new(Id(111), 2, ChatId, Id(20), "Evelyn Brooks", "Agent", "We'll start with creative direction. Naomi can shape the vision and propose the team for the first playable.", Epoch.AddMinutes(1)),
        new CommunicationHubMessageResponse(Id(112), 3, ChatId, Id(20), "Evelyn Brooks", "Agent", "One arena. Responsive controls. A satisfying replay loop. Let's keep the first milestone focused.", Epoch.AddMinutes(2))
        { Artifacts = [new(Id(113), Id(112), DocumentId, Id(114)) { Title = "Prism Break · first playable", Status = "Draft" }] }
    ];

    public string FilmDocumentTitle => Scene switch {
        "implementation" => "PB-03 · implementation handoff",
        "technical" => "PB-03 · technical review",
        "quality" => "Prism Break · quality review",
        _ => "Prism Break · first playable plan" };
    public string FilmDocumentContent => Scene switch {
        "implementation" => "# Responsive paddle controls\n\n**Adrian Silva · Engineer · PB-03**\n\n## Implementation\n\nKeyboard and pointer input now drive the paddle. Movement is frame-rate independent and clamped to the arena.\n\n```typescript\nconst direction = readPlayerInput();\npaddle.move(direction, deltaTime);\npaddle.clampTo(arena.bounds);\n```\n\n## Checks\n\n- Input response: passed\n- Arena boundaries: passed\n- Clean restart: passed\n\n## Handoff\n\nCandidate published for independent technical review.\n\n*Illustrative sample evidence.*",
        "technical" => "# Independent technical review\n\n**Victor Lin · Technical Director · PB-03**\n\n## Review outcome\n\n**Approved for trusted story integration.**\n\n- Input handling matches the approved design.\n- Frame-rate independence and boundaries are covered.\n- The source candidate and target revision are pinned.\n\n## Integration\n\nTask candidate → story branch.\n\nThe platform integrates the reviewed candidate. QA receives the exact integrated commit.\n\n*Illustrative sample evidence.*",
        "quality" => "# Prove the play\n\n**Rohan Desai · QA · First playable**\n\n## Integrated build checks\n\n| Acceptance criterion | Result |\n| --- | --- |\n| Keyboard and pointer controls respond | Passed |\n| Paddle stays inside the arena | Passed |\n| Bricks break and score increases | Passed |\n| Restart begins a clean round | Passed |\n\n## Whole-game acceptance\n\nRegression complete. Technical readiness reviewed. Gabriel accepts the first playable.\n\n**Ready for the browser preview.**\n\n*Illustrative sample evidence; public deployment is separate.*",
        _ => "# Prism Break\n\n**First playable · Gabriel Reyes, Producer**\n\n## The promise\n\nOne minute to learn. One more go to master.\n\n## Epic → Story → Tasks\n\n**Epic:** Deliver a vibrant browser arcade game.\n\n**Story:** Play a complete round, from launch to replay.\n\n| Task | Owner |\n| --- | --- |\n| Responsive paddle controls | Adrian Silva |\n| Ball and brick collisions | Adrian Silva |\n| Score, lives and clean restart | Adrian Silva |\n| Independent technical review | Victor Lin |\n| Integrated build QA | Rohan Desai |\n\n## Ready to begin\n\nScope and assignments approved. Sprint 01 activated.\n\n*Illustrative project plan.*" };

    public WorkBoardDetailResponse FilmBoard
    {
        get
        {
            if (Scene is "board-empty" or "board-epic" or "board-stories" or "board-tasks" or "board-progress1" or "board-progress2" or "board-progress3" or "board-complete") return SequenceBoard;
            var names = new[] { "Ready", "In progress", "Technical Review", "QA", "Done" };
            var columns = names.Select((n,i) => new WorkBoardColumnResponse(Id(130+i), n, i==0?"ToDo":i==4?"Done":"InProgress", i,"None",null)).ToArray();
            var titles = new[] { "Responsive paddle controls", "Ball and brick collisions", "Score, lives and restart", "Approve the first playable plan" };
            var stage = Scene switch { "board-ready" => 0, "board-review" => 2, "board-qa" => 3, "board-done" => 4, _ => 1 };
            var items = titles.Select((title,i) => new WorkBoardItemResponse(Id(140+i), BoardId, columns[stage==4?4:i==0?stage:i==3?4:0].Id,
                null, SprintId, "Task", title, i==0?"Move the paddle with keyboard or pointer input. Keep motion responsive and inside the arena.":"First playable · approved scope",
                stage==4||i==3?"Completed":i==0&&stage>0?"InProgress":"Ready", "Medium", 3, i, 1, null, Epoch, Epoch,
                AssignedEmployeeId:Id(i==3?22:23), AssignedInstallationId:Id(i==3?62:63), AssignedDisplayName:i==3?"Gabriel Reyes":"Adrian Silva")
            { Identifier=$"PB-{i+3:D2}", TypeKey="work.task.v1", Planning = new Wire.WorkItemPlanningSpecification(
                ["Support keyboard and pointer input", "Keep controls frame-rate independent"],
                ["Paddle responds immediately to input", "Paddle never leaves the arena", "Restart resets position and score"]) }).ToArray();
            return new(new WorkBoardSummaryResponse(BoardId,OrganizationId,ProjectId,"Prism Break · first playable","One arena. Great controls. One more go.",true,false,true,Epoch,4,3,1,Epoch,Epoch,
                [WorkBoardActions.Read,WorkItemActions.Read,WorkSprintActions.Read]) { Key="PB" },columns,items);
        }
    }
}
